using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace RelayPulse.Api.Data;

/// <summary>
/// datetime2 carries no kind; everything stored is UTC, so everything read back is stamped
/// <see cref="DateTimeKind.Utc"/>. Writing a <see cref="DateTimeKind.Local"/> value is a bug and throws.
/// </summary>
public sealed class UtcDateTimeConverter()
    : ValueConverter<DateTime, DateTime>(v => ToStore(v), v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
{
    private static DateTime ToStore(DateTime value) =>
        value.Kind == DateTimeKind.Local
            ? throw new InvalidOperationException("Refusing to store a DateTimeKind.Local value; convert to UTC first.")
            : value;
}
