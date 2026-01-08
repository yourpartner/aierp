using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

public static class TaskSchedulerModule
{
    public static void MapTaskSchedulerModule(this WebApplication app)
    {
        app.MapPost("/operations/tasks/{id:guid}/confirm", ConfirmTask).RequireAuthorization();
        app.MapPost("/operations/tasks/{id:guid}/retry", RetryTask).RequireAuthorization();
        app.MapPost("/ai/tasks/plan", PlanTask).RequireAuthorization();
    }

    private static async Task<IResult> ConfirmTask(Guid id, HttpRequest req, NpgsqlDataSource ds, CancellationToken ct)
    {
        if (!TryGetCompany(req, out var companyCode, out var error))
            return error;

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        JsonObject payloadObj;
        DateTimeOffset? existingNextRun = null;
        await using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.CommandText = "SELECT payload, next_run_at FROM scheduler_tasks WHERE id=$1 AND company_code=$2 FOR UPDATE";
            selectCmd.Parameters.AddWithValue(id);
            selectCmd.Parameters.AddWithValue(companyCode);
            await using var reader = await selectCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "task not found" });
            }
            payloadObj = JsonNode.Parse(reader.GetString(0)) as JsonObject ?? new JsonObject();
            if (!reader.IsDBNull(1))
            {
                var dt = reader.GetFieldValue<DateTime>(1);
                existingNextRun = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            }
        }

        var resultObj = payloadObj.TryGetPropertyValue("result", out var resultNode) && resultNode is JsonObject r ? r : new JsonObject();
        var summary = resultObj["summary"] as JsonObject ?? new JsonObject();
        summary["reviewStatus"] = "confirmed";
        summary["confirmedAt"] = DateTimeOffset.UtcNow.ToString("O");
        resultObj["summary"] = summary;
        payloadObj["result"] = resultObj;
        payloadObj["status"] = "pending";

        var scheduleObj = payloadObj.TryGetPropertyValue("schedule", out var scheduleNode) && scheduleNode is JsonObject sc ? sc : null;
        var nextRun = SchedulerPlanHelper.ComputeNextOccurrence(scheduleObj, DateTimeOffset.UtcNow) ?? existingNextRun ?? DateTimeOffset.UtcNow.AddHours(1);

        await using (var update = conn.CreateCommand())
        {
            update.CommandText = "UPDATE scheduler_tasks SET payload=$3::jsonb, next_run_at=$4, locked_by=NULL, locked_at=NULL, updated_at=now() WHERE id=$1 AND company_code=$2 RETURNING to_jsonb(scheduler_tasks)";
            update.Parameters.AddWithValue(id);
            update.Parameters.AddWithValue(companyCode);
            update.Parameters.AddWithValue(payloadObj.ToJsonString());
            update.Parameters.AddWithValue(nextRun.UtcDateTime);
            var updated = await update.ExecuteScalarAsync(ct) as string;
            await tx.CommitAsync(ct);
            return Results.Ok(JsonNode.Parse(updated!));
        }
    }

    private static async Task<IResult> RetryTask(Guid id, HttpRequest req, NpgsqlDataSource ds, CancellationToken ct)
    {
        if (!TryGetCompany(req, out var companyCode, out var error))
            return error;

        DateTimeOffset? nextRunOverride = null;
        JsonObject? appendNote = null;
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        var root = doc.RootElement;
        if (root.TryGetProperty("nextRunAt", out var nextNode) && nextNode.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(nextNode.GetString(), out var parsed))
            nextRunOverride = parsed.ToUniversalTime();
        if (root.TryGetProperty("note", out var noteNode) && noteNode.ValueKind == JsonValueKind.String)
            appendNote = new JsonObject { ["message"] = noteNode.GetString(), ["recordedAt"] = DateTimeOffset.UtcNow.ToString("O") };

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        JsonObject payloadObj;
        await using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.CommandText = "SELECT payload FROM scheduler_tasks WHERE id=$1 AND company_code=$2 FOR UPDATE";
            selectCmd.Parameters.AddWithValue(id);
            selectCmd.Parameters.AddWithValue(companyCode);
            var existing = await selectCmd.ExecuteScalarAsync(ct) as string;
            if (existing is null)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "task not found" });
            }
            payloadObj = JsonNode.Parse(existing) as JsonObject ?? new JsonObject();
        }

        var resultObj = payloadObj.TryGetPropertyValue("result", out var resultNode) && resultNode is JsonObject r ? r : new JsonObject();
        if (appendNote is not null)
        {
            var logArr = resultObj["logs"] as JsonArray ?? new JsonArray();
            logArr.Add(appendNote);
            resultObj["logs"] = logArr;
        }
        var summary = resultObj["summary"] as JsonObject ?? new JsonObject();
        summary["reviewStatus"] = "retry";
        summary["retryAt"] = DateTimeOffset.UtcNow.ToString("O");
        resultObj["summary"] = summary;
        payloadObj["result"] = resultObj;
        payloadObj["status"] = "pending";

        var scheduleObj = payloadObj.TryGetPropertyValue("schedule", out var scheduleNode) && scheduleNode is JsonObject sc ? sc : null;
        var computedNext = nextRunOverride ?? SchedulerPlanHelper.ComputeNextOccurrence(scheduleObj, DateTimeOffset.UtcNow) ?? DateTimeOffset.UtcNow.AddMinutes(5);

        await using (var update = conn.CreateCommand())
        {
            update.CommandText = "UPDATE scheduler_tasks SET payload=$3::jsonb, next_run_at=$4, locked_by=NULL, locked_at=NULL, updated_at=now() WHERE id=$1 AND company_code=$2 RETURNING to_jsonb(scheduler_tasks)";
            update.Parameters.AddWithValue(id);
            update.Parameters.AddWithValue(companyCode);
            update.Parameters.AddWithValue(payloadObj.ToJsonString());
            update.Parameters.AddWithValue(computedNext.UtcDateTime);
            var updated = await update.ExecuteScalarAsync(ct) as string;
            await tx.CommitAsync(ct);
            return Results.Ok(JsonNode.Parse(updated!));
        }
    }

    private static async Task<IResult> PlanTask(HttpRequest req, CancellationToken ct)
    {
        string companyCode = string.Empty;
        if (req.Headers.TryGetValue("x-company-code", out var header) && !string.IsNullOrWhiteSpace(header))
        {
            companyCode = header.ToString();
        }

        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        var root = doc.RootElement;
        var nlText = root.TryGetProperty("nlText", out var nlNode) && nlNode.ValueKind == JsonValueKind.String ? nlNode.GetString() : null;
        if (string.IsNullOrWhiteSpace(nlText))
            return Results.BadRequest(new { error = "nlText required" });

        var interpreted = SchedulerPlanHelper.Interpret(companyCode, nlText);
        return Results.Ok(new
        {
            plan = interpreted.Plan,
            schedule = interpreted.Schedule,
            notes = interpreted.Notes
        });
    }

    private static bool TryGetCompany(HttpRequest req, out string companyCode, out IResult error)
    {
        if (req.Headers.TryGetValue("x-company-code", out var cc) && !string.IsNullOrWhiteSpace(cc))
        {
            companyCode = cc.ToString();
            error = Results.Ok();
            return true;
        }
        companyCode = string.Empty;
        error = Results.BadRequest(new { error = "Missing x-company-code" });
        return false;
    }
}

