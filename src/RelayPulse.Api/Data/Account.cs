namespace RelayPulse.Api.Data;

public sealed class Account
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Industry { get; set; }

    /// <summary>IANA id. One per account; the schema cannot express per-location zones.</summary>
    public required string Timezone { get; set; }

    /// <summary>UTC. Not the start of history — never window on it.</summary>
    public DateTime CreatedAt { get; set; }
}
