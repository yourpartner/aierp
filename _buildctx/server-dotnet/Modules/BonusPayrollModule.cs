using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

/// <summary>
/// 賞与計算モジュール
/// - 賞与に対する源泉徴収税額は「賞与に対する源泉徴収税額の算出率の表」を使用
/// - 社会保険料は月額と同じ料率（ただし賞与特有の上限あり）
/// - 雇用保険も同率適用
/// </summary>
public static class BonusPayrollModule
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    internal sealed class BonusRunRequest
    {
        public List<BonusEmployeeInput> Employees { get; set; } = new();
        public string Month { get; set; } = "";
        public string BonusType { get; set; } = "summer";
        public Guid? PolicyId { get; set; }
        public bool Debug { get; set; }
    }

    internal sealed class BonusEmployeeInput
    {
        public Guid EmployeeId { get; set; }
        public decimal BonusAmount { get; set; }
    }

    public sealed class BonusCalcEntry
    {
        public Guid EmployeeId { get; set; }
        public string? EmployeeCode { get; set; }
        public string? EmployeeName { get; set; }
        public string? DepartmentCode { get; set; }
        public string? DepartmentName { get; set; }
        public decimal BonusAmount { get; set; }
        public decimal HealthIns { get; set; }
        public decimal CareIns { get; set; }
        public decimal Pension { get; set; }
        public decimal EmpIns { get; set; }
        public decimal WithholdingTax { get; set; }
        public decimal NetAmount { get; set; }
        public decimal TaxRate { get; set; }
        public string? TaxNote { get; set; }
        public List<JsonObject>? PayrollSheet { get; set; }
        public List<JsonObject>? AccountingDraft { get; set; }
    }

    public static void MapEndpoints(WebApplication app)
    {
        app.MapPost("/payroll/bonus/run", async (HttpRequest req, NpgsqlDataSource ds, LawDatasetService law, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString();

            BonusRunRequest? body;
            try { body = await JsonSerializer.DeserializeAsync<BonusRunRequest>(req.Body, JsonOpts, ct); }
            catch { return Results.BadRequest(new { error = "Invalid request body" }); }

            if (body is null || body.Employees.Count == 0 || string.IsNullOrWhiteSpace(body.Month))
                return Results.BadRequest(new { error = "employees and month required" });

            try
            {
                // ポリシー読込（社保料率の都道府県・業種判定に使用）
                JsonElement? policyBody = null;
                if (body.PolicyId.HasValue)
                {
                    policyBody = await LoadPolicyAsync(ds, companyCode, body.PolicyId.Value, ct);
                }
                policyBody ??= await LoadActivePolicyAsync(ds, companyCode, ct);

                var entries = new List<BonusCalcEntry>();
                foreach (var input in body.Employees)
                {
                    var entry = await CalculateBonusAsync(ds, law, companyCode, input.EmployeeId, input.BonusAmount, body.Month, policyBody, ct);
                    entries.Add(entry);
                }
                return Results.Ok(new { month = body.Month, bonusType = body.BonusType, entries });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();

        app.MapPost("/payroll/bonus/save", async (HttpRequest req, PayrollService payroll, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var body = await JsonSerializer.DeserializeAsync<PayrollService.PayrollManualSaveRequest>(req.Body, JsonOpts, ct);
            if (body is null || body.Entries.Count == 0 || string.IsNullOrWhiteSpace(body.Month))
                return Results.BadRequest(new { error = "month and entries required" });

            body.RunType = "bonus";
            var user = Auth.GetUserCtx(req);
            try
            {
                var result = await payroll.SaveManualAsync(cc.ToString(), user, body, ct);
                return Results.Ok(result);
            }
            catch (PayrollExecutionException ex)
            {
                return Results.Json(ex.Payload, statusCode: ex.StatusCode);
            }
        }).RequireAuthorization();
    }

    private static async Task<JsonElement?> LoadPolicyAsync(NpgsqlDataSource ds, string companyCode, Guid policyId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM payroll_policies WHERE company_code=$1 AND id=$2 LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(policyId);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrEmpty(json)) return null;
        return JsonDocument.Parse(json).RootElement;
    }

    private static async Task<JsonElement?> LoadActivePolicyAsync(NpgsqlDataSource ds, string companyCode, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM payroll_policies WHERE company_code=$1 AND is_active=true ORDER BY updated_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrEmpty(json)) return null;
        return JsonDocument.Parse(json).RootElement;
    }

    /// <summary>
    /// 賞与計算のコアロジック
    ///
    /// 日本の賞与源泉徴収の仕組み:
    /// 1. 前月の給与（社会保険料等控除後）を求める
    /// 2.「賞与に対する源泉徴収税額の算出率の表」から税率を求める
    /// 3. 賞与から社会保険料を控除した額 × 税率 = 源泉徴収税額
    /// </summary>
    internal static async Task<BonusCalcEntry> CalculateBonusAsync(
        NpgsqlDataSource ds, LawDatasetService law,
        string companyCode, Guid employeeId, decimal bonusAmount, string month,
        JsonElement? policyBody, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);

        // 従業員情報の読込
        string? empJson = null, empCode = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT payload, employee_code FROM employees WHERE company_code=$1 AND id=$2 LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(employeeId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                empJson = rd.IsDBNull(0) ? null : rd.GetString(0);
                empCode = rd.IsDBNull(1) ? null : rd.GetString(1);
            }
        }
        if (string.IsNullOrEmpty(empJson))
            throw new Exception($"Employee {employeeId} not found");

        using var empDoc = JsonDocument.Parse(empJson);
        var emp = empDoc.RootElement;

        var empName = emp.TryGetProperty("nameKanji", out var nk) ? nk.GetString() :
                      emp.TryGetProperty("name", out var nn) ? nn.GetString() : null;
        var deptCode = emp.TryGetProperty("departmentCode", out var dc) ? dc.GetString() : null;
        var deptName = emp.TryGetProperty("primaryDepartmentName", out var dn) ? dn.GetString() :
                       emp.TryGetProperty("departmentName", out var dn2) ? dn2.GetString() : null;

        // 月日を解析
        var monthDate = DateTime.ParseExact(month + "-01", "yyyy-MM-dd", null);

        // 社会保険料の計算（月額と同じ料率を賞与額に適用）
        // 賞与の社保上限: 厚生年金は1回あたり150万円上限
        var insuranceBase = bonusAmount;
        var pensionBase = Math.Min(insuranceBase, 1_500_000m);

        // ポリシーから料率取得（ポリシーがない場合は空の JsonElement を使用）
        var policyEl = policyBody ?? JsonDocument.Parse("{}").RootElement;

        var healthResult = law.GetHealthRate(emp, policyEl, monthDate, insuranceBase);
        var careResult = law.GetCareInsuranceRate(emp, policyEl, monthDate, insuranceBase);
        var pensionResult = law.GetPensionRate(emp, policyEl, monthDate, pensionBase);
        var empInsResult = law.GetEmploymentRate(emp, policyEl, monthDate);

        // 標準報酬月額のオーバーライドがある場合はそれを使用（賞与では使わない、直接賞与額に料率適用）
        var healthIns = Math.Round(insuranceBase * healthResult.rate);
        var careIns = Math.Round(insuranceBase * careResult.rate);
        var pension = Math.Round(pensionBase * pensionResult.rate);
        var empIns = Math.Round(bonusAmount * empInsResult.rate);
        var socialTotal = healthIns + careIns + pension + empIns;

        // 前月の課税給与（社保控除後）を取得
        var prevMonthTaxable = await GetPreviousMonthTaxableAsync(conn, companyCode, employeeId, month, ct);

        // 扶養人数の取得
        int dependents = CountDependents(emp, month);

        // 賞与に対する源泉徴収税率の算出
        var (taxRate, taxNote) = await LookupBonusTaxRateAsync(conn, prevMonthTaxable, dependents, month, ct);

        // 源泉徴収税額 = (賞与 - 社会保険料) × 税率
        var taxableBonus = bonusAmount - socialTotal;
        if (taxableBonus < 0) taxableBonus = 0;
        var wht = Math.Round(taxableBonus * taxRate);
        var netAmount = bonusAmount - socialTotal - wht;

        var sheet = new List<JsonObject>
        {
            MakeSheetItem("BONUS", "賞与", bonusAmount, "earning"),
            MakeSheetItem("HEALTH_INS", "健康保険", healthIns, "deduction"),
            MakeSheetItem("CARE_INS", "介護保険", careIns, "deduction"),
            MakeSheetItem("PENSION", "厚生年金", pension, "deduction"),
            MakeSheetItem("EMP_INS", "雇用保険", empIns, "deduction"),
            MakeSheetItem("WHT", "源泉徴収税", wht, "deduction"),
        };

        var draft = BuildBonusAccountingDraft(bonusAmount, healthIns, careIns, pension, empIns, wht, empCode, deptCode);

        return new BonusCalcEntry
        {
            EmployeeId = employeeId,
            EmployeeCode = empCode,
            EmployeeName = empName,
            DepartmentCode = deptCode,
            DepartmentName = deptName,
            BonusAmount = bonusAmount,
            HealthIns = healthIns,
            CareIns = careIns,
            Pension = pension,
            EmpIns = empIns,
            WithholdingTax = wht,
            NetAmount = netAmount,
            TaxRate = taxRate,
            TaxNote = taxNote,
            PayrollSheet = sheet,
            AccountingDraft = draft,
        };
    }

    private static async Task<decimal> GetPreviousMonthTaxableAsync(
        NpgsqlConnection conn, string companyCode, Guid employeeId, string month, CancellationToken ct)
    {
        var parts = month.Split('-');
        var year = int.Parse(parts[0]);
        var mon = int.Parse(parts[1]);
        if (mon == 1) { year--; mon = 12; } else { mon--; }
        var prevMonth = $"{year:D4}-{mon:D2}";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT pre.payroll_sheet
            FROM payroll_run_entries pre
            JOIN payroll_runs pr ON pr.id = pre.run_id
            WHERE pr.company_code = $1 AND pre.employee_id = $2 AND pr.period_month = $3
              AND pr.run_type IN ('manual', 'auto')
            ORDER BY pr.updated_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(employeeId);
        cmd.Parameters.AddWithValue(prevMonth);

        var json = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrEmpty(json)) return 0m;

        using var doc = JsonDocument.Parse(json);
        decimal sumEarn = 0, sumSi = 0;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var code = item.TryGetProperty("itemCode", out var ic) ? ic.GetString() : null;
            var amt = item.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDecimal() : 0m;
            if (code is "BASE" or "COMMUTE") sumEarn += amt;
            else if (code is "HEALTH_INS" or "CARE_INS" or "PENSION" or "EMP_INS") sumSi += amt;
        }
        return Math.Max(0, sumEarn - sumSi);
    }

    private static async Task<(decimal Rate, string Note)> LookupBonusTaxRateAsync(
        NpgsqlConnection conn, decimal prevMonthTaxable, int dependents, string month, CancellationToken ct)
    {
        if (dependents > 7) dependents = 7;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT rate FROM withholding_table
            WHERE category = 'bonus_rate'
              AND dependents = $1
              AND min_amount <= $2
              AND (max_amount IS NULL OR max_amount > $2)
              AND (effective_from IS NULL OR effective_from <= $3::date)
              AND (effective_to IS NULL OR effective_to >= $3::date)
            ORDER BY min_amount DESC LIMIT 1";
        cmd.Parameters.AddWithValue(dependents);
        cmd.Parameters.AddWithValue(prevMonthTaxable);
        cmd.Parameters.AddWithValue(month + "-01");
        var result = await cmd.ExecuteScalarAsync(ct);

        if (result is decimal rate)
            return (rate, $"bonus_rate:dep={dependents}:prev={prevMonthTaxable:N0}");

        // フォールバック: 令和6年の概算税率テーブル
        var fallbackRate = EstimateBonusTaxRate(prevMonthTaxable, dependents);
        return (fallbackRate, $"bonus_rate:fallback:dep={dependents}:prev={prevMonthTaxable:N0}");
    }

    /// <summary>
    /// 令和6年(2024) 賞与に対する源泉徴収税額の算出率の表（甲欄）概算
    /// </summary>
    private static decimal EstimateBonusTaxRate(decimal prevMonthTaxable, int dependents)
    {
        decimal baseRate;
        if (prevMonthTaxable < 68_000m) baseRate = 0.0m;
        else if (prevMonthTaxable < 79_000m) baseRate = 0.02042m;
        else if (prevMonthTaxable < 252_000m) baseRate = 0.04084m;
        else if (prevMonthTaxable < 300_000m) baseRate = 0.06126m;
        else if (prevMonthTaxable < 334_000m) baseRate = 0.08168m;
        else if (prevMonthTaxable < 363_000m) baseRate = 0.10210m;
        else if (prevMonthTaxable < 395_000m) baseRate = 0.12252m;
        else if (prevMonthTaxable < 427_000m) baseRate = 0.14294m;
        else if (prevMonthTaxable < 550_000m) baseRate = 0.16336m;
        else if (prevMonthTaxable < 700_000m) baseRate = 0.18378m;
        else if (prevMonthTaxable < 900_000m) baseRate = 0.20420m;
        else if (prevMonthTaxable < 1_200_000m) baseRate = 0.24504m;
        else if (prevMonthTaxable < 1_700_000m) baseRate = 0.28588m;
        else if (prevMonthTaxable < 2_500_000m) baseRate = 0.32672m;
        else if (prevMonthTaxable < 5_000_000m) baseRate = 0.36756m;
        else baseRate = 0.40840m;

        var adjustment = dependents * 0.02042m;
        return Math.Max(0m, baseRate - adjustment);
    }

    private static int CountDependents(JsonElement emp, string month)
    {
        if (emp.TryGetProperty("dependents", out var depArr) && depArr.ValueKind == JsonValueKind.Array)
        {
            var parts = month.Split('-');
            var cutoffDate = new DateTime(int.Parse(parts[0]), 12, 31);
            int count = 0;
            foreach (var dep in depArr.EnumerateArray())
            {
                if (dep.ValueKind != JsonValueKind.Object) continue;
                var nameKana = dep.TryGetProperty("nameKana", out var nk) ? nk.GetString() : null;
                var nameKanji = dep.TryGetProperty("nameKanji", out var nj) ? nj.GetString() : null;
                var birthDate = dep.TryGetProperty("birthDate", out var bd) ? bd.GetString() : null;
                if (string.IsNullOrWhiteSpace(nameKana) && string.IsNullOrWhiteSpace(nameKanji)) continue;
                if (string.IsNullOrWhiteSpace(birthDate)) continue;
                if (!DateTime.TryParse(birthDate, out var bdDate)) continue;
                var age = cutoffDate.Year - bdDate.Year;
                if (bdDate > cutoffDate.AddYears(-age)) age--;
                if (age < 16) continue;
                count++;
            }
            return count;
        }
        if (emp.TryGetProperty("dependents", out var depNum) && depNum.ValueKind == JsonValueKind.Number)
            return depNum.GetInt32();
        return 0;
    }

    private static JsonObject MakeSheetItem(string code, string name, decimal amount, string kind) => new()
    {
        ["itemCode"] = code,
        ["itemName"] = name,
        ["amount"] = amount,
        ["kind"] = kind,
    };

    private static List<JsonObject> BuildBonusAccountingDraft(
        decimal bonusAmt, decimal health, decimal care, decimal pension, decimal empIns, decimal wht,
        string? empCode, string? deptCode)
    {
        var lines = new List<JsonObject>();
        int lineNo = 1;

        lines.Add(new JsonObject
        {
            ["lineNo"] = lineNo++, ["accountCode"] = "833", ["accountName"] = "賞与手当",
            ["drcr"] = "DR", ["amount"] = bonusAmt,
            ["employeeCode"] = empCode, ["departmentCode"] = deptCode,
        });
        lines.Add(new JsonObject
        {
            ["lineNo"] = lineNo++, ["accountCode"] = "315", ["accountName"] = "未払費用",
            ["drcr"] = "CR", ["amount"] = bonusAmt,
        });

        var totalDeduction = health + care + pension + empIns + wht;
        lines.Add(new JsonObject
        {
            ["lineNo"] = lineNo++, ["accountCode"] = "315", ["accountName"] = "未払費用",
            ["drcr"] = "DR", ["amount"] = totalDeduction,
        });

        if (health + care > 0)
            lines.Add(new JsonObject { ["lineNo"] = lineNo++, ["accountCode"] = "3181", ["accountName"] = "社会保険預り金", ["drcr"] = "CR", ["amount"] = health + care });
        if (pension > 0)
            lines.Add(new JsonObject { ["lineNo"] = lineNo++, ["accountCode"] = "3182", ["accountName"] = "厚生年金預り金", ["drcr"] = "CR", ["amount"] = pension });
        if (empIns > 0)
            lines.Add(new JsonObject { ["lineNo"] = lineNo++, ["accountCode"] = "3183", ["accountName"] = "雇用保険預り金", ["drcr"] = "CR", ["amount"] = empIns });
        if (wht > 0)
            lines.Add(new JsonObject { ["lineNo"] = lineNo++, ["accountCode"] = "3184", ["accountName"] = "源泉所得税預り金", ["drcr"] = "CR", ["amount"] = wht });

        return lines;
    }
}
