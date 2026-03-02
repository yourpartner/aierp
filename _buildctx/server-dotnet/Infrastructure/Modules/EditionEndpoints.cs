using System.Text.Json;

namespace Server.Infrastructure.Modules;

/// <summary>
/// Edition API 端点 - 提供版本和模块信息给前端
/// </summary>
public static class EditionEndpoints
{
    public static void MapEditionEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<ModuleRegistry>();
        var options = app.Services.GetRequiredService<EditionOptions>();
        
        // 获取当前版本信息和启用的模块
        app.MapGet("/edition", () =>
        {
            return Results.Ok(registry.GetEditionInfo());
        }).AllowAnonymous();
        
        // 获取启用的模块ID列表
        app.MapGet("/edition/modules", () =>
        {
            return Results.Ok(new
            {
                modules = registry.GetEnabledModuleIds()
            });
        }).AllowAnonymous();
        
        // 获取所有菜单配置
        app.MapGet("/edition/menus", () =>
        {
            var menus = registry.GetEnabledMenus().Select(m => new
            {
                id = m.Id,
                label = m.Label,
                icon = m.Icon,
                path = m.Path,
                parentId = m.ParentId,
                order = m.Order,
                permission = m.Permission
            });
            return Results.Ok(new { menus });
        }).AllowAnonymous();
        
        // 调试端点：获取所有已注册模块（仅在DebugMode启用时可用）
        app.MapGet("/edition/debug/all-modules", () =>
        {
            if (!options.DebugMode)
            {
                return Results.Forbid();
            }
            
            var allModules = registry.GetAllModules().Select(m => new
            {
                id = m.Id,
                name = m.Name,
                description = m.Description,
                category = m.Category.ToString(),
                version = m.Version,
                dependencies = m.Dependencies,
                enabled = registry.IsModuleEnabled(m.Id)
            });
            
            return Results.Ok(new { modules = allModules });
        }).AllowAnonymous();
    }
}

