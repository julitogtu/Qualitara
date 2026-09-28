using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelayPulse.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false),
                    name = table.Column<string>(type: "varchar(120)", unicode: false, maxLength: 120, nullable: false),
                    industry = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    timezone = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "week_buckets",
                columns: table => new
                {
                    timezone = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    week_start_local = table.Column<DateOnly>(type: "date", nullable: false),
                    utc_start = table.Column<DateTime>(type: "datetime2", nullable: false),
                    utc_end = table.Column<DateTime>(type: "datetime2", nullable: false),
                    is_complete = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_week_buckets", x => new { x.timezone, x.week_start_local });
                });

            migrationBuilder.CreateTable(
                name: "activity_events",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false),
                    account_id = table.Column<int>(type: "int", nullable: false),
                    location = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    event_type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    duration_seconds = table.Column<int>(type: "int", nullable: true),
                    outcome = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activity_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_activity_events_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_activity_events_account_occurred",
                table: "activity_events",
                columns: new[] { "account_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_week_buckets_timezone_utc_start",
                table: "week_buckets",
                columns: new[] { "timezone", "utc_start" },
                unique: true)
                .Annotation("SqlServer:Include", new[] { "utc_end", "week_start_local", "is_complete" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity_events");

            migrationBuilder.DropTable(
                name: "week_buckets");

            migrationBuilder.DropTable(
                name: "accounts");
        }
    }
}
