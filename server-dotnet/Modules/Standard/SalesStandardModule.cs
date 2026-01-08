using Server.Infrastructure.Modules;

namespace Server.Modules.Standard;

/// <summary>
/// 销售模块 - 标准版
/// </summary>
public class SalesStandardModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "sales",
        Name = "销售管理",
        Description = "销售分析、销售预警、销售发票、出库单等功能",
        Category = ModuleCategory.Standard,
        Version = "1.0.0",
        Dependencies = new[] { "crm", "finance_core" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_sales", Label = "menu.sales", Icon = "Sell", Path = "", ParentId = null, Order = 350 },
            new MenuConfig { Id = "menu_sales_analytics", Label = "menu.salesAnalytics", Icon = "DataAnalysis", Path = "/sales-analytics", ParentId = "menu_sales", Order = 351 },
            new MenuConfig { Id = "menu_sales_alerts", Label = "menu.salesAlerts", Icon = "AlarmClock", Path = "/sales-alerts", ParentId = "menu_sales", Order = 352 },
            new MenuConfig { Id = "menu_sales_invoices", Label = "menu.salesInvoices", Icon = "Document", Path = "/sales-invoices", ParentId = "menu_sales", Order = 353 },
            new MenuConfig { Id = "menu_delivery_notes", Label = "menu.deliveryNotes", Icon = "Van", Path = "/delivery-notes", ParentId = "menu_sales", Order = 354 },
        }
    };
    
    public override void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<SalesOrderTaskService>();
        services.AddScoped<SalesOrderLifecycleService>();
        services.AddScoped<SalesAnalyticsAiService>();
        services.AddSingleton<WeComNotificationService>();
        services.AddHostedService<SalesMonitorBackgroundService>();
    }
    
    public override void MapEndpoints(WebApplication app)
    {
        var accountSelectionService = app.Services.CreateScope().ServiceProvider.GetService<AccountSelectionService>();
        DeliveryNoteModule.MapDeliveryNoteModule(app, accountSelectionService);
        SalesInvoiceModule.MapSalesInvoiceModule(app, accountSelectionService);
        SalesAnalyticsModule.MapSalesAnalyticsModule(app);
        SalesAlertModule.MapSalesAlertModule(app);
    }
}

