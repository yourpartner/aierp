using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Infrastructure.Skills;

/// <summary>
/// 采集 AI 执行结果和用户修正行为，用于后续的自学习。
/// </summary>
public sealed class LearningEventCollector
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<LearningEventCollector> _logger;

    public LearningEventCollector(NpgsqlDataSource ds, ILogger<LearningEventCollector> logger)
    {
        _ds = ds;
        _logger = logger;
    }

    /// <summary>
    /// 记录 AI 创建凭证的事件（调用 create_voucher 后）
    /// </summary>
    public async Task RecordVoucherCreationAsync(
        string companyCode,
        string? sessionId,
        string? skillId,
        JsonObject context,
        JsonObject aiOutput,
        CancellationToken ct = default)
    {
        try
        {
            await InsertEventAsync(companyCode, "ai_voucher_created", sessionId, skillId, context, aiOutput, null, "pending_review", ct);
            _logger.LogDebug("[Learning] 记录 AI 凭证创建事件: company={Company}, skill={Skill}", companyCode, skillId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Learning] 记录凭证创建事件失败");
        }
    }

    /// <summary>
    /// 记录用户修正 AI 创建的凭证的事件
    /// </summary>
    public async Task RecordUserCorrectionAsync(
        string companyCode,
        Guid voucherId,
        JsonObject originalData,
        JsonObject correctedData,
        CancellationToken ct = default)
    {
        try
        {
            var context = new JsonObject
            {
                ["voucherId"] = voucherId.ToString(),
                ["original"] = originalData
            };
            var userAction = new JsonObject
            {
                ["corrected"] = correctedData,
                ["correctedAt"] = DateTimeOffset.UtcNow.ToString("O")
            };
            await InsertEventAsync(companyCode, "user_voucher_correction", null, null, context, null, userAction, "correction", ct);
            _logger.LogInformation("[Learning] 记录用户修正事件: company={Company}, voucher={VoucherId}", companyCode, voucherId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Learning] 记录用户修正事件失败");
        }
    }

    /// <summary>
    /// 确认 AI 的判断正确（凭证未被修改，过了一段时间后由后台任务调用）
    /// </summary>
    public async Task ConfirmAiDecisionAsync(
        string companyCode,
        Guid voucherId,
        CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE ai_learning_events
SET outcome = 'confirmed', updated_at = now()
WHERE company_code = $1
  AND event_type = 'ai_voucher_created'
  AND outcome = 'pending_review'
  AND context->>'voucherId' = $2";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(voucherId.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Learning] 确认 AI 决策失败");
        }
    }

    /// <summary>
    /// 基于已收集的学习事件，更新/创建 ai_learned_patterns 记录。
    /// 这个方法会分析「同一供应商→AI使用的科目→用户是否修正」的统计数据。
    /// </summary>
    public async Task ConsolidatePatternsAsync(string companyCode, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);

            // 从 confirmed 的事件中提取供应商→科目映射
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO ai_learned_patterns (company_code, pattern_type, conditions, recommendation, confidence, sample_count, last_updated_at)
SELECT
  $1,
  'vendor_account',
  jsonb_build_object('vendorName', context->>'vendorName'),
  jsonb_build_object(
    'debitAccount', ai_output->>'debitAccount',
    'creditAccount', ai_output->>'creditAccount',
    'summary', ai_output->>'summary'
  ),
  CASE
    WHEN COUNT(*) >= 10 THEN 0.95
    WHEN COUNT(*) >= 5 THEN 0.85
    WHEN COUNT(*) >= 3 THEN 0.75
    ELSE 0.6
  END,
  COUNT(*),
  now()
FROM ai_learning_events
WHERE company_code = $1
  AND event_type = 'ai_voucher_created'
  AND outcome = 'confirmed'
  AND context->>'vendorName' IS NOT NULL
  AND ai_output->>'debitAccount' IS NOT NULL
GROUP BY context->>'vendorName', ai_output->>'debitAccount', ai_output->>'creditAccount', ai_output->>'summary'
HAVING COUNT(*) >= 2
ON CONFLICT (company_code, pattern_type, conditions)
DO UPDATE SET
  recommendation = EXCLUDED.recommendation,
  confidence = EXCLUDED.confidence,
  sample_count = EXCLUDED.sample_count,
  last_updated_at = now()";
            cmd.Parameters.AddWithValue(companyCode);
            var affected = await cmd.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("[Learning] 模式凝练完成: company={Company}, patterns={Count}", companyCode, affected);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Learning] 模式凝练失败: company={Company}", companyCode);
        }
    }

    private async Task InsertEventAsync(
        string companyCode,
        string eventType,
        string? sessionId,
        string? skillId,
        JsonObject context,
        JsonObject? aiOutput,
        JsonObject? userAction,
        string outcome,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO ai_learning_events (company_code, event_type, session_id, skill_id, context, ai_output, user_action, outcome)
VALUES ($1, $2, $3, $4, $5::jsonb, $6::jsonb, $7::jsonb, $8)";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(eventType);
        cmd.Parameters.AddWithValue((object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)skillId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(context.ToJsonString());
        cmd.Parameters.AddWithValue((object?)aiOutput?.ToJsonString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)userAction?.ToJsonString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue(outcome);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
