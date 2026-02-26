// =============================
// - Run database migrations at startup (tables, generated columns, indexes, seed jsonstructures)
// - Provide generic CRUD/search DSL driven by jsonstructures schema/query/coreFields
// - Enforce voucher-specific rules: DR/CR balance, yymm+6 numbering, inject companyCode/voucherNo
// - Issue short-lived SAS tokens for Azure Blob attachments
// Convention: every request must carry x-company-code for tenant isolation
// =============================
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Json.Schema;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;
using System.Linq;
using System.Collections.Generic;
using Server.Infrastructure; // Auth/Database/Storage utilities
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Server.Domain; // SchemasService for jsonstructures
using Server.Modules;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Server.Infrastructure.Modules;

// Authorization helper notes (must appear before top-level statements):
// - User context comes from headers: x-user-id / x-roles / x-dept-id
// - jsonstructures.auth.actions defines allowed operations
// - jsonstructures.auth.scopes produces parameterized row-level filters
// We reference Server.Infrastructure.Auth instead of redefining helpers here.

var builder = WebApplication.CreateBuilder(args);

// Force backend to listen on port 5179 unless ASPNETCORE_URLS explicitly overrides it.
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://0.0.0.0:5179");
}

var allowedOrigins = (builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? Array.Empty<string>())
    .Select(o => o?.Trim())
    .Where(o => !string.IsNullOrWhiteSpace(o))
    .Select(o => o!) // Ensure non-nullable string
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
if (allowedOrigins.Count == 0)
{
    allowedOrigins.Add("http://localhost:5180");
    allowedOrigins.Add("http://127.0.0.1:5180");
    allowedOrigins.Add("https://yanxia-web.azurewebsites.net");
}
var allowedOriginsArray = allowedOrigins.ToArray();

// Register database and storage via infrastructure extension methods.
builder.Services.AddPostgres(builder.Configuration);
builder.Services.AddAzureBlob(builder.Configuration);
// BlobServiceClient is registered centrally by the Storage infrastructure.
builder.Services.AddSingleton<Server.Infrastructure.LawDatasetService>();
builder.Services.AddScoped<Server.Modules.HrCrudService>();
builder.Services.AddScoped<Server.Modules.InvoiceRegistryService>();
builder.Services.Configure<Server.Infrastructure.AiMessageCleanupOptions>(builder.Configuration.GetSection("AiMessageCleanup"));
builder.Services.Configure<AzureStorageOptions>(builder.Configuration.GetSection("AzureStorage"));
builder.Services.AddSingleton<AzureBlobService>();
builder.Services.AddHostedService<Server.Infrastructure.AiMessageCleanupService>();
builder.Services.AddScoped<Server.Modules.FinanceService>();
builder.Services.AddScoped<Server.Modules.FinancialStatementService>();
builder.Services.AddScoped<Server.Modules.WorkflowRulesService>();
builder.Services.AddScoped<Server.Modules.VoucherAutomationService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<Server.Infrastructure.ApnsService>();
builder.Services.AddHostedService<Server.Infrastructure.NotificationSchedulerService>();
builder.Services.AddHostedService<Server.Infrastructure.TaskSchedulerService>();
builder.Services.AddScoped<Server.Modules.AgentScenarioService>();
builder.Services.AddScoped<Server.Modules.AgentAccountingRuleService>();
builder.Services.AddScoped<Server.Modules.InvoiceTaskService>();
builder.Services.AddScoped<Server.Modules.SalesOrderTaskService>();
builder.Services.AddScoped<Server.Modules.PayrollTaskService>();
builder.Services.AddScoped<Server.Modules.UnifiedTaskService>();
builder.Services.Configure<Server.Infrastructure.EmailSettings>(builder.Configuration.GetSection("Email"));
builder.Services.AddSingleton<Server.Infrastructure.EmailService>();
builder.Services.AddScoped<Server.Services.StaffingEmailService>();
// 模块系统（按 EditionOptions 启用/禁用模块，并注册服务/菜单/端点）
// 注意：模块里可能包含 HostedService（如 payroll / moneytree / sales / wecom），不要在这里重复注册同一个 HostedService，避免后台任务跑两份。
builder.Services.AddErpModules(builder.Configuration);

// PayrollAgentService - AI Agent that autonomously decides when to trigger payroll calculations
// Uncomment to enable autonomous payroll calculation
// builder.Services.AddHostedService<Server.Modules.PayrollAgentService>();
builder.Services.AddScoped<Server.Modules.AccountSelectionService>();
builder.Services.AddScoped<Server.Modules.PaymentMatchingService>();

// 企业微信/AI 相关服务由模块（ai_core/wecom 等）负责注册

// Register permissive CORS policy for dev before building the app.
builder.Services.AddCors(opts =>
{
    opts.AddPolicy("DevAll", p => p
        .WithOrigins(allowedOriginsArray)
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials());
});

// Basic JWT configuration (demo secret, adjust for production).
var jwtKey = builder.Configuration["Jwt:Key"] ?? "dev-secret-change-me-please-use-32bytes-or-more-123456";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "erp.local";
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromMinutes(2)
        };
    });
builder.Services.AddAuthorization();

// Claude API HttpClient (previously OpenAI)
builder.Services.AddHttpClient("claude", client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
    client.Timeout = TimeSpan.FromSeconds(240);
    client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
});

// OpenAI API 客户端
builder.Services.AddHttpClient("openai", client =>
{
    client.BaseAddress = new Uri("https://api.openai.com/v1/");
    client.Timeout = TimeSpan.FromSeconds(240);
});
builder.Services.AddScoped<Server.Modules.AgentKitService>();
builder.Services.AddScoped<Server.Modules.AgentScenarioService>();
// Moneytree 服务由模块（moneytree）注册，避免重复注册 HostedService

var app = builder.Build();

// Global Exception Handler to capture 500 errors in logs
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[GLOBAL ERROR] {context.Request.Method} {context.Request.Path}: {ex}");
        
        // If not started yet, return JSON error
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { 
                error = "Internal Server Error", 
                message = ex.Message,
                detail = ex.ToString()
            });
        }
        else
        {
            throw; // Re-throw if response already started
        }
    }
});
var uploadRoot = Path.Combine(AppContext.BaseDirectory, "uploads", "ai-files");
Directory.CreateDirectory(uploadRoot);
var uploadedFiles = new ConcurrentDictionary<string, UploadedFileRecord>();

var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logsDir);
var aiTasksLogPath = Path.Combine(logsDir, "ai_tasks.log");
var aiTasksLogLock = new object();
void AppendAiTasksLog(string message)
{
    lock (aiTasksLogLock)
    {
        File.AppendAllText(aiTasksLogPath, $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}");
    }
}

var searchRequestJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNameCaseInsensitive = true
};

try
{
    using var scope = app.Services.CreateScope();
    var financial = scope.ServiceProvider.GetRequiredService<FinancialStatementService>();
    var defaultCompany = builder.Configuration.GetSection("App").GetValue<string>("DefaultCompany") ?? "JP01";
    financial.SeedDefaultTemplateAsync(defaultCompany).GetAwaiter().GetResult();
}
catch (Exception ex)
{
    Console.WriteLine($"[financial] seed skipped: {ex.Message}");
}

// Inject CORS headers as early as possible (even for 401/exception) and handle preflight requests.
app.UseCors("DevAll");
// Fallback middleware to ensure every response has CORS headers (401/403/errors included).
app.Use(async (ctx, next) =>
{
    var originHeader = ctx.Request.Headers.TryGetValue("Origin", out var originValues) ? originValues.ToString() : string.Empty;
    var originAllowed = !string.IsNullOrEmpty(originHeader) && allowedOrigins.Contains(originHeader);

    if (originAllowed)
    {
        if (!ctx.Response.Headers.ContainsKey("Access-Control-Allow-Origin"))
            ctx.Response.Headers["Access-Control-Allow-Origin"] = originHeader;
        ctx.Response.Headers["Vary"] = "Origin";
        var reqHdrs = ctx.Request.Headers["Access-Control-Request-Headers"].ToString();
        if (!ctx.Response.Headers.ContainsKey("Access-Control-Allow-Headers"))
            ctx.Response.Headers["Access-Control-Allow-Headers"] = string.IsNullOrEmpty(reqHdrs) ? "Authorization,Content-Type,x-company-code,x-openai-key,x-anthropic-key" : reqHdrs;
        var reqMethod = ctx.Request.Headers["Access-Control-Request-Method"].ToString();
        if (!ctx.Response.Headers.ContainsKey("Access-Control-Allow-Methods"))
            ctx.Response.Headers["Access-Control-Allow-Methods"] = string.IsNullOrEmpty(reqMethod) ? "GET,POST,PUT,DELETE,OPTIONS" : reqMethod;
        if (!ctx.Response.Headers.ContainsKey("Access-Control-Allow-Credentials"))
            ctx.Response.Headers["Access-Control-Allow-Credentials"] = "true";
        if (!ctx.Response.Headers.ContainsKey("Access-Control-Expose-Headers"))
            ctx.Response.Headers["Access-Control-Expose-Headers"] = "*";
    }

    if (!string.IsNullOrEmpty(originHeader) && string.Equals(ctx.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
    {
        if (!originAllowed)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.CompleteAsync();
            return;
        }
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        await ctx.Response.CompleteAsync();
        return;
    }

    await next();
});
app.UseAuthentication();
app.Use(async (ctx, next) =>
{
    // Map JWT claims back into legacy headers so Auth.GetUserCtx can reuse the pipeline.
    var uid = ctx.User?.FindFirst("uid")?.Value;
    var roles = ctx.User?.FindFirst("roles")?.Value;
    var dept = ctx.User?.FindFirst("deptId")?.Value;
    if (!string.IsNullOrEmpty(uid)) ctx.Request.Headers["x-user-id"] = uid;
    if (!string.IsNullOrEmpty(roles)) ctx.Request.Headers["x-roles"] = roles;
    if (!string.IsNullOrEmpty(dept)) ctx.Request.Headers["x-dept-id"] = dept;
    await next();
});
app.UseAuthorization();

// 模块系统：提供 /edition* 给前端（菜单/启用模块），并映射各模块端点（/staffing/*、/portal/* 等）
app.MapEditionEndpoints();
app.UseModuleEndpoints();

// Fallback OPTIONS handler in case earlier middleware is bypassed.
app.MapMethods("/{**any}", new[]{"OPTIONS"}, (HttpContext ctx) =>
{
    var origin = ctx.Request.Headers["Origin"].ToString();
    if (!string.IsNullOrEmpty(origin) && allowedOrigins.Contains(origin))
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
        ctx.Response.Headers["Vary"] = "Origin";
        ctx.Response.Headers["Access-Control-Allow-Credentials"] = "true";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = ctx.Request.Headers["Access-Control-Request-Headers"].ToString();
        ctx.Response.Headers["Access-Control-Allow-Methods"] = ctx.Request.Headers["Access-Control-Request-Method"].ToString();
        return Results.NoContent();
    }
    return Results.StatusCode(StatusCodes.Status403Forbidden);
});

// Migration is intentionally NOT auto-executed on startup.
// To run migrate.sql (and the related fallback seed/migration helpers), explicitly set:
// - env var: YANXIA_RUN_MIGRATE=1  (or true)
// then restart the service.
//
// This prevents accidental overwrites of production data by migrate.sql seeds/updates.
var runMigrate =
    string.Equals(Environment.GetEnvironmentVariable("YANXIA_RUN_MIGRATE"), "1", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(Environment.GetEnvironmentVariable("YANXIA_RUN_MIGRATE"), "true", StringComparison.OrdinalIgnoreCase);

if (runMigrate)
app.Lifetime.ApplicationStarted.Register(async () =>
{
    try
    {
        var ds = app.Services.GetRequiredService<NpgsqlDataSource>();
        await using (var conn = await ds.OpenConnectionAsync())
        {
            // 将 PostgreSQL 的 RAISE NOTICE 输出到控制台（Azure LogStream 可见），便于在“不删除数据”的前提下查看重复数据明细。
            conn.Notice += (_, e) =>
            {
                try
                {
                    var n = e.Notice;
                    Console.WriteLine("[migrate][notice] " + n.MessageText);
                    if (!string.IsNullOrWhiteSpace(n.Detail))
                        Console.WriteLine("[migrate][notice] detail: " + n.Detail);
                    if (!string.IsNullOrWhiteSpace(n.Where))
                        Console.WriteLine("[migrate][notice] where: " + n.Where);
                }
                catch { }
            };

            var sqlPath = Path.Combine(AppContext.BaseDirectory, "migrate.sql");
            if (File.Exists(sqlPath))
            {
                var sql = await File.ReadAllTextAsync(sqlPath);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                // Azure 上偶发 DB 读超时会导致 migrate 噪声；迁移脚本本身是幂等且允许更长时间完成
                cmd.CommandTimeout = 180;
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
    catch (Exception ex)
    {
        // 打印完整异常，便于在 Azure LogStream 里定位“迁移没落地”的真实原因（例如 UNIQUE 约束因重复数据创建失败）。
        try { Console.WriteLine("[migrate] failed: " + ex); } catch {}
        // Ignore migration failures so the service can still start.
    }

    // Fallback: even if the migration fails, try to create critical tables/minimal schema seeds to avoid 42P01 errors.
    try
    {
        var ds = app.Services.GetRequiredService<NpgsqlDataSource>();
        await using var conn2 = await ds.OpenConnectionAsync();
        await using (var cmd = conn2.CreateCommand())
        {
            cmd.CommandText = @"
CREATE EXTENSION IF NOT EXISTS pgcrypto;
-- 兜底：schemas 表，避免 /schemas/* 访问 42P01
CREATE TABLE IF NOT EXISTS schemas (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NULL,
  name TEXT NOT NULL,
  version INTEGER NOT NULL,
  is_active BOOLEAN NOT NULL DEFAULT TRUE,
  schema JSONB NOT NULL,
  ui JSONB,
  query JSONB,
  core_fields JSONB,
  validators JSONB,
  numbering JSONB,
  ai_hints JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE(company_code, name, version)
);

CREATE TABLE IF NOT EXISTS approval_tasks (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  entity TEXT NOT NULL,
  object_id UUID NOT NULL,
  step_no INT NOT NULL,
  step_name TEXT NULL,
  approver_user_id TEXT NOT NULL,
  approver_email TEXT NULL,
  status TEXT NOT NULL DEFAULT 'pending',
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_approvals_company_user ON approval_tasks(company_code, approver_user_id, status);
CREATE INDEX IF NOT EXISTS idx_approvals_company_obj ON approval_tasks(company_code, entity, object_id);
CREATE TABLE IF NOT EXISTS certificate_requests (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- 兜底：公司设置表（若主迁移未执行）
CREATE TABLE IF NOT EXISTS company_settings (
  company_code TEXT PRIMARY KEY,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
-- 通用任务调度表（兜底）
CREATE TABLE IF NOT EXISTS scheduler_tasks (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  nl_spec TEXT NOT NULL,
  plan JSONB,
  schedule JSONB,
  status TEXT NOT NULL DEFAULT 'pending',
  result JSONB,
  next_run_at TIMESTAMPTZ,
  last_run_at TIMESTAMPTZ,
  locked_by TEXT,
  locked_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_scheduler_tasks_company_status ON scheduler_tasks(company_code, status);
CREATE INDEX IF NOT EXISTS idx_scheduler_tasks_next_run ON scheduler_tasks(company_code, next_run_at);
-- 若历史版本存在 id 主键/唯一索引，迁移为以 company_code 为主键
DO $$
BEGIN
  IF EXISTS (
    SELECT 1 FROM information_schema.columns 
    WHERE table_name='company_settings' AND column_name='id'
  ) THEN
    BEGIN
      -- 尝试删除旧主键
      EXECUTE 'ALTER TABLE company_settings DROP CONSTRAINT IF EXISTS company_settings_pkey';
    EXCEPTION WHEN OTHERS THEN NULL; END;
    BEGIN
      -- 删除历史唯一索引
      EXECUTE 'DROP INDEX IF EXISTS uq_company_settings_company';
    EXCEPTION WHEN OTHERS THEN NULL; END;
    BEGIN
      -- 删除 id 列
      EXECUTE 'ALTER TABLE company_settings DROP COLUMN IF EXISTS id';
    EXCEPTION WHEN OTHERS THEN NULL; END;
  END IF;
  -- 确保以 company_code 为主键
  BEGIN
    EXECUTE 'ALTER TABLE company_settings ADD CONSTRAINT pk_company_settings_company PRIMARY KEY (company_code)';
  EXCEPTION WHEN duplicate_table OR duplicate_object THEN NULL; WHEN OTHERS THEN NULL; END;
END $$;

-- 最小库存表（兜底）：仅当主迁移失败时创建，字段与生成列保持一致
CREATE TABLE IF NOT EXISTS materials (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  material_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED,
  base_uom TEXT GENERATED ALWAYS AS (payload->>'baseUom') STORED,
  is_batch_mgmt BOOLEAN GENERATED ALWAYS AS ((payload->>'batchManagement')='true') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_materials_company_code ON materials(company_code, material_code);
CREATE INDEX IF NOT EXISTS idx_materials_company_name ON materials(company_code, name);

CREATE TABLE IF NOT EXISTS warehouses (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  warehouse_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_warehouses_company_code ON warehouses(company_code, warehouse_code);
CREATE INDEX IF NOT EXISTS idx_warehouses_company_name ON warehouses(company_code, name);

CREATE TABLE IF NOT EXISTS bins (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  warehouse_code TEXT GENERATED ALWAYS AS (payload->>'warehouseCode') STORED,
  bin_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_bins_company_wh_bin ON bins(company_code, warehouse_code, bin_code);
CREATE INDEX IF NOT EXISTS idx_bins_company_wh ON bins(company_code, warehouse_code);

-- 库存状态表（兜底）
CREATE TABLE IF NOT EXISTS stock_statuses (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  status_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_stock_statuses_company_code ON stock_statuses(company_code, status_code);
CREATE INDEX IF NOT EXISTS idx_stock_statuses_company_name ON stock_statuses(company_code, name);

-- 批次表（兜底）
CREATE TABLE IF NOT EXISTS batches (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  material_code TEXT GENERATED ALWAYS AS (payload->>'materialCode') STORED,
  batch_no TEXT GENERATED ALWAYS AS (payload->>'batchNo') STORED,
  mfg_date DATE GENERATED ALWAYS AS ((payload->>'mfgDate')::date) STORED,
  exp_date DATE GENERATED ALWAYS AS ((payload->>'expDate')::date) STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_batches_company_mat_batch ON batches(company_code, material_code, batch_no);
CREATE INDEX IF NOT EXISTS idx_batches_company_mat ON batches(company_code, material_code);
-- 序列表兜底
CREATE TABLE IF NOT EXISTS material_sequences (
  company_code TEXT NOT NULL,
  yymm TEXT NOT NULL,
  last_number INTEGER NOT NULL DEFAULT 0,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY(company_code, yymm)
);
CREATE TABLE IF NOT EXISTS warehouse_sequences (
  company_code TEXT NOT NULL,
  yymm TEXT NOT NULL,
  last_number INTEGER NOT NULL DEFAULT 0,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY(company_code, yymm)
);
";
            await cmd.ExecuteNonQueryAsync();
        }

        // Seed minimal schemas (approval task / certificate request) if missing.
        // Use SchemasService to avoid manual SQL string handling.
        var hasApprovalTask = await Server.Domain.SchemasService.GetActiveSchema(ds, "approval_task", null);
        if (hasApprovalTask is null)
        {
            var obj = new
            {
                schema = new { type = "object", properties = new { } },
                ui = new { list = new { columns = new[] { "entity", "step_no", "step_name", "status", "created_at" } }, form = new { layout = Array.Empty<object>() } },
                query = new { filters = new[] { "approver_user_id", "status", "entity", "created_at" }, sorts = new[] { "created_at" } },
                core_fields = new { coreFields = Array.Empty<object>() }
            };
            var json = System.Text.Json.JsonSerializer.Serialize(obj);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            try { await Server.Domain.SchemasService.SaveAndActivate(ds, "approval_task", doc.RootElement, null); } catch {}
        }
        var hasCertReq = await Server.Domain.SchemasService.GetActiveSchema(ds, "certificate_request", null);
        if (hasCertReq is null)
        {
            var obj = new
            {
                schema = new
                {
                    type = "object",
                    properties = new
                    {
                        employeeId = new { type = "string" },
                        type = new { type = "string" },
                        language = new { type = "string" },
                        purpose = new { type = "string" },
                        toEmail = new { type = "string" },
                        subject = new { type = "string" },
                        bodyText = new { type = "string" },
                        status = new { type = "string" }
                    },
                    required = new[] { "employeeId", "type" }
                },
                ui = new { list = new { columns = new[] { "created_at", "status" } }, form = new { layout = Array.Empty<object>() } },
                query = new { filters = new[] { "status", "created_at" }, sorts = new[] { "created_at" } },
                core_fields = new { coreFields = Array.Empty<object>() }
            };
            var json = System.Text.Json.JsonSerializer.Serialize(obj);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            try { await Server.Domain.SchemasService.SaveAndActivate(ds, "certificate_request", doc.RootElement, null); } catch {}
        }
// Additional fallback: ensure company_settings schema is registered globally.
        var hasCompanySetting = await Server.Domain.SchemasService.GetActiveSchema(ds, "company_setting", null);
        if (hasCompanySetting is null)
        {
            var obj = new
            {
                schema = new
                {
                    type = "object",
                    properties = new
                    {
                        companyName = new { type = "string" },
                        companyAddress = new { type = "string" },
                        companyRep = new { type = "string" },
                        workdayDefaultStart = new { type = "string", pattern = @"^\d{2}:\d{2}$" },
                        workdayDefaultEnd = new { type = "string", pattern = @"^\d{2}:\d{2}$" },
                        lunchMinutes = new { type = "number", minimum = 0, maximum = 240 },
                        paymentTerms = new
                        {
                            type = "object",
                            properties = new
                            {
                                cutOffDay = new { type = "number", minimum = 1, maximum = 31 },
                                paymentMonth = new { type = "number", minimum = 0, maximum = 2 },
                                paymentDay = new { type = "number", minimum = 1, maximum = 31 },
                                description = new { type = "string" }
                            }
                        },
                        seal = new {
                            type = "object",
                            properties = new {
                                format = new { type = "string" },
                                size = new { type = "number", minimum = 0 },
                                offsetX = new { type = "number" },
                                offsetY = new { type = "number" },
                                opacity = new { type = "number", minimum = 0, maximum = 1 },
                                // Used only for input; sensitive values are not stored in plaintext.
                                plainBase64 = new { type = "string" },
                                dataUrl = new { type = "string" }
                            }
                        }
                    },
                    required = Array.Empty<string>()
                },
                ui = new
                {
                    list = new { columns = new[] { "created_at" } },
                    form = new
                    {
                        layout = new object[]
                        {
                            new { type = "grid", cols = new object[]
                                {
                                    new { field = "companyName", label = "会社名", span = 12 },
                                    new { field = "companyAddress", label = "会社住所", span = 12 },
                                    new { field = "companyRep", label = "代表者", span = 6 },
                                    new { field = "workdayDefaultStart", label = "始業(HH:mm)", span = 6 },
                                    new { field = "workdayDefaultEnd", label = "終業(HH:mm)", span = 6 },
                                    new { field = "lunchMinutes", label = "休憩(分)", span = 6, props = new { type = "number" } }
                                } }
                            , new { type = "grid", cols = new object[]
                                {
                                    new { field = "paymentTerms.cutOffDay", label = "給与締日", span = 6, props = new { type = "number" } },
                                    new { field = "paymentTerms.paymentMonth", label = "支払月(0=当月,1=翌月,2=翌々月)", span = 6, props = new { type = "number" } },
                                    new { field = "paymentTerms.paymentDay", label = "支払日", span = 6, props = new { type = "number" } },
                                    new { field = "paymentTerms.description", label = "支払条件", span = 12 }
                                } }
                            , new { type = "grid", cols = new object[]
                                {
                                    new { field = "seal.format", label = "社印形式(png/jpg)", span = 6 },
                                    new { field = "seal.size", label = "社印サイズ(pt)", span = 6, props = new { type = "number" } },
                                    new { field = "seal.offsetX", label = "X オフセット(pt)", span = 6, props = new { type = "number" } },
                                    new { field = "seal.offsetY", label = "Y オフセット(pt)", span = 6, props = new { type = "number" } },
                                    new { field = "seal.opacity", label = "不透明度(0-1)", span = 6, props = new { type = "number" } }
                                } }
                            , new { field = "seal.plainBase64", label = "社印Base64(暗号化保存)", widget = "textarea", props = new { type = "textarea", rows = 3 } }
                            , new { field = "seal.dataUrl", label = "社印DataURL(任意)", widget = "textarea", props = new { type = "textarea", rows = 2 } }
                        }
                    }
                },
                query = new { filters = new[] { "created_at" }, sorts = new[] { "created_at" } },
                core_fields = new { coreFields = Array.Empty<object>() }
            };
            var json = System.Text.Json.JsonSerializer.Serialize(obj);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            try { await Server.Domain.SchemasService.SaveAndActivate(ds, "company_setting", doc.RootElement, null); } catch {}
        }
        var hasAccountingPeriod = await Server.Domain.SchemasService.GetActiveSchema(ds, "accounting_period", null);
        if (hasAccountingPeriod is null)
        {
            var obj = new
            {
                schema = new
                {
                    type = "object",
                    properties = new
                    {
                        periodStart = new { type = "string", format = "date" },
                        periodEnd = new { type = "string", format = "date" },
                        isOpen = new { type = "boolean" },
                        memo = new { type = "string" }
                    },
                    required = new[] { "periodStart", "periodEnd" }
                },
                ui = new
                {
                    list = new { columns = new[] { "period_start", "period_end", "is_open", "updated_at" } },
                    form = new { layout = System.Array.Empty<object>() }
                },
                query = new { filters = new[] { "period_start", "period_end", "is_open" }, sorts = new[] { "period_start" } },
                core_fields = new { coreFields = System.Array.Empty<object>() }
            };
            var json = System.Text.Json.JsonSerializer.Serialize(obj);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            try { await Server.Domain.SchemasService.SaveAndActivate(ds, "accounting_period", doc.RootElement, null); } catch {}
        }
        var hasSchedulerTask = await Server.Domain.SchemasService.GetActiveSchema(ds, "scheduler_task", null);
        if (hasSchedulerTask is null)
        {
            var obj = new
            {
                schema = new
                {
                    type = "object",
                    properties = new
                    {
                        nlSpec = new { type = "string", minLength = 1 },
                        status = new { type = "string", @enum = new[] { "pending", "running", "waiting_review", "failed" } },
                        plan = new { type = "object" },
                        schedule = new { type = "object" },
                        result = new { type = "object" },
                        notes = new { type = "array", items = new { type = "string" } }
                    },
                    required = new[] { "nlSpec" }
                },
                ui = new
                {
                    list = new { columns = new[] { "status", "nl_spec", "next_run_at", "last_run_at", "updated_at" } },
                    form = new
                    {
                        layout = new object[]
                        {
                            new
                            {
                                type = "grid",
                                cols = new object[]
                                {
                                    new { field = "nlSpec", label = "自然语言描述", span = 24, props = new { type = "textarea", rows = 4 } }
                                }
                            },
                            new
                            {
                                type = "grid",
                                cols = new object[]
                                {
                                    new { field = "plan", label = "计划(JSON)", span = 24, props = new { type = "json" } },
                                    new { field = "schedule", label = "调度(JSON)", span = 24, props = new { type = "json" } }
                                }
                            },
                            new
                            {
                                type = "grid",
                                cols = new object[]
                                {
                                    new { field = "status", label = "状态", span = 12, props = new { disabled = true } }
                                }
                            },
                            new
                            {
                                type = "grid",
                                cols = new object[]
                                {
                                    new { field = "result", label = "执行结果(JSON)", span = 24, props = new { type = "json", disabled = true } }
                                }
                            }
                        }
                    }
                },
                query = new { filters = new[] { "status" }, sorts = new[] { "updated_at" } },
                core_fields = new { coreFields = new[] { "status", "next_run_at", "last_run_at" } }
            };
            var json = System.Text.Json.JsonSerializer.Serialize(obj);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            try { await Server.Domain.SchemasService.SaveAndActivate(ds, "scheduler_task", doc.RootElement, null); } catch { }
        }
    }
    catch { }
});

if (runMigrate)
{
// Additional fallback: create inventory/material tables if the main migration failed (idempotent).
app.Lifetime.ApplicationStarted.Register(async () =>
{
    try
    {
        var ds = app.Services.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS materials (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  material_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED,
  base_uom TEXT GENERATED ALWAYS AS (payload->>'baseUom') STORED,
  is_batch_mgmt BOOLEAN GENERATED ALWAYS AS ((payload->>'batchManagement')='true') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_materials_company_code ON materials(company_code, material_code);

CREATE TABLE IF NOT EXISTS warehouses (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  warehouse_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_warehouses_company_code ON warehouses(company_code, warehouse_code);

CREATE TABLE IF NOT EXISTS bins (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  warehouse_code TEXT GENERATED ALWAYS AS (payload->>'warehouseCode') STORED,
  bin_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_bins_company_wh_bin ON bins(company_code, warehouse_code, bin_code);

CREATE TABLE IF NOT EXISTS stock_statuses (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  status_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_stock_statuses_company_code ON stock_statuses(company_code, status_code);

CREATE TABLE IF NOT EXISTS batches (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  material_code TEXT GENERATED ALWAYS AS (payload->>'materialCode') STORED,
  batch_no TEXT GENERATED ALWAYS AS (payload->>'batchNo') STORED,
  mfg_date DATE GENERATED ALWAYS AS ((payload->>'mfgDate')::date) STORED,
  exp_date DATE GENERATED ALWAYS AS ((payload->>'expDate')::date) STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_batches_company_material_batch ON batches(company_code, material_code, batch_no);

CREATE TABLE IF NOT EXISTS inventory_movements (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  movement_type TEXT GENERATED ALWAYS AS (payload->>'movementType') STORED,
  movement_date DATE GENERATED ALWAYS AS ((payload->>'movementDate')::date) STORED,
  from_warehouse TEXT GENERATED ALWAYS AS (payload->>'fromWarehouse') STORED,
  from_bin TEXT GENERATED ALWAYS AS (payload->>'fromBin') STORED,
  to_warehouse TEXT GENERATED ALWAYS AS (payload->>'toWarehouse') STORED,
  to_bin TEXT GENERATED ALWAYS AS (payload->>'toBin') STORED,
  reference_no TEXT GENERATED ALWAYS AS (payload->>'referenceNo') STORED
);
CREATE INDEX IF NOT EXISTS idx_inv_moves_company_date ON inventory_movements(company_code, movement_date DESC);

CREATE TABLE IF NOT EXISTS inventory_ledger (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  movement_id UUID NOT NULL,
  line_no INTEGER NOT NULL,
  movement_type TEXT NOT NULL,
  movement_date DATE NOT NULL,
  material_code TEXT NOT NULL,
  quantity NUMERIC(18,6) NOT NULL,
  uom TEXT,
  from_warehouse TEXT,
  from_bin TEXT,
  to_warehouse TEXT,
  to_bin TEXT,
  batch_no TEXT,
  status_code TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_inv_ledger_company_material ON inventory_ledger(company_code, material_code, movement_date);

CREATE TABLE IF NOT EXISTS inventory_balances (
  company_code TEXT NOT NULL,
  material_code TEXT NOT NULL,
  warehouse_code TEXT NOT NULL,
  bin_code TEXT NULL,
  status_code TEXT NULL,
  batch_no TEXT NULL,
  quantity NUMERIC(18,6) NOT NULL DEFAULT 0,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY(company_code, material_code, warehouse_code, bin_code, status_code, batch_no)
);
";
        await cmd.ExecuteNonQueryAsync();
    }
    catch { }
});
}

// Register HR payroll endpoints.
// app.MapHrPayrollModule(); // Registered via PayrollStandardModule
app.MapTaskSchedulerModule();
// 以下模块端点已通过 app.UseModuleEndpoints() 由模块系统统一注册，不再重复调用：
// - InventoryModule / InventoryCountModule (通过 InventoryStandardModule)
// - DeliveryNoteModule / SalesInvoiceModule / SalesAnalyticsModule / SalesAlertModule (通过 SalesStandardModule)
// - FixedAssetModule (通过 FixedAssetStandardModule)
// - WeComMessageModule / WeComChatbotModule (通过 WeComStandardModule)

// AI销售分析自然语言接口
app.MapPost("/analytics/sales/ai-analyze", async (HttpRequest req, Server.Modules.SalesAnalyticsAiService aiService) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    
    using var body = await JsonDocument.ParseAsync(req.Body);
    var root = body.RootElement;
    
    if (!root.TryGetProperty("query", out var queryEl) || queryEl.ValueKind != JsonValueKind.String)
        return Results.BadRequest(new { error = "query required" });
    
    var query = queryEl.GetString()!;
    var dateFrom = root.TryGetProperty("dateFrom", out var dfEl) ? dfEl.GetString() : null;
    var dateTo = root.TryGetProperty("dateTo", out var dtEl) ? dtEl.GetString() : null;
    
    var result = await aiService.AnalyzeAsync(cc.ToString()!, query, dateFrom, dateTo);
    
    return Results.Ok(new
    {
        success = result.Success,
        chartType = result.ChartType,
        chartTitle = result.ChartTitle,
        explanation = result.Explanation,
        data = result.Data,
        echartsConfig = result.EchartsConfig,
        sql = result.Sql,
        error = result.Error
    });
}).RequireAuthorization();

// 销售订单生命周期追踪
app.MapGet("/sales-orders/{soNo}/lifecycle", async (string soNo, HttpRequest req, Server.Modules.SalesOrderLifecycleService lifecycleService) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    
    var lifecycle = await lifecycleService.GetLifecycleAsync(cc.ToString()!, soNo);
    if (lifecycle == null)
        return Results.NotFound(new { error = "Sales order not found" });
    
    return Results.Ok(lifecycle);
}).RequireAuthorization();

// 批量获取生命周期摘要
app.MapPost("/sales-orders/lifecycle-summaries", async (HttpRequest req, Server.Modules.SalesOrderLifecycleService lifecycleService) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    
    using var body = await JsonDocument.ParseAsync(req.Body);
    var root = body.RootElement;
    
    if (!root.TryGetProperty("soNos", out var soNosEl) || soNosEl.ValueKind != JsonValueKind.Array)
        return Results.BadRequest(new { error = "soNos array required" });
    
    var soNos = soNosEl.EnumerateArray()
        .Where(e => e.ValueKind == JsonValueKind.String)
        .Select(e => e.GetString()!)
        .ToList();
    
    var summaries = await lifecycleService.GetLifecycleSummariesAsync(cc.ToString()!, soNos);
    return Results.Ok(summaries);
}).RequireAuthorization();
// Register regulatory maintenance endpoints.
Server.Modules.LawAdminModule.MapLawAdminModule(app);
// Register push notification endpoints.
// Server.Modules.NotificationsModule.MapNotificationsModule(app); // Registered via NotificationsStandardModule
// Register notification policy endpoints.
// Server.Modules.NotificationsPoliciesModule.MapNotificationsPoliciesModule(app); // Registered via NotificationsStandardModule
// Register localization maintenance endpoints.
Server.Modules.LocalizationMaintenanceModule.MapLocalizationMaintenanceModule(app);
// Register CRM endpoints.
// Server.Modules.CrmModule.MapCrmModule(app); // Registered via CrmStandardModule
// Register Finance Reports endpoints (仕訳帳、総勘定元帳、勘定明細、財務諸表).
Server.Modules.Standard.FinanceReportsModule.MapFinanceReportsModule(app);
// Register FB Payment endpoints (全銀協フォーマット自動支払).
Server.Modules.Standard.FbPaymentModule.MapFbPaymentModule(app);
// Register Moneytree endpoints (銀行データ連携).
// Note: Moneytree endpoints moved to MoneytreeStandardModule

// Basic root probe endpoint to avoid 405 errors.
app.MapGet("/", () => Results.Json(new { ok = true })).AllowAnonymous();

app.MapGet("/health", async (IServiceProvider sp) =>
{
    var env = app.Environment.EnvironmentName;
    // Health check: database connectivity.
    bool dbOk = false; string? dbErr = null;
    try
    {
        var ds = sp.GetService<NpgsqlDataSource>();
        if (ds is not null)
        {
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "select 1";
            await cmd.ExecuteScalarAsync();
            dbOk = true;
        }
        else dbErr = "NpgsqlDataSource not registered";
    }
    catch (Exception ex) { dbErr = ex.Message; }

    // Health check: storage connectivity.
    var blobs = sp.GetService<BlobServiceClient>();
    var storageOk = blobs is not null;
    var cfg = sp.GetService<IConfiguration>();
    var container = cfg?.GetSection("AzureStorage")["Container"] ?? "attachments";

    var ok = dbOk; // Ready = database connection succeeds (minimum requirement).
    return Results.Ok(new
    {
        ok,
        env,
        db = new { ok = dbOk, error = dbErr },
        storage = new { ok = storageOk, container }
    });
});
// Temporary debug: check if OPENAI_API_KEY is visible.
app.MapGet("/debug/ai-key", () =>
{
    var k = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    return Results.Ok(new { hasKey = !string.IsNullOrWhiteSpace(k), length = k?.Length ?? 0 });
});

// Debug: view open_items projection by voucher number.
app.MapGet("/debug/open-items", async (string voucherNo, NpgsqlDataSource ds, HttpRequest req) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT to_jsonb(t) FROM (SELECT * FROM open_items WHERE company_code=$1 AND voucher_id=(SELECT id FROM vouchers WHERE company_code=$1 AND voucher_no=$2 LIMIT 1) ORDER BY voucher_line_no) t";
    cmd.Parameters.AddWithValue(cc.ToString());
    cmd.Parameters.AddWithValue(voucherNo);
    var rows = new List<string>();
    await using var rd = await cmd.ExecuteReaderAsync();
    while (await rd.ReadAsync()) rows.Add(rd.GetFieldValue<string>(0));
    return Results.Text("[" + string.Join(',', rows) + "]", "application/json");
}).RequireAuthorization();
// Maintenance: rebuild open_items projection (per voucher or entire set).
app.MapPost("/maintenance/open-items/rebuild", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
    using var body = await JsonDocument.ParseAsync(req.Body);
    var root = body.RootElement;
    var voucherNo = root.TryGetProperty("voucherNo", out var vn) && vn.ValueKind==JsonValueKind.String ? vn.GetString() : null;
    var scopeAll = root.TryGetProperty("all", out var al) && al.ValueKind==JsonValueKind.True;
    if (string.IsNullOrEmpty(voucherNo) && !scopeAll) return Results.BadRequest(new { error = "voucherNo or all required" });

    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    try
    {
        // Select target voucher.
        var sqlPick = scopeAll
            ? "SELECT id, payload FROM vouchers WHERE company_code=$1"
            : "SELECT id, payload FROM vouchers WHERE company_code=$1 AND voucher_no=$2";
        await using var pick = conn.CreateCommand();
        pick.CommandText = sqlPick;
        pick.Parameters.AddWithValue(cc.ToString());
        if (!scopeAll) pick.Parameters.AddWithValue(voucherNo!);
        var targets = new List<(Guid id, JsonDocument payload)>();
        await using (var r = await pick.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var id = r.GetGuid(0);
                var payloadText = r.GetFieldValue<string>(1);
                targets.Add((id, JsonDocument.Parse(payloadText)));
            }
        }

        foreach (var (vid, doc) in targets)
        {
            // Delete old projection first.
            await using (var del = conn.CreateCommand())
            { del.CommandText = "DELETE FROM open_items WHERE company_code=$1 AND voucher_id=$2"; del.Parameters.AddWithValue(cc.ToString()); del.Parameters.AddWithValue(vid); await del.ExecuteNonQueryAsync(); }

            var rootEl = doc.RootElement;
            var posting = rootEl.GetProperty("header").GetProperty("postingDate").GetString()!;
            var currency = rootEl.GetProperty("header").TryGetProperty("currency", out var c) && c.ValueKind==JsonValueKind.String ? c.GetString()! : "JPY";
            for (int idx = 0; idx < rootEl.GetProperty("lines").GetArrayLength(); idx++)
            {
                var line = rootEl.GetProperty("lines")[idx];
                var accountCode = line.GetProperty("accountCode").GetString() ?? string.Empty;
                decimal amt = 0m;
                if (line.TryGetProperty("amount", out var am) && am.ValueKind==JsonValueKind.Number) { if (!am.TryGetDecimal(out amt)) amt = (decimal)am.GetDouble(); }
                if (amt <= 0) continue;
                // Generate projections only for open-item accounts.
                bool isOpen = false;
                await using (var q = conn.CreateCommand())
                {
                    q.CommandText = "SELECT (payload->>'openItem')::boolean FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1";
                    q.Parameters.AddWithValue(cc.ToString()); q.Parameters.AddWithValue(accountCode);
                    var v = await q.ExecuteScalarAsync(); isOpen = v is bool b && b;
                }
                if (!isOpen) continue;
                string? partnerId = null;
                foreach (var key in new[]{"customerId","vendorId","employeeId"})
                { if (line.TryGetProperty(key, out var pv) && pv.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(pv.GetString())) { partnerId = pv.GetString(); break; } }

                await using var ins = conn.CreateCommand();
                ins.CommandText = @"INSERT INTO open_items(company_code, voucher_id, voucher_line_no, account_code, partner_id, currency, doc_date, original_amount, residual_amount, refs)
                                    VALUES ($1, $2, $3, $4, $5, $6, $7::date, $8, $8, $9::jsonb)";
                ins.Parameters.AddWithValue(cc.ToString());
                ins.Parameters.AddWithValue(vid);
                ins.Parameters.AddWithValue(idx+1);
                ins.Parameters.AddWithValue(accountCode);
                ins.Parameters.AddWithValue((object?)partnerId ?? DBNull.Value);
                ins.Parameters.AddWithValue(currency!);
                ins.Parameters.AddWithValue(posting);
                ins.Parameters.AddWithValue(amt);
                ins.Parameters.AddWithValue(JsonSerializer.Serialize(new { source="voucher", voucherId=vid, lineNo=idx+1 }));
                await ins.ExecuteNonQueryAsync();
            }
        }

        await tx.CommitAsync();
        return Results.Ok(new { ok = true, rebuilt = targets.Count });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem(ex.Message);
    }
}).RequireAuthorization();

app.MapPost("/maintenance/bank-master/import", async (HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var companyCode) || string.IsNullOrWhiteSpace(companyCode))
    {
        return Results.BadRequest(new { error = "Missing x-company-code" });
    }

    string? sourceUrl = req.Query.TryGetValue("sourceUrl", out var qs) ? qs.ToString() : null;
    string? localFile = req.Query.TryGetValue("localFilePath", out var qlf) ? qlf.ToString() : null;

    if (req.ContentLength > 0)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            var root = doc.RootElement;
            if (string.IsNullOrWhiteSpace(sourceUrl) && root.TryGetProperty("sourceUrl", out var su) && su.ValueKind == JsonValueKind.String)
            {
                sourceUrl = su.GetString();
            }
            if (string.IsNullOrWhiteSpace(localFile) && root.TryGetProperty("localFilePath", out var lf) && lf.ValueKind == JsonValueKind.String)
            {
                localFile = lf.GetString();
            }
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "请求体 JSON 解析失败。" });
        }
    }

    Uri? sourceUri = null;
    if (!string.IsNullOrWhiteSpace(sourceUrl) && !Uri.TryCreate(sourceUrl, UriKind.Absolute, out sourceUri))
    {
        return Results.BadRequest(new { error = "sourceUrl 必须是有效的绝对地址。" });
    }

    try
    {
        var result = await BankMasterImporter.ImportAsync(
            ds,
            companyCode.ToString(),
            new BankImportOptions
            {
                SourceUrl = sourceUri,
                LocalFilePath = string.IsNullOrWhiteSpace(localFile) ? null : localFile
            },
            ct);

        return Results.Ok(new
        {
            ok = true,
            result.BankCount,
            result.BranchCount,
            result.Source,
            result.ImportedAt
        });
    }
    catch (FileNotFoundException ex)
    {
        return Results.BadRequest(new { error = "本地数据文件不存在。", detail = ex.Message });
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            title: "下载银行支店数据失败。",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "导入银行支店数据失败。",
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireAuthorization();

// Dev helper: create a test user when the company has none.
app.MapPost("/dev/seed-user", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var company = doc.RootElement.TryGetProperty("companyCode", out var c) && c.ValueKind==JsonValueKind.String ? c.GetString()! : "JP01";
    var emp = doc.RootElement.TryGetProperty("employeeCode", out var e) && e.ValueKind==JsonValueKind.String ? e.GetString()! : "admin";
    var pwd = doc.RootElement.TryGetProperty("password", out var p) && p.ValueKind==JsonValueKind.String ? p.GetString()! : "admin123";
    var name = doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind==JsonValueKind.String ? n.GetString() : "管理员";
    var dept = doc.RootElement.TryGetProperty("deptId", out var d) && d.ValueKind==JsonValueKind.String ? d.GetString() : "D001";
    var roleCode = doc.RootElement.TryGetProperty("roleCode", out var r) && r.ValueKind==JsonValueKind.String ? r.GetString()! : "ADMIN";

    await using var conn = await ds.OpenConnectionAsync();
    // Abort if the company already has users (avoid accidental use).
    await using (var q = conn.CreateCommand())
    {
        q.CommandText = "SELECT COUNT(1) FROM users WHERE company_code=$1";
        q.Parameters.AddWithValue(company);
        var cntObj = await q.ExecuteScalarAsync();
        var cnt = Convert.ToInt64(cntObj ?? 0);
        if (cnt > 0) return Results.BadRequest(new { error = "users already exist for this company" });
    }

    var hash = BCrypt.Net.BCrypt.HashPassword(pwd);
    Guid userId;
    await using (var ins = conn.CreateCommand())
    {
        ins.CommandText = @"INSERT INTO users(company_code, employee_code, password_hash, name, dept_id) VALUES ($1,$2,$3,$4,$5) RETURNING id";
        ins.Parameters.AddWithValue(company);
        ins.Parameters.AddWithValue(emp);
        ins.Parameters.AddWithValue(hash);
        ins.Parameters.AddWithValue((object?)name ?? DBNull.Value);
        ins.Parameters.AddWithValue((object?)dept ?? DBNull.Value);
        userId = (Guid)(await ins.ExecuteScalarAsync() ?? Guid.Empty);
    }
    Guid roleId;
    await using (var insr = conn.CreateCommand())
    {
        insr.CommandText = @"INSERT INTO roles(company_code, role_code, role_name) VALUES ($1,$2,$3)
                             ON CONFLICT (company_code, role_code) DO UPDATE SET role_name=EXCLUDED.role_name
                             RETURNING id";
        insr.Parameters.AddWithValue(company);
        insr.Parameters.AddWithValue(roleCode);
        insr.Parameters.AddWithValue(roleCode);
        roleId = (Guid)(await insr.ExecuteScalarAsync() ?? Guid.Empty);
    }
    await using (var map = conn.CreateCommand())
    {
        map.CommandText = "INSERT INTO user_roles(user_id, role_id) VALUES ($1,$2) ON CONFLICT DO NOTHING";
        map.Parameters.AddWithValue(userId); map.Parameters.AddWithValue(roleId);
        await map.ExecuteNonQueryAsync();
    }
    // Seed basic capabilities for demo roles.
    var caps = new[]{ "roles:manage", "op:bank-collect", "op:bank-payment", "report:financial" };
    foreach (var cap in caps)
    {
        await using var capCmd = conn.CreateCommand();
        capCmd.CommandText = "INSERT INTO role_caps(role_id, cap) VALUES ($1,$2) ON CONFLICT DO NOTHING";
        capCmd.Parameters.AddWithValue(roleId);
        capCmd.Parameters.AddWithValue(cap);
        await capCmd.ExecuteNonQueryAsync();
    }
    return Results.Ok(new { ok = true, companyCode = company, employeeCode = emp, password = pwd, role = roleCode });
});

// Login endpoint: companyCode + employeeCode + password -> JWT.
app.MapPost("/auth/login", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
    var companyCode = doc.RootElement.TryGetProperty("companyCode", out var ccEl) && ccEl.ValueKind == JsonValueKind.String
        ? ccEl.GetString()
        : null;
    var employeeCode = doc.RootElement.TryGetProperty("employeeCode", out var empEl) && empEl.ValueKind == JsonValueKind.String
        ? empEl.GetString()
        : null;
    var password = doc.RootElement.TryGetProperty("password", out var pwdEl) && pwdEl.ValueKind == JsonValueKind.String
        ? pwdEl.GetString()
        : null;
    if (string.IsNullOrWhiteSpace(companyCode) || string.IsNullOrWhiteSpace(employeeCode) || string.IsNullOrWhiteSpace(password))
    {
        return Results.BadRequest(new { error = "missing credentials" });
    }

    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT id, password_hash, name, dept_id, employee_id FROM users WHERE company_code=$1 AND employee_code=$2 LIMIT 1";
    cmd.Parameters.AddWithValue(companyCode);
    cmd.Parameters.AddWithValue(employeeCode);
    await using var rd = await cmd.ExecuteReaderAsync();
    if (!await rd.ReadAsync()) return Results.Unauthorized();
    var userId = rd.GetGuid(0);
    var hash = rd.GetString(1);
    var name = rd.IsDBNull(2) ? null : rd.GetString(2);
    var deptId = rd.IsDBNull(3) ? null : rd.GetString(3);
    var employeeId = rd.IsDBNull(4) ? null : rd.GetGuid(4).ToString();
    if (!BCrypt.Net.BCrypt.Verify(password, hash)) return Results.Unauthorized();

    // Fetch role assignments.
    await rd.CloseAsync();
    await using var cmdR = conn.CreateCommand();
    cmdR.CommandText = @"SELECT r.role_code FROM roles r JOIN user_roles ur ON ur.role_id=r.id WHERE ur.user_id=$1";
    cmdR.Parameters.AddWithValue(userId);
    var roles = new List<string>();
    await using (var r2 = await cmdR.ExecuteReaderAsync())
        while (await r2.ReadAsync()) roles.Add(r2.GetString(0));

    // Aggregate capabilities from role_caps.
    var caps = new List<string>();
    await using (var cmdC = conn.CreateCommand())
    {
        cmdC.CommandText = @"SELECT DISTINCT rc.cap FROM role_caps rc JOIN user_roles ur ON ur.role_id=rc.role_id WHERE ur.user_id=$1";
        cmdC.Parameters.AddWithValue(userId);
        await using var rc = await cmdC.ExecuteReaderAsync();
        while (await rc.ReadAsync()) caps.Add(rc.GetString(0));
    }

    // Lookup resource_id from stf_resources if employee_id is present
    string? resourceId = null;
    if (!string.IsNullOrEmpty(employeeId))
    {
        await using var cmdRes = conn.CreateCommand();
        cmdRes.CommandText = @"SELECT id FROM stf_resources WHERE company_code=$1 AND employee_id=$2 LIMIT 1";
        cmdRes.Parameters.AddWithValue(companyCode);
        cmdRes.Parameters.AddWithValue(Guid.Parse(employeeId));
        var resObj = await cmdRes.ExecuteScalarAsync();
        if (resObj is Guid resGuid) resourceId = resGuid.ToString();
    }

    var claimsList = new List<System.Security.Claims.Claim>
    {
        new System.Security.Claims.Claim("uid", userId.ToString()),
        new System.Security.Claims.Claim("companyCode", companyCode),
        new System.Security.Claims.Claim("employeeCode", employeeCode),
        new System.Security.Claims.Claim("name", name ?? string.Empty),
        new System.Security.Claims.Claim("deptId", deptId ?? string.Empty),
        new System.Security.Claims.Claim("roles", string.Join(',', roles)),
        new System.Security.Claims.Claim("caps", string.Join(',', caps))
    };
    if (!string.IsNullOrEmpty(employeeId))
        claimsList.Add(new System.Security.Claims.Claim("employee_id", employeeId));
    if (!string.IsNullOrEmpty(resourceId))
        claimsList.Add(new System.Security.Claims.Claim("resource_id", resourceId));
    var claims = claimsList.ToArray();
    var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
        issuer: jwtIssuer,
        audience: null,
        claims: claims,
        expires: DateTime.UtcNow.AddHours(8),
        signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)
    );
    var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    return Results.Ok(new { token = jwt, name, roles });
});
// Current user info endpoint.
app.MapGet("/auth/me", (HttpContext ctx) =>
{
    var uid = ctx.User?.FindFirst("uid")?.Value;
    var company = ctx.User?.FindFirst("companyCode")?.Value;
    var emp = ctx.User?.FindFirst("employeeCode")?.Value;
    var name = ctx.User?.FindFirst("name")?.Value;
    var dept = ctx.User?.FindFirst("deptId")?.Value;
    var roles = ctx.User?.FindFirst("roles")?.Value;
    var caps = ctx.User?.FindFirst("caps")?.Value;
    if (string.IsNullOrEmpty(uid)) return Results.Unauthorized();
    return Results.Ok(new { uid, companyCode = company, employeeCode = emp, name, deptId = dept, roles, caps });
}).RequireAuthorization();
// Debug: self-test voucher creation without frontend involvement.
// Temporary debug endpoint to test attachment URL generation
app.MapGet("/debug/voucher-attachments/{id:guid}", async (Guid id, NpgsqlDataSource ds, AzureBlobService blobService) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT to_jsonb(t) FROM (SELECT * FROM vouchers WHERE id = $1 LIMIT 1) t";
    cmd.Parameters.AddWithValue(id);
    var json = (string?)await cmd.ExecuteScalarAsync();
    if (string.IsNullOrEmpty(json)) return Results.NotFound(new { error = "voucher not found" });
    
    var row = JsonNode.Parse(json)?.AsObject();
    if (row is null) return Results.BadRequest(new { error = "parse failed" });
    
    if (row.TryGetPropertyValue("payload", out var payloadNode) && payloadNode is not null)
    {
        try 
        { 
            AddAttachmentUrlsPreserveBlob(payloadNode, blobService);
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = "enrich failed", detail = ex.Message });
        }
    }
    
    var attachments = payloadNode is JsonObject payloadObj && payloadObj.TryGetPropertyValue("attachments", out var attNode) ? attNode : null;
    return Results.Json(new { 
        voucherId = id,
        hasPayload = payloadNode is not null,
        attachments = attachments
    });
});

app.MapGet("/debug/voucher-selftest", async (NpgsqlDataSource ds) =>
{
    try
    {
        var cc = "JP01";
        var posting = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var payloadObj = new
        {
            header = new { companyCode = cc, postingDate = posting, voucherType = "GL", currency = "JPY", summary = "selftest" },
            lines = new[]
            {
                new { lineNo = 1, accountCode = "1000", drcr = "DR", amount = 1000 },
                new { lineNo = 2, accountCode = "1010", drcr = "CR", amount = 1000 }
            }
        };
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(payloadObj);

        await using var conn = await ds.OpenConnectionAsync();
        // Use unified numbering service.
        var numbering = await Server.Infrastructure.VoucherNumberingService.NextAsync(ds, cc, DateTime.UtcNow);
        var voucherNo = numbering.voucherNo;

        await using (var cmd = conn.CreateCommand())
        {
            var table = "vouchers";
            cmd.CommandText = $@"INSERT INTO {table}(company_code, payload)
              VALUES ($1, jsonb_set(jsonb_set($2::jsonb, '{{header,companyCode}}', to_jsonb($1::text), true), '{{header,voucherNo}}', to_jsonb($3::text), true))
              RETURNING to_jsonb({table})";
            cmd.Parameters.AddWithValue(cc);
            cmd.Parameters.AddWithValue(payloadJson);
            cmd.Parameters.AddWithValue(voucherNo);
            var json = (string?)await cmd.ExecuteScalarAsync();
            return Results.Ok(new { ok = true, voucherNo, row = json });
        }
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// AI chat (minimal agent): requires OPENAI_API_KEY env var; otherwise returns 501.
app.MapPost("/ai/chat", (HttpRequest req, NpgsqlDataSource ds) =>
{
    return Results.Json(new { error = "deprecated" }, statusCode: 410);
}).RequireAuthorization();

// AI: Compile natural-language payroll rules into DSL (skeleton).
// moved to HrPayrollModule
// Payroll preview (skeleton without real calculations).
// moved to HrPayrollModule

// AI: Suggest GL accounts for payroll items (skeleton).
// moved to HrPayrollModule

// Conversation list.
app.MapGet("/ai/sessions", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest();
    var uid = req.Headers.TryGetValue("x-user-id", out var u) ? u.ToString() : null;
    if (string.IsNullOrEmpty(uid)) return Results.Unauthorized();
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT id, title, created_at, updated_at FROM ai_sessions WHERE company_code=$1 AND user_id=$2 ORDER BY updated_at DESC LIMIT 50";
    cmd.Parameters.AddWithValue(cc.ToString());
    cmd.Parameters.AddWithValue(uid);
    var list = new List<object>();
    await using (var rd = await cmd.ExecuteReaderAsync())
    {
        while (await rd.ReadAsync())
        {
            list.Add(new
            {
                id = rd.GetGuid(0),
                title = rd.IsDBNull(1) ? null : rd.GetString(1),
                createdAt = rd.GetDateTime(2),
                updatedAt = rd.GetDateTime(3)
            });
        }
    }

    if (list.Count == 0)
    {
        await using var create = conn.CreateCommand();
        create.CommandText = "INSERT INTO ai_sessions(company_code,user_id,title) VALUES ($1,$2,$3) RETURNING id, title, created_at, updated_at";
        create.Parameters.AddWithValue(cc.ToString());
        create.Parameters.AddWithValue(uid);
        create.Parameters.AddWithValue("AI Chat");
        await using var created = await create.ExecuteReaderAsync();
        if (await created.ReadAsync())
        {
            list.Add(new
            {
                id = created.GetGuid(0),
                title = created.IsDBNull(1) ? null : created.GetString(1),
                createdAt = created.GetDateTime(2),
                updatedAt = created.GetDateTime(3)
            });
        }
    }

    return Results.Json(list);
}).RequireAuthorization();

app.MapPost("/ai/sessions", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "missing company" });
    if (!req.Headers.TryGetValue("x-user-id", out var uid) || string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
    var title = "聊天会话";
    var forceNew = false;
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
        {
            var raw = t.GetString();
            if (!string.IsNullOrWhiteSpace(raw)) title = raw!;
        }
        if (doc.RootElement.TryGetProperty("forceNew", out var forceEl) && forceEl.ValueKind == JsonValueKind.True)
        {
            forceNew = true;
        }
    }
    catch { }

    await using var conn = await ds.OpenConnectionAsync();
    if (!forceNew)
    {
        await using var check = conn.CreateCommand();
        check.CommandText = "SELECT id, title FROM ai_sessions WHERE company_code=$1 AND user_id=$2 ORDER BY updated_at DESC LIMIT 1";
        check.Parameters.AddWithValue(cc.ToString());
        check.Parameters.AddWithValue(uid.ToString());
        await using var reader = await check.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var existingId = reader.GetGuid(0);
            var existingTitle = reader.IsDBNull(1) ? null : reader.GetString(1);
            return Results.Json(new { id = existingId.ToString(), title = existingTitle });
        }
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO ai_sessions(company_code,user_id,title) VALUES ($1,$2,$3) RETURNING id";
    cmd.Parameters.AddWithValue(cc.ToString());
    cmd.Parameters.AddWithValue(uid.ToString());
    cmd.Parameters.AddWithValue(title);
    var id = await cmd.ExecuteScalarAsync();
    if (id is null) return Results.Problem("failed to create session");
    return Results.Json(new { id = id.ToString() });
}).RequireAuthorization();

// Conversation messages.
app.MapGet("/ai/sessions/{id}/messages", async (Guid id, HttpRequest req, NpgsqlDataSource ds, AzureBlobService blobService) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "missing company" });
    var uid = req.Headers.TryGetValue("x-user-id", out var u) ? u.ToString() : null;
    if (string.IsNullOrEmpty(uid))
        return Results.Unauthorized();

    var limitParam = req.Query.TryGetValue("limit", out var limitRaw) ? limitRaw.ToString() : null;
    var beforeParam = req.Query.TryGetValue("before", out var beforeRaw) ? beforeRaw.ToString() : null;

    var limit = 50;
    if (!string.IsNullOrWhiteSpace(limitParam) && int.TryParse(limitParam, out var parsedLimit))
    {
        limit = Math.Clamp(parsedLimit, 10, 200);
    }
    var fetchSize = limit + 1;

    DateTimeOffset beforeCursor;
    if (!string.IsNullOrWhiteSpace(beforeParam) && DateTimeOffset.TryParse(beforeParam, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedBefore))
    {
        beforeCursor = parsedBefore.ToUniversalTime();
    }
    else
    {
        beforeCursor = DateTimeOffset.UtcNow.AddMinutes(5);
    }

    await using var conn = await ds.OpenConnectionAsync();
    // Verify session ownership
    await using (var verify = conn.CreateCommand())
    {
        verify.CommandText = "SELECT company_code, user_id FROM ai_sessions WHERE id=$1";
        verify.Parameters.AddWithValue(id);
        await using var reader = await verify.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return Results.NotFound();
        }
        var company = reader.GetString(0);
        var user = reader.GetString(1);
        if (!string.Equals(company, cc, StringComparison.Ordinal) || !string.Equals(user, uid, StringComparison.Ordinal))
        {
            return Results.Forbid();
        }
    }

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
WITH unioned AS (
    SELECT role, content, payload::text AS payload, task_id, created_at
    FROM ai_messages
    WHERE session_id=$1 AND created_at < $2
    UNION ALL
    SELECT role, content, payload::text AS payload, task_id, created_at
    FROM ai_messages_archive
    WHERE session_id=$1 AND created_at < $2
),
selected AS (
    SELECT role, content, payload, task_id, created_at
    FROM unioned
    ORDER BY created_at DESC
    LIMIT $3
)
SELECT role, content, payload, task_id, created_at
FROM selected
ORDER BY created_at ASC;
";
    cmd.Parameters.AddWithValue(id);
    cmd.Parameters.AddWithValue(beforeCursor.UtcDateTime);
    cmd.Parameters.AddWithValue(fetchSize);

    await using var rd = await cmd.ExecuteReaderAsync();
    var buffer = new List<(string Role, string? Content, string? PayloadText, Guid? TaskId, DateTime CreatedAt)>();
    while (await rd.ReadAsync())
    {
        buffer.Add((
            rd.GetString(0),
            rd.IsDBNull(1) ? null : rd.GetString(1),
            rd.IsDBNull(2) ? null : rd.GetString(2),
            rd.IsDBNull(3) ? (Guid?)null : rd.GetGuid(3),
            DateTime.SpecifyKind(rd.GetDateTime(4), DateTimeKind.Utc)
        ));
    }

    var hasMore = false;
    if (buffer.Count > limit)
    {
        hasMore = true;
        buffer.RemoveAt(0);
    }

    string? nextCursor = null;
    if (buffer.Count > 0)
    {
        nextCursor = new DateTimeOffset(buffer[0].CreatedAt, TimeSpan.Zero).ToString("O");
    }

    var resultMessages = new List<object>(buffer.Count);
    foreach (var item in buffer)
    {
        object? payload = null;
        if (!string.IsNullOrEmpty(item.PayloadText))
        {
            try
            {
                var node = JsonNode.Parse(item.PayloadText);
                EnrichAttachmentUrls(node, blobService);
                payload = node;
            }
            catch
            {
                payload = null;
            }
        }
        resultMessages.Add(new
        {
            role = item.Role,
            content = item.Content,
            payload,
            taskId = item.TaskId,
            createdAt = new DateTimeOffset(item.CreatedAt, TimeSpan.Zero)
        });
    }

    return Results.Json(new { messages = resultMessages, hasMore, nextCursor });

    static void EnrichAttachmentUrls(JsonNode? node, AzureBlobService blobService)
    {
        if (node is JsonObject obj)
        {
            AddUrls(obj, "attachments", blobService);
            AddUrls(obj, "documents", blobService);
            foreach (var property in obj)
            {
                EnrichAttachmentUrls(property.Value, blobService);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                EnrichAttachmentUrls(child, blobService);
            }
        }

        static void AddUrls(JsonObject parent, string propertyName, AzureBlobService blobService)
        {
            if (!parent.TryGetPropertyValue(propertyName, out var containerNode)) return;
            if (containerNode is not JsonArray containerArray) return;

            foreach (var entry in containerArray.OfType<JsonObject>())
            {
                if (entry.TryGetPropertyValue("blobName", out var blobValue) && blobValue is JsonValue blobJsonValue && blobJsonValue.TryGetValue<string>(out var blobName) && !string.IsNullOrWhiteSpace(blobName))
                {
                    try
                    {
                        var sasUri = blobService.GetReadUri(blobName);
                        entry["url"] = sasUri;
                        entry["previewUrl"] = sasUri;
                    }
                    catch (Exception ex)
                    {
                        entry["urlError"] = ex.Message;
                    }
                    entry.Remove("blobName");
                }

                EnrichAttachmentUrls(entry, blobService);
            }
        }
    }
}).RequireAuthorization();

app.MapPost("/ai/sessions/{id}/messages", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "missing company" });
    var uid = req.Headers.TryGetValue("x-user-id", out var u) ? u.ToString() : null;
    if (string.IsNullOrEmpty(uid))
        return Results.Unauthorized();

    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    if (!root.TryGetProperty("role", out var roleEl) || roleEl.ValueKind != JsonValueKind.String)
    {
        return Results.BadRequest(new { error = "role required" });
    }
    var role = roleEl.GetString();
    string? content = null;
    if (root.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
    {
        content = contentEl.GetString();
    }
    string? payloadText = null;
    if (root.TryGetProperty("payload", out var payloadEl) && payloadEl.ValueKind != JsonValueKind.Null && payloadEl.ValueKind != JsonValueKind.Undefined)
    {
        payloadText = payloadEl.GetRawText();
    }
    Guid? taskId = null;
    if (root.TryGetProperty("taskId", out var taskIdEl) && taskIdEl.ValueKind == JsonValueKind.String && Guid.TryParse(taskIdEl.GetString(), out var parsedTaskId))
    {
        taskId = parsedTaskId;
    }

    await using var conn = await ds.OpenConnectionAsync();
    await using (var verify = conn.CreateCommand())
    {
        verify.CommandText = "SELECT company_code, user_id FROM ai_sessions WHERE id=$1";
        verify.Parameters.AddWithValue(id);
        await using var reader = await verify.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return Results.NotFound();
        }
        var company = reader.GetString(0);
        var user = reader.GetString(1);
        if (!string.Equals(company, cc, StringComparison.Ordinal) || !string.Equals(user, uid, StringComparison.Ordinal))
        {
            return Results.Forbid();
        }
    }

    await using var tx = await conn.BeginTransactionAsync();
    try
    {
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            if (payloadText is null)
            {
                cmd.CommandText = "INSERT INTO ai_messages(session_id, role, content, task_id) VALUES ($1,$2,$3,$4)";
                cmd.Parameters.AddWithValue(id);
                cmd.Parameters.AddWithValue(role!);
                cmd.Parameters.AddWithValue((object?)content ?? DBNull.Value);
                cmd.Parameters.AddWithValue(taskId.HasValue ? taskId.Value : DBNull.Value);
            }
            else
            {
                cmd.CommandText = "INSERT INTO ai_messages(session_id, role, content, payload, task_id) VALUES ($1,$2,$3,$4,$5)";
                cmd.Parameters.AddWithValue(id);
                cmd.Parameters.AddWithValue(role!);
                cmd.Parameters.AddWithValue((object?)content ?? DBNull.Value);
                cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, payloadText);
                cmd.Parameters.AddWithValue(taskId.HasValue ? taskId.Value : DBNull.Value);
            }
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var up = conn.CreateCommand())
        {
            up.Transaction = tx;
            up.CommandText = "UPDATE ai_sessions SET updated_at = now() WHERE id=$1";
            up.Parameters.AddWithValue(id);
            await up.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }
    catch
    {
        await tx.RollbackAsync();
        throw;
    }
    return Results.Json(new { ok = true });
}).RequireAuthorization();

app.MapPost("/ai/workflows/voucher/from-document", async (HttpRequest req, Server.Modules.VoucherAutomationService workflowService) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "missing company" });

    JsonNode? payload;
    try
    {
        payload = await JsonNode.ParseAsync(req.Body);
    }
    catch
    {
        return Results.BadRequest(new { error = "invalid json" });
    }

    if (payload is not JsonObject obj)
    {
        return Results.BadRequest(new { error = "invalid payload" });
    }

    var userCtx = Server.Infrastructure.Auth.GetUserCtx(req);
    var result = await workflowService.CreateVoucherFromDocumentAsync(cc.ToString(), obj, userCtx, req.HttpContext.RequestAborted);

    if (!result.Success)
    {
        return Results.BadRequest(new { error = result.Message, issues = result.Issues });
    }

    object? voucherPayload = null;
    if (result.Payload.HasValue)
    {
        try
        {
            voucherPayload = JsonSerializer.Deserialize<object>(result.Payload.Value.GetRawText());
        }
        catch
        {
            voucherPayload = null;
        }
    }

    return Results.Json(new
    {
        message = result.Message,
        voucherNo = result.VoucherNo,
        voucherId = result.VoucherId,
        payload = voucherPayload,
        confidence = result.Confidence
    });
}).RequireAuthorization();

// Note: Moneytree endpoints moved to MoneytreeStandardModule

app.MapGet("/references/invoice/verify/{regNo}", async (string regNo, Server.Modules.InvoiceRegistryService invoiceRegistry) =>
{
    var normalized = Server.Modules.InvoiceRegistryService.Normalize(regNo);
    if (string.IsNullOrWhiteSpace(normalized))
        return Results.BadRequest(new { error = "invoice registration number required" });
    if (!Server.Modules.InvoiceRegistryService.IsFormatValid(normalized))
        return Results.BadRequest(new { error = "invalid invoice registration number" });
    try
    {
        var result = await invoiceRegistry.VerifyAsync(normalized);
        return Results.Json(result.ToResponse());
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
}).RequireAuthorization();

// 根据公司名称和地址搜索インボイス登録番号
app.MapGet("/references/invoice/search", async (HttpContext ctx, Server.Modules.InvoiceRegistryService invoiceRegistry) =>
{
    var name = ctx.Request.Query.TryGetValue("name", out var n) ? n.ToString() : "";
    var address = ctx.Request.Query.TryGetValue("address", out var addr) ? addr.ToString() : null;
    var limitStr = ctx.Request.Query.TryGetValue("limit", out var lim) ? lim.ToString() : "10";
    int.TryParse(limitStr, out var limit);
    if (limit <= 0 || limit > 50) limit = 10;
    
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { error = "name parameter required" });
    
    try
    {
        var results = await invoiceRegistry.SearchByNameAsync(name, address, limit);
        return Results.Json(new
        {
            data = results.Select(r => r.ToResponse()).ToList(),
            total = results.Count
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
}).RequireAuthorization();

// Read-only endpoint that returns the active schema so AgentKit can inspect query allow-lists, UI layout, validators, etc.
// The response is the raw to_jsonb(jsonstructures) row containing schema/ui/query/core_fields/validators/numbering/ai_hints.
app.MapGet("/schemas/{name}", async (HttpContext ctx, string name, NpgsqlDataSource ds) =>
{
    var ccHeader = ctx.Request.Headers.TryGetValue("x-company-code", out var h) ? h.ToString() : null;
    var lang = ctx.Request.Query.TryGetValue("lang", out var l) ? l.ToString() : null;
    var doc = await SchemasService.GetActiveSchema(ds, name, ccHeader);
    if (doc is null) return Results.NotFound(new { error = "schema not found" });
    var json = doc.RootElement.GetRawText();

    if (string.Equals(lang, "ja", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
            if (node is not null)
            {
                var changed = LocalizeUiLabelsToJa(node);
                if (changed) json = node.ToJsonString();
            }
        }
        catch { }
    }

    ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    ctx.Response.Headers["Pragma"] = "no-cache";
    ctx.Response.Headers["Expires"] = "0";
    return Results.Text(json, "application/json; charset=utf-8");
});

static bool LocalizeUiLabelsToJa(System.Text.Json.Nodes.JsonObject root)
{
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

    bool changed = false;
    var ui = root["ui"] as System.Text.Json.Nodes.JsonObject;
    if (ui is null) return false;
    var form = ui["form"] as System.Text.Json.Nodes.JsonObject;
    var layout = form?["layout"] as System.Text.Json.Nodes.JsonArray;
    if (layout is not null)
    {
        foreach (var section in layout.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            var cols = section["cols"] as System.Text.Json.Nodes.JsonArray;
            if (cols is null) continue;
            foreach (var col in cols.OfType<System.Text.Json.Nodes.JsonObject>())
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
    return changed;
}

// Lists all schema names (for frontend/Agent enumeration only).
app.MapGet("/schemas", async (HttpContext ctx, NpgsqlDataSource ds) =>
{
    var ccHeader = ctx.Request.Headers.TryGetValue("x-company-code", out var h) ? h.ToString() : null;
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      WITH ranked AS (
        SELECT
          name,
          company_code,
          ROW_NUMBER() OVER (
            PARTITION BY name
            ORDER BY
              CASE
                WHEN $1::text IS NOT NULL AND company_code = $1 THEN 0
                WHEN company_code IS NULL THEN 1
                ELSE 2
              END
          ) AS rn
        FROM schemas
        WHERE (
          $1::text IS NOT NULL AND (company_code = $1 OR company_code IS NULL)
        ) OR (
          $1::text IS NULL AND company_code IS NULL
        )
      )
      SELECT COALESCE(
        jsonb_agg(jsonb_build_object('name', name, 'company_code', company_code)),
        '[]'::jsonb
      )
      FROM ranked
      WHERE rn = 1;";
    cmd.Parameters.AddWithValue((object?)ccHeader ?? DBNull.Value);
    var json = (string?)await cmd.ExecuteScalarAsync();
    ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    ctx.Response.Headers["Pragma"] = "no-cache";
    ctx.Response.Headers["Expires"] = "0";
    return Results.Text(json ?? "[]", "application/json; charset=utf-8");
});
// Save schema (upsert: insert or update if exists).
// Schema structure validation is expected to occur client-side or during load.
app.MapPost("/schemas/{name}", async (HttpRequest req, string name, NpgsqlDataSource ds) =>
{
    using var body = await JsonDocument.ParseAsync(req.Body);
    var root = body.RootElement;
    var ccHeader = req.Headers.TryGetValue("x-company-code", out var h) ? h.ToString() : null;
    var json = await SchemasService.SaveAndActivate(ds, name, root, ccHeader);
    if (json is null) return Results.Problem("insert schema failed");
    return Results.Text(json, "application/json");
}).RequireAuthorization();
// Fetch the active schema definition for:
// - JSON Schema validation
// - Filter/sort whitelists (query.filters/sorts)
// - coreFields (generated columns and constraints created via migrations)
const string EmployeeActiveContractsSql = @"EXISTS (
    SELECT 1
    FROM jsonb_array_elements(
        CASE
            WHEN jsonb_typeof(payload->'contracts') = 'array' THEN payload->'contracts'
            ELSE '[]'::jsonb
        END
    ) AS ct
    WHERE
        COALESCE(
            CASE
                WHEN (ct->>'periodFrom') ~ '^\d{4}-\d{2}-\d{2}$' THEN (ct->>'periodFrom')::date
                ELSE NULL
            END,
            '1900-01-01'::date
        ) <= CURRENT_DATE
        AND (
            NOT (ct ? 'periodTo')
            OR btrim(COALESCE(ct->>'periodTo', '')) = ''
            OR (
                (ct->>'periodTo') ~ '^\d{4}-\d{2}-\d{2}$'
                AND (ct->>'periodTo')::date >= CURRENT_DATE
            )
        )
)";

// GetActiveSchema has moved to Domain/SchemasService; table mapping lives in Infrastructure.Crud.TableFor.
async Task<IResult> HandleObjectSearch(HttpRequest req, string entity, NpgsqlDataSource ds, AzureBlobService blobService)
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "missing company" });

    var companyCode = cc.ToString();
    entity = entity?.Trim() ?? string.Empty;
    if (string.IsNullOrEmpty(entity))
        return Results.BadRequest(new { error = "entity required" });

    SearchRequest searchRequest;
    try
    {
        searchRequest = (await JsonSerializer.DeserializeAsync<SearchRequest>(req.Body, searchRequestJsonOptions, req.HttpContext.RequestAborted)) ?? new SearchRequest();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[search] payload parse error: {ex.Message}");
        return Results.BadRequest(new { error = "invalid_search_payload", detail = ex.Message });
    }

    var table = Crud.TableFor(entity.ToLowerInvariant());
    var user = Auth.GetUserCtx(req);
    var requiresCompany = Crud.RequiresCompanyCode(table);
    var builder = new SearchQueryBuilder(table, companyCode, user, requiresCompany);
    string? employmentStatusFilter = null;
    if (string.Equals(entity, "employee", StringComparison.OrdinalIgnoreCase))
    {
        employmentStatusFilter = ExtractEmploymentStatusFilter(searchRequest.Where);
    }
    builder.ApplyWhere(searchRequest.Where);
    if (!string.IsNullOrEmpty(employmentStatusFilter))
    {
        if (string.Equals(employmentStatusFilter, "active", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddWhereExpression(EmployeeActiveContractsSql);
        }
        else if (string.Equals(employmentStatusFilter, "resigned", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddWhereExpression($"NOT ({EmployeeActiveContractsSql})");
        }
    }
    // openitem 默认只返回未清账的项目（residual_amount > 0）
    // 如果需要查询已清账项目，前端应显式传递 includeCleared=true
    if (string.Equals(entity, "openitem", StringComparison.OrdinalIgnoreCase))
    {
        var includeCleared = searchRequest.Where?.Any(w => 
            string.Equals(w.Field, "includeCleared", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(w.Field, "cleared_flag", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(w.Field, "residual_amount", StringComparison.OrdinalIgnoreCase)
        ) == true;
        if (!includeCleared)
        {
            builder.AddWhereExpression("residual_amount > 0");
        }
    }
    builder.SetOrdering(searchRequest.OrderBy);

    var page = searchRequest.Page.GetValueOrDefault(1);
    if (page < 1) page = 1;
    var pageSize = searchRequest.PageSize;
    // pageSize = null 或 0 表示不分页，全量返回
    if (pageSize != null && pageSize != 0)
    {
        if (pageSize < 1) pageSize = 1;
        if (pageSize > 10000) pageSize = 10000;
    }
    var offset = pageSize == null || pageSize == 0 ? 0 : (page - 1) * pageSize.Value;
    builder.SetPagination(pageSize, offset);

    try
    {
        var result = await builder.ExecuteAsync(ds);
    if (string.Equals(entity, "employee", StringComparison.OrdinalIgnoreCase))
    {
        await EnrichEmployeeDepartmentsAsync(ds, result.Data);
    }
        // Restore legacy behavior: voucher payload attachments should include a resolvable url for preview.
        // FinanceService strips url/previewUrl when persisting (SAS URLs expire), so we enrich on read.
        if (string.Equals(entity, "voucher", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var row in result.Data)
            {
                if (row is null) continue;
                if (row.TryGetPropertyValue("payload", out var payloadNode) && payloadNode is not null)
                {
                    try 
                    { 
                        AddAttachmentUrlsPreserveBlob(payloadNode, blobService);
                        // Debug: log attachment URL enrichment
                        if (payloadNode is JsonObject payloadObj && 
                            payloadObj.TryGetPropertyValue("attachments", out var attNode) && 
                            attNode is JsonArray attArr && attArr.Count > 0)
                        {
                            var first = attArr[0];
                            var hasUrl = first is JsonObject fo && fo.TryGetPropertyValue("url", out var urlNode) && urlNode is not null;
                            Console.WriteLine($"[voucher-search] enriched attachments count={attArr.Count}, first hasUrl={hasUrl}");
                        }
                    } 
                    catch (Exception ex)
                    { 
                        Console.WriteLine($"[voucher-search] AddAttachmentUrlsPreserveBlob error: {ex.Message}");
                    }
                }
            }
        }
        await AuditStamp.EnrichAsync(ds, companyCode, result.Data);
        return Results.Json(new
        {
            page,
            pageSize,
            total = result.Total,
            data = result.Data
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[search] {entity} failed: {ex}");
        return Results.BadRequest(new { error = "search_failed", detail = ex.Message });
    }
}

static string? ExtractEmploymentStatusFilter(List<SearchClause>? clauses)
{
    if (clauses is null) return null;
    for (var i = clauses.Count - 1; i >= 0; i--)
    {
        var clause = clauses[i];
        if (clause is null) continue;
        if (!string.Equals(clause.Field, "__employment_status__", StringComparison.OrdinalIgnoreCase))
            continue;
        var value = clause.Value.ValueKind == JsonValueKind.String ? clause.Value.GetString() : null;
        clauses.RemoveAt(i);
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized == "all") return null;
        return normalized;
    }
    return null;
}

static async Task<string> PrepareEmployeePayloadAsync(NpgsqlDataSource ds, string companyCode, JsonElement payload)
{
    var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(payload.GetRawText()) ?? new();
    var nextCode = await EmployeeNumberingService.NextCodeAsync(ds, companyCode);
    dict["code"] = nextCode;
    return JsonSerializer.Serialize(dict);
}

static async Task EnrichEmployeeDepartmentsAsync(NpgsqlDataSource ds, List<JsonObject> rows)
{
    if (rows is null || rows.Count == 0) return;
    var deptIds = new HashSet<Guid>();
    var idToString = new Dictionary<Guid, string>();
    foreach (var row in rows)
    {
        var idStr = ExtractPrimaryDepartmentId(row);
        if (Guid.TryParse(idStr, out var guid) && deptIds.Add(guid))
        {
            idToString[guid] = guid.ToString();
        }
    }
    if (deptIds.Count == 0) return;
    var idsArray = deptIds.ToArray();
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT id::text,
                               COALESCE(name, payload->>'name','') AS name,
                               COALESCE(department_code, payload->>'code','') AS code
                        FROM departments
                        WHERE id = ANY($1::uuid[])";
    cmd.Parameters.AddWithValue(idsArray);
    var map = new Dictionary<string, (string Name, string Code)>(StringComparer.OrdinalIgnoreCase);
    await using (var reader = await cmd.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var code = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            map[id] = (name, code);
        }
    }
    if (map.Count == 0) return;
    foreach (var row in rows)
    {
        var deptId = ExtractPrimaryDepartmentId(row);
        if (string.IsNullOrWhiteSpace(deptId)) continue;
        if (!map.TryGetValue(deptId, out var info)) continue;
        ApplyDepartmentInfo(row, deptId, info.Name, info.Code);
    }
}

static void ApplyDepartmentInfo(JsonObject row, string deptId, string name, string code)
{
    var trimmedName = name?.Trim() ?? string.Empty;
    var trimmedCode = code?.Trim() ?? string.Empty;
    if (string.IsNullOrEmpty(trimmedName) && string.IsNullOrEmpty(trimmedCode)) return;

    SetIfEmpty(row, "primary_department_id", deptId);
    if (!string.IsNullOrEmpty(trimmedName)) SetIfEmpty(row, "primary_department_name", trimmedName);
    if (!string.IsNullOrEmpty(trimmedCode)) SetIfEmpty(row, "primary_department_code", trimmedCode);

    var display = BuildDepartmentDisplay(trimmedName, trimmedCode);
    if (!string.IsNullOrEmpty(display))
    {
        SetIfEmpty(row, "primary_department_display", display);
    }

    if (row.TryGetPropertyValue("payload", out var payloadNode) && payloadNode is JsonObject payload)
    {
        SetIfEmpty(payload, "primaryDepartmentId", deptId);
        if (!string.IsNullOrEmpty(trimmedName)) SetIfEmpty(payload, "primaryDepartmentName", trimmedName);
        if (!string.IsNullOrEmpty(trimmedCode)) SetIfEmpty(payload, "primaryDepartmentCode", trimmedCode);
        var payloadDisplay = BuildDepartmentDisplay(trimmedName, trimmedCode);
        if (!string.IsNullOrEmpty(payloadDisplay)) SetIfEmpty(payload, "primaryDepartmentDisplay", payloadDisplay);
    }
}

static string BuildDepartmentDisplay(string name, string code)
{
    var hasName = !string.IsNullOrEmpty(name);
    var hasCode = !string.IsNullOrEmpty(code);
    if (hasName && hasCode) return $"{name} ({code})";
    if (hasName) return name;
    if (hasCode) return code;
    return string.Empty;
}

static void SetIfEmpty(JsonObject obj, string property, string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return;
    if (obj.TryGetPropertyValue(property, out var existing))
    {
        var current = ReadString(existing);
        if (!string.IsNullOrWhiteSpace(current)) return;
    }
    obj[property] = value;
}

static string? ExtractPrimaryDepartmentId(JsonObject row)
{
    if (row.TryGetPropertyValue("primary_department_id", out var direct))
    {
        var str = ReadString(direct);
        if (!string.IsNullOrWhiteSpace(str)) return str;
    }
    if (row.TryGetPropertyValue("payload", out var payloadNode) && payloadNode is JsonObject payload)
    {
        var keys = new[]
        {
            "primaryDepartmentId",
            "primaryDepartmentID",
            "primaryDeptId",
            "primaryDepartmentCode"
        };
        foreach (var key in keys)
        {
            if (payload.TryGetPropertyValue(key, out var val))
            {
                var str = ReadString(val);
                if (!string.IsNullOrWhiteSpace(str)) return str;
            }
        }
        if (payload.TryGetPropertyValue("departments", out var deptNode) && deptNode is JsonArray arr)
        {
            foreach (var entry in arr)
            {
                if (entry is not JsonObject deptObj) continue;
                var deptKeys = new[] { "departmentId", "department_id", "departmentCode", "department_code" };
                foreach (var key in deptKeys)
                {
                    if (deptObj.TryGetPropertyValue(key, out var val))
                    {
                        var str = ReadString(val);
                        if (!string.IsNullOrWhiteSpace(str)) return str;
                    }
                }
            }
        }
    }
    return null;
}

static string? ReadString(JsonNode? node)
{
    if (node is null) return null;
    if (node is JsonValue value)
    {
        if (value.TryGetValue<string>(out var text)) return text;
    }
    return node.ToString();
}

static async Task<IResult> HandleVoucherUpdate(HttpRequest req, Guid id, FinanceService finance, AzureBlobService blobService)
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });

    using var doc = await JsonDocument.ParseAsync(req.Body);
    if (!doc.RootElement.TryGetProperty("payload", out var payloadEl) || payloadEl.ValueKind != JsonValueKind.Object)
        return Results.BadRequest(new { error = "payload required" });

    // === 学习引擎：更新前获取旧凭证数据，用于检测用户修正 ===
    JsonObject? oldVoucherSnapshot = null;
    try
    {
        var ds = req.HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>();
        await using var snapConn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);
        await using var snapCmd = snapConn.CreateCommand();
        snapCmd.CommandText = @"SELECT payload FROM vouchers WHERE company_code=$1 AND id=$2";
        snapCmd.Parameters.AddWithValue(cc.ToString()!);
        snapCmd.Parameters.AddWithValue(id);
        var oldPayloadStr = (string?)await snapCmd.ExecuteScalarAsync(req.HttpContext.RequestAborted);
        if (!string.IsNullOrWhiteSpace(oldPayloadStr))
        {
            oldVoucherSnapshot = System.Text.Json.Nodes.JsonNode.Parse(oldPayloadStr) as JsonObject;
        }
    }
    catch { /* 获取旧快照失败不阻塞更新 */ }

    var user = Auth.GetUserCtx(req);
    try
    {
        var json = await finance.UpdateVoucherAsync(cc.ToString(), id, payloadEl, user);

        // === 学习引擎：检测科目修改并记录修正事件 ===
        try
        {
            if (oldVoucherSnapshot is not null)
            {
                var learningCollector = req.HttpContext.RequestServices.GetService<Server.Infrastructure.Skills.LearningEventCollector>();
                if (learningCollector is not null)
                {
                    var updatedNode = System.Text.Json.Nodes.JsonNode.Parse(json) as JsonObject;
                    var updatedPayload = updatedNode?.TryGetPropertyValue("payload", out var up) == true ? up as JsonObject : null;
                    if (updatedPayload is not null)
                    {
                        // 比较借方/贷方科目是否发生变化
                        static string? ExtractFirstAccount(JsonObject? payload, string drcr)
                        {
                            if (payload?.TryGetPropertyValue("lines", out var linesNode) != true || linesNode is not System.Text.Json.Nodes.JsonArray lines) return null;
                            foreach (var line in lines)
                            {
                                if (line is JsonObject lo &&
                                    lo.TryGetPropertyValue("drcr", out var d) && d is System.Text.Json.Nodes.JsonValue dv && dv.TryGetValue<string>(out var ds) &&
                                    string.Equals(ds, drcr, StringComparison.OrdinalIgnoreCase) &&
                                    lo.TryGetPropertyValue("accountCode", out var ac) && ac is System.Text.Json.Nodes.JsonValue acv && acv.TryGetValue<string>(out var acs))
                                    return acs;
                            }
                            return null;
                        }
                        var oldDebit = ExtractFirstAccount(oldVoucherSnapshot, "DR");
                        var newDebit = ExtractFirstAccount(updatedPayload, "DR");
                        var oldCredit = ExtractFirstAccount(oldVoucherSnapshot, "CR");
                        var newCredit = ExtractFirstAccount(updatedPayload, "CR");
                        var accountChanged = (!string.Equals(oldDebit, newDebit, StringComparison.OrdinalIgnoreCase))
                                          || (!string.Equals(oldCredit, newCredit, StringComparison.OrdinalIgnoreCase));
                        if (accountChanged)
                        {
                            var originalData = new JsonObject { ["debitAccount"] = oldDebit, ["creditAccount"] = oldCredit };
                            var correctedData = new JsonObject { ["debitAccount"] = newDebit, ["creditAccount"] = newCredit };
                            _ = learningCollector.RecordUserCorrectionAsync(cc.ToString()!, id, originalData, correctedData, CancellationToken.None);

                            // === 银行来源凭证修正：检测是否关联了 moneytree 交易，如果是则记录银行专用学习事件 ===
                            try
                            {
                                var ds2 = req.HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>();
                                await using var bankConn = await ds2.OpenConnectionAsync(req.HttpContext.RequestAborted);
                                await using var bankCmd = bankConn.CreateCommand();
                                bankCmd.CommandText = @"
SELECT mt.description, mt.withdrawal_amount, mt.deposit_amount
FROM moneytree_transactions mt
WHERE mt.company_code = $1 AND mt.voucher_id = $2
LIMIT 1";
                                bankCmd.Parameters.AddWithValue(cc.ToString()!);
                                bankCmd.Parameters.AddWithValue(id);
                                await using var bankReader = await bankCmd.ExecuteReaderAsync(req.HttpContext.RequestAborted);
                                if (await bankReader.ReadAsync(req.HttpContext.RequestAborted))
                                {
                                    var bankDesc = bankReader.IsDBNull(0) ? null : bankReader.GetString(0);
                                    var withdrawal = bankReader.IsDBNull(1) ? 0m : bankReader.GetDecimal(1);
                                    var deposit = bankReader.IsDBNull(2) ? 0m : bankReader.GetDecimal(2);
                                    var isWithdrawal = withdrawal > 0;
                                    var bankAmount = isWithdrawal ? withdrawal : deposit;

                                    // 提取修正后的科目名称
                                    static string? ExtractFirstAccountName(JsonObject? payload, string drcr)
                                    {
                                        if (payload?.TryGetPropertyValue("lines", out var linesNode) != true || linesNode is not System.Text.Json.Nodes.JsonArray lines) return null;
                                        foreach (var line in lines)
                                        {
                                            if (line is JsonObject lo &&
                                                lo.TryGetPropertyValue("drcr", out var d) && d is System.Text.Json.Nodes.JsonValue dv && dv.TryGetValue<string>(out var ds3) &&
                                                string.Equals(ds3, drcr, StringComparison.OrdinalIgnoreCase) &&
                                                lo.TryGetPropertyValue("accountName", out var an) && an is System.Text.Json.Nodes.JsonValue anv && anv.TryGetValue<string>(out var ans))
                                                return ans;
                                        }
                                        return null;
                                    }
                                    var newDebitName = ExtractFirstAccountName(updatedPayload, "DR");
                                    var newCreditName = ExtractFirstAccountName(updatedPayload, "CR");

                                    _ = learningCollector.RecordBankVoucherCorrectionAsync(
                                        cc.ToString()!, id, bankDesc, bankAmount, isWithdrawal,
                                        oldDebit, newDebit, oldCredit, newCredit,
                                        newDebitName, newCreditName, CancellationToken.None);
                                }
                            }
                            catch { /* 银行学习记录失败不影响正常更新 */ }
                        }
                    }
                }
            }
        }
        catch { /* 学习记录失败不影响正常更新 */ }

        // Enrich attachment URLs so the frontend can preview files (e.g. PDF) immediately after save.
        // Without this, the response lacks SAS URLs because FinanceService strips them before persisting.
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node is JsonObject rowObj && rowObj.TryGetPropertyValue("payload", out var payloadNode) && payloadNode is not null)
            {
                AddAttachmentUrlsPreserveBlob(payloadNode, blobService);
            }
            return Results.Text(node?.ToJsonString() ?? json, "application/json");
        }
        catch
        {
            // If enrichment fails, still return the raw json so the save is not lost.
            return Results.Text(json, "application/json");
        }
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}

static async Task<IResult> HandleVoucherNumberUpdate(HttpRequest req, Guid id, FinanceService finance)
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });

    using var doc = await JsonDocument.ParseAsync(req.Body);
    if (!doc.RootElement.TryGetProperty("voucherNo", out var voucherNoEl) || voucherNoEl.ValueKind != JsonValueKind.String)
        return Results.BadRequest(new { error = "voucherNo required" });

    var voucherNo = voucherNoEl.GetString() ?? string.Empty;
    var user = Auth.GetUserCtx(req);
    try
    {
        var json = await finance.UpdateVoucherNumberAsync(cc.ToString(), id, voucherNo, user);
        return Results.Text(json, "application/json");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}
app.MapPost("/objects/{entity}/search", (HttpRequest req, string entity, NpgsqlDataSource ds, AzureBlobService blobService) => HandleObjectSearch(req, entity, ds, blobService)).RequireAuthorization();
app.MapPost("/api/objects/{entity}/search", (HttpRequest req, string entity, NpgsqlDataSource ds, AzureBlobService blobService) => HandleObjectSearch(req, entity, ds, blobService)).RequireAuthorization();
async Task<IResult> HandleGetObject(Guid id, string entity, HttpRequest req, NpgsqlDataSource ds, AzureBlobService blobService)
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "missing company" });

    var table = Crud.TableFor(entity);
    if (string.IsNullOrWhiteSpace(table))
        return Results.NotFound(new { error = "entity not found" });

    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    if (Crud.RequiresCompanyCode(table))
    {
        cmd.CommandText = $"SELECT payload FROM {table} WHERE id=$1 AND company_code=$2 LIMIT 1";
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(cc.ToString());
    }
    else
    {
        cmd.CommandText = $"SELECT payload FROM {table} WHERE id=$1 LIMIT 1";
        cmd.Parameters.AddWithValue(id);
    }
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return Results.NotFound();
    var payload = reader.GetString(0);
    // Restore legacy behavior for voucher detail fetch: include previewable attachment urls.
    if (string.Equals(entity, "voucher", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var node = JsonNode.Parse(payload);
            AddAttachmentUrlsPreserveBlob(node, blobService);
            return Results.Text(node?.ToJsonString() ?? payload, "application/json");
        }
        catch
        {
            // fall back to raw payload
        }
    }
    return Results.Text(payload, "application/json");
}
app.MapGet("/objects/{entity}/{id:guid}", (Guid id, string entity, HttpRequest req, NpgsqlDataSource ds, AzureBlobService blobService) => HandleGetObject(id, entity, req, ds, blobService)).RequireAuthorization();
app.MapGet("/api/objects/{entity}/{id:guid}", (Guid id, string entity, HttpRequest req, NpgsqlDataSource ds, AzureBlobService blobService) => HandleGetObject(id, entity, req, ds, blobService)).RequireAuthorization();

async Task<IResult> HandleBusinessPartnerUpdate(Guid id, HttpRequest req, NpgsqlDataSource ds)
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });

    using var body = await JsonDocument.ParseAsync(req.Body);
    if (!body.RootElement.TryGetProperty("payload", out var payload))
        return Results.BadRequest(new { error = "payload required" });

    var schemaDoc = await SchemasService.GetActiveSchema(ds, "businesspartner", cc.ToString());
    if (schemaDoc is null) return Results.NotFound(new { error = "schema not found" });
    var schema = JsonSchema.FromText(schemaDoc.RootElement.GetProperty("schema").GetRawText());
    var user = Auth.GetUserCtx(req);
    if (!Auth.IsActionAllowed(schemaDoc, "update", user)) return Results.StatusCode(403);

    var result = schema.Evaluate(payload, new EvaluationOptions { OutputFormat = OutputFormat.List });
    if (!result.IsValid)
    {
        var errors = result.Details
            .Where(d => !d.IsValid)
            .Select(d => d.InstanceLocation?.ToString() ?? d.EvaluationPath.ToString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();
        return Results.BadRequest(new { error = "schema validation failed", details = errors });
    }

    var table = Crud.TableFor("businesspartner");
    var updated = await Crud.UpdateRawJson(ds, table, id, cc.ToString()!, payload.GetRawText());
    if (updated is null) return Results.NotFound();
    return Results.Text(updated, "application/json");
}
app.MapPut("/objects/businesspartner/{id:guid}", (Guid id, HttpRequest req, NpgsqlDataSource ds) => HandleBusinessPartnerUpdate(id, req, ds)).RequireAuthorization();
app.MapPut("/api/objects/businesspartner/{id:guid}", (Guid id, HttpRequest req, NpgsqlDataSource ds) => HandleBusinessPartnerUpdate(id, req, ds)).RequireAuthorization();
app.MapPut("/objects/account/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds, FinanceService finance) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });

    using var body = await JsonDocument.ParseAsync(req.Body);
    if (!body.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        return Results.BadRequest(new { error = "payload required" });

    var schemaDoc = await SchemasService.GetActiveSchema(ds, "account", cc.ToString());
    if (schemaDoc is null) return Results.NotFound(new { error = "schema not found" });
    var schema = JsonSchema.FromText(schemaDoc.RootElement.GetProperty("schema").GetRawText());
    var user = Auth.GetUserCtx(req);
    if (!Auth.IsActionAllowed(schemaDoc, "update", user)) return Results.StatusCode(403);

    var result = schema.Evaluate(payload, new EvaluationOptions { OutputFormat = OutputFormat.List });
    if (!result.IsValid)
    {
        var errors = result.Details
            .Where(d => !d.IsValid)
            .Select(d => d.InstanceLocation?.ToString() ?? d.EvaluationPath.ToString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();
        return Results.BadRequest(new { error = "schema validation failed", details = errors });
    }

    try
    {
        var table = Crud.TableFor("account");
        var updated = await finance.UpdateAccount(cc.ToString(), table, id, payload, user);
        return Results.Text(updated, "application/json");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();
app.MapPut("/api/objects/account/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds, FinanceService finance) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });

    using var body = await JsonDocument.ParseAsync(req.Body);
    if (!body.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        return Results.BadRequest(new { error = "payload required" });

    var schemaDoc = await SchemasService.GetActiveSchema(ds, "account", cc.ToString());
    if (schemaDoc is null) return Results.NotFound(new { error = "schema not found" });
    var schema = JsonSchema.FromText(schemaDoc.RootElement.GetProperty("schema").GetRawText());
    var user = Auth.GetUserCtx(req);
    if (!Auth.IsActionAllowed(schemaDoc, "update", user)) return Results.StatusCode(403);

    var result = schema.Evaluate(payload, new EvaluationOptions { OutputFormat = OutputFormat.List });
    if (!result.IsValid)
    {
        var errors = result.Details
            .Where(d => !d.IsValid)
            .Select(d => d.InstanceLocation?.ToString() ?? d.EvaluationPath.ToString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();
        return Results.BadRequest(new { error = "schema validation failed", details = errors });
    }

    try
    {
        var table = Crud.TableFor("account");
        var updated = await finance.UpdateAccount(cc.ToString(), table, id, payload, user);
        return Results.Text(updated, "application/json");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

// 检查科目是否被引用（用于删除前确认）
app.MapGet("/objects/account/{code}/references", async (string code, HttpRequest req, FinanceService finance) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    var result = await finance.CheckAccountReferencesAsync(cc.ToString()!, code, req.HttpContext.RequestAborted);
    return Results.Json(new
    {
        accountCode = result.AccountCode,
        canDelete = result.References.Count == 0,
        references = result.References.Select(r => new { source = r.Source, description = r.Description })
    });
}).RequireAuthorization();
app.MapGet("/api/objects/account/{code}/references", async (string code, HttpRequest req, FinanceService finance) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    var result = await finance.CheckAccountReferencesAsync(cc.ToString()!, code, req.HttpContext.RequestAborted);
    return Results.Json(new
    {
        accountCode = result.AccountCode,
        canDelete = result.References.Count == 0,
        references = result.References.Select(r => new { source = r.Source, description = r.Description })
    });
}).RequireAuthorization();

// 检查取引先是否被引用（用于删除前确认）
app.MapGet("/objects/businesspartner/{id:guid}/references", async (Guid id, HttpRequest req, FinanceService finance) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    var result = await finance.CheckBusinessPartnerReferencesAsync(cc.ToString()!, id, req.HttpContext.RequestAborted);
    return Results.Json(new
    {
        partnerId = result.PartnerId,
        canDelete = result.References.Count == 0,
        references = result.References.Select(r => new { source = r.Source, description = r.Description })
    });
}).RequireAuthorization();
app.MapGet("/api/objects/businesspartner/{id:guid}/references", async (Guid id, HttpRequest req, FinanceService finance) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    var result = await finance.CheckBusinessPartnerReferencesAsync(cc.ToString()!, id, req.HttpContext.RequestAborted);
    return Results.Json(new
    {
        partnerId = result.PartnerId,
        canDelete = result.References.Count == 0,
        references = result.References.Select(r => new { source = r.Source, description = r.Description })
    });
}).RequireAuthorization();

// 检查仓库是否被引用（用于删除前确认）
app.MapGet("/objects/warehouse/{id:guid}/references", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    
    await using var conn = await ds.OpenConnectionAsync();
    var references = new List<object>();
    
    // 获取仓库编码
    string? warehouseCode = null;
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT warehouse_code FROM warehouses WHERE id = $1 AND company_code = $2";
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(cc.ToString());
        warehouseCode = await cmd.ExecuteScalarAsync() as string;
    }
    if (string.IsNullOrEmpty(warehouseCode))
        return Results.NotFound(new { error = "Warehouse not found" });
    
    // 检查 bins 表
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM bins WHERE company_code = $1 AND warehouse_code = $2";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(warehouseCode);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        if (count > 0) references.Add(new { source = "bins", count, description = $"{count} 棚番" });
    }
    
    // 检查 inventory_balances 表
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM inventory_balances WHERE company_code = $1 AND warehouse_code = $2";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(warehouseCode);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        if (count > 0) references.Add(new { source = "inventory_balances", count, description = $"{count} 在庫残高" });
    }
    
    // 检查 inventory_ledger 表
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM inventory_ledger WHERE company_code = $1 AND (from_warehouse = $2 OR to_warehouse = $2)";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(warehouseCode);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        if (count > 0) references.Add(new { source = "inventory_ledger", count, description = $"{count} 在庫移動履歴" });
    }
    
    // 检查 inventory_movements 表（入出库单据）
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM inventory_movements WHERE company_code = $1 AND (from_warehouse = $2 OR to_warehouse = $2)";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(warehouseCode);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        if (count > 0) references.Add(new { source = "inventory_movements", count, description = $"{count} 入出庫伝票" });
    }
    
    return Results.Json(new { warehouseCode, canDelete = references.Count == 0, references });
}).RequireAuthorization();
app.MapGet("/api/objects/warehouse/{id:guid}/references", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    
    await using var conn = await ds.OpenConnectionAsync();
    var references = new List<object>();
    
    string? warehouseCode = null;
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT warehouse_code FROM warehouses WHERE id = $1 AND company_code = $2";
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(cc.ToString());
        warehouseCode = await cmd.ExecuteScalarAsync() as string;
    }
    if (string.IsNullOrEmpty(warehouseCode))
        return Results.NotFound(new { error = "Warehouse not found" });
    
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM bins WHERE company_code = $1 AND warehouse_code = $2";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(warehouseCode);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        if (count > 0) references.Add(new { source = "bins", count, description = $"{count} 棚番" });
    }
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM inventory_balances WHERE company_code = $1 AND warehouse_code = $2";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(warehouseCode);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        if (count > 0) references.Add(new { source = "inventory_balances", count, description = $"{count} 在庫残高" });
    }
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM inventory_ledger WHERE company_code = $1 AND (from_warehouse = $2 OR to_warehouse = $2)";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(warehouseCode);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        if (count > 0) references.Add(new { source = "inventory_ledger", count, description = $"{count} 在庫移動履歴" });
    }
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM inventory_movements WHERE company_code = $1 AND (from_warehouse = $2 OR to_warehouse = $2)";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(warehouseCode);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        if (count > 0) references.Add(new { source = "inventory_movements", count, description = $"{count} 入出庫伝票" });
    }
    
    return Results.Json(new { warehouseCode, canDelete = references.Count == 0, references });
}).RequireAuthorization();

// 检查棚番是否被引用（用于删除前确认）
app.MapGet("/objects/bin/{id:guid}/references", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    
    await using var conn = await ds.OpenConnectionAsync();
    var references = new List<object>();
    
    string? warehouseCode = null, binCode = null;
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT warehouse_code, bin_code FROM bins WHERE id = $1 AND company_code = $2";
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(cc.ToString());
        await using var rd = await cmd.ExecuteReaderAsync();
        if (await rd.ReadAsync()) { warehouseCode = rd.GetString(0); binCode = rd.GetString(1); }
    }
    if (string.IsNullOrEmpty(binCode))
        return Results.NotFound(new { error = "Bin not found" });
    
    // 检查 inventory_balances 表
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM inventory_balances WHERE company_code = $1 AND warehouse_code = $2 AND bin_code = $3";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(warehouseCode!);
        cmd.Parameters.AddWithValue(binCode);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        if (count > 0) references.Add(new { source = "inventory_balances", count, description = $"{count} 在庫残高" });
    }
    
    // 检查 inventory_ledger 表
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM inventory_ledger WHERE company_code = $1 AND ((from_warehouse = $2 AND from_bin = $3) OR (to_warehouse = $2 AND to_bin = $3))";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(warehouseCode!);
        cmd.Parameters.AddWithValue(binCode);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        if (count > 0) references.Add(new { source = "inventory_ledger", count, description = $"{count} 在庫移動履歴" });
    }
    
    // 检查 inventory_movements 表（入出库单据）
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM inventory_movements WHERE company_code = $1 AND ((from_warehouse = $2 AND from_bin = $3) OR (to_warehouse = $2 AND to_bin = $3))";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(warehouseCode!);
        cmd.Parameters.AddWithValue(binCode);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        if (count > 0) references.Add(new { source = "inventory_movements", count, description = $"{count} 入出庫伝票" });
    }
    
    return Results.Json(new { warehouseCode, binCode, canDelete = references.Count == 0, references });
}).RequireAuthorization();
app.MapGet("/api/objects/bin/{id:guid}/references", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    
    await using var conn = await ds.OpenConnectionAsync();
    var references = new List<object>();
    
    string? warehouseCode = null, binCode = null;
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT warehouse_code, bin_code FROM bins WHERE id = $1 AND company_code = $2";
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(cc.ToString());
        await using var rd = await cmd.ExecuteReaderAsync();
        if (await rd.ReadAsync()) { warehouseCode = rd.GetString(0); binCode = rd.GetString(1); }
    }
    if (string.IsNullOrEmpty(binCode))
        return Results.NotFound(new { error = "Bin not found" });
    
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM inventory_balances WHERE company_code = $1 AND warehouse_code = $2 AND bin_code = $3";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(warehouseCode!);
        cmd.Parameters.AddWithValue(binCode);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        if (count > 0) references.Add(new { source = "inventory_balances", count, description = $"{count} 在庫残高" });
    }
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM inventory_ledger WHERE company_code = $1 AND ((from_warehouse = $2 AND from_bin = $3) OR (to_warehouse = $2 AND to_bin = $3))";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(warehouseCode!);
        cmd.Parameters.AddWithValue(binCode);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        if (count > 0) references.Add(new { source = "inventory_ledger", count, description = $"{count} 在庫移動履歴" });
    }
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM inventory_movements WHERE company_code = $1 AND ((from_warehouse = $2 AND from_bin = $3) OR (to_warehouse = $2 AND to_bin = $3))";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(warehouseCode!);
        cmd.Parameters.AddWithValue(binCode);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        if (count > 0) references.Add(new { source = "inventory_movements", count, description = $"{count} 入出庫伝票" });
    }
    
    return Results.Json(new { warehouseCode, binCode, canDelete = references.Count == 0, references });
}).RequireAuthorization();

app.MapPut("/vouchers/{id:guid}", HandleVoucherUpdate).RequireAuthorization();
app.MapPut("/api/vouchers/{id:guid}", HandleVoucherUpdate).RequireAuthorization();
app.MapPut("/vouchers/{id:guid}/number", HandleVoucherNumberUpdate).RequireAuthorization();
app.MapPut("/api/vouchers/{id:guid}/number", HandleVoucherNumberUpdate).RequireAuthorization();

// Generic create endpoint:
// - Requires the x-company-code header as the tenant isolation key.
// - Loads the active schema from jsonstructures and validates with JsonSchema.Net (maxLength/pattern/format/if-then-else, etc.).
// - Enforces auth.actions["create"] role allow-list.
// - Voucher specific logic:
//   1) Ensures debit/credit balance by aggregating drcr/amount.
//   2) Generates voucher numbers as yymm + 6-digit sequence (voucher_sequences increment safely inside the transaction).
//   3) Uses jsonb_set inside SQL to backfill header.companyCode and header.voucherNo so persisted rows stay consistent.
//   4) For open-item accounts, creates open_items projection rows per line to support later matching/clearings.
app.MapPost("/objects/{entity}", async (HttpRequest req, string entity, NpgsqlDataSource ds, Server.Modules.HrCrudService hr, Server.Modules.FinanceService finance) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    var companyCode = cc.ToString();
    using var body = await JsonDocument.ParseAsync(req.Body);
    if (!body.RootElement.TryGetProperty("payload", out var payload))
        return Results.BadRequest(new { error = "payload required" });

    if (string.Equals(entity, "employee", StringComparison.OrdinalIgnoreCase))
    {
        var preparedJson = await PrepareEmployeePayloadAsync(ds, companyCode!, payload);
        using var preparedDoc = JsonDocument.Parse(preparedJson);
        payload = preparedDoc.RootElement.Clone();
    }

    var schemaDoc = await SchemasService.GetActiveSchema(ds, entity, cc.ToString());
    if (schemaDoc is null) return Results.NotFound(new { error = "schema not found" });
    var schema = JsonSchema.FromText(schemaDoc.RootElement.GetProperty("schema").GetRawText());
    var user = Auth.GetUserCtx(req);
    if (!Auth.IsActionAllowed(schemaDoc, "create", user)) return Results.StatusCode(403);
    if (!(string.Equals(entity, "payroll_policy", StringComparison.OrdinalIgnoreCase)
          || string.Equals(entity, "employment_type", StringComparison.OrdinalIgnoreCase)
          || string.Equals(entity, "certificate_request", StringComparison.OrdinalIgnoreCase)))
    {
        var result = schema.Evaluate(payload, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!result.IsValid)
        {
            var errors = result.Details
                .Where(d => !d.IsValid)
                .Select(d => d.InstanceLocation?.ToString() ?? d.EvaluationPath.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList();
            try
            {
                Console.WriteLine($"[schema] create validation failed for {entity}: {payload.GetRawText()}");
                foreach (var err in errors)
                {
                    Console.WriteLine($"  - {err}");
                }
            }
            catch { }
            return Results.BadRequest(new { error = "schema validation failed", details = errors });
        }
    }
    var table = Crud.TableFor(entity);
    if (string.Equals(entity, "company_setting", StringComparison.OrdinalIgnoreCase))
        await EnsureCompanySettingsTableAsync(ds);

    if (entity == "employment_type")
    {
        var inserted = await hr.CreateEmploymentType(cc.ToString()!, table, payload);
        if (inserted is null) return Results.Problem("insert failed");
        return Results.Text(inserted, "application/json");
    }
    
    else if (entity == "payroll_policy")
    {
        var json = await hr.CreatePayrollPolicy(cc.ToString()!, table, payload);
        return Results.Text(json, "application/json");
    }
    else if (entity == "voucher")
    {
        try { var (json, _) = await finance.CreateVoucher(cc.ToString()!, table, payload, user); return Results.Text(json, "application/json"); }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
    else if (entity == "businesspartner")
    {
        try
        {
            // 取引先编码完全自动生成，忽略用户输入
            var payloadObj = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject();
            var newCode = await Server.Infrastructure.BusinessPartnerNumberingService.NextAsync(ds, cc.ToString()!);
            // 同时设置 code 和 partnerCode 确保兼容性
            payloadObj["code"] = newCode;
            payloadObj["partnerCode"] = newCode;
            var inserted = await Crud.InsertRawJson(ds, table, cc.ToString()!, payloadObj.ToJsonString());
            if (inserted is null) return Results.Problem("insert failed");
            return Results.Text(inserted, "application/json");
        }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
    else if (entity == "account")
    {
        try { var json = await finance.CreateAccount(cc.ToString()!, table, payload, user); return Results.Text(json, "application/json"); }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
    else if (entity == "company_setting")
    {
        try
        {
            await EnsureCompanySettingsTableAsync(ds);
            // UPSERT keyed by company_code.
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            // Encrypt seal.plainBase64 or seal.dataUrl via DPAPI and only persist seal.enc.
            string payloadJson = payload.GetRawText();
            try
            {
                using var pdoc = JsonDocument.Parse(payloadJson);
                var root = pdoc.RootElement;
                if (root.TryGetProperty("seal", out var seal) && seal.ValueKind==JsonValueKind.Object)
                {
                    string? format = seal.TryGetProperty("format", out var fm) && fm.ValueKind==JsonValueKind.String ? fm.GetString() : "png";
                    string? dataUrl = seal.TryGetProperty("dataUrl", out var du) && du.ValueKind==JsonValueKind.String ? du.GetString() : null;
                    string? plain = seal.TryGetProperty("plainBase64", out var pb) && pb.ValueKind==JsonValueKind.String ? pb.GetString() : null;
                    string? source = null;
                    if (!string.IsNullOrWhiteSpace(dataUrl))
                    {
                        var idx = dataUrl!.IndexOf(',');
                        source = idx >= 0 ? dataUrl.Substring(idx + 1) : dataUrl;
                    }
                    else if (!string.IsNullOrWhiteSpace(plain))
                    {
                        source = plain;
                    }
                    if (!string.IsNullOrWhiteSpace(source))
                    {
                        try
                        {
                            var bytes = Convert.FromBase64String(source!);
                            string? encB64 = null;
                            if (OperatingSystem.IsWindows())
                            {
                                var enc = System.Security.Cryptography.ProtectedData.Protect(bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                                encB64 = Convert.ToBase64String(enc);
                            }
                            else
                            {
                                // Fallback for non-windows: use plain base64 (not secure, but allows CI to pass and system to run)
                                encB64 = Convert.ToBase64String(bytes);
                            }

                            if (!string.IsNullOrWhiteSpace(encB64))
                            {
                            // Rewrite the seal object so only encrypted bytes and configuration remain.
                            var sealMap = new Dictionary<string, object?>();
                            sealMap["enc"] = encB64;
                            sealMap["format"] = string.IsNullOrWhiteSpace(format) ? "png" : format;
                            if (seal.TryGetProperty("size", out var sz)) sealMap["size"] = sz.ValueKind==JsonValueKind.Number ? sz.GetDouble() : null;
                            if (seal.TryGetProperty("offsetX", out var ox)) sealMap["offsetX"] = ox.ValueKind==JsonValueKind.Number ? ox.GetDouble() : null;
                            if (seal.TryGetProperty("offsetY", out var oy)) sealMap["offsetY"] = oy.ValueKind==JsonValueKind.Number ? oy.GetDouble() : null;
                            if (seal.TryGetProperty("opacity", out var op)) sealMap["opacity"] = op.ValueKind==JsonValueKind.Number ? op.GetDouble() : null;

                            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(payloadJson) ?? new();
                            dict["seal"] = sealMap;
                            payloadJson = System.Text.Json.JsonSerializer.Serialize(dict);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            cmd.CommandText = @"INSERT INTO company_settings(company_code, payload)
                                VALUES ($1, $2::jsonb)
                                ON CONFLICT (company_code) DO UPDATE SET payload=$2::jsonb, updated_at=now()
                                RETURNING to_jsonb(company_settings)";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(payloadJson);
            var json = (string?)await cmd.ExecuteScalarAsync();
            if (json is null) return Results.Problem("upsert failed");
            return Results.Text(json, "application/json");
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
    else if (entity == "bank")
    {
        try { var json = await finance.CreateBank(cc.ToString()!, table, payload, user); return Results.Text(json, "application/json"); }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
    else if (entity == "branch")
    {
        try { var json = await finance.CreateBranch(cc.ToString()!, table, payload, user); return Results.Text(json, "application/json"); }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
    else if (string.Equals(entity, "accounting_period", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var stampedJson = finance.PrepareRootCreateJson(payload, user);
            var inserted = await Crud.InsertRawJson(ds, table, cc.ToString()!, stampedJson);
            if (inserted is null) return Results.Problem("insert failed");
            return Results.Text(inserted, "application/json");
        }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
    else if (entity == "warehouse")
    {
        try
        {
            // Code is no longer auto-generated; frontend inputs it and schema validation enforces it.
            var inserted = await Crud.InsertRawJson(ds, table, cc.ToString()!, payload.GetRawText());
            if (inserted is null) return Results.Problem("insert failed");
            return Results.Text(inserted, "application/json");
        }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
    else if (entity == "material")
    {
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(payload.GetRawText()) ?? new();
            var codeMissing = !dict.ContainsKey("code") || dict["code"] is null || string.IsNullOrWhiteSpace(dict["code"]?.ToString());
            if (codeMissing)
            {
                var numbering = await Server.Infrastructure.MaterialNumberingService.NextAsync(ds, cc.ToString()!, DateTime.UtcNow);
                dict["code"] = numbering.materialCode;
            }
            var jsonStr = JsonSerializer.Serialize(dict);
            var inserted = await Crud.InsertRawJson(ds, table, cc.ToString()!, jsonStr);
            if (inserted is null) return Results.Problem("insert failed");
            return Results.Text(inserted, "application/json");
        }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
    else if (entity == "stock_status")
    {
        try
        {
            // Directly insert the payload (code/name required by schema) and surface readable errors.
            var inserted = await Crud.InsertRawJson(ds, table, cc.ToString()!, payload.GetRawText());
            if (inserted is null) return Results.Problem("insert failed");
            return Results.Text(inserted, "application/json");
        }
        catch (PostgresException pgex) when (pgex.SqlState == "42P01")
        {
            // Missing table: create it dynamically and retry once.
            await using (var conn = await ds.OpenConnectionAsync())
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"CREATE TABLE IF NOT EXISTS stock_statuses (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  status_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_stock_statuses_company_code ON stock_statuses(company_code, status_code);";
                await cmd.ExecuteNonQueryAsync();
            }
            var inserted = await Crud.InsertRawJson(ds, table, cc.ToString()!, payload.GetRawText());
            if (inserted is null) return Results.Problem("insert failed");
            return Results.Text(inserted, "application/json");
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }
    else if (entity == "scheduler_task")
    {
        try
        {
            var payloadObj = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject();
            var nlSpec = payloadObj.TryGetPropertyValue("nlSpec", out var nlNode) && nlNode is JsonValue nlVal && nlVal.TryGetValue<string>(out var nlStr) ? nlStr : null;
            if (string.IsNullOrWhiteSpace(nlSpec)) return Results.BadRequest(new { error = "nlSpec required" });
            if (!payloadObj.TryGetPropertyValue("status", out var statusNode) || statusNode is not JsonValue)
            {
                payloadObj["status"] = "pending";
            }

            var planObj = payloadObj.TryGetPropertyValue("plan", out var planNode) && planNode is JsonObject planJson ? planJson : null;
            var scheduleObj = payloadObj.TryGetPropertyValue("schedule", out var scheduleNode) && scheduleNode is JsonObject scheduleJson ? scheduleJson : null;

            if (planObj is null || scheduleObj is null || !payloadObj.TryGetPropertyValue("notes", out _))
            {
                var interpreted = SchedulerPlanHelper.Interpret(cc.ToString()!, nlSpec!);
                if (planObj is null && interpreted.Plan is not null)
                {
                    planObj = interpreted.Plan;
                    payloadObj["plan"] = interpreted.Plan;
                }
                if (scheduleObj is null && interpreted.Schedule is not null)
                {
                    scheduleObj = interpreted.Schedule;
                    payloadObj["schedule"] = interpreted.Schedule;
                }
                if (interpreted.Notes.Length > 0)
                {
                    payloadObj["notes"] = new JsonArray(interpreted.Notes.Select(note => JsonValue.Create(note)).ToArray());
                }
            }

            var nextRun = SchedulerPlanHelper.ComputeNextOccurrence(scheduleObj, DateTimeOffset.UtcNow);

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO scheduler_tasks(company_code, payload, next_run_at, created_at, updated_at)
VALUES ($1, $2::jsonb, $3, now(), now())
RETURNING to_jsonb(scheduler_tasks)";
            cmd.Parameters.AddWithValue(cc.ToString()!);
            cmd.Parameters.AddWithValue(payloadObj.ToJsonString());
            if (nextRun.HasValue) cmd.Parameters.AddWithValue(nextRun.Value.UtcDateTime); else cmd.Parameters.AddWithValue(DBNull.Value);
            var insertedTask = await cmd.ExecuteScalarAsync() as string;
            if (insertedTask is null) return Results.Problem("insert failed");
            return Results.Text(insertedTask, "application/json");
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
    else
    {
        try
        {
            // Certificate confirmation requests need approval tasks and notifications right after insert.
            bool isCert = string.Equals(entity, "certificate_request", StringComparison.OrdinalIgnoreCase);
            // Special case: timesheet payloads auto-populate creator metadata so the frontend need not send employee info.
            if (string.Equals(entity, "timesheet", StringComparison.OrdinalIgnoreCase))
            {
                var userCtx = Auth.GetUserCtx(req);
                // Keep payload shape stable; append creatorUserId and createdMonth as derived fields.
                var dateStr = payload.TryGetProperty("date", out var d) && d.ValueKind==JsonValueKind.String ? d.GetString() : null;
                var month = string.Empty;
                if (DateTime.TryParse(dateStr, out var dt)) month = dt.ToString("yyyy-MM");
                var enriched = JsonSerializer.Deserialize<Dictionary<string, object?>>(payload.GetRawText()) ?? new();
                enriched["creatorUserId"] = userCtx.UserId ?? string.Empty;
                if (!string.IsNullOrEmpty(month)) enriched["createdMonth"] = month;
                var jsonStr = JsonSerializer.Serialize(enriched);
                var insertedTs = await Crud.InsertRawJson(ds, table, cc.ToString()!, jsonStr);
                if (insertedTs is null) return Results.Problem("insert failed");
                return Results.Text(insertedTs, "application/json");
            }
            // certificate_request: inject requester info and initial workflow status.
            if (isCert)
            {
                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(payload.GetRawText()) ?? new();
                    var empCode = req.HttpContext.User?.FindFirst("employeeCode")?.Value;
                    if (!dict.ContainsKey("employeeId") || dict["employeeId"] is null || string.IsNullOrWhiteSpace(dict["employeeId"]?.ToString()))
                        if (!string.IsNullOrWhiteSpace(empCode)) dict["employeeId"] = empCode;
                    if (!dict.ContainsKey("status") || dict["status"] is null) dict["status"] = "pending";
                    var jsonStr = JsonSerializer.Serialize(dict);
                    var insertedCert = await Crud.InsertRawJson(ds, table, cc.ToString()!, jsonStr);
                    if (insertedCert is null) return Results.Problem("insert failed");
                    // Bootstrap approval tasks.
                    try
                    {
                        using var docIns = JsonDocument.Parse(insertedCert);
                        var row = docIns.RootElement;
                        var id = row.GetProperty("id").GetGuid();
                        await InitializeApprovalForObject(app.Configuration, ds, cc.ToString()!, entity, id);
                    }
                    catch { }
                    return Results.Text(insertedCert, "application/json");
                }
                catch (Exception ex)
                {
                    return Results.Problem(ex.Message);
                }
            }

        // inventory_movement: go through the specialized flow (movement row + ledger expansion + balance update).
        if (string.Equals(entity, "inventory_movement", StringComparison.OrdinalIgnoreCase))
        {
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                Guid movementId;
                await using (var ins = conn.CreateCommand())
                {
                    ins.CommandText = @"INSERT INTO inventory_movements(company_code, payload) VALUES ($1, $2::jsonb) RETURNING id";
                    ins.Parameters.AddWithValue(cc.ToString());
                    ins.Parameters.AddWithValue(payload.GetRawText());
                    var obj = await ins.ExecuteScalarAsync();
                    movementId = obj is Guid g ? g : Guid.Empty;
                    if (movementId == Guid.Empty) throw new Exception("insert movement failed");
                }

                var movementType = payload.TryGetProperty("movementType", out var mt) && mt.ValueKind==JsonValueKind.String ? mt.GetString() : null;
                var movementDateStr = payload.TryGetProperty("movementDate", out var md) && md.ValueKind==JsonValueKind.String ? md.GetString() : DateTime.UtcNow.ToString("yyyy-MM-dd");
                var fromWh = payload.TryGetProperty("fromWarehouse", out var fw) && fw.ValueKind==JsonValueKind.String ? fw.GetString() : null;
                var fromBin = payload.TryGetProperty("fromBin", out var fbn) && fbn.ValueKind==JsonValueKind.String ? fbn.GetString() : null;
                var toWh = payload.TryGetProperty("toWarehouse", out var tw) && tw.ValueKind==JsonValueKind.String ? tw.GetString() : null;
                var toBin = payload.TryGetProperty("toBin", out var tbn) && tbn.ValueKind==JsonValueKind.String ? tbn.GetString() : null;
                var lines = payload.TryGetProperty("lines", out var ls) && ls.ValueKind==JsonValueKind.Array ? ls.EnumerateArray().ToArray() : Array.Empty<JsonElement>();

                int idx = 1;
                foreach (var line in lines)
                {
                    var materialCode = line.GetProperty("materialCode").GetString() ?? string.Empty;
                    decimal qty = 0m; if (line.TryGetProperty("quantity", out var q) && q.ValueKind==JsonValueKind.Number) { if (!q.TryGetDecimal(out qty)) qty = (decimal)q.GetDouble(); }
                    var uom = line.TryGetProperty("uom", out var u) && u.ValueKind==JsonValueKind.String ? u.GetString() : null;
                    var batchNo = line.TryGetProperty("batchNo", out var b) && b.ValueKind==JsonValueKind.String ? b.GetString() : null;
                    var statusCode = line.TryGetProperty("statusCode", out var st) && st.ValueKind==JsonValueKind.String ? st.GetString() : null;

                    async Task insertLedgerRow(string type, decimal quantity, string? fwh, string? fbi, string? twh, string? tbi)
                    {
                        await using var il = conn.CreateCommand();
                        il.CommandText = @"INSERT INTO inventory_ledger(company_code, movement_id, line_no, movement_type, movement_date, material_code, quantity, uom, from_warehouse, from_bin, to_warehouse, to_bin, batch_no, status_code)
                                          VALUES ($1,$2,$3,$4,$5::date,$6,$7,$8,$9,$10,$11,$12,$13,$14)";
                        il.Parameters.AddWithValue(cc.ToString());
                        il.Parameters.AddWithValue(movementId);
                        il.Parameters.AddWithValue(idx);
                        il.Parameters.AddWithValue(type);
                        il.Parameters.AddWithValue(movementDateStr!);
                        il.Parameters.AddWithValue(materialCode);
                        il.Parameters.AddWithValue(quantity);
                        il.Parameters.AddWithValue((object?)uom ?? DBNull.Value);
                        il.Parameters.AddWithValue((object?)fwh ?? DBNull.Value);
                        il.Parameters.AddWithValue((object?)fbi ?? DBNull.Value);
                        il.Parameters.AddWithValue((object?)twh ?? DBNull.Value);
                        il.Parameters.AddWithValue((object?)tbi ?? DBNull.Value);
                        il.Parameters.AddWithValue((object?)batchNo ?? DBNull.Value);
                        il.Parameters.AddWithValue((object?)statusCode ?? DBNull.Value);
                        await il.ExecuteNonQueryAsync();
                    }

                    async Task upsertBalance(string material, string? wh, string? bin, string? status, string? batch, decimal delta)
                    {
                        await using var ub = conn.CreateCommand();
                        ub.CommandText = @"INSERT INTO inventory_balances(company_code, material_code, warehouse_code, bin_code, status_code, batch_no, quantity)
                                           VALUES ($1,$2,$3,$4,$5,$6,$7)
                                           ON CONFLICT (company_code, material_code, warehouse_code, bin_code, status_code, batch_no)
                                           DO UPDATE SET quantity = inventory_balances.quantity + EXCLUDED.quantity, updated_at = now()";
                        ub.Parameters.AddWithValue(cc.ToString());
                        ub.Parameters.AddWithValue(material);
                        ub.Parameters.AddWithValue(wh ?? string.Empty);
                        ub.Parameters.AddWithValue((object?)bin ?? DBNull.Value);
                        ub.Parameters.AddWithValue((object?)status ?? DBNull.Value);
                        ub.Parameters.AddWithValue((object?)batch ?? DBNull.Value);
                        ub.Parameters.AddWithValue(delta);
                        await ub.ExecuteNonQueryAsync();
                    }

                    if (string.Equals(movementType, "IN", StringComparison.OrdinalIgnoreCase))
                    {
                        await insertLedgerRow("IN", Math.Abs(qty), null, null, toWh, toBin);
                        await upsertBalance(materialCode, toWh, toBin, statusCode, batchNo, Math.Abs(qty));
                    }
                    else if (string.Equals(movementType, "OUT", StringComparison.OrdinalIgnoreCase))
                    {
                        await insertLedgerRow("OUT", -Math.Abs(qty), fromWh, fromBin, null, null);
                        await upsertBalance(materialCode, fromWh, fromBin, statusCode, batchNo, -Math.Abs(qty));
                    }
                    else if (string.Equals(movementType, "TRANSFER", StringComparison.OrdinalIgnoreCase))
                    {
                        await insertLedgerRow("OUT", -Math.Abs(qty), fromWh, fromBin, null, null);
                        await upsertBalance(materialCode, fromWh, fromBin, statusCode, batchNo, -Math.Abs(qty));
                        await insertLedgerRow("IN", Math.Abs(qty), null, null, toWh, toBin);
                        await upsertBalance(materialCode, toWh, toBin, statusCode, batchNo, Math.Abs(qty));
                    }

                    idx++;
                }

                await tx.CommitAsync();
                return Results.Ok(new { ok = true, id = movementId });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return Results.Problem(ex.Message);
            }
        }

        var inserted3 = await Crud.InsertRawJson(ds, table, cc.ToString()!, payload.GetRawText());
            if (inserted3 is null) return Results.Problem("insert failed");

            return Results.Text(inserted3, "application/json");
        }
        catch (Npgsql.PostgresException pex) when (pex.SqlState == "23505")
        {
            return Results.Json(new { error = "duplicate key", detail = pex.MessageText }, statusCode: 409);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }
}).RequireAuthorization();

// Plan approval routes by reading schema.approval and the object payload, returning concrete steps and candidates.
app.MapPost("/operations/approvals/plan", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    using var body = await JsonDocument.ParseAsync(req.Body);
    var root = body.RootElement;
    var entity = root.GetProperty("entity").GetString()!;
    var objectId = Guid.Parse(root.GetProperty("objectId").GetString()!);
    var plan = await BuildApprovalPlan(ds, cc.ToString()!, entity, objectId);
    return Results.Ok(new { steps = plan });
}).RequireAuthorization();
// Advance approval: current user approve/reject -> log + close step -> move to next step or finish.
app.MapPost("/operations/approvals/next", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    using var body = await JsonDocument.ParseAsync(req.Body);
    var root = body.RootElement;
    var entity = root.GetProperty("entity").GetString()!;
    var objectId = Guid.Parse(root.GetProperty("objectId").GetString()!);
    var action = root.GetProperty("action").GetString()!; // approve|reject
    var comment = root.TryGetProperty("comment", out var c) && c.ValueKind==JsonValueKind.String ? c.GetString() : null;
    var user = Auth.GetUserCtx(req);
    var approverId = user.UserId ?? string.Empty;
    if (string.IsNullOrEmpty(approverId)) return Results.Unauthorized();

    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    // Validation: ensure the current user has a pending task.
    Guid taskId = Guid.Empty; int stepNo = 0; string? stepName = null;
    await using (var pick = conn.CreateCommand())
    {
        pick.CommandText = "SELECT id, step_no, step_name FROM approval_tasks WHERE company_code=$1 AND entity=$2 AND object_id=$3 AND approver_user_id=$4 AND status='pending' LIMIT 1";
        pick.Parameters.AddWithValue(cc.ToString()); pick.Parameters.AddWithValue(entity); pick.Parameters.AddWithValue(objectId); pick.Parameters.AddWithValue(approverId);
        await using var rd = await pick.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) { await tx.RollbackAsync(); return Results.StatusCode(403); }
        taskId = rd.GetGuid(0); stepNo = rd.GetInt32(1); stepName = rd.IsDBNull(2)? null: rd.GetString(2);
    }
    // Complete the current approver task.
    await using (var upd = conn.CreateCommand())
    {
        upd.CommandText = "UPDATE approval_tasks SET status=$2, updated_at=now() WHERE id=$1";
        upd.Parameters.AddWithValue(taskId); upd.Parameters.AddWithValue(action=="approve"? "approved":"rejected");
        await upd.ExecuteNonQueryAsync();
    }

    // If rejected, mark the object status immediately and stop.
    if (string.Equals(action, "reject", StringComparison.OrdinalIgnoreCase))
    {
        var table = Crud.TableFor(entity);
        await using (var upObj = conn.CreateCommand())
        {
            upObj.CommandText = $"UPDATE {table} SET payload = jsonb_set(payload,'{{\"status\"}}', '" + JsonSerializer.Serialize("rejected") + "'::jsonb, true), updated_at=now() WHERE id=$1 AND company_code=$2";
            upObj.Parameters.AddWithValue(objectId); upObj.Parameters.AddWithValue(cc.ToString());
            await upObj.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return Results.Ok(new { ok=true, finished=true, status="rejected" });
    }

    // Determine whether the current step requires all assignees (pick:any skips this).
    bool requireAll = false; // 默认 any
    try
    {
        var schemaDoc = await SchemasService.GetActiveSchema(ds, entity, cc.ToString()!);
        if (schemaDoc is not null)
        {
            var schemaRoot = schemaDoc.RootElement;
            JsonElement ap = default;
            if (schemaRoot.TryGetProperty("schema", out var schemaEl) && schemaEl.ValueKind!=JsonValueKind.Null)
            {
                if (schemaEl.TryGetProperty("approval", out var ap2)) ap = ap2;
            }
            if (ap.ValueKind == JsonValueKind.Undefined && schemaRoot.TryGetProperty("approval", out var apLegacy)) ap = apLegacy; // 兼容旧版列
            if (ap.ValueKind != JsonValueKind.Undefined && ap.TryGetProperty("assignRules", out var ar) && ar.TryGetProperty("pick", out var pk))
                requireAll = string.Equals(pk.GetString(), "all", StringComparison.OrdinalIgnoreCase);
        }
    } catch {}

    if (requireAll)
    {
        // Any pending tasks left in this step?
        await using var chk = conn.CreateCommand();
        chk.CommandText = "SELECT COUNT(1) FROM approval_tasks WHERE company_code=$1 AND entity=$2 AND object_id=$3 AND step_no=$4 AND status='pending'";
        chk.Parameters.AddWithValue(cc.ToString()); chk.Parameters.AddWithValue(entity); chk.Parameters.AddWithValue(objectId); chk.Parameters.AddWithValue(stepNo);
        var left = Convert.ToInt64(await chk.ExecuteScalarAsync());
        if (left > 0)
        {
            await tx.CommitAsync();
            return Results.Ok(new { ok=true, finished=false, advanced=false });
        }
    }
    else
    {
        // pick:any: close the rest of the pending tasks in this step.
        await using var close = conn.CreateCommand();
        close.CommandText = "UPDATE approval_tasks SET status='approved', updated_at=now() WHERE company_code=$1 AND entity=$2 AND object_id=$3 AND step_no=$4 AND status='pending'";
        close.Parameters.AddWithValue(cc.ToString()); close.Parameters.AddWithValue(entity); close.Parameters.AddWithValue(objectId); close.Parameters.AddWithValue(stepNo);
        await close.ExecuteNonQueryAsync();
    }

    // Generate the next step or finish the workflow.
    var plan = await BuildApprovalPlan(ds, cc.ToString()!, entity, objectId);
    var nextStepNo = stepNo + 1;
    var next = plan.FirstOrDefault(s => s.stepNo == nextStepNo);
    if (next.Equals(default((int, string?, System.Collections.Generic.List<(string userId, string? email)>))))
    {
        // Finish: mark the object as approved; for certificate_request send PDF notifications.
        var table = Crud.TableFor(entity);
        await using (var up = conn.CreateCommand())
        {
            up.CommandText = $"UPDATE {table} SET payload = jsonb_set(payload,'{{\"status\"}}','\"approved\"'::jsonb, true), updated_at=now() WHERE id=$1 AND company_code=$2";
            up.Parameters.AddWithValue(objectId); up.Parameters.AddWithValue(cc.ToString());
            await up.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();

        if (string.Equals(entity, "certificate_request", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Generate and send the mail with PDF attached.
                var send = await SendCertificatePdfAsync(app.Configuration, ds, cc.ToString()!, objectId);
                try { return Results.Ok(new { ok=true, finished=true, status="approved", mail = send }); } catch {}
                // Generate the PDF and persist (or keep base64) so the frontend can download it.
                try { await SaveCertificatePdfUrlAsync(app.Configuration, ds, cc.ToString()!, objectId); } catch {}
            }
            catch { }
        }
        return Results.Ok(new { ok=true, finished=true, status="approved" });
    }
    else
    {
        // Insert next-step tasks and fan out notifications.
        var approvers = next.approvers;
        foreach (var apv in approvers)
        {
            await using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO approval_tasks(company_code, entity, object_id, step_no, step_name, approver_user_id, approver_email) VALUES ($1,$2,$3,$4,$5,$6,$7)";
            ins.Parameters.AddWithValue(cc.ToString()); ins.Parameters.AddWithValue(entity); ins.Parameters.AddWithValue(objectId);
            ins.Parameters.AddWithValue(nextStepNo); ins.Parameters.AddWithValue(next.stepName ?? ("Step " + nextStepNo));
            ins.Parameters.AddWithValue(apv.userId); ins.Parameters.AddWithValue((object?)apv.email ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        try { await NotifyApproversAsync(app.Configuration, ds, cc.ToString()!, entity, objectId, nextStepNo); } catch { }
        return Results.Ok(new { ok=true, finished=false, advanced=true, step=nextStepNo });
    }
}).RequireAuthorization();

// AI: compile a natural-language approval policy into the approval JSON skeleton (using Claude).
app.MapPost("/ai/approvals/compile", async (HttpRequest req, NpgsqlDataSource ds, IHttpClientFactory httpClientFactory) =>
{
    var apiKey = req.Headers.TryGetValue("x-anthropic-key", out var hk) && !string.IsNullOrWhiteSpace(hk)
        ? hk.ToString()
        : (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? app.Configuration["Anthropic:ApiKey"]);
    if (string.IsNullOrWhiteSpace(apiKey)) return Results.BadRequest(new { error = "OpenAI API key not configured" });
    using var body = await JsonDocument.ParseAsync(req.Body);
    var nl = body.RootElement.TryGetProperty("nlText", out var t) && t.ValueKind==JsonValueKind.String ? t.GetString() : null;
    if (string.IsNullOrWhiteSpace(nl)) return Results.BadRequest(new { error = "nlText required" });
    var sys = "你是审批策略编译器。将自然语言描述编译成一个 JSON 对象，字段包括 strategy(\"sequential\"), steps[], overrides[], assignRules{resolve[], pick(any|all)}。只输出 JSON。";
    
    var http = httpClientFactory.CreateClient("openai");
    Server.Infrastructure.OpenAiApiHelper.SetOpenAiHeaders(http, apiKey);
    var messages = new object[] { new { role = "system", content = sys }, new { role = "user", content = nl } };
    var openAiResponse = await Server.Infrastructure.OpenAiApiHelper.CallOpenAiAsync(
        http, apiKey, "gpt-4o", messages, temperature: 0, maxTokens: 2000, jsonMode: true);
    
    if (string.IsNullOrWhiteSpace(openAiResponse.Content)) return Results.StatusCode(500);
    try
    {
        var content = openAiResponse.Content ?? "{}";
        // Validate returned text as JSON.
        using var _ = JsonDocument.Parse(content);
        return Results.Text(content, "application/json");
    }
    catch { return Results.Text("{}", "application/json"); }
}).RequireAuthorization();
// Manually seed the first approval step for an object (used for legacy data or rule changes).
app.MapPost("/operations/approvals/init", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    using var body = await JsonDocument.ParseAsync(req.Body);
    var entity = body.RootElement.TryGetProperty("entity", out var e) && e.ValueKind==JsonValueKind.String ? e.GetString() : null;
    var idStr = body.RootElement.TryGetProperty("id", out var i) && i.ValueKind==JsonValueKind.String ? i.GetString() : null;
    if (string.IsNullOrWhiteSpace(entity) || string.IsNullOrWhiteSpace(idStr) || !Guid.TryParse(idStr, out var objectId))
        return Results.BadRequest(new { error = "entity and id required" });

    // Skip creation if a step-1 pending task already exists.
    await using (var conn = await ds.OpenConnectionAsync())
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(1) FROM approval_tasks WHERE company_code=$1 AND entity=$2 AND object_id=$3 AND step_no=1 AND status='pending'";
        cmd.Parameters.AddWithValue(cc.ToString()); cmd.Parameters.AddWithValue(entity!); cmd.Parameters.AddWithValue(objectId);
        var cnt = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        if (cnt > 0) return Results.Ok(new { ok=true, created=false, reason="exists" });
    }

    await InitializeApprovalForObject(app.Configuration, ds, cc.ToString()!, entity!, objectId);
    return Results.Ok(new { ok=true, created=true });
}).RequireAuthorization();

// AI: parse general invoices/documents into suggested entities and payloads (using Claude).
app.MapPost("/ai/documents/parse", async (HttpRequest req, IHttpClientFactory httpClientFactory) =>
{
    var apiKey = req.Headers.TryGetValue("x-anthropic-key", out var hk) && !string.IsNullOrWhiteSpace(hk)
        ? hk.ToString()
        : (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? app.Configuration["Anthropic:ApiKey"]);
    if (string.IsNullOrWhiteSpace(apiKey)) return Results.BadRequest(new { error = "Anthropic API key not configured" });
    var imgs = new List<string>();
    // Try multipart uploads first (Content-Type: multipart/form-data; files[]).
    if (req.HasFormContentType)
    {
        var form = await req.ReadFormAsync();
        foreach (var f in form.Files)
        {
            using var ms = new MemoryStream();
            await f.CopyToAsync(ms);
            var bytes = ms.ToArray();
            var mime = string.IsNullOrWhiteSpace(f.ContentType) ? "image/jpeg" : f.ContentType;
            var b64 = Convert.ToBase64String(bytes);
            imgs.Add($"data:{mime};base64,{b64}");
        }
        var dataUrls = form["dataUrl"]; foreach (var d in dataUrls) if (!string.IsNullOrWhiteSpace(d)) imgs.Add(d!);
    }
    else
    {
        using var body = await JsonDocument.ParseAsync(req.Body);
        var root = body.RootElement;
        if (root.TryGetProperty("images", out var arr) && arr.ValueKind==JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray())
            {
                if (it.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(it.GetString())) imgs.Add(it.GetString()!);
            }
        }
        else if (root.TryGetProperty("imageBase64", out var b64) && b64.ValueKind==JsonValueKind.String)
        {
            var val = b64.GetString(); if (!string.IsNullOrWhiteSpace(val)) imgs.Add(val!);
        }
        else if (root.TryGetProperty("dataUrl", out var du) && du.ValueKind==JsonValueKind.String)
        {
            var val = du.GetString(); if (!string.IsNullOrWhiteSpace(val)) imgs.Add(val!);
        }
    }
    if (imgs.Count == 0) return Results.BadRequest(new { error = "images required (multipart files[] or dataUrl/base64)" });

    // Language defaults: multipart bodies lack a root JSON node, so default to zh; JSON bodies may override.
    var language = "zh";
    try
    {
        if (!req.HasFormContentType)
        {
            using var body2 = await JsonDocument.ParseAsync(req.Body);
            var root2 = body2.RootElement;
            if (root2.TryGetProperty("language", out var lg) && lg.ValueKind==JsonValueKind.String)
                language = lg.GetString() ?? "zh";
        }
    }
    catch {}
    var sys = "你是票据/资料抽取助手。读取用户提供的图片，尽可能提取关键信息，判断应创建的业务实体（voucher/业务凭证、businesspartner/业务伙伴、employee/员工、timesheet/工时、account/科目），为每个合理候选生成标准化 JSON payload。请只输出 JSON。字段命名使用蛇形或语义清晰的英文字段，金额用数字，日期用YYYY-MM-DD。";
    var userText = "请解析这些图片，返回 JSON：{ rawText, fields, suggestedEntity, candidates:[{entity,payload,confidence,reason}] }。若无法确定，也给出 candidates（confidence 较低）。";

    var http = httpClientFactory.CreateClient("openai");
    Server.Infrastructure.OpenAiApiHelper.SetOpenAiHeaders(http, apiKey);
    
    // 构建多模态内容
    var contentParts = new List<object>();
    contentParts.Add(new { type = "text", text = userText + " 语言: " + language });
    foreach (var im in imgs)
    {
        // Accept data URLs or raw base64 (wrap raw bytes as image/jpeg).
        var url = im.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? im : ("data:image/jpeg;base64," + im);
        contentParts.Add(new { type = "image_url", image_url = new { url } });
    }
    
    var messages = new object[]
    {
        new { role = "system", content = sys },
        new { role = "user", content = contentParts.ToArray() }
    };
    
    var openAiResponse = await Server.Infrastructure.OpenAiApiHelper.CallOpenAiAsync(
        http, apiKey, "gpt-4o", messages, temperature: 0, maxTokens: 4096, jsonMode: true);
    
    if (string.IsNullOrWhiteSpace(openAiResponse.Content)) return Results.StatusCode(500);
    try
    {
        var content = openAiResponse.Content ?? "{}";
        using var _ = JsonDocument.Parse(content);
        return Results.Text(content, "application/json");
    }
    catch { return Results.Text("{}", "application/json"); }
}).RequireAuthorization();

// Helpers
static async Task InitializeApprovalForObject(IConfiguration cfg, NpgsqlDataSource ds, string companyCode, string entity, Guid objectId)
{
    var plan = await BuildApprovalPlan(ds, companyCode, entity, objectId);
    var first = plan.FirstOrDefault();
    if (first.Equals(default((int, string?, System.Collections.Generic.List<(string userId, string? email)>)))) return;
    var approvers = first.approvers;
    await using var conn = await ds.OpenConnectionAsync();
    foreach (var apv in approvers)
    {
        await using var ins = conn.CreateCommand();
        ins.CommandText = "INSERT INTO approval_tasks(company_code, entity, object_id, step_no, step_name, approver_user_id, approver_email) VALUES ($1,$2,$3,$4,$5,$6,$7)";
        ins.Parameters.AddWithValue(companyCode); ins.Parameters.AddWithValue(entity); ins.Parameters.AddWithValue(objectId);
        ins.Parameters.AddWithValue(1); ins.Parameters.AddWithValue(first.stepName ?? "Step 1");
        ins.Parameters.AddWithValue(apv.userId); ins.Parameters.AddWithValue((object?)apv.email ?? DBNull.Value);
        await ins.ExecuteNonQueryAsync();
    }
    try { await NotifyApproversAsync(cfg, ds, companyCode, entity, objectId, 1); } catch { }
}

static async Task<List<(int stepNo, string? stepName, List<(string userId, string? email)> approvers)>> BuildApprovalPlan(NpgsqlDataSource ds, string companyCode, string entity, Guid objectId)
{
    // Read schema.approval (prefer schema.approval, fallback to legacy top-level approval) plus the object payload.
    var schemaDoc = await SchemasService.GetActiveSchema(ds, entity, companyCode);
    JsonElement ap = default;
    if (schemaDoc is not null)
    {
        var root = schemaDoc.RootElement;
        if (root.TryGetProperty("schema", out var schemaEl) && schemaEl.ValueKind!=JsonValueKind.Null)
        {
            if (schemaEl.TryGetProperty("approval", out var ap2)) ap = ap2;
        }
        if (ap.ValueKind == JsonValueKind.Undefined && root.TryGetProperty("approval", out var apLegacy)) ap = apLegacy; // 兼容旧版列
    }
    var steps = new List<(int stepNo, string? stepName, List<(string userId, string? email)> approvers)>();
    if (ap.ValueKind == JsonValueKind.Undefined || ap.ValueKind == JsonValueKind.Null)
        return steps;
    // Load the target object payload.
    JsonElement objPayload;
    var table = Crud.TableFor(entity);
    if (string.Equals(entity, "company_setting", StringComparison.OrdinalIgnoreCase))
        await EnsureCompanySettingsTableAsync(ds);
    await using (var conn = await ds.OpenConnectionAsync())
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = $"SELECT payload FROM {table} WHERE company_code=$1 AND id=$2 LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode); cmd.Parameters.AddWithValue(objectId);
        var txt = (string?)await cmd.ExecuteScalarAsync();
        if (string.IsNullOrEmpty(txt)) return steps;
        objPayload = JsonDocument.Parse(txt!).RootElement;
    }
    // Base steps.
    var baseSteps = ap.TryGetProperty("steps", out var sArr) && sArr.ValueKind==JsonValueKind.Array ? sArr.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
    var list = new List<JsonElement>(baseSteps);
    // overrides
    if (ap.TryGetProperty("overrides", out var ov) && ov.ValueKind==JsonValueKind.Array)
    {
        foreach (var rule in ov.EnumerateArray())
        {
            try
            {
                if (!rule.TryGetProperty("when", out var cond)) continue;
                var jpath = cond.TryGetProperty("json", out var jp) ? jp.GetString() : null;
                var ok = false;
                if (!string.IsNullOrEmpty(jpath))
                {
                    var v = ReadJsonPath(objPayload, jpath!);
                    if (cond.TryGetProperty("eq", out var eq)) ok = StringEquals(v, eq);
                    else if (cond.TryGetProperty("gte", out var gte)) ok = CompareNumber(v, gte) >= 0;
                    else if (cond.TryGetProperty("contains", out var ct)) ok = v.IndexOf(ct.GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                if (ok && rule.TryGetProperty("append", out var app) && app.ValueKind==JsonValueKind.Array)
                    list.AddRange(app.EnumerateArray());
            } catch {}
        }
    }
    int idx = 0;
    foreach (var st in list)
    {
        idx++;
        var name = st.TryGetProperty("name", out var nm) && nm.ValueKind==JsonValueKind.String ? nm.GetString() : ($"Step {idx}");
        var whoArr = st.TryGetProperty("who", out var wa) && wa.ValueKind==JsonValueKind.Array ? wa.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
        var approvers = await ResolveApprovers(ds, companyCode, objPayload, whoArr);
        steps.Add((idx, name, approvers));
    }
    return steps;

    static string ReadJsonPath(JsonElement root, string path)
    {
        try{
            var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var cur = root;
            foreach (var p in parts){ if (!cur.TryGetProperty(p, out var nx)) return string.Empty; cur = nx; }
            return cur.ValueKind switch { JsonValueKind.String => cur.GetString() ?? string.Empty, JsonValueKind.Number => cur.ToString(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => cur.ToString() };
        }catch{ return string.Empty; }
    }
    static bool StringEquals(string v, JsonElement rhs){ try{ return string.Equals(v, rhs.GetString(), StringComparison.OrdinalIgnoreCase);}catch{ return false; } }
    static int CompareNumber(string v, JsonElement rhs){ try{ var a=decimal.Parse(v); var b = rhs.TryGetDecimal(out var d)? d : (decimal)rhs.GetDouble(); return a.CompareTo(b);}catch{ return -1; } }
}

static async Task<List<(string userId, string? email)>> ResolveApprovers(NpgsqlDataSource ds, string companyCode, JsonElement payload, JsonElement[] whoArr)
{
    var list = new List<(string, string?)>();
    await using var conn = await ds.OpenConnectionAsync();
    foreach (var who in whoArr)
    {
        try
        {
            var by = who.TryGetProperty("by", out var b) && b.ValueKind==JsonValueKind.String ? b.GetString() : null;
            if (string.Equals(by, "role", StringComparison.OrdinalIgnoreCase))
            {
                var roleCode = who.TryGetProperty("roleCode", out var rc) ? rc.GetString() : null;
                if (string.IsNullOrWhiteSpace(roleCode)) continue;
                await using var q = conn.CreateCommand();
                q.CommandText = @"SELECT u.id::text, COALESCE(e.payload->>'contact.email','')
                                  FROM users u
                                  JOIN user_roles ur ON ur.user_id=u.id
                                  JOIN roles r ON r.id=ur.role_id AND r.role_code=$2
                                  LEFT JOIN employees e ON e.company_code=u.company_code AND e.payload->>'code'=u.employee_code
                                  WHERE u.company_code=$1";
                q.Parameters.AddWithValue(companyCode); q.Parameters.AddWithValue(roleCode!);
                await using var rd = await q.ExecuteReaderAsync();
                while (await rd.ReadAsync()) list.Add((rd.GetString(0), SafeEmail(rd,1)));
            }
            else if (string.Equals(by, "user", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(by, "userId", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(by, "uid", StringComparison.OrdinalIgnoreCase))
            {
                var uid = who.TryGetProperty("userId", out var u1) && u1.ValueKind==JsonValueKind.String ? u1.GetString()
                        : who.TryGetProperty("uid", out var u2) && u2.ValueKind==JsonValueKind.String ? u2.GetString()
                        : who.TryGetProperty("id", out var u3) && u3.ValueKind==JsonValueKind.String ? u3.GetString()
                        : who.TryGetProperty("value", out var u4) && u4.ValueKind==JsonValueKind.String ? u4.GetString()
                        : null;
                if (string.IsNullOrWhiteSpace(uid)) { }
                else
                {
                    await using var q = conn.CreateCommand();
                    q.CommandText = @"SELECT u.id::text, COALESCE(e.payload->>'contact.email','')
                                      FROM users u
                                      LEFT JOIN employees e ON e.company_code=u.company_code AND e.payload->>'code'=u.employee_code
                                      WHERE u.company_code=$1 AND (u.id::text=$2 OR u.employee_code=$2 OR LOWER(u.name)=LOWER($2))
                                      LIMIT 1";
                    q.Parameters.AddWithValue(companyCode); q.Parameters.AddWithValue(uid!.Trim());
                    await using var rd = await q.ExecuteReaderAsync();
                    if (await rd.ReadAsync()) list.Add((rd.GetString(0), SafeEmail(rd,1)));
                }
            }
            else if (string.Equals(by, "capability", StringComparison.OrdinalIgnoreCase) || string.Equals(by, "cap", StringComparison.OrdinalIgnoreCase))
            {
                var cap = who.TryGetProperty("cap", out var cp) ? cp.GetString() : (who.TryGetProperty("capability", out var cp2)? cp2.GetString() : null);
                if (string.IsNullOrWhiteSpace(cap)) continue;
                await using var q = conn.CreateCommand();
                q.CommandText = @"SELECT DISTINCT u.id::text, COALESCE(e.payload->>'contact.email','')
                                  FROM users u
                                  JOIN user_roles ur ON ur.user_id=u.id
                                  JOIN role_caps rc ON rc.role_id=ur.role_id AND rc.cap=$2
                                  LEFT JOIN employees e ON e.company_code=u.company_code AND e.payload->>'code'=u.employee_code
                                  WHERE u.company_code=$1";
                q.Parameters.AddWithValue(companyCode); q.Parameters.AddWithValue(cap!);
                await using var rd = await q.ExecuteReaderAsync();
                while (await rd.ReadAsync()) list.Add((rd.GetString(0), SafeEmail(rd,1)));
            }
            else if (string.Equals(by, "deptManagerOf", StringComparison.OrdinalIgnoreCase))
            {
                // Resolve applicant via payload jsonPath -> fetch department -> find MANAGERs in that department (no escalation for MVP).
                var path = who.TryGetProperty("json", out var jp) ? jp.GetString() : null;
                var empIdText = ReadByPath(payload, path ?? string.Empty);
                string? deptCode = null;
                // Supports UUID or employee code identifiers.
                if (Guid.TryParse(empIdText, out var empGuid))
                {
                    await using var qd = conn.CreateCommand();
                    qd.CommandText = "SELECT payload->>'departmentCode' FROM employees WHERE company_code=$1 AND id=$2 LIMIT 1";
                    qd.Parameters.AddWithValue(companyCode); qd.Parameters.AddWithValue(empGuid);
                    var v = await qd.ExecuteScalarAsync(); deptCode = v as string;
                }
                else if (!string.IsNullOrWhiteSpace(empIdText))
                {
                    await using var qd = conn.CreateCommand();
                    qd.CommandText = "SELECT payload->>'departmentCode' FROM employees WHERE company_code=$1 AND (payload->>'code'=$2 OR id::text=$2) LIMIT 1";
                    qd.Parameters.AddWithValue(companyCode); qd.Parameters.AddWithValue(empIdText);
                    var v = await qd.ExecuteScalarAsync(); deptCode = v as string;
                }
                if (!string.IsNullOrWhiteSpace(deptCode))
                {
                    await using var q = conn.CreateCommand();
                    q.CommandText = @"SELECT u.id::text, COALESCE(e.payload->>'contact.email','')
                                      FROM users u
                                      JOIN user_roles ur ON ur.user_id=u.id
                                      JOIN roles r ON r.id=ur.role_id AND r.role_code='MANAGER'
                                      LEFT JOIN employees e ON e.company_code=u.company_code AND e.payload->>'code'=u.employee_code
                                      WHERE u.company_code=$1 AND u.dept_id=$2";
                    q.Parameters.AddWithValue(companyCode); q.Parameters.AddWithValue(deptCode);
                    await using var rd = await q.ExecuteReaderAsync();
                    while (await rd.ReadAsync()) list.Add((rd.GetString(0), SafeEmail(rd,1)));
                }
            }
        }
        catch { }
    }
    // Deduplicate.
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var uniq = new List<(string,string?)>();
    foreach (var t in list){ if (seen.Add(t.Item1)) uniq.Add(t); }
    return uniq;

    static string ReadByPath(JsonElement root, string? path)
    { try{ if (string.IsNullOrWhiteSpace(path)) return string.Empty; var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries); var cur=root; foreach (var p in parts){ if (!cur.TryGetProperty(p, out var nx)) return string.Empty; cur=nx; } return cur.GetString() ?? cur.ToString(); }catch{ return string.Empty; } }
    static string? SafeEmail(Npgsql.NpgsqlDataReader rd, int idx){ try{ return rd.IsDBNull(idx)? null: (string?)rd.GetString(idx);}catch{ return null; } }
}
static async Task NotifyApproversAsync(IConfiguration cfg, NpgsqlDataSource ds, string companyCode, string entity, Guid objectId, int stepNo)
{
    // Load pending tasks and send email notifications with PDF attachments.
    var agent = (cfg["Agent:Base"] ?? Environment.GetEnvironmentVariable("AGENT_BASE") ?? "http://localhost:3030").TrimEnd('/');
    // Use an HttpClient without the system proxy to avoid local intercepts on 127.0.0.1/localhost.
    var handler = new SocketsHttpHandler { UseProxy = false };
    var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT approver_email FROM approval_tasks WHERE company_code=$1 AND entity=$2 AND object_id=$3 AND step_no=$4 AND status='pending' AND approver_email IS NOT NULL";
    cmd.Parameters.AddWithValue(companyCode); cmd.Parameters.AddWithValue(entity); cmd.Parameters.AddWithValue(objectId); cmd.Parameters.AddWithValue(stepNo);
    var emails = new List<string>();
    await using (var rd = await cmd.ExecuteReaderAsync())
        while (await rd.ReadAsync()) emails.Add(rd.GetString(0));
    if (emails.Count==0) return;

    // Build PDF payload content.
    var table = Crud.TableFor(entity);
    string? subject = $"审批待处理: {entity} #{objectId.ToString().Substring(0,8)}";
    string body = $"请登录系统处理第{stepNo}步审批。";
    var pdfPayload = new { title = "审批请求", company = companyCode, type = entity, purpose = body, date = DateTime.UtcNow.ToString("yyyy-MM-dd"), bodyText = body };
    foreach (var to in emails)
    {
        try
        {
            var content = new { to, subject, textBody = body, pdf = pdfPayload };
            var resp = await http.PostAsync(agent.TrimEnd('/')+"/email/pdf", new StringContent(JsonSerializer.Serialize(content), Encoding.UTF8, "application/json"));
            _ = await resp.Content.ReadAsStringAsync();
        } catch {}
    }
}

static async Task<(string? name, string? address, string? birthday, string? startDate, string? endDate, string? position)> LoadEmployeeBasicsAsync(NpgsqlDataSource ds, string companyCode, string? employeeCodeOrId)
{
    if (string.IsNullOrWhiteSpace(employeeCodeOrId)) return (null, null, null, null, null, null);
    await using var conn = await ds.OpenConnectionAsync();
    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT payload FROM employees WHERE company_code=$1 AND (payload->>'code'=$2 OR employee_code=$2 OR id::text=$2) LIMIT 1";
    q.Parameters.AddWithValue(companyCode);
    q.Parameters.AddWithValue(employeeCodeOrId!);
    var json = (string?)await q.ExecuteScalarAsync();
    if (string.IsNullOrWhiteSpace(json)) return (null, null, null, null, null, null);
    try
    {
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        string? name = null;
        if (root.TryGetProperty("nameKanji", out var nk) && nk.ValueKind==JsonValueKind.String) name = nk.GetString();
        if (string.IsNullOrWhiteSpace(name) && root.TryGetProperty("name", out var nn) && nn.ValueKind==JsonValueKind.String) name = nn.GetString();
        if (string.IsNullOrWhiteSpace(name) && root.TryGetProperty("nameKana", out var nkk) && nkk.ValueKind==JsonValueKind.String) name = nkk.GetString();

        string? address = null;
        if (root.TryGetProperty("contact", out var ct) && ct.ValueKind==JsonValueKind.Object)
            address = ct.TryGetProperty("address", out var ad) && ad.ValueKind==JsonValueKind.String ? ad.GetString() : null;

        string? birthday = root.TryGetProperty("birthDate", out var bd) && bd.ValueKind==JsonValueKind.String ? bd.GetString() : null;

        // employment: start/end from contracts, position from departments
        string? startDate = null; string? endDate = null; string? position = null;
        if (root.TryGetProperty("contracts", out var cs) && cs.ValueKind==JsonValueKind.Array)
        {
            DateTime? minFrom = null; DateTime? maxTo = null; foreach (var c in cs.EnumerateArray())
            {
                var f = c.TryGetProperty("periodFrom", out var pf) && pf.ValueKind==JsonValueKind.String ? pf.GetString() : null;
                var t = c.TryGetProperty("periodTo", out var pt) && (pt.ValueKind==JsonValueKind.String || pt.ValueKind==JsonValueKind.Null) ? (pt.ValueKind==JsonValueKind.Null? null: pt.GetString()) : null;
                if (DateTime.TryParse(f, out var fd)) minFrom = !minFrom.HasValue || fd < minFrom.Value ? fd : minFrom;
                if (!string.IsNullOrWhiteSpace(t) && DateTime.TryParse(t, out var td)) maxTo = !maxTo.HasValue || td > maxTo.Value ? td : maxTo;
            }
            if (minFrom.HasValue) startDate = minFrom.Value.ToString("yyyy-MM-dd");
            if (maxTo.HasValue) endDate = maxTo.Value.ToString("yyyy-MM-dd");
        }
        if (root.TryGetProperty("departments", out var dsArr) && dsArr.ValueKind==JsonValueKind.Array)
        {
            DateTime? bestFrom = null; JsonElement? best = null;
            foreach (var d in dsArr.EnumerateArray())
            {
                var toNull = !(d.TryGetProperty("toDate", out var td) && td.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(td.GetString()));
                if (toNull)
                {
                    best = d; break;
                }
                var fromStr = d.TryGetProperty("fromDate", out var fd) && fd.ValueKind==JsonValueKind.String ? fd.GetString() : null;
                if (DateTime.TryParse(fromStr, out var fdt))
                {
                    if (!bestFrom.HasValue || fdt > bestFrom.Value) { bestFrom = fdt; best = d; }
                }
            }
            if (best.HasValue && best.Value.TryGetProperty("position", out var ps) && ps.ValueKind==JsonValueKind.String)
                position = ps.GetString();
        }
        return (name, address, birthday, startDate, endDate, position);
    }
    catch { return (null, null, null, null, null, null); }
}

static async Task EnsureCompanySettingsTableAsync(NpgsqlDataSource ds)
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"CREATE TABLE IF NOT EXISTS company_settings (
  company_code TEXT PRIMARY KEY,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);";
    try { await cmd.ExecuteNonQueryAsync(); } catch { }
}
static void AddAttachmentUrlsPreserveBlob(JsonNode? node, AzureBlobService blobService)
{
    if (node is null) return;
    if (node is JsonObject obj)
    {
        if (obj.TryGetPropertyValue("attachments", out var attachmentsNode) && attachmentsNode is JsonArray attachmentsArr)
        {
            for (var i = 0; i < attachmentsArr.Count; i++)
            {
                var item = attachmentsArr[i];
                JsonObject? entry = item as JsonObject;

                // legacy: attachments can be stored as a plain string (blobName/id)
                if (entry is null && item is JsonValue jv && jv.TryGetValue<string>(out var asString) && !string.IsNullOrWhiteSpace(asString))
                {
                    entry = new JsonObject
                    {
                        ["id"] = asString,
                        ["blobName"] = asString,
                        ["name"] = asString
                    };
                    attachmentsArr[i] = entry;
                }
                if (entry is null) continue;

                // Prefer blobName; fall back to id for older rows that stored only id.
                string? blobName = null;
                if (entry.TryGetPropertyValue("blobName", out var blobValue) &&
                    blobValue is JsonValue blobJson &&
                    blobJson.TryGetValue<string>(out var blobNameStr) &&
                    !string.IsNullOrWhiteSpace(blobNameStr))
                {
                    blobName = blobNameStr;
                }
                else if (entry.TryGetPropertyValue("id", out var idValue) &&
                         idValue is JsonValue idJson &&
                         idJson.TryGetValue<string>(out var idStr) &&
                         !string.IsNullOrWhiteSpace(idStr))
                {
                    blobName = idStr;
                    // preserve blobName so the frontend / future enrich can rely on it
                    entry["blobName"] = blobName;
                }

                if (!string.IsNullOrWhiteSpace(blobName))
                {
                    try
                    {
                        var sasUri = blobService.GetReadUri(blobName);
                        entry["url"] = sasUri;
                        entry["previewUrl"] = sasUri;
                    }
                    catch (Exception ex)
                    {
                        entry["urlError"] = ex.Message;
                    }
                }
                AddAttachmentUrlsPreserveBlob(entry, blobService);
            }
        }
        foreach (var property in obj)
        {
            AddAttachmentUrlsPreserveBlob(property.Value, blobService);
        }
        return;
    }

    if (node is JsonArray array)
    {
        foreach (var item in array)
        {
            AddAttachmentUrlsPreserveBlob(item, blobService);
        }
    }
}
static async Task<(string? companyName, string? companyAddress, string? companyRep, string? sealDataUrl, double? sealSize, double? sealOffsetX, double? sealOffsetY, double? sealOpacity)> LoadCompanyBasicsAsync(NpgsqlDataSource ds, string companyCode)
{
    string? companyName = null; string? companyAddress = null; string? companyRep = null;
    string? sealDataUrl = null; double? sealSize = null; double? sealOffsetX = null; double? sealOffsetY = null; double? sealOpacity = null;
    await using (var conn = await ds.OpenConnectionAsync())
    {
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT payload FROM company_settings WHERE company_code=$1 LIMIT 1";
            q.Parameters.AddWithValue(companyCode);
            var json = (string?)await q.ExecuteScalarAsync();
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    using var doc = JsonDocument.Parse(json!);
                    var root = doc.RootElement;
                    companyName = root.TryGetProperty("companyName", out var cn) && cn.ValueKind==JsonValueKind.String ? cn.GetString() : companyName;
                    if (string.IsNullOrWhiteSpace(companyName))
                        companyName = root.TryGetProperty("name", out var n) && n.ValueKind==JsonValueKind.String ? n.GetString() : companyName;
                    companyAddress = root.TryGetProperty("companyAddress", out var ca) && ca.ValueKind==JsonValueKind.String ? ca.GetString() : companyAddress;
                    if (string.IsNullOrWhiteSpace(companyAddress))
                        companyAddress = root.TryGetProperty("address", out var ad) && ad.ValueKind==JsonValueKind.String ? ad.GetString() : companyAddress;
                    companyRep = root.TryGetProperty("companyRep", out var cr) && cr.ValueKind==JsonValueKind.String ? cr.GetString() : companyRep;
                    if (string.IsNullOrWhiteSpace(companyRep))
                        companyRep = root.TryGetProperty("representative", out var rp) && rp.ValueKind==JsonValueKind.String ? rp.GetString() : companyRep;
                    if (root.TryGetProperty("seal", out var seal) && seal.ValueKind==JsonValueKind.Object)
                    {
                        var format = seal.TryGetProperty("format", out var fm) && fm.ValueKind==JsonValueKind.String ? (fm.GetString() ?? "png") : "png";
                        if (seal.TryGetProperty("enc", out var enc) && enc.ValueKind==JsonValueKind.String)
                        {
                            var encString = enc.GetString();
                            if (!string.IsNullOrWhiteSpace(encString))
                        {
                            try
                            {
                                    string b64;
                                    if (OperatingSystem.IsWindows())
                                    {
                                        var bytes = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(encString!), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                                        b64 = Convert.ToBase64String(bytes);
                                    }
                                    else
                                    {
                                        // In non-windows environments, we assume the data is not DPAPI encrypted or we skip it.
                                        // For now, just use the string as-is if it's not encrypted, or skip if it is.
                                        // This is a fallback for Linux/macOS.
                                        b64 = encString!;
                                    }
                                    sealDataUrl = $"data:image/{format};base64,{b64}";
                            }
                            catch { }
                            }
                        }
                        if (seal.TryGetProperty("size", out var sz) && sz.ValueKind==JsonValueKind.Number) { if (sz.TryGetDouble(out var d)) sealSize = d; }
                        if (seal.TryGetProperty("offsetX", out var ox) && ox.ValueKind==JsonValueKind.Number) { if (ox.TryGetDouble(out var d)) sealOffsetX = d; }
                        if (seal.TryGetProperty("offsetY", out var oy) && oy.ValueKind==JsonValueKind.Number) { if (oy.TryGetDouble(out var d)) sealOffsetY = d; }
                        if (seal.TryGetProperty("opacity", out var op) && op.ValueKind==JsonValueKind.Number) { if (op.TryGetDouble(out var d)) sealOpacity = d; }
                    }
                }
                catch { }
            }
        }
        if (string.IsNullOrWhiteSpace(companyName))
        {
            await using var q2 = conn.CreateCommand();
            q2.CommandText = "SELECT name FROM companies WHERE company_code=$1 LIMIT 1";
            q2.Parameters.AddWithValue(companyCode);
            var nm = await q2.ExecuteScalarAsync();
            companyName = nm as string;
        }
    }
    return (companyName, companyAddress, companyRep, sealDataUrl, sealSize, sealOffsetX, sealOffsetY, sealOpacity);
}

static async Task<(bool ok, string? body)> SendCertificatePdfAsync(IConfiguration cfg, NpgsqlDataSource ds, string companyCode, Guid objectId)
{
    var agent = cfg["Agent:Base"] ?? Environment.GetEnvironmentVariable("AGENT_BASE") ?? "http://localhost:3030";
    // Disable system proxy so localhost/127.0.0.1 calls are not intercepted.
    var handler = new SocketsHttpHandler { UseProxy = false };
    var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    // Load the certificate request payload.
    string? toEmail = null; string? subject = null; string? type = null; string? purpose = null; string? language = null; string? employeeCode = null; string? bodyText = null;
    await using var conn = await ds.OpenConnectionAsync();
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT payload FROM certificate_requests WHERE company_code=$1 AND id=$2 LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode); cmd.Parameters.AddWithValue(objectId);
        var txt = (string?)await cmd.ExecuteScalarAsync(); if (string.IsNullOrEmpty(txt)) return (false, "request not found");
        using var doc = JsonDocument.Parse(txt!);
        var p = doc.RootElement;
        toEmail = p.TryGetProperty("toEmail", out var te) && te.ValueKind==JsonValueKind.String ? te.GetString() : null;
        subject = p.TryGetProperty("subject", out var sj) && sj.ValueKind==JsonValueKind.String ? sj.GetString() : ("证明书");
        type = p.TryGetProperty("type", out var tp) && tp.ValueKind==JsonValueKind.String ? tp.GetString() : null;
        purpose = p.TryGetProperty("purpose", out var pr) && pr.ValueKind==JsonValueKind.String ? pr.GetString() : null;
        language = p.TryGetProperty("language", out var lg) && lg.ValueKind==JsonValueKind.String ? lg.GetString() : null;
        employeeCode = p.TryGetProperty("employeeId", out var ei) && ei.ValueKind==JsonValueKind.String ? ei.GetString() : null;
        bodyText = p.TryGetProperty("bodyText", out var bt) && bt.ValueKind==JsonValueKind.String ? bt.GetString() : null;
    }
    // Fetch employee name/email; fallback to employee email if toEmail is blank.
    string? empName = null; string? empEmail = null;
    if (!string.IsNullOrWhiteSpace(employeeCode))
    {
        await using var qe = conn.CreateCommand();
        qe.CommandText = "SELECT payload->>'name', COALESCE(payload->>'contact.email', payload->'contact'->>'email') FROM employees WHERE company_code=$1 AND (payload->>'code'=$2 OR id::text=$2) LIMIT 1";
        qe.Parameters.AddWithValue(companyCode); qe.Parameters.AddWithValue(employeeCode!);
        await using var rd = await qe.ExecuteReaderAsync();
        if (await rd.ReadAsync())
        {
            empName = rd.IsDBNull(0) ? null : rd.GetString(0);
            empEmail = rd.IsDBNull(1) ? null : rd.GetString(1);
        }
    }
    if (string.IsNullOrWhiteSpace(toEmail)) toEmail = empEmail;
    if (string.IsNullOrWhiteSpace(toEmail)) return (false, "missing toEmail");
    // Build the PDF payload (choose a template based on request type).
    string? template = null;
    if (!string.IsNullOrWhiteSpace(type))
    {
        var t = type!.ToLowerInvariant();
        if (t.Contains("resignation") || t.Contains("離職") || t.Contains("退職") || t.Contains("离职"))
            template = "jp_resignation_form";
    }
    // Determine the title with language-specific fallbacks.
    string resolvedTitle = subject ?? "证明书";
    if (template == "jp_resignation_form")
    {
        resolvedTitle = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? "Certificate of Resignation"
            : (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? "离职证明书" : "退職証明書");
    }
    var basics = await LoadEmployeeBasicsAsync(ds, companyCode, employeeCode);
    // If no employee name, backfill using the basics info.
    if (string.IsNullOrWhiteSpace(empName)) empName = basics.name;
    var company = await LoadCompanyBasicsAsync(ds, companyCode);
    var pdf = new {
        template,
        title = resolvedTitle,
        company = companyCode,
        companyName = company.companyName,
        companyAddress = company.companyAddress,
        companyRep = company.companyRep,
        seal = string.IsNullOrWhiteSpace(company.sealDataUrl) ? null : new { image = company.sealDataUrl, size = (company.sealSize ?? 56.7), offsetX = (company.sealOffsetX ?? 0.0), offsetY = (company.sealOffsetY ?? 0.0), opacity = (company.sealOpacity ?? 0.8) },
        employee = new { name = empName, code = employeeCode, address = basics.address, birthday = basics.birthday },
        employment = new { startDate = basics.startDate, endDate = basics.endDate, position = basics.position },
        type,
        purpose,
        date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        bodyText = bodyText ?? "",
        docId = objectId.ToString()
    };
    var content = new { to = toEmail, subject = subject, textBody = "请查收附件。", pdf };
    async Task<HttpResponseMessage?> TrySend(string baseUrl)
    {
        try
        {
            Console.WriteLine($"[mail] sending certificate_request id={objectId} to={toEmail} via={baseUrl}/email/pdf");
            return await http.PostAsync(baseUrl.TrimEnd('/')+"/email/pdf", new StringContent(JsonSerializer.Serialize(content), Encoding.UTF8, "application/json"));
        }
        catch { return null; }
    }
    var respSend = await TrySend(agent);
    if (respSend is null || !respSend.IsSuccessStatusCode)
    {
        var alt = agent.Contains("localhost") ? agent.Replace("localhost","127.0.0.1") : agent.Replace("127.0.0.1","localhost");
        if (alt != agent)
        {
            var resp2 = await TrySend(alt);
            if (resp2 is not null) respSend = resp2;
        }
    }
    if (respSend is null) return (false, "send connect failed");
    var text = await respSend.Content.ReadAsStringAsync();
    Console.WriteLine($"[mail] send result status={(int)respSend.StatusCode} ok={respSend.IsSuccessStatusCode} body={(string.IsNullOrWhiteSpace(text)?"<empty>":text)}");
    return (respSend.IsSuccessStatusCode, text);
}

// API: manually generate a PDF for a certificate_request and persist it (pdf.data/filename).
app.MapPost("/operations/certificate_request/{id}/pdf", async (HttpRequest req, Guid id, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    try
    {
        await SaveCertificatePdfUrlAsync(app.Configuration, ds, cc.ToString()!, id);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
}).AllowAnonymous();
// Reopen a certificate request: reset status to pending and rebuild the first approval step.
app.MapPost("/operations/certificate_request/{id}/reopen", async (HttpRequest req, Guid id, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    try
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Reset status.
        await using (var up = conn.CreateCommand())
        {
            up.CommandText = "UPDATE certificate_requests SET payload = jsonb_set(payload,'{status}', to_jsonb('pending'::text), true), updated_at=now() WHERE id=$1 AND company_code=$2";
            up.Parameters.AddWithValue(id); up.Parameters.AddWithValue(cc.ToString());
            var n = await up.ExecuteNonQueryAsync(); if (n==0) { await tx.RollbackAsync(); return Results.NotFound(new { error = "not found" }); }
        }

        // Remove previous approval tasks.
        await using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM approval_tasks WHERE company_code=$1 AND entity='certificate_request' AND object_id=$2";
            del.Parameters.AddWithValue(cc.ToString()); del.Parameters.AddWithValue(id);
            await del.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();

        // Recreate the first approval step and notify approvers.
        await InitializeApprovalForObject(app.Configuration, ds, cc.ToString()!, "certificate_request", id);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
}).RequireAuthorization();

// Download the PDF for a certificate request; auto-generate if missing.
app.MapGet("/operations/certificate_request/{id}/pdf", async (HttpRequest req, Guid id, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    try
    {
        await using var conn = await ds.OpenConnectionAsync();
        string? data = null; string? filename = null; string? url = null;
        async Task ReadAsync()
        {
            await using var q = conn.CreateCommand();
            q.CommandText = "SELECT payload->'pdf'->>'data', payload->'pdf'->>'filename', payload->>'pdfUrl' FROM certificate_requests WHERE company_code=$1 AND id=$2 LIMIT 1";
            q.Parameters.AddWithValue(cc.ToString());
            q.Parameters.AddWithValue(id);
            await using var rd = await q.ExecuteReaderAsync();
            if (await rd.ReadAsync())
            {
                data = rd.IsDBNull(0) ? null : rd.GetString(0);
                filename = rd.IsDBNull(1) ? null : rd.GetString(1);
                url = rd.IsDBNull(2) ? null : rd.GetString(2);
            }
        }
        await ReadAsync();
        if (string.IsNullOrWhiteSpace(data) && string.IsNullOrWhiteSpace(url))
        {
            // Auto-generate and persist (fallback to just-in-time rendering if this fails).
            try { await SaveCertificatePdfUrlAsync(app.Configuration, ds, cc.ToString()!, id); await ReadAsync(); } catch {}
        }
        if (!string.IsNullOrWhiteSpace(data))
        {
            try
            {
                var bytes = Convert.FromBase64String(data!);
                return Results.File(bytes, "application/pdf", string.IsNullOrWhiteSpace(filename) ? "certificate.pdf" : filename);
            }
            catch
            {
                // If base64 is invalid, fall back to url or re-render.
            }
        }
        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                var http = new HttpClient();
                var resp = await http.GetAsync(url!);
                if (resp.IsSuccessStatusCode)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync();
                    var name = string.IsNullOrWhiteSpace(filename) ? (Path.GetFileName(new Uri(url!).AbsolutePath) ?? "certificate.pdf") : filename!;
                    return Results.File(bytes, "application/pdf", name);
                }
                Console.WriteLine($"[certificate_request] fetch pdfUrl failed status={(int)resp.StatusCode} url={url}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"[certificate_request] pdfUrl fetch error: {e.Message}");
            }
        }

        // As a last resort render on the fly and stream back.
        try
        {
            string? type = null; string? purpose = null; string? language = null; string? employeeCode = null; string subject = "证明书"; string? bodyText = null; string? empName = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT payload FROM certificate_requests WHERE company_code=$1 AND id=$2 LIMIT 1";
                cmd.Parameters.AddWithValue(cc.ToString()); cmd.Parameters.AddWithValue(id);
                var txt = (string?)await cmd.ExecuteScalarAsync(); if (!string.IsNullOrEmpty(txt))
                {
                    using var doc = JsonDocument.Parse(txt!);
                    var p = doc.RootElement;
                    subject = p.TryGetProperty("subject", out var sj) && sj.ValueKind==JsonValueKind.String ? (sj.GetString() ?? subject) : subject;
                    type = p.TryGetProperty("type", out var tp) && tp.ValueKind==JsonValueKind.String ? tp.GetString() : null;
                    purpose = p.TryGetProperty("purpose", out var pr) && pr.ValueKind==JsonValueKind.String ? pr.GetString() : null;
                    language = p.TryGetProperty("language", out var lg) && lg.ValueKind==JsonValueKind.String ? lg.GetString() : null;
                    employeeCode = p.TryGetProperty("employeeId", out var ei) && ei.ValueKind==JsonValueKind.String ? ei.GetString() : null;
                    bodyText = p.TryGetProperty("bodyText", out var bt) && bt.ValueKind==JsonValueKind.String ? bt.GetString() : null;
                }
            }
            if (string.IsNullOrWhiteSpace(employeeCode))
            {
                var fromJwt = req.HttpContext.User?.FindFirst("employeeCode")?.Value;
                if (!string.IsNullOrWhiteSpace(fromJwt)) employeeCode = fromJwt;
            }
            var basics = await LoadEmployeeBasicsAsync(ds, cc.ToString()!, employeeCode);
            empName = basics.name;
            // Choose template by request type.
            string? template = null;
            if (!string.IsNullOrWhiteSpace(type))
            {
                var t = type!.ToLowerInvariant();
                if (t.Contains("resignation") || t.Contains("離職") || t.Contains("退職") || t.Contains("离职"))
                    template = "jp_resignation_form";
            }
            string resolvedTitle = subject ?? "证明书";
            if (template == "jp_resignation_form")
            {
                resolvedTitle = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
                    ? "Certificate of Resignation"
                    : (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? "离职证明书" : "退職証明書");
            }
            var company = await LoadCompanyBasicsAsync(ds, cc.ToString()!);
            var pdfPayload = new {
                template,
                title = resolvedTitle,
                company = cc.ToString(),
                companyName = company.companyName,
                companyAddress = company.companyAddress,
                companyRep = company.companyRep,
                seal = string.IsNullOrWhiteSpace(company.sealDataUrl) ? null : new { image = company.sealDataUrl, size = (company.sealSize ?? 56.7), offsetX = (company.sealOffsetX ?? 0.0), offsetY = (company.sealOffsetY ?? 0.0), opacity = (company.sealOpacity ?? 0.8) },
                employee = new { name = empName, code = employeeCode, address = basics.address, birthday = basics.birthday },
                employment = new { startDate = basics.startDate, endDate = basics.endDate, position = basics.position },
                type,
                purpose,
                date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                bodyText = bodyText ?? "",
                docId = id.ToString()
            };
            // Use HttpClient without system proxy to avoid localhost interception.
            var handler = new SocketsHttpHandler { UseProxy = false };
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            var basePref = (app.Configuration["Agent:Base"] ?? Environment.GetEnvironmentVariable("AGENT_BASE") ?? "http://localhost:3030").TrimEnd('/');
            async Task<HttpResponseMessage?> TryRender(string b)
            {
                try
                {
                    return await http.PostAsync(b+"/pdf/render",
                        new StringContent(System.Text.Json.JsonSerializer.Serialize(new { pdf = pdfPayload }), Encoding.UTF8, "application/json"));
                }
                catch
                {
                    return null;
                }
            }
            var resp = await TryRender(basePref);
            if (resp is null || !resp.IsSuccessStatusCode)
            {
                // If configured host is localhost swap to 127.0.0.1 (and vice versa) as a fallback.
                var alt = basePref.Contains("localhost") ? basePref.Replace("localhost","127.0.0.1") : basePref.Replace("127.0.0.1","localhost");
                if (alt != basePref)
                {
                    var resp2 = await TryRender(alt);
                    if (resp2 is not null) resp = resp2;
                }
            }
            if (resp is null)
            {
                return Results.Problem($"render connect failed: tried {basePref} and its 127.0.0.1/localhost alternate", statusCode: 502);
            }
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return Results.Problem("render failed: "+text, statusCode: 502);
            using var parsed = JsonDocument.Parse(text);
            var data2 = parsed.RootElement.GetProperty("data").GetString();
            var name2 = parsed.RootElement.TryGetProperty("filename", out var fn2) && fn2.ValueKind==JsonValueKind.String ? fn2.GetString() : "certificate.pdf";
            var bytes2 = Convert.FromBase64String(data2!);
            return Results.File(bytes2, "application/pdf", name2);
        }
        catch (Exception e)
        {
            return Results.Problem("render error: "+ e.Message);
        }
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
}).RequireAuthorization();
// Generate a PDF and write download information back to the request (MVP stores base64 in payload.pdf.data; can switch to blob URL later).
static async Task SaveCertificatePdfUrlAsync(IConfiguration cfg, NpgsqlDataSource ds, string companyCode, Guid objectId)
{
    var agent = cfg["Agent:Base"] ?? Environment.GetEnvironmentVariable("AGENT_BASE") ?? "http://localhost:3030";
    var http = new HttpClient();
    string? type = null; string? purpose = null; string? language = null; string? employeeCode = null; string? bodyText = null; string subject = "证明书";
    await using var conn = await ds.OpenConnectionAsync();
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT payload FROM certificate_requests WHERE company_code=$1 AND id=$2 LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode); cmd.Parameters.AddWithValue(objectId);
        var txt = (string?)await cmd.ExecuteScalarAsync(); if (string.IsNullOrEmpty(txt)) return;
        using var doc = JsonDocument.Parse(txt!);
        var p = doc.RootElement;
        subject = p.TryGetProperty("subject", out var sj) && sj.ValueKind==JsonValueKind.String ? (sj.GetString() ?? subject) : subject;
        type = p.TryGetProperty("type", out var tp) && tp.ValueKind==JsonValueKind.String ? tp.GetString() : null;
        purpose = p.TryGetProperty("purpose", out var pr) && pr.ValueKind==JsonValueKind.String ? pr.GetString() : null;
        language = p.TryGetProperty("language", out var lg) && lg.ValueKind==JsonValueKind.String ? lg.GetString() : null;
        employeeCode = p.TryGetProperty("employeeId", out var ei) && ei.ValueKind==JsonValueKind.String ? ei.GetString() : null;
        bodyText = p.TryGetProperty("bodyText", out var bt) && bt.ValueKind==JsonValueKind.String ? bt.GetString() : null;
    }
    var basics = await LoadEmployeeBasicsAsync(ds, companyCode, employeeCode);
    string? empName = basics.name;
    string? template2 = null;
    if (!string.IsNullOrWhiteSpace(type))
    {
        var t2 = type!.ToLowerInvariant();
        if (t2.Contains("resignation") || t2.Contains("離職") || t2.Contains("退職") || t2.Contains("离职"))
            template2 = "jp_resignation_form";
    }
    string resolvedTitle2 = subject ?? "证明书";
    if (template2 == "jp_resignation_form")
    {
        resolvedTitle2 = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? "Certificate of Resignation"
            : (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? "离职证明书" : "退職証明書");
    }
    var company = await LoadCompanyBasicsAsync(ds, companyCode);
    var pdfPayload = new {
        template = template2,
        title = resolvedTitle2,
        company = companyCode,
        companyName = company.companyName,
        companyAddress = company.companyAddress,
        companyRep = company.companyRep,
        seal = string.IsNullOrWhiteSpace(company.sealDataUrl) ? null : new { image = company.sealDataUrl, size = (company.sealSize ?? 56.7), offsetX = (company.sealOffsetX ?? 0.0), offsetY = (company.sealOffsetY ?? 0.0), opacity = (company.sealOpacity ?? 0.8) },
        employee = new { name = empName, code = employeeCode, address = basics.address, birthday = basics.birthday },
        employment = new { startDate = basics.startDate, endDate = basics.endDate, position = basics.position },
        type,
        purpose,
        date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        bodyText = bodyText ?? "",
        docId = objectId.ToString()
    };
    var req = new { pdf = pdfPayload };
    async Task<HttpResponseMessage?> TryRender(string b)
    {
        try { return await http.PostAsync(b+"/pdf/render", new StringContent(System.Text.Json.JsonSerializer.Serialize(req), Encoding.UTF8, "application/json")); }
        catch { return null; }
    }
    var resp = await TryRender(agent);
    if (resp is null || !resp.IsSuccessStatusCode)
    {
        var alt = agent.Contains("localhost") ? agent.Replace("localhost","127.0.0.1") : agent.Replace("127.0.0.1","localhost");
        if (alt != agent)
        {
            var resp2 = await TryRender(alt);
            if (resp2 is not null) resp = resp2;
        }
    }
    if (resp is null) return;
    var text = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode) return;
    using var parsed = JsonDocument.Parse(text);
    var data = parsed.RootElement.GetProperty("data").GetString();
    var filename = parsed.RootElement.TryGetProperty("filename", out var fn) && fn.ValueKind==JsonValueKind.String ? fn.GetString() : "certificate.pdf";
    await using (var up = conn.CreateCommand())
    {
        up.CommandText = "UPDATE certificate_requests SET payload = jsonb_set(jsonb_set(payload,'{\"pdf\",\"data\"}', to_jsonb($3::text), true),'{\"pdf\",\"filename\"}', to_jsonb($4::text), true), updated_at=now() WHERE company_code=$1 AND id=$2";
        up.Parameters.AddWithValue(companyCode); up.Parameters.AddWithValue(objectId);
        up.Parameters.AddWithValue((object?)data ?? DBNull.Value); up.Parameters.AddWithValue((object?)filename ?? DBNull.Value);
        await up.ExecuteNonQueryAsync();
    }
}

// Operation: bank receipt allocation - reduce open_items per allocation and generate a receipt voucher (credit by cleared accounts).
app.MapPost("/operations/bank-collect/allocate", async (HttpRequest req, NpgsqlDataSource ds, FinanceService finance) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    using var body = await JsonDocument.ParseAsync(req.Body);
    var root = body.RootElement;
    var header = root.GetProperty("header");
    var allocations = root.GetProperty("allocations").EnumerateArray().ToArray();
    var postingDateRaw = header.GetProperty("postingDate").GetString()!;
    var currency = header.TryGetProperty("currency", out var cur) && cur.ValueKind==JsonValueKind.String ? cur.GetString()! : "JPY";
    var bankAccountCode = header.GetProperty("bankAccountCode").GetString()!;
    decimal feeAmount = 0m;
    string? feeAccountCode = null;
    if (header.TryGetProperty("bankFeeAmount", out var feeEl) && feeEl.ValueKind == JsonValueKind.Number)
    {
        feeAmount = feeEl.GetDecimal();
    }
    if (header.TryGetProperty("bankFeeAccountCode", out var feeAccEl) && feeAccEl.ValueKind == JsonValueKind.String)
    {
        var rawAcc = feeAccEl.GetString();
        if (!string.IsNullOrWhiteSpace(rawAcc)) feeAccountCode = rawAcc;
    }
    if (feeAmount < 0) return Results.BadRequest(new { error = "bank fee must be >= 0" });
    if (feeAmount > 0 && string.IsNullOrWhiteSpace(feeAccountCode)) return Results.BadRequest(new { error = "bank fee account required" });
    // Authorization: requires capability "op:bank-collect" or roles granting the same capability.
    var user = Auth.GetUserCtx(req);
    if (!(user.Caps?.Contains("op:bank-collect") == true))
        return Results.Json(new { error = "forbidden: require capability op:bank-collect" }, statusCode: 403);

    if (!DateTime.TryParse(postingDateRaw, out var postingDateValue))
        return Results.BadRequest(new { error = "invalid postingDate" });
    try
    {
        await finance.EnsureVoucherCreateAllowed(cc.ToString()!, postingDateValue);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    var postingDate = postingDateValue.ToString("yyyy-MM-dd");

    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    var crMap = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    var partnerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    decimal total = 0m;
    foreach (var a in allocations)
    {
        var id = a.GetProperty("openItemId").GetGuid();
        var apply = a.GetProperty("applyAmount").GetDecimal();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT account_code, residual_amount, partner_id FROM open_items WHERE id=$1 AND company_code=$2 FOR UPDATE";
            q.Parameters.AddWithValue(id);
            q.Parameters.AddWithValue(cc.ToString());
            await using var rd = await q.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) { await tx.RollbackAsync(); return Results.BadRequest(new { error = "open item not found" }); }
            var acc = rd.GetString(0);
            var residual = rd.GetDecimal(1);
            var partnerId = rd.IsDBNull(2) ? null : rd.GetString(2);
            if (residual < apply) { await tx.RollbackAsync(); return Results.BadRequest(new { error = "insufficient residual" }); }
            if (!crMap.ContainsKey(acc)) crMap[acc] = 0m;
            crMap[acc] += apply;
            if (!string.IsNullOrWhiteSpace(partnerId)) partnerIds.Add(partnerId);
        }
        await using (var u = conn.CreateCommand())
        {
            u.CommandText = @"UPDATE open_items SET residual_amount = residual_amount - $3,
                               cleared_flag = (residual_amount - $3) <= 0.00001,
                               cleared_at = CASE WHEN (residual_amount - $3) <= 0.00001 THEN now() ELSE cleared_at END,
                               updated_at = now() WHERE id=$1 AND company_code=$2";
            u.Parameters.AddWithValue(id);
            u.Parameters.AddWithValue(cc.ToString());
            u.Parameters.AddWithValue(apply);
            await u.ExecuteNonQueryAsync();
        }
        total += apply;
    }

    if (feeAmount >= total && feeAmount > 0)
    {
        await tx.RollbackAsync();
        return Results.BadRequest(new { error = "bank fee must be less than total applied amount" });
    }

    var netAmount = total - feeAmount;
    if (netAmount <= 0)
    {
        await tx.RollbackAsync();
        return Results.BadRequest(new { error = "net bank amount must be positive" });
    }

    // 查询取引先名称用于生成 summary
    var partnerNames = new List<string>();
    if (partnerIds.Count > 0)
    {
        await using var pCmd = conn.CreateCommand();
        pCmd.CommandText = "SELECT payload->>'name' FROM businesspartners WHERE company_code=$1 AND partner_code = ANY($2)";
        pCmd.Parameters.AddWithValue(cc.ToString());
        pCmd.Parameters.AddWithValue(partnerIds.ToArray());
        await using var pRd = await pCmd.ExecuteReaderAsync();
        while (await pRd.ReadAsync())
        {
            if (!pRd.IsDBNull(0))
            {
                var name = pRd.GetString(0);
                if (!string.IsNullOrWhiteSpace(name)) partnerNames.Add(name);
            }
        }
    }

    // 构建更详细的 summary
    var summaryParts = new List<string> { "銀行入金" };
    if (partnerNames.Count > 0)
    {
        summaryParts.Add(string.Join("・", partnerNames.Take(3)));
        if (partnerNames.Count > 3) summaryParts.Add($"他{partnerNames.Count - 3}件");
    }
    summaryParts.Add($"{total:#,0}{currency}");
    var voucherSummary = string.Join(" | ", summaryParts);

    // Build the receipt voucher (debit bank, credit cleared accounts) and assign a voucher number.
    var numbering = await Server.Infrastructure.VoucherNumberingService.NextAsync(ds, cc.ToString()!, postingDateValue.Date);
    var lines = new List<object>();
    lines.Add(new { lineNo = 1, accountCode = bankAccountCode, drcr = "DR", amount = netAmount });
    int ln = 2;
    if (feeAmount > 0 && !string.IsNullOrWhiteSpace(feeAccountCode))
    {
        lines.Add(new { lineNo = ln++, accountCode = feeAccountCode!, drcr = "DR", amount = feeAmount });
    }
    foreach (var kv in crMap)
    {
        lines.Add(new { lineNo = ln++, accountCode = kv.Key, drcr = "CR", amount = kv.Value });
    }
    var voucherPayloadNode = new JsonObject
    {
        ["header"] = new JsonObject
        {
            ["companyCode"] = cc.ToString(),
            ["postingDate"] = postingDate,
            ["voucherType"] = "IN",
            ["currency"] = currency,
            ["summary"] = voucherSummary
        },
        ["lines"] = JsonNode.Parse(JsonSerializer.Serialize(lines)) ?? new JsonArray()
    };
    finance.ApplyVoucherCreateAudit(voucherPayloadNode, user);
    var voucherPayloadJson = JsonNode.Parse(voucherPayloadNode.ToJsonString())?.ToJsonString();
    Guid voucherId = Guid.Empty;
    await using (var cmd = conn.CreateCommand())
    {
        // vouchers.voucher_no is computed; use jsonb_set to set header.voucherNo before insert.
        cmd.CommandText = @"INSERT INTO vouchers(company_code, payload)
                            VALUES ($1, jsonb_set($2::jsonb, '{header,voucherNo}', to_jsonb($3::text)))
                            RETURNING to_jsonb(vouchers)";
        cmd.Parameters.AddWithValue(cc.ToString()!);
        cmd.Parameters.AddWithValue(voucherPayloadJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue(numbering.voucherNo);
        var json = (string?)await cmd.ExecuteScalarAsync();
        if (json is null) { await tx.RollbackAsync(); return Results.Problem("create receipt failed"); }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var docRoot = doc.RootElement;
            if (docRoot.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && Guid.TryParse(idEl.GetString(), out var parsed))
            {
                voucherId = parsed;
            }
        }
        catch { }
    }

    await tx.CommitAsync();
    return Results.Ok(new { amount = total, voucherNo = numbering.voucherNo, voucherId });
}).RequireAuthorization();

// 智能入金匹配 - 获取客户的可匹配项（请求书优先）
app.MapGet("/operations/payment-matching/{customerCode}", async (string customerCode, HttpRequest req, NpgsqlDataSource ds, Server.Modules.PaymentMatchingService matchingService) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    
    try
    {
        var items = await matchingService.GetMatchableItemsAsync(cc.ToString()!, customerCode);
        return Results.Ok(items);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to get matchable items: {ex.Message}");
    }
}).RequireAuthorization();

// 智能入金匹配 - 自动匹配建议
app.MapPost("/operations/payment-matching/auto-match", async (HttpRequest req, NpgsqlDataSource ds, Server.Modules.PaymentMatchingService matchingService) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    
    using var body = await JsonDocument.ParseAsync(req.Body);
    var root = body.RootElement;
    
    if (!root.TryGetProperty("customerCode", out var ccEl) || ccEl.ValueKind != JsonValueKind.String)
        return Results.BadRequest(new { error = "customerCode required" });
    if (!root.TryGetProperty("amount", out var amtEl))
        return Results.BadRequest(new { error = "amount required" });
    
    var customerCode = ccEl.GetString()!;
    var amount = amtEl.GetDecimal();
    
    try
    {
        var result = await matchingService.AutoMatchAsync(cc.ToString()!, customerCode, amount);
        return Results.Ok(new
        {
            success = result.Success,
            allocations = result.Allocations.Select(a => new { openItemId = a.OpenItemId, amount = a.Amount }),
            totalMatched = result.TotalMatched,
            remaining = result.Remaining,
            message = result.Message
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to auto match: {ex.Message}");
    }
}).RequireAuthorization();

app.MapPost("/operations/bank-payment/allocate", async (HttpRequest req, NpgsqlDataSource ds, FinanceService finance) =>
    {
        if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
            return Results.BadRequest(new { error = "Missing x-company-code" });
        using var body = await JsonDocument.ParseAsync(req.Body);
        var root = body.RootElement;
    var header = root.GetProperty("header");
    var allocations = root.GetProperty("allocations").EnumerateArray().ToArray();
    var postingDateRaw = header.GetProperty("postingDate").GetString()!;
    var currency = header.TryGetProperty("currency", out var cur) && cur.ValueKind==JsonValueKind.String ? cur.GetString()! : "JPY";
    var bankAccountCode = header.GetProperty("bankAccountCode").GetString()!;

    var user = Auth.GetUserCtx(req);
    if (!(user.Caps?.Contains("op:bank-payment") == true))
        return Results.Json(new { error = "forbidden: require capability op:bank-payment" }, statusCode: 403);

    if (!DateTime.TryParse(postingDateRaw, out var postingDateValue))
        return Results.BadRequest(new { error = "invalid postingDate" });
    try
    {
        await finance.EnsureVoucherCreateAllowed(cc.ToString()!, postingDateValue);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    var postingDate = postingDateValue.ToString("yyyy-MM-dd");

        await using var conn = await ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

    if (allocations.Length == 0)
    {
        await tx.RollbackAsync();
        return Results.BadRequest(new { error = "allocations required" });
    }

    var drMap = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    var partnerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    decimal total = 0m;
    foreach (var a in allocations)
    {
        var id = a.GetProperty("openItemId").GetGuid();
        var apply = a.GetProperty("applyAmount").GetDecimal();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT account_code, residual_amount, partner_id FROM open_items WHERE id=$1 AND company_code=$2 FOR UPDATE";
            q.Parameters.AddWithValue(id);
            q.Parameters.AddWithValue(cc.ToString());
            await using var rd = await q.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) { await tx.RollbackAsync(); return Results.BadRequest(new { error = "open item not found" }); }
            var acc = rd.GetString(0);
            var residual = rd.GetDecimal(1);
            var partnerId = rd.IsDBNull(2) ? null : rd.GetString(2);
            if (residual < apply) { await tx.RollbackAsync(); return Results.BadRequest(new { error = "insufficient residual" }); }
            if (!drMap.ContainsKey(acc)) drMap[acc] = 0m;
            drMap[acc] += apply;
            if (!string.IsNullOrWhiteSpace(partnerId)) partnerIds.Add(partnerId);
        }
        await using (var u = conn.CreateCommand())
        {
            u.CommandText = @"UPDATE open_items SET residual_amount = residual_amount - $3,
                               cleared_flag = (residual_amount - $3) <= 0.00001,
                               cleared_at = CASE WHEN (residual_amount - $3) <= 0.00001 THEN now() ELSE cleared_at END,
                               updated_at = now() WHERE id=$1 AND company_code=$2";
            u.Parameters.AddWithValue(id);
            u.Parameters.AddWithValue(cc.ToString());
            u.Parameters.AddWithValue(apply);
            await u.ExecuteNonQueryAsync();
        }
        total += apply;
    }

    // 查询取引先名称用于生成 summary
    var partnerNames = new List<string>();
    if (partnerIds.Count > 0)
    {
        await using var pCmd = conn.CreateCommand();
        pCmd.CommandText = "SELECT payload->>'name' FROM businesspartners WHERE company_code=$1 AND partner_code = ANY($2)";
        pCmd.Parameters.AddWithValue(cc.ToString());
        pCmd.Parameters.AddWithValue(partnerIds.ToArray());
        await using var pRd = await pCmd.ExecuteReaderAsync();
        while (await pRd.ReadAsync())
        {
            if (!pRd.IsDBNull(0))
            {
                var name = pRd.GetString(0);
                if (!string.IsNullOrWhiteSpace(name)) partnerNames.Add(name);
            }
        }
    }

    // 构建更详细的 summary
    var summaryParts = new List<string> { "銀行出金" };
    if (partnerNames.Count > 0)
    {
        summaryParts.Add(string.Join("・", partnerNames.Take(3)));
        if (partnerNames.Count > 3) summaryParts.Add($"他{partnerNames.Count - 3}件");
    }
    summaryParts.Add($"{total:#,0}{currency}");
    var voucherSummary = string.Join(" | ", summaryParts);

    // 手数料处理
    decimal bankFeeAmount = 0m;
    string? bankFeeAccountCode = null;
    if (header.TryGetProperty("bankFeeAmount", out var feeAmtNode) && feeAmtNode.ValueKind == JsonValueKind.Number)
    {
        bankFeeAmount = feeAmtNode.GetDecimal();
    }
    if (header.TryGetProperty("bankFeeAccountCode", out var feeAccNode) && feeAccNode.ValueKind == JsonValueKind.String)
    {
        bankFeeAccountCode = feeAccNode.GetString();
    }
    // 当社負担の場合、実際の出金額 = 消込金額 + 手数料
    var actualPayment = total + bankFeeAmount;

    var numbering = await Server.Infrastructure.VoucherNumberingService.NextAsync(ds, cc.ToString()!, postingDateValue.Date);
    var linesList = new List<object> { new { lineNo = 1, accountCode = bankAccountCode, drcr = "CR", amount = actualPayment } };
    int ln = 2;
    foreach (var kv in drMap)
    {
        linesList.Add(new { lineNo = ln++, accountCode = kv.Key, drcr = "DR", amount = kv.Value });
    }
    // 手数料行（当社負担の場合のみ）
    if (bankFeeAmount > 0 && !string.IsNullOrWhiteSpace(bankFeeAccountCode))
    {
        linesList.Add(new { lineNo = ln++, accountCode = bankFeeAccountCode, drcr = "DR", amount = bankFeeAmount });
    }

    // 更新 summary 包含手数料信息
    if (bankFeeAmount > 0)
    {
        voucherSummary += $" (手数料 {bankFeeAmount:#,0})";
    }

    var voucherPayloadNode = new JsonObject
    {
        ["header"] = new JsonObject
        {
            ["companyCode"] = cc.ToString(),
            ["postingDate"] = postingDate,
            ["voucherType"] = "OT",
            ["currency"] = currency,
            ["summary"] = voucherSummary
        },
        ["lines"] = JsonNode.Parse(JsonSerializer.Serialize(linesList)) ?? new JsonArray()
    };
    finance.ApplyVoucherCreateAudit(voucherPayloadNode, user);
    var voucherPayloadJson = JsonNode.Parse(voucherPayloadNode.ToJsonString())?.ToJsonString();

    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"INSERT INTO vouchers(company_code, payload)
                            VALUES ($1, jsonb_set($2::jsonb, '{header,voucherNo}', to_jsonb($3::text)))
                            RETURNING to_jsonb(vouchers)";
        cmd.Parameters.AddWithValue(cc.ToString()!);
        cmd.Parameters.AddWithValue(voucherPayloadJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue(numbering.voucherNo);
        var json = (string?)await cmd.ExecuteScalarAsync();
        if (json is null) { await tx.RollbackAsync(); return Results.Problem("create payment failed"); }
        }

        await tx.CommitAsync();
    return Results.Ok(new { amount = actualPayment, clearingAmount = total, feeAmount = bankFeeAmount, voucherNo = numbering.voucherNo });
}).RequireAuthorization();

bool HasFinancialReportCapability(Auth.UserCtx ctx)
    => (ctx.Caps?.Contains("report:financial") ?? false)
       || (ctx.Caps?.Contains("roles:manage") ?? false)
       || (ctx.Roles?.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase) || string.Equals(r, "FinanceManager", StringComparison.OrdinalIgnoreCase)) ?? false);

// bool TryParseMonth(string? value, out DateOnly month)
// {
//     if (!string.IsNullOrWhiteSpace(value))
//     {
//         if (DateOnly.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out month))
//             return true;
//         if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
//         {
//             month = new DateOnly(day.Year, day.Month, 1);
//             return true;
//         }
//     }
//     month = default;
//     return false;
// }

app.MapPost("/financial/fs-nodes/import-template", async (HttpRequest req, FinancialStatementService financial) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    var user = Auth.GetUserCtx(req);
    if (!HasFinancialReportCapability(user))
        return Results.Json(new { error = "forbidden: require capability report:financial" }, statusCode: 403);
    try
    {
        await financial.SeedDefaultTemplateAsync(cc.ToString()!, req.HttpContext.RequestAborted);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapGet("/financial/fs-nodes", async (HttpRequest req, [FromQuery] string? statement, FinancialStatementService financial) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    var user = Auth.GetUserCtx(req);
    if (!HasFinancialReportCapability(user))
        return Results.Json(new { error = "forbidden: require capability report:financial" }, statusCode: 403);
    try
    {
        var nodes = await financial.ListNodesAsync(cc.ToString()!, statement, req.HttpContext.RequestAborted);
        return Results.Json(nodes);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapPost("/financial/fs-nodes", async (HttpRequest req, FinancialStatementService financial) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    var user = Auth.GetUserCtx(req);
    if (!HasFinancialReportCapability(user))
        return Results.Json(new { error = "forbidden: require capability report:financial" }, statusCode: 403);
    var input = await req.ReadFromJsonAsync<FinancialStatementService.FsNodeInput>(cancellationToken: req.HttpContext.RequestAborted);
    if (input is null) return Results.BadRequest(new { error = "invalid payload" });
    try
    {
        var dto = await financial.CreateNodeAsync(cc.ToString()!, input, req.HttpContext.RequestAborted);
        return Results.Json(dto);
    }
    catch (PostgresException pg) when (pg.SqlState == "23505")
    {
        return Results.BadRequest(new { error = "code already exists" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapPut("/financial/fs-nodes/{id:guid}", async (HttpRequest req, Guid id, FinancialStatementService financial) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    var user = Auth.GetUserCtx(req);
    if (!HasFinancialReportCapability(user))
        return Results.Json(new { error = "forbidden: require capability report:financial" }, statusCode: 403);
    var input = await req.ReadFromJsonAsync<FinancialStatementService.FsNodeInput>(cancellationToken: req.HttpContext.RequestAborted);
    if (input is null) return Results.BadRequest(new { error = "invalid payload" });
    try
    {
        var dto = await financial.UpdateNodeAsync(cc.ToString()!, id, input, req.HttpContext.RequestAborted);
        return Results.Json(dto);
    }
    catch (PostgresException pg) when (pg.SqlState == "23505")
    {
        return Results.BadRequest(new { error = "code already exists" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();
app.MapDelete("/financial/fs-nodes/{id:guid}", async (HttpRequest req, Guid id, FinancialStatementService financial) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    var user = Auth.GetUserCtx(req);
    if (!HasFinancialReportCapability(user))
        return Results.Json(new { error = "forbidden: require capability report:financial" }, statusCode: 403);
    try
    {
        await financial.DeleteNodeAsync(cc.ToString()!, id, req.HttpContext.RequestAborted);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();
// Generic delete: physical removal for MVP (can switch to soft delete later).
// Note: /reports/financial/balance-sheet and /reports/financial/income-statement moved to FinanceReportsModule
app.MapGet("/objects/employee/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds, AzureBlobService blobService) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });

    var schemaDoc = await SchemasService.GetActiveSchema(ds, "employee", cc.ToString());
    if (schemaDoc is null) return Results.NotFound(new { error = "schema not found" });

    var user = Auth.GetUserCtx(req);
    if (!Auth.IsActionAllowed(schemaDoc, "read", user)) return Results.StatusCode(403);

    var (scopeSql, scopeArgs, _) = Auth.BuildAuthScopes(schemaDoc, user, 3);
    var json = await Crud.GetDetailJson(ds, "employees", id, cc.ToString()!, scopeSql, scopeArgs);
    if (json is null) return Results.NotFound(new { error = "not found" });

    try
    {
        var node = JsonNode.Parse(json);
        AddAttachmentUrlsPreserveBlob(node, blobService);
        return Results.Text(node?.ToJsonString() ?? json, "application/json");
    }
    catch
    {
        return Results.Text(json, "application/json");
    }
}).RequireAuthorization();

app.MapPut("/objects/employee/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });

    using var body = await JsonDocument.ParseAsync(req.Body);
    if (!body.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        return Results.BadRequest(new { error = "payload required" });

    var schemaDoc = await SchemasService.GetActiveSchema(ds, "employee", cc.ToString());
    if (schemaDoc is null) return Results.NotFound(new { error = "schema not found" });

    var user = Auth.GetUserCtx(req);
    if (!Auth.IsActionAllowed(schemaDoc, "update", user)) return Results.StatusCode(403);

    try
    {
        var schema = JsonSchema.FromText(schemaDoc.RootElement.GetProperty("schema").GetRawText());
        var validation = schema.Evaluate(payload, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!validation.IsValid)
        {
            var errors = validation.Details
                .Where(d => !d.IsValid)
                .Select(d => d.InstanceLocation?.ToString() ?? d.EvaluationPath.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList();
            return Results.BadRequest(new { error = "schema validation failed", details = errors });
        }

    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"schema load failed: {ex.Message}" });
    }

    var updated = await Crud.UpdateRawJson(ds, "employees", id, cc.ToString()!, payload.GetRawText());
    if (updated is null) return Results.NotFound(new { error = "not found" });

    return Results.Text(updated, "application/json");
}).RequireAuthorization();

app.MapPost("/employees/{id:guid}/attachments", async (Guid id, HttpRequest req, AzureBlobService blobService, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });

    var schemaDoc = await SchemasService.GetActiveSchema(ds, "employee", cc.ToString());
    if (schemaDoc is null) return Results.NotFound(new { error = "schema not found" });

    var user = Auth.GetUserCtx(req);
    if (!Auth.IsActionAllowed(schemaDoc, "update", user)) return Results.StatusCode(403);

    string fileNameHeader = req.Headers.TryGetValue("X-File-Name", out var fn) ? fn.ToString() : string.Empty;
    var originalName = string.IsNullOrWhiteSpace(fileNameHeader) ? "attachment.bin" : Uri.UnescapeDataString(fileNameHeader);
    var contentType = string.IsNullOrWhiteSpace(req.ContentType) ? "application/octet-stream" : req.ContentType!;

    await using var buffer = new MemoryStream();
    await req.Body.CopyToAsync(buffer, req.HttpContext.RequestAborted);
    if (buffer.Length == 0) return Results.BadRequest(new { error = "empty file" });
    buffer.Position = 0;

    var extension = Path.GetExtension(originalName);
    var normalizedExt = string.IsNullOrWhiteSpace(extension) ? string.Empty : extension.ToLowerInvariant();
    var blobName = $"hr/employees/{cc}/{id:D}/{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}{normalizedExt}";

    AzureBlobUploadResult uploadResult;
    try
    {
        uploadResult = await blobService.UploadAsync(buffer, blobName, contentType, req.HttpContext.RequestAborted);
    }
    catch (Exception ex)
    {
        return Results.Problem($"上传文件到 Azure Storage 失败: {ex.Message}");
    }

    var (scopeSql, scopeArgs, _) = Auth.BuildAuthScopes(schemaDoc, user, 3);
    var existingJson = await Crud.GetDetailJson(ds, "employees", id, cc.ToString()!, scopeSql, scopeArgs);
    if (existingJson is null) return Results.NotFound(new { error = "not found" });

    JsonObject payloadObject;
    try
    {
        var root = JsonNode.Parse(existingJson)?.AsObject() ?? new JsonObject();
        payloadObject = root["payload"] as JsonObject ?? new JsonObject();
    }
    catch
    {
        payloadObject = new JsonObject();
    }

    if (payloadObject["attachments"] is not JsonArray attachmentsArray)
    {
        attachmentsArray = new JsonArray();
        payloadObject["attachments"] = attachmentsArray;
    }

    var attachmentEntry = new JsonObject
    {
        ["id"] = Guid.NewGuid().ToString(),
        ["fileName"] = originalName,
        ["blobName"] = uploadResult.BlobName,
        ["contentType"] = uploadResult.ContentType,
        ["size"] = uploadResult.Size,
        ["uploadedAt"] = DateTimeOffset.UtcNow.ToString("O")
    };
    if (!string.IsNullOrWhiteSpace(user.UserId)) attachmentEntry["uploadedBy"] = user.UserId;
    attachmentsArray.Add(attachmentEntry);

    var updatedPayloadJson = payloadObject.ToJsonString();
    var updatedRowJson = await Crud.UpdateRawJson(ds, "employees", id, cc.ToString()!, updatedPayloadJson);
    if (updatedRowJson is null) return Results.NotFound(new { error = "update failed" });

    try
    {
        var updatedNode = JsonNode.Parse(updatedRowJson);
        AddAttachmentUrlsPreserveBlob(updatedNode, blobService);
        return Results.Text(updatedNode?.ToJsonString() ?? updatedRowJson, "application/json");
    }
    catch
    {
        return Results.Text(updatedRowJson, "application/json");
    }
}).RequireAuthorization();

// Generic delete: physical removal for MVP (can change to soft delete later).
app.MapDelete("/objects/{entity}/{id}", async (HttpRequest req, string entity, Guid id, NpgsqlDataSource ds, FinanceService finance, AzureBlobService blobService) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    var table = Crud.TableFor(entity);
    var schemaDoc = await SchemasService.GetActiveSchema(ds, entity, req.Headers.TryGetValue("x-company-code", out var h3) ? h3.ToString() : null);
    if (schemaDoc is not null)
    {
    var user = Auth.GetUserCtx(req);
        if (!Auth.IsActionAllowed(schemaDoc, "delete", user)) return Results.StatusCode(403);
    }
    // Track attachments to delete for voucher entity
    var manualAttachmentBlobNames = new List<string>();
    if (entity == "voucher")
    {
        string? existingPayloadJson;
        await using (var connFetch = await ds.OpenConnectionAsync())
        await using (var cmdFetch = connFetch.CreateCommand())
        {
            cmdFetch.CommandText = $"SELECT payload FROM {table} WHERE id=$1 AND company_code=$2";
            cmdFetch.Parameters.AddWithValue(id);
            cmdFetch.Parameters.AddWithValue(cc.ToString());
            existingPayloadJson = (string?)await cmdFetch.ExecuteScalarAsync();
        }
        if (existingPayloadJson is null) return Results.NotFound(new { error = "not found" });
        try
        {
            await finance.EnsureVoucherDeleteAllowed(cc.ToString()!, existingPayloadJson, id);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        
        // Extract manual attachments to delete from Azure Storage
        // Only delete attachments with source="manual", keep agent attachments (managed by chatbot tasks)
        try
        {
            using var payloadDoc = JsonDocument.Parse(existingPayloadJson);
            if (payloadDoc.RootElement.TryGetProperty("attachments", out var attachmentsEl) && 
                attachmentsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var att in attachmentsEl.EnumerateArray())
                {
                    // Check source field - only delete "manual" source or unknown source
                    var source = att.TryGetProperty("source", out var srcEl) && srcEl.ValueKind == JsonValueKind.String
                        ? srcEl.GetString() : null;
                    
                    // Delete if source is "manual" or not set (legacy data, assume manual)
                    // Keep if source is "agent" (managed by chatbot tasks)
                    if (!string.Equals(source, "agent", StringComparison.OrdinalIgnoreCase))
                    {
                        if (att.TryGetProperty("blobName", out var blobEl) && blobEl.ValueKind == JsonValueKind.String)
                        {
                            var blobName = blobEl.GetString();
                            if (!string.IsNullOrWhiteSpace(blobName))
                            {
                                manualAttachmentBlobNames.Add(blobName);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore errors parsing attachments - proceed with delete
        }
    }
    else if (entity == "account")
    {
        // 检查科目是否被引用
        string? accountCode;
        await using (var connFetch = await ds.OpenConnectionAsync())
        await using (var cmdFetch = connFetch.CreateCommand())
        {
            cmdFetch.CommandText = $"SELECT payload->>'code' FROM {table} WHERE id=$1 AND company_code=$2";
            cmdFetch.Parameters.AddWithValue(id);
            cmdFetch.Parameters.AddWithValue(cc.ToString());
            accountCode = (string?)await cmdFetch.ExecuteScalarAsync();
        }
        if (string.IsNullOrWhiteSpace(accountCode)) return Results.NotFound(new { error = "not found" });
        try
        {
            await finance.EnsureAccountDeleteAllowed(cc.ToString()!, accountCode, req.HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
    else if (entity == "businesspartner")
    {
        // 检查取引先是否被引用
        try
        {
            await finance.EnsureBusinessPartnerDeleteAllowed(cc.ToString()!, id, req.HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
    // For voucher entity, delete related open_items before deleting the voucher
    if (entity == "voucher")
    {
        await using var connOi = await ds.OpenConnectionAsync();

        // 1. 重置被此凭证清账的其他凭证的 open_items（还原为未清账状态）
        await using var cmdResetCleared = connOi.CreateCommand();
        cmdResetCleared.CommandText = @"
            UPDATE open_items
            SET residual_amount = original_amount,
                cleared_flag = false,
                cleared_at = NULL,
                cleared_by = NULL,
                refs = refs - 'clearingVoucherNo' - 'clearingLineNo' - 'clearingHistory',
                updated_at = now()
            WHERE company_code = $1
              AND cleared_flag = true
              AND (
                cleared_by = (SELECT voucher_no FROM vouchers WHERE id = $2 AND company_code = $1)
                OR refs->>'clearingVoucherId' = $2::text
              )";
        cmdResetCleared.Parameters.AddWithValue(cc.ToString());
        cmdResetCleared.Parameters.AddWithValue(id);
        var resetClearedCount = await cmdResetCleared.ExecuteNonQueryAsync();
        if (resetClearedCount > 0)
        {
            Console.WriteLine($"[voucher-delete] Reset {resetClearedCount} open_item(s) cleared by deleted voucher {id} back to uncleared");
        }

        // 2. 删除此凭证自身的 open_items
        await using var cmdOi = connOi.CreateCommand();
        cmdOi.CommandText = "DELETE FROM open_items WHERE company_code=$1 AND voucher_id=$2";
        cmdOi.Parameters.AddWithValue(cc.ToString());
        cmdOi.Parameters.AddWithValue(id);
        await cmdOi.ExecuteNonQueryAsync();
        
        // Reset moneytree_transactions that reference this voucher back to 'pending' status
        await using var connMt = await ds.OpenConnectionAsync();
        await using var cmdMt = connMt.CreateCommand();
        cmdMt.CommandText = @"
            UPDATE moneytree_transactions 
            SET posting_status = 'pending',
                posting_error = NULL,
                voucher_id = NULL,
                voucher_no = NULL,
                rule_id = NULL,
                rule_title = NULL,
                cleared_open_item_id = NULL,
                posting_run_id = NULL,
                updated_at = now()
            WHERE company_code = $1 AND voucher_id = $2";
        cmdMt.Parameters.AddWithValue(cc.ToString());
        cmdMt.Parameters.AddWithValue(id);
        var resetCount = await cmdMt.ExecuteNonQueryAsync();
        if (resetCount > 0)
        {
            Console.WriteLine($"[voucher-delete] Reset {resetCount} moneytree transaction(s) to pending status");
        }
    }
    
    var n = await Crud.DeleteById(ds, table, id, cc.ToString()!);
    if (n == 0) return Results.NotFound(new { error = "not found" });
    
    // After successful delete, clean up manual attachment blobs from Azure Storage
    if (entity == "voucher" && manualAttachmentBlobNames.Count > 0 && blobService.IsConfigured)
    {
        foreach (var blobName in manualAttachmentBlobNames)
        {
            try
            {
                await blobService.DeleteAsync(blobName, req.HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                // Log but don't fail the delete operation
                Console.WriteLine($"[voucher-delete] Failed to delete blob {blobName}: {ex.Message}");
            }
        }
    }
    
    return Results.Ok(new { ok = true });
}).RequireAuthorization();
// AI file analysis and intent inference (deprecated endpoint).
app.MapPost("/ai/files/analyze", (HttpRequest req) =>
{
    return Results.Json(new { error = "deprecated" }, statusCode: 410);
}).RequireAuthorization();

var listWorkflowRulesHandler = async (HttpRequest req, WorkflowRulesService workflowRules) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "missing company" });
    var list = await workflowRules.ListAsync(cc.ToString(), req.HttpContext.RequestAborted);
    return Results.Json(list.Select(WorkflowRuleResponseHelper.ToRuleResponse));
};

var getWorkflowRuleHandler = async (HttpRequest req, string ruleKey, WorkflowRulesService workflowRules) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "missing company" });
    var rule = await workflowRules.GetAsync(cc.ToString(), ruleKey, req.HttpContext.RequestAborted);
    if (rule is null) return Results.NotFound();
    return Results.Json(WorkflowRuleResponseHelper.ToRuleResponse(rule));
};

var interpretWorkflowRuleHandler = async (HttpRequest req, WorkflowRulesService workflowRules) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "missing company" });
    var apiKey = AiFileHelpers.ResolveOpenAIApiKey(req, app.Configuration);
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.BadRequest(new { error = "未配置 Anthropic API Key" });
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var prompt = doc.RootElement.TryGetProperty("prompt", out var promptEl) && promptEl.ValueKind == JsonValueKind.String
        ? promptEl.GetString()
        : null;
    if (string.IsNullOrWhiteSpace(prompt))
        return Results.BadRequest(new { error = "prompt required" });
    var existing = await workflowRules.ListAsync(cc.ToString(), req.HttpContext.RequestAborted);
    var draft = await AiFileHelpers.GenerateWorkflowRuleAsync(apiKey!, prompt!, existing);
    return Results.Json(new
    {
        draft.RuleKey,
        draft.Title,
        draft.Description,
        draft.Instructions,
        actions = JsonNode.Parse(draft.Actions.ToJsonString()),
        draft.Priority,
        draft.IsActive
    });
};

var upsertWorkflowRuleHandler = async (HttpRequest req, string? ruleKeyParam, WorkflowRulesService workflowRules) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "missing company" });
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    var ruleKey = ruleKeyParam ?? (root.TryGetProperty("ruleKey", out var keyEl) && keyEl.ValueKind == JsonValueKind.String ? keyEl.GetString() ?? string.Empty : string.Empty);
    if (string.IsNullOrWhiteSpace(ruleKey))
        return Results.BadRequest(new { error = "ruleKey required" });
    var title = root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String ? titleEl.GetString() ?? string.Empty : string.Empty;
    var description = root.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String ? descEl.GetString() ?? string.Empty : string.Empty;
    var instructions = root.TryGetProperty("instructions", out var instEl) && instEl.ValueKind == JsonValueKind.String ? instEl.GetString() ?? string.Empty : string.Empty;
    var priority = root.TryGetProperty("priority", out var priEl) && priEl.ValueKind == JsonValueKind.Number ? priEl.GetInt32() : 100;
    var isActive = !root.TryGetProperty("isActive", out var activeEl) || activeEl.ValueKind != JsonValueKind.False;
    JsonNode? actionsNode = null;
    if (root.TryGetProperty("actions", out var actionsEl) && actionsEl.ValueKind == JsonValueKind.Array)
    {
        actionsNode = JsonNode.Parse(actionsEl.GetRawText());
    }
    var rule = await workflowRules.UpsertAsync(cc.ToString(), ruleKey, title, description, instructions, actionsNode, priority, isActive, req.HttpContext.RequestAborted);
    return Results.Json(WorkflowRuleResponseHelper.ToRuleResponse(rule));
};

var deleteWorkflowRuleHandler = async (HttpRequest req, string ruleKey, WorkflowRulesService workflowRules) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "missing company" });
    await workflowRules.DeleteAsync(cc.ToString(), ruleKey, req.HttpContext.RequestAborted);
    return Results.Json(new { ok = true });
};

var testWorkflowRuleHandler = async (HttpRequest req, VoucherAutomationService automationService) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "missing company" });
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    var ruleKey = root.TryGetProperty("ruleKey", out var keyEl) && keyEl.ValueKind == JsonValueKind.String ? keyEl.GetString() : null;
    if (string.IsNullOrWhiteSpace(ruleKey)) return Results.BadRequest(new { error = "ruleKey required" });
    if (!root.TryGetProperty("payload", out var payloadEl) || payloadEl.ValueKind != JsonValueKind.Object)
        return Results.BadRequest(new { error = "payload required" });
    var payloadObj = JsonNode.Parse(payloadEl.GetRawText()) as JsonObject ?? new JsonObject();
    payloadObj["ruleKey"] = ruleKey;
    var result = await automationService.CreateVoucherFromDocumentAsync(cc.ToString(), payloadObj, Auth.GetUserCtx(req), req.HttpContext.RequestAborted);
    return Results.Json(new
    {
        result.Success,
        result.Message,
        result.Issues,
        result.VoucherNo,
        result.VoucherId,
        result.Confidence,
        payload = result.Payload
    });
};

app.MapGet("/ai/workflow-rules", listWorkflowRulesHandler).RequireAuthorization();
app.MapGet("/api/ai/workflow-rules", listWorkflowRulesHandler).RequireAuthorization();

app.MapGet("/ai/workflow-rules/{ruleKey}", getWorkflowRuleHandler).RequireAuthorization();
app.MapGet("/api/ai/workflow-rules/{ruleKey}", getWorkflowRuleHandler).RequireAuthorization();

app.MapPost("/ai/workflow-rules/interpret", interpretWorkflowRuleHandler).RequireAuthorization();
app.MapPost("/api/ai/workflow-rules/interpret", interpretWorkflowRuleHandler).RequireAuthorization();

app.MapPost("/ai/workflow-rules", (HttpRequest req, WorkflowRulesService workflowRules) => upsertWorkflowRuleHandler(req, null, workflowRules)).RequireAuthorization();
app.MapPost("/api/ai/workflow-rules", (HttpRequest req, WorkflowRulesService workflowRules) => upsertWorkflowRuleHandler(req, null, workflowRules)).RequireAuthorization();

app.MapPut("/ai/workflow-rules/{ruleKey}", (HttpRequest req, string ruleKey, WorkflowRulesService workflowRules) => upsertWorkflowRuleHandler(req, ruleKey, workflowRules)).RequireAuthorization();
app.MapPut("/api/ai/workflow-rules/{ruleKey}", (HttpRequest req, string ruleKey, WorkflowRulesService workflowRules) => upsertWorkflowRuleHandler(req, ruleKey, workflowRules)).RequireAuthorization();

app.MapDelete("/ai/workflow-rules/{ruleKey}", deleteWorkflowRuleHandler).RequireAuthorization();
app.MapDelete("/api/ai/workflow-rules/{ruleKey}", deleteWorkflowRuleHandler).RequireAuthorization();

app.MapPost("/ai/workflows/test", testWorkflowRuleHandler).RequireAuthorization();
app.MapPost("/api/ai/workflows/test", testWorkflowRuleHandler).RequireAuthorization();

app.MapGet("/ai/agent/accounting-rules", async (HttpRequest req, AgentAccountingRuleService service) =>
{
    var includeAll = req.Query.TryGetValue("all", out var allVal) &&
                     ((bool.TryParse(allVal, out var parsedBool) && parsedBool) || string.Equals(allVal.ToString(), "1", StringComparison.OrdinalIgnoreCase));
    return await AgentAccountingRuleEndpoints.ListAsync(req, service, includeAll);
}).RequireAuthorization();

app.MapGet("/api/ai/agent/accounting-rules", async (HttpRequest req, AgentAccountingRuleService service) =>
{
    var includeAll = req.Query.TryGetValue("all", out var allVal) &&
                     ((bool.TryParse(allVal, out var parsedBool) && parsedBool) || string.Equals(allVal.ToString(), "1", StringComparison.OrdinalIgnoreCase));
    return await AgentAccountingRuleEndpoints.ListAsync(req, service, includeAll);
}).RequireAuthorization();

app.MapGet("/ai/agent/accounting-rules/{id:guid}", (HttpRequest req, Guid id, AgentAccountingRuleService service) => AgentAccountingRuleEndpoints.GetAsync(req, id, service)).RequireAuthorization();
app.MapGet("/api/ai/agent/accounting-rules/{id:guid}", (HttpRequest req, Guid id, AgentAccountingRuleService service) => AgentAccountingRuleEndpoints.GetAsync(req, id, service)).RequireAuthorization();

app.MapPost("/ai/agent/accounting-rules", (HttpRequest req, AgentAccountingRuleService service) => AgentAccountingRuleEndpoints.UpsertAsync(req, null, service)).RequireAuthorization();
app.MapPost("/api/ai/agent/accounting-rules", (HttpRequest req, AgentAccountingRuleService service) => AgentAccountingRuleEndpoints.UpsertAsync(req, null, service)).RequireAuthorization();

app.MapPut("/ai/agent/accounting-rules/{id:guid}", (HttpRequest req, Guid id, AgentAccountingRuleService service) => AgentAccountingRuleEndpoints.UpsertAsync(req, id, service)).RequireAuthorization();
app.MapPut("/api/ai/agent/accounting-rules/{id:guid}", (HttpRequest req, Guid id, AgentAccountingRuleService service) => AgentAccountingRuleEndpoints.UpsertAsync(req, id, service)).RequireAuthorization();

app.MapDelete("/ai/agent/accounting-rules/{id:guid}", (HttpRequest req, Guid id, AgentAccountingRuleService service) => AgentAccountingRuleEndpoints.DeleteAsync(req, id, service)).RequireAuthorization();
app.MapDelete("/api/ai/agent/accounting-rules/{id:guid}", (HttpRequest req, Guid id, AgentAccountingRuleService service) => AgentAccountingRuleEndpoints.DeleteAsync(req, id, service)).RequireAuthorization();

app.MapGet("/ai/agent-scenarios", async (HttpRequest req, AgentScenarioService service) =>
{
    var includeAll = req.Query.TryGetValue("all", out var allVal) &&
                     ((bool.TryParse(allVal, out var parsedBool) && parsedBool) || string.Equals(allVal.ToString(), "1", StringComparison.OrdinalIgnoreCase));
    return await AgentScenarioEndpoints.ListAsync(req, service, includeAll);
}).RequireAuthorization();

app.MapGet("/api/ai/agent-scenarios", async (HttpRequest req, AgentScenarioService service) =>
{
    var includeAll = req.Query.TryGetValue("all", out var allVal) &&
                     ((bool.TryParse(allVal, out var parsedBool) && parsedBool) || string.Equals(allVal.ToString(), "1", StringComparison.OrdinalIgnoreCase));
    return await AgentScenarioEndpoints.ListAsync(req, service, includeAll);
}).RequireAuthorization();

app.MapGet("/ai/agent-scenarios/{scenarioKey}", (HttpRequest req, string scenarioKey, AgentScenarioService service) => AgentScenarioEndpoints.GetAsync(req, scenarioKey, service)).RequireAuthorization();
app.MapGet("/api/ai/agent-scenarios/{scenarioKey}", (HttpRequest req, string scenarioKey, AgentScenarioService service) => AgentScenarioEndpoints.GetAsync(req, scenarioKey, service)).RequireAuthorization();

app.MapPost("/ai/agent-scenarios", (HttpRequest req, AgentScenarioService service) => AgentScenarioEndpoints.UpsertAsync(req, null, service)).RequireAuthorization();
app.MapPost("/api/ai/agent-scenarios", (HttpRequest req, AgentScenarioService service) => AgentScenarioEndpoints.UpsertAsync(req, null, service)).RequireAuthorization();

app.MapPut("/ai/agent-scenarios/{scenarioKey}", (HttpRequest req, string scenarioKey, AgentScenarioService service) => AgentScenarioEndpoints.UpsertAsync(req, scenarioKey, service)).RequireAuthorization();
app.MapPut("/api/ai/agent-scenarios/{scenarioKey}", (HttpRequest req, string scenarioKey, AgentScenarioService service) => AgentScenarioEndpoints.UpsertAsync(req, scenarioKey, service)).RequireAuthorization();

app.MapDelete("/ai/agent-scenarios/{scenarioKey}", (HttpRequest req, string scenarioKey, AgentScenarioService service) => AgentScenarioEndpoints.DeleteAsync(req, scenarioKey, service)).RequireAuthorization();
app.MapDelete("/api/ai/agent-scenarios/{scenarioKey}", (HttpRequest req, string scenarioKey, AgentScenarioService service) => AgentScenarioEndpoints.DeleteAsync(req, scenarioKey, service)).RequireAuthorization();

app.MapPost("/ai/agent-scenarios/test", (HttpRequest req, AgentKitService agentKit) => AgentScenarioEndpoints.TestAsync(req, agentKit)).RequireAuthorization();
app.MapPost("/api/ai/agent-scenarios/test", (HttpRequest req, AgentKitService agentKit) => AgentScenarioEndpoints.TestAsync(req, agentKit)).RequireAuthorization();

app.MapPost("/ai/agent-scenarios/interpret", (HttpRequest req, AgentScenarioService service, AgentKitService agentKit) => AgentScenarioEndpoints.InterpretAsync(req, service, agentKit, app.Configuration)).RequireAuthorization();
app.MapPost("/api/ai/agent-scenarios/interpret", (HttpRequest req, AgentScenarioService service, AgentKitService agentKit) => AgentScenarioEndpoints.InterpretAsync(req, service, agentKit, app.Configuration)).RequireAuthorization();

// ===== Unified Agent Skills API =====
app.MapGet("/ai/agent/skills", (HttpRequest req, AgentSkillService svc) => AgentSkillEndpoints.ListSkillsAsync(req, svc)).RequireAuthorization();
app.MapGet("/api/ai/agent/skills", (HttpRequest req, AgentSkillService svc) => AgentSkillEndpoints.ListSkillsAsync(req, svc)).RequireAuthorization();

app.MapGet("/ai/agent/skills/key/{skillKey}", (HttpRequest req, string skillKey, AgentSkillService svc) => AgentSkillEndpoints.GetSkillAsync(req, skillKey, svc)).RequireAuthorization();
app.MapGet("/api/ai/agent/skills/key/{skillKey}", (HttpRequest req, string skillKey, AgentSkillService svc) => AgentSkillEndpoints.GetSkillAsync(req, skillKey, svc)).RequireAuthorization();

app.MapGet("/ai/agent/skills/{id:guid}", (HttpRequest req, Guid id, AgentSkillService svc) => AgentSkillEndpoints.GetSkillByIdAsync(req, id, svc)).RequireAuthorization();
app.MapGet("/api/ai/agent/skills/{id:guid}", (HttpRequest req, Guid id, AgentSkillService svc) => AgentSkillEndpoints.GetSkillByIdAsync(req, id, svc)).RequireAuthorization();

app.MapPost("/ai/agent/skills", (HttpRequest req, AgentSkillService svc) => AgentSkillEndpoints.UpsertSkillAsync(req, svc)).RequireAuthorization();
app.MapPost("/api/ai/agent/skills", (HttpRequest req, AgentSkillService svc) => AgentSkillEndpoints.UpsertSkillAsync(req, svc)).RequireAuthorization();

app.MapDelete("/ai/agent/skills/{id:guid}", (HttpRequest req, Guid id, AgentSkillService svc) => AgentSkillEndpoints.DeleteSkillAsync(req, id, svc)).RequireAuthorization();
app.MapDelete("/api/ai/agent/skills/{id:guid}", (HttpRequest req, Guid id, AgentSkillService svc) => AgentSkillEndpoints.DeleteSkillAsync(req, id, svc)).RequireAuthorization();

// Skill Rules
app.MapGet("/ai/agent/skills/{skillId:guid}/rules", (HttpRequest req, Guid skillId, AgentSkillService svc) => AgentSkillEndpoints.ListRulesAsync(req, skillId, svc)).RequireAuthorization();
app.MapGet("/api/ai/agent/skills/{skillId:guid}/rules", (HttpRequest req, Guid skillId, AgentSkillService svc) => AgentSkillEndpoints.ListRulesAsync(req, skillId, svc)).RequireAuthorization();

app.MapPost("/ai/agent/skills/{skillId:guid}/rules", (HttpRequest req, Guid skillId, AgentSkillService svc) => AgentSkillEndpoints.UpsertRuleAsync(req, skillId, svc)).RequireAuthorization();
app.MapPost("/api/ai/agent/skills/{skillId:guid}/rules", (HttpRequest req, Guid skillId, AgentSkillService svc) => AgentSkillEndpoints.UpsertRuleAsync(req, skillId, svc)).RequireAuthorization();

app.MapDelete("/ai/agent/skills/rules/{ruleId:guid}", (HttpRequest req, Guid ruleId, AgentSkillService svc) => AgentSkillEndpoints.DeleteRuleAsync(req, ruleId, svc)).RequireAuthorization();
app.MapDelete("/api/ai/agent/skills/rules/{ruleId:guid}", (HttpRequest req, Guid ruleId, AgentSkillService svc) => AgentSkillEndpoints.DeleteRuleAsync(req, ruleId, svc)).RequireAuthorization();

// Skill Examples
app.MapGet("/ai/agent/skills/{skillId:guid}/examples", (HttpRequest req, Guid skillId, AgentSkillService svc) => AgentSkillEndpoints.ListExamplesAsync(req, skillId, svc)).RequireAuthorization();
app.MapGet("/api/ai/agent/skills/{skillId:guid}/examples", (HttpRequest req, Guid skillId, AgentSkillService svc) => AgentSkillEndpoints.ListExamplesAsync(req, skillId, svc)).RequireAuthorization();

app.MapPost("/ai/agent/skills/{skillId:guid}/examples", (HttpRequest req, Guid skillId, AgentSkillService svc) => AgentSkillEndpoints.UpsertExampleAsync(req, skillId, svc)).RequireAuthorization();
app.MapPost("/api/ai/agent/skills/{skillId:guid}/examples", (HttpRequest req, Guid skillId, AgentSkillService svc) => AgentSkillEndpoints.UpsertExampleAsync(req, skillId, svc)).RequireAuthorization();

app.MapDelete("/ai/agent/skills/examples/{exampleId:guid}", (HttpRequest req, Guid exampleId, AgentSkillService svc) => AgentSkillEndpoints.DeleteExampleAsync(req, exampleId, svc)).RequireAuthorization();
app.MapDelete("/api/ai/agent/skills/examples/{exampleId:guid}", (HttpRequest req, Guid exampleId, AgentSkillService svc) => AgentSkillEndpoints.DeleteExampleAsync(req, exampleId, svc)).RequireAuthorization();

// AgentKit conversation entrypoint.
app.MapPost("/ai/agent/message", async (HttpRequest req, AgentKitService agentKit) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
    var root = doc.RootElement;
    var message = root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String ? msgEl.GetString() : null;
    if (string.IsNullOrWhiteSpace(message))
    {
        return Results.BadRequest(new { error = "message 必填" });
    }
    Guid? sessionId = null;
    if (root.TryGetProperty("sessionId", out var sidEl) && sidEl.ValueKind == JsonValueKind.String && Guid.TryParse(sidEl.GetString(), out var sidGuid))
    {
        sessionId = sidGuid;
    }
    string? scenarioKey = null;
    if (root.TryGetProperty("scenarioKey", out var scenarioEl) && scenarioEl.ValueKind == JsonValueKind.String)
    {
        scenarioKey = scenarioEl.GetString();
    }
    string? answerTo = null;
    if (root.TryGetProperty("answerTo", out var answerEl) && answerEl.ValueKind == JsonValueKind.String)
    {
        answerTo = answerEl.GetString();
    }
    Guid? taskId = null;
    if (root.TryGetProperty("taskId", out var taskEl) && taskEl.ValueKind == JsonValueKind.String && Guid.TryParse(taskEl.GetString(), out var parsedTaskId))
    {
        taskId = parsedTaskId;
    }
    string? bankTransactionId = null;
    if (root.TryGetProperty("bankTransactionId", out var bankTxEl) && bankTxEl.ValueKind == JsonValueKind.String)
    {
        bankTransactionId = bankTxEl.GetString();
    }
    var language = root.TryGetProperty("language", out var langEl) && langEl.ValueKind == JsonValueKind.String
        ? langEl.GetString()
        : (req.Headers.TryGetValue("x-lang", out var langHeader) ? langHeader.ToString() : null);
    var companyCode = req.Headers.TryGetValue("x-company-code", out var ccValue) ? ccValue.ToString() ?? "JP01" : "JP01";
    var apiKey = AiFileHelpers.ResolveOpenAIApiKey(req, app.Configuration);
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return Results.BadRequest(new { error = "未配置 Anthropic API Key" });
    }
    var userCtx = Auth.GetUserCtx(req);
    if (root.TryGetProperty("documentSessionId", out var docSessionEl) && docSessionEl.ValueKind == JsonValueKind.String)
    {
        var desiredSession = docSessionEl.GetString();
        if (!string.IsNullOrWhiteSpace(desiredSession) && sessionId.HasValue)
        {
            await agentKit.SetActiveDocumentSessionIdAsync(sessionId.Value, desiredSession, req.HttpContext.RequestAborted);
        }
    }

    var result = await agentKit.ProcessUserMessageAsync(new AgentKitService.AgentMessageRequest(
        sessionId,
        companyCode,
        userCtx,
        message!,
        apiKey!,
        language ?? "ja",
        id => uploadedFiles.TryGetValue(id, out var record) ? record : null,
        scenarioKey,
        answerTo,
        taskId,
        bankTransactionId),
        req.HttpContext.RequestAborted);
    var activeDocumentSessionId = await agentKit.GetActiveDocumentSessionIdAsync(result.SessionId, req.HttpContext.RequestAborted);
    return Results.Json(new
    {
        sessionId = result.SessionId,
        messages = result.Messages,
        activeDocumentSessionId
    });
}).RequireAuthorization();

app.MapPost("/ai/sessions/{sessionId:guid}/document-session", async (Guid sessionId, HttpRequest req, AgentKitService agentKit, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "missing company" });
    var uid = req.Headers.TryGetValue("x-user-id", out var userHeader) ? userHeader.ToString() : null;
    if (string.IsNullOrWhiteSpace(uid))
        return Results.Unauthorized();

    await using var conn = await ds.OpenConnectionAsync();
    await using (var verify = conn.CreateCommand())
    {
        verify.CommandText = "SELECT company_code, user_id FROM ai_sessions WHERE id=$1";
        verify.Parameters.AddWithValue(sessionId);
        await using var reader = await verify.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return Results.NotFound();
        }
        var company = reader.GetString(0);
        var user = reader.GetString(1);
        if (!string.Equals(company, cc.ToString(), StringComparison.Ordinal) || !string.Equals(user, uid, StringComparison.Ordinal))
        {
            return Results.Forbid();
        }
    }

    using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
    var root = doc.RootElement;
    string? documentSessionId = null;
    if (root.TryGetProperty("documentSessionId", out var sessionEl) && sessionEl.ValueKind == JsonValueKind.String)
    {
        documentSessionId = sessionEl.GetString();
    }
    await agentKit.SetActiveDocumentSessionIdAsync(sessionId, string.IsNullOrWhiteSpace(documentSessionId) ? null : documentSessionId, req.HttpContext.RequestAborted);
    var activeDocumentSessionId = await agentKit.GetActiveDocumentSessionIdAsync(sessionId, req.HttpContext.RequestAborted);
    return Results.Json(new { sessionId, activeDocumentSessionId });
}).RequireAuthorization();

app.MapGet("/ai/sessions/{sessionId:guid}/document-session", async (Guid sessionId, HttpRequest req, AgentKitService agentKit, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "missing company" });
    var uid = req.Headers.TryGetValue("x-user-id", out var userHeader) ? userHeader.ToString() : null;
    if (string.IsNullOrWhiteSpace(uid))
        return Results.Unauthorized();

    await using var conn = await ds.OpenConnectionAsync();
    await using (var verify = conn.CreateCommand())
    {
        verify.CommandText = "SELECT company_code, user_id FROM ai_sessions WHERE id=$1";
        verify.Parameters.AddWithValue(sessionId);
        await using var reader = await verify.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return Results.NotFound();
        }
        var company = reader.GetString(0);
        var user = reader.GetString(1);
        if (!string.Equals(company, cc.ToString(), StringComparison.Ordinal) || !string.Equals(user, uid, StringComparison.Ordinal))
        {
            return Results.Forbid();
        }
    }

    var activeDocumentSessionId = await agentKit.GetActiveDocumentSessionIdAsync(sessionId, req.HttpContext.RequestAborted);
    return Results.Json(new { sessionId, activeDocumentSessionId });
}).RequireAuthorization();
// AgentKit file upload entrypoint.
app.MapPost("/ai/agent/upload", async (HttpRequest req, AgentKitService agentKit, AzureBlobService blobService) =>
{
    if (!req.HasFormContentType)
    {
        return Results.BadRequest(new { error = "multipart/form-data required" });
    }
    var form = await req.ReadFormAsync(req.HttpContext.RequestAborted);
    var file = form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "file required" });
    }
    const long maxUploadSize = 20L * 1024 * 1024;
    if (file.Length > maxUploadSize)
    {
        return Results.BadRequest(new { error = "单个文件不能超过 20MB" });
    }

    var companyCode = req.Headers.TryGetValue("x-company-code", out var ccValue) ? ccValue.ToString() ?? "JP01" : "JP01";
    Guid? sessionId = null;
    if (form.TryGetValue("sessionId", out var sidValue) && Guid.TryParse(sidValue.ToString(), out var parsed))
    {
        sessionId = parsed;
    }
    string? scenarioKey = null;
    if (form.TryGetValue("scenarioKey", out var scenarioValue))
    {
        scenarioKey = scenarioValue.ToString();
    }

    var apiKey = AiFileHelpers.ResolveOpenAIApiKey(req, app.Configuration);
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return Results.BadRequest(new { error = "未配置 Anthropic API Key" });
    }

    var ext = Path.GetExtension(file.FileName ?? string.Empty);
    var fileId = Guid.NewGuid().ToString("n");
    var storedPath = Path.Combine(uploadRoot, string.IsNullOrWhiteSpace(ext) ? fileId : fileId + ext);
    await using (var fs = System.IO.File.Create(storedPath))
    {
        await file.CopyToAsync(fs, req.HttpContext.RequestAborted);
    }

    var blobName = string.Empty;
    try
    {
        blobName = string.IsNullOrWhiteSpace(ext)
            ? $"{companyCode.ToLowerInvariant()}/{DateTime.UtcNow:yyyy/MM/dd}/{fileId}"
            : $"{companyCode.ToLowerInvariant()}/{DateTime.UtcNow:yyyy/MM/dd}/{fileId}{ext}";
        await using var uploadStream = System.IO.File.OpenRead(storedPath);
        await blobService.UploadAsync(uploadStream, blobName, file.ContentType ?? "application/octet-stream", req.HttpContext.RequestAborted);
    }
    catch (Exception ex)
    {
        // Clean up the temporary file before returning an error.
        try { System.IO.File.Delete(storedPath); } catch { }
        return Results.Problem($"上传文件到 Azure Storage 失败: {ex.Message}");
    }

    uploadedFiles[fileId] = new UploadedFileRecord(
        file.FileName ?? "uploaded",
        storedPath,
        file.ContentType ?? "application/octet-stream",
        file.Length,
        DateTimeOffset.UtcNow,
        companyCode,
        req.Headers.TryGetValue("x-user-id", out var uidValue) ? uidValue.ToString() : null,
        blobName);

    var language = form.TryGetValue("language", out var langVal) ? langVal.ToString() : (req.Headers.TryGetValue("x-lang", out var langHeader) ? langHeader.ToString() : null);
    var userCtx = Auth.GetUserCtx(req);
    string? answerTo = null;
    if (form.TryGetValue("answerTo", out var answerValue))
    {
        var answerRaw = answerValue.ToString();
        if (!string.IsNullOrWhiteSpace(answerRaw)) answerTo = answerRaw.Trim();
    }

    var result = await agentKit.ProcessFileAsync(new AgentKitService.AgentFileRequest(
        sessionId,
        companyCode,
        userCtx,
        fileId,
        file.FileName ?? "uploaded",
        file.ContentType ?? "application/octet-stream",
        file.Length,
        apiKey!,
        language ?? "ja",
        id => uploadedFiles.TryGetValue(id, out var record) ? record : null,
        scenarioKey,
        blobName,
        null,
        null,
        answerTo),
        req.HttpContext.RequestAborted);

    return Results.Json(new
    {
        sessionId = result.SessionId,
        fileId,
        messages = result.Messages
    });
}).RequireAuthorization();
app.MapPost("/ai/agent/tasks", async (HttpRequest req, AgentKitService agentKit, AzureBlobService blobService, InvoiceTaskService taskService) =>
{
    if (!req.HasFormContentType)
    {
        return Results.BadRequest(new { error = "multipart/form-data required" });
    }

    var form = await req.ReadFormAsync(req.HttpContext.RequestAborted);
    if (form.Files.Count == 0)
    {
        return Results.BadRequest(new { error = "至少上传一个文件" });
    }

    var companyCode = req.Headers.TryGetValue("x-company-code", out var ccValue) ? ccValue.ToString() ?? "JP01" : "JP01";
    Guid? sessionId = null;
    if (form.TryGetValue("sessionId", out var sidValue) && Guid.TryParse(sidValue.ToString(), out var parsedSession))
    {
        sessionId = parsedSession;
    }
    string? scenarioKey = null;
    if (form.TryGetValue("scenarioKey", out var scenarioValue))
    {
        scenarioKey = scenarioValue.ToString();
    }
    var message = form.TryGetValue("message", out var messageValue) ? messageValue.ToString() : null;
    string? answerTo = null;
    if (form.TryGetValue("answerTo", out var answerValue))
    {
        var answerRaw = answerValue.ToString();
        if (!string.IsNullOrWhiteSpace(answerRaw)) answerTo = answerRaw.Trim();
    }
    var language = form.TryGetValue("language", out var languageValue)
        ? languageValue.ToString()
        : (req.Headers.TryGetValue("x-lang", out var langHeader) ? langHeader.ToString() : null);

    var apiKey = AiFileHelpers.ResolveOpenAIApiKey(req, app.Configuration);
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return Results.BadRequest(new { error = "未配置 Anthropic API Key" });
    }

    var userCtx = Auth.GetUserCtx(req);
    var savedFiles = new List<AgentKitService.UploadedFileEnvelope>();

    foreach (var file in form.Files)
    {
        if (file.Length <= 0)
        {
            continue;
        }
        const long maxUploadSize = 20L * 1024 * 1024;
        if (file.Length > maxUploadSize)
        {
            return Results.BadRequest(new { error = $"文件 {file.FileName} 超出 20MB 限制" });
        }

        var ext = Path.GetExtension(file.FileName ?? string.Empty);
        var fileId = Guid.NewGuid().ToString("n");
        var storedPath = Path.Combine(uploadRoot, string.IsNullOrWhiteSpace(ext) ? fileId : fileId + ext);
        await using (var fs = System.IO.File.Create(storedPath))
        {
            await file.CopyToAsync(fs, req.HttpContext.RequestAborted);
        }

        var blobName = string.Empty;
        try
        {
            blobName = string.IsNullOrWhiteSpace(ext)
                ? $"{companyCode.ToLowerInvariant()}/{DateTime.UtcNow:yyyy/MM/dd}/{fileId}"
                : $"{companyCode.ToLowerInvariant()}/{DateTime.UtcNow:yyyy/MM/dd}/{fileId}{ext}";
            await using var uploadStream = System.IO.File.OpenRead(storedPath);
            await blobService.UploadAsync(uploadStream, blobName, file.ContentType ?? "application/octet-stream", req.HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            try { System.IO.File.Delete(storedPath); } catch { }
            return Results.Problem($"上传文件到 Azure Storage 失败: {ex.Message}");
        }

        var uploadedRecord = new UploadedFileRecord(
            file.FileName ?? "uploaded",
            storedPath,
            file.ContentType ?? "application/octet-stream",
            file.Length,
            DateTimeOffset.UtcNow,
            companyCode,
            req.Headers.TryGetValue("x-user-id", out var uidValue) ? uidValue.ToString() : null,
            blobName);

        uploadedFiles[fileId] = uploadedRecord;
        savedFiles.Add(new AgentKitService.UploadedFileEnvelope(fileId, uploadedRecord));
    }

    if (savedFiles.Count == 0)
    {
        return Results.BadRequest(new { error = "上传文件内容为空" });
    }

    var prepResult = await agentKit.PrepareAgentTaskAsync(
        new AgentKitService.AgentTaskRequest(sessionId, companyCode, userCtx, message, apiKey!, language ?? "ja", scenarioKey, answerTo, id => uploadedFiles.TryGetValue(id, out var record) ? record : null),
        savedFiles,
        req.HttpContext.RequestAborted);

    foreach (var doc in prepResult.Documents)
    {
        if (uploadedFiles.TryGetValue(doc.FileId, out var existing))
        {
            uploadedFiles[doc.FileId] = existing with { Analysis = doc.Data?.DeepClone()?.AsObject() };
        }
    }

    try
    {
        var planningRequest = new AgentKitService.AgentTaskPlanningRequest(
            prepResult.SessionId,
            companyCode,
            userCtx,
            message,
            apiKey!,
            language ?? "ja",
            scenarioKey,
            prepResult.Clarification,
            prepResult.ActiveDocumentSessionId);
        var planResult = await agentKit.PlanTaskGroupsAsync(planningRequest, prepResult.Documents, req.HttpContext.RequestAborted);
        if (planResult is not null)
        {
            if (planResult.Plans.Count > 0 || planResult.UnassignedDocuments.Count > 0)
            {
                var summaryMessage = BuildPlanSummaryMessage(planResult, prepResult.Documents);
                await agentKit.LogAssistantMessageAsync(prepResult.SessionId, summaryMessage, null, req.HttpContext.RequestAborted);
            }

            if (planResult.Plans.Count > 0)
            {
                var executionRequest = new AgentKitService.AgentTaskExecutionRequest(
                    prepResult.SessionId,
                    companyCode,
                    userCtx,
                    apiKey!,
                    language ?? "ja",
                    planResult.Plans,
                    prepResult.Documents,
                    prepResult.Clarification,
                    prepResult.ActiveDocumentSessionId,
                    prepResult.Tasks);
                await agentKit.ExecuteTaskGroupsAsync(executionRequest, req.HttpContext.RequestAborted);
            }
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "[AgentKit] 执行任务失败 sessionId={SessionId}", prepResult.SessionId);
        var errorText = $"执行任务失败：{ex.Message}";
        var errorMessage = new AgentKitService.AgentResultMessage("assistant", errorText, "error", null);
        if (prepResult.Tasks.Count > 0)
        {
            foreach (var task in prepResult.Tasks)
            {
                try
                {
                    await agentKit.LogAssistantMessageAsync(prepResult.SessionId, errorMessage, task.TaskId, req.HttpContext.RequestAborted);
                    await taskService.UpdateStatusAsync(task.TaskId, "failed", new JsonObject
                    {
                        ["error"] = errorText
                    }, req.HttpContext.RequestAborted);
                }
                catch (Exception updateEx)
                {
                    app.Logger.LogWarning(updateEx, "[AgentKit] 标记任务失败 taskId={TaskId}", task.TaskId);
                }
            }
        }
        else
        {
            await agentKit.LogAssistantMessageAsync(prepResult.SessionId, errorMessage, null, req.HttpContext.RequestAborted);
        }
    }

    var latestTasks = await taskService.ListAsync(prepResult.SessionId, req.HttpContext.RequestAborted);
    var tasksPayload = latestTasks.Select(t => new
    {
        id = t.Id,
        sessionId = prepResult.SessionId,
        label = t.DocumentLabel,
        fileId = t.FileId,
        fileName = t.FileName,
        contentType = t.ContentType,
        size = t.Size,
        documentSessionId = t.DocumentSessionId,
        status = t.Status,
        summary = t.Summary,
        analysis = t.Analysis,
        metadata = t.Metadata,
        createdAt = t.CreatedAt,
        updatedAt = t.UpdatedAt
    }).ToArray();

    return Results.Json(new
    {
        sessionId = prepResult.SessionId,
        tasks = tasksPayload
    });
}).RequireAuthorization();

app.MapGet("/ai/sessions/{sessionId:guid}/tasks", async (Guid sessionId, HttpRequest req, InvoiceTaskService invoiceTasks, SalesOrderTaskService salesOrderTasks, PayrollTaskService payrollTasks, NpgsqlDataSource ds) =>
{
    try
    {
        AppendAiTasksLog($"[START] sessionId={sessionId}");
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        {
            AppendAiTasksLog("Missing x-company-code header");
        return Results.BadRequest(new { error = "missing company" });
        }
    var uid = req.Headers.TryGetValue("x-user-id", out var userHeader) ? userHeader.ToString() : null;
    if (string.IsNullOrWhiteSpace(uid))
        {
            AppendAiTasksLog("Missing x-user-id header");
        return Results.Unauthorized();
        }

    await using var conn = await ds.OpenConnectionAsync();
    await using (var verify = conn.CreateCommand())
    {
        verify.CommandText = "SELECT company_code, user_id FROM ai_sessions WHERE id=$1";
        verify.Parameters.AddWithValue(sessionId);
        await using var reader = await verify.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
                AppendAiTasksLog("Session not found");
            return Results.NotFound();
        }
        var company = reader.GetString(0);
        var user = reader.GetString(1);
        if (!string.Equals(company, cc.ToString(), StringComparison.Ordinal) || !string.Equals(user, uid, StringComparison.Ordinal))
        {
                AppendAiTasksLog($"Forbidden: header company={cc}, db company={company}, header user={uid}, db user={user}");
            return Results.Forbid();
        }
    }

        var invoiceList = await invoiceTasks.ListAsync(sessionId, req.HttpContext.RequestAborted);
        var salesList = await salesOrderTasks.ListAsync(sessionId, req.HttpContext.RequestAborted);
        var payrollList = await payrollTasks.ListAsync(sessionId, req.HttpContext.RequestAborted);
        var labelMap = BuildTaskDisplayLabelMap(invoiceList, salesList);

        var tasks = invoiceList
            .Select(t => new AiTaskResponse
            {
                Kind = "invoice",
                Id = t.Id,
                SessionId = sessionId,
                Label = t.DocumentLabel,
                DisplayLabel = labelMap.TryGetValue(t.Id, out var label) ? label : null,
                FileId = t.FileId,
                FileName = t.FileName,
                ContentType = t.ContentType,
                Size = t.Size,
                DocumentSessionId = t.DocumentSessionId,
                Status = t.Status,
                Summary = t.Summary,
                SalesOrderId = null,
                SalesOrderNo = null,
                CustomerCode = null,
                CustomerName = null,
                Analysis = t.Analysis,
                Metadata = t.Metadata,
                Payload = null,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt,
                CompletedAt = null,
                RunId = null,
                EntryId = null,
                EmployeeId = null,
                EmployeeCode = null,
                EmployeeName = null,
                PeriodMonth = null,
                DiffSummary = null,
                TargetUserId = null
            })
            .Concat(salesList.Select(t => new AiTaskResponse
            {
                Kind = "sales_order",
                Id = t.Id,
                SessionId = sessionId,
                Label = null,
                DisplayLabel = labelMap.TryGetValue(t.Id, out var label) ? label : null,
                FileId = null,
                FileName = null,
                ContentType = null,
                Size = null,
                DocumentSessionId = null,
                Status = t.Status,
                Summary = t.Summary,
                SalesOrderId = t.SalesOrderId,
                SalesOrderNo = t.SalesOrderNo,
                CustomerCode = t.CustomerCode,
                CustomerName = t.CustomerName,
                Analysis = null,
                Metadata = t.Metadata,
                Payload = t.Payload,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt,
                CompletedAt = t.CompletedAt,
                RunId = null,
                EntryId = null,
                EmployeeId = null,
                EmployeeCode = null,
                EmployeeName = null,
                PeriodMonth = null,
                DiffSummary = null,
                TargetUserId = null
            }))
            .Concat(payrollList.Select(t => new AiTaskResponse
            {
                Kind = "payroll",
                Id = t.Id,
                SessionId = sessionId,
                Label = t.EmployeeName ?? t.EmployeeCode,
                DisplayLabel = t.EmployeeName,
                FileId = null,
                FileName = null,
                ContentType = null,
                Size = null,
                DocumentSessionId = null,
                Status = t.Status,
                Summary = t.Summary,
                SalesOrderId = null,
                SalesOrderNo = null,
                CustomerCode = null,
                CustomerName = null,
                Analysis = null,
                Metadata = t.Metadata,
                Payload = null,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt,
                CompletedAt = t.CompletedAt,
                RunId = t.RunId,
                EntryId = t.EntryId,
                EmployeeId = t.EmployeeId,
                EmployeeCode = t.EmployeeCode,
                EmployeeName = t.EmployeeName,
                PeriodMonth = t.PeriodMonth,
                DiffSummary = t.DiffSummary,
                TargetUserId = t.TargetUserId
            }))
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .ToArray();
        AppendAiTasksLog($"[SUCCESS] sessionId={sessionId} count={tasks.Length}");
    return Results.Json(new { sessionId, tasks });
    }
    catch (Exception ex)
    {
        AppendAiTasksLog($"[ERROR] sessionId={sessionId} {ex}");
        app.Logger.LogError(ex, "[AI] Failed to list tasks for session {SessionId}", sessionId);
        return Results.Text($"Failed to load tasks: {ex.Message}\n{ex}", "text/plain", Encoding.UTF8, statusCode: 500);
    }
}).RequireAuthorization();

app.MapDelete("/ai/tasks/{taskId:guid}", async (Guid taskId, HttpRequest req, InvoiceTaskService taskService, AzureBlobService blobService) =>
{
    var sessionValue = req.Query["sessionId"].ToString();
    if (string.IsNullOrWhiteSpace(sessionValue) || !Guid.TryParse(sessionValue, out var sessionId))
    {
        return Results.BadRequest(new { error = "缺少 sessionId" });
    }

    var companyCode = req.Headers.TryGetValue("x-company-code", out var ccValue) ? ccValue.ToString() ?? "JP01" : "JP01";
    var userCtx = Auth.GetUserCtx(req);
    if (userCtx is null || string.IsNullOrWhiteSpace(userCtx.UserId))
    {
        return Results.Unauthorized();
    }

    var cancelResult = await taskService.CancelAsync(taskId, sessionId, companyCode, userCtx.UserId, req.HttpContext.RequestAborted);
    if (cancelResult is not null)
    {
        if (!string.IsNullOrWhiteSpace(cancelResult.StoredPath))
        {
            try
            {
                if (System.IO.File.Exists(cancelResult.StoredPath))
                {
                    System.IO.File.Delete(cancelResult.StoredPath);
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "[InvoiceTask] 删除本地文件失败 taskId={TaskId} path={Path}", taskId, cancelResult.StoredPath);
            }
        }

        if (blobService.IsConfigured)
        {
            try
            {
                await blobService.DeleteAsync(cancelResult.BlobName, req.HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "[InvoiceTask] 删除 Azure Blob 失败 taskId={TaskId} blob={Blob}", taskId, cancelResult.BlobName);
            }
        }

        return Results.Ok(new { id = taskId, status = "cancelled" });
    }

    // 如果 CancelAsync 返回 null，可能是已完成的任务，尝试强制删除数据库记录
    var deleted = await taskService.DeleteAsync(taskId, sessionId, companyCode, req.HttpContext.RequestAborted);
    if (deleted)
    {
        return Results.Ok(new { id = taskId, status = "deleted" });
    }

    return Results.BadRequest(new { error = "タスクを削除できません（権限がないか、存在しません）" });
}).RequireAuthorization();

app.MapGet("/ai/tasks/{taskId:guid}", async (Guid taskId, HttpRequest req, InvoiceTaskService invoiceTasks, SalesOrderTaskService salesOrderTasks, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "missing company" });
    var uid = req.Headers.TryGetValue("x-user-id", out var userHeader) ? userHeader.ToString() : null;
    if (string.IsNullOrWhiteSpace(uid))
        return Results.Unauthorized();

    async Task<bool> VerifySessionAsync(Guid sessionId)
    {
    await using var conn = await ds.OpenConnectionAsync();
        await using var verify = conn.CreateCommand();
        verify.CommandText = "SELECT company_code, user_id FROM ai_sessions WHERE id=$1";
        verify.Parameters.AddWithValue(sessionId);
        await using var reader = await verify.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return false;
        }
        var company = reader.GetString(0);
        var user = reader.GetString(1);
        if (!string.Equals(company, cc.ToString(), StringComparison.Ordinal) || !string.Equals(user, uid, StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    async Task<string?> ResolveDisplayLabelAsync(Guid sessionId)
    {
        var invoices = await invoiceTasks.ListAsync(sessionId, req.HttpContext.RequestAborted);
        var sales = await salesOrderTasks.ListAsync(sessionId, req.HttpContext.RequestAborted);
        var map = BuildTaskDisplayLabelMap(invoices, sales);
        return map.TryGetValue(taskId, out var label) ? label : null;
    }

    var invoiceTask = await invoiceTasks.GetAsync(taskId, req.HttpContext.RequestAborted);
    if (invoiceTask is not null)
    {
        if (invoiceTask.SessionId is null || !await VerifySessionAsync(invoiceTask.SessionId.Value))
            return Results.Forbid();

        var displayLabel = await ResolveDisplayLabelAsync(invoiceTask.SessionId.Value);
    return Results.Json(new
    {
            kind = "invoice",
            id = invoiceTask.Id,
            sessionId = invoiceTask.SessionId,
            companyCode = invoiceTask.CompanyCode,
            label = invoiceTask.DocumentLabel,
            displayLabel,
            fileId = invoiceTask.FileId,
            fileName = invoiceTask.FileName,
            contentType = invoiceTask.ContentType,
            size = invoiceTask.Size,
            documentSessionId = invoiceTask.DocumentSessionId,
            status = invoiceTask.Status,
            summary = invoiceTask.Summary,
            analysis = invoiceTask.Analysis,
            metadata = invoiceTask.Metadata,
            createdAt = invoiceTask.CreatedAt,
            updatedAt = invoiceTask.UpdatedAt
        });
    }

    var salesTask = await salesOrderTasks.GetAsync(taskId, req.HttpContext.RequestAborted);
    if (salesTask is null)
        return Results.NotFound();

    if (salesTask.SessionId is null || !await VerifySessionAsync(salesTask.SessionId.Value))
        return Results.Forbid();

    var salesDisplayLabel = await ResolveDisplayLabelAsync(salesTask.SessionId.Value);
    return Results.Json(new
    {
        kind = "sales_order",
        id = salesTask.Id,
        sessionId = salesTask.SessionId,
        companyCode = salesTask.CompanyCode,
        status = salesTask.Status,
        summary = salesTask.Summary,
        payload = salesTask.Payload,
        metadata = salesTask.Metadata,
        salesOrderId = salesTask.SalesOrderId,
        salesOrderNo = salesTask.SalesOrderNo,
        customerCode = salesTask.CustomerCode,
        customerName = salesTask.CustomerName,
        displayLabel = salesDisplayLabel,
        createdAt = salesTask.CreatedAt,
        updatedAt = salesTask.UpdatedAt,
        completedAt = salesTask.CompletedAt
    });
}).RequireAuthorization();

static Dictionary<Guid, string> BuildTaskDisplayLabelMap(
    IReadOnlyList<InvoiceTaskService.InvoiceTask> invoices,
    IReadOnlyList<SalesOrderTaskService.SalesOrderTask> sales)
{
    var combined = invoices
        .Select(t => (t.Id, t.CreatedAt))
        .Concat(sales.Select(t => (t.Id, t.CreatedAt)))
        .OrderBy(entry => entry.CreatedAt)
        .ThenBy(entry => entry.Id)
        .ToList();
    var map = new Dictionary<Guid, string>(combined.Count);
    var index = 1;
    foreach (var entry in combined)
    {
        map[entry.Id] = $"#{index++}";
    }
    return map;
}

static AgentKitService.AgentResultMessage BuildPlanSummaryMessage(AgentKitService.AgentTaskPlanningResult planResult, IReadOnlyList<AgentKitService.AgentTaskDocument> documents)
{
    if (planResult.Plans.Count == 0 && planResult.UnassignedDocuments.Count == 0)
    {
        return new AgentKitService.AgentResultMessage("assistant", "未生成任何任务计划。", "info", null);
    }

    var labelMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var index = 1;
    foreach (var doc in documents)
    {
        if (string.IsNullOrWhiteSpace(doc.DocumentSessionId)) continue;
        if (!labelMap.ContainsKey(doc.DocumentSessionId))
        {
            var label = !string.IsNullOrWhiteSpace(doc.DocumentLabel) ? doc.DocumentLabel : $"#{index++}";
            labelMap[doc.DocumentSessionId] = label;
            nameMap[doc.DocumentSessionId] = doc.FileName ?? doc.FileId ?? doc.DocumentSessionId;
        }
    }

    var sb = new StringBuilder();
    sb.AppendLine("AI 任务分组结果：");
    for (var idx = 0; idx < planResult.Plans.Count; idx++)
    {
        var plan = planResult.Plans[idx];
        sb.AppendLine($"#{idx + 1} 场景：{plan.ScenarioKey}");
        if (!string.IsNullOrWhiteSpace(plan.Reason)) sb.AppendLine("理由：" + plan.Reason);
        if (!string.IsNullOrWhiteSpace(plan.UserMessageOverride)) sb.AppendLine("执行消息：" + plan.UserMessageOverride);
        if (!string.IsNullOrWhiteSpace(plan.DocumentSessionId))
        {
            if (labelMap.TryGetValue(plan.DocumentSessionId, out var label))
            {
                var name = nameMap.TryGetValue(plan.DocumentSessionId, out var displayName) ? displayName : plan.DocumentSessionId;
                sb.AppendLine($"文档分组：{label}（{name}）");
            }
            else
            {
                sb.AppendLine("文档上下文：" + plan.DocumentSessionId);
            }
        }
        if (plan.DocumentIds.Count > 0)
        {
            var names = plan.DocumentIds
                .Select(id =>
                {
                    var doc = documents.FirstOrDefault(d => string.Equals(d.FileId, id, StringComparison.OrdinalIgnoreCase));
                    if (doc is null) return id;
                    var label = !string.IsNullOrWhiteSpace(doc.DocumentSessionId) && labelMap.TryGetValue(doc.DocumentSessionId, out var l) ? l : null;
                    var display = doc.FileName ?? doc.FileId ?? id;
                    return string.IsNullOrWhiteSpace(label) ? display : $"[{label}] {display}";
                })
                .ToList();
            sb.AppendLine("文件：" + string.Join(", ", names));
        }
        sb.AppendLine();
    }

    if (planResult.UnassignedDocuments.Count > 0)
    {
        sb.AppendLine("未分配文件：" + string.Join(", ", planResult.UnassignedDocuments));
    }

    return new AgentKitService.AgentResultMessage("assistant", sb.ToString().TrimEnd(), "info", null);
}

// static IReadOnlyList<AgentKitService.AgentResultMessage>? MergeMessages(AgentKitService.AgentResultMessage? planMessage, IReadOnlyList<AgentKitService.AgentResultMessage>? executionMessages)
// {
//     if (planMessage is null) return executionMessages;
//     if (executionMessages is null) return new[] { planMessage };
//     var list = new List<AgentKitService.AgentResultMessage>(1 + executionMessages.Count)
//     {
//         planMessage
//     };
//     list.AddRange(executionMessages);
//     return list;
// }
// Note: /reports/account-ledger moved to FinanceReportsModule
// Note: /fb-payment/* endpoints moved to FbPaymentModule
// Note: /reports/account-balance moved to FinanceReportsModule

// ==================== AI Learning & Alert API ====================

// 学习报告
app.MapGet("/api/ai/learning/report", async (HttpRequest req, Server.Infrastructure.Skills.LearningEngine engine) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest("missing x-company-code");
    var report = await engine.GenerateReportAsync(cc!, req.HttpContext.RequestAborted);
    return Results.Ok(report);
}).RequireAuthorization();

// 手动触发异常检测
app.MapPost("/api/ai/alerts/run", async (HttpRequest req, Server.Infrastructure.Skills.ProactiveAlertService alertService) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest("missing x-company-code");
    var alerts = await alertService.RunAllChecksAsync(cc!, req.HttpContext.RequestAborted);
    return Results.Ok(new { count = alerts.Count, alerts = alerts.Select(a => new { a.Type, a.Severity, a.Title, a.Message }) });
}).RequireAuthorization();

// 修正规则查询
app.MapGet("/api/ai/learning/correction-rules", async (HttpRequest req, Server.Infrastructure.Skills.LearningEngine engine) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest("missing x-company-code");
    var rules = await engine.AnalyzeCorrectionPatternsAsync(cc!, req.HttpContext.RequestAborted);
    return Results.Ok(rules);
}).RequireAuthorization();

app.Run();

file sealed class AiTaskResponse
{
    public required string Kind { get; init; }
    public required Guid Id { get; init; }
    public required Guid SessionId { get; init; }
    public string? Label { get; init; }
    public string? DisplayLabel { get; init; }
    public string? FileId { get; init; }
    public string? FileName { get; init; }
    public string? ContentType { get; init; }
    public long? Size { get; init; }
    public string? DocumentSessionId { get; init; }
    public required string Status { get; init; }
    public string? Summary { get; init; }
    public Guid? SalesOrderId { get; init; }
    public string? SalesOrderNo { get; init; }
    public string? CustomerCode { get; init; }
    public string? CustomerName { get; init; }
    public JsonObject? Analysis { get; init; }
    public JsonObject? Metadata { get; init; }
    public JsonObject? Payload { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public Guid? RunId { get; init; }
    public Guid? EntryId { get; init; }
    public Guid? EmployeeId { get; init; }
    public string? EmployeeCode { get; init; }
    public string? EmployeeName { get; init; }
    public string? PeriodMonth { get; init; }
    public JsonObject? DiffSummary { get; init; }
    public string? TargetUserId { get; init; }
}

static class AiFileHelpers
{
    public const long MaxImageSizeBytes = 8 * 1024 * 1024; // 8MB 上限

    private static readonly HashSet<string> TextualExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".csv", ".tsv", ".json", ".xml", ".md", ".log", ".yaml", ".yml", ".html", ".htm"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"
    };

    public static string? ResolveOpenAIApiKey(HttpRequest req, IConfiguration configuration)
    {
        if (req.Headers.TryGetValue("x-openai-key", out var header) && !string.IsNullOrWhiteSpace(header.ToString()))
        {
            return header.ToString();
        }
        var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var cfg = configuration["OpenAI:ApiKey"];
        return string.IsNullOrWhiteSpace(cfg) ? null : cfg;
    }

    public static string? ResolveAnthropicApiKey(HttpRequest req, IConfiguration configuration)
    {
        if (req.Headers.TryGetValue("x-anthropic-key", out var header) && !string.IsNullOrWhiteSpace(header.ToString()))
        {
            return header.ToString();
        }
        var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var cfg = configuration["Anthropic:ApiKey"];
        return string.IsNullOrWhiteSpace(cfg) ? null : cfg;
    }

    public static string? ExtractTextPreview(string path, string? contentType, int maxChars)
    {
        try
        {
            var ext = Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
            
            // 处理 PDF 文件
            if (ext == ".pdf" || (!string.IsNullOrEmpty(contentType) && contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)))
            {
                return ExtractPdfText(path, maxChars);
            }
            
            var looksTextual = false;
            if (!string.IsNullOrEmpty(contentType))
            {
                var lower = contentType.ToLowerInvariant();
                if (lower.StartsWith("text/") || lower.Contains("json") || lower.Contains("xml") || lower.Contains("csv") || lower.Contains("plain"))
                {
                    looksTextual = true;
                }
            }
            if (!looksTextual && !TextualExtensions.Contains(ext)) return null;
            using var reader = new StreamReader(path, Encoding.UTF8, true);
            var buffer = new char[2048];
            var sb = new StringBuilder();
            while (sb.Length < maxChars)
            {
                var read = reader.Read(buffer, 0, Math.Min(buffer.Length, maxChars - sb.Length));
                if (read <= 0) break;
                sb.Append(buffer, 0, read);
            }
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }
    
    private static string? ExtractPdfText(string path, int maxChars)
    {
        try
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(path);
            var sb = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                var text = page.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine(text);
                    if (sb.Length >= maxChars) break;
                }
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? SanitizePreview(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var cleaned = text.Replace('\0', ' ');
        if (cleaned.Length > maxChars) cleaned = cleaned[..maxChars];
        return cleaned;
    }

    public static bool IsImageContentType(string? contentType, string? extension)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var lower = contentType.ToLowerInvariant();
            if (lower.StartsWith("image/")) return true;
        }
        if (!string.IsNullOrWhiteSpace(extension))
        {
            if (!extension.StartsWith('.')) extension = "." + extension;
            if (ImageExtensions.Contains(extension)) return true;
        }
        return false;
    }

    public static async Task<string?> ReadFileAsBase64Async(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        return bytes.Length == 0 ? null : Convert.ToBase64String(bytes);
    }

    public record ImageAttachment(string ContentType, string Base64);

    public static async Task<AiFileAnalysisResult> AnalyzeFileWithOpenAIAsync(string apiKey, object payload, string? fileNameForError, string? previewText, ImageAttachment? imageAttachment, IReadOnlyList<WorkflowRule> rules)
    {
        var rulesPrompt = BuildRulesPrompt(rules);
        var sysPromptTemplate = """
You are an accounting assistant working inside an ERP system. You receive metadata about a newly uploaded document (invoice, purchase request, bank statement, etc.). Analyze the information and infer what the user likely wants to do next.
Available workflows:
{RULES}
Always respond with a single JSON object matching this schema:
{
  "summary": {
    "message": string,
    "highlights"?: string[],
    "detectedIntents"?: [ { "intent": string, "confidence": number, "description": string } ]
  },
  "assistantMessage": string|null,
  "workflow"?: {
    "ruleId": string,         // one of the workflow keys listed above, or "none" if nothing fits
    "confidence"?: number,    // 0-1
    "reason"?: string,        // short explanation why this rule (Simplified Chinese)
    "context"?: {
      "document"?: object,    // structured information you extracted: issueDate, number, partnerName, etc.
      "header"?: object,      // additional header suggestions
      "totals"?: object,      // grand totals, tax, etc.
      "lines"?: array         // optional extracted lines
    }
  },
  "actions"?: [ { "type": string, "key": string, "payload"?: object } ]
}
Rules:
- 如果无法明确匹配某个 workflow，则将 workflow.ruleId 设置为 "none"。
- 当选择 workflow 时，请在 context 中填入你提取的结构化数据，便于系统执行动作。
- Never invent facts; mark missing fields为空或留空。
- Keep assistantMessage within 120 Chinese characters.
""";
        var sysPrompt = sysPromptTemplate.Replace("{RULES}", rulesPrompt);

        using var http = new HttpClient();
        http.BaseAddress = new Uri("https://api.openai.com/v1/");
        http.Timeout = TimeSpan.FromSeconds(240);
        Server.Infrastructure.OpenAiApiHelper.SetOpenAiHeaders(http, apiKey);
        
        var metadataJson = JsonSerializer.Serialize(payload);
        var hasPreview = !string.IsNullOrWhiteSpace(previewText);
        var userContent = new List<object>
        {
            new { type = "text", text = metadataJson }
        };
        if (hasPreview)
        {
            userContent.Add(new { type = "text", text = previewText });
        }
        if (imageAttachment is not null)
        {
            userContent.Add(new
            {
                type = "image_url",
                image_url = new
                {
                    url = $"data:{imageAttachment.ContentType};base64,{imageAttachment.Base64}"
                }
            });
        }

        var modelName = "gpt-4o";
        var messages = new object[]
        {
            new { role = "system", content = sysPrompt },
            new { role = "user", content = userContent.ToArray() }
        };
        
        try
        {
            Console.WriteLine($"[AI] calling OpenAI {modelName} (image={(imageAttachment != null).ToString().ToLowerInvariant()}, preview={(previewText?.Length ?? 0)})");
        }
        catch { }
        
        var openAiResponse = await Server.Infrastructure.OpenAiApiHelper.CallOpenAiAsync(
            http, apiKey, modelName, messages, temperature: 0.2, maxTokens: 4096, jsonMode: true);
        
        if (string.IsNullOrWhiteSpace(openAiResponse.Content))
        {
            var failMessage = string.IsNullOrWhiteSpace(fileNameForError)
                ? "AI 分析失败，请稍后重试。"
                : $"AI 分析 {fileNameForError} 失败，请稍后重试。";
            try
            {
                Console.WriteLine("[AI] error: 响应为空");
            }
            catch { }
            return new AiFileAnalysisResult(null, failMessage, new List<object>(), null);
        }
        try
        {
            var content = openAiResponse.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                return new AiFileAnalysisResult(null, null, new List<object>(), null);
            }
            using var resultDoc = JsonDocument.Parse(content);
            object? summary = null;
            string? assistantMessage = null;
            var actions = new List<object>();
            WorkflowSelection? workflow = null;

            if (resultDoc.RootElement.TryGetProperty("summary", out var summaryEl))
            {
                try { summary = JsonSerializer.Deserialize<object>(summaryEl.GetRawText()); } catch { summary = null; }
            }
            if (resultDoc.RootElement.TryGetProperty("assistantMessage", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
            {
                assistantMessage = msgEl.GetString();
            }
            if (resultDoc.RootElement.TryGetProperty("actions", out var actionsEl) && actionsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var act in actionsEl.EnumerateArray())
                {
                    try
                    {
                        var obj = JsonSerializer.Deserialize<object>(act.GetRawText());
                        if (obj is not null) actions.Add(obj);
                    }
                    catch { }
                }
            }
            if (resultDoc.RootElement.TryGetProperty("workflow", out var workflowEl) && workflowEl.ValueKind == JsonValueKind.Object)
            {
                var ruleId = workflowEl.TryGetProperty("ruleId", out var ruleIdEl) && ruleIdEl.ValueKind == JsonValueKind.String ? ruleIdEl.GetString() : null;
                var confidence = workflowEl.TryGetProperty("confidence", out var confEl) && confEl.ValueKind == JsonValueKind.Number ? confEl.GetDouble() : (double?)null;
                var reason = workflowEl.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String ? reasonEl.GetString() : null;
                var contextNode = workflowEl.TryGetProperty("context", out var contextEl) && (contextEl.ValueKind == JsonValueKind.Object)
                    ? JsonNode.Parse(contextEl.GetRawText())?.AsObject()
                    : null;

                if (!string.IsNullOrWhiteSpace(ruleId))
                {
                    workflow = new WorkflowSelection(ruleId!, confidence, reason, contextNode);
                    if (!string.Equals(ruleId, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        var workflowPayload = BuildWorkflowPayload(ruleId!, confidence, reason, contextNode, payload, previewText);
                        actions.Add(new
                        {
                            type = "workflow",
                            key = "voucher.autoCreate",
                            payload = workflowPayload
                        });
                    }
                }
            }
            try
            {
                Console.WriteLine($"[AI] success actions={actions.Count}, workflow={(workflow?.RuleId ?? "none")}");
            }
            catch { }
            return new AiFileAnalysisResult(summary, assistantMessage, actions, workflow);
        }
        catch
        {
            return new AiFileAnalysisResult(null, null, new List<object>(), null);
        }
    }

    private static string BuildRulesPrompt(IReadOnlyList<WorkflowRule> rules)
    {
        if (rules.Count == 0) return "(当前未配置可执行的编排规则，若无法匹配请返回 workflow.ruleId = \"none\")";
        var sb = new StringBuilder();
        var index = 1;
        foreach (var rule in rules)
        {
            sb.Append(index++).Append(". Key: ").Append(rule.RuleKey).AppendLine();
            if (!string.IsNullOrWhiteSpace(rule.Title))
                sb.Append("   标题: ").Append(rule.Title).AppendLine();
            if (!string.IsNullOrWhiteSpace(rule.Description))
                sb.Append("   说明: ").Append(rule.Description).AppendLine();
            if (!string.IsNullOrWhiteSpace(rule.Instructions))
                sb.Append("   适用场景: ").Append(rule.Instructions).AppendLine();
            var actionList = string.Join(", ", rule.Actions.Select(a => a.Type));
            if (!string.IsNullOrWhiteSpace(actionList))
                sb.Append("   动作: ").Append(actionList).AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildRulesSummary(IReadOnlyList<WorkflowRule> rules)
    {
        if (rules.Count == 0) return "(当前无规则)";
        var sb = new StringBuilder();
        foreach (var rule in rules)
        {
            sb.Append("- ").Append(rule.RuleKey);
            if (!string.IsNullOrWhiteSpace(rule.Title))
            {
                sb.Append(" : ").Append(rule.Title);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static JsonObject BuildWorkflowPayload(string ruleKey, double? confidence, string? reason, JsonObject? context, object metadata, string? previewText)
    {
        var payload = new JsonObject
        {
            ["ruleKey"] = ruleKey
        };
        if (confidence.HasValue) payload["confidence"] = confidence.Value;
        if (!string.IsNullOrWhiteSpace(reason)) payload["reason"] = reason;

        var document = EnsureObject(context, "document");
        var header = EnsureObject(context, "header");
        var totals = EnsureObject(context, "totals");
        var lines = EnsureArray(context, "lines");

        payload["document"] = document;
        payload["header"] = header;
        payload["totals"] = totals;
        payload["lines"] = lines;

        if (context is not null)
        {
            payload["context"] = context.DeepClone();
        }

        try
        {
            var metadataNode = JsonNode.Parse(JsonSerializer.Serialize(metadata))?.AsObject();
            if (metadataNode is not null) payload["metadata"] = metadataNode;
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(previewText)) payload["preview"] = previewText;

        return payload;
    }

    private static JsonObject EnsureObject(JsonObject? parent, string property)
    {
        if (parent is not null && parent.TryGetPropertyValue(property, out var node) && node is JsonObject obj)
        {
            return JsonNode.Parse(obj.GetRawText())?.AsObject() ?? new JsonObject();
        }
        return new JsonObject();
    }

    private static JsonArray EnsureArray(JsonObject? parent, string property)
    {
        if (parent is not null && parent.TryGetPropertyValue(property, out var node) && node is JsonArray arr)
        {
            return JsonNode.Parse(arr.GetRawText())?.AsArray() ?? new JsonArray();
        }
        return new JsonArray();
    }

    public static async Task<WorkflowRuleDraft> GenerateWorkflowRuleAsync(string apiKey, string prompt, IReadOnlyList<WorkflowRule> existingRules)
    {
        var existingSummary = BuildRulesSummary(existingRules);
        var sysPromptTemplate = @$"You are an ERP automation architect. Convert the user requirement into a workflow rule that can be executed by the system.
Existing rules:
{existingSummary}
Output must be a JSON object matching this schema:
{{
  ""ruleKey"": string,
  ""title"": string,
  ""description"": string,
  ""instructions"": string,
  ""priority"": number,
  ""isActive"": boolean,
  ""actions"": [ {{ ""type"": string, ""params"": object }} ]
}}
Ensure actions.params can be executed by voucher.autoCreate (e.g. header, lines definitions).
If user prompt lacks details, make reasonable assumptions but keep comments short.";
        var sysPrompt = sysPromptTemplate;

        using var http = new HttpClient();
        http.BaseAddress = new Uri("https://api.openai.com/v1/");
        http.Timeout = TimeSpan.FromSeconds(240);
        Server.Infrastructure.OpenAiApiHelper.SetOpenAiHeaders(http, apiKey);
        
        var messages = new object[]
        {
            new { role = "system", content = sysPrompt },
            new { role = "user", content = prompt }
        };

        var openAiResponse = await Server.Infrastructure.OpenAiApiHelper.CallOpenAiAsync(
            http, apiKey, "gpt-4o", messages, temperature: 0.2, maxTokens: 2000, jsonMode: true);
        
        if (string.IsNullOrWhiteSpace(openAiResponse.Content))
        {
            throw new Exception("无法解析规则：AI 响应为空");
        }

        var content = openAiResponse.Content;
        if (string.IsNullOrWhiteSpace(content)) throw new Exception("AI 未返回任何规则内容");

        using var ruleDoc = JsonDocument.Parse(content);
        var root = ruleDoc.RootElement;

        string GetString(string name) => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : string.Empty;
        int GetInt(string name, int defaultValue) => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : defaultValue;
        bool GetBool(string name, bool defaultValue) => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True ? true : (el.ValueKind == JsonValueKind.False ? false : defaultValue);

        var ruleKey = GetString("ruleKey");
        var title = GetString("title");
        var description = GetString("description");
        var instructions = GetString("instructions");
        var priority = GetInt("priority", 100);
        var isActive = GetBool("isActive", true);

        var actionsNode = root.TryGetProperty("actions", out var actionsEl) && actionsEl.ValueKind == JsonValueKind.Array
            ? JsonNode.Parse(actionsEl.GetRawText())?.AsArray()
            : new JsonArray();
        if (actionsNode is null) actionsNode = new JsonArray();

        var existingKeys = new HashSet<string>(existingRules.Select(r => r.RuleKey), StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(ruleKey)) ruleKey = WorkflowRulesService.SuggestRuleKey(title);
        ruleKey = ruleKey.Trim();
        if (string.IsNullOrEmpty(ruleKey)) ruleKey = WorkflowRulesService.SuggestRuleKey(title);
        if (!Regex.IsMatch(ruleKey, @"^[a-z0-9.\-]+$"))
        {
            ruleKey = WorkflowRulesService.SuggestRuleKey(ruleKey);
        }
        var baseKey = ruleKey;
        var counter = 1;
        while (existingKeys.Contains(ruleKey))
        {
            ruleKey = $"{baseKey}.{counter++}";
        }

        return new WorkflowRuleDraft(ruleKey, title, description, instructions, actionsNode, priority, isActive);
    }
}

record UserCtx(string? UserId, string[] Roles, string? DeptId, string? EmployeeCode = null, string? UserName = null, string? CompanyCode = null);

public sealed record UploadedFileRecord(
    string FileName,
    string StoredPath,
    string ContentType,
    long Size,
    DateTimeOffset CreatedAt,
    string? CompanyCode,
    string? UserId,
    string? BlobName)
{
    // 兼容旧代码字段名
    public DateTimeOffset UploadedAt => CreatedAt;

    // 兼容 AgentKit/发票任务：允许把解析/分析结果附着在文件记录上
    public JsonObject? Analysis { get; init; }
}

record AiFileAnalysisResult(object? summary, string? assistantMessage, List<object> actions, WorkflowSelection? workflow);

public sealed record WorkflowSelection(string RuleId, double? Confidence, string? Reason, JsonObject? Context)
{
    public object ToResponse() => new { ruleId = RuleId, confidence = Confidence, reason = Reason, context = Context };
}

record SearchRequest
{
    public int? Page { get; set; }
    public int? PageSize { get; set; }
    public List<SearchClause>? Where { get; set; }
    public List<SearchOrder>? OrderBy { get; set; }
}

record SearchClause
{
    public string? Field { get; set; }
    public string? Json { get; set; }
    public string? Op { get; set; }
    public JsonElement Value { get; set; }
    public List<SearchClause>? AnyOf { get; set; }
}

record SearchOrder
{
    public string? Field { get; set; }
    public string? Dir { get; set; }
}

record SearchResult(long Total, List<JsonObject> Data);

enum ExpressionKind
{
    Column,
    JsonText
}
sealed class SearchQueryBuilder
{
    private readonly string _table;
    private readonly Auth.UserCtx _user;
    private readonly bool _requiresCompanyCode;
    private readonly List<string> _whereParts = new();
    private readonly List<(object? Value, NpgsqlDbType? Type)> _parameters = new();
    private readonly List<(string Field, string Direction)> _orderRequests = new();
    private int? _limit = 50;
    private int _offset;

    private static readonly Regex IdentifierRegex = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    public SearchQueryBuilder(string table, string companyCode, Auth.UserCtx user, bool requiresCompanyCode = true)
    {
        _table = table;
        _user = user;
        _requiresCompanyCode = requiresCompanyCode;
        if (_requiresCompanyCode)
        {
            _whereParts.Add($"company_code = {AddParam(companyCode)}");
        }
    }

    private string AddParam(object? value, NpgsqlDbType? type = null)
    {
        _parameters.Add((value, type));
        return $"${_parameters.Count}";
    }

    public void ApplyWhere(List<SearchClause>? filters)
    {
        if (filters is null) return;
        foreach (var clause in filters)
        {
            var sql = BuildClause(clause, allowAnyOf: true);
            if (!string.IsNullOrWhiteSpace(sql))
            {
                _whereParts.Add(sql!);
            }
        }
    }

    public void AddWhereExpression(string sql)
    {
        if (!string.IsNullOrWhiteSpace(sql))
        {
            _whereParts.Add(sql);
        }
    }

    public void SetOrdering(List<SearchOrder>? orderBy)
    {
        if (orderBy is null) return;
        foreach (var item in orderBy)
        {
            var candidate = item?.Field;
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var direction = string.Equals(item?.Dir, "DESC", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
            _orderRequests.Add((candidate.Trim(), direction));
        }
    }

    public void SetPagination(int? limit, int offset)
    {
        // limit = null 或 0 表示不限制，全量返回
        if (limit == null || limit == 0)
        {
            _limit = null;
            _offset = 0;
        }
        else
        {
            _limit = Math.Clamp(limit.Value, 1, 10000);
            _offset = Math.Max(0, offset);
        }
    }

    public async Task<SearchResult> ExecuteAsync(NpgsqlDataSource ds)
    {
        var whereSql = _whereParts.Count > 0 ? string.Join(" AND ", _whereParts) : "1=1";
        var baseParams = _parameters.ToList();

        await using var conn = await ds.OpenConnectionAsync();

        var orderSql = BuildOrderSql(conn);

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM {_table} WHERE {whereSql}";
        ApplyParameters(countCmd, baseParams);
        var totalObj = await countCmd.ExecuteScalarAsync();
        var total = totalObj switch
        {
            long l => l,
            int i => i,
            decimal d => (long)d,
            null => 0L,
            _ => Convert.ToInt64(totalObj)
        };

        var dataParams = baseParams.ToList();
        string paginationSql;
        if (_limit == null)
        {
            // 不限制，全量返回
            paginationSql = "";
        }
        else
        {
            var limitPlaceholder = AddParamToList(dataParams, _limit.Value, null);
            var offsetPlaceholder = AddParamToList(dataParams, _offset, null);
            paginationSql = $" LIMIT {limitPlaceholder} OFFSET {offsetPlaceholder}";
        }

        await using var dataCmd = conn.CreateCommand();
        dataCmd.CommandText = $"SELECT to_jsonb(t) FROM (SELECT * FROM {_table} WHERE {whereSql}{orderSql}{paginationSql}) t";
        ApplyParameters(dataCmd, dataParams);

        var rows = new List<JsonObject>();
        await using var reader = await dataCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var json = reader.GetFieldValue<string>(0);
            var node = JsonNode.Parse(json)?.AsObject();
            if (node != null) rows.Add(node);
        }

        return new SearchResult(total, rows);
    }

    private string? BuildClause(SearchClause clause, bool allowAnyOf)
    {
        if (clause is null) return null;
        if (allowAnyOf && clause.AnyOf is { Count: > 0 })
        {
            var orParts = new List<string>();
            foreach (var sub in clause.AnyOf)
            {
                var part = BuildClause(sub, allowAnyOf: false);
                if (!string.IsNullOrWhiteSpace(part))
                    orParts.Add(part!);
            }
            if (orParts.Count == 0) return null;
            return "(" + string.Join(" OR ", orParts) + ")";
        }

        var op = (clause.Op ?? "eq").Trim().ToLowerInvariant();
        var expressionInfo = ResolveExpression(clause);
        if (expressionInfo is null) return null;
        var (expr, kind) = expressionInfo.Value;

        return op switch
        {
            "eq" or "=" => BuildEquals(expr, kind, clause),
            "eq_user" => BuildEqualsUser(expr, kind, clause),
            "contains" => BuildContains(expr, kind, clause),
            "in" => BuildIn(expr, kind, clause),
            "gt" or "gte" or "ge" or "lt" or "lte" or "le" => BuildComparison(expr, kind, clause, op),
            "between" => BuildBetween(expr, kind, clause),
            _ => null
        };
    }

    private string? BuildEquals(string expr, ExpressionKind kind, SearchClause clause)
    {
        if (clause.Value.ValueKind == JsonValueKind.Undefined)
            return null;
        if (clause.Value.ValueKind == JsonValueKind.Null)
            return $"{MakeNullComparable(expr, kind)} IS NULL";

        var raw = ExtractSingleValue(clause.Value);
        if (raw is null)
            return $"{MakeNullComparable(expr, kind)} IS NULL";

        var (value, expression, type) = AlignValue(expr, kind, raw);
        return $"{expression} = {AddParam(value, type)}";
    }

    private string? BuildEqualsUser(string expr, ExpressionKind kind, SearchClause clause)
    {
        var key = clause.Value.ValueKind == JsonValueKind.String ? clause.Value.GetString() : "id";
        var userValue = ResolveUserValue(key);
        if (string.IsNullOrWhiteSpace(userValue)) return null;
        var (value, expression, type) = AlignValue(expr, kind, userValue);
        return $"{expression} = {AddParam(value, type)}";
    }

    private string? BuildContains(string expr, ExpressionKind kind, SearchClause clause)
    {
        var text = ExtractSearchText(clause.Value);
        if (string.IsNullOrEmpty(text)) return null;
        var expression = kind == ExpressionKind.Column ? $"COALESCE({expr}::text, '')" : $"COALESCE({expr}, '')";
        return $"{expression} ILIKE {AddParam("%" + text + "%", NpgsqlDbType.Text)}";
    }

    private string? BuildIn(string expr, ExpressionKind kind, SearchClause clause)
    {
        var items = ExtractArrayValues(clause.Value);
        if (items.Count == 0) return null;

        if (kind == ExpressionKind.JsonText)
        {
            var texts = items.Select(v => v?.ToString() ?? string.Empty).ToArray();
            return $"{expr} = ANY({AddParam(texts, NpgsqlDbType.Array | NpgsqlDbType.Text)})";
        }

        if (items.All(v => v is Guid || (v is string s && Guid.TryParse(s, out _))))
        {
            var guids = items.Select(v =>
            {
                if (v is Guid g) return g;
                return Guid.TryParse(v?.ToString(), out var parsed) ? parsed : Guid.Empty;
            }).Where(g => g != Guid.Empty).ToArray();
            if (guids.Length == 0) return null;
            return $"{expr} = ANY({AddParam(guids, NpgsqlDbType.Array | NpgsqlDbType.Uuid)})";
        }

        if (items.All(v => v is long || v is int || v is decimal || v is double))
        {
            var numbers = items.Select(v => Convert.ToDecimal(v, CultureInfo.InvariantCulture)).ToArray();
            return $"{expr} = ANY({AddParam(numbers, NpgsqlDbType.Array | NpgsqlDbType.Numeric)})";
        }

        var arr = items.Select(v => v?.ToString() ?? string.Empty).ToArray();
        return $"{expr} = ANY({AddParam(arr, NpgsqlDbType.Array | NpgsqlDbType.Text)})";
    }

    private string? BuildComparison(string expr, ExpressionKind kind, SearchClause clause, string op)
    {
        var prepared = PrepareComparable(expr, kind, clause.Value);
        if (prepared is null) return null;
        var (value, expression, type) = prepared.Value;
        var sqlOp = op switch
        {
            "gt" => ">",
            "gte" or "ge" => ">=",
            "lt" => "<",
            "lte" or "le" => "<=",
            _ => ">"
        };
        return $"{expression} {sqlOp} {AddParam(value, type)}";
    }

    private string? BuildBetween(string expr, ExpressionKind kind, SearchClause clause)
    {
        if (clause.Value.ValueKind != JsonValueKind.Array) return null;
        var elements = clause.Value.EnumerateArray().Take(2).ToList();
        if (elements.Count < 2) return null;

        var lower = PrepareComparable(expr, kind, elements[0]);
        var upper = PrepareComparable(expr, kind, elements[1]);
        if (lower is null || upper is null) return null;

        var expression = lower.Value.Expression;
        var lowerParam = AddParam(lower.Value.Value, lower.Value.Type);
        var upperParam = AddParam(upper.Value.Value, upper.Value.Type);
        return $"{expression} BETWEEN {lowerParam} AND {upperParam}";
    }

    private (object Value, string Expression, NpgsqlDbType? Type)? PrepareComparable(string expr, ExpressionKind kind, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            var numeric = element.TryGetDecimal(out var dec) ? dec : Convert.ToDecimal(element.GetDouble(), CultureInfo.InvariantCulture);
            var expression = kind == ExpressionKind.JsonText ? $"NULLIF({expr}, '')::numeric" : expr;
            return (numeric, expression, NpgsqlDbType.Numeric);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            {
                var expression = kind == ExpressionKind.JsonText ? $"NULLIF({expr}, '')::date" : expr;
                return (dt.Date, expression, NpgsqlDbType.Date);
            }
            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var dec))
            {
                var expression = kind == ExpressionKind.JsonText ? $"NULLIF({expr}, '')::numeric" : expr;
                return (dec, expression, NpgsqlDbType.Numeric);
            }
            }

            return null;
        }

    private (object Value, string Expression, NpgsqlDbType? Type) AlignValue(string expr, ExpressionKind kind, object raw)
    {
        switch (raw)
        {
            case Guid g:
                return (g, expr, NpgsqlDbType.Uuid);
            case bool b when kind == ExpressionKind.JsonText:
                return (b ? "true" : "false", expr, NpgsqlDbType.Text);
            case bool b:
                return (b, expr, NpgsqlDbType.Boolean);
            case long l when kind == ExpressionKind.JsonText:
                return (Convert.ToDecimal(l), $"NULLIF({expr}, '')::numeric", NpgsqlDbType.Numeric);
            case long l:
                return (l, expr, NpgsqlDbType.Bigint);
            case int i when kind == ExpressionKind.JsonText:
                return (Convert.ToDecimal(i), $"NULLIF({expr}, '')::numeric", NpgsqlDbType.Numeric);
            case int i:
                return (i, expr, NpgsqlDbType.Integer);
            case decimal dec when kind == ExpressionKind.JsonText:
                return (dec, $"NULLIF({expr}, '')::numeric", NpgsqlDbType.Numeric);
            case decimal dec:
                return (dec, expr, NpgsqlDbType.Numeric);
            case double dbl when kind == ExpressionKind.JsonText:
                return (Convert.ToDecimal(dbl), $"NULLIF({expr}, '')::numeric", NpgsqlDbType.Numeric);
            case double dbl:
                return (dbl, expr, NpgsqlDbType.Double);
            case DateTime dt when kind == ExpressionKind.JsonText:
                return (dt.Date, $"NULLIF({expr}, '')::date", NpgsqlDbType.Date);
            case DateTime dt:
                return (dt.Date, expr, NpgsqlDbType.Date);
            case string s:
                if (Guid.TryParse(s, out var guid))
                    return (guid, expr, NpgsqlDbType.Uuid);
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt2))
                    return (dt2.Date, kind == ExpressionKind.JsonText ? $"NULLIF({expr}, '')::date" : expr, NpgsqlDbType.Date);
                if (kind == ExpressionKind.JsonText && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var dec2Json))
                    return (dec2Json, $"NULLIF({expr}, '')::numeric", NpgsqlDbType.Numeric);
                return (s, kind == ExpressionKind.Column ? $"{expr}::text" : expr, NpgsqlDbType.Text);
            default:
                var text = raw?.ToString() ?? string.Empty;
                return (text, kind == ExpressionKind.Column ? $"{expr}::text" : expr, NpgsqlDbType.Text);
        }
    }

    private static string MakeNullComparable(string expr, ExpressionKind kind)
        => kind == ExpressionKind.Column ? expr : expr;

    private (string Expr, ExpressionKind Kind)? ResolveExpression(SearchClause clause)
    {
        if (!string.IsNullOrWhiteSpace(clause.Field))
        {
            var identifier = SanitizeIdentifier(clause.Field!);
            if (identifier is null) return null;
            return (identifier, ExpressionKind.Column);
        }

        if (!string.IsNullOrWhiteSpace(clause.Json))
        {
            var expr = BuildJsonTextExpression(clause.Json!);
            if (expr is null) return null;
            return (expr, ExpressionKind.JsonText);
        }

        return null;
    }

    private static string? SanitizeIdentifier(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var trimmed = input.Trim();
        if (!IdentifierRegex.IsMatch(trimmed))
        {
            return null;
        }
        return QuoteIdentifier(trimmed);
    }

    private static string QuoteIdentifier(string identifier)
    {
        var escaped = identifier.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    private string BuildOrderSql(NpgsqlConnection conn)
    {
        var expressions = new List<string>();
        foreach (var (field, direction) in _orderRequests)
        {
            var expr = ResolveOrderExpression(conn, field);
            if (expr is null) continue;
            expressions.Add($"{expr} {direction}");
        }

        if (expressions.Count == 0)
        {
            return $" ORDER BY {QuoteIdentifier("updated_at")} DESC, {QuoteIdentifier("created_at")} DESC";
        }

        return " ORDER BY " + string.Join(", ", expressions);
    }

    private string? ResolveOrderExpression(NpgsqlConnection conn, string field)
    {
        if (string.IsNullOrWhiteSpace(field)) return null;
        var trimmed = field.Trim();
        if (!IdentifierRegex.IsMatch(trimmed)) return null;

        var columns = TableMetadataCache.GetColumns(conn, _table);
        if (columns.Contains(trimmed.ToLowerInvariant()))
        {
            return QuoteIdentifier(trimmed.ToLowerInvariant());
        }

        if (!columns.Contains("payload")) return null;

        var jsonKey = SanitizeJsonKey(trimmed);
        if (string.IsNullOrEmpty(jsonKey)) return null;
        var payloadColumn = QuoteIdentifier("payload");
        var expr = $"{payloadColumn}->>'{jsonKey}'";

        if (string.Equals(trimmed, "level", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "order", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "priority", StringComparison.OrdinalIgnoreCase))
        {
            return $"COALESCE(({expr})::integer, 0)";
        }

        return $"COALESCE({expr}, '')";
    }

    private static string? BuildJsonTextExpression(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => SanitizeJsonKey(p))
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
        if (parts.Count == 0) return null;
        if (string.Equals(parts[0], "payload", StringComparison.OrdinalIgnoreCase))
            parts.RemoveAt(0);
        if (parts.Count == 0) return null;

        var sb = new StringBuilder("payload");
        for (int i = 0; i < parts.Count - 1; i++)
        {
            sb.Append("->'").Append(parts[i]).Append("'");
        }
        sb.Append("->>'").Append(parts[^1]).Append("'");
        return sb.ToString();
    }

    private static string SanitizeJsonKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        var cleaned = new StringBuilder(key.Length);
        foreach (var ch in key)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
                cleaned.Append(ch);
        }
        return cleaned.ToString();
    }

    private static object? ExtractSingleValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.TryGetDecimal(out var dec) ? dec : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => element.GetRawText()
        };
    }

    private static List<object?> ExtractArrayValues(JsonElement element)
    {
        var list = new List<object?>();
        if (element.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in element.EnumerateArray())
        {
            list.Add(ExtractSingleValue(item));
        }
        return list;
    }

    private static string? ExtractSearchText(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private string? ResolveUserValue(string? key)
    {
        var normalized = (key ?? "id").Trim().ToLowerInvariant();
        return normalized switch
        {
            "id" or "user" or "user_id" => _user.UserId,
            "employee" or "employee_code" or "employeeid" => _user.EmployeeCode,
            "company" or "company_code" => _user.CompanyCode,
            _ => _user.UserId
        };
    }

    private static string AddParamToList(List<(object? Value, NpgsqlDbType? Type)> list, object? value, NpgsqlDbType? type)
    {
        list.Add((value, type));
        return $"${list.Count}";
    }

    private static void ApplyParameters(NpgsqlCommand cmd, List<(object? Value, NpgsqlDbType? Type)> parameters)
    {
        foreach (var (value, type) in parameters)
        {
            var p = cmd.CreateParameter();
            if (type.HasValue) p.NpgsqlDbType = type.Value;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }
}

static class TableMetadataCache
{
    private static readonly ConcurrentDictionary<string, HashSet<string>> ColumnsCache = new(StringComparer.OrdinalIgnoreCase);

    public static HashSet<string> GetColumns(NpgsqlConnection conn, string table)
    {
        if (ColumnsCache.TryGetValue(table, out var cached))
        {
            return cached;
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_schema='public' AND table_name=@table";
        cmd.Parameters.AddWithValue("table", table);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(name))
            {
                result.Add(name.Trim().ToLowerInvariant());
            }
        }
        ColumnsCache[table] = result;
        return result;
    }
}

static class WorkflowRuleResponseHelper
{
    public static object ToRuleResponse(WorkflowRule rule) => new
    {
        rule.RuleKey,
        rule.Title,
        rule.Description,
        rule.Instructions,
        rule.Priority,
        rule.IsActive,
        updatedAt = rule.UpdatedAt,
        actions = rule.Actions.Select(a => new { type = a.Type, @params = a.Params.DeepClone() })
    };
}
static class AgentAccountingRuleEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<IResult> ListAsync(HttpRequest req, AgentAccountingRuleService service, bool includeInactive)
    {
        var companyCode = req.Headers.TryGetValue("x-company-code", out var ccValue) ? ccValue.ToString() : null;
        if (string.IsNullOrWhiteSpace(companyCode))
        {
            return Results.BadRequest(new { error = "Missing x-company-code" });
        }

        var items = await service.ListAsync(companyCode!, includeInactive, req.HttpContext.RequestAborted);
        return Results.Json(items.Select(ToDto));
    }

    public static async Task<IResult> GetAsync(HttpRequest req, Guid id, AgentAccountingRuleService service)
    {
        var companyCode = req.Headers.TryGetValue("x-company-code", out var ccValue) ? ccValue.ToString() : null;
        if (string.IsNullOrWhiteSpace(companyCode))
        {
            return Results.BadRequest(new { error = "Missing x-company-code" });
        }

        var rule = await service.GetAsync(companyCode!, id, req.HttpContext.RequestAborted);
        if (rule is null) return Results.NotFound(new { error = "not found" });
        return Results.Json(ToDto(rule));
    }

    public static async Task<IResult> UpsertAsync(HttpRequest req, Guid? idFromRoute, AgentAccountingRuleService service)
    {
        var companyCode = req.Headers.TryGetValue("x-company-code", out var ccValue) ? ccValue.ToString() : null;
        if (string.IsNullOrWhiteSpace(companyCode))
        {
            return Results.BadRequest(new { error = "Missing x-company-code" });
        }

        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
        var root = doc.RootElement;

        var title = root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String ? titleEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(title))
        {
            return Results.BadRequest(new { error = "title 必填" });
        }

        string? description = null;
        if (root.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
        {
            description = descEl.GetString();
        }

        var keywords = new List<string>();
        if (root.TryGetProperty("keywords", out var keywordsEl) && keywordsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in keywordsEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var keyword = item.GetString();
                    if (!string.IsNullOrWhiteSpace(keyword)) keywords.Add(keyword.Trim());
                }
            }
        }

        string? accountCode = null;
        if (root.TryGetProperty("accountCode", out var accountCodeEl) && accountCodeEl.ValueKind == JsonValueKind.String)
        {
            accountCode = accountCodeEl.GetString();
        }

        string? accountName = null;
        if (root.TryGetProperty("accountName", out var accountNameEl) && accountNameEl.ValueKind == JsonValueKind.String)
        {
            accountName = accountNameEl.GetString();
        }

        string? note = null;
        if (root.TryGetProperty("note", out var noteEl) && noteEl.ValueKind == JsonValueKind.String)
        {
            note = noteEl.GetString();
        }

        var priority = 100;
        if (root.TryGetProperty("priority", out var priEl))
        {
            priority = priEl.ValueKind switch
            {
                JsonValueKind.Number when priEl.TryGetInt32(out var pri) => pri,
                JsonValueKind.String when int.TryParse(priEl.GetString(), out var pri) => pri,
                _ => priority
            };
        }

        var isActive = true;
        if (root.TryGetProperty("isActive", out var actEl))
        {
            isActive = actEl.ValueKind switch
            {
                JsonValueKind.False => false,
                JsonValueKind.True => true,
                JsonValueKind.String when bool.TryParse(actEl.GetString(), out var parsed) => parsed,
                _ => isActive
            };
        }

        JsonObject? options = null;
        if (root.TryGetProperty("options", out var optionsEl) && optionsEl.ValueKind != JsonValueKind.Null && optionsEl.ValueKind != JsonValueKind.Undefined)
        {
            if (optionsEl.ValueKind == JsonValueKind.Object)
            {
                options = JsonNode.Parse(optionsEl.GetRawText())?.AsObject();
            }
            else if (optionsEl.ValueKind == JsonValueKind.String)
            {
                var raw = optionsEl.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        options = JsonNode.Parse(raw)?.AsObject();
                    }
                    catch
                    {
                        options = null;
                    }
                }
            }
        }

        var input = new AgentAccountingRuleService.AccountingRuleInput(
            idFromRoute,
            title!,
            description,
            keywords,
            accountCode,
            accountName,
            note,
            priority,
            isActive,
            options);

        var result = await service.UpsertAsync(companyCode!, input, req.HttpContext.RequestAborted);
        return Results.Json(ToDto(result));
    }

    public static async Task<IResult> DeleteAsync(HttpRequest req, Guid id, AgentAccountingRuleService service)
    {
        var companyCode = req.Headers.TryGetValue("x-company-code", out var ccValue) ? ccValue.ToString() : null;
        if (string.IsNullOrWhiteSpace(companyCode))
        {
            return Results.BadRequest(new { error = "Missing x-company-code" });
        }

        await service.DeleteAsync(companyCode!, id, req.HttpContext.RequestAborted);
        return Results.Json(new { ok = true });
    }

    private static object ToDto(AgentAccountingRuleService.AccountingRule rule)
    {
        object? options = null;
        if (rule.Options is not null)
        {
            try
            {
                options = JsonSerializer.Deserialize<object>(rule.Options.GetRawText(), JsonOptions);
            }
            catch
            {
                options = rule.Options;
            }
        }

        return new
        {
            id = rule.Id,
            companyCode = rule.CompanyCode,
            title = rule.Title,
            description = rule.Description,
            keywords = rule.Keywords,
            accountCode = rule.AccountCode,
            accountName = rule.AccountName,
            note = rule.Note,
            priority = rule.Priority,
            isActive = rule.IsActive,
            options,
            createdAt = rule.CreatedAt,
            updatedAt = rule.UpdatedAt
        };
    }
}
static class AgentScenarioEndpoints
{
    public static object ToScenarioDto(AgentScenarioService.AgentScenario scenario)
        => ScenarioToResponse(scenario);

    public static async Task<IResult> UpsertAsync(HttpRequest req, string? keyFromRoute, AgentScenarioService service)
    {
        var companyCode = req.Headers.TryGetValue("x-company-code", out var ccValue) ? ccValue.ToString() : null;
        if (string.IsNullOrWhiteSpace(companyCode))
        {
        return Results.BadRequest(new { error = "Missing x-company-code" });
        }

        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
        var root = doc.RootElement;

        var scenarioKey = keyFromRoute;
        if (string.IsNullOrWhiteSpace(scenarioKey))
        {
            scenarioKey = root.TryGetProperty("scenarioKey", out var keyEl) && keyEl.ValueKind == JsonValueKind.String ? keyEl.GetString() : null;
        }
        if (string.IsNullOrWhiteSpace(scenarioKey))
        {
            return Results.BadRequest(new { error = "scenarioKey 必填" });
        }

        var title = root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String ? titleEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(title))
        {
            return Results.BadRequest(new { error = "title 必填" });
        }

        string? description = null;
        if (root.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
        {
            description = descEl.GetString();
        }

        string? instructions = null;
        if (root.TryGetProperty("instructions", out var instEl) && instEl.ValueKind == JsonValueKind.String)
        {
            instructions = instEl.GetString();
        }

        var priority = 100;
        if (root.TryGetProperty("priority", out var priEl) && priEl.ValueKind == JsonValueKind.Number)
        {
            priority = priEl.TryGetInt32(out var pri) ? pri : priority;
        }

        var isActive = true;
        if (root.TryGetProperty("isActive", out var activeEl) && activeEl.ValueKind == JsonValueKind.False)
        {
            isActive = false;
        }

        var toolHints = new List<string>();
        if (root.TryGetProperty("toolHints", out var hintsEl) && hintsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in hintsEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var hint = item.GetString();
                    if (!string.IsNullOrWhiteSpace(hint)) toolHints.Add(hint.Trim());
                }
            }
        }

        string? metadataJson = null;
        if (root.TryGetProperty("metadata", out var metadataEl))
        {
            if (metadataEl.ValueKind == JsonValueKind.Object || metadataEl.ValueKind == JsonValueKind.Array)
            {
                metadataJson = JsonNode.Parse(metadataEl.GetRawText())?.ToJsonString();
            }
            else if (metadataEl.ValueKind == JsonValueKind.String)
            {
                metadataJson = metadataEl.GetString();
            }
        }

        string? contextJson = null;
        if (root.TryGetProperty("context", out var contextEl) && contextEl.ValueKind != JsonValueKind.Null && contextEl.ValueKind != JsonValueKind.Undefined)
        {
            contextJson = contextEl.ValueKind == JsonValueKind.String ? contextEl.GetString() : JsonNode.Parse(contextEl.GetRawText())?.ToJsonString();
        }

        var input = new AgentScenarioService.AgentScenarioInput(
            scenarioKey!,
            title!,
            description,
            instructions,
            toolHints,
            metadataJson,
            contextJson,
            priority,
            isActive);

        var scenario = await service.UpsertAsync(companyCode!, input, req.HttpContext.RequestAborted);
        return Results.Json(ToScenarioDto(scenario));
    }

    public static async Task<IResult> ListAsync(HttpRequest req, AgentScenarioService service, bool includeInactive)
    {
        var companyCode = req.Headers.TryGetValue("x-company-code", out var ccValue) ? ccValue.ToString() : null;
        if (string.IsNullOrWhiteSpace(companyCode))
        {
            return Results.BadRequest(new { error = "Missing x-company-code" });
        }
        var items = await service.ListAsync(companyCode!, includeInactive, req.HttpContext.RequestAborted);
        return Results.Json(items.Select(ToScenarioDto));
    }

    public static async Task<IResult> GetAsync(HttpRequest req, string scenarioKey, AgentScenarioService service)
    {
        var companyCode = req.Headers.TryGetValue("x-company-code", out var ccValue) ? ccValue.ToString() : null;
        if (string.IsNullOrWhiteSpace(companyCode))
        {
        return Results.BadRequest(new { error = "Missing x-company-code" });
        }
        var scenario = await service.GetAsync(companyCode!, scenarioKey, req.HttpContext.RequestAborted);
        if (scenario is null) return Results.NotFound(new { error = "not found" });
        return Results.Json(ToScenarioDto(scenario));
    }

    public static async Task<IResult> DeleteAsync(HttpRequest req, string scenarioKey, AgentScenarioService service)
    {
        var companyCode = req.Headers.TryGetValue("x-company-code", out var ccValue) ? ccValue.ToString() : null;
        if (string.IsNullOrWhiteSpace(companyCode))
        {
        return Results.BadRequest(new { error = "Missing x-company-code" });
        }
        await service.DeleteAsync(companyCode!, scenarioKey, req.HttpContext.RequestAborted);
        return Results.Json(new { ok = true });
    }

    public static async Task<IResult> TestAsync(HttpRequest req, AgentKitService agentKit)
    {
        var companyCode = req.Headers.TryGetValue("x-company-code", out var ccValue) ? ccValue.ToString() : null;
        if (string.IsNullOrWhiteSpace(companyCode))
        {
            return Results.BadRequest(new { error = "Missing x-company-code" });
        }

        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
    var root = doc.RootElement;
        var scenarioKey = root.TryGetProperty("scenarioKey", out var keyEl) && keyEl.ValueKind == JsonValueKind.String ? keyEl.GetString() : null;
        var message = root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String ? msgEl.GetString() : null;
        var fileName = root.TryGetProperty("fileName", out var fnEl) && fnEl.ValueKind == JsonValueKind.String ? fnEl.GetString() : null;
        var contentType = root.TryGetProperty("contentType", out var ctEl) && ctEl.ValueKind == JsonValueKind.String ? ctEl.GetString() : null;
        var preview = root.TryGetProperty("preview", out var previewEl) && previewEl.ValueKind == JsonValueKind.String ? previewEl.GetString() : null;

        var result = await agentKit.PreviewScenariosAsync(companyCode!, scenarioKey, message, fileName, contentType, preview, req.HttpContext.RequestAborted);
        return Results.Json(result);
    }

    public static async Task<IResult> InterpretAsync(HttpRequest req, AgentScenarioService service, AgentKitService agentKit, IConfiguration configuration)
    {
        var companyCode = req.Headers.TryGetValue("x-company-code", out var ccValue) ? ccValue.ToString() : null;
        if (string.IsNullOrWhiteSpace(companyCode))
        {
        return Results.BadRequest(new { error = "Missing x-company-code" });
        }
        var companyCodeValue = companyCode;

        var apiKey = AiFileHelpers.ResolveOpenAIApiKey(req, configuration);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Results.BadRequest(new { error = "Anthropic API Key 未配置" });
        }
        var apiKeyValue = apiKey;

        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
    var root = doc.RootElement;
        var prompt = root.TryGetProperty("prompt", out var promptEl) && promptEl.ValueKind == JsonValueKind.String ? promptEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Results.BadRequest(new { error = "prompt 必填" });
        }
        var promptValue = prompt;

        var existing = await service.ListAsync(companyCodeValue, includeInactive: true, req.HttpContext.RequestAborted);
        var generated = await agentKit.GenerateScenarioAsync(companyCodeValue, promptValue, apiKeyValue, existing, req.HttpContext.RequestAborted);
        return Results.Json(generated);
    }

    public static object ScenarioToResponse(AgentScenarioService.AgentScenario scenario)
    {
        object metadataObj = scenario.Metadata ?? (object)new { };
        object toolHintsObj = scenario.ToolHints ?? (object)Array.Empty<string>();
        object contextObj = scenario.Context ?? (object)new { };

        return new
        {
            scenarioKey = scenario.ScenarioKey,
            title = scenario.Title,
            description = scenario.Description,
            instructions = scenario.Instructions,
            toolHints = toolHintsObj,
            priority = scenario.Priority,
            isActive = scenario.IsActive,
            updatedAt = scenario.UpdatedAt,
            metadata = metadataObj,
            context = contextObj
        };
    }
    public static AgentScenarioService.AgentScenarioInput ReadScenarioInput(JsonElement root, string? scenarioKeyOverride)
    {
        string scenarioKey;
        if (!string.IsNullOrWhiteSpace(scenarioKeyOverride))
        {
            scenarioKey = scenarioKeyOverride.Trim();
        }
        else if (root.TryGetProperty("scenarioKey", out var keyEl) && keyEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(keyEl.GetString()))
        {
            scenarioKey = keyEl.GetString()!.Trim();
        }
        else
        {
            throw new Exception("scenarioKey 必填");
        }

        if (string.IsNullOrWhiteSpace(scenarioKey)) throw new Exception("scenarioKey 不能为空");

        string title = root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String
            ? titleEl.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(title)) title = scenarioKey;

        string? description = root.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String
            ? descEl.GetString()
            : null;

        string? instructions = root.TryGetProperty("instructions", out var instEl) && instEl.ValueKind == JsonValueKind.String
            ? instEl.GetString()
            : null;

        var toolHints = new List<string>();
        if (root.TryGetProperty("toolHints", out var hintsEl))
        {
            switch (hintsEl.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in hintsEl.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            var hint = item.GetString();
                            if (!string.IsNullOrWhiteSpace(hint)) toolHints.Add(hint.Trim());
                        }
                    }
                    break;
                case JsonValueKind.String:
                    var hintSingle = hintsEl.GetString();
                    if (!string.IsNullOrWhiteSpace(hintSingle)) toolHints.Add(hintSingle.Trim());
                    break;
            }
        }

        int priority = 100;
        if (root.TryGetProperty("priority", out var priEl) && priEl.ValueKind == JsonValueKind.Number)
        {
            priority = priEl.GetInt32();
        }

        bool isActive = true;
        if (root.TryGetProperty("isActive", out var activeEl))
        {
            isActive = activeEl.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => activeEl.GetInt32() != 0,
                JsonValueKind.String => bool.TryParse(activeEl.GetString(), out var parsed) ? parsed : true,
                _ => true
            };
        }

        string? metadataJson = null;
        if (root.TryGetProperty("metadata", out var metadataEl) && metadataEl.ValueKind != JsonValueKind.Null && metadataEl.ValueKind != JsonValueKind.Undefined)
        {
            metadataJson = metadataEl.ValueKind == JsonValueKind.String
                ? metadataEl.GetString()
                : JsonNode.Parse(metadataEl.GetRawText())?.ToJsonString();
        }

        string? contextJson = null;
        if (root.TryGetProperty("context", out var contextEl) && contextEl.ValueKind != JsonValueKind.Null && contextEl.ValueKind != JsonValueKind.Undefined)
        {
            contextJson = contextEl.ValueKind == JsonValueKind.String
                ? contextEl.GetString()
                : JsonNode.Parse(contextEl.GetRawText())?.ToJsonString();
        }

        return new AgentScenarioService.AgentScenarioInput(scenarioKey, title, description, instructions, toolHints, metadataJson, contextJson, priority, isActive);
    }
}