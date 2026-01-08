using Server.Infrastructure.Modules;

namespace Server.Modules.Standard;

/// <summary>
/// 库存模块 - 标准版
/// </summary>
public class InventoryStandardModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "inventory",
        Name = "库存管理",
        Description = "物料管理、仓库管理、库存移动、盘点等功能",
        Category = ModuleCategory.Standard,
        Version = "1.0.0",
        Dependencies = new[] { "finance_core" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_inventory", Label = "menu.inventory", Icon = "Box", Path = "", ParentId = null, Order = 400 },
            new MenuConfig { Id = "menu_materials", Label = "menu.materials", Icon = "Goods", Path = "/materials", ParentId = "menu_inventory", Order = 401 },
            new MenuConfig { Id = "menu_warehouses", Label = "menu.warehouses", Icon = "House", Path = "/warehouses", ParentId = "menu_inventory", Order = 402 },
            new MenuConfig { Id = "menu_bins", Label = "menu.bins", Icon = "Grid", Path = "/bins", ParentId = "menu_inventory", Order = 403 },
            new MenuConfig { Id = "menu_inventory_movement", Label = "menu.inventoryMovement", Icon = "Switch", Path = "/inventory/movement", ParentId = "menu_inventory", Order = 404 },
            new MenuConfig { Id = "menu_inventory_balances", Label = "menu.inventoryBalances", Icon = "Histogram", Path = "/inventory/balances", ParentId = "menu_inventory", Order = 405 },
            new MenuConfig { Id = "menu_inventory_ledger", Label = "menu.inventoryLedger", Icon = "Document", Path = "/inventory/ledger", ParentId = "menu_inventory", Order = 406 },
            new MenuConfig { Id = "menu_inventory_counts", Label = "menu.inventoryCounts", Icon = "Checked", Path = "/inventory-counts", ParentId = "menu_inventory", Order = 407 },
        }
    };
    
    public override void MapEndpoints(WebApplication app)
    {
        InventoryModule.MapInventoryModule(app);
        InventoryCountModule.MapInventoryCountModule(app);
    }
}

