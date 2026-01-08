using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Server.Modules;

/// <summary>
/// 销售订单生命周期追踪服务
/// 追踪销售订单从下单到收款的完整流程
/// </summary>
public class SalesOrderLifecycleService
{
    private readonly NpgsqlDataSource _ds;

    public SalesOrderLifecycleService(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    /// <summary>
    /// 生命周期阶段
    /// </summary>
    public enum LifecycleStage
    {
        Order,          // 受注
        DeliveryNote,   // 納品書作成
        Shipped,        // 出庫
        Invoice,        // 請求書 (optional)
        Payment         // 入金
    }

    /// <summary>
    /// 阶段状态
    /// </summary>
    public enum StageStatus
    {
        NotStarted,     // 未開始
        InProgress,     // 進行中
        Completed,      // 完了
        Skipped         // スキップ
    }

    /// <summary>
    /// 阶段信息
    /// </summary>
    public record StageInfo(
        LifecycleStage Stage,
        string StageName,
        string StageNameJp,
        StageStatus Status,
        string StatusLabel,
        DateTime? CompletedAt,
        string? DocumentNo,
        bool IsOptional
    );

    /// <summary>
    /// 生命周期信息
    /// </summary>
    public record LifecycleInfo(
        string SoNo,
        string CustomerCode,
        string CustomerName,
        decimal AmountTotal,
        string Status,
        int CompletedStages,
        int TotalStages,
        decimal ProgressPercent,
        List<StageInfo> Stages
    );

    /// <summary>
    /// 获取销售订单的生命周期状态
    /// </summary>
    public async Task<LifecycleInfo?> GetLifecycleAsync(string companyCode, string soNo, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        // 1. 获取销售订单基本信息
        string? customerCode = null, customerName = null, orderStatus = null;
        decimal amountTotal = 0m;
        DateTime? orderDate = null;

        await using (var orderCmd = new NpgsqlCommand(@"
            SELECT COALESCE(payload->'header'->>'partnerCode', payload->>'partnerCode'),
                   COALESCE(payload->'header'->>'partnerName', payload->>'partnerName'),
                   COALESCE(payload->'header'->>'status', payload->>'status'),
                   COALESCE((payload->'header'->>'amountTotal')::numeric, (payload->>'amountTotal')::numeric),
                   COALESCE((payload->'header'->>'orderDate')::date, (payload->>'orderDate')::date)
            FROM sales_orders
            WHERE company_code = $1 AND so_no = $2
            LIMIT 1", conn))
        {
            orderCmd.Parameters.AddWithValue(companyCode);
            orderCmd.Parameters.AddWithValue(soNo);
            await using var reader = await orderCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            customerCode = reader.IsDBNull(0) ? null : reader.GetString(0);
            customerName = reader.IsDBNull(1) ? customerCode : reader.GetString(1);
            orderStatus = reader.IsDBNull(2) ? "draft" : reader.GetString(2);
            amountTotal = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3);
            orderDate = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
        }

        // 2. 获取相关的纳品书信息
        var deliveryNotes = new List<(string No, string Status, DateTime? ShippedAt, DateTime? DeliveredAt)>();
        await using (var dnCmd = new NpgsqlCommand(@"
            SELECT delivery_no, status, 
                   (payload->'header'->>'shippedAt')::timestamp,
                   (payload->'header'->>'deliveredAt')::timestamp
            FROM delivery_notes
            WHERE company_code = $1 AND so_no = $2
            ORDER BY created_at DESC", conn))
        {
            dnCmd.Parameters.AddWithValue(companyCode);
            dnCmd.Parameters.AddWithValue(soNo);
            await using var reader = await dnCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                deliveryNotes.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                    reader.IsDBNull(3) ? null : reader.GetDateTime(3)
                ));
            }
        }

        // 3. 获取相关的请求书信息
        var invoices = new List<(string No, string Status, DateTime? IssuedAt, DateTime? PaidAt)>();
        await using (var invCmd = new NpgsqlCommand(@"
            SELECT invoice_no, status,
                   (payload->'header'->>'issuedAt')::timestamp,
                   (payload->'header'->>'paidAt')::timestamp
            FROM sales_invoices
            WHERE company_code = $1 
              AND EXISTS (
                  SELECT 1 FROM jsonb_array_elements(payload->'lines') line
                  WHERE line->>'soNo' = $2
              )
            ORDER BY created_at DESC", conn))
        {
            invCmd.Parameters.AddWithValue(companyCode);
            invCmd.Parameters.AddWithValue(soNo);
            await using var reader = await invCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                invoices.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                    reader.IsDBNull(3) ? null : reader.GetDateTime(3)
                ));
            }
        }

        // 4. 检查应收账款清账状态（通过凭证关联）
        bool hasPayment = false;
        DateTime? paymentDate = null;
        if (invoices.Any(i => i.Status == "paid"))
        {
            hasPayment = true;
            paymentDate = invoices.Where(i => i.Status == "paid").Select(i => i.PaidAt).Max();
        }
        else
        {
            // 检查直接关联的应收账款是否已清账
            await using var payCmd = new NpgsqlCommand(@"
                SELECT MIN(oi.cleared_at)
                FROM open_items oi
                JOIN vouchers v ON v.id = oi.voucher_id AND v.company_code = oi.company_code
                WHERE oi.company_code = $1 
                  AND oi.cleared_flag = true
                  AND v.payload->'header'->>'sourceRef' = $2", conn);
            payCmd.Parameters.AddWithValue(companyCode);
            payCmd.Parameters.AddWithValue(soNo);
            var result = await payCmd.ExecuteScalarAsync(ct);
            if (result is DateTime dt)
            {
                hasPayment = true;
                paymentDate = dt;
            }
        }

        // 5. 构建阶段信息
        var stages = new List<StageInfo>();

        // 受注阶段
        stages.Add(new StageInfo(
            LifecycleStage.Order,
            "Order",
            "受注",
            orderStatus != "cancelled" ? StageStatus.Completed : StageStatus.NotStarted,
            orderStatus != "cancelled" ? "完了" : "未開始",
            orderDate,
            soNo,
            false
        ));

        // 纳品书阶段（纳品书作成后直接进入出荷待ち状态，没有中间状态）
        var hasDeliveryNote = deliveryNotes.Count > 0;
        var latestDn = deliveryNotes.FirstOrDefault();
        stages.Add(new StageInfo(
            LifecycleStage.DeliveryNote,
            "DeliveryNote",
            "納品書作成",
            hasDeliveryNote ? StageStatus.Completed : StageStatus.NotStarted,
            hasDeliveryNote ? "完了" : "未開始",
            hasDeliveryNote ? latestDn.ShippedAt : null,
            hasDeliveryNote ? latestDn.No : null,
            false
        ));

        // 出库阶段
        var hasShipped = deliveryNotes.Any(d => d.Status == "shipped" || d.Status == "delivered");
        var shippedDn = deliveryNotes.FirstOrDefault(d => d.Status == "shipped" || d.Status == "delivered");
        stages.Add(new StageInfo(
            LifecycleStage.Shipped,
            "Shipped",
            "出庫",
            hasShipped ? StageStatus.Completed 
                : (hasDeliveryNote && latestDn.Status == "confirmed" ? StageStatus.InProgress : StageStatus.NotStarted),
            hasShipped ? "完了" 
                : (hasDeliveryNote && latestDn.Status == "confirmed" ? "出庫待ち" : "未開始"),
            shippedDn.ShippedAt,
            shippedDn.No,
            false
        ));

        // 请求书阶段（可选）
        var hasInvoice = invoices.Count > 0;
        var issuedInvoice = invoices.FirstOrDefault(i => i.Status != "cancelled");
        stages.Add(new StageInfo(
            LifecycleStage.Invoice,
            "Invoice",
            "請求書",
            hasInvoice && issuedInvoice.Status != "draft"
                ? StageStatus.Completed
                : (hasInvoice ? StageStatus.InProgress 
                    : (hasShipped ? StageStatus.Skipped : StageStatus.NotStarted)),
            hasInvoice && issuedInvoice.Status != "draft"
                ? "発行済"
                : (hasInvoice ? "作成中" : (hasShipped ? "スキップ" : "未開始")),
            issuedInvoice.IssuedAt,
            issuedInvoice.No,
            true // 可选
        ));

        // 入金阶段
        stages.Add(new StageInfo(
            LifecycleStage.Payment,
            "Payment",
            "入金",
            hasPayment ? StageStatus.Completed : StageStatus.NotStarted,
            hasPayment ? "完了" : "未開始",
            paymentDate,
            null,
            false
        ));

        // 计算进度
        var completedStages = stages.Count(s => s.Status == StageStatus.Completed || s.Status == StageStatus.Skipped);
        var requiredStages = stages.Count(s => !s.IsOptional);
        var completedRequired = stages.Count(s => !s.IsOptional && (s.Status == StageStatus.Completed || s.Status == StageStatus.Skipped));
        var progressPercent = requiredStages > 0 ? Math.Round(completedRequired * 100.0m / requiredStages, 1) : 0m;

        return new LifecycleInfo(
            soNo,
            customerCode ?? "",
            customerName ?? "",
            amountTotal,
            orderStatus ?? "draft",
            completedStages,
            stages.Count,
            progressPercent,
            stages
        );
    }

    /// <summary>
    /// 批量获取销售订单的生命周期摘要（用于列表展示）
    /// </summary>
    public async Task<Dictionary<string, LifecycleSummary>> GetLifecycleSummariesAsync(
        string companyCode,
        List<string> soNos,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, LifecycleSummary>();
        if (soNos.Count == 0) return result;

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // 初始化所有订单的状态
        foreach (var soNo in soNos)
        {
            result[soNo] = new LifecycleSummary(false, false, false, false, false, 0);
        }

        // 批量查询纳品书状态
        await using (var dnCmd = new NpgsqlCommand(@"
            SELECT so_no, status
            FROM delivery_notes
            WHERE company_code = $1 AND so_no = ANY($2)", conn))
        {
            dnCmd.Parameters.AddWithValue(companyCode);
            dnCmd.Parameters.AddWithValue(soNos.ToArray());
            await using var reader = await dnCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var soNo = reader.GetString(0);
                var status = reader.GetString(1);
                if (result.TryGetValue(soNo, out var summary))
                {
                    result[soNo] = summary with
                    {
                        HasDeliveryNote = true,
                        HasShipped = summary.HasShipped || status == "shipped" || status == "delivered"
                    };
                }
            }
        }

        // 批量查询请求书状态
        await using (var invCmd = new NpgsqlCommand(@"
            SELECT DISTINCT line->>'soNo' as so_no, si.status
            FROM sales_invoices si,
                 jsonb_array_elements(payload->'lines') line
            WHERE si.company_code = $1 AND line->>'soNo' = ANY($2)", conn))
        {
            invCmd.Parameters.AddWithValue(companyCode);
            invCmd.Parameters.AddWithValue(soNos.ToArray());
            await using var reader = await invCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var soNo = reader.IsDBNull(0) ? null : reader.GetString(0);
                var status = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (soNo != null && result.TryGetValue(soNo, out var summary))
                {
                    result[soNo] = summary with
                    {
                        HasInvoice = true,
                        HasPayment = summary.HasPayment || status == "paid"
                    };
                }
            }
        }

        // 计算完成阶段数
        foreach (var kvp in result.ToList())
        {
            var s = kvp.Value;
            int completed = 1; // 受注始终完成
            if (s.HasDeliveryNote) completed++;
            if (s.HasShipped) completed++;
            if (s.HasInvoice) completed++;
            if (s.HasPayment) completed++;
            result[kvp.Key] = s with { CompletedStages = completed };
        }

        return result;
    }

    /// <summary>
    /// 生命周期摘要
    /// </summary>
    public record LifecycleSummary(
        bool HasDeliveryNote,
        bool HasShipped,
        bool HasInvoice,
        bool HasPayment,
        bool IsComplete,
        int CompletedStages
    );
}

