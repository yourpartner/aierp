using System.Linq;
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
        s = NormalizeBankKana(s);
        foreach (var t in CorpTokens) s = s.Replace(t.ToUpperInvariant(), " ");
        foreach (var w in StopWords) s = s.Replace(w.ToUpperInvariant(), " ");
        s = NoiseRegex.Replace(s, " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static string NormalizeBankKana(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                'ァ' => 'ア', 'ィ' => 'イ', 'ゥ' => 'ウ', 'ェ' => 'エ', 'ォ' => 'オ',
                'ッ' => 'ツ', 'ャ' => 'ヤ', 'ュ' => 'ユ', 'ョ' => 'ヨ', 'ヮ' => 'ワ',
                'ヵ' => 'カ', 'ヶ' => 'ケ',
                'ガ' => 'カ', 'ギ' => 'キ', 'グ' => 'ク', 'ゲ' => 'ケ', 'ゴ' => 'コ',
                'ザ' => 'サ', 'ジ' => 'シ', 'ズ' => 'ス', 'ゼ' => 'セ', 'ゾ' => 'ソ',
                'ダ' => 'タ', 'ヂ' => 'チ', 'ヅ' => 'ツ', 'デ' => 'テ', 'ド' => 'ト',
                'バ' => 'ハ', 'ビ' => 'ヒ', 'ブ' => 'フ', 'ベ' => 'ヘ', 'ボ' => 'ホ',
                'パ' => 'ハ', 'ピ' => 'ヒ', 'プ' => 'フ', 'ペ' => 'ヘ', 'ポ' => 'ホ',
                'ヴ' => 'ウ',
                'ぁ' => 'あ', 'ぃ' => 'い', 'ぅ' => 'う', 'ぇ' => 'え', 'ぉ' => 'お',
                'っ' => 'つ', 'ゃ' => 'や', 'ゅ' => 'ゆ', 'ょ' => 'よ', 'ゎ' => 'わ',
                'が' => 'か', 'ぎ' => 'き', 'ぐ' => 'く', 'げ' => 'け', 'ご' => 'こ',
                'ざ' => 'さ', 'じ' => 'し', 'ず' => 'す', 'ぜ' => 'せ', 'ぞ' => 'そ',
                'だ' => 'た', 'ぢ' => 'ち', 'づ' => 'つ', 'で' => 'て', 'ど' => 'と',
                'ば' => 'は', 'び' => 'ひ', 'ぶ' => 'ふ', 'べ' => 'へ', 'ぼ' => 'ほ',
                'ぱ' => 'は', 'ぴ' => 'ひ', 'ぷ' => 'ふ', 'ぺ' => 'へ', 'ぽ' => 'ほ',
                _ => c
            });
        }
        return sb.ToString();
    }

    private static double SimilarityScore(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (string.IsNullOrWhiteSpace(na) || string.IsNullOrWhiteSpace(nb)) return 0;
        if (string.Equals(na, nb, StringComparison.OrdinalIgnoreCase)) return 1.0;
        var shorter = na.Length <= nb.Length ? na : nb;
        var longer = na.Length > nb.Length ? na : nb;
        if (longer.Contains(shorter, StringComparison.OrdinalIgnoreCase)
            && shorter.Length >= 3
            && (double)shorter.Length / longer.Length >= 0.4)
            return 0.85;
        var tokensA = na.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokensB = nb.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokensA.Length == 0 || tokensB.Length == 0) return 0;
        var overlap = tokensA.Count(t => t.Length >= 2 && tokensB.Any(tb =>
            tb.Length >= 2 && TokenMatch(t, tb)));
        return (double)overlap / Math.Max(tokensA.Length, tokensB.Length);
    }

    private static bool TokenMatch(string t1, string t2)
    {
        if (string.Equals(t1, t2, StringComparison.OrdinalIgnoreCase)) return true;
        var s = t1.Length <= t2.Length ? t1 : t2;
        var l = t1.Length > t2.Length ? t1 : t2;
        if (l.Contains(s, StringComparison.OrdinalIgnoreCase)
            && s.Length >= 2
            && (double)s.Length / l.Length >= 0.6)
            return true;
        return false;
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
/// 搜索未清项（应收/应付），用于银行入出金的清账匹配。
/// counterparty_id 可选：有则按对手方+金额+日期搜索，无则只按金额+日期搜索。
/// fee_amount 可选：有则同时用 amount 和 amount+fee_amount 两个金额匹配。
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
        var counterpartyId = GetString(args, "counterparty_id") ?? GetString(args, "counterpartyId");
        var amountVal = GetDecimal(args, "amount");
        var feeAmountVal = GetDecimal(args, "fee_amount") ?? GetDecimal(args, "feeAmount");
        var transactionType = GetString(args, "transaction_type") ?? GetString(args, "transactionType") ?? "withdrawal";
        var transactionDate = GetString(args, "transaction_date") ?? GetString(args, "transactionDate");
        var companyCode = context.CompanyCode;

        var isWithdrawal = string.Equals(transactionType, "withdrawal", StringComparison.OrdinalIgnoreCase);
        var amount = amountVal ?? 0m;
        var feeAmount = feeAmountVal ?? 0m;
        var totalAmount = amount + feeAmount;
        var txDate = DateTime.TryParse(transactionDate, out var pd) ? pd : DateTime.Today;
        var targetDirection = isWithdrawal ? "CR" : "DR";

        if (amount <= 0)
            return ErrorResult(Localize(context.Language, "amount は必須です（正の数値）", "amount 为必填项（正数）"));

        await using var conn = await _ds.OpenConnectionAsync(ct);

        var hasCounterparty = !string.IsNullOrWhiteSpace(counterpartyId);
        string? partnerIdForQuery = null;

        if (hasCounterparty)
        {
            partnerIdForQuery = counterpartyId;
            if (!Guid.TryParse(counterpartyId, out _))
            {
                await using var resolveCmd = conn.CreateCommand();
                resolveCmd.CommandText = "SELECT id FROM employees WHERE company_code = $1 AND employee_code = $2 LIMIT 1";
                resolveCmd.Parameters.AddWithValue(companyCode);
                resolveCmd.Parameters.AddWithValue(counterpartyId!);
                var resolved = await resolveCmd.ExecuteScalarAsync(ct);
                if (resolved is Guid g)
                    partnerIdForQuery = g.ToString();
            }
        }

        // 构建查询：有对手方 → 按 partner_id 过滤；无对手方 → 仅按金额+日期
        await using var cmd = conn.CreateCommand();
        var partnerFilter = hasCounterparty ? "AND oi.partner_id = $5" : "";
        cmd.CommandText = $@"
WITH oi_with_detail AS (
    SELECT oi.id, oi.account_code, oi.residual_amount, oi.doc_date, 
           v.voucher_no, oi.partner_id,
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
      AND oi.cleared_flag = false
      AND ABS(oi.residual_amount) > 0.01
      {partnerFilter}
)
SELECT id, account_code, residual_amount, doc_date, voucher_no, drcr, effective_date, partner_id
FROM oi_with_detail
WHERE drcr = $2
  AND effective_date <= ($3::date + interval '5 day')
  AND effective_date >= ($3::date - interval '180 day')
ORDER BY ABS(ABS(residual_amount) - $4) ASC, ABS(effective_date - $3::date) ASC
LIMIT 20";
        cmd.Parameters.AddWithValue(companyCode);       // $1
        cmd.Parameters.AddWithValue(targetDirection);    // $2
        cmd.Parameters.AddWithValue(txDate);             // $3
        cmd.Parameters.AddWithValue(amount);             // $4
        if (hasCounterparty)
        {
            cmd.Parameters.AddWithValue(partnerIdForQuery!); // $5 text (open_items.partner_id is TEXT)
        }

        var items = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var residual = reader.GetDecimal(2);
            var diffNet = Math.Abs(Math.Abs(residual) - amount);
            var diffTotal = feeAmount > 0 ? Math.Abs(Math.Abs(residual) - totalAmount) : decimal.MaxValue;
            var bestDiff = Math.Min(diffNet, diffTotal);
            var matchedAmount = diffTotal < diffNet ? "total" : "net";

            items.Add(new
            {
                openItemId = reader.GetGuid(0).ToString(),
                accountCode = reader.GetString(1),
                residualAmount = residual,
                docDate = reader.GetDateTime(3).ToString("yyyy-MM-dd"),
                voucherNo = reader.IsDBNull(4) ? null : reader.GetString(4),
                direction = reader.GetString(5),
                effectiveDate = reader.GetDateTime(6).ToString("yyyy-MM-dd"),
                partnerId = reader.IsDBNull(7) ? null : reader.GetGuid(7).ToString(),
                amountDifference = bestDiff,
                matchedAmount,
                isExactMatch = bestDiff < 0.01m,
                isCloseMatch = bestDiff <= Math.Min(amount * 0.05m, 1000m),
                feeIncluded = matchedAmount == "total" && diffTotal < 0.01m
            });
        }

        // 无对手方时只返回精确匹配或近似匹配（避免歧义）
        if (!hasCounterparty)
        {
            var exactItems = items.Where(i => ((dynamic)i).isExactMatch == true).ToList();
            if (exactItems.Count > 0) items = exactItems;
            else items = items.Where(i => ((dynamic)i).isCloseMatch == true).Take(5).ToList();
        }

        return SuccessResult(new
        {
            counterpartyId = partnerIdForQuery,
            hasCounterparty,
            transactionType,
            searchAmount = amount,
            feeAmount,
            totalAmount = feeAmount > 0 ? totalAmount : (decimal?)null,
            totalFound = items.Count,
            items,
            hint = items.Count == 0
                ? Localize(context.Language,
                    "該当する未清項が見つかりませんでした。search_historical_patterns で過去の記帳パターンを参照するか、適切な科目を選択してください。",
                    "未找到匹配的未清项。请使用 search_historical_patterns 参照历史记账模式，或自行选择合适科目。")
                : feeAmount > 0
                    ? Localize(context.Language,
                        "上記の未清項から最適なものを選んでください。feeIncluded=true の場合は手数料先方負担（振込金額=未清項金額）、false の場合は手数料当方負担です。",
                        "请选择最合适的未清项。feeIncluded=true 表示手续费由对方承担，false 表示手续费由我方承担。")
                    : Localize(context.Language,
                        "上記の未清項から最適なものを選んで清账してください。isExactMatch=true の項目を優先してください。",
                        "请选择最合适的未清项进行清账。优先选择 isExactMatch=true 的项目。")
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
                if (!root.TryGetProperty("bankInfo", out var bankInfo) &&
                    !root.TryGetProperty("bank", out bankInfo)) continue;
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

        // 暫定科目を名前で動的解決（仮払金・仮受金等）→ 学習パターン除外用
        var suspenseCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        {
            await using var scCmd = conn.CreateCommand();
            scCmd.CommandText = @"SELECT account_code FROM accounts WHERE company_code = $1
                AND (payload->>'name' LIKE '%仮払金%' OR payload->>'name' LIKE '%仮受金%')";
            scCmd.Parameters.AddWithValue(companyCode);
            await using var scReader = await scCmd.ExecuteReaderAsync(ct);
            while (await scReader.ReadAsync(ct))
                suspenseCodes.Add(scReader.GetString(0));
        }

        // 1. ai_learned_patterns から学習済みパターンを検索
        var patterns = new List<object>();
        await using var patternCmd = conn.CreateCommand();
        if (suspenseCodes.Count > 0)
        {
            var placeholders = string.Join(",", suspenseCodes.Select((_, i) => $"${i + 2}"));
            patternCmd.CommandText = $@"
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
  AND recommendation->>'debitAccount' NOT IN ({placeholders})
ORDER BY confidence DESC
LIMIT 50";
            patternCmd.Parameters.AddWithValue(companyCode);
            foreach (var sc in suspenseCodes) patternCmd.Parameters.AddWithValue(sc);
        }
        else
        {
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
        }

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

        // 2. 過去の銀行明細から同様の取引の記帳実績を検索（暫定科目を除外）
        var historicalVouchers = new List<object>();
        await using var histCmd = conn.CreateCommand();
        if (suspenseCodes.Count > 0)
        {
            var hPlaceholders = string.Join(",", suspenseCodes.Select((_, i) => $"${i + 2}"));
            histCmd.CommandText = $@"
SELECT mt.description, v.voucher_no, v.payload->'lines' as lines
FROM moneytree_transactions mt
JOIN vouchers v ON v.id = mt.voucher_id AND v.company_code = mt.company_code
WHERE mt.company_code = $1 AND mt.posting_status = 'posted' AND mt.voucher_id IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 FROM jsonb_array_elements(v.payload->'lines') AS line
    WHERE line->>'accountCode' IN ({hPlaceholders})
      AND COALESCE(line->>'isTaxLine','false') = 'false'
  )
ORDER BY mt.updated_at DESC
LIMIT 200";
            histCmd.Parameters.AddWithValue(companyCode);
            foreach (var sc in suspenseCodes) histCmd.Parameters.AddWithValue(sc);
        }
        else
        {
            histCmd.CommandText = @"
SELECT mt.description, v.voucher_no, v.payload->'lines' as lines
FROM moneytree_transactions mt
JOIN vouchers v ON v.id = mt.voucher_id AND v.company_code = mt.company_code
WHERE mt.company_code = $1 AND mt.posting_status = 'posted' AND mt.voucher_id IS NOT NULL
ORDER BY mt.updated_at DESC
LIMIT 200";
            histCmd.Parameters.AddWithValue(companyCode);
        }

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
        var clearingAmountVal = GetDecimal(args, "clearing_amount") ?? GetDecimal(args, "clearingAmount");
        var companyCode = context.CompanyCode;

        if (!Guid.TryParse(openItemId, out var oiGuid))
            return ErrorResult("open_item_id (UUID) is required");

        if (string.IsNullOrWhiteSpace(voucherNo))
            return ErrorResult("voucher_no is required");

        await using var conn = await _ds.OpenConnectionAsync(ct);

        Guid? voucherId = null;
        await using var vCmd = conn.CreateCommand();
        vCmd.CommandText = "SELECT id FROM vouchers WHERE company_code = $1 AND voucher_no = $2 LIMIT 1";
        vCmd.Parameters.AddWithValue(companyCode);
        vCmd.Parameters.AddWithValue(voucherNo);
        var vid = await vCmd.ExecuteScalarAsync(ct);
        if (vid is Guid g) voucherId = g;

        if (!voucherId.HasValue)
            return ErrorResult(Localize(context.Language, $"伝票 {voucherNo} が見つかりません", $"凭证 {voucherNo} 不存在"));

        // clearing_amount 指定时部分清账，未指定时全额清账
        await using var clearCmd = conn.CreateCommand();
        if (clearingAmountVal.HasValue && clearingAmountVal.Value > 0)
        {
            var clearAmt = clearingAmountVal.Value;
            clearCmd.CommandText = @"
UPDATE open_items 
SET residual_amount = GREATEST(residual_amount - $4, 0),
    cleared_flag = (residual_amount - $4) <= 0.00001,
    cleared_at = CASE WHEN (residual_amount - $4) <= 0.00001 THEN now() ELSE cleared_at END,
    cleared_by = $1, 
    updated_at = now() 
WHERE id = $2 AND company_code = $3 AND ABS(residual_amount) > 0.01
RETURNING id, account_code, residual_amount";
            clearCmd.Parameters.AddWithValue(voucherNo);
            clearCmd.Parameters.AddWithValue(oiGuid);
            clearCmd.Parameters.AddWithValue(companyCode);
            clearCmd.Parameters.AddWithValue(clearAmt);
        }
        else
        {
            clearCmd.CommandText = @"
UPDATE open_items 
SET residual_amount = 0,
    cleared_flag = true,
    cleared_at = now(),
    cleared_by = $1, 
    updated_at = now() 
WHERE id = $2 AND company_code = $3 AND ABS(residual_amount) > 0.01
RETURNING id, account_code, residual_amount";
            clearCmd.Parameters.AddWithValue(voucherNo);
            clearCmd.Parameters.AddWithValue(oiGuid);
            clearCmd.Parameters.AddWithValue(companyCode);
        }

        await using var reader = await clearCmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var clearedId = reader.GetGuid(0);
            var accountCode = reader.GetString(1);
            var newResidual = reader.GetDecimal(2);

            Logger.LogInformation("[ClearOpenItem] Cleared open item {OiId} (account={Account}) with voucher {VoucherNo}, newResidual={Residual}", 
                clearedId, accountCode, voucherNo, newResidual);

            if (!string.IsNullOrWhiteSpace(context.BankTransactionId))
            {
                await using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = @"
UPDATE moneytree_transactions 
SET cleared_open_item_id = $1, updated_at = now() 
WHERE company_code = $2 AND id = $3";
                updateCmd.Parameters.AddWithValue(clearedId);
                updateCmd.Parameters.AddWithValue(companyCode);
                if (Guid.TryParse(context.BankTransactionId, out var txGuid))
                    updateCmd.Parameters.AddWithValue(txGuid);
                else
                    updateCmd.Parameters.AddWithValue(context.BankTransactionId);
                await updateCmd.ExecuteNonQueryAsync(ct);
            }

            return SuccessResult(new
            {
                cleared = true,
                fullCleared = newResidual <= 0.01m,
                openItemId = clearedId.ToString(),
                accountCode,
                voucherNo,
                newResidualAmount = newResidual,
                message = newResidual <= 0.01m
                    ? Localize(context.Language, "未清項を全額消込しました", "已全额清账")
                    : Localize(context.Language, $"未清項を一部消込しました（残高: {newResidual:#,0}）", $"已部分清账（余额: {newResidual:#,0}）")
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
