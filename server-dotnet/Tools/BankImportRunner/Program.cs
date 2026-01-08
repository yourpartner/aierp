using System.Collections.Generic;
using System.Diagnostics;
using Npgsql;
using Server.Modules;

static Dictionary<string, string> ParseArgs(string[] arguments)
{
    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < arguments.Length; i++)
    {
        var arg = arguments[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var next = (i + 1) < arguments.Length ? arguments[i + 1] : null;
        if (!string.IsNullOrWhiteSpace(next) && !next.StartsWith("--", StringComparison.Ordinal))
        {
            dict[arg] = next;
            i++;
        }
        else
        {
            dict[arg] = "true";
        }
    }

    return dict;
}

var argsDict = ParseArgs(args);
var defaultConn = "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=Hpxdcd2508";
var connectionString = argsDict.TryGetValue("--connection", out var conn)
    ? conn
    : Environment.GetEnvironmentVariable("BANK_IMPORT_CONNECTION") ?? defaultConn;
var companyCode = argsDict.TryGetValue("--company", out var company) ? company : "JP01";
var sourceUrl = argsDict.TryGetValue("--source", out var src) ? src : null;
var localFile = argsDict.TryGetValue("--file", out var file) ? file : null;
var encodingName = argsDict.TryGetValue("--encoding", out var enc) ? enc : null;

Console.WriteLine($"[INFO] 使用连接串: {connectionString}");
Console.WriteLine($"[INFO] 公司代码: {companyCode}");
if (!string.IsNullOrWhiteSpace(sourceUrl)) Console.WriteLine($"[INFO] 指定下载源: {sourceUrl}");
if (!string.IsNullOrWhiteSpace(localFile)) Console.WriteLine($"[INFO] 指定本地文件: {localFile}");
if (!string.IsNullOrWhiteSpace(encodingName)) Console.WriteLine($"[INFO] 指定编码: {encodingName}");

Uri? sourceUri = null;
if (!string.IsNullOrWhiteSpace(sourceUrl) && !Uri.TryCreate(sourceUrl, UriKind.Absolute, out sourceUri))
{
    Console.WriteLine("[ERROR] source 参数必须是合法的绝对 URL。");
    return;
}

using var dataSource = NpgsqlDataSource.Create(connectionString);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("[WARN] 捕获到取消信号，正在尝试取消导入...");
};

var options = new BankImportOptions
{
    SourceUrl = sourceUri,
    LocalFilePath = string.IsNullOrWhiteSpace(localFile) ? null : localFile,
    EncodingName = string.IsNullOrWhiteSpace(encodingName) ? null : encodingName
};

Console.WriteLine("[INFO] 开始导入银行/支店主数据...");
var stopwatch = Stopwatch.StartNew();
var importTask = Task.Run(() => BankMasterImporter.ImportAsync(dataSource, companyCode, options, cts.Token), cts.Token);

while (!importTask.IsCompleted)
{
    Console.WriteLine($"[INFO] {DateTimeOffset.Now:HH:mm:ss} 正在处理中，已耗时 {stopwatch.Elapsed:c}");
    try
    {
        await Task.WhenAny(importTask, Task.Delay(TimeSpan.FromSeconds(2), cts.Token));
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

try
{
    var result = await importTask;
    stopwatch.Stop();
    Console.WriteLine($"[SUCCESS] 导入完成：银行 {result.BankCount} 条，支店 {result.BranchCount} 条。");
    Console.WriteLine($"[SUCCESS] 数据来源：{result.Source}");
    Console.WriteLine($"[SUCCESS] 耗时：{stopwatch.Elapsed:c}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("[WARN] 导入已取消。");
}
catch (Exception ex)
{
    stopwatch.Stop();
    Console.WriteLine($"[ERROR] 导入失败：{ex.Message}");
    if (ex.InnerException is not null)
    {
        Console.WriteLine($"[ERROR] 内部原因：{ex.InnerException.Message}");
    }
    Console.WriteLine($"[ERROR] 已耗时：{stopwatch.Elapsed:c}");
}
