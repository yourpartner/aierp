using System.Text;
using System.Text.Json;

namespace Server.Modules.Analytics;

/// <summary>
/// 安全 SQL 构建器 - 根据 QuerySpec 生成带权限控制的 SQL
/// </summary>
public class SecureQueryBuilder
{
    private readonly DataAccessRuleService _accessRules;
    
    public SecureQueryBuilder(DataAccessRuleService accessRules)
    {
        _accessRules = accessRules;
    }
    
    /// <summary>
    /// 构建结果
    /// </summary>
    public class BuildResult
    {
        public bool Success { get; set; }
        public string? Sql { get; set; }
        public string? Error { get; set; }
        public List<string> Warnings { get; set; } = new();
        public List<string> AppliedFilters { get; set; } = new();  // 审计用
    }
    
    /// <summary>
    /// 根据 QuerySpec 和用户权限构建安全的 SQL
    /// </summary>
    public BuildResult Build(QuerySpec spec, UserSecurityContext user)
    {
        var result = new BuildResult();
        
        try
        {
            // 1. 检查表访问权限
            if (!_accessRules.CanAccessTable(spec.Table, user))
            {
                result.Success = false;
                result.Error = $"无权访问表 {spec.Table}";
                return result;
            }
            
            var sql = new StringBuilder();
            
            // 2. 构建 SELECT 子句
            var selectClause = BuildSelectClause(spec, user, result);
            if (string.IsNullOrEmpty(selectClause))
            {
                result.Success = false;
                result.Error = "没有可访问的字段";
                return result;
            }
            sql.Append($"SELECT {selectClause} ");
            
            // 3. 构建 FROM 子句
            sql.Append($"FROM {spec.Table} ");
            
            // 4. 构建 WHERE 子句（包含权限过滤）
            var whereClause = BuildWhereClause(spec, user, result);
            sql.Append($"WHERE {whereClause} ");
            
            // 5. 构建 GROUP BY 子句
            var groupByClause = BuildGroupByClause(spec);
            if (!string.IsNullOrEmpty(groupByClause))
            {
                sql.Append($"GROUP BY {groupByClause} ");
            }
            
            // 6. 构建 ORDER BY 子句
            var orderByClause = BuildOrderByClause(spec);
            if (!string.IsNullOrEmpty(orderByClause))
            {
                sql.Append($"ORDER BY {orderByClause} ");
            }
            
            // 7. 构建 LIMIT 子句
            sql.Append($"LIMIT {Math.Min(spec.Limit, 1000)}");
            
            result.Success = true;
            result.Sql = sql.ToString();
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            return result;
        }
    }
    
    private string BuildSelectClause(QuerySpec spec, UserSecurityContext user, BuildResult result)
    {
        var columns = new List<string>();
        
        // 添加维度字段
        foreach (var dim in spec.Dimensions)
        {
            if (!_accessRules.IsFieldAllowed(spec.Table, dim.Field, user))
            {
                result.Warnings.Add($"字段 {dim.Field} 无权访问，已跳过");
                continue;
            }
            
            var col = BuildDimensionColumn(dim);
            columns.Add(col);
        }
        
        // 添加度量字段
        foreach (var metric in spec.Metrics)
        {
            if (!_accessRules.IsFieldAllowed(spec.Table, metric.Field, user))
            {
                result.Warnings.Add($"字段 {metric.Field} 无权访问，已跳过");
                continue;
            }
            
            var col = BuildMetricColumn(metric);
            columns.Add(col);
        }
        
        return string.Join(", ", columns);
    }
    
    private string BuildDimensionColumn(DimensionSpec dim)
    {
        var alias = dim.Alias ?? dim.Field;
        
        if (!string.IsNullOrEmpty(dim.Granularity))
        {
            // 日期字段需要按粒度截断
            var truncUnit = dim.Granularity.ToLower() switch
            {
                "day" => "day",
                "week" => "week",
                "month" => "month",
                "quarter" => "quarter",
                "year" => "year",
                _ => "day"
            };
            return $"DATE_TRUNC('{truncUnit}', {dim.Field}) AS {alias}";
        }
        
        return dim.Field == alias ? dim.Field : $"{dim.Field} AS {alias}";
    }
    
    private string BuildMetricColumn(MetricSpec metric)
    {
        var alias = metric.Alias ?? $"{metric.Aggregate}_{metric.Field}";
        
        var agg = metric.Aggregate.ToLower() switch
        {
            "sum" => $"SUM({metric.Field})",
            "count" => $"COUNT({metric.Field})",
            "count_distinct" => $"COUNT(DISTINCT {metric.Field})",
            "avg" => $"AVG({metric.Field})",
            "max" => $"MAX({metric.Field})",
            "min" => $"MIN({metric.Field})",
            _ => $"SUM({metric.Field})"
        };
        
        return $"{agg} AS {alias}";
    }
    
    private string BuildWhereClause(QuerySpec spec, UserSecurityContext user, BuildResult result)
    {
        var conditions = new List<string>();
        
        // 1. 公司代码过滤（必须）
        conditions.Add("company_code = $1");
        result.AppliedFilters.Add("company_code = $1");
        
        // 2. 行级权限过滤（自动注入，无法绕过）
        var rowFilter = _accessRules.GetRowLevelFilter(spec.Table, user);
        if (!string.IsNullOrEmpty(rowFilter))
        {
            conditions.Add($"({rowFilter})");
            result.AppliedFilters.Add($"[权限过滤] {rowFilter}");
        }
        
        // 3. 时间过滤（代码生成，100% 可靠）
        if (spec.TimeFilter != null)
        {
            var timeFilter = BuildTimeFilter(spec.TimeFilter);
            if (!string.IsNullOrEmpty(timeFilter))
            {
                conditions.Add(timeFilter);
                result.AppliedFilters.Add($"[时间过滤] {timeFilter}");
            }
        }
        
        // 4. 用户自定义过滤
        foreach (var filter in spec.Filters)
        {
            // 检查字段权限
            if (!_accessRules.IsFieldAllowed(spec.Table, filter.Field, user))
            {
                result.Warnings.Add($"过滤字段 {filter.Field} 无权访问，已跳过");
                continue;
            }
            
            var filterSql = BuildFilterCondition(filter);
            if (!string.IsNullOrEmpty(filterSql))
            {
                conditions.Add(filterSql);
            }
        }
        
        return string.Join(" AND ", conditions);
    }
    
    /// <summary>
    /// 构建时间过滤条件（核心：日期逻辑由代码控制）
    /// </summary>
    private string BuildTimeFilter(TimeFilterSpec filter)
    {
        var field = filter.DateField;
        
        return filter.Type.ToLower() switch
        {
            // 本月
            "this_month" => $"DATE_TRUNC('month', {field}) = DATE_TRUNC('month', CURRENT_DATE)",
            
            // 上月
            "last_month" => $"DATE_TRUNC('month', {field}) = DATE_TRUNC('month', CURRENT_DATE - INTERVAL '1 month')",
            
            // 本年
            "this_year" => $"EXTRACT(YEAR FROM {field}) = EXTRACT(YEAR FROM CURRENT_DATE)",
            
            // 去年
            "last_year" => $"EXTRACT(YEAR FROM {field}) = EXTRACT(YEAR FROM CURRENT_DATE) - 1",
            
            // 今年指定月份
            "this_year_month" when filter.Month.HasValue => 
                $"EXTRACT(YEAR FROM {field}) = EXTRACT(YEAR FROM CURRENT_DATE) AND EXTRACT(MONTH FROM {field}) = {filter.Month.Value}",
            
            // 去年指定月份
            "last_year_month" when filter.Month.HasValue => 
                $"EXTRACT(YEAR FROM {field}) = EXTRACT(YEAR FROM CURRENT_DATE) - 1 AND EXTRACT(MONTH FROM {field}) = {filter.Month.Value}",
            
            // 最近N天
            "last_n_days" when filter.N.HasValue => 
                $"{field} >= CURRENT_DATE - INTERVAL '{filter.N.Value} days'",
            
            // 最近N月
            "last_n_months" when filter.N.HasValue => 
                $"{field} >= DATE_TRUNC('month', CURRENT_DATE - INTERVAL '{filter.N.Value} months')",
            
            // 本季度
            "this_quarter" => $"DATE_TRUNC('quarter', {field}) = DATE_TRUNC('quarter', CURRENT_DATE)",
            
            // 今天
            "today" => $"{field} = CURRENT_DATE",
            
            // 本周
            "this_week" => $"DATE_TRUNC('week', {field}) = DATE_TRUNC('week', CURRENT_DATE)",
            
            // 自定义日期范围
            "custom" when !string.IsNullOrEmpty(filter.DateFrom) && !string.IsNullOrEmpty(filter.DateTo) =>
                $"{field} >= '{EscapeDate(filter.DateFrom)}'::date AND {field} <= '{EscapeDate(filter.DateTo)}'::date",
            
            "custom" when !string.IsNullOrEmpty(filter.DateFrom) =>
                $"{field} >= '{EscapeDate(filter.DateFrom)}'::date",
            
            "custom" when !string.IsNullOrEmpty(filter.DateTo) =>
                $"{field} <= '{EscapeDate(filter.DateTo)}'::date",
            
            // 默认：本月
            _ => $"DATE_TRUNC('month', {field}) = DATE_TRUNC('month', CURRENT_DATE)"
        };
    }
    
    private string BuildFilterCondition(FilterSpec filter)
    {
        var field = filter.Field;
        var value = filter.Value;
        
        if (value == null) return "";
        
        return filter.Operator.ToLower() switch
        {
            "eq" => $"{field} = {FormatValue(value)}",
            "neq" => $"{field} <> {FormatValue(value)}",
            "gt" => $"{field} > {FormatValue(value)}",
            "gte" => $"{field} >= {FormatValue(value)}",
            "lt" => $"{field} < {FormatValue(value)}",
            "lte" => $"{field} <= {FormatValue(value)}",
            "like" => $"{field} ILIKE {FormatValue($"%{value}%")}",
            "starts_with" => $"{field} ILIKE {FormatValue($"{value}%")}",
            "ends_with" => $"{field} ILIKE {FormatValue($"%{value}")}",
            "in" when value is JsonElement je && je.ValueKind == JsonValueKind.Array => 
                $"{field} = ANY(ARRAY[{FormatArray(je)}])",
            "not_in" when value is JsonElement je && je.ValueKind == JsonValueKind.Array => 
                $"{field} <> ALL(ARRAY[{FormatArray(je)}])",
            "is_null" => $"{field} IS NULL",
            "is_not_null" => $"{field} IS NOT NULL",
            _ => $"{field} = {FormatValue(value)}"
        };
    }
    
    private string BuildGroupByClause(QuerySpec spec)
    {
        if (!spec.Dimensions.Any()) return "";
        
        var groupByParts = new List<string>();
        var index = 1;
        
        foreach (var dim in spec.Dimensions)
        {
            // 使用位置引用或完整表达式
            if (!string.IsNullOrEmpty(dim.Granularity))
            {
                groupByParts.Add(index.ToString());
            }
            else
            {
                groupByParts.Add(dim.Field);
            }
            index++;
        }
        
        return string.Join(", ", groupByParts);
    }
    
    private string BuildOrderByClause(QuerySpec spec)
    {
        if (!spec.OrderBy.Any())
        {
            // 默认按第一个维度排序
            if (spec.Dimensions.Any())
            {
                return "1 ASC";
            }
            return "";
        }
        
        var orderParts = spec.OrderBy.Select(o => 
            $"{o.Field} {(o.Direction.ToLower() == "desc" ? "DESC" : "ASC")}"
        );
        
        return string.Join(", ", orderParts);
    }
    
    private static string FormatValue(object value)
    {
        return value switch
        {
            string s => $"'{EscapeSql(s)}'",
            int i => i.ToString(),
            long l => l.ToString(),
            decimal d => d.ToString(),
            double db => db.ToString(),
            bool b => b ? "TRUE" : "FALSE",
            JsonElement je when je.ValueKind == JsonValueKind.String => $"'{EscapeSql(je.GetString() ?? "")}'",
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetRawText(),
            JsonElement je when je.ValueKind == JsonValueKind.True => "TRUE",
            JsonElement je when je.ValueKind == JsonValueKind.False => "FALSE",
            _ => $"'{EscapeSql(value.ToString() ?? "")}'"
        };
    }
    
    private static string FormatArray(JsonElement array)
    {
        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            values.Add(FormatValue(item));
        }
        return string.Join(", ", values);
    }
    
    private static string EscapeSql(string value)
    {
        return value.Replace("'", "''");
    }
    
    private static string EscapeDate(string date)
    {
        // 只允许 YYYY-MM-DD 格式
        if (DateTime.TryParse(date, out var parsed))
        {
            return parsed.ToString("yyyy-MM-dd");
        }
        return DateTime.Today.ToString("yyyy-MM-dd");
    }
}

