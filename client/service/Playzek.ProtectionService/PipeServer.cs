using Playzek.Client.Core;

namespace Playzek.ProtectionService;

/// <summary>Ascultă comenzile aplicației desktop pe named pipe-ul local și le execută în ProtectionEngine.</summary>
public sealed class PipeServer(ProtectionEngine engine, ILogger<PipeServer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await engine.InitializeAsync(stoppingToken);
        log.LogInformation("PLAYZEK Protection Service {Version} pornit; pipe {Pipe}", engine.ClientVersion, Ipc.PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var pipe = Ipc.CreateServer();
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            _ = Task.Run(() => HandleAsync(pipe, stoppingToken), stoppingToken);
        }

        await engine.DisposeAsync();
    }

    private async Task HandleAsync(System.IO.Pipes.NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var req = await Ipc.ReadAsync<IpcRequest>(pipe, timeout.Token);
                if (req is null) return;
                var result = await ExecuteAsync(req, timeout.Token);
                await Ipc.WriteAsync(pipe, new IpcResponse(result.Ok, result.Code, result.Message, engine.GetStatus()), timeout.Token);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException)
            {
                log.LogWarning("Cerere IPC respinsă: {Error}", e.Message);
            }
        }
    }

    private Task<EngineResult> ExecuteAsync(IpcRequest req, CancellationToken ct) => req.Command switch
    {
        IpcCommands.Status => Task.FromResult(EngineResult.Success),
        IpcCommands.Login => engine.LoginAsync(req.Username ?? "", req.Password ?? "", ct),
        IpcCommands.Register => engine.RegisterAsync(req.Username ?? "", req.Password ?? "", req.SteamId, ct),
        IpcCommands.Logout => engine.LogoutAsync(ct),
        IpcCommands.RunChecks => engine.RunChecksAsync(ct),
        IpcCommands.EnableProtection => engine.EnableProtectionAsync(req.Game, ct),
        IpcCommands.DisableProtection => engine.DisableProtectionAsync(ct),
        _ => Task.FromResult(EngineResult.Fail("unknown_command", "Comandă necunoscută.")),
    };
}
