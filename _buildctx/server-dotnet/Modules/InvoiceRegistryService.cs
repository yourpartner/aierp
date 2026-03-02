using System.Text.RegularExpressions;
using Npgsql;

namespace Server.Modules;

public class InvoiceRegistryService
{
    private static readonly Regex Pattern = new("^T\\d{13}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly NpgsqlDataSource _ds;

    public InvoiceRegistryService(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public static string Normalize(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim().ToUpperInvariant();

    public static bool IsFormatValid(string? regNo)
        => !string.IsNullOrWhiteSpace(regNo) && Pattern.IsMatch(regNo.Trim().ToUpperInvariant());

    public static string StatusKey(InvoiceVerificationStatus status) => status switch
    {
        InvoiceVerificationStatus.Matched => "matched",
        InvoiceVerificationStatus.NotFound => "not_found",
        InvoiceVerificationStatus.Inactive => "inactive",
        InvoiceVerificationStatus.Expired => "expired",
        _ => status.ToString().ToLowerInvariant()
    };

    public async Task<InvoiceVerificationResult> VerifyAsync(string regNo)
    {
        var normalized = Normalize(regNo);
        if (!IsFormatValid(normalized))
            throw new ArgumentException("invalid invoice registration number", nameof(regNo));

        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT registration_no, name, name_kana, effective_from, effective_to
                              FROM invoice_issuers
                              WHERE registration_no=$1
                              LIMIT 1";
        cmd.Parameters.AddWithValue(normalized);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var name = reader.IsDBNull(1) ? null : reader.GetString(1);
            var nameKana = reader.IsDBNull(2) ? null : reader.GetString(2);
            var effectiveFrom = reader.IsDBNull(3) ? (DateOnly?)null : DateOnly.FromDateTime(reader.GetDateTime(3));
            var effectiveTo = reader.IsDBNull(4) ? (DateOnly?)null : DateOnly.FromDateTime(reader.GetDateTime(4));

            var today = DateTime.UtcNow.Date;
            var status = InvoiceVerificationStatus.Matched;
            if (effectiveTo.HasValue && effectiveTo.Value.ToDateTime(TimeOnly.MinValue) < today)
                status = InvoiceVerificationStatus.Expired;
            else if (effectiveFrom.HasValue && effectiveFrom.Value.ToDateTime(TimeOnly.MinValue) > today)
                status = InvoiceVerificationStatus.Inactive;

            return new InvoiceVerificationResult(normalized, status, name, nameKana, effectiveFrom, effectiveTo, DateTimeOffset.UtcNow);
        }

        return new InvoiceVerificationResult(normalized, InvoiceVerificationStatus.NotFound, null, null, null, null, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 根据公司名称和地址搜索匹配的インボイス登録番号
    /// </summary>
    /// <param name="companyName">公司名称（取引先名）</param>
    /// <param name="address">地址（用于二次匹配提高精度）</param>
    /// <param name="limit">返回结果数量限制</param>
    /// <returns>匹配的登録番号列表</returns>
    public async Task<List<InvoiceIssuerMatch>> SearchByNameAsync(string companyName, string? address = null, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(companyName))
            return new List<InvoiceIssuerMatch>();

        var results = new List<InvoiceIssuerMatch>();
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        
        // 搜索策略：
        // 1. 完全匹配优先
        // 2. 名称包含关键词
        // 3. 假名包含关键词
        // 4. payload 中的地址信息也参与匹配
        cmd.CommandText = @"
            SELECT registration_no, name, name_kana, effective_from, effective_to,
                   payload->>'address' as address,
                   payload->>'prefecture' as prefecture,
                   payload->>'city' as city,
                   CASE 
                       WHEN name = $1 THEN 100
                       WHEN name ILIKE $1 || '%' THEN 90
                       WHEN name ILIKE '%' || $1 || '%' THEN 80
                       WHEN name_kana ILIKE $1 || '%' THEN 70
                       WHEN name_kana ILIKE '%' || $1 || '%' THEN 60
                       ELSE 50
                   END as match_score
            FROM invoice_issuers
            WHERE (name ILIKE '%' || $1 || '%' OR name_kana ILIKE '%' || $1 || '%')
              AND (effective_to IS NULL OR effective_to >= CURRENT_DATE)
            ORDER BY match_score DESC, name ASC
            LIMIT $2";
        cmd.Parameters.AddWithValue(companyName.Trim());
        cmd.Parameters.AddWithValue(limit * 2); // 多获取一些用于地址过滤
        
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var regNo = reader.GetString(0);
            var name = reader.IsDBNull(1) ? null : reader.GetString(1);
            var nameKana = reader.IsDBNull(2) ? null : reader.GetString(2);
            var effectiveFrom = reader.IsDBNull(3) ? (DateOnly?)null : DateOnly.FromDateTime(reader.GetDateTime(3));
            var effectiveTo = reader.IsDBNull(4) ? (DateOnly?)null : DateOnly.FromDateTime(reader.GetDateTime(4));
            var dbAddress = reader.IsDBNull(5) ? null : reader.GetString(5);
            var prefecture = reader.IsDBNull(6) ? null : reader.GetString(6);
            var city = reader.IsDBNull(7) ? null : reader.GetString(7);
            var matchScore = reader.GetInt32(8);
            
            // 组合地址信息
            var fullAddress = string.Join("", new[] { prefecture, city, dbAddress }.Where(s => !string.IsNullOrWhiteSpace(s)));
            
            // 如果提供了地址参数，计算地址相似度并调整分数
            if (!string.IsNullOrWhiteSpace(address) && !string.IsNullOrWhiteSpace(fullAddress))
            {
                var addressScore = CalculateAddressSimilarity(address, fullAddress);
                matchScore = (int)(matchScore * 0.6 + addressScore * 0.4); // 名称60% + 地址40%
            }
            
            results.Add(new InvoiceIssuerMatch(regNo, name, nameKana, effectiveFrom, effectiveTo, matchScore, fullAddress));
        }
        
        // 按最终分数重新排序并限制数量
        return results
            .OrderByDescending(r => r.MatchScore)
            .ThenBy(r => r.Name)
            .Take(limit)
            .ToList();
    }
    
    /// <summary>
    /// 计算地址相似度（0-100）
    /// </summary>
    private static int CalculateAddressSimilarity(string addr1, string addr2)
    {
        if (string.IsNullOrWhiteSpace(addr1) || string.IsNullOrWhiteSpace(addr2))
            return 0;
        
        // 简单的字符级相似度计算
        var s1 = addr1.Replace(" ", "").Replace("　", "");
        var s2 = addr2.Replace(" ", "").Replace("　", "");
        
        // 完全匹配
        if (s1.Equals(s2, StringComparison.OrdinalIgnoreCase))
            return 100;
        
        // 包含关系
        if (s1.Contains(s2, StringComparison.OrdinalIgnoreCase) || s2.Contains(s1, StringComparison.OrdinalIgnoreCase))
            return 80;
        
        // 计算公共字符数
        var commonChars = s1.Intersect(s2).Count();
        var maxLen = Math.Max(s1.Length, s2.Length);
        if (maxLen == 0) return 0;
        
        return (int)(commonChars * 100.0 / maxLen);
    }
}

public enum InvoiceVerificationStatus
{
    Matched,
    NotFound,
    Inactive,
    Expired
}

public record InvoiceVerificationResult(
    string RegistrationNo,
    InvoiceVerificationStatus Status,
    string? Name,
    string? NameKana,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    DateTimeOffset CheckedAt
)
{
    public object ToResponse() => new
    {
        registrationNo = RegistrationNo,
        status = InvoiceRegistryService.StatusKey(Status),
        name = Name,
        nameKana = NameKana,
        effectiveFrom = EffectiveFrom?.ToString("yyyy-MM-dd"),
        effectiveTo = EffectiveTo?.ToString("yyyy-MM-dd"),
        checkedAt = CheckedAt.ToString("O")
    };
}

/// <summary>
/// インボイス発行事業者の検索結果
/// </summary>
public record InvoiceIssuerMatch(
    string RegistrationNo,
    string? Name,
    string? NameKana,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    int MatchScore,
    string? Address = null
)
{
    public object ToResponse() => new
    {
        registrationNo = RegistrationNo,
        name = Name,
        nameKana = NameKana,
        effectiveFrom = EffectiveFrom?.ToString("yyyy-MM-dd"),
        effectiveTo = EffectiveTo?.ToString("yyyy-MM-dd"),
        matchScore = MatchScore,
        address = Address
    };
}

