using Server.Infrastructure.Modules;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Modules;

namespace Server.Modules.Standard;

/// <summary>
/// 财务扩展模块 - 标准版（银行支付、月次结算等高级功能）
/// </summary>
public class FinanceExtStandardModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "finance_ext",
        Name = "财务扩展",
        Description = "银行支付、FB数据、月次结算、财务报表设计等高级功能",
        Category = ModuleCategory.Standard,
        Version = "1.0.0",
        Dependencies = new[] { "finance_core" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_bank_payment", Label = "menu.bankPayment", Icon = "Money", Path = "/operations/bank-payment", ParentId = "menu_finance", Order = 107 },
            new MenuConfig { Id = "menu_fb_payment", Label = "menu.fbPayment", Icon = "Tickets", Path = "/fb-payment", ParentId = "menu_finance", Order = 108 },
            new MenuConfig { Id = "menu_cash_ledger", Label = "menu.cashLedger", Icon = "Coin", Path = "/cash/ledger", ParentId = "menu_finance", Order = 109 },
            new MenuConfig { Id = "menu_financial_statements", Label = "menu.financialStatements", Icon = "DataAnalysis", Path = "/financial/statements", ParentId = "menu_finance", Order = 110 },
            new MenuConfig { Id = "menu_financial_nodes", Label = "menu.financialNodes", Icon = "SetUp", Path = "/financial/nodes", ParentId = "menu_finance", Order = 111 },
            new MenuConfig { Id = "menu_consumption_tax", Label = "menu.consumptionTax", Icon = "Memo", Path = "/financial/consumption-tax", ParentId = "menu_finance", Order = 112 },
            new MenuConfig { Id = "menu_monthly_closing", Label = "menu.monthlyClosing", Icon = "Calendar", Path = "/financial/monthly-closing", ParentId = "menu_finance", Order = 113 },
            new MenuConfig { Id = "menu_ledger_export", Label = "menu.ledgerExport", Icon = "Download", Path = "/ledger-export", ParentId = "menu_finance", Order = 114 },
        }
    };
    
    public override void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<CashManagementService>();
        services.AddScoped<PaymentMatchingService>();
    }
    
    public override void MapEndpoints(WebApplication app)
    {
        MonthlyClosingModule.MapMonthlyClosingModule(app);
        MapConsumptionTaxEndpoints(app);
        MapCashEndpoints(app);
    }

    private static void MapConsumptionTaxEndpoints(WebApplication app)
    {
        app.MapGet("/consumption-tax/settings", async (HttpRequest req, ConsumptionTaxService svc) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var settings = await svc.GetSettingsAsync(cc.ToString());
            return Results.Ok(settings ?? new JsonObject { ["taxationMethod"] = "general", ["simplifiedCategory"] = 5 });
        }).RequireAuthorization();

        app.MapPut("/consumption-tax/settings", async (HttpRequest req, ConsumptionTaxService svc) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
            var settings = JsonNode.Parse(doc.RootElement.GetRawText()) as JsonObject ?? new JsonObject();
            await svc.SaveSettingsAsync(cc.ToString(), settings);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        app.MapPost("/consumption-tax/calculate", async (HttpRequest req, ConsumptionTaxService svc) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
            var root = doc.RootElement;
            var from = root.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            var to = root.TryGetProperty("to", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var taxationMethod = root.TryGetProperty("taxationMethod", out var tm) && tm.ValueKind == JsonValueKind.String ? tm.GetString() : "general";
            var simplifiedCategory = root.TryGetProperty("simplifiedCategory", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt32() : 5;
            var save = root.TryGetProperty("save", out var s) && s.ValueKind == JsonValueKind.True;
            var fiscalYear = root.TryGetProperty("fiscalYear", out var fy) && fy.ValueKind == JsonValueKind.String ? fy.GetString() : null;
            var periodType = root.TryGetProperty("periodType", out var pt) && pt.ValueKind == JsonValueKind.String ? pt.GetString() : "annual";

            if (!DateTime.TryParse(from, out var fromDate) || !DateTime.TryParse(to, out var toDate))
                return Results.BadRequest(new { error = "from/to required" });

            var calc = await svc.CalculateAsync(cc.ToString(), fromDate, toDate, taxationMethod ?? "general", simplifiedCategory);
            Guid? savedId = null;
            if (save)
            {
                if (string.IsNullOrWhiteSpace(fiscalYear)) return Results.BadRequest(new { error = "fiscalYear required when save=true" });
                var user = Server.Infrastructure.Auth.GetUserCtx(req);
                savedId = await svc.SaveReturnAsync(cc.ToString(), fiscalYear!, periodType ?? "annual", taxationMethod ?? "general", calc, user.UserId, user.UserName);
            }

            return Results.Ok(new { calculation = calc, savedId });
        }).RequireAuthorization();

        app.MapGet("/consumption-tax/returns", async (HttpRequest req, ConsumptionTaxService svc) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var fiscalYear = req.Query["fiscalYear"].FirstOrDefault();
            var rows = await svc.GetReturnsAsync(cc.ToString(), string.IsNullOrWhiteSpace(fiscalYear) ? null : fiscalYear);
            return Results.Ok(rows);
        }).RequireAuthorization();

        app.MapDelete("/consumption-tax/returns/{id:guid}", async (Guid id, HttpRequest req, ConsumptionTaxService svc) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var ok = await svc.DeleteReturnAsync(cc.ToString(), id);
            return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "not found" });
        }).RequireAuthorization();

        app.MapGet("/consumption-tax/details", async (HttpRequest req, ConsumptionTaxService svc) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var from = req.Query["from"].FirstOrDefault();
            var to = req.Query["to"].FirstOrDefault();
            var category = req.Query["category"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(category))
                return Results.BadRequest(new { error = "from/to/category required" });
            if (!DateTime.TryParse(from, out var fromDate) || !DateTime.TryParse(to, out var toDate))
                return Results.BadRequest(new { error = "invalid from/to" });

            var items = await svc.GetDetailsAsync(cc.ToString(), fromDate, toDate, category!);
            return Results.Ok(new { items });
        }).RequireAuthorization();
    }

    private static void MapCashEndpoints(WebApplication app)
    {
        // These endpoints are used by CashLedger.vue.
        app.MapGet("/cash/expense-categories", async (HttpRequest req) =>
        {
            // If categories are not persisted, return a minimal default list.
            return Results.Ok(new[]
            {
                new { code = "misc", name = "雑費" },
                new { code = "transportation", name = "交通費" },
                new { code = "dining", name = "会食費" }
            });
        }).RequireAuthorization();

        app.MapGet("/cash-accounts/{cashCode}/imprest-info", async (string cashCode, HttpRequest req, CashManagementService cash) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var info = await cash.GetCashAccountAsync(cc.ToString(), cashCode);
            return info is null ? Results.NotFound(new { error = "not found" }) : Results.Ok(info);
        }).RequireAuthorization();

        app.MapGet("/cash-accounts/{cashCode}/replenish-sources", async (string cashCode, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            
            // 从勘定科目中获取可作为补充来源的现金/银行科目（排除当前科目）
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, account_code, payload
                FROM accounts
                WHERE company_code = $1 
                  AND account_code != $2
                  AND (payload->>'isCash' = 'true' OR payload->>'isBank' = 'true')
                ORDER BY account_code";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(cashCode);
            
            var sources = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var payload = System.Text.Json.Nodes.JsonNode.Parse(reader.GetString(2)) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
                var isCash = payload["isCash"]?.GetValue<bool>() ?? false;
                var isBank = payload["isBank"]?.GetValue<bool>() ?? false;
                sources.Add(new
                {
                    code = reader.GetString(1),
                    name = payload["name"]?.GetValue<string>() ?? reader.GetString(1),
                    isCash,
                    isBank
                });
            }
            return Results.Ok(sources);
        }).RequireAuthorization();

        app.MapGet("/cash-accounts/{cashCode}/transactions", async (string cashCode, HttpRequest req, CashManagementService cash) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            
            var fromStr = req.Query["from"].FirstOrDefault();
            var toStr = req.Query["to"].FirstOrDefault();
            
            // 默认期间：当月
            var from = DateTime.TryParse(fromStr, out var f) ? f : new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            var to = DateTime.TryParse(toStr, out var t) ? t : from.AddMonths(1).AddDays(-1);
            
            var (transactions, openingBalance) = await cash.GetVoucherBasedTransactionsAsync(cc.ToString(), cashCode, from, to);
            return Results.Ok(new { transactions, openingBalance });
        }).RequireAuthorization();

        app.MapPost("/cash-accounts/{cashCode}/transactions", async (string cashCode, HttpRequest req) =>
        {
            return Results.StatusCode(501);
        }).RequireAuthorization();

        app.MapPost("/cash-accounts/{cashCode}/replenish", async (string cashCode, HttpRequest req) =>
        {
            return Results.StatusCode(501);
        }).RequireAuthorization();

        app.MapPost("/cash-accounts/{cashCode}/counts", async (string cashCode, HttpRequest req) =>
        {
            return Results.StatusCode(501);
        }).RequireAuthorization();
    }
}

