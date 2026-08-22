namespace Certiflow.Notifications.Domain;

public readonly record struct NotificationId(Guid Value)
{
    public static NotificationId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct SupplierId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct DocumentId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
