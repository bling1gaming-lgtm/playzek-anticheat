using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Playzek.Client.Core;

/// <summary>Comenzi pe care aplicația desktop le poate trimite serviciului.</summary>
public static class IpcCommands
{
    public const string Status = "status";
    public const string Login = "login";
    public const string Register = "register";
    public const string Logout = "logout";
    public const string RunChecks = "run_checks";
    public const string EnableProtection = "enable_protection";
    public const string DisableProtection = "disable_protection";
}

public sealed record IpcRequest(string Command, string? Username = null, string? Password = null, string? SteamId = null,
    string? Game = null);

public sealed record IpcResponse(bool Ok, string? Code, string? Message, ProtectionStatus? Status);

/// <summary>
/// Canal local între aplicația desktop (fără privilegii) și serviciul de protecție.
/// Named pipe, un mesaj JSON pe linie. ACL: doar utilizatori locali autentificați; accesul din rețea e interzis.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Ipc
{
    public const string PipeName = "PLAYZEK.AntiCheat.v1";
    public const int MaxMessageBytes = 64 * 1024;

    public static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using (var self = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, BackendClient.Json);
        await stream.WriteAsync(bytes, ct);
        await stream.WriteAsync("\n"u8.ToArray(), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Citește o linie (un mesaj). Refuză mesajele mai mari de 64 KB.</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) return default;
            if (one[0] == (byte)'\n') break;
            buffer.WriteByte(one[0]);
            if (buffer.Length > MaxMessageBytes) throw new InvalidDataException("Mesaj IPC prea mare.");
        }
        return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(buffer.ToArray()), BackendClient.Json);
    }

    /// <summary>Client folosit de aplicația desktop: o conexiune per cerere (simplu și robust la repornirea serviciului).</summary>
    public static async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct, int timeoutMs = 2000)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutMs, ct);
        }
        catch (TimeoutException)
        {
            return new IpcResponse(false, "service_unavailable",
                "Serviciul PLAYZEK Protection Service nu rulează. Repornește calculatorul sau repară instalarea.", null);
        }
        await WriteAsync(pipe, request, ct);
        return await ReadAsync<IpcResponse>(pipe, ct)
               ?? new IpcResponse(false, "service_unavailable", "Serviciul a închis conexiunea.", null);
    }
}
