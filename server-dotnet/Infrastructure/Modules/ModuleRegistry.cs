using Microsoft.Extensions.Options;

namespace Server.Infrastructure.Modules;

/// <summary>
/// 模块注册器 - 负责发现、注册和管理所有模块
/// </summary>
public class ModuleRegistry
{
    private readonly Dictionary<string, IModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModuleInfo> _moduleInfos = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _enabledModuleIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly EditionOptions _options;
    
    public ModuleRegistry(EditionOptions options)
    {
        _options = options;
    }
    
    /// <summary>
    /// 注册一个模块
    /// </summary>
    public ModuleRegistry Register<TModule>() where TModule : IModule, new()
    {
        var module = new TModule();
        var info = module.GetInfo();
        _modules[info.Id] = module;
        _moduleInfos[info.Id] = info;
        return this;
    }
    
    /// <summary>
    /// 注册一个模块实例
    /// </summary>
    public ModuleRegistry Register(IModule module)
    {
        var info = module.GetInfo();
        _modules[info.Id] = module;
        _moduleInfos[info.Id] = info;
        return this;
    }
    
    /// <summary>
    /// 根据配置决定启用哪些模块
    /// </summary>
    public void ResolveEnabledModules()
    {
        _enabledModuleIds.Clear();
        
        // 1. 核心模块始终启用
        foreach (var (id, info) in _moduleInfos)
        {
            if (info.Category == ModuleCategory.Core)
            {
                _enabledModuleIds.Add(id);
            }
        }
        
        // 2. 根据版本类型启用对应模块
        var targetCategory = _options.Type switch
        {
            EditionType.Staffing => ModuleCategory.Staffing,
            EditionType.Retail => ModuleCategory.Retail,
            _ => ModuleCategory.Standard
        };
        
        foreach (var (id, info) in _moduleInfos)
        {
            // 标准版模块在所有版本中都可用
            if (info.Category == ModuleCategory.Standard || 
                info.Category == targetCategory ||
                info.Category == ModuleCategory.Extension)
            {
                _enabledModuleIds.Add(id);
            }
        }
        
        // 3. 如果显式指定了启用模块，则仅启用指定的模块（加上核心模块）
        if (_options.EnabledModules.Length > 0)
        {
            var coreModules = _enabledModuleIds.Where(id => 
                _moduleInfos.TryGetValue(id, out var info) && info.Category == ModuleCategory.Core
            ).ToList();
            
            _enabledModuleIds.Clear();
            foreach (var id in coreModules)
            {
                _enabledModuleIds.Add(id);
            }
            foreach (var id in _options.EnabledModules)
            {
                if (_moduleInfos.ContainsKey(id))
                {
                    _enabledModuleIds.Add(id);
                }
            }
        }
        
        // 4. 应用禁用列表
        foreach (var id in _options.DisabledModules)
        {
            // 核心模块不允许禁用
            if (_moduleInfos.TryGetValue(id, out var info) && info.Category != ModuleCategory.Core)
            {
                _enabledModuleIds.Remove(id);
            }
        }
        
        // 5. 解决依赖关系
        ResolveDependencies();
    }
    
    private void ResolveDependencies()
    {
        var toEnable = new HashSet<string>(_enabledModuleIds, StringComparer.OrdinalIgnoreCase);
        var changed = true;
        
        while (changed)
        {
            changed = false;
            foreach (var id in toEnable.ToList())
            {
                if (_moduleInfos.TryGetValue(id, out var info))
                {
                    foreach (var depId in info.Dependencies)
                    {
                        if (!toEnable.Contains(depId) && _moduleInfos.ContainsKey(depId))
                        {
                            toEnable.Add(depId);
                            changed = true;
                        }
                    }
                }
            }
        }
        
        _enabledModuleIds.Clear();
        foreach (var id in toEnable)
        {
            _enabledModuleIds.Add(id);
        }
    }
    
    /// <summary>
    /// 检查模块是否启用
    /// </summary>
    public bool IsModuleEnabled(string moduleId) => _enabledModuleIds.Contains(moduleId);
    
    /// <summary>
    /// 获取所有启用的模块ID
    /// </summary>
    public IReadOnlyCollection<string> GetEnabledModuleIds() => _enabledModuleIds;
    
    /// <summary>
    /// 获取所有启用的模块信息
    /// </summary>
    public IEnumerable<ModuleInfo> GetEnabledModules()
    {
        return _enabledModuleIds
            .Where(id => _moduleInfos.ContainsKey(id))
            .Select(id => _moduleInfos[id]);
    }
    
    /// <summary>
    /// 获取所有已注册的模块信息（调试用）
    /// </summary>
    public IEnumerable<ModuleInfo> GetAllModules() => _moduleInfos.Values;
    
    /// <summary>
    /// 获取启用模块的所有菜单配置
    /// </summary>
    public IEnumerable<MenuConfig> GetEnabledMenus()
    {
        return GetEnabledModules()
            .SelectMany(m => m.Menus)
            .OrderBy(m => m.Order);
    }
    
    /// <summary>
    /// 注册所有启用模块的服务
    /// </summary>
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        foreach (var id in _enabledModuleIds)
        {
            if (_modules.TryGetValue(id, out var module))
            {
                try
                {
                    module.RegisterServices(services, configuration);
                    Console.WriteLine($"[ModuleRegistry] Registered services for module: {id}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ModuleRegistry] Failed to register services for module {id}: {ex.Message}");
                }
            }
        }
    }
    
    /// <summary>
    /// 映射所有启用模块的端点
    /// </summary>
    public void MapEndpoints(WebApplication app)
    {
        foreach (var id in _enabledModuleIds)
        {
            if (_modules.TryGetValue(id, out var module))
            {
                try
                {
                    module.MapEndpoints(app);
                    Console.WriteLine($"[ModuleRegistry] Mapped endpoints for module: {id}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ModuleRegistry] Failed to map endpoints for module {id}: {ex.Message}");
                }
            }
        }
    }
    
    /// <summary>
    /// 获取版本信息
    /// </summary>
    public object GetEditionInfo()
    {
        return new
        {
            type = _options.Type.ToString().ToLowerInvariant(),
            displayName = _options.DisplayName ?? _options.Type.ToString(),
            enabledModules = _enabledModuleIds.ToArray(),
            menus = GetEnabledMenus().Select(m => new
            {
                id = m.Id,
                label = m.Label,
                icon = m.Icon,
                path = m.Path,
                parentId = m.ParentId,
                order = m.Order,
                permission = m.Permission
            })
        };
    }
}

/// <summary>
/// 模块注册扩展方法
/// </summary>
public static class ModuleRegistryExtensions
{
    /// <summary>
    /// 添加模块系统支持
    /// </summary>
    public static IServiceCollection AddModuleSystem(
        this IServiceCollection services, 
        IConfiguration configuration,
        Action<ModuleRegistry> configureModules)
    {
        // 绑定配置
        var options = new EditionOptions();
        configuration.GetSection(EditionOptions.SectionName).Bind(options);
        
        // 创建注册器
        var registry = new ModuleRegistry(options);
        
        // 让调用者注册模块
        configureModules(registry);
        
        // 解析启用的模块
        registry.ResolveEnabledModules();
        
        // 注册模块服务
        registry.RegisterServices(services, configuration);
        
        // 将注册器注册为单例
        services.AddSingleton(registry);
        services.AddSingleton(options);
        
        return services;
    }
    
    /// <summary>
    /// 映射模块端点
    /// </summary>
    public static WebApplication UseModuleEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<ModuleRegistry>();
        registry.MapEndpoints(app);
        return app;
    }
}

