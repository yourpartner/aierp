using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Npgsql;
using Server.Infrastructure;
using Server.Infrastructure.Modules;
using Server.Modules;

namespace Server.Modules.Core;

/// <summary>
/// 认证核心模块 - 菜单 + 用户/角色/权限管理端点
/// </summary>
public class AuthCoreModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "auth_core",
        Name = "认证核心",
        Description = "用户管理、角色权限、登录认证等核心功能",
        Category = ModuleCategory.Core,
        Version = "1.0.0",
        Dependencies = Array.Empty<string>(),
        Menus = new[]
        {
            new MenuConfig { Id = "menu_system", Label = "menu.system", Icon = "Setting", Path = "", ParentId = null, Order = 900 },
            new MenuConfig { Id = "menu_users", Label = "menu.users", Icon = "User", Path = "/system/users", ParentId = "menu_system", Order = 901 },
            new MenuConfig { Id = "menu_roles", Label = "menu.roles", Icon = "Key", Path = "/system/roles", ParentId = "menu_system", Order = 902 },
            new MenuConfig { Id = "menu_company_settings", Label = "menu.companySettings", Icon = "OfficeBuilding", Path = "/company/settings", ParentId = "menu_system", Order = 903 },
        }
    };
    
    public override void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<PermissionService>();
    }

    public override void MapEndpoints(WebApplication app)
    {
        MapPermissionEndpoints(app);
        MapCompanySettingsEndpoints(app);
        MapUserEndpoints(app);
        MapRoleEndpoints(app);
    }

    private static void MapPermissionEndpoints(WebApplication app)
    {
        // NOTE: Many pages call backend directly with /api prefix (bypassing Vite proxy),
        // so we map both /xxx and /api/xxx for compatibility.

        app.MapGet("/permissions/modules", async (HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "roles:manage")) return Results.StatusCode(403);
            return Results.Ok(await svc.GetModulesAsync());
        }).RequireAuthorization();
        app.MapGet("/api/permissions/modules", async (HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "roles:manage")) return Results.StatusCode(403);
            return Results.Ok(await svc.GetModulesAsync());
        }).RequireAuthorization();

        app.MapGet("/permissions/caps", async (HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "roles:manage")) return Results.StatusCode(403);
            var moduleCode = req.Query["moduleCode"].FirstOrDefault();
            return Results.Ok(await svc.GetCapsAsync(string.IsNullOrWhiteSpace(moduleCode) ? null : moduleCode));
        }).RequireAuthorization();
        app.MapGet("/api/permissions/caps", async (HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "roles:manage")) return Results.StatusCode(403);
            var moduleCode = req.Query["moduleCode"].FirstOrDefault();
            return Results.Ok(await svc.GetCapsAsync(string.IsNullOrWhiteSpace(moduleCode) ? null : moduleCode));
        }).RequireAuthorization();

        app.MapGet("/permissions/menus", async (HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "roles:manage")) return Results.StatusCode(403);
            var moduleCode = req.Query["moduleCode"].FirstOrDefault();
            return Results.Ok(await svc.GetMenusAsync(string.IsNullOrWhiteSpace(moduleCode) ? null : moduleCode));
        }).RequireAuthorization();
        app.MapGet("/api/permissions/menus", async (HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "roles:manage")) return Results.StatusCode(403);
            var moduleCode = req.Query["moduleCode"].FirstOrDefault();
            return Results.Ok(await svc.GetMenusAsync(string.IsNullOrWhiteSpace(moduleCode) ? null : moduleCode));
        }).RequireAuthorization();

        // 供前端过滤侧边栏菜单使用：返回当前用户可访问的 menu_key 列表
        // 前端请求 /api/permissions/accessible-menus -> 后端实际路径 /permissions/accessible-menus
        app.MapGet("/permissions/accessible-menus", async (HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });
            if (string.IsNullOrWhiteSpace(user.UserId) || !Guid.TryParse(user.UserId, out var uid))
                return Results.Ok(Array.Empty<string>());

            var menus = await svc.GetAccessibleMenusAsync(user.CompanyCode, uid);
            return Results.Ok(menus);
        }).RequireAuthorization();
        app.MapGet("/api/permissions/accessible-menus", async (HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });
            if (string.IsNullOrWhiteSpace(user.UserId) || !Guid.TryParse(user.UserId, out var uid))
                return Results.Ok(Array.Empty<string>());

            var menus = await svc.GetAccessibleMenusAsync(user.CompanyCode, uid);
            return Results.Ok(menus);
        }).RequireAuthorization();
    }

    private static void MapCompanySettingsEndpoints(WebApplication app)
    {
        // Restore-compatible endpoints (were previously in Program.cs.bak):
        // GET/POST /company/settings
        app.MapGet("/company/settings", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT payload FROM company_settings WHERE company_code=$1 LIMIT 1";
            cmd.Parameters.AddWithValue(cc.ToString());
            var txt = (string?)await cmd.ExecuteScalarAsync(req.HttpContext.RequestAborted);
            if (string.IsNullOrWhiteSpace(txt))
            {
                return Results.Ok(new
                {
                    payload = new { workdayDefaultStart = "09:00", workdayDefaultEnd = "18:00", lunchMinutes = 60 }
                });
            }
            return Results.Text(txt!, "application/json");
        }).RequireAuthorization();
        app.MapGet("/api/company/settings", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT payload FROM company_settings WHERE company_code=$1 LIMIT 1";
            cmd.Parameters.AddWithValue(cc.ToString());
            var txt = (string?)await cmd.ExecuteScalarAsync(req.HttpContext.RequestAborted);
            if (string.IsNullOrWhiteSpace(txt))
            {
                return Results.Ok(new
                {
                    payload = new { workdayDefaultStart = "09:00", workdayDefaultEnd = "18:00", lunchMinutes = 60 }
                });
            }
            return Results.Text(txt!, "application/json");
        }).RequireAuthorization();

        app.MapPost("/company/settings", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
            var payload = doc.RootElement.GetRawText();
            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO company_settings(company_code, payload) VALUES ($1, $2::jsonb)
                                ON CONFLICT (company_code) DO UPDATE SET payload=$2::jsonb, updated_at=now()
                                RETURNING payload";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(payload);
            var txt = (string?)await cmd.ExecuteScalarAsync(req.HttpContext.RequestAborted);
            return Results.Text(txt ?? payload, "application/json");
        }).RequireAuthorization();
        app.MapPost("/api/company/settings", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
            var payload = doc.RootElement.GetRawText();
            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO company_settings(company_code, payload) VALUES ($1, $2::jsonb)
                                ON CONFLICT (company_code) DO UPDATE SET payload=$2::jsonb, updated_at=now()
                                RETURNING payload";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(payload);
            var txt = (string?)await cmd.ExecuteScalarAsync(req.HttpContext.RequestAborted);
            return Results.Text(txt ?? payload, "application/json");
        }).RequireAuthorization();
    }

    private static void MapUserEndpoints(WebApplication app)
    {
        // NOTE: 前端走 /api/users，经 Vite 代理 rewrite 后会打到后端 /users
        app.MapGet("/users", async (HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "user:manage")) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });

            var offset = int.TryParse(req.Query["offset"], out var o) ? o : 0;
            var limit = int.TryParse(req.Query["limit"], out var l) ? Math.Min(l, 500) : 50;
            var userType = req.Query["userType"].FirstOrDefault();
            bool? isActive = null;
            if (bool.TryParse(req.Query["isActive"].FirstOrDefault(), out var ia)) isActive = ia;

            var (users, total) = await svc.GetUsersAsync(user.CompanyCode, string.IsNullOrWhiteSpace(userType) ? null : userType, isActive, offset, limit);
            var rows = users.Select(u => new
            {
                id = u.Id,
                employeeCode = u.EmployeeCode,
                name = u.Name ?? u.EmployeeName,
                userType = u.UserType,
                externalType = u.ExternalType,
                email = u.Email,
                phone = u.Phone,
                isActive = u.IsActive,
                employeeId = u.EmployeeId,
                createdAt = u.CreatedAt,
                lastLoginAt = u.LastLoginAt,
                roleCodes = u.RoleCodes ?? Array.Empty<string>()
            }).ToList();

            return Results.Ok(new { users = rows, total });
        }).RequireAuthorization();
        app.MapGet("/api/users", async (HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "user:manage")) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });

            var offset = int.TryParse(req.Query["offset"], out var o) ? o : 0;
            var limit = int.TryParse(req.Query["limit"], out var l) ? Math.Min(l, 500) : 50;
            var userType = req.Query["userType"].FirstOrDefault();
            bool? isActive = null;
            if (bool.TryParse(req.Query["isActive"].FirstOrDefault(), out var ia)) isActive = ia;

            var (users, total) = await svc.GetUsersAsync(user.CompanyCode, string.IsNullOrWhiteSpace(userType) ? null : userType, isActive, offset, limit);
            var rows = users.Select(u => new
            {
                id = u.Id,
                employeeCode = u.EmployeeCode,
                name = u.Name ?? u.EmployeeName,
                userType = u.UserType,
                externalType = u.ExternalType,
                email = u.Email,
                phone = u.Phone,
                isActive = u.IsActive,
                employeeId = u.EmployeeId,
                createdAt = u.CreatedAt,
                lastLoginAt = u.LastLoginAt,
                roleCodes = u.RoleCodes ?? Array.Empty<string>()
            }).ToList();

            return Results.Ok(new { users = rows, total });
        }).RequireAuthorization();

        app.MapGet("/users/{id:guid}", async (Guid id, HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "user:manage")) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });

            var u = await svc.GetUserByIdAsync(user.CompanyCode, id);
            if (u is null) return Results.NotFound(new { error = "User not found" });

            return Results.Ok(new
            {
                id = u.Id,
                employeeCode = u.EmployeeCode,
                name = u.Name ?? u.EmployeeName,
                userType = u.UserType,
                externalType = u.ExternalType,
                email = u.Email,
                phone = u.Phone,
                isActive = u.IsActive,
                employeeId = u.EmployeeId,
                createdAt = u.CreatedAt,
                lastLoginAt = u.LastLoginAt,
                roleCodes = u.RoleCodes ?? Array.Empty<string>()
            });
        }).RequireAuthorization();
        app.MapGet("/api/users/{id:guid}", async (Guid id, HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "user:manage")) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });

            var u = await svc.GetUserByIdAsync(user.CompanyCode, id);
            if (u is null) return Results.NotFound(new { error = "User not found" });

            return Results.Ok(new
            {
                id = u.Id,
                employeeCode = u.EmployeeCode,
                name = u.Name ?? u.EmployeeName,
                userType = u.UserType,
                externalType = u.ExternalType,
                email = u.Email,
                phone = u.Phone,
                isActive = u.IsActive,
                employeeId = u.EmployeeId,
                createdAt = u.CreatedAt,
                lastLoginAt = u.LastLoginAt,
                roleCodes = u.RoleCodes ?? Array.Empty<string>()
            });
        }).RequireAuthorization();

        app.MapPost("/users", async ([FromBody] CreateUserPayload payload, HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "user:manage")) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });
            if (string.IsNullOrWhiteSpace(payload.EmployeeCode) || string.IsNullOrWhiteSpace(payload.Password))
                return Results.BadRequest(new { error = "employeeCode and password required" });

            var id = await svc.CreateUserAsync(new PermissionService.CreateUserRequest
            {
                CompanyCode = user.CompanyCode,
                EmployeeCode = payload.EmployeeCode,
                Password = payload.Password,
                Name = payload.Name,
                DeptId = payload.DeptId,
                UserType = payload.UserType,
                EmployeeId = payload.EmployeeId,
                Email = payload.Email,
                Phone = payload.Phone,
                ExternalType = payload.ExternalType,
                IsActive = payload.IsActive,
                RoleCodes = payload.RoleCodes
            });
            return Results.Ok(new { id });
        }).RequireAuthorization();
        app.MapPost("/api/users", async ([FromBody] CreateUserPayload payload, HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "user:manage")) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });
            if (string.IsNullOrWhiteSpace(payload.EmployeeCode) || string.IsNullOrWhiteSpace(payload.Password))
                return Results.BadRequest(new { error = "employeeCode and password required" });

            var id = await svc.CreateUserAsync(new PermissionService.CreateUserRequest
            {
                CompanyCode = user.CompanyCode,
                EmployeeCode = payload.EmployeeCode,
                Password = payload.Password,
                Name = payload.Name,
                DeptId = payload.DeptId,
                UserType = payload.UserType,
                EmployeeId = payload.EmployeeId,
                Email = payload.Email,
                Phone = payload.Phone,
                ExternalType = payload.ExternalType,
                IsActive = payload.IsActive,
                RoleCodes = payload.RoleCodes
            });
            return Results.Ok(new { id });
        }).RequireAuthorization();

        app.MapPut("/users/{id:guid}", async (Guid id, [FromBody] UpdateUserPayload payload, HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "user:manage")) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });

            var ok = await svc.UpdateUserAsync(user.CompanyCode, id, new PermissionService.UpdateUserRequest
            {
                Name = payload.Name,
                DeptId = payload.DeptId,
                Email = payload.Email,
                Phone = payload.Phone,
                IsActive = payload.IsActive,
                ExternalType = payload.ExternalType,
                Password = payload.Password,
                RoleCodes = payload.RoleCodes,
                EmployeeId = payload.EmployeeId,
                ClearEmployeeId = payload.EmployeeId is null
            });
            return ok ? Results.Ok(new { success = true }) : Results.NotFound(new { error = "User not found" });
        }).RequireAuthorization();
        app.MapPut("/api/users/{id:guid}", async (Guid id, [FromBody] UpdateUserPayload payload, HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "user:manage")) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });

            var ok = await svc.UpdateUserAsync(user.CompanyCode, id, new PermissionService.UpdateUserRequest
            {
                Name = payload.Name,
                DeptId = payload.DeptId,
                Email = payload.Email,
                Phone = payload.Phone,
                IsActive = payload.IsActive,
                ExternalType = payload.ExternalType,
                Password = payload.Password,
                RoleCodes = payload.RoleCodes,
                EmployeeId = payload.EmployeeId,
                ClearEmployeeId = payload.EmployeeId is null
            });
            return ok ? Results.Ok(new { success = true }) : Results.NotFound(new { error = "User not found" });
        }).RequireAuthorization();

        app.MapDelete("/users/{id:guid}", async (Guid id, HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "user:manage")) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });

            var ok = await svc.DeleteUserAsync(user.CompanyCode, id);
            return ok ? Results.Ok(new { success = true }) : Results.NotFound(new { error = "User not found" });
        }).RequireAuthorization();
        app.MapDelete("/api/users/{id:guid}", async (Guid id, HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "user:manage")) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });

            var ok = await svc.DeleteUserAsync(user.CompanyCode, id);
            return ok ? Results.Ok(new { success = true }) : Results.NotFound(new { error = "User not found" });
        }).RequireAuthorization();
    }

    private static void MapRoleEndpoints(WebApplication app)
    {
        // NOTE: 前端走 /api/roles，经 Vite 代理 rewrite 后会打到后端 /roles
        app.MapGet("/roles", async (HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "roles:manage")) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });

            var roles = await svc.GetRolesAsync(user.CompanyCode, includeSystem: true);
            var rows = roles.Select(r => new
            {
                id = r.Id,
                roleCode = r.RoleCode,
                roleName = r.RoleName,
                description = r.Description,
                roleType = r.RoleType,
                isActive = r.IsActive,
                createdAt = r.CreatedAt,
                caps = r.Caps ?? Array.Empty<string>(),
                userCount = r.UserCount
            }).ToList();
            return Results.Ok(rows);
        }).RequireAuthorization();
        app.MapGet("/api/roles", async (HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "roles:manage")) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });

            var roles = await svc.GetRolesAsync(user.CompanyCode, includeSystem: true);
            var rows = roles.Select(r => new
            {
                id = r.Id,
                roleCode = r.RoleCode,
                roleName = r.RoleName,
                description = r.Description,
                roleType = r.RoleType,
                isActive = r.IsActive,
                createdAt = r.CreatedAt,
                caps = r.Caps ?? Array.Empty<string>(),
                userCount = r.UserCount
            }).ToList();
            return Results.Ok(rows);
        }).RequireAuthorization();

        app.MapGet("/roles/{id:guid}", async (Guid id, HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "roles:manage")) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });

            var role = await svc.GetRoleByIdAsync(user.CompanyCode, id);
            if (role is null) return Results.NotFound(new { error = "Role not found" });
            var dataScopes = await svc.GetRoleDataScopesAsync(id);

            return Results.Ok(new
            {
                role = new
                {
                    id = role.Id,
                    roleCode = role.RoleCode,
                    roleName = role.RoleName,
                    description = role.Description,
                    roleType = role.RoleType,
                    isActive = role.IsActive,
                    createdAt = role.CreatedAt,
                    caps = role.Caps ?? Array.Empty<string>(),
                    userCount = role.UserCount
                },
                dataScopes = dataScopes.Select(ds => new { entityType = ds.EntityType, scopeType = ds.ScopeType, scopeFilter = ds.ScopeFilter }).ToList()
            });
        }).RequireAuthorization();

        app.MapPost("/roles", async ([FromBody] CreateRolePayload payload, HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "roles:manage")) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });
            if (string.IsNullOrWhiteSpace(payload.RoleCode)) return Results.BadRequest(new { error = "roleCode required" });

            var id = await svc.CreateRoleAsync(new PermissionService.CreateRoleRequest
            {
                CompanyCode = user.CompanyCode,
                RoleCode = payload.RoleCode,
                RoleName = payload.RoleName,
                Description = payload.Description,
                RoleType = payload.RoleType,
                SourcePrompt = payload.SourcePrompt,
                IsActive = payload.IsActive,
                Caps = payload.Caps,
                DataScopes = payload.DataScopes?.Select(ds => new PermissionService.DataScopeInput
                {
                    EntityType = ds.EntityType,
                    ScopeType = ds.ScopeType,
                    ScopeFilter = ds.ScopeFilter
                }).ToArray()
            });

            return Results.Ok(new { id });
        }).RequireAuthorization();

        app.MapPut("/roles/{id:guid}", async (Guid id, [FromBody] UpdateRolePayload payload, HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "roles:manage")) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });

            var ok = await svc.UpdateRoleAsync(user.CompanyCode, id, new PermissionService.UpdateRoleRequest
            {
                RoleName = payload.RoleName,
                Description = payload.Description,
                IsActive = payload.IsActive,
                SourcePrompt = payload.SourcePrompt,
                Caps = payload.Caps,
                DataScopes = payload.DataScopes?.Select(ds => new PermissionService.DataScopeInput
                {
                    EntityType = ds.EntityType,
                    ScopeType = ds.ScopeType,
                    ScopeFilter = ds.ScopeFilter
                }).ToArray()
            });

            return ok ? Results.Ok(new { success = true }) : Results.NotFound(new { error = "Role not found" });
        }).RequireAuthorization();

        app.MapDelete("/roles/{id:guid}", async (Guid id, HttpRequest req, PermissionService svc) =>
        {
            var user = Auth.GetUserCtx(req);
            if (!HasCap(user, "roles:manage")) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(user.CompanyCode)) return Results.BadRequest(new { error = "Missing x-company-code" });

            var ok = await svc.DeleteRoleAsync(user.CompanyCode, id);
            return ok ? Results.Ok(new { success = true }) : Results.BadRequest(new { error = "Cannot delete role (builtin or in use)" });
        }).RequireAuthorization();

        // ====== AI role endpoints (front-end expects these; if AI backend isn't wired, return 501 instead of 405) ======
        app.MapPost("/roles/ai-generate", async (HttpRequest req) =>
        {
            return Results.StatusCode(501);
        }).RequireAuthorization();

        app.MapPost("/roles/ai-apply", async (HttpRequest req) =>
        {
            return Results.StatusCode(501);
        }).RequireAuthorization();

        app.MapPost("/roles/ai-check", async (HttpRequest req) =>
        {
            return Results.StatusCode(501);
        }).RequireAuthorization();

        app.MapGet("/roles/ai-audit", async () =>
        {
            return Results.StatusCode(501);
        }).RequireAuthorization();
    }

    private static bool HasCap(Auth.UserCtx user, string cap)
        => user.Caps.Contains(cap, StringComparer.OrdinalIgnoreCase);

    public sealed record DataScopePayload(string EntityType, string ScopeType, string? ScopeFilter);

    public sealed record CreateUserPayload(
        string EmployeeCode,
        string Password,
        string? Name,
        string? DeptId,
        string? UserType,
        Guid? EmployeeId,
        string? Email,
        string? Phone,
        string? ExternalType,
        bool? IsActive,
        string[]? RoleCodes);

    public sealed record UpdateUserPayload(
        string? Name,
        string? DeptId,
        string? Email,
        string? Phone,
        bool? IsActive,
        string? ExternalType,
        string? Password,
        string[]? RoleCodes,
        Guid? EmployeeId);

    public sealed record CreateRolePayload(
        string RoleCode,
        string? RoleName,
        string? Description,
        string? RoleType,
        string? SourcePrompt,
        bool? IsActive,
        string[]? Caps,
        DataScopePayload[]? DataScopes);

    public sealed record UpdateRolePayload(
        string? RoleName,
        string? Description,
        bool? IsActive,
        string? SourcePrompt,
        string[]? Caps,
        DataScopePayload[]? DataScopes);
}


