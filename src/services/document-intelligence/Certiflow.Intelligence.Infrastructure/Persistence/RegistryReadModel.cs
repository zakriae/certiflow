namespace Certiflow.Intelligence.Infrastructure.Persistence;

/// <summary>
/// What Document Intelligence knows about a supplier, kept from the registry's events.
/// <para>
/// <b>This is what makes the entity-match check real.</b> Scoring "does this certificate name the
/// supplier it was filed against" needs the supplier's legal name, and that belongs to BC1. Without
/// this table the check has nothing to compare against and scores whatever it is handed — which is
/// exactly the failure the SRS §16.1 mismatch case exists to catch.
/// </para>
/// <para>
/// A copy, not a query. Compliance keeps its own copy of profiles for the same reason: a context
/// that reaches into another's database is not a separate context (SRS §4.3).
/// </para>
/// </summary>
public sealed class SupplierRecord
{
    private SupplierRecord() => LegalName = null!;

    public SupplierRecord(Guid supplierId, string legalName, string? tradingName)
    {
        SupplierId = supplierId;
        LegalName = legalName;
        TradingName = tradingName;
    }

    public Guid SupplierId { get; private set; }

    public string LegalName { get; private set; }

    public string? TradingName { get; private set; }

    public void Update(string legalName, string? tradingName)
    {
        LegalName = legalName;
        TradingName = tradingName;
    }
}

/// <summary>
/// What a requirement demands, from the registry's published profile.
/// <para>
/// Carries the accepted issuers and the auto-accept threshold, so the scorer can check an issuer
/// and apply the right bar per requirement rather than one global constant.
/// </para>
/// </summary>
public sealed class RequirementRecord
{
    /// <summary>
    /// Issuers are joined on U+001F, not a comma. Certification body names contain commas
    /// ("SGS United Kingdom Ltd, Systems and Services"), and a comma-separated list would split one
    /// issuer into two halves that match nothing.
    /// </summary>
    private const char Separator = '\u001F';

    private RequirementRecord()
    {
        DocumentType = null!;
        AcceptedIssuersPacked = null!;
    }

    public RequirementRecord(
        Guid requirementId,
        Guid categoryId,
        string documentType,
        bool requiresIssuerMatch,
        IReadOnlyList<string> acceptedIssuers,
        decimal autoAcceptThreshold)
    {
        RequirementId = requirementId;
        CategoryId = categoryId;
        DocumentType = documentType;
        RequiresIssuerMatch = requiresIssuerMatch;
        AcceptedIssuersPacked = string.Join(Separator, acceptedIssuers);
        AutoAcceptThreshold = autoAcceptThreshold;
    }

    public Guid RequirementId { get; private set; }

    public Guid CategoryId { get; private set; }

    public string DocumentType { get; private set; }

    public bool RequiresIssuerMatch { get; private set; }

    public string AcceptedIssuersPacked { get; private set; }

    public decimal AutoAcceptThreshold { get; private set; }

    public IReadOnlyList<string> AcceptedIssuers =>
        string.IsNullOrEmpty(AcceptedIssuersPacked) ? [] : AcceptedIssuersPacked.Split(Separator);

    public void Update(
        string documentType,
        bool requiresIssuerMatch,
        IReadOnlyList<string> acceptedIssuers,
        decimal autoAcceptThreshold)
    {
        DocumentType = documentType;
        RequiresIssuerMatch = requiresIssuerMatch;
        AcceptedIssuersPacked = string.Join(Separator, acceptedIssuers);
        AutoAcceptThreshold = autoAcceptThreshold;
    }
}
