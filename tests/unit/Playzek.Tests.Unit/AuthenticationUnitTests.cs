using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Playzek.Authentication;
using Playzek.Data;

namespace Playzek.Tests.Unit;

public class PasswordHashingTests
{
    [Fact]
    public void Hash_is_salted_and_verifies_only_the_right_password()
    {
        var h = new PasswordHashing();
        var a = h.Hash("corect-horse-battery");
        var b = h.Hash("corect-horse-battery");

        Assert.NotEqual(a, b);
        Assert.True(h.Verify(a, "corect-horse-battery"));
        Assert.False(h.Verify(a, "corect-horse-batterY"));
        Assert.DoesNotContain("corect", a);
    }
}

public class InputRulesTests
{
    [Theory]
    [InlineData("STEAM_0:1:12345", "76561197960290419")]
    [InlineData("STEAM_1:0:1", "76561197960265730")]
    [InlineData("76561198000000000", "76561198000000000")]
    public void NormalizeSteamId_converts_legacy_format(string raw, string expected) =>
        Assert.Equal(expected, InputRules.NormalizeSteamId(raw));

    [Theory]
    [InlineData("STEAM_ID_LAN")]
    [InlineData("BOT")]
    [InlineData("12345")]
    [InlineData("")]
    public void NormalizeSteamId_rejects_non_steam_ids(string raw) => Assert.Null(InputRules.NormalizeSteamId(raw));

    [Theory]
    [InlineData("ab", false)]
    [InlineData("player_1", true)]
    [InlineData("nume cu spatiu", false)]
    [InlineData("<script>", false)]
    public void Username_rules(string name, bool valid) => Assert.Equal(valid, InputRules.IsValidUsername(name));

    [Fact]
    public void Password_rules()
    {
        Assert.Equal("password_too_short", InputRules.ValidatePassword("scurta")?.Code);
        Assert.Equal("password_too_long", InputRules.ValidatePassword(new string('x', 129))?.Code);
        Assert.Null(InputRules.ValidatePassword("destul-de-lunga"));
    }

    [Theory]
    [InlineData("0.1.0", "0.1.0", true)]
    [InlineData("0.2.0", "0.1.9", true)]
    [InlineData("0.0.9", "0.1.0", false)]
    [InlineData("abc", "0.1.0", false)]
    public void Version_comparison(string v, string min, bool ok) => Assert.Equal(ok, SessionPolicy.IsVersionAtLeast(v, min));
}

public class SessionPolicyTests
{
    private static readonly DateTime T0 = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SessionPolicyOptions Options = new();

    private static (AntiCheatSession, User, RegisteredDevice) Make()
    {
        var user = new User { Username = "u", NormalizedUsername = "u", PasswordHash = "x" };
        var device = new RegisteredDevice { FingerprintHash = "f", DeviceName = "pc" };
        var session = new AntiCheatSession
        {
            TokenHash = "h", ClientVersion = "0.1.0", CreatedAt = T0, ExpiresAt = T0.AddHours(6), LastHeartbeatAt = T0,
        };
        return (session, user, device);
    }

    [Fact]
    public void Fresh_session_is_valid()
    {
        var (s, u, d) = Make();
        Assert.Same(DenialReasons.Ok, SessionPolicy.Evaluate(s, u, d, T0.AddSeconds(10), Options));
    }

    [Fact]
    public void Missing_heartbeats_invalidate_the_session_after_the_grace_period()
    {
        var (s, u, d) = Make();
        Assert.Same(DenialReasons.Ok, SessionPolicy.Evaluate(s, u, d, T0.AddSeconds(45), Options));
        Assert.Same(DenialReasons.HeartbeatTimeout, SessionPolicy.Evaluate(s, u, d, T0.AddSeconds(46), Options));
    }

    [Fact]
    public void Absolute_expiry_wins_even_with_recent_heartbeat()
    {
        var (s, u, d) = Make();
        s.LastHeartbeatAt = T0.AddHours(6).AddSeconds(-1);
        Assert.Same(DenialReasons.SessionExpired, SessionPolicy.Evaluate(s, u, d, T0.AddHours(6), Options));
    }

    [Theory]
    [InlineData(AntiCheatSessionStatus.Revoked, "session_revoked")]
    [InlineData(AntiCheatSessionStatus.Ended, "session_ended")]
    [InlineData(AntiCheatSessionStatus.Expired, "session_expired")]
    public void Non_active_status_is_never_valid(AntiCheatSessionStatus status, string code)
    {
        var (s, u, d) = Make();
        s.Status = status;
        Assert.Equal(code, SessionPolicy.Evaluate(s, u, d, T0, Options).Code);
    }

    [Fact]
    public void Suspended_account_or_revoked_device_is_rejected()
    {
        var (s, u, d) = Make();
        u.Status = AccountStatus.Suspended;
        Assert.Same(DenialReasons.AccountInactive, SessionPolicy.Evaluate(s, u, d, T0, Options));
        u.Status = AccountStatus.Active;
        d.Status = DeviceStatus.Revoked;
        Assert.Same(DenialReasons.DeviceNotActive, SessionPolicy.Evaluate(s, u, d, T0, Options));
    }

    [Fact]
    public void ApplyExpiry_marks_timed_out_session_as_expired()
    {
        var (s, _, _) = Make();
        SessionPolicy.ApplyExpiry(s, DenialReasons.HeartbeatTimeout, T0.AddMinutes(1));
        Assert.Equal(AntiCheatSessionStatus.Expired, s.Status);
        Assert.Equal("heartbeat_timeout", s.EndReason);
    }
}

public class TokenServiceTests
{
    private static TokenService Create(out JwtOptions options)
    {
        options = new JwtOptions { SigningKey = new string('k', 48) };
        return new TokenService(Options.Create(options), TimeProvider.System);
    }

    [Fact]
    public async Task Issued_token_validates_and_carries_player_claims()
    {
        var svc = Create(out _);
        var id = Guid.NewGuid();
        var token = svc.CreatePlayerToken(id, "jucator");

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Token, svc.ValidationParameters());
        Assert.True(result.IsValid);
        Assert.Equal(id.ToString(), result.Claims["sub"]);
        Assert.Equal("player", result.Claims[TokenService.TokenTypeClaim]);
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_rejected()
    {
        var other = new TokenService(Options.Create(new JwtOptions { SigningKey = new string('z', 48) }), TimeProvider.System);
        var token = other.CreatePlayerToken(Guid.NewGuid(), "x");
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Token, Create(out _).ValidationParameters());
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Short_signing_key_is_refused_at_startup() =>
        Assert.Throws<InvalidOperationException>(() => TokenService.CreateKey("prea-scurta"));
}
