using System.Text.Json;
using Npgsql;

namespace Server.Infrastructure;

// Sample law dataset service (later replace with DB + automated ingestion)
public class LawDatasetService
{
    private readonly NpgsqlDataSource _ds;
    public LawDatasetService(NpgsqlDataSource ds) { _ds = ds; }

    private const string Version = "JP-EXAMPLE-2024-10";

    private static readonly List<(decimal min, decimal max, decimal rate)> PensionTable = new(){ (0m,650000m,0.0915m), (650000m, 99999999m, 0.0920m) };

    private static readonly Dictionary<string, decimal> EmploymentTable = new(StringComparer.OrdinalIgnoreCase)
    {
        { "一般", 0.006m }, { "建設", 0.008m }, { "農林", 0.007m }
    };

    private static string GetString(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind==JsonValueKind.String ? (v.GetString() ?? "") : "";
    private static DateTime FirstDay(DateTime d) => new DateTime(d.Year, d.Month, 1);

    /// <summary>
    /// 计算员工在指定月份的年龄（周岁），按“生日所在月”规则
    /// </summary>
    private static int? CalculateAge(JsonElement employee, DateTime month)
    {
        var birthDateStr = GetString(employee, "birthDate");
        if (string.IsNullOrWhiteSpace(birthDateStr)) return null;
        if (!DateTime.TryParse(birthDateStr, out var birthDate)) return null;

        // 以“月份”为单位判断年龄：生日当月视为已满岁
        var age = month.Year - birthDate.Year;
        if (month.Month < birthDate.Month)
            age--;
        return age;
    }

    /// <summary>
    /// 判断员工在指定月份是否需要缴纳介护保险（40岁~64岁）
    /// 日本法律：40岁生日当月开始缴纳，65岁生日当月停止（转为第一号被保险者）
    /// </summary>
    private bool IsCareInsuranceEligible(JsonElement employee, DateTime month)
    {
        var age = CalculateAge(employee, month);
        if (!age.HasValue) return false;
        return age.Value >= 40 && age.Value < 65;
    }

    /// <summary>
    /// 获取介护保险费率（仅介护部分，不含健康保险）
    /// 只有 40-64 岁的员工才需要缴纳介护保险
    /// </summary>
    public (decimal rate, string version, string note) GetCareInsuranceRate(JsonElement employee, JsonElement policy, DateTime month)
    {
        // 首先检查年龄：只有 40-64 岁需要缴纳介护保险
        var age = CalculateAge(employee, month);
        if (!age.HasValue || age.Value < 40 || age.Value >= 65)
        {
            return (0m, Version, $"care:not_eligible(age={age})");
        }

        string pref = "";
        try { if (policy.TryGetProperty("law", out var lw) && lw.ValueKind==JsonValueKind.Object) pref = GetString(lw, "prefecture"); } catch {}
        if (string.IsNullOrWhiteSpace(pref)) pref = GetString(employee, "companyPref");
        if (string.IsNullOrWhiteSpace(pref)) pref = "東京都";

        var key = $"{pref}:care";
        var (ok, rate, ver, note) = QueryFromDb(companyCode: GetString(employee, "companyCode"), kind: "care", key: key, baseAmt: null, month);
        if (ok) return (rate, ver!, $"{note} age={age}");
        return (0m, Version, $"{pref}:care missing_rate age={age}");
    }

    public (decimal rate, string version, string note) GetHealthRate(JsonElement employee, JsonElement policy, DateTime month, decimal baseAmount)
    {
        // Prioritize values explicitly set in the policy
        string pref = "";
        try { if (policy.TryGetProperty("law", out var lw) && lw.ValueKind==JsonValueKind.Object) pref = GetString(lw, "prefecture"); } catch {}
        if (string.IsNullOrWhiteSpace(pref)) pref = GetString(employee, "companyPref");
        if (string.IsNullOrWhiteSpace(pref)) pref = "東京都";

        var baseAmt = baseAmount;
        var companyCode = GetString(employee, "companyCode");

        // 只返回纯健康保险费率（不包含介护），介护保险单独计算
        var (ok, healthRate, ver, note) = QueryFromDb(companyCode: companyCode, kind:"health", key: pref, baseAmt, month);
        
        if (!ok)
        {
            return (0m, Version, $"{pref}:health missing_rate");
        }

        return (healthRate, ver ?? Version, $"{pref} base={baseAmt}");
    }

    public (decimal rate, string version, string note) GetPensionRate(JsonElement employee, JsonElement policy, DateTime month, decimal baseAmount)
    {
        var baseAmt = baseAmount;
        var (ok, rate, ver, note) = QueryFromDb(companyCode: GetString(employee, "companyCode"), kind:"pension", key: null, baseAmt, month);
        if (ok) return (rate, ver!, note!);
        // Fallback: ignore company range and pick latest entry in same month
        try
        {
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT rate, COALESCE(version,''), COALESCE(note,'')
                                FROM law_rates
                                WHERE kind='pension' AND effective_from <= $1 AND (effective_to IS NULL OR effective_to >= $1)
                                ORDER BY effective_from DESC LIMIT 1";
            cmd.Parameters.AddWithValue(FirstDay(month));
            using var rd2 = cmd.ExecuteReader();
            if (rd2.Read())
            {
                var rate2 = rd2.GetDecimal(0); var v = rd2.GetString(1); var n = rd2.GetString(2);
                return (rate2, string.IsNullOrWhiteSpace(v)? Version : v, string.IsNullOrWhiteSpace(n)? $"base={baseAmt}" : n);
            }
        }
        catch {}
        var row = PensionTable.FirstOrDefault(x => baseAmt >= x.min && baseAmt < x.max);
        var r = row.rate == 0 ? PensionTable[0].rate : row.rate;
        return (r, Version, $"base={baseAmt}");
    }

    public (decimal rate, string version, string note) GetEmploymentRate(JsonElement employee, JsonElement policy, DateTime month)
    {
        string cls = "";
        try { if (policy.TryGetProperty("law", out var lw) && lw.ValueKind==JsonValueKind.Object) cls = GetString(lw, "employmentIndustry"); } catch {}
        if (string.IsNullOrWhiteSpace(cls))
        {
            // Extract industry from policy text ("general"/"construction"/"agriculture")
            string polTxt = GetString(policy, "companyText"); if (string.IsNullOrWhiteSpace(polTxt)) polTxt = GetString(policy, "nlText"); if (string.IsNullOrWhiteSpace(polTxt)) polTxt = GetString(policy, "text");
            if (!string.IsNullOrWhiteSpace(polTxt))
            {
                if (polTxt.Contains("建設")) cls = "建設";
                else if (polTxt.Contains("農林") || polTxt.Contains("農林水産")) cls = "農林";
                else if (polTxt.Contains("一般")) cls = "一般";
            }
        }
        if (string.IsNullOrWhiteSpace(cls)) cls = GetString(employee, "companyClass");
        if (string.IsNullOrWhiteSpace(cls)) cls = "一般";
        var (ok, rate, ver, note) = QueryFromDb(companyCode: GetString(employee, "companyCode"), kind:"employment", key: cls, baseAmt: null, month);
        if (ok) return (rate, ver!, note!);
        // Secondary lookup ignoring company boundary, matching by key + period
        try
        {
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT rate, COALESCE(version,''), COALESCE(note,'')
                                FROM law_rates
                                WHERE kind='employment' AND key=$1 AND effective_from <= $2 AND (effective_to IS NULL OR effective_to >= $2)
                                ORDER BY effective_from DESC LIMIT 1";
            cmd.Parameters.AddWithValue(cls);
            cmd.Parameters.AddWithValue(FirstDay(month));
            using var rd = cmd.ExecuteReader();
            if (rd.Read())
            {
                var rate2 = rd.GetDecimal(0); var v = rd.GetString(1); var n = rd.GetString(2);
                return (rate2, string.IsNullOrWhiteSpace(v)? Version : v, string.IsNullOrWhiteSpace(n)? cls : n);
            }
        }
        catch {}
        var r = EmploymentTable.TryGetValue(cls, out var rr) ? rr : EmploymentTable["一般"];
        return (r, Version, cls);
    }

    private (bool ok, decimal rate, string? version, string? note) QueryFromDb(string? companyCode, string kind, string? key, decimal? baseAmt, DateTime month)
    {
        try
        {
            // 将空字符串视为 NULL
            if (string.IsNullOrWhiteSpace(companyCode)) companyCode = null;
            if (string.IsNullOrWhiteSpace(key)) key = null;
            
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            
            // 根据是否有 baseAmt 使用不同的 SQL 查询
            if (baseAmt.HasValue)
            {
                cmd.CommandText = @"SELECT rate, version, note FROM law_rates
                            WHERE (company_code IS NULL OR company_code = $1)
                              AND kind = $2
                              AND ($3::text IS NULL OR key = $3)
                              AND effective_from <= $4
                              AND (effective_to IS NULL OR effective_to >= $4)
                              AND (min_amount IS NULL OR min_amount <= $5)
                              AND (max_amount IS NULL OR $5 < max_amount)
                            ORDER BY company_code NULLS FIRST, effective_from DESC
                            LIMIT 1";
                cmd.Parameters.AddWithValue((object?)companyCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue(kind);
                cmd.Parameters.AddWithValue((object?)key ?? DBNull.Value);
                cmd.Parameters.AddWithValue(FirstDay(month));
                cmd.Parameters.AddWithValue(baseAmt.Value);
            }
            else
            {
                cmd.CommandText = @"SELECT rate, version, note FROM law_rates
                            WHERE (company_code IS NULL OR company_code = $1)
                              AND kind = $2
                              AND ($3::text IS NULL OR key = $3)
                              AND effective_from <= $4
                              AND (effective_to IS NULL OR effective_to >= $4)
                            ORDER BY company_code NULLS FIRST, effective_from DESC
                            LIMIT 1";
                cmd.Parameters.AddWithValue((object?)companyCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue(kind);
                cmd.Parameters.AddWithValue((object?)key ?? DBNull.Value);
                cmd.Parameters.AddWithValue(FirstDay(month));
            }
            
            using var rd = cmd.ExecuteReader();
            if (rd.Read())
            {
                var rate = rd.GetDecimal(0);
                var ver = rd.IsDBNull(1) ? Version : rd.GetString(1);
                var note = rd.IsDBNull(2) ? string.Empty : rd.GetString(2);
                return (true, rate, ver, note);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QueryFromDb] ERROR: {ex.Message}");
        }
        return (false, 0m, null, null);
    }
}


