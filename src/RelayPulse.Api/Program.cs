using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RelayPulse.Api.Data;
using RelayPulse.Api.Data.Seeding;
using RelayPulse.Api.Pulse;
using RelayPulse.Core;

var builder = WebApplication.CreateBuilder(args);

// Supplied by appsettings.Development.json locally, or ConnectionStrings__Relay in any environment.
var connectionString = builder.Configuration.GetConnectionString("Relay")
    ?? throw new InvalidOperationException(
        "Connection string 'Relay' is not configured. Set ConnectionStrings__Relay (see .env.example).");

builder.Services.AddCore();
builder.Services.AddDbContext<RelayDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddScoped<SeedImporter>();
builder.Services.AddScoped<PulseQuery>();
builder.Services.AddProblemDetails();

// Verdicts go over the wire as "not_enough_volume", "insufficient_history", …
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));

var app = builder.Build();

// `dotnet run --project src/RelayPulse.Api -- seed [path]`: migrate, load the fixture, build week_buckets, exit.
if (args is ["seed", .. var rest])
{
    await using var scope = app.Services.CreateAsyncScope();
    var importer = scope.ServiceProvider.GetRequiredService<SeedImporter>();
    await importer.RunAsync(SeedPath.Resolve(rest is [var path, ..] ? path : null), CancellationToken.None);
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapPulse();

app.Run();

public partial class Program;
