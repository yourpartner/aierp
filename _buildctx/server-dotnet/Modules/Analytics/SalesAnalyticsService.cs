using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules.Analytics;

/// <summary>
/// 销售分析服务 - 两阶段架构
/// 阶段1: AI 识别用户意图，输出结构化 QuerySpec
/// 阶段2: 代码生成安全 SQL，执行查询，生成图表
/// </summary>
public class SalesAnalyticsService
{
    private readonly NpgsqlDataSource _ds;
    private readonly DataAccessRuleService _accessRules;
    private readonly SecureQueryBuilder _queryBuilder;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;
    
    private static readonly string IntentRecognitionPrompt = @"
あなたは販売データ分析のインテント認識AIです。
ユーザーの自然言語クエリを分析し、構造化されたクエリ仕様をJSON形式で出力してください。

【重要】あなたはSQLを生成しません。クエリの意図だけを構造化してください。
日付の計算はシステムが行うので、「今月」「今年」「先月」などはそのまま type として出力してください。

【出力JSON形式】
{
  ""queryType"": ""sales_summary|sales_trend|customer_ranking|product_ranking|order_list"",
  ""table"": ""sales_orders|delivery_notes|sales_invoices"",
  ""metrics"": [
    {""field"": ""amount_total"", ""aggregate"": ""sum"", ""alias"": ""total_sales""}
  ],
  ""dimensions"": [
    {""field"": ""order_date"", ""granularity"": ""day|week|month|year"", ""alias"": ""date""}
  ],
  ""timeFilter"": {
    ""type"": ""this_month|last_month|this_year|last_year|this_year_month|last_n_days|last_n_months|custom"",
    ""month"": 11,
    ""n"": 30,
    ""dateFrom"": ""2024-01-01"",
    ""dateTo"": ""2024-12-31"",
    ""dateField"": ""order_date""
  },
  ""filters"": [
    {""field"": ""customer_code"", ""operator"": ""eq|like|in"", ""value"": ""...""}
  ],
  ""orderBy"": [
    {""field"": ""total_sales"", ""direction"": ""desc""}
  ],
  ""limit"": 100,
  ""chartType"": ""line|bar|pie|table"",
  ""chartTitle"": ""グラフのタイトル"",
  ""explanation"": ""この分析は...""
}

【利用可能なフィールド】
sales_orders:
  - order_date: 受注日
  - delivery_date: 納品予定日
  - amount_total: 受注金額
  - partner_code: 顧客コード
  - customer_name: 顧客名
  - status: ステータス
  - dept_code: 部門コード
  - region_code: 地域コード

【timeFilter.type の意味】
- this_month: 今月
- last_month: 先月
- this_year: 今年
- last_year: 去年
- this_year_month: 今年の特定月（monthを指定）
- last_n_days: 過去N日（nを指定）
- last_n_months: 過去Nヶ月（nを指定）
- this_quarter: 今四半期
- custom: カスタム期間（dateFrom, dateToを指定）

【例】
ユーザー: 「今月の売上は？」
→ timeFilter: {type: ""this_month"", dateField: ""order_date""}

ユーザー: 「今年11月の売上」
→ timeFilter: {type: ""this_year_month"", month: 11, dateField: ""order_date""}

ユーザー: 「過去3ヶ月のトレンド」
→ timeFilter: {type: ""last_n_months"", n: 3, dateField: ""order_date""}
   dimensions: [{field: ""order_date"", granularity: ""month""}]

【注意】
- 必ずJSON形式のみで回答してください
- SQLは生成しないでください
- 日付の具体的な年（2023, 2024等）を推測しないでください
";

    public SalesAnalyticsService(NpgsqlDataSource ds, IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _ds = ds;
        _httpClientFactory = httpClientFactory;
        _accessRules = new DataAccessRuleService(ds);
        _queryBuilder = new SecureQueryBuilder(_accessRules);
        _apiKey = config["Anthropic:ApiKey"] ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
    }
    
    /// <summary>
    /// 分析结果
    /// </summary>
    public record AnalysisResult(
        bool Success,
        string? Sql,
        string ChartType,
        string ChartTitle,
        string? XAxisField,
        string[]? YAxisFields,
        string? Explanation,
        JsonArray? Data,
        object? EchartsConfig,
        string? Error,
        List<string>? Warnings,
        List<string>? AppliedFilters
    );
    
    /// <summary>
    /// 处理自然语言查询
    /// </summary>
    public async Task<AnalysisResult> AnalyzeAsync(
        string companyCode,
        string userQuery,
        UserSecurityContext user,
        string? dateFrom = null,
        string? dateTo = null,
        CancellationToken ct = default)
    {
        try
        {
            // 阶段1: AI 识别意图
            var querySpec = await RecognizeIntentAsync(userQuery, dateFrom, dateTo, ct);
            if (querySpec == null)
            {
                return new AnalysisResult(
                    false, null, "table", userQuery, null, null, 
                    null, null, null, "クエリの解析に失敗しました", null, null);
            }
            
            // 阶段2: 代码生成安全 SQL
            var buildResult = _queryBuilder.Build(querySpec, user);
            if (!buildResult.Success)
            {
                return new AnalysisResult(
                    false, null, "table", userQuery, null, null,
                    null, null, null, buildResult.Error, buildResult.Warnings, null);
            }
            
            // 阶段3: 执行查询
            var data = await ExecuteQueryAsync(companyCode, buildResult.Sql!, ct);
            
            // 阶段4: 生成图表配置
            var xAxisField = querySpec.Dimensions.FirstOrDefault()?.Alias 
                ?? querySpec.Dimensions.FirstOrDefault()?.Field;
            var yAxisFields = querySpec.Metrics.Select(m => m.Alias ?? $"{m.Aggregate}_{m.Field}").ToArray();
            
            var echartsConfig = GenerateEchartsConfig(
                querySpec.ChartType, 
                querySpec.ChartTitle ?? userQuery, 
                xAxisField, 
                yAxisFields, 
                data);
            
            return new AnalysisResult(
                true,
                buildResult.Sql,
                querySpec.ChartType,
                querySpec.ChartTitle ?? userQuery,
                xAxisField,
                yAxisFields,
                querySpec.Explanation,
                data,
                echartsConfig,
                null,
                buildResult.Warnings,
                buildResult.AppliedFilters
            );
        }
        catch (Exception ex)
        {
            return new AnalysisResult(
                false, null, "table", userQuery, null, null,
                null, null, null, ex.Message, null, null);
        }
    }
    
    /// <summary>
    /// AI 意图识别 - 只负责理解用户想查什么 (使用 Claude)
    /// </summary>
    private async Task<QuerySpec?> RecognizeIntentAsync(
        string userQuery, 
        string? dateFrom, 
        string? dateTo, 
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var contextInfo = $@"
現在の日付: {today:yyyy-MM-dd}
今月: {today:yyyy}年{today.Month}月
今年: {today:yyyy}年

ユーザーの質問: {userQuery}
";
        
        if (!string.IsNullOrEmpty(dateFrom) || !string.IsNullOrEmpty(dateTo))
        {
            contextInfo += $"\n指定期間: {dateFrom ?? "なし"} ～ {dateTo ?? "なし"}";
        }
        
        var http = _httpClientFactory.CreateClient("openai");
        OpenAiApiHelper.SetOpenAiHeaders(http, _apiKey);

        var messages = new object[]
        {
            new { role = "system", content = IntentRecognitionPrompt },
            new { role = "user", content = contextInfo }
        };

        var openAiResponse = await OpenAiApiHelper.CallOpenAiAsync(
            http, _apiKey, "gpt-4o", messages,
            temperature: 0.1, maxTokens: 800, jsonMode: true, ct: ct);

        if (string.IsNullOrEmpty(openAiResponse.Content))
        {
            return null;
        }
        
        // 提取 JSON
        var jsonContent = ExtractJsonFromResponse(openAiResponse.Content);
        
        try
        {
            var spec = JsonSerializer.Deserialize<QuerySpec>(jsonContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return spec;
        }
        catch
        {
            return null;
        }
    }
    
    private static string ExtractJsonFromResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return "{}";
        
        var trimmed = response.Trim();
        
        if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
        {
            return trimmed;
        }
        
        var jsonBlockMatch = System.Text.RegularExpressions.Regex.Match(
            trimmed, 
            @"```(?:json)?\s*([\s\S]*?)```", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        if (jsonBlockMatch.Success)
        {
            var extracted = jsonBlockMatch.Groups[1].Value.Trim();
            if (extracted.StartsWith("{") && extracted.EndsWith("}"))
            {
                return extracted;
            }
        }
        
        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return trimmed.Substring(firstBrace, lastBrace - firstBrace + 1);
        }
        
        return "{}";
    }
    
    private async Task<JsonArray> ExecuteQueryAsync(string companyCode, string sql, CancellationToken ct)
    {
        var result = new JsonArray();
        
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue(companyCode);
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            
            while (await reader.ReadAsync(ct))
            {
                var row = new JsonObject();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    if (reader.IsDBNull(i))
                    {
                        row[name] = null;
                    }
                    else
                    {
                        var value = reader.GetValue(i);
                        row[name] = value switch
                        {
                            int intVal => intVal,
                            long longVal => longVal,
                            decimal decVal => decVal,
                            double dblVal => dblVal,
                            float fltVal => fltVal,
                            DateTime dtVal => dtVal.ToString("yyyy-MM-dd"),
                            DateOnly doVal => doVal.ToString("yyyy-MM-dd"),
                            bool boolVal => boolVal,
                            _ => value.ToString()
                        };
                    }
                }
                result.Add(row);
            }
        }
        catch (Exception ex)
        {
            var errorRow = new JsonObject { ["error"] = ex.Message };
            result.Add(errorRow);
        }
        
        return result;
    }
    
    private object GenerateEchartsConfig(string chartType, string title, string? xAxisField, string[]? yAxisFields, JsonArray data)
    {
        if (data.Count == 0 || data[0] is not JsonObject firstRow)
        {
            return new { title = new { text = title }, series = Array.Empty<object>() };
        }
        
        if (string.IsNullOrEmpty(xAxisField))
        {
            xAxisField = firstRow.Select(p => p.Key).FirstOrDefault(k => 
                k.Contains("date", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("period", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("name", StringComparison.OrdinalIgnoreCase)
            );
        }
        
        if (yAxisFields == null || yAxisFields.Length == 0)
        {
            yAxisFields = firstRow
                .Where(p => p.Value is JsonValue jv && (jv.TryGetValue<decimal>(out _) || jv.TryGetValue<int>(out _)))
                .Select(p => p.Key)
                .Take(3)
                .ToArray();
        }
        
        var xAxisData = data
            .Select(d => (d as JsonObject)?[xAxisField ?? ""]?.ToString() ?? "")
            .ToArray();
        
        return chartType switch
        {
            "line" => new
            {
                title = new { text = title, left = "center" },
                tooltip = new { trigger = "axis" },
                legend = new { bottom = 10, data = yAxisFields },
                xAxis = new { type = "category", data = xAxisData },
                yAxis = new { type = "value" },
                series = yAxisFields.Select(field => (object)new
                {
                    name = field,
                    type = "line",
                    smooth = true,
                    data = data.Select(d => GetNumericValue((d as JsonObject)?[field])).ToArray()
                }).ToArray()
            },
            "bar" => new
            {
                title = new { text = title, left = "center" },
                tooltip = new { trigger = "axis" },
                legend = new { bottom = 10, data = yAxisFields },
                xAxis = new { type = "category", data = xAxisData },
                yAxis = new { type = "value" },
                series = yAxisFields.Select(field => (object)new
                {
                    name = field,
                    type = "bar",
                    data = data.Select(d => GetNumericValue((d as JsonObject)?[field])).ToArray()
                }).ToArray()
            },
            "pie" => new
            {
                title = new { text = title, left = "center" },
                tooltip = new { trigger = "item", formatter = "{b}: {c} ({d}%)" },
                legend = new { bottom = 10 },
                series = new object[]
                {
                    new
                    {
                        type = "pie",
                        radius = "55%",
                        center = new[] { "50%", "45%" },
                        data = data.Select(d =>
                        {
                            var obj = d as JsonObject;
                            return new
                            {
                                name = obj?[xAxisField ?? ""]?.ToString() ?? "",
                                value = GetNumericValue(obj?[yAxisFields.FirstOrDefault() ?? ""])
                            };
                        }).ToArray()
                    }
                }
            },
            _ => new
            {
                title = new { text = title },
                data = data
            }
        };
    }
    
    private static decimal GetNumericValue(JsonNode? node)
    {
        if (node is null) return 0m;
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<decimal>(out var d)) return d;
            if (jv.TryGetValue<int>(out var i)) return i;
            if (jv.TryGetValue<long>(out var l)) return l;
            if (jv.TryGetValue<double>(out var dbl)) return (decimal)dbl;
        }
        return 0m;
    }
}

