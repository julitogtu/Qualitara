using System.Net;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayPulse.Api.Data;
using RelayPulse.Api.Data.Seeding;
using Testcontainers.MsSql;

namespace RelayPulse.Tests.Integration;

/// <summary>
/// One SQL Server container per test collection. Migrations and db/seed.sql are applied once, by
/// the same <see cref="SeedImporter"/> the app's <c>seed</c> command runs. When Docker is not
/// available the fixture records why and every test in the collection skips with that message.
/// </summary>
public sealed class PulseApiFixture : IAsyncLifetime
{
    private const string Image = "mcr.microsoft.com/mssql/server:2022-latest";

    private MsSqlContainer? _container;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            _container = new MsSqlBuilder(Image).Build();
            await _container.StartAsync(cancellationToken);
        }
        catch (DockerUnavailableException ex)
        {
            // Only "no Docker" is a skip; anything failing past this point is a real failure.
            SkipReason = $"Integration tests skipped: Docker is not available ({ex.Message}).";
            return;
        }

        // The container's default catalog is master; migrate into a database of our own.
        var connectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "RelayPulse",
        }.ConnectionString;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Not Development: appsettings.Development.json points at localhost:1433.
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Relay", connectionString);
        });

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            // Guard: if the override ever stops reaching Program, the tests would silently run
            // against the developer's compose database instead of this container.
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            var actual = new SqlConnectionStringBuilder(db.Database.GetConnectionString());
            if (actual.DataSource != new SqlConnectionStringBuilder(connectionString).DataSource)
            {
                throw new InvalidOperationException($"API is not using the test container (got {actual.DataSource}).");
            }

            var importer = scope.ServiceProvider.GetRequiredService<SeedImporter>();
            await importer.RunAsync(SeedPath.Resolve(null), cancellationToken);
        }

        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>GET /api/accounts/{id}/pulse[?week=…]; skips the calling test when Docker is absent.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> GetPulseAsync(
        int accountId, string? week, CancellationToken cancellationToken)
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);

        var url = $"/api/accounts/{accountId}/pulse" + (week is null ? string.Empty : $"?week={week}");
        using var response = await _client!.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body.Length == 0 ? "null" : body);
        return (response.StatusCode, document.RootElement.Clone());
    }

    /// <summary>As <see cref="GetPulseAsync"/>, asserting HTTP 200.</summary>
    public async Task<JsonElement> GetOkPulseAsync(int accountId, string? week, CancellationToken cancellationToken)
    {
        var (status, body) = await GetPulseAsync(accountId, week, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, status);
        return body;
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<PulseApiFixture>
{
    public const string Name = "SQL Server";
}
