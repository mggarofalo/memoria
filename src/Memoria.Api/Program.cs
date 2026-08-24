var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

// Liveness: the process is up. Does not touch the QMD backend.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Readiness: reserved for the QMD backend probe once the proxy lands (MEMORIA API epic).
app.MapGet("/ready", () => Results.Ok(new { status = "ok" }));

app.Run();

// Exposed so WebApplicationFactory<Program> can boot the app in tests.
public partial class Program;
