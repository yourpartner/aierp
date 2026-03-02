using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Server.Modules;

/// <summary>
/// 月次締め（月結）API エンドポイント
/// </summary>
public static class MonthlyClosingModule
{
    public static void MapMonthlyClosingModule(this WebApplication app)
    {
        // ========================================
        // 月次締め一覧・詳細
        // ========================================

        // 月次締め一覧取得
        app.MapGet("/monthly-closing", async (HttpRequest req, MonthlyClosingService service) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var yearStr = req.Query["year"].FirstOrDefault();
            int? year = int.TryParse(yearStr, out var y) ? y : null;
            var limitStr = req.Query["limit"].FirstOrDefault();
            var limit = int.TryParse(limitStr, out var l) ? l : 24;

            var list = await service.ListAsync(cc!, year, limit);
            return Results.Json(list);
        }).RequireAuthorization();

        // 月次締め詳細取得
        app.MapGet("/monthly-closing/{yearMonth}", async (string yearMonth, HttpRequest req, MonthlyClosingService service) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            if (!ValidateYearMonth(yearMonth))
                return Results.BadRequest(new { error = "Invalid yearMonth format (YYYY-MM)" });

            var result = await service.GetAsync(cc!, yearMonth);
            return result is null ? Results.NotFound() : Results.Json(result);
        }).RequireAuthorization();

        // ========================================
        // 月次締め開始・チェック
        // ========================================

        // 月次締め開始
        app.MapPost("/monthly-closing/start", async (HttpRequest req, MonthlyClosingService service) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var yearMonth = root.TryGetProperty("yearMonth", out var ym) ? ym.GetString() : null;
            if (string.IsNullOrWhiteSpace(yearMonth) || !ValidateYearMonth(yearMonth))
                return Results.BadRequest(new { error = "yearMonth required (YYYY-MM)" });

            var startedBy = root.TryGetProperty("startedBy", out var sb) ? sb.GetString() : null;

            try
            {
                var result = await service.StartOrGetAsync(cc!, yearMonth, startedBy);
                return Results.Json(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // 全チェック実行
        app.MapPost("/monthly-closing/{yearMonth}/check", async (string yearMonth, HttpRequest req, MonthlyClosingService service) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            if (!ValidateYearMonth(yearMonth))
                return Results.BadRequest(new { error = "Invalid yearMonth format (YYYY-MM)" });

            string? checkedBy = null;
            if (req.ContentLength > 0)
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                if (doc.RootElement.TryGetProperty("checkedBy", out var cb))
                    checkedBy = cb.GetString();
            }

            try
            {
                var results = await service.RunAllChecksAsync(cc!, yearMonth, checkedBy);
                return Results.Json(new { ok = true, results });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // 単一チェック実行
        app.MapPost("/monthly-closing/{yearMonth}/check/{itemKey}", async (string yearMonth, string itemKey, HttpRequest req, MonthlyClosingService service) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            if (!ValidateYearMonth(yearMonth))
                return Results.BadRequest(new { error = "Invalid yearMonth format (YYYY-MM)" });

            string? checkedBy = null;
            if (req.ContentLength > 0)
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                if (doc.RootElement.TryGetProperty("checkedBy", out var cb))
                    checkedBy = cb.GetString();
            }

            try
            {
                var result = await service.RunCheckAsync(cc!, yearMonth, itemKey, checkedBy);
                return Results.Json(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // 手動チェック結果登録
        app.MapPut("/monthly-closing/{yearMonth}/check/{itemKey}", async (string yearMonth, string itemKey, HttpRequest req, MonthlyClosingService service) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            if (!ValidateYearMonth(yearMonth))
                return Results.BadRequest(new { error = "Invalid yearMonth format (YYYY-MM)" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var s) ? s.GetString() : "pending";
            var comment = root.TryGetProperty("comment", out var c) ? c.GetString() : null;
            var checkedBy = root.TryGetProperty("checkedBy", out var cb) ? cb.GetString() : null;

            try
            {
                var result = await service.SetManualCheckResultAsync(cc!, yearMonth, itemKey, status!, comment, checkedBy);
                return Results.Json(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // ========================================
        // 消費税集計
        // ========================================

        app.MapPost("/monthly-closing/{yearMonth}/tax-summary", async (string yearMonth, HttpRequest req, MonthlyClosingService service) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            if (!ValidateYearMonth(yearMonth))
                return Results.BadRequest(new { error = "Invalid yearMonth format (YYYY-MM)" });

            try
            {
                var result = await service.CalculateTaxSummaryAsync(cc!, yearMonth);
                return Results.Json(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // ========================================
        // 承認・締め確定
        // ========================================

        // 承認申請
        app.MapPost("/monthly-closing/{yearMonth}/submit-approval", async (string yearMonth, HttpRequest req, MonthlyClosingService service) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            if (!ValidateYearMonth(yearMonth))
                return Results.BadRequest(new { error = "Invalid yearMonth format (YYYY-MM)" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var submittedBy = root.TryGetProperty("submittedBy", out var sb) ? sb.GetString() : "unknown";

            try
            {
                await service.SubmitForApprovalAsync(cc!, yearMonth, submittedBy!);
                return Results.Ok(new { ok = true, message = "承認申請完了" });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // 承認
        app.MapPost("/monthly-closing/{yearMonth}/approve", async (string yearMonth, HttpRequest req, MonthlyClosingService service) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            if (!ValidateYearMonth(yearMonth))
                return Results.BadRequest(new { error = "Invalid yearMonth format (YYYY-MM)" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var approvedBy = root.TryGetProperty("approvedBy", out var ab) ? ab.GetString() : "unknown";
            var comment = root.TryGetProperty("comment", out var c) ? c.GetString() : null;

            try
            {
                await service.ApproveAsync(cc!, yearMonth, approvedBy!, comment);
                return Results.Ok(new { ok = true, message = "承認完了" });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // 月次締め確定
        app.MapPost("/monthly-closing/{yearMonth}/close", async (string yearMonth, HttpRequest req, MonthlyClosingService service) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            if (!ValidateYearMonth(yearMonth))
                return Results.BadRequest(new { error = "Invalid yearMonth format (YYYY-MM)" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var closedBy = root.TryGetProperty("closedBy", out var cb) ? cb.GetString() : "unknown";

            try
            {
                await service.CloseAsync(cc!, yearMonth, closedBy!);
                return Results.Ok(new { ok = true, message = "月次締め完了" });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // 月次締め再開（特権）
        app.MapPost("/monthly-closing/{yearMonth}/reopen", async (string yearMonth, HttpRequest req, MonthlyClosingService service) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            if (!ValidateYearMonth(yearMonth))
                return Results.BadRequest(new { error = "Invalid yearMonth format (YYYY-MM)" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var reopenedBy = root.TryGetProperty("reopenedBy", out var rb) ? rb.GetString() : "unknown";
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : "";

            if (string.IsNullOrWhiteSpace(reason))
                return Results.BadRequest(new { error = "再開理由を入力してください" });

            try
            {
                await service.ReopenAsync(cc!, yearMonth, reopenedBy!, reason!);
                return Results.Ok(new { ok = true, message = "月次締め再開完了" });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // ========================================
        // チェック項目マスタ
        // ========================================

        // チェック項目一覧取得
        app.MapGet("/monthly-closing/check-items", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT item_key, item_name_ja, item_name_en, item_name_zh, category, check_type, priority, is_required, is_active
                FROM monthly_closing_check_items
                WHERE (company_code = $1 OR company_code IS NULL)
                ORDER BY priority, item_key";
            cmd.Parameters.AddWithValue(cc.ToString());

            var list = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new
                {
                    itemKey = reader.GetString(0),
                    itemNameJa = reader.GetString(1),
                    itemNameEn = reader.IsDBNull(2) ? null : reader.GetString(2),
                    itemNameZh = reader.IsDBNull(3) ? null : reader.GetString(3),
                    category = reader.GetString(4),
                    checkType = reader.GetString(5),
                    priority = reader.GetInt32(6),
                    isRequired = reader.GetBoolean(7),
                    isActive = reader.GetBoolean(8)
                });
            }

            return Results.Json(list);
        }).RequireAuthorization();
    }

    private static bool ValidateYearMonth(string yearMonth)
    {
        if (string.IsNullOrWhiteSpace(yearMonth)) return false;
        var parts = yearMonth.Split('-');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var year) || year < 2000 || year > 2100) return false;
        if (!int.TryParse(parts[1], out var month) || month < 1 || month > 12) return false;
        return true;
    }
}

