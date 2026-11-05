using Memoria.Api.Auth;
using Memoria.Api.Configuration;
using Memoria.Api.Endpoints;
using Memoria.Api.Hosting;
using Memoria.Api.Http;
using Memoria.Api.Ingest;
using Memoria.Api.Mcp;
using Memoria.Api.OpenApi;
using Memoria.Api.Search;
using Memoria.Api.Sidecar;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

// `dotnet Memoria.Api.dll --health-check` is the container HEALTHCHECK: probe
// the running instance's /health and exit 0 or 1 without starting a host.
if (HealthProbe.IsRequested(args))
{
	return await HealthProbe.RunAsync(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
}

bool bootstrap = BootstrapAdminKey.IsRequested(args);
WebApplicationBuilder builder = WebApplication.CreateBuilder(bootstrap ? BootstrapAdminKey.HostArguments(args) : args);

// Machine-local overrides (the sidecar secret, a LAN base URL) live in a file
// .gitignore excludes. It outranks appsettings.*.json but not environment
// variables or --switches.
LocalSettings.Insert(builder.Configuration, builder.Environment.ContentRootFileProvider);

// `dotnet Memoria.Api.dll --bootstrap-admin-key <label>` creates the first admin
// key, prints it once and exits. The builder is used for its configuration only.
if (bootstrap)
{
	return await BootstrapAdminKey.RunAsync(args, builder.Configuration, Console.Out, Console.Error);
}

// Bind the LAN by default so the laptop can reach this host; ASPNETCORE_URLS,
// --urls or a Kestrel:Endpoints section take precedence.
if (string.IsNullOrEmpty(builder.Configuration[WebHostDefaults.ServerUrlsKey]) && !builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
{
	builder.WebHost.UseUrls("http://0.0.0.0:8080");
}

builder.Services
	.AddOptions<MemoriaOptions>()
	.Bind(builder.Configuration.GetSection(MemoriaOptions.SectionName))
	.ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<MemoriaOptions>, MemoriaOptionsValidator>();

builder.Services.AddMemoriaQmdClient();

// The mode picks which authentication handler is registered, so it is decided
// at build time. Everything in the section is also validated with the rest.
IConfigurationSection memoriaSection = builder.Configuration.GetSection(MemoriaOptions.SectionName);
builder.Services.AddMemoriaAuth(memoriaSection.GetSection(nameof(MemoriaOptions.Auth)).Get<AuthOptions>() ?? new AuthOptions());
builder.Services.AddScoped<ScopedCorpus>();
builder.Services.AddMemoriaIngest();

builder.Services.AddProblemDetails(options =>
	options.CustomizeProblemDetails = context =>
		context.ProblemDetails.Extensions["traceId"] = RequestLoggingMiddleware.CurrentTraceId(context.HttpContext));
builder.Services.AddExceptionHandler<MemoriaExceptionHandler>();

// MCP is decided at build time because the endpoint and its services are
// registered together; the section is also validated with the rest of the options.
McpOptions mcpOptions = memoriaSection.GetSection(nameof(MemoriaOptions.Mcp)).Get<McpOptions>() ?? new McpOptions();
if (mcpOptions.Enabled)
{
	builder.Services.AddMemoriaMcpServer();
}

builder.Services.AddOpenApi("v1", options =>
{
	// 3.0 rather than 3.1: oapi-codegen (the Go CLI's generator) reads 3.0.
	options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;
	options.AddDocumentTransformer<MemoriaDocumentTransformer>();
	options.AddOperationTransformer<SecurityOperationTransformer>();
	options.AddSchemaTransformer<PlainNumberSchemaTransformer>();
	options.AddDocumentTransformer<NullableReferenceDocumentTransformer>();
});

WebApplication app = builder.Build();

await app.InitializeMemoriaAuthAsync();
app.WarnAboutRedactionSettings();

// First, so logs and the failed-authentication limit see the real client.
app.UseMemoriaForwardedHeaders(app.Services.GetRequiredService<IOptions<MemoriaOptions>>().Value.Auth.TrustedProxies);
app.UseMiddleware<ForwardedHeaderWarning>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRouting();
app.UseMemoriaAuth();

// The spec describes the public surface and carries no data, so it is anonymous.
app.MapOpenApi().AllowAnonymous();
app.MapHealth();
app.MapV1();
app.MapAdminKeys();
app.MapIngest();
if (mcpOptions.Enabled)
{
	app.MapMemoriaMcp(mcpOptions);
}

await app.RunAsync();
return 0;

// Exposed so WebApplicationFactory<Program> can boot the app in tests.
public partial class Program;
