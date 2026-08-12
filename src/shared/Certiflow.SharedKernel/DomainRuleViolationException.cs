namespace Certiflow.SharedKernel;

/// <summary>
/// Thrown when an operation would put an aggregate into a state the domain forbids.
/// <para>
/// This is distinct from input validation. FluentValidation at the Application boundary rejects
/// a <em>malformed request</em> and produces a 400; this exception means the request was
/// well-formed but <em>illegal</em>, and the Api layer maps it to a 409 Conflict with Problem
/// Details (SRS §5.3). Both layers exist deliberately — see the tech-stack doc §3.
/// </para>
/// <para>
/// <see cref="Rule"/> carries a stable machine-readable code so the API contract, the UI message
/// and the audit entry can all name the same broken rule.
/// </para>
/// </summary>
public sealed class DomainRuleViolationException : Exception
{
    public DomainRuleViolationException(string rule, string message) : base(message) => Rule = rule;

    public string Rule { get; }
}
