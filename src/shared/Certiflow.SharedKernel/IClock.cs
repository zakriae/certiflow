namespace Certiflow.SharedKernel;

/// <summary>
/// The only source of "now" above the domain layer.
/// <para>
/// Domain methods take the current date as an explicit parameter instead of reading a clock —
/// <c>obligation.Attach(evidence, today)</c> — because expiry and at-risk rules are pure
/// functions of a date and testing them should not require freezing time. This interface exists
/// so the Application layer has one seam to substitute, which is what makes the Expiry Watch
/// (FR-5.4) and the point-in-time query (FR-5.8) testable at all.
/// </para>
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
