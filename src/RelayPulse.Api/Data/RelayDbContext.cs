using Microsoft.EntityFrameworkCore;
using RelayPulse.Core.Time;

namespace RelayPulse.Api.Data;

public sealed class RelayDbContext(DbContextOptions<RelayDbContext> options) : DbContext(options)
{
    /// <summary>Exact-duplicate-free projection of activity_events. All reporting reads use it.</summary>
    public const string DedupView = "activity_events_dedup";

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<ActivityEvent> ActivityEvents => Set<ActivityEvent>();
    public DbSet<WeekBucket> WeekBuckets => Set<WeekBucket>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>()
            .HaveConversion<UtcDateTimeConverter>()
            .HaveColumnType("datetime2");

        // Properties<> covers mapped entities only. Raw SQL (SqlQueryRaw<T> into unmapped types,
        // i.e. the aggregation) uses the default type mapping, which otherwise yields Unspecified.
        configurationBuilder.DefaultTypeMapping<DateTime>()
            .HasConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Mirrors db/schema.sql: snake_case names, explicit ids (no IDENTITY), varchar widths.
        modelBuilder.Entity<Account>(e =>
        {
            e.ToTable("accounts");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(a => a.Name).HasColumnName("name").HasMaxLength(120).IsUnicode(false);
            e.Property(a => a.Industry).HasColumnName("industry").HasMaxLength(60).IsUnicode(false);
            e.Property(a => a.Timezone).HasColumnName("timezone").HasMaxLength(60).IsUnicode(false);
            e.Property(a => a.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<ActivityEvent>(e =>
        {
            e.ToTable("activity_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.Location).HasColumnName("location").HasMaxLength(80).IsUnicode(false);
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(40).IsUnicode(false);
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.DurationSeconds).HasColumnName("duration_seconds");
            e.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(40).IsUnicode(false);

            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.AccountId, x.OccurredAt }).HasDatabaseName("ix_activity_events_account_occurred");
        });

        modelBuilder.Entity<WeekBucket>(e =>
        {
            e.ToTable("week_buckets");
            e.HasKey(b => new { b.Timezone, b.WeekStartLocal });
            e.Property(b => b.Timezone).HasColumnName("timezone").HasMaxLength(60).IsUnicode(false);
            e.Property(b => b.WeekStartLocal).HasColumnName("week_start_local");
            e.Property(b => b.UtcStart).HasColumnName("utc_start");
            e.Property(b => b.UtcEnd).HasColumnName("utc_end");
            e.Property(b => b.IsComplete).HasColumnName("is_complete");

            // The aggregation range-joins on (timezone, utc_start <= occurred_at < utc_end).
            e.HasIndex(b => new { b.Timezone, b.UtcStart })
                .IsUnique()
                .IncludeProperties(b => new { b.UtcEnd, b.WeekStartLocal, b.IsComplete })
                .HasDatabaseName("ix_week_buckets_timezone_utc_start");
        });
    }
}
