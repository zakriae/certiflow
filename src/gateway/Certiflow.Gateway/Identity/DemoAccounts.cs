namespace Certiflow.Gateway.Identity;

/// <summary>
/// The three accounts printed on the login screen (SRS §16.2), plus the auditor the read-only role
/// needs to be demonstrable at all.
/// <para>
/// Hard-coded on purpose. These are not users — they are fixtures for a system whose real identity
/// provider is Entra External ID (§20 R4), and putting them in a database would invite someone to
/// add a fourth by hand and be surprised when it vanished on the next deploy.
/// </para>
/// </summary>
public sealed record DemoAccount(
    Guid SubjectId,
    string Email,
    string DisplayName,
    string Role,
    Guid? SupplierId);

public static class DemoAccounts
{
    /// <summary>
    /// One shared password across every account, printed on the login screen. This is a demo
    /// issuer standing in for Entra; pretending otherwise with per-account passwords would add
    /// ceremony without adding a single bit of security.
    /// </summary>
    public const string SharedPassword = "Certiflow!Demo1";

    /// <summary>
    /// The supplier account is scoped to one supplier, and that scope travels in the token as
    /// <c>supplier_id</c>. NFR-8 requires the tenant guard in the gateway <b>and</b> in each
    /// service, so the claim has to be on the token rather than looked up per request.
    /// </summary>
    public static readonly IReadOnlyList<DemoAccount> All =
    [
        new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), "admin@certiflow.demo", "Amélie Dubois", Roles.Admin, null),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000a2"), "reviewer@certiflow.demo", "Tom Vandenberg", Roles.Reviewer, null),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000a3"), "auditor@certiflow.demo", "Priya Raghavan", Roles.Auditor, null),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000a4"), "supplier@certiflow.demo", "Claire Fontaine", Roles.SupplierUser, null),
    ];

    public static DemoAccount? Find(string email) =>
        All.FirstOrDefault(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The four app roles from SRS §2. The strings are the ones Entra External ID will emit as app
/// roles, so swapping the issuer does not touch a single authorisation policy.
/// </summary>
public static class Roles
{
    public const string Admin = "Admin";

    public const string Reviewer = "Reviewer";

    public const string Auditor = "Auditor";

    public const string SupplierUser = "SupplierUser";

    /// <summary>A calling service rather than a person. See CertiflowRoles.Service.</summary>
    public const string Service = "Service";
}
