using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RelayPulse.Core.Time;

namespace RelayPulse.Api.Data.Seeding;

/// <summary>
/// Applies migrations, loads db/seed.sql unchanged, then (re)builds week_buckets.
/// Idempotent: the fixture load runs once in a single transaction and is skipped when data is
/// present; week_buckets is fully regenerated on every run.
/// </summary>
public sealed class SeedImporter(RelayDbContext db, ILogger<SeedImporter> logger)
{
    private const int StatementsPerBatch = 500;

    private static readonly string[] AllowedPrefixes =
    [
        "INSERT INTO accounts ",
        "INSERT INTO activity_events ",
    ];

    public async Task RunAsync(string seedPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(seedPath))
        {
            throw new FileNotFoundException($"Seed file not found at '{Path.GetFullPath(seedPath)}'.", seedPath);
        }

        await db.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Pulse: migrations applied.");

        if (await db.Accounts.AnyAsync(cancellationToken) || await db.ActivityEvents.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Pulse: seed data already present, skipping fixture load.");
        }
        else
        {
            var statements = await LoadFixtureAsync(seedPath, cancellationToken);
            logger.LogInformation("Pulse: loaded {Statements} statements from {SeedPath}.", statements, seedPath);
        }

        var buckets = await RebuildWeekBucketsAsync(cancellationToken);
        logger.LogInformation("Pulse: week_buckets rebuilt with {Buckets} rows.", buckets);
    }

    private async Task<int> LoadFixtureAsync(string seedPath, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();

        var batch = new StringBuilder();
        var inBatch = 0;
        var total = 0;

        await foreach (var statement in ReadStatementsAsync(seedPath, cancellationToken))
        {
            batch.AppendLine(statement);
            inBatch++;
            total++;

            if (inBatch == StatementsPerBatch)
            {
                await ExecuteAsync(connection, transaction, batch.ToString(), cancellationToken);
                batch.Clear();
                inBatch = 0;
            }
        }

        if (inBatch > 0)
        {
            await ExecuteAsync(connection, transaction, batch.ToString(), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return total;
    }

    // Streams the file; seed.sql is 2.4MB and is never held in memory whole.
    private static async IAsyncEnumerable<string> ReadStatementsAsync(
        string seedPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(seedPath, Encoding.UTF8);
        var pending = new StringBuilder();
        var lineNumber = 0;
        var statementStart = 0;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (pending.Length == 0 && (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal)))
            {
                continue;
            }

            if (pending.Length == 0)
            {
                statementStart = lineNumber;
                if (!AllowedPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException(
                        $"Unexpected statement at {seedPath}:{lineNumber}; only INSERTs into accounts and activity_events are allowed.");
                }
            }

            pending.AppendLine(line);
            if (trimmed.EndsWith(';'))
            {
                yield return pending.ToString();
                pending.Clear();
            }
        }

        if (pending.Length > 0)
        {
            throw new InvalidDataException($"Unterminated statement starting at {seedPath}:{statementStart}.");
        }
    }

    private static async Task ExecuteAsync(
        DbConnection connection, IDbContextTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> RebuildWeekBucketsAsync(CancellationToken cancellationToken)
    {
        // Window = global MIN/MAX(occurred_at) through the dedupe view, never the wall clock.
        // Scalar queries on purpose: the UTC converter reaches SqlQueryRaw<DateTime> through
        // DefaultTypeMapping, but NOT DateTime members of a record projection (verified in T1).
        const string From = " AS Value FROM " + RelayDbContext.DedupView;
        var eventCount = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*)" + From).SingleAsync(cancellationToken);
        var timezones = await db.Accounts
            .Select(a => a.Timezone)
            .Distinct()
            .OrderBy(tz => tz)
            .ToListAsync(cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.WeekBuckets.ExecuteDeleteAsync(cancellationToken);

        if (eventCount == 0)
        {
            logger.LogWarning("Pulse: no events; week_buckets left empty.");
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        // Only now: MIN/MAX over an empty view is NULL, which a DateTime cannot hold.
        var windowStart = await db.Database.SqlQueryRaw<DateTime>("SELECT MIN(occurred_at)" + From).SingleAsync(cancellationToken);
        var windowEnd = await db.Database.SqlQueryRaw<DateTime>("SELECT MAX(occurred_at)" + From).SingleAsync(cancellationToken);

        var buckets = timezones
            .SelectMany(tz => WeekBucketGenerator.Generate(tz, windowStart, windowEnd))
            .ToList();

        db.WeekBuckets.AddRange(buckets);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();

        return buckets.Count;
    }
}
