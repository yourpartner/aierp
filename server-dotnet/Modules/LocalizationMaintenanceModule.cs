using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Server.Modules;

public static class LocalizationMaintenanceModule
{
    public static void MapLocalizationMaintenanceModule(this WebApplication app)
    {
        // 批量将 schemas.ui 中的中文标签翻译为日语（最小集合，可按需扩展）
        app.MapPost("/maintenance/schemas/localize-ja", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });

            var dict = new Dictionary<string, string>
            {
                ["编码"] = "コード",
                ["名称"] = "名称",
                ["基本单位"] = "基本単位",
                ["批次管理"] = "ロット管理",
                ["规格型号"] = "仕様型番",
                ["描述"] = "説明",
                ["凭证号"] = "伝票番号",
                ["凭证类型"] = "伝票タイプ",
                ["过账日期"] = "記帳日",
                ["会计凭证"] = "会計伝票",
                ["工资凭证"] = "給与仕訳",
                ["科目"] = "勘定科目",
                ["部门"] = "部門",
                ["员工"] = "従業員",
                ["金额"] = "金額",
                ["借方"] = "借方",
                ["贷方"] = "貸方",
                ["状态"] = "ステータス",
                ["日期"] = "日付"
            };

            var updated = 0;
            await using var conn = await ds.OpenConnectionAsync();
            // 仅处理该公司（或全局 company_code IS NULL）
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, ui::text FROM schemas WHERE company_code IS NULL OR company_code=$1";
            cmd.Parameters.AddWithValue(cc.ToString());
            var rows = new List<(Guid id, string? ui)>();
            await using (var rd = await cmd.ExecuteReaderAsync())
            {
                while (await rd.ReadAsync()) rows.Add((rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetString(1)));
            }

            foreach (var (id, uiText) in rows)
            {
                if (string.IsNullOrWhiteSpace(uiText)) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(uiText); } catch { continue; }
                if (node is not JsonObject root) continue;

                var changed = TranslateLabels(root, dict);
                if (!changed) continue;

                await using var up = conn.CreateCommand();
                up.CommandText = "UPDATE schemas SET ui=$1::jsonb, updated_at=now() WHERE id=$2";
                up.Parameters.AddWithValue(root.ToJsonString());
                up.Parameters.AddWithValue(id);
                updated += await up.ExecuteNonQueryAsync();
            }

            return Results.Ok(new { updated });
        }).RequireAuthorization();
    }

    private static bool TranslateLabels(JsonObject root, IDictionary<string, string> dict)
    {
        bool changed = false;
        var form = root["form"] as JsonObject;
        var layout = form?["layout"] as JsonArray;
        if (layout is not null)
        {
            foreach (var section in layout.OfType<JsonObject>())
            {
                var cols = section["cols"] as JsonArray;
                if (cols is null) continue;
                foreach (var col in cols.OfType<JsonObject>())
                {
                    var label = col["label"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(label) && dict.TryGetValue(label!, out var ja))
                    {
                        col["label"] = ja;
                        changed = true;
                    }
                }
            }
        }
        // 可按需扩展: list.columnLabels, tabs 等
        return changed;
    }
}


