using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

public sealed class PayrollExecutionException : Exception
{
    public int StatusCode { get; }
    public object Payload { get; }

    public PayrollExecutionException(int statusCode, object payload, string message) : base(message)
    {
        StatusCode = statusCode;
        Payload = payload;
    }
}

public sealed record PayrollExecutionResult(List<object> PayrollSheet, List<object> AccountingDraft, List<object>? Trace);

public static class HrPayrollModule
{
    public static void MapHrPayrollModule(this WebApplication app)
    {
        // AI: 将自然语言薪资规则编译为内部 DSL（骨架）
        app.MapPost("/ai/payroll/compile", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var text = root.TryGetProperty("nlText", out var t) && t.ValueKind==JsonValueKind.String ? t.GetString() : null;
            var companyText = root.TryGetProperty("companyText", out var ct) && ct.ValueKind==JsonValueKind.String ? ct.GetString() : null;
            var employeeText = root.TryGetProperty("employeeText", out var et) && et.ValueKind==JsonValueKind.String ? et.GetString() : null;
            var s = string.Join("\n", new[]{ companyText, employeeText, text }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (string.IsNullOrWhiteSpace(s)) return Results.BadRequest(new { error = "nlText/companyText/employeeText required" });

            decimal ParseAmountNear(string src, string[] anchors)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(src)) return 0m;
                    var idx = anchors.Select(a => src.IndexOf(a, StringComparison.OrdinalIgnoreCase)).Where(i => i>=0).DefaultIfEmpty(-1).Min();
                    if (idx<0) idx = 0;
                    var seg = src.Substring(idx);
                    // very simple patterns: 50万, 50.5万, 500,000円, 500000円
                    var m1 = System.Text.RegularExpressions.Regex.Match(seg, @"(\d+(?:\.\d+)?)\s*万");
                    if (m1.Success) { var v = decimal.Parse(m1.Groups[1].Value); return Math.Round(v * 10000m, 0); }
                    var m2 = System.Text.RegularExpressions.Regex.Match(seg, @"(\d[\d,]*)\s*円");
                    if (m2.Success) { var v = decimal.Parse(m2.Groups[1].Value.Replace(",", "")); return v; }
                    var m3 = System.Text.RegularExpressions.Regex.Match(seg, @"(\d[\d,]*)");
                    if (m3.Success) { var v = decimal.Parse(m3.Groups[1].Value.Replace(",", "")); return v; }
                }
                catch {}
                return 0m;
            }

            string[] typeTokens = new[]{"正社员","正社員","契约社员","契約社員","兼职","パート","个人事业主","個人事業主"};
            var empTypes = new List<object>();
            foreach (var tok in typeTokens)
            {
                if (s.Contains(tok, StringComparison.OrdinalIgnoreCase))
                {
                    var code = tok switch
                    {
                        var x when x.Contains("正") => "FT",
                        var x when x.Contains("契") => "CT",
                        var x when x.Contains("兼职") || x.Contains("パート") => "PT",
                        _ => "OT"
                    };
                    if (!empTypes.Any(x => (string)(x.GetType().GetProperty("code")!.GetValue(x)!) == code))
                        empTypes.Add(new { code, name = tok, isActive = true });
                }
            }
            if (empTypes.Count==0) empTypes.Add(new { code="FT", name="正社员", isActive=true });

            var items = new List<object>();
            var rules = new List<object>();
            var hints = new List<string>();
            var dependencies = new List<string>();
            var riskFlags = new List<string>();
            bool hasBase = s.Contains("基本給", StringComparison.OrdinalIgnoreCase) || s.Contains("基本给", StringComparison.OrdinalIgnoreCase) || s.Contains("月給", StringComparison.OrdinalIgnoreCase) || s.Contains("月给", StringComparison.OrdinalIgnoreCase) || s.Contains("固定", StringComparison.OrdinalIgnoreCase) || s.Contains("基本工资", StringComparison.OrdinalIgnoreCase);
            // 粗略解析金额（若文本中出现）
            var baseAmount = ParseAmountNear(s, new[]{"基本给","基本給","月給","月给","固定","基本工资"});
            var commuteAmount = ParseAmountNear(s, new[]{"通勤","通勤手当"});

            if (hasBase)
            {
                items.Add(new { code="BASE", name="基本工资", kind="earning", isActive=true });
                object formulaObj;
                if (baseAmount>0) formulaObj = new { _const = baseAmount };
                else formulaObj = new { charRef = "employee.baseSalaryMonth" }; // placeholder key, handled via eval as ref when charRef present
                rules.Add(new { item = "BASE", type = "earning", activation = new { forEmploymentTypes = empTypes.Select(t => (string)t.GetType().GetProperty("code")!.GetValue(t)!).ToArray() }, formula = formulaObj, rounding = new { method = "round", precision = 0 } });
            }
            if (s.Contains("通勤", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new { code="COMMUTE", name="通勤手当", kind="earning", isActive=true });
                object formulaObj;
                if (commuteAmount>0) formulaObj = new { _const = commuteAmount, cap = 30000 };
                else formulaObj = new { charRef = "employee.commuteAllowance", cap = 30000 };
                rules.Add(new { item = "COMMUTE", type = "earning", activation = new { forEmploymentTypes = empTypes.Select(t => (string)t.GetType().GetProperty("code")!.GetValue(t)!).ToArray() }, formula = formulaObj, rounding = new { method = "round", precision = 0 } });
            }
            bool wantsHealth = s.Contains("社会保険", StringComparison.OrdinalIgnoreCase) || s.Contains("社保", StringComparison.OrdinalIgnoreCase) || s.Contains("健康保険", StringComparison.OrdinalIgnoreCase);
            bool wantsPension = s.Contains("厚生年金", StringComparison.OrdinalIgnoreCase) || s.Contains("年金", StringComparison.OrdinalIgnoreCase);
            bool wantsEmployment = s.Contains("雇用保険", StringComparison.OrdinalIgnoreCase) || s.Contains("雇佣保险", StringComparison.OrdinalIgnoreCase);
            if (wantsHealth) {
                object baseExpr;
                if (baseAmount>0) baseExpr = new { _const = baseAmount };
                else baseExpr = new { baseRef = "employee.healthBase" };
                rules.Add(new { item = "HEALTH_INS", type = "deduction", activation = new { forEmploymentTypes = empTypes.Select(t => (string)t.GetType().GetProperty("code")!.GetValue(t)!).ToArray() }, formula = new { _base = baseExpr, rate = "policy.law.health.rate", rounding = new { method = "round", precision = 0 } } });
                hints.Add("已生成健康保险计算规则：按都道府県与月额区间匹配费率");
                dependencies.AddRange(new[]{
                    "jp.health.standardMonthly",
                    "jp.health.rate.employee",
                    "jp.health.rate.employer",
                    "jp.health.care.rate.employee",
                    "jp.health.care.rate.employer"
                });
                if (!(s.Contains("都道府", StringComparison.OrdinalIgnoreCase) || s.Contains("都", StringComparison.OrdinalIgnoreCase) || s.Contains("県", StringComparison.OrdinalIgnoreCase))) riskFlags.Add("health.missing_prefecture");
                if (!(s.Contains("協会", StringComparison.OrdinalIgnoreCase) || s.Contains("組合", StringComparison.OrdinalIgnoreCase))) riskFlags.Add("health.missing_scheme");
            }
            if (wantsPension) {
                object baseExpr;
                if (baseAmount>0) baseExpr = new { _const = baseAmount };
                else baseExpr = new { baseRef = "employee.pensionBase" };
                rules.Add(new { item = "PENSION", type = "deduction", activation = new { forEmploymentTypes = empTypes.Select(t => (string)t.GetType().GetProperty("code")!.GetValue(t)!).ToArray() }, formula = new { _base = baseExpr, rate = "policy.law.pension.rate", rounding = new { method = "round", precision = 0 } } });
                hints.Add("已生成厚生年金计算规则：按月额区间匹配费率");
                dependencies.AddRange(new[]{
                    "jp.pension.standardMonthly",
                    "jp.pension.rate.employee",
                    "jp.pension.rate.employer"
                });
            }
            if (wantsEmployment) {
                object baseExpr;
                if (baseAmount>0 || commuteAmount>0) baseExpr = new { sum = new object[]{ new { _const = baseAmount }, new { _const = commuteAmount } } };
                else baseExpr = new { baseRef = "employee.salaryTotal" };
                rules.Add(new { item = "EMP_INS", type = "deduction", activation = new { forEmploymentTypes = empTypes.Select(t => (string)t.GetType().GetProperty("code")!.GetValue(t)!).ToArray() }, formula = new { _base = baseExpr, rate = "policy.law.employment.rate", rounding = new { method = "round", precision = 0 } } });
                hints.Add("已生成雇用保险计算规则：按事业区分匹配费率");
                dependencies.AddRange(new[]{
                    "jp.ei.rate.employee",
                    "jp.ei.rate.employer"
                });
                if (!(s.Contains("一般", StringComparison.OrdinalIgnoreCase) || s.Contains("建設", StringComparison.OrdinalIgnoreCase) || s.Contains("建筑", StringComparison.OrdinalIgnoreCase) || s.Contains("農林水産", StringComparison.OrdinalIgnoreCase))) riskFlags.Add("ei.missing_industry");
            }

            if (!hasBase) riskFlags.Add("employee.missing_base_salary");

            var dsl = new { employmentTypes = empTypes, payrollItems = items, rules, law = new { source = "dataset", dependencies = dependencies.Distinct().ToArray() }, hints };
            var explanation = "已将自然语言编译为可执行规则：BASE/COMMUTE 与三险（如有提及）。计算由本地引擎执行，费率由法规数据集匹配。";
            var tests = new object[]{
                new { name="basic", expectItems = items.Select(i => (string)i.GetType().GetProperty("code")!.GetValue(i)!).ToArray() },
                new { name="law-deps", expectLawDependencies = dependencies.Distinct().ToArray() }
            };
            return Results.Ok(new { dsl, explanation, tests, riskFlags = riskFlags.Distinct().ToArray() });
        }).RequireAuthorization();

        // Payroll: 执行计算（无落库），用于前端独立“工资计算”页预览
        app.MapPost("/operations/payroll/execute", async (HttpRequest req, NpgsqlDataSource ds, LawDatasetService law, CancellationToken ct) =>
        {
            return await ExecutePayrollEndpoint(req, ds, law, ct);
        }).RequireAuthorization();

        // 旧工资预览接口已移除：采用“AI 编译 + 本地执行”新方案，不再直接预览

        // AI: 工资项目 → 会计科目建议（骨架）
        app.MapPost("/ai/payroll/suggest-accounts", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var items = root.TryGetProperty("items", out var it) && it.ValueKind==JsonValueKind.Array ? it.EnumerateArray().Select(x => x.GetString()).Where(s=>!string.IsNullOrWhiteSpace(s)).ToArray() : Array.Empty<string>();
            var map = items.ToDictionary(k => k!, v => new { accountCode = (string?)null, contraAccountCode = (string?)null });
            return Results.Ok(new { suggestions = map, note = "placeholder suggestions" });
        }).RequireAuthorization();
    }

    private static async Task<IResult> ExecutePayrollEndpoint(HttpRequest req, NpgsqlDataSource ds, LawDatasetService law, CancellationToken ct)
    {
        if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
            return Results.BadRequest(new { error = "Missing x-company-code" });

        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        var root = doc.RootElement;
        var employeeIdText = root.TryGetProperty("employeeId", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        var month = root.TryGetProperty("month", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var policyIdText = root.TryGetProperty("policyId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var debug = root.TryGetProperty("debug", out var dbg) && ((dbg.ValueKind == JsonValueKind.True) || (dbg.ValueKind == JsonValueKind.String && bool.TryParse(dbg.GetString(), out var bv) && bv));

        if (string.IsNullOrWhiteSpace(employeeIdText) || string.IsNullOrWhiteSpace(month))
            return Results.BadRequest(new { error = "employeeId and month required" });
        if (!Guid.TryParse(employeeIdText, out var employeeId))
            return Results.BadRequest(new { error = "employeeId invalid" });

        Guid? policyId = null;
        if (!string.IsNullOrWhiteSpace(policyIdText))
        {
            if (!Guid.TryParse(policyIdText, out var pid))
                return Results.BadRequest(new { error = "policyId invalid" });
            policyId = pid;
        }

        try
        {
            var result = await ExecutePayrollInternal(ds, law, cc.ToString(), employeeId, month, policyId, debug, ct);
            if (debug)
            {
                return Results.Ok(new
                {
                    payrollSheet = result.PayrollSheet,
                    accountingDraft = result.AccountingDraft,
                    month,
                    employeeId = employeeIdText,
                    policyId = policyIdText,
                    trace = result.Trace
                });
            }

            return Results.Ok(new
            {
                payrollSheet = result.PayrollSheet,
                accountingDraft = result.AccountingDraft,
                month,
                employeeId = employeeIdText,
                policyId = policyIdText
            });
        }
        catch (PayrollExecutionException ex)
        {
            return Results.Json(ex.Payload, statusCode: ex.StatusCode);
        }
    }

    internal static async Task<PayrollExecutionResult> ExecutePayrollInternal(NpgsqlDataSource ds, LawDatasetService law, string companyCode, Guid employeeId, string month, Guid? policyId, bool debug, CancellationToken ct)
    {
        var trace = debug ? new List<object>() : null;

        await using var conn = await ds.OpenConnectionAsync(ct);
        string? empJson = null;
        string? employeeCodeOut = null;
        string? departmentCodeOut = null;
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT payload, employee_code, department_code FROM employees WHERE company_code=$1 AND id=$2 LIMIT 1";
            q.Parameters.AddWithValue(companyCode);
            q.Parameters.AddWithValue(employeeId);
            await using var rd = await q.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                empJson = rd.IsDBNull(0) ? null : rd.GetString(0);
                employeeCodeOut = rd.IsDBNull(1) ? null : rd.GetString(1);
                departmentCodeOut = rd.IsDBNull(2) ? null : rd.GetString(2);
            }
        }
        if (string.IsNullOrEmpty(empJson))
            throw new PayrollExecutionException(StatusCodes.Status404NotFound, new { error = "employee not found" }, "employee not found");
        using var empDoc = JsonDocument.Parse(empJson);
        var emp = empDoc.RootElement;
        try
        {
            if (string.IsNullOrWhiteSpace(employeeCodeOut) && emp.TryGetProperty("code", out var ec) && ec.ValueKind==JsonValueKind.String)
                employeeCodeOut = ec.GetString();
        } catch {}
        try
        {
            if (string.IsNullOrWhiteSpace(departmentCodeOut) && emp.TryGetProperty("departmentCode", out var dc) && dc.ValueKind==JsonValueKind.String)
                departmentCodeOut = dc.GetString();
        } catch {}
        if (debug)
        {
            var empText = emp.TryGetProperty("nlPayrollDescription", out var nle) && nle.ValueKind==JsonValueKind.String ? nle.GetString() : null;
            trace?.Add(new { step="input.employee", employeeId = employeeId.ToString(), month, nl = empText });
        }

        decimal nlBase = 0m; decimal nlCommute = 0m;
        try
        {
            string? empNl = emp.TryGetProperty("nlPayrollDescription", out var ne) && ne.ValueKind==JsonValueKind.String ? ne.GetString() : null;
            if (!string.IsNullOrWhiteSpace(empNl))
            {
                decimal ParseAmountNearLocal(string src, string[] anchors)
                {
                    try
                    {
                        var idx = anchors.Select(a => src.IndexOf(a, StringComparison.OrdinalIgnoreCase)).Where(i => i>=0).DefaultIfEmpty(-1).Min();
                        if (idx<0) idx = 0;
                        var seg = src.Substring(idx);
                        var m1 = System.Text.RegularExpressions.Regex.Match(seg, @"(\d+(?:\.\d+)?)\s*万");
                        if (m1.Success) { var v = decimal.Parse(m1.Groups[1].Value); return Math.Round(v * 10000m, 0); }
                        var m2 = System.Text.RegularExpressions.Regex.Match(seg, @"(\d[\d,]*)\s*円");
                        if (m2.Success) { var v = decimal.Parse(m2.Groups[1].Value.Replace(",", "")); return v; }
                        var m3 = System.Text.RegularExpressions.Regex.Match(seg, @"(\d[\d,]*)");
                        if (m3.Success) { var v = decimal.Parse(m3.Groups[1].Value.Replace(",", "")); return v; }
                    }
                    catch {}
                    return 0m;
                }
                nlBase = ParseAmountNearLocal(empNl, new[]{"基本给","基本給","月給","月给","固定","基本工资","月薪"});
                nlCommute = ParseAmountNearLocal(empNl, new[]{"通勤","通勤手当","交通费","交通費"});
            }
        } catch {}
        if (debug) trace?.Add(new { step="input.employee.nlParsed", baseAmount = nlBase, commuteAmount = nlCommute });

        JsonElement? policy = null;
        string policySource = "none";
        if (policyId.HasValue)
        {
            await using var qp = conn.CreateCommand();
            qp.CommandText = "SELECT payload FROM payroll_policies WHERE company_code=$1 AND id=$2 LIMIT 1";
            qp.Parameters.AddWithValue(companyCode); qp.Parameters.AddWithValue(policyId.Value);
            var pj = (string?)await qp.ExecuteScalarAsync(ct);
            if (!string.IsNullOrEmpty(pj)) { policy = JsonDocument.Parse(pj).RootElement; policySource = "specified"; }
        }
        if (policy is null)
        {
            await using var qa = conn.CreateCommand();
            qa.CommandText = @"SELECT payload FROM payroll_policies 
                                   WHERE company_code=$1 AND (payload->>'isActive')='true' 
                                   ORDER BY created_at DESC LIMIT 1";
            qa.Parameters.AddWithValue(companyCode);
            var pj = (string?)await qa.ExecuteScalarAsync(ct);
            if (!string.IsNullOrEmpty(pj)) { policy = JsonDocument.Parse(pj).RootElement; policySource = "active"; }
            if (policy is null)
            {
                await using var ql = conn.CreateCommand();
                ql.CommandText = "SELECT payload FROM payroll_policies WHERE company_code=$1 ORDER BY created_at DESC LIMIT 1";
                ql.Parameters.AddWithValue(companyCode);
                var pj2 = (string?)await ql.ExecuteScalarAsync(ct);
                if (!string.IsNullOrEmpty(pj2)) { policy = JsonDocument.Parse(pj2).RootElement; policySource = "latest"; }
            }
        }
        if (debug) trace?.Add(new { step="policy.select", source=policySource });
        if (debug && policy is JsonElement policyEl)
        {
            string? policyText = null;
            try
            {
                if (policyEl.TryGetProperty("companyText", out var cte) && cte.ValueKind==JsonValueKind.String) policyText = cte.GetString();
                else if (policyEl.TryGetProperty("nlText", out var nte) && nte.ValueKind==JsonValueKind.String) policyText = nte.GetString();
                else if (policyEl.TryGetProperty("text", out var te) && te.ValueKind==JsonValueKind.String) policyText = te.GetString();
            } catch {}
            trace?.Add(new { step="input.policy", nl = policyText });
        }

        List<object> sheetOut = new();
        if (policy is JsonElement pBody)
        {
            string? companyTextInPolicy = null;
            try
            {
                if (pBody.TryGetProperty("companyText", out var cte) && cte.ValueKind==JsonValueKind.String) companyTextInPolicy = cte.GetString();
                else if (pBody.TryGetProperty("nlText", out var nte) && nte.ValueKind==JsonValueKind.String) companyTextInPolicy = nte.GetString();
                else if (pBody.TryGetProperty("text", out var te) && te.ValueKind==JsonValueKind.String) companyTextInPolicy = te.GetString();
            } catch {}
            var empText = emp.TryGetProperty("nlPayrollDescription", out var ne) && ne.ValueKind==JsonValueKind.String ? ne.GetString() : null;

            bool hasBaseEmp = !string.IsNullOrWhiteSpace(empText) && (
                empText.Contains("基本給", StringComparison.OrdinalIgnoreCase) || empText.Contains("基本给", StringComparison.OrdinalIgnoreCase) ||
                empText.Contains("月給", StringComparison.OrdinalIgnoreCase) || empText.Contains("月给", StringComparison.OrdinalIgnoreCase) ||
                empText.Contains("固定", StringComparison.OrdinalIgnoreCase) || empText.Contains("基本工资", StringComparison.OrdinalIgnoreCase) ||
                empText.Contains("月薪", StringComparison.OrdinalIgnoreCase)
            );
            bool hasBaseCompany = !string.IsNullOrWhiteSpace(companyTextInPolicy) && (
                companyTextInPolicy.Contains("基本給", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("基本给", StringComparison.OrdinalIgnoreCase) ||
                companyTextInPolicy.Contains("月給", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("月给", StringComparison.OrdinalIgnoreCase) ||
                companyTextInPolicy.Contains("固定", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("基本工资", StringComparison.OrdinalIgnoreCase)
            );
            bool wantsHealth = (!string.IsNullOrWhiteSpace(empText) && (empText.Contains("社会保険", StringComparison.OrdinalIgnoreCase) || empText.Contains("社保", StringComparison.OrdinalIgnoreCase) || empText.Contains("健康保険", StringComparison.OrdinalIgnoreCase)))
                               || (!string.IsNullOrWhiteSpace(companyTextInPolicy) && (companyTextInPolicy.Contains("社会保険", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("社保", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("健康保険", StringComparison.OrdinalIgnoreCase)));
            bool wantsPension = (!string.IsNullOrWhiteSpace(empText) && (empText.Contains("厚生年金", StringComparison.OrdinalIgnoreCase) || empText.Contains("年金", StringComparison.OrdinalIgnoreCase)))
                               || (!string.IsNullOrWhiteSpace(companyTextInPolicy) && (companyTextInPolicy.Contains("厚生年金", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("年金", StringComparison.OrdinalIgnoreCase)));
            bool wantsEmployment = (!string.IsNullOrWhiteSpace(empText) && (empText.Contains("雇用保険", StringComparison.OrdinalIgnoreCase) || empText.Contains("雇佣保险", StringComparison.OrdinalIgnoreCase)))
                               || (!string.IsNullOrWhiteSpace(companyTextInPolicy) && (companyTextInPolicy.Contains("雇用保険", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("雇佣保险", StringComparison.OrdinalIgnoreCase)));
            bool wantsWithholding = (!string.IsNullOrWhiteSpace(empText) && (empText.Contains("源泉", StringComparison.OrdinalIgnoreCase) || empText.Contains("月額表", StringComparison.OrdinalIgnoreCase) || empText.Contains("甲欄", StringComparison.OrdinalIgnoreCase)))
                               || (!string.IsNullOrWhiteSpace(companyTextInPolicy) && (companyTextInPolicy.Contains("源泉", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("月額表", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("甲欄", StringComparison.OrdinalIgnoreCase)));

            var compiled = new List<object>();
            if (hasBaseEmp || hasBaseCompany)
            {
                object fobj; if (nlBase>0) fobj = new { _const = nlBase }; else fobj = new { charRef = "employee.baseSalaryMonth" };
                compiled.Add(new { item = "BASE", type = "earning", formula = fobj });
            }
            bool commuteEmp = !string.IsNullOrWhiteSpace(empText) && (empText.Contains("通勤", StringComparison.OrdinalIgnoreCase) || empText.Contains("交通费", StringComparison.OrdinalIgnoreCase) || empText.Contains("交通費", StringComparison.OrdinalIgnoreCase));
            bool commuteCompany = !string.IsNullOrWhiteSpace(companyTextInPolicy) && companyTextInPolicy.Contains("通勤", StringComparison.OrdinalIgnoreCase);
            if (commuteEmp || commuteCompany)
            {
                object fobj; if (nlCommute>0) fobj = new { _const = nlCommute, cap = 30000 }; else fobj = new { charRef = "employee.commuteAllowance", cap = 30000 };
                compiled.Add(new { item = "COMMUTE", type = "earning", formula = fobj });
            }
            if (wantsHealth)
            {
                object baseExpr; if (nlBase>0) baseExpr = new { _const = nlBase }; else baseExpr = new { baseRef = "employee.healthBase" };
                compiled.Add(new { item = "HEALTH_INS", type = "deduction", formula = new { _base = baseExpr, rate = "policy.law.health.rate" } });
            }
            if (wantsPension)
            {
                object baseExpr; if (nlBase>0) baseExpr = new { _const = nlBase }; else baseExpr = new { baseRef = "employee.pensionBase" };
                compiled.Add(new { item = "PENSION", type = "deduction", formula = new { _base = baseExpr, rate = "policy.law.pension.rate" } });
            }
            if (wantsEmployment)
            {
                object baseExpr; if (nlBase>0 || nlCommute>0) baseExpr = new { sum = new object[]{ new { _const = nlBase }, new { _const = nlCommute } } }; else baseExpr = new { baseRef = "employee.salaryTotal" };
                compiled.Add(new { item = "EMP_INS", type = "deduction", formula = new { _base = baseExpr, rate = "policy.law.employment.rate" } });
            }

            if (wantsWithholding)
            {
                compiled.Add(new { item = "WHT", type = "deduction", formula = new { withholding = new { category = "monthly_ko" } } });
            }

            var arr = new System.Text.Json.Nodes.JsonArray(compiled.Select(o => System.Text.Json.JsonSerializer.SerializeToNode(o)!).ToArray());
            var rulesEl = JsonDocument.Parse(arr.ToJsonString()).RootElement;
            if (debug) trace?.Add(new { step="rules.compiled", items = compiled.Select(o => (string)o.GetType().GetProperty("item")!.GetValue(o)!).ToArray() });

            if (rulesEl.ValueKind==JsonValueKind.Array)
            {
                var monthDate = DateTime.TryParse(month ?? string.Empty, out var md) ? md : DateTime.Today;
                var whtBrackets = new List<(decimal min, decimal? max, decimal rate, decimal deduction, string version)>();
                try
                {
                    await using var connW = await ds.OpenConnectionAsync(ct);
                    await using var cmdW = connW.CreateCommand();
                    cmdW.CommandText = @"SELECT COALESCE(min_amount,0), max_amount, rate, deduction, COALESCE(version,'')
                                              FROM withholding_rates
                                              WHERE (company_code IS NULL OR company_code = $1)
                                                AND category = 'monthly_ko'
                                                AND effective_from <= $2
                                                AND (effective_to IS NULL OR effective_to >= $2)
                                              ORDER BY min_amount NULLS FIRST";
                    cmdW.Parameters.AddWithValue(companyCode);
                    cmdW.Parameters.AddWithValue(new DateTime(monthDate.Year, monthDate.Month, 1));
                    await using var rdW = await cmdW.ExecuteReaderAsync(ct);
                    while (await rdW.ReadAsync(ct))
                    {
                        var min = rdW.GetDecimal(0);
                        var max = rdW.IsDBNull(1) ? (decimal?)null : rdW.GetDecimal(1);
                        var rate = rdW.GetDecimal(2);
                        var deduction = rdW.GetDecimal(3);
                        var ver = rdW.GetString(4);
                        whtBrackets.Add((min, max, rate, deduction, ver));
                    }
                }
                catch {}
                decimal readFromPath(string path)
                {
                    try{
                        if (string.IsNullOrWhiteSpace(path)) return 0m;
                        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
                        JsonElement cur;
                        if (parts[0]=="employee") { cur = emp; }
                        else if (parts[0]=="policy") { cur = pBody; }
                        else return 0m;
                        for (int i=1;i<parts.Length;i++)
                        {
                            if (!cur.TryGetProperty(parts[i], out var next)) return 0m; cur = next;
                        }
                        if (cur.ValueKind==JsonValueKind.Number) { if (cur.TryGetDecimal(out var d)) return d; return Convert.ToDecimal(cur.GetDouble()); }
                        if (cur.ValueKind==JsonValueKind.String && decimal.TryParse(cur.GetString(), out var sd)) return sd;
                        return 0m;
                    }catch{ return 0m; }
                }
                decimal readEmployeeOverride(string key)
                {
                    key = key ?? string.Empty;
                    if (key.Equals("baseSalaryMonth", StringComparison.OrdinalIgnoreCase)) return nlBase>0? nlBase : 0m;
                    if (key.Equals("commuteAllowance", StringComparison.OrdinalIgnoreCase)) return nlCommute>0? nlCommute : 0m;
                    if (key.Equals("salaryTotal", StringComparison.OrdinalIgnoreCase)) return (nlBase>0? nlBase:0m) + (nlCommute>0? nlCommute:0m);
                    if (key.Equals("healthBase", StringComparison.OrdinalIgnoreCase)) return nlBase>0? nlBase : 0m;
                    if (key.Equals("pensionBase", StringComparison.OrdinalIgnoreCase)) return nlBase>0? nlBase : 0m;
                    return 0m;
                }
                (decimal value, decimal? rateUsed, decimal? baseUsed, string? lawVer, string? lawNote) evalFormula(JsonElement f)
                {
                    try{
                        if (f.TryGetProperty("_const", out var cst) && cst.ValueKind==JsonValueKind.Number)
                        {
                            return (cst.TryGetDecimal(out var cd)? cd : Convert.ToDecimal(cst.GetDouble()), null, null, null, null);
                        }
                        if (f.TryGetProperty("sum", out var sumVal) && sumVal.ValueKind==JsonValueKind.Array)
                        {
                            decimal acc = 0m; foreach (var node in sumVal.EnumerateArray()) { var sub = evalFormula(node); acc += sub.value; }
                            return (acc, null, null, null, null);
                        }
                        if (f.TryGetProperty("withholding", out var wht) && wht.ValueKind==JsonValueKind.Object)
                        {
                            decimal sumEarn = 0m, si = 0m, pens = 0m;
                            foreach (var it2 in sheetOut)
                            {
                                var code = (string?)it2.GetType().GetProperty("itemCode")!.GetValue(it2);
                                var amt = (decimal)it2.GetType().GetProperty("amount")!.GetValue(it2)!;
                                if (string.Equals(code, "BASE", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "COMMUTE", StringComparison.OrdinalIgnoreCase)) sumEarn += amt;
                                else if (string.Equals(code, "HEALTH_INS", StringComparison.OrdinalIgnoreCase)) si += amt;
                                else if (string.Equals(code, "PENSION", StringComparison.OrdinalIgnoreCase)) pens += amt;
                            }
                            var taxable = sumEarn - si - pens; if (taxable < 0) taxable = 0;
                            decimal rate = 0m, deduction = 0m; string? ver = null;
                            foreach (var brk in whtBrackets)
                            {
                                bool geMin = taxable >= brk.min;
                                bool ltMax = !brk.max.HasValue || taxable < brk.max.Value;
                                if (geMin && ltMax) { rate = brk.rate; deduction = brk.deduction; ver = brk.version; break; }
                            }
                            if (rate == 0m) return (0m, null, taxable, null, "withholding:not_found");
                            var tax = Math.Round(Math.Max(0m, taxable * rate - deduction), 0, MidpointRounding.AwayFromZero);
                            return (tax, rate, taxable, ver, "withholding");
                        }
                        if (f.TryGetProperty("charRef", out var cref) && cref.ValueKind==JsonValueKind.String)
                        {
                            var key = cref.GetString()!;
                            decimal val = 0m;
                            if (key.StartsWith("employee.", StringComparison.OrdinalIgnoreCase))
                            {
                                var last = key.Split('.').Last();
                                val = readEmployeeOverride(last);
                                if (val==0m) val = readFromPath(key);
                            } else {
                                val = readFromPath(key);
                            }
                            if (f.TryGetProperty("cap", out var cap2) && cap2.ValueKind==JsonValueKind.Number)
                            {
                                var capv = cap2.TryGetDecimal(out var cd2)? cd2 : Convert.ToDecimal(cap2.GetDouble());
                                if (val > capv) val = capv;
                            }
                            return (val, null, null, null, null);
                        }
                        if (f.TryGetProperty("ref", out var rref) && rref.ValueKind==JsonValueKind.String)
                        {
                            var key = rref.GetString()!;
                            decimal val = 0m;
                            if (key.StartsWith("employee.", StringComparison.OrdinalIgnoreCase))
                            {
                                var last = key.Split('.').Last();
                                val = readEmployeeOverride(last);
                                if (val==0m) val = readFromPath(key);
                            } else {
                                val = readFromPath(key);
                            }
                            if (f.TryGetProperty("cap", out var cap) && cap.ValueKind==JsonValueKind.Number)
                            {
                                var capv = cap.TryGetDecimal(out var cd)? cd : Convert.ToDecimal(cap.GetDouble());
                                if (val > capv) val = capv;
                            }
                            return (val, null, null, null, null);
                        }
                        decimal baseVal = 0m; decimal? baseMark = null;
                        if (f.TryGetProperty("_base", out var b) && b.ValueKind==JsonValueKind.Object)
                        {
                            var sub = evalFormula(b);
                            baseVal = sub.value; baseMark = sub.value;
                        }
                        if (baseVal==0m && f.TryGetProperty("baseRef", out var br) && br.ValueKind==JsonValueKind.String) baseVal = readFromPath(br.GetString()!);
                        if (baseVal==0m && f.TryGetProperty("baseRef", out var br2) && br2.ValueKind==JsonValueKind.String)
                        {
                            var key = br2.GetString()!;
                            if (key.StartsWith("employee.", StringComparison.OrdinalIgnoreCase))
                            {
                                var last = key.Split('.').Last();
                                var over = readEmployeeOverride(last);
                                if (over>0) { baseVal = over; baseMark = over; }
                            }
                        }
                        if (baseVal==0m && f.TryGetProperty("sum", out var sumEl) && sumEl.ValueKind==JsonValueKind.Array)
                        {
                            decimal acc = 0m; foreach (var node in sumEl.EnumerateArray()) { var sub = evalFormula(node); acc += sub.value; }
                            baseVal = acc; baseMark = acc;
                        }
                        decimal rateVal = 0m; decimal? rateMark = null; string? lawVer = null; string? lawNote = null;
                        if (f.TryGetProperty("rate", out var rt))
                        {
                            if (rt.ValueKind==JsonValueKind.Number) { rateVal = rt.TryGetDecimal(out var rd)? rd : Convert.ToDecimal(rt.GetDouble()); rateMark = rateVal; }
                            else if (rt.ValueKind==JsonValueKind.String)
                            {
                                var key = rt.GetString()!;
                                if (string.Equals(key, "policy.law.health.rate", StringComparison.OrdinalIgnoreCase)) { var t = law.GetHealthRate(emp, pBody, monthDate); rateVal = t.rate; rateMark = t.rate; lawVer=t.version; lawNote=$"health:{t.note}"; }
                                else if (string.Equals(key, "policy.law.pension.rate", StringComparison.OrdinalIgnoreCase)) { var t = law.GetPensionRate(emp, pBody, monthDate); rateVal = t.rate; rateMark = t.rate; lawVer=t.version; lawNote=$"pension:{t.note}"; }
                                else if (string.Equals(key, "policy.law.employment.rate", StringComparison.OrdinalIgnoreCase)) { var t = law.GetEmploymentRate(emp, pBody, monthDate); rateVal = t.rate; rateMark = t.rate; lawVer=t.version; lawNote=$"employment:{t.note}"; if (debug) trace?.Add(new { step="employment.rate", industry = (pBody.TryGetProperty("law", out var lw) && lw.TryGetProperty("employmentIndustry", out var ei) ? ei.GetString() : null), resolved = t.version }); }
                                else { rateVal = readFromPath(key); rateMark = rateVal; }
                            }
                        }
                        var res = baseVal * rateVal;
                        if (f.TryGetProperty("rounding", out var rnd) && rnd.ValueKind==JsonValueKind.Object)
                        {
                            var method = rnd.TryGetProperty("method", out var me) ? me.GetString() : "round";
                            var precision = rnd.TryGetProperty("precision", out var pr) && pr.ValueKind==JsonValueKind.Number ? pr.GetInt32() : 0;
                            if (method=="round") res = Math.Round(res, precision, MidpointRounding.AwayFromZero);
                            else if (method=="floor") res = Math.Floor(res);
                            else if (method=="ceil") res = Math.Ceiling(res);
                        }
                        return (res, rateMark, baseMark ?? baseVal, lawVer, lawNote);
                    }catch{ return (0m, null, null, null, null); }
                }

                foreach (var rule in rulesEl.EnumerateArray())
                {
                    var itemCode = rule.TryGetProperty("item", out var it) ? it.GetString() : (rule.TryGetProperty("itemCode", out var ic)? ic.GetString(): null);
                    if (string.IsNullOrWhiteSpace(itemCode)) continue;
                    decimal amount = 0m; decimal? rateMarkOut = null; decimal? baseMarkOut = null; string? lawVerOut = null; string? lawNoteOut = null;
                    if (rule.TryGetProperty("formula", out var form) && form.ValueKind==JsonValueKind.Object)
                    {
                        var r = evalFormula(form); amount = r.value; rateMarkOut = r.rateUsed; baseMarkOut = r.baseUsed; lawVerOut = r.lawVer; lawNoteOut = r.lawNote;
                    }
                    if (amount != 0m)
                    {
                        var meta = new { formula = "policy", rate = rateMarkOut, @base = baseMarkOut, lawVersion = lawVerOut, lawNote = lawNoteOut };
                        sheetOut.Add(new { itemCode = itemCode, amount = amount, meta });
                        if (debug) trace?.Add(new { step="rule.eval", item=itemCode, amount, @base = baseMarkOut, rate = rateMarkOut, lawVersion = lawVerOut, lawNote = lawNoteOut });
                    }
                    else if (debug) { trace?.Add(new { step="rule.eval", item=itemCode, amount=0, note="formula evaluated to 0" }); }
                }
                if (debug) trace?.Add(new { step="rules.done", executed = sheetOut.Count > 0, count = sheetOut.Count });
            }
        }

        var journal = new List<object>();
        decimal amtBase = sheetOut.Where(x =>
        {
            var code = (string?)x.GetType().GetProperty("itemCode")!.GetValue(x);
            return string.Equals(code, "BASE", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "COMMUTE", StringComparison.OrdinalIgnoreCase);
        }).Sum(x => (decimal)x.GetType().GetProperty("amount")!.GetValue(x)!);
        decimal amtHealth = sheetOut.Where(x => (string?)x.GetType().GetProperty("itemCode")!.GetValue(x) == "HEALTH_INS").Sum(x => (decimal)x.GetType().GetProperty("amount")!.GetValue(x)!);
        decimal amtPension = sheetOut.Where(x => (string?)x.GetType().GetProperty("itemCode")!.GetValue(x) == "PENSION").Sum(x => (decimal)x.GetType().GetProperty("amount")!.GetValue(x)!);
        decimal amtEmp = sheetOut.Where(x => (string?)x.GetType().GetProperty("itemCode")!.GetValue(x) == "EMP_INS").Sum(x => (decimal)x.GetType().GetProperty("amount")!.GetValue(x)!);
        decimal amtWht = sheetOut.Where(x => (string?)x.GetType().GetProperty("itemCode")!.GetValue(x) == "WHT").Sum(x => (decimal)x.GetType().GetProperty("amount")!.GetValue(x)!);
        int lineNo = 1;
        if (amtBase > 0) journal.Add(new { lineNo = lineNo++, accountCode = "6400", drcr = "DR", amount = amtBase });
        if (amtHealth > 0) journal.Add(new { lineNo = lineNo++, accountCode = "2210", drcr = "DR", amount = amtHealth });
        if (amtPension > 0) journal.Add(new { lineNo = lineNo++, accountCode = "2211", drcr = "DR", amount = amtPension });
        if (amtEmp > 0) journal.Add(new { lineNo = lineNo++, accountCode = "2212", drcr = "DR", amount = amtEmp });
        if (amtWht > 0) journal.Add(new { lineNo = lineNo++, accountCode = "2220", drcr = "DR", amount = amtWht });
        decimal totalDr = journal.Sum(l => (decimal)l.GetType().GetProperty("amount")!.GetValue(l)!);
        if (totalDr > 0) journal.Add(new { lineNo = lineNo++, accountCode = "2200", drcr = "CR", amount = totalDr });

        var codes = journal.Select(l => (string)l.GetType().GetProperty("accountCode")!.GetValue(l)!).Distinct().ToArray();
        var accountNameByCode = new Dictionary<string, string?>();
        try
        {
            foreach (var code in codes)
            {
                await using var qa = conn.CreateCommand();
                qa.CommandText = "SELECT name FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1";
                qa.Parameters.AddWithValue(companyCode); qa.Parameters.AddWithValue(code);
                var name = (string?)await qa.ExecuteScalarAsync(ct);
                accountNameByCode[code] = name;
            }
        } catch {}

        var missing = codes.Where(code =>
        {
            string? nm;
            return !accountNameByCode.TryGetValue(code, out nm) || string.IsNullOrWhiteSpace(nm);
        }).ToArray();
        if (missing.Length > 0)
        {
            var msg = "会计科目不存在: " + string.Join(",", missing);
            if (debug)
                throw new PayrollExecutionException(StatusCodes.Status400BadRequest, new { error = msg, missingAccounts = missing, hint = "请在‘会计科目’主数据中维护，或调用 /admin/accounts/seed/payroll 初始化。" }, msg);
            throw new PayrollExecutionException(StatusCodes.Status400BadRequest, new { error = msg }, msg);
        }

        var enriched = journal.Select(l =>
        {
            var ln = (int)l.GetType().GetProperty("lineNo")!.GetValue(l)!;
            var ac = (string)l.GetType().GetProperty("accountCode")!.GetValue(l)!;
            var dr = (string)l.GetType().GetProperty("drcr")!.GetValue(l)!;
            var am = (decimal)l.GetType().GetProperty("amount")!.GetValue(l)!;
            accountNameByCode.TryGetValue(ac, out var an);
            return (object)new { lineNo = ln, accountCode = ac, accountName = an, drcr = dr, amount = am, employeeCode = employeeCodeOut, departmentCode = departmentCodeOut };
        }).ToList();

        return new PayrollExecutionResult(sheetOut, enriched, trace);
    }
}


