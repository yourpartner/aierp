using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Server.Infrastructure;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Modules;

/// <summary>
/// Indicates the origin of a voucher creation request, affecting how validation errors are handled.
/// </summary>
public enum VoucherSource
{
    /// <summary>Manual entry from frontend UI - validation failures should block save.</summary>
    Manual,
    /// <summary>Created via AI agent/chatbot - validation failures allow save but report warning in session.</summary>
    Agent,
    /// <summary>Background automation (Moneytree, payroll, etc.) - validation failures allow save but create alert task.</summary>
    Auto
}

/// <summary>
/// Result of invoice registration validation, including whether save should proceed.
/// </summary>
public sealed class InvoiceValidationResult
{
    public bool HasRegistrationNo { get; init; }
    public bool IsValid { get; init; }
    public string? Status { get; init; }
    public string? Message { get; init; }
    public string? RegistrationNo { get; init; }
}

/// <summary>
/// Central finance helper that handles voucher creation/update, account CRUD,
/// period checks, and invoice-registration enrichment for accounting workflows.
/// </summary>
public class FinanceService
{
    private static readonly HashSet<string> HeaderTextFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "summary", "memo", "remarks", "description", "comment", "note"
    };
    private static readonly HashSet<string> HeaderAutoFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "updatedAt", "updatedBy", "updatedByDept"
    };
    private static readonly HashSet<string> LineTextFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "note", "description", "memo", "comment"
    };
    private static readonly HashSet<string> LineAutoFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "updatedAt"
    };

    private readonly NpgsqlDataSource _ds;
    private readonly InvoiceRegistryService _invoiceRegistry;
    private readonly UnifiedTaskService _taskService;
    private readonly ConcurrentDictionary<string, (DateTime LoadedAt, IReadOnlyList<FinanceAccountInfo> Accounts)> _accountsCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan AccountsCacheTtl = TimeSpan.FromMinutes(5);
    public FinanceService(NpgsqlDataSource ds, InvoiceRegistryService invoiceRegistry, UnifiedTaskService taskService)
    {
        _ds = ds;
        _invoiceRegistry = invoiceRegistry;
        _taskService = taskService;
    }

    /// <summary>
    /// 刷新总账物化视图 mv_gl_monthly，确保报表数据与凭证一致。
    /// 应在凭证创建/更新/删除后调用。
    /// </summary>
    public static async Task RefreshGlViewAsync(NpgsqlConnection conn)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "REFRESH MATERIALIZED VIEW CONCURRENTLY mv_gl_monthly";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // CONCURRENTLY 可能失败（如无唯一索引），尝试普通刷新
            try
            {
                await using var cmd2 = conn.CreateCommand();
                cmd2.CommandText = "REFRESH MATERIALIZED VIEW mv_gl_monthly";
                await cmd2.ExecuteNonQueryAsync();
            }
            catch
            {
                // 刷新失败不应阻止业务操作，静默忽略
                // 下次查询报表时会自动刷新
            }
        }
    }

    /// <summary>
    /// 使用 NpgsqlDataSource 刷新总账物化视图（用于没有现成连接的场景）
    /// </summary>
    public static async Task RefreshGlViewAsync(NpgsqlDataSource ds)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await RefreshGlViewAsync(conn);
    }

    /// <summary>
    /// Normalizes and inserts a voucher row (header + lines) while enforcing balance checks,
    /// account-specific field rules, automatic numbering (yymm + running sequence),
    /// open-item regeneration, and audit stamping.
    /// </summary>
    /// <param name="companyCode">Tenant identifier for multi-tenant isolation.</param>
    /// <param name="table">Target table name (normally "vouchers").</param>
    /// <param name="payload">Raw voucher payload from callers (header + lines).</param>
    /// <param name="userCtx">Authenticated user used to stamp audit fields.</param>
    /// <param name="source">Origin of the request - affects how invoice validation errors are handled.</param>
    /// <param name="sessionId">AI session ID for Agent source - used to report warnings in chatbot.</param>
    /// <param name="targetUserId">User ID for Auto source - used to create alert tasks.</param>
    /// <returns>Tuple of (JSON string representing the inserted voucher row, optional invoice validation warning).</returns>
    public async Task<(string Json, InvoiceValidationResult? InvoiceWarning)> CreateVoucher(
        string companyCode, 
        string table, 
        JsonElement payload, 
        Auth.UserCtx userCtx,
        VoucherSource source = VoucherSource.Manual,
        string? sessionId = null,
        string? targetUserId = null,
        NpgsqlConnection? externalConn = null,
        NpgsqlTransaction? externalTx = null)
    {
        var payloadNode = JsonNode.Parse(payload.GetRawText()) as JsonObject
            ?? throw new Exception("invalid voucher payload");
        var linesNode = payloadNode["lines"] as JsonArray ?? new JsonArray();

        var headerNode = EnsureHeader(payloadNode);
        AuditStamp.ApplyCreate(headerNode, userCtx);
        var postingDate = EnsurePostingDate(headerNode);
        
        // 验证发票登记号并根据 source 决定处理方式
        var invoiceResult = await ApplyInvoiceRegistrationToHeaderAsync(headerNode);
        InvoiceValidationResult? invoiceWarning = null;
        
        if (invoiceResult.HasRegistrationNo && !invoiceResult.IsValid)
        {
            switch (source)
            {
                case VoucherSource.Manual:
                    // 前端手动操作：直接报错，不保存凭证
                    throw new Exception(invoiceResult.Message ?? "インボイス登録番号が無効です");
                
                case VoucherSource.Agent:
                case VoucherSource.Auto:
                    // Agent/后台自动：保存凭证，但记录警告供后续处理
                    invoiceWarning = invoiceResult;
                    break;
            }
        }
        
        await EnsureVoucherCreateAllowed(companyCode, postingDate);

        var normalizedLines = new List<JsonObject>();
        decimal dr = 0m, cr = 0m;
        int baseLineIndex = 0;

        /// <summary>
        /// Normalizes raw voucher lines (sets line numbers, expands tax lines, and accumulates DR/CR totals).
        /// </summary>
        foreach (var node in linesNode)
        {
            if (node is not JsonObject lineObj) continue;
            baseLineIndex++;

            var baseLine = lineObj.DeepClone().AsObject();

            var drcr = baseLine["drcr"]?.GetValue<string>()?.ToUpperInvariant() ?? "DR";
            var amount = baseLine["amount"]?.GetValue<decimal>() ?? 0m;
            if (drcr == "DR") dr += amount; else cr += amount;

            baseLine["lineNo"] = baseLineIndex;
            normalizedLines.Add(baseLine);

            if (baseLine["tax"] is JsonObject taxObj)
            {
                var taxAmount = taxObj["amount"]?.GetValue<decimal>() ?? 0m;
                if (taxAmount != 0)
                {
                    var taxSide = taxObj["side"]?.GetValue<string>()?.ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(taxSide)) taxSide = drcr;
                    var taxAccountCode = taxObj["accountCode"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(taxAccountCode))
                        throw new Exception($"Tax account not configured for line {baseLineIndex}");

                    var taxLine = new JsonObject
                    {
                        ["accountCode"] = taxAccountCode,
                        ["drcr"] = taxSide,
                        ["amount"] = Math.Abs(taxAmount),
                        ["baseLineNo"] = baseLineIndex,
                        ["isTaxLine"] = true
                    };
                    if (taxObj["taxType"] is JsonValue taxTypeValue && taxTypeValue.TryGetValue<string>(out var taxTypeStr))
                        taxLine["taxType"] = taxTypeStr;
                    if (taxObj["rate"] is JsonValue rateValue)
                    {
                        if (rateValue.TryGetValue<double>(out var rateNumber)) taxLine["taxRate"] = rateNumber;
                        else if (rateValue.TryGetValue<string>(out var rateStr)) taxLine["taxRate"] = rateStr;
                    }
                    normalizedLines.Add(taxLine);

                    if (string.Equals(taxSide, "DR", StringComparison.OrdinalIgnoreCase)) dr += Math.Abs(taxAmount);
                    else cr += Math.Abs(taxAmount);
                }
            }
        }

        if (dr != cr) throw new Exception($"Voucher not balanced: DR={dr} CR={cr}");

        var finalLinesArray = new JsonArray();
        int seq = 1;
        foreach (var line in normalizedLines)
        {
            line["lineNo"] = seq++;
            finalLinesArray.Add(line);
        }
        payloadNode["lines"] = finalLinesArray;

        var payloadJson = payloadNode.ToJsonString();
        using var payloadDoc = JsonDocument.Parse(payloadJson);
        var payloadElement = payloadDoc.RootElement;

        string? violation = null;
        // 支持外部传入连接和事务（用于事务一致性场景），否则自己创建
        var ownsConnection = externalConn is null;
        var conn = externalConn ?? await _ds.OpenConnectionAsync();
        NpgsqlTransaction? tx = null;
        if (ownsConnection)
        {
            tx = await conn.BeginTransactionAsync();
        }
        else
        {
            tx = externalTx; // 使用外部事务
        }
        var accountMetaCache = new Dictionary<string, AccountMeta?>(StringComparer.OrdinalIgnoreCase);
        async Task<AccountMeta?> LoadAccountMetaAsync(string accountCode)
        {
            if (accountMetaCache.TryGetValue(accountCode, out var cached)) return cached;
            await using var q = conn.CreateCommand();
            q.Transaction = tx;
            q.CommandText = "SELECT payload FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1";
            q.Parameters.AddWithValue(companyCode);
            q.Parameters.AddWithValue(accountCode);
            await using var rd = await q.ExecuteReaderAsync();
            AccountMeta? meta = null;
            if (await rd.ReadAsync())
            {
                var payloadText = rd.GetFieldValue<string>(0);
                using var ap = JsonDocument.Parse(payloadText);
                var root = ap.RootElement;
                var fieldRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("fieldRules", out var fr) && fr.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in fr.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(prop.Value.GetString()))
                            fieldRules[prop.Name] = prop.Value.GetString()!;
                    }
                }
                var openItem = root.TryGetProperty("openItem", out var oi) && oi.ValueKind == JsonValueKind.True;
                meta = new AccountMeta { OpenItem = openItem, FieldRules = fieldRules };
            }
            accountMetaCache[accountCode] = meta;
            return meta;
        }

        foreach (var (line, idx) in payloadElement.GetProperty("lines").EnumerateArray().Select((v, i) => (v, i)))
        {
            if (line.TryGetProperty("isTaxLine", out var taxFlag) && taxFlag.ValueKind == JsonValueKind.True)
                continue;

            var accountCode = line.GetProperty("accountCode").GetString() ?? string.Empty;
            var meta = await LoadAccountMetaAsync(accountCode);
            if (meta is null || meta.FieldRules.Count == 0) continue;
            string? StateOf(string field) => meta.FieldRules.TryGetValue(field, out var s) ? s : null;
            bool IsMissing(string field)
                => !line.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null || (v.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(v.GetString()));
            var fieldLabelMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["customerId"] = "得意先",
                ["vendorId"] = "仕入先",
                ["employeeId"] = "従業員",
                ["departmentId"] = "部門",
                ["paymentDate"] = "支払日"
            };
            string FieldLabel(string f) => fieldLabelMap.TryGetValue(f, out var l) ? l : f;
            void Require(string f)
            {
                if (violation is null && StateOf(f) == "required" && IsMissing(f))
                    violation = $"明細行 {idx + 1}（勘定科目 {accountCode}）：{FieldLabel(f)} は必須です";
            }
            void Forbid(string f)
            {
                if (violation is null && StateOf(f) == "hidden" && !IsMissing(f))
                    violation = $"明細行 {idx + 1}（勘定科目 {accountCode}）：{FieldLabel(f)} は入力できません";
            }

            Require("customerId"); Require("vendorId"); Require("employeeId"); Require("departmentId"); Require("paymentDate");
            Forbid("customerId"); Forbid("vendorId"); Forbid("employeeId"); Forbid("departmentId"); Forbid("paymentDate");
            if (violation is not null) break;
        }
        if (violation is not null) throw new Exception(violation);

        // Validate account codes before inserting (CreateVoucher should mirror UpdateVoucherAsync semantics)
        var accountCodeSet = payloadElement.GetProperty("lines")
            .EnumerateArray()
            .Select(l => l.TryGetProperty("accountCode", out var ac) && ac.ValueKind == JsonValueKind.String ? (ac.GetString() ?? string.Empty).Trim() : string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await EnsureAccountCodesExistAsync(conn, tx, companyCode, accountCodeSet, CancellationToken.None);

        var numbering = await VoucherNumberingService.NextAsync(conn, tx!, companyCode, postingDate);
        var no = numbering.voucherNo;

        string json;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $@"INSERT INTO {table}(company_code, payload)
          VALUES ($1, jsonb_set(jsonb_set($2::jsonb, '{{header,companyCode}}', to_jsonb($1::text), true), '{{header,voucherNo}}', to_jsonb($3::text), true))
          RETURNING to_jsonb({table})";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(payloadJson);
            cmd.Parameters.AddWithValue(no);
            var execObj = await cmd.ExecuteScalarAsync();
            if (execObj is not string execJson) throw new Exception("insert voucher failed");
            json = execJson;
        }

        Guid insertedVoucherId = Guid.Empty;
        string? insertedVoucherNo = null;
        
        using (var insertedDoc = JsonDocument.Parse(json))
        {
            var insertedRoot = insertedDoc.RootElement;
            if (insertedRoot.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && Guid.TryParse(idEl.GetString(), out var parsedGuid))
            {
                insertedVoucherId = parsedGuid;
            }
            if (insertedRoot.TryGetProperty("payload", out var storedPayload))
            {
                if (insertedVoucherId != Guid.Empty)
            {
                    await RebuildOpenItemsForVoucherAsync(conn, tx, companyCode, insertedVoucherId, storedPayload, LoadAccountMetaAsync);
            }
                // 提取 voucherNo
                if (storedPayload.TryGetProperty("header", out var headerEl) && 
                    headerEl.TryGetProperty("voucherNo", out var voucherNoEl) && 
                    voucherNoEl.ValueKind == JsonValueKind.String)
                {
                    insertedVoucherNo = voucherNoEl.GetString();
                }
            }
        }

        // 只有在自己创建连接时才提交事务
        if (ownsConnection && tx != null)
        {
            await tx.CommitAsync();
        }
        
        // 刷新总账物化视图，确保报表数据一致（仅在自己管理连接时）
        if (ownsConnection)
        {
            await RefreshGlViewAsync(conn);
        }
        
        // 对于 Auto source，如果有发票警告，创建警报任务通知用户
        if (source == VoucherSource.Auto && invoiceWarning != null && !string.IsNullOrWhiteSpace(targetUserId))
        {
            await CreateInvoiceWarningAlertAsync(companyCode, invoiceWarning, targetUserId, insertedVoucherNo, insertedVoucherId != Guid.Empty ? insertedVoucherId : null);
        }
        
        // 清理自己创建的资源
        if (ownsConnection)
        {
            if (tx != null) await tx.DisposeAsync();
            await conn.DisposeAsync();
        }
        
        return (json, invoiceWarning);
    }
    
    /// <summary>
    /// Creates an alert task to notify the target user about an invalid invoice registration number.
    /// </summary>
    private async Task CreateInvoiceWarningAlertAsync(string companyCode, InvoiceValidationResult warning, string targetUserId, string? voucherNo, Guid? voucherId)
    {
        try
        {
            await _taskService.CreateInvoiceValidationWarningAsync(
                companyCode,
                sessionId: null,
                targetUserId,
                warning.RegistrationNo ?? string.Empty,
                warning.Message ?? "インボイス登録番号が無効です",
                voucherNo,
                voucherId);
        }
        catch
        {
            // 警报创建失败不应阻止凭证保存，静默忽略
        }
    }

    /// <summary>
    /// Lightweight cache entry describing account-specific open-item behavior and field rules.
    /// </summary>
    private sealed class AccountMeta
    {
        public bool OpenItem { get; init; }
        public Dictionary<string, string> FieldRules { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Updates the voucher number for an existing record (typically for manual corrections)
    /// while enforcing uniqueness and account-level update rules.
    /// </summary>
    /// <param name="companyCode">Tenant identifier.</param>
    /// <param name="voucherId">Primary key of the voucher to update.</param>
    /// <param name="newVoucherNo">New voucher number the user wants to assign.</param>
    /// <param name="userCtx">Authenticated user for auditing.</param>
    public async Task<string> UpdateVoucherNumberAsync(string companyCode, Guid voucherId, string newVoucherNo, Auth.UserCtx userCtx)
    {
        if (string.IsNullOrWhiteSpace(newVoucherNo))
            throw new Exception("会計伝票番号を入力してください");

        newVoucherNo = newVoucherNo.Trim();

        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        string? existingRowJson;
        await using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = "SELECT to_jsonb(vouchers) FROM vouchers WHERE company_code=$1 AND id=$2 FOR UPDATE";
            selectCmd.Parameters.AddWithValue(companyCode);
            selectCmd.Parameters.AddWithValue(voucherId);
            existingRowJson = (string?)await selectCmd.ExecuteScalarAsync();
        }

        if (existingRowJson is null)
        {
            await tx.RollbackAsync();
            throw new Exception("会計伝票が見つかりません");
        }

        var rowNode = JsonNode.Parse(existingRowJson) as JsonObject ?? new JsonObject();
        var payloadNode = rowNode["payload"] as JsonObject ?? new JsonObject();
        var existingPayloadJson = payloadNode.ToJsonString();

        var header = EnsureHeader(payloadNode);
        string? currentVoucherNo = null;
        if (header.TryGetPropertyValue("voucherNo", out var voucherNoNode) && voucherNoNode is JsonValue voucherNoVal && voucherNoVal.TryGetValue<string>(out var current))
        {
            currentVoucherNo = current;
        }

        if (string.Equals(currentVoucherNo, newVoucherNo, StringComparison.OrdinalIgnoreCase))
        {
            await tx.CommitAsync();
            return existingRowJson;
        }

        await using (var dupCmd = conn.CreateCommand())
        {
            dupCmd.Transaction = tx;
            dupCmd.CommandText = "SELECT 1 FROM vouchers WHERE company_code=$1 AND voucher_no=$2 AND id<>$3 LIMIT 1";
            dupCmd.Parameters.AddWithValue(companyCode);
            dupCmd.Parameters.AddWithValue(newVoucherNo);
            dupCmd.Parameters.AddWithValue(voucherId);
            var exists = await dupCmd.ExecuteScalarAsync();
            if (exists is not null && exists is not DBNull)
            {
                await tx.RollbackAsync();
                throw new Exception("同じ番号の会計伝票が既に存在します");
            }
        }

        header["voucherNo"] = JsonValue.Create(newVoucherNo);
        ApplyVoucherUpdateAudit(payloadNode, userCtx);
        var updatedPayloadJson = payloadNode.ToJsonString();

        using var updatedDoc = JsonDocument.Parse(updatedPayloadJson);
        await EnsureVoucherUpdateAllowed(companyCode, existingPayloadJson, updatedDoc.RootElement);

        string? updatedRowJson;
        await using (var updateCmd = conn.CreateCommand())
        {
            updateCmd.Transaction = tx;
            updateCmd.CommandText = @"UPDATE vouchers
                                      SET payload=$1::jsonb, updated_at=now()
                                      WHERE company_code=$2 AND id=$3
                                      RETURNING to_jsonb(vouchers)";
            updateCmd.Parameters.AddWithValue(updatedPayloadJson);
            updateCmd.Parameters.AddWithValue(companyCode);
            updateCmd.Parameters.AddWithValue(voucherId);
            updatedRowJson = (string?)await updateCmd.ExecuteScalarAsync();
        }

        if (updatedRowJson is null)
        {
            await tx.RollbackAsync();
            throw new Exception("会計伝票の更新に失敗しました");
        }

        await tx.CommitAsync();
        return updatedRowJson;
    }

    public record FinanceAccountInfo(string Code, string Name, IReadOnlyList<string> Aliases);

    /// <summary>
    /// Returns the chart of accounts (code + name + aliases) for the given company,
    /// caching the result for a short TTL to minimize database traffic.
    /// </summary>
    /// <param name="companyCode">Tenant identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<FinanceAccountInfo>> GetAccountsAsync(string companyCode, CancellationToken ct = default)
    {
        if (_accountsCache.TryGetValue(companyCode, out var cached) && DateTime.UtcNow - cached.LoadedAt < AccountsCacheTtl)
        {
            return cached.Accounts;
        }

        var accounts = new List<FinanceAccountInfo>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_code, payload->>'name', COALESCE((payload->'aliases')::text, '[]') FROM accounts WHERE company_code=$1";
        cmd.Parameters.AddWithValue(companyCode);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var code = reader.GetString(0);
            var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1) ?? string.Empty;
            string[] aliases = Array.Empty<string>();
            if (!reader.IsDBNull(2))
            {
                try
                {
                    var aliasJson = reader.GetFieldValue<string>(2);
                    var arr = JsonNode.Parse(aliasJson) as JsonArray;
                    if (arr is not null)
                    {
                        aliases = arr.Select(node => (node as JsonValue)?.GetValue<string>() ?? string.Empty)
                                     .Where(s => !string.IsNullOrWhiteSpace(s))
                                     .ToArray();
                    }
                }
                catch
                {
                    // ignore malformed aliases
                }
            }
            accounts.Add(new FinanceAccountInfo(code, name, aliases));
        }

        var snapshot = (DateTime.UtcNow, (IReadOnlyList<FinanceAccountInfo>)accounts);
        _accountsCache[companyCode] = snapshot;
        return snapshot.Item2;
    }

    /// <summary>
    /// Recomputes open-item entries for a voucher whenever it is inserted. Only accounts flagged as
    /// open-item accounts generate rows in <c>open_items</c>.
    /// </summary>
    private static async Task RebuildOpenItemsForVoucherAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        string companyCode,
        Guid voucherId,
        JsonElement payload,
        Func<string, Task<AccountMeta?>> resolveMeta)
    {
        if (!payload.TryGetProperty("header", out var header) || header.ValueKind != JsonValueKind.Object)
            return;
        if (!header.TryGetProperty("postingDate", out var postingEl) || postingEl.ValueKind != JsonValueKind.String)
            return;
        var postingDate = postingEl.GetString();
        if (string.IsNullOrWhiteSpace(postingDate)) return;
        var currency = header.TryGetProperty("currency", out var curEl) && curEl.ValueKind == JsonValueKind.String ? curEl.GetString() : "JPY";

        await using (var del = conn.CreateCommand())
        {
            if (transaction is not null) del.Transaction = transaction;
            del.CommandText = "DELETE FROM open_items WHERE company_code=$1 AND voucher_id=$2";
            del.Parameters.AddWithValue(companyCode);
            del.Parameters.AddWithValue(voucherId);
            await del.ExecuteNonQueryAsync();
        }

        if (!payload.TryGetProperty("lines", out var linesEl) || linesEl.ValueKind != JsonValueKind.Array) return;
        var lineIdx = 0;
        foreach (var line in linesEl.EnumerateArray())
        {
            lineIdx++;
            if (line.TryGetProperty("isTaxLine", out var taxFlag) && taxFlag.ValueKind == JsonValueKind.True)
                continue;
            if (!line.TryGetProperty("accountCode", out var acEl) || acEl.ValueKind != JsonValueKind.String)
                continue;
            var accountCode = acEl.GetString();
            if (string.IsNullOrWhiteSpace(accountCode)) continue;
            var meta = await resolveMeta(accountCode!);
            if (meta is null || !meta.OpenItem) continue;

            if (!line.TryGetProperty("amount", out var amtEl)) continue;
            decimal amt = 0m;
            if (amtEl.ValueKind == JsonValueKind.Number)
            {
                if (!amtEl.TryGetDecimal(out amt))
                {
                    var dbl = amtEl.GetDouble();
                    if (!decimal.TryParse(dbl.ToString(), out amt)) amt = 0m;
                }
            }
            else if (amtEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(amtEl.GetString()))
            {
                decimal.TryParse(amtEl.GetString(), out amt);
            }
            if (amt <= 0) continue;

            string? partnerId = null;
            foreach (var key in new[] { "customerId", "vendorId", "employeeId" })
            {
                if (line.TryGetProperty(key, out var pv) && pv.ValueKind == JsonValueKind.String)
                {
                    var val = pv.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) { partnerId = val; break; }
                }
            }

            // 提取支付日期（用于FIFO清账排序）
            DateTime? paymentDate = null;
            if (line.TryGetProperty("paymentDate", out var pdEl) && pdEl.ValueKind == JsonValueKind.String)
            {
                var pdStr = pdEl.GetString();
                if (!string.IsNullOrWhiteSpace(pdStr) && DateTime.TryParse(pdStr, out var pd))
                {
                    paymentDate = pd;
                }
            }

            // 检查是否是清账行
            var isClearing = line.TryGetProperty("isClearing", out var clearingEl) && clearingEl.ValueKind == JsonValueKind.True;
            
            // 构建refs，包含clearedItems（如果有）
            object refsObj;
            if (isClearing && line.TryGetProperty("clearedItems", out var clearedItemsEl) && clearedItemsEl.ValueKind == JsonValueKind.Array)
            {
                var clearedItems = new List<object>();
                foreach (var item in clearedItemsEl.EnumerateArray())
                {
                    clearedItems.Add(new {
                        voucherNo = item.TryGetProperty("voucherNo", out var vn) ? vn.GetString() : null,
                        lineNo = item.TryGetProperty("lineNo", out var ln) && ln.TryGetInt32(out var lnVal) ? lnVal : 0,
                        amount = item.TryGetProperty("amount", out var amtEl2) && amtEl2.TryGetDecimal(out var amtVal) ? amtVal : 0m,
                        clearedAt = item.TryGetProperty("clearedAt", out var ca) ? ca.GetString() : null
                    });
                }
                refsObj = new { source = "voucher", voucherId, lineNo = lineIdx, clearedItems };
            }
            else
            {
                refsObj = new { source = "voucher", voucherId, lineNo = lineIdx };
            }
            var refs = JsonSerializer.Serialize(refsObj);
            
            await using var ins = conn.CreateCommand();
            if (transaction is not null) ins.Transaction = transaction;
            // 如果是清账行，直接设置为已清账状态（residual_amount=0, cleared_flag=true）
            if (isClearing)
            {
                ins.CommandText = @"INSERT INTO open_items(company_code, voucher_id, voucher_line_no, account_code, partner_id, currency, doc_date, original_amount, residual_amount, cleared_flag, cleared_at, refs, payment_date)
                                VALUES ($1,$2,$3,$4,$5,$6,$7::date,$8,0,true,now(),$9::jsonb,$10::date)";
            }
            else
            {
                ins.CommandText = @"INSERT INTO open_items(company_code, voucher_id, voucher_line_no, account_code, partner_id, currency, doc_date, original_amount, residual_amount, refs, payment_date)
                                VALUES ($1,$2,$3,$4,$5,$6,$7::date,$8,$8,$9::jsonb,$10::date)";
            }
            ins.Parameters.AddWithValue(companyCode);
            ins.Parameters.AddWithValue(voucherId);
            ins.Parameters.AddWithValue(lineIdx);
            ins.Parameters.AddWithValue(accountCode);
            ins.Parameters.AddWithValue(partnerId is null ? DBNull.Value : partnerId);
            ins.Parameters.AddWithValue(currency ?? "JPY");
            ins.Parameters.AddWithValue(postingDate);
            ins.Parameters.AddWithValue(amt);
            ins.Parameters.AddWithValue(refs);
            ins.Parameters.AddWithValue(paymentDate.HasValue ? paymentDate.Value : postingDate); // 没有支付日期时用凭证日期
            await ins.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Creates open_items for a voucher based on its lines with open-item accounts.
    /// Public wrapper for reversal and other operations.
    /// </summary>
    public async Task CreateOpenItemsForVoucher(
        NpgsqlConnection conn,
        string companyCode,
        Guid voucherId,
        JsonObject payloadNode,
        NpgsqlTransaction? transaction = null)
    {
        var payloadJson = payloadNode.ToJsonString();
        using var doc = JsonDocument.Parse(payloadJson);
        var payload = doc.RootElement;

        Func<string, Task<AccountMeta?>> resolveMeta = async (code) =>
        {
            await using var cmd = conn.CreateCommand();
            if (transaction is not null) cmd.Transaction = transaction;
            cmd.CommandText = "SELECT payload FROM accounts WHERE company_code=$1 AND account_code=$2";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(code);
            var json = (string?)await cmd.ExecuteScalarAsync();
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var adoc = JsonDocument.Parse(json);
            var root = adoc.RootElement;
            return new AccountMeta
            {
                OpenItem = root.TryGetProperty("openItem", out var oi) && oi.ValueKind == JsonValueKind.True
            };
        };

        await RebuildOpenItemsForVoucherAsync(conn, transaction, companyCode, voucherId, payload, resolveMeta);
    }

    /// <summary>
    /// Creates a GL account row while enforcing bank/cash validation rules and stamping audit fields.
    /// </summary>
    public async Task<string> CreateAccount(string companyCode, string table, JsonElement payload, Auth.UserCtx userCtx)
    {
        bool isBank = payload.TryGetProperty("isBank", out var ib) && ib.ValueKind == JsonValueKind.True;
        bool isCash = payload.TryGetProperty("isCash", out var ic) && ic.ValueKind == JsonValueKind.True;
        if (isBank && isCash) throw new Exception("勘定科目は銀行口座と現金を同時に設定できません");

        if (isBank)
        {
            if (!payload.TryGetProperty("bankInfo", out var bi) || bi.ValueKind != JsonValueKind.Object)
                throw new Exception("銀行口座勘定科目には bankInfo の設定が必要です");
            string[] reqs = new[] { "bankName", "branchName", "accountType", "accountNo", "holder", "currency" };
            foreach (var f in reqs)
            {
                if (!bi.TryGetProperty(f, out var v) || v.ValueKind == JsonValueKind.Null || (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())))
                    throw new Exception($"銀行情報に必須フィールドが不足しています: {f}");
            }
        }
        if (isCash)
        {
            if (!payload.TryGetProperty("cashCurrency", out var ccEl) || ccEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(ccEl.GetString()))
                throw new Exception("現金勘定科目には cashCurrency の設定が必要です");
        }
        var stampedJson = PrepareRootCreateJson(payload, userCtx);
        var inserted3 = await Crud.InsertRawJson(_ds, table, companyCode, stampedJson);
        if (inserted3 is null) throw new Exception("insert failed");
        
        // 清除账户缓存
        _accountsCache.TryRemove(companyCode, out _);
        
        return inserted3;
    }

    /// <summary>
    /// Updates an existing GL account with the same validation semantics as <see cref="CreateAccount"/>.
    /// </summary>
    public async Task<string> UpdateAccount(string companyCode, string table, Guid id, JsonElement payload, Auth.UserCtx userCtx)
    {
        bool isBank = payload.TryGetProperty("isBank", out var ib) && ib.ValueKind == JsonValueKind.True;
        bool isCash = payload.TryGetProperty("isCash", out var ic) && ic.ValueKind == JsonValueKind.True;
        if (isBank && isCash) throw new Exception("勘定科目は銀行口座と現金を同時に設定できません");

        if (isBank)
        {
            if (!payload.TryGetProperty("bankInfo", out var bi) || bi.ValueKind != JsonValueKind.Object)
                throw new Exception("銀行口座勘定科目には bankInfo の設定が必要です");
            string[] reqs = new[] { "bankName", "branchName", "accountType", "accountNo", "holder", "currency" };
            foreach (var f in reqs)
            {
                if (!bi.TryGetProperty(f, out var v) || v.ValueKind == JsonValueKind.Null || (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())))
                    throw new Exception($"銀行情報に必須フィールドが不足しています: {f}");
            }
        }
        if (isCash)
        {
            if (!payload.TryGetProperty("cashCurrency", out var ccEl) || ccEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(ccEl.GetString()))
                throw new Exception("現金勘定科目には cashCurrency の設定が必要です");
        }

        var stampedJson = PrepareRootUpdateJson(payload, userCtx);
        var updated = await Crud.UpdateRawJson(_ds, table, id, companyCode, stampedJson);
        if (updated is null) throw new Exception("account not found");
        
        // 清除账户缓存
        _accountsCache.TryRemove(companyCode, out _);
        
        return updated;
    }

    /// <summary>
    /// 检查科目代码是否被引用，返回引用位置列表。
    /// </summary>
    public async Task<AccountReferenceCheckResult> CheckAccountReferencesAsync(string companyCode, string accountCode, CancellationToken ct = default)
    {
        var references = new List<AccountReference>();
        await using var conn = await _ds.OpenConnectionAsync(ct);

        // 1. 检查凭证明细
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT COUNT(*) FROM vouchers 
                               WHERE company_code = $1 
                               AND EXISTS (
                                   SELECT 1 FROM jsonb_array_elements(payload->'lines') line 
                                   WHERE line->>'accountCode' = $2
                               )";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(accountCode);
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0);
            if (count > 0)
                references.Add(new AccountReference("vouchers", $"会計伝票 {count} 件で使用中"));
        }

        // 2. 检查未清项
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM open_items WHERE company_code = $1 AND account_code = $2";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(accountCode);
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0);
            if (count > 0)
                references.Add(new AccountReference("open_items", $"未清項目 {count} 件で使用中"));
        }

        // 3. 检查公司设置（进项税/销项税科目）
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT 
                CASE WHEN payload->>'inputTaxAccountCode' = $2 THEN '仮払消費税科目' ELSE NULL END,
                CASE WHEN payload->>'outputTaxAccountCode' = $2 THEN '仮受消費税科目' ELSE NULL END
                FROM company_settings WHERE company_code = $1";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(accountCode);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0))
                    references.Add(new AccountReference("company_settings", reader.GetString(0)));
                if (!reader.IsDBNull(1))
                    references.Add(new AccountReference("company_settings", reader.GetString(1)));
            }
        }

        // 4. 检查银行明细记账规则
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT title FROM moneytree_posting_rules 
                               WHERE company_code = $1 
                               AND (action->>'debitAccount' = $2 OR action->>'creditAccount' = $2)";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(accountCode);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                references.Add(new AccountReference("moneytree_posting_rules", $"銀行明細ルール「{reader.GetString(0)}」で使用中"));
            }
        }

        // 5. 检查 AI 记账规则
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT title FROM ai_accounting_rules WHERE company_code = $1 AND account_code = $2";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(accountCode);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                references.Add(new AccountReference("ai_accounting_rules", $"AI記帳ルール「{reader.GetString(0)}」で使用中"));
            }
        }

        return new AccountReferenceCheckResult(accountCode, references);
    }

    /// <summary>
    /// 检查科目是否可以安全删除（无任何引用）。
    /// </summary>
    public async Task EnsureAccountDeleteAllowed(string companyCode, string accountCode, CancellationToken ct = default)
    {
        var result = await CheckAccountReferencesAsync(companyCode, accountCode, ct);
        if (result.References.Count > 0)
        {
            var details = string.Join("、", result.References.Select(r => r.Description));
            throw new Exception($"勘定科目 {accountCode} は削除できません：{details}");
        }
    }

    public record AccountReference(string Source, string Description);
    public record AccountReferenceCheckResult(string AccountCode, IReadOnlyList<AccountReference> References);

    /// <summary>
    /// 检查取引先是否被引用，返回引用位置列表。
    /// </summary>
    public async Task<BusinessPartnerReferenceCheckResult> CheckBusinessPartnerReferencesAsync(string companyCode, Guid partnerId, CancellationToken ct = default)
    {
        var references = new List<AccountReference>();
        await using var conn = await _ds.OpenConnectionAsync(ct);

        // 1. 检查凭证明细中的 vendorId / customerId
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT COUNT(*) FROM vouchers 
                               WHERE company_code = $1 
                               AND EXISTS (
                                   SELECT 1 FROM jsonb_array_elements(payload->'lines') line 
                                   WHERE line->>'vendorId' = $2 OR line->>'customerId' = $2
                               )";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(partnerId.ToString());
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0);
            if (count > 0)
                references.Add(new AccountReference("vouchers", $"会計伝票 {count} 件で使用中"));
        }

        // 2. 检查未清项
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM open_items WHERE company_code = $1 AND partner_id = $2";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(partnerId.ToString());
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0);
            if (count > 0)
                references.Add(new AccountReference("open_items", $"未清項目 {count} 件で使用中"));
        }

        // 3. 检查销售订单
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT COUNT(*) FROM sales_orders 
                               WHERE company_code = $1 
                               AND (payload->>'partnerId' = $2 OR payload->>'customerId' = $2)";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(partnerId.ToString());
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0);
            if (count > 0)
                references.Add(new AccountReference("sales_orders", $"受注 {count} 件で使用中"));
        }

        // 4. 检查联系人
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM contacts WHERE company_code = $1 AND partner_code = $2";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(partnerId.ToString());
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0);
            if (count > 0)
                references.Add(new AccountReference("contacts", $"連絡先 {count} 件で使用中"));
        }

        // 5. 检查报价单
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM quotes WHERE company_code = $1 AND partner_code = $2";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(partnerId.ToString());
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0);
            if (count > 0)
                references.Add(new AccountReference("quotes", $"見積書 {count} 件で使用中"));
        }

        // 6. 检查商谈/案件
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM deals WHERE company_code = $1 AND partner_code = $2";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(partnerId.ToString());
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0);
            if (count > 0)
                references.Add(new AccountReference("deals", $"案件 {count} 件で使用中"));
        }

        // 7. 检查出库单
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM delivery_notes WHERE company_code = $1 AND customer_code = $2";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(partnerId.ToString());
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0);
            if (count > 0)
                references.Add(new AccountReference("delivery_notes", $"出荷伝票 {count} 件で使用中"));
        }

        return new BusinessPartnerReferenceCheckResult(partnerId, references);
    }

    /// <summary>
    /// 检查取引先是否可以安全删除（无任何引用）。
    /// </summary>
    public async Task EnsureBusinessPartnerDeleteAllowed(string companyCode, Guid partnerId, CancellationToken ct = default)
    {
        var result = await CheckBusinessPartnerReferencesAsync(companyCode, partnerId, ct);
        if (result.References.Count > 0)
        {
            var details = string.Join("、", result.References.Select(r => r.Description));
            throw new Exception($"取引先は削除できません：{details}");
        }
    }

    public record BusinessPartnerReferenceCheckResult(Guid PartnerId, IReadOnlyList<AccountReference> References);

    /// <summary>
    /// Inserts a bank master record after applying audit metadata.
    /// </summary>
    public async Task<string> CreateBank(string companyCode, string table, JsonElement payload, Auth.UserCtx userCtx)
    {
        var stampedJson = PrepareRootCreateJson(payload, userCtx);
        var inserted = await Crud.InsertRawJson(_ds, table, companyCode, stampedJson);
        if (inserted is null) throw new Exception("insert failed");
        return inserted;
    }

    /// <summary>
    /// Inserts a bank branch master record after applying audit metadata.
    /// </summary>
    public async Task<string> CreateBranch(string companyCode, string table, JsonElement payload, Auth.UserCtx userCtx)
    {
        var stampedJson = PrepareRootCreateJson(payload, userCtx);
        var inserted = await Crud.InsertRawJson(_ds, table, companyCode, stampedJson);
        if (inserted is null) throw new Exception("insert failed");
        return inserted;
    }

    /// <summary>
    /// Validates that the accounting period containing <paramref name="postingDate"/> is open for creation.
    /// 期间存在于数据库 = 打开，期间不存在 = 关闭。
    /// 如果记账日期是当月且期间不存在，自动创建并打开该期间。
    /// </summary>
    public async Task EnsureVoucherCreateAllowed(string companyCode, DateTime postingDate)
    {
        var state = await GetPeriodStateAsync(companyCode, postingDate.Date);
        
        // 期间不存在
        if (!state.Exists)
        {
            // 判断是否是当月：记账日期所在月份与今天的月份相同
            var today = DateTime.Today;
            var isCurrentMonth = postingDate.Year == today.Year && postingDate.Month == today.Month;
            
            if (isCurrentMonth)
            {
                // 当月期间不存在时，自动创建并打开
                await AutoCreateAccountingPeriodAsync(companyCode, postingDate);
            }
            else
            {
                // 非当月期间不存在，报错
                throw new Exception($"対象の会計期間（{postingDate:yyyy-MM}）は開いていません。会計期間を開いてから再試行してください。");
            }
        }
        // 期间存在但标记为关闭（备用逻辑）
        else if (!state.IsOpen)
        {
            throw new Exception($"対象の会計期間（{postingDate:yyyy-MM}）は閉鎖されています。");
        }
    }
    
    /// <summary>
    /// 自动创建并打开指定月份的会计期间
    /// </summary>
    private async Task AutoCreateAccountingPeriodAsync(string companyCode, DateTime postingDate)
    {
        var periodStart = new DateTime(postingDate.Year, postingDate.Month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);
        
        var payload = new JsonObject
        {
            ["periodStart"] = periodStart.ToString("yyyy-MM-dd"),
            ["periodEnd"] = periodEnd.ToString("yyyy-MM-dd"),
            ["isOpen"] = true
        };
        
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO accounting_periods (company_code, payload)
            VALUES ($1, $2::jsonb)
            ON CONFLICT DO NOTHING";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(payload.ToJsonString());
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Populates invoice-registration metadata on the voucher header based on the provided payload.
    /// </summary>
    public async Task<InvoiceValidationResult> ApplyInvoiceRegistrationAsync(JsonObject payloadNode)
    {
        var header = EnsureHeader(payloadNode);
        return await ApplyInvoiceRegistrationToHeaderAsync(header);
    }

    /// <summary>
    /// Performs invoice number normalization, verification, and metadata stamping on the header node.
    /// Returns validation result so caller can decide how to handle invalid registrations based on VoucherSource.
    /// </summary>
    private async Task<InvoiceValidationResult> ApplyInvoiceRegistrationToHeaderAsync(JsonObject headerNode)
    {
        if (!headerNode.TryGetPropertyValue("invoiceRegistrationNo", out var valNode) || valNode is not JsonValue value || !value.TryGetValue<string>(out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            headerNode["invoiceRegistrationNo"] = JsonValue.Create(string.Empty);
            ClearInvoiceRegistrationMetadata(headerNode);
            return new InvoiceValidationResult { HasRegistrationNo = false, IsValid = true };
        }

        var normalized = InvoiceRegistryService.Normalize(raw);
        headerNode["invoiceRegistrationNo"] = JsonValue.Create(normalized);

        if (!InvoiceRegistryService.IsFormatValid(normalized))
            throw new Exception("インボイス登録番号は T + 13桁の数字で入力してください");

        var verification = await _invoiceRegistry.VerifyAsync(normalized);
        var statusKey = InvoiceRegistryService.StatusKey(verification.Status);

        // 始终保留登记号和验证状态（无论是否匹配），由调用方根据 VoucherSource 决定是否阻止保存
        headerNode["invoiceRegistrationStatus"] = JsonValue.Create(statusKey);
        headerNode["invoiceRegistrationCheckedAt"] = JsonValue.Create(verification.CheckedAt.ToString("O"));

        if (!string.IsNullOrWhiteSpace(verification.Name))
            headerNode["invoiceRegistrationName"] = JsonValue.Create(verification.Name);
        else
            headerNode.Remove("invoiceRegistrationName");

        if (!string.IsNullOrWhiteSpace(verification.NameKana))
            headerNode["invoiceRegistrationNameKana"] = JsonValue.Create(verification.NameKana);
        else
            headerNode.Remove("invoiceRegistrationNameKana");

        if (verification.EffectiveFrom.HasValue)
            headerNode["invoiceRegistrationEffectiveFrom"] = JsonValue.Create(verification.EffectiveFrom.Value.ToString("yyyy-MM-dd"));
        else
            headerNode.Remove("invoiceRegistrationEffectiveFrom");

        if (verification.EffectiveTo.HasValue)
            headerNode["invoiceRegistrationEffectiveTo"] = JsonValue.Create(verification.EffectiveTo.Value.ToString("yyyy-MM-dd"));
        else
            headerNode.Remove("invoiceRegistrationEffectiveTo");

        var isValid = verification.Status == InvoiceVerificationStatus.Matched;
        var message = verification.Status switch
        {
            InvoiceVerificationStatus.Matched => null,
            InvoiceVerificationStatus.NotFound => $"インボイス登録番号 {normalized} は国税庁データベースに見つかりませんでした",
            InvoiceVerificationStatus.Inactive => $"インボイス登録番号 {normalized} はまだ有効期間に入っていません（開始日: {verification.EffectiveFrom?.ToString("yyyy-MM-dd")}）",
            InvoiceVerificationStatus.Expired => $"インボイス登録番号 {normalized} は有効期限が切れています（終了日: {verification.EffectiveTo?.ToString("yyyy-MM-dd")}）",
            _ => $"インボイス登録番号 {normalized} の検証に失敗しました"
        };

        return new InvoiceValidationResult
        {
            HasRegistrationNo = true,
            IsValid = isValid,
            Status = statusKey,
            Message = message,
            RegistrationNo = normalized
        };
    }

    /// <summary>
    /// Removes all cached invoice-registration metadata fields from the header.
    /// </summary>
    private static void ClearInvoiceRegistrationMetadata(JsonObject headerNode)
    {
        headerNode.Remove("invoiceRegistrationStatus");
        headerNode.Remove("invoiceRegistrationName");
        headerNode.Remove("invoiceRegistrationNameKana");
        headerNode.Remove("invoiceRegistrationCheckedAt");
        headerNode.Remove("invoiceRegistrationEffectiveFrom");
        headerNode.Remove("invoiceRegistrationEffectiveTo");
    }

    /// <summary>
    /// Ensures the payload contains a header object and returns it.
    /// </summary>
    private static JsonObject EnsureHeader(JsonObject payloadNode)
    {
        if (payloadNode.TryGetPropertyValue("header", out var headerNode) && headerNode is JsonObject headerObj)
        {
            return headerObj;
        }
        var header = new JsonObject();
        payloadNode["header"] = header;
        return header;
    }

    /// <summary>
    /// Applies creation audit fields onto the voucher header.
    /// </summary>
    internal void ApplyVoucherCreateAudit(JsonObject payloadNode, Auth.UserCtx userCtx)
    {
        var header = EnsureHeader(payloadNode);
        AuditStamp.ApplyCreate(header, userCtx);
    }

    /// <summary>
    /// Applies update audit fields onto the voucher header.
    /// </summary>
    internal void ApplyVoucherUpdateAudit(JsonObject payloadNode, Auth.UserCtx userCtx)
    {
        var header = EnsureHeader(payloadNode);
        AuditStamp.ApplyUpdate(header, userCtx);
    }

    /// <summary>
    /// Converts the raw JSON payload into a JsonObject and stamps create audit info.
    /// </summary>
    internal string PrepareRootCreateJson(JsonElement payload, Auth.UserCtx userCtx)
    {
        var node = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject();
        AuditStamp.ApplyCreate(node, userCtx);
        return node.ToJsonString();
    }

    /// <summary>
    /// Converts the raw JSON payload into a JsonObject and stamps update audit info.
    /// </summary>
    internal string PrepareRootUpdateJson(JsonElement payload, Auth.UserCtx userCtx)
    {
        var node = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject();
        AuditStamp.ApplyUpdate(node, userCtx);
        return node.ToJsonString();
    }

    /// <summary>
    /// Validates whether a voucher update is permitted given period states and whether changes
    /// are text-only. Throws when rule violations occur.
    /// </summary>
    public async Task EnsureVoucherUpdateAllowed(string companyCode, string existingPayloadJson, JsonElement updatedPayload)
    {
        using var originalDoc = JsonDocument.Parse(existingPayloadJson);
        using var updatedDoc = JsonDocument.Parse(updatedPayload.GetRawText());

        var oldPosting = ParsePostingDate(originalDoc.RootElement);
        var newPosting = ParsePostingDate(updatedDoc.RootElement);
        var textOnly = IsTextOnlyChange(originalDoc.RootElement, updatedDoc.RootElement);

        if (oldPosting.HasValue)
        {
            var oldState = await GetPeriodStateAsync(companyCode, oldPosting.Value);
            // 期间不存在或已关闭
            if (!oldState.Exists || !oldState.IsOpen)
            {
                if (!textOnly)
                    throw new Exception($"対象の会計期間（{oldPosting.Value:yyyy-MM}）は閉鎖されています。テキスト項目のみ更新可能です。");
                if (newPosting.HasValue && newPosting.Value.Date != oldPosting.Value.Date)
                    throw new Exception($"対象の会計期間（{oldPosting.Value:yyyy-MM}）は閉鎖されています。転記日の変更はできません。");
            }
        }

        if (newPosting.HasValue)
        {
            var newState = await GetPeriodStateAsync(companyCode, newPosting.Value);
            // 期间不存在或已关闭
            if ((!newState.Exists || !newState.IsOpen) && !textOnly)
                throw new Exception($"対象の会計期間（{newPosting.Value:yyyy-MM}）は閉鎖されています。テキスト項目のみ更新可能です。");
        }
    }

    /// <summary>
    /// Ensures the voucher can be deleted (i.e., posting period is still open and voucher is not reversed/reversal).
    /// 期间存在于数据库 = 打开，期间不存在 = 关闭。
    /// </summary>
    public async Task EnsureVoucherDeleteAllowed(string companyCode, string existingPayloadJson, Guid? voucherId = null)
    {
        if (string.IsNullOrWhiteSpace(existingPayloadJson)) return;
        using var doc = JsonDocument.Parse(existingPayloadJson);
        var root = doc.RootElement;
        
        // Check if voucher has been reversed
        if (root.TryGetProperty("reversal", out var reversalEl) && reversalEl.ValueKind == JsonValueKind.Object)
        {
            var reversalVoucherNo = reversalEl.TryGetProperty("reversalVoucherNo", out var rvn) && rvn.ValueKind == JsonValueKind.String
                ? rvn.GetString() : "";
            throw new Exception($"この伝票は既に反対仕訳されています（{reversalVoucherNo}）。削除できません。");
        }
        
        // Check if voucher is a reversal voucher itself
        if (root.TryGetProperty("isReversal", out var isRevEl) && isRevEl.ValueKind == JsonValueKind.True)
        {
            throw new Exception("反対仕訳は削除できません。");
        }
        
        // Check if any open_items of this voucher have been cleared by another voucher
        if (voucherId.HasValue)
        {
            await using var conn = await _ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT cleared_by FROM open_items 
                WHERE company_code = $1 AND voucher_id = $2 AND cleared_flag = true
                LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(voucherId.Value);
            var clearedBy = (string?)await cmd.ExecuteScalarAsync();
            if (clearedBy is not null)
            {
                throw new Exception($"この伝票は他の伝票（{clearedBy}）により消込済みです。先に消込を解除してから削除してください。");
            }
        }
        
        var postingDate = ParsePostingDate(root);
        if (!postingDate.HasValue) return;
        var state = await GetPeriodStateAsync(companyCode, postingDate.Value);
        // 期间不存在或已关闭
        if (!state.Exists || !state.IsOpen)
            throw new Exception($"対象の会計期間（{postingDate.Value:yyyy-MM}）は閉鎖されています。伝票の削除はできません。");
    }

    /// <summary>
    /// Guarantees the header has a valid posting date (defaults to today if absent).
    /// </summary>
    private static DateTime EnsurePostingDate(JsonObject headerNode)
    {
        DateTime postingDate;
        if (headerNode.TryGetPropertyValue("postingDate", out var postingValue) && postingValue is JsonValue val)
        {
            if (val.TryGetValue<string>(out var str) && DateTime.TryParse(str, out postingDate))
            {
                postingDate = postingDate.Date;
                headerNode["postingDate"] = JsonValue.Create(postingDate.ToString("yyyy-MM-dd"));
                return postingDate;
            }
        }

        postingDate = DateTime.UtcNow.Date;
        headerNode["postingDate"] = JsonValue.Create(postingDate.ToString("yyyy-MM-dd"));
        return postingDate;
    }

    /// <summary>
    /// Extracts the posting date from a voucher payload if present.
    /// </summary>
    private static DateTime? ParsePostingDate(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("header", out var header) || header.ValueKind != JsonValueKind.Object) return null;
        if (!header.TryGetProperty("postingDate", out var postingEl)) return null;
        if (postingEl.ValueKind == JsonValueKind.String && DateTime.TryParse(postingEl.GetString(), out var result))
            return result.Date;
        return null;
    }

    /// <summary>
    /// Removes textual-only fields before comparing payloads to determine if a change is purely textual.
    /// </summary>
    private static void StripVoucherTextualFields(JsonObject root)
    {
        if (root.TryGetPropertyValue("header", out var headerValue) && headerValue is JsonObject header)
        {
            foreach (var field in HeaderTextFields) header.Remove(field);
            foreach (var field in HeaderAutoFields) header.Remove(field);
        }
        if (root.TryGetPropertyValue("lines", out var linesValue) && linesValue is JsonArray lines)
        {
            foreach (var node in lines)
            {
                if (node is JsonObject line)
                {
                    foreach (var field in LineTextFields) line.Remove(field);
                    foreach (var field in LineAutoFields) line.Remove(field);
                }
            }
        }
    }

    /// <summary>
    /// Determines whether two voucher payloads differ only in textual fields (summary, memo, etc.).
    /// </summary>
    private static bool IsTextOnlyChange(JsonElement original, JsonElement updated)
    {
        var originalNode = JsonNode.Parse(original.GetRawText()) as JsonObject;
        var updatedNode = JsonNode.Parse(updated.GetRawText()) as JsonObject;
        if (originalNode is null || updatedNode is null) return false;
        StripVoucherTextualFields(originalNode);
        StripVoucherTextualFields(updatedNode);
        return JsonNode.DeepEquals(originalNode, updatedNode);
    }

    /// <summary>
    /// Result of querying the accounting period table.
    /// </summary>
    private readonly record struct PeriodState(bool Exists, bool IsOpen);

    /// <summary>
    /// Loads the accounting-period state for the specified posting date.
    /// </summary>
    private async Task<PeriodState> GetPeriodStateAsync(string companyCode, DateTime postingDate)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT is_open
                             FROM accounting_periods
                             WHERE company_code=$1
                               AND period_start IS NOT NULL
                               AND period_end IS NOT NULL
                               AND $2::date BETWEEN period_start AND period_end
                             ORDER BY period_start DESC
                             LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(postingDate.Date);
        var obj = await cmd.ExecuteScalarAsync();
        if (obj is bool b) return new PeriodState(true, b);
        if (obj is null || obj is DBNull) return new PeriodState(false, true);
        return new PeriodState(true, Convert.ToBoolean(obj));
    }

    /// <summary>
    /// Copies immutable header fields (voucher number, created timestamps, etc.) from source to target.
    /// </summary>
    private static void CopyImmutableHeaderFields(JsonObject target, JsonObject source)
    {
        foreach (var kvp in source)
        {
            var key = kvp.Key;
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (key.Equals("voucherNo", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("companyCode", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("created", StringComparison.OrdinalIgnoreCase))
            {
                target[key] = kvp.Value?.DeepClone();
            }
        }
    }

    /// <summary>
    /// Asserts that all referenced account codes exist before persisting data.
    /// </summary>
    private static async Task EnsureAccountCodesExistAsync(NpgsqlConnection conn, NpgsqlTransaction? transaction, string companyCode, IReadOnlyCollection<string> accountCodes, CancellationToken ct = default)
    {
        if (accountCodes.Count == 0) return;
        var distinct = accountCodes.Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinct.Length == 0) return;
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT account_code FROM accounts WHERE company_code = @company AND account_code = ANY(@codes)";
        cmd.Parameters.Add(new NpgsqlParameter<string>("company", companyCode));
        cmd.Parameters.Add(new NpgsqlParameter<string[]>("codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = distinct
        });
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            found.Add(reader.GetString(0));
        }
        await reader.DisposeAsync();
        var missing = distinct.Where(code => !found.Contains(code)).ToArray();
        if (missing.Length > 0)
        {
            throw new Exception($"勘定科目コードが存在しません: {string.Join(", ", missing)}");
        }
    }

    /// <summary>
    /// Updates an existing voucher while re-running balance validation, account checks,
    /// invoice registration enrichment, and open-item rebuilds.
    /// </summary>
    public async Task<string> UpdateVoucherAsync(string companyCode, Guid voucherId, JsonElement payload, Auth.UserCtx userCtx)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            string? existingRowJson;
            await using (var selectCmd = conn.CreateCommand())
            {
                selectCmd.Transaction = tx;
                selectCmd.CommandText = "SELECT to_jsonb(vouchers) FROM vouchers WHERE company_code=$1 AND id=$2 FOR UPDATE";
                selectCmd.Parameters.AddWithValue(companyCode);
                selectCmd.Parameters.AddWithValue(voucherId);
                existingRowJson = (string?)await selectCmd.ExecuteScalarAsync();
            }

            if (existingRowJson is null)
            {
                await tx.RollbackAsync();
                throw new Exception("会計伝票が見つかりません");
            }

            using var existingDoc = JsonDocument.Parse(existingRowJson);
            var existingRoot = existingDoc.RootElement;
            var existingPayloadElement = existingRoot.TryGetProperty("payload", out var p) ? p : throw new Exception("既存伝票データが不正です");
            var existingPayloadNode = JsonNode.Parse(existingPayloadElement.GetRawText()) as JsonObject ?? new JsonObject();
            var existingHeaderNode = existingPayloadNode.TryGetPropertyValue("header", out var eh) && eh is JsonObject ehObj ? ehObj : new JsonObject();
            var existingVoucherNo = existingHeaderNode.TryGetPropertyValue("voucherNo", out var vnNode) && vnNode is JsonValue vnValue && vnValue.TryGetValue<string>(out var voucherNoStr)
                ? voucherNoStr
                : string.Empty;

            var payloadNode = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject();
            var headerNode = EnsureHeader(payloadNode);
            CopyImmutableHeaderFields(headerNode, existingHeaderNode);
            headerNode["companyCode"] = JsonValue.Create(companyCode);
            if (!string.IsNullOrWhiteSpace(existingVoucherNo))
            {
                headerNode["voucherNo"] = JsonValue.Create(existingVoucherNo);
            }

            ApplyVoucherUpdateAudit(payloadNode, userCtx);
            var postingDate = EnsurePostingDate(headerNode);
            await ApplyInvoiceRegistrationToHeaderAsync(headerNode);

            var linesNode = payloadNode.TryGetPropertyValue("lines", out var ln) && ln is JsonArray lnArr ? lnArr : new JsonArray();
            payloadNode["lines"] = linesNode;

            var normalizedLines = new List<JsonObject>();
            decimal debitTotal = 0m;
            decimal creditTotal = 0m;
            JsonObject? firstDebit = null;
            JsonObject? firstCredit = null;

            static decimal ReadAmount(JsonObject line)
            {
                if (line.TryGetPropertyValue("amount", out var node) && node is JsonValue value)
                {
                    if (value.TryGetValue<decimal>(out var dec)) return dec;
                    if (value.TryGetValue<double>(out var dbl)) return Convert.ToDecimal(dbl);
                    if (value.TryGetValue<string>(out var str) && decimal.TryParse(str, out var parsed)) return parsed;
                }
                return 0m;
            }

            static void WriteAmount(JsonObject line, decimal amount)
            {
                line["amount"] = amount;
            }

            static string ReadSide(JsonObject line)
            {
                string? Extract(JsonNode? node)
                {
                    if (node is JsonValue val && val.TryGetValue<string>(out var raw))
                    {
                        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToUpperInvariant();
                    }
                    return null;
                }

            if (line.TryGetPropertyValue("drcr", out var drcrNode))
                {
                    var side = Extract(drcrNode);
                    if (!string.IsNullOrEmpty(side))
                    {
                        line["drcr"] = side;
                        return side;
                    }
                }

                if (line.TryGetPropertyValue("side", out var sideNode))
                {
                    var side = Extract(sideNode);
                    if (!string.IsNullOrEmpty(side))
                    {
                        line["drcr"] = side;
                        line.Remove("side");
                        return side;
                    }
                    line.Remove("side");
                }

                return string.Empty;
            }

            RecalculateTotals();

            /// <summary>
            /// Rebuilds debit/credit totals and normalizes line entries.
            /// </summary>
            void RecalculateTotals()
            {
                debitTotal = 0m;
                creditTotal = 0m;
                firstDebit = null;
                firstCredit = null;
                normalizedLines.Clear();

                foreach (var item in linesNode)
                {
                    if (item is not JsonObject line) continue;
                    var side = ReadSide(line);
                    if (string.IsNullOrEmpty(side)) side = "DR";
                    line["drcr"] = side;
                    var amount = ReadAmount(line);
                    if (side == "DR")
                    {
                        debitTotal += amount;
                        firstDebit ??= line;
                    }
                    else if (side == "CR")
                    {
                        creditTotal += amount;
                        firstCredit ??= line;
                    }
                    normalizedLines.Add(line);
                }
            }

            _ = postingDate; // postingDate already ensured, suppress unused warning in case.

            var invoiceObj = payloadNode.TryGetPropertyValue("analysis", out var analysisNode) && analysisNode is JsonObject analysisObj ? analysisObj : null;

            decimal? expectedTotalAmount = null;
            JsonObject? invoiceObjCache = invoiceObj;

            /// <summary>
            /// Reads a decimal value (supports number/double/string) from the invoice analysis object.
            /// </summary>
            decimal? ReadDecimal(JsonObject? obj, string property)
            {
                if (obj is null) return null;
                if (!obj.TryGetPropertyValue(property, out var node) || node is not JsonValue value) return null;
                if (value.TryGetValue<decimal>(out var decValue)) return decValue;
                if (value.TryGetValue<double>(out var dblValue)) return Convert.ToDecimal(dblValue);
                if (value.TryGetValue<string>(out var strValue) && decimal.TryParse(strValue, out var parsedValue)) return parsedValue;
                return null;
            }

            /// <summary>
            /// Applies invoice-total/tax adjustments to the first debit/credit lines to align with AI totals.
            /// </summary>
            void ApplyInvoiceAdjustments()
            {
                if (invoiceObjCache is null) return;
                var totalAmount = ReadDecimal(invoiceObjCache, "totalAmount");
                var taxAmount = ReadDecimal(invoiceObjCache, "taxAmount");
                if (totalAmount is null) return;
                expectedTotalAmount = totalAmount;
                if (taxAmount is not null && firstDebit is not null && firstCredit is not null)
                {
                    var net = totalAmount.Value - taxAmount.Value;
                    WriteAmount(firstDebit, net);
                    WriteAmount(firstCredit, totalAmount.Value);
                    RecalculateTotals();
                }
            }

            ApplyInvoiceAdjustments();

            if (debitTotal != creditTotal)
            {
                throw new Exception($"借方({debitTotal})と貸方({creditTotal})が一致しません");
            }

            var finalLinesArray = new JsonArray();
            int seq = 1;
            var accountCodeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in normalizedLines)
            {
                var toAdd = line.DeepClone().AsObject();
                toAdd["lineNo"] = seq++;
                if (toAdd.TryGetPropertyValue("accountCode", out var codeNode) && codeNode is JsonValue codeVal && codeVal.TryGetValue<string>(out var codeStr) && !string.IsNullOrWhiteSpace(codeStr))
                {
                    accountCodeSet.Add(codeStr.Trim());
                }
                finalLinesArray.Add(toAdd);
            }
            payloadNode["lines"] = finalLinesArray;

            // Normalize attachments before persisting:
            // - Ensure each attachment is an object with blobName
            // - Strip transient fields (url/previewUrl) so we don't persist expiring SAS URLs
            // - If client sends { id: "...", url: "..." } without blobName, treat id as blobName for backward compatibility
            static void NormalizeAttachments(JsonObject root)
            {
                if (!root.TryGetPropertyValue("attachments", out var attachmentsNode) || attachmentsNode is null)
                    return;

                if (attachmentsNode is JsonArray arr)
                {
                    for (var i = 0; i < arr.Count; i++)
                    {
                        var item = arr[i];
                        if (item is JsonValue v && v.TryGetValue<string>(out var rawId) && !string.IsNullOrWhiteSpace(rawId))
                        {
                            // Old payload: ["blobName1", "blobName2"]
                            arr[i] = new JsonObject
                            {
                                ["id"] = rawId,
                                ["blobName"] = rawId,
                                ["name"] = "ファイル"
                            };
                            continue;
                        }

                        if (item is not JsonObject obj) continue;

                        // If blobName missing, fall back to id.
                        if (!obj.TryGetPropertyValue("blobName", out var blobNode) ||
                            blobNode is not JsonValue blobVal ||
                            !blobVal.TryGetValue<string>(out var blobName) ||
                            string.IsNullOrWhiteSpace(blobName))
                        {
                            if (obj.TryGetPropertyValue("id", out var idNode) &&
                                idNode is JsonValue idVal &&
                                idVal.TryGetValue<string>(out var idStr) &&
                                !string.IsNullOrWhiteSpace(idStr))
                            {
                                obj["blobName"] = idStr;
                            }
                        }

                        // Never persist SAS urls / UI-only fields.
                        obj.Remove("url");
                        obj.Remove("previewUrl");
                        obj.Remove("urlError");
                        obj.Remove("objectUrl");

                        // Ensure id exists for UI lists
                        if (!obj.TryGetPropertyValue("id", out var id2) ||
                            id2 is not JsonValue id2Val ||
                            !id2Val.TryGetValue<string>(out var id2Str) ||
                            string.IsNullOrWhiteSpace(id2Str))
                        {
                            if (obj.TryGetPropertyValue("blobName", out var bn) &&
                                bn is JsonValue bnVal &&
                                bnVal.TryGetValue<string>(out var bnStr) &&
                                !string.IsNullOrWhiteSpace(bnStr))
                            {
                                obj["id"] = bnStr;
                            }
                            else
                            {
                                obj["id"] = Guid.NewGuid().ToString();
                            }
                        }
                    }
                    return;
                }

                // If attachments is not an array, drop it to avoid unexpected schema persistence.
                root.Remove("attachments");
            }

            NormalizeAttachments(payloadNode);

            await EnsureAccountCodesExistAsync(conn, tx, companyCode, accountCodeSet, CancellationToken.None);

            var payloadJson = payloadNode.ToJsonString();
            using var payloadDoc = JsonDocument.Parse(payloadJson);
            var payloadElement = payloadDoc.RootElement;

            string? violation = null;
            var accountMetaCache = new Dictionary<string, AccountMeta?>(StringComparer.OrdinalIgnoreCase);

            async Task<AccountMeta?> LoadAccountMetaAsync(string accountCode)
            {
                if (accountMetaCache.TryGetValue(accountCode, out var cached)) return cached;
                await using var q = conn.CreateCommand();
                q.Transaction = tx;
                q.CommandText = "SELECT payload FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1";
                q.Parameters.AddWithValue(companyCode);
                q.Parameters.AddWithValue(accountCode);
                await using var rd = await q.ExecuteReaderAsync();
                AccountMeta? meta = null;
                if (await rd.ReadAsync())
                {
                    var payloadText = rd.GetFieldValue<string>(0);
                    using var ap = JsonDocument.Parse(payloadText);
                    var root = ap.RootElement;
                    var fieldRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (root.TryGetProperty("fieldRules", out var fr) && fr.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in fr.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(prop.Value.GetString()))
                                fieldRules[prop.Name] = prop.Value.GetString()!;
                        }
                    }
                    var openItem = root.TryGetProperty("openItem", out var oi) && oi.ValueKind == JsonValueKind.True;
                    meta = new AccountMeta { OpenItem = openItem, FieldRules = fieldRules };
                }
                accountMetaCache[accountCode] = meta;
                return meta;
            }

            foreach (var (line, idx) in payloadElement.GetProperty("lines").EnumerateArray().Select((v, i) => (v, i)))
            {
                if (line.TryGetProperty("isTaxLine", out var taxFlag) && taxFlag.ValueKind == JsonValueKind.True)
                    continue;

                var accountCode = line.GetProperty("accountCode").GetString() ?? string.Empty;
                var meta = await LoadAccountMetaAsync(accountCode);
                if (meta is null || meta.FieldRules.Count == 0) continue;

                string? StateOf(string field) => meta.FieldRules.TryGetValue(field, out var s) ? s : null;
                bool IsMissing(string field)
                    => !line.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null || (v.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(v.GetString()));
                var fieldLabelMap2 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["customerId"] = "得意先",
                    ["vendorId"] = "仕入先",
                    ["employeeId"] = "従業員",
                    ["departmentId"] = "部門",
                    ["paymentDate"] = "支払日"
                };
                string FieldLabel2(string f) => fieldLabelMap2.TryGetValue(f, out var l) ? l : f;
                void Require(string f)
                {
                    if (violation is null && StateOf(f) == "required" && IsMissing(f))
                        violation = $"明細行 {idx + 1}（勘定科目 {accountCode}）：{FieldLabel2(f)} は必須です";
                }
                void Forbid(string f)
                {
                    if (violation is null && StateOf(f) == "hidden" && !IsMissing(f))
                        violation = $"明細行 {idx + 1}（勘定科目 {accountCode}）：{FieldLabel2(f)} は入力できません";
                }

                Require("customerId"); Require("vendorId"); Require("employeeId"); Require("departmentId"); Require("paymentDate");
                Forbid("customerId"); Forbid("vendorId"); Forbid("employeeId"); Forbid("departmentId"); Forbid("paymentDate");
                if (violation is not null) break;
            }
            if (violation is not null) throw new Exception(violation);

            await EnsureVoucherUpdateAllowed(companyCode, existingPayloadElement.GetRawText(), payloadElement);

            string? updatedRowJson;
            await using (var updateCmd = conn.CreateCommand())
            {
                updateCmd.Transaction = tx;
                updateCmd.CommandText = @"UPDATE vouchers
SET payload = jsonb_set(jsonb_set($1::jsonb, '{header,companyCode}', to_jsonb($2::text), true), '{header,voucherNo}', to_jsonb($3::text), true),
    updated_at = now()
WHERE company_code=$2 AND id=$4
RETURNING to_jsonb(vouchers)";
                updateCmd.Parameters.AddWithValue(payloadJson);
                updateCmd.Parameters.AddWithValue(companyCode);
                updateCmd.Parameters.AddWithValue(existingVoucherNo);
                updateCmd.Parameters.AddWithValue(voucherId);
                updatedRowJson = (string?)await updateCmd.ExecuteScalarAsync();
            }

            if (updatedRowJson is null)
            {
                await tx.RollbackAsync();
                throw new Exception("会計伝票の更新に失敗しました");
            }

            using var updatedDoc = JsonDocument.Parse(updatedRowJson);
            var updatedPayload = updatedDoc.RootElement.GetProperty("payload");
            await RebuildOpenItemsForVoucherAsync(conn, tx, companyCode, voucherId, updatedPayload, LoadAccountMetaAsync);

            await tx.CommitAsync();
            
            // 刷新总账物化视图，确保报表数据一致
            await RefreshGlViewAsync(conn);
            
            return updatedRowJson;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}


