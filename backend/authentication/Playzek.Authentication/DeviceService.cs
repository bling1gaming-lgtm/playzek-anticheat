using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Playzek.Data;

namespace Playzek.Authentication;

public sealed record DeviceRegistrationRequest(string? Fingerprint, string? DeviceName, string? OsVersion, string? ClientVersion);
public sealed record DeviceInfo(Guid DeviceId, string DeviceName, string Status, DateTime FirstSeenAt, DateTime LastSeenAt);

/// <summary>
/// Înregistrare dispozitiv. Clientul trimite o amprentă care e deja un hash SHA-256 (nu identificatori bruți);
/// serverul o mai hash-uiește o dată înainte de stocare.
/// </summary>
public sealed class DeviceService(
    PlayzekDbContext db,
    AuditWriter audit,
    IOptions<SecurityPolicyOptions> security,
    TimeProvider time)
{
    public async Task<ServiceResult<DeviceInfo>> RegisterAsync(Guid userId, DeviceRegistrationRequest req, string? ip,
        CancellationToken ct)
    {
        if (!InputRules.IsSha256Hex(req.Fingerprint))
            return new ServiceError(ErrorKind.Validation, "invalid_fingerprint",
                "Amprenta dispozitivului trebuie să fie un hash SHA-256 (64 caractere hex, litere mici).");
        var name = (req.DeviceName ?? "").Trim();
        if (name.Length is 0 or > 64)
            return new ServiceError(ErrorKind.Validation, "invalid_device_name", "Numele dispozitivului are 1–64 caractere.");
        if (req.ClientVersion is not null && !InputRules.IsValidVersion(req.ClientVersion))
            return new ServiceError(ErrorKind.Validation, "invalid_client_version", "Versiune de client invalidă.");
        var os = req.OsVersion is { Length: > 64 } ? req.OsVersion[..64] : req.OsVersion;

        var fingerprintHash = Crypto.Sha256Hex(req.Fingerprint!);
        var now = time.GetUtcNow().UtcDateTime;
        var device = await db.RegisteredDevices
            .FirstOrDefaultAsync(d => d.UserId == userId && d.FingerprintHash == fingerprintHash, ct);

        if (device is not null)
        {
            if (device.Status != DeviceStatus.Active)
                return new ServiceError(ErrorKind.Forbidden, "device_" + device.Status.ToString().ToLowerInvariant(),
                    "Acest dispozitiv a fost dezactivat pentru contul tău. Contactează echipa PlayZek.");
            device.LastSeenAt = now;
            device.DeviceName = name;
            device.OsVersion = os;
            device.ClientVersion = req.ClientVersion;
        }
        else
        {
            var activeCount = await db.RegisteredDevices
                .CountAsync(d => d.UserId == userId && d.Status == DeviceStatus.Active, ct);
            if (activeCount >= security.Value.MaxActiveDevicesPerUser)
                return new ServiceError(ErrorKind.Conflict, "device_limit_reached",
                    $"Ai deja {activeCount} dispozitive înregistrate (maxim {security.Value.MaxActiveDevicesPerUser}). " +
                    "Elimină un dispozitiv vechi din cont.");

            device = new RegisteredDevice
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                FingerprintHash = fingerprintHash,
                DeviceName = name,
                OsVersion = os,
                ClientVersion = req.ClientVersion,
                FirstSeenAt = now,
                LastSeenAt = now,
            };
            db.RegisteredDevices.Add(device);
            audit.Add(ActorType.Player, userId.ToString(), "device.registered", "device", device.Id.ToString(), ip);
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult<DeviceInfo>.Ok(ToInfo(device));
    }

    public async Task<List<DeviceInfo>> ListAsync(Guid userId, CancellationToken ct) =>
        (await db.RegisteredDevices.Where(d => d.UserId == userId).OrderByDescending(d => d.LastSeenAt).ToListAsync(ct))
        .Select(ToInfo).ToList();

    public async Task<ServiceResult<bool>> RevokeAsync(Guid userId, Guid deviceId, string? ip, CancellationToken ct)
    {
        var device = await db.RegisteredDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId, ct);
        if (device is null) return new ServiceError(ErrorKind.NotFound, "device_not_found", "Dispozitivul nu există.");
        if (device.Status == DeviceStatus.Active) device.Status = DeviceStatus.Revoked;

        var now = time.GetUtcNow().UtcDateTime;
        var sessions = await db.AntiCheatSessions
            .Where(s => s.DeviceId == deviceId && s.Status == AntiCheatSessionStatus.Active).ToListAsync(ct);
        foreach (var s in sessions)
        {
            s.Status = AntiCheatSessionStatus.Revoked;
            s.EndedAt = now;
            s.EndReason = "device_revoked";
        }
        audit.Add(ActorType.Player, userId.ToString(), "device.revoked", "device", deviceId.ToString(), ip);
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    private static DeviceInfo ToInfo(RegisteredDevice d) =>
        new(d.Id, d.DeviceName, d.Status.ToString(), d.FirstSeenAt, d.LastSeenAt);
}
