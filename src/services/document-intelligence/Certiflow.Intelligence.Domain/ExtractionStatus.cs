namespace Certiflow.Intelligence.Domain;

/// <summary>
/// Where an extraction job is in the pipeline of SRS §8.2.
/// <para>
/// The intermediate stages exist for a reason beyond bookkeeping: they are pushed to the browser
/// over SignalR so the reviewer watches <c>Parsing → Extracting → Grounding</c> rather than a
/// spinner (tech-stack doc §9). A twenty-second wait with visible progress reads as work; the
/// same wait behind a spinner reads as broken.
/// </para>
/// </summary>
public enum ExtractionStatus
{
    /// <summary>Queued, no attempt started.</summary>
    Pending = 0,

    /// <summary>Pulling text and a page map out of the file.</summary>
    Parsing = 1,

    /// <summary>The model is producing structured output against the field schema.</summary>
    Extracting = 2,

    /// <summary>Every citation is being located in the source text.</summary>
    Grounding = 3,

    /// <summary>Terminal success. Immutable from here.</summary>
    Completed = 4,

    /// <summary>An attempt failed; a retry is still available.</summary>
    Failed = 5,

    /// <summary>Terminal failure — attempts exhausted (FR-3.7). Nothing is silently lost.</summary>
    Abandoned = 6,
}

public static class ExtractionStatusExtensions
{
    /// <summary>Completed and Abandoned are terminal; a Failed job can still be retried.</summary>
    public static bool IsTerminal(this ExtractionStatus status) =>
        status is ExtractionStatus.Completed or ExtractionStatus.Abandoned;
}
