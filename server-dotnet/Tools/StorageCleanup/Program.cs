using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

var connectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING") ?? "YOUR_CONNECTION_STRING_HERE";
var containerName = "aierp";

var serviceClient = new BlobServiceClient(connectionString);
var containerClient = serviceClient.GetBlobContainerClient(containerName);

Console.WriteLine($"=== Azure Storage 文件结构 (Container: {containerName}) ===\n");

// 收集所有前缀（文件夹）和文件
var prefixes = new HashSet<string>();
var blobs = new List<(string Name, long Size, DateTimeOffset? LastModified)>();

await foreach (var blobItem in containerClient.GetBlobsAsync())
{
    blobs.Add((blobItem.Name, blobItem.Properties.ContentLength ?? 0, blobItem.Properties.LastModified));
    
    // 提取前缀
    var parts = blobItem.Name.Split('/');
    for (int i = 1; i <= parts.Length - 1; i++)
    {
        prefixes.Add(string.Join("/", parts.Take(i)) + "/");
    }
}

Console.WriteLine($"总文件数: {blobs.Count}\n");

// 显示文件夹结构
Console.WriteLine("📦 文件夹层级结构:");
Console.WriteLine("================================");
foreach (var prefix in prefixes.OrderBy(p => p))
{
    var depth = prefix.Count(c => c == '/') - 1;
    var indent = new string(' ', depth * 2);
    var name = prefix.TrimEnd('/').Split('/').Last();
    Console.WriteLine($"{indent}📁 {name}/");
}

Console.WriteLine("\n📄 所有文件列表:");
Console.WriteLine("================================");
foreach (var blob in blobs.OrderBy(b => b.Name))
{
    var sizeKb = blob.Size / 1024.0;
    Console.WriteLine($"  {blob.Name} ({sizeKb:F1} KB)");
}

// 按前缀统计
Console.WriteLine("\n📊 按目录统计:");
Console.WriteLine("================================");
var topLevelPrefixes = prefixes.Where(p => p.Count(c => c == '/') == 1).ToList();
foreach (var prefix in topLevelPrefixes.OrderBy(p => p))
{
    var count = blobs.Count(b => b.Name.StartsWith(prefix));
    var totalSize = blobs.Where(b => b.Name.StartsWith(prefix)).Sum(b => b.Size);
    Console.WriteLine($"  {prefix,-30} {count,4} 个文件, {totalSize / 1024.0:F1} KB");
}

// JP01员工附件结构说明
Console.WriteLine("\n📋 JP01员工附件存储结构:");
Console.WriteLine("================================");
Console.WriteLine("  hr/employees/{公司代码}/{员工ID}/{年/月/日}/{GUID}{扩展名}");
Console.WriteLine("  例: hr/employees/JP01/12345678-1234-1234-1234-123456789abc/2025/12/24/abc123.pdf");

var jp01EmployeeBlobs = blobs.Where(b => b.Name.StartsWith("hr/employees/JP01/")).ToList();
Console.WriteLine($"\n  JP01员工附件数量: {jp01EmployeeBlobs.Count}");
foreach (var blob in jp01EmployeeBlobs.OrderBy(b => b.Name))
{
    Console.WriteLine($"    {blob.Name}");
}

// 自动删除（无需确认）
Console.WriteLine("\n⚠️  开始自动删除所有文件...");

// 删除所有文件
Console.WriteLine($"\n🗑️  开始删除 {blobs.Count} 个文件...");
int deleted = 0;
foreach (var blob in blobs)
{
    try
    {
        await containerClient.DeleteBlobIfExistsAsync(blob.Name, DeleteSnapshotsOption.IncludeSnapshots);
        deleted++;
        Console.WriteLine($"  ✓ 删除: {blob.Name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗ 删除失败: {blob.Name} - {ex.Message}");
    }
}

Console.WriteLine($"\n✅ 完成! 共删除 {deleted} 个文件.");
