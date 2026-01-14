using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Modules;

public sealed class MoneytreeImportService
{
    private readonly MoneytreeDownloadService _downloader;
    private readonly MoneytreeCsvParser _parser;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<MoneytreeImportService> _logger;
    private readonly IConfiguration _configuration;
    private readonly MoneytreePostingJobQueue _jobQueue;

    public MoneytreeImportService(
        MoneytreeDownloadService downloader,
        MoneytreeCsvParser parser,
        NpgsqlDataSource dataSource,
        IConfiguration configuration,
        MoneytreePostingJobQueue jobQueue,
        ILogger<MoneytreeImportService> logger)
    {
        _downloader = downloader;
        _parser = parser;
        _dataSource = dataSource;
        _configuration = configuration;
        _logger = logger;
        _jobQueue = jobQueue;
    }

    /// <summary>
    /// 导入模式
    /// </summary>
    public enum ImportMode
    {
        /// <summary>正常模式：导入后自动记账</summary>
        Normal,
        /// <summary>历史导入模式：只尝试匹配既存凭证，不自动生成新凭证</summary>
        HistoryLinkOnly
    }
    
    public sealed record MoneytreeImportRequest(string? OtpSecret, DateTimeOffset StartDate, DateTimeOffset EndDate, ImportMode Mode = ImportMode.Normal);
    public sealed record MoneytreeImportResult(Guid BatchId, int TotalRows, int InsertedRows, int SkippedRows, int LinkedRows = 0);

    public async Task<MoneytreeImportResult> ImportAsync(
        string companyCode,
        MoneytreeImportRequest request,
        string? requestedBy,
        CancellationToken ct = default)
    {
        (string Email, string Password, string? OtpSecret) credentials;
        try
        {
            credentials = ResolveCredentials(request.OtpSecret);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Moneytree import failed at credentials: {ex.Message}", ex);
        }

        MoneytreeDownloadService.MoneytreeDownloadResult downloadResult;
        try
        {
            downloadResult = await _downloader.DownloadCsvAsync(
                new MoneytreeDownloadService.MoneytreeDownloadRequest(credentials.Email, credentials.Password, credentials.OtpSecret, request.StartDate, request.EndDate),
                ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Moneytree import failed at download: {ex.Message}", ex);
        }

        IReadOnlyList<MoneytreeCsvParser.MoneytreeRow> rows;
        try
        {
            rows = _parser.Parse(downloadResult.Content, downloadResult.FileName);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Moneytree import failed at parse: {ex.Message}", ex);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        Guid batchId;
        await using (var insertBatch = connection.CreateCommand())
        {
            insertBatch.Transaction = transaction;
            insertBatch.CommandText = """
                INSERT INTO moneytree_import_batches (company_code, requested_by, total_rows, inserted_rows, skipped_rows)
                VALUES ($1, $2, $3, 0, 0)
                RETURNING id;
                """;
            insertBatch.Parameters.AddWithValue(companyCode);
            insertBatch.Parameters.AddWithValue((object?)requestedBy ?? DBNull.Value);
            insertBatch.Parameters.AddWithValue(rows.Count);
            batchId = (Guid)(await insertBatch.ExecuteScalarAsync(ct))!;
        }

        var inserted = 0;
        var skipped = 0;

        // 收集本批次涉及的所有日期
        var datesInBatch = rows
            .Where(r => r.TransactionDate.HasValue)
            .Select(r => r.TransactionDate!.Value.Date)
            .Distinct()
            .ToList();

        // 查询每个日期已有的最大 row_sequence
        var maxSeqByDate = new Dictionary<DateTime, int>();
        if (datesInBatch.Count > 0)
        {
            await using var queryMaxSeq = connection.CreateCommand();
            queryMaxSeq.Transaction = transaction;
            queryMaxSeq.CommandText = """
                SELECT transaction_date::date, COALESCE(MAX(row_sequence), 0)
                FROM moneytree_transactions
                WHERE company_code = $1 AND transaction_date::date = ANY($2)
                GROUP BY transaction_date::date
                """;
            queryMaxSeq.Parameters.AddWithValue(companyCode);
            // 显式指定类型以避免 Npgsql 在某些环境下的解析问题（不指定参数名，按位置顺序）
            queryMaxSeq.Parameters.Add(new NpgsqlParameter
            {
                Value = datesInBatch.ToArray(),
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Date
            });
            await using var reader = await queryMaxSeq.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var date = reader.GetDateTime(0);
                var maxSeq = reader.GetInt32(1);
                maxSeqByDate[date] = maxSeq;
            }
        }

        // 跟踪每个日期的当前序号
        var currentSeqByDate = new Dictionary<DateTime, int>();
        foreach (var date in datesInBatch)
        {
            currentSeqByDate[date] = maxSeqByDate.GetValueOrDefault(date, 0);
        }

        var idx = 0;
        foreach (var row in rows)
        {
            idx++;
            try
            {
                var hash = ComputeHash(companyCode, row);

                // 计算日期内序号
                int rowSeq = idx; // 默认使用批次内序号
                if (row.TransactionDate.HasValue)
                {
                    var date = row.TransactionDate.Value.Date;
                    currentSeqByDate[date]++;
                    rowSeq = currentSeqByDate[date];
                }

                await using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT INTO moneytree_transactions
                    (batch_id, company_code, transaction_date, deposit_amount, withdrawal_amount, balance, currency, bank_name, description, account_name, account_number, hash, row_sequence)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
                    ON CONFLICT (company_code, hash) DO NOTHING;
                    """;

                insertCommand.Parameters.AddWithValue(batchId);
                insertCommand.Parameters.AddWithValue(companyCode);
                insertCommand.Parameters.AddWithValue(row.TransactionDate.HasValue ? row.TransactionDate.Value : (object)DBNull.Value);
                insertCommand.Parameters.AddWithValue(row.DepositAmount.HasValue ? row.DepositAmount.Value : (object)DBNull.Value);
                insertCommand.Parameters.AddWithValue(row.WithdrawalAmount.HasValue ? row.WithdrawalAmount.Value : (object)DBNull.Value);
                insertCommand.Parameters.AddWithValue(row.Balance.HasValue ? row.Balance.Value : (object)DBNull.Value);
                insertCommand.Parameters.AddWithValue((object?)row.Currency ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue((object?)row.BankName ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue(row.Description);
                insertCommand.Parameters.AddWithValue((object?)row.AccountName ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue((object?)row.AccountNumber ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue(hash);
                insertCommand.Parameters.AddWithValue(rowSeq);  // 日期内序号

                var affected = await insertCommand.ExecuteNonQueryAsync(ct);
                if (affected > 0)
                {
                    inserted++;
                }
                else
                {
                    skipped++;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Moneytree import failed at db_insert (row={idx}): {ex.Message}", ex);
            }
        }

        await using (var updateBatch = connection.CreateCommand())
        {
            updateBatch.Transaction = transaction;
            updateBatch.CommandText = """
                UPDATE moneytree_import_batches
                SET inserted_rows = $1,
                    skipped_rows = $2
                WHERE id = $3;
                """;
            updateBatch.Parameters.AddWithValue(inserted);
            updateBatch.Parameters.AddWithValue(skipped);
            updateBatch.Parameters.AddWithValue(batchId);
            await updateBatch.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "Moneytree import completed. Company={CompanyCode}, Batch={BatchId}, Total={Total}, Inserted={Inserted}, Skipped={Skipped}, Mode={Mode}",
            companyCode,
            batchId,
            rows.Count,
            inserted,
            skipped,
            request.Mode);

        var linkedRows = 0;
        
        if (request.Mode == ImportMode.HistoryLinkOnly)
        {
            // 历史导入模式：只尝试匹配既存凭证，不触发自动记账
            _logger.LogInformation("[MoneytreeImport] HistoryLinkOnly mode - starting voucher matching for batch {BatchId}", batchId);
            try
            {
                linkedRows = await MatchExistingVouchersForBatchAsync(companyCode, batchId, requestedBy, ct);
                _logger.LogInformation("[MoneytreeImport] HistoryLinkOnly mode - matched {LinkedRows} transactions to existing vouchers", linkedRows);
            }
            catch (Exception matchEx)
            {
                _logger.LogWarning(matchEx, "[MoneytreeImport] Failed to match existing vouchers for batch {BatchId}", batchId);
            }
        }
        else
        {
            // 正常模式：触发自动记账
            try
            {
                await _jobQueue.EnqueueAsync(new MoneytreePostingJobQueue.MoneytreePostingJob(companyCode, requestedBy, 50), ct);
            }
            catch (Exception enqueueEx)
            {
                _logger.LogWarning(enqueueEx, "Failed to enqueue Moneytree posting job for company {CompanyCode}", companyCode);
            }
        }

        return new MoneytreeImportResult(batchId, rows.Count, inserted, skipped, linkedRows);
    }
    
    /// <summary>
    /// 为批次中的银行明细匹配既存凭证（历史导入模式）
    /// </summary>
    private async Task<int> MatchExistingVouchersForBatchAsync(
        string companyCode,
        Guid batchId,
        string? requestedBy,
        CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        
        // 查询批次中所有未处理的银行明细
        var transactions = new List<(Guid Id, DateTime? TransactionDate, decimal? DepositAmount, decimal? WithdrawalAmount, string? Description, string? BankName)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT id, transaction_date, deposit_amount, withdrawal_amount, description, bank_name
                FROM moneytree_transactions
                WHERE company_code = $1 AND batch_id = $2 AND posting_status = 'pending'
                ORDER BY transaction_date";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(batchId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                transactions.Add((
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                    reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                    reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)
                ));
            }
        }
        
        if (transactions.Count == 0)
        {
            return 0;
        }
        
        // 获取银行科目列表
        var bankAccounts = new List<string>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT account_code FROM accounts 
                WHERE company_code = $1 AND (payload->>'isBank')::boolean = true";
            cmd.Parameters.AddWithValue(companyCode);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                bankAccounts.Add(reader.GetString(0));
            }
        }
        
        if (bankAccounts.Count == 0)
        {
            _logger.LogWarning("[MoneytreeImport] No bank accounts found for company {CompanyCode}", companyCode);
            return 0;
        }
        
        var linkedCount = 0;
        
        foreach (var tx in transactions)
        {
            // 注意：withdrawal_amount 在数据库中存储为负数，deposit_amount 为正数
            var isWithdrawal = (tx.WithdrawalAmount ?? 0m) < 0m;
            var amount = isWithdrawal ? Math.Abs(tx.WithdrawalAmount!.Value) : (tx.DepositAmount ?? 0m);
            if (amount <= 0m) continue;
            
            var transactionDate = tx.TransactionDate ?? DateTime.UtcNow.Date;
            var dateTolerance = isWithdrawal ? 5 : 3; // 出金放宽到±5天，入金±3天
            var bankSide = isWithdrawal ? "CR" : "DR";
            
            // 尝试匹配既存凭证
            Guid? matchedVoucherId = null;
            string? matchedVoucherNo = null;
            
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT v.id, v.payload->'header'->>'voucherNo'
                    FROM vouchers v
                    WHERE v.company_code = $1
                      AND (v.payload->'header'->>'postingDate')::date BETWEEN ($2::date - $3) AND ($2::date + $3)
                      AND EXISTS (
                        SELECT 1 FROM jsonb_array_elements(v.payload->'lines') AS line
                        WHERE (line->>'amount')::numeric = $4
                          AND line->>'accountCode' = ANY($5)
                          AND line->>'drcr' = $6
                      )
                      AND NOT EXISTS (
                        SELECT 1 FROM moneytree_transactions mt
                        WHERE mt.voucher_id = v.id AND mt.company_code = v.company_code
                      )
                    ORDER BY ABS((v.payload->'header'->>'postingDate')::date - $2::date), v.created_at DESC
                    LIMIT 1";
                cmd.Parameters.AddWithValue(companyCode);
                cmd.Parameters.AddWithValue(transactionDate);
                cmd.Parameters.AddWithValue(dateTolerance);
                cmd.Parameters.AddWithValue(amount);
                cmd.Parameters.AddWithValue(bankAccounts.ToArray());
                cmd.Parameters.AddWithValue(bankSide);
                
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    matchedVoucherId = reader.GetGuid(0);
                    matchedVoucherNo = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }
            
            if (matchedVoucherId.HasValue)
            {
                // 更新银行明细状态为linked
                await using var updateCmd = connection.CreateCommand();
                updateCmd.CommandText = @"
                    UPDATE moneytree_transactions 
                    SET posting_status = 'linked',
                        posting_error = $3,
                        voucher_id = $4,
                        voucher_no = $5,
                        updated_at = now()
                    WHERE id = $1 AND company_code = $2";
                updateCmd.Parameters.AddWithValue(tx.Id);
                updateCmd.Parameters.AddWithValue(companyCode);
                updateCmd.Parameters.AddWithValue($"既存伝票に紐付け済み：{matchedVoucherNo}");
                updateCmd.Parameters.AddWithValue(matchedVoucherId.Value);
                updateCmd.Parameters.AddWithValue(matchedVoucherNo ?? "");
                await updateCmd.ExecuteNonQueryAsync(ct);
                
                linkedCount++;
                _logger.LogDebug("[MoneytreeImport] Linked transaction {TxId} to voucher {VoucherNo}", tx.Id, matchedVoucherNo);
            }
            else
            {
                // 没有匹配的凭证，标记为unmatched
                await using var updateCmd = connection.CreateCommand();
                updateCmd.CommandText = @"
                    UPDATE moneytree_transactions 
                    SET posting_status = 'unmatched',
                        posting_error = '既存凭証が見つかりませんでした（履歴インポートモード）',
                        updated_at = now()
                    WHERE id = $1 AND company_code = $2";
                updateCmd.Parameters.AddWithValue(tx.Id);
                updateCmd.Parameters.AddWithValue(companyCode);
                await updateCmd.ExecuteNonQueryAsync(ct);
            }
        }
        
        return linkedCount;
    }

    private (string Email, string Password, string? OtpSecret) ResolveCredentials(string? overrideOtpSecret)
    {
        var email = GetEnv("MONEYTREE_EMAIL") ?? _configuration["Moneytree:Email"];
        var password = GetEnv("MONEYTREE_PASSWORD") ?? _configuration["Moneytree:Password"];
        var otp = overrideOtpSecret
            ?? GetEnv("MONEYTREE_OTP_SECRET")
            ?? _configuration["Moneytree:OtpSecret"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Moneytree 邮箱或密码未配置，请设置环境变量 MONEYTREE_EMAIL/MONEYTREE_PASSWORD。");
        }

        return (email!, password!, string.IsNullOrWhiteSpace(otp) ? null : otp);
    }

    private static string? GetEnv(string name)
    {
        return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
    }

    private static string ComputeHash(string companyCode, MoneytreeCsvParser.MoneytreeRow row)
    {
        var builder = new StringBuilder();
        builder.Append(companyCode).Append('|');
        builder.Append(row.TransactionDate?.ToString("yyyy-MM-dd") ?? string.Empty).Append('|');
        builder.Append(row.DepositAmount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('|');
        builder.Append(row.WithdrawalAmount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('|');
        var netAmount = (row.DepositAmount ?? 0m) - (row.WithdrawalAmount ?? 0m);
        builder.Append(netAmount.ToString(CultureInfo.InvariantCulture)).Append('|');
        builder.Append(row.Balance?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('|');
        builder.Append(row.Currency ?? string.Empty).Append('|');
        builder.Append(row.BankName ?? string.Empty).Append('|');
        builder.Append(row.Description).Append('|');
        builder.Append(row.AccountName ?? string.Empty).Append('|');
        builder.Append(row.AccountNumber ?? string.Empty);

        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }
}
