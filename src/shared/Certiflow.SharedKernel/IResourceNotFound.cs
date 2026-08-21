namespace Certiflow.SharedKernel;

/// <summary>
/// Marks an exception that means "the thing you named does not exist", so the HTTP layer can
/// answer 404 without knowing about any particular context's exception types.
/// <para>
/// A marker interface rather than a shared base class, deliberately. These exceptions live in
/// Application layers that must not share a type hierarchy across bounded contexts (ADR-0004) —
/// and several of them carry context-specific data or already derive from something. An interface
/// costs nothing and imposes no inheritance.
/// </para>
/// <para>
/// Why it exists at all: without it these surfaced as 500. A caller asking for a review task that
/// does not exist was told the system had broken, which is both wrong and the kind of thing that
/// gets ignored in a log full of real failures.
/// </para>
/// </summary>
public interface IResourceNotFound
{
}
