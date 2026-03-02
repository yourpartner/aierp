using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Server.Modules;

/// <summary>
/// 現金・小口現金管理サービス
/// </summary>
public class CashManagementService
{
    private readonly NpgsqlDataSource _ds;
    
    public CashManagementService(NpgsqlDataSource ds)
    {
        _ds = ds;
    }
    
    #region 現金口座
    
    /// <summary>
    /// 現金口座一覧を取得
    /// </summary>
    public async Task<List<JsonObject>> GetCashAccountsAsync(string companyCode, string? cashType = null)
    {
        var accounts = new List<JsonObject>();
        
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        
        cmd.CommandText = @"
            SELECT id, payload, created_at, updated_at
            FROM cash_accounts 
            WHERE company_code = $1" +
            (string.IsNullOrEmpty(cashType) ? "" : " AND cash_type = $2") +
            " ORDER BY cash_code";
        
        cmd.Parameters.AddWithValue(companyCode);
        if (!string.IsNullOrEmpty(cashType))
            cmd.Parameters.AddWithValue(cashType);
        
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var payload = JsonNode.Parse(reader.GetString(1)) as JsonObject ?? new JsonObject();
            payload["id"] = reader.GetGuid(0).ToString();
            payload["createdAt"] = reader.GetDateTime(2).ToString("yyyy-MM-dd HH:mm");
            payload["updatedAt"] = reader.GetDateTime(3).ToString("yyyy-MM-dd HH:mm");
            accounts.Add(payload);
        }
        
        return accounts;
    }
    
    /// <summary>
    /// 現金口座を取得
    /// </summary>
    public async Task<JsonObject?> GetCashAccountAsync(string companyCode, string cashCode)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        
        cmd.CommandText = @"
            SELECT id, payload, created_at, updated_at
            FROM cash_accounts 
            WHERE company_code = $1 AND cash_code = $2";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(cashCode);
        
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        
        var payload = JsonNode.Parse(reader.GetString(1)) as JsonObject ?? new JsonObject();
        payload["id"] = reader.GetGuid(0).ToString();
        payload["createdAt"] = reader.GetDateTime(2).ToString("yyyy-MM-dd HH:mm");
        payload["updatedAt"] = reader.GetDateTime(3).ToString("yyyy-MM-dd HH:mm");
        return payload;
    }
    
    /// <summary>
    /// 勘定科目から現金/銀行科目の取引履歴を取得（仕訳ベース）
    /// </summary>
    public async Task<(List<JsonObject> Transactions, decimal OpeningBalance)> GetVoucherBasedTransactionsAsync(
        string companyCode, 
        string accountCode, 
        DateTime fromDate, 
        DateTime toDate)
    {
        var transactions = new List<JsonObject>();
        decimal openingBalance = 0;
        
        await using var conn = await _ds.OpenConnectionAsync();
        
        // 期首残高を計算（fromDate以前の借方合計 - 貸方合計）
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT 
                    COALESCE(SUM(CASE WHEN line->>'drcr' = 'DR' THEN (line->>'amount')::numeric ELSE 0 END), 0) -
                    COALESCE(SUM(CASE WHEN line->>'drcr' = 'CR' THEN (line->>'amount')::numeric ELSE 0 END), 0) as balance
                FROM vouchers v,
                     jsonb_array_elements(v.payload->'lines') AS line
                WHERE v.company_code = $1 
                  AND line->>'accountCode' = $2
                  AND (v.payload->'header'->>'postingDate')::date < $3";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(accountCode);
            cmd.Parameters.AddWithValue(fromDate);
            
            var result = await cmd.ExecuteScalarAsync();
            openingBalance = result is decimal dec ? dec : 0;
        }
        
        // 期間内の取引を取得
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT 
                    v.id,
                    v.payload->'header'->>'voucherNo' as voucher_no,
                    (v.payload->'header'->>'postingDate')::date as posting_date,
                    v.payload->'header'->>'summary' as summary,
                    line->>'drcr' as drcr,
                    (line->>'amount')::numeric as amount,
                    line->>'memo' as memo,
                    (SELECT string_agg(ol->>'accountCode', ',') 
                     FROM jsonb_array_elements(v.payload->'lines') ol 
                     WHERE ol->>'accountCode' != $2) as counterpart_accounts
                FROM vouchers v,
                     jsonb_array_elements(v.payload->'lines') AS line
                WHERE v.company_code = $1 
                  AND line->>'accountCode' = $2
                  AND (v.payload->'header'->>'postingDate')::date >= $3 
                  AND (v.payload->'header'->>'postingDate')::date <= $4
                ORDER BY (v.payload->'header'->>'postingDate')::date, v.payload->'header'->>'voucherNo'";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(accountCode);
            cmd.Parameters.AddWithValue(fromDate);
            cmd.Parameters.AddWithValue(toDate);
            
            decimal runningBalance = openingBalance;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var voucherId = reader.GetGuid(0).ToString();
                var voucherNo = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var postingDate = reader.GetDateTime(2);
                var summary = reader.IsDBNull(3) ? "" : reader.GetString(3);
                var drcr = reader.GetString(4);
                var amount = reader.GetDecimal(5);
                var memo = reader.IsDBNull(6) ? "" : reader.GetString(6);
                var counterpartAccounts = reader.IsDBNull(7) ? "" : reader.GetString(7);
                
                // 残高計算（資産科目は借方+、貸方-）
                if (drcr == "DR")
                    runningBalance += amount;
                else
                    runningBalance -= amount;
                
                var tx = new JsonObject
                {
                    ["transactionDate"] = postingDate.ToString("yyyy-MM-dd"),
                    ["transactionNo"] = voucherNo,
                    ["transactionType"] = drcr == "DR" ? "receipt" : "payment",
                    ["amount"] = amount,
                    ["balanceAfter"] = runningBalance,
                    ["description"] = string.IsNullOrEmpty(memo) ? summary : memo,
                    ["counterparty"] = counterpartAccounts,
                    ["voucherId"] = voucherId,
                    ["voucherNo"] = voucherNo
                };
                transactions.Add(tx);
            }
        }
        
        return (transactions, openingBalance);
    }
    
    /// <summary>
    /// 現金口座を作成
    /// </summary>
    public async Task<Guid> CreateCashAccountAsync(string companyCode, JsonObject payload)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        
        // 初期残高を設定
        if (!payload.ContainsKey("currentBalance"))
            payload["currentBalance"] = 0;
        if (!payload.ContainsKey("isActive"))
            payload["isActive"] = true;
        
        cmd.CommandText = @"
            INSERT INTO cash_accounts (company_code, payload)
            VALUES ($1, $2::jsonb)
            RETURNING id";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(payload.ToJsonString());
        
        var id = (Guid?)await cmd.ExecuteScalarAsync();
        return id ?? Guid.Empty;
    }
    
    /// <summary>
    /// 現金口座を更新
    /// </summary>
    public async Task<bool> UpdateCashAccountAsync(string companyCode, string cashCode, JsonObject payload)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        
        cmd.CommandText = @"
            UPDATE cash_accounts 
            SET payload = $3::jsonb, updated_at = now()
            WHERE company_code = $1 AND cash_code = $2";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(cashCode);
        cmd.Parameters.AddWithValue(payload.ToJsonString());
        
        var rows = await cmd.ExecuteNonQueryAsync();
        return rows > 0;
    }
    
    #endregion
    
    #region 現金取引
    
    /// <summary>
    /// 取引番号を採番
    /// </summary>
    private async Task<string> GenerateTransactionNoAsync(NpgsqlConnection conn, string companyCode, string cashCode, DateTime date)
    {
        var datePrefix = date.ToString("yyyyMMdd");
        
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(MAX(CAST(SUBSTRING(transaction_no FROM 10) AS INTEGER)), 0) + 1
            FROM cash_transactions
            WHERE company_code = $1 AND cash_code = $2 AND transaction_no LIKE $3";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(cashCode);
        cmd.Parameters.AddWithValue($"{datePrefix}-%");
        
        var seq = await cmd.ExecuteScalarAsync();
        var seqNo = seq is int i ? i : (seq is long l ? (int)l : 1);
        return $"{datePrefix}-{seqNo:D4}";
    }
    
    /// <summary>
    /// 現金取引を登録（仕訳自動作成オプション付き）
    /// </summary>
    public async Task<(Guid TransactionId, string TransactionNo, Guid? VoucherId, string? VoucherNo)> CreateTransactionAsync(
        string companyCode, 
        JsonObject payload,
        bool createVoucher,
        string? debitAccountCode,
        string? creditAccountCode,
        FinanceService? financeService,
        string? userId,
        string? userName)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        
        try
        {
            var cashCode = payload["cashCode"]?.ToString() ?? "";
            var transactionDateStr = payload["transactionDate"]?.ToString();
            var transactionDate = DateTime.TryParse(transactionDateStr, out var d) ? d : DateTime.Today;
            var transactionType = payload["transactionType"]?.ToString() ?? "payment";
            var amount = payload["amount"]?.GetValue<decimal>() ?? 0;
            
            // 取引番号を採番
            var transactionNo = await GenerateTransactionNoAsync(conn, companyCode, cashCode, transactionDate);
            payload["transactionNo"] = transactionNo;
            
            // 現金口座の残高を取得・更新
            decimal currentBalance = 0;
            await using (var getCmd = conn.CreateCommand())
            {
                getCmd.CommandText = "SELECT COALESCE((payload->>'currentBalance')::numeric, 0) FROM cash_accounts WHERE company_code = $1 AND cash_code = $2 FOR UPDATE";
                getCmd.Parameters.AddWithValue(companyCode);
                getCmd.Parameters.AddWithValue(cashCode);
                var result = await getCmd.ExecuteScalarAsync();
                currentBalance = result is decimal dec ? dec : 0;
            }
            
            // 残高計算
            decimal newBalance;
            if (transactionType == "receipt" || transactionType == "replenish")
                newBalance = currentBalance + amount;
            else if (transactionType == "payment")
                newBalance = currentBalance - amount;
            else // adjustment
                newBalance = currentBalance + amount; // adjustment can be positive or negative
            
            payload["balanceAfter"] = newBalance;
            payload["createdBy"] = userId;
            payload["createdByName"] = userName;
            
            // 取引を登録
            Guid transactionId;
            await using (var insertCmd = conn.CreateCommand())
            {
                insertCmd.CommandText = @"
                    INSERT INTO cash_transactions (company_code, payload)
                    VALUES ($1, $2::jsonb)
                    RETURNING id";
                insertCmd.Parameters.AddWithValue(companyCode);
                insertCmd.Parameters.AddWithValue(payload.ToJsonString());
                transactionId = (Guid)(await insertCmd.ExecuteScalarAsync() ?? Guid.Empty);
            }
            
            // 現金口座の残高を更新
            await using (var updateCmd = conn.CreateCommand())
            {
                updateCmd.CommandText = @"
                    UPDATE cash_accounts 
                    SET payload = jsonb_set(payload, '{currentBalance}', to_jsonb($3::numeric)),
                        updated_at = now()
                    WHERE company_code = $1 AND cash_code = $2";
                updateCmd.Parameters.AddWithValue(companyCode);
                updateCmd.Parameters.AddWithValue(cashCode);
                updateCmd.Parameters.AddWithValue(newBalance);
                await updateCmd.ExecuteNonQueryAsync();
            }
            
            await tx.CommitAsync();
            
            // 仕訳作成（オプション）
            Guid? voucherId = null;
            string? voucherNo = null;
            if (createVoucher && financeService != null && !string.IsNullOrEmpty(debitAccountCode) && !string.IsNullOrEmpty(creditAccountCode))
            {
                (voucherId, voucherNo) = await CreateVoucherForTransactionAsync(
                    companyCode, payload, transactionNo, debitAccountCode, creditAccountCode, financeService, userId, userName);
                
                // 取引に仕訳情報を紐付け
                if (voucherId.HasValue)
                {
                    await using var conn2 = await _ds.OpenConnectionAsync();
                    await using var linkCmd = conn2.CreateCommand();
                    linkCmd.CommandText = @"
                        UPDATE cash_transactions 
                        SET payload = jsonb_set(jsonb_set(payload, '{voucherId}', to_jsonb($3::text)), '{voucherNo}', to_jsonb($4::text))
                        WHERE id = $1 AND company_code = $2";
                    linkCmd.Parameters.AddWithValue(transactionId);
                    linkCmd.Parameters.AddWithValue(companyCode);
                    linkCmd.Parameters.AddWithValue(voucherId.Value.ToString());
                    linkCmd.Parameters.AddWithValue(voucherNo ?? "");
                    await linkCmd.ExecuteNonQueryAsync();
                }
            }
            
            return (transactionId, transactionNo, voucherId, voucherNo);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
    
    /// <summary>
    /// 取引に対する仕訳を作成
    /// </summary>
    private async Task<(Guid?, string?)> CreateVoucherForTransactionAsync(
        string companyCode,
        JsonObject transaction,
        string transactionNo,
        string debitAccountCode,
        string creditAccountCode,
        FinanceService financeService,
        string? userId,
        string? userName)
    {
        var transactionDate = transaction["transactionDate"]?.ToString() ?? DateTime.Today.ToString("yyyy-MM-dd");
        var amount = transaction["amount"]?.GetValue<decimal>() ?? 0;
        var description = transaction["description"]?.ToString() ?? "";
        var counterparty = transaction["counterparty"]?.ToString();
        
        var summary = string.IsNullOrEmpty(counterparty) 
            ? $"現金取引 {transactionNo} {description}"
            : $"現金取引 {transactionNo} {counterparty} {description}";
        
        var voucherPayload = new JsonObject
        {
            ["header"] = new JsonObject
            {
                ["companyCode"] = companyCode,
                ["postingDate"] = transactionDate,
                ["voucherType"] = "OT",
                ["currency"] = "JPY",
                ["summary"] = summary
            },
            ["lines"] = new JsonArray
            {
                new JsonObject
                {
                    ["lineNo"] = 1,
                    ["accountCode"] = debitAccountCode,
                    ["drcr"] = "DR",
                    ["amount"] = amount
                },
                new JsonObject
                {
                    ["lineNo"] = 2,
                    ["accountCode"] = creditAccountCode,
                    ["drcr"] = "CR",
                    ["amount"] = amount
                }
            }
        };
        
        try
        {
            // 仕訳番号を採番して作成
            var postingDate = DateTime.Parse(transactionDate);
            var numbering = await Server.Infrastructure.VoucherNumberingService.NextAsync(_ds, companyCode, postingDate);
            
            await using var conn = await _ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            
            cmd.CommandText = @"
                INSERT INTO vouchers(company_code, payload)
                VALUES ($1, jsonb_set($2::jsonb, '{header,voucherNo}', to_jsonb($3::text)))
                RETURNING id, payload->'header'->>'voucherNo'";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(voucherPayload.ToJsonString());
            cmd.Parameters.AddWithValue(numbering.voucherNo);
            
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var voucherId = reader.GetGuid(0);
                var voucherNo = reader.IsDBNull(1) ? null : reader.GetString(1);
                
                // 刷新总账物化视图
                await FinanceService.RefreshGlViewAsync(conn);
                
                return (voucherId, voucherNo);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CashManagement] Failed to create voucher: {ex.Message}");
        }
        
        return (null, null);
    }
    
    /// <summary>
    /// 現金取引一覧を取得（出納帳）
    /// </summary>
    public async Task<(List<JsonObject> Transactions, decimal OpeningBalance)> GetTransactionsAsync(
        string companyCode, 
        string cashCode, 
        DateTime fromDate, 
        DateTime toDate)
    {
        var transactions = new List<JsonObject>();
        decimal openingBalance = 0;
        
        await using var conn = await _ds.OpenConnectionAsync();
        
        // 期首残高を計算（fromDate以前の最後の残高）
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT COALESCE((payload->>'balanceAfter')::numeric, 0)
                FROM cash_transactions
                WHERE company_code = $1 AND cash_code = $2 AND transaction_date < $3
                ORDER BY transaction_date DESC, transaction_no DESC
                LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(cashCode);
            cmd.Parameters.AddWithValue(fromDate);
            
            var result = await cmd.ExecuteScalarAsync();
            openingBalance = result is decimal dec ? dec : 0;
        }
        
        // 期間内の取引を取得
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT id, payload, created_at
                FROM cash_transactions
                WHERE company_code = $1 AND cash_code = $2 
                  AND transaction_date >= $3 AND transaction_date <= $4
                ORDER BY transaction_date, transaction_no";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(cashCode);
            cmd.Parameters.AddWithValue(fromDate);
            cmd.Parameters.AddWithValue(toDate);
            
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var payload = JsonNode.Parse(reader.GetString(1)) as JsonObject ?? new JsonObject();
                payload["id"] = reader.GetGuid(0).ToString();
                payload["createdAt"] = reader.GetDateTime(2).ToString("yyyy-MM-dd HH:mm");
                transactions.Add(payload);
            }
        }
        
        return (transactions, openingBalance);
    }
    
    #endregion
    
    #region 現金実査
    
    /// <summary>
    /// 現金実査を登録
    /// </summary>
    public async Task<(Guid CountId, Guid? AdjustmentVoucherId, string? AdjustmentVoucherNo)> CreateCashCountAsync(
        string companyCode,
        JsonObject payload,
        bool createAdjustmentVoucher,
        string? adjustmentDebitAccount,
        string? adjustmentCreditAccount,
        FinanceService? financeService,
        string? userId,
        string? userName)
    {
        var cashCode = payload["cashCode"]?.ToString() ?? "";
        var actualBalance = payload["actualBalance"]?.GetValue<decimal>() ?? 0;
        
        await using var conn = await _ds.OpenConnectionAsync();
        
        // 帳簿残高を取得
        decimal bookBalance = 0;
        await using (var getCmd = conn.CreateCommand())
        {
            getCmd.CommandText = "SELECT COALESCE((payload->>'currentBalance')::numeric, 0) FROM cash_accounts WHERE company_code = $1 AND cash_code = $2";
            getCmd.Parameters.AddWithValue(companyCode);
            getCmd.Parameters.AddWithValue(cashCode);
            var result = await getCmd.ExecuteScalarAsync();
            bookBalance = result is decimal dec ? dec : 0;
        }
        
        var difference = actualBalance - bookBalance;
        payload["bookBalance"] = bookBalance;
        payload["difference"] = difference;
        payload["countedBy"] = userId;
        payload["countedByName"] = userName;
        
        // 実査記録を登録
        Guid countId;
        await using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.CommandText = @"
                INSERT INTO cash_counts (company_code, payload)
                VALUES ($1, $2::jsonb)
                RETURNING id";
            insertCmd.Parameters.AddWithValue(companyCode);
            insertCmd.Parameters.AddWithValue(payload.ToJsonString());
            countId = (Guid)(await insertCmd.ExecuteScalarAsync() ?? Guid.Empty);
        }
        
        // 差異がある場合、調整仕訳を作成
        Guid? adjustmentVoucherId = null;
        string? adjustmentVoucherNo = null;
        
        if (difference != 0 && createAdjustmentVoucher && financeService != null)
        {
            var countDate = payload["countDate"]?.ToString() ?? DateTime.Today.ToString("yyyy-MM-dd");
            var adjustmentReason = payload["adjustmentReason"]?.ToString() ?? "現金実査差異";
            
            string drAccount, crAccount;
            decimal absAmount = Math.Abs(difference);

            // 现金科目：从 accounts 中查找 payload.isCash=true 的科目（不依赖硬编码/公司设定）
            async Task<string> GetCashAccountCodeAsync()
            {
                await using var q = conn.CreateCommand();
                q.CommandText = @"SELECT account_code
                                  FROM accounts
                                  WHERE company_code = $1
                                    AND COALESCE((payload->>'isCash')::boolean, false) = true
                                  ORDER BY account_code
                                  LIMIT 1";
                q.Parameters.AddWithValue(companyCode);
                var obj = await q.ExecuteScalarAsync();
                if (obj is string s && !string.IsNullOrWhiteSpace(s)) return s.Trim();
                throw new Exception("現金科目が設定されていません。accounts に isCash=true の科目を設定してください。");
            }

            var cashAccountCode = await GetCashAccountCodeAsync();
            
            if (difference > 0)
            {
                // 実際が多い → 現金増加、雑収入
                drAccount = adjustmentCreditAccount ?? cashAccountCode; // 現金
                crAccount = adjustmentDebitAccount ?? "914"; // 雑収入
            }
            else
            {
                // 実際が少ない → 現金減少、雑損失
                drAccount = adjustmentDebitAccount ?? "924"; // 雑損失
                crAccount = adjustmentCreditAccount ?? cashAccountCode; // 現金
            }
            
            var voucherPayload = new JsonObject
            {
                ["header"] = new JsonObject
                {
                    ["companyCode"] = companyCode,
                    ["postingDate"] = countDate,
                    ["voucherType"] = "OT",
                    ["currency"] = "JPY",
                    ["summary"] = $"現金実査差異調整 {adjustmentReason}"
                },
                ["lines"] = new JsonArray
                {
                    new JsonObject { ["lineNo"] = 1, ["accountCode"] = drAccount, ["drcr"] = "DR", ["amount"] = absAmount },
                    new JsonObject { ["lineNo"] = 2, ["accountCode"] = crAccount, ["drcr"] = "CR", ["amount"] = absAmount }
                }
            };
            
            try
            {
                var postingDate = DateTime.Parse(countDate);
                var numbering = await Server.Infrastructure.VoucherNumberingService.NextAsync(_ds, companyCode, postingDate);
                
                await using var voucherCmd = conn.CreateCommand();
                voucherCmd.CommandText = @"
                    INSERT INTO vouchers(company_code, payload)
                    VALUES ($1, jsonb_set($2::jsonb, '{header,voucherNo}', to_jsonb($3::text)))
                    RETURNING id, payload->'header'->>'voucherNo'";
                voucherCmd.Parameters.AddWithValue(companyCode);
                voucherCmd.Parameters.AddWithValue(voucherPayload.ToJsonString());
                voucherCmd.Parameters.AddWithValue(numbering.voucherNo);
                
                await using var voucherReader = await voucherCmd.ExecuteReaderAsync();
                if (await voucherReader.ReadAsync())
                {
                    adjustmentVoucherId = voucherReader.GetGuid(0);
                    adjustmentVoucherNo = voucherReader.IsDBNull(1) ? null : voucherReader.GetString(1);
                    
                    // 刷新总账物化视图
                    await FinanceService.RefreshGlViewAsync(conn);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CashCount] Failed to create adjustment voucher: {ex.Message}");
            }
            
            // 実査記録に仕訳情報を紐付け
            if (adjustmentVoucherId.HasValue)
            {
                await using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = @"
                    UPDATE cash_counts 
                    SET payload = jsonb_set(jsonb_set(payload, '{adjustmentVoucherId}', to_jsonb($3::text)), '{adjustmentVoucherNo}', to_jsonb($4::text))
                    WHERE id = $1 AND company_code = $2";
                updateCmd.Parameters.AddWithValue(countId);
                updateCmd.Parameters.AddWithValue(companyCode);
                updateCmd.Parameters.AddWithValue(adjustmentVoucherId.Value.ToString());
                updateCmd.Parameters.AddWithValue(adjustmentVoucherNo ?? "");
                await updateCmd.ExecuteNonQueryAsync();
            }
            
            // 現金口座の残高を実際残高に更新
            await using var balanceCmd = conn.CreateCommand();
            balanceCmd.CommandText = @"
                UPDATE cash_accounts 
                SET payload = jsonb_set(jsonb_set(payload, '{currentBalance}', to_jsonb($3::numeric)), '{lastReconciledAt}', to_jsonb($4::text)),
                    updated_at = now()
                WHERE company_code = $1 AND cash_code = $2";
            balanceCmd.Parameters.AddWithValue(companyCode);
            balanceCmd.Parameters.AddWithValue(cashCode);
            balanceCmd.Parameters.AddWithValue(actualBalance);
            balanceCmd.Parameters.AddWithValue(DateTime.UtcNow.ToString("o"));
            await balanceCmd.ExecuteNonQueryAsync();
        }
        
        return (countId, adjustmentVoucherId, adjustmentVoucherNo);
    }
    
    /// <summary>
    /// 実査履歴を取得
    /// </summary>
    public async Task<List<JsonObject>> GetCashCountsAsync(string companyCode, string cashCode)
    {
        var counts = new List<JsonObject>();
        
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        
        cmd.CommandText = @"
            SELECT id, payload, created_at
            FROM cash_counts
            WHERE company_code = $1 AND cash_code = $2
            ORDER BY count_date DESC, created_at DESC
            LIMIT 50";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(cashCode);
        
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var payload = JsonNode.Parse(reader.GetString(1)) as JsonObject ?? new JsonObject();
            payload["id"] = reader.GetGuid(0).ToString();
            payload["createdAt"] = reader.GetDateTime(2).ToString("yyyy-MM-dd HH:mm");
            counts.Add(payload);
        }
        
        return counts;
    }
    
    #endregion
    
    #region 現金補充
    
    /// <summary>
    /// 現金補充を実行（銀行から引出し、または他の現金口座から振替）
    /// </summary>
    /// <remarks>
    /// 日本の中小企業で一般的な小口現金の定額資金前渡制（インプレストシステム）に対応。
    /// 補充時に自動仕訳を作成し、両口座の残高を更新する。
    /// </remarks>
    public async Task<CashReplenishmentResult> CreateReplenishmentAsync(
        string companyCode,
        CashReplenishmentRequest request,
        FinanceService? financeService,
        string? userId,
        string? userName)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        
        try
        {
            var targetCashCode = request.TargetCashCode;
            var sourceAccountCode = request.SourceAccountCode;
            var amount = request.Amount;
            var replenishDate = request.ReplenishDate ?? DateTime.Today;
            var memo = request.Memo ?? "";
            
            // 1. 補充先（現金口座）の勘定科目コードを取得
            string? targetAccountCode = targetCashCode;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT account_code
                    FROM accounts
                    WHERE company_code = $1 
                      AND account_code = $2
                    LIMIT 1";
                cmd.Parameters.AddWithValue(companyCode);
                cmd.Parameters.AddWithValue(targetCashCode);
                var result = await cmd.ExecuteScalarAsync();
                if (result != null)
                    targetAccountCode = result.ToString()!;
            }
            
            // 2. 補充元が現金科目かどうか確認
            bool sourceIsCash = false;
            bool sourceIsBank = false;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT 
                        COALESCE((payload->>'isCash')::boolean, false),
                        COALESCE((payload->>'isBank')::boolean, false)
                    FROM accounts 
                    WHERE company_code = $1 
                      AND account_code = $2
                    LIMIT 1";
                cmd.Parameters.AddWithValue(companyCode);
                cmd.Parameters.AddWithValue(sourceAccountCode);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    sourceIsCash = reader.GetBoolean(0);
                    sourceIsBank = reader.GetBoolean(1);
                }
            }
            
            // 3. 補充先の現金口座に入金取引を登録
            var transactionNo = await GenerateTransactionNoAsync(conn, companyCode, targetCashCode, replenishDate);
            
            decimal targetCurrentBalance = 0;
            await using (var getCmd = conn.CreateCommand())
            {
                getCmd.CommandText = @"
                    SELECT COALESCE(
                        (SELECT (payload->>'balanceAfter')::numeric 
                         FROM cash_transactions 
                         WHERE company_code = $1 AND cash_code = $2 
                         ORDER BY transaction_date DESC, transaction_no DESC LIMIT 1),
                        0
                    )";
                getCmd.Parameters.AddWithValue(companyCode);
                getCmd.Parameters.AddWithValue(targetCashCode);
                var result = await getCmd.ExecuteScalarAsync();
                targetCurrentBalance = result is decimal dec ? dec : 0;
            }
            
            var newTargetBalance = targetCurrentBalance + amount;
            
            var targetPayload = new JsonObject
            {
                ["cashCode"] = targetCashCode,
                ["transactionNo"] = transactionNo,
                ["transactionDate"] = replenishDate.ToString("yyyy-MM-dd"),
                ["transactionType"] = "replenish",
                ["amount"] = amount,
                ["balanceAfter"] = newTargetBalance,
                ["description"] = string.IsNullOrEmpty(memo) 
                    ? (sourceIsBank ? "銀行から補充" : "現金補充") 
                    : memo,
                ["sourceAccountCode"] = sourceAccountCode,
                ["createdBy"] = userId,
                ["createdByName"] = userName
            };
            
            Guid targetTransactionId;
            await using (var insertCmd = conn.CreateCommand())
            {
                insertCmd.CommandText = @"
                    INSERT INTO cash_transactions (company_code, payload)
                    VALUES ($1, $2::jsonb)
                    RETURNING id";
                insertCmd.Parameters.AddWithValue(companyCode);
                insertCmd.Parameters.AddWithValue(targetPayload.ToJsonString());
                targetTransactionId = (Guid)(await insertCmd.ExecuteScalarAsync() ?? Guid.Empty);
            }
            
            // 4. 補充元が現金口座の場合、出金取引も登録
            Guid? sourceTransactionId = null;
            string? sourceTransactionNo = null;
            if (sourceIsCash)
            {
                sourceTransactionNo = await GenerateTransactionNoAsync(conn, companyCode, sourceAccountCode, replenishDate);
                
                decimal sourceCurrentBalance = 0;
                await using (var getCmd = conn.CreateCommand())
                {
                    getCmd.CommandText = @"
                        SELECT COALESCE(
                            (SELECT (payload->>'balanceAfter')::numeric 
                             FROM cash_transactions 
                             WHERE company_code = $1 AND cash_code = $2 
                             ORDER BY transaction_date DESC, transaction_no DESC LIMIT 1),
                            0
                        )";
                    getCmd.Parameters.AddWithValue(companyCode);
                    getCmd.Parameters.AddWithValue(sourceAccountCode);
                    var result = await getCmd.ExecuteScalarAsync();
                    sourceCurrentBalance = result is decimal dec ? dec : 0;
                }
                
                var newSourceBalance = sourceCurrentBalance - amount;
                
                var sourcePayload = new JsonObject
                {
                    ["cashCode"] = sourceAccountCode,
                    ["transactionNo"] = sourceTransactionNo,
                    ["transactionDate"] = replenishDate.ToString("yyyy-MM-dd"),
                    ["transactionType"] = "payment",
                    ["amount"] = amount,
                    ["balanceAfter"] = newSourceBalance,
                    ["description"] = $"現金補充振替 → {targetCashCode}",
                    ["targetAccountCode"] = targetCashCode,
                    ["createdBy"] = userId,
                    ["createdByName"] = userName
                };
                
                await using (var insertCmd = conn.CreateCommand())
                {
                    insertCmd.CommandText = @"
                        INSERT INTO cash_transactions (company_code, payload)
                        VALUES ($1, $2::jsonb)
                        RETURNING id";
                    insertCmd.Parameters.AddWithValue(companyCode);
                    insertCmd.Parameters.AddWithValue(sourcePayload.ToJsonString());
                    sourceTransactionId = (Guid)(await insertCmd.ExecuteScalarAsync() ?? Guid.Empty);
                }
            }
            
            await tx.CommitAsync();
            
            // 5. 仕訳作成（オプション）
            Guid? voucherId = null;
            string? voucherNo = null;
            if (request.CreateVoucher && financeService != null)
            {
                var summary = sourceIsBank 
                    ? $"現金補充（銀行引出） {memo}".Trim()
                    : $"現金振替補充 {memo}".Trim();
                
                var voucherPayload = new JsonObject
                {
                    ["header"] = new JsonObject
                    {
                        ["companyCode"] = companyCode,
                        ["postingDate"] = replenishDate.ToString("yyyy-MM-dd"),
                        ["voucherType"] = "OT",
                        ["currency"] = "JPY",
                        ["summary"] = summary
                    },
                    ["lines"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["lineNo"] = 1,
                            ["accountCode"] = targetAccountCode,
                            ["drcr"] = "DR",
                            ["amount"] = amount,
                            ["memo"] = "現金補充"
                        },
                        new JsonObject
                        {
                            ["lineNo"] = 2,
                            ["accountCode"] = sourceAccountCode,
                            ["drcr"] = "CR",
                            ["amount"] = amount,
                            ["memo"] = sourceIsBank ? "銀行引出" : "現金振替"
                        }
                    }
                };
                
                try
                {
                    var numbering = await Server.Infrastructure.VoucherNumberingService.NextAsync(_ds, companyCode, replenishDate);
                    
                    await using var voucherConn = await _ds.OpenConnectionAsync();
                    await using var voucherCmd = voucherConn.CreateCommand();
                    voucherCmd.CommandText = @"
                        INSERT INTO vouchers(company_code, payload)
                        VALUES ($1, jsonb_set($2::jsonb, '{header,voucherNo}', to_jsonb($3::text)))
                        RETURNING id, payload->'header'->>'voucherNo'";
                    voucherCmd.Parameters.AddWithValue(companyCode);
                    voucherCmd.Parameters.AddWithValue(voucherPayload.ToJsonString());
                    voucherCmd.Parameters.AddWithValue(numbering.voucherNo);
                    
                    await using var voucherReader = await voucherCmd.ExecuteReaderAsync();
                    if (await voucherReader.ReadAsync())
                    {
                        voucherId = voucherReader.GetGuid(0);
                        voucherNo = voucherReader.IsDBNull(1) ? null : voucherReader.GetString(1);
                    }
                    
                    // 取引に仕訳情報を紐付け
                    if (voucherId.HasValue)
                    {
                        await using var linkCmd = voucherConn.CreateCommand();
                        linkCmd.CommandText = @"
                            UPDATE cash_transactions 
                            SET payload = jsonb_set(jsonb_set(payload, '{voucherId}', to_jsonb($3::text)), '{voucherNo}', to_jsonb($4::text))
                            WHERE id = $1 AND company_code = $2";
                        linkCmd.Parameters.AddWithValue(targetTransactionId);
                        linkCmd.Parameters.AddWithValue(companyCode);
                        linkCmd.Parameters.AddWithValue(voucherId.Value.ToString());
                        linkCmd.Parameters.AddWithValue(voucherNo ?? "");
                        await linkCmd.ExecuteNonQueryAsync();
                        
                        if (sourceTransactionId.HasValue)
                        {
                            await using var linkCmd2 = voucherConn.CreateCommand();
                            linkCmd2.CommandText = @"
                                UPDATE cash_transactions 
                                SET payload = jsonb_set(jsonb_set(payload, '{voucherId}', to_jsonb($3::text)), '{voucherNo}', to_jsonb($4::text))
                                WHERE id = $1 AND company_code = $2";
                            linkCmd2.Parameters.AddWithValue(sourceTransactionId.Value);
                            linkCmd2.Parameters.AddWithValue(companyCode);
                            linkCmd2.Parameters.AddWithValue(voucherId.Value.ToString());
                            linkCmd2.Parameters.AddWithValue(voucherNo ?? "");
                            await linkCmd2.ExecuteNonQueryAsync();
                        }
                        
                        // 刷新总账物化视图
                        await FinanceService.RefreshGlViewAsync(voucherConn);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CashReplenishment] Failed to create voucher: {ex.Message}");
                }
            }
            
            return new CashReplenishmentResult
            {
                TargetTransactionId = targetTransactionId,
                TargetTransactionNo = transactionNo,
                SourceTransactionId = sourceTransactionId,
                SourceTransactionNo = sourceTransactionNo,
                VoucherId = voucherId,
                VoucherNo = voucherNo,
                Amount = amount,
                NewBalance = newTargetBalance
            };
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
    
    /// <summary>
    /// 補充可能な口座一覧を取得（銀行口座・現金口座）
    /// </summary>
    public async Task<List<JsonObject>> GetReplenishmentSourcesAsync(string companyCode, string excludeCashCode)
    {
        var sources = new List<JsonObject>();
        
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        
        // isCash または isBank が true の勘定科目を取得（自分自身は除く）
        // account_code と payload->>'code' の両方を code として使用
        cmd.CommandText = @"
            SELECT 
                account_code,
                COALESCE(payload->>'name', account_code) as name,
                COALESCE((payload->>'isCash')::boolean, false) as is_cash,
                COALESCE((payload->>'isBank')::boolean, false) as is_bank
            FROM accounts 
            WHERE company_code = $1 
              AND (COALESCE((payload->>'isCash')::boolean, false) = true 
                   OR COALESCE((payload->>'isBank')::boolean, false) = true)
              AND account_code != $2
            ORDER BY 
                COALESCE((payload->>'isBank')::boolean, false) DESC,
                account_code";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(excludeCashCode);
        
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var code = reader.GetString(0);
            var source = new JsonObject
            {
                ["code"] = code,
                ["name"] = reader.GetString(1),
                ["isCash"] = reader.GetBoolean(2),
                ["isBank"] = reader.GetBoolean(3),
                ["type"] = reader.GetBoolean(3) ? "bank" : "cash"
            };
            sources.Add(source);
        }
        
        return sources;
    }
    
    /// <summary>
    /// 定額資金前渡制の補充推奨額を計算
    /// </summary>
    public async Task<JsonObject> CalculateImprestReplenishmentAsync(string companyCode, string cashCode)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        
        // 現金口座情報を取得
        decimal imprestAmount = 0;
        decimal currentBalance = 0;
        bool imprestSystem = false;
        
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT 
                    COALESCE((payload->>'imprestAmount')::numeric, 0),
                    COALESCE((payload->>'currentBalance')::numeric, 0),
                    COALESCE((payload->>'imprestSystem')::boolean, false)
                FROM accounts
                WHERE company_code = $1 
                  AND account_code = $2
                LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(cashCode);
            
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                imprestAmount = reader.GetDecimal(0);
                currentBalance = reader.GetDecimal(1);
                imprestSystem = reader.GetBoolean(2);
            }
        }
        
        // 最新の残高を取引履歴から取得
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT COALESCE((payload->>'balanceAfter')::numeric, 0)
                FROM cash_transactions
                WHERE company_code = $1 AND cash_code = $2
                ORDER BY transaction_date DESC, transaction_no DESC
                LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(cashCode);
            var result = await cmd.ExecuteScalarAsync();
            if (result is decimal dec)
                currentBalance = dec;
        }
        
        // 当期の支出サマリーを取得
        var expenseSummary = new JsonArray();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT 
                    COALESCE(payload->>'category', '未分類') as category,
                    COUNT(*) as count,
                    SUM((payload->>'amount')::numeric) as total
                FROM cash_transactions
                WHERE company_code = $1 
                  AND cash_code = $2 
                  AND payload->>'transactionType' = 'payment'
                  AND transaction_date >= date_trunc('month', CURRENT_DATE)
                GROUP BY payload->>'category'
                ORDER BY total DESC";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(cashCode);
            
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                expenseSummary.Add(new JsonObject
                {
                    ["category"] = reader.GetString(0),
                    ["count"] = reader.GetInt64(1),
                    ["amount"] = reader.GetDecimal(2)
                });
            }
        }
        
        // 推奨補充額を計算
        var recommendedAmount = imprestSystem && imprestAmount > 0 
            ? Math.Max(0, imprestAmount - currentBalance)
            : 0m;
        
        return new JsonObject
        {
            ["cashCode"] = cashCode,
            ["imprestSystem"] = imprestSystem,
            ["imprestAmount"] = imprestAmount,
            ["currentBalance"] = currentBalance,
            ["recommendedAmount"] = recommendedAmount,
            ["expenseSummary"] = expenseSummary
        };
    }
    
    public record CashReplenishmentRequest
    {
        public string TargetCashCode { get; init; } = "";
        public string SourceAccountCode { get; init; } = "";
        public decimal Amount { get; init; }
        public DateTime? ReplenishDate { get; init; }
        public string? Memo { get; init; }
        public bool CreateVoucher { get; init; } = true;
    }
    
    public record CashReplenishmentResult
    {
        public Guid TargetTransactionId { get; init; }
        public string TargetTransactionNo { get; init; } = "";
        public Guid? SourceTransactionId { get; init; }
        public string? SourceTransactionNo { get; init; }
        public Guid? VoucherId { get; init; }
        public string? VoucherNo { get; init; }
        public decimal Amount { get; init; }
        public decimal NewBalance { get; init; }
    }
    
    #endregion
    
    #region 支出カテゴリ
    
    /// <summary>
    /// デフォルトの支出カテゴリを取得
    /// </summary>
    public static List<ExpenseCategory> GetDefaultExpenseCategories()
    {
        return new List<ExpenseCategory>
        {
            new("transportation", "旅費交通費", "842"),
            new("supplies", "消耗品費", "852"),
            new("communication", "通信費", "843"),
            new("entertainment", "交際費", "844"),
            new("miscellaneous", "雑費", "869"),
            new("postage", "郵送費", "843"), // 通信費に統合
            new("books", "新聞図書費", "861"),
        };
    }
    
    public record ExpenseCategory(string Code, string Name, string AccountCode);
    
    #endregion
}

