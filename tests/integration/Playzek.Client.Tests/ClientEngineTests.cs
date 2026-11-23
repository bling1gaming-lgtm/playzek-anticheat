using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Playzek.Client.Core;
using Playzek.Tests.Integration;

namespace Playzek.Client.Tests;

/// <summary>Simulează căderea rețelei între client și backend.</summary>
public sealed class SwitchableNetwork : DelegatingHandler
{
    public bool Offline { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Offline ? throw new HttpRequestException("rețea simulată indisponibilă (test)") : base.SendAsync(request, ct);
}

public sealed class MemoryTokenStore : ITokenStore
{
    public string? Token { get; private set; }
    public string? LoadRefreshToken() => Token;
    public void SaveRefreshToken(string token) => Token = token;
    public void Clear() => Token = null;
}

/// <summary>Verificări deterministe pentru test — rezultate marcate explicit ca simulate.</summary>
public sealed class FakeChecks : ISystemChecks
{
    public string WindowsVersion => "Windows 11 (build 26100.1) [TEST]";
    public IReadOnlyList<CheckOutcome> RunCompatibilityChecks() =>
    [
        new("compat.windows_version", "Versiune Windows", "Pass", "Information", "[TEST] build simulat", DateTime.UtcNow),
        new("security.secure_boot", "Secure Boot", "Warning", "Warning", "[TEST] Secure Boot simulat dezactivat", DateTime.UtcNow),
    ];
}

public sealed class FakeDevice(string seed) : IDeviceIdentity
{
    public string Fingerprint { get; } = WindowsDeviceIdentity.Compute(seed);
    public string DeviceName => "PC-TEST";
}

public sealed class ClientEngineTests : IAsyncLifetime
{
    private readonly PlayzekApiFactory _api = new();
    private readonly SwitchableNetwork _net = new();
    private readonly MemoryTokenStore _tokens = new();
    private ProtectionEngine _engine = null!;
    private string _steamId = null!;

    public async Task InitializeAsync()
    {
        await _api.InitializeAsync();
        _net.InnerHandler = _api.Server.CreateHandler();
        _engine = NewEngine();
        _steamId = ApiHelpers.NewSteamId();
    }

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        await _api.DisposeAsync();
    }

    private ProtectionEngine NewEngine() => new(
        new BackendClient(new HttpClient(_net, disposeHandler: false) { BaseAddress = _api.Server.BaseAddress }),
        _tokens, new FakeChecks(), new FakeDevice("machine-guid-test"), _api.Clock,
        new EngineOptions { ClientVersionOverride = "0.1.0" });

    private async Task<string> RegisterAndLoginAsync()
    {
        var user = "client" + Guid.NewGuid().ToString("N")[..8];
        var r = await _engine.RegisterAsync(user, "parola-foarte-sigura", _steamId, CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        return user;
    }

    private Task<System.Text.Json.JsonElement> ServerAuthorizeAsync() =>
        _api.CreateGameServerAsync().ContinueWith(k => _api.GameServerClient(k.Result).AuthorizeAsync(_steamId)).Unwrap();

    [Fact]
    public async Task Full_workflow_login_device_checks_protection_and_server_validation()
    {
        await RegisterAndLoginAsync();
        var s = _engine.GetStatus();
        Assert.Equal(AccountState.LoggedIn, s.Account);
        Assert.NotNull(s.DeviceId);
        Assert.Equal(BackendState.Online, s.Backend);
        Assert.True(s.ChecksReported);

        var reports = await _api.WithDbAsync((db, _) => db.IntegrityReports.CountAsync(r => r.DeviceId == s.DeviceId));
        Assert.Equal(2, reports);

        // Înainte de activare, serverul de joc refuză jucătorul.
        Assert.Equal("no_active_session", (await ServerAuthorizeAsync()).GetProperty("code").GetString());

        var enable = await _engine.EnableProtectionAsync("cs16", CancellationToken.None);
        Assert.True(enable.Ok, enable.Message);
        Assert.Equal(ProtectionState.Active, _engine.GetStatus().Protection);
        Assert.True((await ServerAuthorizeAsync()).GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task Short_network_interruption_reconnects_without_losing_the_session()
    {
        await RegisterAndLoginAsync();
        await _engine.EnableProtectionAsync("cs16", CancellationToken.None);

        _net.Offline = true;
        _api.Clock.Advance(TimeSpan.FromSeconds(15));
        await _engine.HeartbeatOnceAsync(CancellationToken.None);
        Assert.Equal(ProtectionState.Reconnecting, _engine.GetStatus().Protection);
        Assert.Equal(BackendState.Offline, _engine.GetStatus().Backend);

        _net.Offline = false;
        _api.Clock.Advance(TimeSpan.FromSeconds(15));
        var r = await _engine.HeartbeatOnceAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(ProtectionState.Active, _engine.GetStatus().Protection);
        Assert.True((await ServerAuthorizeAsync()).GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task Long_outage_expires_the_session_on_the_server_and_client_reports_why()
    {
        await RegisterAndLoginAsync();
        await _engine.EnableProtectionAsync("cs16", CancellationToken.None);

        _net.Offline = true;
        _api.Clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal("heartbeat_timeout", (await ServerAuthorizeAsync()).GetProperty("code").GetString());

        _net.Offline = false;
        var r = await _engine.HeartbeatOnceAsync(CancellationToken.None);
        Assert.False(r.Ok);
        var s = _engine.GetStatus();
        Assert.Equal(ProtectionState.Stopped, s.Protection);
        Assert.Equal("heartbeat_timeout", s.ProtectionCode);
    }

    [Fact]
    public async Task Disabling_protection_is_seen_by_the_game_server()
    {
        await RegisterAndLoginAsync();
        await _engine.EnableProtectionAsync("cs16", CancellationToken.None);
        await _engine.DisableProtectionAsync(CancellationToken.None);
        Assert.Equal(ProtectionState.Off, _engine.GetStatus().Protection);
        Assert.Equal("no_active_session", (await ServerAuthorizeAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Access_token_is_refreshed_transparently_during_a_long_session()
    {
        await RegisterAndLoginAsync();
        await _engine.EnableProtectionAsync("cs16", CancellationToken.None);
        for (var i = 0; i < 6; i++) // 6 × 15 s heartbeat… apoi sărim peste expirarea access token-ului
        {
            _api.Clock.Advance(TimeSpan.FromSeconds(15));
            Assert.True((await _engine.HeartbeatOnceAsync(CancellationToken.None)).Ok);
        }
        for (var i = 0; i < 60; i++) // ~15 minute de heartbeat-uri
        {
            _api.Clock.Advance(TimeSpan.FromSeconds(15));
            var r = await _engine.HeartbeatOnceAsync(CancellationToken.None);
            Assert.True(r.Ok, $"heartbeat {i}: {r.Message}");
        }
    }

    [Fact]
    public async Task Service_restart_resumes_the_login_from_the_secure_store()
    {
        var user = await RegisterAndLoginAsync();
        Assert.NotNull(_tokens.Token);
        await _engine.DisposeAsync();

        _engine = NewEngine();
        await _engine.InitializeAsync(CancellationToken.None);
        var s = _engine.GetStatus();
        Assert.Equal(AccountState.LoggedIn, s.Account);
        Assert.Equal(user, s.Username);
    }

    [Fact]
    public async Task Logout_clears_the_stored_token_and_revokes_it_on_the_server()
    {
        await RegisterAndLoginAsync();
        var token = _tokens.Token!;
        await _engine.LogoutAsync(CancellationToken.None);
        Assert.Null(_tokens.Token);
        Assert.Equal(AccountState.LoggedOut, _engine.GetStatus().Account);

        var r = await _api.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = token });
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_reports_a_clear_message()
    {
        var user = await RegisterAndLoginAsync();
        await _engine.LogoutAsync(CancellationToken.None);
        var r = await _engine.LoginAsync(user, "parola-gresita!!", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal("invalid_credentials", r.Code);
        Assert.False(string.IsNullOrWhiteSpace(r.Message));
    }

    [Fact]
    public async Task Protection_cannot_be_enabled_while_logged_out()
    {
        var r = await _engine.EnableProtectionAsync("cs16", CancellationToken.None);
        Assert.Equal("not_logged_in", r.Code);
    }
}

public sealed class ClientUnitTests
{
    [Fact]
    public void Plain_http_to_a_remote_host_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => BackendClient.EnsureSecureEndpoint(new Uri("http://api.playzek.ro/")));
        BackendClient.EnsureSecureEndpoint(new Uri("https://api.playzek.ro/"));
        BackendClient.EnsureSecureEndpoint(new Uri("http://127.0.0.1:5080/"));
    }

    [Theory]
    [InlineData(26100, "Pass")]
    [InlineData(22000, "Pass")]
    [InlineData(19045, "Pass")]
    [InlineData(19044, "Fail")]
    [InlineData(17763, "Fail")]
    public void Windows_build_support_matches_the_spec(int build, string expected) =>
        Assert.Equal(expected, WindowsSystemChecks.EvaluateBuild(build).Item1);

    [Fact]
    public void Device_fingerprint_is_a_sha256_and_does_not_contain_the_machine_guid()
    {
        var guid = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
        var fp = WindowsDeviceIdentity.Compute(guid);
        Assert.Matches("^[0-9a-f]{64}$", fp);
        Assert.DoesNotContain(guid, fp);
        Assert.Equal(fp, WindowsDeviceIdentity.Compute(guid.ToUpperInvariant()));
    }

    [Fact]
    public void Real_device_identity_can_be_read_on_this_machine() =>
        Assert.Matches("^[0-9a-f]{64}$", new WindowsDeviceIdentity().Fingerprint);

    [Fact]
    public void Real_system_checks_run_on_this_machine_without_throwing()
    {
        var checks = new WindowsSystemChecks();
        var results = checks.RunCompatibilityChecks();
        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.Contains(r.Result, new[] { "Pass", "Fail", "Warning", "Error", "NotApplicable" }));
        Assert.Contains("build", checks.WindowsVersion);
    }

    [Fact]
    public void Dpapi_token_store_round_trips_and_file_is_not_plain_text()
    {
        var path = Path.Combine(Path.GetTempPath(), "playzek-test-" + Guid.NewGuid().ToString("N"), "session.bin");
        var store = new DpapiTokenStore(path);
        store.SaveRefreshToken("refresh-token-secret-value");
        Assert.Equal("refresh-token-secret-value", store.LoadRefreshToken());
        Assert.DoesNotContain("refresh-token-secret-value", File.ReadAllText(path));
        store.Clear();
        Assert.Null(store.LoadRefreshToken());
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public async Task Ipc_messages_round_trip_and_oversized_messages_are_rejected()
    {
        var ms = new MemoryStream();
        await Ipc.WriteAsync(ms, new IpcRequest(IpcCommands.Login, "jucator", "parola"), CancellationToken.None);
        ms.Position = 0;
        var back = await Ipc.ReadAsync<IpcRequest>(ms, CancellationToken.None);
        Assert.Equal("login", back!.Command);
        Assert.Equal("jucator", back.Username);

        var huge = new MemoryStream(new byte[Ipc.MaxMessageBytes + 10]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Ipc.ReadAsync<IpcRequest>(huge, CancellationToken.None));
    }
}
