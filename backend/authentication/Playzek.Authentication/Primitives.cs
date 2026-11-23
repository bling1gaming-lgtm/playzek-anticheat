using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;

namespace Playzek.Authentication;

/// <summary>Rezultatul unei operații: fie valoare, fie un cod de eroare stabil + mesaj clar.</summary>
public sealed record ServiceResult<T>(T? Value, ServiceError? Error)
{
    public bool Succeeded => Error is null;
    public static ServiceResult<T> Ok(T value) => new(value, null);
    public static ServiceResult<T> Fail(ServiceError error) => new(default, error);
    public static implicit operator ServiceResult<T>(ServiceError error) => Fail(error);
}

public enum ErrorKind { Validation, Unauthorized, Forbidden, NotFound, Conflict, Locked }

public sealed record ServiceError(ErrorKind Kind, string Code, string Message);

public static class Crypto
{
    /// <summary>Token opac, aleator, 256 biți, codat base64url.</summary>
    public static string NewOpaqueToken(int bytes = 32) =>
        Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static bool FixedTimeEqualsHex(string a, string b) =>
        a.Length == b.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>PBKDF2-HMAC-SHA512 (formatul v3 din ASP.NET Core Identity, 100.000 iterații), cu sare per parolă.</summary>
public sealed class PasswordHashing
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object Subject = new();

    public string Hash(string password) => Hasher.HashPassword(Subject, password);

    public bool Verify(string hash, string password) =>
        Hasher.VerifyHashedPassword(Subject, hash, password) != PasswordVerificationResult.Failed;
}

public static partial class InputRules
{
    public const int PasswordMinLength = 10;
    public const int PasswordMaxLength = 128;

    [GeneratedRegex("^[A-Za-z0-9_.-]{3,32}$")]
    private static partial Regex UsernameRegex();

    [GeneratedRegex("^7656119[0-9]{10}$")]
    private static partial Regex SteamId64Regex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256HexRegex();

    [GeneratedRegex(@"^\d{1,5}(\.\d{1,5}){1,3}$")]
    private static partial Regex VersionRegex();

    public static bool IsValidUsername(string? v) => v is not null && UsernameRegex().IsMatch(v);
    public static bool IsValidSteamId64(string? v) => v is not null && SteamId64Regex().IsMatch(v);
    public static bool IsSha256Hex(string? v) => v is not null && Sha256HexRegex().IsMatch(v);
    public static bool IsValidVersion(string? v) => v is not null && VersionRegex().IsMatch(v);

    public static ServiceError? ValidatePassword(string? password)
    {
        if (password is null || password.Length < PasswordMinLength)
            return new(ErrorKind.Validation, "password_too_short",
                $"Parola trebuie să aibă cel puțin {PasswordMinLength} caractere.");
        if (password.Length > PasswordMaxLength)
            return new(ErrorKind.Validation, "password_too_long",
                $"Parola poate avea cel mult {PasswordMaxLength} caractere.");
        return null;
    }

    /// <summary>Convertește un SteamID în format STEAM_X:Y:Z (cum îl raportează HLDS/AMXX) în SteamID64.</summary>
    public static string? NormalizeSteamId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (IsValidSteamId64(raw)) return raw;
        var m = Regex.Match(raw, @"^STEAM_[0-5]:([01]):(\d{1,10})$");
        if (!m.Success) return null;
        var y = long.Parse(m.Groups[1].Value);
        var z = long.Parse(m.Groups[2].Value);
        var id64 = 76561197960265728L + z * 2 + y;
        return id64.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
