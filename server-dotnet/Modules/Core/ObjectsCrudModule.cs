using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Domain;
using Server.Infrastructure;
using Server.Infrastructure.Modules;

namespace Server.Modules.Core;

/// <summary>
/// Core generic CRUD endpoints for /objects/{entity}/{id}.
/// This restores functionality that existed in older Program.cs snapshots and keeps Program.cs thinner.
/// </summary>
public sealed class ObjectsCrudModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "objects_crud",
        Name = "通用对象接口",
        Description = "通用对象 CRUD（/objects/*）",
        Category = ModuleCategory.Core,
        Version = "1.0.0",
        Dependencies = Array.Empty<string>(),
        Menus = Array.Empty<MenuConfig>()
    };

    public override void MapEndpoints(WebApplication app)
    {
        // Generic PUT: update payload for non-special entities.
        // Special entities are handled by dedicated endpoints in Program.cs (voucher/account/businesspartner/employee).
        app.MapPut("/objects/{entity}/{id:guid}", async (HttpRequest req, string entity, Guid id, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            // Avoid endpoint ambiguity/behavior change for special entities.
            if (string.Equals(entity, "voucher", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entity, "account", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entity, "businesspartner", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entity, "employee", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Use dedicated endpoint for this entity" });
            }

            using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
            if (!body.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "payload required" });

            // Schema validation (if schema exists).
            var schemaDoc = await SchemasService.GetActiveSchema(ds, entity, cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "update", user)) return Results.StatusCode(403);
                var schema = Json.Schema.JsonSchema.FromText(schemaDoc.RootElement.GetProperty("schema").GetRawText());
                var payloadNode = System.Text.Json.Nodes.JsonNode.Parse(payload.GetRawText());
                var result = schema.Evaluate(payloadNode);
                if (!result.IsValid) return Results.BadRequest(new { error = "schema validation failed", details = result.Details });
            }

            var table = Crud.TableFor(entity);
            var updated = await Crud.UpdateRawJson(ds, table, id, cc.ToString(), payload.GetRawText());
            return updated is null ? Results.NotFound(new { error = "not found" }) : Results.Text(updated, "application/json");
        }).RequireAuthorization();

        // Generic DELETE (soft delete is handled by table triggers/logic if needed; here we hard delete by id).
        app.MapDelete("/objects/{entity}/{id:guid}", async (HttpRequest req, string entity, Guid id, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            // Avoid ambiguity for special entities; they have dedicated endpoints.
            if (string.Equals(entity, "voucher", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entity, "account", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entity, "businesspartner", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entity, "employee", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Use dedicated endpoint for this entity" });
            }

            var schemaDoc = await SchemasService.GetActiveSchema(ds, entity, cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "delete", user)) return Results.StatusCode(403);
            }

            var table = Crud.TableFor(entity);
            var n = await Crud.DeleteById(ds, table, id, cc.ToString());
            return n > 0 ? Results.Ok(new { ok = true, deleted = n }) : Results.NotFound(new { error = "not found" });
        }).RequireAuthorization();
    }
}


