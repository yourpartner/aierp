using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Server.Modules;

/// <summary>
/// 权限管理服务 - 提供用户、角色、权限的CRUD操作
/// </summary>
public class PermissionService
{
    private readonly NpgsqlDataSource _ds;

    public PermissionService(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    #region Permission Modules & Caps

    /// <summary>获取所有权限模块</summary>
    public async Task<List<PermissionModule>> GetModulesAsync()
    {
        var list = new List<PermissionModule>();
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, module_code, module_name, icon, display_order, is_active 
                            FROM permission_modules 
                            WHERE is_active = true 
                            ORDER BY display_order";
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            list.Add(new PermissionModule
            {
                Id = rd.GetGuid(0),
                ModuleCode = rd.GetString(1),
                ModuleName = JsonDocument.Parse(rd.GetString(2)).RootElement,
                Icon = rd.IsDBNull(3) ? null : rd.GetString(3),
                DisplayOrder = rd.GetInt32(4),
                IsActive = rd.GetBoolean(5)
            });
        }
        return list;
    }

    /// <summary>获取所有权限能力</summary>
    public async Task<List<PermissionCap>> GetCapsAsync(string? moduleCode = null)
    {
        var list = new List<PermissionCap>();
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, cap_code, cap_name, module_code, cap_type, is_sensitive, description, display_order 
                            FROM permission_caps 
                            WHERE is_active = true " +
                            (moduleCode != null ? " AND module_code = $1" : "") +
                            " ORDER BY module_code, display_order";
        if (moduleCode != null) cmd.Parameters.AddWithValue(moduleCode);
        
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            list.Add(new PermissionCap
            {
                Id = rd.GetGuid(0),
                CapCode = rd.GetString(1),
                CapName = JsonDocument.Parse(rd.GetString(2)).RootElement,
                ModuleCode = rd.GetString(3),
                CapType = rd.GetString(4),
                IsSensitive = rd.GetBoolean(5),
                Description = rd.IsDBNull(6) ? null : JsonDocument.Parse(rd.GetString(6)).RootElement,
                DisplayOrder = rd.GetInt32(7)
            });
        }
        return list;
    }

    /// <summary>获取所有菜单定义</summary>
    public async Task<List<PermissionMenu>> GetMenusAsync(string? moduleCode = null)
    {
        var list = new List<PermissionMenu>();
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, module_code, menu_key, menu_name, menu_path, caps_required, display_order 
                            FROM permission_menus 
                            WHERE is_active = true " +
                            (moduleCode != null ? " AND module_code = $1" : "") +
                            " ORDER BY module_code, display_order";
        if (moduleCode != null) cmd.Parameters.AddWithValue(moduleCode);
        
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            list.Add(new PermissionMenu
            {
                Id = rd.GetGuid(0),
                ModuleCode = rd.GetString(1),
                MenuKey = rd.GetString(2),
                MenuName = JsonDocument.Parse(rd.GetString(3)).RootElement,
                MenuPath = rd.IsDBNull(4) ? null : rd.GetString(4),
                CapsRequired = rd.IsDBNull(5) ? Array.Empty<string>() : (string[])rd.GetValue(5),
                DisplayOrder = rd.GetInt32(6)
            });
        }
        return list;
    }

    #endregion

    #region Users

    /// <summary>获取用户列表</summary>
    public async Task<(List<UserInfo> Users, int Total)> GetUsersAsync(string companyCode, string? userType = null, bool? isActive = null, int offset = 0, int limit = 50)
    {
        var list = new List<UserInfo>();
        int total = 0;
        await using var conn = await _ds.OpenConnectionAsync();
        
        // Count
        await using (var cntCmd = conn.CreateCommand())
        {
            var cntSql = "SELECT COUNT(1) FROM users WHERE company_code = $1";
            var paramIdx = 2;
            if (userType != null) cntSql += $" AND user_type = ${paramIdx++}";
            if (isActive != null) cntSql += $" AND is_active = ${paramIdx++}";
            cntCmd.CommandText = cntSql;
            cntCmd.Parameters.AddWithValue(companyCode);
            if (userType != null) cntCmd.Parameters.AddWithValue(userType);
            if (isActive != null) cntCmd.Parameters.AddWithValue(isActive.Value);
            total = Convert.ToInt32(await cntCmd.ExecuteScalarAsync() ?? 0);
        }

        // Query
        await using var cmd = conn.CreateCommand();
        var sql = @"SELECT u.id, u.company_code, u.employee_code, u.name, u.dept_id, u.user_type, 
                           u.employee_id, u.email, u.phone, u.is_active, u.external_type, u.last_login_at, u.created_at,
                           COALESCE(e.name, u.name) as emp_name,
                           ARRAY_AGG(r.role_code) FILTER (WHERE r.role_code IS NOT NULL) as role_codes
                    FROM users u
                    LEFT JOIN employees e ON u.employee_id = e.id
                    LEFT JOIN user_roles ur ON ur.user_id = u.id
                    LEFT JOIN roles r ON r.id = ur.role_id
                    WHERE u.company_code = $1";
        var pIdx = 2;
        if (userType != null) sql += $" AND u.user_type = ${pIdx++}";
        if (isActive != null) sql += $" AND u.is_active = ${pIdx++}";
        sql += " GROUP BY u.id, e.name ORDER BY u.created_at DESC OFFSET $" + pIdx++ + " LIMIT $" + pIdx;
        
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(companyCode);
        if (userType != null) cmd.Parameters.AddWithValue(userType);
        if (isActive != null) cmd.Parameters.AddWithValue(isActive.Value);
        cmd.Parameters.AddWithValue(offset);
        cmd.Parameters.AddWithValue(limit);

        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            list.Add(new UserInfo
            {
                Id = rd.GetGuid(0),
                CompanyCode = rd.GetString(1),
                EmployeeCode = rd.GetString(2),
                Name = rd.IsDBNull(3) ? null : rd.GetString(3),
                DeptId = rd.IsDBNull(4) ? null : rd.GetString(4),
                UserType = rd.IsDBNull(5) ? "internal" : rd.GetString(5),
                EmployeeId = rd.IsDBNull(6) ? null : rd.GetGuid(6),
                Email = rd.IsDBNull(7) ? null : rd.GetString(7),
                Phone = rd.IsDBNull(8) ? null : rd.GetString(8),
                IsActive = rd.IsDBNull(9) || rd.GetBoolean(9),
                ExternalType = rd.IsDBNull(10) ? null : rd.GetString(10),
                LastLoginAt = rd.IsDBNull(11) ? null : rd.GetDateTime(11),
                CreatedAt = rd.GetDateTime(12),
                EmployeeName = rd.IsDBNull(13) ? null : rd.GetString(13),
                RoleCodes = rd.IsDBNull(14) ? Array.Empty<string>() : (string[])rd.GetValue(14)
            });
        }
        return (list, total);
    }

    /// <summary>获取单个用户详情</summary>
    public async Task<UserInfo?> GetUserByIdAsync(string companyCode, Guid userId)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT u.id, u.company_code, u.employee_code, u.name, u.dept_id, u.user_type, 
                                   u.employee_id, u.email, u.phone, u.is_active, u.external_type, u.last_login_at, u.created_at,
                                   COALESCE(e.name, u.name) as emp_name,
                                   ARRAY_AGG(r.role_code) FILTER (WHERE r.role_code IS NOT NULL) as role_codes
                            FROM users u
                            LEFT JOIN employees e ON u.employee_id = e.id
                            LEFT JOIN user_roles ur ON ur.user_id = u.id
                            LEFT JOIN roles r ON r.id = ur.role_id
                            WHERE u.company_code = $1 AND u.id = $2
                            GROUP BY u.id, e.name";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(userId);

        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;

        return new UserInfo
        {
            Id = rd.GetGuid(0),
            CompanyCode = rd.GetString(1),
            EmployeeCode = rd.GetString(2),
            Name = rd.IsDBNull(3) ? null : rd.GetString(3),
            DeptId = rd.IsDBNull(4) ? null : rd.GetString(4),
            UserType = rd.IsDBNull(5) ? "internal" : rd.GetString(5),
            EmployeeId = rd.IsDBNull(6) ? null : rd.GetGuid(6),
            Email = rd.IsDBNull(7) ? null : rd.GetString(7),
            Phone = rd.IsDBNull(8) ? null : rd.GetString(8),
            IsActive = rd.IsDBNull(9) || rd.GetBoolean(9),
            ExternalType = rd.IsDBNull(10) ? null : rd.GetString(10),
            LastLoginAt = rd.IsDBNull(11) ? null : rd.GetDateTime(11),
            CreatedAt = rd.GetDateTime(12),
            EmployeeName = rd.IsDBNull(13) ? null : rd.GetString(13),
            RoleCodes = rd.IsDBNull(14) ? Array.Empty<string>() : (string[])rd.GetValue(14)
        };
    }

    /// <summary>创建用户</summary>
    public async Task<Guid> CreateUserAsync(CreateUserRequest req)
    {
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(req.Password);
        
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO users(company_code, employee_code, password_hash, name, dept_id, user_type, employee_id, email, phone, external_type, is_active)
                            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                            RETURNING id";
        cmd.Parameters.AddWithValue(req.CompanyCode);
        cmd.Parameters.AddWithValue(req.EmployeeCode);
        cmd.Parameters.AddWithValue(passwordHash);
        cmd.Parameters.AddWithValue((object?)req.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)req.DeptId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(req.UserType ?? "internal");
        cmd.Parameters.AddWithValue(req.EmployeeId.HasValue ? (object)req.EmployeeId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue((object?)req.Email ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)req.Phone ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)req.ExternalType ?? DBNull.Value);
        cmd.Parameters.AddWithValue(req.IsActive ?? true);

        var userId = (Guid)(await cmd.ExecuteScalarAsync() ?? Guid.Empty);

        // 分配角色
        if (req.RoleCodes?.Length > 0)
        {
            await AssignRolesToUserAsync(conn, req.CompanyCode, userId, req.RoleCodes);
        }

        return userId;
    }

    /// <summary>更新用户</summary>
    public async Task<bool> UpdateUserAsync(string companyCode, Guid userId, UpdateUserRequest req)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        
        var updates = new List<string>();
        var pIdx = 3;
        if (req.Name != null) updates.Add($"name = ${pIdx++}");
        if (req.DeptId != null) updates.Add($"dept_id = ${pIdx++}");
        if (req.Email != null) updates.Add($"email = ${pIdx++}");
        if (req.Phone != null) updates.Add($"phone = ${pIdx++}");
        if (req.IsActive.HasValue) updates.Add($"is_active = ${pIdx++}");
        if (req.ExternalType != null) updates.Add($"external_type = ${pIdx++}");
        if (req.EmployeeId.HasValue || req.ClearEmployeeId) updates.Add($"employee_id = ${pIdx++}");
        updates.Add("updated_at = now()");

        if (updates.Count == 1) // 只有updated_at
        {
            // 至少更新角色
        }
        else
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE users SET {string.Join(", ", updates)} WHERE company_code = $1 AND id = $2";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(userId);
            if (req.Name != null) cmd.Parameters.AddWithValue(req.Name);
            if (req.DeptId != null) cmd.Parameters.AddWithValue(req.DeptId);
            if (req.Email != null) cmd.Parameters.AddWithValue(req.Email);
            if (req.Phone != null) cmd.Parameters.AddWithValue(req.Phone);
            if (req.IsActive.HasValue) cmd.Parameters.AddWithValue(req.IsActive.Value);
            if (req.ExternalType != null) cmd.Parameters.AddWithValue(req.ExternalType);
            if (req.EmployeeId.HasValue) cmd.Parameters.AddWithValue(req.EmployeeId.Value);
            else if (req.ClearEmployeeId) cmd.Parameters.AddWithValue(DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        // 更新角色
        if (req.RoleCodes != null)
        {
            // 删除旧的角色关联
            await using var delCmd = conn.CreateCommand();
            delCmd.CommandText = "DELETE FROM user_roles WHERE user_id = $1";
            delCmd.Parameters.AddWithValue(userId);
            await delCmd.ExecuteNonQueryAsync();

            if (req.RoleCodes.Length > 0)
            {
                await AssignRolesToUserAsync(conn, companyCode, userId, req.RoleCodes);
            }
        }

        // 更新密码
        if (!string.IsNullOrEmpty(req.Password))
        {
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(req.Password);
            await using var pwdCmd = conn.CreateCommand();
            pwdCmd.CommandText = "UPDATE users SET password_hash = $1 WHERE company_code = $2 AND id = $3";
            pwdCmd.Parameters.AddWithValue(passwordHash);
            pwdCmd.Parameters.AddWithValue(companyCode);
            pwdCmd.Parameters.AddWithValue(userId);
            await pwdCmd.ExecuteNonQueryAsync();
        }

        return true;
    }

    /// <summary>删除用户（软删除）</summary>
    public async Task<bool> DeleteUserAsync(string companyCode, Guid userId)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET is_active = false, updated_at = now() WHERE company_code = $1 AND id = $2";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(userId);
        var affected = await cmd.ExecuteNonQueryAsync();
        return affected > 0;
    }

    private async Task AssignRolesToUserAsync(NpgsqlConnection conn, string companyCode, Guid userId, string[] roleCodes)
    {
        foreach (var roleCode in roleCodes)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO user_roles(user_id, role_id)
                                SELECT $1, id FROM roles WHERE company_code = $2 AND role_code = $3
                                ON CONFLICT DO NOTHING";
            cmd.Parameters.AddWithValue(userId);
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(roleCode);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    #endregion

    #region Roles

    /// <summary>获取角色列表</summary>
    public async Task<List<RoleInfo>> GetRolesAsync(string companyCode, bool includeSystem = true)
    {
        var list = new List<RoleInfo>();
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT r.id, r.company_code, r.role_code, r.role_name, r.description, r.role_type, r.is_active, r.source_prompt, r.created_at,
                                   ARRAY_AGG(rc.cap) FILTER (WHERE rc.cap IS NOT NULL) as caps,
                                   (SELECT COUNT(1) FROM user_roles ur WHERE ur.role_id = r.id) as user_count
                            FROM roles r
                            LEFT JOIN role_caps rc ON rc.role_id = r.id
                            WHERE (r.company_code = $1 OR ($2 = true AND r.company_code IS NULL))
                              AND r.is_active = true
                            GROUP BY r.id
                            ORDER BY r.role_type, r.created_at";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(includeSystem);

        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            list.Add(new RoleInfo
            {
                Id = rd.GetGuid(0),
                CompanyCode = rd.IsDBNull(1) ? null : rd.GetString(1),
                RoleCode = rd.GetString(2),
                RoleName = rd.IsDBNull(3) ? null : rd.GetString(3),
                Description = rd.IsDBNull(4) ? null : rd.GetString(4),
                RoleType = rd.IsDBNull(5) ? "custom" : rd.GetString(5),
                IsActive = rd.IsDBNull(6) || rd.GetBoolean(6),
                SourcePrompt = rd.IsDBNull(7) ? null : rd.GetString(7),
                CreatedAt = rd.GetDateTime(8),
                Caps = rd.IsDBNull(9) ? Array.Empty<string>() : (string[])rd.GetValue(9),
                UserCount = Convert.ToInt32(rd.GetValue(10))
            });
        }
        return list;
    }

    /// <summary>获取单个角色详情</summary>
    public async Task<RoleInfo?> GetRoleByIdAsync(string companyCode, Guid roleId)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT r.id, r.company_code, r.role_code, r.role_name, r.description, r.role_type, r.is_active, r.source_prompt, r.created_at,
                                   ARRAY_AGG(rc.cap) FILTER (WHERE rc.cap IS NOT NULL) as caps,
                                   (SELECT COUNT(1) FROM user_roles ur WHERE ur.role_id = r.id) as user_count
                            FROM roles r
                            LEFT JOIN role_caps rc ON rc.role_id = r.id
                            WHERE (r.company_code = $1 OR r.company_code IS NULL) AND r.id = $2
                            GROUP BY r.id";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(roleId);

        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;

        return new RoleInfo
        {
            Id = rd.GetGuid(0),
            CompanyCode = rd.IsDBNull(1) ? null : rd.GetString(1),
            RoleCode = rd.GetString(2),
            RoleName = rd.IsDBNull(3) ? null : rd.GetString(3),
            Description = rd.IsDBNull(4) ? null : rd.GetString(4),
            RoleType = rd.IsDBNull(5) ? "custom" : rd.GetString(5),
            IsActive = rd.IsDBNull(6) || rd.GetBoolean(6),
            SourcePrompt = rd.IsDBNull(7) ? null : rd.GetString(7),
            CreatedAt = rd.GetDateTime(8),
            Caps = rd.IsDBNull(9) ? Array.Empty<string>() : (string[])rd.GetValue(9),
            UserCount = Convert.ToInt32(rd.GetValue(10))
        };
    }

    /// <summary>获取角色的数据范围配置</summary>
    public async Task<List<RoleDataScope>> GetRoleDataScopesAsync(Guid roleId)
    {
        var list = new List<RoleDataScope>();
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, role_id, entity_type, scope_type, scope_filter
                            FROM role_data_scopes WHERE role_id = $1";
        cmd.Parameters.AddWithValue(roleId);

        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            list.Add(new RoleDataScope
            {
                Id = rd.GetGuid(0),
                RoleId = rd.GetGuid(1),
                EntityType = rd.GetString(2),
                ScopeType = rd.GetString(3),
                ScopeFilter = rd.IsDBNull(4) ? null : JsonDocument.Parse(rd.GetString(4)).RootElement
            });
        }
        return list;
    }

    /// <summary>创建角色</summary>
    public async Task<Guid> CreateRoleAsync(CreateRoleRequest req)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO roles(company_code, role_code, role_name, description, role_type, source_prompt, is_active)
                            VALUES ($1, $2, $3, $4, $5, $6, $7)
                            RETURNING id";
        cmd.Parameters.AddWithValue(req.CompanyCode);
        cmd.Parameters.AddWithValue(req.RoleCode);
        cmd.Parameters.AddWithValue((object?)req.RoleName ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)req.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue(req.RoleType ?? "custom");
        cmd.Parameters.AddWithValue((object?)req.SourcePrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue(req.IsActive ?? true);

        var roleId = (Guid)(await cmd.ExecuteScalarAsync() ?? Guid.Empty);

        // 添加能力
        if (req.Caps?.Length > 0)
        {
            await SetRoleCapsAsync(conn, roleId, req.Caps);
        }

        // 添加数据范围
        if (req.DataScopes?.Length > 0)
        {
            await SetRoleDataScopesAsync(conn, roleId, req.DataScopes);
        }

        return roleId;
    }

    /// <summary>更新角色</summary>
    public async Task<bool> UpdateRoleAsync(string companyCode, Guid roleId, UpdateRoleRequest req)
    {
        await using var conn = await _ds.OpenConnectionAsync();

        // 检查是否为系统角色（不能修改系统角色的基本信息）
        await using (var chkCmd = conn.CreateCommand())
        {
            chkCmd.CommandText = "SELECT role_type FROM roles WHERE id = $1";
            chkCmd.Parameters.AddWithValue(roleId);
            var roleType = await chkCmd.ExecuteScalarAsync() as string;
            if (roleType == "builtin")
            {
                // 系统角色只能修改caps和data_scopes，不能修改基本信息
                if (req.Caps != null)
                {
                    await using var delCmd = conn.CreateCommand();
                    delCmd.CommandText = "DELETE FROM role_caps WHERE role_id = $1";
                    delCmd.Parameters.AddWithValue(roleId);
                    await delCmd.ExecuteNonQueryAsync();
                    await SetRoleCapsAsync(conn, roleId, req.Caps);
                }
                if (req.DataScopes != null)
                {
                    await using var delCmd = conn.CreateCommand();
                    delCmd.CommandText = "DELETE FROM role_data_scopes WHERE role_id = $1";
                    delCmd.Parameters.AddWithValue(roleId);
                    await delCmd.ExecuteNonQueryAsync();
                    await SetRoleDataScopesAsync(conn, roleId, req.DataScopes);
                }
                return true;
            }
        }

        // 更新角色基本信息
        var updates = new List<string>();
        var pIdx = 3;
        if (req.RoleName != null) updates.Add($"role_name = ${pIdx++}");
        if (req.Description != null) updates.Add($"description = ${pIdx++}");
        if (req.IsActive.HasValue) updates.Add($"is_active = ${pIdx++}");
        if (req.SourcePrompt != null) updates.Add($"source_prompt = ${pIdx++}");
        updates.Add("updated_at = now()");

        if (updates.Count > 1)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE roles SET {string.Join(", ", updates)} WHERE company_code = $1 AND id = $2";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(roleId);
            if (req.RoleName != null) cmd.Parameters.AddWithValue(req.RoleName);
            if (req.Description != null) cmd.Parameters.AddWithValue(req.Description);
            if (req.IsActive.HasValue) cmd.Parameters.AddWithValue(req.IsActive.Value);
            if (req.SourcePrompt != null) cmd.Parameters.AddWithValue(req.SourcePrompt);
            await cmd.ExecuteNonQueryAsync();
        }

        // 更新能力
        if (req.Caps != null)
        {
            await using var delCmd = conn.CreateCommand();
            delCmd.CommandText = "DELETE FROM role_caps WHERE role_id = $1";
            delCmd.Parameters.AddWithValue(roleId);
            await delCmd.ExecuteNonQueryAsync();
            await SetRoleCapsAsync(conn, roleId, req.Caps);
        }

        // 更新数据范围
        if (req.DataScopes != null)
        {
            await using var delCmd = conn.CreateCommand();
            delCmd.CommandText = "DELETE FROM role_data_scopes WHERE role_id = $1";
            delCmd.Parameters.AddWithValue(roleId);
            await delCmd.ExecuteNonQueryAsync();
            await SetRoleDataScopesAsync(conn, roleId, req.DataScopes);
        }

        return true;
    }

    /// <summary>删除角色</summary>
    public async Task<bool> DeleteRoleAsync(string companyCode, Guid roleId)
    {
        await using var conn = await _ds.OpenConnectionAsync();

        // 检查是否为系统角色
        await using (var chkCmd = conn.CreateCommand())
        {
            chkCmd.CommandText = "SELECT role_type FROM roles WHERE id = $1";
            chkCmd.Parameters.AddWithValue(roleId);
            var roleType = await chkCmd.ExecuteScalarAsync() as string;
            if (roleType == "builtin") return false; // 不能删除系统角色
        }

        // 检查是否有用户使用
        await using (var chkCmd = conn.CreateCommand())
        {
            chkCmd.CommandText = "SELECT COUNT(1) FROM user_roles WHERE role_id = $1";
            chkCmd.Parameters.AddWithValue(roleId);
            var cnt = Convert.ToInt64(await chkCmd.ExecuteScalarAsync() ?? 0);
            if (cnt > 0) return false; // 有用户使用，不能删除
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM roles WHERE company_code = $1 AND id = $2 AND role_type != 'builtin'";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(roleId);
        var affected = await cmd.ExecuteNonQueryAsync();
        return affected > 0;
    }

    private async Task SetRoleCapsAsync(NpgsqlConnection conn, Guid roleId, string[] caps)
    {
        foreach (var cap in caps)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO role_caps(role_id, cap) VALUES ($1, $2) ON CONFLICT DO NOTHING";
            cmd.Parameters.AddWithValue(roleId);
            cmd.Parameters.AddWithValue(cap);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task SetRoleDataScopesAsync(NpgsqlConnection conn, Guid roleId, DataScopeInput[] scopes)
    {
        foreach (var scope in scopes)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO role_data_scopes(role_id, entity_type, scope_type, scope_filter)
                                VALUES ($1, $2, $3, $4)
                                ON CONFLICT (role_id, entity_type) DO UPDATE SET scope_type = $3, scope_filter = $4";
            cmd.Parameters.AddWithValue(roleId);
            cmd.Parameters.AddWithValue(scope.EntityType);
            cmd.Parameters.AddWithValue(scope.ScopeType);
            cmd.Parameters.AddWithValue(scope.ScopeFilter != null ? (object)scope.ScopeFilter : DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>复制系统角色到公司</summary>
    public async Task<Guid?> CopySystemRoleAsync(string companyCode, string systemRoleCode, string? newRoleCode = null, string? newRoleName = null)
    {
        await using var conn = await _ds.OpenConnectionAsync();

        // 查找系统角色
        Guid systemRoleId;
        string? roleName, description;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, role_name, description FROM roles WHERE company_code IS NULL AND role_code = $1";
            cmd.Parameters.AddWithValue(systemRoleCode);
            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) return null;
            systemRoleId = rd.GetGuid(0);
            roleName = rd.IsDBNull(1) ? null : rd.GetString(1);
            description = rd.IsDBNull(2) ? null : rd.GetString(2);
        }

        // 创建新角色
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO roles(company_code, role_code, role_name, description, role_type)
                                VALUES ($1, $2, $3, $4, 'custom')
                                ON CONFLICT (company_code, role_code) DO UPDATE SET role_name = $3, description = $4
                                RETURNING id";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(newRoleCode ?? systemRoleCode);
            cmd.Parameters.AddWithValue((object?)(newRoleName ?? roleName) ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)description ?? DBNull.Value);
            var newRoleId = (Guid)(await cmd.ExecuteScalarAsync() ?? Guid.Empty);

            // 复制caps
            await using var capCmd = conn.CreateCommand();
            capCmd.CommandText = @"INSERT INTO role_caps(role_id, cap)
                                   SELECT $1, cap FROM role_caps WHERE role_id = $2
                                   ON CONFLICT DO NOTHING";
            capCmd.Parameters.AddWithValue(newRoleId);
            capCmd.Parameters.AddWithValue(systemRoleId);
            await capCmd.ExecuteNonQueryAsync();

            // 复制data_scopes
            await using var scopeCmd = conn.CreateCommand();
            scopeCmd.CommandText = @"INSERT INTO role_data_scopes(role_id, entity_type, scope_type, scope_filter)
                                     SELECT $1, entity_type, scope_type, scope_filter FROM role_data_scopes WHERE role_id = $2
                                     ON CONFLICT DO NOTHING";
            scopeCmd.Parameters.AddWithValue(newRoleId);
            scopeCmd.Parameters.AddWithValue(systemRoleId);
            await scopeCmd.ExecuteNonQueryAsync();

            return newRoleId;
        }
    }

    #endregion

    #region Permission Check

    /// <summary>检查用户是否有特定能力</summary>
    public async Task<bool> HasCapAsync(string companyCode, Guid userId, string cap)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT 1 FROM role_caps rc
                            JOIN user_roles ur ON ur.role_id = rc.role_id
                            WHERE ur.user_id = $1 AND rc.cap = $2
                            LIMIT 1";
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(cap);
        var result = await cmd.ExecuteScalarAsync();
        return result != null;
    }

    /// <summary>获取用户的数据范围配置</summary>
    public async Task<string> GetDataScopeAsync(string companyCode, Guid userId, string entityType)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        // 用户可能有多个角色，取最宽松的scope
        cmd.CommandText = @"SELECT ds.scope_type FROM role_data_scopes ds
                            JOIN user_roles ur ON ur.role_id = ds.role_id
                            WHERE ur.user_id = $1 AND ds.entity_type = $2
                            ORDER BY CASE ds.scope_type 
                                WHEN 'all' THEN 1 
                                WHEN 'department' THEN 2 
                                WHEN 'self' THEN 3 
                                ELSE 4 END
                            LIMIT 1";
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(entityType);
        var result = await cmd.ExecuteScalarAsync() as string;
        return result ?? "self"; // 默认只能看自己的
    }

    /// <summary>获取用户可访问的菜单</summary>
    public async Task<List<string>> GetAccessibleMenusAsync(string companyCode, Guid userId)
    {
        var userCaps = new HashSet<string>();
        
        // 获取用户所有能力
        await using var conn = await _ds.OpenConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT DISTINCT rc.cap FROM role_caps rc
                                JOIN user_roles ur ON ur.role_id = rc.role_id
                                WHERE ur.user_id = $1";
            cmd.Parameters.AddWithValue(userId);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                userCaps.Add(rd.GetString(0));
            }
        }

        // 获取可访问的菜单
        var menus = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT menu_key, caps_required FROM permission_menus WHERE is_active = true";
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var menuKey = rd.GetString(0);
                var capsRequired = rd.IsDBNull(1) ? Array.Empty<string>() : (string[])rd.GetValue(1);
                
                // 如果没有要求任何cap，或者用户有任一required cap，则可访问
                if (capsRequired.Length == 0 || capsRequired.Any(c => userCaps.Contains(c)))
                {
                    menus.Add(menuKey);
                }
            }
        }

        return menus;
    }

    #endregion

    #region AI Role Generation

    /// <summary>保存AI生成的角色配置</summary>
    public async Task<Guid> SaveAiGenerationAsync(string companyCode, string userPrompt, JsonElement aiResponse, Guid? roleId = null)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO ai_role_generations(company_code, role_id, user_prompt, ai_response, status)
                            VALUES ($1, $2, $3, $4, 'pending')
                            RETURNING id";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(roleId.HasValue ? (object)roleId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(userPrompt);
        cmd.Parameters.AddWithValue(aiResponse.GetRawText());
        
        return (Guid)(await cmd.ExecuteScalarAsync() ?? Guid.Empty);
    }

    /// <summary>应用AI生成的角色配置</summary>
    public async Task<Guid?> ApplyAiGenerationAsync(string companyCode, Guid generationId, Guid appliedBy)
    {
        await using var conn = await _ds.OpenConnectionAsync();

        // 获取AI生成记录
        JsonElement? aiResponse;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT ai_response FROM ai_role_generations WHERE id = $1 AND company_code = $2 AND status = 'pending'";
            cmd.Parameters.AddWithValue(generationId);
            cmd.Parameters.AddWithValue(companyCode);
            var json = await cmd.ExecuteScalarAsync() as string;
            if (json == null) return null;
            aiResponse = JsonDocument.Parse(json).RootElement;
        }

        // 解析AI响应并创建角色
        var roleCode = aiResponse.Value.TryGetProperty("roleCode", out var rc) ? rc.GetString() : null;
        var roleName = aiResponse.Value.TryGetProperty("roleName", out var rn) ? rn.GetString() : null;
        var description = aiResponse.Value.TryGetProperty("description", out var desc) ? desc.GetString() : null;
        var caps = aiResponse.Value.TryGetProperty("capabilities", out var capsEl) && capsEl.ValueKind == JsonValueKind.Array
            ? capsEl.EnumerateArray().Select(c => c.GetString()!).ToArray()
            : Array.Empty<string>();

        if (string.IsNullOrEmpty(roleCode)) return null;

        // 检查是否已存在
        Guid roleId;
        await using (var chkCmd = conn.CreateCommand())
        {
            chkCmd.CommandText = "SELECT id FROM roles WHERE company_code = $1 AND role_code = $2";
            chkCmd.Parameters.AddWithValue(companyCode);
            chkCmd.Parameters.AddWithValue(roleCode);
            var existingId = await chkCmd.ExecuteScalarAsync();
            if (existingId != null)
            {
                roleId = (Guid)existingId;
                // 更新现有角色
                await UpdateRoleAsync(companyCode, roleId, new UpdateRoleRequest
                {
                    RoleName = roleName,
                    Description = description,
                    Caps = caps,
                    SourcePrompt = null // 从generation记录中获取
                });
            }
            else
            {
                // 创建新角色
                roleId = await CreateRoleAsync(new CreateRoleRequest
                {
                    CompanyCode = companyCode,
                    RoleCode = roleCode,
                    RoleName = roleName,
                    Description = description,
                    RoleType = "ai_generated",
                    Caps = caps
                });
            }
        }

        // 更新generation记录
        await using (var updCmd = conn.CreateCommand())
        {
            updCmd.CommandText = @"UPDATE ai_role_generations SET status = 'applied', applied_at = now(), applied_by = $1, role_id = $2 WHERE id = $3";
            updCmd.Parameters.AddWithValue(appliedBy);
            updCmd.Parameters.AddWithValue(roleId);
            updCmd.Parameters.AddWithValue(generationId);
            await updCmd.ExecuteNonQueryAsync();
        }

        return roleId;
    }

    #endregion

    #region DTOs

    public class PermissionModule
    {
        public Guid Id { get; set; }
        public string ModuleCode { get; set; } = "";
        public JsonElement ModuleName { get; set; }
        public string? Icon { get; set; }
        public int DisplayOrder { get; set; }
        public bool IsActive { get; set; }
    }

    public class PermissionCap
    {
        public Guid Id { get; set; }
        public string CapCode { get; set; } = "";
        public JsonElement CapName { get; set; }
        public string ModuleCode { get; set; } = "";
        public string CapType { get; set; } = "action";
        public bool IsSensitive { get; set; }
        public JsonElement? Description { get; set; }
        public int DisplayOrder { get; set; }
    }

    public class PermissionMenu
    {
        public Guid Id { get; set; }
        public string ModuleCode { get; set; } = "";
        public string MenuKey { get; set; } = "";
        public JsonElement MenuName { get; set; }
        public string? MenuPath { get; set; }
        public string[] CapsRequired { get; set; } = Array.Empty<string>();
        public int DisplayOrder { get; set; }
    }

    public class UserInfo
    {
        public Guid Id { get; set; }
        public string CompanyCode { get; set; } = "";
        public string EmployeeCode { get; set; } = "";
        public string? Name { get; set; }
        public string? DeptId { get; set; }
        public string UserType { get; set; } = "internal";
        public Guid? EmployeeId { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public bool IsActive { get; set; }
        public string? ExternalType { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? EmployeeName { get; set; }
        public string[] RoleCodes { get; set; } = Array.Empty<string>();
    }

    public class RoleInfo
    {
        public Guid Id { get; set; }
        public string? CompanyCode { get; set; }
        public string RoleCode { get; set; } = "";
        public string? RoleName { get; set; }
        public string? Description { get; set; }
        public string RoleType { get; set; } = "custom";
        public bool IsActive { get; set; }
        public string? SourcePrompt { get; set; }
        public DateTime CreatedAt { get; set; }
        public string[] Caps { get; set; } = Array.Empty<string>();
        public int UserCount { get; set; }
    }

    public class RoleDataScope
    {
        public Guid Id { get; set; }
        public Guid RoleId { get; set; }
        public string EntityType { get; set; } = "";
        public string ScopeType { get; set; } = "all";
        public JsonElement? ScopeFilter { get; set; }
    }

    public class CreateUserRequest
    {
        public string CompanyCode { get; set; } = "";
        public string EmployeeCode { get; set; } = "";
        public string Password { get; set; } = "";
        public string? Name { get; set; }
        public string? DeptId { get; set; }
        public string? UserType { get; set; }
        public Guid? EmployeeId { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? ExternalType { get; set; }
        public bool? IsActive { get; set; }
        public string[]? RoleCodes { get; set; }
    }

    public class UpdateUserRequest
    {
        public string? Name { get; set; }
        public string? DeptId { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public bool? IsActive { get; set; }
        public string? ExternalType { get; set; }
        public string? Password { get; set; }
        public string[]? RoleCodes { get; set; }
        public Guid? EmployeeId { get; set; }
        public bool ClearEmployeeId { get; set; } // 是否清除关联员工
    }

    public class CreateRoleRequest
    {
        public string CompanyCode { get; set; } = "";
        public string RoleCode { get; set; } = "";
        public string? RoleName { get; set; }
        public string? Description { get; set; }
        public string? RoleType { get; set; }
        public string? SourcePrompt { get; set; }
        public bool? IsActive { get; set; }
        public string[]? Caps { get; set; }
        public DataScopeInput[]? DataScopes { get; set; }
    }

    public class UpdateRoleRequest
    {
        public string? RoleName { get; set; }
        public string? Description { get; set; }
        public bool? IsActive { get; set; }
        public string? SourcePrompt { get; set; }
        public string[]? Caps { get; set; }
        public DataScopeInput[]? DataScopes { get; set; }
    }

    public class DataScopeInput
    {
        public string EntityType { get; set; } = "";
        public string ScopeType { get; set; } = "all";
        public string? ScopeFilter { get; set; }
    }

    #endregion
}

