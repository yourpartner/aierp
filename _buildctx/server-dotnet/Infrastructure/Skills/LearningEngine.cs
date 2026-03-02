using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Infrastructure.Skills;

/// <summary>
/// 学习引擎 — 从历史操作中提取规律、自动优化 AI Prompt
/// 在 LearningEventCollector 收集的事件数据上做高层分析
/// </summary>
public class LearningEngine
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<LearningEngine> _logger;
    private readonly LearningEventCollector _collector;

    public LearningEngine(
        NpgsqlDataSource ds,
        ILogger<LearningEngine> logger,
        LearningEventCollector collector)
    {
        _ds = ds;
        _logger = logger;
        _collector = collector;
    }

    // ==================== 1. 修正模式分析 ====================

    /// <summary>
    /// 分析用户修正模式，生成"修正规则"提示词片段
    /// 输出可直接注入到 AI System Prompt 中
    /// </summary>
    public async Task<List<CorrectionRule>> AnalyzeCorrectionPatternsAsync(
        string companyCode, CancellationToken ct)
    {
        var rules = new List<CorrectionRule>();

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT 
    context->>'vendorName' as vendor,
    ai_output->>'debitAccount' as ai_debit,
    user_action->'corrected'->>'debitAccount' as corrected_debit,
    ai_output->>'creditAccount' as ai_credit,
    user_action->'corrected'->>'creditAccount' as corrected_credit,
    COUNT(*) as times
FROM ai_learning_events
WHERE company_code = $1
  AND event_type = 'user_voucher_correction'
  AND user_action IS NOT NULL
GROUP BY 1, 2, 3, 4, 5
HAVING COUNT(*) >= 2
ORDER BY COUNT(*) DESC
LIMIT 50";
        cmd.Parameters.AddWithValue(companyCode);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var vendor = reader.IsDBNull(0) ? null : reader.GetString(0);
            var aiDebit = reader.IsDBNull(1) ? null : reader.GetString(1);
            var correctedDebit = reader.IsDBNull(2) ? null : reader.GetString(2);
            var aiCredit = reader.IsDBNull(3) ? null : reader.GetString(3);
            var correctedCredit = reader.IsDBNull(4) ? null : reader.GetString(4);
            var times = reader.GetInt64(5);

            if (correctedDebit != null && correctedDebit != aiDebit)
            {
                rules.Add(new CorrectionRule
                {
                    Vendor = vendor,
                    Field = "debitAccount",
                    WrongValue = aiDebit,
                    CorrectValue = correctedDebit,
                    Occurrences = (int)times,
                    Confidence = times >= 5 ? 0.95 : times >= 3 ? 0.85 : 0.7
                });
            }
            if (correctedCredit != null && correctedCredit != aiCredit)
            {
                rules.Add(new CorrectionRule
                {
                    Vendor = vendor,
                    Field = "creditAccount",
                    WrongValue = aiCredit,
                    CorrectValue = correctedCredit,
                    Occurrences = (int)times,
                    Confidence = times >= 5 ? 0.95 : times >= 3 ? 0.85 : 0.7
                });
            }
        }

        return rules;
    }

    /// <summary>
    /// 将修正规则生成为可注入 System Prompt 的文本
    /// </summary>
    public async Task<string> BuildCorrectionPromptAsync(string companyCode, CancellationToken ct)
    {
        var rules = await AnalyzeCorrectionPatternsAsync(companyCode, ct);
        if (rules.Count == 0) return "";

        var lines = new List<string> { "### 历史修正规则（基于用户修正模式，请严格遵守）：" };
        foreach (var rule in rules.Where(r => r.Confidence >= 0.8))
        {
            var vendorClause = string.IsNullOrEmpty(rule.Vendor) ? "" : $"（供应商: {rule.Vendor}）";
            lines.Add($"- {vendorClause}{rule.Field}: 使用 `{rule.CorrectValue}` 而不是 `{rule.WrongValue}`（修正{rule.Occurrences}次，置信度{rule.Confidence:P0}）");
        }
        return string.Join("\n", lines);
    }

    // ==================== 2. 意图识别增强 ====================

    /// <summary>
    /// 基于历史成功路由记录，为意图分类提供额外信号
    /// </summary>
    public async Task<Dictionary<string, double>> GetIntentBoostsAsync(
        string companyCode, string userId, CancellationToken ct)
    {
        var boosts = new Dictionary<string, double>();

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT skill_id, COUNT(*) as cnt
FROM ai_learning_events
WHERE company_code = $1
  AND context->>'userId' = $2
  AND outcome IN ('confirmed', 'pending_review')
  AND created_at > now() - interval '30 days'
GROUP BY skill_id
ORDER BY COUNT(*) DESC
LIMIT 10";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var total = 0L;
        var counts = new List<(string skill, long count)>();
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0)) continue;
            var skill = reader.GetString(0);
            var count = reader.GetInt64(1);
            counts.Add((skill, count));
            total += count;
        }

        if (total > 0)
        {
            foreach (var (skill, count) in counts)
            {
                boosts[skill] = (double)count / total;
            }
        }

        return boosts;
    }

    // ==================== 3. 综合学习分析 ====================

    /// <summary>
    /// 生成学习分析报告
    /// </summary>
    public async Task<LearningReport> GenerateReportAsync(string companyCode, CancellationToken ct)
    {
        var report = new LearningReport { CompanyCode = companyCode };

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // 总事件数
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT 
    COUNT(*) FILTER (WHERE event_type = 'ai_voucher_created') as total_created,
    COUNT(*) FILTER (WHERE outcome = 'confirmed') as confirmed,
    COUNT(*) FILTER (WHERE event_type = 'user_voucher_correction') as corrections
FROM ai_learning_events
WHERE company_code = $1 AND created_at > now() - interval '30 days'";
            cmd.Parameters.AddWithValue(companyCode);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                report.TotalCreated = r.GetInt64(0);
                report.Confirmed = r.GetInt64(1);
                report.Corrections = r.GetInt64(2);
            }
        }

        // 准确率
        if (report.TotalCreated > 0)
        {
            report.AccuracyRate = (double)report.Confirmed / report.TotalCreated;
        }

        // 学习模式数
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT COUNT(*) FROM ai_learned_patterns
WHERE company_code = $1 AND confidence >= 0.75";
            cmd.Parameters.AddWithValue(companyCode);
            var patternCount = await cmd.ExecuteScalarAsync(ct);
            report.LearnedPatterns = Convert.ToInt64(patternCount ?? 0L);
        }

        return report;
    }
}

public class CorrectionRule
{
    public string? Vendor { get; set; }
    public string Field { get; set; } = "";
    public string? WrongValue { get; set; }
    public string? CorrectValue { get; set; }
    public int Occurrences { get; set; }
    public double Confidence { get; set; }
}

public class LearningReport
{
    public string CompanyCode { get; set; } = "";
    public long TotalCreated { get; set; }
    public long Confirmed { get; set; }
    public long Corrections { get; set; }
    public double AccuracyRate { get; set; }
    public long LearnedPatterns { get; set; }
}
