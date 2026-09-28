using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using RelayPulse.Api.Data;
using RelayPulse.Core.Pulse;
using RelayPulse.Core.Statistics;

namespace RelayPulse.Api.Pulse;

public static class PulseEndpoints
{
    public const string InvalidWeek = "pulse.invalid_week";
    public const string AccountNotFound = "pulse.account_not_found";

    public static IEndpointRouteBuilder MapPulse(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/accounts/{id:int}/pulse", GetPulseAsync);
        return app;
    }

    private static async Task<Results<Ok<PulseResponse>, ProblemHttpResult>> GetPulseAsync(
        int id,
        string? week,
        RelayDbContext db,
        PulseQuery query,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(PulseEndpoints).FullName!);
        DateOnly? requested = null;
        if (week is not null)
        {
            if (!DateOnly.TryParseExact(week, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return Problem(400, InvalidWeek, $"'{week}' is not a date in YYYY-MM-DD form.");
            }

            requested = parsed;
        }

        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (account is null)
        {
            return Problem(404, AccountNotFound, $"No account with id {id}.");
        }

        // Complete weeks come from week_buckets, which is built from the global MIN/MAX(occurred_at):
        // the default is the last complete week of the data, never of the wall clock.
        var completeWeeks = await db.WeekBuckets.AsNoTracking()
            .Where(b => b.Timezone == account.Timezone && b.IsComplete)
            .OrderBy(b => b.WeekStartLocal)
            .Select(b => b.WeekStartLocal)
            .ToListAsync(cancellationToken);

        if (completeWeeks.Count == 0)
        {
            // No data loaded at all: only an explicit week is a bad parameter.
            if (requested is not null)
            {
                return Problem(400, InvalidWeek, "There are no complete weeks of data yet.");
            }

            var nothing = new PulseRow(null, 0, Verdict.InsufficientHistory, null, null, null, null, 0, []);
            return TypedResults.Ok(new PulseResponse(account.Id, account.Name, account.Timezone, null, "empty", nothing, []));
        }

        var target = requested ?? completeWeeks[^1];
        var index = completeWeeks.BinarySearch(target);
        if (index < 0)
        {
            return Problem(400, InvalidWeek,
                $"{target:yyyy-MM-dd} is not the Monday of a complete week; choose a Monday from " +
                $"{completeWeeks[0]:yyyy-MM-dd} to {completeWeeks[^1]:yyyy-MM-dd}.");
        }

        // All complete weeks up to the target, not just the baseline window: the location list and
        // "has any events" are properties of the whole history (PulseBuilder.Build's contract).
        var rows = await query.RunAsync(id, completeWeeks[0], target, cancellationToken);
        var report = PulseBuilder.Build(completeWeeks, target, rows);

        logger.LogInformation("Pulse: served {AccountId} week {Week}", id, target.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        return TypedResults.Ok(new PulseResponse(
            account.Id,
            account.Name,
            account.Timezone,
            new PulsePeriod(
                target,
                target.AddDays(6),
                requested is null,
                completeWeeks[0],
                completeWeeks[^1],
                index > 0 ? completeWeeks[index - 1] : null,
                index < completeWeeks.Count - 1 ? completeWeeks[index + 1] : null),
            report.HasEvents ? "ok" : "empty",
            PulseRow.From(report.Account),
            report.Locations.Select(PulseRow.From).ToList()));
    }

    private static ProblemHttpResult Problem(int status, string code, string detail) =>
        TypedResults.Problem(statusCode: status, title: code, detail: detail);
}
