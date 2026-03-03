using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Server.Modules;

/// <summary>
/// 賃金台帳モジュール - 年次賃金台帳の参照・Excel出力
/// </summary>
public static class WageLedgerModule
{
    // 支給項目コード → 賃金台帳行区分
    static readonly Dictionary<string, string> EarningBuckets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BASE"]           = "base",
        ["ADJUST"]         = "adjust",
        ["WD_OT_SAL"]      = "adjust",
        ["OT_SAL"]         = "adjust",
        ["HOL_OT_SAL"]     = "adjust",
        ["NIGHT_OT_SAL"]   = "adjust",
        ["COMMUTE"]        = "commute",
        ["FAMILY_ALLOW"]   = "family",
        ["DEPENDENT_ALLOW"]= "family",
    };
    // 控除項目コード
    static readonly HashSet<string> DeductionCodes = new(StringComparer.OrdinalIgnoreCase)
        { "WHT","RESIDENT_TAX","HEALTH_INS","CARE_INS","PENSION","EMP_INS","WHT_DEDUCT" };

    public static void MapWageLedgerModule(this WebApplication app)
    {
        // GET /payroll/wage-ledger/employees?year=2025
        // 指定年度に給与データのある従業員一覧
        app.MapGet("/payroll/wage-ledger/employees", async (HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString()!;
            var year = req.Query["year"].FirstOrDefault() ?? DateTime.Today.Year.ToString();

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT DISTINCT ON (e.employee_code)
    e.employee_code,
    COALESCE(emp.payload->>'nameKanji', e.employee_name, e.employee_code) AS display_name,
    e.department_code,
    COALESCE(
        (emp.payload->'contracts'->0->>'employmentTypeCode'),
        (emp.payload->'contracts'->0->>'position'),
        ''
    ) AS position,
    COALESCE(emp.payload->>'gender', '') AS gender
FROM payroll_run_entries e
JOIN payroll_runs r ON r.id = e.run_id
LEFT JOIN employees emp ON emp.company_code = e.company_code AND emp.employee_code = e.employee_code
WHERE e.company_code = $1
  AND r.period_month LIKE $2
ORDER BY e.employee_code";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue($"{year}-%");

            var employees = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                employees.Add(new
                {
                    code         = reader.GetString(0),
                    name         = reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1),
                    department   = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    position     = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    gender       = reader.IsDBNull(4) ? "" : reader.GetString(4),
                });
            }
            return Results.Ok(employees);
        }).RequireAuthorization();

        // GET /payroll/wage-ledger/excel?year=2025&employeeCode=YP227
        // 一人分の賃金台帳 Excel を返す
        app.MapGet("/payroll/wage-ledger/excel", async (HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString()!;
            var year = req.Query["year"].FirstOrDefault() ?? DateTime.Today.Year.ToString();
            var empCode = req.Query["employeeCode"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(empCode))
                return Results.BadRequest(new { error = "employeeCode is required" });

            var (wb, fileName) = await BuildWageLedgerWorkbook(ds, companyCode, year, empCode, ct);
            if (wb == null)
                return Results.NotFound(new { error = "No payroll data found" });

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            ms.Position = 0;
            return Results.File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }).RequireAuthorization();

        // GET /payroll/wage-ledger/bulk-excel?year=2025
        // 全従業員の賃金台帳を1ファイル（複数シート）に出力
        app.MapGet("/payroll/wage-ledger/bulk-excel", async (HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString()!;
            var year = req.Query["year"].FirstOrDefault() ?? DateTime.Today.Year.ToString();
            var deptFilter = req.Query["department"].FirstOrDefault();

            // 対象従業員を取得
            var employees = await GetEmployeesForYearAsync(ds, companyCode, year, deptFilter, ct);
            if (employees.Count == 0)
                return Results.NotFound(new { error = "No payroll data found for this year" });

            using var wb = new XLWorkbook();
            foreach (var emp in employees)
            {
                var data = await GetEmployeeYearlyDataAsync(ds, companyCode, year, emp.Code, ct);
                BuildSheet(wb, emp, data, year);
            }

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            ms.Position = 0;
            var fileName = $"賃金台帳_{companyCode}_{year}.xlsx";
            return Results.File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }).RequireAuthorization();
    }

    // ─── Internal helpers ────────────────────────────────────────────

    record EmployeeInfo(string Code, string Name, string Department, string Position, string Gender);

    static async Task<List<EmployeeInfo>> GetEmployeesForYearAsync(
        NpgsqlDataSource ds, string companyCode, string year, string? deptFilter, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        var extra = string.IsNullOrWhiteSpace(deptFilter) ? "" : " AND e.department_code = $3";
        cmd.CommandText = $@"
SELECT DISTINCT ON (e.employee_code)
    e.employee_code,
    COALESCE(emp.payload->>'nameKanji', e.employee_name, e.employee_code),
    e.department_code,
    COALESCE(emp.payload->'contracts'->0->>'employmentTypeCode', ''),
    COALESCE(emp.payload->>'gender','')
FROM payroll_run_entries e
JOIN payroll_runs r ON r.id = e.run_id
LEFT JOIN employees emp ON emp.company_code = e.company_code AND emp.employee_code = e.employee_code
WHERE e.company_code = $1 AND r.period_month LIKE $2{extra}
ORDER BY e.employee_code";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue($"{year}-%");
        if (!string.IsNullOrWhiteSpace(deptFilter)) cmd.Parameters.AddWithValue(deptFilter);

        var list = new List<EmployeeInfo>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new EmployeeInfo(rd.GetString(0), rd.IsDBNull(1)?"":rd.GetString(1), rd.IsDBNull(2)?"":rd.GetString(2), rd.IsDBNull(3)?"":rd.GetString(3), rd.IsDBNull(4)?"":rd.GetString(4)));
        return list;
    }

    record MonthData(int Month, string RunType, List<JsonElement> Items);

    static async Task<List<MonthData>> GetEmployeeYearlyDataAsync(
        NpgsqlDataSource ds, string companyCode, string year, string empCode, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT r.period_month, r.run_type, e.payroll_sheet
FROM payroll_run_entries e
JOIN payroll_runs r ON r.id = e.run_id
WHERE e.company_code = $1 AND r.period_month LIKE $2 AND e.employee_code = $3
  AND r.status = 'completed'
ORDER BY r.period_month, r.run_type";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue($"{year}-%");
        cmd.Parameters.AddWithValue(empCode);

        var list = new List<MonthData>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var periodStr = rd.GetString(0);  // "2025-03"
            var runType = rd.GetString(1);
            if (!int.TryParse(periodStr.Split('-').LastOrDefault(), out var m)) m = 0;
            var sheetJson = rd.IsDBNull(2) ? "[]" : rd.GetString(2);
            var items = new List<JsonElement>();
            try
            {
                using var doc = JsonDocument.Parse(sheetJson);
                foreach (var el in doc.RootElement.EnumerateArray()) items.Add(el.Clone());
            }
            catch { /* ignore */ }
            list.Add(new MonthData(m, runType, items));
        }
        return list;
    }

    static async Task<(XLWorkbook? wb, string fileName)> BuildWageLedgerWorkbook(
        NpgsqlDataSource ds, string companyCode, string year, string empCode, CancellationToken ct)
    {
        var empList = await GetEmployeesForYearAsync(ds, companyCode, year, null, ct);
        var emp = empList.FirstOrDefault(e => e.Code == empCode);
        if (emp == null) return (null, "");

        var data = await GetEmployeeYearlyDataAsync(ds, companyCode, year, empCode, ct);
        if (data.Count == 0) return (null, "");

        var wb = new XLWorkbook();
        BuildSheet(wb, emp, data, year);
        var safeCode = string.Concat(empCode.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        return (wb, $"{safeCode}_{emp.Name}_賃金台帳_{year}.xlsx");
    }

    static void BuildSheet(XLWorkbook wb, EmployeeInfo emp, List<MonthData> data, string year)
    {
        // シート名は社員コード+名前（Excelシート名は31文字以内）
        var sheetName = $"{emp.Code}_{emp.Name}";
        if (sheetName.Length > 31) sheetName = sheetName[..31];
        sheetName = string.Concat(sheetName.Where(c => !"\\/:*?[]".Contains(c)));

        var ws = wb.Worksheets.Add(sheetName);

        // フォントとデフォルトスタイル
        ws.Style.Font.FontName = "MS P明朝";
        ws.Style.Font.FontSize = 10;

        // 列幅
        ws.Column(1).Width = 14;  // 項目名
        for (int col = 2; col <= 17; col++) ws.Column(col).Width = 8.5;

        // ─── Row 2: ヘッダー情報 ───
        int yearInt = int.TryParse(year, out var y) ? y : DateTime.Today.Year;
        int reiwa = yearInt - 2018;

        ws.Cell(2, 1).Value = "氏名";
        ws.Cell(2, 2).Value = emp.Name;
        ws.Range(2, 2, 2, 3).Merge();
        ws.Cell(2, 4).Value = "性別";
        ws.Cell(2, 5).Value = emp.Gender == "F" ? "女性" : emp.Gender == "M" ? "男性" : "";
        ws.Cell(2, 7).Value = "所属";
        ws.Cell(2, 8).Value = emp.Department;
        ws.Cell(2, 9).Value = "職名";
        ws.Cell(2, 10).Value = emp.Position;
        ws.Range(2, 10, 2, 12).Merge();
        ws.Cell(2, 16).Value = $"令和{reiwa:00}年";
        ws.Cell(2, 17).Value = "";

        StyleInfoRow(ws, 2);

        // ─── Row 4: 月次ヘッダー ───
        string[] monthHeaders = ["1月","2月","3月","4月","5月","6月","7月","8月","9月","10月","11月","12月","賞与①","賞与②","賞与③","合計"];
        for (int i = 0; i < monthHeaders.Length; i++)
        {
            var cell = ws.Cell(4, i + 2);
            cell.Value = monthHeaders[i];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontSize = 10;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#D6E4BC");
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#9DB2BF");
        }

        // ─── 行定義 ───
        var rowDefs = new[]
        {
            (5,  "勤労日数",    "workDays",  false),
            (6,  "勤労時間数",  "workHours", false),
            (7,  "時間外勤務時間数", "otHours", false),
            (8,  "休日勤務時間数",  "holHours", false),
            (9,  "深夜勤務時間数",  "nightHours", false),
            (10, "基本給",      "base",      false),
            (11, "調整賃金",    "adjust",    false),
            (12, "通勤手当",    "commute",   false),
            (13, "扶養手当",    "family",    false),
            (14, "その他手当",  "other",     false),
            (15, "支給額合計",  "totalEarn", true),
            (16, "健康保険",    "HEALTH_INS",false),
            (17, "厚生年金",    "PENSION",   false),
            (18, "雇用保険",    "EMP_INS",   false),
            (19, "所得税",      "WHT",       false),
            (20, "住民税",      "RESIDENT_TAX",false),
            (23, "控除合計",    "totalDeduct",true),
            (24, "差引支給額",  "netPay",    true),
        };

        // 行ラベルを設定
        foreach (var (row, label, _, isBold) in rowDefs)
        {
            var cell = ws.Cell(row, 1);
            cell.Value = label;
            cell.Style.Font.FontName = "MS P明朝";
            cell.Style.Font.FontSize = 10;
            if (isBold) cell.Style.Font.Bold = true;
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#9DB2BF");
            cell.Style.Fill.BackgroundColor = isBold ? XLColor.FromHtml("#F0F4E8") : XLColor.FromHtml("#F8F9F4");
        }

        // 小計行の背景色
        foreach (int totalRow in new[]{15, 23, 24})
        {
            ws.Range(totalRow, 1, totalRow, 17).Style.Fill.BackgroundColor = XLColor.FromHtml("#EBF2D8");
            ws.Range(totalRow, 1, totalRow, 17).Style.Font.Bold = true;
        }

        // 区切り線（勤怠 / 支給 / 控除の間）
        ws.Range(9, 1, 9, 17).Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        ws.Range(15, 1, 15, 17).Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        ws.Range(20, 1, 20, 17).Style.Border.BottomBorder = XLBorderStyleValues.Medium;

        // ─── データ集計 ───
        // bonus以外（manual/monthly/regular 等）は月次として扱う
        var monthlyByMonth = data.Where(d => d.RunType != "bonus").GroupBy(d => d.Month).ToDictionary(g => g.Key, g => g.ToList());
        var bonusRuns = data.Where(d => d.RunType == "bonus").OrderBy(d => d.Month).ToList();

        // 列index: 月1→col2, 月12→col13, 賞与①→col14, 賞与②→col15, 賞与③→col16, 合計→col17
        for (int month = 1; month <= 12; month++)
        {
            int col = month + 1;
            if (!monthlyByMonth.TryGetValue(month, out var entries)) continue;
            var items = entries.SelectMany(e => e.Items).ToList();
            FillDataColumn(ws, col, items, rowDefs, false);
        }

        for (int bi = 0; bi < Math.Min(bonusRuns.Count, 3); bi++)
        {
            int col = 14 + bi;
            FillDataColumn(ws, col, bonusRuns[bi].Items, rowDefs, true);
        }

        // 行5〜9（勤怠）は数値データがない場合は空欄のままでよい

        // 合計列
        FillTotalColumn(ws, 17, rowDefs, monthlyByMonth, bonusRuns);

        // 外枠
        ws.Range(4, 1, 24, 17).Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        ws.Range(4, 1, 24, 17).Style.Border.OutsideBorderColor = XLColor.FromHtml("#4F7849");

        // 行4の外枠強調
        ws.Range(4, 1, 4, 17).Style.Border.TopBorder = XLBorderStyleValues.Medium;
        ws.Range(4, 1, 4, 17).Style.Border.TopBorderColor = XLColor.FromHtml("#4F7849");
    }

    static void FillDataColumn(IXLWorksheet ws, int col, List<JsonElement> items, (int row, string label, string key, bool isBold)[] rowDefs, bool isBonus)
    {
        decimal base_ = 0, adjust = 0, commute = 0, family = 0, other = 0;
        decimal healthIns = 0, pension = 0, empIns = 0, wht = 0, residentTax = 0;
        decimal careIns = 0;

        foreach (var item in items)
        {
            var code = item.TryGetProperty("itemCode", out var c) ? c.GetString() ?? "" : "";
            var amount = item.TryGetProperty("amount", out var a) && a.TryGetDecimal(out var av) ? av : 0m;

            if (EarningBuckets.TryGetValue(code, out var bucket))
            {
                switch (bucket)
                {
                    case "base":    base_ += amount; break;
                    case "adjust":  adjust += amount; break;
                    case "commute": commute += amount; break;
                    case "family":  family += amount; break;
                }
            }
            else if (DeductionCodes.Contains(code))
            {
                switch (code.ToUpperInvariant())
                {
                    case "HEALTH_INS": healthIns += amount; break;
                    case "CARE_INS":   careIns += amount; break;
                    case "PENSION":    pension += amount; break;
                    case "EMP_INS":    empIns += amount; break;
                    case "WHT":        wht += Math.Abs(amount); break;
                    case "RESIDENT_TAX": residentTax += amount; break;
                    case "WHT_DEDUCT":   wht = Math.Max(0, wht - Math.Abs(amount)); break;
                }
            }
            else if (amount > 0)
            {
                other += amount;
            }
        }

        var totalEarn = base_ + adjust + commute + family + other;
        var totalDeduct = healthIns + careIns + pension + empIns + wht + residentTax;
        var netPay = totalEarn - totalDeduct;

        var vals = new Dictionary<string, decimal>
        {
            ["base"] = base_, ["adjust"] = adjust, ["commute"] = commute,
            ["family"] = family, ["other"] = other, ["totalEarn"] = totalEarn,
            ["HEALTH_INS"] = healthIns + careIns, ["PENSION"] = pension,
            ["EMP_INS"] = empIns, ["WHT"] = wht, ["RESIDENT_TAX"] = residentTax,
            ["totalDeduct"] = totalDeduct, ["netPay"] = netPay,
        };

        foreach (var (row, _, key, isBold) in rowDefs)
        {
            if (!vals.TryGetValue(key, out var val)) continue;
            if (val == 0) continue;
            var cell = ws.Cell(row, col);
            cell.Value = val;
            cell.Style.NumberFormat.Format = "#,##0";
            cell.Style.Font.FontName = "MS P明朝";
            cell.Style.Font.FontSize = 10;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#9DB2BF");
            if (isBold) cell.Style.Font.Bold = true;
        }
    }

    static void FillTotalColumn(IXLWorksheet ws, int col, (int row, string label, string key, bool isBold)[] rowDefs,
        Dictionary<int, List<MonthData>> monthlyByMonth, List<MonthData> bonusRuns)
    {
        var allItems = monthlyByMonth.Values.SelectMany(g => g).SelectMany(d => d.Items)
            .Concat(bonusRuns.SelectMany(d => d.Items)).ToList();
        FillDataColumn(ws, col, allItems, rowDefs, false);
        // 合計列は太字
        foreach (var (row, _, _, _) in rowDefs)
        {
            var cell = ws.Cell(row, col);
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#EBF2D8");
        }
    }

    static void StyleInfoRow(IXLWorksheet ws, int row)
    {
        for (int col = 1; col <= 17; col++)
        {
            var cell = ws.Cell(row, col);
            cell.Style.Font.FontName = "MS P明朝";
            cell.Style.Font.FontSize = 10;
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#9DB2BF");
        }
        var labelCells = new[] { 1, 4, 7, 9 };
        foreach (var lc in labelCells)
        {
            ws.Cell(row, lc).Style.Font.Bold = true;
            ws.Cell(row, lc).Style.Fill.BackgroundColor = XLColor.FromHtml("#D6E4BC");
            ws.Cell(row, lc).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }
    }
}
