namespace Server.Infrastructure.Modules;

/// <summary>
/// 模块分类，用于区分不同行业版本的模块
/// </summary>
public enum ModuleCategory
{
    /// <summary>核心模块 - 所有版本都需要</summary>
    Core,
    /// <summary>标准版模块</summary>
    Standard,
    /// <summary>人才派遣版模块</summary>
    Staffing,
    /// <summary>零售版模块</summary>
    Retail,
    /// <summary>通用扩展模块</summary>
    Extension
}

/// <summary>
/// 模块元数据，描述模块的基本信息
/// </summary>
public class ModuleInfo
{
    /// <summary>模块唯一标识符</summary>
    public required string Id { get; init; }
    
    /// <summary>模块显示名称</summary>
    public required string Name { get; init; }
    
    /// <summary>模块描述</summary>
    public string? Description { get; init; }
    
    /// <summary>模块分类</summary>
    public ModuleCategory Category { get; init; } = ModuleCategory.Standard;
    
    /// <summary>模块版本</summary>
    public string Version { get; init; } = "1.0.0";
    
    /// <summary>依赖的其他模块ID列表</summary>
    public string[] Dependencies { get; init; } = Array.Empty<string>();
    
    /// <summary>前端菜单配置</summary>
    public MenuConfig[] Menus { get; init; } = Array.Empty<MenuConfig>();
}

/// <summary>
/// 前端菜单配置
/// </summary>
public class MenuConfig
{
    /// <summary>菜单唯一标识</summary>
    public required string Id { get; init; }
    
    /// <summary>菜单显示名称（支持i18n key）</summary>
    public required string Label { get; init; }
    
    /// <summary>菜单图标</summary>
    public string? Icon { get; init; }
    
    /// <summary>前端路由路径</summary>
    public required string Path { get; init; }
    
    /// <summary>父菜单ID（用于构建菜单树）</summary>
    public string? ParentId { get; init; }
    
    /// <summary>排序权重（越小越靠前）</summary>
    public int Order { get; init; } = 100;
    
    /// <summary>所需权限</summary>
    public string? Permission { get; init; }
}

/// <summary>
/// 模块接口 - 所有功能模块都应实现此接口
/// </summary>
public interface IModule
{
    /// <summary>获取模块元数据</summary>
    ModuleInfo GetInfo();
    
    /// <summary>注册模块服务到DI容器</summary>
    void RegisterServices(IServiceCollection services, IConfiguration configuration);
    
    /// <summary>映射模块的HTTP端点</summary>
    void MapEndpoints(WebApplication app);
}

/// <summary>
/// 模块基类，提供通用实现
/// </summary>
public abstract class ModuleBase : IModule
{
    public abstract ModuleInfo GetInfo();
    
    public virtual void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // 子类可覆盖以注册服务
    }
    
    public virtual void MapEndpoints(WebApplication app)
    {
        // 子类可覆盖以映射端点
    }
}

