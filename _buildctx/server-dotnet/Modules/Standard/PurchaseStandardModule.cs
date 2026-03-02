using Server.Infrastructure.Modules;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Server.Modules.Standard;

/// <summary>
/// 采购模块 - 标准版
/// </summary>
public class PurchaseStandardModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "purchase",
        Name = "采购管理",
        Description = "采购订单、供应商发票等功能",
        Category = ModuleCategory.Standard,
        Version = "1.0.0",
        Dependencies = new[] { "finance_core" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_purchase", Label = "menu.purchase", Icon = "ShoppingBag", Path = "", ParentId = null, Order = 500 },
            new MenuConfig { Id = "menu_purchase_orders", Label = "menu.purchaseOrders", Icon = "Document", Path = "/purchase-orders", ParentId = "menu_purchase", Order = 501 },
            new MenuConfig { Id = "menu_vendor_invoices", Label = "menu.vendorInvoices", Icon = "Tickets", Path = "/vendor-invoices", ParentId = "menu_purchase", Order = 502 },
        }
    };
    
    public override void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<InvoiceRegistryService>();
    }

    public override void MapEndpoints(WebApplication app)
    {
        // Helper endpoints for Purchase UI.

        // GET /purchase-orders/last-price?partnerCode=...&materialCode=...
        app.MapGet("/purchase-orders/last-price", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var partnerCode = req.Query["partnerCode"].FirstOrDefault();
            var materialCode = req.Query["materialCode"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(partnerCode) || string.IsNullOrWhiteSpace(materialCode))
                return Results.BadRequest(new { error = "partnerCode and materialCode required" });

            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT
                  (line->>'unitPrice')::numeric AS unit_price
                FROM purchase_orders po,
                     jsonb_array_elements(COALESCE(po.payload->'lines','[]'::jsonb)) AS line
                WHERE po.company_code = $1
                  AND (po.payload->>'partnerCode') = $2
                  AND (line->>'materialCode') = $3
                ORDER BY po.created_at DESC
                LIMIT 1";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(partnerCode);
            cmd.Parameters.AddWithValue(materialCode);
            var val = await cmd.ExecuteScalarAsync(req.HttpContext.RequestAborted);
            if (val is null || val is DBNull) return Results.Ok(new { found = false, unitPrice = 0m });
            return Results.Ok(new { found = true, unitPrice = Convert.ToDecimal(val) });
        }).RequireAuthorization();

        // GET /purchase-orders/{id}/progress
        // NOTE: If detailed receipts/vouchers linkage is not implemented in current backend snapshot, return empty structure (no 405).
        app.MapGet("/purchase-orders/{id:guid}/progress", (Guid id) =>
        {
            return Results.Ok(new { receipts = Array.Empty<object>(), vouchers = Array.Empty<object>(), receivedSummary = new { } });
        }).RequireAuthorization();
    }
}

