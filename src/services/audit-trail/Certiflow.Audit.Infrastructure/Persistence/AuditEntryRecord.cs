using Certiflow.Audit.Domain;

namespace Certiflow.Audit.Infrastructure.Persistence;

/// <summary>
/// How an <see cref="AuditEntry"/> is stored.
/// <para>
/// A persistence model rather than mapping the entity directly, for the same reason BC3 and BC5
/// have one: EF cannot construct it. Every property here is settable and every constructor
/// parameter matches a column, whereas <see cref="AuditEntry"/> is deliberately the opposite —
/// get-only throughout, no mutating method anywhere, and a private constructor that takes an
/// <c>entryId</c> which the type exposes only as an alias for its identity.
/// </para>
/// <para>
/// Relaxing any of that to please the ORM would weaken the exact property the audit trail is sold
/// on: that no code path in the assembly can alter an entry after it exists. The mapping cost is
/// this file.
/// </para>
/// </summary>
public sealed class AuditEntryRecord
{
    private AuditEntryRecord()
    {
        Actor = null!;
        Action = null!;
        EntityType = null!;
        EntityId = null!;
        PayloadJson = null!;
        PreviousHash = null!;
        EntryHash = null!;
    }

    public long EntryId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string Actor { get; private set; }

    public string Action { get; private set; }

    public string EntityType { get; private set; }

    public string EntityId { get; private set; }

    public Guid CorrelationId { get; private set; }

    public string PayloadJson { get; private set; }

    public string PreviousHash { get; private set; }

    public string EntryHash { get; private set; }

    public static AuditEntryRecord FromDomain(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new AuditEntryRecord
        {
            EntryId = entry.EntryId,
            OccurredAt = entry.OccurredAt,
            Actor = entry.Actor,
            Action = entry.Action,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            CorrelationId = entry.CorrelationId,
            PayloadJson = entry.PayloadJson,
            PreviousHash = entry.PreviousHash,
            EntryHash = entry.EntryHash,
        };
    }

    /// <summary>
    /// Rebuilds the entry <b>without recomputing its hash</b>, which is what makes verification
    /// meaningful: a tampered row must come back exactly as stored so the verifier can catch the
    /// disagreement between its contents and its hash.
    /// </summary>
    public AuditEntry ToDomain() => AuditEntry.FromPersistedState(
        EntryId, OccurredAt, Actor, Action, EntityType, EntityId, CorrelationId, PayloadJson, PreviousHash, EntryHash);
}
