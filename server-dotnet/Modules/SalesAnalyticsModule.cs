using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Server.Modules;

/// <summary>
/// 销售分析模块 - 提供传统图表分析和AI自然语言分析
/// </summary>
public static class SalesAnalyticsModule
{
    public static void MapSalesAnalyticsModule(this WebApplication app)
    {
        // 销售概览统计
        app.MapGet("/analytics/sales/overview", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var period = req.Query["period"].FirstOrDefault() ?? "month"; // day, week, month, year
            var fromDate = req.Query["fromDate"].FirstOrDefault();
            var toDate = req.Query["toDate"].FirstOrDefault();

            await using var conn = await ds.OpenConnectionAsync();

            // 构建日期条件
            var dateCondition = "";
            if (!string.IsNullOrEmpty(fromDate)) dateCondition += " AND so.order_date >= $2::date";
            if (!string.IsNullOrEmpty(toDate)) dateCondition += " AND so.order_date <= $3::date";

            // 总订单数和金额
            var overviewSql = $@"
                SELECT 
                    COUNT(*) as order_count,
                    COALESCE(SUM(so.amount_total), 0) as total_amount,
                    COUNT(DISTINCT so.partner_code) as customer_count
                FROM sales_orders so
                WHERE so.company_code = $1 AND so.status != 'cancelled' {dateCondition}";

            decimal totalAmount = 0, orderCount = 0, customerCount = 0;
            await using (var cmd = new NpgsqlCommand(overviewSql, conn))
            {
                cmd.Parameters.AddWithValue(cc.ToString()!);
                if (!string.IsNullOrEmpty(fromDate)) cmd.Parameters.AddWithValue(DateOnly.Parse(fromDate));
                if (!string.IsNullOrEmpty(toDate)) cmd.Parameters.AddWithValue(DateOnly.Parse(toDate));
                
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    orderCount = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                    totalAmount = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
                    customerCount = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                }
            }

            return Results.Ok(new
            {
                orderCount,
                totalAmount,
                customerCount,
                avgOrderAmount = orderCount > 0 ? Math.Round(totalAmount / orderCount, 0) : 0
            });
        }).RequireAuthorization();

        // 按时间趋势分析
        app.MapGet("/analytics/sales/trend", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var granularity = req.Query["granularity"].FirstOrDefault() ?? "day"; // day, week, month
            var fromDate = req.Query["fromDate"].FirstOrDefault() ?? DateTime.Today.AddMonths(-3).ToString("yyyy-MM-dd");
            var toDate = req.Query["toDate"].FirstOrDefault() ?? DateTime.Today.ToString("yyyy-MM-dd");

            await using var conn = await ds.OpenConnectionAsync();

            var truncFunc = granularity switch
            {
                "week" => "date_trunc('week', so.order_date)",
                "month" => "date_trunc('month', so.order_date)",
                _ => "so.order_date"
            };

            var sql = $@"
                SELECT 
                    {truncFunc}::date as period,
                    COUNT(*) as order_count,
                    COALESCE(SUM(so.amount_total), 0) as total_amount
                FROM sales_orders so
                WHERE so.company_code = $1 
                  AND so.status != 'cancelled'
                  AND so.order_date >= $2::date 
                  AND so.order_date <= $3::date
                GROUP BY {truncFunc}
                ORDER BY period ASC";

            var data = new List<object>();
            await using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue(cc.ToString()!);
                cmd.Parameters.AddWithValue(DateOnly.Parse(fromDate));
                cmd.Parameters.AddWithValue(DateOnly.Parse(toDate));
                
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    data.Add(new
                    {
                        period = reader.GetDateTime(0).ToString("yyyy-MM-dd"),
                        orderCount = reader.GetInt64(1),
                        totalAmount = reader.GetDecimal(2)
                    });
                }
            }

            return Results.Ok(new { data, granularity, fromDate, toDate });
        }).RequireAuthorization();

        // 按客户分析
        app.MapGet("/analytics/sales/by-customer", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var fromDate = req.Query["fromDate"].FirstOrDefault() ?? DateTime.Today.AddMonths(-12).ToString("yyyy-MM-dd");
            var toDate = req.Query["toDate"].FirstOrDefault() ?? DateTime.Today.ToString("yyyy-MM-dd");
            var limit = int.TryParse(req.Query["limit"].FirstOrDefault(), out var l) ? l : 20;

            await using var conn = await ds.OpenConnectionAsync();

            var sql = @"
                SELECT 
                    so.partner_code,
                    COALESCE(bp.name, so.partner_code) as customer_name,
                    COUNT(*) as order_count,
                    COALESCE(SUM(so.amount_total), 0) as total_amount,
                    MAX(so.order_date) as last_order_date
                FROM sales_orders so
                LEFT JOIN businesspartners bp ON bp.company_code = so.company_code AND bp.partner_code = so.partner_code
                WHERE so.company_code = $1 
                  AND so.status != 'cancelled'
                  AND so.order_date >= $2::date 
                  AND so.order_date <= $3::date
                GROUP BY so.partner_code, bp.name
                ORDER BY total_amount DESC
                LIMIT $4";

            var data = new List<object>();
            await using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue(cc.ToString()!);
                cmd.Parameters.AddWithValue(DateOnly.Parse(fromDate));
                cmd.Parameters.AddWithValue(DateOnly.Parse(toDate));
                cmd.Parameters.AddWithValue(limit);
                
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    data.Add(new
                    {
                        customerCode = reader.GetString(0),
                        customerName = reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1),
                        orderCount = reader.GetInt64(2),
                        totalAmount = reader.GetDecimal(3),
                        lastOrderDate = reader.IsDBNull(4) ? null : reader.GetDateTime(4).ToString("yyyy-MM-dd")
                    });
                }
            }

            return Results.Ok(new { data });
        }).RequireAuthorization();

        // 按品类/商品分析
        app.MapGet("/analytics/sales/by-product", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var fromDate = req.Query["fromDate"].FirstOrDefault() ?? DateTime.Today.AddMonths(-12).ToString("yyyy-MM-dd");
            var toDate = req.Query["toDate"].FirstOrDefault() ?? DateTime.Today.ToString("yyyy-MM-dd");
            var limit = int.TryParse(req.Query["limit"].FirstOrDefault(), out var l) ? l : 20;

            await using var conn = await ds.OpenConnectionAsync();

            var sql = @"
                SELECT 
                    line->>'materialCode' as material_code,
                    COALESCE(m.name, line->>'materialName', line->>'materialCode') as material_name,
                    SUM((line->>'quantity')::numeric) as total_qty,
                    SUM((line->>'amount')::numeric) as total_amount
                FROM sales_orders so,
                     jsonb_array_elements(so.payload->'lines') line
                LEFT JOIN materials m ON m.company_code = so.company_code AND m.material_code = line->>'materialCode'
                WHERE so.company_code = $1 
                  AND so.status != 'cancelled'
                  AND so.order_date >= $2::date 
                  AND so.order_date <= $3::date
                GROUP BY line->>'materialCode', m.name, line->>'materialName'
                ORDER BY total_amount DESC NULLS LAST
                LIMIT $4";

            var data = new List<object>();
            await using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue(cc.ToString()!);
                cmd.Parameters.AddWithValue(DateOnly.Parse(fromDate));
                cmd.Parameters.AddWithValue(DateOnly.Parse(toDate));
                cmd.Parameters.AddWithValue(limit);
                
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    data.Add(new
                    {
                        materialCode = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        materialName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        totalQty = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2),
                        totalAmount = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3)
                    });
                }
            }

            return Results.Ok(new { data });
        }).RequireAuthorization();

        // 客户下单频率变化分析（用于发现上升/下降客户）
        app.MapGet("/analytics/sales/customer-trend", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var monthsToCompare = int.TryParse(req.Query["months"].FirstOrDefault(), out var m) ? m : 3;
            var limit = int.TryParse(req.Query["limit"].FirstOrDefault(), out var l) ? l : 20;
            var trend = req.Query["trend"].FirstOrDefault() ?? "both"; // rising, declining, both

            await using var conn = await ds.OpenConnectionAsync();

            // 比较最近N个月和之前N个月的订单数据
            var sql = @"
                WITH recent AS (
                    SELECT 
                        partner_code,
                        COUNT(*) as order_count,
                        COALESCE(SUM(amount_total), 0) as total_amount
                    FROM sales_orders
                    WHERE company_code = $1 
                      AND status != 'cancelled'
                      AND order_date >= (CURRENT_DATE - $2 * INTERVAL '1 month')::date
                    GROUP BY partner_code
                ),
                previous AS (
                    SELECT 
                        partner_code,
                        COUNT(*) as order_count,
                        COALESCE(SUM(amount_total), 0) as total_amount
                    FROM sales_orders
                    WHERE company_code = $1 
                      AND status != 'cancelled'
                      AND order_date >= (CURRENT_DATE - $2 * 2 * INTERVAL '1 month')::date
                      AND order_date < (CURRENT_DATE - $2 * INTERVAL '1 month')::date
                    GROUP BY partner_code
                )
                SELECT 
                    COALESCE(r.partner_code, p.partner_code) as partner_code,
                    COALESCE(bp.name, COALESCE(r.partner_code, p.partner_code)) as customer_name,
                    COALESCE(p.order_count, 0) as prev_order_count,
                    COALESCE(p.total_amount, 0) as prev_amount,
                    COALESCE(r.order_count, 0) as recent_order_count,
                    COALESCE(r.total_amount, 0) as recent_amount,
                    COALESCE(r.order_count, 0) - COALESCE(p.order_count, 0) as order_change,
                    COALESCE(r.total_amount, 0) - COALESCE(p.total_amount, 0) as amount_change,
                    CASE 
                        WHEN COALESCE(p.total_amount, 0) = 0 THEN 100
                        ELSE ROUND(((COALESCE(r.total_amount, 0) - COALESCE(p.total_amount, 0)) / p.total_amount * 100)::numeric, 1)
                    END as change_percent
                FROM recent r
                FULL OUTER JOIN previous p ON r.partner_code = p.partner_code
                LEFT JOIN businesspartners bp ON bp.company_code = $1 AND bp.partner_code = COALESCE(r.partner_code, p.partner_code)
                WHERE (r.partner_code IS NOT NULL OR p.partner_code IS NOT NULL)
                ORDER BY amount_change DESC
                LIMIT $3";

            var data = new List<object>();
            await using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue(cc.ToString()!);
                cmd.Parameters.AddWithValue(monthsToCompare);
                cmd.Parameters.AddWithValue(limit * 2); // 获取更多数据以便筛选
                
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var amountChange = reader.IsDBNull(7) ? 0m : reader.GetDecimal(7);
                    var trendType = amountChange > 0 ? "rising" : (amountChange < 0 ? "declining" : "stable");

                    if (trend == "both" || trend == trendType)
                    {
                        data.Add(new
                        {
                            customerCode = reader.GetString(0),
                            customerName = reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1),
                            prevOrderCount = reader.GetInt64(2),
                            prevAmount = reader.GetDecimal(3),
                            recentOrderCount = reader.GetInt64(4),
                            recentAmount = reader.GetDecimal(5),
                            orderChange = reader.GetInt64(6),
                            amountChange = amountChange,
                            changePercent = reader.IsDBNull(8) ? 0m : reader.GetDecimal(8),
                            trend = trendType
                        });
                    }
                }
            }

            // 根据趋势筛选和限制
            if (trend == "rising")
                data = data.OrderByDescending(d => ((dynamic)d).amountChange).Take(limit).ToList();
            else if (trend == "declining")
                data = data.OrderBy(d => ((dynamic)d).amountChange).Take(limit).ToList();
            else
                data = data.Take(limit).ToList();

            return Results.Ok(new { data, monthsCompared = monthsToCompare });
        }).RequireAuthorization();

        // 生成 ECharts 配置
        app.MapPost("/analytics/sales/echarts-config", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var body = await JsonDocument.ParseAsync(req.Body);
            var root = body.RootElement;
            
            var chartType = root.TryGetProperty("chartType", out var ct) ? ct.GetString() : "line";
            var dataSource = root.TryGetProperty("dataSource", out var dse) ? dse.GetString() : "trend";
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : "销售分析";

            // 根据数据源类型生成不同的 ECharts 配置
            var config = chartType switch
            {
                "bar" => GenerateBarChartConfig(title!),
                "pie" => GeneratePieChartConfig(title!),
                _ => GenerateLineChartConfig(title!)
            };

            return Results.Ok(new { config, chartType, dataSource });
        }).RequireAuthorization();
    }

    private static object GenerateLineChartConfig(string title)
    {
        return new
        {
            title = new { text = title, left = "center" },
            tooltip = new { trigger = "axis" },
            legend = new { bottom = 10 },
            xAxis = new { type = "category", data = Array.Empty<string>() },
            yAxis = new { type = "value" },
            series = new object[]
            {
                new { name = "订单数", type = "line", data = Array.Empty<int>() },
                new { name = "销售额", type = "line", yAxisIndex = 0, data = Array.Empty<decimal>() }
            }
        };
    }

    private static object GenerateBarChartConfig(string title)
    {
        return new
        {
            title = new { text = title, left = "center" },
            tooltip = new { trigger = "axis" },
            legend = new { bottom = 10 },
            xAxis = new { type = "category", data = Array.Empty<string>() },
            yAxis = new { type = "value" },
            series = new[]
            {
                new { name = "销售额", type = "bar", data = Array.Empty<decimal>() }
            }
        };
    }

    private static object GeneratePieChartConfig(string title)
    {
        return new
        {
            title = new { text = title, left = "center" },
            tooltip = new { trigger = "item" },
            legend = new { bottom = 10 },
            series = new[]
            {
                new
                {
                    name = "销售占比",
                    type = "pie",
                    radius = "50%",
                    data = Array.Empty<object>()
                }
            }
        };
    }
}

