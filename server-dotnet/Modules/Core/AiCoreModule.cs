using Server.Infrastructure.Modules;

namespace Server.Modules.Core;

/// <summary>
/// AI核心模块 - 包含AI对话、智能助手等功能
/// </summary>
public class AiCoreModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "ai_core",
        Name = "AI核心",
        Description = "AI对话助手、智能场景、自动化规则等核心AI功能",
        Category = ModuleCategory.Core,
        Version = "1.0.0",
        Dependencies = Array.Empty<string>(),
        Menus = new[]
        {
            new MenuConfig { Id = "menu_ai", Label = "menu.ai", Icon = "ChatLineSquare", Path = "", ParentId = null, Order = 10 },
            new MenuConfig { Id = "menu_chat", Label = "menu.chat", Icon = "ChatDotRound", Path = "/chat", ParentId = "menu_ai", Order = 11 },
            new MenuConfig { Id = "menu_agent_scenarios", Label = "menu.agentScenarios", Icon = "Collection", Path = "/ai/agent-scenarios", ParentId = "menu_ai", Order = 12 },
            new MenuConfig { Id = "menu_agent_rules", Label = "menu.agentRules", Icon = "SetUp", Path = "/ai/agent-rules", ParentId = "menu_ai", Order = 13 },
            new MenuConfig { Id = "menu_workflow_rules", Label = "menu.workflowRules", Icon = "Operation", Path = "/workflow/rules", ParentId = "menu_ai", Order = 14 },
        }
    };
    
    public override void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<AgentKitService>();
        services.AddScoped<AgentScenarioService>();
        services.AddScoped<AgentAccountingRuleService>();
        services.AddScoped<WorkflowRulesService>();
        services.AddScoped<AiChatbotService>();
        services.AddScoped<AiLearningService>();
    }
}

