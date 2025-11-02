using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Server.Infrastructure;

// 存储基础设施：统一注册 Azure Blob 客户端
public static class Storage
{
    // 注册 BlobServiceClient 单例。
    // 读取 AzureStorage:AccountName/AccountKey 配置。
    public static IServiceCollection AddAzureBlob(this IServiceCollection services, IConfiguration configuration)
    {
        var cfg = configuration.GetSection("AzureStorage");
        var connectionString = cfg["ConnectionString"];
        var accountName = cfg["AccountName"];
        var accountKey = cfg["AccountKey"];

        // 优先使用连接串；若无连接串则尝试账号/密钥；均缺失时跳过注册（本地无存储也可运行）
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
            // 配置无效时静默跳过注册，避免开发环境因缺少密钥而崩溃
        }

        // 未注册 BlobServiceClient 也返回，调用方需据此判定是否提供附件能力
        return services;
    }
}


