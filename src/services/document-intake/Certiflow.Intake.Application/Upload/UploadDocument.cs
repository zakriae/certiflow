using Certiflow.Intake.Application.Abstractions;
using Certiflow.Intake.Domain;
using Certiflow.SharedKernel;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Certiflow.Intake.Application.Upload;

/// <summary>
/// A supplier submitting a file against a requirement (FR-2.1).
/// <para>
/// The stream is passed rather than a byte array: a 20 MB cap times a few concurrent uploads is
/// enough to matter, and nothing here needs the whole file resident at once.
/// </para>
/// </summary>
public sealed record UploadDocumentCommand(
    Guid SupplierId,
    Guid RequirementId,
    string ExpectedDocumentType,
    string FileName,
    string ContentType,
    Stream Content,
    string UploadedBy,
    Guid? SupersedesDocumentId = null) : IRequest<UploadDocumentResult>;

/// <summary>
/// The outcome. <paramref name="DuplicateOfDocumentId"/> is set when the upload was rejected as a
/// byte-identical resubmission — not an error, just nothing new to do.
/// </summary>
public sealed record UploadDocumentResult(
    Guid DocumentId,
    string Status,
    Guid? DuplicateOfDocumentId = null);

public sealed class UploadDocumentValidator : AbstractValidator<UploadDocumentCommand>
{
    public UploadDocumentValidator()
    {
        RuleFor(c => c.SupplierId).NotEmpty();
        RuleFor(c => c.RequirementId).NotEmpty();
        RuleFor(c => c.ExpectedDocumentType).NotEmpty().MaximumLength(100);
        RuleFor(c => c.FileName).NotEmpty().MaximumLength(260);
        RuleFor(c => c.ContentType).NotEmpty();
        RuleFor(c => c.UploadedBy).NotEmpty();
        RuleFor(c => c.Content).NotNull();

        // Which content types are allowed, and how big a file may be, are domain rules enforced by
        // Document.Accept. Repeating them here would mean two places to change and one to forget.
    }
}

public sealed class UploadDocumentHandler(
    IDocumentRepository repository,
    IDocumentBlobStore blobStore,
    IDocumentInspector inspector,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<UploadDocumentHandler> logger) : IRequestHandler<UploadDocumentCommand, UploadDocumentResult>
{
    public async Task<UploadDocumentResult> Handle(
        UploadDocumentCommand command,
        CancellationToken cancellationToken)
    {
        var supplierId = new SupplierId(command.SupplierId);
        var requirementId = new RequirementId(command.RequirementId);

        var inspection = await inspector.InspectAsync(command.Content, command.ContentType, cancellationToken);

        // Duplicate check before the upload, so a resubmission costs a hash rather than a round
        // trip to storage.
        var duplicate = await repository.FindDuplicateAsync(
            supplierId, requirementId, inspection.ContentHash, cancellationToken);

        if (duplicate is not null)
        {
            UploadLog.DuplicateRejected(logger, command.FileName, duplicate.Id.Value);

            return new UploadDocumentResult(
                duplicate.Id.Value,
                nameof(DocumentStatus.Accepted),
                DuplicateOfDocumentId: duplicate.Id.Value);
        }

        // Blob first, then the row.
        //
        // The failure modes are not symmetrical. If the database write fails after this, an orphan
        // blob is left behind: cheap, invisible to users, and reclaimable by a sweep. The reverse -
        // a document row whose bytes were never stored - is a broken record that the extraction
        // pipeline will keep retrying and a reviewer will eventually be asked to open. Given one of
        // the two must happen first, it should be this one.
        var blobPath = $"{command.SupplierId:D}/{command.RequirementId:D}/{Guid.CreateVersion7():N}-{command.FileName}";

        command.Content.Position = 0;

        var storageReference = await blobStore.StoreAsync(
            command.Content, blobPath, command.ContentType, cancellationToken);

        // Every remaining rule - allowed content type, size cap, page cap - is enforced here, in
        // the aggregate, and throws rather than returning a flag.
        var document = Document.Accept(
            supplierId,
            requirementId,
            command.ExpectedDocumentType,
            command.FileName,
            command.ContentType,
            inspection.SizeBytes,
            inspection.ContentHash,
            storageReference,
            inspection.PageCount,
            command.UploadedBy,
            clock.UtcNow,
            command.SupersedesDocumentId is { } supersedes ? new DocumentId(supersedes) : null);

        await repository.AddAsync(document, cancellationToken);

        // One save commits the document row and the outbox row together. That is what makes
        // DocumentStored impossible to lose and impossible to publish for a write that rolled
        // back (SRS §5.3, §19 Q6).
        await unitOfWork.SaveChangesAsync(cancellationToken);

        UploadLog.Accepted(
            logger, document.Id.Value, command.FileName, inspection.SizeBytes, inspection.PageCount ?? 0);

        return new UploadDocumentResult(document.Id.Value, document.Status.ToString());
    }
}

internal static partial class UploadLog
{
    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Information,
        Message = "Accepted {FileName} as {DocumentId} ({SizeBytes} bytes, {PageCount} page(s))")]
    public static partial void Accepted(
        ILogger logger, Guid documentId, string fileName, long sizeBytes, int pageCount);

    [LoggerMessage(
        EventId = 2202,
        Level = LogLevel.Information,
        Message = "Rejected {FileName} as byte-identical to existing document {DocumentId}")]
    public static partial void DuplicateRejected(ILogger logger, string fileName, Guid documentId);
}
