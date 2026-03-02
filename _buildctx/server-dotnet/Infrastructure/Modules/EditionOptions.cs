namespace Server.Infrastructure.Modules;

/// <summary>
/// 版本类型枚举
/// </summary>
public enum EditionType
{
    /// <summary>标准版</summary>
    Standard,
    /// <summary>人才派遣版</summary>
    Staffing,
    /// <summary>零售版</summary>
    Retail
}

/// <summary>
/// 版本配置选项
/// </summary>
public class EditionOptions
{
    public const string SectionName = "Edition";
    
    /// <summary>版本类型</summary>
    public EditionType Type { get; set; } = EditionType.Standard;
    
    /// <summary>版本显示名称</summary>
    public string? DisplayName { get; set; }
    
    /// <summary>启用的模块ID列表（为空则根据版本类型自动选择）</summary>
    public string[] EnabledModules { get; set; } = Array.Empty<string>();
    
    /// <summary>禁用的模块ID列表（优先级高于EnabledModules）</summary>
    public string[] DisabledModules { get; set; } = Array.Empty<string>();
    
    /// <summary>是否启用调试模式（显示所有可用模块）</summary>
    public bool DebugMode { get; set; } = false;
}

