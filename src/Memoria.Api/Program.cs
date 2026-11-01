using Memoria.Api.Configuration;
using Memoria.Api.Endpoints;
using Memoria.Api.Http;
using Memoria.Api.OpenApi;
using Memoria.Api.Sidecar;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Machine-local overrides (the sidecar secret, a LAN base URL) live in a file
// .gitignore excludes. Loaded last so it wins over appsettings.*.json.
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

// Bind the LAN by default so the laptop can reach this host; ASPNETCORE_URLS,
// --urls or a Kestrel:Endpoints section take precedence. Nothing routes this
// beyond the LAN until MEMORIA-7 and MEMORIA-3 land.
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
builder.Services.AddScoped<ICallerScope, UnrestrictedCallerScope>();

builder.Services.AddProblemDetails(options =>
	options.CustomizeProblemDetails = context =>
		context.ProblemDetails.Extensions["traceId"] = RequestLoggingMiddleware.CurrentTraceId(context.HttpContext));
builder.Services.AddExceptionHandler<MemoriaExceptionHandler>();

builder.Services.AddOpenApi("v1", options =>
{
	// 3.0 rather than 3.1: oapi-codegen (the Go CLI's generator) reads 3.0.
	options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;
	options.AddDocumentTransformer<MemoriaDocumentTransformer>();
});

WebApplication app = builder.Build();

app.UseMiddleware<RequestLoggingMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapHealth();
app.MapV1();

app.Run();

// Exposed so WebApplicationFactory<Program> can boot the app in tests.
public partial class Program;
