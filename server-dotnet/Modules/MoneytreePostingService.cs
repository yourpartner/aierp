using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

public sealed class MoneytreePostingService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly MoneytreePostingRuleService _ruleService;
    private readonly FinanceService _financeService;
    private readonly ILogger<MoneytreePostingService> _logger;
    private readonly ConcurrentDictionary<string, BankAccountCacheEntry> _bankAccountCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _accountNameCache = new(StringComparer.OrdinalIgnoreCase);
    
    // 手续费识别关键词
    private static readonly string[] BankFeeKeywords = { "振込手数料", "手数料", "振込ﾃｽｳﾘｮｳ", "ﾃｽｳﾘｮｳ" };
    // 银行手续费税率（10%）
    private const decimal BankFeeTaxRate = 10m;
    private static readonly TimeSpan BankCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly OtaPlatform[] OtaPlatforms =
    {
        new("BOOKING.COM", new[] { "BOOKING", "BOOKING.COM", "ﾌﾞｯｷﾝｸﾞ", "ブッキング" }),
        new("AGODA", new[] { "AGODA", "ｱｺﾞﾀﾞ", "アゴダ" }),
        new("EXPEDIA", new[] { "EXPEDIA", "ｴｸｽﾍﾟﾃﾞｨｱ", "エクスペディア" }),
        new("RAKUTEN OYADO", new[] { "RAKUTEN OYADO", "RAKUTEN", "楽天", "楽天トラベル", "ﾗｸﾃﾝ", "ラクテンステイ", "ﾗｸﾃﾝｽﾃｲ", "楽天ステイ" }),
        new("JALAN", new[] { "JALAN", "じゃらん", "ｼﾞｬﾗﾝ" }),
        new("AIRBNB", new[] { "AIRBNB", "ｴｱｰﾋﾞｰ", "エアビー" }),
        new("CTRIP/TRIP.COM", new[] { "TRIP.COM", "TRIPCOM", "CTRIP", "Ctrip", "シートリップ", "ｼｰﾄﾘｯﾌﾟ" }),
        new("HOTELS.COM", new[] { "HOTELS.COM", "HOTELS", "ﾎﾃﾙｽﾞ", "ホテルズ" }),
        new("IKYU", new[] { "IKYU", "一休", "ｲｯｷｭｳ" }),
        new("RELUX", new[] { "RELUX", "ﾘﾗｯｸｽ", "リラックス" }),
        new("JTB", new[] { "JTB", "ジェイティービー", "ＪＴＢ" })
    };

    public MoneytreePostingService(
        NpgsqlDataSource dataSource,
        MoneytreePostingRuleService ruleService,
        FinanceService financeService,
        ILogger<MoneytreePostingService> logger)
    {
        _dataSource = dataSource;
        _ruleService = ruleService;
        _financeService = financeService;
        _logger = logger;
    }

    // --- Flexible business partner matching helpers ---
    private static readonly Regex PartnerNoiseRegex = new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);
    private static readonly string[] PartnerStopwords =
    {
        "振込", "振替", "ﾌﾘｺﾐ", "ﾌﾘｶｴ", "ﾌﾘｶｴﾆｭｳｷﾝ", "入金", "出金",
        "普通預金", "当座預金", "PAYPAY", "ﾍﾟｲﾍﾟｲ", "銀行", "ﾊﾞﾝｺｳ"
    };
    private static readonly string[] CorporateTokens =
    {
        "株式会社", "（株）", "(株)", "（有）", "(有)", "有限会社", "合同会社", "合名会社", "合資会社",
        "KK", "K K", "K.K", "K.K.", "CO", "CO.", "CO.,LTD", "CO., LTD", "CO LTD", "LTD", "LTD.", "LIMITED", "INC", "INC.", "LLC", "GMBH"
    };

    private static string NormalizePartnerText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        // Normalize width/compat chars and upper-case for stable matching
        var s = input.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        // Replace typical separators with spaces
        s = s.Replace('\u3000', ' ').Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        foreach (var token in CorporateTokens)
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                s = s.Replace(token.ToUpperInvariant(), " ");
            }
        }
        foreach (var w in PartnerStopwords)
        {
            if (!string.IsNullOrWhiteSpace(w))
            {
                s = s.Replace(w.ToUpperInvariant(), " ");
            }
        }
        s = PartnerNoiseRegex.Replace(s, " ");
        // Collapse whitespace
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static string[] ExtractPartnerTokens(string? input)
    {
        var norm = NormalizePartnerText(input);
        if (string.IsNullOrWhiteSpace(norm)) return Array.Empty<string>();
        return norm
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static double TokenOverlapScore(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var setA = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
        var inter = setA.Count(x => setB.Contains(x));
        var denom = Math.Max(setA.Count, setB.Count);
        return denom == 0 ? 0 : (double)inter / denom;
    }

    private static double SimilarityScore(string input, string candidate)
    {
        var a = NormalizePartnerText(input);
        var b = NormalizePartnerText(candidate);
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 1.0;
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase))
        {
            // strong substring signal
            return 0.95;
        }
        var ta = ExtractPartnerTokens(a);
        var tb = ExtractPartnerTokens(b);
        var tokenScore = TokenOverlapScore(ta, tb);
        // Mild boost if any token appears as substring
        var substringBoost = ta.Any(t => b.Contains(t, StringComparison.OrdinalIgnoreCase)) ? 0.1 : 0.0;
        return Math.Min(1.0, tokenScore + substringBoost);
    }

    public async Task<MoneytreePostingResult> ProcessAsync(
        string companyCode,
        Auth.UserCtx user,
        int batchSize = 20,
        CancellationToken ct = default)
    {
        var rules = await _ruleService.ListAsync(companyCode, includeInactive: false, ct);
        if (rules.Count == 0)
        {
            return MoneytreePostingResult.Empty;
        }

        // 预加载手续费配对信息
        var feePairings = await BuildFeePairingsAsync(companyCode, ct);

        // One runId per processing batch; used to link transactions to the confirmation approval task (entity=moneytree_posting).
        var runId = Guid.NewGuid();
        var stats = new ProcessingStats { RunId = runId };
        for (var i = 0; i < batchSize; i++)
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            var row = await FetchNextPendingAsync(conn, tx, companyCode, ct);
            if (row is null)
            {
                await tx.RollbackAsync(ct);
                break;
            }

            // 如果这条是手续费且已被配对到其他入出金，跳过（等待入出金一起处理）
            if (feePairings.FeeToPayment.TryGetValue(row.Id, out var pairedPaymentId))
            {
                // 这是一条手续费，检查状态
                var feeStatus = await GetTransactionStatusAsync(conn, tx, row.Id, ct);
                if (feeStatus == "merged" || feeStatus == "linked")
                {
                    // 已经被处理了
                    await tx.RollbackAsync(ct);
                    stats.Apply(feeStatus == "posted" ? PostingOutcome.Posted : PostingOutcome.Linked, transactionId: row.Id);
                    continue;
                }
                
                // 检查配对的入出金状态，如果还未处理，跳过手续费（等待入出金来处理）
                var paymentStatus = await GetTransactionStatusAsync(conn, tx, pairedPaymentId, ct);
                if (paymentStatus == "pending" || paymentStatus == "needs_rule")
                {
                    // 入出金还未处理，跳过手续费，让入出金来带着手续费一起处理
                    await tx.RollbackAsync(ct);
                    _logger.LogDebug("[MoneytreePosting] Skipping fee {FeeId} - waiting for paired payment {PaymentId}", row.Id, pairedPaymentId);
                    continue;
                }
            }

            // 查找配对的手续费
            MoneytreeTransactionRow? pairedFee = null;
            if (feePairings.PaymentToFee.TryGetValue(row.Id, out var feeId))
            {
                pairedFee = await LoadTransactionForUpdateAsync(conn, tx, companyCode, feeId, ct);
            }

            var (outcome, voucherInfo, error) = await ProcessRowAsync(conn, tx, companyCode, row, rules, user, pairedFee, runId, ct);
            await tx.CommitAsync(ct);
            stats.Apply(outcome, voucherInfo, transactionId: row.Id, error: error);
            
            // 如果有配对的手续费且处理成功，也添加到返回结果以便前端同时更新显示
            if (pairedFee is not null && (outcome == PostingOutcome.Posted || outcome == PostingOutcome.Linked))
            {
                var feeStatus = outcome == PostingOutcome.Posted ? "posted" : "linked";
                stats.ProcessedItems.Add(new ProcessedItemInfo(pairedFee.Id, feeStatus, voucherInfo?.VoucherNo, null));
            }
        }

        // 处理完成后创建确认任务（无论是否有失败，都创建）
        if (stats.TotalCount > 0)
        {
            try
            {
                var taskId = await CreateConfirmationTaskAsync(companyCode, user, stats, ct);
                stats.ConfirmationTaskId = taskId;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MoneytreePosting] Failed to create confirmation task");
            }
        }

        return stats.ToResult();
    }

    public async Task<MoneytreePostingResult> ProcessSelectedAsync(
        string companyCode,
        Auth.UserCtx user,
        IReadOnlyCollection<Guid> transactionIds,
        CancellationToken ct = default)
    {
        if (transactionIds is null || transactionIds.Count == 0)
        {
            return MoneytreePostingResult.Empty;
        }

        var rules = await _ruleService.ListAsync(companyCode, includeInactive: false, ct);
        if (rules.Count == 0)
        {
            return MoneytreePostingResult.Empty;
        }

        // 预加载手续费配对信息（仅限选中的ID）
        var feePairings = await BuildFeePairingsForIdsAsync(companyCode, transactionIds, ct);
        var processedFeeIds = new HashSet<Guid>();

        // One runId per processing request; used to link transactions to the confirmation approval task (entity=moneytree_posting).
        var runId = Guid.NewGuid();
        var stats = new ProcessingStats { RunId = runId };
        var uniqueIds = new HashSet<Guid>(transactionIds);

        foreach (var id in uniqueIds)
        {
            // 如果这是一条已被合并处理的手续费，跳过
            // 不需要再调用 stats.Apply，因为手续费的状态已经在处理主交易时通过 ProcessedItems.Add 添加了
            if (processedFeeIds.Contains(id))
            {
                continue;
            }

            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            var row = await LoadTransactionForUpdateAsync(conn, tx, companyCode, id, ct);
            if (row is null)
            {
                await tx.RollbackAsync(ct);
                stats.Apply(PostingOutcome.Skipped, transactionId: id);
                continue;
            }

            // 如果这条是手续费且已被配对到其他入出金
            if (feePairings.FeeToPayment.TryGetValue(id, out var paymentId))
            {
                if (uniqueIds.Contains(paymentId))
                {
                    // 这条手续费会在处理对应入出金时一起处理，跳过
                await tx.RollbackAsync(ct);
                continue;
            }
                else
                {
                    // 手续费有配对的入出金，但用户没有选择它，提示用户选择对应的入出金
                    await tx.RollbackAsync(ct);
                    stats.Apply(PostingOutcome.Skipped, transactionId: id, error: $"この手数料は別の入出金明細と紐付けられています。対応する入出金明細を選択してください。");
                    continue;
                }
            }

            // 查找配对的手续费（即使手续费不在选中列表中，也尝试合并）
            MoneytreeTransactionRow? pairedFee = null;
            if (feePairings.PaymentToFee.TryGetValue(id, out var feeId))
            {
                // 先检查手续费状态
                var feeStatus = await GetTransactionStatusAsync(conn, tx, feeId, ct);
                if (feeStatus == "pending" || feeStatus == "needs_rule")
            {
                pairedFee = await LoadTransactionForUpdateAsync(conn, tx, companyCode, feeId, ct);
                if (pairedFee is not null)
                {
                    processedFeeIds.Add(feeId);
                        _logger.LogInformation("[MoneytreePosting] Auto-including paired fee {FeeId} for transaction {TransactionId}", feeId, id);
                }
                }
                // 如果手续费已被处理，pairedFee 保持 null
            }

            var (outcome, voucherInfo, error) = await ProcessRowAsync(conn, tx, companyCode, row, rules, user, pairedFee, runId, ct);
            await tx.CommitAsync(ct);
            stats.Apply(outcome, voucherInfo, transactionId: id, error: error);
            
            // 如果有配对的手续费且处理成功，也添加到返回结果以便前端同时更新显示
            if (pairedFee is not null && (outcome == PostingOutcome.Posted || outcome == PostingOutcome.Linked))
            {
                var feeStatus = outcome == PostingOutcome.Posted ? "posted" : "linked";
                stats.ProcessedItems.Add(new ProcessedItemInfo(pairedFee.Id, feeStatus, voucherInfo?.VoucherNo, null));
            }
        }

        // 手工在银行明细画面触发的自动记账（选中明细）：
        // 不创建任务面板的确认任务，用户应在当前画面直接确认结果。

        return stats.ToResult();
    }

    private async Task<(PostingOutcome Outcome, PostedVoucherInfo? VoucherInfo, string? Error)> ProcessRowAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string companyCode,
        MoneytreeTransactionRow row,
        IReadOnlyList<MoneytreePostingRuleService.MoneytreePostingRule> rules,
        Auth.UserCtx user,
        MoneytreeTransactionRow? pairedFee,
        Guid postingRunId,
        CancellationToken ct)
    {
        var amount = row.GetPositiveAmount();
        if (amount <= 0m)
        {
            await UpdateStatusAsync(conn, tx, row.Id, "skipped", "amount is not positive", null, null, null, null, null, postingRunId, ct);
            return (PostingOutcome.Skipped, null, "amount is not positive");
        }

        var rule = FindMatchingRule(rules, row);
        if (rule is null)
        {
            await UpdateStatusAsync(conn, tx, row.Id, "needs_rule", "no rule matched current transaction", null, null, null, null, null, postingRunId, ct);
            return (PostingOutcome.NeedsRule, null, "no rule matched current transaction");
        }

        MoneytreeAction action;
        try
        {
            action = MoneytreeAction.Parse(rule.Action);
        }
        catch (Exception ex)
        {
            var errMsg1 = $"invalid action definition: {ex.Message}";
            await UpdateStatusAsync(conn, tx, row.Id, "failed", errMsg1, null, null, rule.Id, rule.Title, null, postingRunId, ct);
            return (PostingOutcome.Failed, null, errMsg1);
        }

        string debitAccount;
        string creditAccount;
        string? debitAccountName = null;
        string? creditAccountName = null;
        bool learnedFromHistory = false;
        try
        {
            debitAccount = await ResolveAccountCodeAsync(action.DebitAccount, companyCode, row, ct);
            creditAccount = await ResolveAccountCodeAsync(action.CreditAccount, companyCode, row, ct);
            
            // 判断交易类型（用于历史学习）
            var isWithdrawalForLearning = (row.WithdrawalAmount ?? 0m) < 0m;
            var isDepositForLearning = (row.DepositAmount ?? 0m) > 0m;
            
            // 尝试从历史凭证学习科目
            if (isWithdrawalForLearning)
            {
                // 出金：学习借方科目（银行科目在贷方）
                var bankAccountCode = creditAccount;
                var learned = await LearnAccountFromHistoryAsync(conn, companyCode, row, debitAccount, bankAccountCode, "DR", ct);
                if (learned.HasValue && !string.IsNullOrWhiteSpace(learned.Value.AccountCode))
                {
                    _logger.LogInformation("[MoneytreePosting] 出金历史学习: 借方 {Original} -> {Learned} ({Name})",
                        debitAccount, learned.Value.AccountCode, learned.Value.AccountName);
                    debitAccount = learned.Value.AccountCode;
                    debitAccountName = learned.Value.AccountName;
                    learnedFromHistory = true;
                }
            }
            else if (isDepositForLearning)
            {
                // 入金：从历史凭证学习应收科目
                // 逻辑：销售发票是 借方:売掛金(152) / 贷方:売上(612)
                // 入金时应冲销売掛金，所以入金凭证应该是 借方:银行 / 贷方:売掛金(152)
                // 因此搜索历史凭证中该交易对手的借方应收科目
                var bankAccountCode = debitAccount;
                var learned = await LearnAccountFromHistoryAsync(conn, companyCode, row, creditAccount, bankAccountCode, "DR", ct);
                if (learned.HasValue && !string.IsNullOrWhiteSpace(learned.Value.AccountCode))
                {
                    _logger.LogInformation("[MoneytreePosting] 入金历史学习: 贷方 {Original} -> {Learned} ({Name})",
                        creditAccount, learned.Value.AccountCode, learned.Value.AccountName);
                    creditAccount = learned.Value.AccountCode;
                    creditAccountName = learned.Value.AccountName;
                    learnedFromHistory = true;
                }
            }
            
            if (!learnedFromHistory)
            {
                debitAccountName = await GetAccountNameAsync(conn, companyCode, debitAccount, ct);
                creditAccountName = await GetAccountNameAsync(conn, companyCode, creditAccount, ct);
            }
            else
            {
                // 历史学习成功时，只需获取未学习科目的名称
                if (isWithdrawalForLearning)
                {
                    creditAccountName = await GetAccountNameAsync(conn, companyCode, creditAccount, ct);
                }
                else
                {
                    debitAccountName = await GetAccountNameAsync(conn, companyCode, debitAccount, ct);
                }
            }
        }
        catch (Exception ex)
        {
            var errMsg2 = $"account resolve failed: {ex.Message}";
            await UpdateStatusAsync(conn, tx, row.Id, "failed", errMsg2, null, null, rule.Id, rule.Title, null, postingRunId, ct);
            return (PostingOutcome.Failed, null, errMsg2);
        }

        var debitMeta = new LineMeta();
        var creditMeta = new LineMeta();
        CounterpartyResult? counterparty = null;
        try
        {
            counterparty = await ResolveCounterpartyAsync(companyCode, action.Counterparty, row, ct);
            if (counterparty is not null)
            {
                ApplyCounterpartyToLine(string.Equals(counterparty.AssignLine, "debit", StringComparison.OrdinalIgnoreCase) ? debitMeta : creditMeta, counterparty);
            }
            else
            {
                // Fallback: if the rule does not specify counterparty config, try infer from description.
                // This is especially important for AR/AP accounts that require customerId/vendorId.
                var desc = row.Description ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    var isDepositFallback = (row.DepositAmount ?? 0m) > 0m || (row.NetAmount ?? 0m) > 0m;
                    var preferredType = isDepositFallback ? "customer" : "vendor";
                    var assignLine = isDepositFallback ? "debit" : "credit"; // bank is credit for deposit, debit for withdrawal
                    var inferredConfig = new MoneytreeCounterparty(
                        Types: new[] { preferredType },
                        Code: null,
                        NameKeywords: new[] { "{description}" },
                        EmploymentTypes: null,
                        ActiveOnly: true,
                        AssignLine: assignLine,
                        FallbackType: null,
                        FallbackCode: null);

                    _logger.LogInformation("[MoneytreePosting] Counterparty fallback: action.counterparty is null or unresolved. Trying infer from description. preferredType={Type} assignLine={AssignLine} desc={Desc}",
                        preferredType, assignLine, desc);

                    // IMPORTANT: do NOT cross-match customer/vendor.
                    // Deposit must resolve to customer only; withdrawal must resolve to vendor only.
                    var inferred = await ResolveBusinessPartnerAsync(companyCode, preferredType, inferredConfig, row, ct);

                    if (inferred is not null)
                    {
                        counterparty = inferred;
                        ApplyCounterpartyToLine(string.Equals(counterparty.AssignLine, "debit", StringComparison.OrdinalIgnoreCase) ? debitMeta : creditMeta, counterparty);
                        _logger.LogInformation("[MoneytreePosting] Counterparty fallback: matched kind={Kind} id={Id} name={Name} assignLine={AssignLine}",
                            counterparty.Kind, counterparty.Id, counterparty.Name, counterparty.AssignLine);
                    }
                    else
                    {
                        _logger.LogWarning("[MoneytreePosting] Counterparty fallback: NO MATCH desc={Desc}", desc);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            var errMsg3 = $"counterparty resolve failed: {ex.Message}";
            await UpdateStatusAsync(conn, tx, row.Id, "failed", errMsg3, null, null, rule.Id, rule.Title, null, postingRunId, ct);
            return (PostingOutcome.Failed, null, errMsg3);
        }

        OpenItemReservation? reservation = null;
        Guid? clearedOpenItemId = null;
        CounterpartyResult? settlementPartner = counterparty;
        var settlement = action.Settlement;
        if (settlement?.PartnerOverride is not null)
        {
            settlementPartner = await ResolveCounterpartyAsync(companyCode, settlement.PartnerOverride, row, ct) ?? settlementPartner;
        }
        string? settlementPartnerId = settlementPartner?.Id;
        if (!string.IsNullOrWhiteSpace(settlement?.PartnerId))
        {
            settlementPartnerId = settlement.PartnerId;
        }
        if (settlement?.UseCounterparty == true && settlementPartnerId is null && counterparty is not null)
        {
            settlementPartnerId = counterparty.Id;
            settlementPartner ??= counterparty;
        }

        if (settlement?.Enabled == true)
        {
            if (!string.IsNullOrWhiteSpace(settlementPartnerId))
            {
                ApplyCounterpartyToLine(string.Equals(settlement.TargetLine, "debit", StringComparison.OrdinalIgnoreCase) ? debitMeta : creditMeta, settlementPartner!);
            }

            try
            {
                // Determine direction once for settlement matching.
                var hasWithdrawalAmount0 = (row.WithdrawalAmount ?? 0m) < 0m;
                var hasDepositAmount0 = (row.DepositAmount ?? 0m) > 0m;
                bool isDepositForSettlement;
                if (hasDepositAmount0 || hasWithdrawalAmount0)
                {
                    isDepositForSettlement = hasDepositAmount0 && !hasWithdrawalAmount0;
                }
                else
                {
                    var net0 = row.NetAmount ?? 0m;
                    isDepositForSettlement = net0 > 0m;
                }

                var targetLine = string.Equals(settlement.TargetLine, "debit", StringComparison.OrdinalIgnoreCase) ? "debit" : "credit";
                string? accountForMatch = settlement.AccountCode ?? (targetLine == "debit" ? debitAccount : creditAccount);
                var toleranceValue = settlement.Tolerance ?? 0m;

                _logger.LogInformation("[MoneytreePosting] Settlement search: targetLine={TargetLine}, accountForMatch={Account}, settlementPartnerId={PartnerId}, amount={Amount}, tolerance={Tolerance}",
                    targetLine, accountForMatch, settlementPartnerId, amount, toleranceValue);

                if (settlementPartnerId is not null)
                {
                    reservation = await ReserveOpenItemAsync(conn, tx, companyCode, accountForMatch!, settlementPartnerId, amount, toleranceValue, true, ct);
                    _logger.LogInformation("[MoneytreePosting] Settlement ReserveOpenItemAsync result: reservation={Reservation}", reservation is not null ? $"found {reservation.PrimaryId}" : "null");
                }
                
                // If partnerId is known but rule didn't specify settlement.AccountCode, allow matching open item across accounts.
                // This keeps the solution generic (no hardcoded account codes) while avoiding amount-only matching.
                if (reservation is null && settlementPartnerId is not null && string.IsNullOrWhiteSpace(settlement.AccountCode))
                {
                    var postingDateForSettlement = (row.TransactionDate ?? row.ImportedAt.UtcDateTime).Date;
                    var matchedAccount = await FindUniqueOpenItemAccountByPartnerAndAmountAsync(
                        conn, tx, companyCode, settlementPartnerId, amount, toleranceValue, postingDateForSettlement, forUpdate: true, ct);
                    if (!string.IsNullOrWhiteSpace(matchedAccount))
                    {
                        accountForMatch = matchedAccount;
                        reservation = await ReserveOpenItemAsync(conn, tx, companyCode, accountForMatch!, settlementPartnerId, amount, toleranceValue, true, ct);
                        _logger.LogInformation("[MoneytreePosting] Settlement cross-account match: accountForMatch={Account}, reservation={Reservation}",
                            accountForMatch, reservation is not null ? $"found {reservation.PrimaryId}" : "null");
                    }
                }

                if (reservation is null && ShouldMatchPlatform(settlement.PlatformGroup))
                {
                    reservation = await ReserveOpenItemByPlatformAsync(conn, tx, companyCode, accountForMatch!, row, amount, toleranceValue, true, ct);
                    
                    // 如果通过 OTA 平台匹配成功，从 open item 获取 partner_id 并应用到凭证行
                    if (reservation is not null && settlementPartnerId is null)
                    {
                        var openItemPartnerId = await GetOpenItemPartnerIdAsync(conn, tx, reservation.PrimaryId, ct);
                        if (!string.IsNullOrWhiteSpace(openItemPartnerId))
                        {
                            settlementPartnerId = openItemPartnerId;
                            settlementPartner = new CounterpartyResult("customer", openItemPartnerId, null, settlement.TargetLine);
                            var targetMeta = string.Equals(settlement.TargetLine, "debit", StringComparison.OrdinalIgnoreCase) ? debitMeta : creditMeta;
                            ApplyCounterpartyToLine(targetMeta, settlementPartner);
                            // 同时设置 paymentDate 为银行取引日期（某些科目如売掛金1100要求支付日期）
                            targetMeta.PaymentDate = (row.TransactionDate ?? row.ImportedAt.UtcDateTime).Date;
                            _logger.LogInformation("[MoneytreePosting] Applied partner_id {PartnerId} and paymentDate from open item to voucher line", openItemPartnerId);
                        }
                    }
                }

                if (reservation is not null)
                {
                    // 找到了匹配的open item，使用清账科目替换原来的科目
                    if (targetLine == "debit" && debitAccount != accountForMatch)
                    {
                        _logger.LogInformation("[MoneytreePosting] 清账成功，更新借方科目: {Old} -> {New}", debitAccount, accountForMatch);
                        debitAccount = accountForMatch!;
                        debitAccountName = await GetAccountNameAsync(conn, companyCode, debitAccount, ct);
                    }
                    else if (targetLine == "credit" && creditAccount != accountForMatch)
                    {
                        _logger.LogInformation("[MoneytreePosting] 清账成功，更新贷方科目: {Old} -> {New}", creditAccount, accountForMatch);
                        creditAccount = accountForMatch!;
                        creditAccountName = await GetAccountNameAsync(conn, companyCode, creditAccount, ct);
                    }
                }
                else
                {
                    if (settlement.RequireMatch)
                    {
                        await UpdateStatusAsync(conn, tx, row.Id, "failed", "no open item matched for clearing", null, null, rule.Id, rule.Title, null, postingRunId, ct);
                        return (PostingOutcome.Failed, null, "no open item matched for clearing");
                    }

                    // 只有在没有历史学习成功时才应用fallback科目
                    // 历史学习的优先级高于settlement fallback
                    if (!learnedFromHistory && !string.IsNullOrWhiteSpace(settlement.FallbackAccountCode))
                    {
                        if (string.Equals(settlement.FallbackLine, "debit", StringComparison.OrdinalIgnoreCase))
                        {
                            debitAccount = settlement.FallbackAccountCode!;
                            debitAccountName = await GetAccountNameAsync(conn, companyCode, debitAccount, ct);
                        }
                        else
                        {
                            creditAccount = settlement.FallbackAccountCode!;
                            creditAccountName = await GetAccountNameAsync(conn, companyCode, creditAccount, ct);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                var errMsg4 = $"open item lookup failed: {ex.Message}";
                await UpdateStatusAsync(conn, tx, row.Id, "failed", errMsg4, null, null, rule.Id, rule.Title, null, postingRunId, ct);
                return (PostingOutcome.Failed, null, errMsg4);
            }
        }

        // paymentDate requirement is controlled by account field rules (accounts.payload.fieldRules.paymentDate),
        // not by presence of customerId/vendorId. Mirror FinanceService validation semantics:
        // - If rule is "required": ensure paymentDate is filled (default to transaction date)
        // - If rule is "hidden": ensure paymentDate is empty
        var paymentDateForLine = (row.TransactionDate ?? row.ImportedAt.UtcDateTime).Date;
        var paymentDateRuleCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        async Task<string?> GetFieldRuleAsync(string accountCode, string field)
        {
            var key = $"{accountCode}::{field}";
            if (paymentDateRuleCache.TryGetValue(key, out var cached)) return cached;
            await using var q = conn.CreateCommand();
            q.Transaction = tx;
            q.CommandText = "SELECT payload->'fieldRules'->>$3 FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1";
            q.Parameters.AddWithValue(companyCode);
            q.Parameters.AddWithValue(accountCode);
            q.Parameters.AddWithValue(field);
            var obj = await q.ExecuteScalarAsync(ct);
            var rule = obj is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
            paymentDateRuleCache[key] = rule;
            return rule;
        }

        async Task ApplyPaymentDateRuleAsync(string accountCode, LineMeta meta, string lineLabel)
        {
            var rule = await GetFieldRuleAsync(accountCode, "paymentDate");
            if (string.Equals(rule, "required", StringComparison.OrdinalIgnoreCase))
            {
                if (!meta.PaymentDate.HasValue)
                {
                    meta.PaymentDate = paymentDateForLine;
                    _logger.LogInformation("[MoneytreePosting] paymentDate auto-filled by account rule (required): account={Account} line={Line} date={Date}",
                        accountCode, lineLabel, paymentDateForLine.ToString("yyyy-MM-dd"));
                }
            }
            else if (string.Equals(rule, "hidden", StringComparison.OrdinalIgnoreCase))
            {
                if (meta.PaymentDate.HasValue)
                {
                    meta.PaymentDate = null;
                    _logger.LogInformation("[MoneytreePosting] paymentDate cleared by account rule (hidden): account={Account} line={Line}",
                        accountCode, lineLabel);
            }
        }
        }

        await ApplyPaymentDateRuleAsync(debitAccount, debitMeta, "debit");
        await ApplyPaymentDateRuleAsync(creditAccount, creditMeta, "credit");

        // 既存凭证检查：检查是否已存在匹配的凭证，如果是出金场景则尝试自动关联
        var postingDate = (row.TransactionDate ?? row.ImportedAt.UtcDateTime).Date;
        var effectivePartnerId = settlementPartnerId ?? counterparty?.Id;
        // 对于入金，银行科目在借方；对于出金，银行科目在贷方
        var hasWithdrawalAmount = (row.WithdrawalAmount ?? 0m) < 0m;
        var hasDepositAmount = (row.DepositAmount ?? 0m) > 0m;
        bool isDeposit, isWithdrawal;
        if (hasDepositAmount || hasWithdrawalAmount)
        {
            isDeposit = hasDepositAmount && !hasWithdrawalAmount;
            isWithdrawal = hasWithdrawalAmount && !hasDepositAmount;
        }
        else
        {
            var net = row.NetAmount ?? 0m;
            isDeposit = net > 0m;
            isWithdrawal = net < 0m;
        }
        var bankAccountForCheck = isWithdrawal ? creditAccount : debitAccount;
        // 构建凭证摘要用于检查
        var summaryForCheck = action.SummaryTemplate?.Replace("{description}", row.Description ?? "") ?? row.Description;

        // 计算合计金额（包含配对的手续费）
        var pairedFeeAmount = pairedFee?.GetPositiveAmount() ?? 0m;
        var totalAmountWithFee = amount + pairedFeeAmount;

            try
            {
            // 优先尝试用合计金额（主体+手续费）匹配，如果失败再尝试主体金额
            var (isMatch, existingVoucherId, existingVoucherNo, isExactMatch, matchCount) = await FindMatchingExistingVoucherAsync(
                conn, tx, companyCode, effectivePartnerId, totalAmountWithFee, postingDate, bankAccountForCheck, 
                isWithdrawal, summaryForCheck, ct);
            
            // 如果合计金额没匹配到且有手续费，尝试只用主体金额匹配
            if (!isMatch && pairedFeeAmount > 0m)
            {
                (isMatch, existingVoucherId, existingVoucherNo, isExactMatch, matchCount) = await FindMatchingExistingVoucherAsync(
                    conn, tx, companyCode, effectivePartnerId, amount, postingDate, bankAccountForCheck, 
                    isWithdrawal, summaryForCheck, ct);
            }
            
            if (isMatch && existingVoucherId.HasValue)
            {
                // 出金场景：自动关联（选择最接近日期的凭证）
                // 入金场景：匹配1个→自动关联，匹配多个→标记为疑似重复
                var shouldLink = isWithdrawal || matchCount == 1;
                
                if (shouldLink)
                {
                    // 自动关联到既存凭证
                    var matchInfo = matchCount > 1 ? $"（{matchCount}件中最も近い）" : "";
                    var msg = isExactMatch
                        ? $"既存伝票に紐付け済み（完全一致）{matchInfo}：{existingVoucherNo}"
                        : $"既存伝票に紐付け済み（日付許容範囲内）{matchInfo}：{existingVoucherNo}";
                    _logger.LogInformation("[MoneytreePosting] auto-linking {Type} transaction {Id} to existing voucher {VoucherNo}: partnerId={PartnerId}, amount={Amount}, totalWithFee={TotalWithFee}, date={Date}, matchCount={MatchCount}",
                        isWithdrawal ? "withdrawal" : "deposit", row.Id, existingVoucherNo, effectivePartnerId, amount, totalAmountWithFee, postingDate.ToString("yyyy-MM-dd"), matchCount);
                    
                    await UpdateStatusAsync(conn, tx, row.Id, "linked", msg, existingVoucherId, existingVoucherNo, rule?.Id, rule?.Title, null, postingRunId, ct);
                    
                    // 如果有配对的手续费，也标记为 linked
                    if (pairedFee is not null && pairedFeeAmount > 0m)
                    {
                        var feeMsg = $"既存伝票に紐付け済み（主明細に随伴）：{existingVoucherNo}";
                        await UpdateStatusAsync(conn, tx, pairedFee.Id, "linked", feeMsg, existingVoucherId, existingVoucherNo, rule?.Id, rule?.Title, null, postingRunId, ct);
                        _logger.LogInformation("[MoneytreePosting] auto-linking paired fee {FeeId} to existing voucher {VoucherNo}", pairedFee.Id, existingVoucherNo);
                    }
                    
                    var linkedInfo = new PostedVoucherInfo(row.Id, existingVoucherId, existingVoucherNo, totalAmountWithFee, row.Description, rule?.Title, null);
                    return (PostingOutcome.Linked, linkedInfo, msg);
                }
                else
                {
                    // 入金匹配到多个：标记为疑似重复，让用户决定
                    var msg = $"疑似重複：{matchCount}件の既存伝票が一致。金額={amount}, 日付={postingDate:yyyy-MM-dd}, 銀行科目={bankAccountForCheck}";
                    _logger.LogInformation("[MoneytreePosting] duplicate suspected for deposit transaction {Id}: {Message}, matchCount={MatchCount}", row.Id, msg, matchCount);
                    await UpdateStatusAsync(conn, tx, row.Id, "duplicate_suspected", msg, null, null, rule?.Id, rule?.Title, null, postingRunId, ct);
                    return (PostingOutcome.DuplicateSuspected, null, msg);
                }
            }
            }
            catch (Exception ex)
            {
            _logger.LogWarning(ex, "[MoneytreePosting] existing voucher check failed for transaction {Id}", row.Id);
            // 检查失败不影响后续处理，继续创建凭证
        }

        // 准备手续费信息
        BankFeeInfo? feeInfo = null;
        if (pairedFee is not null)
        {
            var feeAmount = pairedFee.GetPositiveAmount();
            if (feeAmount > 0m)
            {
                var inputTaxAccountCode = await GetInputTaxAccountCodeAsync(conn, companyCode, ct);
                var bankFeeAccountCode = action.BankFeeAccountCode ?? await GetBankFeeAccountCodeAsync(conn, companyCode, ct);
                feeInfo = new BankFeeInfo(
                    pairedFee.Id,
                    feeAmount,
                    bankFeeAccountCode,
                    inputTaxAccountCode,
                    creditAccount // 银行科目
                );
            }
        }

        string insertedJson;
        Guid? voucherId = null;
        string? voucherNo = null;
        
        // 如果有清账reservation，查询被清账凭证的信息
        List<ClearedItemInfo>? clearedItemInfos = null;
        if (reservation is not null)
        {
            clearedItemInfos = await GetClearedItemInfosAsync(conn, tx, reservation, ct);
        }
        
        try
        {
            // 如果有清账reservation，传递清账目标行和被清账凭证信息
            var clearingTargetLine = reservation is not null ? (settlement?.TargetLine ?? "credit") : null;
            _logger.LogInformation("[MoneytreePosting] Creating voucher with debitAccount={Debit}, creditAccount={Credit}, learnedFromHistory={Learned}, clearingTargetLine={ClearingLine}",
                debitAccount, creditAccount, learnedFromHistory, clearingTargetLine);
            using var payloadDoc = BuildVoucherPayload(action, row, amount, debitAccount, creditAccount, debitMeta, creditMeta, feeInfo, clearingTargetLine, clearedItemInfos);
            // Moneytree 是后台自动场景，传入 targetUserId 以便创建警报任务
            var (json, _) = await _financeService.CreateVoucher(
                companyCode, "vouchers", payloadDoc.RootElement, user,
                VoucherSource.Auto, targetUserId: user.UserId);
            insertedJson = json;
            (voucherId, voucherNo) = ExtractVoucherInfo(insertedJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MoneytreePosting] create voucher failed for transaction {Id}", row.Id);
            await UpdateStatusAsync(conn, tx, row.Id, "failed", ex.Message, null, null, rule.Id, rule.Title, null, postingRunId, ct);
            return (PostingOutcome.Failed, null, ex.Message);
        }

        if (reservation is not null)
        {
            try
            {
                clearedOpenItemId = await ApplyOpenItemClearingAsync(conn, tx, reservation, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MoneytreePosting] clearing open item failed for transaction {Id}", row.Id);
                var errMsg5 = $"clearing failed: {ex.Message}";
                await UpdateStatusAsync(conn, tx, row.Id, "failed", errMsg5, voucherId, voucherNo, rule.Id, rule.Title, null, postingRunId, ct);
                return (PostingOutcome.Failed, null, errMsg5);
            }
        }

        await UpdateStatusAsync(conn, tx, row.Id, "posted", null, voucherId, voucherNo, rule.Id, rule.Title, clearedOpenItemId, postingRunId, ct);
        
        // 更新配对的手续费明细状态（与主交易相同，显示同一凭证号）
        if (pairedFee is not null && feeInfo is not null)
        {
            await UpdateStatusAsync(conn, tx, pairedFee.Id, "posted", $"posted with payment voucher {voucherNo}", voucherId, voucherNo, rule.Id, rule.Title, null, postingRunId, ct);
            _logger.LogInformation("[MoneytreePosting] bank fee {FeeId} posted into payment voucher {VoucherNo}", pairedFee.Id, voucherNo);
        }
        
        // 构建凭证信息用于创建确认任务
        var voucherInfo = new PostedVoucherInfo(
            row.Id,
            voucherId,
            voucherNo,
            amount + (feeInfo?.TotalAmount ?? 0m),
            row.Description,
            rule.Title,
            action.NotificationTargetRole
        );
        
        return (PostingOutcome.Posted, voucherInfo, null);
    }

    public async Task<IReadOnlyList<MoneytreeSimulationResult>> SimulateAsync(
        string companyCode,
        IReadOnlyList<Guid> transactionIds,
        CancellationToken ct = default)
    {
        var simulations = new List<MoneytreeSimulationResult>();
        if (transactionIds.Count == 0)
        {
            return simulations;
        }

        var rules = await _ruleService.ListAsync(companyCode, includeInactive: false, ct);
        if (rules.Count == 0)
        {
            return transactionIds.Select(id => new MoneytreeSimulationResult(id, "needs_rule", "no active rules available", null, null, null, null, null, null, false)).ToList();
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        foreach (var id in transactionIds)
        {
            var row = await LoadTransactionAsync(conn, companyCode, id, ct);
            if (row is null)
            {
                simulations.Add(new MoneytreeSimulationResult(id, "not_found", "transaction not found", null, null, null, null, null, null, false));
                continue;
            }

            var amount = row.GetPositiveAmount();
            if (amount <= 0m)
            {
                simulations.Add(new MoneytreeSimulationResult(id, "skipped", "amount is not positive", null, null, null, null, null, null, false));
                continue;
            }

            var rule = FindMatchingRule(rules, row);
            if (rule is null)
            {
                simulations.Add(new MoneytreeSimulationResult(id, "needs_rule", "no rule matched current transaction", null, null, null, null, null, null, false));
                continue;
            }

            MoneytreeAction action;
            try
            {
                action = MoneytreeAction.Parse(rule.Action);
            }
            catch (Exception ex)
            {
                simulations.Add(new MoneytreeSimulationResult(id, "failed", $"invalid action definition: {ex.Message}", rule.Title, null, null, null, null, null, false));
                continue;
            }

            try
            {
                var debitAccount = await ResolveAccountCodeAsync(action.DebitAccount, companyCode, row, ct);
                var creditAccount = await ResolveAccountCodeAsync(action.CreditAccount, companyCode, row, ct);
                var debitAccountName = await GetAccountNameAsync(conn, companyCode, debitAccount, ct);
                var creditAccountName = await GetAccountNameAsync(conn, companyCode, creditAccount, ct);
                var debitMeta = new LineMeta();
                var creditMeta = new LineMeta();
                var counterparty = await ResolveCounterpartyAsync(companyCode, action.Counterparty, row, ct);
                if (counterparty is not null)
                {
                    ApplyCounterpartyToLine(string.Equals(counterparty.AssignLine, "debit", StringComparison.OrdinalIgnoreCase) ? debitMeta : creditMeta, counterparty);
                }

                var settlement = action.Settlement;
                CounterpartyResult? settlementPartner = counterparty;
                if (settlement?.PartnerOverride is not null)
                {
                    settlementPartner = await ResolveCounterpartyAsync(companyCode, settlement.PartnerOverride, row, ct) ?? settlementPartner;
                }
                string? settlementPartnerId = settlementPartner?.Id;
                if (!string.IsNullOrWhiteSpace(settlement?.PartnerId))
                {
                    settlementPartnerId = settlement.PartnerId;
                }
                if (settlement?.UseCounterparty == true && settlementPartnerId is null && counterparty is not null)
                {
                    settlementPartnerId = counterparty.Id;
                    settlementPartner ??= counterparty;
                }

                OpenItemReservation? previewReservation = null;
                if (settlement?.Enabled == true)
                {
                    if (settlementPartnerId is not null)
                    {
                        ApplyCounterpartyToLine(string.Equals(settlement.TargetLine, "debit", StringComparison.OrdinalIgnoreCase) ? debitMeta : creditMeta, settlementPartner!);
                    }
                    var accountForMatch = settlement.AccountCode ?? (string.Equals(settlement.TargetLine, "debit", StringComparison.OrdinalIgnoreCase) ? debitAccount : creditAccount);
                    var toleranceValue = settlement.Tolerance ?? 0m;
                    if (settlementPartnerId is not null)
                    {
                        previewReservation = await ReserveOpenItemAsync(conn, tx: null, companyCode, accountForMatch!, settlementPartnerId, amount, toleranceValue, false, ct);
                    }
                    if (previewReservation is null && ShouldMatchPlatform(settlement.PlatformGroup))
                    {
                        previewReservation = await ReserveOpenItemByPlatformAsync(conn, tx: null, companyCode, accountForMatch!, row, amount, toleranceValue, false, ct);
                    }
                    if (previewReservation is null && settlement.RequireMatch)
                    {
                        simulations.Add(new MoneytreeSimulationResult(id, "failed", "no open item matched for clearing", rule.Title, null, debitAccount, null, creditAccount, null, false));
                        continue;
                    }
                    if (previewReservation is null && !string.IsNullOrWhiteSpace(settlement.FallbackAccountCode))
                    {
                        if (string.Equals(settlement.FallbackLine, "debit", StringComparison.OrdinalIgnoreCase))
                            debitAccount = settlement.FallbackAccountCode!;
                        else
                            creditAccount = settlement.FallbackAccountCode!;
                    }
                }

                using var voucherDoc = BuildVoucherPayload(action, row, amount, debitAccount, creditAccount, debitMeta, creditMeta, null);
                var preview = JsonNode.Parse(voucherDoc.RootElement.GetRawText());
                simulations.Add(new MoneytreeSimulationResult(
                    id,
                    "ready",
                    "rule matched",
                    rule.Title,
                    preview,
                    debitAccount,
                    debitAccountName,
                    creditAccount,
                    creditAccountName,
                    previewReservation is not null));
            }
            catch (Exception ex)
            {
                simulations.Add(new MoneytreeSimulationResult(id, "failed", ex.Message, rule.Title, null, null, null, null, null, false));
            }
        }

        return simulations;
    }

    private async Task<MoneytreeTransactionRow?> FetchNextPendingAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string companyCode, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, transaction_date, deposit_amount, withdrawal_amount,
       COALESCE(deposit_amount,0) - COALESCE(withdrawal_amount,0) AS net_amount,
       balance, currency, bank_name,
       description, account_name, account_number, imported_at, created_at
FROM moneytree_transactions
WHERE company_code = $1 AND posting_status = 'pending'
ORDER BY transaction_date NULLS LAST, COALESCE(row_sequence, 0)
FOR UPDATE SKIP LOCKED
LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Transaction = tx;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return MoneytreeTransactionRow.FromReader(reader);
        }
        return null;
    }

    private async Task<MoneytreeTransactionRow?> LoadTransactionAsync(NpgsqlConnection conn, string companyCode, Guid id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, transaction_date, deposit_amount, withdrawal_amount,
       COALESCE(deposit_amount,0) - COALESCE(withdrawal_amount,0) AS net_amount,
       balance, currency, bank_name,
       description, account_name, account_number, imported_at, created_at
FROM moneytree_transactions
WHERE company_code = $1 AND id = $2
LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return MoneytreeTransactionRow.FromReader(reader);
        }
        return null;
    }

    private async Task<MoneytreeTransactionRow?> LoadTransactionForUpdateAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string companyCode,
        Guid id,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, transaction_date, deposit_amount, withdrawal_amount,
       COALESCE(deposit_amount,0) - COALESCE(withdrawal_amount,0) AS net_amount,
       balance, currency, bank_name,
       description, account_name, account_number, imported_at, created_at
FROM moneytree_transactions
WHERE company_code = $1
  AND id = $2
  AND posting_status <> 'posted'
FOR UPDATE SKIP LOCKED
LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(id);
        cmd.Transaction = tx;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return MoneytreeTransactionRow.FromReader(reader);
        }
        return null;
    }

    private async Task<string?> GetAccountNameAsync(NpgsqlConnection conn, string companyCode, string? accountCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accountCode))
        {
            return null;
        }

        var cacheKey = $"{companyCode}:{accountCode}";
        if (_accountNameCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload->>'name' FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(accountCode);
        var result = await cmd.ExecuteScalarAsync(ct);
        var name = result as string;
        _accountNameCache[cacheKey] = name;
        return name;
    }

    private MoneytreePostingRuleService.MoneytreePostingRule? FindMatchingRule(
        IReadOnlyList<MoneytreePostingRuleService.MoneytreePostingRule> rules,
        MoneytreeTransactionRow row)
    {
        foreach (var rule in rules)
        {
            if (RuleMatches(rule.Matcher, row))
            {
                return rule;
            }
        }
        return null;
    }

    private static bool RuleMatches(JsonNode matcherNode, MoneytreeTransactionRow row)
    {
        if (matcherNode is not JsonObject matcher || matcher.Count == 0)
        {
            return true;
        }

        if (matcher.TryGetPropertyValue("always", out var alwaysNode) &&
            alwaysNode is JsonValue alwaysVal &&
            alwaysVal.TryGetValue<bool>(out var always) &&
            always)
        {
            return true;
        }

        // transactionType: "deposit" (入金) or "withdrawal" (出金)
        // 注意：Moneytree的withdrawal_amount是负数，deposit_amount是正数
        if (matcher.TryGetPropertyValue("transactionType", out var txTypeNode) &&
            txTypeNode is JsonValue txTypeVal &&
            txTypeVal.TryGetValue<string>(out var txType) &&
            !string.IsNullOrWhiteSpace(txType))
        {
            // 判断优先级：先看明确的字段，再看 NetAmount
            // withdrawal_amount < 0 表示出金（Moneytree 使用负数表示出金）
            // deposit_amount > 0 表示入金
            var hasWithdrawal = (row.WithdrawalAmount ?? 0m) < 0m;
            var hasDeposit = (row.DepositAmount ?? 0m) > 0m;
            
            // 如果两者都没有，才用 NetAmount 判断
            bool isDeposit, isWithdrawal;
            if (hasDeposit || hasWithdrawal)
            {
                isDeposit = hasDeposit && !hasWithdrawal;
                isWithdrawal = hasWithdrawal && !hasDeposit;
            }
            else
            {
                // 回退到 NetAmount 判断
                var net = row.NetAmount ?? 0m;
                isDeposit = net > 0m;
                isWithdrawal = net < 0m;
            }
            
            if (string.Equals(txType, "deposit", StringComparison.OrdinalIgnoreCase) && !isDeposit)
            {
                return false;
            }
            if (string.Equals(txType, "withdrawal", StringComparison.OrdinalIgnoreCase) && !isWithdrawal)
            {
                return false;
            }
        }

        if (matcher.TryGetPropertyValue("descriptionContains", out var descNode) &&
            descNode is JsonArray descArray &&
            !ContainsAll(row.Description ?? string.Empty, descArray))
        {
            return false;
        }

        if (matcher.TryGetPropertyValue("descriptionRegex", out var regexNode) &&
            regexNode is JsonValue regexValue &&
            regexValue.TryGetValue<string>(out var regexPattern) &&
            !string.IsNullOrWhiteSpace(regexPattern))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(row.Description ?? string.Empty, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return false;
            }
        }

        if (matcher.TryGetPropertyValue("bankNameIn", out var bankNode) &&
            bankNode is JsonArray bankArray &&
            !MatchesAny(row.BankName, bankArray))
        {
            return false;
        }

        if (matcher.TryGetPropertyValue("accountNameIn", out var accNameNode) &&
            accNameNode is JsonArray accNameArray &&
            !MatchesAny(row.AccountName, accNameArray))
        {
            return false;
        }

        if (matcher.TryGetPropertyValue("accountNumberEquals", out var accNumNode) &&
            accNumNode is JsonValue accNumValue &&
            accNumValue.TryGetValue<string>(out var expectedAccountNumber) &&
            !string.IsNullOrWhiteSpace(expectedAccountNumber))
        {
            if (!string.Equals(row.AccountNumber ?? string.Empty, expectedAccountNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var amount = row.GetPositiveAmount();
        if (matcher.TryGetPropertyValue("amountMin", out var minNode) &&
            minNode is not null &&
            TryGetDecimal(minNode, out var minAmount) &&
            amount < minAmount)
        {
            return false;
        }

        if (matcher.TryGetPropertyValue("amountMax", out var maxNode) &&
            maxNode is not null &&
            TryGetDecimal(maxNode, out var maxAmount) &&
            amount > maxAmount)
        {
            return false;
        }

        if (matcher.TryGetPropertyValue("currencyEquals", out var currencyNode) &&
            currencyNode is JsonValue currencyValue &&
            currencyValue.TryGetValue<string>(out var targetCurrency) &&
            !string.IsNullOrWhiteSpace(targetCurrency))
        {
            if (!string.Equals(row.Currency ?? string.Empty, targetCurrency.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsAll(string source, JsonArray values)
    {
        foreach (var node in values)
        {
            if (node is not JsonValue val || !val.TryGetValue<string>(out var keyword) || string.IsNullOrWhiteSpace(keyword))
                continue;
            if (!source.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static bool MatchesAny(string? source, JsonArray values)
    {
        if (source is null) return false;
        foreach (var node in values)
        {
            if (node is not JsonValue val || !val.TryGetValue<string>(out var target) || string.IsNullOrWhiteSpace(target))
                continue;
            if (string.Equals(source.Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryGetDecimal(JsonNode node, out decimal value)
    {
        value = 0m;
        if (node is null) return false;
        if (node is JsonValue val)
        {
            if (val.TryGetValue<decimal>(out value))
            {
                return true;
            }
            if (val.TryGetValue<double>(out var dbl))
            {
                value = Convert.ToDecimal(dbl);
                return true;
            }
            if (val.TryGetValue<string>(out var str) && decimal.TryParse(str, out var parsed))
            {
                value = parsed;
                return true;
            }
        }
        return false;
    }

    private async Task<string> ResolveAccountCodeAsync(string descriptor, string companyCode, MoneytreeTransactionRow row, CancellationToken ct)
    {
        if (string.Equals(descriptor, "{bankAccount}", StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveBankAccountCodeAsync(companyCode, row, ct);
        }

        return descriptor;
    }

    /// <summary>
    /// 可从历史凭证学习的通用科目（仮払金、仮受金等暂挂科目）
    /// </summary>
    private static readonly HashSet<string> LearnableAccounts = new(StringComparer.OrdinalIgnoreCase)
    {
        "183", // 仮払金 - 出金默认科目
        "319", // 仮受金 - 入金默认科目
        "312", // 買掛金
        "152", // 売掛金
        "315", // 未払金
    };

    /// <summary>
    /// 从历史凭证中学习科目（支持借方和贷方）
    /// </summary>
    /// <param name="conn">数据库连接</param>
    /// <param name="companyCode">公司代码</param>
    /// <param name="row">银行交易行</param>
    /// <param name="currentAccount">当前规则解析的科目</param>
    /// <param name="bankAccountCode">银行科目代码（用于排除）</param>
    /// <param name="targetDrCr">目标借贷方向：DR=借方，CR=贷方</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>学习到的科目，如果没有找到则返回null</returns>
    private async Task<(string? AccountCode, string? AccountName)?> LearnAccountFromHistoryAsync(
        NpgsqlConnection conn,
        string companyCode,
        MoneytreeTransactionRow row,
        string currentAccount,
        string bankAccountCode,
        string targetDrCr,
        CancellationToken ct)
    {
        // 只对"可学习"的通用科目进行历史学习
        if (!LearnableAccounts.Contains(currentAccount))
        {
            return null;
        }

        // 提取交易对手关键词
        var counterpartyKeyword = ExtractCounterpartyKeyword(row.Description);
        if (string.IsNullOrWhiteSpace(counterpartyKeyword))
        {
            _logger.LogDebug("[MoneytreePosting] 历史学习: 无法从摘要中提取交易对手关键词, desc={Desc}", row.Description);
            return null;
        }

        var drcrLabel = targetDrCr == "DR" ? "借方" : "贷方";
        _logger.LogInformation("[MoneytreePosting] 历史学习: 尝试从历史凭证学习{DrCr}科目, keyword={Keyword}, currentAccount={Current}",
            drcrLabel, counterpartyKeyword, currentAccount);

        // 查询历史凭证中相同交易对手的记录
        // 查找OT类型凭证中，指定借贷方向（非银行科目、非税金行）且摘要包含关键词的行
        // 按时间降序、金额降序排序，优先选择最近的、金额最大的科目
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH line_data AS (
                SELECT 
                    v.id,
                    v.created_at,
                    line->>'accountCode' as account_code,
                    line->>'note' as note,
                    line->>'drcr' as drcr,
                    COALESCE((line->>'isTaxLine')::boolean, false) as is_tax_line,
                    COALESCE((line->>'amount')::numeric, 0) as amount,
                    v.payload->'header'->>'summary' as summary
                FROM vouchers v,
                     jsonb_array_elements(v.payload->'lines') as line
                WHERE v.company_code = $1
            )
            SELECT 
                account_code,
                (SELECT payload->>'name' FROM accounts WHERE company_code = $1 AND account_code = line_data.account_code LIMIT 1) as account_name
            FROM line_data
            WHERE drcr = $4
              AND account_code != $2
              AND is_tax_line = false
              AND (note LIKE '%' || $3 || '%' OR summary LIKE '%' || $3 || '%')
            ORDER BY created_at DESC, amount DESC
            LIMIT 1";
        
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(bankAccountCode);
        cmd.Parameters.AddWithValue(counterpartyKeyword);
        cmd.Parameters.AddWithValue(targetDrCr);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var learnedAccountCode = reader.GetString(0);
            var learnedAccountName = reader.IsDBNull(1) ? null : reader.GetString(1);
            
            _logger.LogInformation("[MoneytreePosting] 历史学习成功: {DrCr}科目, keyword={Keyword}, learnedAccount={Code} ({Name})",
                drcrLabel, counterpartyKeyword, learnedAccountCode, learnedAccountName);
            
            return (learnedAccountCode, learnedAccountName);
        }

        _logger.LogDebug("[MoneytreePosting] 历史学习: 未找到匹配的历史凭证, {DrCr}科目, keyword={Keyword}", drcrLabel, counterpartyKeyword);
        return null;
    }

    /// <summary>
    /// 从银行交易摘要中提取交易对手关键词
    /// 例如："振込 カ)フジカフエウイル" -> "フジカフエウイル"
    /// 例如："振込 イメガ(カ" -> "イメガ"
    /// </summary>
    private static string? ExtractCounterpartyKeyword(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var desc = description.Trim();

        // 常见的银行摘要前缀模式
        var prefixes = new[] { "振込 ", "振込　", "ﾌﾘｺﾐ ", "フリコミ ", "入金 ", "出金 " };
        string? keyword = null;
        foreach (var prefix in prefixes)
        {
            if (desc.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var remainder = desc.Substring(prefix.Length).Trim();
                if (!string.IsNullOrWhiteSpace(remainder))
                {
                    keyword = remainder;
                    break;
                }
            }
        }

        // 如果没有匹配的前缀，尝试用空格分割取后半部分
        if (keyword is null)
        {
            var spaceIndex = desc.IndexOfAny(new[] { ' ', '　' });
            if (spaceIndex > 0 && spaceIndex < desc.Length - 1)
            {
                var remainder = desc.Substring(spaceIndex + 1).Trim();
                if (!string.IsNullOrWhiteSpace(remainder))
                {
                    keyword = remainder;
                }
            }
        }

        // 如果仍然没有，返回整个摘要（排除手续费等通用词）
        if (keyword is null)
        {
            var genericTerms = new[] { "振込手数料", "手数料", "利息" };
            if (genericTerms.Any(t => desc.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
            keyword = desc;
        }

        // 清理公司类型标记，提取核心名称
        // 银行描述中常见的格式：
        // - "イメガ(カ" -> "イメガ"（公司类型在后面括号里）
        // - "カ)フジカフエウイル" -> "フジカフエウイル"（公司类型在前面）
        // - "(カ)イメガ" -> "イメガ"
        keyword = CleanCompanyTypeSuffix(keyword);
        
        return string.IsNullOrWhiteSpace(keyword) ? null : keyword;
    }

    /// <summary>
    /// 清理公司名称中的类型标记
    /// </summary>
    private static string CleanCompanyTypeSuffix(string name)
    {
        var result = name.Trim();
        
        // 移除末尾的公司类型标记：(カ, (ユ, .カ, .ユ 等
        var suffixPatterns = new[] { "(カ", "(ユ", "(ド", "(ゴ", ".カ", ".ユ", "（カ", "（ユ" };
        foreach (var suffix in suffixPatterns)
        {
            if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Substring(0, result.Length - suffix.Length).Trim();
                break;
            }
        }
        
        // 移除开头的公司类型标记：カ), ユ), カ., ユ. 等
        var prefixPatterns = new[] { "カ)", "ユ)", "ド)", "ゴ)", "カ.", "ユ.", "カ）", "ユ）" };
        foreach (var prefix in prefixPatterns)
        {
            if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Substring(prefix.Length).Trim();
                break;
            }
        }
        
        return result;
    }

    private async Task<string> ResolveBankAccountCodeAsync(string companyCode, MoneytreeTransactionRow row, CancellationToken ct)
    {
        var accounts = await LoadBankAccountsAsync(companyCode, ct);
        var normalizedTarget = NormalizeAccountNo(row.AccountNumber);
        BankAccountInfo? candidate = null;

        if (!string.IsNullOrWhiteSpace(normalizedTarget))
        {
            candidate = accounts.FirstOrDefault(a => NormalizeAccountNo(a.AccountNo) == normalizedTarget);
        }

        if (candidate is null && !string.IsNullOrWhiteSpace(row.BankName))
        {
            candidate = accounts.FirstOrDefault(a => BankNameEquals(a.BankName, row.BankName));
        }

        if (candidate is null && !string.IsNullOrWhiteSpace(row.AccountName))
        {
            candidate = accounts.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a.Holder) &&
                row.AccountName!.Contains(a.Holder!, StringComparison.OrdinalIgnoreCase));
        }

        if (candidate is null)
        {
            throw new InvalidOperationException("未能根据银行名称和账户匹配到会计科目，请在规则中明确指定 bankAccount。");
        }

        return candidate.AccountCode;
    }

    private async Task<IReadOnlyList<BankAccountInfo>> LoadBankAccountsAsync(string companyCode, CancellationToken ct)
    {
        if (_bankAccountCache.TryGetValue(companyCode, out var cached) && DateTimeOffset.UtcNow - cached.LoadedAt < BankCacheTtl)
        {
            return cached.Accounts;
        }

        var list = new List<BankAccountInfo>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT account_code, payload FROM accounts WHERE company_code=$1 AND COALESCE((payload->>'isBank')::boolean, false) = true";
        cmd.Parameters.AddWithValue(companyCode);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var accountCode = reader.IsDBNull(0) ? null : reader.GetString(0);
            var payload = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(accountCode)) continue;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("bankInfo", out var bankInfo) || bankInfo.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? bankName = bankInfo.TryGetProperty("bankName", out var bn) && bn.ValueKind == JsonValueKind.String ? bn.GetString() : null;
                string? branchName = bankInfo.TryGetProperty("branchName", out var br) && br.ValueKind == JsonValueKind.String ? br.GetString() : null;
                string? accountNo = bankInfo.TryGetProperty("accountNo", out var an) && an.ValueKind == JsonValueKind.String ? an.GetString() : null;
                string? accountType = bankInfo.TryGetProperty("accountType", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString() : null;
                string? holder = bankInfo.TryGetProperty("holder", out var hd) && hd.ValueKind == JsonValueKind.String ? hd.GetString() : null;
                list.Add(new BankAccountInfo(accountCode, bankName, branchName, accountNo, accountType, holder));
            }
            catch
            {
                // ignore malformed payload
            }
        }

        var entry = new BankAccountCacheEntry(DateTimeOffset.UtcNow, list);
        _bankAccountCache[companyCode] = entry;
        return entry.Accounts;
    }

    private static string NormalizeAccountNo(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : new string(value.Where(char.IsLetterOrDigit).ToArray());

    private static bool BankNameEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyCounterpartyToLine(LineMeta meta, CounterpartyResult? result)
    {
        if (result is null) return;
        switch (result.Kind.ToLowerInvariant())
        {
            case "customer":
                meta.CustomerId ??= result.Id;
                break;
            case "vendor":
                meta.VendorId ??= result.Id;
                break;
            case "employee":
                meta.EmployeeId ??= result.Id;
                break;
        }
    }

    private async Task<CounterpartyResult?> ResolveCounterpartyAsync(
        string companyCode,
        MoneytreeCounterparty? config,
        MoneytreeTransactionRow row,
        CancellationToken ct)
    {
        if (config is null) return null;
        var types = config.Types is { Length: > 0 } ? config.Types : new[] { "customer" };
        foreach (var type in types)
        {
            CounterpartyResult? result = type.ToLowerInvariant() switch
            {
                "customer" => await ResolveBusinessPartnerAsync(companyCode, "customer", config, row, ct),
                "vendor" => await ResolveBusinessPartnerAsync(companyCode, "vendor", config, row, ct),
                "employee" => await ResolveEmployeeAsync(companyCode, config, row, ct),
                _ => null
            };
            if (result is not null)
            {
                return result with { AssignLine = config.AssignLine };
            }
        }

        if (!string.IsNullOrWhiteSpace(config.FallbackType) && !string.IsNullOrWhiteSpace(config.FallbackCode))
        {
            return new CounterpartyResult(config.FallbackType!, config.FallbackCode!, null, config.AssignLine);
        }

        return null;
    }

    private async Task<CounterpartyResult?> ResolveBusinessPartnerAsync(
        string companyCode,
        string type,
        MoneytreeCounterparty config,
        MoneytreeTransactionRow row,
        CancellationToken ct)
    {
        var code = config.Code?.Trim();
        // 对 keywords 进行模板替换，将 {description} 等模板变量替换为实际值
        var rawKeywords = config.NameKeywords ?? Array.Empty<string>();
        var keywords = rawKeywords
            .Select(k => ApplyTemplateSimple(k, row))
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToArray();
        
        // 检测是否包含 OTA 平台关键词，如果是，添加 OTA 平台名称作为额外的匹配关键词
        var otaPlatform = DetectOtaPlatform(row);
        if (otaPlatform is not null)
        {
            // 添加 OTA 平台名称和关键词作为额外的匹配选项
            var otaKeywords = new List<string> { otaPlatform.Name };
            otaKeywords.AddRange(otaPlatform.Keywords);
            keywords = keywords.Concat(otaKeywords).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _logger.LogInformation("[MoneytreePosting] ResolveBusinessPartner: detected OTA platform {Platform}, added keywords", otaPlatform.Name);
        }
        
        _logger.LogInformation("[MoneytreePosting] ResolveBusinessPartner: description={Desc}, rawKeywords=[{Raw}], extractedKeywords=[{Keywords}]",
            row.Description,
            string.Join(", ", rawKeywords),
            string.Join(", ", keywords));
        
        if (string.IsNullOrWhiteSpace(code) && keywords.Length == 0)
        {
            _logger.LogInformation("[MoneytreePosting] ResolveBusinessPartner: no code or keywords, returning null");
            return null;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // NOTE: business partner name may live in different columns depending on upstream writes.
        // - displayNameExpr: for returning a human label
        // - searchTextExpr: for matching across all possible name fields even when name column is present
        const string displayNameExpr = "COALESCE(name, payload->>'name', payload->>'nameKanji', payload->>'nameKana', '')";
        const string searchTextExpr =
            "COALESCE(name,'') || ' ' || " +
            "COALESCE(payload->>'name','') || ' ' || " +
            "COALESCE(payload->>'nameKanji','') || ' ' || " +
            "COALESCE(payload->>'nameKana','')";

        async Task<(string? Id, string? Code, string? Name, bool Relaxed)> TryFastMatchAsync(bool relaxedTypeFilter)
        {
        await using var cmd = conn.CreateCommand();
            var sql = new StringBuilder($"SELECT id::text, partner_code, {displayNameExpr} AS name FROM businesspartners WHERE company_code=$1");
        cmd.Parameters.AddWithValue(companyCode);
        var idx = 2;
            if (!relaxedTypeFilter)
            {
        if (string.Equals(type, "customer", StringComparison.OrdinalIgnoreCase))
        {
            sql.Append(" AND flag_customer = true");
        }
        else if (string.Equals(type, "vendor", StringComparison.OrdinalIgnoreCase))
        {
            sql.Append(" AND flag_vendor = true");
                }
        }

        if (!string.IsNullOrWhiteSpace(code))
        {
            sql.Append($" AND partner_code = ${idx}");
            cmd.Parameters.AddWithValue(code);
            idx++;
        }
        else
        {
            sql.Append($" AND {searchTextExpr} ILIKE ANY(${idx})");
            var patterns = keywords.Select(k => "%" + k + "%").ToArray();
            cmd.Parameters.AddWithValue(patterns);
            idx++;
        }
        sql.Append(" ORDER BY updated_at DESC LIMIT 1");
        cmd.CommandText = sql.ToString();
            _logger.LogInformation("[MoneytreePosting] ResolveBusinessPartner: SQL={Sql} relaxedTypeFilter={Relaxed}", cmd.CommandText, relaxedTypeFilter);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var partnerCode = reader.GetString(1);
            var name = reader.IsDBNull(2) ? null : reader.GetString(2);
                return (id, partnerCode, name, relaxedTypeFilter);
            }
            return (null, null, null, relaxedTypeFilter);
        }

        // Fast path: strict match first (respects customer/vendor flags).
        var fast = await TryFastMatchAsync(relaxedTypeFilter: false);
        if (!string.IsNullOrWhiteSpace(fast.Id))
        {
            _logger.LogInformation("[MoneytreePosting] ResolveBusinessPartner: FOUND id={Id}, partner_code={Code}, name={Name}, relaxedTypeFilter={Relaxed}",
                fast.Id, fast.Code, fast.Name, fast.Relaxed);
            return new CounterpartyResult(type.ToLowerInvariant(), fast.Id!, fast.Name, config.AssignLine);
        }
        // If the partner exists but flags are not set (customer/vendor), allow a relaxed fallback with higher confidence later.
        var allowRelaxed = string.Equals(type, "customer", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "vendor", StringComparison.OrdinalIgnoreCase);
        if (allowRelaxed)
        {
            var relaxedFast = await TryFastMatchAsync(relaxedTypeFilter: true);
            if (!string.IsNullOrWhiteSpace(relaxedFast.Id))
            {
                _logger.LogWarning("[MoneytreePosting] ResolveBusinessPartner: FOUND (relaxed type filter) id={Id}, partner_code={Code}, name={Name}. Partner flags may be missing.",
                    relaxedFast.Id, relaxedFast.Code, relaxedFast.Name);
                // Still return; downstream posting expects an id (UUID) even if flags were not set correctly.
                return new CounterpartyResult(type.ToLowerInvariant(), relaxedFast.Id!, relaxedFast.Name, config.AssignLine);
        }
        }

        // Flexible fallback: normalize + token overlap scoring against limited candidates.
        // This catches cases like "KK" vs "株式会社", punctuation/spacing differences, etc.
        var inputText = string.Join(' ', keywords);
        if (string.IsNullOrWhiteSpace(inputText))
        {
            inputText = row.Description ?? string.Empty;
        }
        var tokens = ExtractPartnerTokens(inputText);
        if (tokens.Length == 0)
        {
            _logger.LogInformation("[MoneytreePosting] ResolveBusinessPartner: NO MATCH (no tokens) input={Input}", inputText);
            return null;
        }

        async Task<List<(string Id, string? Code, string? Name, bool Relaxed)>> LoadCandidatesAsync(bool relaxedTypeFilter)
        {
            await using var cmd2 = conn.CreateCommand();
            var sql2 = new StringBuilder($"SELECT id::text, partner_code, {displayNameExpr} AS name FROM businesspartners WHERE company_code=$1");
            cmd2.Parameters.AddWithValue(companyCode);
            var p = 2;
            if (!relaxedTypeFilter)
            {
                if (string.Equals(type, "customer", StringComparison.OrdinalIgnoreCase))
                {
                    sql2.Append(" AND flag_customer = true");
                }
                else if (string.Equals(type, "vendor", StringComparison.OrdinalIgnoreCase))
                {
                    sql2.Append(" AND flag_vendor = true");
                }
            }
            // Use OR of token contains to reduce scan; limit candidates then score in-memory.
            var clauses = new List<string>();
            foreach (var t in tokens.Take(8))
            {
                clauses.Add($"{searchTextExpr} ILIKE ${p}");
                cmd2.Parameters.AddWithValue("%" + t + "%");
                p++;
            }
            if (clauses.Count > 0)
            {
                sql2.Append(" AND (" + string.Join(" OR ", clauses) + ")");
            }
            sql2.Append(" ORDER BY updated_at DESC LIMIT 50");
            cmd2.CommandText = sql2.ToString();

            var candidates = new List<(string Id, string? Code, string? Name, bool Relaxed)>();
            await using var rd2 = await cmd2.ExecuteReaderAsync(ct);
            while (await rd2.ReadAsync(ct))
            {
                var id = rd2.GetString(0);
                var c = rd2.IsDBNull(1) ? null : rd2.GetString(1);
                var n = rd2.IsDBNull(2) ? null : rd2.GetString(2);
                candidates.Add((id, c, n, relaxedTypeFilter));
            }
            return candidates;
        }

        var candidates = await LoadCandidatesAsync(relaxedTypeFilter: false);
        if (candidates.Count == 0 && allowRelaxed)
        {
            candidates = await LoadCandidatesAsync(relaxedTypeFilter: true);
        }
        if (candidates.Count == 0)
        {
            _logger.LogInformation("[MoneytreePosting] ResolveBusinessPartner: NO MATCH found (no candidates) input={Input} tokens=[{Tokens}]", inputText, string.Join(",", tokens));
            return null;
        }

        var scored = candidates
            .Select(c => new { c.Id, c.Code, c.Name, c.Relaxed, Score = SimilarityScore(inputText, c.Name ?? string.Empty) })
            .OrderByDescending(x => x.Score)
            .ToList();
        var best = scored[0];
        var second = scored.Count > 1 ? scored[1] : null;
        var bestScore = best.Score;
        var secondScore = second?.Score ?? 0;

        // Thresholds: keep conservative to avoid wrong auto-posting.
        var minScore = best.Relaxed ? 0.75 : 0.60; // stricter when type filter is relaxed
        const double minGap = 0.12;
        if (bestScore >= minScore && (bestScore - secondScore) >= minGap)
        {
            _logger.LogInformation("[MoneytreePosting] ResolveBusinessPartner: FUZZY MATCH id={Id} partner_code={Code} name={Name} score={Score:F2} input={Input} relaxedTypeFilter={Relaxed}",
                best.Id, best.Code, best.Name, bestScore, inputText, best.Relaxed);
            return new CounterpartyResult(type.ToLowerInvariant(), best.Id, best.Name, config.AssignLine);
        }

        // Log top candidates for tuning
        var top5 = string.Join(" | ", scored.Take(5).Select(x => $"{x.Code}:{x.Name}({x.Score:F2})"));
        _logger.LogInformation("[MoneytreePosting] ResolveBusinessPartner: FUZZY NO MATCH (best={BestScore:F2}, second={SecondScore:F2}) input={Input} relaxedTypeFilter={Relaxed} top={Top}",
            bestScore, secondScore, inputText, best.Relaxed, top5);
        return null;
    }

    // 银行摘要中常见的前缀词，需要移除后再匹配员工姓名
    private static readonly string[] DescriptionPrefixes = { "振込", "振替", "ﾌﾘｺﾐ", "ﾌﾘｶｴ", "給与", "賞与", "ｷｭｳﾖ", "ｼｮｳﾖ" };

    /// <summary>
    /// 清理银行摘要中的前缀词，提取可能的员工姓名部分
    /// 例如：「振込 ヤマナカ ヨシマサ」→「ヤマナカ ヨシマサ」
    /// </summary>
    private static string CleanDescriptionForEmployeeMatch(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return string.Empty;
        var result = description.Trim();
        foreach (var prefix in DescriptionPrefixes)
        {
            if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Substring(prefix.Length).TrimStart();
            }
        }
        return result;
    }

    private async Task<CounterpartyResult?> ResolveEmployeeAsync(
        string companyCode,
        MoneytreeCounterparty config,
        MoneytreeTransactionRow row,
        CancellationToken ct)
    {
        var code = config.Code?.Trim();
        // 对 keywords 进行模板替换，将 {description} 等模板变量替换为实际值
        var rawKeywords = config.NameKeywords ?? Array.Empty<string>();
        var keywords = rawKeywords
            .Select(k => ApplyTemplateSimple(k, row))
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => CleanDescriptionForEmployeeMatch(k)) // 清理摘要前缀
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToArray();
        if (string.IsNullOrWhiteSpace(code) && keywords.Length == 0)
        {
            return null;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        var sql = new StringBuilder("SELECT payload FROM employees WHERE company_code=$1");
        cmd.Parameters.AddWithValue(companyCode);
        var idx = 2;
        if (!string.IsNullOrWhiteSpace(code))
        {
            sql.Append($" AND (employee_code = ${idx} OR payload->>'code' = ${idx})");
            cmd.Parameters.AddWithValue(code);
            idx++;
        }
        if (keywords.Length > 0)
        {
            var clauses = new List<string>();
            foreach (var keyword in keywords)
            {
                var patternParam = $"${idx}";
                cmd.Parameters.AddWithValue("%" + keyword + "%");
                idx++;
                clauses.Add($"COALESCE(payload->>'nameKanji','') ILIKE {patternParam}");

                patternParam = $"${idx}";
                cmd.Parameters.AddWithValue("%" + keyword + "%");
                idx++;
                clauses.Add($"COALESCE(payload->>'name','') ILIKE {patternParam}");

                patternParam = $"${idx}";
                cmd.Parameters.AddWithValue("%" + keyword + "%");
                idx++;
                clauses.Add($"COALESCE(payload->>'nameKana','') ILIKE {patternParam}");
            }
            sql.Append(" AND (" + string.Join(" OR ", clauses) + ")");
        }
        sql.Append(" ORDER BY updated_at DESC LIMIT 20");
        cmd.CommandText = sql.ToString();

        var referenceDate = (row.TransactionDate ?? row.ImportedAt.UtcDateTime).Date;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var payload = reader.GetString(0);
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!EmploymentMatches(root, config.EmploymentTypes, config.ActiveOnly, referenceDate))
                continue;
            var empCode = root.TryGetProperty("code", out var codeNode) && codeNode.ValueKind == JsonValueKind.String ? codeNode.GetString() :
                          root.TryGetProperty("employeeCode", out var ecNode) && ecNode.ValueKind == JsonValueKind.String ? ecNode.GetString() :
                          null;
            if (string.IsNullOrWhiteSpace(empCode)) continue;
            var name = root.TryGetProperty("nameKanji", out var nameNode) && nameNode.ValueKind == JsonValueKind.String ? nameNode.GetString() :
                       root.TryGetProperty("name", out var nameAlt) && nameAlt.ValueKind == JsonValueKind.String ? nameAlt.GetString() :
                       null;
            return new CounterpartyResult("employee", empCode!, name, config.AssignLine);
        }

        return null;
    }

    private static bool EmploymentMatches(JsonElement root, string[]? employmentTypes, bool activeOnly, DateTime referenceDate)
    {
        if (!root.TryGetProperty("contracts", out var contracts) || contracts.ValueKind != JsonValueKind.Array)
        {
            return !activeOnly;
        }

        foreach (var contract in contracts.EnumerateArray())
        {
            var typeCode = contract.TryGetProperty("employmentTypeCode", out var typeNode) && typeNode.ValueKind == JsonValueKind.String
                ? typeNode.GetString()
                : null;
            if (employmentTypes is { Length: > 0 } &&
                (string.IsNullOrWhiteSpace(typeCode) || !employmentTypes.Any(t => string.Equals(t, typeCode, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            var fromStr = contract.TryGetProperty("periodFrom", out var fromNode) && fromNode.ValueKind == JsonValueKind.String ? fromNode.GetString() : null;
            var toStr = contract.TryGetProperty("periodTo", out var toNode) && (toNode.ValueKind == JsonValueKind.String || toNode.ValueKind == JsonValueKind.Null) ? toNode.GetString() : null;
            var fromDate = DateTime.TryParse(fromStr, out var fd) ? fd.Date : (DateTime?)null;
            var toDate = DateTime.TryParse(toStr, out var td) ? td.Date : (DateTime?)null;

            var active = (!fromDate.HasValue || fromDate.Value <= referenceDate) && (!toDate.HasValue || toDate.Value >= referenceDate);
            if (active || !activeOnly)
            {
                return true;
            }
        }

        return !activeOnly;
    }

    private static JsonDocument BuildVoucherPayload(
        MoneytreeAction action,
        MoneytreeTransactionRow row,
        decimal amount,
        string debitAccount,
        string creditAccount,
        LineMeta debitMeta,
        LineMeta creditMeta,
        BankFeeInfo? feeInfo = null,
        string? clearingTargetLine = null,
        List<ClearedItemInfo>? clearedItemInfos = null)
    {
        var summary = ApplyTemplate(action.SummaryTemplate ?? $"Moneytree 入金 | {row.Description}", row, amount);
        var postingDate = ResolvePostingDate(action.PostingDateMode, row);
        var currency = action.Currency ?? row.Currency ?? "JPY";
        var debitNote = ApplyTemplate(action.DebitNote ?? row.Description ?? string.Empty, row, amount);
        var creditNote = ApplyTemplate(action.CreditNote ?? row.Description ?? string.Empty, row, amount);
        var voucherType = NormalizeVoucherType(action.VoucherType) ?? ResolveVoucherType(row);

        // 判断这是否是独立的手续费凭证（需要拆分消费税）
        var isStandaloneBankFee = IsBankFeeTransaction(row.Description) && feeInfo is null;

        // 计算总贷方金额（包含手续费）
        var totalCreditAmount = amount;
        if (feeInfo is not null)
        {
            totalCreditAmount += feeInfo.TotalAmount;
            // 更新摘要以包含手续费信息
            summary += $" (手数料 {feeInfo.TotalAmount:#,0})";
        }

        var lineNo = 1;
        var lines = new JsonArray();

        // 如果是独立的手续费凭证，需要拆分消费税
        if (isStandaloneBankFee)
        {
            // 计算手续费的本体金额和消费税
            var feeNetAmount = Math.Round(amount / (1 + BankFeeTaxRate / 100), 0);  // 本体金额（含税÷1.1，四舍五入）
            var feeTaxAmount = amount - feeNetAmount;  // 税额

            // 手续费本体明细
            var feeLineNo = lineNo++;
            lines.Add(new JsonObject
            {
                ["lineNo"] = feeLineNo,
                ["accountCode"] = debitAccount,
                ["drcr"] = "DR",
                ["amount"] = feeNetAmount,
                ["note"] = debitNote
            });
            ApplyLineMeta(lines[0]!.AsObject(), debitMeta);

            // 仮払消費税明细（如果规则配置了 inputTaxAccountCode）
            if (feeTaxAmount > 0 && action.InputTaxAccountCode is not null)
            {
                lines.Add(new JsonObject
                {
                    ["lineNo"] = lineNo++,
                    ["accountCode"] = action.InputTaxAccountCode,
                    ["drcr"] = "DR",
                    ["amount"] = feeTaxAmount,
                    ["note"] = "消費税",
                    ["baseLineNo"] = feeLineNo,
                    ["isTaxLine"] = true
                });
            }
        }
        else
        {
            // 普通凭证（非独立手续费）
            var drLine = new JsonObject
            {
                ["lineNo"] = lineNo++,
                ["accountCode"] = debitAccount,
                ["drcr"] = "DR",
                ["amount"] = amount,
                ["note"] = debitNote
            };
            // 如果借方是清账行，标记为isClearing，不创建新的open_item，并记录被清账的凭证信息
            if (string.Equals(clearingTargetLine, "debit", StringComparison.OrdinalIgnoreCase))
            {
                drLine["isClearing"] = true;
                if (clearedItemInfos is { Count: > 0 })
                {
                    var clearedItemsArray = new JsonArray();
                    foreach (var info in clearedItemInfos)
                    {
                        clearedItemsArray.Add(new JsonObject
                        {
                            ["voucherNo"] = info.VoucherNo,
                            ["lineNo"] = info.LineNo,
                            ["amount"] = info.Amount,
                            ["clearedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd")
                        });
                    }
                    drLine["clearedItems"] = clearedItemsArray;
                }
            }
            ApplyLineMeta(drLine, debitMeta);
            lines.Add(drLine);
        }

        // 添加配对手续费明细行（如果有配对的手续费）
        if (feeInfo is not null)
        {
            // 计算手续费的本体金额和消费税
            var feeNetAmount = Math.Round(feeInfo.TotalAmount / (1 + BankFeeTaxRate / 100), 0);  // 本体金额（含税÷1.1，四舍五入）
            var feeTaxAmount = feeInfo.TotalAmount - feeNetAmount;  // 税额

            // 手续费本体明细
            var feeLineNo = lineNo++;
            lines.Add(new JsonObject
            {
                ["lineNo"] = feeLineNo,
                ["accountCode"] = feeInfo.FeeAccountCode,
                ["drcr"] = "DR",
                ["amount"] = feeNetAmount,
                ["note"] = "振込手数料"
            });

            // 仮払消費税明细
            if (feeTaxAmount > 0 && !string.IsNullOrWhiteSpace(feeInfo.InputTaxAccountCode))
            {
                lines.Add(new JsonObject
                {
                    ["lineNo"] = lineNo++,
                    ["accountCode"] = feeInfo.InputTaxAccountCode,
                    ["drcr"] = "DR",
                    ["amount"] = feeTaxAmount,
                    ["note"] = "振込手数料消費税",
                    ["baseLineNo"] = feeLineNo,
                    ["isTaxLine"] = true
                });
            }
        }

        // 贷方银行科目（总金额 = 支付金额 + 手续费）
        var creditLine = new JsonObject
        {
            ["lineNo"] = lineNo++,
            ["accountCode"] = creditAccount,
            ["drcr"] = "CR",
            ["amount"] = totalCreditAmount,
            ["note"] = creditNote
        };
        // 如果贷方是清账行，标记为isClearing，不创建新的open_item，并记录被清账的凭证信息
        if (string.Equals(clearingTargetLine, "credit", StringComparison.OrdinalIgnoreCase))
        {
            creditLine["isClearing"] = true;
            if (clearedItemInfos is { Count: > 0 })
            {
                var clearedItemsArray = new JsonArray();
                foreach (var info in clearedItemInfos)
                {
                    clearedItemsArray.Add(new JsonObject
                    {
                        ["voucherNo"] = info.VoucherNo,
                        ["lineNo"] = info.LineNo,
                        ["amount"] = info.Amount,
                        ["clearedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd")
                    });
                }
                creditLine["clearedItems"] = clearedItemsArray;
            }
        }
        ApplyLineMeta(creditLine, creditMeta);
        lines.Add(creditLine);

        var root = new JsonObject
        {
            ["header"] = new JsonObject
            {
                ["postingDate"] = postingDate,
                ["voucherType"] = voucherType,
                ["summary"] = summary,
                ["currency"] = currency
            },
            ["lines"] = lines
        };

        return JsonDocument.Parse(root.ToJsonString());
    }

    private static void ApplyLineMeta(JsonObject line, LineMeta meta)
    {
        if (!string.IsNullOrWhiteSpace(meta.CustomerId))
            line["customerId"] = meta.CustomerId;
        if (!string.IsNullOrWhiteSpace(meta.VendorId))
            line["vendorId"] = meta.VendorId;
        if (!string.IsNullOrWhiteSpace(meta.EmployeeId))
            line["employeeId"] = meta.EmployeeId;
        if (!string.IsNullOrWhiteSpace(meta.DepartmentId))
            line["departmentId"] = meta.DepartmentId;
        if (meta.PaymentDate.HasValue)
            line["paymentDate"] = meta.PaymentDate.Value.ToString("yyyy-MM-dd");
    }

    private static string ResolvePostingDate(string postingMode, MoneytreeTransactionRow row)
    {
        return postingMode switch
        {
            "today" => DateTime.UtcNow.ToString("yyyy-MM-dd"),
            "importedDate" => row.ImportedAt.UtcDateTime.ToString("yyyy-MM-dd"),
            "transactionDate" => (row.TransactionDate ?? row.ImportedAt.UtcDateTime).ToString("yyyy-MM-dd"),
            _ when DateTime.TryParse(postingMode, out var explicitDate) => explicitDate.ToString("yyyy-MM-dd"),
            _ => (row.TransactionDate ?? row.ImportedAt.UtcDateTime).ToString("yyyy-MM-dd")
        };
    }

    private static string ResolveVoucherType(MoneytreeTransactionRow row)
    {
        var deposit = row.DepositAmount ?? 0m;
        var withdrawal = row.WithdrawalAmount ?? 0m;
        if (deposit > 0m && withdrawal <= 0m) return "IN";
        if (withdrawal > 0m && deposit <= 0m) return "OT";
        var net = row.NetAmount ?? (deposit - withdrawal);
        if (net > 0m) return "IN";
        if (net < 0m) return "OT";
        return "GL";
    }

    private static string? NormalizeVoucherType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var upper = raw.Trim().ToUpperInvariant();
        return upper switch
        {
            "GL" or "AP" or "AR" or "AA" or "SA" or "IN" or "OT" => upper,
            _ => null
        };
    }

    private static string ApplyTemplate(string template, MoneytreeTransactionRow row, decimal amount)
    {
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{description}"] = row.Description ?? string.Empty,
            ["{bankName}"] = row.BankName ?? string.Empty,
            ["{accountName}"] = row.AccountName ?? string.Empty,
            ["{accountNumber}"] = row.AccountNumber ?? string.Empty,
            ["{amount}"] = amount.ToString("0.##"),
            ["{transactionDate}"] = row.TransactionDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            ["{balance}"] = row.Balance?.ToString("0.##") ?? string.Empty,
            ["{currency}"] = row.Currency ?? string.Empty
        };

        var result = template;
        foreach (var pair in replacements)
        {
            result = result.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    /// <summary>
    /// 简化版模板替换，用于 counterparty 关键词匹配
    /// </summary>
    private static string ApplyTemplateSimple(string template, MoneytreeTransactionRow row)
    {
        if (string.IsNullOrWhiteSpace(template)) return string.Empty;
        
        // 对 {description} 特殊处理：提取客户名部分
        var description = ExtractCustomerNameFromDescription(row.Description);
        
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{description}"] = description,
            ["{bankName}"] = row.BankName ?? string.Empty,
            ["{accountName}"] = row.AccountName ?? string.Empty,
            ["{accountNumber}"] = row.AccountNumber ?? string.Empty,
            ["{transactionDate}"] = row.TransactionDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            ["{balance}"] = row.Balance?.ToString("0.##") ?? string.Empty,
            ["{currency}"] = row.Currency ?? string.Empty
        };

        var result = template;
        foreach (var pair in replacements)
        {
            result = result.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    /// <summary>
    /// 从银行摘要中提取客户名部分
    /// 银行摘要通常格式为 "振込 [客户名]" 或 "振込 [客户名](カ" 等
    /// </summary>
    private static string ExtractCustomerNameFromDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return string.Empty;
        
        var text = description.Trim();
        
        // 常见的银行摘要前缀
        var prefixes = new[] { "振込 ", "振込　", "振込", "ﾌﾘｺﾐ ", "ﾌﾘｺﾐ" };
        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(prefix.Length).Trim();
                break;
            }
        }
        
        // 移除常见的后缀，如 "(カ" "(ﾕ" "(ｶ" 等（银行振込人类型标识）
        var suffixPatterns = new[] { @"\(カ$", @"\(ﾕ$", @"\(ｶ$", @"\(ﾕ\)$", @"\(カ\)$", @"\(ｶ\)$" };
        foreach (var pattern in suffixPatterns)
        {
            text = System.Text.RegularExpressions.Regex.Replace(text, pattern, "").Trim();
        }
        
        return text;
    }

    private static (Guid? VoucherId, string? VoucherNo) ExtractVoucherInfo(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Guid? voucherId = null;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && Guid.TryParse(idEl.GetString(), out var parsedId))
            {
                voucherId = parsedId;
            }

            string? voucherNo = null;
            if (root.TryGetProperty("payload", out var payloadEl) && payloadEl.ValueKind == JsonValueKind.Object)
            {
                if (payloadEl.TryGetProperty("header", out var headerEl) && headerEl.ValueKind == JsonValueKind.Object)
                {
                    if (headerEl.TryGetProperty("voucherNo", out var voucherNoEl) && voucherNoEl.ValueKind == JsonValueKind.String)
                    {
                        voucherNo = voucherNoEl.GetString();
                    }
                }
            }
            return (voucherId, voucherNo);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// 检查是否存在匹配的既存凭证（相同取引先、金额、日期、银行科目）
    /// 对于出金（withdrawal），放宽日期匹配范围（±5天），因为付款凭证创建日期可能与实际银行扣款日期不同
    /// </summary>
    /// <returns>
    /// (IsMatch, VoucherId, VoucherNo, IsExactMatch)
    /// - IsMatch: 是否找到匹配凭证
    /// - VoucherId: 匹配凭证的ID（用于关联）
    /// - VoucherNo: 匹配凭证的编号（用于显示）
    /// - IsExactMatch: 是否精确匹配（日期完全一致）
    /// </returns>
    private async Task<(bool IsMatch, Guid? VoucherId, string? VoucherNo, bool IsExactMatch, int MatchCount)> FindMatchingExistingVoucherAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string companyCode,
        string? partnerId,
        decimal amount,
        DateTime transactionDate,
        string bankAccountCode,
        bool isWithdrawal,
        string? summary,
        CancellationToken ct)
    {
        // 出金场景：放宽日期范围（±5天），因为付款凭证日期可能与银行扣款日期不同
        // 入金场景：精确匹配日期
        var dateTolerance = isWithdrawal ? 5 : 0;
        var bankSide = isWithdrawal ? "CR" : "DR";
        
        // 核心逻辑：
        // 1. 既存凭证可能有多条同一银行科目的行（主体+手续费分开记账）
        // 2. 需要合计同一银行科目的所有行金额，然后与银行明细合计金额匹配
        // 3. 银行明细合计金额 = 主体金额 + 配对手续费金额（由调用方计算后传入）
        
        // 先查询匹配数量
        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = @"
SELECT COUNT(*)
FROM vouchers
WHERE company_code = $1
  AND (payload->'header'->>'postingDate')::date BETWEEN ($2::date - $5) AND ($2::date + $5)
  AND (
    SELECT COALESCE(SUM((line->>'amount')::numeric), 0)
    FROM jsonb_array_elements(payload->'lines') AS line
    WHERE line->>'accountCode' = $3
      AND line->>'drcr' = $6
  ) = $4
  AND NOT EXISTS (
    SELECT 1 FROM moneytree_transactions mt
    WHERE mt.voucher_id = vouchers.id
      AND mt.company_code = vouchers.company_code
  )";
        countCmd.Parameters.AddWithValue(companyCode);           // $1
        countCmd.Parameters.AddWithValue(transactionDate);       // $2
        countCmd.Parameters.AddWithValue(bankAccountCode);       // $3
        countCmd.Parameters.AddWithValue(amount);                // $4
        countCmd.Parameters.AddWithValue(dateTolerance);         // $5
        countCmd.Parameters.AddWithValue(bankSide);              // $6
        if (tx is not null) countCmd.Transaction = tx;
        
        var matchCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct) ?? 0);
        
        if (matchCount > 0)
        {
            // 获取最佳匹配的凭证
            await using var cmd1 = conn.CreateCommand();
            cmd1.CommandText = @"
SELECT id, payload->'header'->>'voucherNo' AS voucher_no,
       (payload->'header'->>'postingDate')::date = $2::date AS is_exact_match
FROM vouchers
WHERE company_code = $1
  AND (payload->'header'->>'postingDate')::date BETWEEN ($2::date - $5) AND ($2::date + $5)
  AND (
    SELECT COALESCE(SUM((line->>'amount')::numeric), 0)
    FROM jsonb_array_elements(payload->'lines') AS line
    WHERE line->>'accountCode' = $3
      AND line->>'drcr' = $6
  ) = $4
  AND NOT EXISTS (
    SELECT 1 FROM moneytree_transactions mt
    WHERE mt.voucher_id = vouchers.id
      AND mt.company_code = vouchers.company_code
  )
ORDER BY ABS((payload->'header'->>'postingDate')::date - $2::date), created_at DESC
LIMIT 1";
            cmd1.Parameters.AddWithValue(companyCode);           // $1
            cmd1.Parameters.AddWithValue(transactionDate);       // $2
            cmd1.Parameters.AddWithValue(bankAccountCode);       // $3
            cmd1.Parameters.AddWithValue(amount);                // $4
            cmd1.Parameters.AddWithValue(dateTolerance);         // $5
            cmd1.Parameters.AddWithValue(bankSide);              // $6
            if (tx is not null) cmd1.Transaction = tx;

            await using var reader1 = await cmd1.ExecuteReaderAsync(ct);
            if (await reader1.ReadAsync(ct))
            {
                var voucherId = reader1.GetGuid(0);
                var voucherNo = reader1.IsDBNull(1) ? null : reader1.GetString(1);
                var isExactMatch = !reader1.IsDBNull(2) && reader1.GetBoolean(2);
                _logger.LogInformation("[MoneytreePosting] FindMatchingExistingVoucher: matched by bank account SUM amount={Amount}. voucherNo={VoucherNo}, matchCount={Count}", amount, voucherNo, matchCount);
                return (true, voucherId, voucherNo, isExactMatch, matchCount);
            }
        }

        // 策略2：通过取引先（customerId/vendorId/employeeId）+ 银行科目合计金额匹配
        if (!string.IsNullOrWhiteSpace(partnerId))
        {
            // 先查询匹配数量
            await using var countCmd2 = conn.CreateCommand();
            countCmd2.CommandText = @"
SELECT COUNT(*)
FROM vouchers
WHERE company_code = $1
  AND (payload->'header'->>'postingDate')::date BETWEEN ($2::date - $6) AND ($2::date + $6)
  AND EXISTS (
    SELECT 1 FROM jsonb_array_elements(payload->'lines') AS line
    WHERE line->>'customerId' = $4 
       OR line->>'vendorId' = $4 
       OR line->>'employeeId' = $4
  )
  AND (
    SELECT COALESCE(SUM((line->>'amount')::numeric), 0)
    FROM jsonb_array_elements(payload->'lines') AS line
    WHERE line->>'accountCode' = $5
      AND line->>'drcr' = $7
  ) = $3
  AND NOT EXISTS (
    SELECT 1 FROM moneytree_transactions mt
    WHERE mt.voucher_id = vouchers.id
      AND mt.company_code = vouchers.company_code
  )";
            countCmd2.Parameters.AddWithValue(companyCode);       // $1
            countCmd2.Parameters.AddWithValue(transactionDate);   // $2
            countCmd2.Parameters.AddWithValue(amount);            // $3
            countCmd2.Parameters.AddWithValue(partnerId);         // $4
            countCmd2.Parameters.AddWithValue(bankAccountCode);   // $5
            countCmd2.Parameters.AddWithValue(dateTolerance);     // $6
            countCmd2.Parameters.AddWithValue(bankSide);          // $7
            if (tx is not null) countCmd2.Transaction = tx;
            
            var matchCount2 = Convert.ToInt32(await countCmd2.ExecuteScalarAsync(ct) ?? 0);
            
            if (matchCount2 > 0)
            {
                await using var cmd2 = conn.CreateCommand();
                cmd2.CommandText = @"
SELECT id, payload->'header'->>'voucherNo' AS voucher_no,
       (payload->'header'->>'postingDate')::date = $2::date AS is_exact_match
FROM vouchers
WHERE company_code = $1
  AND (payload->'header'->>'postingDate')::date BETWEEN ($2::date - $6) AND ($2::date + $6)
  AND EXISTS (
    SELECT 1 FROM jsonb_array_elements(payload->'lines') AS line
    WHERE line->>'customerId' = $4 
       OR line->>'vendorId' = $4 
       OR line->>'employeeId' = $4
  )
  AND (
    SELECT COALESCE(SUM((line->>'amount')::numeric), 0)
    FROM jsonb_array_elements(payload->'lines') AS line
    WHERE line->>'accountCode' = $5
      AND line->>'drcr' = $7
  ) = $3
  AND NOT EXISTS (
    SELECT 1 FROM moneytree_transactions mt
    WHERE mt.voucher_id = vouchers.id
      AND mt.company_code = vouchers.company_code
  )
ORDER BY ABS((payload->'header'->>'postingDate')::date - $2::date), created_at DESC
LIMIT 1";
                cmd2.Parameters.AddWithValue(companyCode);       // $1
                cmd2.Parameters.AddWithValue(transactionDate);   // $2
                cmd2.Parameters.AddWithValue(amount);            // $3
                cmd2.Parameters.AddWithValue(partnerId);         // $4
                cmd2.Parameters.AddWithValue(bankAccountCode);   // $5
                cmd2.Parameters.AddWithValue(dateTolerance);     // $6
                cmd2.Parameters.AddWithValue(bankSide);          // $7
                if (tx is not null) cmd2.Transaction = tx;

                await using var reader2 = await cmd2.ExecuteReaderAsync(ct);
                if (await reader2.ReadAsync(ct))
                {
                    var voucherId = reader2.GetGuid(0);
                    var voucherNo = reader2.IsDBNull(1) ? null : reader2.GetString(1);
                    var isExactMatch = !reader2.IsDBNull(2) && reader2.GetBoolean(2);
                    _logger.LogInformation("[MoneytreePosting] FindMatchingExistingVoucher: matched by partnerId={PartnerId}, bank SUM amount={Amount}. voucherNo={VoucherNo}, matchCount={Count}", partnerId, amount, voucherNo, matchCount2);
                    return (true, voucherId, voucherNo, isExactMatch, matchCount2);
                }
            }
        }

        return (false, null, null, false, 0);
    }
    
    /// <summary>
    /// 向后兼容的重复检查方法（仅返回是否重复和凭证号）
    /// </summary>
    private async Task<(bool IsDuplicate, string? ExistingVoucherNo)> CheckDuplicateVoucherAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string companyCode,
        string? partnerId,
        decimal amount,
        DateTime postingDate,
        string bankAccountCode,
        string? summary,
        CancellationToken ct)
    {
        // 使用新方法，默认按入金场景处理（精确日期匹配）
        var (isMatch, _, voucherNo, _, _) = await FindMatchingExistingVoucherAsync(
            conn, tx, companyCode, partnerId, amount, postingDate, bankAccountCode, 
            isWithdrawal: false, summary, ct);
        return (isMatch, voucherNo);
    }
    
    /// <summary>
    /// 从摘要中提取用于重复检查的关键词
    /// </summary>
    private static string ExtractSummaryKeyword(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return string.Empty;
        
        var text = summary.Trim();
        
        // 移除常见前缀（振込、ﾌﾘｺﾐ等）
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^(振込|ﾌﾘｺﾐ|フリコミ)\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // 移除常见后缀（法人格标识）
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*[\(（](カ|ｶ|ユ|ﾕ)[\)）]$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // 取前10个字符作为关键词（避免过于精确的匹配）
        if (text.Length > 10)
            text = text.Substring(0, 10);
        
        return text.Trim();
    }

    private async Task<OpenItemReservation?> ReserveOpenItemAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string companyCode,
        string accountCode,
        string? partnerId,
        decimal amount,
        decimal tolerance,
        bool forUpdate,
        CancellationToken ct)
    {
        var candidates = await LoadOpenItemsAsync(conn, tx, companyCode, accountCode, partnerId, forUpdate, ct);
        if (candidates.Count == 0)
        {
            return null;
        }

        // 策略1：单条精确匹配（按FIFO顺序找第一个满足条件的）
        var bestSingle = candidates.FirstOrDefault(c => Math.Abs(c.ResidualAmount - amount) <= tolerance);
        if (bestSingle is not null)
        {
            var applied = Math.Min(bestSingle.ResidualAmount, amount);
            return new OpenItemReservation(
                new List<OpenItemReservationEntry> { new(bestSingle.Id, applied) },
                applied);
        }

        // 策略2：FIFO顺序累加组合（先进先出原则）
        var fifoResult = FindFifoMatch(candidates, amount, tolerance);
        if (fifoResult is not null)
        {
            _logger.LogInformation("[MoneytreePosting] FIFO匹配成功: {Count}条未清项, 金额={Amount}", 
                fifoResult.Entries.Count, fifoResult.TotalAppliedAmount);
            return fifoResult;
        }

        // 策略3：非FIFO的任意组合（当FIFO无法匹配时的fallback）
        var combo = FindCombination(candidates, amount, tolerance, maxItems: 6);
        if (combo is not null)
        {
            _logger.LogInformation("[MoneytreePosting] 非FIFO组合匹配: {Count}条未清项, 金额={Amount}", 
                combo.Entries.Count, combo.TotalAppliedAmount);
        }
        return combo;
    }

    /// <summary>
    /// 按FIFO顺序累加未清项，直到金额匹配
    /// </summary>
    private OpenItemReservation? FindFifoMatch(
        List<OpenItemCandidate> candidates,
        decimal amount,
        decimal tolerance)
    {
        var entries = new List<OpenItemReservationEntry>();
        decimal sum = 0m;

        // 按FIFO顺序（candidates已按支付日期排序）依次累加
        foreach (var candidate in candidates)
        {
            entries.Add(new OpenItemReservationEntry(candidate.Id, candidate.ResidualAmount));
            sum += candidate.ResidualAmount;

            // 精确匹配
            if (Math.Abs(sum - amount) <= tolerance)
            {
                return new OpenItemReservation(entries, sum);
            }

            // 累加超过目标金额时，尝试调整最后一条
            if (sum > amount + tolerance)
            {
                // 超出太多，FIFO组合不适用
                break;
            }
        }

        // 检查是否累加所有明细后接近目标金额
        if (entries.Count > 0 && Math.Abs(sum - amount) <= tolerance)
        {
            return new OpenItemReservation(entries, sum);
        }

        return null;
    }

    private async Task<OpenItemReservation?> ReserveOpenItemByPlatformAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string companyCode,
        string accountCode,
        MoneytreeTransactionRow row,
        decimal amount,
        decimal tolerance,
        bool forUpdate,
        CancellationToken ct)
    {
        var platform = DetectOtaPlatform(row);
        if (platform is null)
        {
            return null;
        }

        var paymentDate = (row.TransactionDate ?? row.ImportedAt.UtcDateTime).Date;
        var candidates = await LoadOpenItemsForPlatformAsync(conn, tx, companyCode, accountCode, forUpdate, paymentDate, ct);
        if (candidates.Count == 0)
        {
            return null;
        }

        var voucherCache = new Dictionary<Guid, JsonObject?>();
        var matched = new List<OpenItemCandidate>();
        foreach (var candidate in candidates)
        {
            if (candidate.VoucherId is null || candidate.LineNo is null)
            {
                continue;
            }

            var summary = await GetVoucherLineSummaryAsync(conn, tx, companyCode, candidate.VoucherId.Value, candidate.LineNo.Value, voucherCache, ct);
            var normalizedSummary = NormalizeOtaText(summary);
            if (normalizedSummary.Length == 0)
            {
                continue;
            }

            if (platform.NormalizedKeywords.Any(keyword => normalizedSummary.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                // OTA平台匹配时没有支付日期，使用凭证日期代替
                matched.Add(new OpenItemCandidate(candidate.Id, candidate.ResidualAmount, candidate.DocDate, null));
            }
        }

        if (matched.Count == 0)
        {
            return null;
        }

        var bestSingle = matched.FirstOrDefault(c => c.ResidualAmount >= amount - tolerance);
        if (bestSingle is not null)
        {
            var applied = Math.Min(bestSingle.ResidualAmount, amount);
            return new OpenItemReservation(
                new List<OpenItemReservationEntry> { new(bestSingle.Id, applied) },
                applied);
        }

        return FindCombination(matched, amount, tolerance, maxItems: 4);
    }

    private sealed record OpenItemCandidate(Guid Id, decimal ResidualAmount, DateTime DocDate, DateTime? PaymentDate);
    private sealed record OpenItemDetailCandidate(Guid Id, decimal ResidualAmount, DateTime DocDate, Guid? VoucherId, int? LineNo);

    private async Task<List<OpenItemCandidate>> LoadOpenItemsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string companyCode,
        string accountCode,
        string? partnerId,
        bool forUpdate,
        CancellationToken ct)
    {
        var list = new List<OpenItemCandidate>();
        await using var cmd = conn.CreateCommand();
        var forUpdateClause = forUpdate ? "FOR UPDATE SKIP LOCKED" : string.Empty;
        // 直接从 open_items 表读取 payment_date，用于FIFO排序
        // partner_id可能是UUID格式或partner_code格式，需要同时支持两种匹配
        cmd.CommandText = $@"
SELECT id, residual_amount, doc_date, payment_date
FROM open_items
WHERE company_code = $1
  AND account_code = $2
  AND residual_amount > 0
  AND (
        $3::text IS NULL
        OR partner_id = $3
        OR partner_id IN (SELECT id::text FROM businesspartners WHERE partner_code = $3 AND company_code = $1)
      )
ORDER BY COALESCE(payment_date, doc_date), id
{forUpdateClause}";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(accountCode);
        cmd.Parameters.AddWithValue((object?)partnerId ?? DBNull.Value);
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var residual = reader.GetDecimal(1);
            var docDate = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2);
            var paymentDate = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3);
            list.Add(new OpenItemCandidate(id, residual, docDate, paymentDate));
        }

        // 按支付日期FIFO排序（支付日期为空时用凭证日期代替）
        list.Sort((a, b) =>
        {
            var dateA = a.PaymentDate ?? a.DocDate;
            var dateB = b.PaymentDate ?? b.DocDate;
            var cmp = dateA.CompareTo(dateB);
            if (cmp != 0) return cmp;
            return a.ResidualAmount.CompareTo(b.ResidualAmount);
        });
        return list;
    }

    private async Task<string?> FindUniqueOpenItemAccountByPartnerAndAmountAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string companyCode,
        string partnerId,
        decimal amount,
        decimal tolerance,
        DateTime postingDate,
        bool forUpdate,
        CancellationToken ct)
    {
        // Find a UNIQUE open item by partner + amount across any account.
        // If multiple candidates exist, return null to avoid wrong clearing.
        await using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        var forUpdateClause = forUpdate ? "FOR UPDATE SKIP LOCKED" : string.Empty;
        cmd.CommandText = $@"
SELECT account_code
FROM open_items
WHERE company_code = $1
  AND residual_amount > 0
  AND cleared_flag = false
  AND ABS(residual_amount - $3::numeric) <= $4::numeric
  AND (doc_date IS NULL OR doc_date <= $5::date)
  AND (
        partner_id = $2
        OR partner_id IN (SELECT id::text FROM businesspartners WHERE partner_code = $2 AND company_code = $1)
      )
ORDER BY COALESCE(payment_date, doc_date) DESC NULLS LAST, id
LIMIT 2
{forUpdateClause}";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(partnerId);
        cmd.Parameters.AddWithValue(amount);
        cmd.Parameters.AddWithValue(tolerance);
        cmd.Parameters.AddWithValue(postingDate);

        var accounts = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0)) continue;
            var acc = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(acc)) accounts.Add(acc);
        }
        if (accounts.Count != 1) return null;
        return accounts[0];
    }

    private async Task<List<OpenItemDetailCandidate>> LoadOpenItemsForPlatformAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string companyCode,
        string accountCode,
        bool forUpdate,
        DateTime paymentDate,
        CancellationToken ct)
    {
        var list = new List<OpenItemDetailCandidate>();
        await using var cmd = conn.CreateCommand();
        var forUpdateClause = forUpdate ? "FOR UPDATE SKIP LOCKED" : string.Empty;
        cmd.CommandText = $@"
SELECT id, residual_amount, doc_date, refs
FROM open_items
WHERE company_code = $1
  AND account_code = $2
  AND residual_amount > 0
  AND (doc_date IS NULL OR doc_date <= $3::date)
ORDER BY doc_date, id
{forUpdateClause}";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(accountCode);
        cmd.Parameters.AddWithValue(paymentDate);
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var residual = reader.GetDecimal(1);
            var docDate = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2);
            var refsJson = reader.IsDBNull(3) ? null : reader.GetString(3);
            var (voucherId, lineNo) = ParseOpenItemRefs(refsJson);
            list.Add(new OpenItemDetailCandidate(id, residual, docDate, voucherId, lineNo));
        }
        return list;
    }

    private static (Guid? VoucherId, int? LineNo) ParseOpenItemRefs(string? refsJson)
    {
        if (string.IsNullOrWhiteSpace(refsJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(refsJson);
            var root = doc.RootElement;
            Guid? voucherId = null;
            if (root.TryGetProperty("voucherId", out var voucherNode) &&
                voucherNode.ValueKind == JsonValueKind.String &&
                Guid.TryParse(voucherNode.GetString(), out var parsed))
            {
                voucherId = parsed;
            }
            int? lineNo = null;
            if (root.TryGetProperty("lineNo", out var lineNode) &&
                lineNode.ValueKind == JsonValueKind.Number &&
                lineNode.TryGetInt32(out var lineVal))
            {
                lineNo = lineVal;
            }
            return (voucherId, lineNo);
        }
        catch
        {
            return (null, null);
        }
    }

    private OpenItemReservation? FindCombination(
        List<OpenItemCandidate> candidates,
        decimal amount,
        decimal tolerance,
        int maxItems)
    {
        OpenItemReservation? best = null;
        decimal bestDiff = decimal.MaxValue;

        void dfs(int index, List<OpenItemCandidate> picked, decimal sum)
        {
            if (picked.Count > maxItems || sum - amount > tolerance)
            {
                return;
            }

            var diff = Math.Abs(sum - amount);
            if (picked.Count > 0 && diff <= tolerance)
            {
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    var adjusted = picked.Select(p => new OpenItemReservationEntry(p.Id, p.ResidualAmount)).ToList();
                    var appliedSum = sum;
                    if (sum > amount)
                    {
                        var excess = sum - amount;
                        for (var j = adjusted.Count - 1; j >= 0 && excess > 0; j--)
                        {
                            var entry = adjusted[j];
                            var reduce = Math.Min(entry.Amount, excess);
                            adjusted[j] = entry with { Amount = entry.Amount - reduce };
                            excess -= reduce;
                        }
                        adjusted = adjusted.Where(e => e.Amount > 0).ToList();
                        appliedSum = adjusted.Sum(e => e.Amount);
                    }
                    best = new OpenItemReservation(adjusted, appliedSum);
                }
            }

            for (var i = index; i < candidates.Count; i++)
            {
                picked.Add(candidates[i]);
                dfs(i + 1, picked, sum + candidates[i].ResidualAmount);
                picked.RemoveAt(picked.Count - 1);
                if (bestDiff == 0) return;
            }
        }

        dfs(0, new List<OpenItemCandidate>(), 0m);
        return best;
    }

    private async Task<Guid?> ApplyOpenItemClearingAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        OpenItemReservation reservation,
        CancellationToken ct)
    {
        Guid? lastId = null;
        foreach (var entry in reservation.Entries)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE open_items
SET residual_amount = residual_amount - $3,
    cleared_flag = (residual_amount - $3) <= 0.00001,
    cleared_at = CASE WHEN (residual_amount - $3) <= 0.00001 THEN now() ELSE cleared_at END,
    updated_at = now()
WHERE id = $1
RETURNING id";
            cmd.Parameters.AddWithValue(entry.Id);
            cmd.Parameters.AddWithValue(entry.Amount);
            cmd.Parameters.AddWithValue(entry.Amount);
            cmd.Transaction = tx;
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is Guid guid)
            {
                lastId = guid;
            }
        }
        return lastId;
    }

    private async Task<string?> GetVoucherLineSummaryAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string companyCode,
        Guid voucherId,
        int lineNo,
        Dictionary<Guid, JsonObject?> cache,
        CancellationToken ct)
    {
        var payload = await LoadVoucherPayloadAsync(conn, tx, companyCode, voucherId, cache, ct);
        if (payload is null) return null;
        if (payload["lines"] is not JsonArray lines) return null;
        foreach (var node in lines.OfType<JsonObject>())
        {
            if (node.TryGetPropertyValue("lineNo", out var lineNoNode) &&
                lineNoNode is JsonValue lineVal &&
                lineVal.TryGetValue<int>(out var currentLine) &&
                currentLine == lineNo)
            {
                foreach (var key in new[] { "note", "description", "memo", "summary" })
                {
                    if (node.TryGetPropertyValue(key, out var textNode) &&
                        textNode is JsonValue textVal &&
                        textVal.TryGetValue<string>(out var text) &&
                        !string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
        }
        return null;
    }

    private async Task<JsonObject?> LoadVoucherPayloadAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string companyCode,
        Guid voucherId,
        Dictionary<Guid, JsonObject?> cache,
        CancellationToken ct)
    {
        if (cache.TryGetValue(voucherId, out var cached))
        {
            return cached;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM vouchers WHERE company_code=$1 AND id=$2 LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(voucherId);
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is not string payloadJson || string.IsNullOrWhiteSpace(payloadJson))
        {
            cache[voucherId] = null;
            return null;
        }

        try
        {
            var node = JsonNode.Parse(payloadJson) as JsonObject;
            cache[voucherId] = node;
            return node;
        }
        catch
        {
            cache[voucherId] = null;
            return null;
        }
    }

    private static bool ShouldMatchPlatform(string? group)
        => !string.IsNullOrWhiteSpace(group) && string.Equals(group.Trim(), "ota", StringComparison.OrdinalIgnoreCase);

    private static OtaPlatform? DetectOtaPlatform(MoneytreeTransactionRow row)
    {
        var normalized = NormalizeOtaText($"{row.Description} {row.AccountName} {row.BankName}");
        if (normalized.Length == 0) return null;
        foreach (var platform in OtaPlatforms)
        {
            if (platform.NormalizedKeywords.Any(keyword => normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                return platform;
            }
        }
        return null;
    }

    private static string NormalizeOtaText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var normalized = text.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch) || (ch >= '\u3040' && ch <= '\u30FF'))
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    private static async Task UpdateStatusAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid id,
        string status,
        string? error,
        Guid? voucherId,
        string? voucherNo,
        Guid? ruleId,
        string? ruleTitle,
        Guid? clearedOpenItemId,
        Guid? postingRunId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE moneytree_transactions
SET posting_status = $2,
    posting_error = $3,
    voucher_id = $4,
    voucher_no = $5,
    rule_id = $6,
    rule_title = $7,
    cleared_open_item_id = $8,
    posting_run_id = $9,
    updated_at = now()
WHERE id = $1";
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.AddWithValue((object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)voucherId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)voucherNo ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)ruleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)ruleTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)clearedOpenItemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)postingRunId ?? DBNull.Value);
        cmd.Transaction = tx;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed class ProcessingStats
    {
        private int _total;
        private int _posted;
        private int _needsRule;
        private int _skipped;
        private int _failed;
        private int _duplicateSuspected;
        private int _merged;
        private int _linked;
        public Guid RunId { get; init; }
        public Guid? ConfirmationTaskId { get; set; }
        public List<PostedVoucherInfo> PostedVouchers { get; } = new();
        public List<ProcessedItemInfo> ProcessedItems { get; } = new();

        public int TotalCount => _total;
        public int PostedCount => _posted;
        public int NeedsRuleCount => _needsRule;
        public int SkippedCount => _skipped;
        public int FailedCount => _failed;
        public int DuplicateSuspectedCount => _duplicateSuspected;
        public int MergedCount => _merged;
        public int LinkedCount => _linked;

        public void Apply(PostingOutcome outcome, PostedVoucherInfo? voucherInfo = null, Guid? transactionId = null, string? error = null)
        {
            _total++;
            string status;
            string? voucherNo = null;
            switch (outcome)
            {
                case PostingOutcome.Posted:
                    _posted++;
                    status = "posted";
                    if (voucherInfo is not null)
                    {
                        PostedVouchers.Add(voucherInfo);
                        voucherNo = voucherInfo.VoucherNo;
                        transactionId ??= voucherInfo.TransactionId;
                    }
                    break;
                case PostingOutcome.NeedsRule:
                    _needsRule++;
                    status = "needs_rule";
                    break;
                case PostingOutcome.Skipped:
                    _skipped++;
                    status = "skipped";
                    break;
                case PostingOutcome.Failed:
                    _failed++;
                    status = "failed";
                    break;
                case PostingOutcome.DuplicateSuspected:
                    _duplicateSuspected++;
                    status = "duplicate_suspected";
                    break;
                case PostingOutcome.Merged:
                    _merged++;
                    status = "merged";
                    break;
                case PostingOutcome.Linked:
                    _linked++;
                    status = "linked";
                    if (voucherInfo is not null)
                    {
                        voucherNo = voucherInfo.VoucherNo;
                        transactionId ??= voucherInfo.TransactionId;
                    }
                    break;
                default:
                    status = "unknown";
                    break;
            }
            
            if (transactionId.HasValue)
            {
                ProcessedItems.Add(new ProcessedItemInfo(transactionId.Value, status, voucherNo, error));
            }
        }

        public MoneytreePostingResult ToResult()
            => new(_total, _posted, _needsRule, _skipped, _failed, _duplicateSuspected, _merged, _linked, ConfirmationTaskId, ProcessedItems.Count > 0 ? ProcessedItems : null);
    }

    private sealed record PostedVoucherInfo(
        Guid TransactionId,
        Guid? VoucherId,
        string? VoucherNo,
        decimal Amount,
        string? Description,
        string? RuleTitle,
        string? TargetRole
    );

    private enum PostingOutcome
    {
        Posted,
        NeedsRule,
        Skipped,
        Failed,
        DuplicateSuspected,
        Merged,
        /// <summary>
        /// 已关联到既存凭证（不新建凭证）
        /// </summary>
        Linked
    }

    public sealed record MoneytreePostingResult(
        int Total, 
        int Posted, 
        int NeedsRule, 
        int Skipped, 
        int Failed, 
        int DuplicateSuspected = 0,
        int Merged = 0,
        int Linked = 0,
        Guid? ConfirmationTaskId = null,
        IReadOnlyList<ProcessedItemInfo>? Items = null)
    {
        public static MoneytreePostingResult Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, null, null);
    }
    
    public sealed record ProcessedItemInfo(
        Guid Id,
        string Status,
        string? VoucherNo,
        string? Error);

    public sealed record MoneytreeSimulationResult(
        Guid TransactionId,
        string Status,
        string Message,
        string? RuleTitle,
        JsonNode? Voucher,
        string? DebitAccount,
        string? DebitAccountName,
        string? CreditAccount,
        string? CreditAccountName,
        bool WouldClearOpenItem);

    private sealed record MoneytreeTransactionRow(
        Guid Id,
        DateTime? TransactionDate,
        decimal? DepositAmount,
        decimal? WithdrawalAmount,
        decimal? NetAmount,
        decimal? Balance,
        string? Currency,
        string? BankName,
        string? Description,
        string? AccountName,
        string? AccountNumber,
        DateTimeOffset ImportedAt)
    {
        public static MoneytreeTransactionRow FromReader(NpgsqlDataReader reader)
            => new(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? DateTimeOffset.UtcNow : reader.GetFieldValue<DateTimeOffset>(11));

        public decimal GetPositiveAmount()
        {
            var net = NetAmount ?? ((DepositAmount ?? 0m) - (WithdrawalAmount ?? 0m));
            if (net > 0) return net;
            if (DepositAmount.HasValue && DepositAmount.Value > 0) return DepositAmount.Value;
            if (WithdrawalAmount.HasValue && WithdrawalAmount.Value > 0) return WithdrawalAmount.Value;
            if (net < 0) return Math.Abs(net);
            return DepositAmount ?? Math.Abs(WithdrawalAmount ?? 0m);
        }
    }

    private sealed class LineMeta
    {
        public string? CustomerId { get; set; }
        public string? VendorId { get; set; }
        public string? EmployeeId { get; set; }
        public string? DepartmentId { get; set; }
        public DateTime? PaymentDate { get; set; }
    }

    private sealed record CounterpartyResult(string Kind, string Id, string? Name, string AssignLine);

    private sealed record BankAccountInfo(string AccountCode, string? BankName, string? BranchName, string? AccountNo, string? AccountType, string? Holder);

    private sealed record BankAccountCacheEntry(DateTimeOffset LoadedAt, IReadOnlyList<BankAccountInfo> Accounts);

    private sealed record MoneytreeCounterparty(
        string[] Types,
        string? Code,
        string[]? NameKeywords,
        string[]? EmploymentTypes,
        bool ActiveOnly,
        string AssignLine,
        string? FallbackType,
        string? FallbackCode);

    private sealed record MoneytreeSettlementAction(
        bool Enabled,
        string TargetLine,
        string? AccountCode,
        string? PartnerId,
        bool UseCounterparty,
        MoneytreeCounterparty? PartnerOverride,
        decimal? Tolerance,
        bool RequireMatch,
        string? FallbackAccountCode,
        string? FallbackLine,
        string? PlatformGroup);

    private sealed record OtaPlatform(string Name, string[] Keywords)
    {
        public string[] NormalizedKeywords { get; } = Keywords
            .Select(NormalizeOtaText)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
    }

    private sealed record MoneytreeAction(
        string DebitAccount,
        string CreditAccount,
        string SummaryTemplate,
        string PostingDateMode,
        string? Currency,
        string? DebitNote,
        string? CreditNote,
        MoneytreeSettlementAction? Settlement,
        MoneytreeCounterparty? Counterparty,
        string? VoucherType,
        string? BankFeeAccountCode,
        string? InputTaxAccountCode,
        string? NotificationTargetRole,
        string? NotificationTargetUserId)
    {
        public static MoneytreeAction Parse(JsonNode node)
        {
            if (node is not JsonObject obj)
                throw new ArgumentException("action must be an object");

            var debit = obj.TryGetPropertyValue("debitAccount", out var debitNode) && debitNode is JsonValue debitVal && debitVal.TryGetValue<string>(out var debitStr)
                ? debitStr
                : throw new ArgumentException("debitAccount is required");

            var credit = obj.TryGetPropertyValue("creditAccount", out var creditNode) && creditNode is JsonValue creditVal && creditVal.TryGetValue<string>(out var creditStr)
                ? creditStr
                : throw new ArgumentException("creditAccount is required");

            var summaryTemplate = obj.TryGetPropertyValue("summaryTemplate", out var summaryNode) && summaryNode is JsonValue summaryVal && summaryVal.TryGetValue<string>(out var summaryStr)
                ? summaryStr
                : "Moneytree 入金 | {description}";

            var postingDateMode = obj.TryGetPropertyValue("postingDate", out var postingNode) && postingNode is JsonValue postingVal && postingVal.TryGetValue<string>(out var postingStr)
                ? postingStr
                : "transactionDate";

            var currency = obj.TryGetPropertyValue("currency", out var currencyNode) && currencyNode is JsonValue currencyVal && currencyVal.TryGetValue<string>(out var currencyStr)
                ? currencyStr
                : null;

            var debitNote = obj.TryGetPropertyValue("debitNote", out var debitNoteNode) && debitNoteNode is JsonValue debitNoteVal && debitNoteVal.TryGetValue<string>(out var debitNoteStr)
                ? debitNoteStr
                : null;

            var creditNote = obj.TryGetPropertyValue("creditNote", out var creditNoteNode) && creditNoteNode is JsonValue creditNoteVal && creditNoteVal.TryGetValue<string>(out var creditNoteStr)
                ? creditNoteStr
                : null;

            MoneytreeSettlementAction? settlement = null;
            if (obj.TryGetPropertyValue("settlement", out var settlementNode) && settlementNode is JsonObject settlementObj)
            {
                settlement = ParseSettlement(settlementObj, credit);
            }
            else if (obj.TryGetPropertyValue("ar", out var legacyNode) && legacyNode is JsonObject legacyObj)
            {
                settlement = ParseSettlement(legacyObj, credit);
            }

            MoneytreeCounterparty? counterparty = null;
            if (obj.TryGetPropertyValue("counterparty", out var cpNode) && cpNode is JsonObject cpObj)
            {
                counterparty = ParseCounterparty(cpObj);
            }

            string? voucherType = null;
            if (obj.TryGetPropertyValue("voucherType", out var vtNode) &&
                vtNode is JsonValue vtVal &&
                vtVal.TryGetValue<string>(out var vtStr) &&
                !string.IsNullOrWhiteSpace(vtStr))
            {
                voucherType = vtStr.Trim();
            }

            // 手续费科目代码（可选，默认使用6610雑費）
            string? bankFeeAccountCode = null;
            if (obj.TryGetPropertyValue("bankFeeAccountCode", out var feeAccNode) &&
                feeAccNode is JsonValue feeAccVal &&
                feeAccVal.TryGetValue<string>(out var feeAccStr) &&
                !string.IsNullOrWhiteSpace(feeAccStr))
            {
                bankFeeAccountCode = feeAccStr.Trim();
            }

            // 进项税科目代码（用于独立手续费凭证的消费税拆分）
            string? inputTaxAccountCode = null;
            if (obj.TryGetPropertyValue("inputTaxAccountCode", out var itaNode) &&
                itaNode is JsonValue itaVal &&
                itaVal.TryGetValue<string>(out var itaStr) &&
                !string.IsNullOrWhiteSpace(itaStr))
            {
                inputTaxAccountCode = itaStr.Trim();
            }

            // 通知配置
            string? notificationTargetRole = null;
            string? notificationTargetUserId = null;
            if (obj.TryGetPropertyValue("notification", out var notifNode) && notifNode is JsonObject notifObj)
            {
                if (notifObj.TryGetPropertyValue("targetRole", out var roleNode) &&
                    roleNode is JsonValue roleVal &&
                    roleVal.TryGetValue<string>(out var roleStr) &&
                    !string.IsNullOrWhiteSpace(roleStr))
                {
                    notificationTargetRole = roleStr.Trim();
                }
                if (notifObj.TryGetPropertyValue("targetUserId", out var userNode) &&
                    userNode is JsonValue userVal &&
                    userVal.TryGetValue<string>(out var userStr) &&
                    !string.IsNullOrWhiteSpace(userStr))
                {
                    notificationTargetUserId = userStr.Trim();
                }
            }

            return new MoneytreeAction(
                debit.Trim(),
                credit.Trim(),
                summaryTemplate,
                postingDateMode,
                currency,
                debitNote,
                creditNote,
                settlement,
                counterparty,
                voucherType,
                bankFeeAccountCode,
                inputTaxAccountCode,
                notificationTargetRole,
                notificationTargetUserId);
        }

        private static MoneytreeSettlementAction? ParseSettlement(JsonObject obj, string defaultAccount)
        {
            var enabled = !obj.TryGetPropertyValue("enabled", out var enabledNode) ||
                          (enabledNode is JsonValue enabledVal && (!enabledVal.TryGetValue<bool>(out var isEnabled) || isEnabled));
            if (!enabled) return null;

            // 只有规则明确指定了accountCode时才使用，否则返回null让ProcessRowAsync使用动态的debit/creditAccount
            // 这样历史学习后的科目才能正确用于清账搜索
            var account = obj.TryGetPropertyValue("accountCode", out var accNode) && accNode is JsonValue accVal && accVal.TryGetValue<string>(out var accStr) && !string.IsNullOrWhiteSpace(accStr)
                ? accStr
                : null;
            var partnerId = obj.TryGetPropertyValue("partnerId", out var partnerNode) && partnerNode is JsonValue partnerVal && partnerVal.TryGetValue<string>(out var partnerStr)
                ? partnerStr
                : null;
            decimal? tolerance = null;
            if (obj.TryGetPropertyValue("tolerance", out var toleranceNode) && toleranceNode is not null && TryGetDecimal(toleranceNode, out var tol))
            {
                tolerance = tol;
            }
            var targetLine = obj.TryGetPropertyValue("line", out var lineNode) && lineNode is JsonValue lineVal && lineVal.TryGetValue<string>(out var lineStr)
                ? lineStr.ToLowerInvariant()
                : "credit";
            var requireMatch = obj.TryGetPropertyValue("requireMatch", out var reqNode) &&
                               reqNode is JsonValue reqVal &&
                               reqVal.TryGetValue<bool>(out var reqBool) &&
                               reqBool;
            var useCounterparty = obj.TryGetPropertyValue("useCounterparty", out var useCpNode) &&
                                  useCpNode is JsonValue useCpVal &&
                                  useCpVal.TryGetValue<bool>(out var useCpBool) &&
                                  useCpBool;
            MoneytreeCounterparty? partnerOverride = null;
            if (obj.TryGetPropertyValue("partner", out var partnerObjNode) && partnerObjNode is JsonObject partnerObj)
            {
                partnerOverride = ParseCounterparty(partnerObj);
            }
            var fallbackAccount = obj.TryGetPropertyValue("fallbackAccount", out var fbNode) && fbNode is JsonValue fbVal && fbVal.TryGetValue<string>(out var fbStr)
                ? fbStr
                : null;
            var fallbackLine = obj.TryGetPropertyValue("fallbackLine", out var fLineNode) && fLineNode is JsonValue fLineVal && fLineVal.TryGetValue<string>(out var fLineStr)
                ? fLineStr.ToLowerInvariant()
                : null;
            var platformGroup = obj.TryGetPropertyValue("platformGroup", out var pgNode) && pgNode is JsonValue pgVal && pgVal.TryGetValue<string>(out var pgStr)
                ? pgStr
                : null;

            return new MoneytreeSettlementAction(
                true,
                targetLine,
                account,
                partnerId,
                useCounterparty,
                partnerOverride,
                tolerance,
                requireMatch,
                fallbackAccount,
                fallbackLine,
                platformGroup);
        }

        private static MoneytreeCounterparty? ParseCounterparty(JsonObject obj)
        {
            string[] types;
            if (obj.TryGetPropertyValue("type", out var typeNode))
            {
                if (typeNode is JsonValue typeVal && typeVal.TryGetValue<string>(out var singleType))
                    types = new[] { singleType };
                else if (typeNode is JsonArray typeArr)
                    types = typeArr.OfType<JsonValue>().Select(v => v.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                else
                    types = Array.Empty<string>();
            }
            else
            {
                types = Array.Empty<string>();
            }

            var code = obj.TryGetPropertyValue("code", out var codeNode) && codeNode is JsonValue codeVal && codeVal.TryGetValue<string>(out var codeStr)
                ? codeStr
                : null;

            string[]? keywords = null;
            if (obj.TryGetPropertyValue("nameContains", out var nameNode))
            {
                if (nameNode is JsonValue keywordVal && keywordVal.TryGetValue<string>(out var keywordStr))
                    keywords = new[] { keywordStr };
                else if (nameNode is JsonArray keywordArr)
                    keywords = keywordArr.OfType<JsonValue>().Select(v => v.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            }

            string[]? employmentTypes = null;
            if (obj.TryGetPropertyValue("employmentTypes", out var empNode))
            {
                if (empNode is JsonValue empVal && empVal.TryGetValue<string>(out var empStr))
                    employmentTypes = new[] { empStr };
                else if (empNode is JsonArray empArr)
                    employmentTypes = empArr.OfType<JsonValue>().Select(v => v.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            }

            var activeOnly = true;
            if (obj.TryGetPropertyValue("activeOnly", out var activeNode) &&
                activeNode is JsonValue activeVal &&
                activeVal.TryGetValue<bool>(out var activeBool))
            {
                activeOnly = activeBool;
            }
            var assignLine = obj.TryGetPropertyValue("assignLine", out var lineNode) && lineNode is JsonValue lineVal && lineVal.TryGetValue<string>(out var assignStr)
                ? assignStr.ToLowerInvariant()
                : "credit";
            var fallbackType = obj.TryGetPropertyValue("fallbackType", out var fbTypeNode) && fbTypeNode is JsonValue fbTypeVal && fbTypeVal.TryGetValue<string>(out var fbTypeStr)
                ? fbTypeStr
                : null;
            var fallbackCode = obj.TryGetPropertyValue("fallbackCode", out var fbCodeNode) && fbCodeNode is JsonValue fbCodeVal && fbCodeVal.TryGetValue<string>(out var fbCodeStr)
                ? fbCodeStr
                : null;

            return new MoneytreeCounterparty(
                types,
                code,
                keywords,
                employmentTypes,
                activeOnly,
                assignLine,
                fallbackType,
                fallbackCode);
        }
    }

    private sealed record OpenItemReservation(IReadOnlyList<OpenItemReservationEntry> EntriesInternal, decimal TotalAmount)
    {
        public IReadOnlyList<OpenItemReservationEntry> Entries { get; } = EntriesInternal;
        public decimal TotalAppliedAmount => TotalAmount;
        public Guid PrimaryId => Entries.Count > 0 ? Entries[0].Id : Guid.Empty;
    }

    private sealed record OpenItemReservationEntry(Guid Id, decimal Amount);

    /// <summary>
    /// 被清账凭证信息，用于在清账凭证中记录清账了哪些凭证
    /// </summary>
    private sealed record ClearedItemInfo(string VoucherNo, int LineNo, decimal Amount);

    /// <summary>
    /// 将入金凭证的清账行的open_item标记为已清账，并记录清账了哪些凭证
    /// </summary>
    private async Task MarkClearingLineAsCleared(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid voucherId,
        List<ClearedItemInfo> clearedItemInfos,
        CancellationToken ct)
    {
        // 构建clearedItems JSON数组
        var clearedItemsJson = new JsonArray();
        foreach (var info in clearedItemInfos)
        {
            clearedItemsJson.Add(new JsonObject
            {
                ["voucherNo"] = info.VoucherNo,
                ["lineNo"] = info.LineNo,
                ["amount"] = info.Amount,
                ["clearedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd")
            });
        }
        
        // 查找该凭证中带有isClearing标记的行对应的open_item，并更新为已清账
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
UPDATE open_items oi
SET residual_amount = 0,
    cleared_flag = true,
    cleared_at = now(),
    refs = jsonb_set(COALESCE(refs, '{}'::jsonb), '{clearedItems}', $2::jsonb),
    updated_at = now()
WHERE oi.voucher_id = $1
  AND EXISTS (
    SELECT 1 FROM vouchers v 
    WHERE v.id = oi.voucher_id 
    AND (v.payload->'lines'->(oi.voucher_line_no - 1)->>'isClearing')::boolean = true
  )";
        cmd.Parameters.AddWithValue(voucherId);
        cmd.Parameters.AddWithValue(clearedItemsJson.ToJsonString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 从reservation获取被清账凭证的详细信息
    /// </summary>
    private async Task<List<ClearedItemInfo>> GetClearedItemInfosAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        OpenItemReservation reservation,
        CancellationToken ct)
    {
        var result = new List<ClearedItemInfo>();
        foreach (var entry in reservation.Entries)
        {
            await using var cmd = conn.CreateCommand();
            if (tx is not null) cmd.Transaction = tx;
            cmd.CommandText = @"
SELECT v.voucher_no, oi.voucher_line_no
FROM open_items oi
JOIN vouchers v ON oi.voucher_id = v.id
WHERE oi.id = $1";
            cmd.Parameters.AddWithValue(entry.Id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var voucherNo = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var lineNo = reader.IsDBNull(1) ? 1 : reader.GetInt32(1);
                result.Add(new ClearedItemInfo(voucherNo, lineNo, entry.Amount));
            }
        }
        return result;
    }

    /// <summary>
    /// 手续费信息，用于合并到支付凭证
    /// </summary>
    private sealed record BankFeeInfo(
        Guid FeeTransactionId,
        decimal TotalAmount,          // 含税总额
        string FeeAccountCode,        // 手续费科目代码（如6610雑費）
        string? InputTaxAccountCode,  // 仮払消費税科目代码
        string BankAccountCode        // 银行科目代码
    );

    /// <summary>
    /// 手续费配对结果
    /// </summary>
    private sealed record FeePairingResult(
        Dictionary<Guid, Guid> PaymentToFee,  // 支付ID -> 手续费ID
        Dictionary<Guid, Guid> FeeToPayment   // 手续费ID -> 支付ID
    );

    /// <summary>
    /// 构建手续费配对信息（全量）
    /// </summary>
    private async Task<FeePairingResult> BuildFeePairingsAsync(string companyCode, CancellationToken ct)
    {
        var paymentToFee = new Dictionary<Guid, Guid>();
        var feeToPayment = new Dictionary<Guid, Guid>();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        // 查询所有待处理的明细，按CSV原始行序排序（保持银行原始顺序）
        cmd.CommandText = @"
SELECT id, transaction_date, withdrawal_amount, deposit_amount, description, account_number, bank_name, created_at, COALESCE(balance, 0), COALESCE(row_sequence, 0)
FROM moneytree_transactions
WHERE company_code = $1 
  AND posting_status IN ('pending', 'needs_rule')
ORDER BY transaction_date, row_sequence";
        cmd.Parameters.AddWithValue(companyCode);
        
        var transactions = new List<FeePairingCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            transactions.Add(new FeePairingCandidate(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1),
                reader.IsDBNull(2) ? 0m : reader.GetDecimal(2),
                reader.IsDBNull(3) ? 0m : reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetDateTime(7),
                reader.GetDecimal(8),  // balance
                reader.GetInt32(9)     // row_sequence
            ));
        }

        BuildPairingsFromCandidates(transactions, paymentToFee, feeToPayment);
        return new FeePairingResult(paymentToFee, feeToPayment);
    }

    /// <summary>
    /// 构建手续费配对信息（仅限指定ID，支持自动扩展查询未选中的手续费）
    /// </summary>
    private async Task<FeePairingResult> BuildFeePairingsForIdsAsync(
        string companyCode, 
        IReadOnlyCollection<Guid> ids, 
        CancellationToken ct)
    {
        var paymentToFee = new Dictionary<Guid, Guid>();
        var feeToPayment = new Dictionary<Guid, Guid>();

        if (ids.Count == 0)
        {
            return new FeePairingResult(paymentToFee, feeToPayment);
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        
        // 第一步：查询指定ID的明细
        var transactions = new List<FeePairingCandidate>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT id, transaction_date, withdrawal_amount, deposit_amount, description, account_number, bank_name, created_at, COALESCE(balance, 0), COALESCE(row_sequence, 0)
FROM moneytree_transactions
WHERE company_code = $1 
  AND id = ANY($2)
ORDER BY transaction_date, row_sequence";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(ids.ToArray());
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                transactions.Add(new FeePairingCandidate(
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1),
                    reader.IsDBNull(2) ? 0m : reader.GetDecimal(2),
                    reader.IsDBNull(3) ? 0m : reader.GetDecimal(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetDateTime(7),
                    reader.GetDecimal(8),  // balance
                    reader.GetInt32(9)     // row_sequence
                ));
            }
        }

        // 第二步：收集需要扩展查询手续费的日期和账户
        // 如果用户已经选择了手续费，就不再自动扩展搜索
        var userSelectedFees = transactions.Where(t => IsBankFeeTransaction(t.Description)).ToList();
        if (userSelectedFees.Count > 0)
        {
            _logger.LogInformation("[MoneytreePosting] BuildFeePairingsForIds: user already selected {Count} fees, skipping auto-extension", userSelectedFees.Count);
        }
        
        // 找出所有非手续费的主交易（即用户选中的出金交易）
        var mainTransactions = transactions
            .Where(t => !IsBankFeeTransaction(t.Description) && t.WithdrawalAmount != 0m)
            .ToList();
        
        // 只有当用户没有选择手续费时，才自动搜索配对的手续费
        if (userSelectedFees.Count == 0 && mainTransactions.Count > 0)
        {
            // 收集日期和账户组合
            var dateAccountPairs = mainTransactions
                .Where(t => t.TransactionDate.HasValue)
                .Select(t => (Date: t.TransactionDate!.Value.Date, Account: t.AccountNumber ?? t.BankName ?? "default"))
                .Distinct()
                .ToList();

            if (dateAccountPairs.Count > 0)
            {
                // 查询同一天同一账户的所有未记账手续费（包括 pending 和 needs_rule 状态）
                await using var cmd2 = conn.CreateCommand();
                cmd2.CommandText = @"
SELECT id, transaction_date, withdrawal_amount, deposit_amount, description, account_number, bank_name, created_at, COALESCE(balance, 0), COALESCE(row_sequence, 0)
FROM moneytree_transactions
WHERE company_code = $1 
  AND posting_status IN ('pending', 'needs_rule')
  AND id <> ALL($2)
  AND (transaction_date, COALESCE(account_number, bank_name, 'default')) = ANY(SELECT * FROM unnest($3::date[], $4::text[]))
ORDER BY transaction_date, row_sequence";
                cmd2.Parameters.AddWithValue(companyCode);
                cmd2.Parameters.AddWithValue(ids.ToArray());
                cmd2.Parameters.AddWithValue(dateAccountPairs.Select(p => p.Date).ToArray());
                cmd2.Parameters.AddWithValue(dateAccountPairs.Select(p => p.Account).ToArray());
                
                await using var reader2 = await cmd2.ExecuteReaderAsync(ct);
                while (await reader2.ReadAsync(ct))
                {
                    var candidate = new FeePairingCandidate(
                        reader2.GetGuid(0),
                        reader2.IsDBNull(1) ? (DateTime?)null : reader2.GetDateTime(1),
                        reader2.IsDBNull(2) ? 0m : reader2.GetDecimal(2),
                        reader2.IsDBNull(3) ? 0m : reader2.GetDecimal(3),
                        reader2.IsDBNull(4) ? null : reader2.GetString(4),
                        reader2.IsDBNull(5) ? null : reader2.GetString(5),
                        reader2.IsDBNull(6) ? null : reader2.GetString(6),
                        reader2.GetDateTime(7),
                        reader2.GetDecimal(8),
                        reader2.GetInt32(9)
                    );
                    
                    // 只添加手续费类型的交易
                    if (IsBankFeeTransaction(candidate.Description))
                    {
                        transactions.Add(candidate);
                    }
                }
            }
        }

        // 按银行原始顺序配对：手续费配对到紧邻的主交易
        _logger.LogInformation("[MoneytreePosting] BuildFeePairingsForIds: about to pair {Count} transactions. Transactions: {Details}",
            transactions.Count,
            string.Join("; ", transactions.Select(t => $"[{t.Id}] date={t.TransactionDate:yyyy-MM-dd}, seq={t.RowSequence}, desc={t.Description}, withdraw={t.WithdrawalAmount}, account={t.AccountNumber ?? t.BankName}")));
        
        BuildPairingsFromCandidates(transactions, paymentToFee, feeToPayment);
        
        _logger.LogInformation("[MoneytreePosting] BuildFeePairingsForIds: paired {Count} fee-payment pairs. Pairings: {Pairings}",
            paymentToFee.Count,
            string.Join("; ", paymentToFee.Select(kv => $"payment={kv.Key} -> fee={kv.Value}")));
        
        return new FeePairingResult(paymentToFee, feeToPayment);
    }

    private sealed record FeePairingCandidate(
        Guid Id,
        DateTime? TransactionDate,
        decimal WithdrawalAmount,
        decimal DepositAmount,
        string? Description,
        string? AccountNumber,
        string? BankName,
        DateTime CreatedAt,
        decimal Balance,
        int RowSequence  // CSV原始行序，用于保持银行原始顺序
    );

    /// <summary>
    /// 从候选列表中构建配对关系
    /// </summary>
    private void BuildPairingsFromCandidates(
        List<FeePairingCandidate> transactions,
        Dictionary<Guid, Guid> paymentToFee,
        Dictionary<Guid, Guid> feeToPayment)
    {
        // 按银行账户分组
        var byAccount = transactions.GroupBy(t => t.AccountNumber ?? t.BankName ?? "default");
        
        foreach (var group in byAccount)
        {
            // 按日期、CSV原始行序排序（保持银行原始顺序）
            var ordered = group
                .OrderBy(t => t.TransactionDate)
                .ThenBy(t => t.RowSequence)
                .ToList();
            
            for (var i = 0; i < ordered.Count; i++)
            {
                var current = ordered[i];
                
                // 检查是否是手续费明细
                if (!IsBankFeeTransaction(current.Description))
                {
                    continue;
                }

                // 已经配对过则跳过
                if (feeToPayment.ContainsKey(current.Id))
                {
                    continue;
                }

                // PayPay银行：手续费在前（row_sequence小），主交易在后（row_sequence大）
                // 所以先向后查找（direction=1）紧邻的主交易
                var paired = TryPairFeeWithPayment(ordered, i, 1, current, paymentToFee, feeToPayment);
                
                // 如果向后没找到，向前查找（兼容其他银行可能的不同顺序）
                if (!paired)
                {
                    TryPairFeeWithPayment(ordered, i, -1, current, paymentToFee, feeToPayment);
                }
            }
        }
    }

    /// <summary>
    /// 尝试将手续费与入出金明细配对
    /// </summary>
    private bool TryPairFeeWithPayment(
        List<FeePairingCandidate> ordered,
        int feeIndex,
        int direction,
        FeePairingCandidate fee,
        Dictionary<Guid, Guid> paymentToFee,
        Dictionary<Guid, Guid> feeToPayment)
    {
        var start = feeIndex + direction;
        var end = direction > 0 ? ordered.Count : -1;
        
        _logger.LogInformation("[MoneytreePosting] TryPairFeeWithPayment: fee={FeeId} desc={FeeDesc} date={FeeDate}, direction={Dir}, searching from {Start} to {End}",
            fee.Id, fee.Description, fee.TransactionDate?.ToString("yyyy-MM-dd"), direction, start, end);
        
        for (var j = start; j != end; j += direction)
        {
            var candidate = ordered[j];
            
            _logger.LogInformation("[MoneytreePosting] TryPairFeeWithPayment: checking candidate[{J}]={CandidateId} desc={CandidateDesc} date={CandidateDate} withdrawal={Withdrawal}",
                j, candidate.Id, candidate.Description, candidate.TransactionDate?.ToString("yyyy-MM-dd"), candidate.WithdrawalAmount);
            
            // 必须是同一天
            if (candidate.TransactionDate != fee.TransactionDate)
            {
                _logger.LogInformation("[MoneytreePosting] TryPairFeeWithPayment: date mismatch, breaking. fee.Date={FeeDate}, candidate.Date={CandidateDate}",
                    fee.TransactionDate, candidate.TransactionDate);
                break;
            }
            
            // 必须是出金或入金（非手续费）
            // 注意：withdrawal_amount 存储的是负数，deposit_amount 存储的是正数
            var hasAmount = candidate.WithdrawalAmount < 0m || candidate.DepositAmount > 0m;
            
            // 如果遇到另一条手续费，停止搜索（PayPay银行手续费必须紧邻主交易）
            if (IsBankFeeTransaction(candidate.Description))
            {
                _logger.LogInformation("[MoneytreePosting] TryPairFeeWithPayment: encountered another fee, stopping search. candidateFee={CandidateId}",
                    candidate.Id);
                break;
            }
            
            if (!hasAmount)
            {
                _logger.LogInformation("[MoneytreePosting] TryPairFeeWithPayment: skipping. hasAmount={HasAmount}",
                    hasAmount);
                continue;
            }
            
            // 检查这个明细是否已经配对了手续费
            if (paymentToFee.ContainsKey(candidate.Id))
            {
                _logger.LogInformation("[MoneytreePosting] TryPairFeeWithPayment: already paired, continuing");
                continue;
            }
            
            // 配对成功
            paymentToFee[candidate.Id] = fee.Id;
            feeToPayment[fee.Id] = candidate.Id;
            _logger.LogInformation("[MoneytreePosting] Paired fee {FeeId} with transaction {TransactionId}", fee.Id, candidate.Id);
            return true;
        }
        
        _logger.LogInformation("[MoneytreePosting] TryPairFeeWithPayment: no match found for fee {FeeId}", fee.Id);
        return false;
    }

    /// <summary>
    /// 判断是否是银行手续费明细
    /// </summary>
    private static bool IsBankFeeTransaction(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return false;
        }
        
        return BankFeeKeywords.Any(keyword => 
            description.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 获取交易状态
    /// </summary>
    private static async Task<string?> GetTransactionStatusAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        Guid id,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT posting_status FROM moneytree_transactions WHERE id = $1";
        cmd.Parameters.AddWithValue(id);
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    /// <summary>
    /// 从 open item 获取 partner_id
    /// </summary>
    private static async Task<string?> GetOpenItemPartnerIdAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        Guid openItemId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT partner_id FROM open_items WHERE id = $1";
        cmd.Parameters.AddWithValue(openItemId);
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    /// <summary>
    /// 获取进项税科目代码
    /// </summary>
    private async Task<string> GetInputTaxAccountCodeAsync(
        NpgsqlConnection conn,
        string companyCode,
        CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT payload->>'inputTaxAccountCode' FROM company_settings WHERE company_code=$1 LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is string code && !string.IsNullOrWhiteSpace(code))
            {
                return code.Trim();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MoneytreePosting] 获取进项税科目失败");
        }
        throw new InvalidOperationException("会社設定に仮払消費税科目（inputTaxAccountCode）が設定されていません。会社設定を確認してください。");
    }

    private static async Task<string> GetBankFeeAccountCodeAsync(
        NpgsqlConnection conn,
        string companyCode,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload->>'bankFeeAccountCode' FROM company_settings WHERE company_code=$1 LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is string code && !string.IsNullOrWhiteSpace(code))
        {
            return code.Trim();
        }
        throw new InvalidOperationException("会社設定に振込手数料科目（bankFeeAccountCode）が設定されていません。会社設定を確認してください。");
    }

    /// <summary>
    /// 创建自动记账确认任务
    /// </summary>
    private async Task<Guid?> CreateConfirmationTaskAsync(
        string companyCode,
        Auth.UserCtx user,
        ProcessingStats stats,
        CancellationToken ct)
    {
        var vouchers = stats.PostedVouchers;

        // 确定通知目标用户
        var targetRole = vouchers.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.TargetRole))?.TargetRole ?? "accountant";
        var targetUserIds = await FindUsersByRoleAsync(companyCode, targetRole, ct);
        if (targetUserIds.Count == 0 && !string.IsNullOrWhiteSpace(user.UserId))
        {
            // fallback：至少发给触发者
            targetUserIds.Add(user.UserId);
        }
        
        if (targetUserIds.Count == 0)
        {
            _logger.LogWarning("[MoneytreePosting] No users found with role {Role} for confirmation task", targetRole);
            return null;
        }

        // 创建任务摘要
        var totalAmount = vouchers.Sum(v => v.Amount);
        var voucherNos = vouchers.Where(v => !string.IsNullOrWhiteSpace(v.VoucherNo)).Select(v => v.VoucherNo).ToList();
        var stepName = $"銀行自動記帳確認 (total={stats.TotalCount}, posted={stats.PostedCount}, failed={stats.FailedCount}, needs_rule={stats.NeedsRuleCount}, dup={stats.DuplicateSuspectedCount}, merged={stats.MergedCount})";
        
        // Use the processing run id as object_id, so the UI can fetch exactly the transactions processed in this run.
        // (entity=moneytree_posting, object_id=posting_run_id)
        var objectId = stats.RunId != Guid.Empty ? stats.RunId : Guid.NewGuid();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        
        // 为每个目标用户创建任务
        Guid? firstTaskId = null;
        foreach (var userId in targetUserIds)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO approval_tasks (company_code, entity, object_id, step_no, step_name, approver_user_id, status)
VALUES ($1, $2, $3, $4, $5, $6, $7)
RETURNING id";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue("moneytree_posting");
            cmd.Parameters.AddWithValue(objectId);
            cmd.Parameters.AddWithValue(1);
            cmd.Parameters.AddWithValue(stepName);
            cmd.Parameters.AddWithValue(userId);
            cmd.Parameters.AddWithValue("pending");
            
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is Guid taskId)
            {
                firstTaskId ??= taskId;
                _logger.LogInformation("[MoneytreePosting] Created confirmation task {TaskId} for user {UserId}", taskId, userId);
            }
        }

        // 保存简要信息到 step_name（展示用）
        if (firstTaskId.HasValue)
        {
            try
            {
                await using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = @"
UPDATE approval_tasks 
SET step_name = step_name || E'\n' || $2
WHERE object_id = $1 AND entity = 'moneytree_posting'";
                updateCmd.Parameters.AddWithValue(objectId);
                updateCmd.Parameters.AddWithValue(
                    voucherNos.Count > 0
                        ? $"伝票: {string.Join(", ", voucherNos.Take(5))}{(voucherNos.Count > 5 ? "..." : "")}"
                        : "伝票: (なし)");
                await updateCmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MoneytreePosting] Failed to update task metadata");
            }
        }

        return firstTaskId;
    }

    /// <summary>
    /// 根据角色查找用户ID列表
    /// </summary>
    private async Task<List<string>> FindUsersByRoleAsync(string companyCode, string roleCode, CancellationToken ct)
    {
        var userIds = new List<string>();
        
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT DISTINCT u.id::text
FROM users u
JOIN user_roles ur ON ur.user_id = u.id
JOIN roles r ON r.id = ur.role_id
WHERE u.company_code = $1
  AND u.is_active = true
  AND (r.role_code = $2 OR r.role_code = 'admin')
LIMIT 10";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(roleCode);
        
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var userId = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(userId))
            {
                userIds.Add(userId);
            }
        }
        
        return userIds;
    }
}

