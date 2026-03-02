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
    /// 记录对话中用户指定科目的学习事件（如：用户说"旅費交通費科目を使ってください"）
    /// 同时直接 upsert 到 ai_learned_patterns 实现即时学习
    /// </summary>
    public async Task RecordChatAccountSpecificationAsync(
        string companyCode,
        string? sessionId,
        string? category,
        string? vendorName,
        string? summary,
        string debitAccount,
        string? debitAccountName,
        string creditAccount,
        string? creditAccountName,
        CancellationToken ct = default)
    {
        try
        {
            var context = new JsonObject
            {
                ["category"] = category,
                ["vendorName"] = vendorName,
                ["summary"] = summary
            };
            var aiOutput = new JsonObject
            {
                ["debitAccount"] = debitAccount,
                ["debitAccountName"] = debitAccountName,
                ["creditAccount"] = creditAccount,
                ["creditAccountName"] = creditAccountName
            };
            await InsertEventAsync(companyCode, "voucher_account_resolved", sessionId, "invoice_booking", context, aiOutput, null, "confirmed", ct);

            // 即时更新品类级别模式 — 让下一次同品类发票立刻命中
            if (!string.IsNullOrWhiteSpace(category))
            {
                await UpsertCategoryPatternAsync(companyCode, category, debitAccount, debitAccountName, creditAccount, creditAccountName, ct);
            }
            // 即时更新供应商级别模式
            if (!string.IsNullOrWhiteSpace(vendorName))
            {
                await UpsertVendorPatternAsync(companyCode, vendorName, debitAccount, debitAccountName, creditAccount, creditAccountName, ct);
            }
            _logger.LogInformation("[Learning] 记录对话指定科目事件并即时更新模式: company={Company}, category={Category}, vendor={Vendor}, debit={Debit}",
                companyCode, category, vendorName, debitAccount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Learning] 记录对话指定科目事件失败");
        }
    }

    /// <summary>
    /// 即时 upsert 品类→科目映射到 ai_learned_patterns
    /// </summary>
    public async Task UpsertCategoryPatternAsync(
        string companyCode,
        string category,
        string debitAccount,
        string? debitAccountName,
        string creditAccount,
        string? creditAccountName,
        CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO ai_learned_patterns (company_code, pattern_type, conditions, recommendation, confidence, sample_count, last_updated_at)
VALUES ($1, 'category_account',
  jsonb_build_object('category', $2),
  jsonb_build_object('debitAccount', $3, 'debitAccountName', $4, 'creditAccount', $5, 'creditAccountName', $6),
  0.70, 1, now())
ON CONFLICT ON CONSTRAINT uq_ai_learned_patterns_key
DO UPDATE SET
  recommendation = EXCLUDED.recommendation,
  confidence = LEAST(0.98, ai_learned_patterns.confidence + 0.05),
  sample_count = ai_learned_patterns.sample_count + 1,
  last_updated_at = now()";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(category);
            cmd.Parameters.AddWithValue(debitAccount);
            cmd.Parameters.AddWithValue((object?)debitAccountName ?? DBNull.Value);
            cmd.Parameters.AddWithValue(creditAccount);
            cmd.Parameters.AddWithValue((object?)creditAccountName ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
            _logger.LogDebug("[Learning] Upsert 品类模式: {Category} → DR:{Debit}/CR:{Credit}", category, debitAccount, creditAccount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Learning] Upsert 品类模式失败: category={Category}", category);
        }
    }

    /// <summary>
    /// 即时 upsert 供应商→科目映射到 ai_learned_patterns
    /// </summary>
    public async Task UpsertVendorPatternAsync(
        string companyCode,
        string vendorName,
        string debitAccount,
        string? debitAccountName,
        string creditAccount,
        string? creditAccountName,
        CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO ai_learned_patterns (company_code, pattern_type, conditions, recommendation, confidence, sample_count, last_updated_at)
VALUES ($1, 'vendor_account',
  jsonb_build_object('vendorName', $2),
  jsonb_build_object('debitAccount', $3, 'debitAccountName', $4, 'creditAccount', $5, 'creditAccountName', $6),
  0.70, 1, now())
ON CONFLICT ON CONSTRAINT uq_ai_learned_patterns_key
DO UPDATE SET
  recommendation = EXCLUDED.recommendation,
  confidence = LEAST(0.98, ai_learned_patterns.confidence + 0.05),
  sample_count = ai_learned_patterns.sample_count + 1,
  last_updated_at = now()";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(vendorName);
            cmd.Parameters.AddWithValue(debitAccount);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)debitAccountName ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            cmd.Parameters.AddWithValue(creditAccount);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)creditAccountName ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            await cmd.ExecuteNonQueryAsync(ct);
            _logger.LogDebug("[Learning] Upsert 供应商模式: {Vendor} → DR:{Debit}/CR:{Credit}", vendorName, debitAccount, creditAccount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Learning] Upsert 供应商模式失败: vendor={Vendor}", vendorName);
        }
    }

    // ==================== 银行明细记账专用学习方法 ====================

    /// <summary>
    /// 记录用户通过 ChatKit 对银行明细记账的事件（凭证创建后调用）。
    /// 同时即时更新 ai_learned_patterns 中的 bank_description_account 映射。
    /// </summary>
    public async Task RecordBankPostingViaChatAsync(
        string companyCode,
        string? sessionId,
        string bankTransactionId,
        string? description,
        decimal amount,
        bool isWithdrawal,
        string? debitAccount,
        string? debitAccountName,
        string? creditAccount,
        string? creditAccountName,
        string? counterpartyKind,
        string? counterpartyId,
        Guid? voucherId,
        string? voucherNo,
        CancellationToken ct = default)
    {
        try
        {
            var context = new JsonObject
            {
                ["bankTransactionId"] = bankTransactionId,
                ["description"] = description,
                ["amount"] = amount,
                ["isWithdrawal"] = isWithdrawal,
                ["counterpartyKind"] = counterpartyKind,
                ["counterpartyId"] = counterpartyId,
                ["voucherId"] = voucherId?.ToString(),
                ["voucherNo"] = voucherNo
            };
            var aiOutput = new JsonObject
            {
                ["debitAccount"] = debitAccount,
                ["debitAccountName"] = debitAccountName,
                ["creditAccount"] = creditAccount,
                ["creditAccountName"] = creditAccountName
            };
            await InsertEventAsync(companyCode, "bank_voucher_created_via_chat", sessionId, "bank_auto_booking", context, aiOutput, null, "confirmed", ct);

            // AI 自动记账不进入学习历史（只有人工修正才学习）
            // 仅记录事件，不 upsert 科目模式
            _logger.LogInformation("[Learning] 记录银行 ChatKit 记账事件（不更新学习模式）: company={Company}, txId={TxId}, desc={Desc}",
                companyCode, bankTransactionId, description);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Learning] 记录银行 ChatKit 记账事件失败");
        }
    }

    /// <summary>
    /// 记录银行来源凭证被用户修正的事件。
    /// 对比原始科目和修正后科目，更新 bank_description_account 模式。
    /// </summary>
    public async Task RecordBankVoucherCorrectionAsync(
        string companyCode,
        Guid voucherId,
        string? bankDescription,
        decimal? bankAmount,
        bool? isWithdrawal,
        string? oldDebit,
        string? newDebit,
        string? oldCredit,
        string? newCredit,
        string? newDebitName,
        string? newCreditName,
        CancellationToken ct = default)
    {
        try
        {
            var context = new JsonObject
            {
                ["voucherId"] = voucherId.ToString(),
                ["bankDescription"] = bankDescription,
                ["bankAmount"] = bankAmount,
                ["isWithdrawal"] = isWithdrawal,
                ["source"] = "bank_auto_posting"
            };
            var originalData = new JsonObject { ["debitAccount"] = oldDebit, ["creditAccount"] = oldCredit };
            var correctedData = new JsonObject
            {
                ["debitAccount"] = newDebit,
                ["debitAccountName"] = newDebitName,
                ["creditAccount"] = newCredit,
                ["creditAccountName"] = newCreditName
            };
            var userAction = new JsonObject
            {
                ["corrected"] = correctedData,
                ["original"] = originalData,
                ["correctedAt"] = DateTimeOffset.UtcNow.ToString("O")
            };
            await InsertEventAsync(companyCode, "bank_voucher_correction", null, "bank_auto_booking", context, null, userAction, "correction", ct);

            // 即时更新银行摘要→科目模式（用修正后的科目覆盖）
            // 仮払金(183)/仮受金(319) 是兜底科目，不应作为学习数据
            var defaultAccounts = new HashSet<string> { "183", "319" };
            if (!string.IsNullOrWhiteSpace(bankDescription)
                && !string.IsNullOrWhiteSpace(newDebit) && !string.IsNullOrWhiteSpace(newCredit)
                && !defaultAccounts.Contains(newDebit) && !defaultAccounts.Contains(newCredit))
            {
                await UpsertBankDescriptionPatternAsync(companyCode, bankDescription, isWithdrawal ?? true, newDebit, newDebitName, newCredit, newCreditName, ct);
            }
            _logger.LogInformation("[Learning] 记录银行凭证修正事件: company={Company}, voucher={VoucherId}, desc={Desc}",
                companyCode, voucherId, bankDescription);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Learning] 记录银行凭证修正事件失败");
        }
    }

    /// <summary>
    /// 即时 upsert 银行摘要→科目映射到 ai_learned_patterns (pattern_type='bank_description_account')
    /// </summary>
    public async Task UpsertBankDescriptionPatternAsync(
        string companyCode,
        string description,
        bool isWithdrawal,
        string debitAccount,
        string? debitAccountName,
        string creditAccount,
        string? creditAccountName,
        CancellationToken ct = default)
    {
        try
        {
            // 对描述做简单规范化，去除金额、日期等变化部分，保留核心关键词
            var normalizedDesc = NormalizeBankDescription(description);
            if (string.IsNullOrWhiteSpace(normalizedDesc)) return;

            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO ai_learned_patterns (company_code, pattern_type, conditions, recommendation, confidence, sample_count, last_updated_at)
VALUES ($1, 'bank_description_account',
  jsonb_build_object('description', $2, 'isWithdrawal', $3),
  jsonb_build_object('debitAccount', $4, 'debitAccountName', $5::text, 'creditAccount', $6, 'creditAccountName', $7::text),
  0.75, 1, now())
ON CONFLICT ON CONSTRAINT uq_ai_learned_patterns_key
DO UPDATE SET
  recommendation = EXCLUDED.recommendation,
  confidence = LEAST(0.98, ai_learned_patterns.confidence + 0.05),
  sample_count = ai_learned_patterns.sample_count + 1,
  last_updated_at = now()";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(normalizedDesc);
            cmd.Parameters.AddWithValue(isWithdrawal);
            cmd.Parameters.AddWithValue(debitAccount);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)debitAccountName ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            cmd.Parameters.AddWithValue(creditAccount);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)creditAccountName ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            await cmd.ExecuteNonQueryAsync(ct);
            _logger.LogDebug("[Learning] Upsert 银行摘要模式: {Desc} → DR:{Debit}/CR:{Credit}", normalizedDesc, debitAccount, creditAccount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Learning] Upsert 银行摘要模式失败: desc={Desc}", description);
        }
    }

    /// <summary>
    /// 规范化银行交易摘要：去除金额、日期、序号等变化部分，保留交易对手名和交易类型关键词。
    /// </summary>
    private static string NormalizeBankDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "";
        var result = description.Trim();
        // 去除常见的日期模式 (yyyy/MM/dd, MM/dd 等)
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\d{2,4}[/\-\.]\d{1,2}([/\-\.]\d{1,2})?", " ");
        // 去除纯数字（金额、序号等）
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\b\d{4,}\b", " ");
        // 规范化空白
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ").Trim();
        return result;
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
            _logger.LogInformation("[Learning] 供应商模式凝练完成: company={Company}, patterns={Count}", companyCode, affected);

            // 同时凝练品类级别模式：从已确认的事件中提取 category→科目映射
            await using var catCmd = conn.CreateCommand();
            catCmd.CommandText = @"
INSERT INTO ai_learned_patterns (company_code, pattern_type, conditions, recommendation, confidence, sample_count, last_updated_at)
SELECT
  $1,
  'category_account',
  jsonb_build_object('category', context->>'category'),
  jsonb_build_object(
    'debitAccount', ai_output->>'debitAccount',
    'debitAccountName', ai_output->>'debitAccountName',
    'creditAccount', ai_output->>'creditAccount',
    'creditAccountName', ai_output->>'creditAccountName'
  ),
  CASE
    WHEN COUNT(*) >= 10 THEN 0.95
    WHEN COUNT(*) >= 5 THEN 0.85
    WHEN COUNT(*) >= 3 THEN 0.75
    ELSE 0.65
  END,
  COUNT(*),
  now()
FROM ai_learning_events
WHERE company_code = $1
  AND event_type IN ('ai_voucher_created', 'voucher_account_resolved')
  AND outcome IN ('confirmed', 'pending_review')
  AND context->>'category' IS NOT NULL
  AND context->>'category' != ''
  AND ai_output->>'debitAccount' IS NOT NULL
GROUP BY context->>'category', ai_output->>'debitAccount', ai_output->>'debitAccountName', ai_output->>'creditAccount', ai_output->>'creditAccountName'
HAVING COUNT(*) >= 1
ON CONFLICT ON CONSTRAINT uq_ai_learned_patterns_key
DO UPDATE SET
  recommendation = EXCLUDED.recommendation,
  confidence = EXCLUDED.confidence,
  sample_count = EXCLUDED.sample_count,
  last_updated_at = now()";
            catCmd.Parameters.AddWithValue(companyCode);
            var catAffected = await catCmd.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("[Learning] 品类模式凝练完成: company={Company}, patterns={Count}", companyCode, catAffected);
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
