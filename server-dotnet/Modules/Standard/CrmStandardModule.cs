using Server.Infrastructure.Modules;

namespace Server.Modules.Standard;

/// <summary>
/// CRM模块 - 标准版
/// </summary>
public class CrmStandardModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "crm",
        Name = "客户关系管理",
        Description = "联系人、商机、报价、销售订单、活动等CRM功能",
        Category = ModuleCategory.Standard,
        Version = "1.0.0",
        Dependencies = new[] { "finance_core" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_crm", Label = "menu.crm", Icon = "Connection", Path = "", ParentId = null, Order = 300 },
            new MenuConfig { Id = "menu_contacts", Label = "menu.contacts", Icon = "Avatar", Path = "/crm/contacts", ParentId = "menu_crm", Order = 301 },
            new MenuConfig { Id = "menu_deals", Label = "menu.deals", Icon = "TrendCharts", Path = "/crm/deals", ParentId = "menu_crm", Order = 302 },
            new MenuConfig { Id = "menu_quotes", Label = "menu.quotes", Icon = "Tickets", Path = "/crm/quotes", ParentId = "menu_crm", Order = 303 },
            new MenuConfig { Id = "menu_sales_orders", Label = "menu.salesOrders", Icon = "ShoppingCart", Path = "/crm/sales-orders", ParentId = "menu_crm", Order = 304 },
            new MenuConfig { Id = "menu_activities", Label = "menu.activities", Icon = "Bell", Path = "/crm/activities", ParentId = "menu_crm", Order = 305 },
            new MenuConfig { Id = "menu_businesspartners", Label = "menu.businessPartners", Icon = "OfficeBuilding", Path = "/businesspartners", ParentId = "menu_crm", Order = 306 },
        }
    };
    
    public override void MapEndpoints(WebApplication app)
    {
        CrmModule.MapCrmModule(app);
    }
}

