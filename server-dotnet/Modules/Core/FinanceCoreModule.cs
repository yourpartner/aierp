using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;
using Server.Infrastructure;
using Server.Infrastructure.Modules;

namespace Server.Modules.Core;

/// <summary>
/// 财务核心模块 - 包含会计、凭证、科目等基础财务功能
/// </summary>
public class FinanceCoreModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "finance_core",
        Name = "财务核心",
        Description = "会计凭证、科目管理、账本等核心财务功能",
        Category = ModuleCategory.Core,
        Version = "1.0.0",
        Dependencies = Array.Empty<string>(),
        Menus = new[]
        {
            new MenuConfig { Id = "menu_finance", Label = "menu.finance", Icon = "Money", Path = "", ParentId = null, Order = 100 },
            new MenuConfig { Id = "menu_vouchers", Label = "menu.vouchers", Icon = "Document", Path = "/vouchers", ParentId = "menu_finance", Order = 101 },
            new MenuConfig { Id = "menu_voucher_new", Label = "menu.voucherNew", Icon = "Plus", Path = "/voucher/new", ParentId = "menu_finance", Order = 102 },
            new MenuConfig { Id = "menu_accounts", Label = "menu.accounts", Icon = "List", Path = "/accounts", ParentId = "menu_finance", Order = 103 },
            new MenuConfig { Id = "menu_account_ledger", Label = "menu.accountLedger", Icon = "Notebook", Path = "/account-ledger", ParentId = "menu_finance", Order = 104 },
            new MenuConfig { Id = "menu_account_balance", Label = "menu.accountBalance", Icon = "DataAnalysis", Path = "/account-balance", ParentId = "menu_finance", Order = 105 },
            new MenuConfig { Id = "menu_trial_balance", Label = "menu.trialBalance", Icon = "DataLine", Path = "/trial-balance", ParentId = "menu_finance", Order = 106 },
        }
    };
    
    public override void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<FinanceService>();
        services.AddScoped<FinancialStatementService>();
        services.AddScoped<ConsumptionTaxService>();
        services.AddScoped<MonthlyClosingService>();
        services.AddScoped<VoucherAutomationService>();
        services.AddScoped<AccountSelectionService>();
    }

    public override void MapEndpoints(WebApplication app)
    {
        MapTrialBalanceEndpoints(app);
        MapVoucherAttachmentEndpoints(app);
        // NOTE: Other finance endpoints still live in Program.cs for now.
        // New/restore-required endpoints should be added here to keep module architecture consistent.
    }

    private static void MapVoucherAttachmentEndpoints(WebApplication app)
    {
        // Upload (multipart/form-data, field name: file)
        app.MapPost("/voucher-attachments/upload", async (HttpRequest req, AzureBlobService blobService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            if (!blobService.IsConfigured) return Results.StatusCode(501);

            var form = await req.ReadFormAsync(req.HttpContext.RequestAborted);
            var file = form.Files.GetFile("file");
            if (file is null) return Results.BadRequest(new { error = "file required" });
            if (file.Length <= 0) return Results.BadRequest(new { error = "empty file" });

            var originalName = string.IsNullOrWhiteSpace(file.FileName) ? "attachment.bin" : file.FileName;
            var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;

            var ext = Path.GetExtension(originalName);
            var normalizedExt = string.IsNullOrWhiteSpace(ext) ? string.Empty : ext.ToLowerInvariant();
            var companyCode = cc.ToString().Trim();

            // Keep a stable, predictable blob naming convention with company prefix.
            // Path format matches existing attachments: {companyCode}/finance/vouchers/{yyyy}/{MM}/{dd}/{uuid}.{ext}
            var blobName = $"{companyCode}/finance/vouchers/{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}{normalizedExt}";

            await using var stream = file.OpenReadStream();
            await using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, req.HttpContext.RequestAborted);
            buffer.Position = 0;

            AzureBlobUploadResult uploadResult;
            try
            {
                uploadResult = await blobService.UploadAsync(buffer, blobName, contentType, req.HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                return Results.Problem($"上传文件到 Azure Storage 失败: {ex.Message}");
            }

            string? url = null;
            try { url = blobService.GetReadUri(uploadResult.BlobName); } catch { }

            // Response is used by front-end and will be persisted into voucher.payload.attachments,
            // FinanceService.UpdateVoucherAsync will strip url/previewUrl before saving.
            return Results.Ok(new
            {
                id = uploadResult.BlobName,
                name = originalName,
                fileName = originalName,
                blobName = uploadResult.BlobName,
                contentType = uploadResult.ContentType,
                size = uploadResult.Size,
                uploadedAt = DateTimeOffset.UtcNow.ToString("O"),
                url
            });
        }).RequireAuthorization();

        // Delete blob by blobName (front passes blobName url-encoded).
        app.MapDelete("/voucher-attachments/{blobName}", async (string blobName, HttpRequest req, AzureBlobService blobService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            if (!blobService.IsConfigured) return Results.StatusCode(501);

            var decoded = Uri.UnescapeDataString(blobName ?? string.Empty);
            var companyCode = cc.ToString().Trim();
            if (!decoded.StartsWith(companyCode + "/", StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(403);

            await blobService.DeleteAsync(decoded, req.HttpContext.RequestAborted);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();
    }

    private static void MapTrialBalanceEndpoints(WebApplication app)
    {
        // Trial Balance report for TrialBalance.vue
        // POST /reports/trial-balance { year?, month?, periodStart?, periodEnd?, showZeroBalance?, accountCategory? }
        app.MapPost("/reports/trial-balance", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
            var root = body.RootElement;

            var showZero = root.TryGetProperty("showZeroBalance", out var sz) && sz.ValueKind == JsonValueKind.True;
            var accountCategory = root.TryGetProperty("accountCategory", out var ac) && ac.ValueKind == JsonValueKind.String
                ? (ac.GetString() ?? "ALL")
                : "ALL";

            DateOnly start;
            DateOnly end;

            if (root.TryGetProperty("periodStart", out var ps) && ps.ValueKind == JsonValueKind.String &&
                root.TryGetProperty("periodEnd", out var pe) && pe.ValueKind == JsonValueKind.String &&
                DateOnly.TryParse(ps.GetString(), out var s1) &&
                DateOnly.TryParse(pe.GetString(), out var e1))
            {
                start = s1;
                end = e1;
            }
            else
            {
                var year = root.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : DateTime.Today.Year;
                if (root.TryGetProperty("month", out var m) && m.ValueKind == JsonValueKind.Number)
                {
                    var month = Math.Clamp(m.GetInt32(), 1, 12);
                    start = new DateOnly(year, month, 1);
                    end = start.AddMonths(1).AddDays(-1);
                }
                else
                {
                    start = new DateOnly(year, 1, 1);
                    end = new DateOnly(year, 12, 31);
                }
            }

            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);
            await using var cmd = conn.CreateCommand();

            // Aggregate vouchers lines into opening/period buckets.
            var sb = new StringBuilder();
            sb.Append(@"
WITH voucher_lines AS (
  SELECT
    v.posting_date,
    line->>'accountCode' AS account_code,
    COALESCE(line->>'drcr','DR') AS drcr,
    COALESCE((line->>'amount')::numeric, 0) AS amount
  FROM vouchers v,
       jsonb_array_elements(v.payload->'lines') AS line
  WHERE v.company_code = $1
    AND v.posting_date <= $3::date
)
SELECT
  a.account_code,
  a.name AS account_name,
  a.pl_bs_type AS category,
  COALESCE(SUM(CASE WHEN vl.posting_date < $2::date AND vl.drcr='DR' THEN vl.amount ELSE 0 END),0) AS opening_dr,
  COALESCE(SUM(CASE WHEN vl.posting_date < $2::date AND vl.drcr='CR' THEN vl.amount ELSE 0 END),0) AS opening_cr,
  COALESCE(SUM(CASE WHEN vl.posting_date >= $2::date AND vl.posting_date <= $3::date AND vl.drcr='DR' THEN vl.amount ELSE 0 END),0) AS period_dr,
  COALESCE(SUM(CASE WHEN vl.posting_date >= $2::date AND vl.posting_date <= $3::date AND vl.drcr='CR' THEN vl.amount ELSE 0 END),0) AS period_cr
FROM accounts a
LEFT JOIN voucher_lines vl ON vl.account_code = a.account_code
WHERE a.company_code = $1
");
            if (!string.IsNullOrWhiteSpace(accountCategory) && !string.Equals(accountCategory, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(" AND a.pl_bs_type = $4");
                cmd.Parameters.AddWithValue(cc.ToString());
                cmd.Parameters.AddWithValue(start.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue(end.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue(accountCategory);
            }
            else
            {
                cmd.Parameters.AddWithValue(cc.ToString());
                cmd.Parameters.AddWithValue(start.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue(end.ToString("yyyy-MM-dd"));
            }
            sb.Append(@"
GROUP BY a.account_code, a.name, a.pl_bs_type
ORDER BY a.account_code;
");

            cmd.CommandText = sb.ToString();

            var rows = new List<object>();
            decimal totOpeningDr = 0, totOpeningCr = 0, totPeriodDr = 0, totPeriodCr = 0, totClosingDr = 0, totClosingCr = 0;

            await using var rd = await cmd.ExecuteReaderAsync(req.HttpContext.RequestAborted);
            while (await rd.ReadAsync(req.HttpContext.RequestAborted))
            {
                var accountCode = rd.GetString(0);
                var accountName = rd.IsDBNull(1) ? "" : rd.GetString(1);
                var category = rd.IsDBNull(2) ? "" : rd.GetString(2);
                var openingDr = rd.GetDecimal(3);
                var openingCr = rd.GetDecimal(4);
                var periodDr = rd.GetDecimal(5);
                var periodCr = rd.GetDecimal(6);

                var openingNet = openingDr - openingCr;
                var openingDrBal = openingNet >= 0 ? openingNet : 0;
                var openingCrBal = openingNet < 0 ? -openingNet : 0;

                var periodNet = periodDr - periodCr;
                var closingNet = openingNet + periodNet;
                var closingDrBal = closingNet >= 0 ? closingNet : 0;
                var closingCrBal = closingNet < 0 ? -closingNet : 0;

                if (!showZero)
                {
                    var allZero =
                        openingDrBal == 0 && openingCrBal == 0 &&
                        periodDr == 0 && periodCr == 0 &&
                        closingDrBal == 0 && closingCrBal == 0;
                    if (allZero) continue;
                }

                totOpeningDr += openingDrBal;
                totOpeningCr += openingCrBal;
                totPeriodDr += periodDr;
                totPeriodCr += periodCr;
                totClosingDr += closingDrBal;
                totClosingCr += closingCrBal;

                rows.Add(new
                {
                    accountCode,
                    accountName,
                    category,
                    openingDrBalance = openingDrBal,
                    openingCrBalance = openingCrBal,
                    periodDr,
                    periodCr,
                    closingDrBalance = closingDrBal,
                    closingCrBalance = closingCrBal
                });
            }

            var totals = new
            {
                openingDr = totOpeningDr,
                openingCr = totOpeningCr,
                periodDr = totPeriodDr,
                periodCr = totPeriodCr,
                closingDr = totClosingDr,
                closingCr = totClosingCr,
                periodDiff = totPeriodDr - totPeriodCr,
                isBalanced = (totPeriodDr - totPeriodCr) == 0
            };

            return Results.Ok(new
            {
                data = rows,
                totals,
                period = new { start = start.ToString("yyyy-MM-dd"), end = end.ToString("yyyy-MM-dd") }
            });
        }).RequireAuthorization();
    }
}

