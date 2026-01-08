using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

/// <summary>
/// 固定资产管理模块
/// 包括：资产类别管理、固定资产管理、折旧记账等功能
/// </summary>
public static class FixedAssetModule
{
    public static void MapFixedAssetModule(this WebApplication app)
    {
        // ========================================
        // 资产类别管理 (Asset Classes)
        // ========================================

        // 获取资产类别列表
        app.MapGet("/fixed-assets/classes", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var sql = @"SELECT to_jsonb(t) FROM (
                SELECT ac.*, 
                       (SELECT payload->>'name' FROM accounts WHERE company_code = ac.company_code AND account_code = ac.acquisition_account) as acquisition_account_name,
                       (SELECT payload->>'name' FROM accounts WHERE company_code = ac.company_code AND account_code = ac.disposal_account) as disposal_account_name,
                       (SELECT payload->>'name' FROM accounts WHERE company_code = ac.company_code AND account_code = ac.depreciation_expense_account) as depreciation_expense_account_name,
                       (SELECT payload->>'name' FROM accounts WHERE company_code = ac.company_code AND account_code = ac.accumulated_depreciation_account) as accumulated_depreciation_account_name
                FROM asset_classes ac 
                WHERE ac.company_code = $1 
                ORDER BY ac.class_name
            ) t";
            var rows = await Crud.QueryJsonRows(ds, sql, new object?[] { cc.ToString() });
            return Results.Text("[" + string.Join(',', rows) + "]", "application/json");
        }).RequireAuthorization();

        // 获取单个资产类别
        app.MapGet("/fixed-assets/classes/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var sql = @"SELECT to_jsonb(t) FROM (
                SELECT * FROM asset_classes WHERE id = $1 AND company_code = $2
            ) t";
            var rows = await Crud.QueryJsonRows(ds, sql, new object?[] { id, cc.ToString() });
            if (rows.Count == 0) return Results.NotFound();
            return Results.Text(rows[0], "application/json");
        }).RequireAuthorization();

        // 创建资产类别
        app.MapPost("/fixed-assets/classes", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var json = ExtractPayload(doc.RootElement);
            var result = await Crud.InsertRawJson(ds, "asset_classes", cc!, json);
            return result is null ? Results.Problem("Insert failed") : Results.Text(result, "application/json");
        }).RequireAuthorization();

        // 更新资产类别
        app.MapPut("/fixed-assets/classes/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var json = ExtractPayload(doc.RootElement);
            var result = await Crud.UpdateRawJson(ds, "asset_classes", id, cc!, json);
            return result is null ? Results.NotFound() : Results.Text(result, "application/json");
        }).RequireAuthorization();

        // 删除资产类别
        app.MapDelete("/fixed-assets/classes/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            // 检查是否有关联的资产
            await using var conn = await ds.OpenConnectionAsync();
            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM fixed_assets WHERE company_code = $1 AND asset_class_id = $2::text";
            checkCmd.Parameters.AddWithValue(cc.ToString());
            checkCmd.Parameters.AddWithValue(id);
            var count = (long)(await checkCmd.ExecuteScalarAsync() ?? 0);
            if (count > 0)
                return Results.BadRequest(new { error = $"この資産クラスに関連する資産が{count}件あります。削除できません。" });

            var n = await Crud.DeleteById(ds, "asset_classes", id, cc!);
            return n > 0 ? Results.Ok(new { ok = true, deleted = n }) : Results.NotFound();
        }).RequireAuthorization();

        // ========================================
        // 固定资产管理 (Fixed Assets)
        // ========================================

        // 获取固定资产列表
        app.MapGet("/fixed-assets/assets", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var qp = req.Query;
            var assetClassId = qp["assetClassId"].FirstOrDefault();
            var assetNo = qp["assetNo"].FirstOrDefault();
            var assetName = qp["assetName"].FirstOrDefault();

            var where = new List<string> { "fa.company_code = $1" };
            var args = new List<object?> { cc.ToString() };
            int idx = 2;

            if (!string.IsNullOrWhiteSpace(assetClassId))
            {
                where.Add($"fa.asset_class_id = ${idx}");
                args.Add(assetClassId);
                idx++;
            }
            if (!string.IsNullOrWhiteSpace(assetNo))
            {
                where.Add($"fa.asset_no ILIKE ${idx}");
                args.Add($"%{assetNo}%");
                idx++;
            }
            if (!string.IsNullOrWhiteSpace(assetName))
            {
                where.Add($"fa.asset_name ILIKE ${idx}");
                args.Add($"%{assetName}%");
                idx++;
            }

            var sql = $@"SELECT to_jsonb(t) FROM (
                SELECT fa.*, 
                       ac.class_name as asset_class_name
                FROM fixed_assets fa
                LEFT JOIN asset_classes ac ON ac.id::text = fa.asset_class_id AND ac.company_code = fa.company_code
                WHERE {string.Join(" AND ", where)}
                ORDER BY fa.asset_no
                LIMIT 500
            ) t";

            var rows = await Crud.QueryJsonRows(ds, sql, args);
            return Results.Text("[" + string.Join(',', rows) + "]", "application/json");
        }).RequireAuthorization();

        // 获取单个固定资产（含交易明细和折旧计划预览）
        app.MapGet("/fixed-assets/assets/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            // 获取公司设定中的决算月
            var fiscalYearEndMonth = await GetFiscalYearEndMonthAsync(ds, cc!);

            // 获取资产主数据（含资产类别信息）
            var assetSql = @"SELECT to_jsonb(t) FROM (
                SELECT fa.*, 
                       ac.class_name as asset_class_name,
                       ac.depreciation_expense_account,
                       ac.accumulated_depreciation_account,
                       ac.include_tax_in_depreciation
                FROM fixed_assets fa
                LEFT JOIN asset_classes ac ON ac.id::text = fa.asset_class_id AND ac.company_code = fa.company_code
                WHERE fa.id = $1 AND fa.company_code = $2
            ) t";
            var assetRows = await Crud.QueryJsonRows(ds, assetSql, new object?[] { id, cc.ToString() });
            if (assetRows.Count == 0) return Results.NotFound();

            // 获取已记账的交易明细
            var txSql = @"SELECT to_jsonb(t) FROM (
                SELECT * FROM asset_transactions 
                WHERE asset_id = $1 AND company_code = $2 
                ORDER BY posting_date, created_at
            ) t";
            var txRows = await Crud.QueryJsonRows(ds, txSql, new object?[] { id, cc.ToString() });

            var assetNode = JsonNode.Parse(assetRows[0]);
            if (assetNode is JsonObject assetObj)
            {
                // 添加已记账的交易
                var txArray = new JsonArray();
                var postedDepreciationMonths = new HashSet<string>(); // 记录已记账的折旧月份
                foreach (var txJson in txRows)
                {
                    var txNode = JsonNode.Parse(txJson);
                    if (txNode != null)
                    {
                        txArray.Add(txNode);
                        // 记录已记账的折旧月份
                        var txType = txNode["transaction_type"]?.GetValue<string>() ?? txNode["payload"]?["transactionType"]?.GetValue<string>();
                        if (txType == "DEPRECIATION")
                        {
                            var postingDate = txNode["posting_date"]?.GetValue<string>() ?? txNode["payload"]?["postingDate"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(postingDate) && postingDate.Length >= 7)
                            {
                                postedDepreciationMonths.Add(postingDate.Substring(0, 7)); // YYYY-MM
                            }
                        }
                    }
                }
                assetObj["transactions"] = txArray;

                // 计算未来的折旧计划预览（按事业年度）
                var pendingTransactions = CalculateDepreciationSchedule(assetObj, postedDepreciationMonths, fiscalYearEndMonth);
                assetObj["pendingTransactions"] = pendingTransactions;
            }

            return Results.Text(assetNode?.ToJsonString() ?? assetRows[0], "application/json");
        }).RequireAuthorization();

        // 创建固定资产
        app.MapPost("/fixed-assets/assets", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var payloadNode = JsonNode.Parse(ExtractPayload(root));

            if (payloadNode is JsonObject payload)
            {
                // 部门必填验证
                var departmentId = payload["departmentId"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(departmentId))
                    return Results.BadRequest(new { error = "部門を選択してください（必須項目）" });

                // 自动生成资产编号（如果没有提供）
                if (!payload.ContainsKey("assetNo") || string.IsNullOrWhiteSpace(payload["assetNo"]?.GetValue<string>()))
                {
                    var newAssetNo = await GenerateAssetNoAsync(ds, cc!);
                    payload["assetNo"] = newAssetNo;
                }

                // 设置初始帐簿价额等于取得价额
                if (payload.ContainsKey("acquisitionCost") && !payload.ContainsKey("bookValue"))
                {
                    payload["bookValue"] = payload["acquisitionCost"]?.DeepClone();
                }
            }

            var json = payloadNode?.ToJsonString() ?? ExtractPayload(root);
            var result = await Crud.InsertRawJson(ds, "fixed_assets", cc!, json);
            return result is null ? Results.Problem("Insert failed") : Results.Text(result, "application/json");
        }).RequireAuthorization();

        // 更新固定资产
        app.MapPut("/fixed-assets/assets/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            
            // 部门必填验证
            var departmentId = root.TryGetProperty("departmentId", out var deptProp) ? deptProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(departmentId))
                return Results.BadRequest(new { error = "部門を選択してください（必須項目）" });
            
            var json = ExtractPayload(root);
            var result = await Crud.UpdateRawJson(ds, "fixed_assets", id, cc!, json);
            return result is null ? Results.NotFound() : Results.Text(result, "application/json");
        }).RequireAuthorization();

        // 删除固定资产
        app.MapDelete("/fixed-assets/assets/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            // 检查是否有关联的交易（除了取得交易）
            await using var conn = await ds.OpenConnectionAsync();
            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM asset_transactions WHERE company_code = $1 AND asset_id = $2 AND transaction_type != 'ACQUISITION'";
            checkCmd.Parameters.AddWithValue(cc.ToString());
            checkCmd.Parameters.AddWithValue(id);
            var count = (long)(await checkCmd.ExecuteScalarAsync() ?? 0);
            if (count > 0)
                return Results.BadRequest(new { error = $"この資産に関連する取引（償却等）が{count}件あります。削除できません。" });

            // 先删除取得交易，再删除资产
            await using var delTxCmd = conn.CreateCommand();
            delTxCmd.CommandText = "DELETE FROM asset_transactions WHERE company_code = $1 AND asset_id = $2";
            delTxCmd.Parameters.AddWithValue(cc.ToString());
            delTxCmd.Parameters.AddWithValue(id);
            await delTxCmd.ExecuteNonQueryAsync();

            var n = await Crud.DeleteById(ds, "fixed_assets", id, cc!);
            return n > 0 ? Results.Ok(new { ok = true, deleted = n }) : Results.NotFound();
        }).RequireAuthorization();

        // ========================================
        // 资产取得（创建资产+凭证）
        // ========================================

        // 资产取得
        app.MapPost("/fixed-assets/acquire", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            // 解析请求数据
            var assetClassId = root.TryGetProperty("assetClassId", out var aci) ? aci.GetString() : null;
            var assetName = root.TryGetProperty("assetName", out var an) ? an.GetString() : null;
            var departmentId = root.TryGetProperty("departmentId", out var deptProp) ? deptProp.GetString() : null;
            var depreciationMethod = root.TryGetProperty("depreciationMethod", out var dm) ? dm.GetString() : "STRAIGHT_LINE";
            var usefulLife = root.TryGetProperty("usefulLife", out var ul) ? ul.GetInt32() : 5;
            var acquisitionDate = root.TryGetProperty("acquisitionDate", out var ad) ? ad.GetString() : null;
            var acquisitionCost = root.TryGetProperty("acquisitionCost", out var acv) ? acv.GetDecimal() : 0;
            var remarks = root.TryGetProperty("remarks", out var rm) ? rm.GetString() : "";

            if (string.IsNullOrWhiteSpace(assetClassId))
                return Results.BadRequest(new { error = "資産クラスを選択してください" });
            if (string.IsNullOrWhiteSpace(assetName))
                return Results.BadRequest(new { error = "資産名称を入力してください" });
            if (string.IsNullOrWhiteSpace(departmentId))
                return Results.BadRequest(new { error = "部門を選択してください（必須項目）" });
            if (string.IsNullOrWhiteSpace(acquisitionDate))
                return Results.BadRequest(new { error = "取得日を選択してください" });
            if (acquisitionCost <= 0)
                return Results.BadRequest(new { error = "取得価額を入力してください" });

            // 解析凭证行
            var voucherLines = new JsonArray();
            if (root.TryGetProperty("voucherLines", out var lines))
            {
                foreach (var line in lines.EnumerateArray())
                {
                    voucherLines.Add(JsonNode.Parse(line.GetRawText()));
                }
            }

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 0. 查询资产类别，判断有形/无形
                bool isTangible = true;
                await using var acCmd = conn.CreateCommand();
                acCmd.CommandText = "SELECT payload FROM asset_classes WHERE id = $1::uuid AND company_code = $2";
                acCmd.Parameters.AddWithValue(Guid.Parse(assetClassId!));
                acCmd.Parameters.AddWithValue(cc.ToString());
                var acPayloadStr = await acCmd.ExecuteScalarAsync() as string;
                if (!string.IsNullOrEmpty(acPayloadStr))
                {
                    using var acDoc = JsonDocument.Parse(acPayloadStr);
                    if (acDoc.RootElement.TryGetProperty("isTangible", out var tangibleProp))
                    {
                        isTangible = tangibleProp.GetBoolean();
                    }
                }

                // 1. 生成资产编号
                var assetNo = await GenerateAssetNoAsync(ds, cc!);

                // 2. 计算折旧开始日（有形资产：翌月1日，无形资产：当月1日）
                var acqDate = DateOnly.Parse(acquisitionDate!);
                DateOnly depreciationStartDate;
                if (isTangible)
                {
                    // 有形资产：翌月1日
                    depreciationStartDate = new DateOnly(acqDate.Year, acqDate.Month, 1).AddMonths(1);
                }
                else
                {
                    // 无形资产：当月1日
                    depreciationStartDate = new DateOnly(acqDate.Year, acqDate.Month, 1);
                }

                // 3. 创建资产主数据
                var assetId = Guid.NewGuid();
                var assetPayload = new JsonObject
                {
                    ["assetNo"] = assetNo,
                    ["assetName"] = assetName,
                    ["assetClassId"] = assetClassId,
                    ["departmentId"] = departmentId,
                    ["depreciationMethod"] = depreciationMethod,
                    ["usefulLife"] = usefulLife,
                    ["acquisitionCost"] = acquisitionCost,
                    ["bookValue"] = acquisitionCost, // 初始帐簿价额等于取得价额
                    ["capitalizationDate"] = acquisitionDate,
                    ["depreciationStartDate"] = depreciationStartDate.ToString("yyyy-MM-dd"),
                    ["remarks"] = remarks
                };

                await using var assetCmd = conn.CreateCommand();
                assetCmd.CommandText = "INSERT INTO fixed_assets(id, company_code, payload) VALUES ($1, $2, $3::jsonb)";
                assetCmd.Parameters.AddWithValue(assetId);
                assetCmd.Parameters.AddWithValue(cc.ToString());
                assetCmd.Parameters.AddWithValue(assetPayload.ToJsonString());
                await assetCmd.ExecuteNonQueryAsync();

                // 4. 生成凭证编号
                var dateParts = acquisitionDate!.Split('-');
                var year = int.Parse(dateParts[0]);
                var month = int.Parse(dateParts[1]);
                var voucherNo = await GenerateVoucherNoAsync(conn, cc!, year, month);

                // 5. 创建会计凭证
                var voucherId = Guid.NewGuid();
                var voucherPayloadLines = new JsonArray();
                int lineNo = 1;
                foreach (var line in voucherLines)
                {
                    if (line is JsonObject lineObj)
                    {
                        var newLine = new JsonObject
                        {
                            ["lineNo"] = lineNo++,
                            ["accountCode"] = lineObj["accountCode"]?.DeepClone(),
                            ["drcr"] = lineObj["drcr"]?.DeepClone(),
                            ["amount"] = lineObj["amount"]?.DeepClone(),
                            ["taxRate"] = lineObj["taxRate"]?.DeepClone() ?? 0,
                            ["note"] = $"資産取得「{assetNo} {assetName}」"
                        };
                        // 如果是借方资产科目，关联资产ID
                        if (lineObj["drcr"]?.GetValue<string>() == "DR")
                        {
                            newLine["assetId"] = assetId.ToString();
                        }
                        if (lineObj["vendorId"] != null && !string.IsNullOrEmpty(lineObj["vendorId"]?.GetValue<string>()))
                        {
                            newLine["vendorId"] = lineObj["vendorId"]?.DeepClone();
                        }
                        voucherPayloadLines.Add(newLine);
                    }
                }

                var voucherPayload = new JsonObject
                {
                    ["header"] = new JsonObject
                    {
                        ["companyCode"] = cc.ToString(),
                        ["postingDate"] = acquisitionDate,
                        ["voucherType"] = "AA",
                        ["voucherNo"] = voucherNo,
                        ["currency"] = "JPY",
                        ["summary"] = $"資産取得「{assetNo} {assetName}」"
                    },
                    ["lines"] = voucherPayloadLines
                };

                await using var voucherCmd = conn.CreateCommand();
                voucherCmd.CommandText = "INSERT INTO vouchers(id, company_code, payload) VALUES ($1, $2, $3::jsonb)";
                voucherCmd.Parameters.AddWithValue(voucherId);
                voucherCmd.Parameters.AddWithValue(cc.ToString());
                voucherCmd.Parameters.AddWithValue(voucherPayload.ToJsonString());
                await voucherCmd.ExecuteNonQueryAsync();

                // 6. 创建资产取得交易记录
                var txPayload = new JsonObject
                {
                    ["transactionType"] = "ACQUISITION",
                    ["postingDate"] = acquisitionDate,
                    ["amount"] = acquisitionCost,
                    ["voucherId"] = voucherId.ToString(),
                    ["voucherNo"] = voucherNo,
                    ["note"] = "資産取得"
                };

                await using var txCmd = conn.CreateCommand();
                txCmd.CommandText = "INSERT INTO asset_transactions(company_code, asset_id, payload) VALUES ($1, $2, $3::jsonb)";
                txCmd.Parameters.AddWithValue(cc.ToString());
                txCmd.Parameters.AddWithValue(assetId);
                txCmd.Parameters.AddWithValue(txPayload.ToJsonString());
                await txCmd.ExecuteNonQueryAsync();

                await tx.CommitAsync();
                
                // 刷新总账物化视图
                await FinanceService.RefreshGlViewAsync(conn);

                return Results.Ok(new
                {
                    ok = true,
                    assetId,
                    assetNo,
                    voucherId,
                    voucherNo,
                    depreciationStartDate = depreciationStartDate.ToString("yyyy-MM-dd")
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return Results.Problem(ex.Message);
            }
        }).RequireAuthorization();

        // 已有资产的资本化（只创建凭证和交易记录）
        app.MapPost("/fixed-assets/assets/{assetId:guid}/capitalize", async (Guid assetId, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var acquisitionDate = root.TryGetProperty("acquisitionDate", out var ad) ? ad.GetString() : null;
            var acquisitionCost = root.TryGetProperty("acquisitionCost", out var acv) ? acv.GetDecimal() : 0;

            if (string.IsNullOrWhiteSpace(acquisitionDate))
                return Results.BadRequest(new { error = "取得日を選択してください" });
            if (acquisitionCost <= 0)
                return Results.BadRequest(new { error = "取得価額を入力してください" });

            // 解析凭证行
            var voucherLines = new JsonArray();
            if (root.TryGetProperty("voucherLines", out var lines))
            {
                foreach (var line in lines.EnumerateArray())
                {
                    voucherLines.Add(JsonNode.Parse(line.GetRawText()));
                }
            }

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 1. 获取资产信息（包含资产类别）
                await using var getAssetCmd = conn.CreateCommand();
                getAssetCmd.CommandText = "SELECT asset_no, asset_name, asset_class_id FROM fixed_assets WHERE id = $1 AND company_code = $2";
                getAssetCmd.Parameters.AddWithValue(assetId);
                getAssetCmd.Parameters.AddWithValue(cc.ToString());
                await using var reader = await getAssetCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    await tx.RollbackAsync();
                    return Results.NotFound(new { error = "資産が見つかりません" });
                }
                var assetNo = reader.GetString(0);
                var assetName = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var assetClassId = reader.IsDBNull(2) ? "" : reader.GetString(2);
                await reader.CloseAsync();

                // 1.1 查询资产类别判断有形/无形
                bool isTangible = true;
                if (!string.IsNullOrEmpty(assetClassId) && Guid.TryParse(assetClassId, out var classGuid))
                {
                    await using var acCmd = conn.CreateCommand();
                    acCmd.CommandText = "SELECT payload FROM asset_classes WHERE id = $1::uuid AND company_code = $2";
                    acCmd.Parameters.AddWithValue(classGuid);
                    acCmd.Parameters.AddWithValue(cc.ToString());
                    var acPayloadStr = await acCmd.ExecuteScalarAsync() as string;
                    if (!string.IsNullOrEmpty(acPayloadStr))
                    {
                        using var acDoc = JsonDocument.Parse(acPayloadStr);
                        if (acDoc.RootElement.TryGetProperty("isTangible", out var tangibleProp))
                        {
                            isTangible = tangibleProp.GetBoolean();
                        }
                    }
                }

                // 1.2 计算折旧开始日（有形资产：翌月1日，无形资产：当月1日）
                var acqDate = DateOnly.Parse(acquisitionDate!);
                DateOnly depreciationStartDate;
                if (isTangible)
                {
                    depreciationStartDate = new DateOnly(acqDate.Year, acqDate.Month, 1).AddMonths(1);
                }
                else
                {
                    depreciationStartDate = new DateOnly(acqDate.Year, acqDate.Month, 1);
                }

                // 2. 检查是否已有取得交易
                await using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT COUNT(*) FROM asset_transactions WHERE asset_id = $1 AND company_code = $2 AND transaction_type = 'ACQUISITION'";
                checkCmd.Parameters.AddWithValue(assetId);
                checkCmd.Parameters.AddWithValue(cc.ToString());
                var existingCount = (long)(await checkCmd.ExecuteScalarAsync() ?? 0);
                if (existingCount > 0)
                {
                    await tx.RollbackAsync();
                    return Results.BadRequest(new { error = "この資産は既に取得済みです" });
                }

                // 3. 生成凭证编号
                var dateParts = acquisitionDate!.Split('-');
                var year = int.Parse(dateParts[0]);
                var month = int.Parse(dateParts[1]);
                var voucherNo = await GenerateVoucherNoAsync(conn, cc!, year, month);

                // 4. 创建会计凭证
                var voucherId = Guid.NewGuid();
                var voucherPayloadLines = new JsonArray();
                int lineNo = 1;
                foreach (var line in voucherLines)
                {
                    if (line is JsonObject lineObj)
                    {
                        var newLine = new JsonObject
                        {
                            ["lineNo"] = lineNo++,
                            ["accountCode"] = lineObj["accountCode"]?.DeepClone(),
                            ["drcr"] = lineObj["drcr"]?.DeepClone(),
                            ["amount"] = lineObj["amount"]?.DeepClone(),
                            ["taxRate"] = lineObj["taxRate"]?.DeepClone() ?? 0,
                            ["note"] = $"資産取得「{assetNo} {assetName}」"
                        };
                        if (lineObj["drcr"]?.GetValue<string>() == "DR")
                        {
                            newLine["assetId"] = assetId.ToString();
                        }
                        if (lineObj["vendorId"] != null && !string.IsNullOrEmpty(lineObj["vendorId"]?.GetValue<string>()))
                        {
                            newLine["vendorId"] = lineObj["vendorId"]?.DeepClone();
                        }
                        voucherPayloadLines.Add(newLine);
                    }
                }

                var voucherPayload = new JsonObject
                {
                    ["header"] = new JsonObject
                    {
                        ["companyCode"] = cc.ToString(),
                        ["postingDate"] = acquisitionDate,
                        ["voucherType"] = "AA",
                        ["voucherNo"] = voucherNo,
                        ["currency"] = "JPY",
                        ["summary"] = $"資産取得「{assetNo} {assetName}」"
                    },
                    ["lines"] = voucherPayloadLines
                };

                await using var voucherCmd = conn.CreateCommand();
                voucherCmd.CommandText = "INSERT INTO vouchers(id, company_code, payload) VALUES ($1, $2, $3::jsonb)";
                voucherCmd.Parameters.AddWithValue(voucherId);
                voucherCmd.Parameters.AddWithValue(cc.ToString());
                voucherCmd.Parameters.AddWithValue(voucherPayload.ToJsonString());
                await voucherCmd.ExecuteNonQueryAsync();

                // 5. 创建资产取得交易记录
                var txPayload = new JsonObject
                {
                    ["transactionType"] = "ACQUISITION",
                    ["postingDate"] = acquisitionDate,
                    ["amount"] = acquisitionCost,
                    ["voucherId"] = voucherId.ToString(),
                    ["voucherNo"] = voucherNo,
                    ["note"] = "資産取得"
                };

                await using var txCmd = conn.CreateCommand();
                txCmd.CommandText = "INSERT INTO asset_transactions(company_code, asset_id, payload) VALUES ($1, $2, $3::jsonb)";
                txCmd.Parameters.AddWithValue(cc.ToString());
                txCmd.Parameters.AddWithValue(assetId);
                txCmd.Parameters.AddWithValue(txPayload.ToJsonString());
                await txCmd.ExecuteNonQueryAsync();

                // 6. 更新资产主数据的取得价额和帐簿价额
                await using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = @"UPDATE fixed_assets 
                                          SET payload = payload || $1::jsonb,
                                              updated_at = now()
                                          WHERE id = $2 AND company_code = $3";
                var updatePayload = new JsonObject
                {
                    ["acquisitionCost"] = acquisitionCost,
                    ["bookValue"] = acquisitionCost,
                    ["capitalizationDate"] = acquisitionDate,
                    ["depreciationStartDate"] = depreciationStartDate.ToString("yyyy-MM-dd")
                };
                updateCmd.Parameters.AddWithValue(updatePayload.ToJsonString());
                updateCmd.Parameters.AddWithValue(assetId);
                updateCmd.Parameters.AddWithValue(cc.ToString());
                await updateCmd.ExecuteNonQueryAsync();

                await tx.CommitAsync();
                
                // 刷新总账物化视图
                await FinanceService.RefreshGlViewAsync(conn);

                return Results.Ok(new
                {
                    ok = true,
                    assetId,
                    assetNo,
                    voucherId,
                    voucherNo,
                    depreciationStartDate = depreciationStartDate.ToString("yyyy-MM-dd")
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return Results.Problem(ex.Message);
            }
        }).RequireAuthorization();

        // ========================================
        // 资产除却/处置（创建凭证 + 记录交易 + 将帐簿価額清零）
        // ========================================
        app.MapPost("/fixed-assets/assets/{assetId:guid}/dispose", async (Guid assetId, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var disposalDate = root.TryGetProperty("disposalDate", out var dd) ? dd.GetString() : null;
            var note = root.TryGetProperty("note", out var nt) ? nt.GetString() : null;

            if (string.IsNullOrWhiteSpace(disposalDate))
                return Results.BadRequest(new { error = "除却日を選択してください" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 1) 读取资产信息
                string assetNo, assetName, assetClassId;
                decimal acquisitionCost, bookValue;
                await using (var getAssetCmd = conn.CreateCommand())
                {
                    getAssetCmd.CommandText = @"SELECT asset_no, asset_name, asset_class_id, acquisition_cost, book_value
                                                FROM fixed_assets
                                                WHERE id = $1 AND company_code = $2";
                    getAssetCmd.Parameters.AddWithValue(assetId);
                    getAssetCmd.Parameters.AddWithValue(cc.ToString());
                    await using var reader = await getAssetCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        await tx.RollbackAsync();
                        return Results.NotFound(new { error = "資産が見つかりません" });
                    }
                    assetNo = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    assetName = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    assetClassId = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    acquisitionCost = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3);
                    bookValue = reader.IsDBNull(4) ? 0m : reader.GetDecimal(4);
                    await reader.CloseAsync();
                }

                if (bookValue <= 0)
                {
                    await tx.RollbackAsync();
                    return Results.BadRequest(new { error = "帳簿価額が 0 の資産は除却できません" });
                }

                // 2) 重复除却检查
                await using (var checkCmd = conn.CreateCommand())
                {
                    checkCmd.CommandText = @"SELECT COUNT(*) FROM asset_transactions
                                             WHERE company_code = $1 AND asset_id = $2 AND transaction_type = 'DISPOSAL'";
                    checkCmd.Parameters.AddWithValue(cc.ToString());
                    checkCmd.Parameters.AddWithValue(assetId);
                    var existing = (long)(await checkCmd.ExecuteScalarAsync() ?? 0);
                    if (existing > 0)
                    {
                        await tx.RollbackAsync();
                        return Results.BadRequest(new { error = "この資産は既に除却済みです" });
                    }
                }

                // 3) 读取资产クラス的科目映射（方案3：完全自动决定会计科目）
                string? acquisitionAccount = null;
                string? accumulatedDepAccount = null;
                string? disposalAccount = null;
                if (!string.IsNullOrWhiteSpace(assetClassId) && Guid.TryParse(assetClassId, out var classGuid))
                {
                    await using var acCmd = conn.CreateCommand();
                    acCmd.CommandText = @"SELECT acquisition_account, accumulated_depreciation_account, disposal_account
                                          FROM asset_classes
                                          WHERE id = $1::uuid AND company_code = $2";
                    acCmd.Parameters.AddWithValue(classGuid);
                    acCmd.Parameters.AddWithValue(cc.ToString());
                    await using var r = await acCmd.ExecuteReaderAsync();
                    if (await r.ReadAsync())
                    {
                        acquisitionAccount = r.IsDBNull(0) ? null : r.GetString(0);
                        accumulatedDepAccount = r.IsDBNull(1) ? null : r.GetString(1);
                        disposalAccount = r.IsDBNull(2) ? null : r.GetString(2);
                    }
                    await r.CloseAsync();
                }

                if (string.IsNullOrWhiteSpace(acquisitionAccount))
                {
                    await tx.RollbackAsync();
                    return Results.BadRequest(new { error = "資産クラスの取得勘定（acquisitionAccount）が設定されていません" });
                }
                if (string.IsNullOrWhiteSpace(accumulatedDepAccount))
                {
                    await tx.RollbackAsync();
                    return Results.BadRequest(new { error = "資産クラスの累計償却勘定（accumulatedDepreciationAccount）が設定されていません" });
                }
                if (string.IsNullOrWhiteSpace(disposalAccount))
                {
                    await tx.RollbackAsync();
                    return Results.BadRequest(new { error = "資産クラスの除却勘定（disposalAccount）が設定されていません" });
                }

                // 4) 生成凭证编号
                var dateParts = disposalDate!.Split('-');
                var year = int.Parse(dateParts[0]);
                var month = int.Parse(dateParts[1]);
                var voucherNo = await GenerateVoucherNoAsync(conn, cc!, year, month);

                // 5) 创建会计凭证
                var voucherId = Guid.NewGuid();
                var voucherPayloadLines = new JsonArray();
                var summary = string.IsNullOrWhiteSpace(note) ? $"資産除却「{assetNo} {assetName}」" : note!.Trim();

                // 方案3：自动生成除却分录（用户不可指定科目/行）
                // 借：累計償却（取得価額 - 帳簿価額）
                // 借：除却損（帳簿価額）
                // 贷：固定資産（取得価額）(assetId 关联在这条行上)
                var accumulatedDepAmount = acquisitionCost - bookValue;
                if (accumulatedDepAmount < 0) accumulatedDepAmount = 0;

                int lineNo = 1;
                if (accumulatedDepAmount > 0)
                {
                    voucherPayloadLines.Add(new JsonObject
                    {
                        ["lineNo"] = lineNo++,
                        ["accountCode"] = accumulatedDepAccount,
                        ["drcr"] = "DR",
                        ["amount"] = Math.Round(accumulatedDepAmount, 0),
                        ["taxRate"] = 0,
                        ["note"] = summary
                    });
                }

                voucherPayloadLines.Add(new JsonObject
                {
                    ["lineNo"] = lineNo++,
                    ["accountCode"] = disposalAccount,
                    ["drcr"] = "DR",
                    ["amount"] = Math.Round(bookValue, 0),
                    ["taxRate"] = 0,
                    ["note"] = summary
                });

                voucherPayloadLines.Add(new JsonObject
                {
                    ["lineNo"] = lineNo++,
                    ["accountCode"] = acquisitionAccount,
                    ["drcr"] = "CR",
                    ["amount"] = Math.Round(acquisitionCost, 0),
                    ["taxRate"] = 0,
                    ["assetId"] = assetId.ToString(),
                    ["note"] = summary
                });

                var voucherPayload = new JsonObject
                {
                    ["header"] = new JsonObject
                    {
                        ["companyCode"] = cc.ToString(),
                        ["postingDate"] = disposalDate,
                        ["voucherType"] = "AA",
                        ["voucherNo"] = voucherNo,
                        ["currency"] = "JPY",
                        ["summary"] = summary
                    },
                    ["lines"] = voucherPayloadLines
                };

                await using (var voucherCmd = conn.CreateCommand())
                {
                    voucherCmd.CommandText = "INSERT INTO vouchers(id, company_code, payload) VALUES ($1, $2, $3::jsonb)";
                    voucherCmd.Parameters.AddWithValue(voucherId);
                    voucherCmd.Parameters.AddWithValue(cc.ToString());
                    voucherCmd.Parameters.AddWithValue(voucherPayload.ToJsonString());
                    await voucherCmd.ExecuteNonQueryAsync();
                }

                // 6) 创建资产除却交易记录（amount 记录除却时的帳簿価額）
                var txPayload = new JsonObject
                {
                    ["transactionType"] = "DISPOSAL",
                    ["postingDate"] = disposalDate,
                    ["amount"] = bookValue,
                    ["voucherId"] = voucherId.ToString(),
                    ["voucherNo"] = voucherNo,
                    ["note"] = summary
                };
                await using (var txCmd = conn.CreateCommand())
                {
                    txCmd.CommandText = "INSERT INTO asset_transactions(company_code, asset_id, payload) VALUES ($1, $2, $3::jsonb)";
                    txCmd.Parameters.AddWithValue(cc.ToString());
                    txCmd.Parameters.AddWithValue(assetId);
                    txCmd.Parameters.AddWithValue(txPayload.ToJsonString());
                    await txCmd.ExecuteNonQueryAsync();
                }

                // 7) 更新资产：帐簿価額清零 + 记录处置日
                await using (var updateCmd = conn.CreateCommand())
                {
                    updateCmd.CommandText = @"UPDATE fixed_assets
                                              SET payload = payload || $1::jsonb,
                                                  updated_at = now()
                                              WHERE id = $2 AND company_code = $3";
                    var updatePayload = new JsonObject
                    {
                        ["bookValue"] = 0,
                        ["disposalDate"] = disposalDate,
                        ["disposed"] = true
                    };
                    updateCmd.Parameters.AddWithValue(updatePayload.ToJsonString());
                    updateCmd.Parameters.AddWithValue(assetId);
                    updateCmd.Parameters.AddWithValue(cc.ToString());
                    await updateCmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                
                // 刷新总账物化视图
                await FinanceService.RefreshGlViewAsync(conn);
                
                return Results.Ok(new { ok = true, voucherId, voucherNo, assetId, assetNo, acquisitionCost, bookValueBefore = bookValue });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // ========================================
        // 资产交易管理 (Asset Transactions)
        // ========================================

        // 创建资产交易（取得）
        app.MapPost("/fixed-assets/assets/{assetId:guid}/transactions", async (Guid assetId, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var payloadNode = JsonNode.Parse(ExtractPayload(doc.RootElement));

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO asset_transactions(company_code, asset_id, payload) 
                                VALUES ($1, $2, $3::jsonb) 
                                RETURNING to_jsonb(asset_transactions)";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(assetId);
            cmd.Parameters.AddWithValue(payloadNode?.ToJsonString() ?? "{}");

            var result = await cmd.ExecuteScalarAsync() as string;
            return result is null ? Results.Problem("Insert failed") : Results.Text(result, "application/json");
        }).RequireAuthorization();

        // 删除资产交易
        app.MapDelete("/fixed-assets/transactions/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();

            // 获取交易信息
            await using var getCmd = conn.CreateCommand();
            getCmd.CommandText = "SELECT asset_id, transaction_type, amount FROM asset_transactions WHERE id = $1 AND company_code = $2";
            getCmd.Parameters.AddWithValue(id);
            getCmd.Parameters.AddWithValue(cc.ToString());
            await using var reader = await getCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return Results.NotFound();

            var assetId = reader.GetGuid(0);
            var txType = reader.GetString(1);
            var amount = reader.GetDecimal(2);
            await reader.CloseAsync();

            // 删除交易
            await using var delCmd = conn.CreateCommand();
            delCmd.CommandText = "DELETE FROM asset_transactions WHERE id = $1 AND company_code = $2";
            delCmd.Parameters.AddWithValue(id);
            delCmd.Parameters.AddWithValue(cc.ToString());
            var deleted = await delCmd.ExecuteNonQueryAsync();

            // 如果是折旧交易，恢复帐簿价额
            if (txType == "DEPRECIATION" && deleted > 0)
            {
                await using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = @"UPDATE fixed_assets 
                                          SET payload = jsonb_set(payload, '{bookValue}', to_jsonb((payload->>'bookValue')::numeric + $1)),
                                              updated_at = now()
                                          WHERE id = $2 AND company_code = $3";
                updateCmd.Parameters.AddWithValue(amount);
                updateCmd.Parameters.AddWithValue(assetId);
                updateCmd.Parameters.AddWithValue(cc.ToString());
                await updateCmd.ExecuteNonQueryAsync();
            }

            return deleted > 0 ? Results.Ok(new { ok = true }) : Results.NotFound();
        }).RequireAuthorization();

        // ========================================
        // 折旧计划与执行 (Depreciation)
        // ========================================

        // 获取折旧计划（按年）
        app.MapGet("/fixed-assets/depreciation-schedule", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var yearStr = req.Query["year"].FirstOrDefault();
            if (!int.TryParse(yearStr, out var year)) year = DateTime.Now.Year;

            var result = new List<object>();
            for (int month = 1; month <= 12; month++)
            {
                var yearMonth = $"{year:D4}-{month:D2}";
                var lastDayOfMonth = new DateTime(year, month, DateTime.DaysInMonth(year, month));

                // 计算该月需要折旧的资产数
                var countSql = @"SELECT COUNT(*) FROM fixed_assets 
                                 WHERE company_code = $1 
                                   AND depreciation_start_date <= $2
                                   AND book_value > 0";
                await using var conn = await ds.OpenConnectionAsync();
                await using var countCmd = conn.CreateCommand();
                countCmd.CommandText = countSql;
                countCmd.Parameters.AddWithValue(cc.ToString());
                countCmd.Parameters.AddWithValue(lastDayOfMonth);
                var assetCount = (long)(await countCmd.ExecuteScalarAsync() ?? 0);

                // 检查该月是否已执行折旧
                await using var runCmd = conn.CreateCommand();
                runCmd.CommandText = @"SELECT id, voucher_id, voucher_no, executed_at, executed_by, asset_count 
                                       FROM depreciation_runs 
                                       WHERE company_code = $1 AND year_month = $2";
                runCmd.Parameters.AddWithValue(cc.ToString());
                runCmd.Parameters.AddWithValue(yearMonth);
                await using var runReader = await runCmd.ExecuteReaderAsync();

                object? runInfo = null;
                if (await runReader.ReadAsync())
                {
                    runInfo = new
                    {
                        id = runReader.GetGuid(0),
                        voucherId = runReader.IsDBNull(1) ? null : runReader.GetGuid(1).ToString(),
                        voucherNo = runReader.IsDBNull(2) ? null : runReader.GetString(2),
                        executedAt = runReader.GetDateTime(3).ToString("yyyy-MM-dd"),
                        executedBy = runReader.IsDBNull(4) ? null : runReader.GetString(4),
                        assetCount = runReader.GetInt32(5)
                    };
                }
                await runReader.CloseAsync();

                result.Add(new
                {
                    yearMonth,
                    year,
                    month,
                    pendingAssetCount = assetCount,
                    run = runInfo
                });
            }

            return Results.Json(result);
        }).RequireAuthorization();

        // 执行折旧记账
        app.MapPost("/fixed-assets/depreciation-run", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var yearMonth = root.TryGetProperty("yearMonth", out var ym) ? ym.GetString() : null;
            if (string.IsNullOrWhiteSpace(yearMonth) || !yearMonth.Contains("-"))
                return Results.BadRequest(new { error = "yearMonth required (YYYY-MM)" });

            var executedBy = root.TryGetProperty("executedBy", out var eb) ? eb.GetString() : "system";

            // 解析年月
            var parts = yearMonth.Split('-');
            if (!int.TryParse(parts[0], out var year) || !int.TryParse(parts[1], out var month))
                return Results.BadRequest(new { error = "Invalid yearMonth format" });

            var lastDayOfMonth = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            var postingDate = lastDayOfMonth.ToString("yyyy-MM-dd");

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 检查是否已执行
                await using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT id FROM depreciation_runs WHERE company_code = $1 AND year_month = $2";
                checkCmd.Parameters.AddWithValue(cc.ToString());
                checkCmd.Parameters.AddWithValue(yearMonth);
                var existingRun = await checkCmd.ExecuteScalarAsync();
                if (existingRun != null)
                {
                    await tx.RollbackAsync();
                    return Results.BadRequest(new { error = "この期間の償却は既に実行されています" });
                }

                // 获取需要折旧的资产及其类别信息
                await using var assetsCmd = conn.CreateCommand();
                assetsCmd.CommandText = @"
                    SELECT fa.id, fa.asset_no, fa.asset_name, fa.depreciation_method, fa.useful_life, 
                           fa.acquisition_cost, fa.book_value, fa.depreciation_start_date,
                           ac.depreciation_expense_account, ac.accumulated_depreciation_account, ac.include_tax_in_depreciation
                    FROM fixed_assets fa
                    JOIN asset_classes ac ON ac.id::text = fa.asset_class_id AND ac.company_code = fa.company_code
                    WHERE fa.company_code = $1 
                      AND fa.depreciation_start_date <= $2
                      AND fa.book_value > 0";
                assetsCmd.Parameters.AddWithValue(cc.ToString());
                assetsCmd.Parameters.AddWithValue(lastDayOfMonth);

                var depreciationItems = new List<DepreciationItem>();
                await using var assetsReader = await assetsCmd.ExecuteReaderAsync();
                while (await assetsReader.ReadAsync())
                {
                    var item = new DepreciationItem
                    {
                        AssetId = assetsReader.GetGuid(0),
                        AssetNo = assetsReader.GetString(1),
                        AssetName = assetsReader.IsDBNull(2) ? "" : assetsReader.GetString(2),
                        DepreciationMethod = assetsReader.IsDBNull(3) ? "STRAIGHT_LINE" : assetsReader.GetString(3),
                        UsefulLife = assetsReader.IsDBNull(4) ? 5 : assetsReader.GetInt32(4),
                        AcquisitionCost = assetsReader.GetDecimal(5),
                        BookValue = assetsReader.GetDecimal(6),
                        DepreciationStartDate = assetsReader.GetDateTime(7),
                        DepreciationExpenseAccount = assetsReader.IsDBNull(8) ? "" : assetsReader.GetString(8),
                        AccumulatedDepreciationAccount = assetsReader.IsDBNull(9) ? "" : assetsReader.GetString(9),
                        IncludeTaxInDepreciation = !assetsReader.IsDBNull(10) && assetsReader.GetBoolean(10)
                    };

                    // 计算折旧金额（传递当前折旧年月和决算月）
                    var fiscalYearEndMonth = await GetFiscalYearEndMonthAsync(ds, cc.ToString()!);
                    item.DepreciationAmount = CalculateMonthlyDepreciation(item, year, month, fiscalYearEndMonth);
                    if (item.DepreciationAmount > 0)
                    {
                        depreciationItems.Add(item);
                    }
                }
                await assetsReader.CloseAsync();

                if (depreciationItems.Count == 0)
                {
                    await tx.RollbackAsync();
                    return Results.BadRequest(new { error = "償却対象の資産がありません" });
                }

                // 生成凭证编号
                var voucherNo = await GenerateVoucherNoAsync(conn, cc!, year, month);

                // 创建凭证
                var voucherId = Guid.NewGuid();
                var voucherLines = new JsonArray();
                int lineNo = 1;

                foreach (var item in depreciationItems)
                {
                    var note = $"資産「{item.AssetNo} {item.AssetName}」 {year}年{month}月 償却記帳";
                    var depAmount = Math.Round(item.DepreciationAmount, 0);

                    // 借方：折旧费
                    voucherLines.Add(new JsonObject
                    {
                        ["lineNo"] = lineNo++,
                        ["accountCode"] = item.DepreciationExpenseAccount,
                        ["drcr"] = "DR",
                        ["amount"] = depAmount,
                        ["taxRate"] = 0,
                        ["note"] = note
                    });

                    // 贷方：累计折旧
                    voucherLines.Add(new JsonObject
                    {
                        ["lineNo"] = lineNo++,
                        ["accountCode"] = item.AccumulatedDepreciationAccount,
                        ["drcr"] = "CR",
                        ["amount"] = depAmount,
                        ["taxRate"] = 0,
                        ["note"] = note
                    });

                    // 如果含消费税，添加消费税行
                    if (item.IncludeTaxInDepreciation)
                    {
                        var taxAmount = Math.Round(depAmount * 0.1m, 0);
                        voucherLines.Add(new JsonObject
                        {
                            ["lineNo"] = lineNo++,
                            ["accountCode"] = "189", // 仮払消費税
                            ["drcr"] = "DR",
                            ["amount"] = taxAmount,
                            ["taxRate"] = 0,
                            ["note"] = "仮払消費税"
                        });
                    }
                }

                var voucherPayload = new JsonObject
                {
                    ["header"] = new JsonObject
                    {
                        ["companyCode"] = cc.ToString(),
                        ["postingDate"] = postingDate,
                        ["voucherType"] = "AA",
                        ["voucherNo"] = voucherNo,
                        ["currency"] = "JPY",
                        ["summary"] = $"{year}年{month}月 固定資産償却"
                    },
                    ["lines"] = voucherLines
                };

                // 插入凭证
                await using var voucherCmd = conn.CreateCommand();
                voucherCmd.CommandText = "INSERT INTO vouchers(id, company_code, payload) VALUES ($1, $2, $3::jsonb)";
                voucherCmd.Parameters.AddWithValue(voucherId);
                voucherCmd.Parameters.AddWithValue(cc.ToString());
                voucherCmd.Parameters.AddWithValue(voucherPayload.ToJsonString());
                await voucherCmd.ExecuteNonQueryAsync();

                // 为每个资产创建折旧交易记录，并更新帐簿价额
                foreach (var item in depreciationItems)
                {
                    var depAmount = Math.Round(item.DepreciationAmount, 0);

                    // 创建交易记录
                    var txPayload = new JsonObject
                    {
                        ["transactionType"] = "DEPRECIATION",
                        ["postingDate"] = postingDate,
                        ["amount"] = depAmount,
                        ["voucherId"] = voucherId.ToString(),
                        ["voucherNo"] = voucherNo,
                        ["note"] = $"{year}年{month}月 償却"
                    };

                    await using var txCmd = conn.CreateCommand();
                    txCmd.CommandText = "INSERT INTO asset_transactions(company_code, asset_id, payload) VALUES ($1, $2, $3::jsonb)";
                    txCmd.Parameters.AddWithValue(cc.ToString());
                    txCmd.Parameters.AddWithValue(item.AssetId);
                    txCmd.Parameters.AddWithValue(txPayload.ToJsonString());
                    await txCmd.ExecuteNonQueryAsync();

                    // 更新帐簿价额
                    var newBookValue = item.BookValue - depAmount;
                    if (newBookValue < 0) newBookValue = 0;

                    await using var updateCmd = conn.CreateCommand();
                    updateCmd.CommandText = @"UPDATE fixed_assets 
                                              SET payload = jsonb_set(payload, '{bookValue}', to_jsonb($1)),
                                                  updated_at = now()
                                              WHERE id = $2 AND company_code = $3";
                    updateCmd.Parameters.AddWithValue(newBookValue);
                    updateCmd.Parameters.AddWithValue(item.AssetId);
                    updateCmd.Parameters.AddWithValue(cc.ToString());
                    await updateCmd.ExecuteNonQueryAsync();
                }

                // 创建折旧执行记录
                await using var runInsertCmd = conn.CreateCommand();
                runInsertCmd.CommandText = @"INSERT INTO depreciation_runs(company_code, year_month, asset_count, voucher_id, voucher_no, executed_by)
                                             VALUES ($1, $2, $3, $4, $5, $6)
                                             RETURNING id";
                runInsertCmd.Parameters.AddWithValue(cc.ToString());
                runInsertCmd.Parameters.AddWithValue(yearMonth);
                runInsertCmd.Parameters.AddWithValue(depreciationItems.Count);
                runInsertCmd.Parameters.AddWithValue(voucherId);
                runInsertCmd.Parameters.AddWithValue(voucherNo);
                runInsertCmd.Parameters.AddWithValue(executedBy ?? "system");
                var runId = (Guid)(await runInsertCmd.ExecuteScalarAsync())!;

                await tx.CommitAsync();
                
                // 刷新总账物化视图
                await FinanceService.RefreshGlViewAsync(conn);

                return Results.Ok(new
                {
                    ok = true,
                    runId,
                    voucherId,
                    voucherNo,
                    assetCount = depreciationItems.Count,
                    yearMonth
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return Results.Problem(ex.Message);
            }
        }).RequireAuthorization();

        // 删除折旧执行记录（撤销折旧）
        app.MapDelete("/fixed-assets/depreciation-run/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 获取执行记录信息
                await using var getCmd = conn.CreateCommand();
                getCmd.CommandText = "SELECT voucher_id, voucher_no FROM depreciation_runs WHERE id = $1 AND company_code = $2";
                getCmd.Parameters.AddWithValue(id);
                getCmd.Parameters.AddWithValue(cc.ToString());
                await using var reader = await getCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    await tx.RollbackAsync();
                    return Results.NotFound();
                }
                var voucherId = reader.IsDBNull(0) ? (Guid?)null : reader.GetGuid(0);
                var voucherNo = reader.IsDBNull(1) ? null : reader.GetString(1);
                await reader.CloseAsync();

                // 获取关联的折旧交易，恢复帐簿价额
                if (voucherId.HasValue)
                {
                    await using var txCmd = conn.CreateCommand();
                    txCmd.CommandText = @"SELECT asset_id, amount FROM asset_transactions 
                                          WHERE company_code = $1 AND voucher_id = $2";
                    txCmd.Parameters.AddWithValue(cc.ToString());
                    txCmd.Parameters.AddWithValue(voucherId.Value.ToString());
                    await using var txReader = await txCmd.ExecuteReaderAsync();

                    var updates = new List<(Guid assetId, decimal amount)>();
                    while (await txReader.ReadAsync())
                    {
                        updates.Add((txReader.GetGuid(0), txReader.GetDecimal(1)));
                    }
                    await txReader.CloseAsync();

                    // 恢复帐簿价额
                    foreach (var (assetId, amount) in updates)
                    {
                        await using var updateCmd = conn.CreateCommand();
                        updateCmd.CommandText = @"UPDATE fixed_assets 
                                                  SET payload = jsonb_set(payload, '{bookValue}', to_jsonb((payload->>'bookValue')::numeric + $1)),
                                                      updated_at = now()
                                                  WHERE id = $2 AND company_code = $3";
                        updateCmd.Parameters.AddWithValue(amount);
                        updateCmd.Parameters.AddWithValue(assetId);
                        updateCmd.Parameters.AddWithValue(cc.ToString());
                        await updateCmd.ExecuteNonQueryAsync();
                    }

                    // 删除折旧交易记录
                    await using var delTxCmd = conn.CreateCommand();
                    delTxCmd.Transaction = tx;
                    delTxCmd.CommandText = "DELETE FROM asset_transactions WHERE company_code = $1 AND voucher_id = $2";
                    delTxCmd.Parameters.AddWithValue(cc.ToString());
                    delTxCmd.Parameters.AddWithValue(voucherId.Value.ToString());
                    await delTxCmd.ExecuteNonQueryAsync();

                    // 删除关联的 open_items
                    await using var delOiCmd = conn.CreateCommand();
                    delOiCmd.Transaction = tx;
                    delOiCmd.CommandText = "DELETE FROM open_items WHERE company_code = $1 AND voucher_id = $2";
                    delOiCmd.Parameters.AddWithValue(cc.ToString());
                    delOiCmd.Parameters.AddWithValue(voucherId.Value);
                    await delOiCmd.ExecuteNonQueryAsync();

                    // 删除凭证
                    await using var delVoucherCmd = conn.CreateCommand();
                    delVoucherCmd.Transaction = tx;
                    delVoucherCmd.CommandText = "DELETE FROM vouchers WHERE id = $1 AND company_code = $2";
                    delVoucherCmd.Parameters.AddWithValue(voucherId.Value);
                    delVoucherCmd.Parameters.AddWithValue(cc.ToString());
                    await delVoucherCmd.ExecuteNonQueryAsync();
                }

                // 删除执行记录
                await using var delRunCmd = conn.CreateCommand();
                delRunCmd.CommandText = "DELETE FROM depreciation_runs WHERE id = $1 AND company_code = $2";
                delRunCmd.Parameters.AddWithValue(id);
                delRunCmd.Parameters.AddWithValue(cc.ToString());
                var deleted = await delRunCmd.ExecuteNonQueryAsync();

                await tx.CommitAsync();
                
                // 如果删除了凭证，刷新总账物化视图
                if (voucherId.HasValue)
                {
                    await FinanceService.RefreshGlViewAsync(conn);
                }
                
                return deleted > 0 ? Results.Ok(new { ok = true }) : Results.NotFound();
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return Results.Problem(ex.Message);
            }
        }).RequireAuthorization();

        // 查看折旧凭证
        app.MapGet("/fixed-assets/depreciation-run/{id:guid}/voucher", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT to_jsonb(v) FROM 
                                (SELECT * FROM vouchers WHERE id = (
                                    SELECT voucher_id FROM depreciation_runs WHERE id = $1 AND company_code = $2
                                )) v";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());

            var result = await cmd.ExecuteScalarAsync() as string;
            return result is null ? Results.NotFound() : Results.Text(result, "application/json");
        }).RequireAuthorization();
    }

    // ========================================
    // 辅助方法
    // ========================================

    private static string ExtractPayload(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("payload", out var payloadNode) &&
            payloadNode.ValueKind == JsonValueKind.Object)
        {
            return payloadNode.GetRawText();
        }
        return root.GetRawText();
    }

    private static async Task<string> GenerateAssetNoAsync(NpgsqlDataSource ds, string companyCode)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO asset_sequences(company_code, last_number) 
                            VALUES ($1, 1) 
                            ON CONFLICT (company_code) 
                            DO UPDATE SET last_number = asset_sequences.last_number + 1, updated_at = now()
                            RETURNING last_number";
        cmd.Parameters.AddWithValue(companyCode);
        var num = (int)(await cmd.ExecuteScalarAsync())!;
        return num.ToString();
    }

    private static async Task<string> GenerateVoucherNoAsync(NpgsqlConnection conn, string companyCode, int year, int month)
    {
        var yymm = $"{year % 100:D2}{month:D2}";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO voucher_sequences(company_code, yymm, last_number) 
                            VALUES ($1, $2, 1) 
                            ON CONFLICT (company_code, yymm) 
                            DO UPDATE SET last_number = voucher_sequences.last_number + 1, updated_at = now()
                            RETURNING last_number";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(yymm);
        var num = (int)(await cmd.ExecuteScalarAsync())!;
        return $"{yymm}{num:D6}";
    }

    /// <summary>
    /// 计算资产的折旧计划预览（未记账的折旧）
    /// 日本折旧规则（按事业年度计算）：
    /// 【定額法】
    ///   1. 年额 = 取得価額 × 償却率
    ///   2. 取得年度和最终年度：年额 × 使用月数 / 12
    ///   3. 月割：年额 / 12，最后一个月调整端数
    /// 【定率法（200%定率法）】
    ///   1. 調整前償却額 = 期首帳簿価額 × 償却率
    ///   2. 若調整前償却額 >= 償却保証額 → 使用此金额
    ///   3. 若調整前償却額 < 償却保証額 → 改定償却（改定取得価額 × 改定償却率）
    ///   4. 取得年度：年额 × 使用月数 / 12
    /// </summary>
    private static JsonArray CalculateDepreciationSchedule(JsonObject asset, HashSet<string> postedMonths, int fiscalYearEndMonth = 12)
    {
        var result = new JsonArray();

        // 备忘价額（日本规则：保留1円）
        const decimal RESIDUAL_VALUE = 1m;

        // 获取资产信息
        var depreciationStartDateStr = asset["depreciation_start_date"]?.GetValue<string>()
                                     ?? asset["payload"]?["depreciationStartDate"]?.GetValue<string>();
        var usefulLife = asset["useful_life"]?.GetValue<int>()
                       ?? asset["payload"]?["usefulLife"]?.GetValue<int>() ?? 0;
        var acquisitionCost = asset["acquisition_cost"]?.GetValue<decimal>()
                            ?? asset["payload"]?["acquisitionCost"]?.GetValue<decimal>() ?? 0;
        var bookValue = asset["book_value"]?.GetValue<decimal>()
                      ?? asset["payload"]?["bookValue"]?.GetValue<decimal>() ?? 0;
        var depreciationMethod = asset["payload"]?["depreciationMethod"]?.GetValue<string>() ?? "STRAIGHT_LINE";

        // 如果没有折旧开始日或耐用年数，返回空
        if (string.IsNullOrEmpty(depreciationStartDateStr) || usefulLife <= 0 || bookValue <= RESIDUAL_VALUE)
            return result;

        if (!DateTime.TryParse(depreciationStartDateStr, out var depreciationStartDate))
            return result;

        // 当前帐簿价额
        var remainingBookValue = bookValue;

        if (depreciationMethod == "DECLINING_BALANCE")
        {
            // 定率法：简化逻辑 - 只生成未来月份的折旧预定
            var (rate, revisedRate, guaranteeRate) = GetDecliningBalanceParams(usefulLife);
            
            // 償却保証額 = 取得価額 × 保証率
            var guaranteedAmount = Math.Floor(acquisitionCost * guaranteeRate);
            
            // 从当前时间开始，按事业年度循环
            var now = DateTime.Now;
            var startMonth = depreciationStartDate > now ? depreciationStartDate : new DateTime(now.Year, now.Month, 1);
            var currentFiscalYear = GetFiscalYear(startMonth, fiscalYearEndMonth);
            
            // 计算最大折旧期限（通常定率法在耐用年数+2年内折完）
            var maxEndDate = depreciationStartDate.AddYears(usefulLife + 2);
            
            var yearLoopCount = 0;
            while (remainingBookValue > RESIDUAL_VALUE && yearLoopCount <= usefulLife + 2)
            {
                yearLoopCount++;
                
                var (fyStart, fyEnd) = GetFiscalYearRange(currentFiscalYear, fiscalYearEndMonth);
                
                // 计算本年度的折旧额
                decimal annualAmount;
                var adjustedAmount = Math.Floor(remainingBookValue * rate);
                
                // 判断是否需要改定償却
                if (adjustedAmount < guaranteedAmount && guaranteeRate > 0)
                {
                    // 改定償却
                    annualAmount = Math.Floor(remainingBookValue * revisedRate);
                }
                else
                {
                    annualAmount = adjustedAmount;
                }
                
                // 确保不超过剩余可折旧金额
                if (remainingBookValue - annualAmount < RESIDUAL_VALUE)
                {
                    annualAmount = remainingBookValue - RESIDUAL_VALUE;
                }
                
                if (annualAmount <= 0)
                    break;
                
                // 确定本年度内要处理的月份范围
                var monthStart = fyStart > startMonth ? fyStart : startMonth;
                var monthEnd = fyEnd < maxEndDate ? fyEnd : maxEndDate;
                
                // 计算本年度内有效月数
                int totalMonthsInYear = 12;
                int effectiveMonths = 0;
                var tempMonth = monthStart;
                while (tempMonth <= monthEnd && tempMonth <= fyEnd)
                {
                    effectiveMonths++;
                    tempMonth = tempMonth.AddMonths(1);
                }
                
                if (effectiveMonths <= 0)
                {
                    currentFiscalYear++;
                    continue;
                }
                
                var monthlyAmount = Math.Floor(annualAmount / totalMonthsInYear);
                
                // 生成每月折旧记录
                var currentMonth = monthStart;
                var accumulatedInYear = 0m;
                var monthIndex = 0;
                
                while (currentMonth <= monthEnd && currentMonth <= fyEnd && remainingBookValue > RESIDUAL_VALUE)
                {
                    var yearMonth = currentMonth.ToString("yyyy-MM");
                    
                    // 跳过已记账的月份
                    if (postedMonths.Contains(yearMonth))
                    {
                        currentMonth = currentMonth.AddMonths(1);
                        monthIndex++;
                        continue;
                    }
                    
                    decimal thisMonthAmount = monthlyAmount;
                    
                    // 确保不超过剩余金额
                    if (remainingBookValue - thisMonthAmount < RESIDUAL_VALUE)
                    {
                        thisMonthAmount = remainingBookValue - RESIDUAL_VALUE;
                    }
                    
                    if (thisMonthAmount > 0)
                    {
                        var pendingTx = new JsonObject
                        {
                            ["transactionType"] = "DEPRECIATION",
                            ["postingDate"] = $"{yearMonth}-{DateTime.DaysInMonth(currentMonth.Year, currentMonth.Month):D2}",
                            ["yearMonth"] = yearMonth,
                            ["amount"] = thisMonthAmount,
                            ["note"] = $"{currentFiscalYear}年度 {currentMonth.Month}月 償却",
                            ["isPending"] = true
                        };
                        result.Add(pendingTx);
                        
                        remainingBookValue -= thisMonthAmount;
                        accumulatedInYear += thisMonthAmount;
                    }
                    
                    currentMonth = currentMonth.AddMonths(1);
                    monthIndex++;
                }
                
                currentFiscalYear++;
            }
        }
        else
        {
            // 定额法：简化逻辑 - 只生成未来月份的折旧预定
            // 月額 = 取得価額 ÷ 耐用年数 ÷ 12
            var monthlyAmount = Math.Floor(acquisitionCost / usefulLife / 12);
            
            // 计算折旧结束日期（耐用年数 × 12 个月后）
            var depreciationEndDate = depreciationStartDate.AddMonths(usefulLife * 12 - 1);
            
            // 从当前时间的下一个月开始生成预定（或从折旧开始日开始，取较晚者）
            var now = DateTime.Now;
            var startMonth = depreciationStartDate > now ? depreciationStartDate : new DateTime(now.Year, now.Month, 1);
            
            // 遍历从startMonth到折旧结束日期的每个月
            var currentMonth = startMonth;
            while (currentMonth <= depreciationEndDate && remainingBookValue > RESIDUAL_VALUE)
            {
                var yearMonth = currentMonth.ToString("yyyy-MM");
                
                // 跳过已记账的月份
                if (postedMonths.Contains(yearMonth))
                {
                    currentMonth = currentMonth.AddMonths(1);
                    continue;
                }
                
                // 计算本月折旧金额
                decimal thisMonthAmount = monthlyAmount;
                
                // 最后一个月：折完剩余金额（保留1円）
                if (currentMonth.Year == depreciationEndDate.Year && currentMonth.Month == depreciationEndDate.Month)
                {
                    thisMonthAmount = remainingBookValue - RESIDUAL_VALUE;
                }
                // 确保不超过剩余可折旧金额
                else if (remainingBookValue - thisMonthAmount < RESIDUAL_VALUE)
                {
                    thisMonthAmount = remainingBookValue - RESIDUAL_VALUE;
                }
                
                if (thisMonthAmount > 0)
                {
                    var fiscalYear = GetFiscalYear(currentMonth, fiscalYearEndMonth);
                    var pendingTx = new JsonObject
                    {
                        ["transactionType"] = "DEPRECIATION",
                        ["postingDate"] = $"{yearMonth}-{DateTime.DaysInMonth(currentMonth.Year, currentMonth.Month):D2}",
                        ["yearMonth"] = yearMonth,
                        ["amount"] = thisMonthAmount,
                        ["note"] = $"{fiscalYear}年度 {currentMonth.Month}月 償却",
                        ["isPending"] = true
                    };
                    result.Add(pendingTx);
                    
                    remainingBookValue -= thisMonthAmount;
                }
                
                currentMonth = currentMonth.AddMonths(1);
            }
        }

        return result;
    }
    
    /// <summary>
    /// 获取日期所属的事业年度
    /// </summary>
    private static int GetFiscalYear(DateTime date, int fiscalYearEndMonth)
    {
        // 例：12月决算 → 1月~12月为同一年度
        // 例：3月决算 → 4月~次年3月为同一年度
        if (date.Month <= fiscalYearEndMonth)
            return date.Year;
        else
            return date.Year + 1;
    }
    
    /// <summary>
    /// 获取事业年度的起止日期
    /// </summary>
    private static (DateTime Start, DateTime End) GetFiscalYearRange(int fiscalYear, int fiscalYearEndMonth)
    {
        // 例：2025年度（12月决算）= 2025/1/1 ~ 2025/12/31
        // 例：2026年度（3月决算）= 2025/4/1 ~ 2026/3/31
        DateTime end = new DateTime(fiscalYear, fiscalYearEndMonth, DateTime.DaysInMonth(fiscalYear, fiscalYearEndMonth));
        DateTime start = end.AddMonths(-11);
        start = new DateTime(start.Year, start.Month, 1);
        return (start, end);
    }
    
    /// <summary>
    /// 计算从指定日期到事业年度末的月数
    /// </summary>
    private static int CountMonthsInFiscalYear(DateTime fromDate, DateTime fiscalYearEnd)
    {
        var from = new DateTime(fromDate.Year, fromDate.Month, 1);
        var to = new DateTime(fiscalYearEnd.Year, fiscalYearEnd.Month, 1);
        return ((to.Year - from.Year) * 12 + to.Month - from.Month) + 1;
    }
    
    /// <summary>
    /// 获取定額法的折旧率参数
    /// </summary>
    private static (decimal DepRate, decimal RevisedRate, decimal GuaranteeRate) GetStraightLineParams(int usefulLife)
    {
        // 定額法償却率 = 1 / 耐用年数
        var rate = Math.Round(1.0m / usefulLife, 3);
        return (rate, 0, 0);
    }
    
    /// <summary>
    /// 从公司设定中获取决算月
    /// </summary>
    private static async Task<int> GetFiscalYearEndMonthAsync(NpgsqlDataSource ds, string companyCode)
    {
        try
        {
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT payload->>'fiscalYearEndMonth' FROM company_settings WHERE company_code = $1 ORDER BY created_at DESC LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            var result = await cmd.ExecuteScalarAsync();
            if (result != null && result != DBNull.Value && int.TryParse(result.ToString(), out var month) && month >= 1 && month <= 12)
            {
                return month;
            }
        }
        catch
        {
            // 忽略错误，使用默认值
        }
        return 12; // 默认12月决算
    }

    /// <summary>
    /// 计算月折旧额（用于定期折旧执行）
    /// 日本折旧规则（按事业年度计算）：
    /// 【定額法】
    ///   - 年額 = 取得価額 ÷ 耐用年数
    ///   - 取得年度/最終年度は月割計算
    /// 【定率法（200%定率法）】
    ///   - 使用官方折旧率表
    ///   - 当年折旧 &lt; 償却保証額 时切换到改定償却法
    /// </summary>
    private static decimal CalculateMonthlyDepreciation(DepreciationItem item, int depYear, int depMonth, int fiscalYearEndMonth = 12)
    {
        // 备忘价額
        const decimal RESIDUAL_VALUE = 1m;

        if (item.UsefulLife <= 0 || item.BookValue <= RESIDUAL_VALUE) return 0;

        var depDate = new DateTime(depYear, depMonth, 1);
        var startDate = item.DepreciationStartDate;
        
        // 当前折旧月所在的事业年度
        var currentFiscalYear = GetFiscalYear(depDate, fiscalYearEndMonth);
        var (fyStart, fyEnd) = GetFiscalYearRange(currentFiscalYear, fiscalYearEndMonth);
        
        // 计算折旧开始日所在的事业年度（第1年度）
        var firstFiscalYear = GetFiscalYear(startDate, fiscalYearEndMonth);
        
        // 计算当前是第几个年度
        var yearIndex = currentFiscalYear - firstFiscalYear + 1;
        
        // 当前事业年度内的使用月数
        int useMonths;
        if (yearIndex == 1)
        {
            // 取得年度
            useMonths = CountMonthsInFiscalYear(startDate, fyEnd);
        }
        else
        {
            useMonths = 12;
        }

        if (item.DepreciationMethod == "DECLINING_BALANCE")
        {
            // 定率法：使用日本官方折旧率表
            var (rate, revisedRate, guaranteeRate) = GetDecliningBalanceParams(item.UsefulLife);
            
            // 償却保証額 = 取得価額 × 保証率
            var guaranteedAmount = Math.Floor(item.AcquisitionCost * guaranteeRate);
            
            // 通常の定率法：期首帳簿価額 × 償却率
            var annualAmount = Math.Floor(item.BookValue * rate);
            decimal yearDepreciation;
            bool useRevisedMethod = false;
            
            // 判断是否应该使用改定償却法
            if (annualAmount < guaranteedAmount && guaranteeRate > 0)
            {
                useRevisedMethod = true;
                // 改定償却法：改定取得価額 × 改定償却率
                // 改定取得価額は切替時の期首残高（ここでは現在の帳簿価額を使用）
                yearDepreciation = Math.Floor(item.BookValue * revisedRate);
            }
            else
            {
                yearDepreciation = annualAmount;
            }
            
            // 取得年度の月割計算
            if (yearIndex == 1)
            {
                yearDepreciation = Math.Floor(yearDepreciation * useMonths / 12);
            }
            
            // 月額を計算
            var monthlyAmount = Math.Floor(yearDepreciation / useMonths);
            
            // 年度最後の月の場合、端数調整
            if (depMonth == fiscalYearEndMonth)
            {
                // 計算年度内の累計折旧額（本月之前已折旧的月数 × 月額）
                int monthsDepreciatedInYear;
                if (yearIndex == 1)
                {
                    monthsDepreciatedInYear = CountMonthsInFiscalYear(startDate, new DateTime(depYear, depMonth, 1)) - 1;
                }
                else
                {
                    monthsDepreciatedInYear = depMonth - (fiscalYearEndMonth == 12 ? 1 : fiscalYearEndMonth + 1);
                    if (monthsDepreciatedInYear < 0) monthsDepreciatedInYear += 12;
                }
                var alreadyDepreciated = monthlyAmount * monthsDepreciatedInYear;
                monthlyAmount = yearDepreciation - alreadyDepreciated;
            }
            
            // 最后一期调整：确保帳簿価額正好等于備忘価額
            if (item.BookValue - monthlyAmount <= RESIDUAL_VALUE)
            {
                monthlyAmount = item.BookValue - RESIDUAL_VALUE;
            }
            
            return Math.Max(0, monthlyAmount);
        }
        else
        {
            // 定额法：年額 = 取得価額 ÷ 耐用年数
            var annualAmount = Math.Floor(item.AcquisitionCost / item.UsefulLife);
            
            // 判断是否是最终年度
            bool isFinalYear = yearIndex >= item.UsefulLife;
            
            decimal yearDepreciation;
            if (isFinalYear)
            {
                // 最终年度：将剩余金额全部折完（留1円）
                yearDepreciation = item.BookValue - RESIDUAL_VALUE;
            }
            else if (yearIndex == 1)
            {
                // 取得年度：月割計算
                yearDepreciation = Math.Floor(annualAmount * useMonths / 12);
            }
            else
            {
                yearDepreciation = annualAmount;
            }
            
            // 月額を計算
            var monthlyAmount = Math.Floor(yearDepreciation / useMonths);
            
            // 年度最後の月の場合、端数調整
            if (depMonth == fiscalYearEndMonth)
            {
                int monthsDepreciatedInYear;
                if (yearIndex == 1)
                {
                    monthsDepreciatedInYear = CountMonthsInFiscalYear(startDate, new DateTime(depYear, depMonth, 1)) - 1;
                }
                else
                {
                    monthsDepreciatedInYear = depMonth - (fiscalYearEndMonth == 12 ? 1 : fiscalYearEndMonth + 1);
                    if (monthsDepreciatedInYear < 0) monthsDepreciatedInYear += 12;
                }
                var alreadyDepreciated = monthlyAmount * monthsDepreciatedInYear;
                monthlyAmount = yearDepreciation - alreadyDepreciated;
            }
            
            // 最后一期调整：确保帳簿価額正好等于備忘価額
            if (item.BookValue - monthlyAmount <= RESIDUAL_VALUE)
            {
                monthlyAmount = item.BookValue - RESIDUAL_VALUE;
            }
            
            return Math.Max(0, monthlyAmount);
        }
    }

    /// <summary>
    /// 日本200%定率法の償却率表（平成24年4月1日以降取得分）
    /// 耐用年数 -> (償却率, 改定償却率, 保証率)
    /// </summary>
    private static readonly Dictionary<int, (decimal Rate, decimal RevisedRate, decimal GuaranteeRate)> DecliningBalanceRates = new()
    {
        { 2,  (1.000m, 0.000m, 0.00000m) },
        { 3,  (0.667m, 1.000m, 0.11089m) },
        { 4,  (0.500m, 1.000m, 0.12499m) },
        { 5,  (0.400m, 0.500m, 0.10800m) },
        { 6,  (0.333m, 0.334m, 0.09911m) },
        { 7,  (0.286m, 0.334m, 0.08680m) },
        { 8,  (0.250m, 0.334m, 0.07909m) },
        { 9,  (0.222m, 0.250m, 0.07126m) },
        { 10, (0.200m, 0.250m, 0.06552m) },
        { 11, (0.182m, 0.200m, 0.05992m) },
        { 12, (0.167m, 0.200m, 0.05566m) },
        { 13, (0.154m, 0.167m, 0.05180m) },
        { 14, (0.143m, 0.167m, 0.04854m) },
        { 15, (0.133m, 0.143m, 0.04565m) },
        { 16, (0.125m, 0.143m, 0.04294m) },
        { 17, (0.118m, 0.125m, 0.04038m) },
        { 18, (0.111m, 0.112m, 0.03884m) },
        { 19, (0.105m, 0.112m, 0.03693m) },
        { 20, (0.100m, 0.112m, 0.03486m) },
        { 21, (0.095m, 0.100m, 0.03335m) },
        { 22, (0.091m, 0.100m, 0.03182m) },
        { 23, (0.087m, 0.091m, 0.03052m) },
        { 24, (0.083m, 0.084m, 0.02969m) },
        { 25, (0.080m, 0.084m, 0.02841m) },
        { 26, (0.077m, 0.084m, 0.02716m) },
        { 27, (0.074m, 0.077m, 0.02624m) },
        { 28, (0.071m, 0.072m, 0.02568m) },
        { 29, (0.069m, 0.072m, 0.02463m) },
        { 30, (0.067m, 0.072m, 0.02366m) },
        { 31, (0.065m, 0.067m, 0.02286m) },
        { 32, (0.063m, 0.067m, 0.02216m) },
        { 33, (0.061m, 0.063m, 0.02161m) },
        { 34, (0.059m, 0.063m, 0.02097m) },
        { 35, (0.057m, 0.059m, 0.02051m) },
        { 36, (0.056m, 0.059m, 0.01974m) },
        { 37, (0.054m, 0.056m, 0.01950m) },
        { 38, (0.053m, 0.056m, 0.01882m) },
        { 39, (0.051m, 0.053m, 0.01860m) },
        { 40, (0.050m, 0.053m, 0.01791m) },
        { 41, (0.049m, 0.050m, 0.01741m) },
        { 42, (0.048m, 0.050m, 0.01694m) },
        { 43, (0.047m, 0.048m, 0.01664m) },
        { 44, (0.045m, 0.046m, 0.01664m) },
        { 45, (0.044m, 0.046m, 0.01634m) },
        { 46, (0.043m, 0.044m, 0.01601m) },
        { 47, (0.043m, 0.044m, 0.01532m) },
        { 48, (0.042m, 0.044m, 0.01499m) },
        { 49, (0.041m, 0.042m, 0.01475m) },
        { 50, (0.040m, 0.042m, 0.01440m) },
    };

    /// <summary>
    /// 获取定率法的折旧参数
    /// </summary>
    private static (decimal Rate, decimal RevisedRate, decimal GuaranteeRate) GetDecliningBalanceParams(int usefulLife)
    {
        if (DecliningBalanceRates.TryGetValue(usefulLife, out var rates))
            return rates;
        
        // 如果表中没有，使用计算值（200%定率法近似）
        var rate = Math.Round(2.0m / usefulLife, 3);
        return (rate, rate, 0.05m);
    }

    private class DepreciationItem
    {
        public Guid AssetId { get; set; }
        public string AssetNo { get; set; } = "";
        public string AssetName { get; set; } = "";
        public string DepreciationMethod { get; set; } = "STRAIGHT_LINE";
        public int UsefulLife { get; set; } = 5;
        public decimal AcquisitionCost { get; set; }
        public decimal BookValue { get; set; }
        public DateTime DepreciationStartDate { get; set; }
        public string DepreciationExpenseAccount { get; set; } = "";
        public string AccumulatedDepreciationAccount { get; set; } = "";
        public bool IncludeTaxInDepreciation { get; set; }
        public decimal DepreciationAmount { get; set; }
    }
}

