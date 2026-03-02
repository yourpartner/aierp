using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Server.Modules;

/// <summary>
/// 科目选择服务 - 用于自动选择应收账款、销售收入等科目
/// 支持按业务规则查找科目，并记住客户的偏好选择
/// </summary>
public class AccountSelectionService
{
    private readonly NpgsqlDataSource _ds;

    public AccountSelectionService(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    /// <summary>
    /// 科目查找结果
    /// </summary>
    public record AccountSelectionResult(
        bool Success,
        string? AccountCode,
        string? AccountName,
        string? ErrorMessage,
        bool MultipleOptions,
        List<AccountOption>? Options
    );

    /// <summary>
    /// 可选科目
    /// </summary>
    public record AccountOption(string AccountCode, string AccountName);

    /// <summary>
    /// 获取应收账款科目
    /// 优先级：
    /// 1. 同类业务场景（同客户的销售出库凭证）最近使用的科目
    /// 2. 清账基准为CUSTOMER的BS科目
    /// </summary>
    public async Task<AccountSelectionResult> GetArAccountAsync(
        string companyCode,
        string? customerCode,
        CancellationToken ct = default)
    {
        // 1. 先从历史凭证中查找同类业务场景最近使用的科目
        if (!string.IsNullOrEmpty(customerCode))
        {
            var recentAccount = await GetRecentVoucherAccountAsync(
                companyCode, 
                customerCode, 
                "openItemBaseline = 'CUSTOMER'", // 清账基准为客户的科目
                ct
            );
            if (!string.IsNullOrEmpty(recentAccount))
            {
                var name = await GetAccountNameAsync(companyCode, recentAccount, ct);
                return new AccountSelectionResult(true, recentAccount, name, null, false, null);
            }
        }

        // 2. 查找符合条件的科目（清账基准为CUSTOMER的BS科目）
        var accounts = await FindAccountsAsync(
            companyCode,
            "openItem = true AND openItemBaseline = 'CUSTOMER'",
            ct
        );

        return ProcessAccountResult(accounts, "売掛金科目が見つかりません。openItem=true かつ openItemBaseline='CUSTOMER' の科目を設定してください。");
    }

    /// <summary>
    /// 获取销售收入科目
    /// 优先级：
    /// 1. 同类业务场景（同客户的销售出库凭证）最近使用的科目
    /// 2. PL科目中消费税区分为销项税的科目
    /// </summary>
    public async Task<AccountSelectionResult> GetRevenueAccountAsync(
        string companyCode,
        string? customerCode,
        CancellationToken ct = default)
    {
        // 1. 先从历史凭证中查找同类业务场景最近使用的科目
        if (!string.IsNullOrEmpty(customerCode))
        {
            var recentAccount = await GetRecentVoucherAccountAsync(
                companyCode, 
                customerCode, 
                "category = 'PL' AND taxType = 'OUTPUT_TAX'", // PL科目且销项税
                ct
            );
            if (!string.IsNullOrEmpty(recentAccount))
            {
                var name = await GetAccountNameAsync(companyCode, recentAccount, ct);
                return new AccountSelectionResult(true, recentAccount, name, null, false, null);
            }
        }

        // 2. 查找符合条件的科目（PL科目中消费税区分为销项税）
        var accounts = await FindAccountsAsync(
            companyCode,
            "category = 'PL' AND taxType = 'OUTPUT_TAX'",
            ct
        );

        return ProcessAccountResult(accounts, "売上科目が見つかりません。category='PL' かつ taxType='OUTPUT_TAX' の科目を設定してください。");
    }

    /// <summary>
    /// 获取销项税科目
    /// 查找条件: taxType = 'TAX_ACCOUNT' 且通常是仮受消費税
    /// </summary>
    public async Task<AccountSelectionResult> GetOutputTaxAccountAsync(
        string companyCode,
        CancellationToken ct = default)
    {
        var accounts = await FindOutputTaxAccountsAsync(companyCode, ct);

        // 如果没有找到，尝试更宽松的查找
        if (accounts.Count == 0)
        {
            accounts = await FindAccountsAsync(
                companyCode,
                "taxType = 'TAX_ACCOUNT'",
                ct
            );
        }

        return ProcessAccountResult(accounts, "仮受消費税科目が見つかりません。taxType='TAX_ACCOUNT' の科目を設定してください。");
    }

    /// <summary>
    /// 从历史凭证中查找同客户最近使用的符合条件的科目
    /// </summary>
    private async Task<string?> GetRecentVoucherAccountAsync(
        string companyCode,
        string customerCode,
        string accountCondition,
        CancellationToken ct)
    {
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            
            // 构建科目条件的SQL
            var accountWhere = "";
            if (accountCondition.Contains("openItemBaseline = 'CUSTOMER'"))
            {
                accountWhere = "AND a.payload->>'openItemBaseline' = 'CUSTOMER'";
            }
            else if (accountCondition.Contains("category = 'PL' AND taxType = 'OUTPUT_TAX'"))
            {
                accountWhere = "AND a.payload->>'category' = 'PL' AND a.payload->>'taxType' = 'OUTPUT_TAX'";
            }
            
            // 查找该客户最近的销售相关凭证中使用的科目
            // 使用 vouchers 表，从 payload->'lines' 中提取明细
            cmd.CommandText = $@"
                WITH voucher_lines AS (
                    SELECT 
                        v.id as voucher_id,
                        v.company_code,
                        v.updated_at,
                        line->>'accountCode' as account_code,
                        line->>'customerId' as customer_id
                    FROM vouchers v,
                         jsonb_array_elements(v.payload->'lines') as line
                    WHERE v.company_code = $1
                )
                SELECT vl.account_code
                FROM voucher_lines vl
                JOIN accounts a ON a.company_code = vl.company_code AND a.account_code = vl.account_code
                WHERE vl.company_code = $1
                  AND vl.customer_id = $2
                  {accountWhere}
                ORDER BY vl.updated_at DESC
                LIMIT 1";
            
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(customerCode);
            
            var result = await cmd.ExecuteScalarAsync(ct);
            return result as string;
        }
        catch (Exception ex)
        {
            // 如果查询失败（例如没有历史凭证），返回null让系统使用默认科目
            Console.WriteLine($"[AccountSelection] GetRecentVoucherAccountAsync failed: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> GetAccountNameAsync(
        string companyCode,
        string accountCode,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT payload->>'name' 
            FROM accounts 
            WHERE company_code = $1 AND account_code = $2
            LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(accountCode);
        
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    private async Task<List<AccountOption>> FindAccountsAsync(
        string companyCode,
        string condition,
        CancellationToken ct)
    {
        var accounts = new List<AccountOption>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        // 构建动态SQL - 根据条件查找科目
        // 注意: payload中的字段名可能是驼峰式
        var sql = @"
            SELECT account_code, payload->>'name' as name
            FROM accounts 
            WHERE company_code = $1";

        // 解析条件并转换为SQL
        if (condition.Contains("openItem = true"))
        {
            sql += " AND (payload->>'openItem')::boolean = true";
        }
        if (condition.Contains("openItemBaseline = 'CUSTOMER'"))
        {
            sql += " AND payload->>'openItemBaseline' = 'CUSTOMER'";
        }
        if (condition.Contains("category = 'PL'"))
        {
            sql += " AND payload->>'category' = 'PL'";
        }
        if (condition.Contains("taxType = 'OUTPUT_TAX'"))
        {
            sql += " AND payload->>'taxType' = 'OUTPUT_TAX'";
        }
        if (condition.Contains("taxType = 'TAX_ACCOUNT'"))
        {
            sql += " AND payload->>'taxType' = 'TAX_ACCOUNT'";
        }
        if (condition.Contains("name LIKE"))
        {
            sql += " AND (payload->>'name' LIKE '%仮受%' OR payload->>'name' LIKE '%消費税%')";
        }
        if (condition.Contains("code LIKE"))
        {
            sql += " AND (account_code LIKE '2191%' OR account_code LIKE '2150%')";
        }

        sql += " ORDER BY account_code LIMIT 100";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(companyCode);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var code = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
            if (!string.IsNullOrEmpty(code))
            {
                accounts.Add(new AccountOption(code, name));
            }
        }

        return accounts;
    }

    private async Task<List<AccountOption>> FindOutputTaxAccountsAsync(
        string companyCode,
        CancellationToken ct)
    {
        var accounts = new List<AccountOption>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            SELECT account_code, payload->>'name' as name
            FROM accounts 
            WHERE company_code = $1
              AND payload->>'taxType' = 'TAX_ACCOUNT'
              AND (
                payload->>'name' LIKE '%仮受%'
                OR payload->>'name' LIKE '%消費税%'
                OR account_code LIKE '2191%'
                OR account_code LIKE '2150%'
              )
            ORDER BY
              CASE
                WHEN payload->>'name' LIKE '%仮受消費税%' THEN 0
                WHEN payload->>'name' LIKE '%仮受%' AND payload->>'name' LIKE '%消費税%' THEN 1
                WHEN payload->>'name' LIKE '%仮受%' THEN 2
                WHEN account_code LIKE '2191%' OR account_code LIKE '2150%' THEN 3
                ELSE 4
              END,
              account_code
            LIMIT 100";
        cmd.Parameters.AddWithValue(companyCode);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var code = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
            if (!string.IsNullOrEmpty(code))
            {
                accounts.Add(new AccountOption(code, name));
            }
        }

        return accounts;
    }

    private static AccountSelectionResult ProcessAccountResult(List<AccountOption> accounts, string noAccountMessage)
    {
        if (accounts.Count == 0)
        {
            return new AccountSelectionResult(false, null, null, noAccountMessage, false, null);
        }
        if (accounts.Count == 1)
        {
            return new AccountSelectionResult(true, accounts[0].AccountCode, accounts[0].AccountName, null, false, null);
        }
        // 多个选项时，默认使用第一个，但标记有多个选项
        return new AccountSelectionResult(
            true, 
            accounts[0].AccountCode, 
            accounts[0].AccountName, 
            null, 
            true, 
            accounts
        );
    }

    // ============================================
    // 供应商请求书相关的科目选择
    // ============================================

    /// <summary>
    /// 获取库存科目（借方）- 用于供应商请求书
    /// 筛选条件: BS科目 + 非清账管理 + 消费税区分非课税或对象外
    /// 匹配优先级: 品目 → 种别 → 候选列表
    /// </summary>
    public async Task<AccountSelectionResult> GetInventoryAccountAsync(
        string companyCode,
        string? materialCode,
        string? materialCategory,
        CancellationToken ct = default)
    {
        // 1. 从历史记录中查找同一品目使用的科目
        if (!string.IsNullOrEmpty(materialCode))
        {
            var historyAccount = await GetAccountFromUsageHistoryAsync(companyCode, "dr_material", materialCode, ct);
            if (!string.IsNullOrEmpty(historyAccount))
            {
                var name = await GetAccountNameAsync(companyCode, historyAccount, ct);
                return new AccountSelectionResult(true, historyAccount, name, null, false, null);
            }
        }

        // 2. 从历史记录中查找同一种别使用的科目
        if (!string.IsNullOrEmpty(materialCategory))
        {
            var historyAccount = await GetAccountFromUsageHistoryAsync(companyCode, "dr_category", materialCategory, ct);
            if (!string.IsNullOrEmpty(historyAccount))
            {
                var name = await GetAccountNameAsync(companyCode, historyAccount, ct);
                return new AccountSelectionResult(true, historyAccount, name, null, false, null);
            }
        }

        // 3. 查找符合条件的科目（BS资产类 + 非清账管理 + 非课税/对象外）
        var accounts = await FindInventoryAccountsAsync(companyCode, ct);
        return ProcessAccountResult(accounts, "在庫科目が見つかりません。BS科目（資産）で、清算管理なし、消費税区分が非課税または対象外の科目を設定してください。");
    }

    /// <summary>
    /// 获取应付账款科目（贷方）- 用于供应商请求书
    /// 筛选条件: BS科目 + 清账基准为供应商 + 消费税区分非课税或对象外
    /// 匹配优先级: 供应商历史 → 候选列表
    /// </summary>
    public async Task<AccountSelectionResult> GetApAccountAsync(
        string companyCode,
        string? vendorCode,
        CancellationToken ct = default)
    {
        // 1. 从历史记录中查找同一供应商使用的科目
        if (!string.IsNullOrEmpty(vendorCode))
        {
            var historyAccount = await GetAccountFromUsageHistoryAsync(companyCode, "cr_vendor", vendorCode, ct);
            if (!string.IsNullOrEmpty(historyAccount))
            {
                var name = await GetAccountNameAsync(companyCode, historyAccount, ct);
                return new AccountSelectionResult(true, historyAccount, name, null, false, null);
            }
        }

        // 2. 查找符合条件的科目（BS负债类 + 清账基准为供应商 + 非课税/对象外）
        var accounts = await FindApAccountsAsync(companyCode, ct);
        return ProcessAccountResult(accounts, "買掛金科目が見つかりません。BS科目（負債）で、清算基準が「仕入先」、消費税区分が非課税または対象外の科目を設定してください。");
    }

    /// <summary>
    /// 获取进项税科目 - 用于供应商请求书
    /// </summary>
    public async Task<AccountSelectionResult> GetInputTaxAccountAsync(
        string companyCode,
        CancellationToken ct = default)
    {
        var accounts = await FindInputTaxAccountsAsync(companyCode, ct);

        return ProcessAccountResult(accounts, "仮払消費税科目が見つかりません。taxType='INPUT_TAX' の科目を設定してください。");
    }

    private async Task<List<AccountOption>> FindInputTaxAccountsAsync(
        string companyCode,
        CancellationToken ct)
    {
        var accounts = new List<AccountOption>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            SELECT account_code, payload->>'name' as name
            FROM accounts 
            WHERE company_code = $1
              AND (
                payload->>'taxType' = 'INPUT_TAX'
                OR (payload->>'name' LIKE '%仮払%' AND payload->>'name' LIKE '%消費税%')
                OR payload->>'name' LIKE '%仮払消費税%'
              )
            ORDER BY
              CASE
                WHEN payload->>'taxType' = 'INPUT_TAX' THEN 0
                WHEN payload->>'name' LIKE '%仮払消費税%' THEN 1
                WHEN payload->>'name' LIKE '%仮払%' AND payload->>'name' LIKE '%消費税%' THEN 2
                ELSE 3
              END,
              account_code
            LIMIT 100";
        cmd.Parameters.AddWithValue(companyCode);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var code = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
            if (!string.IsNullOrEmpty(code))
            {
                accounts.Add(new AccountOption(code, name));
            }
        }

        return accounts;
    }

    /// <summary>
    /// 获取默认税率 - 用于供应商请求书
    /// 匹配优先级: 品目 → 种别 → 供应商 → 默认10%
    /// </summary>
    public async Task<decimal> GetTaxRateAsync(
        string companyCode,
        string? materialCode,
        string? materialCategory,
        string? vendorCode,
        CancellationToken ct = default)
    {
        // 1. 从品目历史获取
        if (!string.IsNullOrEmpty(materialCode))
        {
            var rate = await GetTaxRateFromHistoryAsync(companyCode, "tax_material", materialCode, ct);
            if (rate.HasValue) return rate.Value;
        }

        // 2. 从种别历史获取
        if (!string.IsNullOrEmpty(materialCategory))
        {
            var rate = await GetTaxRateFromHistoryAsync(companyCode, "tax_category", materialCategory, ct);
            if (rate.HasValue) return rate.Value;
        }

        // 3. 从供应商历史获取
        if (!string.IsNullOrEmpty(vendorCode))
        {
            var rate = await GetTaxRateFromHistoryAsync(companyCode, "tax_vendor", vendorCode, ct);
            if (rate.HasValue) return rate.Value;
        }

        // 4. 默认10%
        return 10m;
    }

    /// <summary>
    /// 记录科目使用历史
    /// </summary>
    public async Task RecordAccountUsageAsync(
        string companyCode,
        string usageType,
        string contextValue,
        string accountCode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(contextValue) || string.IsNullOrEmpty(accountCode)) return;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO account_usage_history (company_code, usage_type, context_value, account_code, usage_count, last_used_at)
            VALUES ($1, $2, $3, $4, 1, now())
            ON CONFLICT (company_code, usage_type, context_value, account_code)
            DO UPDATE SET usage_count = account_usage_history.usage_count + 1, last_used_at = now()";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(usageType);
        cmd.Parameters.AddWithValue(contextValue);
        cmd.Parameters.AddWithValue(accountCode);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 记录税率使用历史
    /// </summary>
    public async Task RecordTaxRateUsageAsync(
        string companyCode,
        string usageType,
        string contextValue,
        decimal taxRate,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(contextValue)) return;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO account_usage_history (company_code, usage_type, context_value, account_code, usage_count, last_used_at)
            VALUES ($1, $2, $3, $4, 1, now())
            ON CONFLICT (company_code, usage_type, context_value, account_code)
            DO UPDATE SET usage_count = account_usage_history.usage_count + 1, last_used_at = now()";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(usageType);
        cmd.Parameters.AddWithValue(contextValue);
        cmd.Parameters.AddWithValue(taxRate.ToString("F2")); // 存储为字符串
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // 从使用历史中获取科目
    private async Task<string?> GetAccountFromUsageHistoryAsync(
        string companyCode,
        string usageType,
        string contextValue,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT account_code 
            FROM account_usage_history 
            WHERE company_code = $1 AND usage_type = $2 AND context_value = $3
            ORDER BY usage_count DESC, last_used_at DESC
            LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(usageType);
        cmd.Parameters.AddWithValue(contextValue);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    // 从使用历史中获取税率
    private async Task<decimal?> GetTaxRateFromHistoryAsync(
        string companyCode,
        string usageType,
        string contextValue,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT account_code 
            FROM account_usage_history 
            WHERE company_code = $1 AND usage_type = $2 AND context_value = $3
            ORDER BY usage_count DESC, last_used_at DESC
            LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(usageType);
        cmd.Parameters.AddWithValue(contextValue);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is string str && decimal.TryParse(str, out var rate))
        {
            return rate;
        }
        return null;
    }

    // 查找库存科目（BS资产类 + 非清账管理 + 非课税/对象外）
    private async Task<List<AccountOption>> FindInventoryAccountsAsync(string companyCode, CancellationToken ct)
    {
        var accounts = new List<AccountOption>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT account_code, payload->>'name' as name
            FROM accounts 
            WHERE company_code = $1
              AND payload->>'category' = 'BS'
              AND payload->>'accountType' = 'asset'
              AND (payload->>'openItem' IS NULL OR (payload->>'openItem')::boolean = false)
              AND (payload->>'openItemBaseline' IS NULL OR payload->>'openItemBaseline' = '')
              AND (payload->>'taxType' IN ('NON_TAX', 'NON_TAXABLE', 'OUT_OF_SCOPE', 'EXEMPT') 
                   OR payload->>'taxType' IS NULL 
                   OR payload->>'taxType' = '')
            ORDER BY account_code
            LIMIT 100";
        cmd.Parameters.AddWithValue(companyCode);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var code = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
            if (!string.IsNullOrEmpty(code))
            {
                accounts.Add(new AccountOption(code, name));
            }
        }
        return accounts;
    }

    // 查找应付账款科目（BS负债类 + 清账基准为供应商 + 非课税/对象外）
    private async Task<List<AccountOption>> FindApAccountsAsync(string companyCode, CancellationToken ct)
    {
        var accounts = new List<AccountOption>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT account_code, payload->>'name' as name
            FROM accounts 
            WHERE company_code = $1
              AND payload->>'category' = 'BS'
              AND payload->>'accountType' = 'liability'
              AND (payload->>'openItem')::boolean = true
              AND payload->>'openItemBaseline' = 'VENDOR'
              AND (payload->>'taxType' IN ('NON_TAX', 'NON_TAXABLE', 'OUT_OF_SCOPE', 'EXEMPT') 
                   OR payload->>'taxType' IS NULL 
                   OR payload->>'taxType' = '')
            ORDER BY account_code
            LIMIT 100";
        cmd.Parameters.AddWithValue(companyCode);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var code = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
            if (!string.IsNullOrEmpty(code))
            {
                accounts.Add(new AccountOption(code, name));
            }
        }
        return accounts;
    }

    // 按科目代码模式查找
    private async Task<List<AccountOption>> FindAccountsByCodePatternAsync(
        string companyCode,
        string[] codePatterns,
        CancellationToken ct)
    {
        var accounts = new List<AccountOption>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        var patterns = string.Join(" OR ", codePatterns.Select((_, i) => $"account_code LIKE ${i + 2}"));
        cmd.CommandText = $@"
            SELECT account_code, payload->>'name' as name
            FROM accounts 
            WHERE company_code = $1 AND ({patterns})
            ORDER BY account_code
            LIMIT 100";
        cmd.Parameters.AddWithValue(companyCode);
        foreach (var pattern in codePatterns)
        {
            cmd.Parameters.AddWithValue(pattern + "%");
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var code = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
            if (!string.IsNullOrEmpty(code))
            {
                accounts.Add(new AccountOption(code, name));
            }
        }
        return accounts;
    }

    // ============================================
    // 销售相关的凭证创建
    // ============================================

    /// <summary>
    /// 创建销售出库凭证
    /// DR: 売掛金（应收账款）
    /// CR: 売上（销售收入）
    /// CR: 仮受消費税（销项税）
    /// </summary>
    public async Task<(Guid VoucherId, string VoucherNo)?> CreateSalesVoucherAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string companyCode,
        string customerCode,
        string customerName,
        string deliveryNo,
        DateOnly deliveryDate,
        decimal totalAmount,
        decimal taxAmount,
        string currentUser,
        CancellationToken ct = default)
    {
        // 1. 获取科目
        var arResult = await GetArAccountAsync(companyCode, customerCode, ct);
        if (!arResult.Success)
        {
            throw new Exception(arResult.ErrorMessage ?? "売掛金科目が見つかりません");
        }

        var revenueResult = await GetRevenueAccountAsync(companyCode, customerCode, ct);
        if (!revenueResult.Success)
        {
            throw new Exception(revenueResult.ErrorMessage ?? "売上科目が見つかりません");
        }

        var taxResult = await GetOutputTaxAccountAsync(companyCode, ct);
        if (!taxResult.Success)
        {
            throw new Exception(taxResult.ErrorMessage ?? "仮受消費税科目が見つかりません");
        }

        var arAccountCode = arResult.AccountCode!;
        var revenueAccountCode = revenueResult.AccountCode!;
        var taxAccountCode = taxResult.AccountCode!;

        // 2. 获取凭证编号
        var numbering = await Server.Infrastructure.VoucherNumberingService.NextAsync(_ds, companyCode, deliveryDate.ToDateTime(TimeOnly.MinValue));
        var voucherNo = numbering.voucherNo;

        // 3. 构建凭证明细
        // 注意：消费税明细行需要设置 baseLineNo 指向对应的税基明细（销售收入）
        var netAmount = totalAmount - taxAmount;
        var lines = new List<object>
        {
            new { lineNo = 1, accountCode = arAccountCode, drcr = "DR", amount = totalAmount, customerId = customerCode },
        };
        
        int revenueLineNo = 0;
        int lineNo = 2;
        if (netAmount > 0)
        {
            revenueLineNo = lineNo;
            lines.Add(new { lineNo = lineNo++, accountCode = revenueAccountCode, drcr = "CR", amount = netAmount });
        }
        if (taxAmount > 0 && revenueLineNo > 0)
        {
            // 设置 baseLineNo 指向销售收入明细，建立消费税与税基的关联
            // taxRate/taxType を追加することで MonthlyClosingService.CalculateTaxSummaryAsync が正しく集計できる
            lines.Add(new { lineNo = lineNo++, accountCode = taxAccountCode, drcr = "CR", amount = taxAmount, baseLineNo = revenueLineNo, isTaxLine = true, taxRate = 10, taxType = "OUTPUT_TAX" });
        }
        else if (taxAmount > 0)
        {
            lines.Add(new { lineNo = lineNo++, accountCode = taxAccountCode, drcr = "CR", amount = taxAmount, isTaxLine = true, taxRate = 10, taxType = "OUTPUT_TAX" });
        }

        // 4. 构建凭证
        var summary = $"売上 | {customerName} | {deliveryNo}";
        var voucherPayload = new JsonObject
        {
            ["header"] = new JsonObject
            {
                ["companyCode"] = companyCode,
                ["voucherNo"] = voucherNo,
                ["postingDate"] = deliveryDate.ToString("yyyy-MM-dd"),
                ["voucherType"] = "SA", // Sales
                ["currency"] = "JPY",
                ["summary"] = summary,
                ["createdBy"] = currentUser,
                ["createdAt"] = DateTime.UtcNow.ToString("o"),
                ["source"] = "delivery_shipment",
                ["sourceRef"] = deliveryNo
            },
            ["lines"] = JsonNode.Parse(JsonSerializer.Serialize(lines)) ?? new JsonArray()
        };

        // 5. 插入凭证
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO vouchers(company_code, payload)
            VALUES ($1, $2::jsonb)
            RETURNING id", conn, tx);
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(voucherPayload.ToJsonString());

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is Guid voucherId)
        {
            // 6. 为売掛金明细行创建open_items记录（用于银行入金配分消込）
            await using var insCmd = new NpgsqlCommand(@"
                INSERT INTO open_items(company_code, voucher_id, voucher_line_no, account_code, partner_id, currency, doc_date, original_amount, residual_amount, refs, payment_date)
                VALUES ($1, $2, $3, $4, $5, $6, $7::date, $8, $8, $9::jsonb, $10::date)", conn, tx);
            insCmd.Parameters.AddWithValue(companyCode);
            insCmd.Parameters.AddWithValue(voucherId);
            insCmd.Parameters.AddWithValue(1); // lineNo = 1 for 売掛金
            insCmd.Parameters.AddWithValue(arAccountCode);
            insCmd.Parameters.AddWithValue(customerCode);
            insCmd.Parameters.AddWithValue("JPY");
            insCmd.Parameters.AddWithValue(deliveryDate.ToString("yyyy-MM-dd"));
            insCmd.Parameters.AddWithValue(totalAmount);
            insCmd.Parameters.AddWithValue(new JsonObject { ["invoiceNo"] = deliveryNo }.ToJsonString());
            insCmd.Parameters.AddWithValue(deliveryDate.ToString("yyyy-MM-dd")); // payment_date = delivery_date for 売掛金
            await insCmd.ExecuteNonQueryAsync(ct);

            return (voucherId, voucherNo);
        }

        return null;
    }
}

