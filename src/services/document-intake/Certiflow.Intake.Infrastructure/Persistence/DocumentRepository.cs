using Certiflow.Intake.Application.Abstractions;
using Certiflow.Intake.Domain;
using Microsoft.EntityFrameworkCore;

namespace Certiflow.Intake.Infrastructure.Persistence;

public sealed class DocumentRepository(IntakeDbContext context) : IDocumentRepository
{
    public async Task<Document?> FindAsync(DocumentId documentId, CancellationToken cancellationToken) =>
        await context.Documents.FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);

    public async Task<Document?> FindDuplicateAsync(
        SupplierId supplierId,
        RequirementId requirementId,
        Sha256Hash contentHash,
        CancellationToken cancellationToken)
    {
        // Superseded and quarantined documents are excluded deliberately. Re-uploading a file that
        // was previously rejected, or one that has since been replaced, is a legitimate act - only
        // a document currently standing as evidence makes a resubmission a duplicate (FR-2.4).
        return await context.Documents
            .Where(d => d.SupplierId == supplierId
                     && d.RequirementId == requirementId
                     && d.Status == DocumentStatus.Accepted)
            .FirstOrDefaultAsync(d => d.Sha256!.Value == contentHash.Value, cancellationToken);
    }

    public async Task AddAsync(Document document, CancellationToken cancellationToken) =>
        await context.Documents.AddAsync(document, cancellationToken);
}
