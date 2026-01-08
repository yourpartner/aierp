using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System;
using System.Collections.Generic;

namespace Server.Infrastructure;

// Database infrastructure:
// - Registers and manages the PostgreSQL connection pool (NpgsqlDataSource).
// - Future additions (rotation/interceptors/diagnostics) can live here.
public static class Database
{
    // Registers the PostgreSQL data source (connection pool) using ConnectionStrings:Default.
    public static IServiceCollection AddPostgres(this IServiceCollection services, IConfiguration configuration)
    {
        var preferEnv = configuration.GetSection("Database").GetValue<bool?>("PreferEnvironment") ?? true;
        string? raw = null;
        if (preferEnv)
        {
            raw = Environment.GetEnvironmentVariable("PG_CONNECTION_STRING");
            // SECURITY: Never log full connection strings (may contain passwords).
            Console.WriteLine($"[database] env PG_CONNECTION_STRING is {(string.IsNullOrWhiteSpace(raw) ? "not set" : "set")}");
            if (!string.IsNullOrWhiteSpace(raw) && string.Equals(raw.Trim(), "ignore", StringComparison.OrdinalIgnoreCase))
            {
                // Allow explicitly skipping the env var by setting it to IGNORE.
                raw = null;
            }
        }
        raw = raw
              ?? configuration.GetConnectionString("Default")
              ?? throw new InvalidOperationException("Missing PostgreSQL connection string.");
        var connString = NormalizeConnectionString(raw);
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connString);
            Console.WriteLine($"[database] connecting to {builder.Host}:{builder.Port}/{builder.Database}");
        }
        catch
        {
            Console.WriteLine("[database] connection string detected but failed to parse for logging");
        }
        var dataSource = new NpgsqlDataSourceBuilder(connString).Build();
        services.AddSingleton(dataSource);
        return services;
    }

    private static string NormalizeConnectionString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("PostgreSQL connection string is empty.");

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(trimmed);
            var builder = new NpgsqlConnectionStringBuilder();

            builder.Host = uri.Host;
            if (uri.Port > 0)
                builder.Port = uri.Port;
            var database = uri.AbsolutePath.Trim('/');
            if (!string.IsNullOrEmpty(database))
                builder.Database = Uri.UnescapeDataString(database);

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                builder.Username = Uri.UnescapeDataString(parts[0]);
                if (parts.Length > 1)
                    builder.Password = Uri.UnescapeDataString(parts[1]);
            }

            var query = uri.Query;
            if (!string.IsNullOrEmpty(query))
            {
                foreach (var kv in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = kv.Split('=', 2);
                    if (pair.Length == 0) continue;
                    var key = Uri.UnescapeDataString(pair[0]);
                    var value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
                    if (!string.IsNullOrWhiteSpace(key))
                        builder[key] = value;
                }
            }

            return builder.ConnectionString;
        }

        return trimmed;
    }
}

public static class Crud
{
    private static readonly HashSet<string> GlobalTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "banks",
        "branches"
    };

    // Map entity names to table names (consistent with existing conventions).
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
            "agent_scenario" => "agent_scenarios",
            // HR/Payroll entities (payroll_item removed).
            "employment_type" => "employment_types",
            "payroll_policy" => "payroll_policies",
                "certificate_request" => "certificate_requests",
                "timesheet" => "timesheets",
                "timesheet_submission" => "timesheet_submissions",
            // CRM entities.
            "activity" => "activities",
            // Procurement entities.
            "purchase_order" => "purchase_orders",
            "vendor_invoice" => "vendor_invoices",
            // Staffing entities (人才派遣模块).
            // NOTE: staffing tables use dedicated stf_* payload tables to avoid impacting Standard edition schemas/tables.
            "resource" => "stf_resources",
            "staffing_project" => "stf_projects",
            "staffing_project_candidate" => "stf_project_candidates",
            "staffing_contract" => "stf_contracts",
            "staffing_timesheet_summary" => "stf_timesheet_summaries",
            "staffing_invoice" => "stf_invoices",
            "staffing_invoice_line" => "stf_invoice_lines",
            "staffing_email_account" => "stf_email_accounts",
            "staffing_email_template" => "stf_email_templates",
            "staffing_email_message" => "stf_email_messages",
            "staffing_email_queue" => "stf_email_queue",
            "staffing_email_rule" => "stf_email_rules",
            "staffing_purchase_order" => "stf_purchase_orders",
            "staffing_freelancer_invoice" => "stf_freelancer_invoices",
            _ => entity + "s"
        };

    public static bool RequiresCompanyCode(string table)
        => !GlobalTables.Contains(table);

    // Generic create helper: insert JSON and return to_jsonb(table).
    public static async Task<string?> InsertRawJson(NpgsqlDataSource ds, string table, string companyCode, string payloadJson)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        if (RequiresCompanyCode(table))
        {
            cmd.CommandText = $"INSERT INTO {table}(company_code, payload) VALUES ($1, $2::jsonb) RETURNING to_jsonb({table})";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(payloadJson);
        }
        else
        {
            cmd.CommandText = $"INSERT INTO {table}(payload) VALUES ($1::jsonb) RETURNING to_jsonb({table})";
            cmd.Parameters.AddWithValue(payloadJson);
        }
        var obj = await cmd.ExecuteScalarAsync();
        return obj as string;
    }

    // Generic update helper: update by id/company_code and return to_jsonb(table).
    public static async Task<string?> UpdateRawJson(NpgsqlDataSource ds, string table, Guid id, string companyCode, string payloadJson)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        if (RequiresCompanyCode(table))
        {
            cmd.CommandText = $"UPDATE {table} SET payload=$1::jsonb, updated_at=now() WHERE id=$2 AND company_code=$3 RETURNING to_jsonb({table})";
            cmd.Parameters.AddWithValue(payloadJson);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(companyCode);
        }
        else
        {
            cmd.CommandText = $"UPDATE {table} SET payload=$1::jsonb, updated_at=now() WHERE id=$2 RETURNING to_jsonb({table})";
            cmd.Parameters.AddWithValue(payloadJson);
            cmd.Parameters.AddWithValue(id);
        }
        var obj = await cmd.ExecuteScalarAsync();
        return obj as string;
    }

    // Generic delete helper: delete by id/company_code.
    public static async Task<int> DeleteById(NpgsqlDataSource ds, string table, Guid id, string companyCode)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        if (RequiresCompanyCode(table))
        {
            cmd.CommandText = $"DELETE FROM {table} WHERE id=$1 AND company_code=$2";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(companyCode);
        }
        else
        {
            cmd.CommandText = $"DELETE FROM {table} WHERE id=$1";
            cmd.Parameters.AddWithValue(id);
        }
        return await cmd.ExecuteNonQueryAsync();
    }

    // Generic detail helper: supports extra scope SQL fragments.
    public static async Task<string?> GetDetailJson(NpgsqlDataSource ds, string table, Guid id, string companyCode, string extraSql, IList<object?> authArgs)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        if (RequiresCompanyCode(table))
        {
            cmd.CommandText = $"SELECT to_jsonb(t) FROM (SELECT * FROM {table} WHERE id=$1 AND company_code=$2" + extraSql + ") t";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(companyCode);
        }
        else
        {
            cmd.CommandText = $"SELECT to_jsonb(t) FROM (SELECT * FROM {table} WHERE id=$1" + extraSql + ") t";
            cmd.Parameters.AddWithValue(id);
        }
        foreach (var a in authArgs) cmd.Parameters.AddWithValue(a!);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    // Generic query helper: execute a prepared SQL and return each row as JSON.
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


