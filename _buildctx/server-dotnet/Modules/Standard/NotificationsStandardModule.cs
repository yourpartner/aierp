using Server.Infrastructure.Modules;

namespace Server.Modules.Standard;

/// <summary>
/// 通知模块 - 标准版
/// </summary>
public class NotificationsStandardModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "notifications",
        Name = "通知管理",
        Description = "推送通知、通知策略等功能",
        Category = ModuleCategory.Standard,
        Version = "1.0.0",
        Dependencies = Array.Empty<string>(),
        Menus = new[]
        {
            new MenuConfig { Id = "menu_notifications", Label = "menu.notifications", Icon = "Bell", Path = "", ParentId = "menu_system", Order = 910 },
            new MenuConfig { Id = "menu_notification_runs", Label = "menu.notificationRuns", Icon = "Clock", Path = "/notifications/runs", ParentId = "menu_system", Order = 911 },
            new MenuConfig { Id = "menu_notification_logs", Label = "menu.notificationLogs", Icon = "Document", Path = "/notifications/logs", ParentId = "menu_system", Order = 912 },
        }
    };
    
    public override void MapEndpoints(WebApplication app)
    {
        NotificationsModule.MapNotificationsModule(app);
        NotificationsPoliciesModule.MapNotificationsPoliciesModule(app);
    }
}

