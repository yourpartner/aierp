using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

/// <summary>
/// 年末調整モジュール
///
/// 年末調整の計算フロー:
/// 1. 年間給与収入を集計（1月〜12月の月額給与 + 賞与）
/// 2. 給与所得控除を適用（収入に応じた控除額）
/// 3. 所得控除を適用:
///    - 基礎控除 48万円（所得2,400万円以下）
///    - 配偶者控除・配偶者特別控除
///    - 扶養控除（一般16歳〜/特定19〜22歳/老人70歳〜）
///    - 社会保険料控除（年間社保料の全額）
///    - 生命保険料控除（最大12万円）
///    - 地震保険料控除（最大5万円）
///    - 住宅借入金等特別控除（住宅ローン減税）
/// 4. 課税所得に税率を適用して年税額を計算
/// 5. 年間源泉徴収税額と比較
/// 6. 差額を還付（過払い）または徴収（不足）
/// </summary>
public static class YearEndAdjustmentModule
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    internal sealed class YearEndRunRequest
    {
        public int Year { get; set; }
        public List<YearEndEmployeeInput> Employees { get; set; } = new();
    }

    internal sealed class YearEndEmployeeInput
    {
        public Guid EmployeeId { get; set; }
        // 各種控除申告（従業員から収集）
        public decimal LifeInsurancePremium { get; set; }         // 一般生命保険料（支払額）
        public decimal MedicalInsurancePremium { get; set; }      // 介護医療保険料（支払額）
        public decimal PensionInsurancePremium { get; set; }      // 個人年金保険料（支払額）
        public decimal EarthquakeInsurancePremium { get; set; }   // 地震保険料
        public decimal HousingLoanBalance { get; set; }           // 住宅ローン年末残高
        public decimal HousingLoanDeductionRate { get; set; }     // 住宅ローン控除率（通常0.7%=0.007）
        public bool IsFirstYearHousingLoan { get; set; }          // 住宅ローン1年目（税務署で確定申告が必要）
        public decimal OtherDeductions { get; set; }              // その他の所得控除
        public bool HasSpouse { get; set; }                       // 配偶者あり
        public decimal SpouseIncome { get; set; }                 // 配偶者の年間所得
    }

    public sealed class YearEndResult
    {
        public Guid EmployeeId { get; set; }
        public string? EmployeeCode { get; set; }
        public string? EmployeeName { get; set; }

        // 集計データ
        public decimal AnnualGrossIncome { get; set; }        // 年間給与収入合計
        public decimal AnnualBonusIncome { get; set; }        // 年間賞与収入合計
        public decimal TotalIncome { get; set; }              // 給与収入合計

        // 控除
        public decimal EmploymentIncomeDeduction { get; set; } // 給与所得控除
        public decimal GrossIncome { get; set; }               // 給与所得金額

        public decimal BasicDeduction { get; set; }           // 基礎控除
        public decimal SpouseDeduction { get; set; }          // 配偶者(特別)控除
        public decimal DependentDeduction { get; set; }       // 扶養控除
        public decimal SocialInsuranceDeduction { get; set; } // 社会保険料控除
        public decimal LifeInsuranceDeduction { get; set; }   // 生命保険料控除
        public decimal EarthquakeDeduction { get; set; }      // 地震保険料控除
        public decimal TotalDeductions { get; set; }          // 所得控除合計

        public decimal TaxableIncome { get; set; }            // 課税所得金額
        public decimal CalculatedTax { get; set; }            // 算出年税額
        public decimal HousingLoanCredit { get; set; }        // 住宅ローン控除（税額控除）
        public decimal FinalAnnualTax { get; set; }           // 年調年税額（復興特別所得税含む）

        public decimal TotalWithheld { get; set; }            // 年間源泉徴収税額（既徴収）
        public decimal Adjustment { get; set; }               // 差引過不足額（マイナス=還付、プラス=徴収）
        public string AdjustmentType { get; set; } = "";      // "refund" / "collect"

        public List<JsonObject>? PayrollSheet { get; set; }
        public List<JsonObject>? AccountingDraft { get; set; }
        public List<string>? Warnings { get; set; }
    }

    public static void MapEndpoints(WebApplication app)
    {
        // POST /payroll/year-end/run — 年末調整プレビュー
        app.MapPost("/payroll/year-end/run", async (HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            YearEndRunRequest? body;
            try { body = await JsonSerializer.DeserializeAsync<YearEndRunRequest>(req.Body, JsonOpts, ct); }
            catch { return Results.BadRequest(new { error = "Invalid request body" }); }

            if (body is null || body.Employees.Count == 0 || body.Year < 2020)
                return Results.BadRequest(new { error = "year and employees required" });

            try
            {
                var results = new List<YearEndResult>();
                foreach (var input in body.Employees)
                {
                    var result = await CalculateYearEndAsync(ds, cc.ToString(), body.Year, input, ct);
                    results.Add(result);
                }
                return Results.Ok(new { year = body.Year, entries = results });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // POST /payroll/year-end/save — 年末調整結果の保存
        app.MapPost("/payroll/year-end/save", async (HttpRequest req, PayrollService payroll, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var body = await JsonSerializer.DeserializeAsync<PayrollService.PayrollManualSaveRequest>(req.Body, JsonOpts, ct);
            if (body is null || body.Entries.Count == 0 || string.IsNullOrWhiteSpace(body.Month))
                return Results.BadRequest(new { error = "month and entries required" });

            body.RunType = "year_end_adjustment";
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

    private static async Task<YearEndResult> CalculateYearEndAsync(
        NpgsqlDataSource ds, string companyCode, int year, YearEndEmployeeInput input, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);

        // 従業員情報の読込
        string? empJson = null, empCode = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT payload, employee_code FROM employees WHERE company_code=$1 AND id=$2 LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(input.EmployeeId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                empJson = rd.IsDBNull(0) ? null : rd.GetString(0);
                empCode = rd.IsDBNull(1) ? null : rd.GetString(1);
            }
        }
        if (string.IsNullOrEmpty(empJson))
            throw new Exception($"Employee {input.EmployeeId} not found");

        using var empDoc = JsonDocument.Parse(empJson);
        var emp = empDoc.RootElement;
        var empName = emp.TryGetProperty("nameKanji", out var nk) ? nk.GetString() :
                      emp.TryGetProperty("name", out var nn) ? nn.GetString() : null;

        var warnings = new List<string>();

        // 1月〜12月の給与データを集計
        var (annualGross, annualBonus, annualSi, annualWht) = await AggregateAnnualPayrollAsync(conn, companyCode, input.EmployeeId, year, ct);

        if (annualGross == 0 && annualBonus == 0)
            warnings.Add("対象年度の給与データがありません");

        var totalIncome = annualGross + annualBonus;

        // Step 1: 給与所得控除
        var employmentDeduction = CalcEmploymentIncomeDeduction(totalIncome);
        var grossIncome = totalIncome - employmentDeduction;
        if (grossIncome < 0) grossIncome = 0;

        // Step 2: 各種所得控除
        var basicDeduction = CalcBasicDeduction(grossIncome);

        // 配偶者控除
        var spouseDeduction = 0m;
        if (input.HasSpouse)
            spouseDeduction = CalcSpouseDeduction(grossIncome, input.SpouseIncome);

        // 扶養控除
        var dependentDeduction = CalcDependentDeduction(emp, year);

        // 社会保険料控除（年間の社保料全額が控除対象）
        var socialInsDeduction = annualSi;

        // 生命保険料控除
        var lifeInsDeduction = CalcLifeInsuranceDeduction(
            input.LifeInsurancePremium, input.MedicalInsurancePremium, input.PensionInsurancePremium);

        // 地震保険料控除
        var earthquakeDeduction = Math.Min(input.EarthquakeInsurancePremium, 50_000m);

        // その他控除
        var otherDeduction = input.OtherDeductions;

        var totalDeductions = basicDeduction + spouseDeduction + dependentDeduction
            + socialInsDeduction + lifeInsDeduction + earthquakeDeduction + otherDeduction;

        // Step 3: 課税所得
        var taxableIncome = grossIncome - totalDeductions;
        if (taxableIncome < 0) taxableIncome = 0;
        // 千円未満切り捨て
        taxableIncome = Math.Floor(taxableIncome / 1000m) * 1000m;

        // Step 4: 年税額計算（所得税の速算表）
        var calcTax = CalcIncomeTax(taxableIncome);

        // Step 5: 住宅ローン控除（税額控除）
        var housingLoanCredit = 0m;
        if (input.HousingLoanBalance > 0 && !input.IsFirstYearHousingLoan)
        {
            housingLoanCredit = Math.Floor(input.HousingLoanBalance * input.HousingLoanDeductionRate);
            // 控除限度額（一般住宅の場合 年14万円 ※令和4年以降入居分）
            housingLoanCredit = Math.Min(housingLoanCredit, 140_000m);
        }
        if (input.IsFirstYearHousingLoan)
            warnings.Add("住宅ローン控除1年目は確定申告が必要です（年末調整では控除できません）");

        // 算出税額 - 住宅ローン控除
        var taxAfterCredit = calcTax - housingLoanCredit;
        if (taxAfterCredit < 0) taxAfterCredit = 0;

        // 復興特別所得税（2.1%）
        var finalTax = Math.Floor(taxAfterCredit * 1.021m);

        // Step 6: 過不足額
        var adjustment = finalTax - annualWht;
        var adjustmentType = adjustment < 0 ? "refund" : adjustment > 0 ? "collect" : "none";

        // PayrollSheet 構築
        var sheet = new List<JsonObject>();
        if (adjustment < 0)
        {
            sheet.Add(new JsonObject
            {
                ["itemCode"] = "YEA_REFUND",
                ["itemName"] = "年末調整還付",
                ["amount"] = Math.Abs(adjustment),
                ["kind"] = "earning",
            });
        }
        else if (adjustment > 0)
        {
            sheet.Add(new JsonObject
            {
                ["itemCode"] = "YEA_COLLECT",
                ["itemName"] = "年末調整徴収",
                ["amount"] = adjustment,
                ["kind"] = "deduction",
            });
        }

        // 仕訳ドラフト
        var draft = BuildYearEndAccountingDraft(adjustment, empCode);

        return new YearEndResult
        {
            EmployeeId = input.EmployeeId,
            EmployeeCode = empCode,
            EmployeeName = empName,
            AnnualGrossIncome = annualGross,
            AnnualBonusIncome = annualBonus,
            TotalIncome = totalIncome,
            EmploymentIncomeDeduction = employmentDeduction,
            GrossIncome = grossIncome,
            BasicDeduction = basicDeduction,
            SpouseDeduction = spouseDeduction,
            DependentDeduction = dependentDeduction,
            SocialInsuranceDeduction = socialInsDeduction,
            LifeInsuranceDeduction = lifeInsDeduction,
            EarthquakeDeduction = earthquakeDeduction,
            TotalDeductions = totalDeductions,
            TaxableIncome = taxableIncome,
            CalculatedTax = calcTax,
            HousingLoanCredit = housingLoanCredit,
            FinalAnnualTax = finalTax,
            TotalWithheld = annualWht,
            Adjustment = adjustment,
            AdjustmentType = adjustmentType,
            PayrollSheet = sheet,
            AccountingDraft = draft,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    /// <summary>年間給与・賞与・社保・源泉の集計</summary>
    private static async Task<(decimal Gross, decimal Bonus, decimal SocialIns, decimal Wht)> AggregateAnnualPayrollAsync(
        NpgsqlConnection conn, string companyCode, Guid employeeId, int year, CancellationToken ct)
    {
        decimal totalGross = 0, totalBonus = 0, totalSi = 0, totalWht = 0;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT pr.run_type, pre.payroll_sheet
            FROM payroll_run_entries pre
            JOIN payroll_runs pr ON pr.id = pre.run_id
            WHERE pr.company_code = $1 AND pre.employee_id = $2
              AND pr.period_month >= $3 AND pr.period_month <= $4
              AND pr.status != 'deleted'
            ORDER BY pr.period_month";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(employeeId);
        cmd.Parameters.AddWithValue($"{year}-01");
        cmd.Parameters.AddWithValue($"{year}-12");

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var runType = rd.IsDBNull(0) ? "manual" : rd.GetString(0);
            var sheetJson = rd.IsDBNull(1) ? null : rd.GetString(1);
            if (string.IsNullOrEmpty(sheetJson)) continue;

            using var doc = JsonDocument.Parse(sheetJson);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var code = item.TryGetProperty("itemCode", out var ic) ? ic.GetString() : null;
                var amt = item.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDecimal() : 0m;

                if (code is "BASE" or "COMMUTE" or "OVERTIME_STD" or "OVERTIME_60" or "HOLIDAY_PAY" or "LATE_NIGHT_PAY")
                {
                    if (runType == "bonus")
                        totalBonus += amt;
                    else
                        totalGross += amt;
                }
                else if (code == "BONUS")
                {
                    totalBonus += amt;
                }
                else if (code is "HEALTH_INS" or "CARE_INS" or "PENSION" or "EMP_INS")
                {
                    totalSi += amt;
                }
                else if (code == "WHT")
                {
                    totalWht += amt;
                }
            }
        }
        return (totalGross, totalBonus, totalSi, totalWht);
    }

    /// <summary>
    /// 給与所得控除額（令和2年分以降）
    /// 国税庁: https://www.nta.go.jp/taxes/shiraberu/taxanswer/shotoku/1410.htm
    /// </summary>
    private static decimal CalcEmploymentIncomeDeduction(decimal income)
    {
        if (income <= 1_625_000m) return 550_000m;
        if (income <= 1_800_000m) return income * 0.4m - 100_000m;
        if (income <= 3_600_000m) return income * 0.3m + 80_000m;
        if (income <= 6_600_000m) return income * 0.2m + 440_000m;
        if (income <= 8_500_000m) return income * 0.1m + 1_100_000m;
        return 1_950_000m; // 上限195万円
    }

    /// <summary>
    /// 基礎控除（令和2年分以降）
    /// 合計所得金額に応じて段階的に減少
    /// </summary>
    private static decimal CalcBasicDeduction(decimal grossIncome)
    {
        if (grossIncome <= 24_000_000m) return 480_000m;
        if (grossIncome <= 24_500_000m) return 320_000m;
        if (grossIncome <= 25_000_000m) return 160_000m;
        return 0m; // 所得2,500万円超は控除なし
    }

    /// <summary>
    /// 配偶者控除・配偶者特別控除
    /// 配偶者の合計所得金額に応じて控除額が変動
    /// </summary>
    private static decimal CalcSpouseDeduction(decimal ownGrossIncome, decimal spouseIncome)
    {
        if (ownGrossIncome > 10_000_000m) return 0m; // 本人所得1,000万円超は控除なし

        // 配偶者の所得が48万円以下 → 配偶者控除
        if (spouseIncome <= 480_000m)
        {
            if (ownGrossIncome <= 9_000_000m) return 380_000m;
            if (ownGrossIncome <= 9_500_000m) return 260_000m;
            return 130_000m; // 950万〜1000万
        }

        // 配偶者の所得が48万円超133万円以下 → 配偶者特別控除
        if (spouseIncome > 1_330_000m) return 0m;

        // 段階的に減少
        if (spouseIncome <= 950_000m) return ownGrossIncome <= 9_000_000m ? 380_000m : ownGrossIncome <= 9_500_000m ? 260_000m : 130_000m;
        if (spouseIncome <= 1_000_000m) return ownGrossIncome <= 9_000_000m ? 360_000m : ownGrossIncome <= 9_500_000m ? 240_000m : 120_000m;
        if (spouseIncome <= 1_050_000m) return ownGrossIncome <= 9_000_000m ? 310_000m : ownGrossIncome <= 9_500_000m ? 210_000m : 110_000m;
        if (spouseIncome <= 1_100_000m) return ownGrossIncome <= 9_000_000m ? 260_000m : ownGrossIncome <= 9_500_000m ? 180_000m : 90_000m;
        if (spouseIncome <= 1_150_000m) return ownGrossIncome <= 9_000_000m ? 210_000m : ownGrossIncome <= 9_500_000m ? 140_000m : 70_000m;
        if (spouseIncome <= 1_200_000m) return ownGrossIncome <= 9_000_000m ? 160_000m : ownGrossIncome <= 9_500_000m ? 110_000m : 60_000m;
        if (spouseIncome <= 1_250_000m) return ownGrossIncome <= 9_000_000m ? 110_000m : ownGrossIncome <= 9_500_000m ? 80_000m : 40_000m;
        if (spouseIncome <= 1_300_000m) return ownGrossIncome <= 9_000_000m ? 60_000m : ownGrossIncome <= 9_500_000m ? 40_000m : 20_000m;
        return ownGrossIncome <= 9_000_000m ? 30_000m : ownGrossIncome <= 9_500_000m ? 20_000m : 10_000m;
    }

    /// <summary>
    /// 扶養控除の計算
    /// - 一般扶養親族（16歳〜18歳, 23歳〜69歳）: 38万円
    /// - 特定扶養親族（19歳〜22歳）: 63万円
    /// - 老人扶養親族（70歳〜）: 48万円（同居の場合58万円、ここでは48万円で計算）
    /// </summary>
    private static decimal CalcDependentDeduction(JsonElement emp, int year)
    {
        if (!emp.TryGetProperty("dependents", out var deps) || deps.ValueKind != JsonValueKind.Array)
            return 0m;

        decimal total = 0;
        var cutoffDate = new DateTime(year, 12, 31);

        foreach (var dep in deps.EnumerateArray())
        {
            if (dep.ValueKind != JsonValueKind.Object) continue;

            var nameKana = dep.TryGetProperty("nameKana", out var nk) ? nk.GetString() : null;
            var nameKanji = dep.TryGetProperty("nameKanji", out var nj) ? nj.GetString() : null;
            var birthDate = dep.TryGetProperty("birthDate", out var bd) ? bd.GetString() : null;

            if (string.IsNullOrWhiteSpace(nameKana) && string.IsNullOrWhiteSpace(nameKanji)) continue;
            if (string.IsNullOrWhiteSpace(birthDate) || !DateTime.TryParse(birthDate, out var bdDate)) continue;

            var age = cutoffDate.Year - bdDate.Year;
            if (bdDate > cutoffDate.AddYears(-age)) age--;

            if (age < 16) continue; // 16歳未満は控除対象外

            if (age >= 19 && age <= 22)
                total += 630_000m; // 特定扶養親族
            else if (age >= 70)
                total += 480_000m; // 老人扶養親族
            else
                total += 380_000m; // 一般扶養親族
        }
        return total;
    }

    /// <summary>
    /// 生命保険料控除の計算（新制度: 平成24年以降の契約）
    /// 一般・介護医療・個人年金の各枠最大4万円、合計最大12万円
    /// </summary>
    private static decimal CalcLifeInsuranceDeduction(decimal general, decimal medical, decimal pension)
    {
        decimal CalcOne(decimal premium)
        {
            if (premium <= 0) return 0;
            if (premium <= 20_000m) return premium;
            if (premium <= 40_000m) return premium / 2m + 10_000m;
            if (premium <= 80_000m) return premium / 4m + 20_000m;
            return 40_000m; // 上限4万円
        }

        var total = CalcOne(general) + CalcOne(medical) + CalcOne(pension);
        return Math.Min(total, 120_000m); // 合計上限12万円
    }

    /// <summary>
    /// 所得税の速算表（令和2年分以降）
    /// </summary>
    private static decimal CalcIncomeTax(decimal taxableIncome)
    {
        if (taxableIncome <= 1_950_000m) return taxableIncome * 0.05m;
        if (taxableIncome <= 3_300_000m) return taxableIncome * 0.10m - 97_500m;
        if (taxableIncome <= 6_950_000m) return taxableIncome * 0.20m - 427_500m;
        if (taxableIncome <= 9_000_000m) return taxableIncome * 0.23m - 636_000m;
        if (taxableIncome <= 18_000_000m) return taxableIncome * 0.33m - 1_536_000m;
        if (taxableIncome <= 40_000_000m) return taxableIncome * 0.40m - 2_796_000m;
        return taxableIncome * 0.45m - 4_796_000m;
    }

    private static List<JsonObject> BuildYearEndAccountingDraft(decimal adjustment, string? empCode)
    {
        var lines = new List<JsonObject>();
        if (adjustment == 0) return lines;

        if (adjustment < 0)
        {
            // 還付: 預り金を減少させて従業員に返金
            var refundAmt = Math.Abs(adjustment);
            lines.Add(new JsonObject
            {
                ["lineNo"] = 1, ["accountCode"] = "3184", ["accountName"] = "源泉所得税預り金",
                ["drcr"] = "DR", ["amount"] = refundAmt, ["employeeCode"] = empCode,
            });
            lines.Add(new JsonObject
            {
                ["lineNo"] = 2, ["accountCode"] = "315", ["accountName"] = "未払費用",
                ["drcr"] = "CR", ["amount"] = refundAmt,
            });
        }
        else
        {
            // 徴収: 預り金を増加
            lines.Add(new JsonObject
            {
                ["lineNo"] = 1, ["accountCode"] = "315", ["accountName"] = "未払費用",
                ["drcr"] = "DR", ["amount"] = adjustment,
            });
            lines.Add(new JsonObject
            {
                ["lineNo"] = 2, ["accountCode"] = "3184", ["accountName"] = "源泉所得税預り金",
                ["drcr"] = "CR", ["amount"] = adjustment, ["employeeCode"] = empCode,
            });
        }
        return lines;
    }
}
