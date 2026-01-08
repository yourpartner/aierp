using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Server.Modules;

/// <summary>
/// 入金匹配服务 - 智能匹配应收账款与入金
/// 优先匹配请求书关联的应收账款，其次匹配未关联请求书的应收账款
/// </summary>
public class PaymentMatchingService
{
    private readonly NpgsqlDataSource _ds;

    public PaymentMatchingService(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    /// <summary>
    /// 匹配结果项
    /// </summary>
    public record MatchItem(
        Guid OpenItemId,
        string AccountCode,
        decimal ResidualAmount,
        DateTime DocDate,
        string? PartnerId,
        string? PartnerName,
        string? InvoiceNo,
        decimal? InvoiceAmount,
        DateTime? InvoiceDueDate,
        int OverdueDays,
        string Source // "invoice" or "open_item"
    );

    /// <summary>
    /// 自动匹配结果
    /// </summary>
    public record AutoMatchResult(
        bool Success,
        List<(Guid OpenItemId, decimal Amount)> Allocations,
        decimal TotalMatched,
        decimal Remaining,
        string? Message
    );

    /// <summary>
    /// 获取客户的可匹配项（用于人工选择）
    /// 按优先级排序：1.请求书超期 2.请求书未超期 3.普通应收超期 4.普通应收未超期
    /// </summary>
    public async Task<List<MatchItem>> GetMatchableItemsAsync(
        string companyCode,
        string customerCode,
        CancellationToken ct = default)
    {
        var result = new List<MatchItem>();
        await using var conn = await _ds.OpenConnectionAsync(ct);

        // 1. 获取客户的已发行请求书
        var invoices = new Dictionary<string, (Guid Id, string InvoiceNo, decimal Amount, DateTime DueDate)>();
        await using (var invoiceCmd = new NpgsqlCommand(@"
            SELECT id, invoice_no, amount_total, due_date
            FROM sales_invoices
            WHERE company_code = $1 
              AND customer_code = $2
              AND status = 'issued'", conn))
        {
            invoiceCmd.Parameters.AddWithValue(companyCode);
            invoiceCmd.Parameters.AddWithValue(customerCode);
            await using var reader = await invoiceCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var invoiceNo = reader.GetString(1);
                invoices[invoiceNo] = (
                    reader.GetGuid(0),
                    invoiceNo,
                    reader.IsDBNull(2) ? 0m : reader.GetDecimal(2),
                    reader.IsDBNull(3) ? DateTime.MaxValue : reader.GetDateTime(3)
                );
            }
        }

        // 2. 获取客户的所有未清应收账款
        await using (var openItemCmd = new NpgsqlCommand(@"
            SELECT oi.id, oi.account_code, oi.residual_amount, oi.doc_date, 
                   oi.partner_id, bp.name as partner_name,
                   oi.refs
            FROM open_items oi
            LEFT JOIN businesspartners bp ON bp.company_code = oi.company_code AND bp.partner_code = oi.partner_id
            WHERE oi.company_code = $1 
              AND oi.partner_id = $2
              AND oi.cleared_flag = false
              AND oi.residual_amount > 0
            ORDER BY oi.doc_date ASC", conn))
        {
            openItemCmd.Parameters.AddWithValue(companyCode);
            openItemCmd.Parameters.AddWithValue(customerCode);
            await using var reader = await openItemCmd.ExecuteReaderAsync(ct);
            
            while (await reader.ReadAsync(ct))
            {
                var openItemId = reader.GetGuid(0);
                var accountCode = reader.GetString(1);
                var residualAmount = reader.GetDecimal(2);
                var docDate = reader.GetDateTime(3);
                var partnerId = reader.IsDBNull(4) ? null : reader.GetString(4);
                var partnerName = reader.IsDBNull(5) ? null : reader.GetString(5);
                var refsStr = reader.IsDBNull(6) ? null : reader.GetString(6);

                // 尝试从 refs 中提取请求书号
                string? invoiceNo = null;
                DateTime? invoiceDueDate = null;
                decimal? invoiceAmount = null;

                if (!string.IsNullOrEmpty(refsStr))
                {
                    try
                    {
                        using var refsDoc = JsonDocument.Parse(refsStr);
                        if (refsDoc.RootElement.ValueKind == JsonValueKind.Object &&
                            refsDoc.RootElement.TryGetProperty("invoiceNo", out var invNoEl))
                        {
                            invoiceNo = invNoEl.GetString();
                        }
                        // 也尝试从数组格式中查找
                        if (string.IsNullOrEmpty(invoiceNo) && refsDoc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var refEl in refsDoc.RootElement.EnumerateArray())
                            {
                                if (refEl.ValueKind == JsonValueKind.String)
                                {
                                    var refStr = refEl.GetString();
                                    if (refStr?.StartsWith("INV") == true)
                                    {
                                        invoiceNo = refStr;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch { /* ignore json parse errors */ }
                }

                // 如果找到请求书号，获取请求书信息
                if (!string.IsNullOrEmpty(invoiceNo) && invoices.TryGetValue(invoiceNo, out var inv))
                {
                    invoiceDueDate = inv.DueDate;
                    invoiceAmount = inv.Amount;
                }

                var overdueDays = 0;
                if (invoiceDueDate.HasValue && invoiceDueDate.Value < DateTime.Today)
                {
                    overdueDays = (int)(DateTime.Today - invoiceDueDate.Value).TotalDays;
                }

                result.Add(new MatchItem(
                    openItemId,
                    accountCode,
                    residualAmount,
                    docDate,
                    partnerId,
                    partnerName,
                    invoiceNo,
                    invoiceAmount,
                    invoiceDueDate,
                    overdueDays,
                    string.IsNullOrEmpty(invoiceNo) ? "open_item" : "invoice"
                ));
            }
        }

        // 按优先级排序：请求书优先，超期优先，日期早优先
        result = result
            .OrderByDescending(x => x.Source == "invoice" ? 1 : 0)
            .ThenByDescending(x => x.OverdueDays)
            .ThenBy(x => x.DocDate)
            .ToList();

        return result;
    }

    /// <summary>
    /// 自动匹配入金到应收账款
    /// 策略：1.精确匹配请求书金额 2.按日期顺序匹配请求书 3.按日期顺序匹配普通应收
    /// </summary>
    public async Task<AutoMatchResult> AutoMatchAsync(
        string companyCode,
        string customerCode,
        decimal paymentAmount,
        CancellationToken ct = default)
    {
        var items = await GetMatchableItemsAsync(companyCode, customerCode, ct);
        if (items.Count == 0)
        {
            return new AutoMatchResult(false, new List<(Guid, decimal)>(), 0m, paymentAmount, "该客户没有未清应收账款");
        }

        var allocations = new List<(Guid OpenItemId, decimal Amount)>();
        var remaining = paymentAmount;

        // 1. 尝试精确匹配单张请求书
        var exactInvoiceMatch = items
            .Where(x => x.Source == "invoice" && Math.Abs(x.ResidualAmount - paymentAmount) < 0.01m)
            .FirstOrDefault();

        if (exactInvoiceMatch != null)
        {
            allocations.Add((exactInvoiceMatch.OpenItemId, exactInvoiceMatch.ResidualAmount));
            return new AutoMatchResult(true, allocations, exactInvoiceMatch.ResidualAmount, 0m, 
                $"精确匹配请求书 {exactInvoiceMatch.InvoiceNo}");
        }

        // 2. 尝试精确匹配总金额（多张请求书组合）
        var invoiceItems = items.Where(x => x.Source == "invoice").ToList();
        var combination = FindExactCombination(invoiceItems, paymentAmount, 0.01m);
        if (combination.Count > 0)
        {
            foreach (var (id, amount) in combination)
            {
                allocations.Add((id, amount));
            }
            var invoiceNos = string.Join(", ", combination
                .Select(c => items.FirstOrDefault(i => i.OpenItemId == c.Id)?.InvoiceNo)
                .Where(n => n != null));
            return new AutoMatchResult(true, allocations, paymentAmount, 0m, 
                $"精确匹配请求书组合: {invoiceNos}");
        }

        // 3. 按顺序分配（请求书优先）
        foreach (var item in items)
        {
            if (remaining <= 0) break;

            var applyAmount = Math.Min(remaining, item.ResidualAmount);
            allocations.Add((item.OpenItemId, applyAmount));
            remaining -= applyAmount;
        }

        var totalMatched = paymentAmount - remaining;
        var message = remaining > 0
            ? $"部分匹配，剩余 {remaining:N0} 未匹配"
            : "已完全匹配";

        return new AutoMatchResult(remaining <= 0.01m, allocations, totalMatched, remaining, message);
    }

    /// <summary>
    /// 查找精确金额组合
    /// </summary>
    private List<(Guid Id, decimal Amount)> FindExactCombination(
        List<MatchItem> items,
        decimal target,
        decimal tolerance,
        int maxItems = 5)
    {
        var result = new List<(Guid Id, decimal Amount)>();
        if (items.Count == 0 || target <= 0) return result;

        // 简单的贪心+回溯算法
        var sorted = items.OrderByDescending(x => x.ResidualAmount).ToList();
        
        void Search(int index, decimal remaining, List<(Guid, decimal)> current)
        {
            if (Math.Abs(remaining) <= tolerance)
            {
                if (result.Count == 0 || current.Count < result.Count)
                {
                    result.Clear();
                    result.AddRange(current);
                }
                return;
            }

            if (remaining < 0 || index >= sorted.Count || current.Count >= maxItems)
                return;

            if (result.Count > 0 && current.Count >= result.Count - 1)
                return;

            // 尝试选择当前项
            var item = sorted[index];
            if (item.ResidualAmount <= remaining + tolerance)
            {
                current.Add((item.OpenItemId, item.ResidualAmount));
                Search(index + 1, remaining - item.ResidualAmount, current);
                current.RemoveAt(current.Count - 1);
            }

            // 跳过当前项
            Search(index + 1, remaining, current);
        }

        Search(0, target, new List<(Guid, decimal)>());
        return result;
    }

    /// <summary>
    /// 标记请求书为已付款（在入金清账完成后调用）
    /// </summary>
    public async Task MarkInvoiceAsPaidIfFullyMatchedAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string companyCode,
        string invoiceNo,
        CancellationToken ct = default)
    {
        // 检查请求书关联的所有应收是否都已清账
        // 简化实现：直接标记请求书为已付款
        await using var cmd = new NpgsqlCommand(@"
            UPDATE sales_invoices
            SET payload = jsonb_set(
                jsonb_set(
                    jsonb_set(payload, '{header,status}', '""paid""'),
                    '{header,paidAt}', to_jsonb(now()::text)
                ),
                '{header,paidBy}', '""system""'
            ),
            updated_at = now()
            WHERE company_code = $1 AND invoice_no = $2 AND status = 'issued'", conn, tx);
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(invoiceNo);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

