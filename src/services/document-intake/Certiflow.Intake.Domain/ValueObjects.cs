using System.Globalization;
using Certiflow.SharedKernel;

namespace Certiflow.Intake.Domain;

public readonly record struct DocumentId(Guid Value)
{
    public static DocumentId New() => new(Guid.CreateVersion7());

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

/// <summary>
/// A SHA-256 content hash (FR-2.4).
/// <para>
/// A value object rather than a string so it cannot be confused with the many other 64-character
/// strings in this system — and so it is normalised once, here, instead of at every comparison. The
/// duplicate check compares these, and a case difference silently defeating it would mean the same
/// certificate could be submitted twice.
/// </para>
/// </summary>
public sealed record Sha256Hash
{
    private Sha256Hash(string value) => Value = value;

    public string Value { get; }

    public static Sha256Hash Parse(string value)
    {
        var trimmed = Guard.AgainstNullOrWhiteSpace(value, "intake.sha256.required");

        Guard.Require(
            trimmed.Length == 64,
            "intake.sha256.wrong_length",
            $"A SHA-256 hash is 64 hex characters, but this is {trimmed.Length}.");

        Guard.Require(
            trimmed.All(Uri.IsHexDigit),
            "intake.sha256.not_hex",
            "A SHA-256 hash must contain only hexadecimal characters.");

        return new Sha256Hash(trimmed.ToLowerInvariant());
    }

    public override string ToString() => Value;
}

/// <summary>
/// Where the bytes live: a container and a blob path (SRS §7.1).
/// <para>
/// Deliberately <em>not</em> a URL. A URL implies reachability, and these containers are private —
/// access is only ever a short-lived user-delegation SAS minted at the moment of download
/// (FR-2.5, NFR-10). Storing a URL is how a private document ends up in a browser history.
/// </para>
/// </summary>
public sealed record StorageReference
{
    private StorageReference(string container, string blobPath)
    {
        Container = container;
        BlobPath = blobPath;
    }

    public string Container { get; }

    public string BlobPath { get; }

    public static StorageReference Create(string container, string blobPath)
    {
        var safeContainer = Guard.AgainstNullOrWhiteSpace(container, "intake.storage.container_required");
        var safePath = Guard.AgainstNullOrWhiteSpace(blobPath, "intake.storage.path_required");

        // A path that can climb out of its prefix is a path traversal waiting to happen the first
        // time any of this is fed to something that resolves relative segments.
        Guard.Against(
            safePath.Contains("..", StringComparison.Ordinal) || safePath.StartsWith('/'),
            "intake.storage.path_not_relative",
            $"Blob path '{safePath}' must be relative and must not contain '..'.");

        return new StorageReference(safeContainer, safePath);
    }

    /// <summary>
    /// The canonical layout: one prefix per supplier, so a mis-scoped SAS leaks one supplier's
    /// documents rather than every supplier's.
    /// </summary>
    public static StorageReference ForDocument(
        string container,
        SupplierId supplierId,
        DocumentId documentId,
        string fileExtension) =>
        Create(container, $"{supplierId.Value:D}/{documentId.Value:D}{fileExtension}");

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Container}/{BlobPath}");
}
