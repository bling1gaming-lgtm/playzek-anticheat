using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Playzek.Client.Core;

public interface IDeviceIdentity
{
    /// <summary>SHA-256 hex (litere mici). Identificatorul brut nu părăsește calculatorul.</summary>
    string Fingerprint { get; }
    string DeviceName { get; }
}

/// <summary>
/// Amprenta dispozitivului = SHA-256("playzek-device-v1|" + MachineGuid). MachineGuid e generat de Windows
/// la instalare și e citibil de orice utilizator; nu folosim MAC, serii de disc sau alte date hardware.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceIdentity : IDeviceIdentity
{
    public string Fingerprint { get; } = Compute(ReadMachineGuid());
    public string DeviceName { get; } = Environment.MachineName.Length > 64 ? Environment.MachineName[..64] : Environment.MachineName;

    public static string Compute(string machineGuid) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("playzek-device-v1|" + machineGuid.Trim().ToLowerInvariant())));

    private static string ReadMachineGuid()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string
               ?? throw new InvalidOperationException("MachineGuid nu poate fi citit din registru.");
    }
}
