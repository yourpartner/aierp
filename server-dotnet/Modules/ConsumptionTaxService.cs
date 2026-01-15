using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Server.Modules;

/// <summary>
/// 消費税申告書計算・生成サービス
/// 日本の消費税法に準拠した申告書データを生成
/// </summary>
public class ConsumptionTaxService
{
    private readonly NpgsqlDataSource _ds;
    
    public ConsumptionTaxService(NpgsqlDataSource ds)
    {
        _ds = ds;
    }
    
    /// <summary>
    /// 簡易課税のみなし仕入率
    /// </summary>
    public static readonly Dictionary<int, decimal> SimplifiedRates = new()
    {
        { 1, 0.90m }, // 第一種（卸売業）
        { 2, 0.80m }, // 第二種（小売業）
        { 3, 0.70m }, // 第三種（製造業等）
        { 4, 0.60m }, // 第四種（その他）
        { 5, 0.50m }, // 第五種（サービス業等）
        { 6, 0.40m }, // 第六種（不動産業）
    };
    
    /// <summary>
    /// 会社の消費税設定を取得
    /// </summary>
    public async Task<JsonObject?> GetSettingsAsync(string companyCode)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM company_settings WHERE company_code = $1";
        cmd.Parameters.AddWithValue(companyCode);
        
        var json = (string?)await cmd.ExecuteScalarAsync();
        if (string.IsNullOrEmpty(json)) return null;
        
        var payload = JsonNode.Parse(json) as JsonObject;
        return payload?["consumptionTax"] as JsonObject;
    }
    
    /// <summary>
    /// 会社の消費税設定を保存
    /// </summary>
    public async Task SaveSettingsAsync(string companyCode, JsonObject settings)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        
        // まず既存の company_settings を取得
        await using var getCmd = conn.CreateCommand();
        getCmd.CommandText = "SELECT payload FROM company_settings WHERE company_code = $1";
        getCmd.Parameters.AddWithValue(companyCode);
        var existingJson = (string?)await getCmd.ExecuteScalarAsync();
        
        JsonObject payload;
        if (string.IsNullOrEmpty(existingJson))
        {
            payload = new JsonObject();
        }
        else
        {
            payload = JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject();
        }
        
        payload["consumptionTax"] = settings;
        
        await using var upsertCmd = conn.CreateCommand();
        upsertCmd.CommandText = @"
            INSERT INTO company_settings (company_code, payload)
            VALUES ($1, $2::jsonb)
            ON CONFLICT (company_code) DO UPDATE SET
                payload = $2::jsonb,
                updated_at = now()";
        upsertCmd.Parameters.AddWithValue(companyCode);
        upsertCmd.Parameters.AddWithValue(payload.ToJsonString());
        await upsertCmd.ExecuteNonQueryAsync();
    }
    
    /// <summary>
    /// 指定期間の消費税を集計
    /// </summary>
    public async Task<ConsumptionTaxCalculation> CalculateAsync(
        string companyCode, 
        DateTime fromDate, 
        DateTime toDate,
        string taxationMethod,
        int simplifiedCategory = 5)
    {
        var calc = new ConsumptionTaxCalculation
        {
            Period = new PeriodInfo { From = fromDate, To = toDate },
            TaxationMethod = taxationMethod
        };
        
        await using var conn = await _ds.OpenConnectionAsync();
        
        // 科目ごとの消費税区分を取得
        var accountTaxTypes = new Dictionary<string, (string TaxType, string? TaxCategory)>();
        await using (var accCmd = conn.CreateCommand())
        {
            accCmd.CommandText = @"
                SELECT account_code, 
                       payload->>'taxType' as tax_type,
                       payload->>'taxCategory' as tax_category
                FROM accounts 
                WHERE company_code = $1";
            accCmd.Parameters.AddWithValue(companyCode);
            
            await using var reader = await accCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var code = reader.GetString(0);
                var taxType = reader.IsDBNull(1) ? "NON_TAX" : reader.GetString(1);
                var taxCategory = reader.IsDBNull(2) ? null : reader.GetString(2);
                accountTaxTypes[code] = (taxType, taxCategory);
            }
        }
        
        // 伝票明細を集計
        // インボイス登録番号（T番号）の有無により仕入税額控除率を計算
        await using (var voucherCmd = conn.CreateCommand())
        {
            voucherCmd.CommandText = @"
                SELECT 
                    line->>'accountCode' as account_code,
                    line->>'drcr' as drcr,
                    COALESCE((line->>'amount')::numeric, 0) as amount,
                    COALESCE((line->>'taxAmount')::numeric, 0) as tax_amount,
                    (v.payload->'header'->>'postingDate')::date as posting_date,
                    v.payload->'header'->>'invoiceRegistrationNo' as invoice_no,
                    v.payload->'header'->>'invoiceRegistrationStatus' as invoice_status
                FROM vouchers v,
                     jsonb_array_elements(v.payload->'lines') as line
                WHERE v.company_code = $1
                  AND (v.payload->'header'->>'postingDate')::date >= $2
                  AND (v.payload->'header'->>'postingDate')::date <= $3";
            voucherCmd.Parameters.AddWithValue(companyCode);
            voucherCmd.Parameters.AddWithValue(fromDate);
            voucherCmd.Parameters.AddWithValue(toDate);
            
            await using var reader = await voucherCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var accountCode = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var drcr = reader.IsDBNull(1) ? "DR" : reader.GetString(1);
                var amount = reader.GetDecimal(2);
                var taxAmount = reader.GetDecimal(3);
                var postingDate = reader.IsDBNull(4) ? fromDate : reader.GetDateTime(4);
                var invoiceNo = reader.IsDBNull(5) ? null : reader.GetString(5);
                var invoiceStatus = reader.IsDBNull(6) ? null : reader.GetString(6);
                
                if (!accountTaxTypes.TryGetValue(accountCode, out var taxInfo))
                    continue;
                
                var (taxType, taxCategory) = taxInfo;
                
                // 金額の符号を調整（CR科目は正、DR科目は負として集計）
                var signedAmount = drcr == "CR" ? amount : -amount;
                var signedTax = drcr == "CR" ? taxAmount : -taxAmount;
                
                // 売上系科目
                // OUTPUT_TAX も売上として扱う（既存科目との互換性）
                if (taxCategory == "sales" || taxType.StartsWith("TAXABLE") || taxType == "EXPORT" || taxType == "EXEMPT" || taxType == "OUTPUT_TAX")
                {
                    switch (taxType)
                    {
                        case "TAXABLE_10":
                            calc.Sales.Taxable10.NetAmount += signedAmount;
                            calc.Sales.Taxable10.TaxAmount += signedTax;
                            break;
                        case "TAXABLE_8":
                            calc.Sales.Taxable8.NetAmount += signedAmount;
                            calc.Sales.Taxable8.TaxAmount += signedTax;
                            break;
                        case "EXEMPT":
                            calc.Sales.Exempt.NetAmount += signedAmount;
                            break;
                        case "EXPORT":
                            calc.Sales.Export.NetAmount += signedAmount;
                            break;
                        case "OUTPUT_TAX":
                            // OUTPUT_TAX科目は標準税率10%の売上として扱う
                            // 税額がある場合は税額を使用、なければ金額から逆算
                            calc.Sales.Taxable10.NetAmount += signedAmount;
                            calc.Sales.Taxable10.TaxAmount += signedTax != 0 ? signedTax : Math.Round(signedAmount * 0.10m, 0);
                            break;
                    }
                }
                // 仕入系科目
                // INPUT_TAX も仕入として扱う（既存科目との互換性）
                else if (taxCategory == "purchase" || taxType.StartsWith("INPUT"))
                {
                    // 仕入は DR が正なので符号反転
                    var purchaseAmount = -signedAmount;
                    var purchaseTax = -signedTax;
                    
                    // インボイス制度：T番号の有無で控除率を決定
                    // 有効なT番号がある場合は100%控除、ない場合は経過措置により80%/50%/0%
                    var deductionRate = GetInvoiceDeductionRate(invoiceNo, invoiceStatus, postingDate);
                    
                    // 控除可能な税額と控除対象外の税額を計算
                    var actualTax = purchaseTax != 0 ? purchaseTax : Math.Round(purchaseAmount * 0.10m, 0);
                    var deductibleTax = Math.Round(actualTax * deductionRate, 0);
                    var nonDeductibleTax = actualTax - deductibleTax;
                    
                    switch (taxType)
                    {
                        case "INPUT_10":
                            calc.Purchases.Taxable10.NetAmount += purchaseAmount;
                            calc.Purchases.Taxable10.TaxAmount += deductibleTax;
                            // 控除対象外：不能控除的税額のみ（税抜金額は全額課税仕入に計上済み）
                            if (nonDeductibleTax > 0)
                            {
                                calc.Purchases.NonDeductible.TaxAmount += nonDeductibleTax;
                            }
                            break;
                        case "INPUT_8":
                            calc.Purchases.Taxable8.NetAmount += purchaseAmount;
                            calc.Purchases.Taxable8.TaxAmount += deductibleTax;
                            if (nonDeductibleTax > 0)
                            {
                                calc.Purchases.NonDeductible.TaxAmount += nonDeductibleTax;
                            }
                            break;
                        case "INPUT_TAX":
                            // INPUT_TAX科目は標準税率10%の仕入として扱う
                            calc.Purchases.Taxable10.NetAmount += purchaseAmount;
                            calc.Purchases.Taxable10.TaxAmount += deductibleTax;
                            if (nonDeductibleTax > 0)
                            {
                                calc.Purchases.NonDeductible.TaxAmount += nonDeductibleTax;
                            }
                            break;
                    }
                }
            }
        }
        
        // 合計計算
        calc.Sales.Total.NetAmount = calc.Sales.Taxable10.NetAmount + calc.Sales.Taxable8.NetAmount 
                                    + calc.Sales.Exempt.NetAmount + calc.Sales.Export.NetAmount;
        calc.Sales.Total.TaxAmount = calc.Sales.Taxable10.TaxAmount + calc.Sales.Taxable8.TaxAmount;
        
        // 仕入合計：税抜金額は全額、税額は控除可能分のみ
        // NonDeductible.TaxAmount は控除対象外（経過措置による）
        calc.Purchases.Total.NetAmount = calc.Purchases.Taxable10.NetAmount + calc.Purchases.Taxable8.NetAmount;
        calc.Purchases.Total.TaxAmount = calc.Purchases.Taxable10.TaxAmount + calc.Purchases.Taxable8.TaxAmount;
        
        // 税額計算（課税方式別）
        CalculateTaxDue(calc, taxationMethod, simplifiedCategory);
        
        return calc;
    }
    
    /// <summary>
    /// 納付税額を計算
    /// </summary>
    private void CalculateTaxDue(ConsumptionTaxCalculation calc, string taxationMethod, int simplifiedCategory)
    {
        decimal salesTaxTotal = calc.Sales.Total.TaxAmount;
        decimal purchaseTaxDeductible;
        
        switch (taxationMethod)
        {
            case "simplified":
                // 簡易課税: 売上税額 × (1 - みなし仕入率)
                var rate = SimplifiedRates.GetValueOrDefault(simplifiedCategory, 0.50m);
                purchaseTaxDeductible = salesTaxTotal * rate;
                calc.Calculation.SimplifiedCategory = simplifiedCategory;
                calc.Calculation.DeemedPurchaseRate = rate;
                break;
                
            case "special_20pct":
                // 2割特例: 売上税額 × 80%
                purchaseTaxDeductible = salesTaxTotal * 0.80m;
                break;
                
            default: // general
                // 原則課税: 実際の仕入税額
                purchaseTaxDeductible = calc.Purchases.Total.TaxAmount;
                break;
        }
        
        calc.Calculation.SalesTaxTotal = salesTaxTotal;
        calc.Calculation.PurchaseTaxDeductible = purchaseTaxDeductible;
        
        // 差引税額（国税消費税）= 売上税額 - 仕入税額控除
        var netConsumptionTax = salesTaxTotal - purchaseTaxDeductible;
        // 100円未満切捨て
        calc.Calculation.NetConsumptionTax = Math.Floor(netConsumptionTax / 100) * 100;
        
        // 地方消費税 = 国税消費税 × 22/78
        calc.Calculation.LocalConsumptionTax = Math.Floor(calc.Calculation.NetConsumptionTax * 22 / 78);
        
        // 納付税額合計
        calc.Calculation.TotalTaxDue = calc.Calculation.NetConsumptionTax + calc.Calculation.LocalConsumptionTax;
        
        // 中間納付額を差し引いた確定納付額
        calc.Calculation.FinalPayment = calc.Calculation.TotalTaxDue - calc.Calculation.InterimPaid;
        
        // 税率別内訳
        CalculateBreakdown(calc);
    }
    
    /// <summary>
    /// インボイス制度に基づく仕入税額控除率を計算
    /// T番号（インボイス登録番号）の有無と経過措置により控除率を決定
    /// </summary>
    /// <param name="invoiceNo">インボイス登録番号（T + 13桁）</param>
    /// <param name="invoiceStatus">検証結果（valid/invalid/null）</param>
    /// <param name="postingDate">転記日</param>
    /// <returns>控除率（1.0 = 100%, 0.8 = 80%, 0.5 = 50%, 0.0 = 0%）</returns>
    private static decimal GetInvoiceDeductionRate(string? invoiceNo, string? invoiceStatus, DateTime postingDate)
    {
        // 有効なT番号がある場合は100%控除
        if (!string.IsNullOrEmpty(invoiceNo) && invoiceStatus == "valid")
        {
            return 1.00m;
        }
        
        // T番号がない、または無効な場合は経過措置を適用
        // 2023/10/01 ~ 2026/09/30: 80%控除
        // 2026/10/01 ~ 2029/09/30: 50%控除
        // 2029/10/01 ~          : 0%控除（控除不可）
        if (postingDate < new DateTime(2026, 10, 1))
        {
            return 0.80m;
        }
        else if (postingDate < new DateTime(2029, 10, 1))
        {
            return 0.50m;
        }
        else
        {
            return 0.00m;
        }
    }
    
    /// <summary>
    /// 税率別内訳を計算
    /// </summary>
    private void CalculateBreakdown(ConsumptionTaxCalculation calc)
    {
        // 標準税率10%の内訳
        if (calc.Sales.Taxable10.NetAmount > 0)
        {
            // 課税標準額（千円未満切捨て）
            var taxBase10 = Math.Floor(calc.Sales.Taxable10.NetAmount / 1000) * 1000;
            calc.Breakdown.Standard10.SalesNetAmount = calc.Sales.Taxable10.NetAmount;
            calc.Breakdown.Standard10.SalesTaxAmount = calc.Sales.Taxable10.TaxAmount;
            calc.Breakdown.Standard10.TaxBase = taxBase10;
            // 消費税（国税）7.8%
            calc.Breakdown.Standard10.ConsumptionTax78 = taxBase10 * 0.078m;
            // 地方消費税 2.2%
            calc.Breakdown.Standard10.LocalTax22 = taxBase10 * 0.022m;
        }
        
        // 軽減税率8%の内訳
        if (calc.Sales.Taxable8.NetAmount > 0)
        {
            var taxBase8 = Math.Floor(calc.Sales.Taxable8.NetAmount / 1000) * 1000;
            calc.Breakdown.Reduced8.SalesNetAmount = calc.Sales.Taxable8.NetAmount;
            calc.Breakdown.Reduced8.SalesTaxAmount = calc.Sales.Taxable8.TaxAmount;
            calc.Breakdown.Reduced8.TaxBase = taxBase8;
            // 消費税（国税）6.24%
            calc.Breakdown.Reduced8.ConsumptionTax624 = taxBase8 * 0.0624m;
            // 地方消費税 1.76%
            calc.Breakdown.Reduced8.LocalTax176 = taxBase8 * 0.0176m;
        }
    }
    
    /// <summary>
    /// 集計明細を取得（ドリルダウン用）
    /// </summary>
    public async Task<List<VoucherLineDetail>> GetDetailsAsync(
        string companyCode, 
        DateTime fromDate, 
        DateTime toDate,
        string category) // e.g. "sales_taxable_10", "purchase_taxable_10"
    {
        var details = new List<VoucherLineDetail>();
        var targetTaxTypes = GetTaxTypesForCategory(category);
        
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        
        // 只通过 baseLineNo 关联获取消费税金额
        // 消费税明细的 baseLineNo 指向税基明细的 lineNo
        cmd.CommandText = @"
            WITH base_lines AS (
                SELECT 
                    v.id as voucher_id,
                    v.payload->'header'->>'voucherNo' as voucher_no,
                    (v.payload->'header'->>'postingDate')::date as posting_date,
                    v.payload->'header'->>'summary' as summary,
                    line->>'accountCode' as account_code,
                    line->>'lineNo' as line_no,
                    line->>'drcr' as drcr,
                    COALESCE((line->>'amount')::numeric, 0) as amount
                FROM vouchers v
                CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') as line
                JOIN accounts a ON a.company_code = v.company_code 
                               AND a.account_code = line->>'accountCode'
                WHERE v.company_code = $1
                  AND (v.payload->'header'->>'postingDate')::date >= $2
                  AND (v.payload->'header'->>'postingDate')::date <= $3
                  AND a.payload->>'taxType' = ANY($4)
            ),
            tax_lines AS (
                SELECT 
                    v.id as voucher_id,
                    line->>'baseLineNo' as base_line_no,
                    COALESCE((line->>'amount')::numeric, 0) as tax_amount
                FROM vouchers v
                CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') as line
                JOIN accounts a ON a.company_code = v.company_code 
                               AND a.account_code = line->>'accountCode'
                WHERE v.company_code = $1
                  AND (v.payload->'header'->>'postingDate')::date >= $2
                  AND (v.payload->'header'->>'postingDate')::date <= $3
                  AND a.payload->>'taxType' = 'TAX_ACCOUNT'
                  AND line->>'baseLineNo' IS NOT NULL
            )
            SELECT 
                b.voucher_id,
                b.voucher_no,
                b.posting_date,
                b.summary,
                b.account_code,
                a.payload->>'name' as account_name,
                b.drcr,
                b.amount,
                COALESCE(t.tax_amount, 0) as tax_amount
            FROM base_lines b
            JOIN accounts a ON a.company_code = $1 AND a.account_code = b.account_code
            LEFT JOIN tax_lines t ON t.voucher_id = b.voucher_id AND t.base_line_no = b.line_no
            ORDER BY b.posting_date, b.voucher_no";
        
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(fromDate);
        cmd.Parameters.AddWithValue(toDate);
        cmd.Parameters.AddWithValue(targetTaxTypes);
        
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            details.Add(new VoucherLineDetail
            {
                VoucherId = reader.GetGuid(0).ToString(),
                VoucherNo = reader.IsDBNull(1) ? "" : reader.GetString(1),
                PostingDate = reader.GetDateTime(2),
                Summary = reader.IsDBNull(3) ? "" : reader.GetString(3),
                AccountCode = reader.IsDBNull(4) ? "" : reader.GetString(4),
                AccountName = reader.IsDBNull(5) ? "" : reader.GetString(5),
                DrCr = reader.IsDBNull(6) ? "DR" : reader.GetString(6),
                Amount = reader.GetDecimal(7),
                TaxAmount = reader.GetDecimal(8)
            });
        }
        
        return details;
    }
    
    private string[] GetTaxTypesForCategory(string category)
    {
        return category switch
        {
            // 包含新旧两种 taxType 以保持兼容性
            "sales_taxable_10" => new[] { "TAXABLE_10", "OUTPUT_TAX" },
            "sales_taxable_8" => new[] { "TAXABLE_8" },
            "sales_exempt" => new[] { "EXEMPT" },
            "sales_export" => new[] { "EXPORT" },
            "purchase_taxable_10" => new[] { "INPUT_10", "INPUT_TAX" },
            "purchase_taxable_8" => new[] { "INPUT_8" },
            _ => Array.Empty<string>()
        };
    }
    
    /// <summary>
    /// 申告書データを保存
    /// </summary>
    public async Task<Guid> SaveReturnAsync(
        string companyCode,
        string fiscalYear,
        string periodType,
        string taxationMethod,
        ConsumptionTaxCalculation calculation,
        string? createdById = null,
        string? createdByName = null)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        
        var calcJson = JsonSerializer.Serialize(calculation, new JsonSerializerOptions 
        { 
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
        });
        
        cmd.CommandText = @"
            INSERT INTO consumption_tax_returns 
                (company_code, fiscal_year, period_type, taxation_method, status, calculation, created_by_name)
            VALUES ($1, $2, $3, $4, 'calculated', $5::jsonb, $6)
            ON CONFLICT (company_code, fiscal_year, period_type) DO UPDATE SET
                taxation_method = EXCLUDED.taxation_method,
                calculation = EXCLUDED.calculation,
                status = 'calculated',
                updated_at = now()
            RETURNING id";
        
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(fiscalYear);
        cmd.Parameters.AddWithValue(periodType);
        cmd.Parameters.AddWithValue(taxationMethod);
        cmd.Parameters.AddWithValue(calcJson);
        cmd.Parameters.AddWithValue(createdByName ?? (object)DBNull.Value);
        
        var id = (Guid?)await cmd.ExecuteScalarAsync();
        return id ?? Guid.Empty;
    }
    
    /// <summary>
    /// 申告書一覧を取得
    /// </summary>
    public async Task<List<JsonObject>> GetReturnsAsync(string companyCode, string? fiscalYear = null)
    {
        var returns = new List<JsonObject>();
        
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        
        cmd.CommandText = @"
            SELECT id, fiscal_year, period_type, status, taxation_method, 
                   calculation, created_at, created_by_name, submitted_at
            FROM consumption_tax_returns
            WHERE company_code = $1" +
            (string.IsNullOrEmpty(fiscalYear) ? "" : " AND fiscal_year = $2") +
            " ORDER BY fiscal_year DESC, period_type";
        
        cmd.Parameters.AddWithValue(companyCode);
        if (!string.IsNullOrEmpty(fiscalYear))
            cmd.Parameters.AddWithValue(fiscalYear);
        
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var obj = new JsonObject
            {
                ["id"] = reader.GetGuid(0).ToString(),
                ["fiscalYear"] = reader.GetString(1),
                ["periodType"] = reader.GetString(2),
                ["status"] = reader.GetString(3),
                ["taxationMethod"] = reader.GetString(4),
                ["createdAt"] = reader.GetDateTime(6).ToString("yyyy-MM-dd HH:mm"),
            };
            
            if (!reader.IsDBNull(5))
            {
                var calcJson = reader.GetString(5);
                obj["calculation"] = JsonNode.Parse(calcJson);
            }
            if (!reader.IsDBNull(7))
                obj["createdByName"] = reader.GetString(7);
            if (!reader.IsDBNull(8))
                obj["submittedAt"] = reader.GetDateTime(8).ToString("yyyy-MM-dd HH:mm");
            
            returns.Add(obj);
        }
        
        return returns;
    }
    
    /// <summary>
    /// 申告書を取得
    /// </summary>
    public async Task<JsonObject?> GetReturnAsync(string companyCode, Guid id)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        
        cmd.CommandText = @"
            SELECT id, fiscal_year, period_type, status, taxation_method, 
                   calculation, form_data, created_at, created_by_name, submitted_at
            FROM consumption_tax_returns
            WHERE company_code = $1 AND id = $2";
        
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(id);
        
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        
        var obj = new JsonObject
        {
            ["id"] = reader.GetGuid(0).ToString(),
            ["fiscalYear"] = reader.GetString(1),
            ["periodType"] = reader.GetString(2),
            ["status"] = reader.GetString(3),
            ["taxationMethod"] = reader.GetString(4),
            ["createdAt"] = reader.GetDateTime(7).ToString("yyyy-MM-dd HH:mm"),
        };
        
        if (!reader.IsDBNull(5))
            obj["calculation"] = JsonNode.Parse(reader.GetString(5));
        if (!reader.IsDBNull(6))
            obj["formData"] = JsonNode.Parse(reader.GetString(6));
        if (!reader.IsDBNull(8))
            obj["createdByName"] = reader.GetString(8);
        if (!reader.IsDBNull(9))
            obj["submittedAt"] = reader.GetDateTime(9).ToString("yyyy-MM-dd HH:mm");
        
        return obj;
    }
    
    /// <summary>
    /// 申告書を削除
    /// </summary>
    public async Task<bool> DeleteReturnAsync(string companyCode, Guid id)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        
        cmd.CommandText = "DELETE FROM consumption_tax_returns WHERE company_code = $1 AND id = $2 AND status != 'submitted'";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(id);
        
        var rows = await cmd.ExecuteNonQueryAsync();
        return rows > 0;
    }
}

#region Data Models

public class ConsumptionTaxCalculation
{
    public PeriodInfo Period { get; set; } = new();
    public string TaxationMethod { get; set; } = "general";
    public SalesInfo Sales { get; set; } = new();
    public PurchasesInfo Purchases { get; set; } = new();
    public CalculationResult Calculation { get; set; } = new();
    public TaxBreakdown Breakdown { get; set; } = new();
}

public class PeriodInfo
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
}

public class SalesInfo
{
    public TaxableAmount Taxable10 { get; set; } = new();
    public TaxableAmount Taxable8 { get; set; } = new();
    public TaxableAmount Exempt { get; set; } = new();
    public TaxableAmount Export { get; set; } = new();
    public TaxableAmount Total { get; set; } = new();
}

public class PurchasesInfo
{
    public TaxableAmount Taxable10 { get; set; } = new();
    public TaxableAmount Taxable8 { get; set; } = new();
    public TaxableAmount NonDeductible { get; set; } = new();
    public TaxableAmount Total { get; set; } = new();
}

public class TaxableAmount
{
    public decimal NetAmount { get; set; }
    public decimal TaxAmount { get; set; }
}

public class CalculationResult
{
    public decimal SalesTaxTotal { get; set; }
    public decimal PurchaseTaxDeductible { get; set; }
    public decimal NetConsumptionTax { get; set; }
    public decimal LocalConsumptionTax { get; set; }
    public decimal TotalTaxDue { get; set; }
    public decimal InterimPaid { get; set; }
    public decimal FinalPayment { get; set; }
    public int? SimplifiedCategory { get; set; }
    public decimal? DeemedPurchaseRate { get; set; }
}

public class TaxBreakdown
{
    public RateBreakdown Standard10 { get; set; } = new();
    public RateBreakdown Reduced8 { get; set; } = new();
}

public class RateBreakdown
{
    public decimal SalesNetAmount { get; set; }
    public decimal SalesTaxAmount { get; set; }
    public decimal TaxBase { get; set; }
    public decimal ConsumptionTax78 { get; set; }
    public decimal ConsumptionTax624 { get; set; }
    public decimal LocalTax22 { get; set; }
    public decimal LocalTax176 { get; set; }
}

public class VoucherLineDetail
{
    public string VoucherId { get; set; } = "";
    public string VoucherNo { get; set; } = "";
    public DateTime PostingDate { get; set; }
    public string Summary { get; set; } = "";
    public string AccountCode { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string DrCr { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal TaxAmount { get; set; }
}

#endregion

