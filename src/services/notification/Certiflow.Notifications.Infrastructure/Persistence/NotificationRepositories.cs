using Certiflow.Notifications.Application.Abstractions;
using Certiflow.Notifications.Domain;
using Microsoft.EntityFrameworkCore;

namespace Certiflow.Notifications.Infrastructure.Persistence;

public sealed class NotificationRepository(NotificationsDbContext context) : INotificationRepository
{
    public void Add(Notification notification) => context.Notifications.Add(notification);

    public async Task<Notification?> FindAsync(NotificationId id, CancellationToken cancellationToken) =>
        await context.Notifications.FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

    public async Task<bool> SaveIfNewAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsDuplicateKey(exception))
        {
            // The expected outcome for every nightly sweep after the first, not an error. The
            // entries are detached so the context is usable afterwards - leaving a failed insert
            // tracked makes the next SaveChanges retry it and fail again.
            foreach (var entry in context.ChangeTracker.Entries<Notification>().ToList())
            {
                entry.State = EntityState.Detached;
            }

            return false;
        }
    }

    /// <summary>
    /// 2601 and 2627 are SQL Server's unique-index and unique-constraint violations. Matched on the
    /// number rather than the message, which is localised.
    /// </summary>
    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };
}

public sealed class SupplierContactDirectory(NotificationsDbContext context) : ISupplierContactDirectory
{
    public async Task<SupplierContact?> FindAsync(SupplierId supplierId, CancellationToken cancellationToken)
    {
        var record = await context.Contacts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.SupplierId == supplierId.Value, cancellationToken);

        return record?.ToContact();
    }

    public async Task UpsertAsync(SupplierContact contact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contact);

        var existing = await context.Contacts
            .FirstOrDefaultAsync(c => c.SupplierId == contact.SupplierId.Value, cancellationToken);

        if (existing is null)
        {
            context.Contacts.Add(SupplierContactRecord.From(contact));
        }
        else
        {
            existing.Update(contact);
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
