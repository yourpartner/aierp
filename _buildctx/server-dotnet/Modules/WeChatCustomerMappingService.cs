using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Modules;

/// <summary>
/// 微信用户与ERP客户关联服务
/// 支持自动匹配、手动关联和匹配确认
/// </summary>
public class WeChatCustomerMappingService
{
    private readonly ILogger<WeChatCustomerMappingService> _logger;
    private readonly NpgsqlDataSource _ds;

    public WeChatCustomerMappingService(
        ILogger<WeChatCustomerMappingService> logger,
        NpgsqlDataSource ds)
    {
        _logger = logger;
        _ds = ds;
    }

    /// <summary>
    /// 获取或创建用户映射
    /// </summary>
    public async Task<CustomerMappingInfo?> GetOrCreateMappingAsync(
        string companyCode,
        string userId,
        string? userName,
        string userType,
        CancellationToken ct)
    {
        // 首先尝试获取已有映射
        var existing = await GetMappingByUserIdAsync(companyCode, userId, userType, ct);
        if (existing != null)
        {
            // 更新最后消息时间
            await UpdateLastMessageTimeAsync(existing.Id, ct);
            return existing;
        }

        // 创建新映射
        var mapping = new CustomerMappingInfo
        {
            Id = Guid.NewGuid(),
            WeComUserId = userId,
            WeComName = userName,
            IsConfirmed = false
        };

        // 尝试自动匹配客户
        if (!string.IsNullOrEmpty(userName))
        {
            var (partnerCode, partnerName, confidence) = await TryAutoMatchCustomerAsync(companyCode, userName, ct);
            if (!string.IsNullOrEmpty(partnerCode))
            {
                mapping.PartnerCode = partnerCode;
                mapping.PartnerName = partnerName;
                mapping.IsConfirmed = confidence >= 0.9m; // 高置信度自动确认
                
                _logger.LogInformation(
                    "[CustomerMapping] Auto-matched {WeComName} to {PartnerCode} ({PartnerName}) with confidence {Confidence}",
                    userName, partnerCode, partnerName, confidence);
            }
        }

        // 保存映射
        await SaveMappingAsync(companyCode, userId, userType, mapping, ct);

        return mapping;
    }

    /// <summary>
    /// 根据用户ID获取映射
    /// </summary>
    public async Task<CustomerMappingInfo?> GetMappingByUserIdAsync(
        string companyCode,
        string userId,
        string userType,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        cmd.CommandText = userType == "external"
            ? """
              SELECT id, external_user_id, wecom_name, partner_code, partner_name, is_confirmed
              FROM wecom_customer_mappings
              WHERE company_code = $1 AND external_user_id = $2
              LIMIT 1
              """
            : """
              SELECT id, wecom_user_id, wecom_name, partner_code, partner_name, is_confirmed
              FROM wecom_customer_mappings
              WHERE company_code = $1 AND wecom_user_id = $2
              LIMIT 1
              """;
        
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new CustomerMappingInfo
            {
                Id = reader.GetGuid(0),
                WeComUserId = reader.IsDBNull(1) ? null : reader.GetString(1),
                WeComName = reader.IsDBNull(2) ? null : reader.GetString(2),
                PartnerCode = reader.IsDBNull(3) ? null : reader.GetString(3),
                PartnerName = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsConfirmed = reader.GetBoolean(5)
            };
        }

        return null;
    }

    /// <summary>
    /// 手动关联客户
    /// </summary>
    public async Task<bool> LinkCustomerAsync(
        string companyCode,
        Guid mappingId,
        string partnerCode,
        CancellationToken ct)
    {
        // 查找客户名称
        var partnerName = await GetPartnerNameAsync(companyCode, partnerCode, ct);
        if (string.IsNullOrEmpty(partnerName))
        {
            _logger.LogWarning("[CustomerMapping] Partner not found: {PartnerCode}", partnerCode);
            return false;
        }

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE wecom_customer_mappings
            SET partner_code = $2,
                partner_name = $3,
                mapping_type = 'manual',
                is_confirmed = true,
                confidence = 1.0,
                updated_at = now()
            WHERE id = $1 AND company_code = $4
            """;
        cmd.Parameters.AddWithValue(mappingId);
        cmd.Parameters.AddWithValue(partnerCode);
        cmd.Parameters.AddWithValue(partnerName);
        cmd.Parameters.AddWithValue(companyCode);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        
        if (rows > 0)
        {
            _logger.LogInformation("[CustomerMapping] Manually linked mapping {MappingId} to partner {PartnerCode}",
                mappingId, partnerCode);
        }

        return rows > 0;
    }

    /// <summary>
    /// 确认自动匹配的关联
    /// </summary>
    public async Task<bool> ConfirmMappingAsync(
        string companyCode,
        Guid mappingId,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE wecom_customer_mappings
            SET mapping_type = 'confirmed',
                is_confirmed = true,
                confidence = 1.0,
                updated_at = now()
            WHERE id = $1 AND company_code = $2
            """;
        cmd.Parameters.AddWithValue(mappingId);
        cmd.Parameters.AddWithValue(companyCode);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    /// <summary>
    /// 解除关联
    /// </summary>
    public async Task<bool> UnlinkCustomerAsync(
        string companyCode,
        Guid mappingId,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE wecom_customer_mappings
            SET partner_code = NULL,
                partner_name = NULL,
                partner_id = NULL,
                mapping_type = 'auto',
                is_confirmed = false,
                confidence = 0,
                updated_at = now()
            WHERE id = $1 AND company_code = $2
            """;
        cmd.Parameters.AddWithValue(mappingId);
        cmd.Parameters.AddWithValue(companyCode);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    /// <summary>
    /// 获取所有映射列表
    /// </summary>
    public async Task<List<CustomerMappingDetail>> GetMappingsAsync(
        string companyCode,
        bool? confirmedOnly,
        int limit,
        int offset,
        CancellationToken ct)
    {
        var mappings = new List<CustomerMappingDetail>();

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        var whereClause = confirmedOnly.HasValue
            ? confirmedOnly.Value ? "AND is_confirmed = true" : "AND is_confirmed = false"
            : "";

        cmd.CommandText = $"""
            SELECT id, wecom_user_id, external_user_id, wecom_name, wecom_avatar,
                   partner_code, partner_name, mapping_type, confidence, is_confirmed,
                   last_message_at, message_count, order_count, created_at
            FROM wecom_customer_mappings
            WHERE company_code = $1 {whereClause}
            ORDER BY last_message_at DESC NULLS LAST, created_at DESC
            LIMIT $2 OFFSET $3
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(limit);
        cmd.Parameters.AddWithValue(offset);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            mappings.Add(new CustomerMappingDetail
            {
                Id = reader.GetGuid(0),
                WeComUserId = reader.IsDBNull(1) ? null : reader.GetString(1),
                ExternalUserId = reader.IsDBNull(2) ? null : reader.GetString(2),
                WeComName = reader.IsDBNull(3) ? null : reader.GetString(3),
                WeComAvatar = reader.IsDBNull(4) ? null : reader.GetString(4),
                PartnerCode = reader.IsDBNull(5) ? null : reader.GetString(5),
                PartnerName = reader.IsDBNull(6) ? null : reader.GetString(6),
                MappingType = reader.IsDBNull(7) ? "auto" : reader.GetString(7),
                Confidence = reader.IsDBNull(8) ? 0 : reader.GetDecimal(8),
                IsConfirmed = reader.GetBoolean(9),
                LastMessageAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
                MessageCount = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                OrderCount = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                CreatedAt = reader.GetDateTime(13)
            });
        }

        return mappings;
    }

    /// <summary>
    /// 根据微信名称搜索可能匹配的客户
    /// </summary>
    public async Task<List<PartnerSuggestion>> SuggestPartnersAsync(
        string companyCode,
        string searchTerm,
        int limit,
        CancellationToken ct)
    {
        var suggestions = new List<PartnerSuggestion>();

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT payload->>'code' as code, 
                   payload->>'name' as name,
                   payload->>'nameKana' as name_kana,
                   similarity(payload->>'name', $2) as sim
            FROM businesspartners
            WHERE company_code = $1
              AND (payload->>'name' ILIKE '%' || $2 || '%' 
                   OR payload->>'nameKana' ILIKE '%' || $2 || '%'
                   OR payload->>'code' ILIKE '%' || $2 || '%')
              AND COALESCE(payload->>'status', 'active') = 'active'
            ORDER BY sim DESC, payload->>'name'
            LIMIT $3
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(searchTerm);
        cmd.Parameters.AddWithValue(limit);

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                suggestions.Add(new PartnerSuggestion
                {
                    Code = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    NameKana = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Similarity = reader.IsDBNull(3) ? 0 : reader.GetDecimal(3)
                });
            }
        }
        catch
        {
            // similarity 函数可能不存在，使用简单查询
            cmd.CommandText = """
                SELECT payload->>'code' as code, 
                       payload->>'name' as name,
                       payload->>'nameKana' as name_kana
                FROM businesspartners
                WHERE company_code = $1
                  AND (payload->>'name' ILIKE '%' || $2 || '%' 
                       OR payload->>'nameKana' ILIKE '%' || $2 || '%'
                       OR payload->>'code' ILIKE '%' || $2 || '%')
                  AND COALESCE(payload->>'status', 'active') = 'active'
                ORDER BY payload->>'name'
                LIMIT $3
                """;
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                suggestions.Add(new PartnerSuggestion
                {
                    Code = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    NameKana = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Similarity = 0
                });
            }
        }

        return suggestions;
    }

    /// <summary>
    /// 增加订单计数
    /// </summary>
    public async Task IncrementOrderCountAsync(Guid mappingId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE wecom_customer_mappings
            SET order_count = order_count + 1,
                updated_at = now()
            WHERE id = $1
            """;
        cmd.Parameters.AddWithValue(mappingId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    #region 私有方法

    private async Task<(string? PartnerCode, string? PartnerName, decimal Confidence)> TryAutoMatchCustomerAsync(
        string companyCode,
        string wecomName,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        // 1. 精确匹配
        cmd.CommandText = """
            SELECT payload->>'code' as code, payload->>'name' as name
            FROM businesspartners
            WHERE company_code = $1
              AND (payload->>'name' = $2 OR payload->>'code' = $2)
              AND COALESCE(payload->>'status', 'active') = 'active'
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(wecomName);

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                return (
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    1.0m
                );
            }
        }

        // 2. 包含匹配（微信名包含客户名或客户名包含微信名）
        cmd.Parameters.Clear();
        cmd.CommandText = """
            SELECT payload->>'code' as code, payload->>'name' as name,
                   CASE 
                       WHEN payload->>'name' = $2 THEN 1.0
                       WHEN payload->>'name' ILIKE $2 || '%' THEN 0.9
                       WHEN $2 ILIKE payload->>'name' || '%' THEN 0.85
                       WHEN payload->>'name' ILIKE '%' || $2 || '%' THEN 0.7
                       WHEN $2 ILIKE '%' || payload->>'name' || '%' THEN 0.65
                       ELSE 0.5
                   END as confidence
            FROM businesspartners
            WHERE company_code = $1
              AND (payload->>'name' ILIKE '%' || $2 || '%' 
                   OR $2 ILIKE '%' || payload->>'name' || '%')
              AND COALESCE(payload->>'status', 'active') = 'active'
            ORDER BY confidence DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(wecomName);

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                return (
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? 0 : reader.GetDecimal(2)
                );
            }
        }

        return (null, null, 0);
    }

    private async Task SaveMappingAsync(
        string companyCode,
        string userId,
        string userType,
        CustomerMappingInfo mapping,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        if (userType == "external")
        {
            cmd.CommandText = """
                INSERT INTO wecom_customer_mappings
                (id, company_code, external_user_id, wecom_name, partner_code, partner_name, 
                 mapping_type, confidence, is_confirmed, last_message_at, message_count)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, now(), 1)
                ON CONFLICT (company_code, external_user_id) 
                DO UPDATE SET wecom_name = EXCLUDED.wecom_name,
                              last_message_at = now(),
                              message_count = wecom_customer_mappings.message_count + 1
                """;
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO wecom_customer_mappings
                (id, company_code, wecom_user_id, wecom_name, partner_code, partner_name, 
                 mapping_type, confidence, is_confirmed, last_message_at, message_count)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, now(), 1)
                ON CONFLICT (company_code, wecom_user_id) 
                DO UPDATE SET wecom_name = EXCLUDED.wecom_name,
                              last_message_at = now(),
                              message_count = wecom_customer_mappings.message_count + 1
                """;
        }

        cmd.Parameters.AddWithValue(mapping.Id);
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(string.IsNullOrEmpty(mapping.WeComName) ? (object)DBNull.Value : mapping.WeComName);
        cmd.Parameters.AddWithValue(string.IsNullOrEmpty(mapping.PartnerCode) ? (object)DBNull.Value : mapping.PartnerCode);
        cmd.Parameters.AddWithValue(string.IsNullOrEmpty(mapping.PartnerName) ? (object)DBNull.Value : mapping.PartnerName);
        cmd.Parameters.AddWithValue(mapping.IsConfirmed ? "confirmed" : "auto");
        cmd.Parameters.AddWithValue(mapping.IsConfirmed ? 1.0m : 0m);
        cmd.Parameters.AddWithValue(mapping.IsConfirmed);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpdateLastMessageTimeAsync(Guid mappingId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE wecom_customer_mappings
            SET last_message_at = now(),
                message_count = message_count + 1
            WHERE id = $1
            """;
        cmd.Parameters.AddWithValue(mappingId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<string?> GetPartnerNameAsync(string companyCode, string partnerCode, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT payload->>'name' as name
            FROM businesspartners
            WHERE company_code = $1 AND payload->>'code' = $2
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(partnerCode);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    #endregion
}

#region 数据模型

public class CustomerMappingDetail
{
    public Guid Id { get; set; }
    public string? WeComUserId { get; set; }
    public string? ExternalUserId { get; set; }
    public string? WeComName { get; set; }
    public string? WeComAvatar { get; set; }
    public string? PartnerCode { get; set; }
    public string? PartnerName { get; set; }
    public string MappingType { get; set; } = "auto";
    public decimal Confidence { get; set; }
    public bool IsConfirmed { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public int MessageCount { get; set; }
    public int OrderCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PartnerSuggestion
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameKana { get; set; }
    public decimal Similarity { get; set; }
}

#endregion

