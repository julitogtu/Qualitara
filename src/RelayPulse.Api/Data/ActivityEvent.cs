namespace RelayPulse.Api.Data;

/// <summary>Raw table row. Reporting reads go through the activity_events_dedup view, not this.</summary>
public sealed class ActivityEvent
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public required string Location { get; set; }
    public required string EventType { get; set; }

    /// <summary>UTC.</summary>
    public DateTime OccurredAt { get; set; }

    public int? DurationSeconds { get; set; }
    public string? Outcome { get; set; }
}
