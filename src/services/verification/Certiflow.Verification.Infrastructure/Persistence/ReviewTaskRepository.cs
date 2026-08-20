using Certiflow.Verification.Application.Abstractions;
using Certiflow.Verification.Domain;
using Microsoft.EntityFrameworkCore;

namespace Certiflow.Verification.Infrastructure.Persistence;

public sealed class ReviewTaskRepository(VerificationDbContext context) : IReviewTaskRepository
{
    public async Task<ReviewTask?> FindAsync(ReviewTaskId reviewTaskId, CancellationToken cancellationToken) =>
        await context.ReviewTasks.FirstOrDefaultAsync(t => t.Id == reviewTaskId, cancellationToken);

    public async Task<ReviewTask?> FindOpenForDocumentAsync(DocumentId documentId, CancellationToken cancellationToken) =>
        await context.ReviewTasks.FirstOrDefaultAsync(
            t => t.DocumentId == documentId
              && (t.Status == ReviewTaskStatus.Open || t.Status == ReviewTaskStatus.InProgress),
            cancellationToken);

    public async Task AddAsync(ReviewTask task, CancellationToken cancellationToken) =>
        await context.ReviewTasks.AddAsync(task, cancellationToken);
}
