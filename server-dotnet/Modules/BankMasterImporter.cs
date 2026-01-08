using CsvHelper;
using CsvHelper.Configuration;
using Npgsql;
using NpgsqlTypes;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Linq;

namespace Server.Modules;

public sealed record BankImportResult(int BankCount, int BranchCount, string Source, DateTimeOffset ImportedAt);

public sealed class BankImportOptions
{
    public Uri? SourceUrl { get; init; }
    public string? LocalFilePath { get; init; }
    public string? EncodingName { get; init; }
}

public static class BankMasterImporter
{
    private static readonly Uri DefaultSource = new("https://www.zengin.or.jp/branch_data/dl/zenkoku.zip");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };
    private static readonly string[] BankCodeHeaders = { "bank_code", "bankcode", "bankCode", "銀行コード", "金融機関コード" };
    private static readonly string[] BankNameHeaders = { "bank_name", "name", "bankName", "銀行名" };
    private static readonly string[] BankKanaHeaders = { "bank_kana", "bankkana", "bankNameKana", "銀行名カナ" };
    private static readonly string[] BranchCodeHeaders = { "branch_code", "branchcode", "branchCode", "支店コード" };
    private static readonly string[] BranchNameHeaders = { "branch_name", "branchname", "branchName", "支店名", "bank_branch_name" };
    private static readonly string[] BranchKanaHeaders = { "branch_kana", "branchkana", "branchNameKana", "支店名カナ" };
    private static readonly string[] BranchNoteHeaders = { "note", "備考", "remarks", "bank_branch_name", "bank_branch_code" };

    public static async Task<BankImportResult> ImportAsync(
        NpgsqlDataSource dataSource,
        string companyCode,
        BankImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _ = companyCode;
        if (dataSource == null) throw new ArgumentNullException(nameof(dataSource));

        options ??= new BankImportOptions();

        var sourceDescription = string.Empty;
        Stream? payloadStream = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(options.LocalFilePath))
            {
                var fullPath = ResolveLocalPath(options.LocalFilePath!);
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException($"未找到指定的本地文件：{fullPath}", fullPath);
                }

                payloadStream = File.OpenRead(fullPath);
                sourceDescription = fullPath;
            }
            else
            {
                var source = options.SourceUrl ?? DefaultSource;
                using var httpClient = new HttpClient();
                using var response = await httpClient.GetAsync(
                    source,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();

                var memory = new MemoryStream();
                await response.Content.CopyToAsync(memory, cancellationToken);
                memory.Position = 0;
                payloadStream = memory;
                sourceDescription = source.ToString();
            }

            if (payloadStream == null)
            {
                throw new InvalidOperationException("未能获取银行支店数据流。");
            }

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var fileHint = options.LocalFilePath ?? sourceDescription;
            var isZip = LooksLikeZip(payloadStream, fileHint);
            var encoding = ResolveEncoding(isZip, fileHint, options.EncodingName);

            Dictionary<string, BankRow> banks;
            List<BranchRow> branches;

            if (isZip)
            {
                using var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read, leaveOpen: false);
                var csvEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
                if (csvEntry == null)
                {
                    throw new InvalidOperationException("压缩包中未找到 CSV 数据文件。");
                }

                using var entryStream = csvEntry.Open();
                (banks, branches) = await ParseCsvAsync(entryStream, encoding, cancellationToken);
            }
            else
            {
                if (payloadStream.CanSeek)
                {
                    payloadStream.Seek(0, SeekOrigin.Begin);
                }
                (banks, branches) = await ParseCsvAsync(payloadStream, encoding, cancellationToken);
            }

            if (banks.Count == 0 || branches.Count == 0)
            {
                throw new InvalidOperationException("解析结果为空，请检查数据源格式是否变更。");
            }

            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await DeleteExistingAsync(connection, cancellationToken);
            var bankCount = await BulkInsertBanksAsync(connection, banks.Values, cancellationToken);
            var branchCount = await BulkInsertBranchesAsync(connection, branches, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new BankImportResult(bankCount, branchCount, sourceDescription, DateTimeOffset.UtcNow);
        }
        finally
        {
            payloadStream?.Dispose();
        }
    }

    private static async Task DeleteExistingAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using (var deleteBranches = connection.CreateCommand())
        {
            deleteBranches.CommandText = "DELETE FROM branches";
            deleteBranches.CommandTimeout = 0;
            await deleteBranches.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteBanks = connection.CreateCommand())
        {
            deleteBanks.CommandText = "DELETE FROM banks";
            deleteBanks.CommandTimeout = 0;
            await deleteBanks.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static Task<int> BulkInsertBanksAsync(
        NpgsqlConnection connection,
        IEnumerable<BankRow> banks,
        CancellationToken cancellationToken)
    {
        var ordered = banks
            .GroupBy(b => b.Code, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(b => b.Code, StringComparer.Ordinal)
            .ToList();

        if (ordered.Count == 0)
        {
            return Task.FromResult(0);
        }

        using var importer = connection.BeginBinaryImport("COPY banks (id, payload) FROM STDIN (FORMAT BINARY)");

        foreach (var bank in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            importer.StartRow();
            importer.Write(Guid.NewGuid(), NpgsqlDbType.Uuid);

            var payload = JsonSerializer.Serialize(new
            {
                bankCode = bank.Code,
                name = bank.Name,
                nameKana = bank.NameKana
            }, JsonOptions);

            importer.Write(payload, NpgsqlDbType.Jsonb);
        }

        importer.Complete();
        return Task.FromResult(ordered.Count);
    }

    private static Task<int> BulkInsertBranchesAsync(
        NpgsqlConnection connection,
        IEnumerable<BranchRow> branches,
        CancellationToken cancellationToken)
    {
        var deduped = new Dictionary<(string BankCode, string BranchCode), BranchRow>();
        foreach (var branch in branches)
        {
            deduped[(branch.BankCode, branch.BranchCode)] = branch;
        }

        var ordered = deduped.Values
            .OrderBy(b => b.BankCode, StringComparer.Ordinal)
            .ThenBy(b => b.BranchCode, StringComparer.Ordinal)
            .ToList();

        if (ordered.Count == 0)
        {
            return Task.FromResult(0);
        }

        using var importer = connection.BeginBinaryImport("COPY branches (id, payload) FROM STDIN (FORMAT BINARY)");

        foreach (var branch in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            importer.StartRow();
            importer.Write(Guid.NewGuid(), NpgsqlDbType.Uuid);

            var payload = JsonSerializer.Serialize(new
            {
                bankCode = branch.BankCode,
                branchCode = branch.BranchCode,
                branchName = branch.Name,
                branchNameKana = branch.NameKana,
                note = branch.Note
            }, JsonOptions);

            importer.Write(payload, NpgsqlDbType.Jsonb);
        }

        importer.Complete();
        return Task.FromResult(ordered.Count);
    }

    private static bool IsHeaderToken(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var normalized = value.Replace("\"", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Contains("銀行コード", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("金融機関コード", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLocalPath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var baseDir = Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(baseDir, path));
    }

    private static bool LooksLikeZip(Stream stream, string? hint)
    {
        if (!string.IsNullOrWhiteSpace(hint) && hint.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!stream.CanSeek)
        {
            return false;
        }

        var original = stream.Position;
        Span<byte> header = stackalloc byte[2];
        var read = stream.Read(header);
        stream.Position = original;
        if (read < 2)
        {
            return false;
        }

        return header[0] == (byte)'P' && header[1] == (byte)'K';
    }

    private static Encoding ResolveEncoding(bool isZip, string? hint, string? encodingName)
    {
        if (!string.IsNullOrWhiteSpace(encodingName))
        {
            return Encoding.GetEncoding(encodingName);
        }

        if (isZip)
        {
            return Encoding.GetEncoding("shift_jis");
        }

        if (!string.IsNullOrWhiteSpace(hint) && hint.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        return Encoding.UTF8;
    }

    private static async Task<(Dictionary<string, BankRow> banks, List<BranchRow> branches)> ParseCsvAsync(
        Stream csvStream,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        if (csvStream == null) throw new ArgumentNullException(nameof(csvStream));

        using var reader = new StreamReader(csvStream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = null,
            DetectDelimiter = false,
            Delimiter = ",",
            IgnoreBlankLines = true,
            TrimOptions = TrimOptions.Trim
        };

        using var csv = new CsvReader(reader, csvConfig);
        if (!await csv.ReadAsync())
        {
            throw new InvalidOperationException("CSV 数据为空或缺少表头。");
        }
        csv.ReadHeader();
        var headerLookup = BuildHeaderLookup(csv.HeaderRecord);

        var banks = new Dictionary<string, BankRow>(StringComparer.Ordinal);
        var branches = new List<BranchRow>();

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bankCode = ReadField(csv, headerLookup, BankCodeHeaders, 0);
            if (string.IsNullOrWhiteSpace(bankCode))
            {
                continue;
            }

            if (IsHeaderToken(bankCode))
            {
                continue;
            }

            var bankName = ReadField(csv, headerLookup, BankNameHeaders, 1) ?? string.Empty;
            var bankKana = ReadField(csv, headerLookup, BankKanaHeaders, 2) ?? string.Empty;
            var branchCode = ReadField(csv, headerLookup, BranchCodeHeaders, 3);
            var branchName = ReadField(csv, headerLookup, BranchNameHeaders, 4) ?? string.Empty;
            var branchKana = ReadField(csv, headerLookup, BranchKanaHeaders, 5) ?? string.Empty;
            var noteRaw = ReadField(csv, headerLookup, BranchNoteHeaders, 6);
            var note = string.IsNullOrWhiteSpace(noteRaw) ? null : noteRaw;

            if (string.IsNullOrWhiteSpace(branchCode))
            {
                continue;
            }

            banks[bankCode] = new BankRow(bankCode, bankName, bankKana);
            branches.Add(new BranchRow(bankCode, branchCode, branchName, branchKana, note));
        }

        return (banks, branches);
    }

    private static Dictionary<string, int> BuildHeaderLookup(string[]? headers)
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (headers == null)
        {
            return dict;
        }

        for (int i = 0; i < headers.Length; i++)
        {
            var header = headers[i]?.Trim();
            if (string.IsNullOrWhiteSpace(header)) continue;
            if (!dict.ContainsKey(header))
            {
                dict[header] = i;
            }
        }

        return dict;
    }

    private static string? ReadField(
        CsvReader csv,
        IReadOnlyDictionary<string, int> headerLookup,
        IReadOnlyList<string> candidates,
        int? fallbackIndex = null)
    {
        foreach (var candidate in candidates)
        {
            if (headerLookup.TryGetValue(candidate, out var idx))
            {
                return csv.GetField(idx)?.Trim();
            }
        }

        if (fallbackIndex.HasValue)
        {
            try
            {
                return csv.GetField(fallbackIndex.Value)?.Trim();
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private sealed record BankRow(string Code, string Name, string NameKana);

    private sealed record BranchRow(string BankCode, string BranchCode, string Name, string NameKana, string? Note);
}

