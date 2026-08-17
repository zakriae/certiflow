using Certiflow.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Certiflow.Audit.Domain.Tests;

/// <summary>
/// SRS §19 Q8 — "how do you know the audit trail wasn't edited?". Each test here is one way of
/// editing it, and the assertion is that the verifier names the row.
/// </summary>
public sealed class AuditChainTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid Correlation = Guid.Parse("7f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f");

    /// <summary>A short, valid ledger of the kind the walkthrough runs the tamper test against.</summary>
    private static List<AuditEntry> Ledger(int count = 5)
    {
        var entries = new List<AuditEntry>();
        AuditEntry? previous = null;

        for (var i = 1; i <= count; i++)
        {
            previous = AuditEntry.Append(
                previous,
                Start.AddSeconds(i),
                actor: i % 2 == 0 ? "reviewer@certiflow.demo" : "system",
                action: "DocumentApproved",
                entityType: "Document",
                entityId: $"doc-{i:D4}",
                Correlation,
                payloadJson: $"{{\"documentId\":\"doc-{i:D4}\",\"sequence\":{i}}}");

            entries.Add(previous);
        }

        return entries;
    }

    /// <summary>
    /// Simulates what an UPDATE against the table does: the stored hash stays, the contents change.
    /// </summary>
    private static AuditEntry Tampered(AuditEntry original, string newPayload) =>
        AuditEntry.FromPersistedState(
            original.EntryId,
            original.OccurredAt,
            original.Actor,
            original.Action,
            original.EntityType,
            original.EntityId,
            original.CorrelationId,
            newPayload,
            original.PreviousHash,
            original.EntryHash);

    [Fact]
    public void The_first_entry_starts_the_chain_at_the_genesis_hash()
    {
        var first = Ledger(1).Single();

        first.EntryId.Should().Be(1);
        first.PreviousHash.Should().Be(AuditEntry.GenesisHash);
        first.IsGenesis.Should().BeTrue();
    }

    [Fact]
    public void Each_entry_links_to_its_predecessor()
    {
        var ledger = Ledger();

        for (var i = 1; i < ledger.Count; i++)
        {
            ledger[i].PreviousHash.Should().Be(ledger[i - 1].EntryHash);
            ledger[i].EntryId.Should().Be(ledger[i - 1].EntryId + 1);
        }
    }

    [Fact]
    public void An_untouched_ledger_verifies()
    {
        var result = AuditChainVerifier.Verify(Ledger(20));

        result.IsValid.Should().BeTrue();
        result.EntriesVerified.Should().Be(20);
        result.FirstBrokenEntryId.Should().BeNull();
        result.BreakKind.Should().Be(ChainBreakKind.None);
    }

    [Fact]
    public void An_empty_ledger_verifies_vacuously()
    {
        AuditChainVerifier.Verify([]).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Editing_one_row_is_detected_and_the_row_is_named()
    {
        // The tamper test of SRS §11.3, in a unit test. Somebody changed who approved what.
        var ledger = Ledger();
        ledger[2] = Tampered(ledger[2], "{\"documentId\":\"doc-0003\",\"sequence\":3,\"approved\":false}");

        var result = AuditChainVerifier.Verify(ledger);

        result.IsValid.Should().BeFalse();
        result.FirstBrokenEntryId.Should().Be(3);
        result.BreakKind.Should().Be(ChainBreakKind.ContentAltered);
        result.EntriesVerified.Should().Be(2, "the first two entries were fine");
        result.Detail.Should().Contain("does not hash to its stored value");
    }

    [Fact]
    public void Deleting_a_row_is_detected_even_though_the_survivors_still_link_correctly()
    {
        // The subtle case. Removing an entry leaves the remaining hashes internally consistent with
        // each other, so only the id sequence gives it away — which is why EntryId is in the hash.
        var ledger = Ledger();
        ledger.RemoveAt(2);

        var result = AuditChainVerifier.Verify(ledger);

        result.IsValid.Should().BeFalse();
        result.BreakKind.Should().Be(ChainBreakKind.SequenceGap);
        result.Detail.Should().Contain("missing 3");
    }

    [Fact]
    public void A_forged_entry_spliced_into_the_middle_breaks_the_link()
    {
        var ledger = Ledger();

        // An entry with the right id, built on the wrong predecessor.
        ledger[2] = AuditEntry.Append(
            ledger[1],
            Start.AddSeconds(3),
            actor: "attacker",
            action: "DocumentApproved",
            entityType: "Document",
            entityId: "doc-0003",
            Correlation,
            payloadJson: "{\"documentId\":\"doc-0003\",\"sequence\":3,\"forged\":true}");

        // Entry 3 itself hashes correctly, so the break surfaces at entry 4, whose PreviousHash
        // still refers to the entry that was replaced.
        var result = AuditChainVerifier.Verify(ledger);

        result.IsValid.Should().BeFalse();
        result.FirstBrokenEntryId.Should().Be(4);
        result.BreakKind.Should().Be(ChainBreakKind.LinkBroken);
    }

    [Fact]
    public void Rewriting_the_first_entry_to_hide_the_start_of_the_chain_is_detected()
    {
        var ledger = Ledger();
        var forgedFirst = AuditEntry.FromPersistedState(
            1,
            ledger[0].OccurredAt,
            ledger[0].Actor,
            ledger[0].Action,
            ledger[0].EntityType,
            ledger[0].EntityId,
            ledger[0].CorrelationId,
            ledger[0].PayloadJson,
            previousHash: new string('a', 64),
            entryHash: ledger[0].EntryHash);

        ledger[0] = forgedFirst;

        var result = AuditChainVerifier.Verify(ledger);

        // Changing PreviousHash also changes what the entry should hash to, so this is caught as an
        // alteration before the genesis rule is even reached. Both checks matter; either suffices.
        result.IsValid.Should().BeFalse();
        result.FirstBrokenEntryId.Should().Be(1);
    }

    [Fact]
    public void Changing_a_single_character_of_the_payload_changes_the_hash()
    {
        var a = AuditEntry.Append(null, Start, "system", "DocumentStored", "Document", "doc-1", Correlation, "{\"a\":1}");
        var b = AuditEntry.Append(null, Start, "system", "DocumentStored", "Document", "doc-1", Correlation, "{\"a\":2}");

        a.EntryHash.Should().NotBe(b.EntryHash);
    }

    [Fact]
    public void Field_boundaries_cannot_be_shifted_to_forge_a_matching_hash()
    {
        // Without length prefixes in the canonical form, actor "a" + action "b" would hash the same
        // as actor "a<sep>b" + action "" — letting somebody rewrite a record and keep its hash.
        var a = AuditEntry.Append(null, Start, "alice", "Approved", "Document", "doc-1", Correlation, "{}");
        var b = AuditEntry.Append(null, Start, "alic", "eApproved", "Document", "doc-1", Correlation, "{}");

        a.EntryHash.Should().NotBe(b.EntryHash);
    }

    [Fact]
    public void The_same_entry_hashes_identically_however_its_timestamp_is_expressed()
    {
        // A chain must verify the same in any time zone, or verification depends on the reader.
        var utc = AuditEntry.Append(null, Start, "system", "DocumentStored", "Document", "doc-1", Correlation, "{}");
        var offset = AuditEntry.Append(
            null,
            Start.ToOffset(TimeSpan.FromHours(2)),
            "system",
            "DocumentStored",
            "Document",
            "doc-1",
            Correlation,
            "{}");

        utc.EntryHash.Should().Be(offset.EntryHash);
    }

    [Fact]
    public void A_hash_is_a_lowercase_hex_sha256()
    {
        var entry = Ledger(1).Single();

        entry.EntryHash.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void An_entry_without_an_actor_is_refused(string? actor)
    {
        // "system" is an actor. Blank is not — an unattributable audit entry is not an audit entry.
        var act = () => AuditEntry.Append(
            null, Start, actor!, "DocumentStored", "Document", "doc-1", Correlation, "{}");

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("audit.entry.actor_required");
    }

    [Fact]
    public void An_entry_without_a_payload_is_refused()
    {
        var act = () => AuditEntry.Append(
            null, Start, "system", "DocumentStored", "Document", "doc-1", Correlation, "   ");

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("audit.entry.payload_required");
    }
}
