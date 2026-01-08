using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace Server.Modules;

public sealed class AgentAccountingRuleService
{
    private readonly NpgsqlDataSource _dataSource;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public AgentAccountingRuleService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public sealed record AccountingRule(
        Guid Id,
        string CompanyCode,
        string Title,
        string? Description,
        IReadOnlyList<string> Keywords,
        string? AccountCode,
        string? AccountName,
        string? Note,
        int Priority,
        bool IsActive,
        JsonObject? Options,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    public sealed record AccountingRuleInput(
        Guid? Id,
        string Title,
        string? Description,
        IReadOnlyList<string> Keywords,
        string? AccountCode,
        string? AccountName,
        string? Note,
        int Priority,
        bool IsActive,
        JsonObject? Options);

    public async Task<IReadOnlyList<AccountingRule>> ListAsync(string companyCode, bool includeInactive, CancellationToken ct)
    {
        var list = new List<AccountingRule>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = includeInactive
            ? @"SELECT id, company_code, title, description, keywords, account_code, account_name, note, priority, is_active, options, created_at, updated_at
                 FROM ai_accounting_rules
                 WHERE company_code=$1
                 ORDER BY is_active DESC, priority, updated_at DESC"
            : @"SELECT id, company_code, title, description, keywords, account_code, account_name, note, priority, is_active, options, created_at, updated_at
                 FROM ai_accounting_rules
                 WHERE company_code=$1 AND is_active = TRUE
                 ORDER BY priority, updated_at DESC";
        cmd.Parameters.AddWithValue(companyCode);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadRule(reader));
        }
        return list;
    }

    public async Task<AccountingRule?> GetAsync(string companyCode, Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, company_code, title, description, keywords, account_code, account_name, note, priority, is_active, options, created_at, updated_at
                              FROM ai_accounting_rules
                              WHERE company_code=$1 AND id=$2";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadRule(reader);
        }
        return null;
    }

    public async Task<AccountingRule> UpsertAsync(string companyCode, AccountingRuleInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Title))
            throw new ArgumentException("title 必填", nameof(input));

        var keywords = input.Keywords?.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();
        var optionsJson = input.Options is null ? null : JsonSerializer.Serialize(input.Options, JsonOptions);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        if (!input.Id.HasValue || input.Id.Value == Guid.Empty)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO ai_accounting_rules(company_code, title, description, keywords, account_code, account_name, note, priority, is_active, options, created_at, updated_at)
                                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,now(),now())
                                RETURNING id, company_code, title, description, keywords, account_code, account_name, note, priority, is_active, options, created_at, updated_at";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(input.Title.Trim());
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(input.Description) ? DBNull.Value : input.Description!.Trim());
            var keywordsParam = cmd.Parameters.Add("keywords", NpgsqlDbType.Array | NpgsqlDbType.Text);
            keywordsParam.Value = keywords.Length == 0 ? Array.Empty<string>() : keywords;
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(input.AccountCode) ? DBNull.Value : input.AccountCode!.Trim());
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(input.AccountName) ? DBNull.Value : input.AccountName!.Trim());
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(input.Note) ? DBNull.Value : input.Note!.Trim());
            cmd.Parameters.AddWithValue(input.Priority);
            cmd.Parameters.AddWithValue(input.IsActive);
            var optionsParam = cmd.Parameters.Add("options", NpgsqlDbType.Jsonb);
            optionsParam.Value = optionsJson is null ? DBNull.Value : optionsJson;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return ReadRule(reader);
            }
            throw new Exception("无法创建会计规则");
        }
        else
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE ai_accounting_rules
                                SET title=$3,
                                    description=$4,
                                    keywords=$5,
                                    account_code=$6,
                                    account_name=$7,
                                    note=$8,
                                    priority=$9,
                                    is_active=$10,
                                    options=$11,
                                    updated_at=now()
                                WHERE company_code=$1 AND id=$2
                                RETURNING id, company_code, title, description, keywords, account_code, account_name, note, priority, is_active, options, created_at, updated_at";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(input.Id.Value);
            cmd.Parameters.AddWithValue(input.Title.Trim());
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(input.Description) ? DBNull.Value : input.Description!.Trim());
            var keywordsParam = cmd.Parameters.Add("keywords", NpgsqlDbType.Array | NpgsqlDbType.Text);
            keywordsParam.Value = keywords.Length == 0 ? Array.Empty<string>() : keywords;
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(input.AccountCode) ? DBNull.Value : input.AccountCode!.Trim());
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(input.AccountName) ? DBNull.Value : input.AccountName!.Trim());
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(input.Note) ? DBNull.Value : input.Note!.Trim());
            cmd.Parameters.AddWithValue(input.Priority);
            cmd.Parameters.AddWithValue(input.IsActive);
            var optionsParam = cmd.Parameters.Add("options", NpgsqlDbType.Jsonb);
            optionsParam.Value = optionsJson is null ? DBNull.Value : optionsJson;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return ReadRule(reader);
            }
            throw new Exception("会计规则不存在或已删除");
        }
    }

    public async Task DeleteAsync(string companyCode, Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ai_accounting_rules WHERE company_code=$1 AND id=$2";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static AccountingRule ReadRule(NpgsqlDataReader reader)
    {
        var id = reader.GetGuid(0);
        var companyCode = reader.GetString(1);
        var title = reader.GetString(2);
        var description = reader.IsDBNull(3) ? null : reader.GetString(3);
        var keywords = reader.IsDBNull(4) ? Array.Empty<string>() : reader.GetFieldValue<string[]>(4);
        var accountCode = reader.IsDBNull(5) ? null : reader.GetString(5);
        var accountName = reader.IsDBNull(6) ? null : reader.GetString(6);
        var note = reader.IsDBNull(7) ? null : reader.GetString(7);
        var priority = reader.IsDBNull(8) ? 100 : reader.GetInt32(8);
        var isActive = !reader.IsDBNull(9) && reader.GetBoolean(9);
        JsonObject? options = null;
        if (!reader.IsDBNull(10))
        {
            try
            {
                options = JsonNode.Parse(reader.GetFieldValue<string>(10))?.AsObject();
            }
            catch
            {
                options = null;
            }
        }
        var createdAt = reader.IsDBNull(11)
            ? DateTimeOffset.UtcNow
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(11), DateTimeKind.Utc));
        var updatedAt = reader.IsDBNull(12)
            ? createdAt
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(12), DateTimeKind.Utc));

        return new AccountingRule(
            id,
            companyCode,
            title,
            description,
            keywords,
            string.IsNullOrWhiteSpace(accountCode) ? null : accountCode,
            string.IsNullOrWhiteSpace(accountName) ? null : accountName,
            string.IsNullOrWhiteSpace(note) ? null : note,
            priority,
            isActive,
            options,
            createdAt,
            updatedAt);
    }
}

