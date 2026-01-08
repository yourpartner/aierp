using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Server.Infrastructure;

// Storage infrastructure: registers the Azure Blob Service client.
public static class Storage
{
    // Registers a singleton BlobServiceClient using AzureStorage configuration.
    public static IServiceCollection AddAzureBlob(this IServiceCollection services, IConfiguration configuration)
    {
        var cfg = configuration.GetSection("AzureStorage");
        var connectionString = cfg["ConnectionString"];
        var accountName = cfg["AccountName"];
        var accountKey = cfg["AccountKey"];

        // Prefer connection string; otherwise fall back to account/key; skip registration if both missing.
        try
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                var clientFromConn = new BlobServiceClient(connectionString);
                services.AddSingleton(clientFromConn);
                return services;
            }

            if (!string.IsNullOrWhiteSpace(accountName) && !string.IsNullOrWhiteSpace(accountKey))
            {
                var uri = new Uri($"https://{accountName}.blob.core.windows.net");
                var cred = new Azure.Storage.StorageSharedKeyCredential(accountName, accountKey);
                var client = new BlobServiceClient(uri, cred);
                services.AddSingleton(client);
                return services;
            }
        }
        catch
        {
            // Swallow invalid configuration errors so local development can proceed without secrets.
        }

        // Return even if BlobServiceClient is not registered (callers can detect attachment capability).
        return services;
    }
}


