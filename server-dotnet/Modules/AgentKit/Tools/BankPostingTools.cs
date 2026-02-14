using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Modules.AgentKit.Tools;

// =====================================================================
//  银行明细记账专用 Agent 工具
//  - identify_bank_counterparty : 从银行摘要识别交易对手
//  - search_bank_open_items     : 搜索对手方的未清项
//  - resolve_bank_account       : 根据银行名/账号解析会计科目
// =====================================================================

/// <summary>
/// 从银行交易摘要中识别交易对手（员工/供应商/客户）。
/// </summary>
public sealed class IdentifyBankCounterpartyTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;
    public override string Name => "identify_bank_counterparty";

    public IdentifyBankCounterpartyTool(NpgsqlDataSource ds, ILogger<IdentifyBankCounterpartyTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(
        JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var description = GetString(args, "description") ?? GetString(args, "desc") ?? "";
        var transactionType = GetString(args, "transaction_type") ?? GetString(args, "transactionType") ?? "withdrawal";
        var isWithdrawal = string.Equals(transactionType, "withdrawal", StringComparison.OrdinalIgnoreCase);
        var companyCode = context.CompanyCode;

        if (string.IsNullOrWhiteSpace(description))
            return ErrorResult(Localize(context.Language, "摘要(description)は必須です", "摘要(description)为必填项"));

        // 从摘要中提取对手方名称
        var extractedName = ExtractCounterpartyName(description);
        if (string.IsNullOrWhiteSpace(extractedName))
        {
            return SuccessResult(new
            {
                matched = false,
                extractedName = (string?)null,
                message = Localize(context.Language, "摘要から取引先名を抽出できませんでした", "无法从摘要中提取对手方名称")
            });
        }

        await using var conn = await _ds.OpenConnectionAsync(ct);

        if (isWithdrawal)
        {
            // 出金：员工 → 供应商
            var employee = await MatchEmployeeAsync(conn, companyCode, extractedName, ct);
            if (employee != null)
            {
                return SuccessResult(new
                {
                    matched = true,
                    extractedName,
                    counterpartyKind = "employee",
                    counterpartyId = employee.Value.Id,
                    counterpartyName = employee.Value.Name,
                    assignLine = "debit"
                });
            }

            var vendor = await MatchBusinessPartnerAsync(conn, companyCode, "vendor", extractedName, ct);
            if (vendor != null)
            {
                return SuccessResult(new
                {
                    matched = true,
                    extractedName,
                    counterpartyKind = "vendor",
                    counterpartyId = vendor.Value.Id,
                    counterpartyName = vendor.Value.Name,
                    assignLine = "debit"
                });
            }
        }
        else
        {
            // 入金：客户
            var customer = await MatchBusinessPartnerAsync(conn, companyCode, "customer", extractedName, ct);
            if (customer != null)
            {
                return SuccessResult(new
                {
                    matched = true,
                    extractedName,
                    counterpartyKind = "customer",
                    counterpartyId = customer.Value.Id,
                    counterpartyName = customer.Value.Name,
                    assignLine = "credit"
                });
            }
        }

        return SuccessResult(new
        {
            matched = false,
            extractedName,
            message = Localize(context.Language,
                $"「{extractedName}」に該当する取引先が見つかりませんでした",
                $"未找到匹配「{extractedName}」的交易对手")
        });
    }

    // --- Name extraction ---
    private static readonly string[] TransferPrefixes =
    {
        "振込 ", "振込　", "振替 ", "振替　", "ﾌﾘｺﾐ ", "入金 ", "出金 "
    };

    private static string ExtractCounterpartyName(string description)
    {
        var result = description.Trim();
        foreach (var prefix in TransferPrefixes)
        {
            if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                result = result[prefix.Length..].TrimStart();
                break;
            }
        }
        result = Regex.Replace(result, @"\(.+?\)", "").Trim();
        result = Regex.Replace(result, @"\s+\d+$", "").Trim();
        return result;
    }

    // --- Normalize for matching ---
    private static readonly Regex NoiseRegex = new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);
    private static readonly string[] StopWords =
    {
        "振込", "振替", "ﾌﾘｺﾐ", "ﾌﾘｶｴ", "入金", "出金", "普通預金", "当座預金", "銀行"
    };
    private static readonly string[] CorpTokens =
    {
        "株式会社", "（株）", "(株)", "有限会社", "合同会社", "KK", "K.K.", "CO.", "LTD", "LLC", "INC"
    };

    private static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var s = input.Normalize(System.Text.NormalizationForm.FormKC).ToUpperInvariant()
            .Replace('\u3000', ' ').Replace('\t', ' ');
        foreach (var t in CorpTokens) s = s.Replace(t.ToUpperInvariant(), " ");
        foreach (var w in StopWords) s = s.Replace(w.ToUpperInvariant(), " ");
        s = NoiseRegex.Replace(s, " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static double SimilarityScore(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (string.IsNullOrWhiteSpace(na) || string.IsNullOrWhiteSpace(nb)) return 0;
        if (string.Equals(na, nb, StringComparison.OrdinalIgnoreCase)) return 1.0;
        if (na.Contains(nb, StringComparison.OrdinalIgnoreCase) || nb.Contains(na, StringComparison.OrdinalIgnoreCase)) return 0.85;
        // simple token overlap
        var tokensA = na.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokensB = nb.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokensA.Length == 0 || tokensB.Length == 0) return 0;
        var overlap = tokensA.Count(t => tokensB.Any(tb => tb.Contains(t) || t.Contains(tb)));
        return (double)overlap / Math.Max(tokensA.Length, tokensB.Length);
    }

    // --- Employee matching ---
    private async Task<(string Id, string? Name)?> MatchEmployeeAsync(
        NpgsqlConnection conn, string companyCode, string searchName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT employee_code, payload FROM employees WHERE company_code = $1 ORDER BY updated_at DESC LIMIT 100";
        cmd.Parameters.AddWithValue(companyCode);

        var candidates = new List<(string Code, string? Name, double Score)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var code = reader.GetString(0);
            using var doc = JsonDocument.Parse(reader.GetString(1));
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var nameKana = root.TryGetProperty("nameKana", out var nk) && nk.ValueKind == JsonValueKind.String ? nk.GetString() : null;

            double score = 0;
            if (!string.IsNullOrWhiteSpace(nameKana))
                score = Math.Max(score, SimilarityScore(searchName, nameKana));
            if (!string.IsNullOrWhiteSpace(name))
                score = Math.Max(score, SimilarityScore(searchName, name));

            if (score >= 0.6)
                candidates.Add((code, name, score));
        }

        if (candidates.Count == 0) return null;
        var sorted = candidates.OrderByDescending(c => c.Score).ToList();
        var best = sorted[0];
        return best.Score >= 0.7 ? (best.Code, best.Name) : null;
    }

    // --- Business partner matching ---
    private async Task<(string Id, string? Name)?> MatchBusinessPartnerAsync(
        NpgsqlConnection conn, string companyCode, string type, string searchName, CancellationToken ct)
    {
        var flagField = string.Equals(type, "customer", StringComparison.OrdinalIgnoreCase) ? "isCustomer" : "isVendor";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT payload->>'code', payload FROM businesspartners WHERE company_code = $1 AND (payload->>'{flagField}')::boolean = true ORDER BY updated_at DESC LIMIT 200";
        cmd.Parameters.AddWithValue(companyCode);

        var candidates = new List<(string Code, string? Name, double Score)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var code = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (string.IsNullOrWhiteSpace(code)) continue;
            using var doc = JsonDocument.Parse(reader.GetString(1));
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var nameKana = root.TryGetProperty("nameKana", out var nk) && nk.ValueKind == JsonValueKind.String ? nk.GetString() : null;

            double score = 0;
            if (!string.IsNullOrWhiteSpace(nameKana))
                score = Math.Max(score, SimilarityScore(searchName, nameKana));
            if (!string.IsNullOrWhiteSpace(name))
                score = Math.Max(score, SimilarityScore(searchName, name));

            if (score >= 0.6)
                candidates.Add((code!, name, score));
        }

        if (candidates.Count == 0) return null;
        var sorted = candidates.OrderByDescending(c => c.Score).ToList();
        var best = sorted[0];
        return best.Score >= 0.7 ? (best.Code, best.Name) : null;
    }
}

/// <summary>
/// 搜索对手方的未清项（应收/应付），用于银行入出金的清账匹配。
/// </summary>
public sealed class SearchBankOpenItemsTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;
    public override string Name => "search_bank_open_items";

    public SearchBankOpenItemsTool(NpgsqlDataSource ds, ILogger<SearchBankOpenItemsTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(
        JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var counterpartyId = GetString(args, "counterparty_id") ?? GetString(args, "counterpartyId") ?? "";
        var amountVal = GetDecimal(args, "amount");
        var transactionType = GetString(args, "transaction_type") ?? GetString(args, "transactionType") ?? "withdrawal";
        var transactionDate = GetString(args, "transaction_date") ?? GetString(args, "transactionDate");
        var companyCode = context.CompanyCode;

        if (string.IsNullOrWhiteSpace(counterpartyId))
            return ErrorResult(Localize(context.Language, "counterparty_id は必須です", "counterparty_id 为必填项"));

        var isWithdrawal = string.Equals(transactionType, "withdrawal", StringComparison.OrdinalIgnoreCase);
        var amount = amountVal ?? 0m;
        var txDate = DateTime.TryParse(transactionDate, out var pd) ? pd : DateTime.Today;
        // 出金 → 找贷方未清项(CR), 入金 → 找借方未清项(DR)
        var targetDirection = isWithdrawal ? "CR" : "DR";

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
WITH oi_with_drcr AS (
    SELECT oi.id, oi.account_code, oi.residual_amount, oi.doc_date, 
           v.voucher_no,
           COALESCE((SELECT line->>'drcr' FROM jsonb_array_elements(v.payload->'lines') AS line 
                     WHERE (line->>'lineNo')::int = oi.voucher_line_no LIMIT 1), 'DR') as drcr
    FROM open_items oi
    JOIN vouchers v ON v.id = oi.voucher_id AND v.company_code = oi.company_code
    WHERE oi.company_code = $1
      AND oi.partner_id = $2
      AND ABS(oi.residual_amount) > 0.01
      AND oi.doc_date <= $4
)
SELECT id, account_code, residual_amount, doc_date, voucher_no, drcr
FROM oi_with_drcr
WHERE drcr = $3
ORDER BY ABS(ABS(residual_amount) - $5) ASC, doc_date DESC
LIMIT 20";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(counterpartyId);
        cmd.Parameters.AddWithValue(targetDirection);
        cmd.Parameters.AddWithValue(txDate);
        cmd.Parameters.AddWithValue(amount);

        var items = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var residual = reader.GetDecimal(2);
            var diff = amount > 0 ? Math.Abs(Math.Abs(residual) - amount) : 0m;
            items.Add(new
            {
                openItemId = reader.GetGuid(0).ToString(),
                accountCode = reader.GetString(1),
                residualAmount = residual,
                docDate = reader.GetDateTime(3).ToString("yyyy-MM-dd"),
                voucherNo = reader.IsDBNull(4) ? null : reader.GetString(4),
                direction = reader.GetString(5),
                amountDifference = diff,
                isExactMatch = diff < 0.01m,
                isCloseMatch = diff <= Math.Min(amount * 0.05m, 1000m)
            });
        }

        return SuccessResult(new
        {
            counterpartyId,
            transactionType,
            searchAmount = amount,
            totalFound = items.Count,
            items,
            hint = items.Count == 0
                ? Localize(context.Language, "該当する未清項が見つかりませんでした。兜底科目を使用してください。", "未找到匹配的未清项。请使用兜底科目。")
                : Localize(context.Language, "上記の未清項から最適なものを選んで清账してください。isExactMatch=true の項目を優先してください。", "请从以上未清项中选择最合适的进行清账。优先选择 isExactMatch=true 的项目。")
        });
    }
}

/// <summary>
/// 根据银行名/账号/持有者名解析对应的会计科目编码。
/// </summary>
public sealed class ResolveBankAccountTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;
    public override string Name => "resolve_bank_account";

    public ResolveBankAccountTool(NpgsqlDataSource ds, ILogger<ResolveBankAccountTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(
        JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var bankName = GetString(args, "bank_name") ?? GetString(args, "bankName");
        var accountNumber = GetString(args, "account_number") ?? GetString(args, "accountNumber");
        var accountName = GetString(args, "account_name") ?? GetString(args, "accountName");
        var companyCode = context.CompanyCode;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_code, payload FROM accounts WHERE company_code=$1 AND COALESCE((payload->>'isBank')::boolean, false) = true";
        cmd.Parameters.AddWithValue(companyCode);

        var candidates = new List<(string AccountCode, string? BankName, string? AccountNo, string? Holder)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var accountCode = reader.GetString(0);
            try
            {
                using var doc = JsonDocument.Parse(reader.GetString(1));
                var root = doc.RootElement;
                if (!root.TryGetProperty("bank", out var bankInfo)) continue;
                var bn = bankInfo.TryGetProperty("bankName", out var bnEl) && bnEl.ValueKind == JsonValueKind.String ? bnEl.GetString() : null;
                var an = bankInfo.TryGetProperty("accountNo", out var anEl) && anEl.ValueKind == JsonValueKind.String ? anEl.GetString() : null;
                var hd = bankInfo.TryGetProperty("holder", out var hdEl) && hdEl.ValueKind == JsonValueKind.String ? hdEl.GetString() : null;
                candidates.Add((accountCode, bn, an, hd));
            }
            catch { /* skip */ }
        }

        // 匹配逻辑
        (string AccountCode, string? BankName, string? AccountNo, string? Holder)? match = null;

        // 1. 按账号匹配
        if (!string.IsNullOrWhiteSpace(accountNumber))
        {
            var normalized = NormalizeAccountNo(accountNumber);
            match = candidates.FirstOrDefault(c => NormalizeAccountNo(c.AccountNo) == normalized);
        }

        // 2. 按银行名匹配
        if (match == null && !string.IsNullOrWhiteSpace(bankName))
        {
            match = candidates.FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(c.BankName) &&
                (c.BankName.Contains(bankName, StringComparison.OrdinalIgnoreCase) ||
                 bankName.Contains(c.BankName, StringComparison.OrdinalIgnoreCase)));
        }

        // 3. 按持有者名匹配
        if (match == null && !string.IsNullOrWhiteSpace(accountName))
        {
            match = candidates.FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(c.Holder) &&
                accountName.Contains(c.Holder, StringComparison.OrdinalIgnoreCase));
        }

        // 4. 如果只有一个银行账户，直接使用
        if (match == null && candidates.Count == 1)
        {
            match = candidates[0];
        }

        if (match != null)
        {
            return SuccessResult(new
            {
                found = true,
                accountCode = match.Value.AccountCode,
                bankName = match.Value.BankName,
                accountNo = match.Value.AccountNo,
                holder = match.Value.Holder
            });
        }

        return SuccessResult(new
        {
            found = false,
            availableBankAccounts = candidates.Select(c => new
            {
                c.AccountCode,
                c.BankName,
                c.AccountNo,
                c.Holder
            }),
            message = Localize(context.Language,
                "該当する銀行口座が見つかりませんでした。上記の候補から選択してください。",
                "未找到匹配的银行账户。请从以上候选中选择。")
        });
    }

    private static string NormalizeAccountNo(string? no)
    {
        if (string.IsNullOrWhiteSpace(no)) return "";
        return new string(no.Where(char.IsDigit).ToArray()).TrimStart('0');
    }
}
