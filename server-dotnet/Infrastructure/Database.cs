using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Server.Infrastructure;

// 数据库基础设施：
// - 提供服务注册扩展，集中管理 PostgreSQL 连接池（NpgsqlDataSource）
// - 后续可在此集中加入连接字符串轮换、拦截器、诊断等能力
public static class Database
{
    // 注册 PostgreSQL 数据源（连接池）。
    // 读取配置 ConnectionStrings:Default，创建并以单例形式注入。
    public static IServiceCollection AddPostgres(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration.GetConnectionString("Default")!;
        var dataSource = new NpgsqlDataSourceBuilder(connString).Build();
        services.AddSingleton(dataSource);
        return services;
    }
}

public static class Crud
{
    // 将实体名映射到表名（与现有约定保持一致）
    public static string TableFor(string entity)
        => entity switch
        {
            "voucher" => "vouchers",
            "branch" => "branches",
            "openitem" => "open_items",
                "notification_policy" => "notification_policies",
                "notification_rule_run" => "notification_rule_runs",
                "notification_log" => "notification_logs",
                // Inventory
                "material" => "materials",
                "warehouse" => "warehouses",
                "bin" => "bins",
                "stock_status" => "stock_statuses",
                "batch" => "batches",
            "accounting_period" => "accounting_periods",
            "invoice_issuer" => "invoice_issuers",
            // HR/Payroll 新增实体（移除 payroll_item）
            "employment_type" => "employment_types",
            "payroll_policy" => "payroll_policies",
                "certificate_request" => "certificate_requests",
                "timesheet" => "timesheets",
            // CRM 实体
            "activity" => "activities",
            _ => entity + "s"
        };

    // 通用创建：插入 JSON 并返回 to_jsonb(table)
    public static async Task<string?> InsertRawJson(NpgsqlDataSource ds, string table, string companyCode, string payloadJson)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO {table}(company_code, payload) VALUES ($1, $2::jsonb) RETURNING to_jsonb({table})";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(payloadJson);
        var obj = await cmd.ExecuteScalarAsync();
        return obj as string;
    }

    // 通用更新：按 id/company_code 更新 JSON 并返回 to_jsonb(table)
    public static async Task<string?> UpdateRawJson(NpgsqlDataSource ds, string table, Guid id, string companyCode, string payloadJson)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {table} SET payload=$1::jsonb, updated_at=now() WHERE id=$2 AND company_code=$3 RETURNING to_jsonb({table})";
        cmd.Parameters.AddWithValue(payloadJson);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(companyCode);
        var obj = await cmd.ExecuteScalarAsync();
        return obj as string;
    }

    // 通用删除：按 id/company_code 删除
    public static async Task<int> DeleteById(NpgsqlDataSource ds, string table, Guid id, string companyCode)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE id=$1 AND company_code=$2";
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(companyCode);
        return await cmd.ExecuteNonQueryAsync();
    }

    // 通用详情：支持追加权限 scopes 的 SQL 片段
    public static async Task<string?> GetDetailJson(NpgsqlDataSource ds, string table, Guid id, string companyCode, string extraSql, IList<object?> authArgs)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT to_jsonb(t) FROM (SELECT * FROM {table} WHERE id=$1 AND company_code=$2" + extraSql + ") t";
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(companyCode);
        foreach (var a in authArgs) cmd.Parameters.AddWithValue(a!);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    // 通用查询：执行已构建好的 SQL，并返回每行 JSON 字符串
    public static async Task<List<string>> QueryJsonRows(NpgsqlDataSource ds, string sql, IList<object?> args)
    {
        var rows = new List<string>();
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < args.Count; i++) cmd.Parameters.AddWithValue(args[i]!);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetFieldValue<string>(0));
        return rows;
    }
}


