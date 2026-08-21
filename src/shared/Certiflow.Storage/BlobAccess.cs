using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace Certiflow.Storage;

/// <summary>
/// Container clients and read URLs, with or without an account key.
/// <para>
/// <b>The keyless path had never been executed.</b> Locally the store is Azurite and
/// <c>UseDevelopmentStorage=true</c> is a connection string, so every service built its client from
/// one — and in Azure, where <c>allowSharedKeyAccess</c> is false and there is no key to have
/// (NFR-9), the configuration supplies a service URI instead and the client construction threw.
/// Uploading a document returned 500 on the first real deployment for exactly that reason.
/// </para>
/// </summary>
public static class BlobAccess
{
    /// <summary>
    /// Builds a container client from whichever is configured. A service URI means managed identity;
    /// a connection string means Azurite or a key, and is what development uses.
    /// </summary>
    public static (BlobContainerClient Container, BlobServiceClient Service) CreateContainer(
        string? serviceUri, string? connectionString, string containerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        var service = !string.IsNullOrWhiteSpace(serviceUri)
            ? new BlobServiceClient(new Uri(serviceUri), new DefaultAzureCredential())
            : new BlobServiceClient(connectionString);

        var container = service.GetBlobContainerClient(containerName);
        container.CreateIfNotExists(PublicAccessType.None);

        // The service client is returned alongside, because a user-delegation SAS has to be signed
        // with a key obtained from it and the container client cannot hand it back.
        return (container, service);
    }

    /// <summary>
    /// A short-lived read URL for one blob (NFR-10).
    /// <para>
    /// Two ways to sign one, and which is available depends on how the client was built. With an
    /// account key the SDK signs directly. With a managed identity there is no key, so the SAS must
    /// be signed with a <b>user delegation key</b> obtained from the service — which is the whole
    /// reason each storage-using identity holds Storage Blob Delegator as well as Contributor.
    /// </para>
    /// </summary>
    public static async Task<string> CreateReadUrlAsync(
        BlobContainerClient container,
        BlobServiceClient service,
        string blobPath,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);

        var blob = container.GetBlobClient(blobPath);
        var expiresOn = DateTimeOffset.UtcNow.Add(lifetime);

        var builder = new BlobSasBuilder
        {
            BlobContainerName = container.Name,
            BlobName = blobPath,
            Resource = "b",
            ExpiresOn = expiresOn,
        };

        builder.SetPermissions(BlobSasPermissions.Read);

        if (blob.CanGenerateSasUri)
        {
            return blob.GenerateSasUri(builder).ToString();
        }

        // Backdated slightly: the signing service and the storage service can disagree about the
        // current time by a few seconds, and a key that starts in the future is rejected outright.
        var delegationKey = await service.GetUserDelegationKeyAsync(
            DateTimeOffset.UtcNow.AddMinutes(-5), expiresOn, cancellationToken);

        var sas = builder.ToSasQueryParameters(delegationKey.Value, service.AccountName).ToString();

        return $"{blob.Uri}?{sas}";
    }
}
