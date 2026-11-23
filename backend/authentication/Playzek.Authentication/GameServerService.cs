using Microsoft.EntityFrameworkCore;
using Playzek.Data;

namespace Playzek.Authentication;

public sealed record CreatedGameServer(Guid ServerId, string Name, string Game, string ApiKey);
public sealed record AuthorizePlayerRequest(string? SteamId, string? PlayerName);
public sealed record PlayerAuthorization(bool Allowed, string Code, string Message, string MessageEn,
    Guid? GameSessionId, DateTime? SessionExpiresAt);

/// <summary>
/// Autentificare server-la-server (cheie API per server de joc) și autorizarea jucătorilor.
/// Serverul de joc NU primește token-ul de sesiune al clientului: îl identifică pe jucător după SteamID
/// (informație autoritativă pe partea de server), iar backend-ul decide dacă există o sesiune validă.
/// </summary>
public sealed class GameServerService(
    PlayzekDbContext db,
    AuditWriter audit,
    AntiCheatSessionService sessions,
    TimeProvider time)
{
    public const string ApiKeyHeader = "X-Playzek-Server-Key";
    private const string KeyPrefix = "pzs_";

    public async Task<ServiceResult<CreatedGameServer>> CreateAsync(string? name, string? game, string? address,
        string actor, CancellationToken ct)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 64)
            return new ServiceError(ErrorKind.Validation, "invalid_name", "Numele serverului are 1–64 caractere.");
        if (!Enum.TryParse<SupportedGame>(game, ignoreCase: true, out var g) || !Enum.IsDefined(g))
            return new ServiceError(ErrorKind.Validation, "invalid_game", "Joc necunoscut. Valori acceptate: cs16, cs2.");

        // Format cheie: pzs_<8 caractere id public>_<secret 256 biți>. Se afișează o singură dată.
        var publicId = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
        var apiKey = $"{KeyPrefix}{publicId}_{Crypto.NewOpaqueToken()}";
        var server = new GameServer
        {
            Id = Guid.NewGuid(),
            Name = name,
            Game = g,
            Address = address,
            ApiKeyPrefix = KeyPrefix + publicId,
            ApiKeyHash = Crypto.Sha256Hex(apiKey),
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.GameServers.Add(server);
        audit.Add(ActorType.Administrator, actor, "game_server.created", "game_server", server.Id.ToString());
        await db.SaveChangesAsync(ct);
        return ServiceResult<CreatedGameServer>.Ok(new CreatedGameServer(server.Id, name, g.ToString(), apiKey));
    }

    public async Task<GameServer?> AuthenticateAsync(string? apiKey, string? pluginVersion, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length > 128 || !apiKey.StartsWith(KeyPrefix, StringComparison.Ordinal))
            return null;
        var hash = Crypto.Sha256Hex(apiKey);
        var server = await db.GameServers.FirstOrDefaultAsync(s => s.ApiKeyHash == hash, ct);
        if (server is null || server.Status != GameServerStatus.Active) return null;

        server.LastSeenAt = time.GetUtcNow().UtcDateTime;
        if (!string.IsNullOrEmpty(pluginVersion) && pluginVersion.Length <= 32) server.PluginVersion = pluginVersion;
        await db.SaveChangesAsync(ct);
        return server;
    }

    public async Task<ServiceResult<PlayerAuthorization>> AuthorizePlayerAsync(GameServer server, AuthorizePlayerRequest req,
        CancellationToken ct)
    {
        var steamId = InputRules.NormalizeSteamId(req.SteamId);
        if (steamId is null)
            return new ServiceError(ErrorKind.Validation, "invalid_steam_id",
                "SteamID invalid. Serverul trebuie să trimită SteamID64 sau STEAM_X:Y:Z.");

        var now = time.GetUtcNow().UtcDateTime;
        var profile = await db.PlayerProfiles.FirstOrDefaultAsync(p => p.SteamId64 == steamId, ct);

        DenialReasons.Reason reason;
        AntiCheatSession? session = null;
        if (profile is null)
        {
            reason = DenialReasons.AccountNotLinked;
        }
        else
        {
            session = await db.AntiCheatSessions.Include(s => s.User).Include(s => s.Device)
                .Where(s => s.UserId == profile.UserId && s.Status == AntiCheatSessionStatus.Active)
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (session is null)
            {
                // Fără sesiune: verificăm totuși ban-ul, ca mesajul să fie cel corect.
                var ban = await new BanLookup(db).FindActiveAsync(profile.UserId, now, ct);
                reason = ban is not null ? DenialReasons.Banned : DenialReasons.NoActiveSession;
            }
            else
            {
                reason = await sessions.EvaluateAsync(session, now, ct);
                if (reason == DenialReasons.Ok && session.Game is { } g && g != server.Game)
                    reason = DenialReasons.GameMismatch;
                SessionPolicy.ApplyExpiry(session, reason, now);
            }
        }

        var allowed = reason == DenialReasons.Ok;
        var gameSession = new GameSession
        {
            Id = Guid.NewGuid(),
            GameServerId = server.Id,
            UserId = profile?.UserId,
            AntiCheatSessionId = session?.Id,
            SteamId64 = steamId,
            Authorized = allowed,
            DecisionCode = reason.Code,
            JoinedAt = now,
        };
        db.GameSessions.Add(gameSession);
        audit.Add(ActorType.GameServer, server.Id.ToString(), allowed ? "player.authorized" : "player.denied",
            "user", profile?.UserId.ToString(), details: $"steam_id={steamId}; code={reason.Code}");
        await db.SaveChangesAsync(ct);

        return ServiceResult<PlayerAuthorization>.Ok(new PlayerAuthorization(allowed, reason.Code, reason.MessageRo,
            reason.MessageEn, gameSession.Id, allowed ? session!.ExpiresAt : null));
    }

    /// <summary>Jucătorul a ieșit de pe server.</summary>
    public async Task<bool> RecordDisconnectAsync(GameServer server, Guid gameSessionId, CancellationToken ct)
    {
        var gs = await db.GameSessions.FirstOrDefaultAsync(x => x.Id == gameSessionId && x.GameServerId == server.Id, ct);
        if (gs is null) return false;
        gs.LeftAt ??= time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
