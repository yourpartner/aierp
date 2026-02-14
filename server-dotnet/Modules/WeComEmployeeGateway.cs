using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Infrastructure;
using Server.Infrastructure.Skills;

namespace Server.Modules;

/// <summary>
/// 企业微信员工 AI Gateway
/// 
/// 核心职责：
/// 1. 接收企业微信内部员工消息（通过自建应用回调）
/// 2. 意图分类（规则引擎 + LLM）
/// 3. 多轮对话管理（会话状态维护）
/// 4. 意图路由 → 调用系统 API 完成操作
/// 5. 生成自然语言回复 → 通过企业微信发送
/// </summary>
public class WeComEmployeeGateway
{
    private readonly ILogger<WeComEmployeeGateway> _logger;
    private readonly NpgsqlDataSource _ds;
    private readonly IConfiguration _config;
    private readonly WeComIntentClassifier _intentClassifier;
    private readonly WeComNotificationService _wecomService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimesheetAiParser _timesheetParser;
    private readonly IServiceProvider _serviceProvider;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public WeComEmployeeGateway(
        ILogger<WeComEmployeeGateway> logger,
        NpgsqlDataSource ds,
        IConfiguration config,
        WeComIntentClassifier intentClassifier,
        WeComNotificationService wecomService,
        IHttpClientFactory httpClientFactory,
        TimesheetAiParser timesheetParser,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _ds = ds;
        _config = config;
        _intentClassifier = intentClassifier;
        _wecomService = wecomService;
        _httpClientFactory = httpClientFactory;
        _timesheetParser = timesheetParser;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// 处理来自企业微信内部员工的消息（主入口）
    /// </summary>
    public async Task<EmployeeGatewayResponse> HandleEmployeeMessageAsync(
        string companyCode, WeComMessage message, CancellationToken ct)
    {
        var channelUserId = message.FromUser;
        _logger.LogInformation("[EmployeeGW] 收到员工消息: user={User}, type={Type}, content={Content}",
            channelUserId, message.MsgType, message.Content?.Length > 50 ? message.Content[..50] + "..." : message.Content);

        try
        {
            // 1. 身份解析 → 查绑定表 + 加载权限
            var session = await GetOrCreateSessionAsync(companyCode, channelUserId, ct);

            // 2. 未绑定 → 进入绑定引导流程
            if (!session.IsBound)
            {
                var bindReply = await HandleBindingFlowAsync(companyCode, session, message, ct);
                if (_wecomService.IsConfigured)
                    await _wecomService.SendTextMessageAsync(bindReply, channelUserId, ct);
                return new EmployeeGatewayResponse("binding", bindReply, session.Id);
            }

            // 3. 保存入站消息（密码消息不保存）
            await SaveMessageAsync(session.Id, companyCode, channelUserId, "in", message.MsgType,
                message.Content, null, null, ct);

            // 4. 意图分类
            var intent = await _intentClassifier.ClassifyAsync(
                message.Content ?? "", message.MsgType, session.CurrentIntent, ct);

            _logger.LogInformation("[EmployeeGW] 意图分类: intent={Intent}, confidence={Confidence:F2}",
                intent.Intent, intent.Confidence);

            // 5. 权限守卫 → 检查用户是否有执行该意图的能力
            var permissionCheck = CheckPermission(session, intent.Intent);
            if (!permissionCheck.Allowed)
            {
                var denyReply = permissionCheck.Message;
                await SaveMessageAsync(session.Id, companyCode, channelUserId, "out", "text",
                    denyReply, "permission_denied", null, ct);
                if (_wecomService.IsConfigured)
                    await _wecomService.SendTextMessageAsync(denyReply, channelUserId, ct);
                return new EmployeeGatewayResponse("permission_denied", denyReply, session.Id);
            }

            // 6. 路由到对应处理器
            var reply = await RouteIntentAsync(companyCode, session, message, intent, ct);

            // 空回复 → 静默处理（如批次聚合中的非首张图片）
            if (string.IsNullOrEmpty(reply))
            {
                return new EmployeeGatewayResponse(intent.Intent, "", session.Id);
            }

            // 7. 保存出站消息
            await SaveMessageAsync(session.Id, companyCode, channelUserId, "out", "text",
                reply, intent.Intent, null, ct);

            // 8. 更新会话状态
            await UpdateSessionAsync(session, intent, ct);

            // 9. 发送回复
            if (_wecomService.IsConfigured)
            {
                await _wecomService.SendTextMessageAsync(reply, channelUserId, ct);
            }

            return new EmployeeGatewayResponse(intent.Intent, reply, session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EmployeeGW] 处理消息失败: user={User}", channelUserId);
            var errorReply = "抱歉，系统暂时出现了问题，请稍后再试。如有紧急事项，请联系管理员。";
            if (_wecomService.IsConfigured)
            {
                try { await _wecomService.SendTextMessageAsync(errorReply, channelUserId, ct); } catch { /* 忽略 */ }
            }
            return new EmployeeGatewayResponse("error", errorReply, null);
        }
    }

    // ==================== 绑定引导流程 ====================

    /// <summary>
    /// 处理未绑定用户的自助绑定流程
    /// 状态机：null → awaiting_employee_code → awaiting_password → bound
    /// </summary>
    private async Task<string> HandleBindingFlowAsync(
        string companyCode, EmployeeSession session, WeComMessage message, CancellationToken ct)
    {
        var text = (message.Content ?? "").Trim();
        var bindState = session.SessionState?["bind_step"]?.GetValue<string>();

        // 检查是否被锁定
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmdLock = conn.CreateCommand();
        cmdLock.CommandText = @"
            SELECT bind_fail_count, bind_locked_until 
            FROM employee_channel_bindings 
            WHERE channel = 'wecom' AND channel_user_id = $1 AND status = 'pending'
            ORDER BY created_at DESC LIMIT 1";
        cmdLock.Parameters.AddWithValue(session.WeComUserId);
        await using var lockReader = await cmdLock.ExecuteReaderAsync(ct);
        if (await lockReader.ReadAsync(ct))
        {
            var failCount = lockReader.GetInt32(0);
            var lockedUntil = lockReader.IsDBNull(1) ? (DateTimeOffset?)null : lockReader.GetFieldValue<DateTimeOffset>(1);
            if (lockedUntil.HasValue && lockedUntil.Value > DateTimeOffset.UtcNow)
            {
                await lockReader.CloseAsync();
                return $"验证失败次数过多，已锁定至 {lockedUntil.Value.ToOffset(TimeSpan.FromHours(9)):HH:mm}。\n请联系管理员处理。";
            }
        }
        await lockReader.CloseAsync();

        // 状态机
        switch (bindState)
        {
            case "awaiting_password":
            {
                // 用户输入的是密码 → 验证
                var pendingCode = session.SessionState?["pending_employee_code"]?.GetValue<string>();
                if (string.IsNullOrEmpty(pendingCode))
                {
                    await UpdateSessionStateAsync(session, "binding", new JsonObject(), ct);
                    return "会话已过期，请重新发送：绑定 您的工号\n例如：绑定 E1106";
                }

                return await VerifyPasswordAndBindAsync(companyCode, session, pendingCode, text, ct);
            }
            default:
            {
                // 检查是否是"绑定 XXX"格式
                var bindMatch = System.Text.RegularExpressions.Regex.Match(
                    text, @"^(?:绑定|バインド|bind)\s+(\S+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (bindMatch.Success)
                {
                    var employeeCode = bindMatch.Groups[1].Value;
                    return await StartBindingAsync(companyCode, session, employeeCode, ct);
                }

                // 首次交互 / 其他消息 → 引导绑定
                return "您好！首次使用需要绑定员工账号。\n\n请发送：绑定 您的工号\n例如：绑定 E1106\n\n如果您不知道自己的工号，请联系管理员。";
            }
        }
    }

    /// <summary>
    /// 开始绑定流程 - 查找员工并要求输入密码
    /// </summary>
    private async Task<string> StartBindingAsync(
        string companyCode, EmployeeSession session, string employeeCode, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, name, dept_id FROM users 
            WHERE company_code = $1 AND employee_code = $2 AND is_active = true
            LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(employeeCode);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            await reader.CloseAsync();
            return $"未找到工号 {employeeCode} 对应的账号。\n请检查工号是否正确，或联系管理员。\n\n重新输入：绑定 您的工号";
        }

        var userId = reader.GetGuid(0);
        var name = reader.IsDBNull(1) ? "従業員" : reader.GetString(1);
        var deptId = reader.IsDBNull(2) ? null : reader.GetString(2);
        await reader.CloseAsync();

        // 姓名脱敏：田中太郎 → 田*太郎
        var maskedName = name.Length > 2
            ? name[0] + new string('*', name.Length - 2) + name[^1]
            : name.Length == 2 ? name[0] + "*" : name;

        // 查找部门名
        string? deptName = null;
        if (!string.IsNullOrEmpty(deptId))
        {
            await using var cmdDept = conn.CreateCommand();
            cmdDept.CommandText = @"SELECT name FROM departments WHERE company_code = $1 AND department_code = $2 LIMIT 1";
            cmdDept.Parameters.AddWithValue(companyCode);
            cmdDept.Parameters.AddWithValue(deptId);
            var dn = await cmdDept.ExecuteScalarAsync(ct);
            deptName = dn as string;
        }

        // 保存待绑定状态
        var state = new JsonObject
        {
            ["bind_step"] = "awaiting_password",
            ["pending_employee_code"] = employeeCode,
            ["pending_user_id"] = userId.ToString()
        };
        await UpdateSessionStateAsync(session, "binding", state, ct);

        var deptInfo = !string.IsNullOrEmpty(deptName) ? $"（所属：{deptName}）" : "";
        return $"找到员工：{maskedName}{deptInfo}\n\n确认是您本人吗？请发送您的系统登录密码进行验证。\n\n（密码仅用于一次性验证，不会被保存）";
    }

    /// <summary>
    /// 验证密码并完成绑定
    /// </summary>
    private async Task<string> VerifyPasswordAndBindAsync(
        string companyCode, EmployeeSession session, string employeeCode, string password, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        // 查找用户和密码哈希
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, password_hash, name, employee_id FROM users 
            WHERE company_code = $1 AND employee_code = $2 AND is_active = true
            LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(employeeCode);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            await reader.CloseAsync();
            await ClearSessionStateAsync(session, ct);
            return "账号信息异常，请重新发送：绑定 您的工号";
        }

        var userId = reader.GetGuid(0);
        var hash = reader.GetString(1);
        var name = reader.IsDBNull(2) ? "従業員" : reader.GetString(2);
        var employeeId = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3);
        await reader.CloseAsync();

        // BCrypt 验证密码
        if (!BCrypt.Net.BCrypt.Verify(password, hash))
        {
            // 在 session_state 中维护失败计数（简单可靠）
            var currentFail = session.SessionState?["bind_fail_count"]?.GetValue<int>() ?? 0;
            currentFail++;

            if (currentFail >= 3)
            {
                // 锁定：在绑定表中插入锁定记录
                await using var cmdLockInsert = conn.CreateCommand();
                cmdLockInsert.CommandText = @"
                    INSERT INTO employee_channel_bindings 
                    (company_code, user_id, channel, channel_user_id, status, bind_fail_count, bind_locked_until, bind_method)
                    VALUES ($1, $2, 'wecom', $3, 'pending', $4, now() + interval '24 hours', 'self_service')
                    ON CONFLICT DO NOTHING";
                cmdLockInsert.Parameters.AddWithValue(companyCode);
                cmdLockInsert.Parameters.AddWithValue(userId);
                cmdLockInsert.Parameters.AddWithValue(session.WeComUserId);
                cmdLockInsert.Parameters.AddWithValue(currentFail);
                await cmdLockInsert.ExecuteNonQueryAsync(ct);

                await ClearSessionStateAsync(session, ct);
                return "验证失败次数过多，已锁定 24 小时。\n请联系管理员处理。";
            }

            // 更新 session state 中的失败计数
            var failState = new JsonObject
            {
                ["bind_step"] = "awaiting_password",
                ["pending_employee_code"] = employeeCode,
                ["pending_user_id"] = userId.ToString(),
                ["bind_fail_count"] = currentFail
            };
            await UpdateSessionStateAsync(session, "binding", failState, ct);

            return $"密码不正确，请重试。（剩余 {3 - currentFail} 次机会）";
        }

        // 密码验证通过 → 创建绑定
        // 先清理旧的 pending 记录
        await using var cmdClean = conn.CreateCommand();
        cmdClean.CommandText = @"
            DELETE FROM employee_channel_bindings 
            WHERE channel = 'wecom' AND channel_user_id = $1 AND status != 'active'";
        cmdClean.Parameters.AddWithValue(session.WeComUserId);
        await cmdClean.ExecuteNonQueryAsync(ct);

        // 插入正式绑定
        await using var cmdBind = conn.CreateCommand();
        cmdBind.CommandText = @"
            INSERT INTO employee_channel_bindings 
            (company_code, user_id, channel, channel_user_id, channel_name, bind_method, status, bound_at)
            VALUES ($1, $2, 'wecom', $3, $4, 'self_service', 'active', now())
            ON CONFLICT DO NOTHING
            RETURNING id";
        cmdBind.Parameters.AddWithValue(companyCode);
        cmdBind.Parameters.AddWithValue(userId);
        cmdBind.Parameters.AddWithValue(session.WeComUserId);
        cmdBind.Parameters.AddWithValue(name);
        var bindId = await cmdBind.ExecuteScalarAsync(ct);

        if (bindId == null)
        {
            return "绑定失败，该微信账号可能已绑定其他员工。\n请联系管理员处理。";
        }

        // 更新会话
        session.UserId = userId;
        session.EmployeeId = employeeId;
        session.IsBound = true;
        await using var cmdUpdateSession = conn.CreateCommand();
        cmdUpdateSession.CommandText = @"
            UPDATE wecom_employee_sessions 
            SET employee_id = $2, session_state = '{}'::jsonb, updated_at = now()
            WHERE id = $1";
        cmdUpdateSession.Parameters.AddWithValue(session.Id);
        cmdUpdateSession.Parameters.AddWithValue(employeeId.HasValue ? (object)employeeId.Value : DBNull.Value);
        await cmdUpdateSession.ExecuteNonQueryAsync(ct);

        // 加载权限
        session.Caps = await LoadUserCapsAsync(conn, userId, companyCode, ct);

        // 通知管理员
        _logger.LogInformation("[EmployeeGW] 绑定成功: user={UserId}, employee={EmployeeCode}, channel=wecom:{ChannelUser}",
            userId, employeeCode, session.WeComUserId);

        // 构建功能列表
        var features = BuildFeatureList(session.Caps);

        return $"✅ 绑定成功！欢迎，{name}さん。\n\n您可以使用以下功能：\n{features}\n\n输入「帮助」查看完整功能列表。";
    }

    // ==================== 权限守卫 ====================

    /// <summary>
    /// 意图→所需权限映射表
    /// </summary>
    private static readonly Dictionary<string, string> IntentCapMap = new()
    {
        ["timesheet.entry"]     = "ai.timesheet.entry",
        ["timesheet.upload"]    = "ai.timesheet.entry",
        ["timesheet.query"]     = "ai.timesheet.query",
        ["timesheet.submit"]    = "ai.timesheet.entry",
        ["timesheet.approve"]   = "ai.timesheet.approve",
        ["payroll.query"]       = "ai.payroll.query",
        ["payroll.report"]      = "ai.payroll.report",
        ["invoice.recognize"]   = "ai.invoice.recognize",
        ["voucher.create"]      = "ai.voucher.create",
        ["report.financial"]    = "ai.report.financial",
        ["certificate.apply"]   = "ai.certificate.apply",
        ["certificate.approve"] = "ai.certificate.approve",
        ["leave.query"]         = "ai.leave.apply",
        ["leave.approve"]       = "ai.leave.approve",
        ["order.manage"]        = "ai.order.manage",
        ["delivery.manage"]     = "ai.delivery.manage",
    };

    /// <summary>
    /// 意图的中文友好名映射
    /// </summary>
    private static readonly Dictionary<string, string> IntentNameMap = new()
    {
        ["timesheet.entry"]     = "工时录入",
        ["timesheet.upload"]    = "工时上传",
        ["timesheet.query"]     = "工时查询",
        ["timesheet.submit"]    = "工时提交",
        ["timesheet.approve"]   = "工时审批",
        ["payroll.query"]       = "薪资查询",
        ["payroll.report"]      = "薪资报表",
        ["invoice.recognize"]   = "发票识别",
        ["voucher.create"]      = "记账",
        ["report.financial"]    = "财务报表",
        ["certificate.apply"]   = "证明书申请",
        ["certificate.approve"] = "证明书审批",
        ["leave.query"]         = "休假管理",
        ["leave.approve"]       = "休假审批",
        ["order.manage"]        = "订单管理",
        ["delivery.manage"]     = "纳品书管理",
    };

    private static (bool Allowed, string Message) CheckPermission(EmployeeSession session, string intent)
    {
        // 通用意图、确认/取消不需要权限检查
        if (intent is "general.question" or "confirm" or "deny" or "help" or "binding")
            return (true, "");

        if (!IntentCapMap.TryGetValue(intent, out var requiredCap))
            return (true, "");  // 未知意图不拦截

        if (session.Caps.Contains(requiredCap))
            return (true, "");

        var intentName = IntentNameMap.TryGetValue(intent, out var name) ? name : intent;
        return (false, $"抱歉，您没有「{intentName}」的使用权限。\n如需开通，请联系管理员。\n\n输入「帮助」查看您可用的功能。");
    }

    /// <summary>
    /// 根据用户能力构建功能列表
    /// </summary>
    private static string BuildFeatureList(List<string> caps)
    {
        var features = new List<string>();

        if (caps.Contains("ai.timesheet.entry"))  features.Add("📝 工时录入/查询 - 发送 \"今天9点到18点\"");
        if (caps.Contains("ai.payroll.query"))     features.Add("💰 薪资查询 - 发送 \"查看工资\"");
        if (caps.Contains("ai.certificate.apply")) features.Add("📄 证明书申请 - 发送 \"申请在职证明\"");
        if (caps.Contains("ai.leave.apply"))       features.Add("🏖 休假申请 - 发送 \"请假\"");
        if (caps.Contains("ai.timesheet.approve")) features.Add("✅ 工时审批 - 发送 \"审批工时\"");
        if (caps.Contains("ai.leave.approve"))     features.Add("✅ 休假审批 - 发送 \"审批休假\"");
        if (caps.Contains("ai.invoice.recognize")) features.Add("🧾 发票识别 - 发送发票图片");
        if (caps.Contains("ai.voucher.create"))    features.Add("📒 记账 - 发送 \"记账\"");
        if (caps.Contains("ai.report.financial"))  features.Add("📊 财务报表 - 发送 \"查看报表\"");
        if (caps.Contains("ai.order.manage"))      features.Add("📦 订单管理 - 发送 \"查看订单\"");
        if (caps.Contains("ai.delivery.manage"))   features.Add("🚚 纳品书管理 - 发送 \"纳品书\"");

        return features.Count > 0
            ? string.Join("\n", features)
            : "（暂无可用功能，请联系管理员分配权限）";
    }

    // ==================== 意图→技能映射 ====================

    private static readonly Dictionary<string, string> IntentToSkillMap = new()
    {
        ["timesheet.entry"]     = "timesheet",
        ["timesheet.upload"]    = "timesheet",
        ["timesheet.query"]     = "timesheet",
        ["timesheet.submit"]    = "timesheet",
        ["payroll.query"]       = "payroll",
        ["certificate.apply"]   = "certificate",
        ["leave.query"]         = "leave",
        ["invoice.recognize"]   = "invoice.booking",
    };

    /// <summary>根据意图路由到对应处理器</summary>
    private async Task<string> RouteIntentAsync(
        string companyCode, EmployeeSession session, WeComMessage message,
        WeComIntentClassifier.IntentResult intent, CancellationToken ct)
    {
        // 帮助命令
        if (intent.Intent == "help" || (message.Content ?? "").Trim() is "帮助" or "ヘルプ" or "help")
        {
            var features = BuildFeatureList(session.Caps);
            return $"📋 您可以使用以下功能：\n\n{features}\n\n直接发送对应的指令即可。";
        }

        var reply = intent.Intent switch
        {
            "timesheet.entry" => await HandleTimesheetEntryAsync(companyCode, session, message, intent, ct),
            "timesheet.upload" => await HandleTimesheetUploadAsync(companyCode, session, message, intent, ct),
            "timesheet.query" => await HandleTimesheetQueryAsync(companyCode, session, ct),
            "timesheet.submit" => await HandleTimesheetSubmitAsync(companyCode, session, ct),
            "payroll.query" => await HandlePayrollQueryAsync(companyCode, session, ct),
            "certificate.apply" => await HandleCertificateApplyAsync(companyCode, session, message, intent, ct),
            "leave.query" => await HandleLeaveAsync(companyCode, session, message, intent, ct),
            "invoice.recognize" => await HandleInvoiceImageAsync(companyCode, session, message, ct),
            "confirm" => await HandleConfirmAsync(companyCode, session, intent, ct),
            "deny" => await HandleDenyAsync(session, ct),
            _ => await HandleGeneralAsync(companyCode, session, message, ct)
        };

        // Context Engine: 记录活跃技能（用于后续跟进判断）
        if (IntentToSkillMap.TryGetValue(intent.Intent, out var skillName))
        {
            session.SessionState ??= new JsonObject();
            session.SessionState["activeSkill"] = skillName;
            session.SessionState["lastActionTime"] = DateTimeOffset.UtcNow.ToString("O");
        }

        return reply;
    }

    // ==================== 工时录入 ====================

    private async Task<string> HandleTimesheetEntryAsync(
        string companyCode, EmployeeSession session, WeComMessage message,
        WeComIntentClassifier.IntentResult intent, CancellationToken ct)
    {
        if (session.ResourceId == null)
            return "抱歉，您的账号尚未关联员工信息。请联系管理员完成员工绑定。";

        var rawInput = intent.Entities.GetValueOrDefault("rawInput", message.Content ?? "");
        var scope = intent.Entities.GetValueOrDefault("scope", "today");

        // 使用 LLM 解析自然语言中的具体时间
        var parsedEntries = await ParseTimesheetFromTextAsync(companyCode, rawInput, scope, ct);

        if (parsedEntries == null || parsedEntries.Count == 0)
        {
            // 引导用户提供更多信息
            await UpdateSessionStateAsync(session, "timesheet.entry", new JsonObject
            {
                ["awaitingInput"] = true
            }, ct);

            return scope switch
            {
                "week" => "收到，您想录入本周的工时。请告诉我每天的上班和下班时间，例如：\n" +
                           "周一~周五 9:00-18:00\n" +
                           "或者分别告诉我：\n" +
                           "周一 9:00-18:00\n周二 9:00-19:00\n...\n\n" +
                           "您也可以直接上传 Excel 工时表文件。",
                _ => "请告诉我您的上班和下班时间，例如：\n" +
                     "今天 9:00-18:00\n\n" +
                     "或者更详细：\n" +
                     "今天 9:00 到 18:00，午休 1 小时"
            };
        }

        // 保存解析的数据到会话，等待确认
        var entriesJson = new JsonArray();
        foreach (var e in parsedEntries)
        {
            entriesJson.Add(new JsonObject
            {
                ["date"] = e.Date.ToString("yyyy-MM-dd"),
                ["dayOfWeek"] = e.Date.ToString("ddd"),
                ["startTime"] = e.StartTime?.ToString(@"hh\:mm"),
                ["endTime"] = e.EndTime?.ToString(@"hh\:mm"),
                ["breakMinutes"] = e.BreakMinutes,
                ["regularHours"] = e.RegularHours,
                ["overtimeHours"] = e.OvertimeHours,
                ["isHoliday"] = e.IsHoliday
            });
        }

        await UpdateSessionStateAsync(session, "timesheet.entry", new JsonObject
        {
            ["pendingEntries"] = entriesJson,
            ["awaitingConfirmation"] = true
        }, ct);

        // 构造确认消息
        var totalRegular = parsedEntries.Sum(e => e.RegularHours);
        var totalOvertime = parsedEntries.Sum(e => e.OvertimeHours);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("已识别以下工时信息：\n");
        foreach (var e in parsedEntries)
        {
            var line = $"  {e.Date:yyyy-MM-dd}({e.Date:ddd}) {e.StartTime:hh\\:mm}~{e.EndTime:hh\\:mm}";
            if (e.OvertimeHours > 0) line += $" (含加班 {e.OvertimeHours:F1}h)";
            sb.AppendLine(line);
        }
        sb.AppendLine($"\n合计：正常 {totalRegular:F1}h + 加班 {totalOvertime:F1}h");
        sb.AppendLine("\n确认录入吗？回复「是」确认，「否」取消修改。");

        return sb.ToString();
    }

    private async Task<string> HandleTimesheetUploadAsync(
        string companyCode, EmployeeSession session, WeComMessage message,
        WeComIntentClassifier.IntentResult intent, CancellationToken ct)
    {
        if (session.ResourceId == null)
            return "抱歉，您的账号尚未关联员工信息。请联系管理员完成员工绑定。";

        // 如果是文件/图片消息，下载并通过 AI 解析
        if ((message.MsgType == "file" || message.MsgType == "image") && !string.IsNullOrEmpty(message.MediaId))
        {
            // 1. 从企业微信下载文件
            var mediaResult = await _wecomService.DownloadMediaAsync(message.MediaId, ct);
            if (mediaResult == null)
            {
                return "文件下载失败，请重新发送。如果问题持续，请在系统网页版中上传工时表文件。";
            }

            var (fileData, mimeType, fileName) = mediaResult.Value;

            // 2. AI 解析
            TimesheetAiParser.ParseResult parseResult;
            if (mimeType.StartsWith("image/"))
            {
                parseResult = await _timesheetParser.ParseImageAsync(fileData, mimeType, ct);
            }
            else if (mimeType.Contains("csv") || (fileName?.EndsWith(".csv") ?? false))
            {
                var csvText = System.Text.Encoding.UTF8.GetString(fileData);
                parseResult = await _timesheetParser.ParseCsvAsync(csvText, ct);
            }
            else
            {
                // Excel 或其他 → 尝试文本模式
                var textContent = System.Text.Encoding.UTF8.GetString(
                    fileData.Take(Math.Min(fileData.Length, 8000)).ToArray());
                parseResult = await _timesheetParser.ParseExcelTextAsync(textContent, ct);
            }

            if (!parseResult.Success || parseResult.Entries.Count == 0)
            {
                return $"AI解析未能从文件中识别出工时数据。\n" +
                       $"原因：{parseResult.ErrorMessage ?? "无法解析文件内容"}\n\n" +
                       "请确认文件包含日期和上下班时间信息，或尝试直接用文字告诉我。";
            }

            // 3. 构造确认数据
            var entriesJson = new JsonArray();
            foreach (var e in parseResult.Entries)
            {
                entriesJson.Add(new JsonObject
                {
                    ["date"] = e.Date,
                    ["startTime"] = e.StartTime,
                    ["endTime"] = e.EndTime,
                    ["breakMinutes"] = e.BreakMinutes,
                    ["regularHours"] = e.RegularHours,
                    ["overtimeHours"] = e.OvertimeHours,
                    ["isHoliday"] = e.IsHoliday
                });
            }

            await UpdateSessionStateAsync(session, "timesheet.entry", new JsonObject
            {
                ["pendingEntries"] = entriesJson,
                ["awaitingConfirmation"] = true,
                ["source"] = "file_upload"
            }, ct);

            // 4. 返回确认消息
            var totalRegular = parseResult.Entries.Sum(e => e.RegularHours);
            var totalOvertime = parseResult.Entries.Sum(e => e.OvertimeHours);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"AI解析完成！信頼度: {parseResult.Confidence * 100:F0}%\n");
            sb.AppendLine($"共识别 {parseResult.Entries.Count} 天工时数据：\n");
            
            foreach (var e in parseResult.Entries.Take(10))
            {
                sb.AppendLine($"  {e.Date} {e.StartTime}~{e.EndTime} = {e.RegularHours:F1}h{(e.OvertimeHours > 0 ? $"+{e.OvertimeHours:F1}h" : "")}");
            }
            if (parseResult.Entries.Count > 10)
                sb.AppendLine($"  ... 等共 {parseResult.Entries.Count} 天");

            sb.AppendLine($"\n合计：正常 {totalRegular:F1}h + 加班 {totalOvertime:F1}h");

            if (parseResult.Warnings.Count > 0)
            {
                sb.AppendLine($"\n注意：{string.Join("；", parseResult.Warnings)}");
            }

            sb.AppendLine("\n确认录入吗？回复「是」确认，「否」取消。");
            return sb.ToString();
        }

        return "请直接发送 Excel 文件或拍照上传工时表。\n支持的格式：Excel (.xlsx/.xls)、CSV、图片";
    }

    // ==================== 工时查询 ====================

    private async Task<string> HandleTimesheetQueryAsync(
        string companyCode, EmployeeSession session, CancellationToken ct)
    {
        if (session.ResourceId == null)
            return "抱歉，您的账号尚未关联员工信息。";

        await using var conn = await _ds.OpenConnectionAsync(ct);
        var currentMonth = DateTime.Today.ToString("yyyy-MM");

        // 查询本月每日工时
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT entry_date, start_time, end_time, regular_hours, overtime_hours, holiday_flag
            FROM timesheet_daily_entries
            WHERE company_code = $1 AND resource_id = $2
              AND entry_date >= date_trunc('month', CURRENT_DATE)
              AND entry_date <= CURRENT_DATE
            ORDER BY entry_date";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(session.ResourceId.Value);

        var entries = new List<(DateTime date, TimeSpan? start, TimeSpan? end, decimal regular, decimal overtime, bool holiday)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add((
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? null : reader.GetFieldValue<TimeSpan>(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<TimeSpan>(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetBoolean(5)
            ));
        }

        if (entries.Count == 0)
        {
            return $"本月（{currentMonth}）暂无工时记录。\n\n" +
                   "您可以通过以下方式录入工时：\n" +
                   "1. 直接告诉我，例如：「今天 9:00-18:00」\n" +
                   "2. 上传 Excel 工时表文件";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📋 {currentMonth} 本月工时：\n");

        var totalRegular = 0m;
        var totalOvertime = 0m;
        foreach (var e in entries)
        {
            var flag = e.holiday ? " 🔴休" : "";
            sb.AppendLine($"  {e.date:MM/dd}({e.date:ddd}) {e.start:hh\\:mm}~{e.end:hh\\:mm} = {e.regular:F1}h{(e.overtime > 0 ? $"+{e.overtime:F1}h加班" : "")}{flag}");
            totalRegular += e.regular;
            totalOvertime += e.overtime;
        }
        sb.AppendLine($"\n合计：正常 {totalRegular:F1}h + 加班 {totalOvertime:F1}h");
        sb.AppendLine($"已录入 {entries.Count} 天");

        // 查询月度汇总审批状态
        await using var cmdSummary = conn.CreateCommand();
        cmdSummary.CommandText = @"
            SELECT approval_status, submitted_at 
            FROM staffing_timesheet_summary 
            WHERE company_code = $1 AND resource_id = $2 AND year_month = $3 
            LIMIT 1";
        cmdSummary.Parameters.AddWithValue(companyCode);
        cmdSummary.Parameters.AddWithValue(session.ResourceId.Value);
        cmdSummary.Parameters.AddWithValue(currentMonth);

        await using var reader2 = await cmdSummary.ExecuteReaderAsync(ct);
        if (await reader2.ReadAsync(ct))
        {
            var approvalStatus = reader2.IsDBNull(0) ? "draft" : reader2.GetString(0);
            sb.AppendLine($"\n审批状态：{FormatApprovalStatus(approvalStatus)}");
        }
        else
        {
            sb.AppendLine("\n审批状态：未提交");
        }

        return sb.ToString();
    }

    // ==================== 工时提交 ====================

    private async Task<string> HandleTimesheetSubmitAsync(
        string companyCode, EmployeeSession session, CancellationToken ct)
    {
        if (session.ResourceId == null)
            return "抱歉，您的账号尚未关联员工信息。";

        var currentMonth = DateTime.Today.ToString("yyyy-MM");

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // 检查是否有工时记录
        await using var cmdCount = conn.CreateCommand();
        cmdCount.CommandText = @"
            SELECT COUNT(*), COALESCE(SUM(regular_hours),0), COALESCE(SUM(overtime_hours),0)
            FROM timesheet_daily_entries
            WHERE company_code = $1 AND resource_id = $2
              AND entry_date >= date_trunc('month', CURRENT_DATE)
              AND entry_date < date_trunc('month', CURRENT_DATE) + interval '1 month'";
        cmdCount.Parameters.AddWithValue(companyCode);
        cmdCount.Parameters.AddWithValue(session.ResourceId.Value);

        await using var reader = await cmdCount.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetInt64(0) == 0)
            return "本月暂无工时记录，无法提交。请先录入工时。";

        var count = reader.GetInt64(0);
        var totalRegular = reader.GetDecimal(1);
        var totalOvertime = reader.GetDecimal(2);
        await reader.CloseAsync();

        // 检查是否已提交
        await using var cmdCheck = conn.CreateCommand();
        cmdCheck.CommandText = @"
            SELECT approval_status FROM staffing_timesheet_summary 
            WHERE company_code = $1 AND resource_id = $2 AND year_month = $3 LIMIT 1";
        cmdCheck.Parameters.AddWithValue(companyCode);
        cmdCheck.Parameters.AddWithValue(session.ResourceId.Value);
        cmdCheck.Parameters.AddWithValue(currentMonth);

        var existingStatus = (await cmdCheck.ExecuteScalarAsync(ct))?.ToString();
        if (existingStatus == "submitted" || existingStatus == "approved")
            return $"本月工时已{(existingStatus == "approved" ? "审批通过" : "提交审批中")}，无需重复提交。";

        // 保存到待确认状态
        await UpdateSessionStateAsync(session, "timesheet.submit", new JsonObject
        {
            ["month"] = currentMonth,
            ["dayCount"] = count,
            ["totalRegular"] = totalRegular,
            ["totalOvertime"] = totalOvertime,
            ["awaitingConfirmation"] = true
        }, ct);

        return $"即将提交 {currentMonth} 工时审批：\n\n" +
               $"  已录入天数：{count} 天\n" +
               $"  正常工时：{totalRegular:F1} 小时\n" +
               $"  加班工时：{totalOvertime:F1} 小时\n\n" +
               "确认提交吗？回复「是」确认。";
    }

    // ==================== 工资查询 ====================

    private async Task<string> HandlePayrollQueryAsync(
        string companyCode, EmployeeSession session, CancellationToken ct)
    {
        if (session.EmployeeId == null)
            return "抱歉，您的账号尚未关联员工信息。";

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT pay_period, gross_salary, total_deductions, net_salary, status, paid_at
            FROM payroll_results
            WHERE company_code = $1 AND employee_id = $2
            ORDER BY pay_period DESC LIMIT 3";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(session.EmployeeId.Value);

        var results = new List<(string period, decimal gross, decimal deductions, decimal net, string status, DateTime? paid)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add((
                reader.GetString(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5)
            ));
        }

        if (results.Count == 0)
            return "暂无工资记录。如有疑问请联系人事部门。";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("💰 最近工资明细：\n");

        foreach (var r in results)
        {
            sb.AppendLine($"── {r.period} ──");
            sb.AppendLine($"  应发合计：¥{r.gross:N0}");
            sb.AppendLine($"  扣除合计：¥{r.deductions:N0}");
            sb.AppendLine($"  实发金额：¥{r.net:N0}");
            sb.AppendLine($"  状态：{(r.status == "paid" ? $"已发放 ({r.paid:yyyy-MM-dd})" : "处理中")}");
            sb.AppendLine();
        }

        sb.AppendLine("如需更详细的工资明细，请登录系统查看。");
        return sb.ToString();
    }

    // ==================== 证明书申请 ====================

    private async Task<string> HandleCertificateApplyAsync(
        string companyCode, EmployeeSession session, WeComMessage message,
        WeComIntentClassifier.IntentResult intent, CancellationToken ct)
    {
        if (session.EmployeeId == null)
            return "抱歉，您的账号尚未关联员工信息。";

        // 检查是否已有待处理的申请
        var certType = DetectCertificateType(message.Content ?? "");

        if (certType == null)
        {
            return "请告诉我您需要申请哪种证明书：\n\n" +
                   "1. 在职证明\n" +
                   "2. 收入证明\n" +
                   "3. 退职证明\n" +
                   "4. 就业证明\n\n" +
                   "回复编号或名称即可。";
        }

        await UpdateSessionStateAsync(session, "certificate.apply", new JsonObject
        {
            ["certificateType"] = certType,
            ["awaitingConfirmation"] = true
        }, ct);

        var typeName = certType switch
        {
            "employment" => "在职证明",
            "income" => "收入证明",
            "resignation" => "退职证明",
            "employment_cert" => "就业证明",
            _ => certType
        };

        return $"您要申请「{typeName}」，请确认以下信息：\n\n" +
               $"  类型：{typeName}\n" +
               $"  用途：请简要说明用途（如签证、贷款等）\n\n" +
               "回复「是」直接提交，或回复用途后提交。";
    }

    // ==================== 休假申请/查询 ====================

    private async Task<string> HandleLeaveAsync(
        string companyCode, EmployeeSession session, WeComMessage message,
        WeComIntentClassifier.IntentResult intent, CancellationToken ct)
    {
        if (session.EmployeeId == null)
            return "抱歉，您的账号尚未关联员工信息。";

        var msg = message.Content ?? "";

        // 判断是查询还是申请
        var isQuery = System.Text.RegularExpressions.Regex.IsMatch(msg,
            @"(查|確認|看|剩|残|余|あと|何日|有几|多少|balance)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (isQuery)
        {
            return await QueryLeaveBalanceAsync(companyCode, session, ct);
        }

        // 请假申请 - 尝试解析日期
        var parsedLeave = await ParseLeaveRequestAsync(msg, ct);
        if (parsedLeave == null)
        {
            await UpdateSessionStateAsync(session, "leave.apply", new JsonObject
            {
                ["awaitingInput"] = true
            }, ct);

            return "请告诉我您的请假信息，例如：\n\n" +
                   "「2月15日请一天有休」\n" +
                   "「下周一到周三请病假」\n" +
                   "「明天请半天假」\n\n" +
                   "或者回复「查看余额」查看剩余假期天数。";
        }

        // 保存到会话，等待确认
        await UpdateSessionStateAsync(session, "leave.apply", new JsonObject
        {
            ["leaveType"] = parsedLeave.LeaveType,
            ["startDate"] = parsedLeave.StartDate.ToString("yyyy-MM-dd"),
            ["endDate"] = parsedLeave.EndDate.ToString("yyyy-MM-dd"),
            ["days"] = parsedLeave.Days,
            ["reason"] = parsedLeave.Reason,
            ["awaitingConfirmation"] = true
        }, ct);

        var typeLabel = parsedLeave.LeaveType switch
        {
            "paid" => "有給休暇",
            "sick" => "病気休暇",
            "special" => "特別休暇",
            "unpaid" => "無給休暇",
            _ => parsedLeave.LeaveType
        };

        return $"休暇申請の確認：\n\n" +
               $"  種類：{typeLabel}\n" +
               $"  期間：{parsedLeave.StartDate:yyyy-MM-dd} ～ {parsedLeave.EndDate:yyyy-MM-dd}\n" +
               $"  日数：{parsedLeave.Days} 日\n" +
               (parsedLeave.Reason != null ? $"  理由：{parsedLeave.Reason}\n" : "") +
               "\n提出しますか？「はい」で確認。";
    }

    private async Task<string> QueryLeaveBalanceAsync(
        string companyCode, EmployeeSession session, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        
        // 查询今年已使用的假期
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT leave_type, COALESCE(SUM(days), 0) as used_days, COUNT(*) as count
            FROM leave_requests
            WHERE company_code = $1 AND employee_id = $2 
              AND start_date >= date_trunc('year', CURRENT_DATE)
              AND status IN ('approved', 'pending')
            GROUP BY leave_type";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(session.EmployeeId!.Value);

        var usageByType = new Dictionary<string, (decimal used, int count)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var type = reader.GetString(0);
            usageByType[type] = (reader.GetDecimal(1), reader.GetInt32(2));
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📊 {DateTime.Today.Year}年 休暇状況：\n");
        
        // 有给休假（默认年间20天，实际应从员工信息获取）
        var paidUsed = usageByType.GetValueOrDefault("paid", (0, 0));
        var paidTotal = 20m; // TODO: 从员工记录获取实际年假天数
        sb.AppendLine($"  有給休暇：{paidUsed.used}/{paidTotal}日 使用済み（残り {paidTotal - paidUsed.used}日）");
        
        if (usageByType.ContainsKey("sick"))
            sb.AppendLine($"  病気休暇：{usageByType["sick"].used}日 使用");
        if (usageByType.ContainsKey("special"))
            sb.AppendLine($"  特別休暇：{usageByType["special"].used}日 使用");

        // 查询最近的申请
        await using var cmdRecent = conn.CreateCommand();
        cmdRecent.CommandText = @"
            SELECT start_date, end_date, days, leave_type, status
            FROM leave_requests
            WHERE company_code = $1 AND employee_id = $2 
            ORDER BY created_at DESC LIMIT 3";
        cmdRecent.Parameters.AddWithValue(companyCode);
        cmdRecent.Parameters.AddWithValue(session.EmployeeId!.Value);

        await using var reader2 = await cmdRecent.ExecuteReaderAsync(ct);
        var hasRecent = false;
        while (await reader2.ReadAsync(ct))
        {
            if (!hasRecent) { sb.AppendLine("\n最近の申請："); hasRecent = true; }
            var start = reader2.GetDateTime(0);
            var end = reader2.GetDateTime(1);
            var days = reader2.GetDecimal(2);
            var type = reader2.GetString(3);
            var status = reader2.GetString(4);
            sb.AppendLine($"  {start:MM/dd}~{end:MM/dd} {days}日 [{FormatLeaveType(type)}] {FormatApprovalStatus(status)}");
        }

        sb.AppendLine("\n休暇を申請する場合は、日付と種類を教えてください。");
        return sb.ToString();
    }

    private async Task<LeaveRequestData?> ParseLeaveRequestAsync(string text, CancellationToken ct)
    {
        var apiKey = _config["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        var today = DateTime.Today;
        var jsonExample = @"{""leaveType"":""paid|sick|special|unpaid"",""startDate"":""YYYY-MM-DD"",""endDate"":""YYYY-MM-DD"",""days"":1.0,""reason"":""...""}";
        var systemPrompt = $@"你是请假申请解析器。从用户消息中提取请假信息。
今天是 {today:yyyy-MM-dd}（{today:dddd}）。

返回 JSON：{jsonExample}
如果无法解析，返回 null。
仅返回 JSON，不要其他文字。";

        try
        {
            var response = await client.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", new
            {
                model = "gpt-4o-mini",
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = text }
                },
                temperature = 0.1,
                max_tokens = 200
            }, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var content = result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content) || content.Trim() == "null") return null;

            content = content.Trim();
            if (content.StartsWith("```")) content = string.Join('\n', content.Split('\n').Skip(1).SkipLast(1));

            using var doc = JsonDocument.Parse(content);
            var r = doc.RootElement;
            return new LeaveRequestData
            {
                LeaveType = r.GetProperty("leaveType").GetString() ?? "paid",
                StartDate = DateTime.Parse(r.GetProperty("startDate").GetString()!),
                EndDate = DateTime.Parse(r.GetProperty("endDate").GetString()!),
                Days = r.TryGetProperty("days", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDecimal() : 1,
                Reason = r.TryGetProperty("reason", out var re) && re.ValueKind == JsonValueKind.String ? re.GetString() : null
            };
        }
        catch { return null; }
    }

    private async Task<string> ConfirmLeaveAsync(
        string companyCode, EmployeeSession session, CancellationToken ct)
    {
        if (session.EmployeeId == null) return "系统错误：未关联员工。";

        var state = session.SessionState;
        if (state == null || !state.ContainsKey("startDate"))
            return "没有待确认的休暇申请。请重新告诉我您的请假信息。";

        var leaveType = state["leaveType"]?.GetValue<string>() ?? "paid";
        var startDate = DateTime.Parse(state["startDate"]!.GetValue<string>());
        var endDate = DateTime.Parse(state["endDate"]!.GetValue<string>());
        var days = state["days"]?.GetValue<decimal>() ?? 1;
        var reason = state["reason"]?.GetValue<string>();

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO leave_requests 
            (company_code, employee_id, resource_id, leave_type, start_date, end_date, days, reason, status, source)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, 'pending', 'wecom')
            RETURNING id";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(session.EmployeeId.Value);
        cmd.Parameters.AddWithValue(session.ResourceId.HasValue ? (object)session.ResourceId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(leaveType);
        cmd.Parameters.AddWithValue(startDate);
        cmd.Parameters.AddWithValue(endDate);
        cmd.Parameters.AddWithValue(days);
        cmd.Parameters.AddWithValue(reason ?? (object)DBNull.Value);

        var id = await cmd.ExecuteScalarAsync(ct);

        await ClearSessionStateAsync(session, ct);

        var typeLabel = FormatLeaveType(leaveType);
        return $"✅ {typeLabel}申請が提出されました！\n\n" +
               $"  申請ID：{id}\n" +
               $"  期間：{startDate:yyyy-MM-dd} ～ {endDate:yyyy-MM-dd} ({days}日)\n\n" +
               "承認結果は企業微信でお知らせします。";
    }

    private static string FormatLeaveType(string type) => type switch
    {
        "paid" => "有給休暇",
        "sick" => "病気休暇",
        "special" => "特別休暇",
        "unpaid" => "無給休暇",
        _ => type
    };

    public class LeaveRequestData
    {
        public string LeaveType { get; set; } = "paid";
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public decimal Days { get; set; } = 1;
        public string? Reason { get; set; }
    }

    // ==================== 确认/取消 ====================

    private async Task<string> HandleConfirmAsync(
        string companyCode, EmployeeSession session,
        WeComIntentClassifier.IntentResult intent, CancellationToken ct)
    {
        var contextIntent = intent.Entities.GetValueOrDefault("contextIntent", session.CurrentIntent ?? "");

        return contextIntent switch
        {
            "timesheet.entry" => await ConfirmTimesheetEntryAsync(companyCode, session, ct),
            "timesheet.submit" => await ConfirmTimesheetSubmitAsync(companyCode, session, ct),
            "certificate.apply" => await ConfirmCertificateAsync(companyCode, session, ct),
            "leave.apply" => await ConfirmLeaveAsync(companyCode, session, ct),
            _ => "抱歉，没有待确认的操作。请告诉我您需要什么帮助？"
        };
    }

    private async Task<string> HandleDenyAsync(EmployeeSession session, CancellationToken ct)
    {
        var prevIntent = session.CurrentIntent;
        await ClearSessionStateAsync(session, ct);
        return prevIntent switch
        {
            "timesheet.entry" => "已取消工时录入。如需重新录入，请告诉我具体的上下班时间。",
            "timesheet.submit" => "已取消工时提交。",
            "certificate.apply" => "已取消证明书申请。如需重新申请，请告诉我申请类型。",
            "leave.apply" => "已取消休暇申请。如需重新申请，请告诉我请假日期和类型。",
            _ => "好的，已取消。有其他需要请随时告诉我。"
        };
    }

    // ==================== 确认操作的实际执行 ====================

    private async Task<string> ConfirmTimesheetEntryAsync(
        string companyCode, EmployeeSession session, CancellationToken ct)
    {
        if (session.ResourceId == null) return "系统错误：未关联员工。";

        var state = session.SessionState;
        if (state == null || !state.ContainsKey("pendingEntries"))
            return "没有待确认的工时数据。请重新录入。";

        var entriesNode = state["pendingEntries"];
        if (entriesNode is not JsonArray entriesArr || entriesArr.Count == 0)
            return "没有待确认的工时数据。";

        await using var conn = await _ds.OpenConnectionAsync(ct);
        var savedCount = 0;

        foreach (var entryNode in entriesArr)
        {
            if (entryNode is not JsonObject entry) continue;

            var date = DateOnly.Parse(entry["date"]!.GetValue<string>());
            var startTimeStr = entry["startTime"]?.GetValue<string>();
            var endTimeStr = entry["endTime"]?.GetValue<string>();
            var breakMins = entry["breakMinutes"]?.GetValue<int>() ?? 60;
            var regularHours = entry["regularHours"]?.GetValue<decimal>() ?? 0;
            var overtimeHours = entry["overtimeHours"]?.GetValue<decimal>() ?? 0;
            var isHoliday = entry["isHoliday"]?.GetValue<bool>() ?? false;

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO timesheet_daily_entries 
                (company_code, resource_id, contract_id, entry_date, start_time, end_time, 
                 break_minutes, regular_hours, overtime_hours, holiday_flag, source)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 'wecom')
                ON CONFLICT (company_code, resource_id, entry_date, contract_id)
                DO UPDATE SET 
                    start_time = EXCLUDED.start_time,
                    end_time = EXCLUDED.end_time,
                    break_minutes = EXCLUDED.break_minutes,
                    regular_hours = EXCLUDED.regular_hours,
                    overtime_hours = EXCLUDED.overtime_hours,
                    holiday_flag = EXCLUDED.holiday_flag,
                    source = 'wecom',
                    updated_at = now()";

            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(session.ResourceId.Value);
            cmd.Parameters.AddWithValue(session.ContractId.HasValue ? (object)session.ContractId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue(date.ToDateTime(TimeOnly.MinValue));
            cmd.Parameters.AddWithValue(!string.IsNullOrEmpty(startTimeStr) ? (object)TimeSpan.Parse(startTimeStr) : DBNull.Value);
            cmd.Parameters.AddWithValue(!string.IsNullOrEmpty(endTimeStr) ? (object)TimeSpan.Parse(endTimeStr) : DBNull.Value);
            cmd.Parameters.AddWithValue(breakMins);
            cmd.Parameters.AddWithValue(regularHours);
            cmd.Parameters.AddWithValue(overtimeHours);
            cmd.Parameters.AddWithValue(isHoliday);

            await cmd.ExecuteNonQueryAsync(ct);
            savedCount++;
        }

        await ClearSessionStateAsync(session, ct);

        return $"✅ 已成功录入 {savedCount} 天工时！\n\n" +
               "您可以：\n" +
               "• 回复「查看工时」查看本月工时汇总\n" +
               "• 回复「提交工时」提交本月审批\n" +
               "• 继续录入其他日期的工时";
    }

    private async Task<string> ConfirmTimesheetSubmitAsync(
        string companyCode, EmployeeSession session, CancellationToken ct)
    {
        if (session.ResourceId == null) return "系统错误：未关联员工。";

        var state = session.SessionState;
        var month = state?["month"]?.GetValue<string>() ?? DateTime.Today.ToString("yyyy-MM");
        var totalRegular = state?["totalRegular"]?.GetValue<decimal>() ?? 0;
        var totalOvertime = state?["totalOvertime"]?.GetValue<decimal>() ?? 0;

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // Upsert staffing_timesheet_summary + 设置审批状态
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO staffing_timesheet_summary 
            (company_code, resource_id, year_month, actual_hours, overtime_hours, 
             status, approval_status, submitted_at, submitted_by)
            VALUES ($1, $2, $3, $4, $5, 'confirmed', 'submitted', now(), $2)
            ON CONFLICT (company_code, contract_id, year_month)
            DO UPDATE SET 
                actual_hours = EXCLUDED.actual_hours,
                overtime_hours = EXCLUDED.overtime_hours,
                approval_status = 'submitted',
                submitted_at = now(),
                updated_at = now()";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(session.ResourceId.Value);
        cmd.Parameters.AddWithValue(month);
        cmd.Parameters.AddWithValue(totalRegular);
        cmd.Parameters.AddWithValue(totalOvertime);

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EmployeeGW] 提交工时汇总失败，可能缺少 contract_id");
            // 如果 UNIQUE 约束需要 contract_id，尝试查找有效合约
            return "提交失败：未找到有效的派遣合约。请联系管理员确认您的合约信息。";
        }

        await ClearSessionStateAsync(session, ct);

        return $"✅ {month} 工时已提交审批！\n\n" +
               $"  正常工时：{totalRegular:F1}h\n" +
               $"  加班工时：{totalOvertime:F1}h\n\n" +
               "审批结果会通过企业微信通知您。";
    }

    private async Task<string> ConfirmCertificateAsync(
        string companyCode, EmployeeSession session, CancellationToken ct)
    {
        if (session.EmployeeId == null) return "系统错误：未关联员工。";

        var state = session.SessionState;
        var certType = state?["certificateType"]?.GetValue<string>() ?? "employment";

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO certificate_requests 
            (company_code, payload, employee_id, request_type, status, requested_at, wecom_source)
            VALUES ($1, $2::jsonb, $3, $4, 'pending', now(), TRUE)
            RETURNING id";
        
        var payload = JsonSerializer.Serialize(new
        {
            employeeId = session.EmployeeId.Value,
            type = certType,
            source = "wecom",
            status = "pending"
        });

        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(payload);
        cmd.Parameters.AddWithValue(session.EmployeeId.Value);
        cmd.Parameters.AddWithValue(certType);

        var id = await cmd.ExecuteScalarAsync(ct);

        await ClearSessionStateAsync(session, ct);

        var typeName = certType switch
        {
            "employment" => "在职证明",
            "income" => "收入证明",
            "resignation" => "退职证明",
            "employment_cert" => "就业证明",
            _ => certType
        };

        return $"✅ {typeName}申请已提交！\n\n" +
               $"  申请编号：{id}\n" +
               $"  预计处理时间：1-3 个工作日\n\n" +
               "处理完成后会通过企业微信通知您。";
    }

    // ==================== 发票图片批次聚合 ====================

    /// <summary>
    /// 多图批次聚合机制
    /// 
    /// 问题：用户在微信中一次选择多张发票图片发送时，每张图片是独立消息。
    /// 如果逐张处理，会导致：
    ///   - 同一张多页发票的多张照片被当作不同发票处理（重复记账）
    ///   - 用户收到多条"正在识别"和多条结果回复（体验差）
    /// 
    /// 解决方案：收到第一张图片后等待一个短窗口（5秒），将窗口内到达的所有图片
    /// 聚合为一个批次，统一提交给 AgentKit 处理。
    /// </summary>
    private sealed class InvoiceBatch
    {
        public string CompanyCode { get; init; } = "";
        public string ChannelUserId { get; init; } = "";
        public Guid? SessionUserId { get; init; }
        public HashSet<string> Caps { get; init; } = new();
        public List<InvoiceBatchItem> Items { get; } = new();
        public DateTimeOffset FirstImageAt { get; init; }
        public TaskCompletionSource<string> Completion { get; } = new();
        public CancellationTokenSource Cts { get; } = new();
        public bool IsProcessing { get; set; }
    }

    private sealed class InvoiceBatchItem
    {
        public string FileId { get; init; } = "";
        public string FileName { get; init; } = "";
        public string MimeType { get; init; } = "";
        public string StoredPath { get; init; } = "";
        public string BlobName { get; init; } = "";
        public long FileSize { get; init; }
    }

    /// <summary>用户 → 当前待处理批次（静态，跨请求共享）</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, InvoiceBatch>
        _pendingBatches = new();

    /// <summary>批次聚合等待窗口</summary>
    private static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(5);

    // ==================== 发票识别+自动记账 ====================

    /// <summary>
    /// 处理从 WeChat/LINE 发送的发票图片/文件
    /// 支持多图批次聚合：5秒窗口内的多张图片合并为一个批次
    /// </summary>
    private async Task<string> HandleInvoiceImageAsync(
        string companyCode, EmployeeSession session, WeComMessage message, CancellationToken ct)
    {
        // 1. 无媒体时给提示
        if (string.IsNullOrEmpty(message.MediaId))
        {
            return "🧾 发票识别\n\n请直接拍照或发送发票图片/PDF，我来自动识别并记账。\n\n" +
                   "💡 支持一次发送多张图片，系统会自动批量处理。\n" +
                   "支持格式：图片(JPG/PNG)、PDF";
        }

        // 2. 下载媒体文件
        var mediaResult = await _wecomService.DownloadMediaAsync(message.MediaId, ct);
        if (mediaResult == null)
        {
            return "❌ 文件下载失败，请重新发送。";
        }

        var (fileData, mimeType, fileName) = mediaResult.Value;
        fileName ??= $"invoice_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

        var ext = mimeType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "application/pdf" => ".pdf",
            _ when mimeType.StartsWith("image/") => ".jpg",
            _ => Path.GetExtension(fileName)
        };
        if (string.IsNullOrEmpty(ext)) ext = ".bin";
        if (!fileName.Contains('.')) fileName += ext;

        // 3. 保存到本地 + 上传 Blob
        var fileId = Guid.NewGuid().ToString("n");
        var uploadRoot = Path.Combine(Path.GetTempPath(), "yanxia_uploads");
        Directory.CreateDirectory(uploadRoot);
        var storedPath = Path.Combine(uploadRoot, fileId + ext);
        await File.WriteAllBytesAsync(storedPath, fileData, ct);

        var blobName = $"{companyCode.ToLowerInvariant()}/{DateTime.UtcNow:yyyy/MM/dd}/{fileId}{ext}";
        try
        {
            var blobService = _serviceProvider.GetRequiredService<AzureBlobService>();
            await using var uploadStream = File.OpenRead(storedPath);
            await blobService.UploadAsync(uploadStream, blobName, mimeType, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EmployeeGW] Azure Blob 上传失败（继续使用本地文件）");
            blobName = "";
        }

        var batchItem = new InvoiceBatchItem
        {
            FileId = fileId,
            FileName = fileName,
            MimeType = mimeType,
            StoredPath = storedPath,
            BlobName = blobName,
            FileSize = fileData.Length
        };

        // 4. 批次聚合逻辑
        var batchKey = $"{companyCode}:{message.FromUser}";
        var isFirstInBatch = false;

        var batch = _pendingBatches.AddOrUpdate(
            batchKey,
            // 新建批次（第一张图片）
            _ =>
            {
                isFirstInBatch = true;
                var b = new InvoiceBatch
                {
                    CompanyCode = companyCode,
                    ChannelUserId = message.FromUser,
                    SessionUserId = session.UserId,
                    Caps = new HashSet<string>(session.Caps ?? new List<string>()),
                    FirstImageAt = DateTimeOffset.UtcNow
                };
                b.Items.Add(batchItem);
                return b;
            },
            // 追加到现有批次
            (_, existing) =>
            {
                if (!existing.IsProcessing)
                {
                    existing.Items.Add(batchItem);
                    _logger.LogInformation("[EmployeeGW] 图片加入批次: user={User}, batch_size={Count}",
                        message.FromUser, existing.Items.Count);
                }
                else
                {
                    // 上一批已在处理中，创建新批次
                    isFirstInBatch = true;
                    var b = new InvoiceBatch
                    {
                        CompanyCode = companyCode,
                        ChannelUserId = message.FromUser,
                        SessionUserId = session.UserId,
                        Caps = new HashSet<string>(session.Caps ?? new List<string>()),
                        FirstImageAt = DateTimeOffset.UtcNow
                    };
                    b.Items.Add(batchItem);
                    return b;
                }
                return existing;
            });

        if (isFirstInBatch)
        {
            // 第一张图 → 发送"收到"提示 + 启动定时器
            if (_wecomService.IsConfigured)
            {
                try
                {
                    await _wecomService.SendTextMessageAsync(
                        "🔍 收到发票图片，等待5秒看是否有更多图片...", message.FromUser, ct);
                }
                catch { /* 提示失败不影响 */ }
            }

            // 启动后台定时器，等待窗口到期后统一处理
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(BatchWindow, batch.Cts.Token);
                    await ProcessInvoiceBatchAsync(batchKey, batch);
                }
                catch (OperationCanceledException) { /* 正常取消 */ }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[EmployeeGW] 批次处理异常: user={User}", message.FromUser);
                    batch.Completion.TrySetResult("❌ 批次处理异常，请重试。");
                }
            });

            // 等待批次处理完成并返回结果
            // 注意：RouteIntentAsync 要求返回 reply，所以第一张图要等待整个批次完成
            return await batch.Completion.Task;
        }
        else
        {
            // 后续图片 → 静默加入批次，不重复回复
            _logger.LogInformation("[EmployeeGW] 图片已加入批次（静默）: user={User}, count={Count}",
                message.FromUser, batch.Items.Count);
            return ""; // 空回复 → 不发送消息
        }
    }

    /// <summary>批次窗口到期，统一处理所有图片</summary>
    private async Task ProcessInvoiceBatchAsync(string batchKey, InvoiceBatch batch)
    {
        // 标记正在处理，防止新图片加入
        batch.IsProcessing = true;
        _pendingBatches.TryRemove(batchKey, out _);

        var itemCount = batch.Items.Count;
        _logger.LogInformation("[EmployeeGW] 开始处理发票批次: user={User}, images={Count}",
            batch.ChannelUserId, itemCount);

        // 通知用户开始处理
        if (_wecomService.IsConfigured && itemCount > 1)
        {
            try
            {
                await _wecomService.SendTextMessageAsync(
                    $"📋 共收到 {itemCount} 张图片，正在批量识别记账...",
                    batch.ChannelUserId, CancellationToken.None);
            }
            catch { }
        }
        else if (_wecomService.IsConfigured)
        {
            try
            {
                await _wecomService.SendTextMessageAsync(
                    "🔍 正在识别发票，请稍候...",
                    batch.ChannelUserId, CancellationToken.None);
            }
            catch { }
        }

        try
        {
            var agentKit = _serviceProvider.GetRequiredService<AgentKitService>();
            var apiKey = _config["OpenAI:ApiKey"] ?? _config["Anthropic:ApiKey"] ?? "";
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                batch.Completion.TrySetResult("❌ AI 服务未配置，无法识别发票。");
                return;
            }

            var userCtx = new Auth.UserCtx(
                UserId: batch.SessionUserId?.ToString(),
                Roles: Array.Empty<string>(),
                Caps: batch.Caps.ToArray(),
                DeptId: null,
                EmployeeCode: null,
                UserName: batch.ChannelUserId,
                CompanyCode: batch.CompanyCode
            );

            // 构建文件存储（所有批次图片）
            var fileStore = new Dictionary<string, UploadedFileRecord>();
            foreach (var item in batch.Items)
            {
                fileStore[item.FileId] = new UploadedFileRecord(
                    item.FileName, item.StoredPath, item.MimeType, item.FileSize,
                    DateTimeOffset.UtcNow, batch.CompanyCode, batch.SessionUserId?.ToString(), item.BlobName);
            }

            Guid? sessionId = null;
            var allReplies = new List<string>();

            if (itemCount == 1)
            {
                // 单张图片 → 直接处理
                var item = batch.Items[0];
                var result = await agentKit.ProcessFileAsync(
                    new AgentKitService.AgentFileRequest(
                        SessionId: null,
                        CompanyCode: batch.CompanyCode,
                        UserCtx: userCtx,
                        FileId: item.FileId,
                        FileName: item.FileName,
                        ContentType: item.MimeType,
                        Size: item.FileSize,
                        ApiKey: apiKey,
                        Language: "ja",
                        FileResolver: id => fileStore.GetValueOrDefault(id),
                        ScenarioKey: null,
                        BlobName: item.BlobName),
                    CancellationToken.None);

                allReplies.Add(ExtractAgentReply(result));
            }
            else
            {
                // 多张图片 → 第一张创建会话，后续追加到同一会话
                for (var i = 0; i < batch.Items.Count; i++)
                {
                    var item = batch.Items[i];
                    var userMessage = i == 0
                        ? $"我上传了 {itemCount} 张发票图片，请逐一识别并记账。这是第 1 张。"
                        : $"这是第 {i + 1}/{itemCount} 张发票图片。如果和前面是同一张发票的不同页，请合并处理；如果是不同发票，请分别创建凭证。";

                    var result = await agentKit.ProcessFileAsync(
                        new AgentKitService.AgentFileRequest(
                            SessionId: sessionId, // 复用会话
                            CompanyCode: batch.CompanyCode,
                            UserCtx: userCtx,
                            FileId: item.FileId,
                            FileName: item.FileName,
                            ContentType: item.MimeType,
                            Size: item.FileSize,
                            ApiKey: apiKey,
                            Language: "ja",
                            FileResolver: id => fileStore.GetValueOrDefault(id),
                            ScenarioKey: null,
                            BlobName: item.BlobName,
                            UserMessage: userMessage),
                        CancellationToken.None);

                    sessionId = result.SessionId; // 后续图片复用同一会话
                    var reply = ExtractAgentReply(result);
                    allReplies.Add($"📄 图片 {i + 1}/{itemCount}:\n{reply}");

                    _logger.LogInformation("[EmployeeGW] 批次图片 {Index}/{Total} 处理完成",
                        i + 1, itemCount);
                }
            }

            var finalReply = string.Join("\n\n" + new string('─', 30) + "\n\n", allReplies);

            // 微信消息长度限制，超长时截断
            if (finalReply.Length > 2000)
            {
                finalReply = finalReply[..2000] + "\n\n... (完整内容请在网页版查看)";
            }

            batch.Completion.TrySetResult(finalReply);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EmployeeGW] 批次处理失败: user={User}", batch.ChannelUserId);
            batch.Completion.TrySetResult("❌ 发票识别过程中出现错误，请稍后重试。\n\n您也可以在网页版上传发票。");
        }
        finally
        {
            // 清理本地临时文件
            foreach (var item in batch.Items)
            {
                try { if (File.Exists(item.StoredPath)) File.Delete(item.StoredPath); } catch { }
            }
        }
    }

    /// <summary>从 AgentKit 运行结果中提取可读的回复文本</summary>
    private static string ExtractAgentReply(AgentKitService.AgentRunResult result)
    {
        if (result.Messages == null || result.Messages.Count == 0)
            return "发票已收到，但 AI 未能给出处理结果。请在网页版查看。";

        var sb = new System.Text.StringBuilder();
        foreach (var msg in result.Messages)
        {
            if (msg.Role == "assistant" && !string.IsNullOrWhiteSpace(msg.Content))
            {
                var text = msg.Content;
                if (text.Length > 1500)
                {
                    text = text[..1500] + "\n\n... (完整内容请在网页版查看)";
                }
                sb.AppendLine(text);
            }
        }

        var reply = sb.ToString().Trim();
        return string.IsNullOrEmpty(reply) ? "✅ 发票处理完成，请在网页版查看详细记账结果。" : reply;
    }

    // ==================== 通用问答 ====================

    private async Task<string> HandleGeneralAsync(
        string companyCode, EmployeeSession session, WeComMessage message, CancellationToken ct)
    {
        // 对于无法明确分类的消息，使用 LLM 生成友好回复
        var apiKey = _config["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "您好！我可以帮您：\n\n" +
                   "1. 📋 录入工时 - 「今天 9:00-18:00」\n" +
                   "2. 📊 查看工时 - 「查看本月工时」\n" +
                   "3. 📤 提交工时 - 「提交本月工时」\n" +
                   "4. 💰 查询工资 - 「查看工资」\n" +
                   "5. 📄 申请证明 - 「申请在职证明」\n\n" +
                   "请问有什么可以帮到您？";
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            var requestBody = new
            {
                model = "gpt-4o-mini",
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = @"你是一个友好的企业员工助手，帮助员工处理日常事务。你的能力包括：
1. 工时录入（员工告诉你上下班时间，你记录工时）
2. 工时查询（查看本月已录入的工时）
3. 工时提交审批
4. 工资查询（查看最近的工资明细）
5. 证明书申请（在职证明、收入证明等）

请简洁友好地引导用户使用这些功能。如果用户的问题超出能力范围，请建议联系管理员。
回复使用中文，保持简短亲切。"
                    },
                    new { role = "user", content = message.Content ?? "" }
                },
                temperature = 0.7,
                max_tokens = 300
            };

            var response = await client.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", requestBody, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                   ?? "有什么可以帮到您？";
        }
        catch
        {
            return "您好！我可以帮您录入工时、查看工资、申请证明等。请问有什么需要？";
        }
    }

    // ==================== 辅助方法 ====================

    /// <summary>使用 LLM 从自然语言解析工时数据</summary>
    private async Task<List<TimesheetEntry>?> ParseTimesheetFromTextAsync(
        string companyCode, string text, string scope, CancellationToken ct)
    {
        var apiKey = _config["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var today = DateTime.Today;

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        var systemPrompt = $@"你是一个工时解析器。从用户消息中提取工作时间信息。
今天是 {today:yyyy-MM-dd}（{today:dddd}）。

请以 JSON 数组格式返回，每个元素包含：
- date: ""YYYY-MM-DD""
- startTime: ""HH:mm""  
- endTime: ""HH:mm""
- breakMinutes: 数字（默认60）
- isHoliday: boolean

仅返回 JSON 数组，不要其他文字。如果无法解析，返回空数组 []。

示例输入：""今天9点到18点""
示例输出：[{{""date"":""{today:yyyy-MM-dd}"",""startTime"":""09:00"",""endTime"":""18:00"",""breakMinutes"":60,""isHoliday"":false}}]

示例输入：""本周一到五都是9:00-18:00""
示例输出：(返回周一到周五每天的记录)";

        var requestBody = new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = text }
            },
            temperature = 0.1,
            max_tokens = 500
        };

        try
        {
            var response = await client.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", requestBody, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var content = result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

            if (string.IsNullOrWhiteSpace(content)) return null;
            content = content.Trim();
            if (content.StartsWith("```")) content = content.Split('\n', 3).Length > 1 ? string.Join('\n', content.Split('\n').Skip(1).SkipLast(1)) : content;
            content = content.Trim();

            using var doc = JsonDocument.Parse(content);
            var entries = new List<TimesheetEntry>();

            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                var dateStr = elem.GetProperty("date").GetString();
                var startStr = elem.GetProperty("startTime").GetString();
                var endStr = elem.GetProperty("endTime").GetString();
                var breakMins = elem.TryGetProperty("breakMinutes", out var bm) ? bm.GetInt32() : 60;
                var isHoliday = elem.TryGetProperty("isHoliday", out var ih) && ih.GetBoolean();

                if (dateStr == null || startStr == null || endStr == null) continue;

                var date = DateTime.Parse(dateStr);
                var start = TimeSpan.Parse(startStr);
                var end = TimeSpan.Parse(endStr);
                var workMinutes = (end - start).TotalMinutes - breakMins;
                var regularHours = Math.Min((decimal)workMinutes / 60, 8);
                var overtimeHours = Math.Max((decimal)workMinutes / 60 - 8, 0);

                entries.Add(new TimesheetEntry
                {
                    Date = date,
                    StartTime = start,
                    EndTime = end,
                    BreakMinutes = breakMins,
                    RegularHours = regularHours,
                    OvertimeHours = overtimeHours,
                    IsHoliday = isHoliday
                });
            }

            return entries.Count > 0 ? entries : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EmployeeGW] 工时文本解析失败");
            return null;
        }
    }

    private static string? DetectCertificateType(string text)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(在[职職]|在籍)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return "employment";
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(收入|給与|年収|income)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return "income";
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(退[职職]|離職|退社)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return "resignation";
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(就[业業]|雇用)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return "employment_cert";
        return null;
    }

    private static string FormatApprovalStatus(string status) => status switch
    {
        "draft" => "草稿",
        "submitted" => "审批中",
        "approved" => "已批准 ✅",
        "rejected" => "已退回 ❌",
        _ => status
    };

    // ==================== 会话管理 ====================

    private async Task<EmployeeSession> GetOrCreateSessionAsync(
        string companyCode, string wecomUserId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        // ======== Step 1: 查绑定表 ========
        Guid? boundUserId = null;
        Guid? boundEmployeeId = null;
        bool isBound = false;

        await using var cmdBinding = conn.CreateCommand();
        cmdBinding.CommandText = @"
            SELECT b.user_id, u.employee_id
            FROM employee_channel_bindings b
            JOIN users u ON u.id = b.user_id
            WHERE b.channel = 'wecom' AND b.channel_user_id = $1 AND b.status = 'active'
            LIMIT 1";
        cmdBinding.Parameters.AddWithValue(wecomUserId);

        await using var bindReader = await cmdBinding.ExecuteReaderAsync(ct);
        if (await bindReader.ReadAsync(ct))
        {
            boundUserId = bindReader.GetGuid(0);
            boundEmployeeId = bindReader.IsDBNull(1) ? null : bindReader.GetGuid(1);
            isBound = true;
        }
        await bindReader.CloseAsync();

        // ======== Step 2: 查活跃会话 ========
        await using var cmdFind = conn.CreateCommand();
        cmdFind.CommandText = @"
            SELECT id, employee_id, resource_id, current_intent, session_state
            FROM wecom_employee_sessions
            WHERE company_code = $1 AND wecom_user_id = $2 AND expires_at > now()
            ORDER BY last_active_at DESC LIMIT 1";
        cmdFind.Parameters.AddWithValue(companyCode);
        cmdFind.Parameters.AddWithValue(wecomUserId);

        await using var reader = await cmdFind.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var session = new EmployeeSession
            {
                Id = reader.GetGuid(0),
                CompanyCode = companyCode,
                WeComUserId = wecomUserId,
                UserId = boundUserId,
                EmployeeId = boundEmployeeId ?? (reader.IsDBNull(1) ? null : reader.GetGuid(1)),
                ResourceId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                CurrentIntent = reader.IsDBNull(3) ? null : reader.GetString(3),
                SessionState = reader.IsDBNull(4) ? null : JsonNode.Parse(reader.GetString(4)) as JsonObject,
                IsBound = isBound
            };
            await reader.CloseAsync();

            // 刷新过期时间
            await using var cmdRefresh = conn.CreateCommand();
            cmdRefresh.CommandText = @"
                UPDATE wecom_employee_sessions 
                SET last_active_at = now(), expires_at = now() + interval '30 minutes', updated_at = now()
                WHERE id = $1";
            cmdRefresh.Parameters.AddWithValue(session.Id);
            await cmdRefresh.ExecuteNonQueryAsync(ct);

            // 加载权限
            if (isBound && boundUserId.HasValue)
            {
                session.Caps = await LoadUserCapsAsync(conn, boundUserId.Value, companyCode, ct);
                // 同时解析 resourceId（如果还没有）
                if (session.ResourceId == null && session.EmployeeId.HasValue)
                {
                    await using var cmdRes = conn.CreateCommand();
                    cmdRes.CommandText = @"SELECT id FROM stf_resources WHERE company_code = $1 AND employee_id = $2 LIMIT 1";
                    cmdRes.Parameters.AddWithValue(companyCode);
                    cmdRes.Parameters.AddWithValue(session.EmployeeId.Value);
                    var resObj = await cmdRes.ExecuteScalarAsync(ct);
                    if (resObj is Guid rid) session.ResourceId = rid;
                }
            }

            return session;
        }
        await reader.CloseAsync();

        // ======== Step 3: 创建新会话 ========
        Guid? resourceId = null;
        Guid? contractId = null;

        if (isBound && boundEmployeeId.HasValue)
        {
            // 通过 employee_id 查找 resource
            await using var cmdRes = conn.CreateCommand();
            cmdRes.CommandText = @"SELECT id FROM stf_resources WHERE company_code = $1 AND employee_id = $2 LIMIT 1";
            cmdRes.Parameters.AddWithValue(companyCode);
            cmdRes.Parameters.AddWithValue(boundEmployeeId.Value);
            var resObj = await cmdRes.ExecuteScalarAsync(ct);
            if (resObj is Guid rid) resourceId = rid;
        }

        // 查找有效合约
        if (resourceId != null)
        {
            await using var cmdContract = conn.CreateCommand();
            cmdContract.CommandText = @"
                SELECT id FROM stf_contracts 
                WHERE company_code = $1 AND payload->>'resource_id' = $2 AND payload->>'status' = 'active'
                ORDER BY payload->>'start_date' DESC LIMIT 1";
            cmdContract.Parameters.AddWithValue(companyCode);
            cmdContract.Parameters.AddWithValue(resourceId.Value.ToString());
            var cid = await cmdContract.ExecuteScalarAsync(ct);
            contractId = cid is Guid g ? g : null;
        }

        // 插入新会话
        await using var cmdInsert = conn.CreateCommand();
        cmdInsert.CommandText = @"
            INSERT INTO wecom_employee_sessions 
            (company_code, wecom_user_id, employee_id, resource_id, session_state)
            VALUES ($1, $2, $3, $4, $5::jsonb)
            RETURNING id";
        cmdInsert.Parameters.AddWithValue(companyCode);
        cmdInsert.Parameters.AddWithValue(wecomUserId);
        cmdInsert.Parameters.AddWithValue(boundEmployeeId.HasValue ? (object)boundEmployeeId.Value : DBNull.Value);
        cmdInsert.Parameters.AddWithValue(resourceId.HasValue ? (object)resourceId.Value : DBNull.Value);
        cmdInsert.Parameters.AddWithValue("{}");

        var newId = (Guid)(await cmdInsert.ExecuteScalarAsync(ct))!;

        var newSession = new EmployeeSession
        {
            Id = newId,
            CompanyCode = companyCode,
            WeComUserId = wecomUserId,
            UserId = boundUserId,
            EmployeeId = boundEmployeeId,
            ResourceId = resourceId,
            ContractId = contractId,
            CurrentIntent = null,
            SessionState = null,
            IsBound = isBound
        };

        // 加载权限
        if (isBound && boundUserId.HasValue)
        {
            newSession.Caps = await LoadUserCapsAsync(conn, boundUserId.Value, companyCode, ct);
        }

        return newSession;
    }

    /// <summary>
    /// 加载用户的所有 AI 能力（从 role_caps 表）
    /// </summary>
    private static async Task<List<string>> LoadUserCapsAsync(
        NpgsqlConnection conn, Guid userId, string companyCode, CancellationToken ct)
    {
        var caps = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT rc.cap 
            FROM role_caps rc
            JOIN user_roles ur ON ur.role_id = rc.role_id
            WHERE ur.user_id = $1 AND rc.cap LIKE 'ai.%'";
        cmd.Parameters.AddWithValue(userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            caps.Add(reader.GetString(0));
        }
        return caps;
    }

    private async Task SaveMessageAsync(
        Guid sessionId, string companyCode, string wecomUserId,
        string direction, string messageType, string? content, string? intent,
        JsonObject? metadata, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO wecom_employee_messages 
            (session_id, company_code, wecom_user_id, direction, message_type, content, intent, metadata)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb)";
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(wecomUserId);
        cmd.Parameters.AddWithValue(direction);
        cmd.Parameters.AddWithValue(messageType);
        cmd.Parameters.AddWithValue(content ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue(intent ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue(metadata != null ? metadata.ToJsonString() : "{}");

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpdateSessionAsync(
        EmployeeSession session, WeComIntentClassifier.IntentResult intent, CancellationToken ct)
    {
        // 只有高置信度的意图才更新当前意图（避免一般问题覆盖上下文）
        if (intent.Confidence < 0.5m || intent.Intent == "general.question" || intent.Intent == "unknown")
            return;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE wecom_employee_sessions 
            SET current_intent = $2, last_active_at = now(), updated_at = now()
            WHERE id = $1";
        cmd.Parameters.AddWithValue(session.Id);
        cmd.Parameters.AddWithValue(intent.Intent);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpdateSessionStateAsync(
        EmployeeSession session, string intent, JsonObject state, CancellationToken ct)
    {
        session.CurrentIntent = intent;
        session.SessionState = state;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE wecom_employee_sessions 
            SET current_intent = $2, session_state = $3::jsonb, 
                last_active_at = now(), expires_at = now() + interval '30 minutes', updated_at = now()
            WHERE id = $1";
        cmd.Parameters.AddWithValue(session.Id);
        cmd.Parameters.AddWithValue(intent);
        cmd.Parameters.AddWithValue(state.ToJsonString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ClearSessionStateAsync(EmployeeSession session, CancellationToken ct)
    {
        session.CurrentIntent = null;
        session.SessionState = null;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE wecom_employee_sessions 
            SET current_intent = NULL, session_state = '{}', updated_at = now()
            WHERE id = $1";
        cmd.Parameters.AddWithValue(session.Id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ==================== 内部类型 ====================

    public class EmployeeSession
    {
        public Guid Id { get; set; }
        public string CompanyCode { get; set; } = "";
        public string WeComUserId { get; set; } = "";
        public Guid? UserId { get; set; }          // → users.id
        public Guid? EmployeeId { get; set; }
        public Guid? ResourceId { get; set; }
        public Guid? ContractId { get; set; }
        public string? CurrentIntent { get; set; }
        public JsonObject? SessionState { get; set; }
        public List<string> Caps { get; set; } = new();  // AI capabilities
        public bool IsBound { get; set; }           // 是否已绑定系统账号
    }

    public class TimesheetEntry
    {
        public DateTime Date { get; set; }
        public TimeSpan? StartTime { get; set; }
        public TimeSpan? EndTime { get; set; }
        public int BreakMinutes { get; set; } = 60;
        public decimal RegularHours { get; set; }
        public decimal OvertimeHours { get; set; }
        public bool IsHoliday { get; set; }
    }

    public record EmployeeGatewayResponse(string Intent, string Reply, Guid? SessionId);
}
