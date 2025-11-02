using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

public class FinanceService
{
    private static readonly HashSet<string> HeaderTextFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "summary", "memo", "remarks", "description", "comment", "note"
    };
    private static readonly HashSet<string> HeaderAutoFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "updatedAt", "updatedBy", "updatedByEmployee", "updatedByDept", "updatedByName"
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
    public FinanceService(NpgsqlDataSource ds, InvoiceRegistryService invoiceRegistry)
    {
        _ds = ds;
        _invoiceRegistry = invoiceRegistry;
    }

    public async Task<string> CreateVoucher(string companyCode, string table, JsonElement payload, Auth.UserCtx userCtx)
    {
        var payloadNode = JsonNode.Parse(payload.GetRawText()) as JsonObject
            ?? throw new Exception("invalid voucher payload");
        var linesNode = payloadNode["lines"] as JsonArray ?? new JsonArray();

        var headerNode = EnsureHeader(payloadNode);
        StampCreate(headerNode, userCtx);
        var postingDate = EnsurePostingDate(headerNode);
        await ApplyInvoiceRegistrationOnHeaderAsync(headerNode);
        await EnsureVoucherCreateAllowed(companyCode, postingDate);

        var normalizedLines = new List<JsonObject>();
        decimal dr = 0m, cr = 0m;
        int baseLineIndex = 0;

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
        await using var conn = await _ds.OpenConnectionAsync();
        foreach (var (line, idx) in payloadElement.GetProperty("lines").EnumerateArray().Select((v, i) => (v, i)))
        {
            if (line.TryGetProperty("isTaxLine", out var taxFlag) && taxFlag.ValueKind == JsonValueKind.True)
                continue;

            var accountCode = line.GetProperty("accountCode").GetString() ?? string.Empty;
            await using (var q = conn.CreateCommand())
            {
                q.CommandText = "SELECT payload FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1";
                q.Parameters.AddWithValue(companyCode);
                q.Parameters.AddWithValue(accountCode);
                await using var rd = await q.ExecuteReaderAsync();
                if (await rd.ReadAsync())
                {
                    var payloadText = rd.GetFieldValue<string>(0);
                    using var ap = JsonDocument.Parse(payloadText);
                    var fr = ap.RootElement.TryGetProperty("fieldRules", out var frEl) ? frEl : default;
                    if (fr.ValueKind != JsonValueKind.Object) continue;
                    string? stateOf(string field)
                        => fr.TryGetProperty(field, out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    bool isMissing(string field)
                        => !line.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null || (v.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(v.GetString()));
                    void require(string f)
                    {
                        if (violation is null && stateOf(f) == "required" && isMissing(f))
                            violation = $"lines[{idx}].{f} required by account {accountCode}";
                    }
                    void forbid(string f)
                    {
                        if (violation is null && stateOf(f) == "hidden" && !isMissing(f))
                            violation = $"lines[{idx}].{f} must be empty for account {accountCode}";
                    }

                    require("customerId"); require("vendorId"); require("employeeId"); require("departmentId"); require("paymentDate");
                    forbid("customerId"); forbid("vendorId"); forbid("employeeId"); forbid("departmentId"); forbid("paymentDate");
                }
            }
            if (violation is not null) break;
        }
        if (violation is not null) throw new Exception(violation);

        var numbering = await VoucherNumberingService.NextAsync(_ds, companyCode, postingDate);
        var no = numbering.voucherNo;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"INSERT INTO {table}(company_code, payload)
          VALUES ($1, jsonb_set(jsonb_set($2::jsonb, '{{header,companyCode}}', to_jsonb($1::text), true), '{{header,voucherNo}}', to_jsonb($3::text), true))
          RETURNING to_jsonb({table})";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(payloadJson);
            cmd.Parameters.AddWithValue(no);
            var execObj = await cmd.ExecuteScalarAsync();
            if (execObj is not string json) throw new Exception("insert voucher failed");
            return json;
        }
    }

    public async Task<string> CreateAccount(string companyCode, string table, JsonElement payload, Auth.UserCtx userCtx)
    {
        bool isBank = payload.TryGetProperty("isBank", out var ib) && ib.ValueKind == JsonValueKind.True;
        bool isCash = payload.TryGetProperty("isCash", out var ic) && ic.ValueKind == JsonValueKind.True;
        if (isBank && isCash) throw new Exception("A科目不能同时为银行科目与现金科目");

        if (isBank)
        {
            if (!payload.TryGetProperty("bankInfo", out var bi) || bi.ValueKind != JsonValueKind.Object)
                throw new Exception("银行科目需要维护 bankInfo");
            string[] reqs = new[] { "bankName", "branchName", "accountType", "accountNo", "holder", "currency" };
            foreach (var f in reqs)
            {
                if (!bi.TryGetProperty(f, out var v) || v.ValueKind == JsonValueKind.Null || (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())))
                    throw new Exception($"银行信息缺少必填字段: {f}");
            }
        }
        if (isCash)
        {
            if (!payload.TryGetProperty("cashCurrency", out var ccEl) || ccEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(ccEl.GetString()))
                throw new Exception("现金科目需要设置 cashCurrency");
        }
        var stampedJson = PrepareRootCreateJson(payload, userCtx);
        var inserted3 = await Crud.InsertRawJson(_ds, table, companyCode, stampedJson);
        if (inserted3 is null) throw new Exception("insert failed");
        return inserted3;
    }

    public async Task<string> CreateBank(string companyCode, string table, JsonElement payload, Auth.UserCtx userCtx)
    {
        var stampedJson = PrepareRootCreateJson(payload, userCtx);
        var inserted = await Crud.InsertRawJson(_ds, table, companyCode, stampedJson);
        if (inserted is null) throw new Exception("insert failed");
        return inserted;
    }

    public async Task<string> CreateBranch(string companyCode, string table, JsonElement payload, Auth.UserCtx userCtx)
    {
        var stampedJson = PrepareRootCreateJson(payload, userCtx);
        var inserted = await Crud.InsertRawJson(_ds, table, companyCode, stampedJson);
        if (inserted is null) throw new Exception("insert failed");
        return inserted;
    }

    public async Task EnsureVoucherCreateAllowed(string companyCode, DateTime postingDate)
    {
        var state = await GetPeriodStateAsync(companyCode, postingDate.Date);
        if (state.Exists && !state.IsOpen)
            throw new Exception("会计期间已关闭，禁止新增凭证");
    }

    private static void StampCreate(JsonObject header, Auth.UserCtx ctx)
    {
        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        var userToken = ctx.UserId ?? ctx.EmployeeCode ?? string.Empty;
        if (!header.TryGetPropertyValue("createdAt", out var createdAtNode) || string.IsNullOrWhiteSpace(createdAtNode?.GetValue<string>()))
            header["createdAt"] = JsonValue.Create(nowIso);
        if (!header.TryGetPropertyValue("createdBy", out var createdByNode) || string.IsNullOrWhiteSpace(createdByNode?.GetValue<string>()))
            header["createdBy"] = JsonValue.Create(userToken);
        header["updatedAt"] = JsonValue.Create(nowIso);
        header["updatedBy"] = JsonValue.Create(userToken);
    }

    private static void StampUpdate(JsonObject header, Auth.UserCtx ctx)
    {
        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        var userToken = ctx.UserId ?? ctx.EmployeeCode ?? string.Empty;
        if (!header.TryGetPropertyValue("createdAt", out var createdAtNode) || string.IsNullOrWhiteSpace(createdAtNode?.GetValue<string>()))
            header["createdAt"] = JsonValue.Create(nowIso);
        if (!header.TryGetPropertyValue("createdBy", out var createdByNode) || string.IsNullOrWhiteSpace(createdByNode?.GetValue<string>()))
            header["createdBy"] = JsonValue.Create(userToken);
        header["updatedAt"] = JsonValue.Create(nowIso);
        header["updatedBy"] = JsonValue.Create(userToken);
    }

    public async Task ApplyInvoiceRegistrationAsync(JsonObject payloadNode)
    {
        var header = EnsureHeader(payloadNode);
        await ApplyInvoiceRegistrationOnHeaderAsync(header);
    }

private async Task ApplyInvoiceRegistrationOnHeaderAsync(JsonObject headerNode)
    {
        if (!headerNode.TryGetPropertyValue("invoiceRegistrationNo", out var valNode) || valNode is not JsonValue value || !value.TryGetValue<string>(out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            headerNode["invoiceRegistrationNo"] = JsonValue.Create(string.Empty);
            ClearInvoiceRegistrationMetadata(headerNode);
            return;
        }

        var normalized = InvoiceRegistryService.Normalize(raw);
        headerNode["invoiceRegistrationNo"] = JsonValue.Create(normalized);

        if (!InvoiceRegistryService.IsFormatValid(normalized))
            throw new Exception("インボイス登録番号は T + 13桁の数字で入力してください");

        var verification = await _invoiceRegistry.VerifyAsync(normalized);
        headerNode["invoiceRegistrationStatus"] = JsonValue.Create(InvoiceRegistryService.StatusKey(verification.Status));
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
    }

    private static void ClearInvoiceRegistrationMetadata(JsonObject headerNode)
    {
        headerNode.Remove("invoiceRegistrationStatus");
        headerNode.Remove("invoiceRegistrationName");
        headerNode.Remove("invoiceRegistrationNameKana");
        headerNode.Remove("invoiceRegistrationCheckedAt");
        headerNode.Remove("invoiceRegistrationEffectiveFrom");
        headerNode.Remove("invoiceRegistrationEffectiveTo");
    }

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

    internal void ApplyVoucherCreateAudit(JsonObject payloadNode, Auth.UserCtx userCtx)
    {
        var header = EnsureHeader(payloadNode);
        StampCreate(header, userCtx);
    }

    internal void ApplyVoucherUpdateAudit(JsonObject payloadNode, Auth.UserCtx userCtx)
    {
        var header = EnsureHeader(payloadNode);
        StampUpdate(header, userCtx);
    }

    internal string PrepareRootCreateJson(JsonElement payload, Auth.UserCtx userCtx)
    {
        var node = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject();
        StampCreate(node, userCtx);
        return node.ToJsonString();
    }

    internal string PrepareRootUpdateJson(JsonElement payload, Auth.UserCtx userCtx)
    {
        var node = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject();
        StampUpdate(node, userCtx);
        return node.ToJsonString();
    }

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
            if (oldState.Exists && !oldState.IsOpen)
            {
                if (!textOnly)
                    throw new Exception("会计期间已关闭，仅允许文本更新");
                if (newPosting.HasValue && newPosting.Value.Date != oldPosting.Value.Date)
                    throw new Exception("会计期间已关闭，禁止变更过账日期");
            }
        }

        if (newPosting.HasValue)
        {
            var newState = await GetPeriodStateAsync(companyCode, newPosting.Value);
            if (newState.Exists && !newState.IsOpen && !textOnly)
                throw new Exception("会计期间已关闭，仅允许文本更新");
        }
    }

    public async Task EnsureVoucherDeleteAllowed(string companyCode, string existingPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(existingPayloadJson)) return;
        using var doc = JsonDocument.Parse(existingPayloadJson);
        var postingDate = ParsePostingDate(doc.RootElement);
        if (!postingDate.HasValue) return;
        var state = await GetPeriodStateAsync(companyCode, postingDate.Value);
        if (state.Exists && !state.IsOpen)
            throw new Exception("会计期间已关闭，禁止删除凭证");
    }

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

    private static DateTime? ParsePostingDate(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("header", out var header) || header.ValueKind != JsonValueKind.Object) return null;
        if (!header.TryGetProperty("postingDate", out var postingEl)) return null;
        if (postingEl.ValueKind == JsonValueKind.String && DateTime.TryParse(postingEl.GetString(), out var result))
            return result.Date;
        return null;
    }

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

    private static bool IsTextOnlyChange(JsonElement original, JsonElement updated)
    {
        var originalNode = JsonNode.Parse(original.GetRawText()) as JsonObject;
        var updatedNode = JsonNode.Parse(updated.GetRawText()) as JsonObject;
        if (originalNode is null || updatedNode is null) return false;
        StripVoucherTextualFields(originalNode);
        StripVoucherTextualFields(updatedNode);
        return JsonNode.DeepEquals(originalNode, updatedNode);
    }

    private readonly record struct PeriodState(bool Exists, bool IsOpen);

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
}


