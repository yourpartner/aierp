using Server.Infrastructure.Modules;

namespace Server.Modules.Standard;

/// <summary>
/// 薪酬模块 - 标准版
/// </summary>
public class PayrollStandardModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "payroll",
        Name = "薪酬管理",
        Description = "工资计算、薪酬项目、工资历史等功能",
        Category = ModuleCategory.Standard,
        Version = "1.0.0",
        Dependencies = new[] { "hr_core", "finance_core" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_payroll", Label = "menu.payroll", Icon = "Wallet", Path = "", ParentId = "menu_hr", Order = 210 },
            new MenuConfig { Id = "menu_payroll_execute", Label = "menu.payrollExecute", Icon = "CircleCheck", Path = "/payroll-execute", ParentId = "menu_hr", Order = 212, Permission = "payroll.execute" },
            new MenuConfig { Id = "menu_payroll_history", Label = "menu.payrollHistory", Icon = "Clock", Path = "/payroll-history", ParentId = "menu_hr", Order = 213, Permission = "payroll.history" },
            new MenuConfig { Id = "hr.resident_tax", Label = "menu.residentTax", Icon = "Money", Path = "/hr/resident-tax", ParentId = "menu_hr", Order = 214, Permission = "payroll:resident_tax" },
        }
    };
    
    public override void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<PayrollService>();
        services.AddScoped<PayrollTaskService>();
        services.AddScoped<PayrollPreflightService>();
        services.AddSingleton<PayrollDeadlineService>();
        services.AddHostedService(sp => sp.GetRequiredService<PayrollDeadlineService>());
    }
    
    public override void MapEndpoints(WebApplication app)
    {
        app.MapHrPayrollModule();
    }
}

