using Server.Infrastructure.Modules;

namespace Server.Modules.Standard;

/// <summary>
/// 企业微信模块 - 标准版
/// </summary>
public class WeComStandardModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "wecom",
        Name = "企业微信集成",
        Description = "企业微信消息推送、AI客服等功能",
        Category = ModuleCategory.Standard,
        Version = "1.0.0",
        Dependencies = new[] { "ai_core" },
        Menus = Array.Empty<MenuConfig>() // 后台模块，无前端菜单
    };
    
    public override void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<WeChatCustomerMappingService>();
        services.AddHostedService<AiLearningBackgroundService>();
    }
    
    public override void MapEndpoints(WebApplication app)
    {
        WeComMessageModule.MapWeComMessageModule(app);
        WeComChatbotModule.MapWeComChatbotModule(app);
    }
}

