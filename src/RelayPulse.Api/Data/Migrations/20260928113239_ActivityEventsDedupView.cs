using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelayPulse.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ActivityEventsDedupView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The seed holds 12 exact duplicates (every column but id). Collapse each group to one
            // row, keeping the lowest id. GROUP BY treats NULLs as equal, so rows with NULL
            // duration_seconds / outcome dedupe too. The raw table is never modified; every
            // reporting read goes through this view.
            migrationBuilder.Sql("""
                CREATE VIEW activity_events_dedup AS
                SELECT MIN(id) AS id,
                       account_id,
                       location,
                       event_type,
                       occurred_at,
                       duration_seconds,
                       outcome
                FROM activity_events
                GROUP BY account_id, location, event_type, occurred_at, duration_seconds, outcome;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS activity_events_dedup;");
        }
    }
}
