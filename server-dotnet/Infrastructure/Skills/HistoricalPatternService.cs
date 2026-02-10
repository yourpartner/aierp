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
