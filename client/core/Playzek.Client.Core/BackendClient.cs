using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Playzek.Client.Core;

public sealed record LoginResponse(Guid UserId, string Username, string AccessToken, DateTime AccessTokenExpiresAt,
    string RefreshToken, DateTime RefreshTokenExpiresAt);
public sealed record RegisterResponse(Guid UserId, string Username, string? SteamId64);
public sealed record MeResponse(Guid UserId, string Username, string? SteamId64, string AccountStatus, bool Banned,
    string? BanMessage, DateTime CreatedAt);
public sealed record DeviceResponse(Guid DeviceId, string DeviceName, string Status, DateTime FirstSeenAt, DateTime LastSeenAt);
public sealed record SessionStartResponse(Guid SessionId, string SessionToken, DateTime ExpiresAt,
    int HeartbeatIntervalSeconds, int HeartbeatGraceSeconds);
public sealed record HeartbeatResponse(Guid SessionId, bool Valid, string Code, string Message, DateTime ExpiresAt,
    DateTime? LastHeartbeatAt);
public sealed record ReportCheck(string Category, string Result, string Severity, DateTime CheckedAt, string? Summary,
    string? EvidenceReference);

/// <summary>Rezultatul unui apel: succes, eroare de la API (cod stabil + mesaj) sau eroare de rețea.</summary>
public sealed record ApiResult<T>(T? Value, int StatusCode, string? ErrorCode, string? ErrorMessage, bool NetworkError)
{
    public bool Ok => ErrorCode is null && !NetworkError;
    public static ApiResult<T> Success(T v, int status) => new(v, status, null, null, false);
}

/// <summary>
/// Client HTTP pentru PLAYZEK AC Backend. Refuză HTTP necriptat către orice host care nu e local.
/// </summary>
public sealed class BackendClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;

    public BackendClient(HttpClient http)
    {
        if (http.BaseAddress is null) throw new ArgumentException("HttpClient.BaseAddress lipsește.");
        EnsureSecureEndpoint(http.BaseAddress);
        _http = http;
    }

    public Uri BaseAddress => _http.BaseAddress!;

    public static void EnsureSecureEndpoint(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttps) return;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return;
        throw new InvalidOperationException(
            $"Backend-ul trebuie accesat prin HTTPS (primit: {uri}). HTTP e permis doar pe localhost pentru dezvoltare.");
    }

    public Task<ApiResult<RegisterResponse>> RegisterAsync(string username, string password, string? steamId, CancellationToken ct) =>
        SendAsync<RegisterResponse>(HttpMethod.Post, "api/v1/auth/register", new { username, password, steamId }, null, null, ct);

    public Task<ApiResult<LoginResponse>> LoginAsync(string username, string password, CancellationToken ct) =>
        SendAsync<LoginResponse>(HttpMethod.Post, "api/v1/auth/login", new { username, password }, null, null, ct);

    public Task<ApiResult<LoginResponse>> RefreshAsync(string refreshToken, CancellationToken ct) =>
        SendAsync<LoginResponse>(HttpMethod.Post, "api/v1/auth/refresh", new { refreshToken }, null, null, ct);

    public Task<ApiResult<object>> LogoutAsync(string refreshToken, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, "api/v1/auth/logout", new { refreshToken }, null, null, ct);

    public Task<ApiResult<MeResponse>> MeAsync(string access, CancellationToken ct) =>
        SendAsync<MeResponse>(HttpMethod.Get, "api/v1/me", null, access, null, ct);

    public Task<ApiResult<DeviceResponse>> RegisterDeviceAsync(string access, string fingerprint, string deviceName,
        string osVersion, string clientVersion, CancellationToken ct) =>
        SendAsync<DeviceResponse>(HttpMethod.Post, "api/v1/devices",
            new { fingerprint, deviceName, osVersion, clientVersion }, access, null, ct);

    public Task<ApiResult<SessionStartResponse>> StartSessionAsync(string access, Guid deviceId, string clientVersion,
        string? game, CancellationToken ct) =>
        SendAsync<SessionStartResponse>(HttpMethod.Post, "api/v1/anticheat/sessions/",
            new { deviceId, clientVersion, game }, access, null, ct);

    public Task<ApiResult<HeartbeatResponse>> HeartbeatAsync(string access, string session, CancellationToken ct) =>
        SendAsync<HeartbeatResponse>(HttpMethod.Post, "api/v1/anticheat/sessions/heartbeat", null, access, session, ct);

    public Task<ApiResult<SessionStartResponse>> RenewSessionAsync(string access, string session, CancellationToken ct) =>
        SendAsync<SessionStartResponse>(HttpMethod.Post, "api/v1/anticheat/sessions/renew", null, access, session, ct);

    public Task<ApiResult<object>> EndSessionAsync(string access, string session, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, "api/v1/anticheat/sessions/end", null, access, session, ct);

    public Task<ApiResult<JsonElement>> SubmitReportsAsync(string access, string? session, Guid deviceId,
        string windowsVersion, string clientVersion, IReadOnlyList<ReportCheck> checks, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Post, "api/v1/integrity-reports",
            new { deviceId, windowsVersion, clientVersion, checks }, access, session, ct);

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, string? access,
        string? session, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        if (access is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        if (session is not null) req.Headers.Add("X-Playzek-Session", session);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new ApiResult<T>(default, 0, "network_error", "Serverul PlayZek nu poate fi contactat.", true);
        }

        using (resp)
        {
            var status = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode == HttpStatusCode.NoContent || resp.Content.Headers.ContentLength == 0)
                    return ApiResult<T>.Success(default!, status);
                var value = await resp.Content.ReadFromJsonAsync<T>(Json, ct);
                return ApiResult<T>.Success(value!, status);
            }

            string code = "http_" + status, message = $"Eroare de la server ({status}).";
            try
            {
                var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
                if (problem.ValueKind == JsonValueKind.Object)
                {
                    if (problem.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString()!;
                    if (problem.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String) message = d.GetString()!;
                }
            }
            catch (JsonException)
            {
                // Corp ne-JSON (ex. 429 din rate limiter) — păstrăm codul HTTP.
            }
            if (status == 429) message = "Prea multe cereri. Încearcă din nou peste un minut.";
            return new ApiResult<T>(default, status, code, message, false);
        }
    }
}
