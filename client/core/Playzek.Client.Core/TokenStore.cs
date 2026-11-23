using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Playzek.Client.Core;

public interface ITokenStore
{
    string? LoadRefreshToken();
    void SaveRefreshToken(string token);
    void Clear();
}

/// <summary>
/// Stocare securizată a refresh token-ului cu DPAPI (scope CurrentUser al contului serviciului).
/// Serviciul rulează ca LocalService, deci doar acel cont (și administratorii) îl pot decripta.
/// Aplicația desktop nu vede niciodată token-urile.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenStore(string path) : ITokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PLAYZEK-ANTI-CHEAT/refresh-token/v1");

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PLAYZEK", "AntiCheat", "session.bin");

    public string? LoadRefreshToken()
    {
        if (!File.Exists(path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            Clear(); // Fișier corupt sau criptat de alt cont — îl ignorăm.
            return null;
        }
    }

    public void SaveRefreshToken(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        File.Move(tmp, path, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
