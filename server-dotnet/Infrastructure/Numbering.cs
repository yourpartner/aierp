using Npgsql;
using System;
using System.Threading.Tasks;

namespace Server.Infrastructure
{
    // 凭证编号服务：按 company_code + yymm 维护递增号，返回 yymm-000001 样式
    public static class VoucherNumberingService
    {
        public static async Task<(string yymm, long nextNumber, string voucherNo)> NextAsync(NpgsqlDataSource ds, string companyCode, DateTime postingDate)
        {
            var yymm = postingDate.ToString("yyMM");
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            // 确保记录存在
            await using (var ensure = conn.CreateCommand())
            {
                ensure.CommandText = "INSERT INTO voucher_sequences(company_code,yymm,last_number) VALUES ($1,$2,0) ON CONFLICT DO NOTHING";
                ensure.Parameters.AddWithValue(companyCode);
                ensure.Parameters.AddWithValue(yymm);
                await ensure.ExecuteNonQueryAsync();
            }
            // 原子自增并返回
            long next;
            await using (var inc = conn.CreateCommand())
            {
                inc.CommandText = "UPDATE voucher_sequences SET last_number = last_number + 1, updated_at=now() WHERE company_code=$1 AND yymm=$2 RETURNING last_number";
                inc.Parameters.AddWithValue(companyCode);
                inc.Parameters.AddWithValue(yymm);
                var obj = await inc.ExecuteScalarAsync();
                next = Convert.ToInt64(obj ?? 0);
            }
            await tx.CommitAsync();
            var voucherNo = yymm + next.ToString().PadLeft(6, '0');
            return (yymm, next, voucherNo);
        }
    }

    // 物料编号服务：按 company_code + yymm 维护递增号，返回 MATyymm##### 样式
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

    // 仓库编号服务：按 company_code + yymm 维护递增号，返回 WHyymm#### 样式
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
}
