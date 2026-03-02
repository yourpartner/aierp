using Npgsql;
using System;
using System.Threading.Tasks;

namespace Server.Infrastructure
{
    // Voucher numbering service: increments per company_code + yymm and yields yymm-000001 style numbers.
    public static class VoucherNumberingService
    {
        public static async Task<(string yymm, long nextNumber, string voucherNo)> NextAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            string companyCode,
            DateTime postingDate)
        {
            var yymm = postingDate.ToString("yyMM");

            // Ensure sequence row exists
            await using (var ensure = conn.CreateCommand())
            {
                ensure.Transaction = tx;
                ensure.CommandText = "INSERT INTO voucher_sequences(company_code,yymm,last_number) VALUES ($1,$2,0) ON CONFLICT DO NOTHING";
                ensure.Parameters.AddWithValue(companyCode);
                ensure.Parameters.AddWithValue(yymm);
                await ensure.ExecuteNonQueryAsync();
            }

            // 防御策略：先检查 vouchers 表中该 yymm 前缀的最大编号，确保序列不落后
            await using (var sync = conn.CreateCommand())
            {
                sync.Transaction = tx;
                sync.CommandText = @"
                    UPDATE voucher_sequences
                    SET last_number = GREATEST(last_number, COALESCE((
                        SELECT MAX(SUBSTRING(voucher_no FROM 5)::bigint)
                        FROM vouchers
                        WHERE company_code = $1
                          AND voucher_no LIKE $2 || '%'
                          AND LENGTH(voucher_no) = 10
                    ), 0)),
                    updated_at = now()
                    WHERE company_code = $1 AND yymm = $2";
                sync.Parameters.AddWithValue(companyCode);
                sync.Parameters.AddWithValue(yymm);
                await sync.ExecuteNonQueryAsync();
            }

            // Atomically increment and return
            long next;
            await using (var inc = conn.CreateCommand())
            {
                inc.Transaction = tx;
                inc.CommandText = "UPDATE voucher_sequences SET last_number = last_number + 1, updated_at=now() WHERE company_code=$1 AND yymm=$2 RETURNING last_number";
                inc.Parameters.AddWithValue(companyCode);
                inc.Parameters.AddWithValue(yymm);
                var obj = await inc.ExecuteScalarAsync();
                next = Convert.ToInt64(obj ?? 0);
            }

            var voucherNo = yymm + next.ToString().PadLeft(6, '0');
            return (yymm, next, voucherNo);
        }

        public static async Task<(string yymm, long nextNumber, string voucherNo)> NextAsync(NpgsqlDataSource ds, string companyCode, DateTime postingDate)
        {
            var yymm = postingDate.ToString("yyMM");
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var result = await NextAsync(conn, tx, companyCode, postingDate);
            await tx.CommitAsync();
            return result;
        }
    }

    // Material numbering service: company_code + yymm with MATyymm##### format.
    public static class MaterialNumberingService
    {
        public static async Task<(string yymm, long nextNumber, string materialCode)> NextAsync(NpgsqlDataSource ds, string companyCode, DateTime now)
        {
            var yymm = now.ToString("yyMM");
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var ensure = conn.CreateCommand())
            {
                ensure.CommandText = "INSERT INTO material_sequences(company_code,yymm,last_number) VALUES ($1,$2,0) ON CONFLICT DO NOTHING";
                ensure.Parameters.AddWithValue(companyCode);
                ensure.Parameters.AddWithValue(yymm);
                await ensure.ExecuteNonQueryAsync();
            }
            long next;
            await using (var inc = conn.CreateCommand())
            {
                inc.CommandText = "UPDATE material_sequences SET last_number = last_number + 1, updated_at=now() WHERE company_code=$1 AND yymm=$2 RETURNING last_number";
                inc.Parameters.AddWithValue(companyCode);
                inc.Parameters.AddWithValue(yymm);
                var obj = await inc.ExecuteScalarAsync();
                next = Convert.ToInt64(obj ?? 0);
            }
            await tx.CommitAsync();
            var materialCode = $"MAT{yymm}" + next.ToString().PadLeft(5, '0');
            return (yymm, next, materialCode);
        }
    }

    // Warehouse numbering service: company_code + yymm with WHyymm#### format.
    public static class WarehouseNumberingService
    {
        public static async Task<(string yymm, long nextNumber, string warehouseCode)> NextAsync(NpgsqlDataSource ds, string companyCode, DateTime now)
        {
            var yymm = now.ToString("yyMM");
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var ensure = conn.CreateCommand())
            {
                ensure.CommandText = "INSERT INTO warehouse_sequences(company_code,yymm,last_number) VALUES ($1,$2,0) ON CONFLICT DO NOTHING";
                ensure.Parameters.AddWithValue(companyCode);
                ensure.Parameters.AddWithValue(yymm);
                await ensure.ExecuteNonQueryAsync();
            }
            long next;
            await using (var inc = conn.CreateCommand())
            {
                inc.CommandText = "UPDATE warehouse_sequences SET last_number = last_number + 1, updated_at=now() WHERE company_code=$1 AND yymm=$2 RETURNING last_number";
                inc.Parameters.AddWithValue(companyCode);
                inc.Parameters.AddWithValue(yymm);
                var obj = await inc.ExecuteScalarAsync();
                next = Convert.ToInt64(obj ?? 0);
            }
            await tx.CommitAsync();
            var warehouseCode = $"WH{yymm}" + next.ToString().PadLeft(4, '0');
            return (yymm, next, warehouseCode);
        }
    }

    // Business Partner numbering service: BP + 6 digits (BP000001, BP000002, ...)
    public static class BusinessPartnerNumberingService
    {
        public static async Task<string> NextAsync(NpgsqlDataSource ds, string companyCode)
        {
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            // 先检查现有最大编号
            long existingMax = 0;
            await using (var maxCmd = conn.CreateCommand())
            {
                maxCmd.CommandText = @"SELECT COALESCE(MAX(
                    CASE WHEN (payload->>'code') ~ '^BP\d{6}$' 
                         THEN SUBSTRING((payload->>'code') FROM 3)::bigint 
                         ELSE 0 END
                ), 0)
                FROM businesspartners
                WHERE company_code=$1";
                maxCmd.Parameters.AddWithValue(companyCode);
                var obj = await maxCmd.ExecuteScalarAsync();
                existingMax = obj switch
                {
                    long l => l,
                    int i => i,
                    decimal d => (long)d,
                    _ => obj is null ? 0L : Convert.ToInt64(obj)
                };
            }

            // 确保序列表有记录
            await using (var ensure = conn.CreateCommand())
            {
                ensure.CommandText = @"INSERT INTO bp_sequences(company_code, last_number)
                                       VALUES ($1, $2)
                                       ON CONFLICT (company_code) DO NOTHING";
                ensure.Parameters.AddWithValue(companyCode);
                ensure.Parameters.AddWithValue(existingMax);
                await ensure.ExecuteNonQueryAsync();
            }

            // 同步最大值
            await using (var sync = conn.CreateCommand())
            {
                sync.CommandText = @"UPDATE bp_sequences
                                     SET last_number = GREATEST(last_number, $2), updated_at=now()
                                     WHERE company_code=$1";
                sync.Parameters.AddWithValue(companyCode);
                sync.Parameters.AddWithValue(existingMax);
                await sync.ExecuteNonQueryAsync();
            }

            // 递增并返回新编号
            long next;
            await using (var inc = conn.CreateCommand())
            {
                inc.CommandText = @"UPDATE bp_sequences
                                    SET last_number = last_number + 1, updated_at=now()
                                    WHERE company_code=$1
                                    RETURNING last_number";
                inc.Parameters.AddWithValue(companyCode);
                var obj = await inc.ExecuteScalarAsync();
                next = obj switch
                {
                    long l => l,
                    int i => i,
                    decimal d => (long)d,
                    _ => obj is null ? 0L : Convert.ToInt64(obj)
                };
            }

            await tx.CommitAsync();
            if (next <= 0) next = 1;
            return $"BP{next:D6}";
        }
    }

    // Purchase Order numbering service: PO-YYYYMMDD-000001 format
    public static class PurchaseOrderNumberingService
    {
        public static async Task<string> NextAsync(NpgsqlDataSource ds, string companyCode, DateTime orderDate)
        {
            var yyyymmdd = orderDate.ToString("yyyyMMdd");
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            
            // Ensure sequence row exists for this company + date
            await using (var ensure = conn.CreateCommand())
            {
                ensure.CommandText = "INSERT INTO po_sequences(company_code, yyyymmdd, last_number) VALUES ($1, $2, 0) ON CONFLICT DO NOTHING";
                ensure.Parameters.AddWithValue(companyCode);
                ensure.Parameters.AddWithValue(yyyymmdd);
                await ensure.ExecuteNonQueryAsync();
            }
            
            // Atomically increment and return
            long next;
            await using (var inc = conn.CreateCommand())
            {
                inc.CommandText = "UPDATE po_sequences SET last_number = last_number + 1, updated_at = now() WHERE company_code = $1 AND yyyymmdd = $2 RETURNING last_number";
                inc.Parameters.AddWithValue(companyCode);
                inc.Parameters.AddWithValue(yyyymmdd);
                var obj = await inc.ExecuteScalarAsync();
                next = Convert.ToInt64(obj ?? 0);
            }
            
            await tx.CommitAsync();
            var poNo = $"PO-{yyyymmdd}-{next:D6}";
            return poNo;
        }
    }

    // Employee codes: auto-detect prefix pattern (e.g. YP001, E0268) and continue the sequence.
    // Falls back to purely numeric codes if no prefix pattern is found.
    public static class EmployeeNumberingService
    {
        public static async Task<string> NextCodeAsync(NpgsqlDataSource ds, string companyCode)
        {
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            string prefix = "";
            int padWidth = 0;
            long existingMax = 0;

            // Detect the dominant prefix pattern (e.g. "YP", "E", "WEL") from existing employees
            await using (var detectCmd = conn.CreateCommand())
            {
                detectCmd.Transaction = tx;
                detectCmd.CommandText = @"
                    WITH prefixed AS (
                        SELECT
                            regexp_replace(payload->>'code', '\d+$', '') AS pfx,
                            SUBSTRING(payload->>'code' FROM '\d+$')::bigint AS num,
                            LENGTH(SUBSTRING(payload->>'code' FROM '\d+$')) AS num_len
                        FROM employees
                        WHERE company_code = $1
                          AND jsonb_typeof(payload) = 'object'
                          AND (payload->>'code') ~ '^[A-Za-z]+\d+$'
                    )
                    SELECT pfx, MAX(num), MAX(num_len), COUNT(*)
                    FROM prefixed
                    GROUP BY pfx
                    ORDER BY COUNT(*) DESC
                    LIMIT 1";
                detectCmd.Parameters.AddWithValue(companyCode);
                await using var reader = await detectCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    prefix = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    existingMax = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    padWidth = reader.IsDBNull(2) ? 3 : reader.GetInt32(2);
                }
            }

            // Fallback: purely numeric codes
            if (string.IsNullOrEmpty(prefix))
            {
                await using var maxCmd = conn.CreateCommand();
                maxCmd.Transaction = tx;
                maxCmd.CommandText = @"SELECT COALESCE(MAX((payload->>'code')::bigint),0)
                                       FROM employees
                                       WHERE company_code=$1
                                         AND jsonb_typeof(payload) = 'object'
                                         AND (payload->>'code') ~ '^\d+$'";
                maxCmd.Parameters.AddWithValue(companyCode);
                var obj = await maxCmd.ExecuteScalarAsync();
                existingMax = ToLong(obj);
            }

            await using (var ensure = conn.CreateCommand())
            {
                ensure.Transaction = tx;
                ensure.CommandText = @"INSERT INTO employee_sequences(company_code, last_number)
                                       VALUES ($1, $2)
                                       ON CONFLICT (company_code) DO NOTHING";
                ensure.Parameters.AddWithValue(companyCode);
                ensure.Parameters.AddWithValue(existingMax);
                await ensure.ExecuteNonQueryAsync();
            }

            await using (var sync = conn.CreateCommand())
            {
                sync.Transaction = tx;
                sync.CommandText = @"UPDATE employee_sequences
                                     SET last_number = GREATEST(last_number, $2), updated_at=now()
                                     WHERE company_code=$1";
                sync.Parameters.AddWithValue(companyCode);
                sync.Parameters.AddWithValue(existingMax);
                await sync.ExecuteNonQueryAsync();
            }

            long next;
            await using (var inc = conn.CreateCommand())
            {
                inc.Transaction = tx;
                inc.CommandText = @"UPDATE employee_sequences
                                    SET last_number = last_number + 1, updated_at=now()
                                    WHERE company_code=$1
                                    RETURNING last_number";
                inc.Parameters.AddWithValue(companyCode);
                var obj = await inc.ExecuteScalarAsync();
                next = ToLong(obj);
            }

            await tx.CommitAsync();
            if (next <= 0) next = 1;

            if (!string.IsNullOrEmpty(prefix))
            {
                if (padWidth < 3) padWidth = 3;
                return prefix + next.ToString().PadLeft(padWidth, '0');
            }
            return next.ToString();
        }

        private static long ToLong(object? obj) => obj switch
        {
            long l => l,
            int i => i,
            decimal d => (long)d,
            _ => obj is null ? 0L : Convert.ToInt64(obj)
        };
    }
}
