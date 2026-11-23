using Playzek.Data;

namespace Playzek.Authentication;

/// <summary>Motivele stabile pentru care o sesiune nu e validă / un jucător nu poate intra, cu mesaje clare.</summary>
public static class DenialReasons
{
    public sealed record Reason(string Code, string MessageRo, string MessageEn);

    public static readonly Reason Ok = new("ok", "Sesiune anti-cheat validă.", "Anti-cheat session is valid.");
    public static readonly Reason AccountNotLinked = new("account_not_linked",
        "Niciun cont PlayZek nu este asociat acestui SteamID. Instalează PLAYZEK ANTI-CHEAT, autentifică-te și asociază contul Steam.",
        "No PlayZek account is linked to this SteamID. Install PLAYZEK ANTI-CHEAT, log in and link your Steam account.");
    public static readonly Reason NoActiveSession = new("no_active_session",
        "PLAYZEK ANTI-CHEAT nu rulează sau protecția nu este activată. Pornește aplicația și activează protecția.",
        "PLAYZEK ANTI-CHEAT is not running or protection is not enabled. Start the application and enable protection.");
    public static readonly Reason SessionExpired = new("session_expired",
        "Sesiunea anti-cheat a expirat. Reactivează protecția din aplicație.",
        "The anti-cheat session has expired. Re-enable protection in the application.");
    public static readonly Reason HeartbeatTimeout = new("heartbeat_timeout",
        "Aplicația anti-cheat nu a mai comunicat cu serverul PlayZek. Verifică dacă rulează și dacă ai conexiune la internet.",
        "The anti-cheat application stopped communicating with PlayZek. Check that it is running and online.");
    public static readonly Reason SessionRevoked = new("session_revoked",
        "Sesiunea anti-cheat a fost revocată.", "The anti-cheat session was revoked.");
    public static readonly Reason SessionEnded = new("session_ended",
        "Protecția a fost oprită din aplicație.", "Protection was stopped in the application.");
    public static readonly Reason AccountInactive = new("account_inactive",
        "Contul PlayZek nu este activ.", "The PlayZek account is not active.");
    public static readonly Reason DeviceNotActive = new("device_not_active",
        "Dispozitivul de pe care rulează aplicația nu mai este autorizat.", "The device running the client is no longer authorized.");
    public static readonly Reason Banned = new("banned",
        "Contul este suspendat (ban) pe serverele PlayZek.", "The account is banned from PlayZek servers.");
    public static readonly Reason GameMismatch = new("game_mismatch",
        "Protecția este activată pentru alt joc decât cel al acestui server.",
        "Protection is enabled for a different game than this server's.");
    public static readonly Reason InvalidToken = new("invalid_session_token",
        "Token de sesiune anti-cheat invalid.", "Invalid anti-cheat session token.");
}

/// <summary>
/// Regulile serverului pentru validitatea unei sesiuni. Funcție pură — aceeași logică e folosită la
/// heartbeat, la reînnoire și când serverul de joc cere autorizarea unui jucător.
/// </summary>
public static class SessionPolicy
{
    public static DenialReasons.Reason Evaluate(AntiCheatSession session, User user, RegisteredDevice device,
        DateTime now, SessionPolicyOptions options)
    {
        switch (session.Status)
        {
            case AntiCheatSessionStatus.Revoked: return DenialReasons.SessionRevoked;
            case AntiCheatSessionStatus.Ended: return DenialReasons.SessionEnded;
            case AntiCheatSessionStatus.Expired:
                return session.EndReason == DenialReasons.HeartbeatTimeout.Code
                    ? DenialReasons.HeartbeatTimeout
                    : DenialReasons.SessionExpired;
        }
        if (user.Status != AccountStatus.Active) return DenialReasons.AccountInactive;
        if (device.Status != DeviceStatus.Active) return DenialReasons.DeviceNotActive;
        if (now >= session.ExpiresAt) return DenialReasons.SessionExpired;

        var lastContact = session.LastHeartbeatAt ?? session.CreatedAt;
        if (now - lastContact > options.HeartbeatGracePeriod) return DenialReasons.HeartbeatTimeout;

        return DenialReasons.Ok;
    }

    /// <summary>Dacă sesiunea a devenit invalidă din cauza timpului, o marcăm Expired în baza de date.</summary>
    public static void ApplyExpiry(AntiCheatSession session, DenialReasons.Reason reason, DateTime now)
    {
        if (session.Status != AntiCheatSessionStatus.Active) return;
        if (reason == DenialReasons.SessionExpired || reason == DenialReasons.HeartbeatTimeout)
        {
            session.Status = AntiCheatSessionStatus.Expired;
            session.EndedAt = now;
            session.EndReason = reason.Code;
        }
    }

    public static bool IsVersionAtLeast(string version, string minimum) =>
        Version.TryParse(version, out var v) && Version.TryParse(minimum, out var m) && v >= m;
}
