using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Modules.AgentKit.Tools;

// =====================================================================
//  銀行明細記帳専用 Agent ツール (Skill 駆動アーキテクチャ)
//  - identify_bank_counterparty : 銀行摘要から取引先を識別
//  - search_bank_open_items     : 未清項を検索（paymentDate + 30日容差）
//  - resolve_bank_account       : 銀行口座から勘定科目を解決
//  - search_historical_patterns : 類似取引の過去記帳パターンを検索
//  - clear_open_item            : 未清項を消込（create_voucher 成功後に呼ぶ）
//  - skip_transaction           : 取引をスキップ（人手確認が必要）
// =====================================================================

/// <summary>
/// 从银行交易摘要中识别交易对手（员工/供应商/客户）。
/// 返回 UUID 格式的 partner_id（用于 open_items 匹配）。
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
            var employee = await MatchEmployeeAsync(conn, companyCode, extractedName, ct);
            if (employee != null)
            {
                return SuccessResult(new
                {
                    matched = true,
                    extractedName,
                    counterpartyKind = "employee",
                    counterpartyId = employee.Value.Uuid,
                    counterpartyCode = employee.Value.Code,
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
        var tokensA = na.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokensB = nb.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokensA.Length == 0 || tokensB.Length == 0) return 0;
        var overlap = tokensA.Count(t => tokensB.Any(tb => tb.Contains(t) || t.Contains(tb)));
        return (double)overlap / Math.Max(tokensA.Length, tokensB.Length);
    }

    // 返回 UUID + employee_code + name
    private async Task<(string Uuid, string Code, string? Name)?> MatchEmployeeAsync(
        NpgsqlConnection conn, string companyCode, string searchName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, employee_code, payload FROM employees WHERE company_code = $1 ORDER BY updated_at DESC LIMIT 100";
        cmd.Parameters.AddWithValue(companyCode);

        var candidates = new List<(Guid Id, string Code, string? Name, double Score)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var code = reader.GetString(1);
            using var doc = JsonDocument.Parse(reader.GetString(2));
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var nameKana = root.TryGetProperty("nameKana", out var nk) && nk.ValueKind == JsonValueKind.String ? nk.GetString() : null;

            double score = 0;
            if (!string.IsNullOrWhiteSpace(nameKana))
                score = Math.Max(score, SimilarityScore(searchName, nameKana));
            if (!string.IsNullOrWhiteSpace(name))
                score = Math.Max(score, SimilarityScore(searchName, name));

            if (score >= 0.6)
                candidates.Add((id, code, name, score));
        }

        if (candidates.Count == 0) return null;
        var sorted = candidates.OrderByDescending(c => c.Score).ToList();
        var best = sorted[0];
        return best.Score >= 0.7 ? (best.Id.ToString(), best.Code, best.Name) : null;
    }

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
/// 使用 paymentDate（凭证行的支付日）+ 30天容差来匹配，而非 doc_date。
/// counterparty_id 支持 UUID 和 employee_code 两种格式（自动解析）。
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
        var targetDirection = isWithdrawal ? "CR" : "DR";

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // employee_code → UUID 解析
        var partnerIdForQuery = counterpartyId;
        if (!Guid.TryParse(counterpartyId, out _))
        {
            await using var resolveCmd = conn.CreateCommand();
            resolveCmd.CommandText = "SELECT id FROM employees WHERE company_code = $1 AND employee_code = $2 LIMIT 1";
            resolveCmd.Parameters.AddWithValue(companyCode);
            resolveCmd.Parameters.AddWithValue(counterpartyId);
            var resolved = await resolveCmd.ExecuteScalarAsync(ct);
            if (resolved is Guid g)
                partnerIdForQuery = g.ToString();
        }

        // paymentDate ベースの検索: 凭证行の paymentDate と銀行取引日の差が 30日以内
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
WITH oi_with_detail AS (
    SELECT oi.id, oi.account_code, oi.residual_amount, oi.doc_date, 
           v.voucher_no,
           COALESCE((SELECT line->>'drcr' FROM jsonb_array_elements(v.payload->'lines') AS line 
                     WHERE (line->>'lineNo')::int = oi.voucher_line_no LIMIT 1), 'DR') as drcr,
           COALESCE(
               (SELECT (line->>'paymentDate')::date FROM jsonb_array_elements(v.payload->'lines') AS line 
                WHERE (line->>'lineNo')::int = oi.voucher_line_no AND line->>'paymentDate' IS NOT NULL LIMIT 1),
               oi.doc_date
           ) as effective_date
    FROM open_items oi
    JOIN vouchers v ON v.id = oi.voucher_id AND v.company_code = oi.company_code
    WHERE oi.company_code = $1
      AND oi.partner_id = $2
      AND oi.cleared_flag = false
      AND ABS(oi.residual_amount) > 0.01
)
SELECT id, account_code, residual_amount, doc_date, voucher_no, drcr, effective_date
FROM oi_with_detail
WHERE drcr = $3
  AND ABS(effective_date - $4::date) <= 30
ORDER BY ABS(ABS(residual_amount) - $5) ASC, ABS(effective_date - $4::date) ASC
LIMIT 20";
        cmd.Parameters.AddWithValue(companyCode);
        // partner_id 列是 UUID 类型，必须传 Guid 而非 string，否则 uuid = text 比较会失败
        if (Guid.TryParse(partnerIdForQuery, out var partnerGuid))
            cmd.Parameters.AddWithValue(partnerGuid);
        else
            cmd.Parameters.AddWithValue(partnerIdForQuery);
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
                effectiveDate = reader.GetDateTime(6).ToString("yyyy-MM-dd"),
                amountDifference = diff,
                isExactMatch = diff < 0.01m,
                isCloseMatch = diff <= Math.Min(amount * 0.05m, 1000m)
            });
        }

        return SuccessResult(new
        {
            counterpartyId = partnerIdForQuery,
            transactionType,
            searchAmount = amount,
            dateToleranceDays = 30,
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
            catch { }
        }

        (string AccountCode, string? BankName, string? AccountNo, string? Holder)? match = null;

        if (!string.IsNullOrWhiteSpace(accountNumber))
        {
            var normalized = NormalizeAccountNo(accountNumber);
            match = candidates.FirstOrDefault(c => NormalizeAccountNo(c.AccountNo) == normalized);
        }
        if (match == null && !string.IsNullOrWhiteSpace(bankName))
        {
            match = candidates.FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(c.BankName) &&
                (c.BankName.Contains(bankName, StringComparison.OrdinalIgnoreCase) ||
                 bankName.Contains(c.BankName, StringComparison.OrdinalIgnoreCase)));
        }
        if (match == null && !string.IsNullOrWhiteSpace(accountName))
        {
            match = candidates.FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(c.Holder) &&
                accountName.Contains(c.Holder, StringComparison.OrdinalIgnoreCase));
        }
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
            availableBankAccounts = candidates.Select(c => new { c.AccountCode, c.BankName, c.AccountNo, c.Holder }),
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

/// <summary>
/// 查找类似银行交易的历史记账模式，帮助 AI 决定使用什么科目。
/// </summary>
public sealed class SearchHistoricalPatternsTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;
    public override string Name => "search_historical_patterns";

    public SearchHistoricalPatternsTool(NpgsqlDataSource ds, ILogger<SearchHistoricalPatternsTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(
        JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var description = GetString(args, "description") ?? "";
        var transactionType = GetString(args, "transaction_type") ?? "withdrawal";
        var companyCode = context.CompanyCode;

        if (string.IsNullOrWhiteSpace(description))
            return ErrorResult("description is required");

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // 1. ai_learned_patterns から学習済みパターンを検索
        // 実テーブル構造: id, company_code, pattern_type, conditions(jsonb), recommendation(jsonb), confidence, sample_count, last_updated_at
        var patterns = new List<object>();
        await using var patternCmd = conn.CreateCommand();
        patternCmd.CommandText = @"
SELECT id::text,
       conditions->>'description' AS cond_desc,
       conditions->>'isWithdrawal' AS cond_withdrawal,
       recommendation->>'debitAccount' AS debit,
       recommendation->>'creditAccount' AS credit,
       recommendation->>'debitAccountName' AS debit_name,
       recommendation->>'creditAccountName' AS credit_name,
       confidence
FROM ai_learned_patterns 
WHERE company_code = $1 AND pattern_type = 'bank_description_account'
ORDER BY confidence DESC
LIMIT 50";
        patternCmd.Parameters.AddWithValue(companyCode);

        var isWithdrawal = string.Equals(transactionType, "withdrawal", StringComparison.OrdinalIgnoreCase);
        await using var patternReader = await patternCmd.ExecuteReaderAsync(ct);
        while (await patternReader.ReadAsync(ct))
        {
            var patternDesc = patternReader.IsDBNull(1) ? "" : patternReader.GetString(1);
            if (!string.IsNullOrWhiteSpace(patternDesc) && IsSimilarDescription(description, patternDesc))
            {
                patterns.Add(new
                {
                    patternId = patternReader.GetString(0),
                    matchedDescription = patternDesc,
                    debitAccount = patternReader.IsDBNull(3) ? null : patternReader.GetString(3),
                    creditAccount = patternReader.IsDBNull(4) ? null : patternReader.GetString(4),
                    debitAccountName = patternReader.IsDBNull(5) ? null : patternReader.GetString(5),
                    creditAccountName = patternReader.IsDBNull(6) ? null : patternReader.GetString(6),
                    confidence = patternReader.GetDecimal(7).ToString("F2")
                });
            }
        }

        // 2. 過去の銀行明細から同様の取引の記帳実績を検索
        var historicalVouchers = new List<object>();
        await using var histCmd = conn.CreateCommand();
        histCmd.CommandText = @"
SELECT mt.description, v.voucher_no, v.payload->'lines' as lines
FROM moneytree_transactions mt
JOIN vouchers v ON v.id = mt.voucher_id AND v.company_code = mt.company_code
WHERE mt.company_code = $1 AND mt.posting_status = 'posted' AND mt.voucher_id IS NOT NULL
ORDER BY mt.updated_at DESC
LIMIT 200";
        histCmd.Parameters.AddWithValue(companyCode);

        await using var histReader = await histCmd.ExecuteReaderAsync(ct);
        while (await histReader.ReadAsync(ct))
        {
            var histDesc = histReader.IsDBNull(0) ? "" : histReader.GetString(0);
            if (IsSimilarDescription(description, histDesc))
            {
                var voucherNo = histReader.IsDBNull(1) ? null : histReader.GetString(1);
                var linesJson = histReader.IsDBNull(2) ? "[]" : histReader.GetString(2);
                var accounts = ExtractAccountCodes(linesJson);
                historicalVouchers.Add(new
                {
                    description = histDesc,
                    voucherNo,
                    accounts
                });
                if (historicalVouchers.Count >= 5) break;
            }
        }

        return SuccessResult(new
        {
            searchDescription = description,
            learnedPatterns = patterns,
            historicalVouchers,
            hint = patterns.Count > 0
                ? Localize(context.Language, "学習済みパターンが見つかりました。上記のパターンを参考にしてください。", "找到学习模式，请参考。")
                : historicalVouchers.Count > 0
                    ? Localize(context.Language, "過去の類似取引が見つかりました。参考にしてください。", "找到历史类似交易，请参考。")
                    : Localize(context.Language, "類似する過去の取引が見つかりませんでした。", "未找到类似的历史交易。")
        });
    }

    private static bool IsSimilarDescription(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var na = a.Normalize(System.Text.NormalizationForm.FormKC).ToUpperInvariant();
        var nb = b.Normalize(System.Text.NormalizationForm.FormKC).ToUpperInvariant();
        if (na.Contains(nb) || nb.Contains(na)) return true;
        var tokensA = na.Split(new[] { ' ', '　', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var tokensB = nb.Split(new[] { ' ', '　', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokensA.Length == 0 || tokensB.Length == 0) return false;
        var overlap = tokensA.Count(t => tokensB.Any(tb => tb.Contains(t) || t.Contains(tb)));
        return (double)overlap / Math.Max(tokensA.Length, tokensB.Length) >= 0.5;
    }

    private static List<object> ExtractAccountCodes(string linesJson)
    {
        var result = new List<object>();
        try
        {
            using var doc = JsonDocument.Parse(linesJson);
            foreach (var line in doc.RootElement.EnumerateArray())
            {
                var code = line.TryGetProperty("accountCode", out var c) ? c.GetString() : null;
                var drcr = line.TryGetProperty("drcr", out var d) ? d.GetString() : null;
                var amt = line.TryGetProperty("amount", out var a) ? a.GetDecimal() : 0m;
                if (!string.IsNullOrWhiteSpace(code))
                    result.Add(new { accountCode = code, drcr, amount = amt });
            }
        }
        catch { }
        return result;
    }
}

/// <summary>
/// 消込指定的未清項。create_voucher 完成后调用此工具进行清账。
/// </summary>
public sealed class ClearOpenItemTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;
    public override string Name => "clear_open_item";

    public ClearOpenItemTool(NpgsqlDataSource ds, ILogger<ClearOpenItemTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(
        JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var openItemId = GetString(args, "open_item_id") ?? GetString(args, "openItemId") ?? "";
        var voucherNo = GetString(args, "voucher_no") ?? GetString(args, "voucherNo") ?? "";
        var companyCode = context.CompanyCode;

        if (!Guid.TryParse(openItemId, out var oiGuid))
            return ErrorResult("open_item_id (UUID) is required");

        if (string.IsNullOrWhiteSpace(voucherNo))
            return ErrorResult("voucher_no is required");

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // 获取清账凭证的 ID
        Guid? voucherId = null;
        await using var vCmd = conn.CreateCommand();
        vCmd.CommandText = "SELECT id FROM vouchers WHERE company_code = $1 AND voucher_no = $2 LIMIT 1";
        vCmd.Parameters.AddWithValue(companyCode);
        vCmd.Parameters.AddWithValue(voucherNo);
        var vid = await vCmd.ExecuteScalarAsync(ct);
        if (vid is Guid g) voucherId = g;

        if (!voucherId.HasValue)
            return ErrorResult(Localize(context.Language, $"伝票 {voucherNo} が見つかりません", $"凭证 {voucherNo} 不存在"));

        // 执行清账
        await using var clearCmd = conn.CreateCommand();
        clearCmd.CommandText = @"
UPDATE open_items 
SET residual_amount = 0, 
    cleared_by_voucher_id = $1, 
    cleared_at = now(), 
    updated_at = now() 
WHERE id = $2 AND company_code = $3 AND ABS(residual_amount) > 0.01
RETURNING id, account_code, residual_amount";
        clearCmd.Parameters.AddWithValue(voucherId.Value);
        clearCmd.Parameters.AddWithValue(oiGuid);
        clearCmd.Parameters.AddWithValue(companyCode);

        await using var reader = await clearCmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var clearedId = reader.GetGuid(0);
            var accountCode = reader.GetString(1);

            Logger.LogInformation("[ClearOpenItem] Cleared open item {OiId} (account={Account}) with voucher {VoucherNo}", 
                clearedId, accountCode, voucherNo);

            // 更新 moneytree_transactions 的 cleared_open_item_id
            if (!string.IsNullOrWhiteSpace(context.BankTransactionId))
            {
                await using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = @"
UPDATE moneytree_transactions 
SET cleared_open_item_id = $1, updated_at = now() 
WHERE company_code = $2 AND id = $3";
                updateCmd.Parameters.AddWithValue(clearedId);
                updateCmd.Parameters.AddWithValue(companyCode);
                updateCmd.Parameters.AddWithValue(context.BankTransactionId);
                await updateCmd.ExecuteNonQueryAsync(ct);
            }

            return SuccessResult(new
            {
                cleared = true,
                openItemId = clearedId.ToString(),
                accountCode,
                voucherNo,
                message = Localize(context.Language, "未清項を消込しました", "已成功清账")
            });
        }

        return SuccessResult(new
        {
            cleared = false,
            openItemId,
            message = Localize(context.Language, "未清項が見つからないか、既に消込済みです", "未清项不存在或已清账")
        });
    }
}

/// <summary>
/// 标记银行交易为跳过/需要人工确认。当 AI 判断无法自动处理时使用。
/// </summary>
public sealed class SkipTransactionTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;
    public override string Name => "skip_transaction";

    public SkipTransactionTool(NpgsqlDataSource ds, ILogger<SkipTransactionTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(
        JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var transactionId = GetString(args, "transaction_id") ?? GetString(args, "transactionId") ?? "";
        var reason = GetString(args, "reason") ?? "AI could not determine the correct posting";
        var companyCode = context.CompanyCode;

        if (string.IsNullOrWhiteSpace(transactionId) || !Guid.TryParse(transactionId, out var txGuid))
            return ErrorResult("transaction_id (UUID) is required");

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE moneytree_transactions 
SET posting_status = 'needs_rule', 
    posting_error = $1, 
    posting_method = 'SkillSkipped',
    updated_at = now() 
WHERE company_code = $2 AND id = $3 AND posting_status NOT IN ('posted', 'linked')
RETURNING id";
        cmd.Parameters.AddWithValue(reason);
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(txGuid);
        var result = await cmd.ExecuteScalarAsync(ct);

        if (result is Guid)
        {
            return SuccessResult(new
            {
                skipped = true,
                transactionId,
                reason,
                message = Localize(context.Language, "この取引はスキップされました。手動確認が必要です。", "该交易已跳过，需要手动确认。")
            });
        }

        return SuccessResult(new
        {
            skipped = false,
            transactionId,
            message = Localize(context.Language, "取引が見つからないか、既に処理済みです。", "交易未找到或已处理。")
        });
    }
}
