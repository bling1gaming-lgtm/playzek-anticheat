using Microsoft.EntityFrameworkCore;
using Playzek.Authentication;
using Playzek.Data;

namespace Playzek.Api;

public sealed record IntegrityCheckItem(string? Category, string? Result, string? Severity, DateTime? CheckedAt,
    string? Summary, string? EvidenceReference);

public sealed record IntegrityReportBatch(Guid DeviceId, string? WindowsVersion, string? ClientVersion,
    List<IntegrityCheckItem>? Checks);

public sealed record IntegrityReportAccepted(List<Guid> ReportIds);

/// <summary>
/// Primește rezultatele verificărilor de integritate făcute de client. Fiecare verificare devine un raport
/// structurat (ID unic, jucător, dispozitiv, sesiune, timestamp, Windows, versiune client, categorie,
/// rezultat, severitate, referință dovadă). Nu se acceptă conținut de fișiere sau date personale.
/// </summary>
public sealed class IntegrityReportService(PlayzekDbContext db, TimeProvider time)
{
    public const int MaxChecksPerBatch = 100;

    public async Task<ServiceResult<IntegrityReportAccepted>> SubmitAsync(Guid userId, string? sessionToken,
        IntegrityReportBatch batch, CancellationToken ct)
    {
        if (batch.Checks is not { Count: > 0 and <= MaxChecksPerBatch })
            return new ServiceError(ErrorKind.Validation, "invalid_checks", $"Trimite între 1 și {MaxChecksPerBatch} verificări.");
        if (string.IsNullOrWhiteSpace(batch.WindowsVersion) || batch.WindowsVersion.Length > 64)
            return new ServiceError(ErrorKind.Validation, "invalid_windows_version", "Versiunea Windows lipsește.");
        if (!InputRules.IsValidVersion(batch.ClientVersion))
            return new ServiceError(ErrorKind.Validation, "invalid_client_version", "Versiune de client invalidă.");

        var device = await db.RegisteredDevices.FirstOrDefaultAsync(d => d.Id == batch.DeviceId && d.UserId == userId, ct);
        if (device is null)
            return new ServiceError(ErrorKind.NotFound, "device_not_registered", "Dispozitivul nu e înregistrat pe contul tău.");

        Guid? sessionId = null;
        if (!string.IsNullOrEmpty(sessionToken) && sessionToken.Length <= 128)
        {
            var hash = Crypto.Sha256Hex(sessionToken);
            sessionId = await db.AntiCheatSessions.Where(s => s.TokenHash == hash && s.UserId == userId)
                .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var ids = new List<Guid>();
        foreach (var c in batch.Checks)
        {
            if (string.IsNullOrWhiteSpace(c.Category) || c.Category.Length > 64)
                return new ServiceError(ErrorKind.Validation, "invalid_category", "Categoria verificării lipsește sau e prea lungă.");
            if (!Enum.TryParse<CheckResult>(c.Result, true, out var result) || !Enum.IsDefined(result))
                return new ServiceError(ErrorKind.Validation, "invalid_result", $"Rezultat necunoscut: {c.Result}.");
            if (!Enum.TryParse<Severity>(c.Severity, true, out var severity) || !Enum.IsDefined(severity))
                return new ServiceError(ErrorKind.Validation, "invalid_severity", $"Severitate necunoscută: {c.Severity}.");
            // „Încălcare confirmată” e o decizie umană după revizuire, nu ceva ce poate raporta clientul.
            if (severity == Severity.ConfirmedViolation)
                return new ServiceError(ErrorKind.Validation, "severity_not_allowed",
                    "Clientul nu poate raporta o încălcare confirmată; asta se decide doar la revizuire.");

            var checkedAt = c.CheckedAt?.ToUniversalTime() ?? now;
            if (checkedAt > now.AddMinutes(5)) checkedAt = now;

            var report = new IntegrityReport
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                DeviceId = device.Id,
                AntiCheatSessionId = sessionId,
                CheckedAt = checkedAt,
                ReceivedAt = now,
                WindowsVersion = batch.WindowsVersion,
                ClientVersion = batch.ClientVersion!,
                Category = c.Category,
                Result = result,
                Severity = severity,
                Summary = c.Summary is { Length: > 1024 } ? c.Summary[..1024] : c.Summary,
                EvidenceReference = c.EvidenceReference is { Length: > 256 } ? c.EvidenceReference[..256] : c.EvidenceReference,
            };
            db.IntegrityReports.Add(report);
            ids.Add(report.Id);
        }
        await db.SaveChangesAsync(ct);
        return ServiceResult<IntegrityReportAccepted>.Ok(new IntegrityReportAccepted(ids));
    }
}
