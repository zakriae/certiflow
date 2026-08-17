using Certiflow.SharedKernel;

namespace Certiflow.Audit.Domain;

/// <summary>How a chain failed, if it did.</summary>
public enum ChainBreakKind
{
    None = 0,

    /// <summary>An entry's contents no longer hash to its stored hash — the row was edited.</summary>
    ContentAltered = 1,

    /// <summary>An entry's <c>PreviousHash</c> does not match its predecessor — a row was removed or inserted.</summary>
    LinkBroken = 2,

    /// <summary>Entry ids are not consecutive — a row was deleted.</summary>
    SequenceGap = 3,

    /// <summary>The first entry does not declare itself the start of the chain.</summary>
    MissingGenesis = 4,
}

/// <summary>
/// The result of verifying a chain (FR-8.3).
/// <para>
/// Reports the <em>first</em> break rather than a list, deliberately. Once one entry is invalid every
/// entry after it is invalid too, so a list of five hundred failures tells an auditor nothing that
/// "entry 412 was altered" does not tell them better.
/// </para>
/// </summary>
public sealed record ChainVerificationResult(
    bool IsValid,
    int EntriesVerified,
    long? FirstBrokenEntryId,
    ChainBreakKind BreakKind,
    string? Detail)
{
    public static ChainVerificationResult Valid(int entriesVerified) =>
        new(true, entriesVerified, null, ChainBreakKind.None, null);

    public static ChainVerificationResult Broken(
        int entriesVerified,
        long entryId,
        ChainBreakKind kind,
        string detail) =>
        new(false, entriesVerified, entryId, kind, detail);
}

/// <summary>
/// Recomputes a chain and reports the first break (FR-8.3, SRS §19 Q8).
/// <para>
/// This is what backs the tamper test in the walkthrough: edit one row directly in SQL, run this,
/// and it names the row. Ten seconds, and an abstract guarantee becomes something the viewer watched
/// happen (SRS §11.3).
/// </para>
/// <para>
/// Pure over a sequence of entries, so it can verify a slice from the database, a paged range, or a
/// list built in a unit test. The caller supplies the entries in ascending id order.
/// </para>
/// </summary>
public static class AuditChainVerifier
{
    public static ChainVerificationResult Verify(IEnumerable<AuditEntry> entries)
    {
        Guard.AgainstNull(entries, "audit.verify.entries_required");

        AuditEntry? previous = null;
        var verified = 0;

        foreach (var entry in entries)
        {
            // 1. Does the entry still hash to what it claims? This is the check that catches an
            //    UPDATE to any column.
            var recomputed = entry.RecomputeHash();

            if (!string.Equals(recomputed, entry.EntryHash, StringComparison.Ordinal))
            {
                return ChainVerificationResult.Broken(
                    verified,
                    entry.EntryId,
                    ChainBreakKind.ContentAltered,
                    $"Entry {entry.EntryId} does not hash to its stored value: expected {entry.EntryHash}, recomputed {recomputed}.");
            }

            if (previous is null)
            {
                // 2. The first entry of a full chain must declare itself the genesis. Verifying a
                //    mid-chain slice is legitimate, so this only applies when the slice starts at 1.
                if (entry.EntryId == 1 && !entry.IsGenesis)
                {
                    return ChainVerificationResult.Broken(
                        verified,
                        entry.EntryId,
                        ChainBreakKind.MissingGenesis,
                        $"Entry 1 must reference the genesis hash but references {entry.PreviousHash}.");
                }
            }
            else
            {
                // 3. Consecutive ids. A DELETE leaves a gap that the hashes alone would not reveal,
                //    because the surviving entries still link to each other correctly.
                if (entry.EntryId != previous.EntryId + 1)
                {
                    return ChainVerificationResult.Broken(
                        verified,
                        entry.EntryId,
                        ChainBreakKind.SequenceGap,
                        $"Entry {entry.EntryId} follows {previous.EntryId}; the ledger is missing {previous.EntryId + 1}.");
                }

                // 4. The link itself.
                if (!string.Equals(entry.PreviousHash, previous.EntryHash, StringComparison.Ordinal))
                {
                    return ChainVerificationResult.Broken(
                        verified,
                        entry.EntryId,
                        ChainBreakKind.LinkBroken,
                        $"Entry {entry.EntryId} references predecessor hash {entry.PreviousHash}, but entry {previous.EntryId} hashes to {previous.EntryHash}.");
                }
            }

            previous = entry;
            verified++;
        }

        return ChainVerificationResult.Valid(verified);
    }
}
