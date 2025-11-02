// =============================
// 本文件为最小可运行的 .NET 8 Web API 入口：
// - 启动时执行数据库迁移（创建表/生成列/索引/种子 jsonstructures）
// - 通用 CRUD 与搜索 DSL（通过 jsonstructures 的 schema/query/coreFields 驱动）
// - 凭证特殊逻辑：借贷平衡校验、yymm+6 编号、注入 companyCode/voucherNo
// - Azure Blob 附件读取：按需签发短期只读 SAS
// 约定：所有请求须携带 x-company-code 作为租户隔离键
// =============================
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Json.Schema;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;
using System.Linq;
using System.Collections.Generic;
using Server.Infrastructure; // 引入 Auth/Database/Storage 工具
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Server.Domain; // 引入 SchemasService
using Server.Modules;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

// 权限辅助（需置于顶部，先于顶层语句）
// 权限模型：
// - 通过请求头 x-user-id / x-roles / x-dept-id 构建用户上下文
// - 通过 jsonstructures.auth.actions 判定动作是否允许
// - 通过 jsonstructures.auth.scopes 生成行级过滤 SQL 片段，并参数化避免注入
// 说明：之所以放在顶部，是因为 .NET 8 顶层语句限制，需先声明再使用

// 移除 Program.cs 内部的权限辅助，改从 Server.Infrastructure.Auth 引用

var builder = WebApplication.CreateBuilder(args);

var allowedOrigins = (builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? Array.Empty<string>())
    .Select(o => o?.Trim())
    .Where(o => !string.IsNullOrWhiteSpace(o))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
if (allowedOrigins.Count == 0)
{
    allowedOrigins.Add("http://localhost:5180");
    allowedOrigins.Add("http://127.0.0.1:5180");
}
var allowedOriginsArray = allowedOrigins.ToArray();

// 数据库与存储注册改为基础设施扩展方法
builder.Services.AddPostgres(builder.Configuration);
builder.Services.AddAzureBlob(builder.Configuration);
// BlobServiceClient 由 Storage 基础设施统一注册
builder.Services.AddSingleton<Server.Infrastructure.LawDatasetService>();
builder.Services.AddScoped<Server.Modules.HrCrudService>();
builder.Services.AddScoped<Server.Modules.InvoiceRegistryService>();
builder.Services.AddScoped<Server.Modules.FinanceService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<Server.Infrastructure.ApnsService>();
builder.Services.AddHostedService<Server.Infrastructure.NotificationSchedulerService>();
builder.Services.AddHostedService<Server.Infrastructure.TaskSchedulerService>();

// CORS（开发环境放开）应在 Build 之前注册
builder.Services.AddCors(opts =>
{
    opts.AddPolicy("DevAll", p => p
        .WithOrigins(allowedOriginsArray)
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials());
});

// JWT 配置（简单密钥，演示用）
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

var app = builder.Build();
// 全局最早注入 CORS 允许头（包括出现异常/401 时），并处理预检
// 统一 CORS 处理（单一中间件，覆盖预检和所有响应）
app.UseCors("DevAll");
// 兜底：确保所有响应都带上 CORS 头（包括 401/403/异常）
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
            ctx.Response.Headers["Access-Control-Allow-Headers"] = string.IsNullOrEmpty(reqHdrs) ? "Authorization,Content-Type,x-company-code,x-openai-key" : reqHdrs;
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
    // 将 JWT Claims 映射为现有头，便于复用 Auth.GetUserCtx
    var uid = ctx.User?.FindFirst("uid")?.Value;
    var roles = ctx.User?.FindFirst("roles")?.Value;
    var dept = ctx.User?.FindFirst("deptId")?.Value;
    if (!string.IsNullOrEmpty(uid)) ctx.Request.Headers["x-user-id"] = uid;
    if (!string.IsNullOrEmpty(roles)) ctx.Request.Headers["x-roles"] = roles;
    if (!string.IsNullOrEmpty(dept)) ctx.Request.Headers["x-dept-id"] = dept;
    await next();
});
app.UseAuthorization();

// 兜底处理所有预检请求（已在上方中间件返回 204），此处保留以防中间件被绕过
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

// 在应用启动时运行迁移脚本：
// - 目的：保证表结构/索引/生成列/种子数据(jsonstructures) 就绪（幂等，可重复执行）
// - 使用嵌入式 SQL 文件（migrate.sql），避免手工执行步骤遗漏
// - 生产环境可替换为正式的迁移管道；MVP 阶段优先可运行
app.Lifetime.ApplicationStarted.Register(async () =>
{
    try
    {
        var ds = app.Services.GetRequiredService<NpgsqlDataSource>();
        await using (var conn = await ds.OpenConnectionAsync())
        {
            var sqlPath = Path.Combine(AppContext.BaseDirectory, "migrate.sql");
            if (File.Exists(sqlPath))
            {
                var sql = await File.ReadAllTextAsync(sqlPath);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
    catch (Exception ex)
    {
        try { Console.WriteLine("[migrate] skipped: " + ex.Message); } catch {}
        // 忽略迁移失败，保证服务可用
    }

    // 兜底：即使迁移整体失败，确保关键表与最小 schema 种子存在（避免 42P01/404）
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

        // 最小 schema 种子：approval_task / certificate_request（仅当不存在时插入）
        // 使用 SchemasService 以避免复杂 SQL 字符串转义
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
        // 新增：company_setting schema 兜底注册（全局）
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
                        seal = new {
                            type = "object",
                            properties = new {
                                format = new { type = "string" },
                                size = new { type = "number", minimum = 0 },
                                offsetX = new { type = "number" },
                                offsetY = new { type = "number" },
                                opacity = new { type = "number", minimum = 0, maximum = 1 },
                                // 仅用于输入，不会明文落库
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
                                    new { field = "companyName", label = "公司名称", span = 12 },
                                    new { field = "companyAddress", label = "公司地址", span = 12 },
                                    new { field = "companyRep", label = "代表者", span = 6 },
                                    new { field = "workdayDefaultStart", label = "上班(HH:mm)", span = 6 },
                                    new { field = "workdayDefaultEnd", label = "下班(HH:mm)", span = 6 },
                                    new { field = "lunchMinutes", label = "午休(分钟)", span = 6, props = new { type = "number" } }
                                } }
                            , new { type = "grid", cols = new object[]
                                {
                                    new { field = "seal.format", label = "印章格式(png/jpg)", span = 6 },
                                    new { field = "seal.size", label = "印章尺寸(pt)", span = 6, props = new { type = "number" } },
                                    new { field = "seal.offsetX", label = "X偏移(pt)", span = 6, props = new { type = "number" } },
                                    new { field = "seal.offsetY", label = "Y偏移(pt)", span = 6, props = new { type = "number" } },
                                    new { field = "seal.opacity", label = "不透明度(0-1)", span = 6, props = new { type = "number" } }
                                } }
                            , new { field = "seal.plainBase64", label = "印章Base64(不会明文落库)", widget = "textarea", props = new { type = "textarea", rows = 3 } }
                            , new { field = "seal.dataUrl", label = "印章DataURL(可选)", widget = "textarea", props = new { type = "textarea", rows = 2 } }
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

// 兜底：若迁移整体失败，额外尝试创建库存相关表与 material 等 schema（幂等）
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

// 注册 HR 工资模块端点
app.MapHrPayrollModule();
app.MapTaskSchedulerModule();
// 注册 库存管理模块端点
Server.Modules.InventoryModule.MapInventoryModule(app);
// 注册 法规维护模块端点
Server.Modules.LawAdminModule.MapLawAdminModule(app);
// 注册 推送通知模块端点
Server.Modules.NotificationsModule.MapNotificationsModule(app);
// 注册 通知策略模块端点
Server.Modules.NotificationsPoliciesModule.MapNotificationsPoliciesModule(app);
// 注册 本地化维护端点
Server.Modules.LocalizationMaintenanceModule.MapLocalizationMaintenanceModule(app);
// 注册 CRM 模块端点
Server.Modules.CrmModule.MapCrmModule(app);

app.MapGet("/health", async (IServiceProvider sp) =>
{
    var env = app.Environment.EnvironmentName;
    // 检查数据库
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

    // 检查存储
    var blobs = sp.GetService<BlobServiceClient>();
    var storageOk = blobs is not null;
    var cfg = sp.GetService<IConfiguration>();
    var container = cfg?.GetSection("AzureStorage")["Container"] ?? "attachments";

    var ok = dbOk; // 就绪定义：至少数据库可连接
    return Results.Ok(new
    {
        ok,
        env,
        db = new { ok = dbOk, error = dbErr },
        storage = new { ok = storageOk, container }
    });
});
// 临时调试：检查 OPENAI_API_KEY 是否可见
app.MapGet("/debug/ai-key", () =>
{
    var k = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    return Results.Ok(new { hasKey = !string.IsNullOrWhiteSpace(k), length = k?.Length ?? 0 });
});

// 调试：按凭证号查看 open_items 投影
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

// 维护：重建 open_items 投影（按凭证号或全部）
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
        // 选取目标凭证
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
            // 先删除旧的投影
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
                // 仅对 openItem 科目生成投影
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

// 开发用：一次性创建测试用户（若该公司尚无用户）
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
    // 若该公司已有任何用户，则拒绝（避免误用）
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
    // 给角色加一些基础能力，便于演示
    var caps = new[]{ "roles:manage", "op:bank-collect", "op:bank-payment" };
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

// 登录接口：companyCode + employeeCode + password -> JWT
app.MapPost("/auth/login", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    var company = root.GetProperty("companyCode").GetString()!;
    var emp = root.GetProperty("employeeCode").GetString()!;
    var pwd = root.GetProperty("password").GetString()!;

    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT id, password_hash, name, dept_id FROM users WHERE company_code=$1 AND employee_code=$2 LIMIT 1";
    cmd.Parameters.AddWithValue(company);
    cmd.Parameters.AddWithValue(emp);
    await using var rd = await cmd.ExecuteReaderAsync();
    if (!await rd.ReadAsync()) return Results.Unauthorized();
    var userId = rd.GetGuid(0);
    var hash = rd.GetString(1);
    var name = rd.IsDBNull(2) ? null : rd.GetString(2);
    var deptId = rd.IsDBNull(3) ? null : rd.GetString(3);
    if (!BCrypt.Net.BCrypt.Verify(pwd, hash)) return Results.Unauthorized();

    // 取角色
    await rd.CloseAsync();
    await using var cmdR = conn.CreateCommand();
    cmdR.CommandText = @"SELECT r.role_code FROM roles r JOIN user_roles ur ON ur.role_id=r.id WHERE ur.user_id=$1";
    cmdR.Parameters.AddWithValue(userId);
    var roles = new List<string>();
    await using (var r2 = await cmdR.ExecuteReaderAsync())
        while (await r2.ReadAsync()) roles.Add(r2.GetString(0));

    // 取 capabilities（从角色聚合 role_caps.cap）
    var caps = new List<string>();
    await using (var cmdC = conn.CreateCommand())
    {
        cmdC.CommandText = @"SELECT DISTINCT rc.cap FROM role_caps rc JOIN user_roles ur ON ur.role_id=rc.role_id WHERE ur.user_id=$1";
        cmdC.Parameters.AddWithValue(userId);
        await using var rc = await cmdC.ExecuteReaderAsync();
        while (await rc.ReadAsync()) caps.Add(rc.GetString(0));
    }

    var claims = new[]
    {
        new System.Security.Claims.Claim("uid", userId.ToString()),
        new System.Security.Claims.Claim("companyCode", company),
        new System.Security.Claims.Claim("employeeCode", emp),
        new System.Security.Claims.Claim("name", name ?? string.Empty),
        new System.Security.Claims.Claim("deptId", deptId ?? string.Empty),
        new System.Security.Claims.Claim("roles", string.Join(',', roles)),
        new System.Security.Claims.Claim("caps", string.Join(',', caps))
    };
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

// 当前用户信息
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

// 调试：自检创建一张最小凭证（不依赖前端），返回凭证号或错误
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
        // 使用统一编号服务
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

// AI 路由：返回 actions（如 openEmbed(key,payload)），用于前端根据意图打开页面并预填
app.MapPost("/ai/route", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    var apiKey = req.Headers.TryGetValue("x-openai-key", out var hk) && !string.IsNullOrWhiteSpace(hk)
        ? hk.ToString()
        : (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? app.Configuration["OpenAI:ApiKey"]);
    if (string.IsNullOrWhiteSpace(apiKey)) return Results.BadRequest(new { error = "OpenAI API key not configured" });

    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    var messages = root.TryGetProperty("messages", out var ms) && ms.ValueKind==JsonValueKind.Array ? ms : default;
    if (ms.ValueKind != JsonValueKind.Array) return Results.BadRequest(new { error = "messages required" });

    // 从 schemas 读取可用实体/路由（MVP：示例包含 account.new / voucher.new）
    var entities = new List<object>();
    try
    {
        var schemaAcc = await Server.Domain.SchemasService.GetActiveSchema(ds, "account", req.Headers.TryGetValue("x-company-code", out var ccRoute) ? ccRoute.ToString() : null);
        if (schemaAcc is not null && schemaAcc.RootElement.TryGetProperty("schema", out var sAcc))
        {
            // 仅暴露安全字段示例
            var fields = new List<string>();
            if (sAcc.TryGetProperty("properties", out var props) && props.ValueKind==JsonValueKind.Object)
            {
                foreach (var p in props.EnumerateObject())
                {
                    if (p.Name is "code" or "name") fields.Add(p.Name);
                }
            }
            entities.Add(new { key = "account.new", entity = "account", fields });
        }
        var schemaV = await Server.Domain.SchemasService.GetActiveSchema(ds, "voucher", req.Headers.TryGetValue("x-company-code", out var ccRoute2) ? ccRoute2.ToString() : null);
        if (schemaV is not null)
        {
            // 列出允许预填的一些头字段与行字段
            var vfields = new []{ "header.currency", "header.postingDate", "header.summary", "lines[].accountCode", "lines[].drcr", "lines[].amount" };
            entities.Add(new { key = "voucher.new", entity = "voucher", fields = vfields });
        }
    }
    catch { }

    // 构造工具定义（OpenAI function calling 格式）
    var tools = new[]
    {
        new {
            type = "function",
            function = new {
                name = "open_embed",
                description = "Open a UI page by key and optionally pass initial payload for prefill",
                parameters = new {
                    type = "object",
                    properties = new {
                        key = new { type = "string", description = "Page key, e.g. account.new" },
                        payload = new { type = "object", description = "Initial form fields to prefill" }
                    },
                    required = new [] { "key" }
                }
            }
        }
    };

    var sysPrompt = @$"You are an ERP assistant. Decide user intent and call open_embed with an appropriate page key and a minimal payload to prefill fields.
Available pages (key → entity, fields):
{string.Join("\n", entities.Select(e => System.Text.Json.JsonSerializer.Serialize(e)))}
Rules:
- Prefer a single call to open_embed that includes payload when user asks to create/edit something.
- Keep payload small and only include known fields for that entity.
- If the user asks to 'create voucher' or similar, call open_embed with key 'voucher.new'. Prefill header.currency if mentioned, header.postingDate if a date is mentioned, and header.summary if provided. If an amount is mentioned, set lines[0].amount and lines[0].drcr if indicated (DR/CR or debit/credit). Do not invent account codes; leave accountCode empty for the user to choose.
- If unsure, ask a short clarifying question in assistant message and do not call tools.
";

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    var chatBody = new {
        model = "gpt-4o-mini",
        messages = new List<object> { new { role = "system", content = sysPrompt } }
            .Concat(ms.EnumerateArray().Select(x => new { role = x.GetProperty("role").GetString(), content = x.GetProperty("content").GetString() }))
            .ToArray(),
        tools,
        tool_choice = "auto",
        temperature = 0.2
    };
    var resp = await http.PostAsync("https://api.openai.com/v1/chat/completions",
        new StringContent(JsonSerializer.Serialize(chatBody), System.Text.Encoding.UTF8, "application/json"));
    var text = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode) return Results.StatusCode((int)resp.StatusCode);

    using var parsed = JsonDocument.Parse(text);
    var msg = parsed.RootElement.GetProperty("choices")[0].GetProperty("message");
    string assistantMessage = msg.TryGetProperty("content", out var c) && c.ValueKind==JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
    var actions = new List<object>();
    if (msg.TryGetProperty("tool_calls", out var tc) && tc.ValueKind==JsonValueKind.Array)
    {
        foreach (var tcall in tc.EnumerateArray())
        {
            var name = tcall.GetProperty("function").GetProperty("name").GetString();
            var argsText = tcall.GetProperty("function").GetProperty("arguments").GetString() ?? "{}";
            try
            {
                using var a = JsonDocument.Parse(argsText);
                if (name == "open_embed")
                {
                    var key = a.RootElement.TryGetProperty("key", out var k) ? k.GetString() : null;
                    object? payloadObj = null;
                    if (a.RootElement.TryGetProperty("payload", out var p))
                    {
                        try { payloadObj = JsonSerializer.Deserialize<object>(p.GetRawText()); }
                        catch { payloadObj = null; }
                    }
                    if (!string.IsNullOrEmpty(key))
                    {
                        actions.Add(new { type = "openEmbed", key, payload = payloadObj } );
                    }
                }
            } catch { }
        }
    }
    return Results.Json(new { assistantMessage, actions });
}).RequireAuthorization();
// AI 聊天（最小代理）：需要设置环境变量 OPENAI_API_KEY；未设置则返回 501
app.MapPost("/ai/chat", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    var apiKey = req.Headers.TryGetValue("x-openai-key", out var hk) && !string.IsNullOrWhiteSpace(hk)
        ? hk.ToString()
        : (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? app.Configuration["OpenAI:ApiKey"]);
    if (string.IsNullOrWhiteSpace(apiKey)) return Results.BadRequest(new { error = "OpenAI API key not configured" });
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    var sessionId = root.TryGetProperty("sessionId", out var sid) && sid.ValueKind==JsonValueKind.String ? sid.GetString() : null;
    var messages = root.TryGetProperty("messages", out var ms) && ms.ValueKind==JsonValueKind.Array ? ms : default;
    if (ms.ValueKind != JsonValueKind.Array) return Results.BadRequest(new { error = "messages required" });

    // 若无 sessionId 则创建会话
    if (string.IsNullOrEmpty(sessionId))
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO ai_sessions(company_code,user_id,title) VALUES ($1,$2,$3) RETURNING id";
        var cc = req.Headers.TryGetValue("x-company-code", out var h) ? h.ToString() : "JP01";
        var uid = req.Headers.TryGetValue("x-user-id", out var u) ? u.ToString() : "demo";
        cmd.Parameters.AddWithValue(cc);
        cmd.Parameters.AddWithValue(uid);
        cmd.Parameters.AddWithValue("聊天会话");
        var id = await cmd.ExecuteScalarAsync();
        sessionId = id?.ToString();
    }

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    var body = new
    {
        model = "gpt-4o-mini",
        messages = ms.EnumerateArray().Select(x => new { role = x.GetProperty("role").GetString(), content = x.GetProperty("content").GetString() }).ToArray(),
        temperature = 0.2
    };
    var resp = await http.PostAsync("https://api.openai.com/v1/chat/completions",
        new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"));
    var text = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode) return Results.StatusCode((int)resp.StatusCode);
    // 持久化消息
    try
    {
        using var parsed = JsonDocument.Parse(text);
        var assistant = parsed.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        await using var conn = await ds.OpenConnectionAsync();
        // 保存用户消息
        foreach (var m in ms.EnumerateArray())
        {
            await using var u = conn.CreateCommand();
            u.CommandText = "INSERT INTO ai_messages(session_id, role, content) VALUES ($1,$2,$3)";
            u.Parameters.AddWithValue(Guid.Parse(sessionId!));
            u.Parameters.AddWithValue(m.GetProperty("role").GetString()!);
            u.Parameters.AddWithValue(m.GetProperty("content").GetString()!);
            await u.ExecuteNonQueryAsync();
        }
        // 保存助手消息
        await using (var a = ds.CreateCommand())
        {
            await using var conn2 = await ds.OpenConnectionAsync();
            await using var a2 = conn2.CreateCommand();
            a2.CommandText = "INSERT INTO ai_messages(session_id, role, content) VALUES ($1,$2,$3)";
            a2.Parameters.AddWithValue(Guid.Parse(sessionId!));
            a2.Parameters.AddWithValue("assistant");
            a2.Parameters.AddWithValue(assistant ?? string.Empty);
            await a2.ExecuteNonQueryAsync();
        }
        await using (var upd = conn.CreateCommand())
        {
            upd.CommandText = "UPDATE ai_sessions SET updated_at = now() WHERE id=$1";
            upd.Parameters.AddWithValue(Guid.Parse(sessionId!));
            await upd.ExecuteNonQueryAsync();
        }
    }
    catch { }
    return Results.Text(text, "application/json");
}).RequireAuthorization();

// AI: 将自然语言薪资规则编译为内部 DSL（骨架）
// moved to HrPayrollModule

// Payroll: 工资预览（骨架，不做真实计算）
// moved to HrPayrollModule

// AI: 工资项目 → 会计科目建议（骨架）
// moved to HrPayrollModule

// 会话列表
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
    await using var rd = await cmd.ExecuteReaderAsync();
    var list = new List<object>();
    while (await rd.ReadAsync()) list.Add(new { id = rd.GetGuid(0), title = rd.IsDBNull(1)? null: rd.GetString(1), createdAt = rd.GetDateTime(2), updatedAt = rd.GetDateTime(3) });
    return Results.Json(list);
}).RequireAuthorization();

app.MapPost("/ai/sessions", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "missing company" });
    if (!req.Headers.TryGetValue("x-user-id", out var uid) || string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
    var title = "聊天会话";
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
        {
            var raw = t.GetString();
            if (!string.IsNullOrWhiteSpace(raw)) title = raw!;
        }
    }
    catch { }

    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO ai_sessions(company_code,user_id,title) VALUES ($1,$2,$3) RETURNING id";
    cmd.Parameters.AddWithValue(cc.ToString());
    cmd.Parameters.AddWithValue(uid.ToString());
    cmd.Parameters.AddWithValue(title);
    var id = await cmd.ExecuteScalarAsync();
    if (id is null) return Results.Problem("failed to create session");
    return Results.Json(new { id = id.ToString() });
}).RequireAuthorization();

// 会话消息
app.MapGet("/ai/sessions/{id}/messages", async (Guid id, NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT role, content, payload::text, created_at FROM ai_messages WHERE session_id=$1 ORDER BY created_at ASC";
    cmd.Parameters.AddWithValue(id);
    await using var rd = await cmd.ExecuteReaderAsync();
    var list = new List<object>();
    while (await rd.ReadAsync())
    {
        var role = rd.GetString(0);
        var content = rd.IsDBNull(1) ? null : rd.GetString(1);
        object? payload = null;
        if (!rd.IsDBNull(2))
        {
            var text = rd.GetString(2);
            try
            {
                using var doc = JsonDocument.Parse(text);
                payload = doc.RootElement.Clone();
            }
            catch
            {
                payload = null;
            }
        }
        var createdAt = rd.GetDateTime(3);
        list.Add(new { role, content, payload, createdAt });
    }
    return Results.Json(list);
}).RequireAuthorization();

app.MapPost("/ai/sessions/{id}/messages", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
{
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

    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    try
    {
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            if (payloadText is null)
            {
                cmd.CommandText = "INSERT INTO ai_messages(session_id, role, content) VALUES ($1,$2,$3)";
                cmd.Parameters.AddWithValue(id);
                cmd.Parameters.AddWithValue(role!);
                cmd.Parameters.AddWithValue((object?)content ?? DBNull.Value);
            }
            else
            {
                cmd.CommandText = "INSERT INTO ai_messages(session_id, role, content, payload) VALUES ($1,$2,$3,$4)";
                cmd.Parameters.AddWithValue(id);
                cmd.Parameters.AddWithValue(role!);
                cmd.Parameters.AddWithValue((object?)content ?? DBNull.Value);
                cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, payloadText);
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

// 只读：获取激活的 Schema（便于 AgentKit 读取 query 白名单、UI 布局、validators 等）
// 返回值为完整行 to_jsonb(jsonstructures)，包含 schema/ui/query/core_fields/validators/numbering/ai_hints
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

// 列出可用 schema 名称与最新版本（仅用于前端/Agent 枚举）
app.MapGet("/schemas", async (HttpContext ctx, NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      WITH latest AS (
        SELECT name, MAX(version) AS version
        FROM schemas
        GROUP BY name
      )
      SELECT COALESCE(
        jsonb_agg(jsonb_build_object('name', l.name, 'version', l.version)),
        '[]'::jsonb
      )
      FROM latest l;";
    var json = (string?)await cmd.ExecuteScalarAsync();
    ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    ctx.Response.Headers["Pragma"] = "no-cache";
    ctx.Response.Headers["Expires"] = "0";
    return Results.Text(json ?? "[]", "application/json; charset=utf-8");
});

// 保存 schema：写入新版本并激活
// 语义：每次保存形成一个新版本；先将旧版本 is_active 置为 FALSE，再插入新版本并激活
// 注意：不在此处做 schema 结构合法性校验（交由前端或后续加载时校验）
app.MapPost("/schemas/{name}", async (HttpRequest req, string name, NpgsqlDataSource ds) =>
{
    using var body = await JsonDocument.ParseAsync(req.Body);
    var root = body.RootElement;
    var ccHeader = req.Headers.TryGetValue("x-company-code", out var h) ? h.ToString() : null;
    var json = await SchemasService.SaveAndActivate(ds, name, root, ccHeader);
    if (json is null) return Results.Problem("insert schema failed");
    return Results.Text(json, "application/json");
}).RequireAuthorization();

// 读取激活的 Schema 定义（来自 jsonstructures）用于：
// - JSON Schema 校验
// - 可筛选/可排序白名单（query.filters/sorts）
// - coreFields（生成列与唯一约束已在迁移中落地）
// GetActiveSchema 已抽离到 Domain/SchemasService

// 表名映射迁移至 Infrastructure.Crud.TableFor

// 通用创建：
// - 强制要求请求头 x-company-code，作为多租户数据隔离键
// - 从 jsonstructures 读取激活 schema，并使用 JsonSchema.Net 逐字段校验（含 maxLength/pattern/format/if-then-else 等）
// - 权限：auth.actions["create"] 角色白名单
// - voucher 特例：
//   1) 借贷平衡校验（按 drcr/amount 汇总）
//   2) 按 yymm + 6 位序号生成凭证号（voucher_sequences 事务内安全递增）
//   3) 通过 jsonb_set 在 SQL 侧回写 header.companyCode 与 header.voucherNo，保证落表即一致
//   4) 针对 openItem 科目，为每条明细生成 open_items 投影行，便于后续清账/匹配
app.MapPost("/objects/{entity}", async (HttpRequest req, string entity, NpgsqlDataSource ds, Server.Modules.HrCrudService hr, Server.Modules.FinanceService finance) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    using var body = await JsonDocument.ParseAsync(req.Body);
    if (!body.RootElement.TryGetProperty("payload", out var payload))
        return Results.BadRequest(new { error = "payload required" });

    var schemaDoc = await SchemasService.GetActiveSchema(ds, entity, cc.ToString());
    if (schemaDoc is null) return Results.NotFound(new { error = "schema not found" });
    var schema = JsonSchema.FromText(schemaDoc.RootElement.GetProperty("schema").GetRawText());
    var user = Auth.GetUserCtx(req);
    if (!Auth.IsActionAllowed(schemaDoc, "create", user)) return Results.StatusCode(403);
    if (!(string.Equals(entity, "payroll_policy", StringComparison.OrdinalIgnoreCase)
          || string.Equals(entity, "employment_type", StringComparison.OrdinalIgnoreCase)
          || string.Equals(entity, "certificate_request", StringComparison.OrdinalIgnoreCase)))
    {
        var result = schema.Evaluate(payload);
        if (!result.IsValid) return Results.BadRequest(new { error = "schema validation failed", details = result.Details });
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
        try { var json = await finance.CreateVoucher(cc.ToString()!, table, payload, user); return Results.Text(json, "application/json"); }
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
            // 以 company_code 为主键的 UPSERT
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            // 对 seal.plainBase64 或 seal.dataUrl 进行加密存储（DPAPI），仅保存 seal.enc
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
                            var enc = System.Security.Cryptography.ProtectedData.Protect(bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                            var encB64 = Convert.ToBase64String(enc);
                            // 重写 seal 对象，仅保留 enc 与配置项
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
            // 不再自动生成编码；编码由前端手工输入并由 schema 校验
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
            // 直接插入 payload（code,name 由前端/Schema 校验必填），捕获并回传可读错误
            var inserted = await Crud.InsertRawJson(ds, table, cc.ToString()!, payload.GetRawText());
            if (inserted is null) return Results.Problem("insert failed");
            return Results.Text(inserted, "application/json");
        }
        catch (PostgresException pgex) when (pgex.SqlState == "42P01")
        {
            // 缺表：动态创建后重试一次
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
            // 证明确认申请：插入后初始化首批审批待办并邮件通知
            bool isCert = string.Equals(entity, "certificate_request", StringComparison.OrdinalIgnoreCase);
            // 特例：timesheet 自动注入创建者与衍生字段（避免前端显式传员工）
            if (string.Equals(entity, "timesheet", StringComparison.OrdinalIgnoreCase))
            {
                var userCtx = Auth.GetUserCtx(req);
                // payload + creatorUserId + createdMonth（尽量不修改 schema 签名，后续 schema 可声明只读字段）
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
            // certificate_request: 注入发起人信息与初始状态
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
                    // 初始化审批
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

        // inventory_movement: 走专用流程（插入 movement + 展开 ledger + 更新 balances）
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

// 规划审批路线：读取 schema.approval 与对象 payload，返回实际步骤与候选人
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

// 推进审批：当前人 approve/reject -> 写日志/关闭本步 -> 进入下一步或完成
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

    // 校验：存在我的 pending 任务
    Guid taskId = Guid.Empty; int stepNo = 0; string? stepName = null;
    await using (var pick = conn.CreateCommand())
    {
        pick.CommandText = "SELECT id, step_no, step_name FROM approval_tasks WHERE company_code=$1 AND entity=$2 AND object_id=$3 AND approver_user_id=$4 AND status='pending' LIMIT 1";
        pick.Parameters.AddWithValue(cc.ToString()); pick.Parameters.AddWithValue(entity); pick.Parameters.AddWithValue(objectId); pick.Parameters.AddWithValue(approverId);
        await using var rd = await pick.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) { await tx.RollbackAsync(); return Results.StatusCode(403); }
        taskId = rd.GetGuid(0); stepNo = rd.GetInt32(1); stepName = rd.IsDBNull(2)? null: rd.GetString(2);
    }
    // 完成我的任务
    await using (var upd = conn.CreateCommand())
    {
        upd.CommandText = "UPDATE approval_tasks SET status=$2, updated_at=now() WHERE id=$1";
        upd.Parameters.AddWithValue(taskId); upd.Parameters.AddWithValue(action=="approve"? "approved":"rejected");
        await upd.ExecuteNonQueryAsync();
    }

    // 如果是驳回，直接标记对象状态并结束
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

    // 判断当前步骤是否全部完成（pick:any 则无需全部）
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
        // 还有未完成的本步任务？
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
        // any：关闭同一步的其他 pending 任务
        await using var close = conn.CreateCommand();
        close.CommandText = "UPDATE approval_tasks SET status='approved', updated_at=now() WHERE company_code=$1 AND entity=$2 AND object_id=$3 AND step_no=$4 AND status='pending'";
        close.Parameters.AddWithValue(cc.ToString()); close.Parameters.AddWithValue(entity); close.Parameters.AddWithValue(objectId); close.Parameters.AddWithValue(stepNo);
        await close.ExecuteNonQueryAsync();
    }

    // 生成下一步或结束
    var plan = await BuildApprovalPlan(ds, cc.ToString()!, entity, objectId);
    var nextStepNo = stepNo + 1;
    var next = plan.FirstOrDefault(s => s.stepNo == nextStepNo);
    if (next.Equals(default((int, string?, System.Collections.Generic.List<(string userId, string? email)>))))
    {
        // 完成：标记对象 approved，若实体为 certificate_request 发送 PDF 证书邮件
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
                // 生成并发送邮件
                var send = await SendCertificatePdfAsync(app.Configuration, ds, cc.ToString()!, objectId);
                try { return Results.Ok(new { ok=true, finished=true, status="approved", mail = send }); } catch {}
                // 生成 PDF 并保存到存储/或直接保存 base64（示例：先保存 base64，便于前端下载）
                try { await SaveCertificatePdfUrlAsync(app.Configuration, ds, cc.ToString()!, objectId); } catch {}
            }
            catch { }
        }
        return Results.Ok(new { ok=true, finished=true, status="approved" });
    }
    else
    {
        // 写入下一步 tasks 并发通知
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

// AI：将自然语言审批策略编译为 approval JSON（骨架）
app.MapPost("/ai/approvals/compile", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    var apiKey = req.Headers.TryGetValue("x-openai-key", out var hk) && !string.IsNullOrWhiteSpace(hk)
        ? hk.ToString()
        : (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? app.Configuration["OpenAI:ApiKey"]);
    if (string.IsNullOrWhiteSpace(apiKey)) return Results.BadRequest(new { error = "OpenAI API key not configured" });
    using var body = await JsonDocument.ParseAsync(req.Body);
    var nl = body.RootElement.TryGetProperty("nlText", out var t) && t.ValueKind==JsonValueKind.String ? t.GetString() : null;
    if (string.IsNullOrWhiteSpace(nl)) return Results.BadRequest(new { error = "nlText required" });
    var sys = "你是审批策略编译器。将自然语言描述编译成一个 JSON 对象，字段包括 strategy(\"sequential\"), steps[], overrides[], assignRules{resolve[], pick(any|all)}。只输出 JSON。";
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    var reqBody = new { model = "gpt-4o-mini", messages = new object[]{ new { role="system", content=sys }, new { role="user", content=nl } }, temperature = 0 };
    var resp = await http.PostAsync("https://api.openai.com/v1/chat/completions", new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json"));
    var text = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode) return Results.StatusCode((int)resp.StatusCode);
    try
    {
        using var parsed = JsonDocument.Parse(text);
        var content = parsed.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
        // 验证为 JSON
        using var _ = JsonDocument.Parse(content);
        return Results.Text(content, "application/json");
    }
    catch { return Results.Text("{}", "application/json"); }
}).RequireAuthorization();

// 手动初始化某对象的第一步审批任务（用于老数据或修改规则后的补建）
app.MapPost("/operations/approvals/init", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    using var body = await JsonDocument.ParseAsync(req.Body);
    var entity = body.RootElement.TryGetProperty("entity", out var e) && e.ValueKind==JsonValueKind.String ? e.GetString() : null;
    var idStr = body.RootElement.TryGetProperty("id", out var i) && i.ValueKind==JsonValueKind.String ? i.GetString() : null;
    if (string.IsNullOrWhiteSpace(entity) || string.IsNullOrWhiteSpace(idStr) || !Guid.TryParse(idStr, out var objectId))
        return Results.BadRequest(new { error = "entity and id required" });

    // 若已存在第1步的 pending 任务，则不重复创建
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

// AI：通用票据/资料图像解析 → 建议实体与 payload（不落库）
app.MapPost("/ai/documents/parse", async (HttpRequest req) =>
{
    var apiKey = req.Headers.TryGetValue("x-openai-key", out var hk) && !string.IsNullOrWhiteSpace(hk)
        ? hk.ToString()
        : (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? app.Configuration["OpenAI:ApiKey"]);
    if (string.IsNullOrWhiteSpace(apiKey)) return Results.BadRequest(new { error = "OpenAI API key not configured" });
    var imgs = new List<string>();
    // 尝试 multipart：Content-Type: multipart/form-data; files[]
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

    // 语言：multipart 情况下没有 root，这里默认 zh；JSON 情况在上面 root 可用时再覆盖
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

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    var contentParts = new List<object>();
    contentParts.Add(new { type = "text", text = userText + " 语言: " + language });
    foreach (var im in imgs)
    {
        // 允许 data:URL 或者纯 base64（默认按 image/jpeg 包装）
        var url = im.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? im : ("data:image/jpeg;base64," + im);
        contentParts.Add(new { type = "image_url", image_url = new { url } });
    }
    var reqBody = new
    {
        model = "gpt-4o-mini",
        messages = new object[]
        {
            new { role = "system", content = sys },
            new { role = "user", content = contentParts.ToArray() }
        },
        temperature = 0
    };
    var resp = await http.PostAsync("https://api.openai.com/v1/chat/completions", new StringContent(System.Text.Json.JsonSerializer.Serialize(reqBody), System.Text.Encoding.UTF8, "application/json"));
    var text = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode) return Results.StatusCode((int)resp.StatusCode);
    try
    {
        using var parsed = JsonDocument.Parse(text);
        var content = parsed.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
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
    // 读取 schema.approval（优先 schema.approval，其次顶层 approval 兼容）与对象 payload
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
    // 对象
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
    // 基础 steps
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
                // 从 payload.jsonPath 取申请人 -> 取部门 -> 在同部门找拥有 MANAGER 角色的用户（找不到时不冒泡，MVP）
                var path = who.TryGetProperty("json", out var jp) ? jp.GetString() : null;
                var empIdText = ReadByPath(payload, path ?? string.Empty);
                string? deptCode = null;
                // 支持以 UUID 或员工编码传入
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
    // 去重
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
    // 取任务列表并按邮箱发送 PDF 通知（简要内容）
    var agent = (cfg["Agent:Base"] ?? Environment.GetEnvironmentVariable("AGENT_BASE") ?? "http://localhost:3030").TrimEnd('/');
    // 使用不走系统代理的 HttpClient，避免本机代理拦截 127.0.0.1/localhost
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

    // 构造 PDF 内容
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
                            try
                            {
                                var bytes = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(enc.GetString()!), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                                var b64 = Convert.ToBase64String(bytes);
                                sealDataUrl = $"data:image/{format};base64,{b64}";
                            }
                            catch { }
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
    // 禁用系统代理，避免本机代理拦截 127.0.0.1/localhost
    var handler = new SocketsHttpHandler { UseProxy = false };
    var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    // 读取申请单
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
    // 员工姓名/邮箱（当申请单未填 toEmail 时回退到员工邮箱）
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
    // 构建 PDF 载荷（根据申请类型选择固定模板）
    string? template = null;
    if (!string.IsNullOrWhiteSpace(type))
    {
        var t = type!.ToLowerInvariant();
        if (t.Contains("resignation") || t.Contains("離職") || t.Contains("退職") || t.Contains("离职"))
            template = "jp_resignation_form";
    }
    // 标题按语言回退
    string resolvedTitle = subject ?? "证明书";
    if (template == "jp_resignation_form")
    {
        resolvedTitle = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? "Certificate of Resignation"
            : (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? "离职证明书" : "退職証明書");
    }
    var basics = await LoadEmployeeBasicsAsync(ds, companyCode, employeeCode);
    // 若 name 为空，用 basics.name 回填
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

// API: 手动为指定的 certificate_request 生成并写回 PDF（pdf.data/filename）
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

// 重新打开证书申请：将 status 退回 pending，并重建首步审批待办
app.MapPost("/operations/certificate_request/{id}/reopen", async (HttpRequest req, Guid id, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    try
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // 退回状态
        await using (var up = conn.CreateCommand())
        {
            up.CommandText = "UPDATE certificate_requests SET payload = jsonb_set(payload,'{status}', to_jsonb('pending'::text), true), updated_at=now() WHERE id=$1 AND company_code=$2";
            up.Parameters.AddWithValue(id); up.Parameters.AddWithValue(cc.ToString());
            var n = await up.ExecuteNonQueryAsync(); if (n==0) { await tx.RollbackAsync(); return Results.NotFound(new { error = "not found" }); }
        }

        // 清理旧待办
        await using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM approval_tasks WHERE company_code=$1 AND entity='certificate_request' AND object_id=$2";
            del.Parameters.AddWithValue(cc.ToString()); del.Parameters.AddWithValue(id);
            await del.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();

        // 重建首步待办并通知
        await InitializeApprovalForObject(app.Configuration, ds, cc.ToString()!, "certificate_request", id);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
}).RequireAuthorization();

// 直接下载指定申请单的 PDF；若不存在则自动生成后返回
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
            // 自动生成并回写（若失败，仍尝试"即时渲染并直传"）
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
                // base64 无效，继续尝试 url 或重生
            }
        }
        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                var http = new HttpClient();
                var resp = await http.GetAsync(url!);
                if (!resp.IsSuccessStatusCode) return Results.Problem($"fetch pdfUrl failed: {(int)resp.StatusCode}", statusCode: 502);
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                var name = string.IsNullOrWhiteSpace(filename) ? (Path.GetFileName(new Uri(url!).AbsolutePath) ?? "certificate.pdf") : filename!;
                return Results.File(bytes, "application/pdf", name);
            }
            catch (Exception e)
            {
                return Results.Problem("pdfUrl fetch error: "+ e.Message, statusCode: 502);
            }
        }

        // 即时渲染并直传（不依赖写回）
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
            // 根据类型选择模板
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
            // 使用不走系统代理的 HttpClient，避免本机代理拦截 127.0.0.1/localhost
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
                // 若配置为 localhost，尝试 127.0.0.1；反之亦然
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

// 生成 PDF 并将可下载信息写回申请单（MVP：将 base64 存入 payload.pdf.data，前端可直接下载；若配置了 Blob，可改为上传并写 pdfUrl）
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

// 操作：银行入金分配：根据 allocations 扣减 open_items 并生成入金凭证（按被清账科目出账）
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

    // 权限：需要 capability "op:bank-collect" 或角色拥有等效能力
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

    // 先锁定并读取被分配的未清项，构建"按科目汇总"的贷方明细
    var crMap = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    decimal total = 0m;
    foreach (var a in allocations)
    {
        var id = a.GetProperty("openItemId").GetGuid();
        var apply = a.GetProperty("applyAmount").GetDecimal();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT account_code, residual_amount FROM open_items WHERE id=$1 AND company_code=$2 FOR UPDATE";
            q.Parameters.AddWithValue(id);
            q.Parameters.AddWithValue(cc.ToString());
            await using var rd = await q.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) { await tx.RollbackAsync(); return Results.BadRequest(new { error = "open item not found" }); }
            var acc = rd.GetString(0);
            var residual = rd.GetDecimal(1);
            if (residual < apply) { await tx.RollbackAsync(); return Results.BadRequest(new { error = "insufficient residual" }); }
            if (!crMap.ContainsKey(acc)) crMap[acc] = 0m;
            crMap[acc] += apply;
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

    // 生成入金凭证（借：银行；贷：按被清账科目汇总）并分配凭证号
    var numbering = await Server.Infrastructure.VoucherNumberingService.NextAsync(ds, cc.ToString()!, postingDateValue.Date);
    var lines = new List<object> { new { lineNo = 1, accountCode = bankAccountCode, drcr = "DR", amount = total } };
    int ln = 2; foreach (var kv in crMap) { lines.Add(new { lineNo = ln++, accountCode = kv.Key, drcr = "CR", amount = kv.Value }); }
    var voucherPayloadNode = new JsonObject
    {
        ["header"] = new JsonObject
        {
            ["companyCode"] = cc.ToString(),
            ["postingDate"] = postingDate,
            ["voucherType"] = "IN",
            ["currency"] = currency,
            ["summary"] = "Bank Receipt"
        },
        ["lines"] = JsonSerializer.SerializeToNode(lines) ?? new JsonArray()
    };
    finance.ApplyVoucherCreateAudit(voucherPayloadNode, user);
    var voucherPayloadJson = voucherPayloadNode.ToJsonString();
    await using (var cmd = conn.CreateCommand())
    {
        // vouchers.voucher_no 为生成列，不能直接插入。通过 jsonb_set 写入 header.voucherNo
        cmd.CommandText = @"INSERT INTO vouchers(company_code, payload)
                            VALUES ($1, jsonb_set($2::jsonb, '{header,voucherNo}', to_jsonb($3::text)))
                            RETURNING to_jsonb(vouchers)";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(voucherPayloadJson);
        cmd.Parameters.AddWithValue(numbering.voucherNo);
        var json = (string?)await cmd.ExecuteScalarAsync();
        if (json is null) { await tx.RollbackAsync(); return Results.Problem("create receipt failed"); }
    }

    await tx.CommitAsync();
    return Results.Ok(new { amount = total, voucherNo = numbering.voucherNo });
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
    decimal total = 0m;
    foreach (var a in allocations)
    {
        var id = a.GetProperty("openItemId").GetGuid();
        var apply = a.GetProperty("applyAmount").GetDecimal();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT account_code, residual_amount FROM open_items WHERE id=$1 AND company_code=$2 FOR UPDATE";
            q.Parameters.AddWithValue(id);
            q.Parameters.AddWithValue(cc.ToString());
            await using var rd = await q.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) { await tx.RollbackAsync(); return Results.BadRequest(new { error = "open item not found" }); }
            var acc = rd.GetString(0);
            var residual = rd.GetDecimal(1);
            if (residual < apply) { await tx.RollbackAsync(); return Results.BadRequest(new { error = "insufficient residual" }); }
            if (!drMap.ContainsKey(acc)) drMap[acc] = 0m;
            drMap[acc] += apply;
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

    var numbering = await Server.Infrastructure.VoucherNumberingService.NextAsync(ds, cc.ToString()!, postingDateValue.Date);
    var linesList = new List<object> { new { lineNo = 1, accountCode = bankAccountCode, drcr = "CR", amount = total } };
    int ln = 2;
    foreach (var kv in drMap)
    {
        linesList.Add(new { lineNo = ln++, accountCode = kv.Key, drcr = "DR", amount = kv.Value });
    }

    var voucherPayloadNode = new JsonObject
    {
        ["header"] = new JsonObject
        {
            ["companyCode"] = cc.ToString(),
            ["postingDate"] = postingDate,
            ["voucherType"] = "OT",
            ["currency"] = currency,
            ["summary"] = "Bank Payment"
        },
        ["lines"] = JsonSerializer.SerializeToNode(linesList) ?? new JsonArray()
    };
    finance.ApplyVoucherCreateAudit(voucherPayloadNode, user);
    var voucherPayloadJson = voucherPayloadNode.ToJsonString();

    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"INSERT INTO vouchers(company_code, payload)
                            VALUES ($1, jsonb_set($2::jsonb, '{header,voucherNo}', to_jsonb($3::text)))
                            RETURNING to_jsonb(vouchers)";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(voucherPayloadJson);
        cmd.Parameters.AddWithValue(numbering.voucherNo);
        var json = (string?)await cmd.ExecuteScalarAsync();
        if (json is null) { await tx.RollbackAsync(); return Results.Problem("create payment failed"); }
    }

    await tx.CommitAsync();
    return Results.Ok(new { amount = total, voucherNo = numbering.voucherNo });
}).RequireAuthorization();

// 操作：部门拖拽调整层级
// 输入：{ departmentId, newParentCode|null, newOrder? }
// 规则：使用部门编码(code)构建 path：parent.path + '/' + code；根节点 path=code
app.MapPost("/operations/department/reparent", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    try
    {
        if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
            return Results.BadRequest(new { error = "Missing x-company-code" });
        using var body = await JsonDocument.ParseAsync(req.Body);
        var root = body.RootElement;
        if (!root.TryGetProperty("departmentId", out var idEl) || idEl.ValueKind!=JsonValueKind.String)
            return Results.BadRequest(new { error = "departmentId required" });
        var depId = Guid.Parse(idEl.GetString()!);
        var newParentCode = root.TryGetProperty("newParentCode", out var p) && p.ValueKind==JsonValueKind.String ? p.GetString() : null;
        var newOrder = root.TryGetProperty("newOrder", out var o) && (o.ValueKind==JsonValueKind.Number) ? o.GetInt32() : (int?)null;

        await using var conn = await ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // 读取目标部门与父部门信息
        string? code; string? oldPath;
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT department_code, payload->>'path' FROM departments WHERE company_code=$1 AND id=$2 FOR UPDATE";
            q.Parameters.AddWithValue(cc.ToString()); q.Parameters.AddWithValue(depId);
            await using var rd = await q.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) { await tx.RollbackAsync(); return Results.NotFound(new { error = "department not found" }); }
            code = rd.IsDBNull(0)? null: rd.GetString(0);
            oldPath = rd.IsDBNull(1)? null: rd.GetString(1);
        }
        if (string.IsNullOrWhiteSpace(code)) { await tx.RollbackAsync(); return Results.BadRequest(new { error = "department code missing" }); }

        string newParentPath = string.Empty;
        if (!string.IsNullOrEmpty(newParentCode))
        {
            await using var qp = conn.CreateCommand();
            qp.CommandText = "SELECT department_code, payload->>'path' FROM departments WHERE company_code=$1 AND department_code=$2 LIMIT 1";
            qp.Parameters.AddWithValue(cc.ToString()); qp.Parameters.AddWithValue(newParentCode!);
            await using var pr = await qp.ExecuteReaderAsync();
            if (!await pr.ReadAsync()) { await tx.RollbackAsync(); return Results.BadRequest(new { error = "parent not found" }); }
            var pCode = pr.IsDBNull(0) ? (string?)null : pr.GetString(0);
            var pPath = pr.IsDBNull(1) ? (string?)null : pr.GetString(1);
            newParentPath = string.IsNullOrWhiteSpace(pPath) ? (pCode ?? newParentCode!) : pPath;
        }
        var newPath = string.IsNullOrEmpty(newParentPath) ? code! : (newParentPath + "/" + code);
        // 容错：若旧 path 为空，则视为 code（避免后续 LIKE/REGEXP 出错）
        if (string.IsNullOrWhiteSpace(oldPath)) oldPath = code;

        // 先写 parentCode 与 path
        await using (var up1 = conn.CreateCommand())
        {
            up1.CommandText = @"
                UPDATE departments
                SET payload = jsonb_set(
                              jsonb_set(payload, '{parentCode}', to_jsonb($3::text), true),
                              '{path}', to_jsonb($4::text), true
                            ),
                    updated_at = now()
                WHERE company_code=$1 AND id=$2";
            up1.Parameters.AddWithValue(cc.ToString());
            up1.Parameters.AddWithValue(depId);
            up1.Parameters.AddWithValue((object?)newParentCode ?? DBNull.Value);
            up1.Parameters.AddWithValue(newPath);
            await up1.ExecuteNonQueryAsync();
        }
        // 如有提供顺序，再单独写入 order
        if (newOrder.HasValue)
        {
            await using var up2 = conn.CreateCommand();
            up2.CommandText = @"UPDATE departments SET payload = jsonb_set(payload, '{order}', to_jsonb($3::int), true), updated_at = now() WHERE company_code=$1 AND id=$2";
            up2.Parameters.AddWithValue(cc.ToString());
            up2.Parameters.AddWithValue(depId);
            up2.Parameters.AddWithValue(newOrder.Value);
            await up2.ExecuteNonQueryAsync();
        }

        // 级联更新子孙 path 前缀
        if (!string.IsNullOrWhiteSpace(oldPath) && oldPath != newPath)
        {
        await using var upChildren = conn.CreateCommand();
        upChildren.CommandText = @"UPDATE departments
                                   SET payload = jsonb_set(payload, '{path}', to_jsonb(regexp_replace(payload->>'path', '^' || $3, $4)))
                                   WHERE company_code=$1 AND (payload->>'path') LIKE $2";
        upChildren.Parameters.AddWithValue(cc.ToString());
        upChildren.Parameters.AddWithValue(oldPath + "/%");
        upChildren.Parameters.AddWithValue(oldPath!);
        upChildren.Parameters.AddWithValue(newPath);
        await upChildren.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return Results.Ok(new { ok = true, path = newPath });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
}).RequireAuthorization();

// 维护：撤销最近一次"银行入金"凭证并重建 open_items（全量）
app.MapPost("/maintenance/bank-collect/undo-latest", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    string? voucherNo = null;
    try
    {
        // 删除最近一张 Bank Receipt 凭证
        await using (var pick = conn.CreateCommand())
        {
            pick.CommandText = "SELECT voucher_no, id FROM vouchers WHERE company_code=$1 AND (payload->'header'->>'summary')='Bank Receipt' ORDER BY created_at DESC LIMIT 1";
            pick.Parameters.AddWithValue(cc.ToString());
            await using var r = await pick.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return Results.BadRequest(new { error = "no receipt voucher found" });
            voucherNo = r.IsDBNull(0) ? null : r.GetString(0);
            var vid = r.GetGuid(1);
            await r.CloseAsync();
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM vouchers WHERE id=$1 AND company_code=$2";
            del.Parameters.AddWithValue(vid); del.Parameters.AddWithValue(cc.ToString());
            await del.ExecuteNonQueryAsync();
        }

        // 重建 open_items（全量）
        // 直接复用上面的维护逻辑：按所有凭证重新生成未清项
        // 选取目标凭证
        var targets = new List<(Guid id, JsonDocument payload)>();
        await using (var pick2 = conn.CreateCommand())
        {
            pick2.CommandText = "SELECT id, payload FROM vouchers WHERE company_code=$1";
            pick2.Parameters.AddWithValue(cc.ToString());
            await using var rr = await pick2.ExecuteReaderAsync();
            while (await rr.ReadAsync())
            {
                var id = rr.GetGuid(0);
                var payloadText = rr.GetFieldValue<string>(1);
                targets.Add((id, JsonDocument.Parse(payloadText)));
            }
        }
        // 清空并重建
        await using (var delAll = conn.CreateCommand())
        { delAll.CommandText = "DELETE FROM open_items WHERE company_code=$1"; delAll.Parameters.AddWithValue(cc.ToString()); await delAll.ExecuteNonQueryAsync(); }
        foreach (var (vid, doc) in targets)
        {
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
                // 仅对 openItem 科目生成投影
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
        return Results.Ok(new { ok = true, deletedVoucherNo = voucherNo });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem(ex.Message);
    }
}).RequireAuthorization();

// 维护：为用户授予 capability（默认 op:bank-collect）。若无合适角色则为公司创建 'OP_BANK' 角色并绑定
app.MapPost("/maintenance/grant-cap", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    var userCtx = Auth.GetUserCtx(req);
    string? employeeCode = root.TryGetProperty("employeeCode", out var e) && e.ValueKind==JsonValueKind.String ? e.GetString() : null;
    var cap = root.TryGetProperty("cap", out var c) && c.ValueKind==JsonValueKind.String ? c.GetString() : "op:bank-collect";
    if (string.IsNullOrWhiteSpace(employeeCode)) return Results.BadRequest(new { error = "employeeCode required" });

    await using var conn = await ds.OpenConnectionAsync();
    // 查用户
    Guid userId;
    await using (var q = conn.CreateCommand())
    {
        q.CommandText = "SELECT id FROM users WHERE company_code=$1 AND employee_code=$2 LIMIT 1";
        q.Parameters.AddWithValue(cc.ToString()); q.Parameters.AddWithValue(employeeCode!);
        var idObj = await q.ExecuteScalarAsync();
        if (idObj is null) return Results.BadRequest(new { error = "user not found" });
        userId = (Guid)idObj;
    }
    // 取/建角色 OP_BANK
    Guid roleId;
    await using (var r = conn.CreateCommand())
    {
        r.CommandText = "SELECT id FROM roles WHERE company_code=$1 AND role_code='OP_BANK' LIMIT 1";
        r.Parameters.AddWithValue(cc.ToString());
        var rid = await r.ExecuteScalarAsync();
        if (rid is Guid g) roleId = g; else {
            await using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO roles(company_code, role_code, role_name) VALUES ($1,'OP_BANK','Bank Ops') RETURNING id";
            ins.Parameters.AddWithValue(cc.ToString());
            roleId = (Guid)(await ins.ExecuteScalarAsync())!;
        }
    }
    // 绑定用户与角色
    await using (var map = conn.CreateCommand())
    {
        map.CommandText = "INSERT INTO user_roles(user_id, role_id) VALUES ($1,$2) ON CONFLICT DO NOTHING";
        map.Parameters.AddWithValue(userId); map.Parameters.AddWithValue(roleId);
        await map.ExecuteNonQueryAsync();
    }
    // 写 capability
    await using (var capCmd = conn.CreateCommand())
    {
        capCmd.CommandText = "INSERT INTO role_caps(role_id, cap) VALUES ($1,$2) ON CONFLICT DO NOTHING";
        capCmd.Parameters.AddWithValue(roleId); capCmd.Parameters.AddWithValue(cap!);
        await capCmd.ExecuteNonQueryAsync();
    }
    return Results.Ok(new { ok = true, employeeCode, cap, role = "OP_BANK" });
}).RequireAuthorization();

// 员工附件上传：multipart/form-data -> Azure Blob -> 写回 employees.payload.attachments
app.MapMethods("/employees/{id}/attachments", new[]{"POST"}, async (HttpContext ctx) =>
{
    var req = ctx.Request; var sp = ctx.RequestServices;
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    var blobs = sp.GetService<BlobServiceClient>();
    if (blobs is null) return Results.StatusCode(501);
    // 兼容两种上传方式：multipart/form-data 与纯二进制流
    string? uploadFileName = null;
    string contentType = req.ContentType ?? "application/octet-stream";
    Stream? uploadStream = null;
    long uploadSize = 0;
    // 允许多次读取 Body 以便回退
    try { req.EnableBuffering(); } catch { }
    if (!string.IsNullOrEmpty(req.ContentType) && req.ContentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var form = await req.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "file required" });
            uploadFileName = file.FileName;
            contentType = string.IsNullOrWhiteSpace(file.ContentType) ? contentType : file.ContentType;
            uploadSize = file.Length;
            uploadStream = file.OpenReadStream();
        }
        catch
        {
            // 回退到原始流保存
            try { req.Body.Position = 0; } catch { }
            uploadFileName = req.Headers["X-File-Name"].ToString();
            if (!string.IsNullOrWhiteSpace(uploadFileName)) { try { uploadFileName = Uri.UnescapeDataString(uploadFileName); } catch { } }
            if (string.IsNullOrWhiteSpace(uploadFileName)) uploadFileName = "upload.bin";
            var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            ms.Position = 0;
            uploadStream = ms;
            uploadSize = ms.Length;
            if (uploadSize == 0) return Results.BadRequest(new { error = "invalid multipart or empty body" });
        }
    }
    else
    {
        // 纯二进制上传：从 Body 读取，文件名从头 X-File-Name 或默认名
        uploadFileName = req.Headers["X-File-Name"].ToString();
        if (!string.IsNullOrWhiteSpace(uploadFileName))
        {
            try { uploadFileName = Uri.UnescapeDataString(uploadFileName); } catch { }
        }
        if (string.IsNullOrWhiteSpace(uploadFileName)) uploadFileName = "upload.bin";
        // 复制到内存流用于获取长度
        var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms);
        ms.Position = 0;
        uploadStream = ms;
        uploadSize = ms.Length;
        if (uploadSize == 0) return Results.BadRequest(new { error = "empty body" });
    }

    var idRaw = ctx.Request.RouteValues["id"]?.ToString() ?? string.Empty;
    Guid empId;
    if (!Guid.TryParse(idRaw, out empId))
    {
        var ds = sp.GetRequiredService<NpgsqlDataSource>();
        await using var connFind = await ds.OpenConnectionAsync();
        await using var cmdFind = connFind.CreateCommand();
        cmdFind.CommandText = @"SELECT id FROM employees WHERE company_code=$1 AND (id::text=$2 OR employee_code=$2 OR payload->>'code'=$2) LIMIT 1";
        cmdFind.Parameters.AddWithValue(cc.ToString());
        cmdFind.Parameters.AddWithValue(idRaw);
        var found = await cmdFind.ExecuteScalarAsync();
        if (found is not Guid g) return Results.NotFound(new { error = "employee not found" });
        empId = g;
    }

    if (uploadStream is null) return Results.BadRequest(new { error = "no upload stream" });
    var cfg = sp.GetRequiredService<IConfiguration>();
    var containerName = cfg.GetSection("AzureStorage")["Container"] ?? "attachments";
    var container = blobs.GetBlobContainerClient(containerName);
    await container.CreateIfNotExistsAsync();
    var safeName = Path.GetFileName(uploadFileName);
    var blobName = $"employees/{cc}/{empId}/{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}_{safeName}";
    var client = container.GetBlobClient(blobName);
    await using (uploadStream)
    {
        await client.UploadAsync(uploadStream, new Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = contentType });
    }

    var item = new
    {
        blobName,
        url = client.Uri.ToString(),
        fileName = safeName,
        size = uploadSize,
        contentType = contentType,
        uploadedAt = DateTime.UtcNow
    };
    var itemArrayJson = "[" + JsonSerializer.Serialize(item) + "]";

    var ds2 = sp.GetRequiredService<NpgsqlDataSource>();
    await using var conn = await ds2.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"UPDATE employees
                        SET payload = jsonb_set(payload, '{attachments}', COALESCE(payload->'attachments','[]'::jsonb) || $3::jsonb, true),
                            updated_at = now()
                        WHERE id=$1 AND company_code=$2
                        RETURNING to_jsonb(employees)";
    cmd.Parameters.AddWithValue(empId);
    cmd.Parameters.AddWithValue(cc.ToString());
    cmd.Parameters.AddWithValue(itemArrayJson);
    var json = (string?)await cmd.ExecuteScalarAsync();
    if (json is null) return Results.NotFound(new { error = "employee not found" });
    return Results.Text(json, "application/json");
}).RequireAuthorization();

// 通用详情：限定 company_code，防止跨租户访问
app.MapGet("/objects/{entity}/{id}", async (HttpRequest req, string entity, Guid id, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    var table = Crud.TableFor(entity);
    var schemaDoc = await SchemasService.GetActiveSchema(ds, entity, req.Headers.TryGetValue("x-company-code", out var h1) ? h1.ToString() : null);
    var user = Auth.GetUserCtx(req);
    if (schemaDoc is not null && !Auth.IsActionAllowed(schemaDoc, "read", user)) return Results.StatusCode(403);
    int argIdx = 3; var extraSql = string.Empty; var authArgs = new List<object?>();
    if (schemaDoc is not null)
    {
        var ex = Auth.BuildAuthScopes(schemaDoc, user, argIdx); extraSql = ex.sql; authArgs = ex.args; argIdx = ex.nextIdx;
    }
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT to_jsonb(t) FROM (SELECT * FROM {table} WHERE id=$1 AND company_code=$2" + extraSql + ") t";
    cmd.Parameters.AddWithValue(id);
    cmd.Parameters.AddWithValue(cc.ToString());
    foreach (var a in authArgs) cmd.Parameters.AddWithValue(a!);
    var json = (string?)await cmd.ExecuteScalarAsync();
    if (json is null) return Results.NotFound(new { error = "not found" });
    return Results.Text(json, "application/json");
}).RequireAuthorization();

// 通用更新：
// - 复用 schema 校验
// - voucher 仍做借贷平衡校验
app.MapPut("/objects/{entity}/{id}", async (HttpRequest req, string entity, Guid id, NpgsqlDataSource ds, FinanceService finance) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    using var body = await JsonDocument.ParseAsync(req.Body);
    if (!body.RootElement.TryGetProperty("payload", out var payload))
        return Results.BadRequest(new { error = "payload required" });
    var schemaDoc = await SchemasService.GetActiveSchema(ds, entity, req.Headers.TryGetValue("x-company-code", out var h2) ? h2.ToString() : null);
    if (schemaDoc is null) return Results.NotFound(new { error = "schema not found" });
    var user = Auth.GetUserCtx(req);
    if (!Auth.IsActionAllowed(schemaDoc, "update", user)) return Results.StatusCode(403);
    var schema = JsonSchema.FromText(schemaDoc.RootElement.GetProperty("schema").GetRawText());
    var result = schema.Evaluate(payload);
    if (!result.IsValid) return Results.BadRequest(new { error = "schema validation failed", details = result.Details });

    if (entity == "voucher")
    {
        var tableV = Crud.TableFor(entity);
        string? existingPayloadJson;
        await using (var connFetch = await ds.OpenConnectionAsync())
        await using (var cmdFetch = connFetch.CreateCommand())
        {
            cmdFetch.CommandText = $"SELECT payload FROM {tableV} WHERE id=$1 AND company_code=$2";
            cmdFetch.Parameters.AddWithValue(id);
            cmdFetch.Parameters.AddWithValue(cc.ToString());
            existingPayloadJson = (string?)await cmdFetch.ExecuteScalarAsync();
        }
        if (existingPayloadJson is null) return Results.NotFound(new { error = "not found" });
        var stampedNode = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject();
        finance.ApplyVoucherUpdateAudit(stampedNode, user);
        await finance.ApplyInvoiceRegistrationAsync(stampedNode);
        var stampedJson = stampedNode.ToJsonString();
        using var stampedDoc = JsonDocument.Parse(stampedJson);
        var stampedRoot = stampedDoc.RootElement;
        try
        {
            await finance.EnsureVoucherUpdateAllowed(cc.ToString()!, existingPayloadJson, stampedRoot);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        // 与创建一致：按 drcr/amount 校验借贷平衡
        decimal dr = 0, cr = 0;
        foreach (var l in stampedRoot.GetProperty("lines").EnumerateArray())
        {
            var kind = l.GetProperty("drcr").GetString();
            decimal amt = 0;
            if (l.TryGetProperty("amount", out var av) && av.ValueKind == JsonValueKind.Number)
            {
                if (!av.TryGetDecimal(out amt)) amt = (decimal)av.GetDouble();
            }
            if (string.Equals(kind, "DR", StringComparison.OrdinalIgnoreCase)) dr += amt; else cr += amt;
        }
        if (dr != cr) return Results.BadRequest(new { error = $"Voucher not balanced: DR={dr} CR={cr}" });

        // 若此凭证已发生清账（open_items 残余小于原始），阻止修改，避免历史不一致
        await using (var chk = ds.CreateCommand())
        {
            await using var connChk = await ds.OpenConnectionAsync();
            await using var cmdChk = connChk.CreateCommand();
            cmdChk.CommandText = "SELECT COUNT(1) FROM open_items WHERE company_code=$1 AND voucher_id=$2 AND residual_amount < original_amount";
            cmdChk.Parameters.AddWithValue(cc.ToString());
            cmdChk.Parameters.AddWithValue(id);
            var cntObj = await cmdChk.ExecuteScalarAsync();
            var cnt = Convert.ToInt64(cntObj ?? 0);
            if (cnt > 0) return Results.BadRequest(new { error = "voucher has cleared items; update is not allowed" });
        }
        // 走专用更新流程：更新凭证 -> 重建 open_items
        await using var connV = await ds.OpenConnectionAsync();
        await using var tx = await connV.BeginTransactionAsync();
        string? updatedJson;
        await using (var cmdUp = connV.CreateCommand())
        {
            cmdUp.CommandText = $"UPDATE {tableV} SET payload=$1::jsonb, updated_at=now() WHERE id=$2 AND company_code=$3 RETURNING to_jsonb({tableV})";
            cmdUp.Parameters.AddWithValue(stampedJson);
            cmdUp.Parameters.AddWithValue(id);
            cmdUp.Parameters.AddWithValue(cc.ToString());
            updatedJson = (string?)await cmdUp.ExecuteScalarAsync();
            if (updatedJson is null) { await tx.RollbackAsync(); return Results.NotFound(new { error = "not found" }); }
        }
        // 删除旧的 open_items
        await using (var del = connV.CreateCommand())
        {
            del.CommandText = "DELETE FROM open_items WHERE company_code=$1 AND voucher_id=$2";
            del.Parameters.AddWithValue(cc.ToString());
            del.Parameters.AddWithValue(id);
            await del.ExecuteNonQueryAsync();
        }
        // 重建 open_items（依据更新后的 payload）
        var posting = stampedRoot.GetProperty("header").GetProperty("postingDate").GetString()!;
        for (int idx = 0; idx < stampedRoot.GetProperty("lines").GetArrayLength(); idx++)
        {
            var line = stampedRoot.GetProperty("lines")[idx];
            var accountCode = line.GetProperty("accountCode").GetString() ?? string.Empty;
            decimal amt = 0m;
            if (line.TryGetProperty("amount", out var am) && am.ValueKind==JsonValueKind.Number)
            {
                if (!am.TryGetDecimal(out amt)) amt = (decimal)am.GetDouble();
            }
            if (amt <= 0) continue;
            // 查询科目 openItem
            bool isOpen = false;
            await using (var q = connV.CreateCommand())
            {
                q.CommandText = "SELECT (payload->>'openItem')::boolean FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1";
                q.Parameters.AddWithValue(cc.ToString());
                q.Parameters.AddWithValue(accountCode);
                var v = await q.ExecuteScalarAsync();
                isOpen = v is bool b && b;
            }
            if (!isOpen) continue;
            string? partnerId = null;
            foreach (var key in new[]{"customerId","vendorId","employeeId"})
            {
                if (line.TryGetProperty(key, out var pv) && pv.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(pv.GetString())) { partnerId = pv.GetString(); break; }
            }
            var currency = stampedRoot.GetProperty("header").TryGetProperty("currency", out var c) && c.ValueKind==JsonValueKind.String ? c.GetString() : "JPY";
            await using (var ins = connV.CreateCommand())
            {
                ins.CommandText = @"INSERT INTO open_items(company_code, voucher_id, voucher_line_no, account_code, partner_id, currency, doc_date, original_amount, residual_amount, refs)
                                    VALUES ($1, $2, $3, $4, $5, $6, $7::date, $8, $8, $9::jsonb)";
                ins.Parameters.AddWithValue(cc.ToString());
                ins.Parameters.AddWithValue(id);
                ins.Parameters.AddWithValue(idx+1);
                ins.Parameters.AddWithValue(accountCode);
                ins.Parameters.AddWithValue((object?)partnerId ?? DBNull.Value);
                ins.Parameters.AddWithValue(currency!);
                ins.Parameters.AddWithValue(posting);
                ins.Parameters.AddWithValue(amt);
                ins.Parameters.AddWithValue(JsonSerializer.Serialize(new { source="voucher", voucherId=id, lineNo=idx+1 }));
                await ins.ExecuteNonQueryAsync();
            }
        }
        await tx.CommitAsync();
        return Results.Text(updatedJson, "application/json");
    }
    else if (entity == "account")
    {
        bool isBank = payload.TryGetProperty("isBank", out var ib) && ib.ValueKind == JsonValueKind.True;
        bool isCash = payload.TryGetProperty("isCash", out var ic) && ic.ValueKind == JsonValueKind.True;
        if (isBank && isCash) return Results.BadRequest(new { error = "A科目不能同时为银行科目与现金科目" });
        if (isBank)
        {
            if (!payload.TryGetProperty("bankInfo", out var bi) || bi.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "银行科目需要维护 bankInfo" });
            string[] reqs = new[] { "bankName", "branchName", "accountType", "accountNo", "holder", "currency" };
            foreach (var f in reqs)
            {
                if (!bi.TryGetProperty(f, out var v) || v.ValueKind == JsonValueKind.Null || (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())))
                    return Results.BadRequest(new { error = $"银行信息缺少必填字段: {f}" });
            }
        }
        if (isCash)
        {
            if (!payload.TryGetProperty("cashCurrency", out var ccEl) || ccEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(ccEl.GetString()))
                return Results.BadRequest(new { error = "现金科目需要设置 cashCurrency" });
        }
    }
    var table = Crud.TableFor(entity);
    var rootAuditEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "account", "bank", "branch", "accounting_period" };
    string updateJson = payload.GetRawText();
    if (rootAuditEntities.Contains(entity))
    {
        updateJson = finance.PrepareRootUpdateJson(payload, user);
    }
    var json = await Crud.UpdateRawJson(ds, table, id, cc.ToString()!, updateJson);
    if (json is null) return Results.NotFound(new { error = "not found" });
    return Results.Text(json, "application/json");
}).RequireAuthorization();

// 通用删除：MVP 物理删除（后续可改软删）
app.MapDelete("/objects/{entity}/{id}", async (HttpRequest req, string entity, Guid id, NpgsqlDataSource ds, FinanceService finance) =>
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
            await finance.EnsureVoucherDeleteAllowed(cc.ToString()!, existingPayloadJson);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
    var n = await Crud.DeleteById(ds, table, id, cc.ToString()!);
    if (n == 0) return Results.NotFound(new { error = "not found" });
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

// 通用搜索 DSL：
// - body.where: [{ field/op/value } 或 { json/op/value }]
// - 仅允许使用 jsonstructures.query.filters 与 sorts 白名单中的字段
// - 自动注入 company_code 条件
app.MapPost("/objects/{entity}/search", async (HttpRequest req, string entity, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    using var body = await JsonDocument.ParseAsync(req.Body);
    var schemaDoc = await SchemasService.GetActiveSchema(ds, entity, cc.ToString());
    if (schemaDoc is null) return Results.NotFound(new { error = "schema not found" });
    var filters = schemaDoc.RootElement.TryGetProperty("query", out var q) && q.TryGetProperty("filters", out var f) && f.ValueKind == JsonValueKind.Array ? f.EnumerateArray().Select(x => x.GetString()!).ToHashSet() : new HashSet<string>();
    // 补充安全白名单：账户科目允许按 isBank / isCash 进行 JSON 过滤（后端字段：payload.isBank / payload.isCash）
    if (string.Equals(entity, "account", StringComparison.OrdinalIgnoreCase))
    {
        filters.Add("isBank");
        filters.Add("isCash");
        filters.Add("isbank");
        filters.Add("iscash");
        filters.Add("bankCashType");
    }
    // 补充安全白名单：员工允许按编码、主部门、姓名（汉字/假名）过滤
    if (string.Equals(entity, "employee", StringComparison.OrdinalIgnoreCase))
    {
        filters.Add("employee_code");
        filters.Add("primary_department_id");
        filters.Add("nameKanji"); // JSON 路径（payload.nameKanji）
        filters.Add("nameKana");  // JSON 路径（payload.nameKana）
    }
    if (string.Equals(entity, "payroll_policy", StringComparison.OrdinalIgnoreCase))
    {
        // 允许按 JSON 路径 isActive 过滤
        filters.Add("isActive");
    }
    if (string.Equals(entity, "voucher", StringComparison.OrdinalIgnoreCase))
    {
        filters.Add("header.summary");
    }
    var sorts = schemaDoc.RootElement.TryGetProperty("query", out var q2) && q2.TryGetProperty("sorts", out var srt) && srt.ValueKind == JsonValueKind.Array ? srt.EnumerateArray().Select(x => x.GetString()!).ToHashSet() : new HashSet<string>();
    if (string.Equals(entity, "employee", StringComparison.OrdinalIgnoreCase))
    {
        sorts.Add("employee_code");
        sorts.Add("created_at");
    }
    if (string.Equals(entity, "payroll_policy", StringComparison.OrdinalIgnoreCase))
    {
        sorts.Add("created_at");
    }
    if (string.Equals(entity, "employment_type", StringComparison.OrdinalIgnoreCase))
    {
        sorts.Add("created_at");
    }
    var user = Auth.GetUserCtx(req);
    if (!Auth.IsActionAllowed(schemaDoc, "read", user)) return Results.StatusCode(403);

    // 页码/页大小：使用 Math.Max/Math.Min 防越界
    var page = body.RootElement.TryGetProperty("page", out var p) ? Math.Max(1, p.GetInt32()) : 1;
    var noPaging = body.RootElement.TryGetProperty("noPaging", out var npEl) && (npEl.ValueKind == JsonValueKind.True || (npEl.ValueKind == JsonValueKind.String && bool.TryParse(npEl.GetString(), out var npBool) && npBool));
    var pageSize = body.RootElement.TryGetProperty("pageSize", out var ps) && !noPaging
        ? Math.Min(1000, Math.Max(1, ps.GetInt32()))
        : 50;
    var where = body.RootElement.TryGetProperty("where", out var w) && w.ValueKind == JsonValueKind.Array ? w.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
    var orderBy = body.RootElement.TryGetProperty("orderBy", out var ob) && ob.ValueKind == JsonValueKind.Array ? ob.EnumerateArray().ToArray() : Array.Empty<JsonElement>();

    var table = Crud.TableFor(entity);
    // 构建 where 片段，便于同时用于数据查询与总数统计
    var whereSql = $" WHERE company_code = $1";
    var args = new List<object?> { cc.ToString() };
    var argIdx = 2;
    var extraClauses = new List<string>();

    string? BuildClause(JsonElement cond)
    {
        if (cond.ValueKind != JsonValueKind.Object) return null;

        if (cond.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array)
        {
            var orClauses = new List<string>();
            foreach (var sub in anyOf.EnumerateArray())
            {
                var subClause = BuildClause(sub);
                if (!string.IsNullOrWhiteSpace(subClause))
                {
                    orClauses.Add("(" + subClause + ")");
                }
            }
            if (orClauses.Count == 0) return null;
            return string.Join(" OR ", orClauses);
        }

        if (cond.TryGetProperty("field", out var fld) && fld.ValueKind == JsonValueKind.String)
        {
            var field = fld.GetString()!;
            if (!filters.Contains(field)) return null;
            var op = cond.TryGetProperty("op", out var opEl) ? opEl.GetString() ?? "eq" : "eq";

            if (op == "between" && cond.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.Array && val.GetArrayLength() == 2)
            {
                object v0; object v1;
                var a0 = val[0]; var a1 = val[1];
                if (a0.ValueKind == JsonValueKind.Number) { v0 = a0.TryGetDecimal(out var d0) ? d0 : (object)a0.GetDouble(); }
                else if (a0.ValueKind == JsonValueKind.String && decimal.TryParse(a0.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pd0)) { v0 = pd0; }
                else { v0 = a0.ToString(); }
                if (a1.ValueKind == JsonValueKind.Number) { v1 = a1.TryGetDecimal(out var d1) ? d1 : (object)a1.GetDouble(); }
                else if (a1.ValueKind == JsonValueKind.String && decimal.TryParse(a1.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pd1)) { v1 = pd1; }
                else { v1 = a1.ToString(); }
                args.Add(v0); args.Add(v1);
                var clause = $"{field} BETWEEN ${argIdx} AND ${argIdx + 1}";
                argIdx += 2;
                return clause;
            }
            if (op == "eq" && cond.TryGetProperty("value", out var val2))
            {
                object? v = val2.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => val2.TryGetInt64(out var i64) ? i64 : (val2.TryGetDecimal(out var dec) ? dec : val2.GetDouble()),
                    JsonValueKind.Null => DBNull.Value,
                    _ => (object?)val2.ToString()
                };
                args.Add(v!);
                var clause = $"{field} = ${argIdx}";
                argIdx++;
                return clause;
            }
            if (op == "contains" && cond.TryGetProperty("value", out var valx))
            {
                args.Add("%" + valx.ToString() + "%");
                var clause = $"{field} ILIKE ${argIdx}";
                argIdx++;
                return clause;
            }
            if (op == "in" && cond.TryGetProperty("value", out var val3) && val3.ValueKind == JsonValueKind.Array)
            {
                var n = val3.GetArrayLength();
                if (n == 0) return null;
                var placeholders = new List<string>();
                foreach (var item in val3.EnumerateArray())
                {
                    placeholders.Add("$" + argIdx);
                    if (item.ValueKind == JsonValueKind.True) args.Add(true);
                    else if (item.ValueKind == JsonValueKind.False) args.Add(false);
                    else if (item.ValueKind == JsonValueKind.Number)
                    {
                        if (item.TryGetInt64(out var i64)) args.Add(i64);
                        else if (item.TryGetDecimal(out var dec)) args.Add(dec);
                        else args.Add(item.GetDouble());
                    }
                    else if (item.ValueKind == JsonValueKind.Null) args.Add(DBNull.Value);
                    else args.Add(item.ToString());
                    argIdx++;
                }
                return $"{field} IN (" + string.Join(",", placeholders) + ")";
            }
            return null;
        }

        if (cond.TryGetProperty("json", out var js) && js.ValueKind == JsonValueKind.String)
        {
            var jsonPath = js.GetString()!;
            var allowBypass = string.Equals(entity, "account", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(jsonPath, "isBank", StringComparison.OrdinalIgnoreCase) || string.Equals(jsonPath, "isCash", StringComparison.OrdinalIgnoreCase));
            if (!filters.Contains(jsonPath) && !allowBypass) return null;
            var op = cond.TryGetProperty("op", out var opEl) ? opEl.GetString() ?? "eq" : "eq";

            if (op == "eq" && cond.TryGetProperty("value", out var val4))
            {
                if (val4.ValueKind == JsonValueKind.True || val4.ValueKind == JsonValueKind.False)
                {
                    var boolVal = val4.ValueKind == JsonValueKind.True;
                    var key = jsonPath.Replace("[]", string.Empty);
                    if (string.Equals(entity, "account", StringComparison.OrdinalIgnoreCase) && (key == "isBank" || key == "isCash"))
                    {
                        args.Add(boolVal);
                        var clause = $"(payload->>'{key}')::boolean = ${argIdx}";
                        argIdx += 1;
                        return clause;
                    }
                    args.Add(key);
                    args.Add(boolVal);
                    var clauseBool = $"(payload #>> string_to_array(${argIdx}, '.'))::boolean = ${argIdx + 1}";
                    argIdx += 2;
                    return clauseBool;
                }

                args.Add(jsonPath.Replace("[]", string.Empty));
                args.Add(val4.ToString());
                var clauseEq = $"payload #>> string_to_array(${argIdx}, '.') = ${argIdx + 1}";
                argIdx += 2;
                return clauseEq;
            }

            if (op == "contains" && cond.TryGetProperty("value", out var val5))
            {
                args.Add(jsonPath.Replace("[]", string.Empty));
                args.Add("%" + val5.ToString() + "%");
                var clauseContains = $"(payload #>> string_to_array(${argIdx}, '.')) ILIKE ${argIdx + 1}";
                argIdx += 2;
                return clauseContains;
            }

            if (op == "in" && cond.TryGetProperty("value", out var valIn) && valIn.ValueKind == JsonValueKind.Array)
            {
                var n = valIn.GetArrayLength();
                if (n == 0) return null;
                var partsLocal = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    partsLocal.Add($"payload #>> string_to_array(${argIdx}, '.') = ${argIdx + 1}");
                    args.Add(jsonPath.Replace("[]", string.Empty));
                    args.Add(valIn[i].ToString());
                    argIdx += 2;
                }
                return "(" + string.Join(" OR ", partsLocal) + ")";
            }

            return null;
        }

        return null;
    }

    foreach (var cond in where)
    {
        var clause = BuildClause(cond);
        if (!string.IsNullOrWhiteSpace(clause))
        {
            extraClauses.Add(clause);
        }
    }
    foreach (var clause in extraClauses)
    {
        whereSql += " AND (" + clause + ")";
    }

    var dataSql = $"SELECT to_jsonb(t) FROM (SELECT * FROM {table}{whereSql}) t";
    if (orderBy.Length > 0)
    {
        var parts = new List<string>();
        foreach (var o in orderBy)
        {
            if (o.TryGetProperty("field", out var fld) && fld.ValueKind == JsonValueKind.String && sorts.Contains(fld.GetString()!))
            {
                var dir = o.TryGetProperty("dir", out var d) && d.GetString()?.ToUpperInvariant() == "DESC" ? "DESC" : "ASC";
                parts.Add($"{fld.GetString()} {dir}");
            }
        }
        if (parts.Count > 0) dataSql += " ORDER BY " + string.Join(",", parts);
    }
    else
    {
        // 默认排序：HR 三实体按创建时间倒序，便于取最近/生效版本
        if (string.Equals(entity, "payroll_policy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity, "employment_type", StringComparison.OrdinalIgnoreCase))
        {
            dataSql += " ORDER BY created_at DESC";
        }
    }
    if (!noPaging)
    {
        dataSql += $" LIMIT {pageSize} OFFSET {(page - 1) * pageSize}";
    }

    // 统计总数（不含分页与排序）
    var countSql = $"SELECT count(1) FROM {table}{whereSql}";
    long total;
    await using (var conn = await ds.OpenConnectionAsync())
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = countSql;
        for (int i = 0; i < args.Count; i++) cmd.Parameters.AddWithValue(args[i]!);
        var obj = await cmd.ExecuteScalarAsync();
        total = obj is long l ? l : Convert.ToInt64(obj);
    }

    var rows = await Crud.QueryJsonRows(ds, dataSql, args);
    var jsonArray = "[" + string.Join(',', rows) + "]";
    if (noPaging)
    {
        page = 1;
        pageSize = rows.Count;
    }
    return Results.Text(JsonSerializer.Serialize(new { data = JsonDocument.Parse(jsonArray).RootElement, page, pageSize, total }), "application/json");
}).RequireAuthorization();

// 角色与能力维护（需要 cap:roles:manage）
app.MapPost("/admin/roles", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    var code = root.GetProperty("roleCode").GetString()!;
    var name = root.TryGetProperty("roleName", out var rn) && rn.ValueKind==JsonValueKind.String ? rn.GetString() : null;
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
    var user = Auth.GetUserCtx(req);
    if (!(user.Caps?.Contains("roles:manage") == true)) return Results.StatusCode(403);
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO roles(company_code, role_code, role_name) VALUES ($1,$2,$3) ON CONFLICT (company_code, role_code) DO UPDATE SET role_name=EXCLUDED.role_name";
    cmd.Parameters.AddWithValue(cc.ToString());
    cmd.Parameters.AddWithValue(code);
    cmd.Parameters.AddWithValue((object?)name ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapDelete("/admin/roles/{roleCode}", async (HttpRequest req, string roleCode, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
    var user = Auth.GetUserCtx(req);
    if (!(user.Caps?.Contains("roles:manage") == true)) return Results.StatusCode(403);
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM roles WHERE company_code=$1 AND role_code=$2";
    cmd.Parameters.AddWithValue(cc.ToString());
    cmd.Parameters.AddWithValue(roleCode);
    await cmd.ExecuteNonQueryAsync();
    return Results.Ok(new { ok = true });
}).AllowAnonymous();

// 一键种子：创建与工资记账相关的会计科目主数据（按公司）
app.MapPost("/admin/accounts/seed/payroll", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    async Task Upsert(string code, string name, string category, object extra)
    {
        var payload = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["name"] = name,
            ["category"] = category
        };
        // 合并额外字段
        foreach (var kv in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(System.Text.Json.JsonSerializer.Serialize(extra))!)
            payload[kv.Key] = kv.Value;
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO accounts(company_code, payload) VALUES ($1, $2::jsonb)
                             ON CONFLICT (company_code, account_code) DO NOTHING";
        cmd.Parameters.AddWithValue(cc.ToString());
        cmd.Parameters.AddWithValue(json);
        await cmd.ExecuteNonQueryAsync();
    }

    // 资产类
    await Upsert("1000", "現金", "BS", new { isCash = true, cashCurrency = "JPY" });
    await Upsert("1010", "普通預金", "BS", new { isBank = true, bankInfo = new { bankName = "みずほ銀行", branchName = "本店", accountType = "普通", accountNo = "0000000", holder = "サンプル株式会社", currency = "JPY" } });

    // 费用类（PL）
    await Upsert("6400", "給与手当", "PL", new { openItem = false });
    await Upsert("6410", "賞与", "PL", new { openItem = false });

    // 负债类（预提/預り金等）
    await Upsert("2200", "未払費用", "BS", new { openItem = false });
    await Upsert("2210", "社会保険預り金", "BS", new { openItem = false });
    await Upsert("2211", "厚生年金預り金", "BS", new { openItem = false });
    await Upsert("2212", "雇用保険預り金", "BS", new { openItem = false });
    await Upsert("2220", "源泉所得税預り金", "BS", new { openItem = false });

    await tx.CommitAsync();
    return Results.Ok(new { ok = true, seeded = new[]{ "1000","1010","6400","6410","2200","2210","2211","2212","2220" } });
}).RequireAuthorization();

app.MapPost("/admin/users/{employeeCode}/roles", async (HttpRequest req, string employeeCode, NpgsqlDataSource ds) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var roleCode = doc.RootElement.GetProperty("roleCode").GetString()!;
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
    var user = Auth.GetUserCtx(req);
    if (!(user.Caps?.Contains("roles:manage") == true)) return Results.StatusCode(403);
    await using var conn = await ds.OpenConnectionAsync();
    // 查 user/role id
    Guid? uid=null, rid=null;
    await using (var q = conn.CreateCommand())
    { q.CommandText = "SELECT id FROM users WHERE company_code=$1 AND employee_code=$2"; q.Parameters.AddWithValue(cc.ToString()); q.Parameters.AddWithValue(employeeCode); uid = (Guid?)await q.ExecuteScalarAsync(); }
    await using (var q2 = conn.CreateCommand())
    { q2.CommandText = "SELECT id FROM roles WHERE company_code=$1 AND role_code=$2"; q2.Parameters.AddWithValue(cc.ToString()); q2.Parameters.AddWithValue(roleCode); rid = (Guid?)await q2.ExecuteScalarAsync(); }
    if (uid is null || rid is null) return Results.NotFound(new { error = "user or role not found" });
    await using var ins = conn.CreateCommand();
    ins.CommandText = "INSERT INTO user_roles(user_id, role_id) VALUES ($1,$2) ON CONFLICT DO NOTHING";
    ins.Parameters.AddWithValue(uid.Value); ins.Parameters.AddWithValue(rid.Value);
    await ins.ExecuteNonQueryAsync();
    return Results.Ok(new { ok = true });
}).AllowAnonymous();

app.MapDelete("/admin/users/{employeeCode}/roles/{roleCode}", async (HttpRequest req, string employeeCode, string roleCode, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
    var user = Auth.GetUserCtx(req);
    if (!(user.Caps?.Contains("roles:manage") == true)) return Results.StatusCode(403);
    await using var conn = await ds.OpenConnectionAsync();
    Guid? uid=null, rid=null;
    await using (var q = conn.CreateCommand())
    { q.CommandText = "SELECT id FROM users WHERE company_code=$1 AND employee_code=$2"; q.Parameters.AddWithValue(cc.ToString()); q.Parameters.AddWithValue(employeeCode); uid = (Guid?)await q.ExecuteScalarAsync(); }
    await using (var q2 = conn.CreateCommand())
    { q2.CommandText = "SELECT id FROM roles WHERE company_code=$1 AND role_code=$2"; q2.Parameters.AddWithValue(cc.ToString()); q2.Parameters.AddWithValue(roleCode); rid = (Guid?)await q2.ExecuteScalarAsync(); }
    if (uid is null || rid is null) return Results.NotFound(new { error = "user or role not found" });
    await using var del = conn.CreateCommand();
    del.CommandText = "DELETE FROM user_roles WHERE user_id=$1 AND role_id=$2";
    del.Parameters.AddWithValue(uid.Value); del.Parameters.AddWithValue(rid.Value);
    await del.ExecuteNonQueryAsync();
    return Results.Ok(new { ok = true });
}).AllowAnonymous();

app.MapPost("/admin/roles/{roleCode}/caps", async (HttpRequest req, string roleCode, NpgsqlDataSource ds) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var cap = doc.RootElement.GetProperty("cap").GetString()!;
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
    var user = Auth.GetUserCtx(req);
    if (!(user.Caps?.Contains("roles:manage", StringComparer.OrdinalIgnoreCase) == true)) return Results.StatusCode(403);
    await using var conn = await ds.OpenConnectionAsync();
    Guid? rid=null;
    await using (var q = conn.CreateCommand()) { q.CommandText = "SELECT id FROM roles WHERE company_code=$1 AND role_code=$2"; q.Parameters.AddWithValue(cc.ToString()); q.Parameters.AddWithValue(roleCode); rid = (Guid?)await q.ExecuteScalarAsync(); }
    if (rid is null) return Results.NotFound(new { error = "role not found" });
    await using var ins = conn.CreateCommand(); ins.CommandText = "INSERT INTO role_caps(role_id, cap) VALUES ($1,$2) ON CONFLICT DO NOTHING"; ins.Parameters.AddWithValue(rid.Value); ins.Parameters.AddWithValue(cap); await ins.ExecuteNonQueryAsync();
    return Results.Ok(new { ok = true });
}).AllowAnonymous();

app.MapDelete("/admin/roles/{roleCode}/caps/{cap}", async (HttpRequest req, string roleCode, string cap, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
    var user = Auth.GetUserCtx(req);
    if (!(user.Caps?.Contains("roles:manage", StringComparer.OrdinalIgnoreCase) == true)) return Results.StatusCode(403);
    await using var conn = await ds.OpenConnectionAsync();
    Guid? rid=null;
    await using (var q = conn.CreateCommand()) { q.CommandText = "SELECT id FROM roles WHERE company_code=$1 AND role_code=$2"; q.Parameters.AddWithValue(cc.ToString()); q.Parameters.AddWithValue(roleCode); rid = (Guid?)await q.ExecuteScalarAsync(); }
    if (rid is null) return Results.NotFound(new { error = "role not found" });
    await using var del = conn.CreateCommand(); del.CommandText = "DELETE FROM role_caps WHERE role_id=$1 AND cap=$2"; del.Parameters.AddWithValue(rid.Value); del.Parameters.AddWithValue(cap); await del.ExecuteNonQueryAsync();
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

// AI：Timesheet 审核建议（最小可用规则集）
app.MapPost("/ai/timesheet/review", async (HttpRequest req) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    var entries = root.TryGetProperty("entries", out var arr) && arr.ValueKind==JsonValueKind.Array ? arr.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
    var month = root.TryGetProperty("month", out var m) && m.ValueKind==JsonValueKind.String ? m.GetString() : null;
    var issues = new List<object>();
    decimal total = 0m;
    foreach (var e in entries)
    {
        var dateStr = e.TryGetProperty("date", out var d) && d.ValueKind==JsonValueKind.String ? d.GetString() : null;
        var hours = e.TryGetProperty("hours", out var h) && (h.ValueKind==JsonValueKind.Number) ? h.GetDecimal() : 0m;
        var note = e.TryGetProperty("note", out var n) && n.ValueKind==JsonValueKind.String ? n.GetString() : string.Empty;
        total += hours;
        if (hours <= 0) issues.Add(new { level="error", date=dateStr, code="HOURS_ZERO", message="工时为0或缺失" });
        if (hours > 12) issues.Add(new { level="warn", date=dateStr, code="HOURS_LONG", message="单日工时>12，请确认是否加班" });
        if (!string.IsNullOrEmpty(note) && note!.Length < 3 && hours >= 8)
            issues.Add(new { level="info", date=dateStr, code="NOTE_SHORT", message="长时段建议补充备注" });
    }
    var decision = issues.Any(i => (string)i.GetType().GetProperty("level")!.GetValue(i)! == "error") ? "reject"
                 : issues.Any(i => (string)i.GetType().GetProperty("level")!.GetValue(i)! == "warn") ? "needs_info"
                 : "approve";
    return Results.Ok(new { month, totalHours = total, decision, issues });
}).RequireAuthorization();

// 读取/保存公司设置（例如工作日默认上/下班时间）
app.MapGet("/company/settings", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT payload FROM company_settings WHERE company_code=$1 LIMIT 1";
    cmd.Parameters.AddWithValue(cc.ToString());
    var txt = (string?)await cmd.ExecuteScalarAsync();
    if (string.IsNullOrWhiteSpace(txt)) return Results.Ok(new { payload = new { workdayDefaultStart = "09:00", workdayDefaultEnd = "18:00", lunchMinutes = 60 } });
    return Results.Text(txt!, "application/json");
}).RequireAuthorization();

app.MapPost("/company/settings", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var payload = doc.RootElement.GetRawText();
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"INSERT INTO company_settings(company_code, payload) VALUES ($1, $2::jsonb)
                        ON CONFLICT (company_code) DO UPDATE SET payload=$2::jsonb, updated_at=now()
                        RETURNING payload";
    cmd.Parameters.AddWithValue(cc.ToString());
    cmd.Parameters.AddWithValue(payload);
    var txt = (string?)await cmd.ExecuteScalarAsync();
    return Results.Text(txt ?? payload, "application/json");
}).RequireAuthorization();

// AI：审批编译（自然语言→步骤/层级）最小骨架
app.MapPost("/ai/approval/compile", async (HttpRequest req) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    var text = root.TryGetProperty("nlText", out var t) && t.ValueKind==JsonValueKind.String ? t.GetString() : "";
    // 规则（示例/占位）：包含"两层/2层/二级"→两层；包含"经理/主管"→第1层；包含"财务/HR/人事/总务"→第2层
    var steps = new List<object>();
    int layers = (text?.Contains("两层")==true || text?.Contains("2层")==true || text?.Contains("二级")==true) ? 2 : 1;
    steps.Add(new { stepNo=1, name="经理审批", role="manager" });
    if (layers>=2) steps.Add(new { stepNo=2, name="HR/财务审批", role="hr_manager" });
    return Results.Ok(new { steps });
}).RequireAuthorization();

// 提交流程：根据公司级审批策略为 timesheet 生成待办
app.MapPost("/operations/timesheet/submit", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
        return Results.BadRequest(new { error = "Missing x-company-code" });
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    if (!root.TryGetProperty("timesheetId", out var idEl) || idEl.ValueKind!=JsonValueKind.String)
        return Results.BadRequest(new { error = "timesheetId required" });
    var tsId = Guid.Parse(idEl.GetString()!);

    // 读取公司审批策略（此处可从 schemas 或配置表获取；先用占位steps）
    var steps = new[]{ new { stepNo=1, role="manager", name="经理" }, new { stepNo=2, role="hr_manager", name="HR" } };

    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    // 更新 timesheet 状态为 submitted
    await using (var u = conn.CreateCommand())
    {
        u.CommandText = "UPDATE timesheets SET payload = jsonb_set(payload, '{status}', to_jsonb('submitted'::text), true), updated_at = now() WHERE id=$1 AND company_code=$2";
        u.Parameters.AddWithValue(tsId); u.Parameters.AddWithValue(cc.ToString());
        var n = await u.ExecuteNonQueryAsync(); if (n==0) { await tx.RollbackAsync(); return Results.NotFound(new { error = "timesheet not found" }); }
    }

    // 解析申请人部门/经理（此处示例从 users 或 employees 取，简化：将审批人设为提交者的经理角色）
    var submitter = Auth.GetUserCtx(req);
    foreach (var s in steps)
    {
        await using var ins = conn.CreateCommand();
        ins.CommandText = @"INSERT INTO approval_tasks(company_code, entity, object_id, step_no, step_name, approver_user_id, status)
                            VALUES ($1,$2,$3,$4,$5,$6,'pending')";
        ins.Parameters.AddWithValue(cc.ToString());
        ins.Parameters.AddWithValue("timesheet");
        ins.Parameters.AddWithValue(tsId);
        ins.Parameters.AddWithValue(s.stepNo);
        ins.Parameters.AddWithValue(s.name);
        // 简化：先写入空 approver，由前端或后端根据角色映射人选；可扩展为查找组织关系
        ins.Parameters.AddWithValue( submitter.UserId ?? string.Empty );
        await ins.ExecuteNonQueryAsync();
    }
    await tx.CommitAsync();
    return Results.Ok(new { ok = true, steps = steps.Select(x=> new { x.stepNo, x.name }) });
}).RequireAuthorization();

// 附件读取 SAS：传入 blobName，返回 5 分钟有效的只读 SAS URL（未注册存储则返回 501）
app.MapPost("/attachments/sas", (HttpRequest req, IServiceProvider sp) =>
{
    var blobs = sp.GetService<BlobServiceClient>();
    if (blobs is null) return Results.StatusCode(501);
    using var doc = JsonDocument.Parse(req.Body);
    if (!doc.RootElement.TryGetProperty("blobName", out var b) || b.ValueKind != JsonValueKind.String)
        return Results.BadRequest(new { error = "blobName required" });
    var cfg = builder.Configuration.GetSection("AzureStorage");
    var container = cfg["Container"]!;
    var blobName = b.GetString()!;
    var client = blobs.GetBlobContainerClient(container).GetBlobClient(blobName);
    var sasBuilder = new BlobSasBuilder
    {
        BlobContainerName = container,
        BlobName = blobName,
        Resource = "b",
        ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(5)
    };
    sasBuilder.SetPermissions(BlobSasPermissions.Read);
    var uri = client.GenerateSasUri(sasBuilder);
    return Results.Ok(new { url = uri.ToString() });
});

app.Run();



// 顶部声明的 UserCtx 类型移动到文件末尾，避免 CS8803
record UserCtx(string? UserId, string[] Roles, string? DeptId, string? EmployeeCode = null, string? UserName = null, string? CompanyCode = null);