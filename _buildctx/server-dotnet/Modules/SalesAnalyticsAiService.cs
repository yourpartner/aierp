using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

/// <summary>
/// AI销售分析服务 - 处理自然语言查询并生成动态图表 (使用 Claude)
/// </summary>
public class SalesAnalyticsAiService
{
    private readonly NpgsqlDataSource _ds;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;
    
    private static readonly string SystemPrompt = @"
あなたは販売データ分析の専門家です。ユーザーの自然言語クエリを分析し、以下のJSON形式で応答してください。
**必ずJSON形式のみで応答し、他のテキストは含めないでください。**

応答JSON形式:
{
  ""sql"": ""実行するSQL文（PostgreSQL構文）"",
  ""chartType"": ""line|bar|pie|table"",
  ""chartTitle"": ""グラフのタイトル"",
  ""xAxisField"": ""X軸に使用するフィールド名"",
  ""yAxisFields"": [""Y軸に使用するフィールド名の配列""],
  ""explanation"": ""分析結果の説明文""
}

利用可能なテーブル:
- sales_orders: id, company_code, so_no, partner_code, amount_total, order_date, status, payload(JSONB: header, lines)
- delivery_notes: id, company_code, delivery_no, so_no, customer_code, customer_name, delivery_date, status
- sales_invoices: id, company_code, invoice_no, customer_code, customer_name, invoice_date, due_date, amount_total, status
- businesspartners: id, company_code, partner_code, name, payload(JSONB)
- materials: id, company_code, material_code, name

注意事項:
- company_codeはシステムが自動的に提供します。SQLでは必ず company_code = $1 の条件を含めてください
- ユーザーに会社コードを確認する必要はありません。すでにログイン中の会社のデータのみを対象とします
- **重要**: 日付に関するクエリでは、必ず上記の【システム情報】に記載された「今日の日付」を基準にしてください。絶対に古い日付や推測した日付を使用しないでください
- 「今月」「今年」「先月」などの相対日付は、必ずPostgreSQLの CURRENT_DATE を使用してください
- 集計結果は適切にグループ化してください
- パフォーマンスのため、LIMIT 100を追加してください
- **回答は必ずJSON形式のみ、説明文などは含めないでください**
- explanationには分析結果の簡潔な説明を日本語で記載してください
";

    public SalesAnalyticsAiService(NpgsqlDataSource ds, IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _ds = ds;
        _httpClientFactory = httpClientFactory;
        _apiKey = config["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
    }

    /// <summary>
    /// AI分析结果
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
        string? Error
    );

    /// <summary>
    /// 处理自然语言查询
    /// </summary>
    public async Task<AnalysisResult> AnalyzeAsync(
        string companyCode,
        string userQuery,
        string? dateFrom = null,
        string? dateTo = null,
        CancellationToken ct = default)
    {
        try
        {
            // 1. 构建提示
            var today = DateTime.Today;
            var contextInfo = $@"
【重要な日付情報 - 必ず守ってください】
現在の日付は {today:yyyy-MM-dd} です。今日は {today:yyyy}年{today.Month}月{today.Day}日 です。
- 「今月」= {today:yyyy}年{today.Month}月
- 「今年」= {today:yyyy}年  
- 「先月」= {today.AddMonths(-1):yyyy}年{today.AddMonths(-1).Month}月

【絶対禁止事項】
- SQLに 2023、2022、2021 などの過去の年をハードコードしないでください
- 必ず CURRENT_DATE、DATE_TRUNC、EXTRACT 関数を使用してください

【会社コード】$1 = '{companyCode}' （自動設定済み）

【ユーザーの質問】{userQuery}

【SQLの書き方例】
- 今月: WHERE DATE_TRUNC('month', order_date) = DATE_TRUNC('month', CURRENT_DATE)
- 今年: WHERE EXTRACT(YEAR FROM order_date) = EXTRACT(YEAR FROM CURRENT_DATE)
- 先月: WHERE DATE_TRUNC('month', order_date) = DATE_TRUNC('month', CURRENT_DATE - INTERVAL '1 month')
- 今年11月: WHERE EXTRACT(YEAR FROM order_date) = EXTRACT(YEAR FROM CURRENT_DATE) AND EXTRACT(MONTH FROM order_date) = 11
";

            // 2. 调用 OpenAI 生成 SQL 和图表配置
            var http = _httpClientFactory.CreateClient("openai");
            OpenAiApiHelper.SetOpenAiHeaders(http, _apiKey);

            var messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = contextInfo }
            };

            var openAiResponse = await OpenAiApiHelper.CallOpenAiAsync(
                http, _apiKey, "gpt-4o", messages,
                temperature: 0.2, maxTokens: 1000, jsonMode: true, ct: ct);

            if (string.IsNullOrWhiteSpace(openAiResponse.Content))
            {
                return new AnalysisResult(false, null, "table", userQuery, null, null, null, null, null, "AI 响应为空");
            }

            var responseContent = openAiResponse.Content ?? "";

            // 3. 解析 AI 响应 - 尝试提取 JSON
            var jsonContent = ExtractJsonFromResponse(responseContent);
            using var aiResponse = JsonDocument.Parse(jsonContent);
            var root = aiResponse.RootElement;

            var sql = root.TryGetProperty("sql", out var sqlEl) ? sqlEl.GetString() : null;
            var chartType = root.TryGetProperty("chartType", out var ctEl) ? ctEl.GetString() ?? "table" : "table";
            var chartTitle = root.TryGetProperty("chartTitle", out var titleEl) ? titleEl.GetString() ?? userQuery : userQuery;
            var xAxisField = root.TryGetProperty("xAxisField", out var xEl) ? xEl.GetString() : null;
            var yAxisFields = root.TryGetProperty("yAxisFields", out var yEl) && yEl.ValueKind == JsonValueKind.Array
                ? yEl.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                : null;
            var explanation = root.TryGetProperty("explanation", out var expEl) ? expEl.GetString() : null;

            if (string.IsNullOrEmpty(sql))
            {
                return new AnalysisResult(false, null, "table", chartTitle, null, null, null, null, null, "SQLの生成に失敗しました");
            }

            // 4. 执行 SQL
            var data = await ExecuteQueryAsync(companyCode, sql, ct);

            // 5. 生成 ECharts 配置
            var echartsConfig = GenerateEchartsConfig(chartType, chartTitle, xAxisField, yAxisFields, data);

            return new AnalysisResult(
                true,
                sql,
                chartType,
                chartTitle,
                xAxisField,
                yAxisFields,
                explanation,
                data,
                echartsConfig,
                null
            );
        }
        catch (Exception ex)
        {
            return new AnalysisResult(false, null, "table", userQuery, null, null, null, null, null, ex.Message);
        }
    }

    private async Task<JsonArray> ExecuteQueryAsync(string companyCode, string sql, CancellationToken ct)
    {
        var result = new JsonArray();

        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);

            // 添加公司代码参数
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
            // 如果 SQL 执行失败，返回错误信息
            var errorRow = new JsonObject
            {
                ["error"] = ex.Message
            };
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

        // 如果没有指定字段，尝试自动推断
        if (string.IsNullOrEmpty(xAxisField))
        {
            xAxisField = firstRow.Select(p => p.Key).FirstOrDefault(k => 
                k.Contains("date", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("period", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("name", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("code", StringComparison.OrdinalIgnoreCase)
            );
        }

        if (yAxisFields == null || yAxisFields.Length == 0)
        {
            yAxisFields = firstRow
                .Where(p => p.Value is JsonValue jv && (jv.TryGetValue<decimal>(out _) || jv.TryGetValue<int>(out _) || jv.TryGetValue<long>(out _)))
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
                series = yAxisFields.Select(field => new
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
                series = yAxisFields.Select(field => new
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
                series = new[]
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

    /// <summary>
    /// 从 AI 响应中提取 JSON，处理可能包含的 markdown 代码块或其他文本
    /// </summary>
    private static string ExtractJsonFromResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return "{}";
        
        var trimmed = response.Trim();
        
        // 如果已经是纯 JSON，直接返回
        if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
        {
            return trimmed;
        }
        
        // 尝试提取 markdown 代码块中的 JSON
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
        
        // 尝试找到第一个 { 和最后一个 } 之间的内容
        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return trimmed.Substring(firstBrace, lastBrace - firstBrace + 1);
        }
        
        // 无法提取，返回空对象
        return "{}";
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
