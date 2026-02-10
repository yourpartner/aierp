using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Server.Infrastructure.Skills;

/// <summary>
/// 构建增强的 AI 上下文：将历史模式、已学习规则、置信度指令注入到系统提示中，
/// 让 AI 在处理发票时拥有「记忆」和「判断力」。
/// </summary>
public sealed class SkillContextBuilder
{
    private readonly HistoricalPatternService _patternService;
    private readonly ILogger<SkillContextBuilder> _logger;

    public SkillContextBuilder(HistoricalPatternService patternService, ILogger<SkillContextBuilder> logger)
    {
        _patternService = patternService;
        _logger = logger;
    }

    /// <summary>
    /// 增强上下文的结果
    /// </summary>
    public sealed record EnrichedContext(
        string HistoricalHints,       // 注入到系统提示的历史参考文本
        string ConfidenceInstructions, // 置信度驱动的行为指令
        decimal EstimatedConfidence    // 预估置信度
    );

    /// <summary>
    /// 根据发票解析结果构建增强上下文。
    /// 这个方法会查询历史数据，生成供 AI 参考的文本，附加到系统提示中。
    /// </summary>
    public async Task<EnrichedContext> BuildInvoiceContextAsync(
        string companyCode,
        JsonObject? parsedInvoiceData,
        string language,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var confidence = 0.3m; // 基础置信度

        if (parsedInvoiceData is null)
        {
            return new EnrichedContext("", BuildConfidenceInstructions(0.3m, language), 0.3m);
        }

        // 提取解析数据中的关键字段
        var vendorName = ExtractString(parsedInvoiceData, "partnerName");
        var totalAmount = ExtractDecimal(parsedInvoiceData, "totalAmount");
        var category = ExtractString(parsedInvoiceData, "category");
        var documentType = ExtractString(parsedInvoiceData, "documentType");

        // 1. 查询供应商历史记账模式
        if (!string.IsNullOrWhiteSpace(vendorName))
        {
            var templates = await _patternService.GetVendorBookingTemplatesAsync(companyCode, vendorName, ct);
            if (templates.Count > 0)
            {
                confidence += 0.3m; // 有历史记录，置信度提升
                sb.AppendLine();
                sb.AppendLine(Loc(language,
                    "【歴史参照】この取引先の過去の仕訳パターン：",
                    "【历史参照】该供应商过去的记账模式："));

                foreach (var (debit, credit, summary, count) in templates)
                {
                    sb.AppendLine(Loc(language,
                        $"  - 借方 {debit} / 貸方 {credit}（{count}回使用、摘要例：{summary ?? "なし"}）",
                        $"  - 借方 {debit} / 贷方 {credit}（使用{count}次，摘要示例：{summary ?? "无"}）"));
                }

                if (templates.Count == 1 && templates[0].Count >= 3)
                {
                    confidence += 0.15m; // 单一模式且多次使用，高置信度
                    sb.AppendLine(Loc(language,
                        $"  → この取引先は一貫して借方{templates[0].DebitAccount}/貸方{templates[0].CreditAccount}で処理されています。同じパターンで処理してください。",
                        $"  → 该供应商一贯使用借方{templates[0].DebitAccount}/贷方{templates[0].CreditAccount}记账。请沿用相同模式。"));
                }
                else if (templates.Count > 1)
                {
                    sb.AppendLine(Loc(language,
                        "  → 複数のパターンがあります。金額や内容に基づいて最適なものを選択してください。",
                        "  → 存在多种记账模式。请根据金额和内容选择最合适的。"));
                }
            }
        }

        // 2. 查询类似金额的历史凭证
        if (totalAmount.HasValue && totalAmount.Value > 0)
        {
            var similar = await _patternService.GetSimilarVouchersAsync(companyCode, totalAmount.Value, vendorName, 3, ct);
            if (similar.Count > 0)
            {
                confidence += 0.1m;
                sb.AppendLine();
                sb.AppendLine(Loc(language,
                    "【類似取引】金額が近い最近の仕訳：",
                    "【类似交易】金额相近的近期凭证："));
                foreach (var v in similar.Take(3))
                {
                    sb.AppendLine(Loc(language,
                        $"  - {v.VoucherNo}: {v.Summary ?? "摘要なし"}, ¥{v.Amount:N0}, 借方{v.DebitAccount}/貸方{v.CreditAccount}, {v.PostingDate}",
                        $"  - {v.VoucherNo}: {v.Summary ?? "无摘要"}, ¥{v.Amount:N0}, 借方{v.DebitAccount}/贷方{v.CreditAccount}, {v.PostingDate}"));
                }
            }
        }

        // 3. 查询已学习模式
        if (!string.IsNullOrWhiteSpace(vendorName))
        {
            var patterns = await _patternService.GetLearnedPatternsAsync(companyCode, "vendor_account", 5, ct);
            var matching = patterns.Where(p =>
            {
                var condVendor = p.Conditions.TryGetPropertyValue("vendorName", out var v) ? v?.GetValue<string>() : null;
                return !string.IsNullOrWhiteSpace(condVendor) && vendorName.Contains(condVendor, StringComparison.OrdinalIgnoreCase);
            }).ToList();

            if (matching.Count > 0)
            {
                var best = matching.OrderByDescending(p => p.Confidence).First();
                confidence += (decimal)best.Confidence * 0.2m;
                sb.AppendLine();
                sb.AppendLine(Loc(language,
                    $"【学習済み】AI学習データによると、この取引先は{(int)(best.Confidence * 100)}%の確率で " +
                    $"借方{best.Recommendation["debitAccount"]}/貸方{best.Recommendation["creditAccount"]}で処理されます（サンプル数: {best.SampleCount}）",
                    $"【已学习】AI 学习数据显示，该供应商有{(int)(best.Confidence * 100)}%概率使用 " +
                    $"借方{best.Recommendation["debitAccount"]}/贷方{best.Recommendation["creditAccount"]}记账（样本数: {best.SampleCount}）"));
            }
        }

        // 4. 根据文档类型和分类微调置信度
        if (!string.IsNullOrWhiteSpace(category))
        {
            confidence += 0.05m; // 有分类信息
        }
        if (!string.IsNullOrWhiteSpace(vendorName) && totalAmount.HasValue)
        {
            confidence += 0.05m; // 关键字段完整
        }

        // 限制置信度范围
        confidence = Math.Clamp(confidence, 0m, 0.98m);

        var historicalHints = sb.ToString();
        var confidenceInstructions = BuildConfidenceInstructions(confidence, language);

        _logger.LogInformation("[SkillContext] 增强上下文构建完成: vendor={Vendor}, amount={Amount}, confidence={Confidence:F2}",
            vendorName ?? "unknown", totalAmount ?? 0, confidence);

        return new EnrichedContext(historicalHints, confidenceInstructions, confidence);
    }

    /// <summary>
    /// 根据置信度生成行为指令，告诉 AI 什么时候该直接做、什么时候该问。
    /// </summary>
    private static string BuildConfidenceInstructions(decimal confidence, string language)
    {
        var sb = new StringBuilder();
        sb.AppendLine();

        if (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("【AI 行为策略】");
            if (confidence >= 0.85m)
            {
                sb.AppendLine("置信度：高。你对这笔交易有充分的历史参考。");
                sb.AppendLine("策略：直接创建凭证，不需要向用户确认科目选择。创建后简要告知结果即可。");
                sb.AppendLine("格式：直接执行 → 显示'已创建凭证 XXXX，借方XXX/贷方XXX，金额¥X,XXX'");
            }
            else if (confidence >= 0.65m)
            {
                sb.AppendLine("置信度：中高。你有一定的历史参考。");
                sb.AppendLine("策略：给出推荐方案并直接创建凭证草稿，让用户一键确认或修改。不要逐项提问。");
                sb.AppendLine("格式：'根据历史记录，建议借方XXX/贷方XXX。已创建凭证草稿，请确认。'");
            }
            else if (confidence >= 0.45m)
            {
                sb.AppendLine("置信度：中。你有一些参考但不确定。");
                sb.AppendLine("策略：给出1-2个候选方案，让用户选择。将所有需要确认的信息合并在一次提问中。");
                sb.AppendLine("禁止：不要分多轮提问，不要每个字段单独确认。");
            }
            else
            {
                sb.AppendLine("置信度：低。缺少历史参考。");
                sb.AppendLine("策略：先完成数据提取，然后用一个结构化的确认卡片列出所有待确认项，让用户一次性确认。");
                sb.AppendLine("禁止：不要逐个字段提问；不要说'请告诉我科目'然后等回复后再问'请告诉我日期'。");
            }
        }
        else
        {
            sb.AppendLine("【AI 行動方針】");
            if (confidence >= 0.85m)
            {
                sb.AppendLine("確信度：高。十分な過去データがあります。");
                sb.AppendLine("方針：確認なしで直接伝票を作成し、結果を簡潔に報告してください。");
                sb.AppendLine("形式：直接実行 → '伝票 XXXX を作成しました。借方XXX/貸方XXX、金額¥X,XXX'");
            }
            else if (confidence >= 0.65m)
            {
                sb.AppendLine("確信度：中高。ある程度の参考データがあります。");
                sb.AppendLine("方針：推奨案を提示して伝票を作成し、ユーザーにワンクリック確認を求めてください。項目ごとの質問は禁止。");
            }
            else if (confidence >= 0.45m)
            {
                sb.AppendLine("確信度：中。参考データが限定的です。");
                sb.AppendLine("方針：1-2つの候補を提示し選択してもらいます。確認事項はまとめて1回で質問してください。");
                sb.AppendLine("禁止：複数ラウンドに分けて質問しないこと。");
            }
            else
            {
                sb.AppendLine("確信度：低。過去データが不足しています。");
                sb.AppendLine("方針：データ抽出後、確認カードで未確定項目をまとめて提示し、一括確認を求めてください。");
                sb.AppendLine("禁止：項目ごとに個別質問しないこと。");
            }
        }

        return sb.ToString();
    }

    private static string? ExtractString(JsonObject data, string key)
    {
        return data.TryGetPropertyValue(key, out var node) && node is JsonValue val && val.TryGetValue<string>(out var str) ? str : null;
    }

    private static decimal? ExtractDecimal(JsonObject data, string key)
    {
        if (!data.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is JsonValue val)
        {
            if (val.TryGetValue<decimal>(out var d)) return d;
            if (val.TryGetValue<double>(out var dbl)) return (decimal)dbl;
            if (val.TryGetValue<string>(out var str) && decimal.TryParse(str, out var parsed)) return parsed;
        }
        return null;
    }

    private static string Loc(string language, string ja, string zh) =>
        string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? zh : ja;
}
