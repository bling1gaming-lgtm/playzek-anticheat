using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Playzek.Api;
using Playzek.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => { o.UseUtcTimestamp = true; o.TimestampFormat = "O"; });

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPlayzekDatabase(builder.Configuration);
builder.Services.AddPlayzekAuthentication(builder.Configuration);
builder.Services.AddScoped<IntegrityReportService>();
builder.Services.AddProblemDetails();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>, TimeProvider>((bearer, jwt, time) =>
    {
        bearer.MapInboundClaims = false;
        bearer.TokenValidationParameters =
            TokenService.CreateValidationParameters(jwt.Value, TokenService.CreateKey(jwt.Value.SigningKey), time);
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("player", p => p.RequireAuthenticatedUser()
        .RequireClaim(TokenService.TokenTypeClaim, TokenService.PlayerTokenType));

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // În producție API-ul stă în spatele Nginx; doar proxy-urile listate sunt crezute.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in builder.Configuration.GetSection("Playzek:TrustedProxies").Get<string[]>() ?? [])
        o.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
});

var rateLimits = builder.Configuration.GetSection("Playzek:RateLimits");
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimits.GetValue("AuthPerMinute", 10),
            Window = TimeSpan.FromMinutes(1),
        }));
    o.AddPolicy("client", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirstValue("sub") ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimits.GetValue("ClientPerMinute", 120),
            Window = TimeSpan.FromMinutes(1),
        }));
    o.AddPolicy("game-server", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Request.Headers[GameServerService.ApiKeyHeader].ToString() is { Length: > 12 } k ? k[..12] : "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimits.GetValue("GameServerPerMinute", 1200),
            Window = TimeSpan.FromMinutes(1),
        }));
});

var app = builder.Build();

// Comenzi de operator (migrate, create-server) — nu există endpoint-uri admin neautentificate.
if (args.Length > 0 && await CliCommands.TryRunAsync(app, args)) return;

await app.Services.InitializeDatabaseAsync(app.Configuration);

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthEndpoints();
app.MapPlayerEndpoints();
app.MapGameServerEndpoints();

app.Run();

public partial class Program;
