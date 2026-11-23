using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Playzek.Data;

namespace Playzek.Tests.Integration;

public abstract class ApiTestBase : IAsyncLifetime
{
    protected PlayzekApiFactory Api { get; } = new();
    public Task InitializeAsync() => Api.InitializeAsync();
    public async Task DisposeAsync() => await Api.DisposeAsync();
}

public class AuthenticationTests : ApiTestBase
{
    [Fact]
    public async Task Register_login_and_me_work_end_to_end()
    {
        var p = await Api.CreatePlayerAsync();
        var me = await Api.Authed(p.AccessToken).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var j = await me.ReadJsonAsync();
        Assert.Equal(p.Username, j.GetProperty("username").GetString());
        Assert.Equal(p.SteamId, j.GetProperty("steamId64").GetString());
    }

    [Fact]
    public async Task Password_is_never_stored_in_plain_text()
    {
        var p = await Api.CreatePlayerAsync();
        var hash = await Api.WithDbAsync((db, _) =>
            db.Users.Where(u => u.Id == p.UserId).Select(u => u.PasswordHash).SingleAsync());
        Assert.DoesNotContain(p.Password, hash);
    }

    [Fact]
    public async Task Duplicate_username_and_weak_password_are_rejected()
    {
        var c = Api.CreateClient();
        var ok = await c.PostAsJsonAsync("/api/v1/auth/register", new { username = "dublura", password = "parola-lunga-1" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var dup = await c.PostAsJsonAsync("/api/v1/auth/register", new { username = "DUBLURA", password = "parola-lunga-1" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var weak = await c.PostAsJsonAsync("/api/v1/auth/register", new { username = "altul", password = "123" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal("password_too_short", (await weak.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Wrong_password_is_401_and_account_locks_after_repeated_failures()
    {
        var p = await Api.CreatePlayerAsync();
        var c = Api.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            var bad = await c.PostAsJsonAsync("/api/v1/auth/login", new { username = p.Username, password = "gresita-gresita" });
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }
        var locked = await c.PostAsJsonAsync("/api/v1/auth/login", new { username = p.Username, password = p.Password });
        Assert.Equal((HttpStatusCode)423, locked.StatusCode);

        Api.Clock.Advance(TimeSpan.FromMinutes(16));
        var ok = await c.PostAsJsonAsync("/api/v1/auth/login", new { username = p.Username, password = p.Password });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoints_require_a_valid_token()
    {
        var anon = await Api.CreateClient().GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        var forged = await Api.Authed("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.invalid").GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    [Fact]
    public async Task Access_token_expires()
    {
        var p = await Api.CreatePlayerAsync();
        Assert.Equal(HttpStatusCode.OK, (await Api.Authed(p.AccessToken).GetAsync("/api/v1/me")).StatusCode);
        Api.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.Authed(p.AccessToken).GetAsync("/api/v1/me")).StatusCode);

        // Refresh token-ul încă valid emite un access token nou.
        var r = await Api.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = p.RefreshToken });
        var fresh = (await r.ReadJsonAsync()).GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Api.Authed(fresh).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Refresh_token_rotates_and_reuse_revokes_everything()
    {
        var p = await Api.CreatePlayerAsync();
        var c = Api.CreateClient();

        var first = await c.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = p.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rotated = (await first.ReadJsonAsync()).GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(p.RefreshToken, rotated);

        // Token-ul vechi refolosit → refuzat și revocă și token-ul nou (posibil furt).
        var reuse = await c.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = p.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        var afterReuse = await c.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = rotated });
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_refresh_token()
    {
        var p = await Api.CreatePlayerAsync();
        var c = Api.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent,
            (await c.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = p.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await c.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = p.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task Auth_actions_are_written_to_the_audit_log()
    {
        var p = await Api.CreatePlayerAsync();
        var actions = await Api.WithDbAsync((db, _) =>
            db.AuditLogs.Where(a => a.ActorId == p.UserId.ToString()).Select(a => a.Action).ToListAsync());
        Assert.Contains("player.registered", actions);
        Assert.Contains("player.login", actions);
        Assert.Contains("device.registered", actions);
    }
}

public class DeviceRegistrationTests : ApiTestBase
{
    [Fact]
    public async Task Same_fingerprint_updates_the_same_device()
    {
        var p = await Api.CreatePlayerAsync();
        var r = await Api.Authed(p.AccessToken).PostAsJsonAsync("/api/v1/devices",
            new { fingerprint = ApiHelpers.Fingerprint(p.Username), deviceName = "PC-NOU", clientVersion = "0.1.0" });
        Assert.Equal(p.DeviceId, (await r.ReadJsonAsync()).GetProperty("deviceId").GetGuid());
    }

    [Fact]
    public async Task Raw_identifiers_are_rejected_and_device_limit_is_enforced()
    {
        var p = await Api.CreatePlayerAsync();
        var c = Api.Authed(p.AccessToken);
        var raw = await c.PostAsJsonAsync("/api/v1/devices", new { fingerprint = "MAC-00-11-22", deviceName = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, raw.StatusCode);

        for (var i = 0; i < 2; i++)
            (await c.PostAsJsonAsync("/api/v1/devices", new { fingerprint = ApiHelpers.Fingerprint($"{p.Username}-{i}"), deviceName = "pc" }))
                .EnsureSuccessStatusCode();
        var fourth = await c.PostAsJsonAsync("/api/v1/devices", new { fingerprint = ApiHelpers.Fingerprint("extra" + p.Username), deviceName = "pc" });
        Assert.Equal(HttpStatusCode.Conflict, fourth.StatusCode);
    }

    [Fact]
    public async Task Player_cannot_start_a_session_on_another_players_device()
    {
        var a = await Api.CreatePlayerAsync();
        var b = await Api.CreatePlayerAsync();
        var r = await Api.Authed(b.AccessToken).PostAsJsonAsync("/api/v1/anticheat/sessions/",
            new { deviceId = a.DeviceId, clientVersion = "0.1.0" });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Revoking_a_device_revokes_its_active_session()
    {
        var p = await Api.CreatePlayerAsync();
        var token = await Api.StartSessionAsync(p);
        (await Api.Authed(p.AccessToken).DeleteAsync($"/api/v1/devices/{p.DeviceId}")).EnsureSuccessStatusCode();
        var hb = await Api.Authed(p.AccessToken, token).PostAsync("/api/v1/anticheat/sessions/heartbeat", null);
        var j = await hb.ReadJsonAsync();
        Assert.False(j.GetProperty("valid").GetBoolean());
        Assert.Equal("session_revoked", j.GetProperty("code").GetString());
    }
}

public class SessionTests : ApiTestBase
{
    [Fact]
    public async Task Heartbeats_keep_the_session_valid()
    {
        var p = await Api.CreatePlayerAsync();
        var token = await Api.StartSessionAsync(p);
        var c = Api.Authed(p.AccessToken, token);
        for (var i = 0; i < 4; i++)
        {
            Api.Clock.Advance(TimeSpan.FromSeconds(15));
            var j = await (await c.PostAsync("/api/v1/anticheat/sessions/heartbeat", null)).ReadJsonAsync();
            Assert.True(j.GetProperty("valid").GetBoolean());
        }
    }

    [Fact]
    public async Task Session_expires_when_the_client_stops_sending_heartbeats()
    {
        var p = await Api.CreatePlayerAsync();
        var token = await Api.StartSessionAsync(p);
        Api.Clock.Advance(TimeSpan.FromSeconds(50));
        var j = await (await Api.Authed(p.AccessToken, token).PostAsync("/api/v1/anticheat/sessions/heartbeat", null)).ReadJsonAsync();
        Assert.False(j.GetProperty("valid").GetBoolean());
        Assert.Equal("heartbeat_timeout", j.GetProperty("code").GetString());

        // Odată expirată, nu mai revine la valid chiar dacă heartbeat-urile reîncep.
        var again = await (await Api.Authed(p.AccessToken, token).PostAsync("/api/v1/anticheat/sessions/heartbeat", null)).ReadJsonAsync();
        Assert.False(again.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Short_network_interruption_within_grace_period_does_not_end_the_session()
    {
        var p = await Api.CreatePlayerAsync();
        var token = await Api.StartSessionAsync(p);
        Api.Clock.Advance(TimeSpan.FromSeconds(40)); // ~2 heartbeat-uri pierdute
        var j = await (await Api.Authed(p.AccessToken, token).PostAsync("/api/v1/anticheat/sessions/heartbeat", null)).ReadJsonAsync();
        Assert.True(j.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Renewal_rotates_the_session_token()
    {
        var p = await Api.CreatePlayerAsync();
        var old = await Api.StartSessionAsync(p);
        var r = await Api.Authed(p.AccessToken, old).PostAsync("/api/v1/anticheat/sessions/renew", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var fresh = (await r.ReadJsonAsync()).GetProperty("sessionToken").GetString()!;

        var oldHb = await Api.Authed(p.AccessToken, old).PostAsync("/api/v1/anticheat/sessions/heartbeat", null);
        Assert.Equal(HttpStatusCode.Unauthorized, oldHb.StatusCode);
        var newHb = await (await Api.Authed(p.AccessToken, fresh).PostAsync("/api/v1/anticheat/sessions/heartbeat", null)).ReadJsonAsync();
        Assert.True(newHb.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Starting_a_new_session_ends_the_previous_one()
    {
        var p = await Api.CreatePlayerAsync();
        var first = await Api.StartSessionAsync(p);
        await Api.StartSessionAsync(p);
        var j = await (await Api.Authed(p.AccessToken, first).PostAsync("/api/v1/anticheat/sessions/heartbeat", null)).ReadJsonAsync();
        Assert.Equal("session_ended", j.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Session_token_of_another_player_is_rejected()
    {
        var a = await Api.CreatePlayerAsync();
        var b = await Api.CreatePlayerAsync();
        var tokenA = await Api.StartSessionAsync(a);
        var r = await Api.Authed(b.AccessToken, tokenA).PostAsync("/api/v1/anticheat/sessions/heartbeat", null);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Outdated_or_blocked_client_versions_cannot_start_sessions()
    {
        var p = await Api.CreatePlayerAsync();
        var c = Api.Authed(p.AccessToken);
        var old = await c.PostAsJsonAsync("/api/v1/anticheat/sessions/", new { deviceId = p.DeviceId, clientVersion = "0.0.9" });
        Assert.Equal("client_outdated", (await old.ReadJsonAsync()).GetProperty("code").GetString());

        await Api.WithDbAsync(async (db, _) =>
        {
            db.ClientVersions.Add(new ClientVersion
            {
                Id = Guid.NewGuid(), Version = "0.1.5", Channel = "stable", IsBlocked = true, ReleasedAt = DateTime.UtcNow,
            });
            return await db.SaveChangesAsync();
        });
        var blocked = await c.PostAsJsonAsync("/api/v1/anticheat/sessions/", new { deviceId = p.DeviceId, clientVersion = "0.1.5" });
        Assert.Equal("client_version_blocked", (await blocked.ReadJsonAsync()).GetProperty("code").GetString());
    }
}

public class GameServerAuthorizationTests : ApiTestBase
{
    [Fact]
    public async Task Unknown_server_key_is_rejected()
    {
        var r = await Api.GameServerClient("pzs_deadbeef_nope").GetAsync("/api/v1/game-server/ping");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        var none = await Api.CreateClient().GetAsync("/api/v1/game-server/ping");
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
    }

    [Fact]
    public async Task Server_key_is_stored_only_as_hash()
    {
        var key = await Api.CreateGameServerAsync();
        var stored = await Api.WithDbAsync((db, _) => db.GameServers.Select(s => s.ApiKeyHash).ToListAsync());
        Assert.DoesNotContain(key, stored);
    }

    [Fact]
    public async Task Player_with_valid_session_is_authorized_and_game_session_recorded()
    {
        var p = await Api.CreatePlayerAsync();
        await Api.StartSessionAsync(p);
        var server = Api.GameServerClient(await Api.CreateGameServerAsync());

        var j = await server.AuthorizeAsync(p.SteamId);
        Assert.True(j.GetProperty("allowed").GetBoolean());
        Assert.Equal("ok", j.GetProperty("code").GetString());

        var recorded = await Api.WithDbAsync((db, _) => db.GameSessions.CountAsync(g => g.UserId == p.UserId && g.Authorized));
        Assert.Equal(1, recorded);
    }

    [Fact]
    public async Task Legacy_steam_id_format_from_hlds_is_accepted()
    {
        var p = await Api.CreatePlayerAsync("76561197960290419"); // = STEAM_0:1:12345
        await Api.StartSessionAsync(p);
        var j = await Api.GameServerClient(await Api.CreateGameServerAsync()).AuthorizeAsync("STEAM_0:1:12345");
        Assert.True(j.GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task Clear_denial_reasons_are_returned()
    {
        var server = Api.GameServerClient(await Api.CreateGameServerAsync());

        var unknown = await server.AuthorizeAsync(ApiHelpers.NewSteamId());
        Assert.Equal("account_not_linked", unknown.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(unknown.GetProperty("message").GetString()));

        var p = await Api.CreatePlayerAsync();
        Assert.Equal("no_active_session", (await server.AuthorizeAsync(p.SteamId)).GetProperty("code").GetString());

        await Api.StartSessionAsync(p);
        Api.Clock.Advance(TimeSpan.FromMinutes(2)); // clientul a fost închis
        var closed = await server.AuthorizeAsync(p.SteamId);
        Assert.False(closed.GetProperty("allowed").GetBoolean());
        Assert.Equal("heartbeat_timeout", closed.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Session_for_cs2_is_not_accepted_on_a_cs16_server()
    {
        var p = await Api.CreatePlayerAsync();
        await Api.StartSessionAsync(p, game: "cs2");
        var j = await Api.GameServerClient(await Api.CreateGameServerAsync("cs16")).AuthorizeAsync(p.SteamId);
        Assert.Equal("game_mismatch", j.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Banned_player_is_denied_even_with_a_running_client()
    {
        var p = await Api.CreatePlayerAsync();
        await Api.StartSessionAsync(p);
        await Api.WithDbAsync(async (db, sp) =>
        {
            db.Bans.Add(new Ban
            {
                Id = Guid.NewGuid(), UserId = p.UserId, Type = BanType.Temporary, Reason = "test",
                CreatedAt = Api.Clock.GetUtcNow().UtcDateTime, ExpiresAt = Api.Clock.GetUtcNow().UtcDateTime.AddDays(1),
            });
            return await db.SaveChangesAsync();
        });
        var j = await Api.GameServerClient(await Api.CreateGameServerAsync()).AuthorizeAsync(p.SteamId);
        Assert.Equal("banned", j.GetProperty("code").GetString());

        // Ban temporar expirat → poate porni o sesiune nouă.
        Api.Clock.Advance(TimeSpan.FromDays(2));
        var login = await Api.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { username = p.Username, password = p.Password });
        var access = (await login.ReadJsonAsync()).GetProperty("accessToken").GetString()!;
        var start = await Api.Authed(access).PostAsJsonAsync("/api/v1/anticheat/sessions/",
            new { deviceId = p.DeviceId, clientVersion = "0.1.0" });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
    }

    [Fact]
    public async Task Ending_protection_in_the_client_denies_server_access()
    {
        var p = await Api.CreatePlayerAsync();
        var token = await Api.StartSessionAsync(p);
        (await Api.Authed(p.AccessToken, token).PostAsync("/api/v1/anticheat/sessions/end", null)).EnsureSuccessStatusCode();
        var j = await Api.GameServerClient(await Api.CreateGameServerAsync()).AuthorizeAsync(p.SteamId);
        Assert.Equal("no_active_session", j.GetProperty("code").GetString());
    }
}

public class IntegrityReportTests : ApiTestBase
{
    [Fact]
    public async Task Structured_reports_are_stored_with_session_and_device()
    {
        var p = await Api.CreatePlayerAsync();
        var token = await Api.StartSessionAsync(p);
        var r = await Api.Authed(p.AccessToken, token).PostAsJsonAsync("/api/v1/integrity-reports", new
        {
            deviceId = p.DeviceId,
            windowsVersion = "Windows 11 26100",
            clientVersion = "0.1.0",
            checks = new[]
            {
                new { category = "secure_boot", result = "Pass", severity = "Information", summary = "Secure Boot activ", evidenceReference = (string?)null },
                new { category = "game_files", result = "Fail", severity = "SuspiciousIndicator", summary = "1 fișier modificat", evidenceReference = (string?)"hash:abc" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var rows = await Api.WithDbAsync((db, _) => db.IntegrityReports.Where(x => x.UserId == p.UserId).ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.All(rows, x => Assert.NotNull(x.AntiCheatSessionId));
        Assert.Contains(rows, x => x.Severity == Severity.SuspiciousIndicator && x.Result == CheckResult.Fail);
    }

    [Fact]
    public async Task Client_cannot_report_a_confirmed_violation()
    {
        var p = await Api.CreatePlayerAsync();
        var r = await Api.Authed(p.AccessToken).PostAsJsonAsync("/api/v1/integrity-reports", new
        {
            deviceId = p.DeviceId, windowsVersion = "Windows 11", clientVersion = "0.1.0",
            checks = new[] { new { category = "x", result = "Fail", severity = "ConfirmedViolation" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}

public class HealthTests : ApiTestBase
{
    [Fact]
    public async Task Health_endpoints_report_status()
    {
        var c = Api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/live")).StatusCode);
        var ready = await c.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("ok", (await ready.ReadJsonAsync()).GetProperty("database").GetString());
    }
}
