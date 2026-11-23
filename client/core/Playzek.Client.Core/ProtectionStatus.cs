namespace Playzek.Client.Core;

public enum AccountState { LoggedOut, LoggingIn, LoggedIn }
public enum BackendState { Unknown, Online, Offline }

/// <summary>
/// Starea protecției. „Active” înseamnă că ultimul heartbeat a fost confirmat de server ca valid —
/// nu e o afirmație a clientului.
/// </summary>
public enum ProtectionState { Off, Starting, Active, Reconnecting, Stopped }

public sealed record SecurityEvent(DateTime At, string Level, string Message);

/// <summary>Instantaneu al stării serviciului, trimis aplicației desktop prin IPC.</summary>
public sealed record ProtectionStatus
{
    public required string ClientVersion { get; init; }
    public required string BackendUrl { get; init; }
    public AccountState Account { get; init; }
    public string? Username { get; init; }
    public string? SteamId64 { get; init; }
    public string? AccountStatus { get; init; }
    public string? BanMessage { get; init; }
    public Guid? DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public string? DeviceStatus { get; init; }
    public BackendState Backend { get; init; }
    public DateTime? LastBackendContact { get; init; }
    public ProtectionState Protection { get; init; }
    public string? ProtectionCode { get; init; }
    public string? ProtectionMessage { get; init; }
    public string? Game { get; init; }
    public Guid? SessionId { get; init; }
    public DateTime? SessionExpiresAt { get; init; }
    public DateTime? LastHeartbeatAt { get; init; }
    public string WindowsVersion { get; init; } = "";
    public IReadOnlyList<CheckOutcome> Checks { get; init; } = [];
    public DateTime? ChecksAt { get; init; }
    public bool ChecksReported { get; init; }
    public IReadOnlyList<SecurityEvent> Events { get; init; } = [];
}
