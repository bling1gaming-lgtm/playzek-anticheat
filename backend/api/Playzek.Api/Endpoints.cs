using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Playzek.Authentication;
using Playzek.Data;

namespace Playzek.Api;

public static class Endpoints
{
    public const string SessionHeader = "X-Playzek-Session";

    public static IResult ToHttp<T>(this ServiceResult<T> r, Func<T, object>? map = null) =>
        r.Succeeded ? Results.Ok(map is null ? r.Value : map(r.Value!)) : r.Error!.ToHttp();

    public static IResult ToHttp(this ServiceError e) => Results.Problem(
        statusCode: e.Kind switch
        {
            ErrorKind.Validation => 400,
            ErrorKind.Unauthorized => 401,
            ErrorKind.Forbidden => 403,
            ErrorKind.NotFound => 404,
            ErrorKind.Conflict => 409,
            ErrorKind.Locked => 423,
            _ => 400,
        },
        title: e.Code,
        detail: e.Message,
        extensions: new Dictionary<string, object?> { ["code"] = e.Code });

    private static Guid UserId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub")!);
    private static string? Ip(this HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (PlayzekDbContext db, CancellationToken ct) =>
        {
            var dbOk = await db.Database.CanConnectAsync(ct);
            return dbOk
                ? Results.Ok(new { status = "ready", database = "ok" })
                : Results.Json(new { status = "not_ready", database = "unreachable" }, statusCode: 503);
        });
    }

    public sealed record LoginBody(string? Username, string? Password);
    public sealed record RefreshBody(string? RefreshToken);

    public static void MapPlayerEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth").RequireRateLimiting("auth");
        auth.MapPost("/register", async (RegisterRequest body, PlayerAuthService svc, HttpContext ctx, CancellationToken ct) =>
            (await svc.RegisterAsync(body, ctx.Ip(), ct)).ToHttp());
        auth.MapPost("/login", async (LoginBody body, PlayerAuthService svc, HttpContext ctx, CancellationToken ct) =>
            (await svc.LoginAsync(body.Username, body.Password, ctx.Ip(), ct)).ToHttp());
        auth.MapPost("/refresh", async (RefreshBody body, PlayerAuthService svc, HttpContext ctx, CancellationToken ct) =>
            (await svc.RefreshAsync(body.RefreshToken, ctx.Ip(), ct)).ToHttp());
        auth.MapPost("/logout", async (RefreshBody body, PlayerAuthService svc, HttpContext ctx, CancellationToken ct) =>
        {
            await svc.LogoutAsync(body.RefreshToken, ctx.Ip(), ct);
            return Results.NoContent();
        });

        var player = app.MapGroup("/api/v1").RequireAuthorization("player").RequireRateLimiting("client");

        player.MapGet("/me", async (ClaimsPrincipal user, PlayzekDbContext db, CancellationToken ct) =>
        {
            var id = user.UserId();
            var u = await db.Users.Include(x => x.Profile).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (u is null) return Results.NotFound();
            var activeBan = await new BanLookup(db).FindActiveAsync(id, DateTime.UtcNow, ct);
            return Results.Ok(new
            {
                userId = u.Id,
                username = u.Username,
                steamId64 = u.Profile?.SteamId64,
                accountStatus = u.Status.ToString(),
                banned = activeBan is not null,
                banMessage = activeBan is null ? null : BanLookup.Describe(activeBan),
                createdAt = u.CreatedAt,
            });
        });

        player.MapPost("/devices", async (DeviceRegistrationRequest body, ClaimsPrincipal user, DeviceService svc,
            HttpContext ctx, CancellationToken ct) => (await svc.RegisterAsync(user.UserId(), body, ctx.Ip(), ct)).ToHttp());
        player.MapGet("/devices", async (ClaimsPrincipal user, DeviceService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(user.UserId(), ct)));
        player.MapDelete("/devices/{deviceId:guid}", async (Guid deviceId, ClaimsPrincipal user, DeviceService svc,
            HttpContext ctx, CancellationToken ct) => (await svc.RevokeAsync(user.UserId(), deviceId, ctx.Ip(), ct)).ToHttp());

        var sessions = player.MapGroup("/anticheat/sessions");
        sessions.MapPost("/", async (StartSessionRequest body, ClaimsPrincipal user, AntiCheatSessionService svc,
            HttpContext ctx, CancellationToken ct) => (await svc.StartAsync(user.UserId(), body, ctx.Ip(), ct)).ToHttp());
        sessions.MapPost("/heartbeat", async (ClaimsPrincipal user, AntiCheatSessionService svc, HttpContext ctx,
            CancellationToken ct) => (await svc.HeartbeatAsync(user.UserId(), ctx.Request.Headers[SessionHeader], ct)).ToHttp());
        sessions.MapPost("/renew", async (ClaimsPrincipal user, AntiCheatSessionService svc, HttpContext ctx,
            CancellationToken ct) => (await svc.RenewAsync(user.UserId(), ctx.Request.Headers[SessionHeader], ctx.Ip(), ct)).ToHttp());
        sessions.MapPost("/end", async (ClaimsPrincipal user, AntiCheatSessionService svc, HttpContext ctx,
            CancellationToken ct) =>
        {
            var r = await svc.EndAsync(user.UserId(), ctx.Request.Headers[SessionHeader], ctx.Ip(), ct);
            return r.Succeeded ? Results.NoContent() : r.Error!.ToHttp();
        });

        player.MapPost("/integrity-reports", async (IntegrityReportBatch body, ClaimsPrincipal user,
            IntegrityReportService svc, HttpContext ctx, CancellationToken ct) =>
            (await svc.SubmitAsync(user.UserId(), ctx.Request.Headers[SessionHeader], body, ct)).ToHttp());
    }

    public sealed record DisconnectBody(Guid GameSessionId);

    public static void MapGameServerEndpoints(this WebApplication app)
    {
        var servers = app.MapGroup("/api/v1/game-server").RequireRateLimiting("game-server");
        servers.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var svc = http.RequestServices.GetRequiredService<GameServerService>();
            var server = await svc.AuthenticateAsync(http.Request.Headers[GameServerService.ApiKeyHeader],
                http.Request.Headers["X-Playzek-Plugin-Version"], http.RequestAborted);
            if (server is null)
                return new ServiceError(ErrorKind.Unauthorized, "invalid_server_key",
                    "Cheia API a serverului de joc lipsește, e greșită sau serverul e dezactivat.").ToHttp();
            http.Items[nameof(GameServer)] = server;
            return await next(ctx);
        });

        static GameServer Server(HttpContext ctx) => (GameServer)ctx.Items[nameof(GameServer)]!;

        servers.MapGet("/ping", (HttpContext ctx) =>
        {
            var s = Server(ctx);
            return Results.Ok(new { serverId = s.Id, name = s.Name, game = s.Game.ToString(), serverTime = DateTime.UtcNow });
        });
        servers.MapPost("/players/authorize", async (AuthorizePlayerRequest body, GameServerService svc, HttpContext ctx,
            CancellationToken ct) => (await svc.AuthorizePlayerAsync(Server(ctx), body, ct)).ToHttp());
        servers.MapPost("/players/disconnect", async (DisconnectBody body, GameServerService svc, HttpContext ctx,
            CancellationToken ct) => await svc.RecordDisconnectAsync(Server(ctx), body.GameSessionId, ct)
            ? Results.NoContent()
            : Results.NotFound());
    }
}
