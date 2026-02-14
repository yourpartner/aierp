using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Infrastructure.Skills;

/// <summary>
/// 查询历史凭证数据，提取供应商→科目映射、关键词→科目映射等模式。
/// 这些模式会注入到 AI 的系统提示中，让 AI 能基于历史做出更准确的判断。
/// </summary>
public sealed class HistoricalPatternService
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<HistoricalPatternService> _logger;

    public HistoricalPatternService(NpgsqlDataSource ds, ILogger<HistoricalPatternService> logger)
    {
        _ds = ds;
        _logger = logger;
    }

    /// <summary>供应商→科目使用模式</summary>
    public sealed record VendorAccountPattern(
        string AccountCode,
        string? AccountName,
        string DrCr,
        int UsageCount,
        DateTime LastUsed
    );

    /// <summary>类似金额的历史凭证摘要</summary>
    public sealed record SimilarVoucherSummary(
        string VoucherNo,
        string? Summary,
        decimal Amount,
        string PostingDate,
        string? DebitAccount,
        string? CreditAccount
    );

    /// <summary>已学习的模式（来自 ai_learned_patterns 表）</summary>
    public sealed record LearnedPattern(
        string PatternType,
        JsonObject Conditions,
        JsonObject Recommendation,
        decimal Confidence,
        int SampleCount
    );

    /// <summary>
    /// 根据供应商名称查找历史科目使用模式。
    /// 例如：过去10张「スターバックス」发票，8张用了831（会議費），2张用了835（交際費）。
    /// </summary>
    public async Task<IReadOnlyList<VendorAccountPattern>> GetVendorAccountPatternsAsync(
        string companyCode, string vendorName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(vendorName)) return Array.Empty<VendorAccountPattern>();

        var results = new List<VendorAccountPattern>();
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            // 从凭证的摘要或行备注中模糊匹配供应商名
            cmd.CommandText = @"
SELECT
  line->>'accountCode' AS account_code,
  line->>'accountName' AS account_name,
  line->>'drcr' AS drcr,
  COUNT(*) AS usage_count,
  MAX(v.created_at) AS last_used
FROM vouchers v,
     jsonb_array_elements(v.payload->'lines') AS line
WHERE v.company_code = $1
  AND (
    v.payload->'header'->>'summary' ILIKE '%' || $2 || '%'
    OR EXISTS (
      SELECT 1 FROM jsonb_array_elements(v.payload->'lines') AS l2
      WHERE l2->>'memo' ILIKE '%' || $2 || '%'
    )
  )
  AND line->>'accountCode' IS NOT NULL
GROUP BY line->>'accountCode', line->>'accountName', line->>'drcr'
ORDER BY usage_count DESC
LIMIT 8";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(vendorName);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                results.Add(new VendorAccountPattern(
                    rd.GetString(0),
                    rd.IsDBNull(1) ? null : rd.GetString(1),
                    rd.IsDBNull(2) ? "DR" : rd.GetString(2),
                    rd.GetInt32(3),
                    rd.GetDateTime(4)
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HistoricalPattern] 查询供应商科目模式失败: vendor={Vendor}", vendorName);
        }
        return results;
    }

    /// <summary>
    /// 查找金额和日期相近的历史凭证，帮助 AI 参考类似交易的处理方式。
    /// </summary>
    public async Task<IReadOnlyList<SimilarVoucherSummary>> GetSimilarVouchersAsync(
        string companyCode, decimal amount, string? description, int limit = 5, CancellationToken ct = default)
    {
        var results = new List<SimilarVoucherSummary>();
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            // 查找金额接近的凭证（±20%范围内）
            var lower = amount * 0.8m;
            var upper = amount * 1.2m;

            cmd.CommandText = @"
SELECT
  v.payload->'header'->>'voucherNo' AS voucher_no,
  v.payload->'header'->>'summary' AS summary,
  COALESCE(
    (SELECT SUM(ABS((l->>'amount')::numeric)) FROM jsonb_array_elements(v.payload->'lines') l WHERE l->>'drcr' = 'DR'),
    0
  ) AS total_amount,
  v.payload->'header'->>'postingDate' AS posting_date,
  (SELECT l->>'accountCode' FROM jsonb_array_elements(v.payload->'lines') l WHERE l->>'drcr' = 'DR' LIMIT 1) AS debit_account,
  (SELECT l->>'accountCode' FROM jsonb_array_elements(v.payload->'lines') l WHERE l->>'drcr' = 'CR' LIMIT 1) AS credit_account
FROM vouchers v
WHERE v.company_code = $1
  AND EXISTS (
    SELECT 1 FROM jsonb_array_elements(v.payload->'lines') l
    WHERE ABS((l->>'amount')::numeric) BETWEEN $2 AND $3
  )
ORDER BY v.created_at DESC
LIMIT $4";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(lower);
            cmd.Parameters.AddWithValue(upper);
            cmd.Parameters.AddWithValue(limit);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                results.Add(new SimilarVoucherSummary(
                    rd.IsDBNull(0) ? "" : rd.GetString(0),
                    rd.IsDBNull(1) ? null : rd.GetString(1),
                    rd.IsDBNull(2) ? 0m : rd.GetDecimal(2),
                    rd.IsDBNull(3) ? "" : rd.GetString(3),
                    rd.IsDBNull(4) ? null : rd.GetString(4),
                    rd.IsDBNull(5) ? null : rd.GetString(5)
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HistoricalPattern] 查询类似凭证失败: amount={Amount}", amount);
        }
        return results;
    }

    /// <summary>
    /// 查询已学习的模式（从 ai_learned_patterns 表中，按置信度降序）
    /// </summary>
    public async Task<IReadOnlyList<LearnedPattern>> GetLearnedPatternsAsync(
        string companyCode, string patternType, int limit = 10, CancellationToken ct = default)
    {
        var results = new List<LearnedPattern>();
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT pattern_type, conditions, recommendation, confidence, sample_count
FROM ai_learned_patterns
WHERE company_code = $1 AND pattern_type = $2 AND confidence >= 0.5
ORDER BY confidence DESC, sample_count DESC
LIMIT $3";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(patternType);
            cmd.Parameters.AddWithValue(limit);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var conditions = JsonNode.Parse(rd.GetString(1)) as JsonObject ?? new JsonObject();
                var recommendation = JsonNode.Parse(rd.GetString(2)) as JsonObject ?? new JsonObject();
                results.Add(new LearnedPattern(
                    rd.GetString(0),
                    conditions,
                    recommendation,
                    rd.GetDecimal(3),
                    rd.GetInt32(4)
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HistoricalPattern] 查询已学习模式失败: type={Type}", patternType);
        }
        return results;
    }

    /// <summary>
    /// 按品类/关键词查询已学习的科目映射模式。
    /// 例如：过去的"駐車料金"都用了旅費交通費。
    /// 支持模糊匹配（ILIKE），使得"駐車"能匹配到"駐車料金"的模式。
    /// </summary>
    public async Task<IReadOnlyList<LearnedPattern>> GetCategoryPatternsAsync(
        string companyCode, string category, int limit = 5, CancellationToken ct = default)
    {
        var results = new List<LearnedPattern>();
        if (string.IsNullOrWhiteSpace(category)) return results;
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT pattern_type, conditions, recommendation, confidence, sample_count
FROM ai_learned_patterns
WHERE company_code = $1
  AND pattern_type = 'category_account'
  AND (
    conditions->>'category' ILIKE '%' || $2 || '%'
    OR $2 ILIKE '%' || (conditions->>'category') || '%'
  )
  AND confidence >= 0.5
ORDER BY confidence DESC, sample_count DESC
LIMIT $3";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(category);
            cmd.Parameters.AddWithValue(limit);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var conditions = JsonNode.Parse(rd.GetString(1)) as JsonObject ?? new JsonObject();
                var recommendation = JsonNode.Parse(rd.GetString(2)) as JsonObject ?? new JsonObject();
                results.Add(new LearnedPattern(
                    rd.GetString(0),
                    conditions,
                    recommendation,
                    rd.GetDecimal(3),
                    rd.GetInt32(4)
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HistoricalPattern] 查询品类模式失败: category={Category}", category);
        }
        return results;
    }

    /// <summary>
    /// 根据费用品类关键词查询历史凭证中实际使用的科目模式。
    /// 直接从 vouchers 表中基于摘要/备注的关键词匹配提取。
    /// </summary>
    public async Task<IReadOnlyList<(string DebitAccount, string? DebitAccountName, string CreditAccount, string? CreditAccountName, int Count)>> GetCategoryBookingTemplatesAsync(
        string companyCode, string categoryKeyword, CancellationToken ct = default)
    {
        var results = new List<(string, string?, string, string?, int)>();
        if (string.IsNullOrWhiteSpace(categoryKeyword)) return results;
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
WITH matched AS (
  SELECT v.payload
  FROM vouchers v
  WHERE v.company_code = $1
    AND (
      v.payload->'header'->>'summary' ILIKE '%' || $2 || '%'
      OR EXISTS (
        SELECT 1 FROM jsonb_array_elements(v.payload->'lines') l
        WHERE l->>'memo' ILIKE '%' || $2 || '%'
           OR l->>'note' ILIKE '%' || $2 || '%'
           OR l->>'accountName' ILIKE '%' || $2 || '%'
      )
    )
  ORDER BY v.created_at DESC
  LIMIT 50
)
SELECT
  dr.account_code AS debit_code,
  dr.account_name AS debit_name,
  cr.account_code AS credit_code,
  cr.account_name AS credit_name,
  COUNT(*) AS cnt
FROM matched m,
  LATERAL (
    SELECT l->>'accountCode' AS account_code, l->>'accountName' AS account_name
    FROM jsonb_array_elements(m.payload->'lines') l
    WHERE l->>'drcr' = 'DR'
    LIMIT 1
  ) dr,
  LATERAL (
    SELECT l->>'accountCode' AS account_code, l->>'accountName' AS account_name
    FROM jsonb_array_elements(m.payload->'lines') l
    WHERE l->>'drcr' = 'CR'
    LIMIT 1
  ) cr
WHERE dr.account_code IS NOT NULL AND cr.account_code IS NOT NULL
GROUP BY dr.account_code, dr.account_name, cr.account_code, cr.account_name
ORDER BY cnt DESC
LIMIT 5";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(categoryKeyword);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                results.Add((
                    rd.GetString(0),
                    rd.IsDBNull(1) ? null : rd.GetString(1),
                    rd.GetString(2),
                    rd.IsDBNull(3) ? null : rd.GetString(3),
                    rd.GetInt32(4)
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HistoricalPattern] 查询品类记账模板失败: keyword={Keyword}", categoryKeyword);
        }
        return results;
    }

    // ====================== 银行明细记账专用查询 ======================

    /// <summary>银行交易→科目 学习模式</summary>
    public sealed record BankTransactionPattern(
        string AccountCode,
        string? AccountName,
        int UsageCount
    );

    /// <summary>
    /// 从已记账的银行交易中学习：根据摘要关键词找到历史上使用的科目。
    /// 对应原 MoneytreePostingService.LearnFromBankLinkedVouchersAsync 的逻辑。
    /// </summary>
    public async Task<IReadOnlyList<BankTransactionPattern>> GetBankDescriptionPatternsAsync(
        string companyCode, string description, bool isWithdrawal, int limit = 5, CancellationToken ct = default)
    {
        var results = new List<BankTransactionPattern>();
        if (string.IsNullOrWhiteSpace(description)) return results;

        // 从摘要中提取对手方名称（去掉"振込 "前缀等）
        var searchTerm = ExtractBankSearchTerm(description);
        if (string.IsNullOrWhiteSpace(searchTerm)) return results;

        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            // 出金学习借方科目、入金学习贷方科目
            var targetDrcr = isWithdrawal ? "DR" : "CR";
            cmd.CommandText = @"
SELECT
  line->>'accountCode' AS account_code,
  line->>'accountName' AS account_name,
  COUNT(*) AS usage_count
FROM vouchers v
INNER JOIN moneytree_transactions mt ON mt.voucher_id = v.id AND mt.company_code = v.company_code
, jsonb_array_elements(v.payload->'lines') AS line
WHERE v.company_code = $1
  AND v.created_at > now() - interval '1 year'
  AND mt.description ILIKE '%' || $2 || '%'
  AND line->>'drcr' = $3
  AND line->>'accountCode' IS NOT NULL
  AND (
    -- 排除银行和应收类科目(11xx现金/銀行、12xx売掛金), 保留13xx(仮払金)、14xx(仮払消費税)等
    NOT (line->>'accountCode') LIKE '1%'
    OR (line->>'accountCode') LIKE '13%'
    OR (line->>'accountCode') LIKE '14%'
    OR (line->>'accountCode') LIKE '15%'
  )
GROUP BY line->>'accountCode', line->>'accountName'
ORDER BY usage_count DESC
LIMIT $4";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(searchTerm);
            cmd.Parameters.AddWithValue(targetDrcr);
            cmd.Parameters.AddWithValue(limit);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                results.Add(new BankTransactionPattern(
                    rd.GetString(0),
                    rd.IsDBNull(1) ? null : rd.GetString(1),
                    rd.GetInt32(2)
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HistoricalPattern] 银行摘要模式查询失败: desc={Desc}", description);
        }
        return results;
    }

    /// <summary>
    /// 按对手方ID查询银行交易中历史使用的科目模式。
    /// </summary>
    public async Task<IReadOnlyList<BankTransactionPattern>> GetBankCounterpartyPatternsAsync(
        string companyCode, string counterpartyKind, string counterpartyId, bool isWithdrawal, int limit = 5, CancellationToken ct = default)
    {
        var results = new List<BankTransactionPattern>();
        if (string.IsNullOrWhiteSpace(counterpartyId)) return results;

        var jsonPath = counterpartyKind.ToLowerInvariant() switch
        {
            "customer" => "customerId",
            "vendor" => "vendorId",
            "employee" => "employeeId",
            _ => null
        };
        if (jsonPath == null) return results;

        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            var targetDrcr = isWithdrawal ? "DR" : "CR";
            cmd.CommandText = $@"
SELECT
  line->>'accountCode' AS account_code,
  line->>'accountName' AS account_name,
  COUNT(*) AS usage_count
FROM vouchers v
INNER JOIN moneytree_transactions mt ON mt.voucher_id = v.id AND mt.company_code = v.company_code
, jsonb_array_elements(v.payload->'lines') AS line
WHERE v.company_code = $1
  AND v.created_at > now() - interval '1 year'
  AND line->>'drcr' = $2
  AND line->>'accountCode' IS NOT NULL
  AND line->>'{jsonPath}' = $3
  AND (
    NOT (line->>'accountCode') LIKE '1%'
    OR (line->>'accountCode') LIKE '13%'
    OR (line->>'accountCode') LIKE '14%'
    OR (line->>'accountCode') LIKE '15%'
  )
GROUP BY line->>'accountCode', line->>'accountName'
ORDER BY usage_count DESC
LIMIT $4";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(targetDrcr);
            cmd.Parameters.AddWithValue(counterpartyId);
            cmd.Parameters.AddWithValue(limit);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                results.Add(new BankTransactionPattern(
                    rd.GetString(0),
                    rd.IsDBNull(1) ? null : rd.GetString(1),
                    rd.GetInt32(2)
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HistoricalPattern] 银行对手方模式查询失败: kind={Kind}, id={Id}", counterpartyKind, counterpartyId);
        }
        return results;
    }

    /// <summary>
    /// 从银行摘要中提取搜索关键词（去掉"振込 "前缀、常见噪音词等）
    /// </summary>
    private static string ExtractBankSearchTerm(string description)
    {
        var s = description.Trim();
        // 常见前缀去除
        var prefixes = new[] { "振込 ", "振込　", "振替 ", "振替　", "ﾌﾘｺﾐ ", "入金 ", "出金 " };
        foreach (var p in prefixes)
        {
            if (s.StartsWith(p, StringComparison.Ordinal))
            {
                s = s[p.Length..].Trim();
                break;
            }
        }
        // 如果太短就返回空
        return s.Length >= 2 ? s : string.Empty;
    }

    /// <summary>
    /// 查询某个供应商最近 N 次被记账的完整科目组合（借方+贷方），
    /// 用于直接复制历史记账模式。
    /// </summary>
    public async Task<IReadOnlyList<(string DebitAccount, string CreditAccount, string? Summary, int Count)>> GetVendorBookingTemplatesAsync(
        string companyCode, string vendorName, CancellationToken ct = default)
    {
        var results = new List<(string, string, string?, int)>();
        if (string.IsNullOrWhiteSpace(vendorName)) return results;
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
WITH matched_vouchers AS (
  SELECT v.id, v.payload
  FROM vouchers v
  WHERE v.company_code = $1
    AND v.payload->'header'->>'summary' ILIKE '%' || $2 || '%'
  ORDER BY v.created_at DESC
  LIMIT 30
)
SELECT
  (SELECT l->>'accountCode' FROM jsonb_array_elements(mv.payload->'lines') l WHERE l->>'drcr' = 'DR' LIMIT 1) AS debit_account,
  (SELECT l->>'accountCode' FROM jsonb_array_elements(mv.payload->'lines') l WHERE l->>'drcr' = 'CR' LIMIT 1) AS credit_account,
  mv.payload->'header'->>'summary' AS summary,
  COUNT(*) AS cnt
FROM matched_vouchers mv
GROUP BY debit_account, credit_account, summary
ORDER BY cnt DESC
LIMIT 5";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(vendorName);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                if (rd.IsDBNull(0) || rd.IsDBNull(1)) continue;
                results.Add((
                    rd.GetString(0),
                    rd.GetString(1),
                    rd.IsDBNull(2) ? null : rd.GetString(2),
                    rd.GetInt32(3)
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HistoricalPattern] 查询供应商记账模板失败: vendor={Vendor}", vendorName);
        }
        return results;
    }
}
