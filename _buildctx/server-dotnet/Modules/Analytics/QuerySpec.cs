using System.Text.Json.Serialization;

namespace Server.Modules.Analytics;

/// <summary>
/// AI 输出的结构化查询规范
/// </summary>
public class QuerySpec
{
    /// <summary>
    /// 查询类型：sales_summary, sales_trend, customer_ranking, product_ranking, etc.
    /// </summary>
    [JsonPropertyName("queryType")]
    public string QueryType { get; set; } = "sales_summary";
    
    /// <summary>
    /// 主表：sales_orders, delivery_notes, sales_invoices
    /// </summary>
    [JsonPropertyName("table")]
    public string Table { get; set; } = "sales_orders";
    
    /// <summary>
    /// 度量字段（聚合）
    /// </summary>
    [JsonPropertyName("metrics")]
    public List<MetricSpec> Metrics { get; set; } = new();
    
    /// <summary>
    /// 维度字段（GROUP BY）
    /// </summary>
    [JsonPropertyName("dimensions")]
    public List<DimensionSpec> Dimensions { get; set; } = new();
    
    /// <summary>
    /// 时间过滤器
    /// </summary>
    [JsonPropertyName("timeFilter")]
    public TimeFilterSpec? TimeFilter { get; set; }
    
    /// <summary>
    /// 其他过滤条件
    /// </summary>
    [JsonPropertyName("filters")]
    public List<FilterSpec> Filters { get; set; } = new();
    
    /// <summary>
    /// 排序
    /// </summary>
    [JsonPropertyName("orderBy")]
    public List<OrderBySpec> OrderBy { get; set; } = new();
    
    /// <summary>
    /// 限制条数
    /// </summary>
    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 100;
    
    /// <summary>
    /// 图表类型：line, bar, pie, table
    /// </summary>
    [JsonPropertyName("chartType")]
    public string ChartType { get; set; } = "bar";
    
    /// <summary>
    /// 图表标题
    /// </summary>
    [JsonPropertyName("chartTitle")]
    public string? ChartTitle { get; set; }
    
    /// <summary>
    /// 说明文字
    /// </summary>
    [JsonPropertyName("explanation")]
    public string? Explanation { get; set; }
}

/// <summary>
/// 度量字段规范
/// </summary>
public class MetricSpec
{
    /// <summary>
    /// 字段名：amount_total, quantity, etc.
    /// </summary>
    [JsonPropertyName("field")]
    public string Field { get; set; } = "";
    
    /// <summary>
    /// 聚合方式：sum, count, avg, max, min
    /// </summary>
    [JsonPropertyName("aggregate")]
    public string Aggregate { get; set; } = "sum";
    
    /// <summary>
    /// 别名
    /// </summary>
    [JsonPropertyName("alias")]
    public string? Alias { get; set; }
}

/// <summary>
/// 维度字段规范
/// </summary>
public class DimensionSpec
{
    /// <summary>
    /// 字段名：order_date, customer_code, product_code, etc.
    /// </summary>
    [JsonPropertyName("field")]
    public string Field { get; set; } = "";
    
    /// <summary>
    /// 时间粒度（仅日期字段）：day, week, month, quarter, year
    /// </summary>
    [JsonPropertyName("granularity")]
    public string? Granularity { get; set; }
    
    /// <summary>
    /// 别名
    /// </summary>
    [JsonPropertyName("alias")]
    public string? Alias { get; set; }
}

/// <summary>
/// 时间过滤规范
/// </summary>
public class TimeFilterSpec
{
    /// <summary>
    /// 时间类型：this_month, last_month, this_year, last_year, 
    /// this_year_month, last_n_days, last_n_months, custom
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "this_month";
    
    /// <summary>
    /// 用于 this_year_month: 指定月份 1-12
    /// </summary>
    [JsonPropertyName("month")]
    public int? Month { get; set; }
    
    /// <summary>
    /// 用于 last_n_days/last_n_months: 数量
    /// </summary>
    [JsonPropertyName("n")]
    public int? N { get; set; }
    
    /// <summary>
    /// 用于 custom: 开始日期 YYYY-MM-DD
    /// </summary>
    [JsonPropertyName("dateFrom")]
    public string? DateFrom { get; set; }
    
    /// <summary>
    /// 用于 custom: 结束日期 YYYY-MM-DD
    /// </summary>
    [JsonPropertyName("dateTo")]
    public string? DateTo { get; set; }
    
    /// <summary>
    /// 日期字段名，默认 order_date
    /// </summary>
    [JsonPropertyName("dateField")]
    public string DateField { get; set; } = "order_date";
}

/// <summary>
/// 过滤条件规范
/// </summary>
public class FilterSpec
{
    /// <summary>
    /// 字段名
    /// </summary>
    [JsonPropertyName("field")]
    public string Field { get; set; } = "";
    
    /// <summary>
    /// 操作符：eq, neq, gt, gte, lt, lte, like, in, not_in
    /// </summary>
    [JsonPropertyName("operator")]
    public string Operator { get; set; } = "eq";
    
    /// <summary>
    /// 值（单个值或数组）
    /// </summary>
    [JsonPropertyName("value")]
    public object? Value { get; set; }
}

/// <summary>
/// 排序规范
/// </summary>
public class OrderBySpec
{
    /// <summary>
    /// 字段名或别名
    /// </summary>
    [JsonPropertyName("field")]
    public string Field { get; set; } = "";
    
    /// <summary>
    /// 方向：asc, desc
    /// </summary>
    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "asc";
}

