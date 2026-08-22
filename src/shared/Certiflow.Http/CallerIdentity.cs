using System.Security.Claims;

namespace Certiflow.Http;

/// <summary>
/// Who the caller is, according to their token.
/// <para>
/// <b>Not according to the request body.</b> Every write endpoint used to take the actor as a
/// field — <c>reviewerId</c>, <c>uploadedBy</c>, <c>requestedBy</c> — because there was no
/// authentication and something had to fill the gap. Once tokens existed, leaving it that way meant
/// a caller could name themselves, and that turns two guarantees into suggestions:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Segregation of duties.</b> The rule refuses an approval by the person who uploaded the
///     document. It compares two strings the caller supplied, so a supplier approving their own
///     upload only had to type a different name.
///   </item>
///   <item>
///     <b>The audit trail.</b> The ledger's whole claim is that it records who did what. An actor
///     the caller chose is a field, not a fact, and hash-chaining a lie preserves it perfectly.
///   </item>
/// </list>
/// <para>
/// The email claim is used rather than the subject id because it is what the domain compares and
/// what a human reads in the ledger. When Entra External ID replaces the seeded issuer the claim is
/// the same one, so nothing here changes.
/// </para>
/// </summary>
public static class CallerIdentity
{
    public static string EmailOf(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var email = principal.FindFirstValue(Claims.Email);

        if (!string.IsNullOrWhiteSpace(email))
        {
            return email;
        }

        // Falls back to the subject rather than to a placeholder. An entry attributed to
        // "unknown" is worse than one attributed to an opaque id: the id identifies somebody.
        var subject = principal.FindFirstValue(Claims.Subject);

        return string.IsNullOrWhiteSpace(subject)
            ? throw new InvalidOperationException("The caller's token carries neither an email nor a subject claim.")
            : subject;
    }

    /// <summary>
    /// The supplier a token is scoped to, or null for staff roles. NFR-8's tenant guard reads this;
    /// it is on the token precisely so a service never has to trust a query parameter for it.
    /// </summary>
    public static Guid? SupplierIdOf(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var value = principal.FindFirstValue(Claims.SupplierId);

        return Guid.TryParse(value, out var supplierId) ? supplierId : null;
    }
}
