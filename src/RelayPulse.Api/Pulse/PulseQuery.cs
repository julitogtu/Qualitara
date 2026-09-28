using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RelayPulse.Api.Data;
using RelayPulse.Core.Pulse;

namespace RelayPulse.Api.Pulse;

/// <summary>The aggregation: weekly counts for one account at both grains, in one statement.</summary>
public sealed class PulseQuery(RelayDbContext db)
{
    // Events are bucketed by a range join onto the account timezone's precomputed weeks, so
    // bucketing is account-local and DST-correct, and partial weeks are excluded by a column
    // rather than by date arithmetic. Reads the dedupe view, never the raw table.
    //
    // GROUPING SETS: the first set is the location grain, the second the account grain (its
    // location comes back NULL, which the NOT NULL column can never produce by itself).
    private const string Sql = $"""
        SELECT b.week_start_local AS WeekStart,
               e.location         AS Location,
               e.event_type       AS EventType,
               e.outcome          AS Outcome,
               COUNT(*)           AS Events
        FROM accounts a
        JOIN week_buckets b
          ON b.timezone = a.timezone
         AND b.is_complete = 1 -- backstop only: PulseEndpoints' complete-week list owns the rule (T3 mutation b′)
         AND b.week_start_local >= @from
         AND b.week_start_local <= @to
        JOIN {RelayDbContext.DedupView} e
          ON e.account_id = a.id
         AND e.occurred_at >= b.utc_start
         AND e.occurred_at <  b.utc_end
        WHERE a.id = @accountId
        GROUP BY GROUPING SETS (
            (b.week_start_local, e.location, e.event_type, e.outcome),
            (b.week_start_local, e.event_type, e.outcome)
        )
        """;

    public Task<List<WeeklyCount>> RunAsync(int accountId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        db.Database
            .SqlQueryRaw<WeeklyCount>(
                Sql,
                new SqlParameter("@accountId", SqlDbType.Int) { Value = accountId },
                new SqlParameter("@from", SqlDbType.Date) { Value = from },
                new SqlParameter("@to", SqlDbType.Date) { Value = to })
            .ToListAsync(cancellationToken);
}
