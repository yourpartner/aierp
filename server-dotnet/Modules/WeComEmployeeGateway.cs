using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
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
        TimesheetAiParser timesheetParser)
    {
        _logger = logger;
        _ds = ds;
        _config = config;
        _intentClassifier = intentClassifier;
        _wecomService = wecomService;
        _httpClientFactory = httpClientFactory;
        _timesheetParser = timesheetParser;
    }

    /// <summary>
    /// 处理来自企业微信内部员工的消息（主入口）
    /// </summary>
    public async Task<EmployeeGatewayResponse> HandleEmployeeMessageAsync(
        string companyCode, WeComMessage message, CancellationToken ct)
    {
        var userId = message.FromUser;
        _logger.LogInformation("[EmployeeGW] 收到员工消息: user={User}, type={Type}, content={Content}",
            userId, message.MsgType, message.Content?.Length > 50 ? message.Content[..50] + "..." : message.Content);

        try
        {
            // 1. 获取或创建会话 + 关联员工信息
            var session = await GetOrCreateSessionAsync(companyCode, userId, ct);

            // 2. 保存入站消息
            await SaveMessageAsync(session.Id, companyCode, userId, "in", message.MsgType,
                message.Content, null, null, ct);

            // 3. 意图分类
            var intent = await _intentClassifier.ClassifyAsync(
                message.Content ?? "", message.MsgType, session.CurrentIntent, ct);

            _logger.LogInformation("[EmployeeGW] 意图分类: intent={Intent}, confidence={Confidence:F2}",
                intent.Intent, intent.Confidence);

            // 4. 路由到对应处理器
            var reply = await RouteIntentAsync(companyCode, session, message, intent, ct);

            // 5. 保存出站消息
            await SaveMessageAsync(session.Id, companyCode, userId, "out", "text",
                reply, intent.Intent, null, ct);

            // 6. 更新会话状态
            await UpdateSessionAsync(session, intent, ct);

            // 7. 发送回复
            if (_wecomService.IsConfigured)
            {
                await _wecomService.SendTextMessageAsync(reply, userId, ct);
            }

            return new EmployeeGatewayResponse(intent.Intent, reply, session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EmployeeGW] 处理消息失败: user={User}", userId);
            var errorReply = "抱歉，系统暂时出现了问题，请稍后再试。如有紧急事项，请联系管理员。";
            if (_wecomService.IsConfigured)
            {
                try { await _wecomService.SendTextMessageAsync(errorReply, userId, ct); } catch { /* 忽略 */ }
            }
            return new EmployeeGatewayResponse("error", errorReply, null);
        }
    }

    /// <summary>根据意图路由到对应处理器</summary>
    private async Task<string> RouteIntentAsync(
        string companyCode, EmployeeSession session, WeComMessage message,
        WeComIntentClassifier.IntentResult intent, CancellationToken ct)
    {
        return intent.Intent switch
        {
            "timesheet.entry" => await HandleTimesheetEntryAsync(companyCode, session, message, intent, ct),
            "timesheet.upload" => await HandleTimesheetUploadAsync(companyCode, session, message, intent, ct),
            "timesheet.query" => await HandleTimesheetQueryAsync(companyCode, session, ct),
            "timesheet.submit" => await HandleTimesheetSubmitAsync(companyCode, session, ct),
            "payroll.query" => await HandlePayrollQueryAsync(companyCode, session, ct),
            "certificate.apply" => await HandleCertificateApplyAsync(companyCode, session, message, intent, ct),
            "leave.query" => await HandleLeaveAsync(companyCode, session, message, intent, ct),
            "confirm" => await HandleConfirmAsync(companyCode, session, intent, ct),
            "deny" => await HandleDenyAsync(session, ct),
            _ => await HandleGeneralAsync(companyCode, session, message, ct)
        };
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

        // 查找活跃会话
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
                EmployeeId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
                ResourceId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                CurrentIntent = reader.IsDBNull(3) ? null : reader.GetString(3),
                SessionState = reader.IsDBNull(4) ? null : JsonNode.Parse(reader.GetString(4)) as JsonObject
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

            return session;
        }
        await reader.CloseAsync();

        // 创建新会话 - 同时查找员工关联
        Guid? employeeId = null;
        Guid? resourceId = null;
        Guid? contractId = null;

        // Strategy 1: 通过企业微信 userId 查找员工（employees 表）
        await using var cmdEmp = conn.CreateCommand();
        cmdEmp.CommandText = @"
            SELECT e.id as employee_id, r.id as resource_id
            FROM employees e
            LEFT JOIN stf_resources r ON r.employee_id = e.id AND r.company_code = e.company_code
            WHERE e.company_code = $1 AND (
                e.payload->>'wecom_user_id' = $2 
                OR e.payload->>'email' = $2
                OR e.payload->>'userId' = $2
            )
            LIMIT 1";
        cmdEmp.Parameters.AddWithValue(companyCode);
        cmdEmp.Parameters.AddWithValue(wecomUserId);

        await using var empReader = await cmdEmp.ExecuteReaderAsync(ct);
        if (await empReader.ReadAsync(ct))
        {
            employeeId = empReader.IsDBNull(0) ? null : empReader.GetGuid(0);
            resourceId = empReader.IsDBNull(1) ? null : empReader.GetGuid(1);
        }
        await empReader.CloseAsync();

        // Strategy 2: 回退到 users 表查找（通过 employee_code 或 wecom_user_id 匹配）
        if (employeeId == null && resourceId == null)
        {
            await using var cmdUser = conn.CreateCommand();
            cmdUser.CommandText = @"
                SELECT u.employee_id, r.id as resource_id
                FROM users u
                LEFT JOIN stf_resources r ON r.employee_id = u.employee_id AND r.company_code = u.company_code
                WHERE u.company_code = $1 AND (
                    u.employee_code = $2
                    OR u.id::text = $2
                )
                LIMIT 1";
            cmdUser.Parameters.AddWithValue(companyCode);
            cmdUser.Parameters.AddWithValue(wecomUserId);

            await using var userReader = await cmdUser.ExecuteReaderAsync(ct);
            if (await userReader.ReadAsync(ct))
            {
                employeeId = userReader.IsDBNull(0) ? null : userReader.GetGuid(0);
                resourceId = userReader.IsDBNull(1) ? null : userReader.GetGuid(1);
            }
            await userReader.CloseAsync();
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
        cmdInsert.Parameters.AddWithValue(employeeId.HasValue ? (object)employeeId.Value : DBNull.Value);
        cmdInsert.Parameters.AddWithValue(resourceId.HasValue ? (object)resourceId.Value : DBNull.Value);
        cmdInsert.Parameters.AddWithValue("{}");

        var newId = (Guid)(await cmdInsert.ExecuteScalarAsync(ct))!;

        return new EmployeeSession
        {
            Id = newId,
            CompanyCode = companyCode,
            WeComUserId = wecomUserId,
            EmployeeId = employeeId,
            ResourceId = resourceId,
            ContractId = contractId,
            CurrentIntent = null,
            SessionState = null
        };
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
        public Guid? EmployeeId { get; set; }
        public Guid? ResourceId { get; set; }
        public Guid? ContractId { get; set; }
        public string? CurrentIntent { get; set; }
        public JsonObject? SessionState { get; set; }
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
