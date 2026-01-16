using Server.Infrastructure.Modules;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules.Standard;

/// <summary>
/// Moneytree银行对接模块 - 标准版
/// </summary>
public class MoneytreeStandardModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "moneytree",
        Name = "银行对接",
        Description = "Moneytree银行数据导入、自动记账规则等功能",
        Category = ModuleCategory.Standard,
        Version = "1.0.0",
        Dependencies = new[] { "finance_core" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_moneytree", Label = "menu.moneytree", Icon = "CreditCard", Path = "/moneytree/transactions", ParentId = "menu_finance", Order = 160 },
        }
    };
    
    public override void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<MoneytreePostingRuleService>();
        services.AddScoped<MoneytreePostingService>();
        services.AddSingleton<MoneytreeCsvParser>();
        services.AddScoped<MoneytreeDownloadService>();
        services.AddScoped<MoneytreeImportService>();
        services.AddSingleton<MoneytreePostingJobQueue>();
        services.AddHostedService<MoneytreePostingBackgroundService>();
    }

    public override void MapEndpoints(WebApplication app)
    {
        // Most Moneytree endpoints are currently still in Program.cs (transactions list, posting run/simulate, rules list/create/update).
        // This module adds missing endpoints required by current frontend.

        // POST /integrations/moneytree/transactions/unlink { ids: string[] }
        app.MapPost("/integrations/moneytree/transactions/unlink", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ids", out var idsEl) || idsEl.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { error = "ids required" });

            var ids = idsEl.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String && Guid.TryParse(x.GetString(), out _))
                .Select(x => Guid.Parse(x.GetString()!))
                .ToArray();
            if (ids.Length == 0) return Results.Ok(new { ok = true, updated = 0 });

            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE moneytree_transactions
                   SET voucher_id = NULL,
                       voucher_no = NULL,
                       posting_status = 'pending',
                       posting_error = NULL,
                       updated_at = now()
                 WHERE company_code = $1
                   AND id = ANY($2::uuid[])";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(ids);
            var n = await cmd.ExecuteNonQueryAsync(req.HttpContext.RequestAborted);
            return Results.Ok(new { ok = true, updated = n });
        }).RequireAuthorization();

        // DELETE /integrations/moneytree/rules/{id}  (soft delete -> set inactive)
        app.MapDelete("/integrations/moneytree/rules/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE moneytree_posting_rules SET is_active = false, updated_at = now() WHERE company_code = $1 AND id = $2";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(id);
            var n = await cmd.ExecuteNonQueryAsync(req.HttpContext.RequestAborted);
            return n > 0 ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "rule not found" });
        }).RequireAuthorization();

        // Approval task completion endpoint (front-end uses approval task id).
        // POST /integrations/moneytree/posting/tasks/{taskId}/complete
        // Marks approval_tasks (entity=moneytree_posting) as approved for the same run/object_id.
        app.MapPost("/integrations/moneytree/posting/tasks/{taskId:guid}/complete", async (Guid taskId, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var user = Auth.GetUserCtx(req);

            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);

            // Find object_id for the given task
            Guid objectId;
            await using (var q = conn.CreateCommand())
            {
                q.CommandText = "SELECT object_id FROM approval_tasks WHERE company_code=$1 AND id=$2 AND entity='moneytree_posting' LIMIT 1";
                q.Parameters.AddWithValue(cc.ToString());
                q.Parameters.AddWithValue(taskId);
                var obj = await q.ExecuteScalarAsync(req.HttpContext.RequestAborted);
                if (obj is not Guid g) return Results.NotFound(new { error = "task not found" });
                objectId = g;
            }

            // Mark all tasks for this run as approved
            await using (var up = conn.CreateCommand())
            {
                up.CommandText = "UPDATE approval_tasks SET status='approved', updated_at=now() WHERE company_code=$1 AND entity='moneytree_posting' AND object_id=$2";
                up.Parameters.AddWithValue(cc.ToString());
                up.Parameters.AddWithValue(objectId);
                var n = await up.ExecuteNonQueryAsync(req.HttpContext.RequestAborted);
                return Results.Ok(new { ok = true, updated = n, objectId = objectId.ToString(), approvedBy = user.UserId });
            }
        }).RequireAuthorization();

        // ==================== 以下是从 Program.cs 迁移的端点 ====================

        // POST /integrations/moneytree/import - 导入银行数据
        app.MapPost("/integrations/moneytree/import", async (HttpRequest req, MoneytreeImportService importService, ILogger<MoneytreeStandardModule> logger) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "missing x-company-code" });

            string? otpSecret = null;
            var defaultEnd = DateTimeOffset.UtcNow;
            var defaultStart = defaultEnd.AddDays(-30);
            var startDate = defaultStart;
            var endDate = defaultEnd;
            if (req.ContentLength.HasValue && req.ContentLength.Value > 0)
            {
                JsonDocument bodyDoc;
                try
                {
                    bodyDoc = await JsonDocument.ParseAsync(req.Body);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "invalid json" });
                }

                using (bodyDoc)
                {
                    var root = bodyDoc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        if (root.TryGetProperty("otpSecret", out var otpEl) && otpEl.ValueKind == JsonValueKind.String)
                        {
                            var rawOtp = otpEl.GetString();
                            otpSecret = string.IsNullOrWhiteSpace(rawOtp) ? null : rawOtp;
                        }
                        if (root.TryGetProperty("startDate", out var startEl) && startEl.ValueKind == JsonValueKind.String)
                        {
                            if (DateTimeOffset.TryParse(startEl.GetString(), out var parsedStart))
                                startDate = parsedStart;
                        }
                        if (root.TryGetProperty("endDate", out var endEl) && endEl.ValueKind == JsonValueKind.String)
                        {
                            if (DateTimeOffset.TryParse(endEl.GetString(), out var parsedEnd))
                                endDate = parsedEnd;
                        }
                    }
                    else if (root.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                    {
                        return Results.BadRequest(new { error = "invalid payload" });
                    }
                }
            }

            if (startDate > endDate)
                return Results.BadRequest(new { error = "startDate must be earlier than endDate" });

            var requestedBy = req.Headers.TryGetValue("x-user-id", out var userId) && !string.IsNullOrWhiteSpace(userId)
                ? userId.ToString()
                : null;

            var companyCode = cc.ToString();

            try
            {
                var result = await importService.ImportAsync(
                    companyCode,
                    new MoneytreeImportService.MoneytreeImportRequest(otpSecret, startDate, endDate),
                    requestedBy,
                    req.HttpContext.RequestAborted);
                return Results.Json(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Moneytree import failed for company {CompanyCode}", companyCode);
                return Results.Problem(ex.Message);
            }
        }).RequireAuthorization();

        // POST /integrations/moneytree/posting/run - 执行自动记账
        app.MapPost("/integrations/moneytree/posting/run", async (HttpRequest req, MoneytreePostingService postingService, ILogger<MoneytreeStandardModule> logger) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "missing x-company-code" });

            var userCtx = Auth.GetUserCtx(req);

            List<Guid>? ids = null;
            if (req.ContentLength.HasValue && req.ContentLength.Value > 0)
            {
                JsonDocument body;
                try
                {
                    body = await JsonDocument.ParseAsync(req.Body);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "invalid json" });
                }

                using (body)
                {
                    var root = body.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ids", out var idsElement))
                    {
                        if (idsElement.ValueKind != JsonValueKind.Array)
                            return Results.BadRequest(new { error = "ids must be an array" });

                        var parsedIds = new List<Guid>();
                        foreach (var item in idsElement.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var gid))
                                parsedIds.Add(gid);
                        }
                        ids = parsedIds;
                    }
                }
            }

            var batchSize = 20;
            if (req.Query.TryGetValue("batchSize", out var batchVal) && int.TryParse(batchVal, out var parsedBatch) && parsedBatch > 0)
                batchSize = Math.Min(parsedBatch, 100);

            try
            {
                if (ids is not null && ids.Count > 0)
                {
                    var manualResult = await postingService.ProcessSelectedAsync(
                        cc.ToString()!,
                        userCtx,
                        ids,
                        req.HttpContext.RequestAborted);
                    return Results.Json(manualResult);
                }

                var result = await postingService.ProcessAsync(cc.ToString()!, userCtx, batchSize, req.HttpContext.RequestAborted);
                return Results.Json(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Moneytree auto posting failed for company {CompanyCode}", cc.ToString());
                return Results.Problem(ex.Message);
            }
        }).RequireAuthorization();

        // POST /integrations/moneytree/posting/simulate - 模拟记账
        app.MapPost("/integrations/moneytree/posting/simulate", async (HttpRequest req, MoneytreePostingService postingService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "missing x-company-code" });

            JsonDocument body;
            try
            {
                body = await JsonDocument.ParseAsync(req.Body);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "invalid json" });
            }

            using (body)
            {
                var root = body.RootElement;
                if (!root.TryGetProperty("ids", out var idsEl) || idsEl.ValueKind != JsonValueKind.Array)
                    return Results.BadRequest(new { error = "ids array is required" });

                var ids = new List<Guid>();
                foreach (var item in idsEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var gid))
                        ids.Add(gid);
                }
                if (ids.Count == 0)
                    return Results.BadRequest(new { error = "ids array is empty" });

                var result = await postingService.SimulateAsync(cc.ToString()!, ids, req.HttpContext.RequestAborted);
                return Results.Json(new { count = result.Count, items = result });
            }
        }).RequireAuthorization();

        // POST /integrations/moneytree/fix-sequence - 根据上传的 CSV 文件修复 row_sequence
        app.MapPost("/integrations/moneytree/fix-sequence", async (HttpRequest req, NpgsqlDataSource ds, MoneytreeCsvParser parser, ILogger<MoneytreeStandardModule> logger) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "missing x-company-code" });

            var companyCode = cc.ToString()!;

            // 读取上传的文件
            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "请使用 multipart/form-data 上传 CSV 文件" });

            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "请上传 CSV 文件" });

            byte[] content;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms);
                content = ms.ToArray();
            }

            // 解析 CSV
            var rows = parser.Parse(content, file.FileName);
            if (rows.Count == 0)
                return Results.BadRequest(new { error = "CSV 文件为空或格式不正确" });

            logger.LogInformation("[Moneytree] fix-sequence: parsed {Count} rows from CSV", rows.Count);

            // 按日期分组，计算每行在该日期内的序号
            var rowsByDate = rows
                .Select((row, idx) => new { Row = row, CsvIndex = idx + 1 })
                .Where(x => x.Row.TransactionDate.HasValue)
                .GroupBy(x => x.Row.TransactionDate!.Value.Date)
                .ToDictionary(g => g.Key, g => g.ToList());

            int updated = 0;
            int notFound = 0;

            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);

            foreach (var (date, dateRows) in rowsByDate)
            {
                int seqInDate = 0;
                foreach (var item in dateRows)
                {
                    seqInDate++;
                    var row = item.Row;

                    // 计算 hash（和导入时相同的逻辑）
                    var hash = ComputeHash(companyCode, row);

                    // 更新 row_sequence
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        UPDATE moneytree_transactions 
                        SET row_sequence = $1, updated_at = now()
                        WHERE company_code = $2 AND hash = $3";
                    cmd.Parameters.AddWithValue(seqInDate);
                    cmd.Parameters.AddWithValue(companyCode);
                    cmd.Parameters.AddWithValue(hash);

                    var affected = await cmd.ExecuteNonQueryAsync(req.HttpContext.RequestAborted);
                    if (affected > 0)
                        updated++;
                    else
                        notFound++;
                }
            }

            logger.LogInformation("[Moneytree] fix-sequence: updated={Updated}, notFound={NotFound}", updated, notFound);

            return Results.Json(new
            {
                success = true,
                csvRows = rows.Count,
                updated,
                notFound,
                message = $"已更新 {updated} 条记录的排序序号，{notFound} 条记录未找到匹配"
            });
        }).RequireAuthorization().DisableAntiforgery();

        // GET /integrations/moneytree/transactions - 查询交易列表
        app.MapGet("/integrations/moneytree/transactions", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "missing x-company-code" });

            var query = req.Query;
            DateTime? startDate = null;
            DateTime? endDate = null;
            if (query.TryGetValue("startDate", out var startVal) && DateTime.TryParse(startVal, out var parsedStart))
                startDate = parsedStart.Date;
            if (query.TryGetValue("endDate", out var endVal) && DateTime.TryParse(endVal, out var parsedEnd))
                endDate = parsedEnd.Date;
            var typeFilter = query.TryGetValue("type", out var typeVal) ? typeVal.ToString() : "all";
            var keyword = query.TryGetValue("keyword", out var kwVal) ? kwVal.ToString().Trim() : string.Empty;
            var page = query.TryGetValue("page", out var pageVal) && int.TryParse(pageVal, out var parsedPage) && parsedPage > 0 ? parsedPage : 1;
            var pageSize = query.TryGetValue("pageSize", out var sizeVal) && int.TryParse(sizeVal, out var parsedSize) && parsedSize > 0 ? parsedSize : 20;
            pageSize = Math.Min(pageSize, 200);

            var filters = new List<string> { "company_code = $1" };
            var parameterValues = new List<object?> { cc.ToString() };
            var pIdx = 2;

            if (startDate.HasValue)
            {
                filters.Add($"transaction_date >= ${pIdx++}");
                parameterValues.Add(startDate.Value);
            }
            if (endDate.HasValue)
            {
                filters.Add($"transaction_date <= ${pIdx++}");
                parameterValues.Add(endDate.Value);
            }

            if (string.Equals(typeFilter, "deposit", StringComparison.OrdinalIgnoreCase))
                filters.Add("(deposit_amount IS NOT NULL AND deposit_amount > 0)");
            else if (string.Equals(typeFilter, "withdrawal", StringComparison.OrdinalIgnoreCase))
                filters.Add("(withdrawal_amount IS NOT NULL AND withdrawal_amount > 0)");

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                filters.Add($"(description ILIKE ${pIdx++})");
                parameterValues.Add($"%{keyword}%");
            }

            var whereClause = string.Join(" AND ", filters);
            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);

            long total = 0;
            await using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = $"SELECT COUNT(*) FROM moneytree_transactions WHERE {whereClause}";
                foreach (var val in parameterValues)
                {
                    countCmd.Parameters.AddWithValue(val ?? DBNull.Value);
                }
                
                var countObj = await countCmd.ExecuteScalarAsync(req.HttpContext.RequestAborted);
                if (countObj is not null && countObj != DBNull.Value)
                    total = Convert.ToInt64(countObj);
            }

            var items = new List<object>();
            await using (var cmd = conn.CreateCommand())
            {
                // 分页参数紧随其后
                var limitIdx = pIdx++;
                var offsetIdx = pIdx++;

                cmd.CommandText = $@"
SELECT id, transaction_date, deposit_amount, withdrawal_amount, balance, currency, bank_name,
       description, account_name, account_number, posting_status, posting_error,
       voucher_no, rule_title, imported_at, row_sequence
FROM moneytree_transactions
WHERE {whereClause}
ORDER BY transaction_date DESC NULLS LAST, row_sequence ASC
LIMIT ${limitIdx} OFFSET ${offsetIdx}";

                // 1. 先按顺序添加 WHERE 条件参数 ($1, $2, $3...)
                foreach (var val in parameterValues)
                {
                    cmd.Parameters.AddWithValue(val ?? DBNull.Value);
                }
                // 2. 再按顺序添加 LIMIT 和 OFFSET 参数
                cmd.Parameters.AddWithValue(pageSize);
                cmd.Parameters.AddWithValue((page - 1) * pageSize);

                await using var reader = await cmd.ExecuteReaderAsync(req.HttpContext.RequestAborted);
                while (await reader.ReadAsync(req.HttpContext.RequestAborted))
                {
                    items.Add(new
                    {
                        id = reader.GetGuid(0),
                        transactionDate = reader.IsDBNull(1) ? null : reader.GetDateTime(1).ToString("yyyy-MM-dd"),
                        depositAmount = reader.IsDBNull(2) ? (decimal?)null : reader.GetDecimal(2),
                        withdrawalAmount = reader.IsDBNull(3) ? (decimal?)null : reader.GetDecimal(3),
                        balance = reader.IsDBNull(4) ? (decimal?)null : reader.GetDecimal(4),
                        currency = reader.IsDBNull(5) ? null : reader.GetString(5),
                        bankName = reader.IsDBNull(6) ? null : reader.GetString(6),
                        description = reader.IsDBNull(7) ? null : reader.GetString(7),
                        accountName = reader.IsDBNull(8) ? null : reader.GetString(8),
                        accountNumber = reader.IsDBNull(9) ? null : reader.GetString(9),
                        postingStatus = reader.IsDBNull(10) ? null : reader.GetString(10),
                        postingError = reader.IsDBNull(11) ? null : reader.GetString(11),
                        voucherNo = reader.IsDBNull(12) ? null : reader.GetString(12),
                        ruleTitle = reader.IsDBNull(13) ? null : reader.GetString(13),
                        importedAt = reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14).ToString("yyyy-MM-dd HH:mm"),
                        rowSequence = reader.IsDBNull(15) ? (int?)null : reader.GetInt32(15)
                    });
                }
            }

            return Results.Json(new { page, pageSize, total, items });
        }).RequireAuthorization();

        // GET /integrations/moneytree/rules - 获取规则列表
        app.MapGet("/integrations/moneytree/rules", async (HttpRequest req, MoneytreePostingRuleService ruleService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "missing x-company-code" });
            var includeInactive = req.Query.TryGetValue("includeInactive", out var inc) &&
                bool.TryParse(inc.ToString(), out var parsed) && parsed;
            var list = await ruleService.ListAsync(cc.ToString()!, includeInactive, req.HttpContext.RequestAborted);
            return Results.Json(list);
        }).RequireAuthorization();

        // POST /integrations/moneytree/rules - 创建规则
        app.MapPost("/integrations/moneytree/rules", async (HttpRequest req, MoneytreePostingRuleService ruleService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "missing x-company-code" });
            JsonDocument body;
            try
            {
                body = await JsonDocument.ParseAsync(req.Body);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "invalid json" });
            }

            using (body)
            {
                var root = body.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return Results.BadRequest(new { error = "invalid payload" });
                var title = root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String
                    ? titleEl.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(title))
                    return Results.BadRequest(new { error = "title is required" });
                var description = root.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String
                    ? descEl.GetString()
                    : null;
                int? priority = null;
                if (root.TryGetProperty("priority", out var priEl))
                {
                    if (priEl.ValueKind == JsonValueKind.Number && priEl.TryGetInt32(out var priValue))
                        priority = priValue;
                    else if (priEl.ValueKind == JsonValueKind.String && int.TryParse(priEl.GetString(), out var priFromString))
                        priority = priFromString;
                }
                bool? isActive = null;
                if (root.TryGetProperty("isActive", out var activeEl))
                {
                    if (activeEl.ValueKind == JsonValueKind.True) isActive = true;
                    else if (activeEl.ValueKind == JsonValueKind.False) isActive = false;
                    else if (activeEl.ValueKind == JsonValueKind.String && bool.TryParse(activeEl.GetString(), out var parsedActive))
                        isActive = parsedActive;
                }
                JsonNode? matcherNode = null;
                if (root.TryGetProperty("matcher", out var matcherEl) && matcherEl.ValueKind != JsonValueKind.Null && matcherEl.ValueKind != JsonValueKind.Undefined)
                {
                    try { matcherNode = JsonNode.Parse(matcherEl.GetRawText()); }
                    catch (JsonException) { return Results.BadRequest(new { error = "invalid matcher json" }); }
                }
                JsonNode? actionNode = null;
                if (root.TryGetProperty("action", out var actionEl) && actionEl.ValueKind != JsonValueKind.Null && actionEl.ValueKind != JsonValueKind.Undefined)
                {
                    try { actionNode = JsonNode.Parse(actionEl.GetRawText()); }
                    catch (JsonException) { return Results.BadRequest(new { error = "invalid action json" }); }
                }
                var user = Auth.GetUserCtx(req);
                try
                {
                    var created = await ruleService.CreateAsync(cc.ToString()!, new MoneytreePostingRuleService.MoneytreePostingRuleUpsert(
                        title,
                        description,
                        priority,
                        matcherNode,
                        actionNode,
                        isActive), user, req.HttpContext.RequestAborted);
                    return Results.Json(created);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }
        }).RequireAuthorization();

        // PUT /integrations/moneytree/rules/{id} - 更新规则
        app.MapPut("/integrations/moneytree/rules/{id:guid}", async (Guid id, HttpRequest req, MoneytreePostingRuleService ruleService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "missing x-company-code" });
            JsonDocument body;
            try
            {
                body = await JsonDocument.ParseAsync(req.Body);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "invalid json" });
            }

            using (body)
            {
                var root = body.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return Results.BadRequest(new { error = "invalid payload" });
                string? title = null;
                if (root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                    title = titleEl.GetString();
                var description = root.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String
                    ? descEl.GetString()
                    : null;
                int? priority = null;
                if (root.TryGetProperty("priority", out var priEl))
                {
                    if (priEl.ValueKind == JsonValueKind.Number && priEl.TryGetInt32(out var priValue))
                        priority = priValue;
                    else if (priEl.ValueKind == JsonValueKind.String && int.TryParse(priEl.GetString(), out var priFromString))
                        priority = priFromString;
                }
                bool? isActive = null;
                if (root.TryGetProperty("isActive", out var activeEl))
                {
                    if (activeEl.ValueKind == JsonValueKind.True) isActive = true;
                    else if (activeEl.ValueKind == JsonValueKind.False) isActive = false;
                    else if (activeEl.ValueKind == JsonValueKind.String && bool.TryParse(activeEl.GetString(), out var parsedActive))
                        isActive = parsedActive;
                }
                JsonNode? matcherNode = null;
                if (root.TryGetProperty("matcher", out var matcherEl) && matcherEl.ValueKind != JsonValueKind.Null && matcherEl.ValueKind != JsonValueKind.Undefined)
                {
                    try { matcherNode = JsonNode.Parse(matcherEl.GetRawText()); }
                    catch (JsonException) { return Results.BadRequest(new { error = "invalid matcher json" }); }
                }
                JsonNode? actionNode = null;
                if (root.TryGetProperty("action", out var actionEl) && actionEl.ValueKind != JsonValueKind.Null && actionEl.ValueKind != JsonValueKind.Undefined)
                {
                    try { actionNode = JsonNode.Parse(actionEl.GetRawText()); }
                    catch (JsonException) { return Results.BadRequest(new { error = "invalid action json" }); }
                }
                var user = Auth.GetUserCtx(req);
                try
                {
                    var updated = await ruleService.UpdateAsync(cc.ToString()!, id, new MoneytreePostingRuleService.MoneytreePostingRuleUpsert(
                        title,
                        description,
                        priority,
                        matcherNode,
                        actionNode,
                        isActive), user, req.HttpContext.RequestAborted);
                    return updated is null ? Results.NotFound(new { error = "rule not found" }) : Results.Json(updated);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }
        }).RequireAuthorization();
    }

    /// <summary>
    /// 计算行的 hash（和 MoneytreeImportService 中的逻辑保持一致）
    /// </summary>
    private static string ComputeHash(string companyCode, MoneytreeCsvParser.MoneytreeRow row)
    {
        var builder = new StringBuilder();
        builder.Append(companyCode).Append('|');
        builder.Append(row.TransactionDate?.ToString("yyyy-MM-dd") ?? string.Empty).Append('|');
        builder.Append(row.DepositAmount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('|');
        builder.Append(row.WithdrawalAmount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('|');
        var netAmount = (row.DepositAmount ?? 0m) - (row.WithdrawalAmount ?? 0m);
        builder.Append(netAmount.ToString(CultureInfo.InvariantCulture)).Append('|');
        builder.Append(row.Balance?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('|');
        builder.Append(row.Currency ?? string.Empty).Append('|');
        builder.Append(row.BankName ?? string.Empty).Append('|');
        builder.Append(row.Description).Append('|');
        builder.Append(row.AccountName ?? string.Empty).Append('|');
        builder.Append(row.AccountNumber ?? string.Empty);

        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }
}

