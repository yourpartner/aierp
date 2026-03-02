using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;
using Server.Infrastructure.Modules;

namespace Server.Modules.Staffing;

/// <summary>
/// 人才派遣 AI 模块 - 智能匹配、自动沟通、预测分析
/// </summary>
public class StaffingAiModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "staffing_ai",
        Name = "智能助手",
        Description = "AI驱动的案件解析、人才匹配、沟通自动化、市场分析、流失预测",
        Category = ModuleCategory.Staffing,
        Version = "1.0.0",
        Dependencies = new[] { "ai_core", "staffing_resource_pool", "staffing_project", "staffing_email" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_staffing_ai", Label = "menu.staffingAi", Icon = "MagicStick", Path = "/staffing/ai", ParentId = "menu_staffing", Order = 280 },
            new MenuConfig { Id = "menu_staffing_ai_matching", Label = "menu.staffingAiMatching", Icon = "Connection", Path = "/staffing/ai/matching", ParentId = "menu_staffing_ai", Order = 281 },
            new MenuConfig { Id = "menu_staffing_ai_market", Label = "menu.staffingAiMarket", Icon = "TrendCharts", Path = "/staffing/ai/market", ParentId = "menu_staffing_ai", Order = 282 },
            new MenuConfig { Id = "menu_staffing_ai_alerts", Label = "menu.staffingAiAlerts", Icon = "Bell", Path = "/staffing/ai/alerts", ParentId = "menu_staffing_ai", Order = 283 },
        }
    };

    public override void MapEndpoints(WebApplication app)
    {
        var resourceTable = Crud.TableFor("resource");
        var projectTable = Crud.TableFor("staffing_project");
        var candidateTable = Crud.TableFor("staffing_project_candidate");
        var contractTable = Crud.TableFor("staffing_contract");
        var timesheetTable = Crud.TableFor("staffing_timesheet_summary");
        var emailMessageTable = Crud.TableFor("staffing_email_message");

        // ========== 1. 智能案件解析 ==========
        // 从邮件/文本自动解析案件需求
        app.MapPost("/staffing/ai/parse-project-request", async (HttpRequest req, NpgsqlDataSource ds, IConfiguration config) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var body = await req.ReadFromJsonAsync<JsonElement>();
            var emailContent = body.GetProperty("content").GetString()!;
            var emailSubject = body.TryGetProperty("subject", out var subj) ? subj.GetString() : "";
            var senderEmail = body.TryGetProperty("senderEmail", out var se) ? se.GetString() : "";

            // 调用 AI 解析
            var prompt = $@"あなたは人材派遣会社の営業アシスタントです。
以下のメールから案件情報を抽出してください。JSONで回答してください。

【メール件名】
{emailSubject}

【メール本文】
{emailContent}

【抽出項目】
- project_name: 案件名（推測可）
- required_skills: 必要スキル（配列）
- experience_years: 経験年数（数値、不明なら null）
- work_location: 勤務地
- remote_policy: リモート可否（full_remote / hybrid / onsite / unknown）
- start_date: 開始希望日（YYYY-MM-DD形式、不明なら null）
- duration_months: 期間（月数、不明なら null）
- headcount: 募集人数（数値、不明なら 1）
- budget_min: 予算下限（万円/月、不明なら null）
- budget_max: 予算上限（万円/月、不明なら null）
- urgency: 緊急度（high / medium / low）
- notes: その他特記事項

JSON形式のみで回答してください。";

            // TODO: 实际调用 OpenAI/Claude API
            // 这里用模拟的解析结果
            var parsedResult = SimulateProjectParsing(emailContent, emailSubject);

            // 尝试匹配发件人到取引先
            Guid? matchedPartnerId = null;
            string? matchedPartnerName = null;
            if (!string.IsNullOrEmpty(senderEmail))
            {
                await using var conn = await ds.OpenConnectionAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT id, payload->>'name' 
                    FROM businesspartners 
                    WHERE company_code = $1 AND (
                        payload->>'email' ILIKE $2 OR
                        payload->>'email' ILIKE $3
                    )
                    LIMIT 1";
                cmd.Parameters.AddWithValue(cc.ToString());
                cmd.Parameters.AddWithValue($"%{senderEmail}%");
                cmd.Parameters.AddWithValue($"%{senderEmail.Split('@').LastOrDefault()}%");

                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    matchedPartnerId = reader.GetGuid(0);
                    matchedPartnerName = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }

            return Results.Ok(new
            {
                parsed = parsedResult,
                matchedPartner = matchedPartnerId != null ? new { id = matchedPartnerId, name = matchedPartnerName } : null,
                confidence = 0.85,
                suggestedActions = new[]
                {
                    new { action = "create_project", label = "案件として登録" },
                    new { action = "find_candidates", label = "候補者を検索" },
                    new { action = "reply_confirm", label = "受領確認を返信" }
                }
            });
        }).RequireAuthorization();

        // ========== 2. 语义级人才匹配 ==========
        app.MapPost("/staffing/ai/match-candidates", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var body = await req.ReadFromJsonAsync<JsonElement>();
            
            // 案件需求
            var requiredSkills = body.TryGetProperty("requiredSkills", out var rs) 
                ? rs.EnumerateArray().Select(x => x.GetString()!).ToList() 
                : new List<string>();
            var experienceYears = body.TryGetProperty("experienceYears", out var ey) ? ey.GetInt32() : 0;
            var projectDescription = (body.TryGetProperty("description", out var desc) ? desc.GetString() : null) ?? "";
            var budgetMax = body.TryGetProperty("budgetMax", out var bm) ? bm.GetDecimal() : 0m;
            var limit = body.TryGetProperty("limit", out var lim) ? lim.GetInt32() : 10;

            await using var conn = await ds.OpenConnectionAsync();

            // 第一步：基础筛选（可用状态的资源）
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT 
                    r.id, r.payload
                FROM {resourceTable} r
                WHERE r.company_code = $1 
                  AND r.status = 'active'
                  AND r.availability_status IN ('available', 'ending_soon')
                ORDER BY r.updated_at DESC
                LIMIT 100";
            cmd.Parameters.AddWithValue(cc.ToString());

            var candidates = new List<dynamic>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                using var payloadDoc = JsonDocument.Parse(reader.GetString(1));
                var p = payloadDoc.RootElement;

                var skills = p.TryGetProperty("skills", out var sk) && sk.ValueKind == JsonValueKind.Array
                    ? sk.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
                    : new List<string>();

                var experienceSummary = p.TryGetProperty("experience_summary", out var es) && es.ValueKind == JsonValueKind.String
                    ? es.GetString() ?? ""
                    : "";

                var resourceCode = p.TryGetProperty("resource_code", out var rc) ? rc.GetString() : null;
                var displayName = p.TryGetProperty("display_name", out var dn) ? dn.GetString() : null;
                var resourceType = p.TryGetProperty("resource_type", out var rt) ? rt.GetString() : null;
                var availabilityStatus = p.TryGetProperty("availability_status", out var av) ? av.GetString() : null;

                decimal? hourlyRate = null;
                if (p.TryGetProperty("hourly_rate", out var hr) && hr.ValueKind == JsonValueKind.Number) hourlyRate = hr.GetDecimal();

                decimal? monthlyRate = null;
                if (p.TryGetProperty("monthly_rate", out var mr) && mr.ValueKind == JsonValueKind.Number) monthlyRate = mr.GetDecimal();
                if (monthlyRate == null && p.TryGetProperty("default_billing_rate", out var dbr) && dbr.ValueKind == JsonValueKind.Number) monthlyRate = dbr.GetDecimal();
                
                // 计算匹配分数
                var matchScore = CalculateMatchScore(
                    requiredSkills, skills, 
                    experienceYears, experienceSummary,
                    projectDescription
                );

                if (matchScore.overall >= 0.3) // 阈值过滤
                {
                    candidates.Add(new
                    {
                        id = reader.GetGuid(0),
                        resourceCode,
                        displayName,
                        resourceType,
                        skills,
                        experienceSummary,
                        hourlyRate,
                        monthlyRate,
                        availabilityStatus,
                        matchScore = matchScore
                    });
                }
            }

            // 按匹配分排序
            var rankedCandidates = candidates
                .OrderByDescending(c => ((dynamic)c).matchScore.overall)
                .Take(limit)
                .ToList();

            return Results.Ok(new
            {
                candidates = rankedCandidates,
                totalMatched = candidates.Count,
                matchCriteria = new { requiredSkills, experienceYears, budgetMax }
            });
        }).RequireAuthorization();

        // 为案件推荐候选人（基于项目ID）
        app.MapGet("/staffing/ai/project/{projectId:guid}/recommendations", async (Guid projectId, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();

            // 获取项目信息
            await using var cmdProject = conn.CreateCommand();
            cmdProject.CommandText = $@"
                SELECT payload
                FROM {projectTable}
                WHERE id = $1 AND company_code = $2";
            cmdProject.Parameters.AddWithValue(projectId);
            cmdProject.Parameters.AddWithValue(cc.ToString());

            List<string> requiredSkills = new();
            int experienceYears = 0;
            string jobDescription = "";
            decimal budgetMax = 0;
            int headcount = 1;

            await using (var reader = await cmdProject.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    using var payloadDoc = JsonDocument.Parse(reader.GetString(0));
                    var p = payloadDoc.RootElement;

                    requiredSkills = p.TryGetProperty("required_skills", out var rs) && rs.ValueKind == JsonValueKind.Array
                        ? rs.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
                        : new List<string>();
                    experienceYears = p.TryGetProperty("experience_years", out var ey) && ey.ValueKind == JsonValueKind.Number ? ey.GetInt32() : 0;
                    jobDescription = p.TryGetProperty("job_description", out var jd) && jd.ValueKind == JsonValueKind.String ? jd.GetString() ?? "" : "";
                    budgetMax = p.TryGetProperty("budget_max", out var bm) && bm.ValueKind == JsonValueKind.Number ? bm.GetDecimal() : 0m;
                    headcount = p.TryGetProperty("headcount", out var hc) && hc.ValueKind == JsonValueKind.Number ? hc.GetInt32() : 1;
                }
                else
                {
                    return Results.NotFound(new { error = "Project not found" });
                }
            }

            // 获取可用候选人并匹配
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT 
                    r.id, r.payload
                FROM " + resourceTable + @" r
                WHERE r.company_code = $1 
                  AND r.status = 'active'
                  AND r.availability_status IN ('available', 'ending_soon')
                  AND NOT EXISTS (
                      SELECT 1 FROM " + candidateTable + @" pc 
                      WHERE pc.company_code = $1 AND pc.payload->>'project_id' = $2 AND pc.payload->>'resource_id' = r.id::text
                  )
                LIMIT 50";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(projectId.ToString());

            var recommendations = new List<object>();
            await using var reader2 = await cmd.ExecuteReaderAsync();
            while (await reader2.ReadAsync())
            {
                using var payloadDoc = JsonDocument.Parse(reader2.GetString(1));
                var p = payloadDoc.RootElement;

                var skills = p.TryGetProperty("skills", out var sk) && sk.ValueKind == JsonValueKind.Array
                    ? sk.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
                    : new List<string>();
                var experienceSummary = p.TryGetProperty("experience_summary", out var es) && es.ValueKind == JsonValueKind.String
                    ? es.GetString() ?? ""
                    : "";

                var matchScore = CalculateMatchScore(requiredSkills, skills, experienceYears, experienceSummary, jobDescription);

                if (matchScore.overall >= 0.4)
                {
                    decimal? monthlyRate = null;
                    if (p.TryGetProperty("monthly_rate", out var mr) && mr.ValueKind == JsonValueKind.Number) monthlyRate = mr.GetDecimal();
                    if (monthlyRate == null && p.TryGetProperty("default_billing_rate", out var dbr) && dbr.ValueKind == JsonValueKind.Number) monthlyRate = dbr.GetDecimal();

                    recommendations.Add(new
                    {
                        resourceId = reader2.GetGuid(0),
                        resourceCode = p.TryGetProperty("resource_code", out var rc) ? rc.GetString() : null,
                        displayName = p.TryGetProperty("display_name", out var dn) ? dn.GetString() : null,
                        resourceType = p.TryGetProperty("resource_type", out var rt) ? rt.GetString() : null,
                        skills,
                        monthlyRate,
                        availabilityStatus = p.TryGetProperty("availability_status", out var av) ? av.GetString() : null,
                        availableFrom = p.TryGetProperty("available_from", out var af) && af.ValueKind == JsonValueKind.String
                            ? DateTime.TryParse(af.GetString(), out var dt) ? dt : (DateTime?)null
                            : (DateTime?)null,
                        matchScore,
                        recommendation = GenerateRecommendationReason(matchScore, skills, requiredSkills)
                    });
                }
            }

            var ranked = recommendations
                .OrderByDescending(r => ((dynamic)r).matchScore.overall)
                .Take(headcount * 3) // 推荐人数的3倍
                .ToList();

            return Results.Ok(new
            {
                projectId,
                recommendations = ranked,
                requiredSkills,
                headcount
            });
        }).RequireAuthorization();

        // ========== 3. 自动生成沟通内容 ==========
        app.MapPost("/staffing/ai/generate-outreach", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var body = await req.ReadFromJsonAsync<JsonElement>();
            var templateType = body.GetProperty("templateType").GetString()!; // project_intro / interview_schedule / contract_confirm
            var resourceId = body.TryGetProperty("resourceId", out var rid) && rid.TryGetGuid(out var ridVal) ? ridVal : (Guid?)null;
            var projectId = body.TryGetProperty("projectId", out var pid) && pid.TryGetGuid(out var pidVal) ? pidVal : (Guid?)null;

            await using var conn = await ds.OpenConnectionAsync();

            // 获取资源信息
            string? resourceName = null, resourceEmail = null;
            if (resourceId.HasValue)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT payload FROM {resourceTable} WHERE id = $1 AND company_code = $2";
                cmd.Parameters.AddWithValue(resourceId.Value);
                cmd.Parameters.AddWithValue(cc.ToString());
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    using var payloadDoc = JsonDocument.Parse(reader.GetString(0));
                    var p = payloadDoc.RootElement;
                    resourceName = p.TryGetProperty("display_name", out var dn) ? dn.GetString() : null;
                    resourceEmail = p.TryGetProperty("email", out var em) ? em.GetString() : null;
                }
            }

            // 获取项目信息
            string? projectName = null, clientName = null;
            List<string> requiredSkills = new();
            if (projectId.HasValue)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    SELECT p.payload, bp.payload->>'name'
                    FROM {projectTable} p
                    LEFT JOIN businesspartners bp ON p.payload->>'client_partner_id' = bp.id::text
                    WHERE p.id = $1 AND p.company_code = $2";
                cmd.Parameters.AddWithValue(projectId.Value);
                cmd.Parameters.AddWithValue(cc.ToString());
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    using var payloadDoc = JsonDocument.Parse(reader.GetString(0));
                    var p = payloadDoc.RootElement;
                    projectName = p.TryGetProperty("project_name", out var pn) ? pn.GetString() : null;
                    requiredSkills = p.TryGetProperty("required_skills", out var rs) && rs.ValueKind == JsonValueKind.Array
                        ? rs.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
                        : new List<string>();
                    clientName = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }

            // 生成邮件内容
            var (subject, bodyContent) = GenerateOutreachContent(
                templateType, resourceName, projectName, clientName, requiredSkills
            );

            return Results.Ok(new
            {
                subject,
                body = bodyContent,
                to = resourceEmail,
                templateType,
                variables = new { resourceName, projectName, clientName, requiredSkills }
            });
        }).RequireAuthorization();

        // ========== 4. 市场行情分析 ==========
        app.MapPost("/staffing/ai/market-analysis", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var body = await req.ReadFromJsonAsync<JsonElement>();
            var skills = body.TryGetProperty("skills", out var sk) 
                ? sk.EnumerateArray().Select(x => x.GetString()!).ToList() 
                : new List<string>();
            var experienceYears = body.TryGetProperty("experienceYears", out var ey) ? ey.GetInt32() : 3;

            await using var conn = await ds.OpenConnectionAsync();

            // 分析历史成交数据
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT 
                    c.payload,
                    r.payload
                FROM {contractTable} c
                JOIN {resourceTable} r ON c.payload->>'resource_id' = r.id::text
                WHERE c.company_code = $1 
                  AND c.payload->>'status' IN ('active', 'completed')
                  AND (c.payload->>'start_date')::date >= now() - interval '12 months'
                ORDER BY (c.payload->>'start_date')::date DESC
                LIMIT 200";
            cmd.Parameters.AddWithValue(cc.ToString());

            var rateData = new List<(decimal billing, decimal cost, List<string> skills)>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                using var cDoc = JsonDocument.Parse(reader.GetString(0));
                var c = cDoc.RootElement;
                using var rDoc = JsonDocument.Parse(reader.GetString(1));
                var r = rDoc.RootElement;

                var billingRate = c.TryGetProperty("billing_rate", out var br) && br.ValueKind == JsonValueKind.Number ? br.GetDecimal() : 0m;
                var costRate = c.TryGetProperty("cost_rate", out var cr) && cr.ValueKind == JsonValueKind.Number ? cr.GetDecimal() : 0m;
                var contractSkills = r.TryGetProperty("skills", out var sk2) && sk2.ValueKind == JsonValueKind.Array
                    ? sk2.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
                    : new List<string>();
                rateData.Add((billingRate, costRate, contractSkills));
            }

            // 筛选技能相关的数据
            var relevantRates = rateData
                .Where(d => skills.Count == 0 || d.skills.Any(s => skills.Any(rs => 
                    s.Contains(rs, StringComparison.OrdinalIgnoreCase) || 
                    rs.Contains(s, StringComparison.OrdinalIgnoreCase))))
                .Select(d => d.billing)
                .OrderBy(r => r)
                .ToList();

            if (relevantRates.Count < 3)
            {
                // 数据不足，使用全部数据
                relevantRates = rateData.Select(d => d.billing).OrderBy(r => r).ToList();
            }

            var analysis = new
            {
                sampleSize = relevantRates.Count,
                skills,
                experienceYears,
                priceRange = relevantRates.Count > 0 ? new
                {
                    min = relevantRates.First(),
                    percentile25 = relevantRates.ElementAtOrDefault(relevantRates.Count / 4),
                    median = relevantRates.ElementAtOrDefault(relevantRates.Count / 2),
                    percentile75 = relevantRates.ElementAtOrDefault(relevantRates.Count * 3 / 4),
                    max = relevantRates.Last(),
                    average = Math.Round(relevantRates.Average(), 0)
                } : null,
                recommendations = new[]
                {
                    new { 
                        price = relevantRates.Count > 0 ? relevantRates.ElementAtOrDefault(relevantRates.Count * 3 / 4) : 650000m,
                        probability = 60,
                        label = "強気"
                    },
                    new {
                        price = relevantRates.Count > 0 ? relevantRates.ElementAtOrDefault(relevantRates.Count / 2) : 600000m,
                        probability = 80,
                        label = "適正"
                    },
                    new {
                        price = relevantRates.Count > 0 ? relevantRates.ElementAtOrDefault(relevantRates.Count / 4) : 550000m,
                        probability = 95,
                        label = "確実"
                    }
                },
                marketTrend = "stable", // rising / stable / falling
                supplyDemandRatio = 0.85, // < 1 表示供不应求
                seasonalNote = GetSeasonalNote()
            };

            return Results.Ok(analysis);
        }).RequireAuthorization();

        // ========== 5. 流失预测与预警 ==========
        app.MapGet("/staffing/ai/churn-alerts", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();

            var alerts = new List<object>();

            // 1. 合同即将到期（30天内）
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT 
                        c.id, c.payload,
                        r.id as resource_id, r.payload as resource_payload,
                        bp.payload as client_payload
                    FROM {contractTable} c
                    JOIN {resourceTable} r ON c.payload->>'resource_id' = r.id::text
                    LEFT JOIN businesspartners bp ON c.payload->>'client_partner_id' = bp.id::text
                    WHERE c.company_code = $1 
                      AND c.payload->>'status' = 'active'
                      AND c.payload->>'end_date' IS NOT NULL
                      AND (c.payload->>'end_date')::date <= CURRENT_DATE + 30
                    ORDER BY (c.payload->>'end_date')::date";
                cmd.Parameters.AddWithValue(cc.ToString());

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    using var cDoc = JsonDocument.Parse(reader.GetString(1));
                    var c = cDoc.RootElement;
                    using var rDoc = JsonDocument.Parse(reader.GetString(3));
                    var r = rDoc.RootElement;
                    using var bpDoc = JsonDocument.Parse(reader.IsDBNull(4) ? "{}" : reader.GetString(4));
                    var bp = bpDoc.RootElement;

                    var endDate = c.TryGetProperty("end_date", out var ed) && ed.ValueKind == JsonValueKind.String
                        ? DateTime.Parse(ed.GetString()!)
                        : DateTime.Today;
                    var daysRemaining = (endDate - DateTime.Today).Days;
                    var name = r.TryGetProperty("display_name", out var dn) ? dn.GetString() : "";
                    
                    alerts.Add(new
                    {
                        type = "contract_expiring",
                        severity = daysRemaining <= 7 ? "critical" : daysRemaining <= 14 ? "high" : "medium",
                        contractId = reader.GetGuid(0),
                        contractNo = c.TryGetProperty("contract_no", out var cn) ? cn.GetString() : null,
                        resourceId = reader.GetGuid(3),
                        resourceName = name,
                        resourceType = r.TryGetProperty("resource_type", out var rt) ? rt.GetString() : null,
                        clientName = bp.TryGetProperty("name", out var cname) ? cname.GetString() : null,
                        endDate,
                        daysRemaining,
                        billingRate = c.TryGetProperty("billing_rate", out var br) && br.ValueKind == JsonValueKind.Number ? br.GetDecimal() : 0m,
                        message = $"{name}の契約が{daysRemaining}日後に終了します",
                        suggestedActions = new[] { "更新確認", "次案件検索", "面談設定" }
                    });
                }
            }

            // 2. 长期无薪资调整（12个月以上活跃但未调薪）
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT 
                        c.id, c.payload,
                        r.id as resource_id, r.payload as resource_payload,
                        bp.payload as client_payload
                    FROM {contractTable} c
                    JOIN {resourceTable} r ON c.payload->>'resource_id' = r.id::text
                    LEFT JOIN businesspartners bp ON c.payload->>'client_partner_id' = bp.id::text
                    WHERE c.company_code = $1 
                      AND c.payload->>'status' = 'active'
                      AND (c.payload->>'start_date')::date <= CURRENT_DATE - 365
                      AND c.updated_at <= CURRENT_DATE - 180
                    LIMIT 20";
                cmd.Parameters.AddWithValue(cc.ToString());

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    using var cDoc = JsonDocument.Parse(reader.GetString(1));
                    var c = cDoc.RootElement;
                    using var rDoc = JsonDocument.Parse(reader.GetString(3));
                    var r = rDoc.RootElement;
                    using var bpDoc = JsonDocument.Parse(reader.IsDBNull(4) ? "{}" : reader.GetString(4));
                    var bp = bpDoc.RootElement;

                    var startDate = c.TryGetProperty("start_date", out var sd) && sd.ValueKind == JsonValueKind.String
                        ? DateTime.Parse(sd.GetString()!)
                        : DateTime.Today;
                    var monthsActive = (int)((DateTime.Today - startDate).TotalDays / 30);
                    var name = r.TryGetProperty("display_name", out var dn) ? dn.GetString() : "";
                    
                    alerts.Add(new
                    {
                        type = "no_salary_review",
                        severity = "medium",
                        contractId = reader.GetGuid(0),
                        contractNo = c.TryGetProperty("contract_no", out var cn) ? cn.GetString() : null,
                        resourceId = reader.GetGuid(2),
                        resourceName = name,
                        clientName = bp.TryGetProperty("name", out var cname) ? cname.GetString() : null,
                        monthsActive,
                        currentRate = c.TryGetProperty("billing_rate", out var br) && br.ValueKind == JsonValueKind.Number ? br.GetDecimal() : 0m,
                        message = $"{name}は{monthsActive}ヶ月間単価変更なし",
                        suggestedActions = new[] { "単価見直し", "面談設定" }
                    });
                }
            }

            // 3. 残業过多（上月残业超过40小时）
            await using (var cmd = conn.CreateCommand())
            {
                var lastMonth = DateTime.Today.AddMonths(-1).ToString("yyyy-MM");
                cmd.CommandText = $@"
                    SELECT 
                        ts.payload->>'resource_id' as resource_id,
                        r.payload->>'display_name' as resource_name,
                        fn_jsonb_numeric(ts.payload,'overtime_hours') as overtime_hours,
                        fn_jsonb_numeric(ts.payload,'actual_hours') as actual_hours,
                        c.payload->>'contract_no' as contract_no,
                        bp.payload->>'name' as client_name
                    FROM {timesheetTable} ts
                    JOIN {resourceTable} r ON ts.payload->>'resource_id' = r.id::text
                    LEFT JOIN {contractTable} c ON ts.payload->>'contract_id' = c.id::text
                    LEFT JOIN businesspartners bp ON c.payload->>'client_partner_id' = bp.id::text
                    WHERE ts.company_code = $1 
                      AND ts.payload->>'year_month' = $2
                      AND fn_jsonb_numeric(ts.payload,'overtime_hours') > 40
                    ORDER BY fn_jsonb_numeric(ts.payload,'overtime_hours') DESC
                    LIMIT 10";
                cmd.Parameters.AddWithValue(cc.ToString());
                cmd.Parameters.AddWithValue(lastMonth);

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var overtime = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2);
                    var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    alerts.Add(new
                    {
                        type = "excessive_overtime",
                        severity = overtime > 60 ? "high" : "medium",
                        resourceId = reader.IsDBNull(0) ? (Guid?)null : Guid.Parse(reader.GetString(0)!),
                        resourceName = name,
                        overtimeHours = overtime,
                        actualHours = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3),
                        contractNo = reader.IsDBNull(4) ? null : reader.GetString(4),
                        clientName = reader.IsDBNull(5) ? null : reader.GetString(5),
                        yearMonth = lastMonth,
                        message = $"{name}の先月残業{overtime}時間",
                        suggestedActions = new[] { "状況確認", "クライアント相談" }
                    });
                }
            }

            // 按严重程度排序
            var sortedAlerts = alerts
                .OrderBy(a => ((dynamic)a).severity switch
                {
                    "critical" => 0,
                    "high" => 1,
                    "medium" => 2,
                    _ => 3
                })
                .ToList();

            return Results.Ok(new
            {
                alerts = sortedAlerts,
                summary = new
                {
                    critical = alerts.Count(a => ((dynamic)a).severity == "critical"),
                    high = alerts.Count(a => ((dynamic)a).severity == "high"),
                    medium = alerts.Count(a => ((dynamic)a).severity == "medium"),
                    total = alerts.Count
                }
            });
        }).RequireAuthorization();

        // ========== 6. AI 工作台 - 综合建议 ==========
        app.MapGet("/staffing/ai/dashboard", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();

            var dashboard = new Dictionary<string, object>();

            // 待处理邮件（未解析）
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT COUNT(*) FROM {emailMessageTable}
                    WHERE company_code = $1 AND payload->>'status' = 'new'";
                cmd.Parameters.AddWithValue(cc.ToString());
                dashboard["unprocessedEmails"] = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }

            // 待匹配案件（open状态且无候选人）
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT COUNT(*) FROM {projectTable} p
                    WHERE p.company_code = $1 
                      AND p.payload->>'status' IN ('open', 'matching')
                      AND NOT EXISTS (
                          SELECT 1 FROM {candidateTable} pc 
                          WHERE pc.company_code = $1 AND pc.payload->>'project_id' = p.id::text
                      )";
                cmd.Parameters.AddWithValue(cc.ToString());
                dashboard["projectsNeedingCandidates"] = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }

            // 可用资源数
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT COUNT(*) FROM {resourceTable}
                    WHERE company_code = $1 AND status = 'active' AND availability_status = 'available'";
                cmd.Parameters.AddWithValue(cc.ToString());
                dashboard["availableResources"] = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }

            // 今日待办
            var todayTasks = new List<object>();
            
            // 今日面试
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT COUNT(*) FROM {candidateTable}
                    WHERE company_code = $1 
                      AND (payload->>'interview_date')::date = CURRENT_DATE
                      AND payload->>'status' = 'interviewing'";
                cmd.Parameters.AddWithValue(cc.ToString());
                var interviews = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                if (interviews > 0)
                {
                    todayTasks.Add(new { type = "interview", count = interviews, label = $"今日の面接 {interviews}件" });
                }
            }

            dashboard["todayTasks"] = todayTasks;

            // AI 建议
            var suggestions = new List<object>
            {
                new { priority = "high", action = "process_emails", message = $"未処理メール {dashboard["unprocessedEmails"]}件を確認してください" },
                new { priority = "medium", action = "match_projects", message = $"候補者未設定の案件 {dashboard["projectsNeedingCandidates"]}件があります" }
            };
            dashboard["suggestions"] = suggestions;

            return Results.Ok(dashboard);
        }).RequireAuthorization();
    }

    // ========== Helper Methods ==========

    private static object SimulateProjectParsing(string content, string? subject)
    {
        // 实际应该调用 LLM，这里模拟解析结果
        return new
        {
            project_name = subject?.Replace("【案件】", "").Trim() ?? "新規案件",
            required_skills = new[] { "Java", "Spring Boot", "AWS" },
            experience_years = 5,
            work_location = "東京",
            remote_policy = "hybrid",
            start_date = DateTime.Today.AddMonths(1).ToString("yyyy-MM-dd"),
            duration_months = 6,
            headcount = 1,
            budget_min = 55,
            budget_max = 70,
            urgency = "medium",
            notes = "コミュニケーション能力重視"
        };
    }

    private static (double overall, double skillMatch, double experienceMatch) CalculateMatchScore(
        List<string> requiredSkills, 
        List<string> candidateSkills,
        int requiredExperience,
        string experienceSummary,
        string jobDescription)
    {
        // 技能匹配分数
        double skillMatch = 0;
        if (requiredSkills.Count > 0 && candidateSkills.Count > 0)
        {
            var matched = requiredSkills.Count(rs => 
                candidateSkills.Any(cs => 
                    cs.Contains(rs, StringComparison.OrdinalIgnoreCase) ||
                    rs.Contains(cs, StringComparison.OrdinalIgnoreCase)));
            skillMatch = (double)matched / requiredSkills.Count;
        }
        else if (requiredSkills.Count == 0)
        {
            skillMatch = 0.5; // 无要求时给基础分
        }

        // 经验匹配分数（简化）
        double experienceMatch = 0.5;
        if (!string.IsNullOrEmpty(experienceSummary))
        {
            // 简单判断：经验描述越长，经验越丰富
            experienceMatch = Math.Min(experienceSummary.Length / 500.0, 1.0);
        }

        // 综合分数
        double overall = skillMatch * 0.6 + experienceMatch * 0.4;

        return (Math.Round(overall, 2), Math.Round(skillMatch, 2), Math.Round(experienceMatch, 2));
    }

    private static string GenerateRecommendationReason(
        (double overall, double skillMatch, double experienceMatch) score,
        List<string> candidateSkills,
        List<string> requiredSkills)
    {
        var matchedSkills = requiredSkills
            .Where(rs => candidateSkills.Any(cs => 
                cs.Contains(rs, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matchedSkills.Count > 0)
        {
            return $"{string.Join("、", matchedSkills)}のスキルがマッチ（適合度{(int)(score.overall * 100)}%）";
        }
        return $"経験・スキルセットが近い（適合度{(int)(score.overall * 100)}%）";
    }

    private static (string subject, string body) GenerateOutreachContent(
        string templateType,
        string? resourceName,
        string? projectName,
        string? clientName,
        List<string> skills)
    {
        var name = resourceName ?? "様";
        var project = projectName ?? "新規案件";
        var client = clientName ?? "大手企業";
        var skillText = skills.Count > 0 ? string.Join("、", skills.Take(3)) : "ご経験のスキル";

        return templateType switch
        {
            "project_intro" => (
                $"【案件のご紹介】{project}",
                $@"{name}様

お世話になっております。

現在、{name}様のご経験・スキルにマッチする案件がございます。

【案件概要】
・案件名：{project}
・クライアント：{client}
・必要スキル：{skillText}
・勤務形態：ハイブリッド（週2-3リモート可）

ご興味がございましたら、詳細をご説明させていただきます。
ご都合の良い日時をお知らせいただけますでしょうか。

何卒よろしくお願いいたします。"
            ),
            "interview_schedule" => (
                $"【面談日程のご相談】{project}",
                $@"{name}様

お世話になっております。

{project}の件でご連絡いたします。
クライアント様との面談日程を調整させてください。

以下の日程でご都合いかがでしょうか。
・○月○日（○）10:00〜
・○月○日（○）14:00〜
・○月○日（○）15:00〜

オンライン（Teams）での実施を予定しております。

ご確認のほど、よろしくお願いいたします。"
            ),
            "contract_confirm" => (
                $"【契約書送付のご連絡】{project}",
                $@"{name}様

お世話になっております。

{project}の契約書を添付にてお送りいたします。

内容をご確認いただき、問題がなければ
ご署名の上、ご返送いただけますでしょうか。

ご不明点がございましたら、お気軽にお問い合わせください。

何卒よろしくお願いいたします。"
            ),
            _ => (
                "ご連絡",
                $"{name}様\n\nお世話になっております。\n\n"
            )
        };
    }

    private static string GetSeasonalNote()
    {
        var month = DateTime.Today.Month;
        return month switch
        {
            1 or 2 => "年度末に向けて案件増加傾向",
            3 => "年度切替で案件が集中する時期",
            4 => "新年度スタート、需要が高い時期",
            5 or 6 => "比較的落ち着いた時期",
            7 or 8 => "夏季、やや案件減少",
            9 or 10 => "下期スタート、需要回復",
            11 or 12 => "年末に向けて徐々に落ち着く",
            _ => ""
        };
    }
}

