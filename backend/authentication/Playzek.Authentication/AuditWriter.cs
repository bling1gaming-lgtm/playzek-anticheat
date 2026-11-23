using Playzek.Data;

namespace Playzek.Authentication;

/// <summary>
/// Adaugă intrări în audit_logs în același DbContext — se salvează în aceeași tranzacție
/// cu operația auditată (SaveChanges este atomic).
/// </summary>
public sealed class AuditWriter(PlayzekDbContext db, TimeProvider time)
{
    public void Add(ActorType actorType, string? actorId, string action,
        string? targetType = null, string? targetId = null, string? ip = null, string? details = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            OccurredAt = time.GetUtcNow().UtcDateTime,
            ActorType = actorType,
            ActorId = actorId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            IpAddress = ip,
            Details = details is { Length: > 4096 } ? details[..4096] : details,
        });
    }
}
