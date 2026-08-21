namespace Certiflow.Reporting.Domain;

public readonly record struct ReportId(Guid Value)
{
    public static ReportId New() => new(Guid.CreateVersion7());

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

public readonly record struct DocumentId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
