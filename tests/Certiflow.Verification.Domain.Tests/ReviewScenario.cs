namespace Certiflow.Verification.Domain.Tests;

internal static class ReviewScenario
{
    public const string Supplier = "contact@meridian-logistics.demo";

    public const string Reviewer = "reviewer@certiflow.demo";

    public static readonly DateOnly Today = new(2026, 8, 18);

    public static readonly DateTimeOffset Now = new(2026, 8, 18, 9, 30, 0, TimeSpan.Zero);

    /// <summary>
    /// The fields of an ISO 9001 extraction where the expiry date scored badly — the exact case the
    /// review screen exists for.
    /// </summary>
    public static IReadOnlyList<FieldSuggestion> Suggestions(decimal expiryConfidence = 0.62m) =>
    [
        new("holderName", "Meridian Logistics SARL", 1.00m, true, 1, "Meridian Logistics SARL", null),
        new("issuerName", "AFNOR Certification", 1.00m, true, 2, "Issued by: AFNOR Certification", null),
        new("certificateNumber", "FR-9001-00417", 1.00m, true, 2, "Certificate Number: FR-9001-00417", null),
        new("issuedOn", "2025-03-14", 1.00m, true, 2, "Date of Issue: 2025-03-14", null),
        new("expiresOn", "2027-03-13", expiryConfidence, true, 2, "Expiry Date: 2027-03-13",
            "Cited text was found on page 3 rather than page 2."),
        new("scope", "Road freight transport", 1.00m, false, 2, "Scope: Road freight transport", null),
    ];

    public static ReviewTask Open(
        string uploadedBy = Supplier,
        RaisedReason reason = RaisedReason.LowConfidence,
        DateOnly? currentEvidenceExpiresOn = null,
        IReadOnlyList<FieldSuggestion>? suggestions = null) =>
        ReviewTask.RaiseFor(
            new DocumentId(Guid.CreateVersion7()),
            new ExtractionJobId(Guid.CreateVersion7()),
            new SupplierId(Guid.CreateVersion7()),
            new RequirementId(Guid.CreateVersion7()),
            documentType: "ISO 9001",
            uploadedBy: uploadedBy,
            reason: reason,
            overallConfidence: 0.62m,
            suggestions: suggestions ?? Suggestions(),
            today: Today,
            currentEvidenceExpiresOn: currentEvidenceExpiresOn);

    /// <summary>Resolves every mandatory field by accepting what the pipeline suggested.</summary>
    public static ReviewTask WithAllMandatoryFieldsResolved(this ReviewTask task, string reviewerId = Reviewer)
    {
        foreach (var field in task.FieldReviews.Where(f => f.IsMandatory).ToList())
        {
            task.ResolveField(field.FieldName, field.SuggestedValue, reviewerId, Now);
        }

        return task;
    }
}
