using Server.Infrastructure.Modules;
using Server.Infrastructure.Skills;
using Server.Modules.AgentKit.Tools;

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
        // 注册 AgentToolRegistry（Scoped，因为需要在每个请求中注册工具）
        services.AddScoped<AgentToolRegistry>();
        
        // 注册各个 Agent 工具（Scoped，因为依赖数据库连接等）
        // 注意：需与 BuildToolDefinitions() 保持一致
        services.AddScoped<CheckAccountingPeriodTool>();
        services.AddScoped<VerifyInvoiceRegistrationTool>();
        services.AddScoped<LookupCustomerTool>();
        services.AddScoped<LookupMaterialTool>();
        services.AddScoped<LookupAccountTool>();
        services.AddScoped<LookupVendorTool>();
        services.AddScoped<SearchVendorReceiptsTool>();
        services.AddScoped<GetExpenseAccountOptionsTool>();
        services.AddScoped<CreateVendorInvoiceTool>();
        services.AddScoped<GetVoucherByNumberTool>();
        services.AddScoped<ExtractBookingSettlementDataTool>();
        services.AddScoped<FindMoneytreeDepositForSettlementTool>();
        services.AddScoped<PreflightCheckTool>();
        services.AddScoped<CalculatePayrollTool>();
        services.AddScoped<SavePayrollTool>();
        services.AddScoped<GetPayrollHistoryTool>();
        services.AddScoped<GetMyPayrollTool>();
        services.AddScoped<GetPayrollComparisonTool>();
        services.AddScoped<GetDepartmentSummaryTool>();
        
        // 注册核心服务
        services.AddScoped<AgentKitService>();
        services.AddScoped<AgentScenarioService>();
        services.AddScoped<AgentAccountingRuleService>();
        services.AddScoped<WorkflowRulesService>();
        services.AddScoped<AiChatbotService>();
        services.AddScoped<AiLearningService>();
        
        // AI Skills + Learning Framework (Phase 1)
        services.AddScoped<HistoricalPatternService>();
        services.AddScoped<LearningEventCollector>();
        services.AddScoped<SkillContextBuilder>();
        services.AddScoped<MessageTaskRouter>();
    }
}

