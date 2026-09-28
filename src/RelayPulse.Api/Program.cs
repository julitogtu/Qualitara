using Microsoft.EntityFrameworkCore;
using RelayPulse.Api.Data;
using RelayPulse.Api.Data.Seeding;
using RelayPulse.Core;

var builder = WebApplication.CreateBuilder(args);

// Supplied by appsettings.Development.json locally, or ConnectionStrings__Relay in any environment.
var connectionString = builder.Configuration.GetConnectionString("Relay")
    ?? throw new InvalidOperationException(
        "Connection string 'Relay' is not configured. Set ConnectionStrings__Relay (see .env.example).");

builder.Services.AddCore();
builder.Services.AddDbContext<RelayDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddScoped<SeedImporter>();
builder.Services.AddProblemDetails();

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

app.Run();

public partial class Program;
