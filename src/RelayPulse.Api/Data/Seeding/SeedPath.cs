namespace RelayPulse.Api.Data.Seeding;

/// <summary>
/// `dotnet run --project` starts in the project directory, so relative seed paths are resolved
/// against the repository root (the directory holding RelayPulse.slnx), not the working directory.
/// </summary>
public static class SeedPath
{
    private const string RootMarker = "RelayPulse.slnx";
    private static readonly string DefaultRelative = Path.Combine("db", "seed.sql");

    public static string Resolve(string? requested)
    {
        var relative = requested ?? DefaultRelative;
        if (Path.IsPathRooted(relative))
        {
            return relative;
        }

        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, RootMarker)))
            {
                return Path.Combine(dir.FullName, relative);
            }
        }

        return Path.GetFullPath(relative);
    }
}
