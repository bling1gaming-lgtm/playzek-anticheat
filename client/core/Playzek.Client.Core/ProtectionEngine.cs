using System.Reflection;

namespace Playzek.Client.Core;

public sealed record EngineOptions
{
    /// <summary>Cât de des reîncercăm când rețeaua sau backend-ul nu răspund.</summary>
    public TimeSpan OfflineRetryInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Reînnoim sesiunea când mai are sub acest timp până la expirare.</summary>
    public TimeSpan RenewBefore { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>Reîmprospătăm access token-ul când mai are sub acest timp.</summary>
    public TimeSpan RefreshAccessBefore { get; init; } = TimeSpan.FromMinutes(2);
    public string? ClientVersionOverride { get; init; }
}

public sealed record EngineResult(bool Ok, string? Code, string? Message)
{
    public static readonly EngineResult Success = new(true, null, null);
    public static EngineResult Fail(string code, string message) => new(false, code, message);
}

/// <summary>
/// Fluxul cerut de spec: LOGIN → REGISTER DEVICE → COMPATIBILITY CHECK → SECURITY SCAN → ENABLE PROTECTION,
/// apoi heartbeat continuu. Rulează în serviciul Windows; UI-ul doar trimite comenzi și citește starea.
/// Thread-safe: toate operațiile care schimbă starea trec prin același semafor.
/// </summary>
public sealed class ProtectionEngine : IAsyncDisposable
{
    private const int MaxEvents = 50;

    private readonly BackendClient _api;
    private readonly ITokenStore _tokens;
    private readonly ISystemChecks _checks;
    private readonly IDeviceIdentity _device;
    private readonly TimeProvider _time;
    private readonly EngineOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LinkedList<SecurityEvent> _events = new();
    private readonly object _eventsLock = new();

    private string? _accessToken;
    private DateTime _accessExpiresAt;
    private string? _refreshToken;
    private string? _sessionToken;
    private TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(15);
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private ProtectionStatus _status;

    public string ClientVersion { get; }

    public ProtectionEngine(BackendClient api, ITokenStore tokens, ISystemChecks checks, IDeviceIdentity device,
        TimeProvider time, EngineOptions? options = null)
    {
        _api = api;
        _tokens = tokens;
        _checks = checks;
        _device = device;
        _time = time;
        _options = options ?? new EngineOptions();
        ClientVersion = _options.ClientVersionOverride ?? CurrentVersion();
        _status = new ProtectionStatus
        {
            ClientVersion = ClientVersion,
            BackendUrl = api.BaseAddress.ToString(),
            WindowsVersion = checks.WindowsVersion,
            DeviceName = device.DeviceName,
        };
    }

    public static string CurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public ProtectionStatus GetStatus()
    {
        lock (_eventsLock) return _status with { Events = _events.ToList() };
    }

    private void Update(Func<ProtectionStatus, ProtectionStatus> change)
    {
        lock (_eventsLock) _status = change(_status);
    }

    private void Event(string level, string message)
    {
        lock (_eventsLock)
        {
            _events.AddFirst(new SecurityEvent(Now, level, message));
            while (_events.Count > MaxEvents) _events.RemoveLast();
        }
    }

    private void MarkBackend<T>(ApiResult<T> r)
    {
        if (r.NetworkError) Update(s => s with { Backend = BackendState.Offline });
        else Update(s => s with { Backend = BackendState.Online, LastBackendContact = Now });
    }

    /// <summary>La pornirea serviciului: dacă există un refresh token salvat, reluăm sesiunea de login.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var stored = _tokens.LoadRefreshToken();
            if (stored is null) return;
            var r = await _api.RefreshAsync(stored, ct);
            MarkBackend(r);
            if (r.Ok) await CompleteLoginAsync(r.Value!, ct);
            else if (!r.NetworkError) { _tokens.Clear(); Event("Information", "Sesiunea de login salvată a expirat. Autentifică-te din nou."); }
            else
            {
                // Offline la pornire: păstrăm token-ul și reîncercăm la următoarea comandă.
                _refreshToken = stored;
                Event("Warning", "Serverul PlayZek nu poate fi contactat. Reîncerc automat.");
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<EngineResult> RegisterAsync(string username, string password, string? steamId, CancellationToken ct)
    {
        var r = await _api.RegisterAsync(username, password, string.IsNullOrWhiteSpace(steamId) ? null : steamId, ct);
        MarkBackend(r);
        if (!r.Ok) return EngineResult.Fail(r.ErrorCode!, r.ErrorMessage!);
        Event("Information", $"Contul „{r.Value!.Username}” a fost creat.");
        return await LoginAsync(username, password, ct);
    }

    public async Task<EngineResult> LoginAsync(string username, string password, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Update(s => s with { Account = AccountState.LoggingIn });
            var r = await _api.LoginAsync(username, password, ct);
            MarkBackend(r);
            if (!r.Ok)
            {
                Update(s => s with { Account = AccountState.LoggedOut });
                return EngineResult.Fail(r.ErrorCode!, r.ErrorMessage!);
            }
            return await CompleteLoginAsync(r.Value!, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>LOGIN reușit → REGISTER DEVICE → COMPATIBILITY + SECURITY CHECK (raportat la backend).</summary>
    private async Task<EngineResult> CompleteLoginAsync(LoginResponse login, CancellationToken ct)
    {
        SetTokens(login);

        var me = await _api.MeAsync(_accessToken!, ct);
        MarkBackend(me);
        var device = await _api.RegisterDeviceAsync(_accessToken!, _device.Fingerprint, _device.DeviceName,
            _checks.WindowsVersion, ClientVersion, ct);
        MarkBackend(device);
        if (!device.Ok)
        {
            Update(s => s with { Account = AccountState.LoggedIn, Username = login.Username, DeviceStatus = device.ErrorMessage });
            Event("Warning", $"Dispozitivul nu a putut fi înregistrat: {device.ErrorMessage}");
            return EngineResult.Fail(device.ErrorCode!, device.ErrorMessage!);
        }

        Update(s => s with
        {
            Account = AccountState.LoggedIn,
            Username = login.Username,
            SteamId64 = me.Value?.SteamId64,
            AccountStatus = me.Value?.AccountStatus,
            BanMessage = me.Value?.BanMessage,
            DeviceId = device.Value!.DeviceId,
            DeviceName = device.Value.DeviceName,
            DeviceStatus = device.Value.Status,
        });
        Event("Information", $"Autentificat ca {login.Username}. Dispozitiv înregistrat: {device.Value!.DeviceName}.");

        await RunChecksCoreAsync(ct);
        return EngineResult.Success;
    }

    public async Task<EngineResult> RunChecksAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await RunChecksCoreAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<EngineResult> RunChecksCoreAsync(CancellationToken ct)
    {
        var results = await Task.Run(_checks.RunCompatibilityChecks, ct);
        Update(s => s with { Checks = results, ChecksAt = Now, ChecksReported = false });
        var warnings = results.Count(c => c.Result is "Fail" or "Warning" or "Error");
        Event(warnings == 0 ? "Information" : "Warning",
            warnings == 0 ? "Verificările de sistem au trecut." : $"Verificări de sistem: {warnings} cu avertismente.");

        var status = GetStatus();
        if (status.DeviceId is not { } deviceId || !await EnsureAccessAsync(ct)) return EngineResult.Success;
        var report = await _api.SubmitReportsAsync(_accessToken!, _sessionToken, deviceId, _checks.WindowsVersion, ClientVersion,
            results.Select(c => new ReportCheck(c.Category, c.Result, c.Severity, c.CheckedAt, c.Summary, null)).ToList(), ct);
        MarkBackend(report);
        Update(s => s with { ChecksReported = report.Ok });
        if (!report.Ok) Event("Warning", $"Raportul de verificare nu a putut fi trimis: {report.ErrorMessage}");
        return EngineResult.Success;
    }

    /// <summary>ENABLE PROTECTION: serverul emite sesiunea; abia după primul heartbeat valid starea devine „Active”.</summary>
    public async Task<EngineResult> EnableProtectionAsync(string? game, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var status = GetStatus();
            if (status.Account != AccountState.LoggedIn || status.DeviceId is not { } deviceId)
                return EngineResult.Fail("not_logged_in", "Autentifică-te și înregistrează dispozitivul înainte de a activa protecția.");
            if (status.Protection is ProtectionState.Active or ProtectionState.Reconnecting or ProtectionState.Starting)
                return EngineResult.Fail("already_active", "Protecția este deja activă.");
            if (!await EnsureAccessAsync(ct))
                return EngineResult.Fail("login_required", "Sesiunea de login a expirat. Autentifică-te din nou.");

            Update(s => s with { Protection = ProtectionState.Starting, ProtectionMessage = "Se pornește sesiunea protejată…", Game = game });
            var r = await _api.StartSessionAsync(_accessToken!, deviceId, ClientVersion, game, ct);
            MarkBackend(r);
            if (!r.Ok)
            {
                Update(s => s with { Protection = ProtectionState.Stopped, ProtectionCode = r.ErrorCode, ProtectionMessage = r.ErrorMessage });
                Event("Warning", $"Protecția nu a putut fi activată: {r.ErrorMessage}");
                return EngineResult.Fail(r.ErrorCode!, r.ErrorMessage!);
            }

            _sessionToken = r.Value!.SessionToken;
            _heartbeatInterval = TimeSpan.FromSeconds(Math.Max(5, r.Value.HeartbeatIntervalSeconds));
            Update(s => s with { SessionId = r.Value.SessionId, SessionExpiresAt = r.Value.ExpiresAt });

            var first = await HeartbeatOnceAsync(ct);
            if (first.Ok) Event("Information", "Protecția este activă. Serverul a confirmat sesiunea.");
            StartLoop();
            return first;
        }
        finally { _gate.Release(); }
    }

    public async Task<EngineResult> DisableProtectionAsync(CancellationToken ct)
    {
        await StopLoopAsync();
        await _gate.WaitAsync(ct);
        try
        {
            await EndSessionCoreAsync("Protecția a fost oprită.", ct);
            return EngineResult.Success;
        }
        finally { _gate.Release(); }
    }

    public async Task<EngineResult> LogoutAsync(CancellationToken ct)
    {
        await StopLoopAsync();
        await _gate.WaitAsync(ct);
        try
        {
            await EndSessionCoreAsync(null, ct);
            if (_refreshToken is not null) MarkBackend(await _api.LogoutAsync(_refreshToken, ct));
            _tokens.Clear();
            _accessToken = _refreshToken = null;
            Update(s => new ProtectionStatus
            {
                ClientVersion = s.ClientVersion, BackendUrl = s.BackendUrl, WindowsVersion = s.WindowsVersion,
                DeviceName = _device.DeviceName, Backend = s.Backend, LastBackendContact = s.LastBackendContact,
            });
            Event("Information", "Te-ai deconectat.");
            return EngineResult.Success;
        }
        finally { _gate.Release(); }
    }

    private async Task EndSessionCoreAsync(string? message, CancellationToken ct)
    {
        if (_sessionToken is not null && await EnsureAccessAsync(ct))
            MarkBackend(await _api.EndSessionAsync(_accessToken!, _sessionToken, ct));
        _sessionToken = null;
        Update(s => s with
        {
            Protection = ProtectionState.Off, ProtectionCode = null, ProtectionMessage = message,
            SessionId = null, SessionExpiresAt = null, LastHeartbeatAt = null,
        });
        if (message is not null) Event("Information", message);
    }

    /// <summary>Un heartbeat; serverul decide dacă sesiunea e validă.</summary>
    internal async Task<EngineResult> HeartbeatOnceAsync(CancellationToken ct)
    {
        if (_sessionToken is null) return EngineResult.Fail("no_session", "Nu există sesiune activă.");
        if (!await EnsureAccessAsync(ct))
        {
            if (GetStatus().Backend == BackendState.Offline) return MarkReconnecting();
            return StopWith("login_required", "Sesiunea de login a expirat. Autentifică-te din nou.");
        }

        var hb = await _api.HeartbeatAsync(_accessToken!, _sessionToken, ct);
        MarkBackend(hb);
        if (hb.NetworkError || hb.StatusCode >= 500 || hb.StatusCode == 429) return MarkReconnecting();
        if (!hb.Ok) return StopWith(hb.ErrorCode!, hb.ErrorMessage!);
        if (!hb.Value!.Valid) return StopWith(hb.Value.Code, hb.Value.Message);

        var wasReconnecting = GetStatus().Protection == ProtectionState.Reconnecting;
        Update(s => s with
        {
            Protection = ProtectionState.Active, ProtectionCode = "ok",
            ProtectionMessage = "Sesiune validată de serverul PlayZek.",
            LastHeartbeatAt = Now, SessionExpiresAt = hb.Value.ExpiresAt,
        });
        if (wasReconnecting) Event("Information", "Conexiunea cu serverul a revenit. Protecția continuă.");

        if (hb.Value.ExpiresAt - Now < _options.RenewBefore)
        {
            var renewed = await _api.RenewSessionAsync(_accessToken!, _sessionToken, ct);
            MarkBackend(renewed);
            if (renewed.Ok)
            {
                _sessionToken = renewed.Value!.SessionToken;
                Update(s => s with { SessionExpiresAt = renewed.Value.ExpiresAt });
                Event("Information", "Sesiunea protejată a fost reînnoită.");
            }
        }
        return EngineResult.Success;
    }

    private EngineResult MarkReconnecting()
    {
        if (GetStatus().Protection != ProtectionState.Reconnecting)
            Event("Warning", "Conexiunea cu serverul PlayZek s-a întrerupt. Reîncerc automat.");
        Update(s => s with
        {
            Protection = ProtectionState.Reconnecting, ProtectionCode = "network_error",
            ProtectionMessage = "Fără conexiune la server. Dacă nu revine în ~45 s, serverele de joc nu te vor mai accepta.",
        });
        return EngineResult.Fail("network_error", "Serverul PlayZek nu poate fi contactat.");
    }

    private EngineResult StopWith(string code, string message)
    {
        _sessionToken = null;
        Update(s => s with { Protection = ProtectionState.Stopped, ProtectionCode = code, ProtectionMessage = message });
        Event("Warning", $"Protecția s-a oprit: {message}");
        return EngineResult.Fail(code, message);
    }

    /// <summary>Asigură un access token valid, reîmprospătându-l cu refresh token-ul (rotit) dacă e nevoie.</summary>
    private async Task<bool> EnsureAccessAsync(CancellationToken ct)
    {
        if (_accessToken is not null && _accessExpiresAt - Now > _options.RefreshAccessBefore) return true;
        if (_refreshToken is null) return false;
        var r = await _api.RefreshAsync(_refreshToken, ct);
        MarkBackend(r);
        if (!r.Ok)
        {
            if (!r.NetworkError)
            {
                _tokens.Clear();
                _accessToken = _refreshToken = null;
                Update(s => s with { Account = AccountState.LoggedOut });
            }
            return false;
        }
        SetTokens(r.Value!);
        return true;
    }

    private void SetTokens(LoginResponse login)
    {
        _accessToken = login.AccessToken;
        _accessExpiresAt = login.AccessTokenExpiresAt;
        _refreshToken = login.RefreshToken;
        _tokens.SaveRefreshToken(login.RefreshToken);
    }

    private void StartLoop()
    {
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;
        _loopTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var status = GetStatus();
                var delay = status.Protection == ProtectionState.Reconnecting ? _options.OfflineRetryInterval : _heartbeatInterval;
                try { await Task.Delay(delay, _time, ct); }
                catch (OperationCanceledException) { return; }

                await _gate.WaitAsync(ct);
                try
                {
                    if (_sessionToken is null) return;
                    await HeartbeatOnceAsync(ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { Event("Warning", $"Eroare internă la heartbeat: {e.Message}"); }
                finally { _gate.Release(); }
            }
        }, ct);
    }

    private async Task StopLoopAsync()
    {
        if (_loopCts is null) return;
        await _loopCts.CancelAsync();
        try { if (_loopTask is not null) await _loopTask; } catch (OperationCanceledException) { }
        _loopCts.Dispose();
        _loopCts = null;
        _loopTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopLoopAsync();
        _gate.Dispose();
    }
}
