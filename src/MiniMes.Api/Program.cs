using MiniMes.Api.Middleware;
using MiniMes.Api.OpenApi;
using MiniMes.Infrastructure;
using MiniMes.Infrastructure.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

const string LogTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}";

builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration) // levels come from appsettings
    .Enrich.FromLogContext()                   // CorrelationId inside a request
    .Enrich.WithProperty("CorrelationId", "-") // added only if still absent (startup logs)
    .WriteTo.Console(outputTemplate: LogTemplate)
    .WriteTo.File("logs/minimes-.log", outputTemplate: LogTemplate,
        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30));

builder.Services.AddControllers();
builder.Services.AddOpenApi(o => o.AddDocumentTransformer<BearerSecuritySchemeTransformer>());
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddHealthChecks();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

// Order matters: the id first so every later log line (including errors) carries it.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();                                                            // /openapi/v1.json
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "MiniMes API")); // /swagger

    // Migrations are applied by hand (dotnet ef database update); only seed here.
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<DevSeeder>().SeedAsync();
}

app.MapControllers();
app.MapHealthChecks("/health"); // anonymous: used by monitors, no token

app.Run();
