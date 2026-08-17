namespace Certiflow.Compliance.Domain;

/// <summary>
/// Strongly-typed ids. A method taking <c>(SupplierId, RequirementId)</c> cannot be called with
/// the arguments swapped, which a method taking <c>(Guid, Guid)</c> silently accepts. Mapped to
/// <c>uniqueidentifier</c> by an EF value converter, so this costs nothing at the database.
/// </summary>
public readonly record struct SupplierId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct RequirementId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct DocumentId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
