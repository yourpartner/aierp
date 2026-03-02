using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;

namespace Server.Infrastructure;

public sealed class AzureBlobService
{
    private readonly AzureStorageOptions _options;
    private readonly BlobContainerClient? _containerClient;

    public AzureBlobService(IOptions<AzureStorageOptions> options)
    {
        _options = options.Value ?? new AzureStorageOptions();

        if (_options.IsConfigured)
        {
            var serviceClient = new BlobServiceClient(_options.ConnectionString);
            _containerClient = serviceClient.GetBlobContainerClient(_options.ContainerName);
        }
    }

    private BlobContainerClient EnsureContainer()
    {
        if (_containerClient is null)
        {
            throw new InvalidOperationException("Azure Storage is not configured. Please provide ConnectionString and ContainerName in the AzureStorage section.");
        }
        return _containerClient;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<AzureBlobUploadResult> UploadAsync(Stream content, string blobName, string contentType, CancellationToken cancellationToken)
    {
        var container = EnsureContainer();
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

        var blobClient = container.GetBlobClient(blobName);
        content.Position = 0;
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType
            }
        };
        await blobClient.UploadAsync(content, options, cancellationToken);

        return new AzureBlobUploadResult(blobName, options.HttpHeaders.ContentType ?? "application/octet-stream", content.Length);
    }

    public string GetReadUri(string blobName)
    {
        var container = EnsureContainer();
        var blobClient = container.GetBlobClient(blobName);
        if (_options.SasExpiryMinutes <= 0)
        {
            return blobClient.Uri.ToString();
        }

        if (blobClient.CanGenerateSasUri)
        {
            var builder = new BlobSasBuilder
            {
                BlobContainerName = container.Name,
                BlobName = blobName,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(_options.SasExpiryMinutes)
            };
            builder.SetPermissions(BlobSasPermissions.Read);
            return blobClient.GenerateSasUri(builder).ToString();
        }

        // If SAS cannot be generated (insufficient permission), fall back to the public URL assuming the container allows read access.
        return blobClient.Uri.ToString();
    }

    public async Task DeleteAsync(string? blobName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(blobName))
        {
            return;
        }

        var container = EnsureContainer();
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        var blobClient = container.GetBlobClient(blobName);
        try
        {
            await blobClient.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Blob does not exist; ignore.
        }
    }
}

public sealed record AzureBlobUploadResult(string BlobName, string ContentType, long Size);

