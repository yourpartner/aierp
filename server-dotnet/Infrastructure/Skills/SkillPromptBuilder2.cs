using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Server.Modules;

namespace Server.Infrastructure.Skills;

/// <summary>
/// Skill 级别的 Prompt 构建器
/// 将 Skill 配置中的 system_prompt 模板 + rules + examples + 历史上下文 → 最终 Prompt
///
/// 支持的模板变量：
///   {rules}     - 自动注入格式化后的业务规则
///   {examples}  - 自动注入格式化后的 few-shot 示例
///   {history}   - 自动注入历史上下文（来自 SkillContextBuilder）
///   {company}   - 公司代码
/// </summary>
public sealed class SkillPromptBuilder2
{
    private readonly AgentSkillService _skillService;
    private readonly SkillContextBuilder _contextBuilder;

    public SkillPromptBuilder2(AgentSkillService skillService, SkillContextBuilder contextBuilder)
    {
        _skillService = skillService;
        _contextBuilder = contextBuilder;
    }

    /// <summary>
    /// 从 Skill 配置构建完整的系统 Prompt
    /// </summary>
    public async Task<string> BuildSystemPromptAsync(
        AgentSkillService.AgentSkillRecord skill,
        string companyCode,
        string language,
        string? historicalContext,
        CancellationToken ct)
    {
        // 加载规则
        var rules = await _skillService.GetEffectiveRulesAsync(companyCode, skill.SkillKey, ct);
        var activeRules = rules.Where(r => r.IsActive).ToList();

        // 加载示例
        var examples = await _skillService.ListExamplesAsync(skill.Id, ct);
        var activeExamples = examples.Where(e => e.IsActive).ToList();

        // 获取 Prompt 模板
        var template = skill.SystemPrompt ?? "";

        // 格式化规则
        var rulesText = FormatRules(activeRules, language);

        // 格式化示例
        var examplesText = FormatExamples(activeExamples, language);

        // 替换模板变量
        var result = template
            .Replace("{rules}", rulesText)
            .Replace("{examples}", examplesText)
            .Replace("{history}", historicalContext ?? "")
            .Replace("{company}", companyCode);

        return result;
    }

    /// <summary>
    /// 从 Skill 配置获取文档提取 Prompt（用于 OCR/发票识别等）
    /// </summary>
    public string GetExtractionPrompt(AgentSkillService.AgentSkillRecord skill, string language)
    {
        if (!string.IsNullOrWhiteSpace(skill.ExtractionPrompt))
            return skill.ExtractionPrompt;

        // 如果没有配置则返回空（由调用方决定是否使用默认值）
        return "";
    }

    /// <summary>
    /// 从 Skill 配置获取跟进对话 Prompt
    /// </summary>
    public string GetFollowupPrompt(AgentSkillService.AgentSkillRecord skill, string language)
    {
        return skill.FollowupPrompt ?? "";
    }

    /// <summary>
    /// 获取 Skill 的模型配置
    /// </summary>
    public static (string Model, string ExtractionModel, double Temperature) GetModelConfig(AgentSkillService.AgentSkillRecord skill)
    {
        var config = skill.ModelConfig;
        var model = GetJsonString(config, "model") ?? "gpt-4o";
        var extractionModel = GetJsonString(config, "extractionModel") ?? "gpt-4o-mini";
        var tempStr = GetJsonString(config, "temperature");
        var temperature = 0.1;
        if (tempStr != null && double.TryParse(tempStr, out var t)) temperature = t;
        return (model, extractionModel, temperature);
    }

    /// <summary>
    /// 获取 Skill 的行为配置
    /// </summary>
    public static SkillBehaviorConfig GetBehaviorConfig(AgentSkillService.AgentSkillRecord skill)
    {
        var config = skill.BehaviorConfig;
        var result = new SkillBehaviorConfig();

        if (config == null) return result;

        if (config.TryGetPropertyValue("confidence", out var confNode) && confNode is JsonObject confObj)
        {
            var highStr = GetJsonString(confObj, "high") ?? GetJsonString(confObj, "highThreshold");
            var medStr = GetJsonString(confObj, "medium") ?? GetJsonString(confObj, "mediumThreshold");
            var lowStr = GetJsonString(confObj, "low") ?? GetJsonString(confObj, "lowThreshold");
            if (highStr != null && double.TryParse(highStr, out var h)) result.HighConfidence = h;
            if (medStr != null && double.TryParse(medStr, out var m)) result.MediumConfidence = m;
            if (lowStr != null && double.TryParse(lowStr, out var l)) result.LowConfidence = l;
        }

        var autoStr = GetJsonString(config, "autoExecute");
        if (autoStr != null && bool.TryParse(autoStr, out var auto)) result.AutoExecute = auto;

        var confirmStr = GetJsonString(config, "requireConfirmation");
        if (confirmStr != null && bool.TryParse(confirmStr, out var confirm)) result.RequireConfirmation = confirm;

        return result;
    }

    // ====================== Formatting Helpers ======================

    private static string FormatRules(IReadOnlyList<AgentSkillService.SkillRuleRecord> rules, string language)
    {
        if (rules.Count == 0) return "";

        var isZh = string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.AppendLine(isZh
            ? "### 业务规则（按优先级排序，请严格遵守）："
            : "### 業務ルール（優先度順・厳守してください）：");

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var line = new StringBuilder();
            line.Append(i + 1).Append(". ").Append(rule.Name);

            // 提取 conditions 中的关键词
            var keywords = GetJsonStringArray(rule.Conditions, "keywords");
            if (keywords.Length > 0)
                line.Append(isZh ? "；关键词：" : "；キーワード：").Append(string.Join(" / ", keywords));

            // 提取 conditions 中的类别
            var category = GetJsonString(rule.Conditions, "category");
            if (!string.IsNullOrWhiteSpace(category))
                line.Append(isZh ? "；类别：" : "；カテゴリ：").Append(category);

            // 提取 conditions 中的交易类型
            var txType = GetJsonString(rule.Conditions, "transactionType");
            if (!string.IsNullOrWhiteSpace(txType))
                line.Append(isZh ? "；交易类型：" : "；取引種別：").Append(txType == "deposit" ? (isZh ? "入金" : "入金") : (isZh ? "出金" : "出金"));

            // 提取 conditions 中的摘要匹配模式
            var descContains = GetJsonStringArray(rule.Conditions, "descriptionContains");
            if (descContains.Length > 0)
                line.Append(isZh ? "；摘要含：" : "；摘要に含む：").Append(string.Join(" / ", descContains));
            var descRegex = GetJsonString(rule.Conditions, "descriptionRegex");
            if (!string.IsNullOrWhiteSpace(descRegex))
                line.Append(isZh ? "；摘要匹配：" : "；摘要パターン：").Append(descRegex);

            // 提取 actions 中的科目（兼容 accountCode 和 debitAccount/creditAccount 两种格式）
            var accountCode = GetJsonString(rule.Actions, "accountCode");
            var accountName = GetJsonString(rule.Actions, "accountName");
            var debitAccount = GetJsonString(rule.Actions, "debitAccount") ?? GetJsonString(rule.Actions, "debitAccountHint");
            var creditAccount = GetJsonString(rule.Actions, "creditAccount") ?? GetJsonString(rule.Actions, "creditAccountHint");
            if (!string.IsNullOrWhiteSpace(accountCode) || !string.IsNullOrWhiteSpace(accountName))
            {
                line.Append(isZh ? "；推荐借方：" : "；推奨借方：");
                if (!string.IsNullOrWhiteSpace(accountCode)) line.Append(accountCode);
                if (!string.IsNullOrWhiteSpace(accountName)) line.Append(' ').Append(accountName);
            }
            if (!string.IsNullOrWhiteSpace(debitAccount) || !string.IsNullOrWhiteSpace(creditAccount))
            {
                if (!string.IsNullOrWhiteSpace(debitAccount))
                    line.Append(isZh ? "；借方科目：" : "；借方科目：").Append(debitAccount);
                if (!string.IsNullOrWhiteSpace(creditAccount))
                    line.Append(isZh ? "；贷方科目：" : "；貸方科目：").Append(creditAccount);
            }

            // 提取 actions 中的清账设置
            var settlement = GetJsonString(rule.Actions, "settlement");
            if (settlement != null || (rule.Actions?.TryGetPropertyValue("settlement", out _) ?? false))
            {
                line.Append(isZh ? "；支持清账" : "；消込対応");
            }

            // 提取 actions 中的摘要模板
            var summaryTemplate = GetJsonString(rule.Actions, "summaryTemplate");
            if (!string.IsNullOrWhiteSpace(summaryTemplate))
                line.Append(isZh ? "；摘要模板：" : "；摘要テンプレート：").Append(summaryTemplate);

            // 提取 actions 中的备注
            var note = GetJsonString(rule.Actions, "note");
            if (!string.IsNullOrWhiteSpace(note))
                line.Append(isZh ? "；备注：" : "；備考：").Append(note);

            // 人均阈值等高级规则
            var threshold = GetJsonString(rule.Actions, "perPersonThreshold");
            if (!string.IsNullOrWhiteSpace(threshold))
            {
                var altCode = GetJsonString(rule.Actions, "alternativeAccountCode");
                var altName = GetJsonString(rule.Actions, "alternativeAccountName");
                line.Append(isZh
                    ? $"；人均>={threshold}用{accountCode} {accountName}，人均<{threshold}用{altCode} {altName}"
                    : $"；一人当たり>={threshold}の場合{accountCode} {accountName}、<{threshold}の場合{altCode} {altName}");
            }

            sb.AppendLine(line.ToString());
        }

        sb.AppendLine(isZh
            ? "匹配上述规则时请优先使用对应科目；不确定时用 lookup_account 确认。"
            : "上記ルールに該当する場合は対応する科目を優先使用し、不明な場合は lookup_account で確認してください。");

        return sb.ToString();
    }

    private static string FormatExamples(IReadOnlyList<AgentSkillService.SkillExampleRecord> examples, string language)
    {
        if (examples.Count == 0) return "";

        var isZh = string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.AppendLine(isZh
            ? "### 参考示例（请参照以下案例的处理模式）："
            : "### 参考例（以下の処理パターンを参照してください）：");

        for (var i = 0; i < Math.Min(examples.Count, 5); i++) // 最多 5 个示例
        {
            var ex = examples[i];
            var exLabel = isZh ? "示例" : "例";
            sb.AppendLine($"--- {exLabel} {i + 1}: {ex.Name ?? ""} ---");

            // 输入
            if (ex.InputData.Count > 0)
            {
                sb.AppendLine(isZh ? "输入：" : "入力：");
                sb.AppendLine(ex.InputData.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            // 期望输出
            if (ex.ExpectedOutput.Count > 0)
            {
                sb.AppendLine(isZh ? "期望处理：" : "期待される処理：");
                sb.AppendLine(ex.ExpectedOutput.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        return sb.ToString();
    }

    // ====================== JSON Helpers ======================

    private static string? GetJsonString(JsonObject? obj, string key)
    {
        if (obj == null) return null;
        if (!obj.TryGetPropertyValue(key, out var node)) return null;
        if (node is JsonValue val)
        {
            if (val.TryGetValue<string>(out var s)) return s;
            // 数值类型也转字符串
            return node.ToString();
        }
        return null;
    }

    private static string[] GetJsonStringArray(JsonObject? obj, string key)
    {
        if (obj == null) return Array.Empty<string>();
        if (!obj.TryGetPropertyValue(key, out var node)) return Array.Empty<string>();
        if (node is JsonArray arr)
            return arr.Select(n => n?.ToString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        return Array.Empty<string>();
    }
}

/// <summary>Skill 行为配置</summary>
public sealed class SkillBehaviorConfig
{
    public double HighConfidence { get; set; } = 0.85;
    public double MediumConfidence { get; set; } = 0.65;
    public double LowConfidence { get; set; } = 0.45;
    public bool AutoExecute { get; set; } = false;
    public bool RequireConfirmation { get; set; } = true;
}
