namespace Playzek.Authentication;

public sealed class JwtOptions
{
    public const string Section = "Playzek:Jwt";

    public string Issuer { get; set; } = "playzek-anticheat";
    public string Audience { get; set; } = "playzek-anticheat-clients";
    /// <summary>Cheie HMAC-SHA256, minim 32 de octeți. Vine din variabilă de mediu, niciodată din repo.</summary>
    public string SigningKey { get; set; } = "";
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);
}

public sealed class SecurityPolicyOptions
{
    public const string Section = "Playzek:Security";

    public int MaxFailedLogins { get; set; } = 5;
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);
    public int MaxActiveDevicesPerUser { get; set; } = 3;
}

/// <summary>Politica serverului pentru sesiunile anti-cheat (validate independent de client).</summary>
public sealed class SessionPolicyOptions
{
    public const string Section = "Playzek:Sessions";

    /// <summary>Durata maximă absolută a unei sesiuni; după asta clientul trebuie să o reînnoiască.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(6);
    /// <summary>Cât de des trimite clientul heartbeat.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>După cât timp fără heartbeat sesiunea nu mai e validă (acoperă întreruperi scurte de rețea).</summary>
    public TimeSpan HeartbeatGracePeriod { get; set; } = TimeSpan.FromSeconds(45);
    /// <summary>Versiunea minimă de client acceptată pentru sesiuni protejate.</summary>
    public string MinimumClientVersion { get; set; } = "0.1.0";
}
