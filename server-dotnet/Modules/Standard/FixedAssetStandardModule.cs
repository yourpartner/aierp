using Server.Infrastructure.Modules;

namespace Server.Modules.Standard;

/// <summary>
/// 固定资产模块 - 标准版
/// </summary>
public class FixedAssetStandardModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "fixed_assets",
        Name = "固定资产管理",
        Description = "资产分类、资产台账、折旧计算等功能",
        Category = ModuleCategory.Standard,
        Version = "1.0.0",
        Dependencies = new[] { "finance_core" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_fixed_assets", Label = "menu.fixedAssets", Icon = "Suitcase", Path = "", ParentId = "menu_finance", Order = 150 },
            new MenuConfig { Id = "menu_asset_classes", Label = "menu.assetClasses", Icon = "Collection", Path = "/fixed-assets/classes", ParentId = "menu_finance", Order = 151 },
            new MenuConfig { Id = "menu_assets_list", Label = "menu.assetsList", Icon = "List", Path = "/fixed-assets/list", ParentId = "menu_finance", Order = 152 },
            new MenuConfig { Id = "menu_depreciation", Label = "menu.depreciation", Icon = "TrendCharts", Path = "/fixed-assets/depreciation", ParentId = "menu_finance", Order = 153 },
        }
    };
    
    public override void MapEndpoints(WebApplication app)
    {
        FixedAssetModule.MapFixedAssetModule(app);
    }
}

