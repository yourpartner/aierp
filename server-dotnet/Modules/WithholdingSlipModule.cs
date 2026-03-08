using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Server.Infrastructure;

namespace Server.Modules;

public static class WithholdingSlipModule
{
    const string JpFontFamily = "Noto Sans JP";
    static bool _fontRegistered;

    static void EnsureFontRegistered()
    {
        if (_fontRegistered) return;
        var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSansJP.ttf");
        if (File.Exists(fontPath))
        {
            using var stream = File.OpenRead(fontPath);
            QuestPDF.Drawing.FontManager.RegisterFont(stream);
        }
        _fontRegistered = true;
    }

    public static void MapWithholdingSlipModule(this WebApplication app)
    {
        app.MapGet("/payroll/withholding-slip/employees", HandleGetEmployees).RequireAuthorization();
        app.MapGet("/payroll/withholding-slip/check-existing", HandleCheckExisting).RequireAuthorization();
        app.MapGet("/payroll/withholding-slip/list", HandleList).RequireAuthorization();
        app.MapPost("/payroll/withholding-slip/generate", HandleGenerate).RequireAuthorization();
    }

    static async Task<IResult> HandleGetEmployees(HttpRequest req, NpgsqlDataSource ds, CancellationToken ct)
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
    COALESCE(emp.payload->'contact'->>'address', '') AS address,
    COALESCE(emp.payload->'contact'->>'postalCode', '') AS postal_code
FROM payroll_run_entries e
JOIN payroll_runs r ON r.id = e.run_id
LEFT JOIN employees emp ON emp.company_code = e.company_code AND emp.employee_code = e.employee_code
WHERE e.company_code = $1 AND r.period_month LIKE $2 AND r.status = 'completed'
ORDER BY e.employee_code";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue($"{year}-%");

        var list = new List<object>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new
            {
                employeeCode = rd.GetString(0),
                name = rd.IsDBNull(1) ? rd.GetString(0) : rd.GetString(1),
                department = rd.IsDBNull(2) ? "" : rd.GetString(2),
                address = rd.IsDBNull(3) ? "" : rd.GetString(3),
                postalCode = rd.IsDBNull(4) ? "" : rd.GetString(4),
            });
        }
        return Results.Ok(list);
    }

    static async Task<IResult> HandleCheckExisting(HttpRequest req, AzureBlobService blobService, CancellationToken ct)
    {
        if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
            return Results.BadRequest(new { error = "Missing x-company-code" });
        var companyCode = cc.ToString()!;
        var year = req.Query["year"].FirstOrDefault() ?? DateTime.Today.Year.ToString();
        var employeeCodes = req.Query["employeeCodes"].FirstOrDefault()?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

        var existing = new List<object>();
        if (!blobService.IsConfigured) return Results.Ok(existing);

        foreach (var empCode in employeeCodes)
        {
            var blobName = BuildBlobName(companyCode, year, empCode.Trim());
            try
            {
                var uri = blobService.GetReadUri(blobName);
                using var http = new HttpClient();
                var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri), ct);
                if (response.IsSuccessStatusCode)
                    existing.Add(new { employeeCode = empCode.Trim(), blobName });
            }
            catch { /* blob doesn't exist or error */ }
        }
        return Results.Ok(existing);
    }

    static async Task<IResult> HandleList(HttpRequest req, NpgsqlDataSource ds, AzureBlobService blobService, CancellationToken ct)
    {
        if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
            return Results.BadRequest(new { error = "Missing x-company-code" });
        var companyCode = cc.ToString()!;
        var year = req.Query["year"].FirstOrDefault() ?? DateTime.Today.Year.ToString();

        if (!blobService.IsConfigured)
            return Results.Ok(Array.Empty<object>());

        var prefix = $"{companyCode}/withholding-slips/{year}/";
        var items = new List<object>();

        var empNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT employee_code, COALESCE(payload->>'nameKanji', employee_code) FROM employees WHERE company_code=$1";
        cmd.Parameters.AddWithValue(companyCode);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            empNames[rd.GetString(0)] = rd.GetString(1);

        await foreach (var blob in blobService.ListBlobsAsync(prefix, ct))
        {
            var blobName = blob.Name;
            var fileName = Path.GetFileNameWithoutExtension(blobName);
            var parts = fileName.Split('_');
            var empCode = parts.Length >= 2 ? parts[1] : "";
            var name = empNames.TryGetValue(empCode, out var n) ? n : empCode;
            var url = blobService.GetReadUri(blobName);

            items.Add(new
            {
                employeeCode = empCode,
                name,
                blobName,
                url,
                createdOn = blob.Properties.CreatedOn?.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm"),
                size = blob.Properties.ContentLength
            });
        }

        return Results.Ok(items);
    }

    static async Task<IResult> HandleGenerate(HttpRequest req, NpgsqlDataSource ds, AzureBlobService blobService, CancellationToken ct)
    {
        if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
            return Results.BadRequest(new { error = "Missing x-company-code" });
        var companyCode = cc.ToString()!;

        var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        var root = body.RootElement;
        var year = root.TryGetProperty("year", out var yp) ? yp.GetString() ?? DateTime.Today.Year.ToString() : DateTime.Today.Year.ToString();
        var overwrite = root.TryGetProperty("overwrite", out var ow) && ow.GetBoolean();

        var targetCodes = new List<string>();
        if (root.TryGetProperty("employeeCodes", out var ecProp) && ecProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in ecProp.EnumerateArray())
            {
                var code = e.GetString();
                if (!string.IsNullOrWhiteSpace(code)) targetCodes.Add(code);
            }
        }

        var company = await SalesPdfService.GetCompanyInfoAsync(ds, companyCode);
        var allEmployees = await GetEmployeeDetailsAsync(ds, companyCode, year, ct);

        if (targetCodes.Count > 0)
            allEmployees = allEmployees.Where(e => targetCodes.Contains(e.Code)).ToList();

        if (allEmployees.Count == 0)
            return Results.BadRequest(new { error = "対象年度の給与データがありません" });

        var results = new List<object>();
        foreach (var emp in allEmployees)
        {
            try
            {
                var blobName = BuildBlobName(companyCode, year, emp.Code);

                if (!overwrite && blobService.IsConfigured)
                {
                    try
                    {
                        var checkUri = blobService.GetReadUri(blobName);
                        using var http = new HttpClient();
                        var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, checkUri), ct);
                        if (resp.IsSuccessStatusCode)
                        {
                            results.Add(new { employeeCode = emp.Code, name = emp.Name, status = "skipped", reason = "既に存在", blobName, url = blobService.GetReadUri(blobName) });
                            continue;
                        }
                    }
                    catch { /* doesn't exist, proceed */ }
                }

                var payrollData = await GetPayrollDataAsync(ds, companyCode, year, emp.Code, ct);
                var slipData = BuildSlipData(emp, company, payrollData, year);
                var pdfBytes = GeneratePdf(slipData, year);

                if (blobService.IsConfigured)
                {
                    using var ms = new MemoryStream(pdfBytes);
                    await blobService.UploadAsync(ms, blobName, "application/pdf", ct);
                    results.Add(new { employeeCode = emp.Code, name = emp.Name, status = "success", blobName, url = blobService.GetReadUri(blobName) });
                }
                else
                {
                    results.Add(new { employeeCode = emp.Code, name = emp.Name, status = "success", blobName = (string?)null, url = (string?)null });
                }
            }
            catch (Exception ex)
            {
                results.Add(new { employeeCode = emp.Code, name = emp.Name, status = "error", reason = ex.Message, blobName = (string?)null, url = (string?)null });
            }
        }

        return Results.Ok(new { year, total = allEmployees.Count, results });
    }

    static string BuildBlobName(string companyCode, string year, string empCode)
        => $"{companyCode}/withholding-slips/{year}/源泉徴収票_{empCode}_{year}.pdf";

    #region Data Models

    record EmployeeDetail(string Code, string Name, string NameKana, string Address, string PostalCode, string Department, string Gender, string BirthDate);

    record SlipData
    {
        // 支払を受ける者
        public string EmployeeName { get; init; } = "";
        public string EmployeeNameKana { get; init; } = "";
        public string EmployeeAddress { get; init; } = "";
        public string EmployeePostalCode { get; init; } = "";
        public string EmployeeCode { get; init; } = "";
        // 支払者
        public string CompanyName { get; init; } = "";
        public string CompanyAddress { get; init; } = "";
        public string CompanyPostalCode { get; init; } = "";
        public string CompanyTel { get; init; } = "";
        public string? CompanyRegistrationNo { get; init; }
        // 種別 (給与・賞与)
        public string PaymentType { get; init; } = "給与・賞与";
        // 支払金額等 (Row 1)
        public decimal PaymentAmount { get; init; }
        public decimal SalaryIncomeAfterDeduction { get; init; }
        public decimal TotalIncomeDeductions { get; init; }
        public decimal WithholdingTaxAmount { get; init; }
        // 配偶者・扶養 (Row 2)
        public bool HasSpouse { get; init; }
        public bool SpouseIsElderly { get; init; }
        public decimal SpouseDeduction { get; init; }
        public int NumSpecificDependents { get; init; }
        public int NumElderlyDependents { get; init; }
        public int NumElderlyCoResident { get; init; }
        public int NumOtherDependents { get; init; }
        public int NumUnder16Dependents { get; init; }
        public int NumSpecialDisabled { get; init; }
        public int NumSpecialDisabledCoResident { get; init; }
        public int NumOtherDisabled { get; init; }
        public bool IsNonResident { get; init; }
        // 保険料等 (Row 3)
        public decimal SocialInsurance { get; init; }
        public decimal SmallBusinessMutualAid { get; init; }
        public decimal LifeInsurance { get; init; }
        public decimal EarthquakeInsurance { get; init; }
        // 住宅借入金等
        public decimal HousingLoanCredit { get; init; }
        // 基礎控除
        public decimal BasicDeduction { get; init; }
        // 生命保険料内訳
        public decimal NewLifeInsurance { get; init; }
        public decimal OldLifeInsurance { get; init; }
        public decimal CareInsurance { get; init; }
        public decimal NewPensionInsurance { get; init; }
        public decimal OldPensionInsurance { get; init; }
        // 配偶者・扶養親族名
        public string SpouseName { get; init; } = "";
        public string SpouseNameKana { get; init; } = "";
        public List<string> DependentNames { get; init; } = new();
        public List<string> Under16DependentNames { get; init; } = new();
        // 摘要
        public string Remarks { get; init; } = "";
        // 年末調整済か
        public bool YearEndAdjusted { get; init; }
        // 生年月日
        public string EmployeeBirthDate { get; init; } = "";
    }

    record PayrollItem(string ItemCode, decimal Amount, string Kind);

    #endregion

    #region Data Queries

    static async Task<List<EmployeeDetail>> GetEmployeeDetailsAsync(NpgsqlDataSource ds, string companyCode, string year, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT DISTINCT ON (e.employee_code)
    e.employee_code,
    COALESCE(emp.payload->>'nameKanji', e.employee_name, e.employee_code),
    COALESCE(emp.payload->>'nameKana', ''),
    COALESCE(emp.payload->'contact'->>'address', ''),
    COALESCE(emp.payload->'contact'->>'postalCode', ''),
    COALESCE(e.department_code, ''),
    COALESCE(emp.payload->>'gender', ''),
    COALESCE(emp.payload->>'birthDate', '')
FROM payroll_run_entries e
JOIN payroll_runs r ON r.id = e.run_id
LEFT JOIN employees emp ON emp.company_code = e.company_code AND emp.employee_code = e.employee_code
WHERE e.company_code = $1 AND r.period_month LIKE $2 AND r.status = 'completed'
ORDER BY e.employee_code";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue($"{year}-%");

        var list = new List<EmployeeDetail>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new EmployeeDetail(
                rd.GetString(0),
                rd.IsDBNull(1) ? "" : rd.GetString(1),
                rd.IsDBNull(2) ? "" : rd.GetString(2),
                rd.IsDBNull(3) ? "" : rd.GetString(3),
                rd.IsDBNull(4) ? "" : rd.GetString(4),
                rd.IsDBNull(5) ? "" : rd.GetString(5),
                rd.IsDBNull(6) ? "" : rd.GetString(6),
                rd.IsDBNull(7) ? "" : rd.GetString(7)
            ));
        }
        return list;
    }

    static async Task<List<PayrollItem>> GetPayrollDataAsync(NpgsqlDataSource ds, string companyCode, string year, string empCode, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT e.payroll_sheet
FROM payroll_run_entries e
JOIN payroll_runs r ON r.id = e.run_id
WHERE e.company_code = $1 AND r.period_month LIKE $2 AND e.employee_code = $3
  AND r.status = 'completed'";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue($"{year}-%");
        cmd.Parameters.AddWithValue(empCode);

        var allItems = new List<PayrollItem>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var json = rd.IsDBNull(0) ? "[]" : rd.GetString(0);
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var code = el.TryGetProperty("itemCode", out var c) ? c.GetString() ?? "" : "";
                    var amount = el.TryGetProperty("amount", out var a) && a.TryGetDecimal(out var av) ? av : 0m;
                    var kind = el.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                    allItems.Add(new PayrollItem(code, amount, kind));
                }
            }
            catch { /* skip invalid JSON */ }
        }
        return allItems;
    }

    #endregion

    #region Calculation

    static readonly HashSet<string> EarningCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BASE", "ADJUST", "WD_OT_SAL", "OT_SAL", "HOL_OT_SAL", "NIGHT_OT_SAL",
        "COMMUTE", "FAMILY_ALLOW", "DEPENDENT_ALLOW",
        "OVERTIME_STD", "OVERTIME_60", "HOLIDAY_PAY", "LATE_NIGHT_PAY",
        "BaseSalary", "TravelFare"
    };

    static readonly HashSet<string> SocialInsuranceCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "HEALTH_INS", "CARE_INS", "PENSION", "EMP_INS",
        "HealthInsurance", "EndowInsurance"
    };

    static SlipData BuildSlipData(EmployeeDetail emp, SalesPdfService.CompanyInfo company, List<PayrollItem> items, string year)
    {
        decimal totalEarnings = 0;
        decimal socialInsurance = 0;
        decimal withholdingTax = 0;

        foreach (var item in items)
        {
            if (item.Kind.Equals("earning", StringComparison.OrdinalIgnoreCase) || EarningCodes.Contains(item.ItemCode))
            {
                if (item.Amount > 0) totalEarnings += item.Amount;
            }

            if (SocialInsuranceCodes.Contains(item.ItemCode))
                socialInsurance += Math.Abs(item.Amount);

            if (item.ItemCode.Equals("WHT", StringComparison.OrdinalIgnoreCase) || item.ItemCode.Equals("IncomeTax", StringComparison.OrdinalIgnoreCase))
                withholdingTax += Math.Abs(item.Amount);
        }

        if (totalEarnings == 0)
        {
            totalEarnings = items.Where(i => i.Kind.Equals("earning", StringComparison.OrdinalIgnoreCase)).Sum(i => Math.Abs(i.Amount));
        }

        var salaryIncomeDeduction = CalcSalaryIncomeDeduction(totalEarnings);
        var salaryIncomeAfterDeduction = Math.Max(0, totalEarnings - salaryIncomeDeduction);
        var basicDeduction = CalcBasicDeduction(salaryIncomeAfterDeduction);
        var totalIncomeDeductions = basicDeduction + socialInsurance;

        return new SlipData
        {
            EmployeeName = emp.Name,
            EmployeeNameKana = emp.NameKana,
            EmployeeAddress = emp.Address,
            EmployeePostalCode = emp.PostalCode,
            EmployeeCode = emp.Code,
            CompanyName = company.Name,
            CompanyAddress = company.Address,
            CompanyPostalCode = company.PostalCode,
            CompanyTel = company.Tel,
            CompanyRegistrationNo = company.RegistrationNo,
            PaymentAmount = totalEarnings,
            SalaryIncomeAfterDeduction = salaryIncomeAfterDeduction,
            TotalIncomeDeductions = totalIncomeDeductions,
            WithholdingTaxAmount = withholdingTax,
            SocialInsurance = socialInsurance,
            BasicDeduction = basicDeduction,
            EmployeeBirthDate = emp.BirthDate,
        };
    }

    static decimal CalcSalaryIncomeDeduction(decimal income)
    {
        if (income <= 550_999m) return income;
        if (income <= 1_618_999m) return 550_000m;
        if (income <= 1_619_999m) return income * 0.4m - 100_000m;
        if (income <= 1_621_999m) return 548_000m + (income >= 1_620_000m ? 4_000m : 0m);
        if (income <= 1_623_999m) return 552_000m;
        if (income <= 1_627_999m) return 556_000m;
        if (income <= 1_799_999m)
        {
            var a = Math.Floor((income - 1_624_000m) / 4_000m) * 4_000m + 1_624_000m;
            return a * 0.4m - 100_000m;
        }
        if (income <= 3_599_999m)
        {
            var a = Math.Floor((income - 1_800_000m) / 4_000m) * 4_000m + 1_800_000m;
            return a * 0.3m + 80_000m;
        }
        if (income <= 6_599_999m)
        {
            var a = Math.Floor((income - 3_600_000m) / 4_000m) * 4_000m + 3_600_000m;
            return a * 0.2m + 440_000m;
        }
        if (income <= 8_499_999m)
        {
            var a = Math.Floor((income - 6_600_000m) / 4_000m) * 4_000m + 6_600_000m;
            return a * 0.1m + 1_100_000m;
        }
        return 1_950_000m;
    }

    static decimal CalcBasicDeduction(decimal totalIncome)
    {
        if (totalIncome <= 24_000_000m) return 480_000m;
        if (totalIncome <= 24_500_000m) return 320_000m;
        if (totalIncome <= 25_000_000m) return 160_000m;
        return 0m;
    }

    #endregion

    #region PDF Generation — 国税庁 源泉徴収票 公式様式準拠

    static byte[] GeneratePdf(SlipData data, string year)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        EnsureFontRegistered();
        int yearInt = int.TryParse(year, out var y) ? y : DateTime.Today.Year;
        int reiwa = yearInt - 2018;

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginTop(20);
                page.MarginBottom(20);
                page.MarginHorizontal(20);
                page.DefaultTextStyle(x => x.FontSize(6.5f).FontFamily(JpFontFamily).FontColor(Colors.Black));
                page.Content().Column(col => RenderOfficialSlip(col, data, yearInt, reiwa));
            });
        });

        using var ms = new MemoryStream();
        doc.GeneratePdf(ms);
        return ms.ToArray();
    }

    // Border & style constants
    const float Bw = 0.5f;       // thin border
    const float BwThick = 1.0f;  // thick outer border
    const float LabelFs = 5.5f;  // label font size
    const float ValueFs = 7.5f;  // value font size
    const float SmallFs = 5.0f;  // small annotation font size

    static string Yen(decimal v) => v != 0 ? $"{v:#,0}" : "";
    static string YenFull(decimal v) => $"{v:#,0}";

    static void RenderOfficialSlip(ColumnDescriptor col, SlipData data, int year, int reiwa)
    {
        // Title
        col.Item().AlignCenter().PaddingBottom(2).Text(text =>
        {
            text.Span("令和 ").FontSize(9);
            text.Span($"{reiwa:D2}").FontSize(11).Bold();
            text.Span(" 年分　給与所得の源泉徴収票").FontSize(9);
        });

        // Outer border wrapping the entire form
        col.Item().Border(BwThick).BorderColor(Colors.Black).Column(main =>
        {
            // ========== 支払を受ける者 (Employee info - TOP section per 国税庁 format) ==========
            main.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.ConstantColumn(28);   // 支払を受ける者 vertical label
                    cd.ConstantColumn(48);   // 住所/氏名 label
                    cd.RelativeColumn(3);    // address / name value
                    cd.ConstantColumn(38);   // 受給者番号 label
                    cd.ConstantColumn(55);   // 受給者番号 value
                });

                // Vertical label spanning both rows
                table.Cell().Row(1).RowSpan(2).Column(1).Border(Bw).Padding(1).AlignCenter().AlignMiddle()
                    .Text("支\n払\nを\n受\nけ\nる\n者").FontSize(SmallFs).LineHeight(1.0f);

                // Row 1: 住所
                table.Cell().Row(1).Column(2).Border(Bw).Padding(1).AlignCenter().AlignMiddle()
                    .Text("住所（居所）\n又は転居前の\n住所（居所）").FontSize(SmallFs);

                table.Cell().Row(1).Column(3).Border(Bw).Padding(2).MinHeight(28).Column(c =>
                {
                    if (!string.IsNullOrEmpty(data.EmployeePostalCode))
                    {
                        c.Item().Row(r =>
                        {
                            r.AutoItem().PaddingRight(2).AlignMiddle().Text("〒").FontSize(SmallFs);
                            var digits = data.EmployeePostalCode.Replace("-", "");
                            for (int i = 0; i < digits.Length; i++)
                            {
                                if (i == 3) r.AutoItem().PaddingHorizontal(2).AlignMiddle().Text("-").FontSize(SmallFs);
                                r.AutoItem().Border(0.3f).Width(8).Height(9).AlignCenter().AlignMiddle()
                                    .Text(digits[i].ToString()).FontSize(SmallFs);
                            }
                        });
                    }
                    c.Item().PaddingTop(2).Text(data.EmployeeAddress).FontSize(ValueFs);
                });

                // 受給者番号 spanning both rows
                table.Cell().Row(1).RowSpan(2).Column(4).Border(Bw).Padding(1).AlignCenter().AlignMiddle()
                    .Text("受給者\n番号").FontSize(SmallFs);
                table.Cell().Row(1).RowSpan(2).Column(5).Border(Bw).Padding(2).AlignCenter().AlignMiddle()
                    .Text(data.EmployeeCode).FontSize(ValueFs);

                // Row 2: 氏名
                table.Cell().Row(2).Column(2).Border(Bw).Padding(1).Column(c =>
                {
                    c.Item().Text("（フリガナ）").FontSize(SmallFs - 1);
                    c.Item().Text("氏　名").FontSize(SmallFs);
                });
                table.Cell().Row(2).Column(3).Border(Bw).Padding(2).MinHeight(25).Column(c =>
                {
                    c.Item().Text(data.EmployeeNameKana).FontSize(SmallFs);
                    c.Item().PaddingTop(1).Text(data.EmployeeName).FontSize(9).Bold();
                });
            });

            // ========== 種別・支払金額・給与所得控除後・所得控除合計・源泉徴収税額 ==========
            main.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.ConstantColumn(55);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                });

                LabelCell(table.Cell().Row(1).Column(1), "種　別");
                LabelCell(table.Cell().Row(1).Column(2), "支　払　金　額");
                LabelCell(table.Cell().Row(1).Column(3), "給与所得控除後の金額\n（調整控除後）");
                LabelCell(table.Cell().Row(1).Column(4), "所得控除の額の合計額");
                LabelCell(table.Cell().Row(1).Column(5), "源泉徴収税額");

                ValueCellLeft(table.Cell().Row(2).Column(1), data.PaymentType);
                ValueCellRight(table.Cell().Row(2).Column(2), YenFull(data.PaymentAmount));
                ValueCellRight(table.Cell().Row(2).Column(3), Yen(data.SalaryIncomeAfterDeduction));
                ValueCellRight(table.Cell().Row(2).Column(4), Yen(data.TotalIncomeDeductions));
                ValueCellRight(table.Cell().Row(2).Column(5), YenFull(data.WithholdingTaxAmount));
            });

            // ========== 配偶者・扶養親族・障害者・非居住者 ==========
            main.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(2);
                    cd.RelativeColumn(2);
                    cd.RelativeColumn(5);
                    cd.RelativeColumn(2);
                    cd.RelativeColumn(2);
                    cd.RelativeColumn(1);
                });

                LabelCell(table.Cell().Row(1).Column(1), "(源泉)控除対象\n配偶者の有無等");
                LabelCell(table.Cell().Row(1).Column(2), "配偶者(特別)\n控除の額");
                LabelCell(table.Cell().Row(1).Column(3), "控除対象扶養親族の数（配偶者を除く。）");
                LabelCell(table.Cell().Row(1).Column(4), "16歳未満\n扶養親族の数");
                LabelCell(table.Cell().Row(1).Column(5), "障害者の数\n（本人を除く。）");
                LabelCell(table.Cell().Row(1).Column(6), "非居\n住者");

                // 配偶者有無
                table.Cell().Row(2).Column(1).Border(Bw).Padding(2).Column(c =>
                {
                    c.Item().Row(r =>
                    {
                        r.RelativeItem().AlignCenter().Text(text =>
                        {
                            text.Span("有 ").FontSize(SmallFs);
                            text.Span(data.HasSpouse ? "○" : "").FontSize(ValueFs);
                        });
                        r.RelativeItem().AlignCenter().Text(text =>
                        {
                            text.Span("無 ").FontSize(SmallFs);
                            text.Span(!data.HasSpouse ? "○" : "").FontSize(ValueFs);
                        });
                    });
                    c.Item().Row(r =>
                    {
                        r.RelativeItem().AlignCenter().Text(text =>
                        {
                            text.Span("老人 ").FontSize(SmallFs);
                            text.Span(data.SpouseIsElderly ? "○" : "").FontSize(ValueFs);
                        });
                    });
                });

                ValueCellRight(table.Cell().Row(2).Column(2), Yen(data.SpouseDeduction));

                // 扶養親族の数 (6 sub-columns per reference: 特定/老人内/老人/その他/特別/その他)
                table.Cell().Row(2).Column(3).Border(Bw).Padding(0).Table(depTable =>
                {
                    depTable.ColumnsDefinition(cd =>
                    {
                        cd.RelativeColumn(1);
                        cd.RelativeColumn(1);
                        cd.RelativeColumn(1);
                        cd.RelativeColumn(1);
                        cd.RelativeColumn(1);
                        cd.RelativeColumn(1);
                    });
                    depTable.Cell().Row(1).Column(1).BorderBottom(Bw).BorderRight(Bw).Padding(1).AlignCenter().Text("特定").FontSize(SmallFs);
                    depTable.Cell().Row(1).Column(2).BorderBottom(Bw).BorderRight(Bw).Padding(1).AlignCenter().Text("老人\n(内)").FontSize(SmallFs - 0.5f);
                    depTable.Cell().Row(1).Column(3).BorderBottom(Bw).BorderRight(Bw).Padding(1).AlignCenter().Text("老人").FontSize(SmallFs);
                    depTable.Cell().Row(1).Column(4).BorderBottom(Bw).BorderRight(Bw).Padding(1).AlignCenter().Text("その他").FontSize(SmallFs);
                    depTable.Cell().Row(1).Column(5).BorderBottom(Bw).BorderRight(Bw).Padding(1).AlignCenter().Text("特別").FontSize(SmallFs);
                    depTable.Cell().Row(1).Column(6).BorderBottom(Bw).Padding(1).AlignCenter().Text("その他").FontSize(SmallFs);

                    depTable.Cell().Row(2).Column(1).BorderRight(Bw).Padding(1).AlignCenter().Text(data.NumSpecificDependents > 0 ? $"{data.NumSpecificDependents}" : "").FontSize(ValueFs);
                    depTable.Cell().Row(2).Column(2).BorderRight(Bw).Padding(1).AlignCenter().Text(data.NumElderlyCoResident > 0 ? $"{data.NumElderlyCoResident}" : "").FontSize(ValueFs);
                    depTable.Cell().Row(2).Column(3).BorderRight(Bw).Padding(1).AlignCenter().Text(data.NumElderlyDependents > 0 ? $"{data.NumElderlyDependents}" : "").FontSize(ValueFs);
                    depTable.Cell().Row(2).Column(4).BorderRight(Bw).Padding(1).AlignCenter().Text(data.NumOtherDependents > 0 ? $"{data.NumOtherDependents}" : "").FontSize(ValueFs);
                    depTable.Cell().Row(2).Column(5).BorderRight(Bw).Padding(1).AlignCenter().Text("").FontSize(ValueFs);
                    depTable.Cell().Row(2).Column(6).Padding(1).AlignCenter().Text("").FontSize(ValueFs);
                });

                // 16歳未満
                table.Cell().Row(2).Column(4).Border(Bw).Padding(2).AlignCenter().AlignMiddle()
                    .Text(data.NumUnder16Dependents > 0 ? $"{data.NumUnder16Dependents}人" : "").FontSize(ValueFs);

                // 障害者 (3 sub-columns: 特別(内)/特別/その他)
                table.Cell().Row(2).Column(5).Border(Bw).Padding(0).Table(disTable =>
                {
                    disTable.ColumnsDefinition(cd =>
                    {
                        cd.RelativeColumn(1);
                        cd.RelativeColumn(1);
                        cd.RelativeColumn(1);
                    });
                    disTable.Cell().Row(1).Column(1).BorderBottom(Bw).BorderRight(Bw).Padding(1).AlignCenter().Text("特別\n(内)").FontSize(SmallFs - 0.5f);
                    disTable.Cell().Row(1).Column(2).BorderBottom(Bw).BorderRight(Bw).Padding(1).AlignCenter().Text("特別").FontSize(SmallFs);
                    disTable.Cell().Row(1).Column(3).BorderBottom(Bw).Padding(1).AlignCenter().Text("その他").FontSize(SmallFs);
                    disTable.Cell().Row(2).Column(1).BorderRight(Bw).Padding(1).AlignCenter().Text(data.NumSpecialDisabledCoResident > 0 ? $"{data.NumSpecialDisabledCoResident}" : "").FontSize(ValueFs);
                    disTable.Cell().Row(2).Column(2).BorderRight(Bw).Padding(1).AlignCenter().Text(data.NumSpecialDisabled > 0 ? $"{data.NumSpecialDisabled}" : "").FontSize(ValueFs);
                    disTable.Cell().Row(2).Column(3).Padding(1).AlignCenter().Text(data.NumOtherDisabled > 0 ? $"{data.NumOtherDisabled}" : "").FontSize(ValueFs);
                });

                table.Cell().Row(2).Column(6).Border(Bw).Padding(2).AlignCenter().AlignMiddle()
                    .Text(data.IsNonResident ? "○" : "").FontSize(ValueFs);
            });

            // ========== 社会保険料等 (5 columns per reference) ==========
            main.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                });

                LabelCell(table.Cell().Row(1).Column(1), "社会保険料等の金額");
                LabelCell(table.Cell().Row(1).Column(2), "（うち）小規模企業\n共済等掛金の額");
                LabelCell(table.Cell().Row(1).Column(3), "生命保険料の控除額");
                LabelCell(table.Cell().Row(1).Column(4), "地震保険料の控除額");
                LabelCell(table.Cell().Row(1).Column(5), "住宅借入金等\n特別控除の額");

                ValueCellRight(table.Cell().Row(2).Column(1), Yen(data.SocialInsurance));
                ValueCellRight(table.Cell().Row(2).Column(2), Yen(data.SmallBusinessMutualAid));
                ValueCellRight(table.Cell().Row(2).Column(3), Yen(data.LifeInsurance));
                ValueCellRight(table.Cell().Row(2).Column(4), Yen(data.EarthquakeInsurance));
                ValueCellRight(table.Cell().Row(2).Column(5), Yen(data.HousingLoanCredit));
            });

            // ========== 特定親族特別控除の額 ==========
            main.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(4);
                });
                LabelCell(table.Cell().Row(1).Column(1), "特定親族特別控除の額");
                table.Cell().Row(1).Column(2).Border(Bw).MinHeight(14);
            });

            // ========== 生命保険料の金額内訳 (5 columns) ==========
            main.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                });

                LabelCell(table.Cell().Row(1).Column(1), "新生命保険料\nの金額");
                LabelCell(table.Cell().Row(1).Column(2), "旧生命保険料\nの金額");
                LabelCell(table.Cell().Row(1).Column(3), "介護医療\n保険料の金額");
                LabelCell(table.Cell().Row(1).Column(4), "新個人年金\n保険料の金額");
                LabelCell(table.Cell().Row(1).Column(5), "旧個人年金\n保険料の金額");

                ValueCellRight(table.Cell().Row(2).Column(1), Yen(data.NewLifeInsurance));
                ValueCellRight(table.Cell().Row(2).Column(2), Yen(data.OldLifeInsurance));
                ValueCellRight(table.Cell().Row(2).Column(3), Yen(data.CareInsurance));
                ValueCellRight(table.Cell().Row(2).Column(4), Yen(data.NewPensionInsurance));
                ValueCellRight(table.Cell().Row(2).Column(5), Yen(data.OldPensionInsurance));
            });

            // ========== 住宅借入金等特別控除の額の内訳 ==========
            main.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                });

                LabelCell(table.Cell().Row(1).Column(1), "住宅借入金等特別控除\nの額の内訳（区分1）");
                LabelCell(table.Cell().Row(1).Column(2), "居住開始年月日\n（1回目）");
                LabelCell(table.Cell().Row(1).Column(3), "住宅借入金等特別控除\nの額の内訳（区分2）");
                LabelCell(table.Cell().Row(1).Column(4), "居住開始年月日\n（2回目）");
                LabelCell(table.Cell().Row(1).Column(5), "住宅借入金等\n特別控除適用数");

                table.Cell().Row(2).Column(1).Border(Bw).Padding(2).MinHeight(12);
                table.Cell().Row(2).Column(2).Border(Bw).Padding(2).MinHeight(12);
                table.Cell().Row(2).Column(3).Border(Bw).Padding(2).MinHeight(12);
                table.Cell().Row(2).Column(4).Border(Bw).Padding(2).MinHeight(12);
                table.Cell().Row(2).Column(5).Border(Bw).Padding(2).MinHeight(12);

                // Second sub-row: 年末残高
                LabelCell(table.Cell().Row(3).Column(1), "住宅借入金等\n年末残高（1回目）");
                table.Cell().Row(3).Column(2).Border(Bw).Padding(2).MinHeight(12);
                LabelCell(table.Cell().Row(3).Column(3), "住宅借入金等\n年末残高（2回目）");
                table.Cell().Row(3).Column(4).Border(Bw).Padding(2).MinHeight(12);
                table.Cell().Row(3).Column(5).Border(Bw).Padding(2).MinHeight(12);
            });

            // ========== 配偶者の合計所得等 ==========
            main.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(2);
                });

                LabelCell(table.Cell().Row(1).Column(1), "配偶者の合計所得");
                LabelCell(table.Cell().Row(1).Column(2), "国民年金保険料等\nの金額");
                LabelCell(table.Cell().Row(1).Column(3), "旧長期損害\n保険料の金額");
                table.Cell().Row(1).Column(4).Border(Bw).MinHeight(14);
            });

            // ========== 基礎控除の額・所得金額調整控除額 ==========
            main.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(3);
                });

                LabelCell(table.Cell().Row(1).Column(1), "基礎控除の額");
                LabelCell(table.Cell().Row(1).Column(2), "所得金額調整控除額");
                table.Cell().Row(1).Column(3).Border(Bw).MinHeight(14);

                ValueCellRight(table.Cell().Row(2).Column(1), Yen(data.BasicDeduction));
                table.Cell().Row(2).Column(2).Border(Bw).Padding(2).MinHeight(12);
                table.Cell().Row(2).Column(3).Border(Bw).MinHeight(12);
            });

            // ========== 摘要 ==========
            main.Item().Table(table =>
            {
                table.ColumnsDefinition(cd => cd.RelativeColumn(1));
                LabelCell(table.Cell().Row(1).Column(1), "摘　　　要");
                table.Cell().Row(2).Column(1).Border(Bw).Padding(4).MinHeight(40)
                    .Text(data.Remarks).FontSize(ValueFs);
            });

            // ========== 控除対象配偶者 ==========
            main.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.ConstantColumn(28);
                    cd.RelativeColumn(2);
                    cd.ConstantColumn(35);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                });

                table.Cell().Row(1).RowSpan(2).Column(1).Border(Bw).Padding(1).AlignCenter().AlignMiddle()
                    .Text("控除\n対象\n配偶\n者").FontSize(SmallFs - 0.5f).LineHeight(1.0f);

                // フリガナ row
                table.Cell().Row(1).Column(2).Border(Bw).Padding(1)
                    .Text(text =>
                    {
                        text.Span("（フリガナ）").FontSize(SmallFs - 1);
                        text.Span($" {data.SpouseNameKana}").FontSize(SmallFs);
                    });
                // 氏名 row
                table.Cell().Row(2).Column(2).Border(Bw).Padding(1).MinHeight(12)
                    .Text(data.SpouseName).FontSize(ValueFs);

                LabelCell(table.Cell().Row(1).RowSpan(2).Column(3), "区分\n(非居住者)");
                table.Cell().Row(1).RowSpan(2).Column(4).Border(Bw).Padding(1).AlignCenter().AlignMiddle()
                    .Text("").FontSize(SmallFs);
                table.Cell().Row(1).RowSpan(2).Column(5).Border(Bw).Padding(1).AlignCenter().AlignMiddle()
                    .Text("").FontSize(SmallFs);
            });

            // ========== 控除対象扶養親族等 + 16歳未満の扶養親族 ==========
            main.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                });

                LabelCell(table.Cell().Row(1).Column(1), "控除対象扶養親族等");
                LabelCell(table.Cell().Row(1).Column(2), "16歳未満の扶養親族");

                // Left: 4 numbered rows
                table.Cell().Row(2).Column(1).Border(Bw).Padding(0).Column(c =>
                {
                    for (int i = 0; i < 4; i++)
                    {
                        var name = i < data.DependentNames.Count ? data.DependentNames[i] : "";
                        c.Item().BorderBottom(i < 3 ? Bw : 0).Padding(1).MinHeight(13).Row(r =>
                        {
                            r.AutoItem().PaddingLeft(2).Text($"({i + 1}) ").FontSize(SmallFs);
                            r.RelativeItem().Text(name).FontSize(ValueFs);
                        });
                    }
                });

                // Right: 4 numbered rows
                table.Cell().Row(2).Column(2).Border(Bw).Padding(0).Column(c =>
                {
                    for (int i = 0; i < 4; i++)
                    {
                        var name = i < data.Under16DependentNames.Count ? data.Under16DependentNames[i] : "";
                        c.Item().BorderBottom(i < 3 ? Bw : 0).Padding(1).MinHeight(13).Row(r =>
                        {
                            r.AutoItem().PaddingLeft(2).Text($"({i + 1}) ").FontSize(SmallFs);
                            r.RelativeItem().Text(name).FontSize(ValueFs);
                        });
                    }
                });
            });

            // ========== チェックボックス行 ==========
            main.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                });

                var labels = new[] { "未成年者", "外国人", "死亡退職", "災害者", "乙欄", "本人\n障害者", "寡婦", "ひとり親", "勤労学生" };
                for (int i = 0; i < labels.Length; i++)
                {
                    table.Cell().Row(1).Column((uint)(i + 1)).Border(Bw).Padding(1).AlignCenter().AlignMiddle().MinHeight(16)
                        .Text(labels[i]).FontSize(SmallFs);
                }
            });

            // ========== 中途就・退職 + 受給者生年月日 ==========
            main.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(2);
                });

                // 中途就・退職
                table.Cell().Row(1).Column(1).Border(Bw).Padding(2).MinHeight(22).Column(c =>
                {
                    c.Item().Text("中途就・退職").FontSize(SmallFs).Bold();
                    c.Item().Row(r =>
                    {
                        r.RelativeItem().Text("就職　　　年　　月　　日").FontSize(SmallFs);
                    });
                    c.Item().Row(r =>
                    {
                        r.RelativeItem().Text("退職　　　年　　月　　日").FontSize(SmallFs);
                    });
                });

                // 受給者生年月日
                table.Cell().Row(1).Column(2).Border(Bw).Padding(2).Column(c =>
                {
                    c.Item().Text("受給者生年月日").FontSize(SmallFs).Bold();
                    c.Item().PaddingTop(2).Row(r =>
                    {
                        if (DateTime.TryParse(data.EmployeeBirthDate, out var bd))
                        {
                            var (era, eraYear) = ToJapaneseEra(bd);
                            var eras = new[] { "明治", "大正", "昭和", "平成", "令和" };
                            foreach (var e in eras)
                            {
                                if (e == era)
                                    r.AutoItem().PaddingHorizontal(1).Text(e).FontSize(SmallFs).Bold().Underline();
                                else
                                    r.AutoItem().PaddingHorizontal(1).Text(e).FontSize(SmallFs - 0.5f);
                            }
                            r.AutoItem().PaddingLeft(4).Text($"{eraYear:D2}年{bd.Month:D2}月{bd.Day:D2}日").FontSize(ValueFs);
                        }
                    });
                });
            });

            // ========== 支払者 ==========
            main.Item().BorderTop(BwThick).Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.ConstantColumn(55);
                    cd.RelativeColumn(2);
                    cd.ConstantColumn(50);
                    cd.RelativeColumn(1);
                });

                // Row 1: 住所 + 法人番号
                table.Cell().Row(1).Column(1).Border(Bw).Padding(1).AlignMiddle()
                    .Text("支払者\n住所(居所)\n又は所在地").FontSize(SmallFs);
                table.Cell().Row(1).Column(2).Border(Bw).Padding(2).MinHeight(18).Column(c =>
                {
                    if (!string.IsNullOrEmpty(data.CompanyPostalCode))
                        c.Item().Text($"〒{data.CompanyPostalCode}").FontSize(SmallFs);
                    c.Item().Text(data.CompanyAddress).FontSize(ValueFs);
                });
                table.Cell().Row(1).Column(3).Border(Bw).Padding(1).AlignMiddle()
                    .Text("法人番号").FontSize(SmallFs);
                table.Cell().Row(1).Column(4).Border(Bw).Padding(2).AlignMiddle()
                    .Text(data.CompanyRegistrationNo ?? "").FontSize(ValueFs);

                // Row 2: 名称 + 電話番号
                table.Cell().Row(2).Column(1).Border(Bw).Padding(1).AlignMiddle()
                    .Text("氏名又は名称").FontSize(SmallFs);
                table.Cell().Row(2).Column(2).Border(Bw).Padding(2).MinHeight(16)
                    .AlignMiddle().Text(data.CompanyName).FontSize(8).Bold();
                table.Cell().Row(2).Column(3).Border(Bw).Padding(1).AlignMiddle()
                    .Text("電話番号").FontSize(SmallFs);
                table.Cell().Row(2).Column(4).Border(Bw).Padding(2).AlignMiddle()
                    .Text(data.CompanyTel ?? "").FontSize(ValueFs);
            });
        });

        // Footer
        col.Item().PaddingTop(4).AlignRight()
            .Text($"作成日: {DateTime.Now:yyyy年MM月dd日}").FontSize(6).FontColor(Colors.Grey.Medium);
    }

    static (string era, int year) ToJapaneseEra(DateTime dt)
    {
        if (dt.Year >= 2019) return ("令和", dt.Year - 2018);
        if (dt.Year >= 1989) return ("平成", dt.Year - 1988);
        if (dt.Year >= 1926) return ("昭和", dt.Year - 1925);
        if (dt.Year >= 1912) return ("大正", dt.Year - 1911);
        return ("明治", dt.Year - 1867);
    }

    // Helper: label cell with gray background
    static void LabelCell(IContainer container, string label)
    {
        container.Border(Bw).Background("#F0F0EC").Padding(2).AlignCenter().AlignMiddle()
            .Text(label).FontSize(LabelFs).Bold();
    }

    // Helper: value cell, right-aligned (for amounts)
    static void ValueCellRight(IContainer container, string value)
    {
        container.Border(Bw).Padding(3).AlignRight().AlignMiddle()
            .Text(value).FontSize(ValueFs);
    }

    // Helper: value cell, left-aligned
    static void ValueCellLeft(IContainer container, string value)
    {
        container.Border(Bw).Padding(3).AlignLeft().AlignMiddle()
            .Text(value).FontSize(ValueFs);
    }

    #endregion
}
