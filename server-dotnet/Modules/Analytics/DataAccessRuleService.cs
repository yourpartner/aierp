using System.Text.Json;
using Npgsql;

namespace Server.Modules.Analytics;

/// <summary>
/// 用户安全上下文
/// </summary>
public class UserSecurityContext
{
    public string UserId { get; set; } = "";
    public string CompanyCode { get; set; } = "";
    public string[] Roles { get; set; } = Array.Empty<string>();
    public string? DeptCode { get; set; }
    public string? RegionCode { get; set; }
    public string[]? AccessibleDepts { get; set; }
    public string[]? AccessibleCustomers { get; set; }
    public string[]? AccessibleSalesReps { get; set; }
    public bool IsAdmin { get; set; }
}

/// <summary>
/// 表访问权限定义
/// </summary>
public class TableAccessRule
{
    /// <summary>
    /// 允许访问的字段（白名单）
    /// </summary>
    public HashSet<string> AllowedFields { get; set; } = new();
    
    /// <summary>
    /// 禁止访问的字段（黑名单，优先级高于白名单）
    /// </summary>
    public HashSet<string> DeniedFields { get; set; } = new();
    
    /// <summary>
    /// 行级过滤表达式（SQL WHERE 片段）
    /// </summary>
    public string? RowFilter { get; set; }
}

/// <summary>
/// 角色权限定义
/// </summary>
public class RoleAccessPolicy
{
    public string RoleName { get; set; } = "";
    public Dictionary<string, TableAccessRule> TableRules { get; set; } = new();
}

/// <summary>
/// 数据访问权限服务
/// </summary>
public class DataAccessRuleService
{
    private readonly NpgsqlDataSource _ds;
    private readonly Dictionary<string, RoleAccessPolicy> _policies;
    
    // 表元数据：定义每张表可用的字段
    private static readonly Dictionary<string, HashSet<string>> TableFields = new()
    {
        ["sales_orders"] = new HashSet<string> {
            "id", "so_no", "partner_code", "customer_name", "order_date", 
            "delivery_date", "amount_total", "status", "currency",
            "sales_rep_id", "dept_code", "region_code", "created_at"
        },
        ["delivery_notes"] = new HashSet<string> {
            "id", "delivery_no", "so_no", "customer_code", "customer_name",
            "delivery_date", "status", "shipped_at", "sales_rep_id", 
            "dept_code", "region_code", "created_at"
        },
        ["sales_invoices"] = new HashSet<string> {
            "id", "invoice_no", "customer_code", "customer_name",
            "invoice_date", "due_date", "amount_total", "status",
            "sales_rep_id", "dept_code", "region_code", "created_at"
        },
        ["customers"] = new HashSet<string> {
            "id", "partner_code", "name", "region_code", "assigned_rep_id",
            "dept_code", "created_at"
        }
    };
    
    // 敏感字段定义（需要特殊权限才能访问）
    private static readonly Dictionary<string, HashSet<string>> SensitiveFields = new()
    {
        ["sales_orders"] = new HashSet<string> { "cost_total", "profit", "margin" },
        ["sales_invoices"] = new HashSet<string> { "cost_total", "profit" }
    };
    
    public DataAccessRuleService(NpgsqlDataSource ds)
    {
        _ds = ds;
        _policies = InitializeDefaultPolicies();
    }
    
    private Dictionary<string, RoleAccessPolicy> InitializeDefaultPolicies()
    {
        return new Dictionary<string, RoleAccessPolicy>
        {
            // 管理员：全部权限
            ["admin"] = new RoleAccessPolicy
            {
                RoleName = "admin",
                TableRules = new Dictionary<string, TableAccessRule>
                {
                    ["*"] = new TableAccessRule
                    {
                        AllowedFields = new HashSet<string> { "*" },
                        RowFilter = null  // 无行级限制
                    }
                }
            },
            
            // 经营者/老板：看所有数据，包括敏感字段
            ["owner"] = new RoleAccessPolicy
            {
                RoleName = "owner",
                TableRules = new Dictionary<string, TableAccessRule>
                {
                    ["*"] = new TableAccessRule
                    {
                        AllowedFields = new HashSet<string> { "*" },
                        RowFilter = null
                    }
                }
            },
            
            // 部门经理：看本部门所有数据
            ["dept_manager"] = new RoleAccessPolicy
            {
                RoleName = "dept_manager",
                TableRules = new Dictionary<string, TableAccessRule>
                {
                    ["sales_orders"] = new TableAccessRule
                    {
                        AllowedFields = new HashSet<string> { "*" },
                        DeniedFields = new HashSet<string> { "cost_total", "profit", "margin" },
                        RowFilter = "dept_code = @userDept"
                    },
                    ["delivery_notes"] = new TableAccessRule
                    {
                        AllowedFields = new HashSet<string> { "*" },
                        RowFilter = "dept_code = @userDept"
                    },
                    ["sales_invoices"] = new TableAccessRule
                    {
                        AllowedFields = new HashSet<string> { "*" },
                        DeniedFields = new HashSet<string> { "cost_total", "profit" },
                        RowFilter = "dept_code = @userDept"
                    },
                    ["customers"] = new TableAccessRule
                    {
                        AllowedFields = new HashSet<string> { "*" },
                        RowFilter = "dept_code = @userDept"
                    }
                }
            },
            
            // 区域经理：看本区域所有数据
            ["regional_manager"] = new RoleAccessPolicy
            {
                RoleName = "regional_manager",
                TableRules = new Dictionary<string, TableAccessRule>
                {
                    ["sales_orders"] = new TableAccessRule
                    {
                        AllowedFields = new HashSet<string> { "*" },
                        DeniedFields = new HashSet<string> { "cost_total", "profit", "margin" },
                        RowFilter = "region_code = @userRegion"
                    },
                    ["delivery_notes"] = new TableAccessRule
                    {
                        AllowedFields = new HashSet<string> { "*" },
                        RowFilter = "region_code = @userRegion"
                    },
                    ["sales_invoices"] = new TableAccessRule
                    {
                        AllowedFields = new HashSet<string> { "*" },
                        DeniedFields = new HashSet<string> { "cost_total", "profit" },
                        RowFilter = "region_code = @userRegion"
                    },
                    ["customers"] = new TableAccessRule
                    {
                        AllowedFields = new HashSet<string> { "*" },
                        RowFilter = "region_code = @userRegion"
                    }
                }
            },
            
            // 普通销售员：只看自己的数据
            ["sales_rep"] = new RoleAccessPolicy
            {
                RoleName = "sales_rep",
                TableRules = new Dictionary<string, TableAccessRule>
                {
                    ["sales_orders"] = new TableAccessRule
                    {
                        AllowedFields = new HashSet<string> {
                            "so_no", "customer_name", "order_date", "delivery_date",
                            "amount_total", "status", "created_at"
                        },
                        RowFilter = "sales_rep_id = @userId"
                    },
                    ["delivery_notes"] = new TableAccessRule
                    {
                        AllowedFields = new HashSet<string> {
                            "delivery_no", "so_no", "customer_name", "delivery_date",
                            "status", "shipped_at", "created_at"
                        },
                        RowFilter = "sales_rep_id = @userId"
                    },
                    ["sales_invoices"] = new TableAccessRule
                    {
                        AllowedFields = new HashSet<string> {
                            "invoice_no", "customer_name", "invoice_date", "due_date",
                            "amount_total", "status", "created_at"
                        },
                        RowFilter = "sales_rep_id = @userId"
                    },
                    ["customers"] = new TableAccessRule
                    {
                        AllowedFields = new HashSet<string> {
                            "partner_code", "name", "created_at"
                        },
                        RowFilter = "assigned_rep_id = @userId"
                    }
                }
            },
            
            // 报表查看者：只能看汇总数据，有行级限制
            ["report_viewer"] = new RoleAccessPolicy
            {
                RoleName = "report_viewer",
                TableRules = new Dictionary<string, TableAccessRule>
                {
                    ["sales_orders"] = new TableAccessRule
                    {
                        AllowedFields = new HashSet<string> {
                            "order_date", "amount_total", "status", "region_code", "dept_code"
                        },
                        RowFilter = "dept_code = ANY(@accessibleDepts)"
                    }
                }
            }
        };
    }
    
    /// <summary>
    /// 获取用户对指定表的有效权限
    /// </summary>
    public TableAccessRule? GetEffectiveTableAccess(string table, UserSecurityContext user)
    {
        if (user.IsAdmin)
        {
            return new TableAccessRule
            {
                AllowedFields = new HashSet<string> { "*" },
                RowFilter = null
            };
        }
        
        TableAccessRule? effectiveRule = null;
        
        foreach (var role in user.Roles)
        {
            if (!_policies.TryGetValue(role, out var policy)) continue;
            
            // 检查通配符规则
            if (policy.TableRules.TryGetValue("*", out var wildcardRule))
            {
                effectiveRule = MergeRules(effectiveRule, wildcardRule);
            }
            
            // 检查具体表规则
            if (policy.TableRules.TryGetValue(table, out var tableRule))
            {
                effectiveRule = MergeRules(effectiveRule, tableRule);
            }
        }
        
        return effectiveRule;
    }
    
    /// <summary>
    /// 合并多个角色的规则（取并集）
    /// </summary>
    private TableAccessRule MergeRules(TableAccessRule? existing, TableAccessRule newRule)
    {
        if (existing == null) return newRule;
        
        var merged = new TableAccessRule();
        
        // 字段：取并集
        if (existing.AllowedFields.Contains("*") || newRule.AllowedFields.Contains("*"))
        {
            merged.AllowedFields = new HashSet<string> { "*" };
        }
        else
        {
            merged.AllowedFields = new HashSet<string>(existing.AllowedFields);
            merged.AllowedFields.UnionWith(newRule.AllowedFields);
        }
        
        // 禁止字段：取交集（更宽松）
        merged.DeniedFields = new HashSet<string>(existing.DeniedFields);
        merged.DeniedFields.IntersectWith(newRule.DeniedFields);
        
        // 行过滤：取 OR（更宽松）
        if (string.IsNullOrEmpty(existing.RowFilter) || string.IsNullOrEmpty(newRule.RowFilter))
        {
            merged.RowFilter = null;  // 任一无限制则无限制
        }
        else if (existing.RowFilter == newRule.RowFilter)
        {
            merged.RowFilter = existing.RowFilter;
        }
        else
        {
            merged.RowFilter = $"({existing.RowFilter}) OR ({newRule.RowFilter})";
        }
        
        return merged;
    }
    
    /// <summary>
    /// 检查字段是否允许访问
    /// </summary>
    public bool IsFieldAllowed(string table, string field, UserSecurityContext user)
    {
        var access = GetEffectiveTableAccess(table, user);
        if (access == null) return false;
        
        // 检查黑名单
        if (access.DeniedFields.Contains(field)) return false;
        
        // 检查白名单
        if (access.AllowedFields.Contains("*")) return true;
        return access.AllowedFields.Contains(field);
    }
    
    /// <summary>
    /// 过滤字段列表，只保留允许的字段
    /// </summary>
    public List<string> FilterAllowedFields(string table, IEnumerable<string> requestedFields, UserSecurityContext user)
    {
        var access = GetEffectiveTableAccess(table, user);
        if (access == null) return new List<string>();
        
        return requestedFields
            .Where(f => !access.DeniedFields.Contains(f))
            .Where(f => access.AllowedFields.Contains("*") || access.AllowedFields.Contains(f))
            .ToList();
    }
    
    /// <summary>
    /// 获取行级过滤条件（已替换参数）
    /// </summary>
    public string? GetRowLevelFilter(string table, UserSecurityContext user)
    {
        var access = GetEffectiveTableAccess(table, user);
        if (access?.RowFilter == null) return null;
        
        // 替换用户上下文参数
        var filter = access.RowFilter
            .Replace("@userId", $"'{EscapeSql(user.UserId)}'")
            .Replace("@userDept", user.DeptCode != null ? $"'{EscapeSql(user.DeptCode)}'" : "NULL")
            .Replace("@userRegion", user.RegionCode != null ? $"'{EscapeSql(user.RegionCode)}'" : "NULL");
        
        // 处理数组参数
        if (filter.Contains("@accessibleDepts") && user.AccessibleDepts != null)
        {
            var depts = string.Join(",", user.AccessibleDepts.Select(d => $"'{EscapeSql(d)}'"));
            filter = filter.Replace("@accessibleDepts", $"ARRAY[{depts}]");
        }
        
        if (filter.Contains("@accessibleCustomers") && user.AccessibleCustomers != null)
        {
            var customers = string.Join(",", user.AccessibleCustomers.Select(c => $"'{EscapeSql(c)}'"));
            filter = filter.Replace("@accessibleCustomers", $"ARRAY[{customers}]");
        }
        
        return filter;
    }
    
    /// <summary>
    /// 检查用户是否有权访问指定表
    /// </summary>
    public bool CanAccessTable(string table, UserSecurityContext user)
    {
        return GetEffectiveTableAccess(table, user) != null;
    }
    
    private static string EscapeSql(string value)
    {
        return value.Replace("'", "''");
    }
}

