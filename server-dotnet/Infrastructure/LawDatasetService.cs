using System.Text.Json;
using Npgsql;

namespace Server.Infrastructure;

// Sample law dataset service (later replace with DB + automated ingestion)
public class LawDatasetService
{
    private readonly NpgsqlDataSource _ds;
    public LawDatasetService(NpgsqlDataSource ds) { _ds = ds; }

    private const string Version = "JP-EXAMPLE-2024-10";

    private static readonly Dictionary<string, List<(decimal min, decimal max, decimal rate)>> HealthTables = new()
    {
        { "東京都", new(){ (0m,630000m,0.0495m), (630000m, 99999999m, 0.0500m) } },
        { "大阪府", new(){ (0m,630000m,0.0497m), (630000m, 99999999m, 0.0502m) } },
    };

    private static readonly List<(decimal min, decimal max, decimal rate)> PensionTable = new(){ (0m,650000m,0.0915m), (650000m, 99999999m, 0.0920m) };

    private static readonly Dictionary<string, decimal> EmploymentTable = new(StringComparer.OrdinalIgnoreCase)
    {
        { "一般", 0.006m }, { "建設", 0.008m }, { "農林", 0.007m }
    };

    private static string GetString(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind==JsonValueKind.String ? (v.GetString() ?? "") : "";
    private static bool GetBool(JsonElement el, string key)
    {
        try{
            if (el.TryGetProperty(key, out var v))
            {
                if (v.ValueKind==JsonValueKind.True) return true;
                if (v.ValueKind==JsonValueKind.False) return false;
                if (v.ValueKind==JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) return b;
            }
        }catch{}
        return false;
    }
    private static decimal GetNumber(JsonElement el, string key)
    {
        try{
            if (el.TryGetProperty(key, out var v) && v.ValueKind==JsonValueKind.Number)
            {
                if (v.TryGetDecimal(out var d)) return d;
                return Convert.ToDecimal(v.GetDouble());
            }
        }catch{}
        return 0m;
    }

    private static DateTime FirstDay(DateTime d) => new DateTime(d.Year, d.Month, 1);

    public (decimal rate, string version, string note) GetHealthRate(JsonElement employee, JsonElement policy, DateTime month, decimal baseAmount)
    {
        // Prioritize values explicitly set in the policy
        string pref = "";
        try { if (policy.TryGetProperty("law", out var lw) && lw.ValueKind==JsonValueKind.Object) pref = GetString(lw, "prefecture"); } catch {}
        if (string.IsNullOrWhiteSpace(pref)) pref = GetString(employee, "companyPref");
        if (string.IsNullOrWhiteSpace(pref)) pref = "東京都";
        bool careEligible = false;
        try { if (policy.TryGetProperty("law", out var lw) && lw.ValueKind==JsonValueKind.Object) careEligible = GetBool(lw, "careEligible"); } catch {}
        if (!careEligible) careEligible = GetBool(employee, "careEligible");
        var key = careEligible ? ($"{pref}:care2") : pref;
        var baseAmt = baseAmount;
        var (ok, rate, ver, note) = QueryFromDb(companyCode: GetString(employee, "companyCode"), kind:"health", key: key, baseAmt, month);
        if (ok) return (rate, ver!, note!);
        if (!HealthTables.TryGetValue(pref, out var table)) table = HealthTables["東京都"];
        var row = table.FirstOrDefault(x => baseAmt >= x.min && baseAmt < x.max);
        var r = row.rate == 0 ? table[0].rate : row.rate;
        return (r, Version, $"{(careEligible? key: pref)} base={baseAmt}");
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
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            var sql = @"SELECT rate, version, note FROM law_rates
                        WHERE (company_code IS NULL OR company_code = $1)
                          AND kind = $2
                          AND ($3 IS NULL OR key = $3)
                          AND effective_from <= $4
                          AND (effective_to IS NULL OR effective_to >= $4)
                          AND ($5 IS NULL OR (min_amount IS NULL OR min_amount <= $5))
                          AND ($5 IS NULL OR (max_amount IS NULL OR $5 < max_amount))
                        ORDER BY company_code NULLS FIRST, effective_from DESC
                        LIMIT 1";
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue((object?)companyCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue(kind);
            cmd.Parameters.AddWithValue((object?)key ?? DBNull.Value);
            cmd.Parameters.AddWithValue(FirstDay(month));
            if (baseAmt.HasValue) cmd.Parameters.AddWithValue(baseAmt.Value); else cmd.Parameters.AddWithValue(DBNull.Value);
            using var rd = cmd.ExecuteReader();
            if (rd.Read())
            {
                var rate = rd.GetDecimal(0);
                var ver = rd.IsDBNull(1) ? Version : rd.GetString(1);
                var note = rd.IsDBNull(2) ? string.Empty : rd.GetString(2);
                return (true, rate, ver, note);
            }
        }
        catch { }
        return (false, 0m, null, null);
    }
}


