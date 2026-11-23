using Playzek.Authentication;
using Playzek.Data;

namespace Playzek.Api;

/// <summary>
/// Comenzi rulate de operator pe VPS:
///   Playzek.Api migrate
///   Playzek.Api create-server --name "PlayZek #1" --game cs16 [--address 1.2.3.4:27015]
/// Cheia API a serverului e afișată o singură dată; în baza de date rămâne doar hash-ul ei.
/// </summary>
public static class CliCommands
{
    public static async Task<bool> TryRunAsync(WebApplication app, string[] args)
    {
        switch (args[0])
        {
            case "migrate":
            {
                await using var scope = app.Services.CreateAsyncScope();
                await Database.MigrateAsync(scope.ServiceProvider.GetRequiredService<PlayzekDbContext>());
                Console.WriteLine("Migrațiile au fost aplicate.");
                return true;
            }
            case "create-server":
            {
                await using var scope = app.Services.CreateAsyncScope();
                var svc = scope.ServiceProvider.GetRequiredService<GameServerService>();
                var r = await svc.CreateAsync(Arg(args, "--name"), Arg(args, "--game"), Arg(args, "--address"),
                    "cli:" + Environment.UserName, CancellationToken.None);
                if (!r.Succeeded)
                {
                    Console.Error.WriteLine($"Eroare: {r.Error!.Message}");
                    Environment.ExitCode = 1;
                    return true;
                }
                Console.WriteLine($"Server creat: {r.Value!.Name} ({r.Value.Game}), id {r.Value.ServerId}");
                Console.WriteLine($"Cheie API (se afișează O SINGURĂ DATĂ, pune-o în configurația pluginului): {r.Value.ApiKey}");
                return true;
            }
            default:
                return false;
        }
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
