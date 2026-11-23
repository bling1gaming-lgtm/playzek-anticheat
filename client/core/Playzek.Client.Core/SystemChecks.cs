using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Playzek.Client.Core;

/// <summary>Rezultatul unei verificări locale. Severitatea folosește aceleași niveluri ca backend-ul.</summary>
public sealed record CheckOutcome(string Category, string DisplayName, string Result, string Severity, string Summary,
    DateTime CheckedAt);

public interface ISystemChecks
{
    string WindowsVersion { get; }
    IReadOnlyList<CheckOutcome> RunCompatibilityChecks();
}

/// <summary>
/// Verificări de compatibilitate și configurație de securitate, doar prin interfețe Windows documentate:
/// registrul (versiune, Secure Boot), TBS API (TPM), WMI Win32_DeviceGuard (VBS).
/// Nu modifică nimic și nu cere dezactivarea vreunei protecții.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemChecks : ISystemChecks
{
    public const int Windows10_22H2Build = 19045;
    public const int Windows11FirstBuild = 22000;

    public string WindowsVersion { get; } = DescribeWindows();

    public IReadOnlyList<CheckOutcome> RunCompatibilityChecks()
    {
        var now = DateTime.UtcNow;
        return
        [
            Safe("compat.windows_version", "Versiune Windows", CheckWindowsVersion, now),
            Safe("compat.architecture", "Arhitectură sistem", CheckArchitecture, now),
            Safe("security.secure_boot", "Secure Boot", CheckSecureBoot, now),
            Safe("security.tpm", "TPM 2.0", CheckTpm, now),
            Safe("security.vbs", "Virtualization-based Security", CheckVbs, now),
        ];
    }

    private static CheckOutcome Safe(string category, string name, Func<(string, string, string)> check, DateTime now)
    {
        try
        {
            var (result, severity, summary) = check();
            return new CheckOutcome(category, name, result, severity, summary, now);
        }
        catch (Exception e)
        {
            return new CheckOutcome(category, name, "Error", "Warning", $"Verificarea nu a putut rula: {e.Message}", now);
        }
    }

    internal static (int build, int ubr, string? displayVersion) ReadBuild()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out var b) ? b : Environment.OSVersion.Version.Build;
        var ubr = key?.GetValue("UBR") is int u ? u : 0;
        return (build, ubr, key?.GetValue("DisplayVersion") as string);
    }

    private static string DescribeWindows()
    {
        var (build, ubr, display) = ReadBuild();
        var family = build >= Windows11FirstBuild ? "Windows 11" : "Windows 10";
        return $"{family} {display ?? ""} (build {build}.{ubr})".Replace("  ", " ");
    }

    /// <summary>Spec: Windows 10 22H2 (build 19045) și Windows 11 (build ≥ 22000).</summary>
    internal static (string, string, string) EvaluateBuild(int build) => build switch
    {
        >= Windows11FirstBuild => ("Pass", "Information", $"Windows 11, build {build} — suportat."),
        Windows10_22H2Build => ("Pass", "Information", "Windows 10 22H2 — suportat."),
        > Windows10_22H2Build => ("Warning", "Warning", $"Build {build} necunoscut între Windows 10 22H2 și Windows 11."),
        _ => ("Fail", "Warning", $"Build {build} nu e suportat. Actualizează la Windows 10 22H2 sau Windows 11."),
    };

    private static (string, string, string) CheckWindowsVersion() => EvaluateBuild(ReadBuild().build);

    private static (string, string, string) CheckArchitecture() =>
        Environment.Is64BitOperatingSystem
            ? ("Pass", "Information", $"Sistem pe 64 de biți ({RuntimeInformation.OSArchitecture}).")
            : ("Fail", "Warning", "Sistemele pe 32 de biți nu sunt suportate.");

    private static (string, string, string) CheckSecureBoot()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        if (key?.GetValue("UEFISecureBootEnabled") is not int value)
            return ("NotApplicable", "Information", "Sistemul nu raportează Secure Boot (firmware BIOS/legacy).");
        return value == 1
            ? ("Pass", "Information", "Secure Boot este activat.")
            : ("Warning", "Warning", "Secure Boot este dezactivat în firmware. Nu e obligatoriu, dar e recomandat.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TpmDeviceInfo
    {
        public uint StructVersion;
        public uint TpmVersion;
        public uint TpmInterfaceType;
        public uint TpmImpRevision;
    }

    [DllImport("tbs.dll", ExactSpelling = true)]
    private static extern uint Tbsi_GetDeviceInfo(uint size, out TpmDeviceInfo info);

    private const uint TbsSuccess = 0;
    private const uint TbsTpmNotFound = 0x8028400F;
    private const uint TpmVersion20 = 2;

    private static (string, string, string) CheckTpm()
    {
        var rc = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), out var info);
        if (rc == TbsTpmNotFound) return ("Warning", "Warning", "Nu a fost găsit niciun TPM.");
        if (rc != TbsSuccess) return ("Error", "Warning", $"Starea TPM nu a putut fi citită (cod 0x{rc:X8}).");
        return info.TpmVersion == TpmVersion20
            ? ("Pass", "Information", "TPM 2.0 disponibil.")
            : ("Warning", "Warning", "Există un TPM, dar nu e versiunea 2.0.");
    }

    private static (string, string, string) CheckVbs()
    {
        using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard",
            "SELECT VirtualizationBasedSecurityStatus FROM Win32_DeviceGuard");
        foreach (var obj in searcher.Get())
        {
            var status = Convert.ToInt32(obj["VirtualizationBasedSecurityStatus"], System.Globalization.CultureInfo.InvariantCulture);
            return status switch
            {
                2 => ("Pass", "Information", "VBS este activ și rulează."),
                1 => ("Warning", "Information", "VBS e activat, dar nu rulează."),
                _ => ("Warning", "Information", "VBS nu este activat. Nu e obligatoriu."),
            };
        }
        return ("NotApplicable", "Information", "Sistemul nu raportează starea VBS.");
    }
}
