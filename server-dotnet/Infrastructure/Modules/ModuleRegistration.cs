using Server.Modules.Core;
using Server.Modules.Standard;
using Server.Modules.Staffing;

namespace Server.Infrastructure.Modules;

/// <summary>
/// 模块注册配置 - 在此处注册所有可用模块
/// </summary>
public static class ModuleRegistration
{
    /// <summary>
    /// 注册所有可用模块到注册器
    /// </summary>
    public static void RegisterAllModules(ModuleRegistry registry)
    {
        // ============ 核心模块 ============
        registry.Register<FinanceCoreModule>();
        registry.Register<HrCoreModule>();
        registry.Register<AuthCoreModule>();
        registry.Register<AiCoreModule>();
        registry.Register<ObjectsCrudModule>();
        
        // ============ 标准版模块 ============
        registry.Register<InventoryStandardModule>();
        registry.Register<CrmStandardModule>();
        registry.Register<SalesStandardModule>();
        registry.Register<PurchaseStandardModule>();
        registry.Register<PayrollStandardModule>();
        registry.Register<FixedAssetStandardModule>();
        registry.Register<MoneytreeStandardModule>();
        registry.Register<FinanceExtStandardModule>();
        registry.Register<NotificationsStandardModule>();
        registry.Register<WeComStandardModule>();
        
        // ============ 人才派遣版模块 ============
        // Phase 1: リソース・案件・契約
        registry.Register<ResourcePoolModule>();
        registry.Register<StaffingProjectModule>();
        registry.Register<StaffingContractModule>();
        // Phase 2: 勤怠・請求
        registry.Register<StaffingTimesheetModule>();
        registry.Register<StaffingBillingModule>();
        // Phase 3: 分析レポート
        registry.Register<StaffingAnalyticsModule>();
        // Phase 4: 邮件自动化 & 员工门户
        registry.Register<EmailEngineModule>();
        registry.Register<StaffPortalModule>();
        // Phase 5: AI 智能助手
        registry.Register<StaffingAiModule>();
        
        // ============ 零售版模块 ============
        // 未来在此添加: registry.Register<RetailPosModule>();
    }
    
    /// <summary>
    /// 便捷扩展方法：添加模块系统并注册所有模块
    /// </summary>
    public static IServiceCollection AddErpModules(this IServiceCollection services, IConfiguration configuration)
    {
        return services.AddModuleSystem(configuration, RegisterAllModules);
    }
}

