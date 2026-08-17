using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Certiflow.SharedKernel;

namespace Certiflow.Audit.Domain;

/// <summary>
/// One immutable, hash-chained record of a state-changing action (SRS §3, §11.3).
/// <para>
/// <b>Append-only in the strongest sense available in code:</b> every property is get-only, there is
/// no mutating method anywhere on this type, and the only way to produce one is <see cref="Append"/>,
/// which requires the entry it follows. No code path in this assembly can change an entry after it
/// exists — a stronger claim than "we don't do that", and the one an auditor is actually asking about.
/// </para>
/// <para>
/// Modelled as an entity rather than an aggregate root even though SRS §11.3 calls it an aggregate:
/// it raises no domain events, and giving it an events collection that is always empty would suggest
/// it might not be. The audit trail is the end of the line — everything publishes to it and it
/// publishes to nothing (SRS §4.3, Conformist).
/// </para>
/// <para>
/// The chain makes tampering <em>detectable</em>, not <em>impossible</em>: each entry hashes its own
/// contents together with its predecessor's hash, so editing any row invalidates every hash after it.
/// Someone with write access to the database can still change a row — they just cannot do it without
/// <see cref="AuditChainVerifier"/> saying exactly which one (SRS §19 Q8).
/// </para>
/// </summary>
public sealed class AuditEntry : Entity<long>
{
    /// <summary>
    /// The predecessor hash of the first entry. A fixed, recognisable value rather than an empty
    /// string, so "this is the genesis entry" and "the previous hash is missing" cannot be confused —
    /// the second is a broken chain and must not verify as the first.
    /// </summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// U+001F UNIT SEPARATOR — a control character that cannot occur in JSON string content or in any
    /// identifier this system produces, so it cannot be smuggled into a field to forge a hash.
    /// <para>
    /// Written numerically because a bare control character in source does not survive every editor,
    /// diff tool or copy-paste — and this constant feeds every hash in the ledger, so it must never
    /// change by accident. It is a value the ledger's entire history depends on.
    /// </para>
    /// </summary>
    private const char FieldSeparator = (char)0x1F;

    private AuditEntry(
        long entryId,
        DateTimeOffset occurredAt,
        string actor,
        string action,
        string entityType,
        string entityId,
        Guid correlationId,
        string payloadJson,
        string previousHash,
        string entryHash)
        : base(entryId)
    {
        OccurredAt = occurredAt;
        Actor = actor;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        CorrelationId = correlationId;
        PayloadJson = payloadJson;
        PreviousHash = previousHash;
        EntryHash = entryHash;
    }

    /// <summary>Sequential. Part of the hash, so an entry cannot be silently reordered or removed.</summary>
    public long EntryId => Id;

    public DateTimeOffset OccurredAt { get; }

    /// <summary>The user or component responsible. Never null — "system" is an actor.</summary>
    public string Actor { get; }

    public string Action { get; }

    public string EntityType { get; }

    public string EntityId { get; }

    /// <summary>Ties this entry to every other entry produced by the same user action (NFR-13).</summary>
    public Guid CorrelationId { get; }

    /// <summary>
    /// The published integration event, verbatim. The audit trail is a conformist: it records what
    /// the publishers emitted and never asks them to change shape (SRS §4.3).
    /// </summary>
    public string PayloadJson { get; }

    public string PreviousHash { get; }

    public string EntryHash { get; }

    public bool IsGenesis => PreviousHash == GenesisHash;

    /// <summary>
    /// Appends a new entry after <paramref name="previous"/>, or starts the chain when it is null.
    /// <para>
    /// Concurrency note: <see cref="EntryId"/> is derived from the predecessor, so appends must be
    /// serialised. That is naturally true here — BC8 consumes its subscription with a single
    /// concurrent handler — and it is backstopped by a unique index on <c>EntryId</c>, so a second
    /// writer loses on insert rather than forking the chain.
    /// </para>
    /// </summary>
    public static AuditEntry Append(
        AuditEntry? previous,
        DateTimeOffset occurredAt,
        string actor,
        string action,
        string entityType,
        string entityId,
        Guid correlationId,
        string payloadJson)
    {
        var entryId = (previous?.EntryId ?? 0) + 1;
        var previousHash = previous?.EntryHash ?? GenesisHash;

        var safeActor = Guard.AgainstNullOrWhiteSpace(actor, "audit.entry.actor_required");
        var safeAction = Guard.AgainstNullOrWhiteSpace(action, "audit.entry.action_required");
        var safeEntityType = Guard.AgainstNullOrWhiteSpace(entityType, "audit.entry.entity_type_required");
        var safeEntityId = Guard.AgainstNullOrWhiteSpace(entityId, "audit.entry.entity_id_required");
        var safePayload = Guard.AgainstNullOrWhiteSpace(payloadJson, "audit.entry.payload_required");

        var entryHash = ComputeHash(
            entryId,
            occurredAt,
            safeActor,
            safeAction,
            safeEntityType,
            safeEntityId,
            correlationId,
            safePayload,
            previousHash);

        return new AuditEntry(
            entryId,
            occurredAt,
            safeActor,
            safeAction,
            safeEntityType,
            safeEntityId,
            correlationId,
            safePayload,
            previousHash,
            entryHash);
    }

    /// <summary>
    /// Rebuilds an entry from stored columns <b>without recomputing its hash</b>.
    /// <para>
    /// Used by EF materialisation, and by the tamper test of SRS §11.3 — which has to produce an entry
    /// whose stored hash disagrees with its contents, because that is precisely what a tampered row
    /// is. Being able to construct one is part of the verifier's test surface, not a hole: nothing
    /// here can alter an entry that already exists.
    /// </para>
    /// </summary>
    public static AuditEntry FromPersistedState(
        long entryId,
        DateTimeOffset occurredAt,
        string actor,
        string action,
        string entityType,
        string entityId,
        Guid correlationId,
        string payloadJson,
        string previousHash,
        string entryHash) =>
        new(
            entryId,
            occurredAt,
            actor,
            action,
            entityType,
            entityId,
            correlationId,
            payloadJson,
            previousHash,
            entryHash);

    /// <summary>Recomputes this entry's hash from its own contents, for verification.</summary>
    public string RecomputeHash() => ComputeHash(
        EntryId, OccurredAt, Actor, Action, EntityType, EntityId, CorrelationId, PayloadJson, PreviousHash);

    /// <summary>
    /// SHA-256 over a length-prefixed canonical form.
    /// <para>
    /// The length prefixes matter. Plain concatenation with a separator lets two different entries
    /// hash identically — actor <c>"a|b"</c> with action <c>"c"</c> produces the same bytes as actor
    /// <c>"a"</c> with action <c>"b|c"</c> — so a determined editor could rewrite a record and keep
    /// its hash valid. Prefixing each field with its length removes that freedom.
    /// </para>
    /// </summary>
    private static string ComputeHash(
        long entryId,
        DateTimeOffset occurredAt,
        string actor,
        string action,
        string entityType,
        string entityId,
        Guid correlationId,
        string payloadJson,
        string previousHash)
    {
        var canonical = new StringBuilder();

        AppendField(canonical, entryId.ToString(CultureInfo.InvariantCulture));

        // Normalised to UTC and round-trip formatted, so a chain verifies identically whatever the
        // time zone of the machine reading it.
        AppendField(canonical, occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        AppendField(canonical, actor);
        AppendField(canonical, action);
        AppendField(canonical, entityType);
        AppendField(canonical, entityId);
        AppendField(canonical, correlationId.ToString("D", CultureInfo.InvariantCulture));
        AppendField(canonical, payloadJson);
        AppendField(canonical, previousHash);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));

        return Convert.ToHexStringLower(digest);

        static void AppendField(StringBuilder builder, string value) =>
            builder
                .Append(value.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(value)
                .Append(FieldSeparator);
    }
}
