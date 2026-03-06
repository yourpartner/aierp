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

        // Get employee names for display
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
            // Extract employee code from pattern: 源泉徴収票_{empCode}_{year}.pdf
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
        public string EmployeeName { get; init; } = "";
        public string EmployeeNameKana { get; init; } = "";
        public string EmployeeAddress { get; init; } = "";
        public string EmployeePostalCode { get; init; } = "";
        public string EmployeeCode { get; init; } = "";
        public string CompanyName { get; init; } = "";
        public string CompanyAddress { get; init; } = "";
        public string CompanyPostalCode { get; init; } = "";
        public string CompanyTel { get; init; } = "";
        public string? CompanyRegistrationNo { get; init; }
        public decimal PaymentAmount { get; init; }
        public decimal SalaryIncomeAfterDeduction { get; init; }
        public decimal TotalIncomeDeductions { get; init; }
        public decimal WithholdingTaxAmount { get; init; }
        public decimal SocialInsurance { get; init; }
        public decimal LifeInsurance { get; init; }
        public decimal EarthquakeInsurance { get; init; }
        public decimal BasicDeduction { get; init; }
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
        decimal residentTax = 0;

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

            if (item.ItemCode.Equals("RESIDENT_TAX", StringComparison.OrdinalIgnoreCase))
                residentTax += Math.Abs(item.Amount);
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

    #region PDF Generation

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
                page.Size(PageSizes.A4.Landscape());
                page.MarginTop(25);
                page.MarginBottom(20);
                page.MarginHorizontal(30);
                page.DefaultTextStyle(x => x.FontSize(8).FontFamily(JpFontFamily).FontColor(Colors.Black));
                page.Content().Column(col => RenderSlip(col, data, yearInt, reiwa));
            });
        });

        using var ms = new MemoryStream();
        doc.GeneratePdf(ms);
        return ms.ToArray();
    }

    static void RenderSlip(ColumnDescriptor col, SlipData data, int year, int reiwa)
    {
        col.Item().AlignCenter().PaddingBottom(4).Text($"令和{reiwa}年分　給与所得の源泉徴収票").FontSize(14).Bold().LetterSpacing(0.05f);

        col.Item().PaddingBottom(8).Border(1.5f).BorderColor(Colors.Black).Padding(0).Column(main =>
        {
            // Row 1: 支払を受ける者
            main.Item().Row(topRow =>
            {
                // Left: Employee info
                topRow.RelativeItem(6).Border(0.5f).BorderColor(Colors.Black).Padding(0).Column(empCol =>
                {
                    empCol.Item().Background("#F5F5F0").BorderBottom(0.5f).BorderColor(Colors.Black).Padding(4)
                        .Text("支払を受ける者").FontSize(7).Bold();

                    empCol.Item().Padding(6).Column(inner =>
                    {
                        inner.Item().Row(r =>
                        {
                            r.RelativeItem(1).Column(c =>
                            {
                                c.Item().Text("住所（居所）").FontSize(6).FontColor(Colors.Grey.Darken2);
                                var addr = string.IsNullOrEmpty(data.EmployeePostalCode) ? data.EmployeeAddress : $"〒{data.EmployeePostalCode}　{data.EmployeeAddress}";
                                c.Item().PaddingTop(2).Text(addr).FontSize(9);
                            });
                        });

                        inner.Item().PaddingTop(4).Row(r =>
                        {
                            r.RelativeItem(1).Column(c =>
                            {
                                c.Item().Text("（フリガナ）").FontSize(6).FontColor(Colors.Grey.Darken2);
                                c.Item().Text(data.EmployeeNameKana).FontSize(7).FontColor(Colors.Grey.Darken1);
                            });
                        });

                        inner.Item().Row(r =>
                        {
                            r.RelativeItem(3).Column(c =>
                            {
                                c.Item().Text("氏名").FontSize(6).FontColor(Colors.Grey.Darken2);
                                c.Item().PaddingTop(1).Text(data.EmployeeName).FontSize(12).Bold();
                            });
                            r.RelativeItem(2).Column(c =>
                            {
                                c.Item().Text("受給者番号").FontSize(6).FontColor(Colors.Grey.Darken2);
                                c.Item().PaddingTop(1).Text(data.EmployeeCode).FontSize(9);
                            });
                        });
                    });
                });

                // Right: Payer info
                topRow.RelativeItem(4).Border(0.5f).BorderColor(Colors.Black).Padding(0).Column(payerCol =>
                {
                    payerCol.Item().Background("#F5F5F0").BorderBottom(0.5f).BorderColor(Colors.Black).Padding(4)
                        .Text("支払者").FontSize(7).Bold();

                    payerCol.Item().Padding(6).Column(inner =>
                    {
                        inner.Item().Column(c =>
                        {
                            c.Item().Text("名称").FontSize(6).FontColor(Colors.Grey.Darken2);
                            c.Item().PaddingTop(1).Text(data.CompanyName).FontSize(10).Bold();
                        });
                        inner.Item().PaddingTop(3).Column(c =>
                        {
                            c.Item().Text("所在地").FontSize(6).FontColor(Colors.Grey.Darken2);
                            var addr = string.IsNullOrEmpty(data.CompanyPostalCode) ? data.CompanyAddress : $"〒{data.CompanyPostalCode}　{data.CompanyAddress}";
                            c.Item().PaddingTop(1).Text(addr).FontSize(8);
                        });
                        if (!string.IsNullOrEmpty(data.CompanyTel))
                        {
                            inner.Item().PaddingTop(2).Text($"TEL: {data.CompanyTel}").FontSize(7);
                        }
                        if (!string.IsNullOrEmpty(data.CompanyRegistrationNo))
                        {
                            inner.Item().PaddingTop(2).Text($"法人番号: {data.CompanyRegistrationNo}").FontSize(7);
                        }
                    });
                });
            });

            // Row 2: 支払金額等
            main.Item().Border(0.5f).BorderColor(Colors.Black).Padding(0).Column(amtCol =>
            {
                amtCol.Item().Background("#F5F5F0").BorderBottom(0.5f).BorderColor(Colors.Black).Padding(4)
                    .Text("支払金額等").FontSize(7).Bold();

                amtCol.Item().Table(table =>
                {
                    table.ColumnsDefinition(cd =>
                    {
                        cd.RelativeColumn(1);
                        cd.RelativeColumn(1);
                        cd.RelativeColumn(1);
                        cd.RelativeColumn(1);
                    });

                    var hStyle = TextStyle.Default.FontSize(7).FontColor(Colors.Grey.Darken2);
                    var vStyle = TextStyle.Default.FontSize(11).Bold();

                    void AmtCell(IContainer c, string label, decimal value)
                    {
                        c.Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(6).Column(inner =>
                        {
                            inner.Item().Text(label).Style(hStyle);
                            inner.Item().PaddingTop(3).AlignRight().Text($"¥{value:#,0}").Style(vStyle);
                        });
                    }

                    AmtCell(table.Cell(), "支払金額", data.PaymentAmount);
                    AmtCell(table.Cell(), "給与所得控除後の金額\n（調整控除後）", data.SalaryIncomeAfterDeduction);
                    AmtCell(table.Cell(), "所得控除の額の合計額", data.TotalIncomeDeductions);
                    AmtCell(table.Cell(), "源泉徴収税額", data.WithholdingTaxAmount);
                });
            });

            // Row 3: 控除の内訳
            main.Item().Border(0.5f).BorderColor(Colors.Black).Padding(0).Column(dedCol =>
            {
                dedCol.Item().Background("#F5F5F0").BorderBottom(0.5f).BorderColor(Colors.Black).Padding(4)
                    .Text("控除等の内訳").FontSize(7).Bold();

                dedCol.Item().Table(table =>
                {
                    table.ColumnsDefinition(cd =>
                    {
                        cd.RelativeColumn(1);
                        cd.RelativeColumn(1);
                        cd.RelativeColumn(1);
                        cd.RelativeColumn(1);
                    });

                    var hStyle = TextStyle.Default.FontSize(7).FontColor(Colors.Grey.Darken2);
                    var vStyle = TextStyle.Default.FontSize(10).Bold();

                    void DedCell(IContainer c, string label, decimal value)
                    {
                        c.Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(5).Column(inner =>
                        {
                            inner.Item().Text(label).Style(hStyle);
                            inner.Item().PaddingTop(2).AlignRight().Text(value > 0 ? $"¥{value:#,0}" : "―").Style(vStyle);
                        });
                    }

                    DedCell(table.Cell(), "社会保険料等の金額", data.SocialInsurance);
                    DedCell(table.Cell(), "基礎控除の額", data.BasicDeduction);
                    DedCell(table.Cell(), "生命保険料の控除額", data.LifeInsurance);
                    DedCell(table.Cell(), "地震保険料の控除額", data.EarthquakeInsurance);
                });
            });

            // Footer note
            main.Item().Padding(6).AlignCenter()
                .Text($"この源泉徴収票は、令和{reiwa}年（{year}年）1月1日から12月31日までの給与等について作成したものです。")
                .FontSize(7).FontColor(Colors.Grey.Darken1);
        });

        col.Item().PaddingTop(8).AlignRight().Text($"作成日: {DateTime.Now:yyyy年MM月dd日}").FontSize(7).FontColor(Colors.Grey.Medium);
    }

    #endregion
}
