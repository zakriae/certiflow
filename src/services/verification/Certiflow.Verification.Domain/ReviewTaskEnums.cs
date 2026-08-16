namespace Certiflow.Verification.Domain;

public readonly record struct ReviewTaskId(Guid Value)
{
    public static ReviewTaskId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct DocumentId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct ExtractionJobId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct SupplierId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct RequirementId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public enum ReviewTaskStatus
{
    Open = 1,

    /// <summary>Assigned to a reviewer, or partially resolved.</summary>
    InProgress = 2,

    /// <summary>A verdict was reached. Terminal.</summary>
    Completed = 3,

    /// <summary>Superseded or withdrawn before a verdict. Terminal, and never receives one.</summary>
    Cancelled = 4,
}

/// <summary>Why a document could not be accepted without a human (SRS §9.1).</summary>
public enum RaisedReason
{
    /// <summary>At least one mandatory field scored below the requirement's threshold.</summary>
    LowConfidence = 1,

    /// <summary>A citation could not be located in the source — the model invented something.</summary>
    GroundingFailure = 2,

    /// <summary>The extraction is self-contradictory, or contradicts the requirement.</summary>
    RuleConflict = 3,

    /// <summary>An admin sent it for review, or extraction was abandoned (FR-3.7).</summary>
    ManualEscalation = 4,
}

/// <summary>
/// Queue priority (FR-4.8).
/// <para>
/// Derived from how close the supplier's <em>current</em> evidence is to expiring, not from the
/// document being reviewed. That is the whole point: a renewal for a certificate lapsing in three
/// days is urgent, and a renewal submitted eleven months early is not, even though the two
/// documents look identical on the review screen.
/// </para>
/// </summary>
public enum ReviewPriority
{
    Low = 0,
    Normal = 1,
    High = 2,

    /// <summary>Existing evidence has already lapsed — the supplier is non-compliant right now.</summary>
    Critical = 3,
}
