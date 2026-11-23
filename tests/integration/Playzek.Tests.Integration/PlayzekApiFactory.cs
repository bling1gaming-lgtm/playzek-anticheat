using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Playzek.Api;
using Playzek.Authentication;
using Playzek.Data;

namespace Playzek.Tests.Integration;

/// <summary>Ceas controlat de test (pentru expirarea sesiunilor fără să așteptăm minute reale).</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>
/// Pornește API-ul real în memorie. Baza de date:
///  - implicit SQLite in-memory (rulare locală, fără servicii externe);
///  - PostgreSQL real dacă e setată PLAYZEK_TEST_POSTGRES (CI) — atunci se aplică migrațiile reale.
/// </summary>
public sealed class PlayzekApiFactory : WebApplicationFactory<Program>
{
    public TestClock Clock { get; } = new();
    private readonly SqliteConnection? _sqlite;
    private readonly string? _postgres;

    public PlayzekApiFactory()
    {
        var pg = Environment.GetEnvironmentVariable("PLAYZEK_TEST_POSTGRES");
        if (!string.IsNullOrEmpty(pg))
        {
            _postgres = $"{pg};Database=playzek_test_{Guid.NewGuid():N}";
        }
        else
        {
            _sqlite = new SqliteConnection("Data Source=:memory:");
            _sqlite.Open();
        }
    }

    public bool UsesPostgres => _postgres is not null;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Playzek:Jwt:SigningKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
        builder.UseSetting("Playzek:Database:Provider", UsesPostgres ? "Postgres" : "Sqlite");
        builder.UseSetting("ConnectionStrings:Playzek", _postgres ?? "Data Source=:memory:");
        builder.UseSetting("Playzek:RateLimits:AuthPerMinute", "1000");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            if (_sqlite is not null)
            {
                services.RemoveAll<DbContextOptions<PlayzekDbContext>>();
                services.RemoveAll<PlayzekDbContext>();
                services.AddDbContext<PlayzekDbContext>(o => o.UseSqlite(_sqlite));
            }
        });
    }

    public async Task InitializeAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        await Database.MigrateAsync(scope.ServiceProvider.GetRequiredService<PlayzekDbContext>());
    }

    public async Task<T> WithDbAsync<T>(Func<PlayzekDbContext, IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<PlayzekDbContext>(), scope.ServiceProvider);
    }

    /// <summary>Șterge baza de test PostgreSQL ÎNAINTE de oprirea host-ului (după oprire nu mai avem servicii).</summary>
    public override async ValueTask DisposeAsync()
    {
        if (UsesPostgres)
        {
            await using var scope = Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PlayzekDbContext>().Database.EnsureDeletedAsync();
        }
        await base.DisposeAsync();
        _sqlite?.Dispose();
    }
}

/// <summary>Jucător complet pregătit: cont, login, dispozitiv, eventual sesiune.</summary>
public sealed record TestPlayer(string Username, string Password, string SteamId, Guid UserId, string AccessToken,
    string RefreshToken, Guid DeviceId);

public static class ApiHelpers
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static int _counter;

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>(Json));

    public static string NewSteamId() =>
        "765611980" + Interlocked.Increment(ref _counter).ToString("D8") ;

    public static string Fingerprint(string seed) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)));

    public static HttpClient Authed(this PlayzekApiFactory f, string accessToken, string? sessionToken = null)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (sessionToken is not null) c.DefaultRequestHeaders.Add(Endpoints.SessionHeader, sessionToken);
        return c;
    }

    public static async Task<TestPlayer> CreatePlayerAsync(this PlayzekApiFactory f, string? steamId = null)
    {
        var client = f.CreateClient();
        var username = "player" + Guid.NewGuid().ToString("N")[..10];
        const string password = "parola-foarte-sigura";
        steamId ??= NewSteamId();

        var reg = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, password, steamId });
        reg.EnsureSuccessStatusCode();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        login.EnsureSuccessStatusCode();
        var lj = await login.ReadJsonAsync();
        var access = lj.GetProperty("accessToken").GetString()!;

        var dev = await f.Authed(access).PostAsJsonAsync("/api/v1/devices", new
        {
            fingerprint = Fingerprint(username),
            deviceName = "PC-TEST",
            osVersion = "Windows 11 26100",
            clientVersion = "0.1.0",
        });
        dev.EnsureSuccessStatusCode();
        var dj = await dev.ReadJsonAsync();

        return new TestPlayer(username, password, steamId, lj.GetProperty("userId").GetGuid(), access,
            lj.GetProperty("refreshToken").GetString()!, dj.GetProperty("deviceId").GetGuid());
    }

    public static async Task<string> StartSessionAsync(this PlayzekApiFactory f, TestPlayer p, string? game = "cs16")
    {
        var r = await f.Authed(p.AccessToken).PostAsJsonAsync("/api/v1/anticheat/sessions/",
            new { deviceId = p.DeviceId, clientVersion = "0.1.0", game });
        r.EnsureSuccessStatusCode();
        return (await r.ReadJsonAsync()).GetProperty("sessionToken").GetString()!;
    }

    public static async Task<string> CreateGameServerAsync(this PlayzekApiFactory f, string game = "cs16") =>
        await f.WithDbAsync(async (_, sp) =>
        {
            var r = await sp.GetRequiredService<GameServerService>().CreateAsync("Test " + game, game, null, "test",
                CancellationToken.None);
            return r.Value!.ApiKey;
        });

    public static HttpClient GameServerClient(this PlayzekApiFactory f, string apiKey)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(GameServerService.ApiKeyHeader, apiKey);
        return c;
    }

    public static async Task<JsonElement> AuthorizeAsync(this HttpClient server, string steamId)
    {
        var r = await server.PostAsJsonAsync("/api/v1/game-server/players/authorize", new { steamId });
        r.EnsureSuccessStatusCode();
        return await r.ReadJsonAsync();
    }
}
