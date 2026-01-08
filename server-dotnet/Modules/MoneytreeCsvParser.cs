using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Server.Modules;

public sealed class MoneytreeCsvParser
{
    private static readonly string[] DateHeaders = { "Transaction Date", "取引日", "Date", "日付" };
    private static readonly string[] DepositHeaders = { "Deposit", "Deposits", "入金" };
    private static readonly string[] WithdrawalHeaders = { "Withdrawal", "Withdrawals", "支出", "出金" };
    private static readonly string[] BalanceHeaders = { "Balance", "残高" };
    private static readonly string[] CurrencyHeaders = { "Currency", "通貨" };
    private static readonly string[] DescriptionHeaders = { "Description", "内容", "摘要", "取引名", "Transaction Description" };
    private static readonly string[] BankHeaders = { "金融機関", "Bank", "Financial Institution" };
    private static readonly string[] AccountNameHeaders = { "Account", "Account Name", "取引アカウント", "口座名" };
    private static readonly string[] AccountNumberHeaders = { "Account Number", "取引アカウント番号" };

    public IReadOnlyList<MoneytreeRow> Parse(byte[] content, string? fileName = null)
    {
        if (LooksLikeExcel(content, fileName))
        {
            return ParseExcel(content);
        }

        return ParseCsv(content);
    }

    private static bool LooksLikeExcel(byte[] content, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var ext = Path.GetExtension(fileName);
            if (ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".xls", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return content.Length >= 4 &&
               content[0] == (byte)'P' &&
               content[1] == (byte)'K' &&
               (content[2] == 3 || content[2] == 5 || content[2] == 7) &&
               (content[3] == 4 || content[3] == 6 || content[3] == 8);
    }

    private static IReadOnlyList<MoneytreeRow> ParseExcel(byte[] content)
    {
        using var memory = new MemoryStream(content);
        using var workbook = new XLWorkbook(memory);
        var worksheet = workbook.Worksheets.FirstOrDefault();
        if (worksheet is null)
        {
            return Array.Empty<MoneytreeRow>();
        }

        var firstRow = worksheet.FirstRowUsed();
        if (firstRow is null)
        {
            return Array.Empty<MoneytreeRow>();
        }

        var headers = firstRow.CellsUsed()
            .Select(c => c.GetString()?.Trim() ?? string.Empty)
            .ToArray();

        var rows = new List<MoneytreeRow>();
        foreach (var row in worksheet.RowsUsed().Skip(1))
        {
            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Length; i++)
            {
                var header = headers[i];
                if (string.IsNullOrWhiteSpace(header))
                {
                    continue;
                }

                raw[header] = row.Cell(i + 1).GetString().Trim();
            }

            if (raw.Count == 0)
            {
                continue;
            }

            rows.Add(BuildRow(raw));
        }

        return rows;
    }

    private static IReadOnlyList<MoneytreeRow> ParseCsv(byte[] content)
    {
        using var memory = new MemoryStream(content);
        using var reader = new StreamReader(memory, Encoding.UTF8, leaveOpen: false);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true,
            MissingFieldFound = null,
            BadDataFound = null,
            HeaderValidated = null,
            IgnoreBlankLines = true,
        };

        using var csv = new CsvReader(reader, config);

        var rows = new List<MoneytreeRow>();
        if (!csv.Read() || !csv.ReadHeader())
        {
            return rows;
        }

        var headers = csv.HeaderRecord ?? Array.Empty<string>();
        while (csv.Read())
        {
            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
            {
                if (string.IsNullOrWhiteSpace(header))
                {
                    continue;
                }
                raw[header] = csv.GetField(header) ?? string.Empty;
            }

            if (raw.Count == 0)
            {
                continue;
            }

            rows.Add(BuildRow(raw));
        }

        return rows;
    }

    private static MoneytreeRow BuildRow(IReadOnlyDictionary<string, string> raw)
    {
        var date = ParseDate(GetField(raw, DateHeaders));
        var deposit = ParseDecimal(GetField(raw, DepositHeaders));
        var withdrawal = ParseDecimal(GetField(raw, WithdrawalHeaders));
        var balance = ParseDecimal(GetField(raw, BalanceHeaders));
        var currency = GetField(raw, CurrencyHeaders);
        var description = GetField(raw, DescriptionHeaders) ?? string.Empty;
        var bankName = GetField(raw, BankHeaders);
        var accountName = GetField(raw, AccountNameHeaders);
        var accountNumber = GetField(raw, AccountNumberHeaders);

        return new MoneytreeRow(
            date,
            deposit,
            withdrawal,
            balance,
            currency,
            bankName,
            description,
            accountName,
            accountNumber,
            raw);
    }

    private static string? GetField(IReadOnlyDictionary<string, string> raw, params string[] names)
    {
        foreach (var name in names)
        {
            if (raw.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        foreach (var kv in raw)
        {
            if (names.Any(n => string.Equals(kv.Key, n, StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(kv.Value))
            {
                return kv.Value.Trim();
            }
        }

        return null;
    }

    private static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.Date;
        }

        if (DateTime.TryParse(text, CultureInfo.GetCultureInfo("ja-JP"), DateTimeStyles.None, out date))
        {
            return date.Date;
        }

        return null;
    }

    private static decimal? ParseDecimal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text.Replace(",", string.Empty);
        if (decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        if (decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.GetCultureInfo("ja-JP"), out value))
        {
            return value;
        }

        return null;
    }

    public sealed record MoneytreeRow(
        DateTime? TransactionDate,
        decimal? DepositAmount,
        decimal? WithdrawalAmount,
        decimal? Balance,
        string? Currency,
        string? BankName,
        string Description,
        string? AccountName,
        string? AccountNumber,
        IReadOnlyDictionary<string, string> Raw)
    {
        public decimal? Amount => (DepositAmount, WithdrawalAmount) switch
        {
            (null, null) => null,
            _ => (DepositAmount ?? 0m) - (WithdrawalAmount ?? 0m)
        };
    }
}
