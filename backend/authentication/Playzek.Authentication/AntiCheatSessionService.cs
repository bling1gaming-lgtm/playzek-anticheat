using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Playzek.Data;

namespace Playzek.Authentication;

public sealed record StartSessionRequest(Guid DeviceId, string? ClientVersion, string? Game);
public sealed record SessionStarted(Guid SessionId, string SessionToken, DateTime ExpiresAt,
    int HeartbeatIntervalSeconds, int HeartbeatGraceSeconds);
public sealed record SessionStatus(Guid SessionId, bool Valid, string Code, string Message, DateTime ExpiresAt,
    DateTime? LastHeartbeatAt);

/// <summary>
/// Sesiuni anti-cheat. Token-ul de sesiune e opac și stocat doar ca hash; validitatea e decisă
/// exclusiv de server (SessionPolicy), nu de ce afișează clientul.
/// </summary>
public sealed class AntiCheatSessionService(
    PlayzekDbContext db,
    AuditWriter audit,
    BanLookup bans,
    IOptions<SessionPolicyOptions> policyOptions,
    TimeProvider time)
{
    private SessionPolicyOptions Policy => policyOptions.Value;
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<ServiceResult<SessionStarted>> StartAsync(Guid userId, StartSessionRequest req, string? ip,
        CancellationToken ct)
    {
        if (!InputRules.IsValidVersion(req.ClientVersion))
            return new ServiceError(ErrorKind.Validation, "invalid_client_version", "Versiune de client invalidă.");
        SupportedGame? game = null;
        if (!string.IsNullOrEmpty(req.Game))
        {
            if (!Enum.TryParse<SupportedGame>(req.Game, ignoreCase: true, out var g) || !Enum.IsDefined(g))
                return new ServiceError(ErrorKind.Validation, "invalid_game", "Joc necunoscut. Valori acceptate: cs16, cs2.");
            game = g;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.Status != AccountStatus.Active)
            return new ServiceError(ErrorKind.Forbidden, DenialReasons.AccountInactive.Code, DenialReasons.AccountInactive.MessageRo);

        var device = await db.RegisteredDevices.FirstOrDefaultAsync(d => d.Id == req.DeviceId && d.UserId == userId, ct);
        if (device is null)
            return new ServiceError(ErrorKind.NotFound, "device_not_registered",
                "Dispozitivul nu este înregistrat pe contul tău. Înregistrează-l mai întâi.");
        if (device.Status != DeviceStatus.Active)
            return new ServiceError(ErrorKind.Forbidden, DenialReasons.DeviceNotActive.Code, DenialReasons.DeviceNotActive.MessageRo);

        if (!SessionPolicy.IsVersionAtLeast(req.ClientVersion!, Policy.MinimumClientVersion))
            return new ServiceError(ErrorKind.Forbidden, "client_outdated",
                $"Versiunea {req.ClientVersion} a aplicației nu mai este acceptată. Actualizează la cel puțin {Policy.MinimumClientVersion}.");
        if (await db.ClientVersions.AnyAsync(v => v.Version == req.ClientVersion && v.IsBlocked, ct))
            return new ServiceError(ErrorKind.Forbidden, "client_version_blocked",
                $"Versiunea {req.ClientVersion} a fost retrasă. Actualizează aplicația.");

        var now = Now;
        if (await bans.FindActiveAsync(userId, now, ct) is { } ban)
            return new ServiceError(ErrorKind.Forbidden, DenialReasons.Banned.Code, BanLookup.Describe(ban));

        // O singură sesiune activă per jucător: cea nouă o înlocuiește pe cea veche.
        var previous = await db.AntiCheatSessions
            .Where(s => s.UserId == userId && s.Status == AntiCheatSessionStatus.Active).ToListAsync(ct);
        foreach (var s in previous)
        {
            s.Status = AntiCheatSessionStatus.Ended;
            s.EndedAt = now;
            s.EndReason = "superseded";
        }

        var token = Crypto.NewOpaqueToken();
        var session = new AntiCheatSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DeviceId = device.Id,
            TokenHash = Crypto.Sha256Hex(token),
            Game = game,
            ClientVersion = req.ClientVersion!,
            CreatedAt = now,
            ExpiresAt = now.Add(Policy.SessionLifetime),
            LastHeartbeatAt = now,
        };
        db.AntiCheatSessions.Add(session);
        device.LastSeenAt = now;
        audit.Add(ActorType.Player, userId.ToString(), "session.started", "anti_cheat_session", session.Id.ToString(), ip);
        await db.SaveChangesAsync(ct);

        return ServiceResult<SessionStarted>.Ok(new SessionStarted(session.Id, token, session.ExpiresAt,
            (int)Policy.HeartbeatInterval.TotalSeconds, (int)Policy.HeartbeatGracePeriod.TotalSeconds));
    }

    /// <summary>Heartbeat de la client. Sesiunea trebuie să aparțină utilizatorului din JWT.</summary>
    public async Task<ServiceResult<SessionStatus>> HeartbeatAsync(Guid userId, string? sessionToken, CancellationToken ct)
    {
        var loaded = await LoadAsync(sessionToken, ct);
        if (loaded is null || loaded.UserId != userId)
            return new ServiceError(ErrorKind.Unauthorized, DenialReasons.InvalidToken.Code, DenialReasons.InvalidToken.MessageRo);

        var now = Now;
        var reason = await EvaluateAsync(loaded, now, ct);
        if (reason == DenialReasons.Ok)
        {
            loaded.LastHeartbeatAt = now;
            loaded.HeartbeatCount++;
        }
        else
        {
            SessionPolicy.ApplyExpiry(loaded, reason, now);
        }
        await db.SaveChangesAsync(ct);
        return ServiceResult<SessionStatus>.Ok(ToStatus(loaded, reason));
    }

    /// <summary>Reînnoire securizată: token nou, cel vechi devine invalid imediat. Merge doar pe o sesiune încă validă.</summary>
    public async Task<ServiceResult<SessionStarted>> RenewAsync(Guid userId, string? sessionToken, string? ip, CancellationToken ct)
    {
        var loaded = await LoadAsync(sessionToken, ct);
        if (loaded is null || loaded.UserId != userId)
            return new ServiceError(ErrorKind.Unauthorized, DenialReasons.InvalidToken.Code, DenialReasons.InvalidToken.MessageRo);

        var now = Now;
        var reason = await EvaluateAsync(loaded, now, ct);
        if (reason != DenialReasons.Ok)
        {
            SessionPolicy.ApplyExpiry(loaded, reason, now);
            await db.SaveChangesAsync(ct);
            return new ServiceError(ErrorKind.Forbidden, reason.Code, reason.MessageRo);
        }

        var token = Crypto.NewOpaqueToken();
        loaded.TokenHash = Crypto.Sha256Hex(token);
        loaded.ExpiresAt = now.Add(Policy.SessionLifetime);
        loaded.LastHeartbeatAt = now;
        audit.Add(ActorType.Player, userId.ToString(), "session.renewed", "anti_cheat_session", loaded.Id.ToString(), ip);
        await db.SaveChangesAsync(ct);
        return ServiceResult<SessionStarted>.Ok(new SessionStarted(loaded.Id, token, loaded.ExpiresAt,
            (int)Policy.HeartbeatInterval.TotalSeconds, (int)Policy.HeartbeatGracePeriod.TotalSeconds));
    }

    /// <summary>Jucătorul oprește protecția din aplicație.</summary>
    public async Task<ServiceResult<bool>> EndAsync(Guid userId, string? sessionToken, string? ip, CancellationToken ct)
    {
        var loaded = await LoadAsync(sessionToken, ct);
        if (loaded is null || loaded.UserId != userId)
            return new ServiceError(ErrorKind.Unauthorized, DenialReasons.InvalidToken.Code, DenialReasons.InvalidToken.MessageRo);
        if (loaded.Status == AntiCheatSessionStatus.Active)
        {
            loaded.Status = AntiCheatSessionStatus.Ended;
            loaded.EndedAt = Now;
            loaded.EndReason = "client_stopped";
            audit.Add(ActorType.Player, userId.ToString(), "session.ended", "anti_cheat_session", loaded.Id.ToString(), ip);
            await db.SaveChangesAsync(ct);
        }
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>Regulile complete: politica de sesiune + ban activ.</summary>
    public async Task<DenialReasons.Reason> EvaluateAsync(AntiCheatSession session, DateTime now, CancellationToken ct)
    {
        var reason = SessionPolicy.Evaluate(session, session.User!, session.Device!, now, Policy);
        if (reason != DenialReasons.Ok) return reason;
        if (await bans.FindActiveAsync(session.UserId, now, ct) is not null) return DenialReasons.Banned;
        return reason;
    }

    private async Task<AntiCheatSession?> LoadAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128) return null;
        var hash = Crypto.Sha256Hex(token);
        return await db.AntiCheatSessions.Include(s => s.User).Include(s => s.Device)
            .FirstOrDefaultAsync(s => s.TokenHash == hash, ct);
    }

    private static SessionStatus ToStatus(AntiCheatSession s, DenialReasons.Reason reason) =>
        new(s.Id, reason == DenialReasons.Ok, reason.Code, reason.MessageRo, s.ExpiresAt, s.LastHeartbeatAt);
}

public sealed class BanLookup(PlayzekDbContext db)
{
    public Task<Ban?> FindActiveAsync(Guid userId, DateTime now, CancellationToken ct) =>
        db.Bans.Where(b => b.UserId == userId && b.LiftedAt == null &&
                           (b.Type == BanType.Permanent || b.ExpiresAt > now))
            .OrderByDescending(b => b.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public static string Describe(Ban ban) => ban.Type == BanType.Permanent
        ? $"Cont suspendat permanent pe serverele PlayZek. Motiv: {ban.Reason}"
        : $"Cont suspendat până la {ban.ExpiresAt:yyyy-MM-dd HH:mm} UTC. Motiv: {ban.Reason}";
}
