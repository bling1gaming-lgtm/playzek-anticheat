using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Playzek.Data;

namespace Playzek.Authentication;

public sealed record RegisterRequest(string? Username, string? Password, string? SteamId);
public sealed record RegisteredUser(Guid UserId, string Username, string? SteamId64);
public sealed record LoginResult(Guid UserId, string Username, string AccessToken, DateTime AccessTokenExpiresAt,
    string RefreshToken, DateTime RefreshTokenExpiresAt);

/// <summary>Înregistrare, login, reînnoire și logout pentru jucători.</summary>
public sealed class PlayerAuthService(
    PlayzekDbContext db,
    PasswordHashing hashing,
    TokenService tokens,
    AuditWriter audit,
    IOptions<JwtOptions> jwtOptions,
    IOptions<SecurityPolicyOptions> securityOptions,
    TimeProvider time)
{
    // Hash calculat o singură dată; verificat când utilizatorul nu există, ca timpul de răspuns
    // să nu dezvăluie dacă un nume de cont există.
    private static readonly string DummyHash = new PasswordHashing().Hash("playzek-dummy-password");

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<ServiceResult<RegisteredUser>> RegisterAsync(RegisterRequest req, string? ip, CancellationToken ct)
    {
        if (!InputRules.IsValidUsername(req.Username))
            return new ServiceError(ErrorKind.Validation, "invalid_username",
                "Numele de utilizator trebuie să aibă 3–32 caractere: litere, cifre, „_”, „.” sau „-”.");
        if (InputRules.ValidatePassword(req.Password) is { } pwError) return pwError;

        string? steamId = null;
        if (!string.IsNullOrWhiteSpace(req.SteamId))
        {
            steamId = InputRules.NormalizeSteamId(req.SteamId);
            if (steamId is null)
                return new ServiceError(ErrorKind.Validation, "invalid_steam_id",
                    "SteamID invalid. Folosește SteamID64 (17 cifre) sau formatul STEAM_0:X:Y.");
        }

        var normalized = req.Username!.ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized, ct))
            return new ServiceError(ErrorKind.Conflict, "username_taken", "Numele de utilizator este deja folosit.");
        if (steamId is not null && await db.PlayerProfiles.AnyAsync(p => p.SteamId64 == steamId, ct))
            return new ServiceError(ErrorKind.Conflict, "steam_id_taken",
                "Acest SteamID este deja asociat altui cont PlayZek.");

        var now = Now;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = req.Username!,
            NormalizedUsername = normalized,
            PasswordHash = hashing.Hash(req.Password!),
            CreatedAt = now,
            UpdatedAt = now,
            Profile = new PlayerProfile { DisplayName = req.Username!, SteamId64 = steamId, CreatedAt = now, UpdatedAt = now },
        };
        db.Users.Add(user);
        audit.Add(ActorType.Player, user.Id.ToString(), "player.registered", "user", user.Id.ToString(), ip);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Cursă între două înregistrări simultane cu același nume / SteamID — indexul unic decide.
            return new ServiceError(ErrorKind.Conflict, "username_or_steam_id_taken",
                "Numele de utilizator sau SteamID-ul este deja folosit.");
        }
        return ServiceResult<RegisteredUser>.Ok(new RegisteredUser(user.Id, user.Username, steamId));
    }

    public async Task<ServiceResult<LoginResult>> LoginAsync(string? username, string? password, string? ip, CancellationToken ct)
    {
        var invalid = new ServiceError(ErrorKind.Unauthorized, "invalid_credentials",
            "Nume de utilizator sau parolă greșită.");
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) ||
            password.Length > InputRules.PasswordMaxLength)
            return invalid;

        var normalized = username.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, ct);
        if (user is null)
        {
            hashing.Verify(DummyHash, password);
            return invalid;
        }

        var now = Now;
        if (user.LockoutUntil is { } until && until > now)
            return new ServiceError(ErrorKind.Locked, "account_locked",
                $"Prea multe încercări greșite. Încearcă din nou după {until:HH:mm} UTC.");

        if (!hashing.Verify(user.PasswordHash, password))
        {
            var policy = securityOptions.Value;
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= policy.MaxFailedLogins)
            {
                user.LockoutUntil = now.Add(policy.LockoutDuration);
                user.FailedLoginCount = 0;
                audit.Add(ActorType.Player, user.Id.ToString(), "player.locked_out", "user", user.Id.ToString(), ip);
            }
            audit.Add(ActorType.Player, user.Id.ToString(), "player.login_failed", "user", user.Id.ToString(), ip);
            await db.SaveChangesAsync(ct);
            return invalid;
        }

        if (user.Status != AccountStatus.Active)
            return new ServiceError(ErrorKind.Forbidden, "account_" + user.Status.ToString().ToLowerInvariant(),
                "Contul nu este activ. Contactează echipa PlayZek.");

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.LastLoginAt = now;
        var refresh = IssueRefreshToken(user.Id, now, out var refreshEntity);
        audit.Add(ActorType.Player, user.Id.ToString(), "player.login", "user", user.Id.ToString(), ip);
        await db.SaveChangesAsync(ct);

        var access = tokens.CreatePlayerToken(user.Id, user.Username);
        return ServiceResult<LoginResult>.Ok(new LoginResult(user.Id, user.Username, access.Token, access.ExpiresAt,
            refresh, refreshEntity.ExpiresAt));
    }

    /// <summary>
    /// Rotație: fiecare refresh token e folosit o singură dată. Reutilizarea unui token deja rotit
    /// înseamnă probabil furt — revocăm toate token-urile utilizatorului.
    /// </summary>
    public async Task<ServiceResult<LoginResult>> RefreshAsync(string? refreshToken, string? ip, CancellationToken ct)
    {
        var invalid = new ServiceError(ErrorKind.Unauthorized, "invalid_refresh_token",
            "Sesiunea de login a expirat. Autentifică-te din nou.");
        if (string.IsNullOrEmpty(refreshToken) || refreshToken.Length > 128) return invalid;

        var hash = Crypto.Sha256Hex(refreshToken);
        var stored = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored?.User is null) return invalid;

        var now = Now;
        if (stored.RevokedAt is not null)
        {
            if (stored.ReplacedByTokenId is not null)
            {
                await RevokeAllForUserAsync(stored.UserId, "reuse_detected", now, ct);
                audit.Add(ActorType.System, null, "player.refresh_token_reuse", "user", stored.UserId.ToString(), ip);
                await db.SaveChangesAsync(ct);
            }
            return invalid;
        }
        if (stored.ExpiresAt <= now) return invalid;
        if (stored.User.Status != AccountStatus.Active)
            return new ServiceError(ErrorKind.Forbidden, "account_inactive", "Contul nu este activ.");

        var newToken = IssueRefreshToken(stored.UserId, now, out var newEntity);
        stored.RevokedAt = now;
        stored.RevokedReason = "rotated";
        stored.ReplacedByTokenId = newEntity.Id;
        await db.SaveChangesAsync(ct);

        var access = tokens.CreatePlayerToken(stored.UserId, stored.User.Username);
        return ServiceResult<LoginResult>.Ok(new LoginResult(stored.UserId, stored.User.Username, access.Token,
            access.ExpiresAt, newToken, newEntity.ExpiresAt));
    }

    public async Task LogoutAsync(string? refreshToken, string? ip, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken) || refreshToken.Length > 128) return;
        var hash = Crypto.Sha256Hex(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null || stored.RevokedAt is not null) return;
        stored.RevokedAt = Now;
        stored.RevokedReason = "logout";
        audit.Add(ActorType.Player, stored.UserId.ToString(), "player.logout", "user", stored.UserId.ToString(), ip);
        await db.SaveChangesAsync(ct);
    }

    private string IssueRefreshToken(Guid userId, DateTime now, out RefreshToken entity)
    {
        var token = Crypto.NewOpaqueToken();
        entity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Crypto.Sha256Hex(token),
            CreatedAt = now,
            ExpiresAt = now.Add(jwtOptions.Value.RefreshTokenLifetime),
        };
        db.RefreshTokens.Add(entity);
        return token;
    }

    private async Task RevokeAllForUserAsync(Guid userId, string reason, DateTime now, CancellationToken ct)
    {
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in active)
        {
            t.RevokedAt = now;
            t.RevokedReason = reason;
        }
    }
}
