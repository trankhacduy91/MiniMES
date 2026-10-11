using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MiniMes.Api.Auth;
using MiniMes.Api.Middleware;
using MiniMes.Api.OpenApi;
using MiniMes.Application.Common;
using MiniMes.Infrastructure;
using MiniMes.Infrastructure.Auth;
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

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
// Configured from IOptions<JwtOptions> lazily, so tools like `dotnet ef` do not need Jwt:Key.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((o, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        o.MapInboundClaims = false; // keep "sub"/"role" as-is instead of long ClaimTypes URIs
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            RoleClaimType = JwtTokenService.RoleClaim,
            NameClaimType = "name",
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });

// No anonymous access in a factory: every endpoint needs a token unless marked [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

var app = builder.Build();

// Order matters: the id first so every later log line (including errors) carries it.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();                                           // /openapi/v1.json
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "MiniMes API")); // /swagger

    // Migrations are applied by hand (dotnet ef database update); only seed here.
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<DevSeeder>().SeedAsync();
}

app.UseAuthentication(); // must come before UseAuthorization
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous(); // used by monitors, no token

app.Run();
