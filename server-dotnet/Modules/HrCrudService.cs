using System.Text.Json;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

public class HrCrudService
{
    private readonly NpgsqlDataSource _ds;
    public HrCrudService(NpgsqlDataSource ds) { _ds = ds; }

    public async Task<string?> CreateEmploymentType(string companyCode, string table, JsonElement payload)
    {
        // Directly persist row; validation is handled by schema in Program.cs.
        return await Crud.InsertRawJson(_ds, table, companyCode, payload.GetRawText());
    }

    /// <summary>
    /// 创建 Payroll Policy，支持两种模式：
    /// 1. 创建新版本：将旧版本设为 inactive，创建新版本
    /// 2. 更新现有版本：如果 payload 包含 id，直接更新该记录（不创建新版本）
    /// 同时限制版本数量，只保留最近 MaxPolicyVersions 个版本
    /// </summary>
    private const int MaxPolicyVersions = 5;

    public async Task<string> CreatePayrollPolicy(string companyCode, string table, JsonElement payload)
    {
        var versionProvided = payload.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString());
        var codeProvided = payload.TryGetProperty("code", out var pc) && pc.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pc.GetString());
        Guid existingId = Guid.Empty;
        var idProvided = payload.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && Guid.TryParse(idEl.GetString(), out existingId);
        var now = DateTimeOffset.Now;
        var genVer = now.ToString("yyyyMMdd-HHmmss");
        var code = codeProvided ? pc.GetString()! : ("POL" + genVer);

        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            string? json;

            if (idProvided)
            {
                // 模式2：更新现有版本（不创建新版本）
                await using var updateCmd = conn.CreateCommand();
                updateCmd.Transaction = tx;
                updateCmd.CommandText = $@"
                    UPDATE {table} 
                    SET payload = jsonb_set(
                        jsonb_set($2::jsonb, '{{version}}', to_jsonb($3::text), true),
                        '{{code}}', to_jsonb($4::text), true
                    ),
                    updated_at = now()
                    WHERE id = $1 AND company_code = $5
                    RETURNING to_jsonb({table})";
                updateCmd.Parameters.AddWithValue(existingId);
                updateCmd.Parameters.AddWithValue(payload.GetRawText());
                updateCmd.Parameters.AddWithValue(versionProvided ? v.GetString()! : genVer);
                updateCmd.Parameters.AddWithValue(code);
                updateCmd.Parameters.AddWithValue(companyCode);
                json = (string?)await updateCmd.ExecuteScalarAsync();
            }
            else
            {
                // 模式1：创建新版本
                // 先将该公司所有 active policy 设为 inactive
                await using var deactivateCmd = conn.CreateCommand();
                deactivateCmd.Transaction = tx;
                deactivateCmd.CommandText = $@"
                    UPDATE {table} 
                    SET payload = jsonb_set(payload, '{{isActive}}', 'false'::jsonb),
                        updated_at = now()
                    WHERE company_code = $1 AND (payload->>'isActive')::boolean = true";
                deactivateCmd.Parameters.AddWithValue(companyCode);
                await deactivateCmd.ExecuteNonQueryAsync();

                // 创建新版本
                await using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = $@"
                    INSERT INTO {table}(company_code, payload) 
                    VALUES ($1, jsonb_set(jsonb_set($2::jsonb, '{{version}}', to_jsonb($3::text), true), '{{code}}', to_jsonb($4::text), true)) 
                    RETURNING to_jsonb({table})";
                insertCmd.Parameters.AddWithValue(companyCode);
                insertCmd.Parameters.AddWithValue(payload.GetRawText());
                insertCmd.Parameters.AddWithValue(versionProvided ? v.GetString()! : genVer);
                insertCmd.Parameters.AddWithValue(code);
                json = (string?)await insertCmd.ExecuteScalarAsync();

                // 清理旧版本，只保留最近 MaxPolicyVersions 个
                await using var cleanupCmd = conn.CreateCommand();
                cleanupCmd.Transaction = tx;
                cleanupCmd.CommandText = $@"
                    DELETE FROM {table} 
                    WHERE company_code = $1 
                    AND id NOT IN (
                        SELECT id FROM {table} 
                        WHERE company_code = $1 
                        ORDER BY created_at DESC 
                        LIMIT {MaxPolicyVersions}
                    )";
                cleanupCmd.Parameters.AddWithValue(companyCode);
                var deleted = await cleanupCmd.ExecuteNonQueryAsync();
                if (deleted > 0)
                {
                    Console.WriteLine($"[policy] Cleaned up {deleted} old policy versions for {companyCode}");
                }
            }

            await tx.CommitAsync();

            if (json is null) throw new Exception("insert/update failed");
            return json;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}


