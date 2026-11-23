namespace Playzek.Data;

// Toate momentele de timp sunt stocate în UTC (timestamptz în PostgreSQL).
// Enum-urile sunt stocate ca text, ca rândurile să fie lizibile la audit.

public enum AccountStatus { Active, Suspended, Disabled }

public enum DeviceStatus { Active, Revoked, Blocked }

public enum SupportedGame { Cs16, Cs2 }

public enum AntiCheatSessionStatus { Active, Ended, Expired, Revoked }

public enum GameServerStatus { Active, Disabled }

public enum BanType { Temporary, Permanent }

/// <summary>Cele cinci niveluri cerute de specificație, de la informativ la încălcare confirmată.</summary>
public enum Severity { Information, Warning, SuspiciousIndicator, HighConfidenceDetection, ConfirmedViolation }

public enum CheckResult { Pass, Fail, Warning, Error, NotApplicable }

public enum ActorType { System, Player, Administrator, GameServer }

public class User
{
    public Guid Id { get; set; }
    /// <summary>Numele așa cum l-a scris jucătorul (afișare).</summary>
    public required string Username { get; set; }
    /// <summary>Nume normalizat (lowercase), unic — folosit la login.</summary>
    public required string NormalizedUsername { get; set; }
    public required string PasswordHash { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutUntil { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public PlayerProfile? Profile { get; set; }
    public List<RegisteredDevice> Devices { get; set; } = [];
}

public class PlayerProfile
{
    public Guid UserId { get; set; }
    public required string DisplayName { get; set; }
    /// <summary>SteamID64 (17 cifre). Serverele de joc identifică jucătorul prin el.</summary>
    public string? SteamId64 { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User? User { get; set; }
}

public class RegisteredDevice
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>SHA-256 peste amprenta trimisă de client (care e deja un hash) — nu stocăm identificatori bruți.</summary>
    public required string FingerprintHash { get; set; }
    public required string DeviceName { get; set; }
    public string? OsVersion { get; set; }
    public string? ClientVersion { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Active;
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    public User? User { get; set; }
}

/// <summary>
/// Token de reînnoire (opac, stocat doar ca hash). Tabel suplimentar față de lista din spec,
/// necesar pentru „token revocation” și „secure session renewal”.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public Guid? ReplacedByTokenId { get; set; }

    public User? User { get; set; }
}

public class GameServer
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public SupportedGame Game { get; set; }
    public string? Address { get; set; }
    /// <summary>Prefix public al cheii API (pentru identificare în loguri), ex. „pzs_ab12cd34”.</summary>
    public required string ApiKeyPrefix { get; set; }
    public required string ApiKeyHash { get; set; }
    public GameServerStatus Status { get; set; } = GameServerStatus.Active;
    public string? PluginVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
}

public class AntiCheatSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public required string TokenHash { get; set; }
    public AntiCheatSessionStatus Status { get; set; } = AntiCheatSessionStatus.Active;
    public SupportedGame? Game { get; set; }
    public required string ClientVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public long HeartbeatCount { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? EndReason { get; set; }

    public User? User { get; set; }
    public RegisteredDevice? Device { get; set; }
}

/// <summary>Intrarea unui jucător pe un server protejat, cu rezultatul autorizării.</summary>
public class GameSession
{
    public Guid Id { get; set; }
    public Guid GameServerId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? AntiCheatSessionId { get; set; }
    public required string SteamId64 { get; set; }
    public bool Authorized { get; set; }
    public required string DecisionCode { get; set; }
    public DateTime JoinedAt { get; set; }
    public DateTime? LeftAt { get; set; }

    public GameServer? GameServer { get; set; }
    public User? User { get; set; }
    public AntiCheatSession? AntiCheatSession { get; set; }
}

public class ClientVersion
{
    public Guid Id { get; set; }
    public required string Version { get; set; }
    public required string Channel { get; set; }
    /// <summary>Versiune retrasă (ex. vulnerabilitate) — clientul nu poate porni sesiuni protejate.</summary>
    public bool IsBlocked { get; set; }
    public DateTime ReleasedAt { get; set; }
    public string? Notes { get; set; }
}

public class IntegrityReport
{
    /// <summary>ID unic de raport.</summary>
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? AntiCheatSessionId { get; set; }
    /// <summary>Momentul la care clientul a făcut verificarea.</summary>
    public DateTime CheckedAt { get; set; }
    /// <summary>Momentul la care serverul a primit raportul.</summary>
    public DateTime ReceivedAt { get; set; }
    public required string WindowsVersion { get; set; }
    public required string ClientVersion { get; set; }
    public required string Category { get; set; }
    public CheckResult Result { get; set; }
    public Severity Severity { get; set; }
    public string? EvidenceReference { get; set; }
    public string? Summary { get; set; }

    public User? User { get; set; }
    public RegisteredDevice? Device { get; set; }
    public AntiCheatSession? AntiCheatSession { get; set; }
}

public class Ban
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public BanType Type { get; set; }
    public required string Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LiftedAt { get; set; }
    public Guid? CreatedByAdministratorId { get; set; }

    public User? User { get; set; }
}

public class AdministratorRole
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    /// <summary>Permisiuni separate prin virgulă, ex. „bans.write,evidence.read”.</summary>
    public required string Permissions { get; set; }
}

public class AdministratorAccount
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string NormalizedUsername { get; set; }
    public required string PasswordHash { get; set; }
    public Guid RoleId { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    /// <summary>Secretul TOTP pentru MFA. Va fi criptat la nivel de aplicație (faza dashboard-ului).</summary>
    public string? MfaSecret { get; set; }
    public bool MfaEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public AdministratorRole? Role { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }
    public DateTime OccurredAt { get; set; }
    public ActorType ActorType { get; set; }
    public string? ActorId { get; set; }
    public required string Action { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? IpAddress { get; set; }
    public string? Details { get; set; }
}

public class SystemSetting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTime UpdatedAt { get; set; }
}
