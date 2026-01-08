using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using System.Linq;
using Npgsql;

namespace Server.Modules;

public class FinancialStatementService
{
    private readonly NpgsqlDataSource _ds;

    public FinancialStatementService(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    #region Materialized View

    public async Task RefreshGlMonthlyAsync(bool concurrently, CancellationToken cancellationToken = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(cancellationToken);
        var sql = concurrently
            ? "REFRESH MATERIALIZED VIEW CONCURRENTLY mv_gl_monthly"
            : "REFRESH MATERIALIZED VIEW mv_gl_monthly";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    #endregion

    #region Statement Queries

    public async Task<FinancialStatementResult> GetBalanceSheetAsync(
        string companyCode,
        DateOnly period,
        string currency,
        bool refreshBeforeQuery,
        CancellationToken cancellationToken = default)
    {
        if (refreshBeforeQuery)
            await RefreshGlMonthlyAsync(concurrently: true, cancellationToken);

        var nodes = await LoadNodeDefinitionsAsync(companyCode, "BS", cancellationToken);
        var amounts = await QueryGroupAmountsAsync(companyCode, currency, period, default, "BS", cancellationToken);
        var tree = BuildTree("BS", nodes, amounts);
        return new FinancialStatementResult(
            "BS",
            currency,
            period,
            period,
            tree
        );
    }

    public async Task<FinancialStatementResult> GetIncomeStatementAsync(
        string companyCode,
        DateOnly from,
        DateOnly to,
        string currency,
        bool refreshBeforeQuery,
        CancellationToken cancellationToken = default)
    {
        if (refreshBeforeQuery)
            await RefreshGlMonthlyAsync(concurrently: true, cancellationToken);

        var nodes = await LoadNodeDefinitionsAsync(companyCode, "PL", cancellationToken);
        var amounts = await QueryGroupAmountsAsync(companyCode, currency, to, from, "PL", cancellationToken);
        var tree = BuildTree("PL", nodes, amounts);
        return new FinancialStatementResult(
            "PL",
            currency,
            from,
            to,
            tree
        );
    }

    private async Task<List<FsNodeDefinition>> LoadNodeDefinitionsAsync(string companyCode, string statement, CancellationToken cancellationToken)
    {
        var list = new List<FsNodeDefinition>();
        await using var conn = await _ds.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, payload FROM fs_nodes WHERE company_code=$1 AND statement=$2 ORDER BY sort_order NULLS LAST, node_code";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(statement);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            var json = reader.GetString(1);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string GetString(string name, string fallback = "")
                => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                    ? (p.GetString() ?? fallback)
                    : fallback;
            string? GetNullableString(string name)
                => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString()
                    : null;
            int? GetNullableInt(string name)
                => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.TryGetInt32(out var val) ? val : (int?)null
                    : null;
            bool GetBool(string name)
                => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True
                    || (root.TryGetProperty(name, out var p2) && p2.ValueKind == JsonValueKind.String && p2.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true);

            var def = new FsNodeDefinition(
                id,
                statement,
                GetString("code"),
                GetString("nameJa", GetString("name", "")),
                GetNullableString("nameEn"),
                GetNullableString("parentCode"),
                GetNullableInt("order"),
                GetBool("isSubtotal"),
                GetNullableString("note")
            );
            if (!string.IsNullOrWhiteSpace(def.Code))
                list.Add(def with { Code = def.Code.Trim() });
        }
        return list;
    }

    private async Task<Dictionary<string, decimal>> QueryGroupAmountsAsync(
        string companyCode,
        string currency,
        DateOnly periodEnd,
        DateOnly periodStart,
        string statement,
        CancellationToken cancellationToken)
    {
        await using var conn = await _ds.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();

        var column = statement == "BS" ? "fs_bs_group" : "fs_pl_group";
        var sql = new StringBuilder();
        sql.Append("SELECT COALESCE(" + column + ", '') AS grp, SUM(m.net_amount) FROM mv_gl_monthly m JOIN accounts a ON a.company_code=m.company_code AND a.account_code=m.account_code WHERE m.company_code=$1 AND m.currency=$2");
        if (statement == "BS")
        {
            sql.Append(" AND m.period_month <= $3");
        }
        else
        {
            sql.Append(" AND m.period_month >= $3 AND m.period_month <= $4");
        }
        sql.Append(" GROUP BY 1");
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(currency);
        var periodEndDate = new DateTime(periodEnd.Year, periodEnd.Month, 1);
        if (statement == "BS")
        {
            cmd.Parameters.AddWithValue(periodEndDate);
        }
        else
        {
            var startDate = new DateTime(periodStart.Year, periodStart.Month, 1);
            cmd.Parameters.AddWithValue(startDate);
            cmd.Parameters.AddWithValue(periodEndDate);
        }

        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (reader.IsDBNull(1)) continue;
            var amount = reader.GetDecimal(1);
            if (!string.IsNullOrWhiteSpace(key))
            {
                map[key] = amount;
            }
            else
            {
                map["__UNASSIGNED_TOTAL__"] = map.TryGetValue("__UNASSIGNED_TOTAL__", out var existing) ? existing + amount : amount;
            }
        }
        return map;
    }

    private List<FinancialStatementNode> BuildTree(string statement, List<FsNodeDefinition> definitions, Dictionary<string, decimal> amountMap)
    {
        var defDict = definitions.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        var childrenMap = new Dictionary<string, List<FsNodeDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in definitions)
        {
            if (!string.IsNullOrWhiteSpace(def.ParentCode) && defDict.ContainsKey(def.ParentCode))
            {
                if (!childrenMap.TryGetValue(def.ParentCode, out var list))
                {
                    list = new List<FsNodeDefinition>();
                    childrenMap[def.ParentCode] = list;
                }
                list.Add(def);
            }
        }

        List<FsNodeDefinition> GetChildren(string code)
            => childrenMap.TryGetValue(code, out var list) ? list : new List<FsNodeDefinition>();

        FinancialStatementNode BuildNode(FsNodeDefinition def, int level)
        {
            var node = new FinancialStatementNode
            {
                Code = def.Code,
                Statement = statement,
                NameJa = string.IsNullOrWhiteSpace(def.NameJa) ? def.Code : def.NameJa,
                NameEn = def.NameEn,
                IsSubtotal = def.IsSubtotal,
                Note = def.Note,
                Level = level,
                Order = def.Order
            };
            var selfAmount = amountMap.TryGetValue(def.Code, out var val) ? val : 0m;
            node.SelfAmount = selfAmount;
            var children = GetChildren(def.Code)
                .Select(child => BuildNode(child, level + 1))
                .OrderBy(c => c.Order ?? int.MaxValue)
                .ThenBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
                .ToList();
            node.Children.AddRange(children);
            var childrenAmount = children.Sum(c => c.Amount);
            node.Amount = (def.IsSubtotal ? 0m : selfAmount) + childrenAmount;
            
            // 日本会計準則対応：「〇〇計」行（isSubtotal=true かつ子なし）は
            // 同じ親の兄弟ノード（非isSubtotal）の合計を表示する
            // 例：「流動資産計」は「現金及び預金」+「受取手形及び売掛金」+「商品」の合計
            foreach (var child in children)
            {
                if (child.IsSubtotal && child.Children.Count == 0 && child.Amount == 0)
                {
                    // この子ノードは「〇〇計」行：兄弟ノードの合計を計算
                    var siblingsTotal = children
                        .Where(c => !c.IsSubtotal) // 非isSubtotalの兄弟のみ
                        .Sum(c => c.Amount);
                    child.Amount = siblingsTotal;
                    child.SelfAmount = siblingsTotal;
                }
            }
            
            return node;
        }

        var roots = definitions
            .Where(d => string.IsNullOrWhiteSpace(d.ParentCode) || !defDict.ContainsKey(d.ParentCode))
            .Select(d => BuildNode(d, 0))
            .OrderBy(n => n.Order ?? int.MaxValue)
            .ThenBy(n => n.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Fallback bucket for accounts not assigned to any node.
        if (amountMap.TryGetValue("__UNASSIGNED_TOTAL__", out var unassignedTotal) && Math.Abs(unassignedTotal) > 0)
        {
            var orphanNode = new FinancialStatementNode
            {
                Code = "__UNASSIGNED__",
                Statement = statement,
                NameJa = "未分配",
                NameEn = "Unassigned",
                Level = 0,
                IsSubtotal = false,
                SelfAmount = unassignedTotal,
                Amount = unassignedTotal
            };
            roots.Add(orphanNode);
        }

        return roots;
    }

    #endregion

    #region Node Maintenance

    public async Task<IReadOnlyList<FsNodeDto>> ListNodesAsync(string companyCode, string? statement, CancellationToken cancellationToken = default)
    {
        var list = new List<FsNodeDto>();
        await using var conn = await _ds.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(statement))
        {
            cmd.CommandText = "SELECT id, payload FROM fs_nodes WHERE company_code=$1 ORDER BY statement, sort_order NULLS LAST, node_code";
            cmd.Parameters.AddWithValue(companyCode);
        }
        else
        {
            cmd.CommandText = "SELECT id, payload FROM fs_nodes WHERE company_code=$1 AND statement=$2 ORDER BY sort_order NULLS LAST, node_code";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(statement);
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            var json = reader.GetString(1);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            list.Add(ParseDto(id, root));
        }
        return list;
    }

    public async Task<FsNodeDto> CreateNodeAsync(string companyCode, FsNodeInput input, CancellationToken cancellationToken = default)
    {
        ValidateInput(input);
        var payload = BuildPayload(input);
        await using var conn = await _ds.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO fs_nodes(company_code, payload) VALUES ($1, $2::jsonb) RETURNING id, payload";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(payload.ToJsonString());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new Exception("insert failed");
        var id = reader.GetGuid(0);
        using var doc = JsonDocument.Parse(reader.GetString(1));
        return ParseDto(id, doc.RootElement);
    }

    public async Task<FsNodeDto> UpdateNodeAsync(string companyCode, Guid id, FsNodeInput input, CancellationToken cancellationToken = default)
    {
        ValidateInput(input);
        var payload = BuildPayload(input);
        await using var conn = await _ds.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE fs_nodes SET payload=$1::jsonb, updated_at=now() WHERE company_code=$2 AND id=$3 RETURNING payload";
        cmd.Parameters.AddWithValue(payload.ToJsonString());
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(id);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is null) throw new Exception("not found");
        using var doc = JsonDocument.Parse((string)result);
        return ParseDto(id, doc.RootElement);
    }

    public async Task DeleteNodeAsync(string companyCode, Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM fs_nodes WHERE company_code=$1 AND id=$2";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateInput(FsNodeInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Statement) || !(input.Statement.Equals("BS", StringComparison.OrdinalIgnoreCase) || input.Statement.Equals("PL", StringComparison.OrdinalIgnoreCase)))
            throw new Exception("statement must be BS or PL");
        if (string.IsNullOrWhiteSpace(input.Code))
            throw new Exception("code required");
        if (string.IsNullOrWhiteSpace(input.NameJa) && string.IsNullOrWhiteSpace(input.NameEn))
            throw new Exception("name required");
        if (!string.IsNullOrWhiteSpace(input.ParentCode) && string.Equals(input.ParentCode, input.Code, StringComparison.OrdinalIgnoreCase))
            throw new Exception("parentCode cannot equal code");
    }

    private static JsonObject BuildPayload(FsNodeInput input)
    {
        var obj = new JsonObject
        {
            ["statement"] = JsonValue.Create(input.Statement.ToUpperInvariant())
        };
        obj["code"] = JsonValue.Create(input.Code.Trim());
        if (!string.IsNullOrWhiteSpace(input.NameJa)) obj["nameJa"] = JsonValue.Create(input.NameJa.Trim());
        if (!string.IsNullOrWhiteSpace(input.NameEn)) obj["nameEn"] = JsonValue.Create(input.NameEn.Trim());
        if (!string.IsNullOrWhiteSpace(input.ParentCode)) obj["parentCode"] = JsonValue.Create(input.ParentCode.Trim());
        if (input.Order.HasValue) obj["order"] = JsonValue.Create(input.Order.Value);
        if (input.IsSubtotal) obj["isSubtotal"] = JsonValue.Create(true);
        if (!string.IsNullOrWhiteSpace(input.Note)) obj["note"] = JsonValue.Create(input.Note.Trim());
        return obj;
    }

    public async Task SeedDefaultTemplateAsync(string companyCode, CancellationToken cancellationToken = default)
    {
        var existing = await ListNodesAsync(companyCode, statement: null, cancellationToken);
        if (existing.Count > 0) return;

        var template = BuildJapaneseTemplate();
        await using var conn = await _ds.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var node in template)
            {
                var payload = BuildPayload(node);
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO fs_nodes(company_code, payload) VALUES ($1, $2::jsonb)";
                cmd.Parameters.AddWithValue(companyCode);
                cmd.Parameters.AddWithValue(payload.ToJsonString());
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static IReadOnlyList<FsNodeInput> BuildJapaneseTemplate()
    {
        var nodes = new List<FsNodeInput>
        {
            new FsNodeInput { Statement="BS", Code="BS-A", NameJa="資産の部", Order=10, IsSubtotal=true },
            new FsNodeInput { Statement="BS", Code="BS-A-1", NameJa="流動資産", ParentCode="BS-A", Order=11, IsSubtotal=true },
            new FsNodeInput { Statement="BS", Code="BS-A-1-1", NameJa="現金及び預金", ParentCode="BS-A-1", Order=111 },
            new FsNodeInput { Statement="BS", Code="BS-A-1-2", NameJa="受取手形及び売掛金", ParentCode="BS-A-1", Order=112 },
            new FsNodeInput { Statement="BS", Code="BS-A-1-3", NameJa="商品", ParentCode="BS-A-1", Order=113 },
            new FsNodeInput { Statement="BS", Code="BS-A-1-9", NameJa="流動資産計", ParentCode="BS-A-1", Order=119, IsSubtotal=true },
            new FsNodeInput { Statement="BS", Code="BS-A-2", NameJa="固定資産", ParentCode="BS-A", Order=12, IsSubtotal=true },
            new FsNodeInput { Statement="BS", Code="BS-A-2-1", NameJa="有形固定資産", ParentCode="BS-A-2", Order=121, IsSubtotal=true },
            new FsNodeInput { Statement="BS", Code="BS-A-2-1-1", NameJa="建物及び構築物", ParentCode="BS-A-2-1", Order=1211 },
            new FsNodeInput { Statement="BS", Code="BS-A-2-1-2", NameJa="機械装置", ParentCode="BS-A-2-1", Order=1212 },
            new FsNodeInput { Statement="BS", Code="BS-A-2-1-9", NameJa="有形固定資産計", ParentCode="BS-A-2-1", Order=1219, IsSubtotal=true },
            new FsNodeInput { Statement="BS", Code="BS-A-2-9", NameJa="固定資産計", ParentCode="BS-A-2", Order=129, IsSubtotal=true },
            new FsNodeInput { Statement="BS", Code="BS-A-9", NameJa="資産合計", ParentCode="BS-A", Order=19, IsSubtotal=true },

            new FsNodeInput { Statement="BS", Code="BS-L", NameJa="負債の部", Order=20, IsSubtotal=true },
            new FsNodeInput { Statement="BS", Code="BS-L-1", NameJa="流動負債", ParentCode="BS-L", Order=21, IsSubtotal=true },
            new FsNodeInput { Statement="BS", Code="BS-L-1-1", NameJa="支払手形及び買掛金", ParentCode="BS-L-1", Order=211 },
            new FsNodeInput { Statement="BS", Code="BS-L-1-2", NameJa="短期借入金", ParentCode="BS-L-1", Order=212 },
            new FsNodeInput { Statement="BS", Code="BS-L-1-9", NameJa="流動負債計", ParentCode="BS-L-1", Order=219, IsSubtotal=true },
            new FsNodeInput { Statement="BS", Code="BS-L-2", NameJa="固定負債", ParentCode="BS-L", Order=22, IsSubtotal=true },
            new FsNodeInput { Statement="BS", Code="BS-L-2-1", NameJa="社債", ParentCode="BS-L-2", Order=221 },
            new FsNodeInput { Statement="BS", Code="BS-L-2-2", NameJa="長期借入金", ParentCode="BS-L-2", Order=222 },
            new FsNodeInput { Statement="BS", Code="BS-L-2-9", NameJa="固定負債計", ParentCode="BS-L-2", Order=229, IsSubtotal=true },
            new FsNodeInput { Statement="BS", Code="BS-L-9", NameJa="負債合計", ParentCode="BS-L", Order=29, IsSubtotal=true },

            new FsNodeInput { Statement="BS", Code="BS-N", NameJa="純資産の部", Order=30, IsSubtotal=true },
            new FsNodeInput { Statement="BS", Code="BS-N-1", NameJa="資本金", ParentCode="BS-N", Order=31 },
            new FsNodeInput { Statement="BS", Code="BS-N-2", NameJa="資本準備金", ParentCode="BS-N", Order=32 },
            new FsNodeInput { Statement="BS", Code="BS-N-3", NameJa="利益剰余金", ParentCode="BS-N", Order=33, IsSubtotal=true },
            new FsNodeInput { Statement="BS", Code="BS-N-3-1", NameJa="その他利益剰余金", ParentCode="BS-N-3", Order=331 },
            new FsNodeInput { Statement="BS", Code="BS-N-3-9", NameJa="利益剰余金計", ParentCode="BS-N-3", Order=339, IsSubtotal=true },
            new FsNodeInput { Statement="BS", Code="BS-N-9", NameJa="純資産合計", ParentCode="BS-N", Order=39, IsSubtotal=true },
            new FsNodeInput { Statement="BS", Code="BS-T", NameJa="負債純資産合計", Order=40, IsSubtotal=true },

            new FsNodeInput { Statement="PL", Code="PL-1", NameJa="売上高", Order=10 },
            new FsNodeInput { Statement="PL", Code="PL-2", NameJa="売上原価", Order=20 },
            new FsNodeInput { Statement="PL", Code="PL-9-1", NameJa="売上総利益", Order=30, IsSubtotal=true },
            new FsNodeInput { Statement="PL", Code="PL-3", NameJa="販売費及び一般管理費", Order=40 },
            new FsNodeInput { Statement="PL", Code="PL-9-2", NameJa="営業利益", Order=50, IsSubtotal=true },
            new FsNodeInput { Statement="PL", Code="PL-4", NameJa="営業外収益", Order=60 },
            new FsNodeInput { Statement="PL", Code="PL-5", NameJa="営業外費用", Order=70 },
            new FsNodeInput { Statement="PL", Code="PL-9-3", NameJa="経常利益", Order=80, IsSubtotal=true },
            new FsNodeInput { Statement="PL", Code="PL-6", NameJa="特別利益", Order=90 },
            new FsNodeInput { Statement="PL", Code="PL-7", NameJa="特別損失", Order=100 },
            new FsNodeInput { Statement="PL", Code="PL-9-4", NameJa="税引前当期純利益", Order=110, IsSubtotal=true },
            new FsNodeInput { Statement="PL", Code="PL-8", NameJa="法人税等", Order=120 },
            new FsNodeInput { Statement="PL", Code="PL-9-5", NameJa="当期純利益", Order=130, IsSubtotal=true }
        };
        return nodes;
    }

    private static FsNodeDto ParseDto(Guid id, JsonElement root)
    {
        string GetString(string name, string fallback = "")
            => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? fallback) : fallback;
        string? GetNullableString(string name)
            => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        int? GetNullableInt(string name)
            => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var val) ? val : (int?)null;
        bool GetBool(string name)
            => root.TryGetProperty(name, out var p) && (p.ValueKind == JsonValueKind.True || (p.ValueKind == JsonValueKind.String && string.Equals(p.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

        return new FsNodeDto
        {
            Id = id,
            Statement = GetString("statement", "BS"),
            Code = GetString("code"),
            NameJa = GetString("nameJa", GetString("name", string.Empty)),
            NameEn = GetNullableString("nameEn"),
            ParentCode = GetNullableString("parentCode"),
            Order = GetNullableInt("order"),
            IsSubtotal = GetBool("isSubtotal"),
            Note = GetNullableString("note")
        };
    }

    #endregion

    #region Helper Records

    private record FsNodeDefinition(Guid Id, string Statement, string Code, string NameJa, string? NameEn, string? ParentCode, int? Order, bool IsSubtotal, string? Note);

    public record FsNodeInput
    {
        public string Statement { get; set; } = "BS";
        public string Code { get; set; } = string.Empty;
        public string NameJa { get; set; } = string.Empty;
        public string? NameEn { get; set; }
        public string? ParentCode { get; set; }
        public int? Order { get; set; }
        public bool IsSubtotal { get; set; }
        public string? Note { get; set; }
    }

    public record FsNodeDto
    {
        public Guid Id { get; set; }
        public string Statement { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string NameJa { get; set; } = string.Empty;
        public string? NameEn { get; set; }
        public string? ParentCode { get; set; }
        public int? Order { get; set; }
        public bool IsSubtotal { get; set; }
        public string? Note { get; set; }
    }

    public record FinancialStatementResult(
        string Statement,
        string Currency,
        DateOnly PeriodStart,
        DateOnly PeriodEnd,
        IReadOnlyList<FinancialStatementNode> Nodes
    );

    public class FinancialStatementNode
    {
        public string Statement { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string NameJa { get; set; } = string.Empty;
        public string? NameEn { get; set; }
        public decimal Amount { get; set; }
        public decimal SelfAmount { get; set; }
        public bool IsSubtotal { get; set; }
        public string? Note { get; set; }
        public int Level { get; set; }
        public int? Order { get; set; }
        public List<FinancialStatementNode> Children { get; } = new();
    }

    #endregion
}


