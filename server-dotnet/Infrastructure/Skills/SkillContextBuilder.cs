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

        // 提取解析数据中的关键字段（兼容多种字段名格式）
        var vendorName = ExtractString(parsedInvoiceData, "partnerName")
                      ?? ExtractString(parsedInvoiceData, "company")
                      ?? ExtractString(parsedInvoiceData, "vendorName")
                      ?? ExtractString(parsedInvoiceData, "supplier")
                      ?? ExtractString(parsedInvoiceData, "storeName");
        var totalAmount = ExtractDecimal(parsedInvoiceData, "totalAmount")
                       ?? ExtractDecimal(parsedInvoiceData, "amount")
                       ?? ExtractDecimal(parsedInvoiceData, "parkingFee")
                       ?? ExtractDecimal(parsedInvoiceData, "total")
                       ?? ExtractDecimal(parsedInvoiceData, "合計");
        var category = ExtractString(parsedInvoiceData, "category");
        var documentType = ExtractString(parsedInvoiceData, "documentType");

        // 如果没有 category 字段，从数据内容推断费用品类
        if (string.IsNullOrWhiteSpace(category))
        {
            category = InferCategoryFromData(parsedInvoiceData);
            if (!string.IsNullOrWhiteSpace(category))
            {
                _logger.LogInformation("[SkillContext] 从解析数据推断品类: {Category}", category);
            }
        }

        _logger.LogInformation("[SkillContext] 字段提取结果: vendor={Vendor}, amount={Amount}, category={Category}, docType={DocType}",
            vendorName ?? "(null)", totalAmount?.ToString() ?? "(null)", category ?? "(null)", documentType ?? "(null)");

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

        // 3. 查询已学习模式（供应商维度）
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
                confidence += best.Confidence >= 0.8m ? 0.35m : (decimal)best.Confidence * 0.25m;
                var bestDebit = best.Recommendation.TryGetPropertyValue("debitAccount", out var bdNode) ? bdNode?.GetValue<string>() : null;
                var bestCredit = best.Recommendation.TryGetPropertyValue("creditAccount", out var bcNode) ? bcNode?.GetValue<string>() : null;
                var bestDebitName = best.Recommendation.TryGetPropertyValue("debitAccountName", out var dn) ? dn?.GetValue<string>() : null;
                var bestCreditName = best.Recommendation.TryGetPropertyValue("creditAccountName", out var cn) ? cn?.GetValue<string>() : null;
                var debitLabel = !string.IsNullOrWhiteSpace(bestDebitName) ? $"{bestDebit}({bestDebitName})" : $"{bestDebit}";
                var creditLabel = !string.IsNullOrWhiteSpace(bestCreditName) ? $"{bestCredit}({bestCreditName})" : $"{bestCredit}";
                sb.AppendLine();
                if (best.Confidence >= 0.7m && !string.IsNullOrWhiteSpace(bestDebit))
                {
                    sb.AppendLine(Loc(language,
                        $"【重要・学習済み科目指定】この取引先について：" +
                        $"費用科目（主な借方）＝{bestDebit}（{bestDebitName ?? ""}）、支払科目（主な貸方）＝{bestCredit}（{bestCreditName ?? ""}）を使用してください。" +
                        $"この費用科目と支払科目について lookup_account を呼び出す必要はありません。" +
                        $"ただし、消費税の処理は通常通り行ってください：適格請求書の場合は税抜金額を費用科目に、消費税額を仮払消費税科目に分けて仕訳してください（仮払消費税の科目コードは lookup_account で確認してください）。" +
                        $"（確信度: {(int)(best.Confidence * 100)}%、サンプル数: {best.SampleCount}）",
                        $"【重要·已学习科目指定】对于该供应商：" +
                        $"费用科目（主借方）={bestDebit}（{bestDebitName ?? ""}）、支付科目（主贷方）={bestCredit}（{bestCreditName ?? ""}）。" +
                        $"无需调用 lookup_account 查找费用科目和支付科目。" +
                        $"但消费税仍需正常处理：若为合规发票，请将税前金额记入费用科目，消费税额记入仮払消費税科目（仮払消費税的科目代码请通过 lookup_account 查询）。" +
                        $"（置信度: {(int)(best.Confidence * 100)}%，样本数: {best.SampleCount}）"));
                }
                else
                {
                    sb.AppendLine(Loc(language,
                        $"【学習済み・取引先】AI学習データによると、この取引先は{(int)(best.Confidence * 100)}%の確率で " +
                        $"借方{debitLabel}/貸方{creditLabel}で処理されます（サンプル数: {best.SampleCount}）。このパターンで処理してください。",
                        $"【已学习·供应商】AI 学习数据显示，该供应商有{(int)(best.Confidence * 100)}%概率使用 " +
                        $"借方{debitLabel}/贷方{creditLabel}记账（样本数: {best.SampleCount}）。请沿用此模式。"));
                }
            }
        }

        // 4. 查询已学习模式（品类/费用类型维度）— 跨供应商通用规则
        var categoryPatternFound = false;
        if (!string.IsNullOrWhiteSpace(category))
        {
            var categoryPatterns = await _patternService.GetCategoryPatternsAsync(companyCode, category, 3, ct);
            if (categoryPatterns.Count > 0)
            {
                categoryPatternFound = true;
                var bestCat = categoryPatterns.OrderByDescending(p => p.Confidence).First();
                // 品类模式直接提供科目代码，权重更大
                confidence += bestCat.Confidence >= 0.8m ? 0.35m : (decimal)bestCat.Confidence * 0.25m;
                var catDebit = bestCat.Recommendation.TryGetPropertyValue("debitAccount", out var cdNode) ? cdNode?.GetValue<string>() : null;
                var catCredit = bestCat.Recommendation.TryGetPropertyValue("creditAccount", out var ccNode) ? ccNode?.GetValue<string>() : null;
                var catDebitName = bestCat.Recommendation.TryGetPropertyValue("debitAccountName", out var cdn) ? cdn?.GetValue<string>() : null;
                var catCreditName = bestCat.Recommendation.TryGetPropertyValue("creditAccountName", out var ccn) ? ccn?.GetValue<string>() : null;
                var catDebitLabel = !string.IsNullOrWhiteSpace(catDebitName) ? $"{catDebit}({catDebitName})" : catDebit ?? "";
                var catCreditLabel = !string.IsNullOrWhiteSpace(catCreditName) ? $"{catCredit}({catCreditName})" : catCredit ?? "";
                var patternCategory = bestCat.Conditions.TryGetPropertyValue("category", out var pcNode) ? pcNode?.GetValue<string>() : category;
                sb.AppendLine();
                // 当置信度 >= 0.7 时，给出强制性指令，直接提供科目代码
                if (bestCat.Confidence >= 0.7m && !string.IsNullOrWhiteSpace(catDebit))
                {
                    sb.AppendLine(Loc(language,
                        $"【重要・学習済み科目指定】この「{patternCategory}」タイプの証憑について：" +
                        $"費用科目（主な借方）＝{catDebit}（{catDebitName ?? ""}）、支払科目（主な貸方）＝{catCredit}（{catCreditName ?? ""}）を使用してください。" +
                        $"この費用科目と支払科目について lookup_account を呼び出す必要はありません。" +
                        $"ただし、消費税の処理は通常通り行ってください：適格請求書の場合は税抜金額を費用科目に、消費税額を仮払消費税科目に分けて仕訳してください（仮払消費税の科目コードは lookup_account で確認してください）。" +
                        $"（確信度: {(int)(bestCat.Confidence * 100)}%、サンプル数: {bestCat.SampleCount}）",
                        $"【重要·已学习科目指定】对于「{patternCategory}」类型的发票：" +
                        $"费用科目（主借方）={catDebit}（{catDebitName ?? ""}）、支付科目（主贷方）={catCredit}（{catCreditName ?? ""}）。" +
                        $"无需调用 lookup_account 查找费用科目和支付科目。" +
                        $"但消费税仍需正常处理：若为合规发票，请将税前金额记入费用科目，消费税额记入仮払消費税科目（仮払消費税的科目代码请通过 lookup_account 查询）。" +
                        $"（置信度: {(int)(bestCat.Confidence * 100)}%，样本数: {bestCat.SampleCount}）"));
                }
                else
                {
                    sb.AppendLine(Loc(language,
                        $"【学習済み・カテゴリ】「{patternCategory}」カテゴリの証憑は{(int)(bestCat.Confidence * 100)}%の確率で " +
                        $"借方{catDebitLabel}/貸方{catCreditLabel}で処理されます（サンプル数: {bestCat.SampleCount}）。このパターンを優先してください。",
                        $"【已学习·品类】「{patternCategory}」类别的发票有{(int)(bestCat.Confidence * 100)}%概率使用 " +
                        $"借方{catDebitLabel}/贷方{catCreditLabel}记账（样本数: {bestCat.SampleCount}）。请优先使用此模式。"));
                }
            }
        }

        // 5. 如果 ai_learned_patterns 没有品类记录，尝试从历史凭证中直接搜索关键词匹配
        if (!categoryPatternFound && !string.IsNullOrWhiteSpace(category))
        {
            // 将 extraction category（如 "transportation"）映射为搜索关键词
            var searchKeywords = MapCategoryToKeywords(category);
            foreach (var keyword in searchKeywords)
            {
                var catTemplates = await _patternService.GetCategoryBookingTemplatesAsync(companyCode, keyword, ct);
                if (catTemplates.Count > 0)
                {
                    categoryPatternFound = true;
                    confidence += 0.15m;
                    sb.AppendLine();
                    sb.AppendLine(Loc(language,
                        $"【歴史参照・カテゴリ】「{keyword}」関連の過去の仕訳パターン：",
                        $"【历史参照·品类】与「{keyword}」相关的历史记账模式："));
                    foreach (var (debit, debitName, credit, creditName, count) in catTemplates.Take(3))
                    {
                        var dLabel = !string.IsNullOrWhiteSpace(debitName) ? $"{debit}({debitName})" : debit;
                        var cLabel = !string.IsNullOrWhiteSpace(creditName) ? $"{credit}({creditName})" : credit;
                        sb.AppendLine(Loc(language,
                            $"  - 借方 {dLabel} / 貸方 {cLabel}（{count}回使用）",
                            $"  - 借方 {dLabel} / 贷方 {cLabel}（使用{count}次）"));
                    }
                    if (catTemplates.Count == 1 && catTemplates[0].Count >= 2)
                    {
                        sb.AppendLine(Loc(language,
                            "  → このカテゴリは一貫して上記パターンで処理されています。同じパターンで処理してください。",
                            "  → 该品类一贯使用上述模式。请沿用相同模式。"));
                    }
                    break; // 找到一个关键词匹配就足够了
                }
            }

            if (!categoryPatternFound)
            {
                confidence += 0.05m; // 有分类信息但无任何历史模式
            }
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
                sb.AppendLine("重要：上面的【已学习科目指定】中如果提供了科目代码，直接在 create_voucher 的 accountCode 中使用该代码，不需要再调用 lookup_account 查找。");
                sb.AppendLine("格式：直接执行 → 显示'已创建凭证 XXXX，借方XXX/贷方XXX，金额¥X,XXX'");
            }
            else if (confidence >= 0.65m)
            {
                sb.AppendLine("置信度：中高。你有一定的历史参考。");
                sb.AppendLine("策略：上面的【已学习科目指定】中如果提供了科目代码，直接使用该代码创建凭证，不需要再调用 lookup_account。创建后让用户一键确认或修改。不要逐项提问。");
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
                sb.AppendLine("重要：上記の【学習済み科目指定】で指示された科目コードがある場合、そのコードを create_voucher の accountCode に直接使用してください。lookup_account で別の名前で検索する必要はありません。");
                sb.AppendLine("形式：直接実行 → '伝票 XXXX を作成しました。借方XXX/貸方XXX、金額¥X,XXX'");
            }
            else if (confidence >= 0.65m)
            {
                sb.AppendLine("確信度：中高。ある程度の参考データがあります。");
                sb.AppendLine("方針：上記の【学習済み科目指定】で指示された科目コードがある場合はそのまま使用し、推奨案で伝票を作成してください。ユーザーにワンクリック確認を求めてください。項目ごとの質問は禁止。");
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

    // ====================== 银行明细记账上下文构建 ======================

    /// <summary>
    /// 根据银行交易明细构建增强上下文。
    /// 查询历史学习数据和类似交易，生成供 AI 参考的文本。
    /// </summary>
    public async Task<EnrichedContext> BuildBankContextAsync(
        string companyCode,
        JsonObject? transactionData,
        string language,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var confidence = 0.3m;

        if (transactionData is null)
        {
            return new EnrichedContext("", BuildConfidenceInstructions(confidence, language), 0.3m);
        }

        // 提取银行交易关键字段
        var description = ExtractString(transactionData, "description") ?? "";
        var depositAmount = ExtractDecimal(transactionData, "depositAmount");
        var withdrawalAmount = ExtractDecimal(transactionData, "withdrawalAmount");
        var amount = depositAmount ?? (withdrawalAmount.HasValue ? Math.Abs(withdrawalAmount.Value) : 0m);
        var isWithdrawal = (withdrawalAmount ?? 0m) < 0m || (depositAmount ?? 0m) <= 0m && (withdrawalAmount ?? 0m) != 0m;
        var bankName = ExtractString(transactionData, "bankName");
        var accountName = ExtractString(transactionData, "accountName");

        _logger.LogInformation("[SkillContext/Bank] 字段提取: desc={Desc}, amount={Amount}, isWithdrawal={IsW}, bank={Bank}",
            description, amount, isWithdrawal, bankName ?? "(null)");

        // 1. 查询已学习模式（bank_description_account）
        var learnedPatterns = await _patternService.GetLearnedPatternsAsync(companyCode, "bank_description_account", 10, ct);
        if (learnedPatterns.Count > 0 && !string.IsNullOrWhiteSpace(description))
        {
            // 模糊匹配摘要
            var matching = learnedPatterns.Where(p =>
            {
                var condDesc = p.Conditions.TryGetPropertyValue("description", out var v) ? v?.GetValue<string>() : null;
                return !string.IsNullOrWhiteSpace(condDesc) &&
                       (description.Contains(condDesc, StringComparison.OrdinalIgnoreCase) ||
                        condDesc.Contains(description, StringComparison.OrdinalIgnoreCase));
            }).ToList();

            if (matching.Count > 0)
            {
                var best = matching.OrderByDescending(p => p.Confidence).First();
                confidence += best.Confidence >= 0.8m ? 0.35m : (decimal)best.Confidence * 0.25m;
                var bestAccount = best.Recommendation.TryGetPropertyValue("accountCode", out var acNode) ? acNode?.GetValue<string>() : null;
                var bestAccountName = best.Recommendation.TryGetPropertyValue("accountName", out var anNode) ? anNode?.GetValue<string>() : null;

                if (best.Confidence >= 0.7m && !string.IsNullOrWhiteSpace(bestAccount))
                {
                    sb.AppendLine();
                    sb.AppendLine(Loc(language,
                        $"【重要・学習済み科目指定】この銀行明細の摘要パターンについて：" +
                        $"科目コード＝{bestAccount}（{bestAccountName ?? ""}）を使用してください。" +
                        $"この科目について lookup_account を呼び出す必要はありません。" +
                        $"（確信度: {(int)(best.Confidence * 100)}%、サンプル数: {best.SampleCount}）",
                        $"【重要·已学习科目指定】对于该银行明细的摘要模式：" +
                        $"科目编码={bestAccount}（{bestAccountName ?? ""}）。" +
                        $"无需调用 lookup_account 查找该科目。" +
                        $"（置信度: {(int)(best.Confidence * 100)}%，样本数: {best.SampleCount}）"));
                }
            }
        }

        // 2. 查询银行交易历史记账模式（从已记账的银行交易中学习）
        if (!string.IsNullOrWhiteSpace(description))
        {
            var bankPatterns = await _patternService.GetBankDescriptionPatternsAsync(
                companyCode, description, isWithdrawal, 3, ct);
            if (bankPatterns.Count > 0)
            {
                confidence += 0.2m;
                sb.AppendLine();
                sb.AppendLine(Loc(language,
                    "【歴史参照】類似摘要の銀行明細の過去の仕訳パターン：",
                    "【历史参照】类似摘要的银行明细过去的记账模式："));
                foreach (var p in bankPatterns)
                {
                    var label = !string.IsNullOrWhiteSpace(p.AccountName) ? $"{p.AccountCode}({p.AccountName})" : p.AccountCode;
                    sb.AppendLine(Loc(language,
                        $"  - 科目 {label}（{p.UsageCount}回使用）",
                        $"  - 科目 {label}（使用{p.UsageCount}次）"));
                }

                if (bankPatterns.Count == 1 && bankPatterns[0].UsageCount >= 3)
                {
                    confidence += 0.15m;
                    sb.AppendLine(Loc(language,
                        $"  → この摘要は一貫して{bankPatterns[0].AccountCode}で処理されています。同じ科目で処理してください。",
                        $"  → 该摘要一贯使用{bankPatterns[0].AccountCode}记账。请沿用相同科目。"));
                }
            }
        }

        // 3. 查询类似金额的银行交易
        if (amount > 0)
        {
            var similar = await _patternService.GetSimilarVouchersAsync(companyCode, amount, description, 3, ct);
            if (similar.Count > 0)
            {
                confidence += 0.05m;
                sb.AppendLine();
                sb.AppendLine(Loc(language,
                    "【類似取引】金額が近い最近の銀行仕訳：",
                    "【类似交易】金额相近的近期银行凭证："));
                foreach (var v in similar.Take(3))
                {
                    sb.AppendLine(Loc(language,
                        $"  - {v.VoucherNo}: {v.Summary ?? "摘要なし"}, ¥{v.Amount:N0}, 借方{v.DebitAccount}/貸方{v.CreditAccount}",
                        $"  - {v.VoucherNo}: {v.Summary ?? "无摘要"}, ¥{v.Amount:N0}, 借方{v.DebitAccount}/贷方{v.CreditAccount}"));
                }
            }
        }

        // 追加关键字段信息
        if (!string.IsNullOrWhiteSpace(description))
            confidence += 0.05m;
        if (amount > 0)
            confidence += 0.05m;

        confidence = Math.Clamp(confidence, 0m, 0.98m);

        var historicalHints = sb.ToString();
        var confidenceInstructions = BuildConfidenceInstructions(confidence, language);

        _logger.LogInformation("[SkillContext/Bank] 增强上下文构建完成: desc={Desc}, amount={Amount}, confidence={Confidence:F2}",
            description, amount, confidence);

        return new EnrichedContext(historicalHints, confidenceInstructions, confidence);
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
            if (val.TryGetValue<string>(out var str))
            {
                if (decimal.TryParse(str, out var parsed)) return parsed;
                // 处理 "1800円" "¥1,800" 等格式：去掉非数字和非小数点字符
                var cleaned = new string(str.Where(c => char.IsDigit(c) || c == '.').ToArray());
                if (!string.IsNullOrWhiteSpace(cleaned) && decimal.TryParse(cleaned, out var cleanParsed))
                    return cleanParsed;
            }
        }
        return null;
    }

    /// <summary>
    /// 从解析数据的字段名和内容推断费用品类。
    /// 当 extraction 结果没有标准 category 字段时使用。
    /// </summary>
    private static string? InferCategoryFromData(JsonObject data)
    {
        // 序列化为字符串做关键词搜索
        var json = data.ToJsonString();

        // 检查字段名和内容中的关键词
        if (json.Contains("駐車", StringComparison.Ordinal)
            || json.Contains("パーキング", StringComparison.Ordinal)
            || json.Contains("parking", StringComparison.OrdinalIgnoreCase)
            || json.Contains("parkingFee", StringComparison.OrdinalIgnoreCase)
            || json.Contains("parkingLocation", StringComparison.OrdinalIgnoreCase))
            return "transportation";

        if (json.Contains("タクシー", StringComparison.Ordinal)
            || json.Contains("乗車", StringComparison.Ordinal)
            || json.Contains("交通", StringComparison.Ordinal)
            || json.Contains("高速", StringComparison.Ordinal)
            || json.Contains("taxi", StringComparison.OrdinalIgnoreCase))
            return "transportation";

        if (json.Contains("レストラン", StringComparison.Ordinal)
            || json.Contains("飲食", StringComparison.Ordinal)
            || json.Contains("会食", StringComparison.Ordinal)
            || json.Contains("食事", StringComparison.Ordinal)
            || json.Contains("dining", StringComparison.OrdinalIgnoreCase)
            || json.Contains("restaurant", StringComparison.OrdinalIgnoreCase))
            return "dining";

        return null;
    }

    /// <summary>
    /// 将发票解析的 category 枚举值映射为搜索关键词列表，
    /// 用于在历史凭证中查找同类型交易。
    /// </summary>
    private static IReadOnlyList<string> MapCategoryToKeywords(string category)
    {
        return category.ToLowerInvariant() switch
        {
            "transportation" => new[] { "駐車", "パーキング", "タクシー", "交通", "乗車", "高速", "parking", "taxi" },
            "dining" => new[] { "会食", "飲食", "レストラン", "食事", "会議費", "飲み", "餐", "dining" },
            "misc" or "other" => new[] { category },
            _ => new[] { category }
        };
    }

    private static string Loc(string language, string ja, string zh) =>
        string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? zh : ja;
}
