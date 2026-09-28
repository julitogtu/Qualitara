using RelayPulse.Core;

var builder = WebApplication.CreateBuilder(args);

// Supplied by appsettings.Development.json locally, or ConnectionStrings__Relay in any environment.
_ = builder.Configuration.GetConnectionString("Relay")
    ?? throw new InvalidOperationException(
        "Connection string 'Relay' is not configured. Set ConnectionStrings__Relay (see .env.example).");

builder.Services.AddCore();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.Run();

public partial class Program;
