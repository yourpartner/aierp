using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

public sealed class PayrollExecutionException : Exception
{
    public int StatusCode { get; }
    public object Payload { get; }

    public PayrollExecutionException(int statusCode, object payload, string message) : base(message)
    {
        StatusCode = statusCode;
        Payload = payload;
    }
}

internal sealed record PayrollManualRunRequest(
    IReadOnlyList<Guid> EmployeeIds, 
    string Month, 
    Guid? PolicyId, 
    bool Debug,
    Dictionary<string, ManualWorkHoursInput>? ManualWorkHours = null);

/// <summary>手动输入的工时数据</summary>
public sealed record ManualWorkHoursInput(decimal TotalHours, decimal HourlyRate);

public sealed record PayrollExecutionResult(
    List<JsonObject> PayrollSheet,
    List<JsonObject> AccountingDraft,
    JsonArray? Trace,
    string? EmployeeCode,
    string? EmployeeName,
    string? DepartmentCode,
    string? DepartmentName,
    decimal NetAmount,
    JsonObject? WorkHours,
    JsonArray? Warnings);

public static class HrPayrollModule
{
    private static readonly JsonSerializerOptions PayrollJsonSerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] CommuteKeywords = new[] { "通勤", "通勤手当", "交通手当", "交通費", "交通费", "交通補助", "交通补贴" };
    // 时薪关键词已移至 Policy 规则的 activation.salaryDescriptionContains 中定义
    // 后端不再硬编码时薪判断逻辑
    private static readonly Dictionary<string, string> PayrollItemDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BASE"] = "基本給",
        ["COMMUTE"] = "交通手当",
        ["HEALTH_INS"] = "健康保険",
        ["PENSION"] = "厚生年金",
        ["EMP_INS"] = "雇用保険",
        ["WHT"] = "源泉徴収税",
        ["OVERTIME_STD"] = "時間外手当",
        ["OVERTIME_60"] = "時間外(60h超)",
        ["HOLIDAY_PAY"] = "休日労働手当",
        ["LATE_NIGHT_PAY"] = "深夜手当",
        ["ABSENCE_DEDUCT"] = "欠勤控除"
    };

    private static readonly HashSet<string> PayrollAmountKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "basesalarymonth",
        "commuteallowance",
        "salarytotal",
        "healthbase",
        "pensionbase",
        "hourlyrate"
    };

    private sealed record JournalAccountInstruction(string? DebitName, string? CreditName);

    private static readonly Dictionary<string, string[]> JournalRuleKeywordMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wages"] = new[] { "基本給", "給与", "給料", "給與", "手当", "手當", "残業", "加班" },
        ["health"] = new[] { "社会保険", "健康保険", "社保" },
        ["pension"] = new[] { "厚生年金", "年金" },
        ["employment"] = new[] { "雇用保険", "雇佣保险" },
        ["withholding"] = new[] { "源泉", "月額表", "甲欄" }
    };

    private static readonly Dictionary<string, string> AccountNameToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["給与手当"] = "832",
        ["給料"] = "832",
        ["給與"] = "832",
        ["人件費"] = "832",
        ["基本給"] = "832",
        ["未払費用"] = "315",
        ["未払金"] = "314",
        ["社会保険預り金"] = "3181",
        ["社会保険預かり金"] = "3181",
        ["健康保険預り金"] = "3181",
        ["厚生年金預り金"] = "3182",
        ["厚生年金預かり金"] = "3182",
        ["雇用保険預り金"] = "3183",
        ["雇用保険預かり金"] = "3183",
        ["源泉所得税預り金"] = "3184",
        ["源泉所得税預かり金"] = "3184"
    };

    // 使用 JapaneseHolidayService 单例来获取日本节假日

    private static readonly HashSet<string> LockedTimesheetStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "submitted",
        "approved",
        "confirmed",
        "locked",
        "reviewed",
        "final"
    };

    private sealed class CompanyWorkSettings
    {
        public TimeSpan WorkdayStart { get; init; } = TimeSpan.FromHours(9);
        public TimeSpan WorkdayEnd { get; init; } = TimeSpan.FromHours(18);
        public int LunchMinutes { get; init; } = 60;
        public decimal StandardDailyHours { get; init; } = 8m;
        public decimal OvertimeThresholdHours { get; init; } = 60m;
    }

    private readonly struct WorkHourSummary
    {
        public WorkHourSummary(
            decimal totalHours,
            decimal regularHours,
            decimal overtime125Hours,
            decimal overtime150Hours,
            decimal holidayHours,
            decimal lateNightHours,
            decimal absenceHours,
            decimal standardDailyHours,
            decimal overtimeThresholdHours,
            int sourceEntries,
            int lockedEntries)
        {
            TotalHours = totalHours;
            RegularHours = regularHours;
            Overtime125Hours = overtime125Hours;
            Overtime150Hours = overtime150Hours;
            HolidayHours = holidayHours;
            LateNightHours = lateNightHours;
            AbsenceHours = absenceHours;
            StandardDailyHours = standardDailyHours;
            OvertimeThresholdHours = overtimeThresholdHours;
            SourceEntries = sourceEntries;
            LockedEntries = lockedEntries;
        }

        public decimal TotalHours { get; }
        public decimal RegularHours { get; }
        public decimal Overtime125Hours { get; }
        public decimal Overtime150Hours { get; }
        public decimal HolidayHours { get; }
        public decimal LateNightHours { get; }
        public decimal AbsenceHours { get; }
        public decimal StandardDailyHours { get; }
        public decimal OvertimeThresholdHours { get; }
        public int SourceEntries { get; }
        public int LockedEntries { get; }
        public bool HasData => SourceEntries > 0;

        public decimal GetScalar(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return 0m;
            switch (key.Trim().ToLowerInvariant())
            {
                case "total":
                case "totalhours":
                    return TotalHours;
                case "regular":
                case "regularhours":
                    return RegularHours;
                case "overtime":
                case "overtime125":
                case "overtimehours":
                    return Overtime125Hours;
                case "overtime60":
                case "overtime150":
                    return Overtime150Hours;
                case "holiday":
                case "holidayhours":
                    return HolidayHours;
                case "latenight":
                case "latenighthours":
                    return LateNightHours;
                case "absence":
                case "shortage":
                    return AbsenceHours;
                case "standarddaily":
                case "standardhours":
                case "standarddailyhours":
                    return StandardDailyHours;
                case "threshold60":
                case "overtimethreshold":
                    return OvertimeThresholdHours;
                default:
                    return 0m;
            }
        }

        public JsonObject ToJson()
        {
            return new JsonObject
            {
                ["totalHours"] = TotalHours,
                ["regularHours"] = RegularHours,
                ["overtimeHours"] = Overtime125Hours,
                ["overtime60Hours"] = Overtime150Hours,
                ["holidayHours"] = HolidayHours,
                ["lateNightHours"] = LateNightHours,
                ["absenceHours"] = AbsenceHours,
                ["standardDailyHours"] = StandardDailyHours,
                ["overtimeThresholdHours"] = OvertimeThresholdHours,
                ["sourceEntries"] = SourceEntries,
                ["lockedEntries"] = LockedEntries
            };
        }

        public static WorkHourSummary Empty => new(0, 0, 0, 0, 0, 0, 0, 8m, 60m, 0, 0);

        /// <summary>
        /// 生成月标准工时（无加班无欠勤），用于月薪人员缺少timesheet时按标准计算
        /// </summary>
        public static WorkHourSummary CreateStandardMonthly(CompanyWorkSettings settings, string month)
        {
            // 计算该月的工作日数（排除周末和日本法定节假日）
            var workDays = CountWorkDaysInMonth(month);
            var monthlyHours = workDays * settings.StandardDailyHours;
            return new WorkHourSummary(
                totalHours: monthlyHours,
                regularHours: monthlyHours,
                overtime125Hours: 0,
                overtime150Hours: 0,
                holidayHours: 0,
                lateNightHours: 0,
                absenceHours: 0,
                standardDailyHours: settings.StandardDailyHours,
                overtimeThresholdHours: settings.OvertimeThresholdHours,
                sourceEntries: workDays, // 虚拟条目数等于工作日数
                lockedEntries: workDays);
        }

        private static int CountWorkDaysInMonth(string month)
        {
            if (!DateTime.TryParse(month + "-01", out var firstDay))
            {
                // 默认按20个工作日计算
                return 20;
            }
            var daysInMonth = DateTime.DaysInMonth(firstDay.Year, firstDay.Month);
            int workDays = 0;
            for (int d = 1; d <= daysInMonth; d++)
            {
                var date = new DateOnly(firstDay.Year, firstDay.Month, d);
                if (!IsHoliday(date))
                {
                    workDays++;
                }
            }
            return workDays > 0 ? workDays : 20;
        }

        private static bool IsHoliday(DateOnly date)
            => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
               || JapaneseHolidayService.Instance.IsNationalHoliday(date);
    }

    private readonly struct FormulaEvalResult
    {
        public FormulaEvalResult(decimal value, decimal? rateUsed, decimal? baseUsed, string? lawVer, string? lawNote)
        {
            Value = value;
            RateUsed = rateUsed;
            BaseUsed = baseUsed;
            LawVer = lawVer;
            LawNote = lawNote;
        }

        public decimal Value { get; }
        public decimal? RateUsed { get; }
        public decimal? BaseUsed { get; }
        public string? LawVer { get; }
        public string? LawNote { get; }
    }

    private readonly struct WithholdingBracket
    {
        public WithholdingBracket(decimal min, decimal? max, decimal rate, decimal deduction, string version)
        {
            Min = min;
            Max = max;
            Rate = rate;
            Deduction = deduction;
            Version = version;
        }

        public decimal Min { get; }
        public decimal? Max { get; }
        public decimal Rate { get; }
        public decimal Deduction { get; }
        public string Version { get; }
    }

    private readonly struct JournalLine
    {
        public JournalLine(int lineNo, string accountCode, string drCr, decimal amount)
        {
            LineNo = lineNo;
            AccountCode = accountCode;
            DrCr = drCr;
            Amount = amount;
        }

        public int LineNo { get; }
        public string AccountCode { get; }
        public string DrCr { get; }
        public decimal Amount { get; }
    }

    private sealed record DepartmentInfo(string? Code, string? Name);

    private static bool IsDeductionItem(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        return code.Equals("HEALTH_INS", StringComparison.OrdinalIgnoreCase)
            || code.Equals("PENSION", StringComparison.OrdinalIgnoreCase)
            || code.Equals("EMP_INS", StringComparison.OrdinalIgnoreCase)
            || code.Equals("WHT", StringComparison.OrdinalIgnoreCase)
            || code.Equals("ABSENCE_DEDUCT", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadJsonString(JsonObject obj, string property)
    {
        if (!obj.TryGetPropertyValue(property, out var node) || node is not JsonValue value) return null;
        return value.TryGetValue<string>(out var text) ? text : null;
    }

    private static decimal ReadJsonDecimal(JsonObject? obj, string property)
    {
        if (obj is null) return 0m;
        if (!obj.TryGetPropertyValue(property, out var node) || node is not JsonValue value) return 0m;
        if (value.TryGetValue<decimal>(out var dec)) return dec;
        if (value.TryGetValue<double>(out var dbl)) return Convert.ToDecimal(dbl);
        if (value.TryGetValue<string>(out var str) && decimal.TryParse(str, out var parsed)) return parsed;
        return 0m;
    }

    private static decimal SumSheetAmount(IEnumerable<JsonObject> sheet, params string[] codes)
    {
        var set = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
        decimal sum = 0m;
        foreach (var entry in sheet)
        {
            var code = ReadJsonString(entry, "itemCode");
            if (code is null || !set.Contains(code)) continue;
            sum += ReadJsonDecimal(entry, "amount");
        }
        return sum;
    }

    private static List<JournalLine> BuildJournalLinesFromDsl(JsonElement journalRulesEl, List<JsonObject> sheetOut)
    {
        var amountByItem = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in sheetOut)
        {
            var code = ReadJsonString(entry, "itemCode");
            if (string.IsNullOrWhiteSpace(code)) continue;
            var amount = ReadJsonDecimal(entry, "amount");
            if (amount == 0m) continue;
            if (amountByItem.TryGetValue(code, out var existing)) amountByItem[code] = existing + amount;
            else amountByItem[code] = amount;
        }

        var aggregated = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        void Acc(string drcr, string account, decimal amount)
        {
            if (string.IsNullOrWhiteSpace(account)) return;
            if (amount <= 0m) return;
            var normalizedAccount = account.Trim();
            var delta = string.Equals(drcr, "DR", StringComparison.OrdinalIgnoreCase) ? amount : -amount;
            if (aggregated.TryGetValue(normalizedAccount, out var existing))
            {
                aggregated[normalizedAccount] = existing + delta;
            }
            else
            {
                aggregated[normalizedAccount] = delta;
                order.Add(normalizedAccount);
            }
        }

        foreach (var rule in journalRulesEl.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object) continue;
            var debitAccount = rule.TryGetProperty("debitAccount", out var da) && da.ValueKind == JsonValueKind.String ? da.GetString() : null;
            var creditAccount = rule.TryGetProperty("creditAccount", out var ca) && ca.ValueKind == JsonValueKind.String ? ca.GetString() : null;
            if (string.IsNullOrWhiteSpace(debitAccount) || string.IsNullOrWhiteSpace(creditAccount)) continue;
            if (!rule.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array) continue;
            decimal sum = 0m;
            foreach (var itemNode in itemsEl.EnumerateArray())
            {
                string? code = null;
                decimal sign = 1m;
                if (itemNode.ValueKind == JsonValueKind.String)
                {
                    code = itemNode.GetString();
                }
                else if (itemNode.ValueKind == JsonValueKind.Object)
                {
                    if (itemNode.TryGetProperty("code", out var ce) && ce.ValueKind == JsonValueKind.String)
                    {
                        code = ce.GetString();
                    }
                    if (itemNode.TryGetProperty("sign", out var se) && se.ValueKind == JsonValueKind.Number)
                    {
                        sign = se.TryGetDecimal(out var sd) ? sd : Convert.ToDecimal(se.GetDouble());
                    }
                }
                if (string.IsNullOrWhiteSpace(code)) continue;
                if (!amountByItem.TryGetValue(code, out var amt)) continue;
                sum += amt * sign;
            }
            if (sum <= 0m) continue;
            Acc("DR", debitAccount!, sum);
            Acc("CR", creditAccount!, sum);
        }

        var journal = new List<JournalLine>();
        int lineNo = 1;
        foreach (var account in order)
        {
            if (!aggregated.TryGetValue(account, out var net) || net == 0m) continue;
            if (net > 0m)
            {
                journal.Add(new JournalLine(lineNo++, account, "DR", net));
            }
            else
            {
                journal.Add(new JournalLine(lineNo++, account, "CR", Math.Abs(net)));
            }
        }
        return journal;
    }

    private static string? ExtractPolicyText(JsonElement policyRoot)
    {
        if (policyRoot.ValueKind != JsonValueKind.Object) return null;
        if (policyRoot.TryGetProperty("companyText", out var ct) && ct.ValueKind == JsonValueKind.String) return ct.GetString();
        if (policyRoot.TryGetProperty("nlText", out var nt) && nt.ValueKind == JsonValueKind.String) return nt.GetString();
        if (policyRoot.TryGetProperty("text", out var tt) && tt.ValueKind == JsonValueKind.String) return tt.GetString();
        return null;
    }

    private static string? CombineTextSegments(params string?[] segments)
    {
        var valid = segments?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (valid is null || valid.Length == 0) return null;
        return string.Join("\n", valid);
    }

    private static List<object> BuildJournalRulesFromText(string? combinedText)
    {
        var list = new List<object>();
        if (string.IsNullOrWhiteSpace(combinedText)) return list;

        bool Has(params string[] tokens) => ContainsAny(combinedText, tokens);
        bool HasKeywords(string[] tokens) => ContainsAny(combinedText, tokens);
        var instructions = ParseJournalInstructions(combinedText);

        string Debit(string rule, string defaultCode) => ResolveAccountCodeForRule(rule, defaultCode, instructions, true);
        string Credit(string rule, string defaultCode) => ResolveAccountCodeForRule(rule, defaultCode, instructions, false);

        var wageItems = new List<object>();
        if (Has("基本給", "基本给", "給与", "给与", "給與", "給料", "月給", "月给")) wageItems.Add(new { code = "BASE" });
        if (HasKeywords(CommuteKeywords)) wageItems.Add(new { code = "COMMUTE" });
        if (Has("残業", "時間外", "加班")) wageItems.Add(new { code = "OVERTIME_STD" });
        if (Has("60時間", "６０時間", "60h", "60小时")) wageItems.Add(new { code = "OVERTIME_60" });
        if (Has("休日", "祝日", "节假日", "節假日", "法定休日")) wageItems.Add(new { code = "HOLIDAY_PAY" });
        if (Has("深夜", "22時", "22点", "22點", "late night", "夜間")) wageItems.Add(new { code = "LATE_NIGHT_PAY" });
        if (Has("欠勤", "欠勤控除", "勤怠控除", "遅刻", "早退", "工时不足", "工時不足"))
            wageItems.Add(new { code = "ABSENCE_DEDUCT", sign = -1m });
        if (wageItems.Count > 0)
        {
            var debit = Debit("wages", "832");
            var credit = Credit("wages", "315");
            list.Add(new
            {
                name = "wages",
                debitAccount = debit,
                creditAccount = credit,
                items = wageItems,
                description = FormatJournalDescription(debit, credit)
            });
        }
        if (Has("社会保険", "健康保険", "社保"))
        {
            var debit = Debit("health", "3181");
            var credit = Credit("health", "315");
            list.Add(new
            {
                name = "health",
                debitAccount = debit,
                creditAccount = credit,
                items = new[] { new { code = "HEALTH_INS" } },
                description = FormatJournalDescription(debit, credit)
            });
        }
        if (Has("厚生年金", "年金"))
        {
            var debit = Debit("pension", "3182");
            var credit = Credit("pension", "315");
            list.Add(new
            {
                name = "pension",
                debitAccount = debit,
                creditAccount = credit,
                items = new[] { new { code = "PENSION" } },
                description = FormatJournalDescription(debit, credit)
            });
        }
        if (Has("雇用保険", "雇佣保险"))
        {
            var debit = Debit("employment", "3183");
            var credit = Credit("employment", "315");
            list.Add(new
            {
                name = "employment",
                debitAccount = debit,
                creditAccount = credit,
                items = new[] { new { code = "EMP_INS" } },
                description = FormatJournalDescription(debit, credit)
            });
        }
        if (Has("源泉", "月額表", "甲欄"))
        {
            var debit = Debit("withholding", "3184");
            var credit = Credit("withholding", "315");
            list.Add(new
            {
                name = "withholding",
                debitAccount = debit,
                creditAccount = credit,
                items = new[] { new { code = "WHT" } },
                description = FormatJournalDescription(debit, credit)
            });
        }
        return list;
    }

    private static Dictionary<string, JournalAccountInstruction> ParseJournalInstructions(string? combinedText)
    {
        var map = new Dictionary<string, JournalAccountInstruction>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(combinedText)) return map;
        var lines = combinedText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (!line.Contains("借方", StringComparison.OrdinalIgnoreCase) || !line.Contains("貸方", StringComparison.OrdinalIgnoreCase)) continue;
            string subject = line;
            foreach (var delimiter in new[] { '：', ':', '。' })
            {
                var idx = line.IndexOf(delimiter);
                if (idx > 0)
                {
                    subject = line[..idx];
                    break;
                }
            }
            var ruleKey = IdentifyJournalRule(subject);
            ruleKey ??= IdentifyJournalRule(line);
            if (ruleKey is null) continue;
            var debitName = ExtractAccountNameFromLine(line, "借方");
            var creditName = ExtractAccountNameFromLine(line, "貸方");
            map[ruleKey] = new JournalAccountInstruction(debitName, creditName);
        }
        return map;
    }

    private static string? IdentifyJournalRule(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var kvp in JournalRuleKeywordMap)
        {
            foreach (var keyword in kvp.Value)
            {
                if (text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return kvp.Key;
                }
            }
        }
        return null;
    }

    private static string? ExtractAccountNameFromLine(string line, string marker)
    {
        var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        idx += marker.Length;
        while (idx < line.Length)
        {
            var ch = line[idx];
            if (ch == '=' || ch == '＝' || ch == ':' || ch == '：')
            {
                idx++;
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                idx++;
                continue;
            }
            break;
        }
        if (idx >= line.Length) return null;
        var end = idx;
        while (end < line.Length)
        {
            var ch = line[end];
            if (ch == '、' || ch == '，' || ch == ',' || ch == '。' || ch == ';' || ch == '；' || ch == '\r' || ch == '\n')
                break;
            end++;
        }
        return line[idx..end].Trim();
    }

    private static string NormalizeAccountName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        return name.Replace("　", string.Empty).Replace(" ", string.Empty).Trim();
    }

    private static string? ResolveAccountCodeByName(string? name)
    {
        var normalized = NormalizeAccountName(name);
        if (string.IsNullOrEmpty(normalized)) return null;
        return AccountNameToCode.TryGetValue(normalized, out var code) ? code : null;
    }

    private static string ResolveAccountCodeForRule(string ruleKey, string defaultCode, Dictionary<string, JournalAccountInstruction> instructions, bool isDebit)
    {
        if (instructions.TryGetValue(ruleKey, out var instruction))
        {
            var name = isDebit ? instruction.DebitName : instruction.CreditName;
            var code = ResolveAccountCodeByName(name);
            if (!string.IsNullOrWhiteSpace(code)) return code;
        }
        return defaultCode;
    }

    private static string GetAccountNameOrCode(string code)
    {
        var entry = AccountNameToCode.FirstOrDefault(kvp => string.Equals(kvp.Value, code, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(entry.Key) ? code : entry.Key;
    }

    private static string FormatJournalDescription(string debitCode, string creditCode)
    {
        var debitName = GetAccountNameOrCode(debitCode);
        var creditName = GetAccountNameOrCode(creditCode);
        return $"{debitName}／{creditName}";
    }
    /// <summary>
    /// Registers all payroll-related API endpoints (DSL compilation, manual/auto runs, history queries, etc.).
    /// </summary>
    public static void MapHrPayrollModule(this WebApplication app)
    {
        // POST /ai/payroll/compile
        // Converts the provided natural-language payroll policy (company + employee context) into
        // a deterministic DSL payload consumed by the payroll executor. The request body looks like
        // { nlText?: string, companyText?: string, employeeText?: string }. The response returns
        // the compiled DSL plus explanation/tests/risk flags for UI preview.
        app.MapPost("/ai/payroll/compile", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var text = root.TryGetProperty("nlText", out var t) && t.ValueKind==JsonValueKind.String ? t.GetString() : null;
            var companyText = root.TryGetProperty("companyText", out var ct) && ct.ValueKind==JsonValueKind.String ? ct.GetString() : null;
            var employeeText = root.TryGetProperty("employeeText", out var et) && et.ValueKind==JsonValueKind.String ? et.GetString() : null;
            var s = string.Join("\n", new[]{ companyText, employeeText, text }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (string.IsNullOrWhiteSpace(s)) return Results.BadRequest(new { error = "nlText/companyText/employeeText required" });

            var items = new List<object>();
            var rules = new List<object>();
            var hints = new List<string>();
            var dependencies = new List<string>();
            var riskFlags = new List<string>();
            var multipliers = new Dictionary<string, decimal>();
            var payrollItemCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var journalRules = new List<object>();
            var journalInstructions = ParseJournalInstructions(s);

            string ResolveJournalDebit(string ruleKey, string defaultCode) => ResolveAccountCodeForRule(ruleKey, defaultCode, journalInstructions, true);
            string ResolveJournalCredit(string ruleKey, string defaultCode) => ResolveAccountCodeForRule(ruleKey, defaultCode, journalInstructions, false);

            void AddPayrollItem(string code, string name, string kind)
            {
                if (payrollItemCodes.Add(code))
                {
                    items.Add(new { code, name, kind, isActive = true });
                }
            }

            void EnsureEarningItem(string code, string name) => AddPayrollItem(code, name, "earning");
            void EnsureDeductionItem(string code, string name) => AddPayrollItem(code, name, "deduction");

            object BuildJournalItem(string code, decimal? sign = null)
            {
                return sign.HasValue ? new { code, sign } : new { code };
            }

            void AddJournalRule(string name, string debitAccount, string creditAccount, IEnumerable<object> journalItems, string? description = null)
            {
                var arr = journalItems?.ToArray() ?? Array.Empty<object>();
                if (arr.Length == 0) return;
                journalRules.Add(new
                {
                    name,
                    debitAccount,
                    creditAccount,
                    items = arr,
                    description
                });
            }

            string[] typeTokens = new[]{"正社员","正社員","契约社员","契約社員","兼职","パート","个人事业主","個人事業主"};
            var empTypes = new List<object>();
            foreach (var tok in typeTokens)
            {
                if (s.Contains(tok, StringComparison.OrdinalIgnoreCase))
                {
                    var code = tok switch
                    {
                        var x when x.Contains("正") => "FT",
                        var x when x.Contains("契") => "CT",
                        var x when x.Contains("兼职") || x.Contains("パート") => "PT",
                        _ => "OT"
                    };
                    if (!empTypes.Any(x => (string)(x.GetType().GetProperty("code")!.GetValue(x)!) == code))
                        empTypes.Add(new { code, name = tok, isActive = true });
                }
            }
            if (empTypes.Count==0) empTypes.Add(new { code="FT", name="正社员", isActive=true });

            var empTypeCodes = empTypes.Select(t => (string)t.GetType().GetProperty("code")!.GetValue(t)!).ToArray();
            object BuildActivation() => new { forEmploymentTypes = empTypeCodes };

            void AddHourlyRule(string code, string name, string hoursRef, decimal defaultMultiplier, bool isDeduction, string? multiplierKey, string hint)
            {
                if (isDeduction) EnsureDeductionItem(code, name);
                else EnsureEarningItem(code, name);

                object hourlyPayload;
                if (string.IsNullOrWhiteSpace(multiplierKey))
                {
                    hourlyPayload = new { hoursRef, baseRef = "employee.baseSalaryMonth", multiplier = defaultMultiplier };
                }
                else
                {
                    if (!multipliers.ContainsKey(multiplierKey))
                    {
                        multipliers[multiplierKey] = defaultMultiplier;
                    }
                    hourlyPayload = new
                    {
                        hoursRef,
                        baseRef = "employee.baseSalaryMonth",
                        multiplier = defaultMultiplier,
                        multiplierRef = $"policy.multipliers.{multiplierKey}"
                    };
                }

                rules.Add(new
                {
                    item = code,
                    type = isDeduction ? "deduction" : "earning",
                    activation = BuildActivation(),
                    formula = new { hourlyPay = hourlyPayload },
                    rounding = new { method = "round_half_down", precision = 0 }
                });
                hints.Add(hint);
            }
            bool hasBase = s.Contains("基本給", StringComparison.OrdinalIgnoreCase) || s.Contains("基本给", StringComparison.OrdinalIgnoreCase) || s.Contains("月給", StringComparison.OrdinalIgnoreCase) || s.Contains("月给", StringComparison.OrdinalIgnoreCase) || s.Contains("固定", StringComparison.OrdinalIgnoreCase) || s.Contains("基本工资", StringComparison.OrdinalIgnoreCase);

            if (hasBase)
            {
                EnsureEarningItem("BASE", "基本工资");
                var formulaObj = new { charRef = "employee.baseSalaryMonth" }; // always read from employee payload
                rules.Add(new { item = "BASE", type = "earning", activation = BuildActivation(), formula = formulaObj, rounding = new { method = "round_half_down", precision = 0 } });
            }
            if (ContainsAny(s, CommuteKeywords))
            {
                EnsureEarningItem("COMMUTE", "通勤手当");
                var formulaObj = new { charRef = "employee.commuteAllowance", cap = 30000 };
                rules.Add(new { item = "COMMUTE", type = "earning", activation = BuildActivation(), formula = formulaObj, rounding = new { method = "round_half_down", precision = 0 } });
            }
            bool wantsHealth = s.Contains("社会保険", StringComparison.OrdinalIgnoreCase) || s.Contains("社保", StringComparison.OrdinalIgnoreCase) || s.Contains("健康保険", StringComparison.OrdinalIgnoreCase);
            bool wantsPension = s.Contains("厚生年金", StringComparison.OrdinalIgnoreCase) || s.Contains("年金", StringComparison.OrdinalIgnoreCase);
            bool wantsEmployment = s.Contains("雇用保険", StringComparison.OrdinalIgnoreCase) || s.Contains("雇佣保险", StringComparison.OrdinalIgnoreCase);
            bool wantsWithholding = s.Contains("源泉", StringComparison.OrdinalIgnoreCase) || s.Contains("月額表", StringComparison.OrdinalIgnoreCase) || s.Contains("甲欄", StringComparison.OrdinalIgnoreCase);
            bool wantsOvertime = ContainsAny(s, "残業", "時間外", "加班");
            bool wantsOvertime60 = ContainsAny(s, "60時間", "６０時間", "60h", "60小时");
            bool wantsHolidayWork = ContainsAny(s, "休日", "祝日", "节假日", "節假日", "法定休日");
            bool wantsLateNight = ContainsAny(s, "深夜", "22時", "22点", "22點", "late night", "夜間");
            bool wantsAbsence = ContainsAny(s, "欠勤", "欠勤控除", "欠勤・遅刻", "勤怠控除", "工时不足", "工時不足", "遅刻", "早退");
            if (wantsHealth) {
                EnsureDeductionItem("HEALTH_INS", "社会保険");
                object baseExpr = new { charRef = "employee.baseSalaryMonth" };
                rules.Add(new { item = "HEALTH_INS", type = "deduction", activation = BuildActivation(), formula = new { _base = baseExpr, rate = "policy.law.health.rate", rounding = new { method = "round_half_down", precision = 0 } } });
                hints.Add("已生成健康保险计算规则：按都道府県与月额区间匹配费率");
                dependencies.AddRange(new[]{
                    "jp.health.standardMonthly",
                    "jp.health.rate.employee",
                    "jp.health.rate.employer",
                    "jp.health.care.rate.employee",
                    "jp.health.care.rate.employer"
                });
                if (!(s.Contains("都道府", StringComparison.OrdinalIgnoreCase) || s.Contains("都", StringComparison.OrdinalIgnoreCase) || s.Contains("県", StringComparison.OrdinalIgnoreCase))) riskFlags.Add("health.missing_prefecture");
                if (!(s.Contains("協会", StringComparison.OrdinalIgnoreCase) || s.Contains("組合", StringComparison.OrdinalIgnoreCase))) riskFlags.Add("health.missing_scheme");
            }
            if (wantsPension) {
                EnsureDeductionItem("PENSION", "厚生年金");
                object baseExpr = new { charRef = "employee.baseSalaryMonth" };
                rules.Add(new { item = "PENSION", type = "deduction", activation = BuildActivation(), formula = new { _base = baseExpr, rate = "policy.law.pension.rate", rounding = new { method = "round_half_down", precision = 0 } } });
                hints.Add("已生成厚生年金计算规则：按月额区间匹配费率");
                dependencies.AddRange(new[]{
                    "jp.pension.standardMonthly",
                    "jp.pension.rate.employee",
                    "jp.pension.rate.employer"
                });
            }
            if (wantsEmployment) {
                EnsureDeductionItem("EMP_INS", "雇用保険");
                object baseExpr = new { charRef = "employee.salaryTotal" };
                rules.Add(new { item = "EMP_INS", type = "deduction", activation = BuildActivation(), formula = new { _base = baseExpr, rate = "policy.law.employment.rate", rounding = new { method = "round_half_down", precision = 0 } } });
                hints.Add("已生成雇用保险计算规则：按事业区分匹配费率");
                dependencies.AddRange(new[]{
                    "jp.ei.rate.employee",
                    "jp.ei.rate.employer"
                });
                if (!(s.Contains("一般", StringComparison.OrdinalIgnoreCase) || s.Contains("建設", StringComparison.OrdinalIgnoreCase) || s.Contains("建筑", StringComparison.OrdinalIgnoreCase) || s.Contains("農林水産", StringComparison.OrdinalIgnoreCase))) riskFlags.Add("ei.missing_industry");
            }
            if (wantsWithholding)
            {
                EnsureDeductionItem("WHT", "源泉徴収税");
                rules.Add(new { item = "WHT", type = "deduction", activation = BuildActivation(), formula = new { withholding = new { category = "monthly_ko" } } });
                hints.Add("已生成源泉徴収税计算规则：按月額表甲欄匹配。");
            }
            if (wantsOvertime)
            {
                AddHourlyRule("OVERTIME_STD", "残業手当", "employee.workHours.overtimeHours", 1.25m, false, "overtime", "已生成残業手当：日別残業時間×1.25倍の時給。");
            }
            if (wantsOvertime && wantsOvertime60)
            {
                AddHourlyRule("OVERTIME_60", "60時間超残業手当", "employee.workHours.overtime60Hours", 1.5m, false, "overtime60", "已生成60時間超残業手当：月60時間超部分×1.5倍の時給。");
            }
            if (wantsHolidayWork)
            {
                AddHourlyRule("HOLIDAY_PAY", "休日労働手当", "employee.workHours.holidayHours", 1.35m, false, "holiday", "已生成休日労働手当：法定休日実績×1.35倍の時給。");
            }
            if (wantsLateNight)
            {
                AddHourlyRule("LATE_NIGHT_PAY", "深夜手当", "employee.workHours.lateNightHours", 1.25m, false, "lateNight", "已生成深夜手当：22時～翌5時実績×1.25倍の時給。");
            }
            if (wantsAbsence)
            {
                AddHourlyRule("ABSENCE_DEDUCT", "欠勤控除", "employee.workHours.absenceHours", 1m, true, null, "已生成欠勤控除：不足時間×基準時給作为控除。");
            }

            var wageJournalItems = new List<object>();
            if (hasBase) wageJournalItems.Add(BuildJournalItem("BASE"));
            if (ContainsAny(s, CommuteKeywords)) wageJournalItems.Add(BuildJournalItem("COMMUTE"));
            if (wantsOvertime) wageJournalItems.Add(BuildJournalItem("OVERTIME_STD"));
            if (wantsOvertime && wantsOvertime60) wageJournalItems.Add(BuildJournalItem("OVERTIME_60"));
            if (wantsHolidayWork) wageJournalItems.Add(BuildJournalItem("HOLIDAY_PAY"));
            if (wantsLateNight) wageJournalItems.Add(BuildJournalItem("LATE_NIGHT_PAY"));
            if (wantsAbsence) wageJournalItems.Add(BuildJournalItem("ABSENCE_DEDUCT", -1m));
            if (wageJournalItems.Count > 0)
            {
                var wageDebit = ResolveJournalDebit("wages", "832");
                var wageCredit = ResolveJournalCredit("wages", "315");
                AddJournalRule("wages", wageDebit, wageCredit, wageJournalItems, FormatJournalDescription(wageDebit, wageCredit));
            }
            if (wantsHealth)
            {
                var healthDebit = ResolveJournalDebit("health", "3181");
                var healthCredit = ResolveJournalCredit("health", "315");
                AddJournalRule("health", healthDebit, healthCredit, new[] { BuildJournalItem("HEALTH_INS") }, FormatJournalDescription(healthDebit, healthCredit));
            }
            if (wantsPension)
            {
                var pensionDebit = ResolveJournalDebit("pension", "3182");
                var pensionCredit = ResolveJournalCredit("pension", "315");
                AddJournalRule("pension", pensionDebit, pensionCredit, new[] { BuildJournalItem("PENSION") }, FormatJournalDescription(pensionDebit, pensionCredit));
            }
            if (wantsEmployment)
            {
                var employmentDebit = ResolveJournalDebit("employment", "3183");
                var employmentCredit = ResolveJournalCredit("employment", "315");
                AddJournalRule("employment", employmentDebit, employmentCredit, new[] { BuildJournalItem("EMP_INS") }, FormatJournalDescription(employmentDebit, employmentCredit));
            }
            if (wantsWithholding)
            {
                var withholdingDebit = ResolveJournalDebit("withholding", "3184");
                var withholdingCredit = ResolveJournalCredit("withholding", "315");
                AddJournalRule("withholding", withholdingDebit, withholdingCredit, new[] { BuildJournalItem("WHT") }, FormatJournalDescription(withholdingDebit, withholdingCredit));
            }

            if (!hasBase) riskFlags.Add("employee.missing_base_salary");

            var dsl = new { employmentTypes = empTypes, payrollItems = items, rules, law = new { source = "dataset", dependencies = dependencies.Distinct().ToArray() }, hints, multipliers, journalRules };
            var explanation = "已将自然语言编译为可执行规则：包含基本项、法定扣除以及勤怠驱动的残業/休日/深夜/欠勤控除（如有提及）。计算由本地引擎执行，费率由法规数据集匹配。";
            var tests = new object[]{
                new { name="basic", expectItems = items.Select(i => (string)i.GetType().GetProperty("code")!.GetValue(i)!).ToArray() },
                new { name="law-deps", expectLawDependencies = dependencies.Distinct().ToArray() }
            };
            return Results.Ok(new { dsl, explanation, tests, riskFlags = riskFlags.Distinct().ToArray() });
        }).RequireAuthorization();

        // POST /operations/payroll/execute
        // Runs a one-off payroll calculation for a single employee without persisting results.
        // Body expects { employeeId: Guid, month: "YYYY-MM", policyId?: Guid, debug?: bool }.
        // Returns net amount, payroll sheet, accounting draft, and trace data (when debug=true).
        app.MapPost("/operations/payroll/execute", async (HttpRequest req, NpgsqlDataSource ds, LawDatasetService law, CancellationToken ct) =>
        {
            return await ExecutePayrollEndpoint(req, ds, law, ct);
        }).RequireAuthorization();

        // POST /payroll/preflight
        // Runs preflight checks to determine if payroll calculation can proceed for the given month.
        // Validates attendance data availability, employee salary configurations, and other prerequisites.
        // Body: { month: "YYYY-MM", employeeIds?: Guid[], config?: { minTimesheetDays?, requireBaseSalary?, ... } }
        app.MapPost("/payroll/preflight", async (HttpRequest req, PayrollPreflightService preflight, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("month", out var monthEl) || monthEl.ValueKind != JsonValueKind.String)
                return Results.BadRequest(new { error = "month required" });

            var month = monthEl.GetString()!;
            IReadOnlyCollection<Guid>? employeeIds = null;
            if (root.TryGetProperty("employeeIds", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
            {
                var idList = new List<Guid>();
                foreach (var item in idsEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var gid))
                        idList.Add(gid);
                }
                if (idList.Count > 0) employeeIds = idList;
            }

            PayrollPreflightService.PreflightConfig? config = null;
            if (root.TryGetProperty("config", out var configEl) && configEl.ValueKind == JsonValueKind.Object)
            {
                var minDays = configEl.TryGetProperty("minTimesheetDays", out var md) && md.ValueKind == JsonValueKind.Number ? md.GetInt32() : 15;
                var reqBase = !configEl.TryGetProperty("requireBaseSalary", out var rb) || rb.ValueKind != JsonValueKind.False;
                var reqSocial = configEl.TryGetProperty("requireSocialInsuranceBase", out var rs) && rs.ValueKind == JsonValueKind.True;
                var reqConfirm = configEl.TryGetProperty("requireConfirmedTimesheet", out var rc) && rc.ValueKind == JsonValueKind.True;
                config = new PayrollPreflightService.PreflightConfig(minDays, reqBase, reqSocial, reqConfirm);
            }

            var result = await preflight.RunPreflightChecksAsync(cc.ToString(), employeeIds, month, config, ct);
            return Results.Ok(result);
        }).RequireAuthorization();

        // GET /holidays
        // Returns Japanese national holidays for a given year or date range.
        // Query: ?year=YYYY or ?from=YYYY-MM-DD&to=YYYY-MM-DD
        app.MapGet("/holidays", (HttpRequest req) =>
        {
            var yearStr = req.Query["year"].ToString();
            var from = req.Query["from"].ToString();
            var to = req.Query["to"].ToString();

            var holidays = new List<object>();

            if (!string.IsNullOrWhiteSpace(yearStr) && int.TryParse(yearStr, out var year))
            {
                // 指定年份的所有节假日
                var yearHolidays = JapaneseHolidayService.Instance.GetHolidays(year);
                foreach (var date in yearHolidays.OrderBy(d => d))
                {
                    holidays.Add(new
                    {
                        date = date.ToString("yyyy-MM-dd"),
                        dayOfWeek = (int)date.DayOfWeek,
                        dayOfWeekName = GetJapaneseDayOfWeek(date.DayOfWeek)
                    });
                }
            }
            else if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to))
            {
                // 指定日期范围的节假日
                if (DateOnly.TryParse(from, out var fromDate) && DateOnly.TryParse(to, out var toDate))
                {
                    for (var d = fromDate; d <= toDate; d = d.AddDays(1))
                    {
                        if (JapaneseHolidayService.Instance.IsNationalHoliday(d))
                        {
                            holidays.Add(new
                            {
                                date = d.ToString("yyyy-MM-dd"),
                                dayOfWeek = (int)d.DayOfWeek,
                                dayOfWeekName = GetJapaneseDayOfWeek(d.DayOfWeek)
                            });
                        }
                    }
                }
            }
            else
            {
                // 默认返回当年的节假日
                var currentYear = DateTime.Now.Year;
                var yearHolidays = JapaneseHolidayService.Instance.GetHolidays(currentYear);
                foreach (var date in yearHolidays.OrderBy(d => d))
                {
                    holidays.Add(new
                    {
                        date = date.ToString("yyyy-MM-dd"),
                        dayOfWeek = (int)date.DayOfWeek,
                        dayOfWeekName = GetJapaneseDayOfWeek(date.DayOfWeek)
                    });
                }
            }

            return Results.Ok(new { holidays, count = holidays.Count });
        }).AllowAnonymous();

        // Alias for frontend api.ts auto-prefix (/api/*)
        app.MapGet("/api/holidays", (HttpRequest req) =>
        {
            return Results.Redirect($"/holidays{req.QueryString}", permanent: false);
        }).AllowAnonymous();

        // GET /holidays/check
        // Checks if a specific date is a holiday (weekend or national holiday).
        // Query: ?date=YYYY-MM-DD
        app.MapGet("/holidays/check", (HttpRequest req) =>
        {
            var dateStr = req.Query["date"].ToString();
            if (string.IsNullOrWhiteSpace(dateStr) || !DateOnly.TryParse(dateStr, out var date))
            {
                return Results.BadRequest(new { error = "date parameter required (format: YYYY-MM-DD)" });
            }

            var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var isNationalHoliday = JapaneseHolidayService.Instance.IsNationalHoliday(date);
            var isHoliday = isWeekend || isNationalHoliday;

            return Results.Ok(new
            {
                date = date.ToString("yyyy-MM-dd"),
                dayOfWeek = (int)date.DayOfWeek,
                dayOfWeekName = GetJapaneseDayOfWeek(date.DayOfWeek),
                isWeekend,
                isNationalHoliday,
                isHoliday
            });
        }).AllowAnonymous();

        // Alias for frontend api.ts auto-prefix (/api/*)
        app.MapGet("/api/holidays/check", (HttpRequest req) =>
        {
            return Results.Redirect($"/holidays/check{req.QueryString}", permanent: false);
        }).AllowAnonymous();

        // GET /payroll/preflight/quick
        // Quick check to determine if payroll can be calculated for a month.
        // Query: ?month=YYYY-MM. Returns summary without detailed per-employee results.
        app.MapGet("/payroll/preflight/quick", async (HttpRequest req, PayrollPreflightService preflight, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var month = req.Query["month"].ToString();
            if (string.IsNullOrWhiteSpace(month))
                return Results.BadRequest(new { error = "month query parameter required" });

            var (canProceed, readyCount, notReadyCount, issues) = await preflight.QuickCheckAsync(cc.ToString(), month, ct);
            return Results.Ok(new
            {
                canProceed,
                readyCount,
                notReadyCount,
                issues
            });
        }).RequireAuthorization();

        // POST /payroll/accuracy-report
        // Generates an accuracy report comparing current calculations against historical data.
        // Body: { month, entries: [{ employeeId, totalAmount, ... }], thresholds?: { percentWarning, ... } }
        // Returns accuracy score, anomaly counts, and per-employee analysis.
        app.MapPost("/payroll/accuracy-report", async (HttpRequest req, PayrollService payroll, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("month", out var monthEl) || monthEl.ValueKind != JsonValueKind.String)
                return Results.BadRequest(new { error = "month required" });

            if (!root.TryGetProperty("entries", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { error = "entries array required" });

            var month = monthEl.GetString()!;
            var entries = new List<PayrollService.PayrollPreviewEntry>();

            foreach (var entryEl in entriesEl.EnumerateArray())
            {
                if (entryEl.ValueKind != JsonValueKind.Object) continue;

                if (!entryEl.TryGetProperty("employeeId", out var empIdEl)) continue;
                if (!Guid.TryParse(empIdEl.GetString(), out var employeeId)) continue;

                var totalAmount = 0m;
                if (entryEl.TryGetProperty("totalAmount", out var amtEl))
                {
                    if (amtEl.ValueKind == JsonValueKind.Number)
                        totalAmount = amtEl.TryGetDecimal(out var d) ? d : Convert.ToDecimal(amtEl.GetDouble());
                    else if (amtEl.ValueKind == JsonValueKind.String && decimal.TryParse(amtEl.GetString(), out var parsed))
                        totalAmount = parsed;
                }

                var employeeCode = entryEl.TryGetProperty("employeeCode", out var codeEl) && codeEl.ValueKind == JsonValueKind.String
                    ? codeEl.GetString() : null;
                var employeeName = entryEl.TryGetProperty("employeeName", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString() : null;

                entries.Add(new PayrollService.PayrollPreviewEntry(
                    employeeId, employeeCode, employeeName, null, null,
                    totalAmount, new System.Text.Json.Nodes.JsonArray(), new System.Text.Json.Nodes.JsonArray(),
                    null, null, null, null));
            }

            if (entries.Count == 0)
                return Results.BadRequest(new { error = "No valid entries provided" });

            // Parse thresholds if provided
            PayrollService.AccuracyThresholds? thresholds = null;
            if (root.TryGetProperty("thresholds", out var threshEl) && threshEl.ValueKind == JsonValueKind.Object)
            {
                var pctWarn = threshEl.TryGetProperty("percentWarning", out var pw) && pw.ValueKind == JsonValueKind.Number
                    ? pw.GetDecimal() : 0.15m;
                var pctCrit = threshEl.TryGetProperty("percentCritical", out var pc) && pc.ValueKind == JsonValueKind.Number
                    ? pc.GetDecimal() : 0.30m;
                var amtWarn = threshEl.TryGetProperty("amountWarning", out var aw) && aw.ValueKind == JsonValueKind.Number
                    ? aw.GetDecimal() : 30000m;
                var amtCrit = threshEl.TryGetProperty("amountCritical", out var ac) && ac.ValueKind == JsonValueKind.Number
                    ? ac.GetDecimal() : 100000m;
                thresholds = new PayrollService.AccuracyThresholds(pctWarn, amtWarn, pctCrit, amtCrit);
            }

            var report = await payroll.GenerateAccuracyReportAsync(cc.ToString(), month, entries, thresholds, ct);
            return Results.Ok(report);
        }).RequireAuthorization();

        // POST /payroll/manual/run
        // Generates payroll previews for one or more employees so that the operator can inspect
        // calculated items before saving. Body: PayrollManualRunRequest { employeeIds[], month,
        // policyId?, debug? }. Response: PayrollManualRunResult with entry list per employee.
        app.MapPost("/payroll/manual/run", async (HttpRequest req, PayrollService payroll, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var body = await JsonSerializer.DeserializeAsync<PayrollManualRunRequest>(req.Body, PayrollJsonSerializerOptions, ct);
            if (body is null || body.EmployeeIds is null || body.EmployeeIds.Count == 0 || string.IsNullOrWhiteSpace(body.Month))
                return Results.BadRequest(new { error = "employeeIds and month required" });

            try
            {
                var result = await payroll.ManualRunAsync(cc.ToString(), body.EmployeeIds, body.Month, body.PolicyId, body.Debug, body.ManualWorkHours, ct);
                return Results.Ok(result);
            }
            catch (PayrollExecutionException ex)
            {
                return Results.Json(new
                {
                    ex.Message,
                    ex.Payload
                }, statusCode: ex.StatusCode);
            }
        }).RequireAuthorization();

        // POST /payroll/manual/save
        // Persists a previously previewed payroll result while optionally overwriting an existing run.
        // Body: PayrollManualSaveRequest { month, policyId?, overwrite, runType, entries[] } where each
        // entry carries payrollSheet/accountingDraft/trace data. Response returns run id plus entry map.
        app.MapPost("/payroll/manual/save", async (HttpRequest req, PayrollService payroll, ILogger<PayrollService>? logger, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            PayrollService.PayrollManualSaveRequest? body;
            try
            {
                // Enable buffering to allow re-reading if needed for debugging
                req.EnableBuffering();
                body = await JsonSerializer.DeserializeAsync<PayrollService.PayrollManualSaveRequest>(req.Body, PayrollJsonSerializerOptions, ct);
                
                logger?.LogInformation("工资保存请求解析成功: Month={Month}, PolicyId={PolicyId}, Overwrite={Overwrite}, EntryCount={EntryCount}",
                    body?.Month, body?.PolicyId, body?.Overwrite, body?.Entries?.Count ?? 0);
                
                // Log entry details for debugging
                if (body?.Entries != null)
                {
                    foreach (var entry in body.Entries)
                    {
                        logger?.LogInformation("Entry: EmployeeId={EmployeeId}, EmployeeCode={EmployeeCode}, TotalAmount={TotalAmount}, PayrollSheetKind={PayrollSheetKind}, AccountingDraftKind={AccountingDraftKind}",
                            entry.EmployeeId, entry.EmployeeCode, entry.TotalAmount, entry.PayrollSheet.ValueKind, entry.AccountingDraft.ValueKind);
                    }
                }
            }
            catch (JsonException jsonEx)
            {
                logger?.LogError(jsonEx, "工资保存请求反序列化失败: {Path}, {LineNumber}, {Position}", jsonEx.Path, jsonEx.LineNumber, jsonEx.BytePositionInLine);
                return Results.BadRequest(new { error = "Invalid JSON format", detail = jsonEx.Message, path = jsonEx.Path, line = jsonEx.LineNumber, position = jsonEx.BytePositionInLine });
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "工资保存请求处理失败");
                return Results.BadRequest(new { error = "Request processing failed", detail = ex.Message });
            }

            if (body is null || body.Entries is null || body.Entries.Count == 0 || string.IsNullOrWhiteSpace(body.Month))
                return Results.BadRequest(new { error = "month and entries required" });

            var user = Auth.GetUserCtx(req);
            try
            {
                var result = await payroll.SaveManualAsync(cc.ToString(), user, body, ct);
                return Results.Ok(result);
            }
            catch (PayrollExecutionException ex)
            {
                logger?.LogWarning("工资保存业务异常: {Message}", ex.Message);
                return Results.Json(ex.Payload, statusCode: ex.StatusCode);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "工资保存失败: {Message}", ex.Message);
                return Results.Json(new { error = "保存に失敗しました", detail = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
        }).RequireAuthorization();

        // POST /payroll/auto/run
        // Launches an automated payroll run for the supplied employee set (or entire company) inside
        // an AI review session. Body requires { month, sessionId, employeeIds?, policyId?, overwrite?,
        // diffPercentThreshold?, diffAmountThreshold?, debug? }. The endpoint saves payroll results,
        // creates journal drafts, and enqueues AI review tasks. Response mirrors manual save result.
        app.MapPost("/payroll/auto/run", async (HttpRequest req, NpgsqlDataSource ds, PayrollService payroll, PayrollTaskService payrollTasks, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString();
            var user = Auth.GetUserCtx(req);
            if (user is null || string.IsNullOrWhiteSpace(user.UserId))
                return Results.Unauthorized();

            var payload = await JsonSerializer.DeserializeAsync<PayrollAutoRunRequest>(req.Body, PayrollJsonSerializerOptions, ct);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Month))
                return Results.BadRequest(new { error = "month required" });
            if (!payload.SessionId.HasValue || payload.SessionId.Value == Guid.Empty)
                return Results.BadRequest(new { error = "sessionId required" });

            if (!await EnsureSessionOwnershipAsync(ds, payload.SessionId.Value, companyCode, user.UserId, ct))
                return Results.Forbid();

            var employeeIds = await payroll.ResolveEmployeeIdsAsync(companyCode, payload.EmployeeIds, ct);
            if (employeeIds.Count == 0)
                return Results.BadRequest(new { error = "no employees to run payroll" });

            var debug = payload.Debug ?? true;
            PayrollService.PayrollManualRunResult preview;
            try
            {
                preview = await payroll.ManualRunAsync(companyCode, employeeIds, payload.Month!, payload.PolicyId, debug, ct);
            }
            catch (PayrollExecutionException ex)
            {
                return Results.Json(new
                {
                    ex.Message,
                    ex.Payload
                }, statusCode: ex.StatusCode);
            }

            var percentThreshold = payload.DiffPercentThreshold ?? 0.15m;
            var amountThreshold = payload.DiffAmountThreshold ?? 30000m;

            var saveEntries = new List<PayrollService.PayrollManualSaveEntry>();
            var candidateInfos = new List<PayrollTaskCandidateInfo>();

            foreach (var entry in preview.Entries)
            {
                var diffClone = entry.DiffSummary is null ? null : entry.DiffSummary.DeepClone().AsObject();
                var needsConfirmation = ShouldFlagDiff(diffClone, percentThreshold, amountThreshold);
                var metadataNode = new JsonObject
                {
                    ["source"] = "auto",
                    ["needsConfirmation"] = needsConfirmation,
                    ["diffPercentThreshold"] = percentThreshold,
                    ["diffAmountThreshold"] = amountThreshold
                };
                saveEntries.Add(CreateManualSaveEntry(entry, debug, metadataNode));
                candidateInfos.Add(new PayrollTaskCandidateInfo(
                    entry.EmployeeId,
                    entry.EmployeeCode,
                    entry.EmployeeName,
                    entry.DepartmentCode,
                    entry.DepartmentName,
                    entry.TotalAmount,
                    diffClone,
                    preview.Month!,
                    needsConfirmation));
            }

            var saveRequest = new PayrollService.PayrollManualSaveRequest
            {
                Month = preview.Month!,
                PolicyId = preview.PolicyId,
                Overwrite = payload.Overwrite,
                RunType = "auto",
                Entries = saveEntries
            };

            var saveResult = await payroll.SaveManualAsync(companyCode, user, saveRequest, ct);

            var createdTasks = 0;
            if (candidateInfos.Count > 0)
            {
                var candidates = new List<PayrollTaskService.PayrollTaskCandidate>();
                foreach (var candidate in candidateInfos)
                {
                    if (!saveResult.EntryIdsByEmployeeId.TryGetValue(candidate.EmployeeId, out var entryId))
                        continue;
                    var diffCopy = candidate.DiffSummary?.DeepClone().AsObject();
                    var summary = BuildPayrollTaskSummary(candidate);
                    candidates.Add(new PayrollTaskService.PayrollTaskCandidate(
                        entryId,
                        candidate.EmployeeId,
                        candidate.EmployeeCode,
                        candidate.EmployeeName,
                        candidate.PeriodMonth,
                        candidate.TotalAmount,
                        diffCopy,
                        summary,
                        candidate.NeedsConfirmation));
                }
                if (candidates.Count > 0)
                {
                    await payrollTasks.CreateTasksAsync(payload.SessionId!.Value, companyCode, saveResult.RunId, payload.TargetUserId ?? user.UserId, candidates, ct);
                    createdTasks = candidates.Count;
                }
            }

            var responseEntries = new List<object>();
            foreach (var candidate in candidateInfos)
            {
                saveResult.EntryIdsByEmployeeId.TryGetValue(candidate.EmployeeId, out var entryId);
                responseEntries.Add(new
                {
                    entryId,
                    employeeId = candidate.EmployeeId,
                    employeeCode = candidate.EmployeeCode,
                    employeeName = candidate.EmployeeName,
                    departmentCode = candidate.DepartmentCode,
                    departmentName = candidate.DepartmentName,
                    totalAmount = candidate.TotalAmount,
                    diffSummary = candidate.DiffSummary,
                    needsConfirmation = candidate.NeedsConfirmation
                });
            }

            return Results.Ok(new
            {
                runId = saveResult.RunId,
                month = preview.Month,
                policyId = preview.PolicyId,
                runType = "auto",
                entryCount = responseEntries.Count,
                tasksCreated = createdTasks,
                entries = responseEntries
            });
        }).RequireAuthorization();

        // ========== Payroll Task Management Endpoints ==========

        // GET /payroll/tasks
        // Lists payroll tasks with optional filters.
        // Query: ?month, ?status, ?taskType, ?userId, ?page, ?pageSize
        app.MapGet("/payroll/tasks", async (HttpRequest req, PayrollTaskService taskService, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var month = req.Query["month"].ToString();
            var status = req.Query["status"].ToString();
            var taskType = req.Query["taskType"].ToString();
            var userId = req.Query["userId"].ToString();
            var page = int.TryParse(req.Query["page"], out var p) ? Math.Max(1, p) : 1;
            var pageSize = int.TryParse(req.Query["pageSize"], out var ps) ? Math.Clamp(ps, 1, 100) : 20;

            var tasks = await taskService.ListByCompanyAsync(
                cc.ToString(),
                string.IsNullOrWhiteSpace(month) ? null : month,
                string.IsNullOrWhiteSpace(status) ? null : status,
                string.IsNullOrWhiteSpace(taskType) ? null : taskType,
                string.IsNullOrWhiteSpace(userId) ? null : userId,
                page, pageSize, ct);

            return Results.Ok(new { tasks, page, pageSize });
        }).RequireAuthorization();

        // GET /payroll/tasks/my
        // Lists tasks assigned to or targeted at the current user.
        // Query: ?pendingOnly=true
        app.MapGet("/payroll/tasks/my", async (HttpRequest req, PayrollTaskService taskService, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var user = Auth.GetUserCtx(req);
            if (user is null || string.IsNullOrWhiteSpace(user.UserId))
                return Results.Unauthorized();

            var pendingOnly = req.Query.TryGetValue("pendingOnly", out var po) &&
                (po.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) || po.ToString() == "1");

            var tasks = await taskService.ListByUserAsync(cc.ToString(), user.UserId, pendingOnly, ct);
            return Results.Ok(tasks);
        }).RequireAuthorization();

        // GET /payroll/tasks/summary
        // Gets summary statistics for payroll tasks.
        // Query: ?month
        app.MapGet("/payroll/tasks/summary", async (HttpRequest req, PayrollTaskService taskService, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var month = req.Query["month"].ToString();
            var summary = await taskService.GetSummaryAsync(cc.ToString(), string.IsNullOrWhiteSpace(month) ? null : month, ct);
            return Results.Ok(summary);
        }).RequireAuthorization();

        // GET /payroll/tasks/{taskId}
        // Gets a single task by ID.
        app.MapGet("/payroll/tasks/{taskId}", async (HttpRequest req, Guid taskId, PayrollTaskService taskService, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var task = await taskService.GetByIdAsync(taskId, cc.ToString(), ct);
            if (task is null)
                return Results.NotFound(new { error = "Task not found" });

            return Results.Ok(task);
        }).RequireAuthorization();

        // POST /payroll/tasks/{taskId}/approve
        // Approves a payroll task.
        // Body: { comments?: string }
        app.MapPost("/payroll/tasks/{taskId}/approve", async (HttpRequest req, Guid taskId, PayrollTaskService taskService, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var user = Auth.GetUserCtx(req);
            if (user is null || string.IsNullOrWhiteSpace(user.UserId))
                return Results.Unauthorized();

            string? comments = null;
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
                if (doc.RootElement.TryGetProperty("comments", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                    comments = cEl.GetString();
            }
            catch { /* Body is optional */ }

            var result = await taskService.UpdateTaskStatusAsync(taskId, cc.ToString(), PayrollTaskService.TaskStatuses.Approved, user.UserId, comments, ct);
            if (!result.Success)
                return Results.BadRequest(new { error = result.Error });

            return Results.Ok(result.Task);
        }).RequireAuthorization();

        // POST /payroll/tasks/{taskId}/reject
        // Rejects a payroll task.
        // Body: { comments?: string }
        app.MapPost("/payroll/tasks/{taskId}/reject", async (HttpRequest req, Guid taskId, PayrollTaskService taskService, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var user = Auth.GetUserCtx(req);
            if (user is null || string.IsNullOrWhiteSpace(user.UserId))
                return Results.Unauthorized();

            string? comments = null;
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
                if (doc.RootElement.TryGetProperty("comments", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                    comments = cEl.GetString();
            }
            catch { }

            var result = await taskService.UpdateTaskStatusAsync(taskId, cc.ToString(), PayrollTaskService.TaskStatuses.Rejected, user.UserId, comments, ct);
            if (!result.Success)
                return Results.BadRequest(new { error = result.Error });

            return Results.Ok(result.Task);
        }).RequireAuthorization();

        // POST /payroll/tasks/{taskId}/assign
        // Assigns a task to a specific user.
        // Body: { userId: string }
        app.MapPost("/payroll/tasks/{taskId}/assign", async (HttpRequest req, Guid taskId, PayrollTaskService taskService, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("userId", out var userIdEl) || userIdEl.ValueKind != JsonValueKind.String)
                return Results.BadRequest(new { error = "userId required" });

            var assigneeUserId = userIdEl.GetString()!;
            var result = await taskService.AssignTaskAsync(taskId, cc.ToString(), assigneeUserId, ct);
            if (!result.Success)
                return Results.BadRequest(new { error = result.Error });

            return Results.Ok(result.Task);
        }).RequireAuthorization();

        // POST /payroll/tasks/{taskId}/status
        // Updates task status to any valid state.
        // Body: { status: string, comments?: string }
        app.MapPost("/payroll/tasks/{taskId}/status", async (HttpRequest req, Guid taskId, PayrollTaskService taskService, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var user = Auth.GetUserCtx(req);
            if (user is null || string.IsNullOrWhiteSpace(user.UserId))
                return Results.Unauthorized();

            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("status", out var statusEl) || statusEl.ValueKind != JsonValueKind.String)
                return Results.BadRequest(new { error = "status required" });

            var newStatus = statusEl.GetString()!;
            string? comments = null;
            if (doc.RootElement.TryGetProperty("comments", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                comments = cEl.GetString();

            var result = await taskService.UpdateTaskStatusAsync(taskId, cc.ToString(), newStatus, user.UserId, comments, ct);
            if (!result.Success)
                return Results.BadRequest(new { error = result.Error });

            return Results.Ok(result.Task);
        }).RequireAuthorization();

        // ========== Payroll Deadline Management Endpoints ==========

        // GET /payroll/deadline
        // Gets the deadline status for the specified month.
        // Query: ?month=YYYY-MM
        app.MapGet("/payroll/deadline", async (HttpRequest req, PayrollDeadlineService deadlineService, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var month = req.Query["month"].ToString();
            if (string.IsNullOrWhiteSpace(month))
                month = DateTime.UtcNow.ToString("yyyy-MM");

            var status = await deadlineService.GetDeadlineStatusAsync(cc.ToString(), month, ct);
            if (status is null)
                return Results.Ok(new { month, hasDeadline = false });

            return Results.Ok(new
            {
                month,
                hasDeadline = true,
                deadlineAt = status.DeadlineAt,
                warningAt = status.WarningAt,
                status = status.Status,
                notifiedAt = status.NotifiedAt,
                completedAt = status.CompletedAt
            });
        }).RequireAuthorization();

        // POST /payroll/deadline
        // Creates or updates a deadline for the specified month.
        // Body: { month: string, deadlineAt: string (ISO), warningAt?: string (ISO) }
        app.MapPost("/payroll/deadline", async (HttpRequest req, PayrollDeadlineService deadlineService, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("month", out var monthEl) || monthEl.ValueKind != JsonValueKind.String)
                return Results.BadRequest(new { error = "month required" });

            if (!root.TryGetProperty("deadlineAt", out var deadlineEl) || deadlineEl.ValueKind != JsonValueKind.String)
                return Results.BadRequest(new { error = "deadlineAt required" });

            var month = monthEl.GetString()!;
            if (!DateTimeOffset.TryParse(deadlineEl.GetString(), out var deadline))
                return Results.BadRequest(new { error = "Invalid deadlineAt format" });

            DateTimeOffset? warning = null;
            if (root.TryGetProperty("warningAt", out var warningEl) && warningEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(warningEl.GetString(), out var parsedWarning))
            {
                warning = parsedWarning;
            }

            await deadlineService.CreateDeadlineAsync(cc.ToString(), month, deadline, warning, ct);
            return Results.Ok(new { success = true, month, deadlineAt = deadline, warningAt = warning });
        }).RequireAuthorization();

        // GET /payroll/runs
        // Lists payroll runs for the company with optional filters (?month, ?policyId, ?runType).
        // Returns summary rows (run id, month, policy info, entry count) for paging in the UI.
        app.MapGet("/payroll/runs", async (HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString();
            var page = int.TryParse(req.Query["page"], out var p) ? Math.Max(1, p) : 1;
            var pageSize = int.TryParse(req.Query["pageSize"], out var ps) ? Math.Clamp(ps, 1, 200) : 20;
            var offset = (page - 1) * pageSize;
            var runType = NormalizeRunType(req.Query["runType"].ToString());
            var month = req.Query["month"].ToString();
            var policyId = ParseGuid(req.Query["policyId"].ToString());

            await using var conn = await ds.OpenConnectionAsync(ct);
            var filters = new List<string> { "r.company_code = @company" };
            var parameters = new Dictionary<string, object?>
            {
                ["company"] = companyCode
            };
            if (!string.IsNullOrWhiteSpace(month))
            {
                filters.Add("r.period_month = @month");
                parameters["month"] = month;
            }
            if (!string.IsNullOrWhiteSpace(runType) && !string.Equals(runType, "all", StringComparison.OrdinalIgnoreCase))
            {
                filters.Add("r.run_type = @runType");
                parameters["runType"] = runType;
            }
            if (policyId.HasValue)
            {
                filters.Add("(r.policy_id = @policyId)");
                parameters["policyId"] = policyId.Value;
            }
            var whereClause = filters.Count > 0 ? string.Join(" AND ", filters) : "1=1";

            long total = 0;
            await using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = $"SELECT COUNT(*) FROM payroll_runs r WHERE {whereClause}";
                foreach (var kvp in parameters)
                {
                    var key = kvp.Key;
                    if (key is "pageSize" or "offset") continue;
                    countCmd.Parameters.AddWithValue(key, kvp.Value ?? DBNull.Value);
                }
                var obj = await countCmd.ExecuteScalarAsync(ct);
                total = obj switch
                {
                    null => 0,
                    long l => l,
                    int i => i,
                    decimal d => (long)d,
                    _ => Convert.ToInt64(obj)
                };
            }

            var items = new List<object>();
            await using (var dataCmd = conn.CreateCommand())
            {
                dataCmd.CommandText = $@"
SELECT r.id, r.period_month, r.run_type, r.status, r.total_amount, r.created_at, r.updated_at,
       r.policy_id, COALESCE(p.payload->>'code','') AS policy_code, COALESCE(p.payload->>'name','') AS policy_name,
       COALESCE(r.metadata, '{{}}'::jsonb) AS metadata,
       (SELECT COUNT(*) FROM payroll_run_entries e WHERE e.run_id = r.id) AS entry_count
FROM payroll_runs r
LEFT JOIN payroll_policies p ON p.id = r.policy_id AND p.company_code = r.company_code
WHERE {whereClause}
ORDER BY r.period_month DESC, r.created_at DESC
LIMIT @pageSize OFFSET @offset";
                foreach (var kvp in parameters)
                {
                    dataCmd.Parameters.AddWithValue(kvp.Key, kvp.Value ?? DBNull.Value);
                }
                dataCmd.Parameters.AddWithValue("pageSize", pageSize);
                dataCmd.Parameters.AddWithValue("offset", offset);
                await using var reader = await dataCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var metadata = reader.IsDBNull(10) ? null : JsonNode.Parse(reader.GetString(10))?.AsObject();
                    items.Add(new
                    {
                        id = reader.GetGuid(0),
                        periodMonth = reader.GetString(1),
                        runType = reader.GetString(2),
                        status = reader.GetString(3),
                        totalAmount = reader.GetDecimal(4),
                        createdAt = reader.GetFieldValue<DateTimeOffset>(5),
                        updatedAt = reader.GetFieldValue<DateTimeOffset>(6),
                        policyId = reader.IsDBNull(7) ? (Guid?)null : reader.GetGuid(7),
                        policyCode = reader.IsDBNull(8) ? null : reader.GetString(8),
                        policyName = reader.IsDBNull(9) ? null : reader.GetString(9),
                        metadata,
                        entryCount = reader.IsDBNull(11) ? 0 : reader.GetInt32(11)
                    });
                }
            }

            return Results.Json(new
            {
                page,
                pageSize,
                total,
                items
            });
        }).RequireAuthorization();

        // GET /payroll/runs/{runId}
        // Fetches metadata plus high-level entry info for a particular payroll run so that
        // operators can see when it was executed and by which policy. Includes paged entry list.
        app.MapGet("/payroll/runs/{runId:guid}", async (Guid runId, HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString();

            await using var conn = await ds.OpenConnectionAsync(ct);
            object? runDetail = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT r.id, r.period_month, r.run_type, r.status, r.total_amount, r.metadata, r.created_at, r.updated_at,
       r.policy_id, COALESCE(p.payload->>'code','') AS policy_code, COALESCE(p.payload->>'name','') AS policy_name
FROM payroll_runs r
LEFT JOIN payroll_policies p ON p.id = r.policy_id AND p.company_code = r.company_code
WHERE r.company_code = @company AND r.id = @runId
LIMIT 1";
                cmd.Parameters.AddWithValue("company", companyCode);
                cmd.Parameters.AddWithValue("runId", runId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    return Results.NotFound(new { error = "run not found" });
                var metadata = reader.IsDBNull(5) ? null : JsonNode.Parse(reader.GetString(5))?.AsObject();
                runDetail = new
                {
                    id = reader.GetGuid(0),
                    periodMonth = reader.GetString(1),
                    runType = reader.GetString(2),
                    status = reader.GetString(3),
                    totalAmount = reader.GetDecimal(4),
                    metadata,
                    createdAt = reader.GetFieldValue<DateTimeOffset>(6),
                    updatedAt = reader.GetFieldValue<DateTimeOffset>(7),
                    policyId = reader.IsDBNull(8) ? (Guid?)null : reader.GetGuid(8),
                    policyCode = reader.IsDBNull(9) ? null : reader.GetString(9),
                    policyName = reader.IsDBNull(10) ? null : reader.GetString(10)
                };
            }

            var entries = new List<object>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT id, employee_id, employee_code, employee_name, department_code, total_amount, diff_summary, metadata, created_at
FROM payroll_run_entries
WHERE run_id = @runId
ORDER BY employee_name NULLS LAST, employee_code NULLS LAST";
                cmd.Parameters.AddWithValue("runId", runId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var diff = reader.IsDBNull(6) ? null : JsonNode.Parse(reader.GetString(6))?.AsObject();
                    var metadata = reader.IsDBNull(7) ? null : JsonNode.Parse(reader.GetString(7))?.AsObject();
                    entries.Add(new
                    {
                        entryId = reader.GetGuid(0),
                        employeeId = reader.GetGuid(1),
                        employeeCode = reader.IsDBNull(2) ? null : reader.GetString(2),
                        employeeName = reader.IsDBNull(3) ? null : reader.GetString(3),
                        departmentCode = reader.IsDBNull(4) ? null : reader.GetString(4),
                        totalAmount = reader.GetDecimal(5),
                        diffSummary = diff,
                        metadata,
                        createdAt = reader.GetFieldValue<DateTimeOffset>(8)
                    });
                }
            }

            return Results.Json(new
            {
                run = runDetail,
                entries
            });
        }).RequireAuthorization();

        // GET /payroll/runs/{runId}/entries/{entryId}
        // Returns the persisted payroll sheet, accounting draft, trace (if available), metadata, and
        // voucher linkage for a specific entry. Used by PayrollHistory drawer.
        app.MapGet("/payroll/runs/{runId:guid}/entries/{entryId:guid}", async (Guid runId, Guid entryId, HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString();

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT r.company_code, r.period_month, r.run_type,
       e.id, e.employee_id, e.employee_code, e.employee_name, e.department_code,
       e.total_amount, e.payroll_sheet, e.accounting_draft, e.diff_summary, e.metadata, t.trace,
       e.voucher_id, e.voucher_no
FROM payroll_run_entries e
JOIN payroll_runs r ON e.run_id = r.id
LEFT JOIN payroll_run_traces t ON t.entry_id = e.id
WHERE r.company_code = @company AND e.run_id = @runId AND e.id = @entryId
LIMIT 1";
            cmd.Parameters.AddWithValue("company", companyCode);
            cmd.Parameters.AddWithValue("runId", runId);
            cmd.Parameters.AddWithValue("entryId", entryId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return Results.NotFound(new { error = "entry not found" });

            var payrollSheet = reader.IsDBNull(9) ? null : JsonNode.Parse(reader.GetString(9));
            var accountingDraft = reader.IsDBNull(10) ? null : JsonNode.Parse(reader.GetString(10));
            var diffSummary = reader.IsDBNull(11) ? null : JsonNode.Parse(reader.GetString(11));
            var metadata = reader.IsDBNull(12) ? null : JsonNode.Parse(reader.GetString(12));
            var trace = reader.IsDBNull(13) ? null : JsonNode.Parse(reader.GetString(13));
            var voucherId = reader.IsDBNull(14) ? (Guid?)null : reader.GetGuid(14);
            var voucherNo = reader.IsDBNull(15) ? null : reader.GetString(15);

            return Results.Json(new
            {
                runType = reader.GetString(2),
                periodMonth = reader.GetString(1),
                entryId = reader.GetGuid(3),
                employeeId = reader.GetGuid(4),
                employeeCode = reader.IsDBNull(5) ? null : reader.GetString(5),
                employeeName = reader.IsDBNull(6) ? null : reader.GetString(6),
                departmentCode = reader.IsDBNull(7) ? null : reader.GetString(7),
                totalAmount = reader.GetDecimal(8),
                payrollSheet,
                accountingDraft,
                diffSummary,
                metadata,
                trace,
                voucherId,
                voucherNo
            });
        }).RequireAuthorization();

        // GET /payroll/run-entries
        // Search endpoint returning paged payroll entries across runs. Supports filters like month,
        // runType, policyId, employeeId, keyword. Used by PayrollHistory list to display records.
        app.MapGet("/payroll/run-entries", async (HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString();
            var month = req.Query["month"].ToString();
            var runType = NormalizeRunType(req.Query["runType"].ToString());
            var policyId = ParseGuid(req.Query["policyId"].ToString());
            var employeeId = ParseGuid(req.Query["employeeId"].ToString());
            var keyword = req.Query["keyword"].ToString();
            var page = int.TryParse(req.Query["page"], out var p) ? Math.Max(1, p) : 1;
            var pageSize = int.TryParse(req.Query["pageSize"], out var ps) ? Math.Clamp(ps, 1, 200) : 20;
            var offset = (page - 1) * pageSize;

            await using var conn = await ds.OpenConnectionAsync(ct);
            var filters = new List<string> { "r.company_code = @company" };
            var parameters = new Dictionary<string, object?>
            {
                ["company"] = companyCode
            };
            if (!string.IsNullOrWhiteSpace(month))
            {
                filters.Add("r.period_month = @month");
                parameters["month"] = month;
            }
            if (!string.IsNullOrWhiteSpace(runType) && !string.Equals(runType, "all", StringComparison.OrdinalIgnoreCase))
            {
                filters.Add("r.run_type = @runType");
                parameters["runType"] = runType;
            }
            if (policyId.HasValue)
            {
                filters.Add("r.policy_id = @policyId");
                parameters["policyId"] = policyId.Value;
            }
            if (employeeId.HasValue)
            {
                filters.Add("e.employee_id = @employeeId");
                parameters["employeeId"] = employeeId.Value;
            }
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                filters.Add("(COALESCE(e.employee_code,'') ILIKE @keyword OR COALESCE(e.employee_name,'') ILIKE @keyword)");
                parameters["keyword"] = $"%{keyword}%";
            }
            var whereClause = filters.Count > 0 ? string.Join(" AND ", filters) : "1=1";

            long total = 0;
            await using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = $@"
SELECT COUNT(*)
FROM payroll_run_entries e
JOIN payroll_runs r ON e.run_id = r.id
WHERE {whereClause}";
                foreach (var kvp in parameters)
                {
                    countCmd.Parameters.AddWithValue(kvp.Key, kvp.Value ?? DBNull.Value);
                }
                var scalar = await countCmd.ExecuteScalarAsync(ct);
                total = scalar switch
                {
                    null => 0,
                    long l => l,
                    int i => i,
                    decimal d => (long)d,
                    _ => Convert.ToInt64(scalar)
                };
            }

            var items = new List<object>();
            await using (var dataCmd = conn.CreateCommand())
            {
                dataCmd.CommandText = $@"
SELECT e.id,
       e.run_id,
       e.employee_id,
       e.employee_code,
       e.employee_name,
       e.department_code,
       e.total_amount,
       e.diff_summary,
       e.created_at,
       r.period_month,
       r.run_type,
       r.policy_id,
       COALESCE(p.payload->>'code','') AS policy_code,
       COALESCE(p.payload->>'name','') AS policy_name,
       e.voucher_id,
       e.voucher_no
FROM payroll_run_entries e
JOIN payroll_runs r ON e.run_id = r.id
LEFT JOIN payroll_policies p ON p.id = r.policy_id AND p.company_code = r.company_code
WHERE {whereClause}
ORDER BY r.period_month DESC, e.created_at DESC
LIMIT @pageSize OFFSET @offset";
                foreach (var kvp in parameters)
                {
                    dataCmd.Parameters.AddWithValue(kvp.Key, kvp.Value ?? DBNull.Value);
                }
                dataCmd.Parameters.AddWithValue("pageSize", pageSize);
                dataCmd.Parameters.AddWithValue("offset", offset);
                await using var reader = await dataCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var diff = reader.IsDBNull(7) ? null : JsonNode.Parse(reader.GetString(7))?.AsObject();
                    items.Add(new
                    {
                        entryId = reader.GetGuid(0),
                        runId = reader.GetGuid(1),
                        employeeId = reader.GetGuid(2),
                        employeeCode = reader.IsDBNull(3) ? null : reader.GetString(3),
                        employeeName = reader.IsDBNull(4) ? null : reader.GetString(4),
                        departmentCode = reader.IsDBNull(5) ? null : reader.GetString(5),
                        totalAmount = reader.GetDecimal(6),
                        diffSummary = diff,
                        createdAt = reader.GetFieldValue<DateTimeOffset>(8),
                        periodMonth = reader.GetString(9),
                        runType = reader.GetString(10),
                    policyId = reader.IsDBNull(11) ? (Guid?)null : reader.GetGuid(11),
                    policyCode = reader.IsDBNull(12) ? null : reader.GetString(12),
                    policyName = reader.IsDBNull(13) ? null : reader.GetString(13),
                    voucherId = reader.IsDBNull(14) ? (Guid?)null : reader.GetGuid(14),
                    voucherNo = reader.IsDBNull(15) ? null : reader.GetString(15)
                    });
                }
            }

            return Results.Json(new
            {
                page,
                pageSize,
                total,
                items
            });
        }).RequireAuthorization();

        // Historical payroll preview endpoint has been removed; the new flow relies on AI compilation plus local execution.

        // POST /ai/payroll/suggest-accounts
        // Stub helper that returns placeholder account suggestions for payroll items. Currently
        // used as a scaffold so the UI can request suggestions before full AI implementation lands.
        app.MapPost("/ai/payroll/suggest-accounts", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var items = root.TryGetProperty("items", out var it) && it.ValueKind==JsonValueKind.Array ? it.EnumerateArray().Select(x => x.GetString()).Where(s=>!string.IsNullOrWhiteSpace(s)).ToArray() : Array.Empty<string>();
            var map = items.ToDictionary(k => k!, v => new { accountCode = (string?)null, contraAccountCode = (string?)null });
            return Results.Ok(new { suggestions = map, note = "placeholder suggestions" });
        }).RequireAuthorization();
    }

    private static async Task<IResult> ExecutePayrollEndpoint(HttpRequest req, NpgsqlDataSource ds, LawDatasetService law, CancellationToken ct)
    {
        if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
            return Results.BadRequest(new { error = "Missing x-company-code" });

        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        var root = doc.RootElement;
        var employeeIdText = root.TryGetProperty("employeeId", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        var month = root.TryGetProperty("month", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var policyIdText = root.TryGetProperty("policyId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var debug = root.TryGetProperty("debug", out var dbg) && ((dbg.ValueKind == JsonValueKind.True) || (dbg.ValueKind == JsonValueKind.String && bool.TryParse(dbg.GetString(), out var bv) && bv));

        if (string.IsNullOrWhiteSpace(employeeIdText) || string.IsNullOrWhiteSpace(month))
            return Results.BadRequest(new { error = "employeeId and month required" });
        if (!Guid.TryParse(employeeIdText, out var employeeId))
            return Results.BadRequest(new { error = "employeeId invalid" });

        Guid? policyId = null;
        if (!string.IsNullOrWhiteSpace(policyIdText))
        {
            if (!Guid.TryParse(policyIdText, out var pid))
                return Results.BadRequest(new { error = "policyId invalid" });
            policyId = pid;
        }

        try
        {
            var result = await ExecutePayrollInternal(ds, law, cc.ToString(), employeeId, month, policyId, debug, null, ct);
            if (debug)
            {
                return Results.Ok(new
                {
                    payrollSheet = result.PayrollSheet,
                    accountingDraft = result.AccountingDraft,
                    month,
                    employeeId = employeeIdText,
                    policyId = policyIdText,
                    trace = result.Trace,
                    employeeCode = result.EmployeeCode,
                    employeeName = result.EmployeeName,
                    departmentCode = result.DepartmentCode,
                    netAmount = result.NetAmount
                });
            }

            return Results.Ok(new
            {
                payrollSheet = result.PayrollSheet,
                accountingDraft = result.AccountingDraft,
                month,
                employeeId = employeeIdText,
                policyId = policyIdText,
                employeeCode = result.EmployeeCode,
                employeeName = result.EmployeeName,
                departmentCode = result.DepartmentCode,
                netAmount = result.NetAmount
            });
        }
        catch (PayrollExecutionException ex)
        {
            return Results.Json(ex.Payload, statusCode: ex.StatusCode);
        }
    }

    internal static async Task<PayrollExecutionResult> ExecutePayrollInternal(NpgsqlDataSource ds, LawDatasetService law, string companyCode, Guid employeeId, string month, Guid? policyId, bool debug, ManualWorkHoursInput? manualWorkHours, CancellationToken ct)
    {
        var trace = debug ? new List<object>() : null;
        string? employeeNameOut = null;
        var hasMonthRange = TryGetPayrollMonthRange(month, out var monthStart, out var monthEnd);

        await using var conn = await ds.OpenConnectionAsync(ct);
        string? empJson = null;
        string? employeeCodeOut = null;
        string? departmentCodeOut = null;
        string? departmentNameOut = null;
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT payload, employee_code FROM employees WHERE company_code=$1 AND id=$2 LIMIT 1";
            q.Parameters.AddWithValue(companyCode);
            q.Parameters.AddWithValue(employeeId);
            await using var rd = await q.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                empJson = rd.IsDBNull(0) ? null : rd.GetString(0);
                employeeCodeOut = rd.IsDBNull(1) ? null : rd.GetString(1);
            }
        }
        if (string.IsNullOrEmpty(empJson))
            throw new PayrollExecutionException(StatusCodes.Status404NotFound, new { error = "employee not found" }, "employee not found");
        using var empDoc = JsonDocument.Parse(empJson);
        var emp = empDoc.RootElement;
        var deptInfo = await ResolveDepartmentFromPayloadAsync(
            emp,
            hasMonthRange ? monthStart : null,
            hasMonthRange ? monthEnd : null,
            conn,
            companyCode,
            ct);
        if (!string.IsNullOrWhiteSpace(deptInfo.Code))
        {
            departmentCodeOut = deptInfo.Code;
        }
        if (!string.IsNullOrWhiteSpace(deptInfo.Name))
        {
            departmentNameOut = deptInfo.Name;
        }
        try
        {
            if (string.IsNullOrWhiteSpace(employeeNameOut))
            {
                if (emp.TryGetProperty("nameKanji", out var nk) && nk.ValueKind == JsonValueKind.String)
                    employeeNameOut = nk.GetString();
                else if (emp.TryGetProperty("name", out var nn) && nn.ValueKind == JsonValueKind.String)
                    employeeNameOut = nn.GetString();
            }
        }
        catch { }
        try
        {
            if (string.IsNullOrWhiteSpace(employeeCodeOut) && emp.TryGetProperty("code", out var ec) && ec.ValueKind==JsonValueKind.String)
                employeeCodeOut = ec.GetString();
        } catch {}
        try
        {
            if (string.IsNullOrWhiteSpace(departmentCodeOut) && emp.TryGetProperty("departmentCode", out var dc) && dc.ValueKind==JsonValueKind.String)
                departmentCodeOut = dc.GetString();
        } catch {}
        try
        {
            if (string.IsNullOrWhiteSpace(departmentNameOut))
            {
                if (emp.TryGetProperty("primaryDepartmentName", out var pdn) && pdn.ValueKind == JsonValueKind.String)
                    departmentNameOut = pdn.GetString();
                else if (emp.TryGetProperty("departmentName", out var dn) && dn.ValueKind == JsonValueKind.String)
                    departmentNameOut = dn.GetString();
            }
        } catch {}

        if (hasMonthRange && !IsEmployeeContractActiveForMonth(emp, monthStart, monthEnd, out var earliestContractStart))
        {
            var displayName = employeeNameOut ?? employeeCodeOut ?? employeeId.ToString();
            var startText = earliestContractStart?.ToString("yyyy-MM-dd") ?? "未設定";
            var errorMessage = $"{displayName} は {startText} 以降に入社予定のため、{month} の給与は計算できません。";
            throw new PayrollExecutionException(StatusCodes.Status400BadRequest, new
            {
                error = "employee_not_active_for_month",
                employeeId,
                employeeCode = employeeCodeOut,
                employeeName = employeeNameOut,
                month,
                contractStart = earliestContractStart?.ToString("yyyy-MM-dd")
            }, errorMessage);
        }
        // 从 salaries 数组中获取当前有效的工资描述
        string? GetActiveSalaryDescription(JsonElement employee, string targetMonth)
        {
            // 首先尝试从 salaries 数组读取
            if (employee.TryGetProperty("salaries", out var salariesArr) && salariesArr.ValueKind == JsonValueKind.Array)
            {
                var monthStart = targetMonth + "-01";
                string? activeSalary = null;
                string? latestStartDate = null;
                
                foreach (var sal in salariesArr.EnumerateArray())
                {
                    var startDate = sal.TryGetProperty("startDate", out var sd) && sd.ValueKind == JsonValueKind.String 
                        ? sd.GetString() 
                        : null;
                    var description = sal.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String 
                        ? desc.GetString() 
                        : null;
                    
                    if (string.IsNullOrWhiteSpace(startDate) || string.IsNullOrWhiteSpace(description))
                        continue;
                    
                    // 开始日期必须小于等于目标月份的第一天
                    if (string.Compare(startDate, monthStart, StringComparison.Ordinal) <= 0)
                    {
                        // 找最近的一条（开始日期最大的）
                        if (latestStartDate == null || string.Compare(startDate, latestStartDate, StringComparison.Ordinal) > 0)
                        {
                            latestStartDate = startDate;
                            activeSalary = description;
                        }
                    }
                }
                
                if (!string.IsNullOrWhiteSpace(activeSalary))
                    return activeSalary;
            }
            
            // 兜底：尝试读取旧的 nlPayrollDescription 字段
            if (employee.TryGetProperty("nlPayrollDescription", out var nl) && nl.ValueKind == JsonValueKind.String)
                return nl.GetString();
            
            return null;
        }
        
        var empNlDescription = GetActiveSalaryDescription(emp, month);
        
        if (debug)
        {
            trace?.Add(new { step="input.employee", employeeId = employeeId.ToString(), month, nl = empNlDescription });
        }

        decimal nlBase = 0m; decimal nlCommute = 0m;
        try
        {
            string? empNl = empNlDescription;
            if (!string.IsNullOrWhiteSpace(empNl))
            {
                decimal ParseAmountNearLocal(string src, string[] anchors)
                {
                    try
                    {
                        var normalizedSrc = NormalizeNumberText(src);
                        var normalizedAnchors = anchors.Select(a => NormalizeNumberText(a ?? string.Empty)).ToArray();
                        var idx = normalizedAnchors.Select(a => normalizedSrc.IndexOf(a, StringComparison.OrdinalIgnoreCase)).Where(i => i>=0).DefaultIfEmpty(-1).Min();
                        // If none of the anchors appear, do NOT parse a number from the whole text.
                        // Otherwise commuteAllowance would mistakenly capture base salary (e.g. "基本給20万円")
                        // and then be capped to 30,000.
                        if (idx < 0) return 0m;
                        var seg = normalizedSrc.Substring(idx);
                        var m1 = System.Text.RegularExpressions.Regex.Match(seg, @"(\d+(?:\.\d+)?)\s*万");
                        if (m1.Success) { var v = decimal.Parse(m1.Groups[1].Value); return Math.Round(v * 10000m, 0); }
                        var m2 = System.Text.RegularExpressions.Regex.Match(seg, @"(\d[\d,]*)\s*円");
                        if (m2.Success) { var v = decimal.Parse(m2.Groups[1].Value.Replace(",", "")); return v; }
                        var m3 = System.Text.RegularExpressions.Regex.Match(seg, @"(\d[\d,]*)");
                        if (m3.Success) { var v = decimal.Parse(m3.Groups[1].Value.Replace(",", "")); return v; }
                    }
                    catch {}
                    return 0m;
                }
                nlBase = ParseAmountNearLocal(empNl, new[]{"基本给","基本給","月給","月给","固定","基本工资","月薪"});
                nlCommute = ParseAmountNearLocal(empNl, CommuteKeywords);
            }
        } catch {}
        
        // 时薪判断和金额解析已移至 Policy 规则中处理
        // 后端不再硬编码时薪关键词，由 Policy 规则的 activation.salaryDescriptionContains 定义
        decimal nlHourlyRate = 0m;
        
        if (debug) trace?.Add(new { step="input.employee.nlParsed", baseAmount = nlBase, commuteAmount = nlCommute, salaryDescription = empNlDescription });

        // 先加载 Policy，因为需要从 Policy 规则中判断时薪条件
        JsonElement? policy = null;
        JsonDocument? policyDoc = null;
        string policySource = "none";
        if (policyId.HasValue)
        {
            await using var qp = conn.CreateCommand();
            qp.CommandText = "SELECT payload FROM payroll_policies WHERE company_code=$1 AND id=$2 LIMIT 1";
            qp.Parameters.AddWithValue(companyCode); qp.Parameters.AddWithValue(policyId.Value);
            var pj = (string?)await qp.ExecuteScalarAsync(ct);
            if (!string.IsNullOrEmpty(pj))
            {
                policyDoc?.Dispose();
                policyDoc = JsonDocument.Parse(pj);
                policy = policyDoc.RootElement;
                policySource = "specified";
            }
        }
        if (policy is null)
        {
            await using var qa = conn.CreateCommand();
            qa.CommandText = @"SELECT payload FROM payroll_policies 
                                   WHERE company_code=$1 AND (payload->>'isActive')='true' 
                                   ORDER BY created_at DESC LIMIT 1";
            qa.Parameters.AddWithValue(companyCode);
            var pj = (string?)await qa.ExecuteScalarAsync(ct);
            if (!string.IsNullOrEmpty(pj))
            {
                policyDoc?.Dispose();
                policyDoc = JsonDocument.Parse(pj);
                policy = policyDoc.RootElement;
                policySource = "active";
            }
            if (policy is null)
            {
                await using var ql = conn.CreateCommand();
                ql.CommandText = "SELECT payload FROM payroll_policies WHERE company_code=$1 ORDER BY created_at DESC LIMIT 1";
                ql.Parameters.AddWithValue(companyCode);
                var pj2 = (string?)await ql.ExecuteScalarAsync(ct);
                if (!string.IsNullOrEmpty(pj2))
                {
                    policyDoc?.Dispose();
                    policyDoc = JsonDocument.Parse(pj2);
                    policy = policyDoc.RootElement;
                    policySource = "latest";
                }
            }
        }
        if (debug) trace?.Add(new { step="policy.select", source=policySource });

        // 从 Policy 规则中扫描时薪条件，判断员工是否匹配
        bool isHourlyRateMode = false;
        string[]? hourlyKeywords = null;
        if (policy is JsonElement policyEl && policyEl.TryGetProperty("rules", out var rulesForScan) && rulesForScan.ValueKind == JsonValueKind.Array)
        {
            foreach (var rule in rulesForScan.EnumerateArray())
            {
                if (rule.TryGetProperty("activation", out var activation) && activation.ValueKind == JsonValueKind.Object)
                {
                    // 检查 salaryDescriptionContains 条件
                    if (activation.TryGetProperty("salaryDescriptionContains", out var containsArr) && containsArr.ValueKind == JsonValueKind.Array)
                    {
                        var keywords = new List<string>();
                        foreach (var kw in containsArr.EnumerateArray())
                        {
                            if (kw.ValueKind == JsonValueKind.String)
                            {
                                var keyword = kw.GetString();
                                if (!string.IsNullOrWhiteSpace(keyword))
                                {
                                    keywords.Add(keyword);
                                }
                            }
                        }
                        if (keywords.Count > 0 && !string.IsNullOrWhiteSpace(empNlDescription))
                        {
                            hourlyKeywords = keywords.ToArray();
                            isHourlyRateMode = keywords.Any(kw => empNlDescription.Contains(kw, StringComparison.OrdinalIgnoreCase));
                            if (isHourlyRateMode)
                            {
                                if (debug) trace?.Add(new { step = "policy.hourlyMode.detected", keywords = hourlyKeywords, salaryDescription = empNlDescription });
                                break;
                            }
                        }
                    }
                }
            }
        }

        // 如果是时薪模式，尝试从给与情报中解析时薪金额
        if (isHourlyRateMode && hourlyKeywords != null && !string.IsNullOrWhiteSpace(empNlDescription))
        {
            try
            {
                var normalizedSrc = NormalizeNumberText(empNlDescription);
                foreach (var kw in hourlyKeywords)
                {
                    var normalizedKw = NormalizeNumberText(kw);
                    var idx = normalizedSrc.IndexOf(normalizedKw, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        var seg = normalizedSrc.Substring(idx);
                        // 尝试匹配 "時給1500円" 或 "時給1500" 格式
                        var m1 = System.Text.RegularExpressions.Regex.Match(seg, @"(\d[\d,]*)\s*円");
                        if (m1.Success) { nlHourlyRate = decimal.Parse(m1.Groups[1].Value.Replace(",", "")); break; }
                        var m2 = System.Text.RegularExpressions.Regex.Match(seg, @"(\d[\d,]*)");
                        if (m2.Success) { nlHourlyRate = decimal.Parse(m2.Groups[1].Value.Replace(",", "")); break; }
                    }
                }
            }
            catch { }

            // 时薪模式但无法解析出时薪金额时，报错
            if (nlHourlyRate <= 0)
            {
                var displayName = employeeNameOut ?? employeeCodeOut ?? employeeId.ToString();
                throw new PayrollExecutionException(StatusCodes.Status400BadRequest, new
                {
                    error = "hourly_rate_parse_failed",
                    employeeId,
                    employeeCode = employeeCodeOut,
                    employeeName = employeeNameOut,
                    month,
                    salaryDescription = empNlDescription,
                    hourlyKeywords,
                    message = $"{displayName} の給与情報に時給キーワードがありますが、時給金額を解析できません。給与情報を確認してください。"
                }, $"{displayName} の時給金額を解析できません。");
            }
        }

        if (debug) trace?.Add(new { step = "policy.hourlyMode.result", isHourlyRateMode, nlHourlyRate, hourlyKeywords });

        var workSettings = await LoadCompanyWorkSettingsAsync(conn, companyCode, ct);
        var workHourSummary = await LoadWorkHourSummaryAsync(conn, companyCode, employeeCodeOut, month, workSettings, ct);
        
        // 处理缺少timesheet的情况
        bool usedStandardHours = false;
        bool usedManualHours = false;
        if (!workHourSummary.HasData)
        {
            if (isHourlyRateMode)
            {
                // 时薪模式：检查是否有手动输入的工时
                if (manualWorkHours != null && manualWorkHours.TotalHours > 0)
                {
                    // 使用手动输入的工时创建虚拟工时数据
                    workHourSummary = new WorkHourSummary(
                        totalHours: manualWorkHours.TotalHours,
                        regularHours: manualWorkHours.TotalHours, // 时薪模式下全部算作regular
                        overtime125Hours: 0,
                        overtime150Hours: 0,
                        holidayHours: 0,
                        lateNightHours: 0,
                        absenceHours: 0,
                        standardDailyHours: workSettings.StandardDailyHours,
                        overtimeThresholdHours: workSettings.OvertimeThresholdHours,
                        sourceEntries: 1, // 虚拟1条记录
                        lockedEntries: 1);
                    usedManualHours = true;
                    // 如果手动输入了时薪，覆盖解析出的时薪
                    if (manualWorkHours.HourlyRate > 0)
                    {
                        nlHourlyRate = manualWorkHours.HourlyRate;
                    }
                    if (debug)
                    {
                        trace?.Add(new { step = "info.usedManualHours", message = "使用手动输入的工时", totalHours = manualWorkHours.TotalHours, hourlyRate = nlHourlyRate });
                    }
                }
                else
                {
                    // 没有手动工时，返回特殊错误让前端处理
                    var displayName = employeeNameOut ?? employeeCodeOut ?? employeeId.ToString();
                    throw new PayrollExecutionException(StatusCodes.Status400BadRequest, new
                    {
                        error = "hourly_rate_missing_timesheet",
                        employeeId,
                        employeeCode = employeeCodeOut,
                        employeeName = employeeNameOut,
                        month,
                        hourlyRate = nlHourlyRate,
                        requiresManualHours = true,
                        message = $"{displayName} は時給制のため、工数データが必要です。手動で工時を入力するか、計算を中止してください。"
                    }, $"{displayName} は時給制のため、工数データが必要です。");
                }
            }
            else
            {
                // 月薪模式：缺少timesheet时，按标准工时计算（无加班无欠勤）
                workHourSummary = WorkHourSummary.CreateStandardMonthly(workSettings, month);
                usedStandardHours = true;
                if (debug)
                {
                    trace?.Add(new { step = "info.usedStandardHours", message = "缺少timesheet，按月标准工时计算（无加班无欠勤）", workDays = workHourSummary.SourceEntries, totalHours = workHourSummary.TotalHours });
                }
            }
        }
        
        JsonObject? workHoursJson = workHourSummary.HasData ? workHourSummary.ToJson() : null;
        if (workHoursJson is not null)
        {
            if (usedStandardHours) workHoursJson["usedStandardHours"] = true;
            if (usedManualHours) workHoursJson["usedManualHours"] = true;
        }
        
        if (debug)
        {
            trace?.Add(new
            {
                step = "input.workHours",
                total = workHourSummary.TotalHours,
                regular = workHourSummary.RegularHours,
                overtime = workHourSummary.Overtime125Hours,
                overtime60 = workHourSummary.Overtime150Hours,
                holiday = workHourSummary.HolidayHours,
                lateNight = workHourSummary.LateNightHours,
                absence = workHourSummary.AbsenceHours,
                sourceEntries = workHourSummary.SourceEntries,
                lockedEntries = workHourSummary.LockedEntries,
                usedStandardHours
            });
        }
        if (debug && policy is JsonElement policyElForDebug)
        {
            string? policyText = null;
            try
            {
                if (policyElForDebug.TryGetProperty("companyText", out var cte) && cte.ValueKind==JsonValueKind.String) policyText = cte.GetString();
                else if (policyElForDebug.TryGetProperty("nlText", out var nte) && nte.ValueKind==JsonValueKind.String) policyText = nte.GetString();
                else if (policyElForDebug.TryGetProperty("text", out var te) && te.ValueKind==JsonValueKind.String) policyText = te.GetString();
            } catch {}
            trace?.Add(new { step="input.policy", nl = policyText });
        }

        var sheetOut = new List<JsonObject>();
        JsonElement? rulesElement = null;
        JsonElement? policyBody = null;
        JsonDocument? compiledRulesDoc = null;
        JsonDocument? compiledJournalRulesDoc = null;
        JsonElement? compiledJournalRulesElement = null;
        string? policyTextCombined = null;
        string? employeeTextForPolicy = empNlDescription;

        if (policy is JsonElement pBody)
        {
            policyBody = pBody;
            policyTextCombined = ExtractPolicyText(pBody);

            if (pBody.TryGetProperty("rules", out var explicitRules) && explicitRules.ValueKind == JsonValueKind.Array && explicitRules.GetArrayLength() > 0)
            {
                rulesElement = explicitRules;
            }
            else
        {
            string? companyTextInPolicy = null;
            try
            {
                if (pBody.TryGetProperty("companyText", out var cte) && cte.ValueKind==JsonValueKind.String) companyTextInPolicy = cte.GetString();
                else if (pBody.TryGetProperty("nlText", out var nte) && nte.ValueKind==JsonValueKind.String) companyTextInPolicy = nte.GetString();
                else if (pBody.TryGetProperty("text", out var te) && te.ValueKind==JsonValueKind.String) companyTextInPolicy = te.GetString();
            } catch {}
            // 優先使用 salaries 配列から取得した empNlDescription、次に旧フィールド nlPayrollDescription
            var empText = !string.IsNullOrWhiteSpace(empNlDescription) 
                ? empNlDescription 
                : (emp.TryGetProperty("nlPayrollDescription", out var ne) && ne.ValueKind==JsonValueKind.String ? ne.GetString() : null);
            var combinedJournalText = CombineTextSegments(companyTextInPolicy, empText);
            var journalInstructionsFallback = ParseJournalInstructions(combinedJournalText);
            string ResolveJournalDebitLocal(string rule, string defaultCode) => ResolveAccountCodeForRule(rule, defaultCode, journalInstructionsFallback, true);
            string ResolveJournalCreditLocal(string rule, string defaultCode) => ResolveAccountCodeForRule(rule, defaultCode, journalInstructionsFallback, false);

            bool hasBaseEmp = !string.IsNullOrWhiteSpace(empText) && (
                empText.Contains("基本給", StringComparison.OrdinalIgnoreCase) || empText.Contains("基本给", StringComparison.OrdinalIgnoreCase) ||
                empText.Contains("月給", StringComparison.OrdinalIgnoreCase) || empText.Contains("月给", StringComparison.OrdinalIgnoreCase) ||
                empText.Contains("固定", StringComparison.OrdinalIgnoreCase) || empText.Contains("基本工资", StringComparison.OrdinalIgnoreCase) ||
                empText.Contains("月薪", StringComparison.OrdinalIgnoreCase)
            );
            bool hasBaseCompany = !string.IsNullOrWhiteSpace(companyTextInPolicy) && (
                companyTextInPolicy.Contains("基本給", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("基本给", StringComparison.OrdinalIgnoreCase) ||
                companyTextInPolicy.Contains("月給", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("月给", StringComparison.OrdinalIgnoreCase) ||
                companyTextInPolicy.Contains("固定", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("基本工资", StringComparison.OrdinalIgnoreCase)
            );
            bool wantsHealth = (!string.IsNullOrWhiteSpace(empText) && (empText.Contains("社会保険", StringComparison.OrdinalIgnoreCase) || empText.Contains("社保", StringComparison.OrdinalIgnoreCase) || empText.Contains("健康保険", StringComparison.OrdinalIgnoreCase)))
                               || (!string.IsNullOrWhiteSpace(companyTextInPolicy) && (companyTextInPolicy.Contains("社会保険", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("社保", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("健康保険", StringComparison.OrdinalIgnoreCase)));
            bool wantsPension = (!string.IsNullOrWhiteSpace(empText) && (empText.Contains("厚生年金", StringComparison.OrdinalIgnoreCase) || empText.Contains("年金", StringComparison.OrdinalIgnoreCase)))
                               || (!string.IsNullOrWhiteSpace(companyTextInPolicy) && (companyTextInPolicy.Contains("厚生年金", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("年金", StringComparison.OrdinalIgnoreCase)));
            bool wantsEmployment = (!string.IsNullOrWhiteSpace(empText) && (empText.Contains("雇用保険", StringComparison.OrdinalIgnoreCase) || empText.Contains("雇佣保险", StringComparison.OrdinalIgnoreCase)))
                               || (!string.IsNullOrWhiteSpace(companyTextInPolicy) && (companyTextInPolicy.Contains("雇用保険", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("雇佣保险", StringComparison.OrdinalIgnoreCase)));
            bool wantsWithholding = (!string.IsNullOrWhiteSpace(empText) && (empText.Contains("源泉", StringComparison.OrdinalIgnoreCase) || empText.Contains("月額表", StringComparison.OrdinalIgnoreCase) || empText.Contains("甲欄", StringComparison.OrdinalIgnoreCase)))
                               || (!string.IsNullOrWhiteSpace(companyTextInPolicy) && (companyTextInPolicy.Contains("源泉", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("月額表", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("甲欄", StringComparison.OrdinalIgnoreCase)));
            bool wantsOvertime = ContainsAny(empText, "残業", "時間外", "加班") || ContainsAny(companyTextInPolicy, "残業", "時間外", "加班");
            bool wantsOvertime60 = ContainsAny(empText, "60時間", "６０時間", "60h", "60小时") || ContainsAny(companyTextInPolicy, "60時間", "６０時間", "60h", "60小时");
            bool wantsHolidayWork = ContainsAny(empText, "休日", "祝日", "节假日", "節假日", "法定休日") || ContainsAny(companyTextInPolicy, "休日", "祝日", "节假日", "節假日", "法定休日");
            bool wantsLateNight = ContainsAny(empText, "深夜", "22時", "22点", "22點", "late night", "夜間") || ContainsAny(companyTextInPolicy, "深夜", "22時", "22点", "22點", "late night", "夜間");
            bool wantsAbsence = ContainsAny(empText, "欠勤", "欠勤控除", "欠勤・遅刻", "勤怠控除", "工时不足", "工時不足", "遅刻", "早退")
                                || ContainsAny(companyTextInPolicy, "欠勤", "欠勤控除", "欠勤・遅刻", "勤怠控除", "工时不足", "工時不足", "遅刻", "早退");

            var compiled = new List<object>();
            var journalRules = new List<object>();
            object BuildJournalItem(string code, decimal? sign = null) => sign.HasValue ? new { code, sign } : new { code };
            void AddJournalRuleLocal(string name, string debitAccount, string creditAccount, IEnumerable<object> journalItems, string? description = null)
            {
                var arrItems = journalItems?.ToArray() ?? Array.Empty<object>();
                if (arrItems.Length == 0) return;
                journalRules.Add(new
                {
                    name,
                    debitAccount,
                    creditAccount,
                    items = arrItems,
                    description
                });
            }
            // 时薪模式：由 policy 规则决定（例如时薪 × 工时），不在这里硬编码计算
            // 月薪模式：BASE = 月薪固定金额
            if (!isHourlyRateMode && (hasBaseEmp || hasBaseCompany))
            {
                object fobj; if (nlBase>0) fobj = new { _const = nlBase }; else fobj = new { charRef = "employee.baseSalaryMonth" };
                compiled.Add(new { item = "BASE", type = "earning", formula = fobj });
            }
                // 交通手当は員工の給与説明に記載がある場合、または nlCommute 金額がある場合のみ計算
                // 会社ポリシーの「交通手当」は仕訳説明のためのもので、全員に支給する意味ではない
                bool commuteEmp = ContainsAny(empText, CommuteKeywords) || nlCommute > 0;
            if (commuteEmp)
            {
                object fobj; if (nlCommute>0) fobj = new { _const = nlCommute, cap = 30000 }; else fobj = new { charRef = "employee.commuteAllowance", cap = 30000 };
                compiled.Add(new { item = "COMMUTE", type = "earning", formula = fobj });
            }
            if (wantsHealth)
            {
                object baseExpr; if (nlBase>0) baseExpr = new { _const = nlBase }; else baseExpr = new { baseRef = "employee.healthBase" };
                compiled.Add(new { item = "HEALTH_INS", type = "deduction", formula = new { _base = baseExpr, rate = "policy.law.health.rate" } });
            }
            if (wantsPension)
            {
                object baseExpr; if (nlBase>0) baseExpr = new { _const = nlBase }; else baseExpr = new { baseRef = "employee.pensionBase" };
                compiled.Add(new { item = "PENSION", type = "deduction", formula = new { _base = baseExpr, rate = "policy.law.pension.rate" } });
            }
            if (wantsEmployment)
            {
                object baseExpr; if (nlBase>0 || nlCommute>0) baseExpr = new { sum = new object[]{ new { _const = nlBase }, new { _const = nlCommute } } }; else baseExpr = new { baseRef = "employee.salaryTotal" };
                compiled.Add(new { item = "EMP_INS", type = "deduction", formula = new { _base = baseExpr, rate = "policy.law.employment.rate" } });
            }

            if (wantsWithholding)
            {
                compiled.Add(new { item = "WHT", type = "deduction", formula = new { withholding = new { category = "monthly_ko" } } });
            }

            // 月薪模式下的时薪计算（用于加班/控除），时薪模式不需要
            decimal effectiveHourlyRate = 0m;
            if (!isHourlyRateMode)
            {
                // 月薪模式：从月薪推算时薪
                decimal baseForHourly = nlBase;
                if (baseForHourly <= 0m)
                {
                    if (emp.TryGetProperty("baseSalaryMonth", out var bsm) && bsm.ValueKind == JsonValueKind.Number)
                    {
                        baseForHourly = bsm.TryGetDecimal(out var dec) ? dec : Convert.ToDecimal(bsm.GetDouble());
                    }
                    else if (emp.TryGetProperty("salaryTotal", out var st) && st.ValueKind == JsonValueKind.Number)
                    {
                        baseForHourly = st.TryGetDecimal(out var dec) ? dec : Convert.ToDecimal(st.GetDouble());
                    }
                }
                effectiveHourlyRate = CalculateHourlyRate(workHourSummary, baseForHourly);
            }
            // 时薪模式下不计算加班/休日/深夜/欠勤，只有月薪模式才需要
            if (!isHourlyRateMode && effectiveHourlyRate > 0m && workHourSummary.HasData)
            {
                decimal multiplierOvertime = GetPolicyMultiplier(policyBody, "overtime", 1.25m);
                decimal multiplierOvertime60 = GetPolicyMultiplier(policyBody, "overtime60", 1.5m);
                decimal multiplierHoliday = GetPolicyMultiplier(policyBody, "holiday", 1.35m);
                decimal multiplierLateNight = GetPolicyMultiplier(policyBody, "lateNight", 1.25m);

                decimal Rate(decimal multiplier) => Math.Round(effectiveHourlyRate * multiplier, 6, MidpointRounding.AwayFromZero);

                if (workHourSummary.Overtime125Hours > 0m)
                {
                    compiled.Add(new
                    {
                        item = "OVERTIME_STD",
                        type = "earning",
                        formula = new
                        {
                            _base = new { _const = workHourSummary.Overtime125Hours },
                            rate = Rate(multiplierOvertime)
                        }
                    });
                }
                if (workHourSummary.Overtime150Hours > 0m)
                {
                    compiled.Add(new
                    {
                        item = "OVERTIME_60",
                        type = "earning",
                        formula = new
                        {
                            _base = new { _const = workHourSummary.Overtime150Hours },
                            rate = Rate(multiplierOvertime60)
                        }
                    });
                }
                if (workHourSummary.HolidayHours > 0m)
                {
                    compiled.Add(new
                    {
                        item = "HOLIDAY_PAY",
                        type = "earning",
                        formula = new
                        {
                            _base = new { _const = workHourSummary.HolidayHours },
                            rate = Rate(multiplierHoliday)
                        }
                    });
                }
                if (workHourSummary.LateNightHours > 0m)
                {
                    compiled.Add(new
                    {
                        item = "LATE_NIGHT_PAY",
                        type = "earning",
                        formula = new
                        {
                            _base = new { _const = workHourSummary.LateNightHours },
                            rate = Rate(multiplierLateNight)
                        }
                    });
                }
                if (workHourSummary.AbsenceHours > 0m)
                {
                    compiled.Add(new
                    {
                        item = "ABSENCE_DEDUCT",
                        type = "deduction",
                        formula = new
                        {
                            _base = new { _const = workHourSummary.AbsenceHours },
                            rate = Rate(1m)
                        }
                    });
                }
            }

            var wageJournalItems = new List<object>();
            // 时薪制员工也需要 BASE 会计分录（时薪 × 工时 = 基本给）
            if (hasBaseEmp || hasBaseCompany || isHourlyRateMode) wageJournalItems.Add(BuildJournalItem("BASE"));
            if (commuteEmp) wageJournalItems.Add(BuildJournalItem("COMMUTE"));
            if (wantsOvertime) wageJournalItems.Add(BuildJournalItem("OVERTIME_STD"));
            if (wantsOvertime && wantsOvertime60) wageJournalItems.Add(BuildJournalItem("OVERTIME_60"));
            if (wantsHolidayWork) wageJournalItems.Add(BuildJournalItem("HOLIDAY_PAY"));
            if (wantsLateNight) wageJournalItems.Add(BuildJournalItem("LATE_NIGHT_PAY"));
            if (wantsAbsence) wageJournalItems.Add(BuildJournalItem("ABSENCE_DEDUCT", -1m));
            if (wageJournalItems.Count > 0)
            {
                AddJournalRuleLocal("wages", ResolveJournalDebitLocal("wages", "832"), ResolveJournalCreditLocal("wages", "315"), wageJournalItems, "基本給および各種手当を給与手当／未払費用で仕訳");
                if (debug) trace?.Add(new { step = "journal.builder", scope = "fallback", rule = "wages", items = wageJournalItems.Select(i => i.ToString()).ToArray() });
            }
            if (wantsHealth)
            {
                AddJournalRuleLocal("health", ResolveJournalDebitLocal("health", "3181"), ResolveJournalCreditLocal("health", "315"), new[] { BuildJournalItem("HEALTH_INS") }, "社会保険預り金／未払費用");
                if (debug) trace?.Add(new { step = "journal.builder", scope = "fallback", rule = "health" });
            }
            if (wantsPension)
            {
                AddJournalRuleLocal("pension", ResolveJournalDebitLocal("pension", "3182"), ResolveJournalCreditLocal("pension", "315"), new[] { BuildJournalItem("PENSION") }, "厚生年金預り金／未払費用");
                if (debug) trace?.Add(new { step = "journal.builder", scope = "fallback", rule = "pension" });
            }
            if (wantsEmployment)
            {
                AddJournalRuleLocal("employment", ResolveJournalDebitLocal("employment", "3183"), ResolveJournalCreditLocal("employment", "315"), new[] { BuildJournalItem("EMP_INS") }, "雇用保険預り金／未払費用");
                if (debug) trace?.Add(new { step = "journal.builder", scope = "fallback", rule = "employment" });
            }
            if (wantsWithholding)
            {
                AddJournalRuleLocal("withholding", ResolveJournalDebitLocal("withholding", "3184"), ResolveJournalCreditLocal("withholding", "315"), new[] { BuildJournalItem("WHT") }, "源泉所得税預り金／未払費用");
                if (debug) trace?.Add(new { step = "journal.builder", scope = "fallback", rule = "withholding" });
            }

            var arr = new System.Text.Json.Nodes.JsonArray(compiled.Select(o => System.Text.Json.JsonSerializer.SerializeToNode(o)!).ToArray());
                compiledRulesDoc = JsonDocument.Parse(arr.ToJsonString());
                rulesElement = compiledRulesDoc.RootElement;
            if (journalRules.Count > 0)
            {
                var jrArray = new System.Text.Json.Nodes.JsonArray(journalRules.Select(o => System.Text.Json.JsonSerializer.SerializeToNode(o)!).ToArray());
                compiledJournalRulesDoc = JsonDocument.Parse(jrArray.ToJsonString());
                compiledJournalRulesElement = compiledJournalRulesDoc.RootElement;
                if (debug)
                {
                    trace?.Add(new { step = "journal.rules.compiled", source = "fallback", count = journalRules.Count });
                }
            }
            if (debug) trace?.Add(new { step="rules.compiled", items = compiled.Select(o => (string)o.GetType().GetProperty("item")!.GetValue(o)!).ToArray() });
            }
        }

        if (compiledJournalRulesElement is null && policyBody.HasValue &&
            policyBody.Value.TryGetProperty("journalRules", out var jrExisting) &&
            jrExisting.ValueKind == JsonValueKind.Array && jrExisting.GetArrayLength() > 0)
        {
            compiledJournalRulesElement = jrExisting;
        }
        else if (compiledJournalRulesElement is null)
        {
            var combinedText = CombineTextSegments(policyTextCombined, employeeTextForPolicy);
            var generatedJournalRules = BuildJournalRulesFromText(combinedText);
            if (generatedJournalRules.Count > 0)
            {
                var jrArray = new JsonArray(generatedJournalRules.Select(o => JsonSerializer.SerializeToNode(o)!).ToArray());
                compiledJournalRulesDoc = JsonDocument.Parse(jrArray.ToJsonString());
                compiledJournalRulesElement = compiledJournalRulesDoc.RootElement;
                if (debug)
                {
                    trace?.Add(new { step = "journal.rules.compiled", source = "policyText", count = generatedJournalRules.Count });
                }
            }
        }

        if (rulesElement.HasValue && rulesElement.Value.ValueKind == JsonValueKind.Array && policyBody.HasValue)
            {
            var rulesEl = rulesElement.Value;
                var monthDate = DateTime.TryParse(month ?? string.Empty, out var md) ? md : DateTime.Today;
            var whtBrackets = await LoadWithholdingBracketsAsync(ds, companyCode, monthDate, ct);
            ApplyPolicyRules(
                rulesEl,
                sheetOut,
                emp,
                policyBody.Value,
                whtBrackets,
                nlBase,
                nlCommute,
                nlHourlyRate,
                debug,
                trace,
                law,
                monthDate,
                workHourSummary,
                policyBody,
                empNlDescription);
        }
        var journal = new List<JournalLine>();
        JsonElement? activeJournalRules = null;
        bool journalRulesFromPolicy = false;
        bool journalRulesFromCompiled = false;
        bool policyHasJournalRules = false;
        if (policyBody.HasValue && policyBody.Value.ValueKind == JsonValueKind.Object &&
            policyBody.Value.TryGetProperty("journalRules", out var jrFromPolicyCandidate))
        {
            policyHasJournalRules = jrFromPolicyCandidate.ValueKind == JsonValueKind.Array && jrFromPolicyCandidate.GetArrayLength() > 0;
            if (policyHasJournalRules)
            {
                activeJournalRules = jrFromPolicyCandidate;
                journalRulesFromPolicy = true;
            }
            if (debug)
            {
                trace?.Add(new { step = "journal.rules.policyCheck", hasProperty = true, entries = jrFromPolicyCandidate.GetArrayLength() });
            }
        }
        else if (policyBody.HasValue && debug)
        {
            trace?.Add(new { step = "journal.rules.policyCheck", hasProperty = false });
        }

        if (!journalRulesFromPolicy && compiledJournalRulesElement.HasValue &&
            compiledJournalRulesElement.Value.ValueKind == JsonValueKind.Array &&
            compiledJournalRulesElement.Value.GetArrayLength() > 0)
        {
            activeJournalRules = compiledJournalRulesElement.Value;
            journalRulesFromCompiled = true;
        }

        if (activeJournalRules.HasValue)
        {
            journal = BuildJournalLinesFromDsl(activeJournalRules.Value, sheetOut);
            if (debug)
            {
                trace?.Add(new
                {
                    step = "journal.rules.active",
                    fromPolicy = journalRulesFromPolicy,
                    fromCompiled = journalRulesFromCompiled,
                    lines = journal.Count
                });
            }
        }
        else if (debug)
        {
            trace?.Add(new { step = "journal.rules.missing" });
        }

        var codes = journal.Select(l => l.AccountCode).Distinct().ToArray();
        var accountNameByCode = new Dictionary<string, string?>();
        try
        {
            foreach (var code in codes)
            {
                await using var qa = conn.CreateCommand();
                qa.CommandText = "SELECT name FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1";
                qa.Parameters.AddWithValue(companyCode); qa.Parameters.AddWithValue(code);
                var name = (string?)await qa.ExecuteScalarAsync(ct);
                accountNameByCode[code] = name;
            }
        } catch {}

        var missing = codes.Where(code =>
        {
            string? nm;
            return !accountNameByCode.TryGetValue(code, out nm) || string.IsNullOrWhiteSpace(nm);
        }).ToArray();
        if (missing.Length > 0)
        {
            var msg = "会计科目不存在: " + string.Join(",", missing);
            if (debug)
                throw new PayrollExecutionException(StatusCodes.Status400BadRequest, new { error = msg, missingAccounts = missing, hint = "请在‘会计科目’主数据中维护，或调用 /admin/accounts/seed/payroll 初始化。" }, msg);
            throw new PayrollExecutionException(StatusCodes.Status400BadRequest, new { error = msg }, msg);
        }

        var enriched = new List<JsonObject>();
        foreach (var entry in journal)
        {
            accountNameByCode.TryGetValue(entry.AccountCode, out var an);
            var obj = new JsonObject
            {
                ["lineNo"] = entry.LineNo,
                ["accountCode"] = entry.AccountCode,
                ["accountName"] = an,
                ["drcr"] = entry.DrCr,
                ["amount"] = JsonValue.Create(entry.Amount),
                ["employeeCode"] = employeeCodeOut,
                ["departmentCode"] = departmentCodeOut,
                ["departmentName"] = departmentNameOut
            };
            enriched.Add(obj);
        }

        decimal netAmount = 0m;
        foreach (var entry in sheetOut)
        {
            var code = ReadJsonString(entry, "itemCode");
            var amount = ReadJsonDecimal(entry, "amount");
            if (amount == 0m) continue;
            if (IsDeductionItem(code)) netAmount -= amount;
            else netAmount += amount;
        }

        JsonArray? traceJson = null;
        if (trace is not null)
        {
            traceJson = JsonSerializer.SerializeToNode(trace, PayrollJsonSerializerOptions)?.AsArray();
        }

        compiledRulesDoc?.Dispose();
        compiledJournalRulesDoc?.Dispose();
        policyDoc?.Dispose();

        var warnings = new JsonArray();
        if (usedStandardHours)
        {
            warnings.Add(new JsonObject
            {
                ["code"] = "usedStandardHours",
                ["message"] = $"{month} の勤怠データがないため、月標準工時（{workHourSummary.TotalHours}時間）で計算しました。加班・欠勤控除は発生しません。"
            });
        }

        return new PayrollExecutionResult(sheetOut, enriched, traceJson, employeeCodeOut, employeeNameOut, departmentCodeOut, departmentNameOut, netAmount, workHoursJson, warnings);
    }

    private static async Task<List<WithholdingBracket>> LoadWithholdingBracketsAsync(
        NpgsqlDataSource ds,
        string companyCode,
        DateTime monthDate,
        CancellationToken ct)
    {
        var list = new List<WithholdingBracket>();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT COALESCE(min_amount,0), max_amount, rate, deduction, COALESCE(version,'')
                                              FROM withholding_rates
                                              WHERE (company_code IS NULL OR company_code = $1)
                                                AND category = 'monthly_ko'
                                                AND effective_from <= $2
                                                AND (effective_to IS NULL OR effective_to >= $2)
                                              ORDER BY min_amount NULLS FIRST";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(new DateTime(monthDate.Year, monthDate.Month, 1));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var min = reader.GetDecimal(0);
                var max = reader.IsDBNull(1) ? (decimal?)null : reader.GetDecimal(1);
                var rate = reader.GetDecimal(2);
                var deduction = reader.GetDecimal(3);
                var version = reader.GetString(4);
                list.Add(new WithholdingBracket(min, max, rate, deduction, version));
            }
        }
        catch
        {
            // ignore load errors; proceed with empty bracket list
        }

        return list;
    }

    private static TimeSpan? ParseTimeSpan(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (TimeSpan.TryParse(value, out var ts)) return ts;
        return null;
    }

    private static decimal RoundHours(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static async Task<CompanyWorkSettings> LoadCompanyWorkSettingsAsync(NpgsqlConnection conn, string companyCode, CancellationToken ct)
    {
        var settings = new CompanyWorkSettings();
        string? json = null;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT payload FROM company_settings WHERE company_code=$1 LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            json = (string?)await cmd.ExecuteScalarAsync(ct);
        }
        catch
        {
            return settings;
        }
        TimeSpan workStart = settings.WorkdayStart;
        TimeSpan workEnd = settings.WorkdayEnd;
        int lunchMinutes = settings.LunchMinutes;
        decimal overtimeThreshold = settings.OvertimeThresholdHours;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("workdayDefaultStart", out var ws) && ws.ValueKind == JsonValueKind.String)
                {
                    var ts = ParseTimeSpan(ws.GetString());
                    if (ts.HasValue) workStart = ts.Value;
                }
                if (root.TryGetProperty("workdayDefaultEnd", out var we) && we.ValueKind == JsonValueKind.String)
                {
                    var ts = ParseTimeSpan(we.GetString());
                    if (ts.HasValue) workEnd = ts.Value;
                }
                if (root.TryGetProperty("lunchMinutes", out var lm) && lm.ValueKind == JsonValueKind.Number)
                {
                    lunchMinutes = lm.TryGetInt32(out var lmi) ? lmi : lunchMinutes;
                }
                if (root.TryGetProperty("overtimeThresholdHours", out var oth) && oth.ValueKind == JsonValueKind.Number)
                {
                    overtimeThreshold = oth.TryGetDecimal(out var dec) ? dec : overtimeThreshold;
                }
            }
            catch
            {
                // ignore parsing errors
            }
        }

        if (lunchMinutes < 0) lunchMinutes = 0;
        var totalMinutes = (workEnd - workStart).TotalMinutes;
        if (totalMinutes <= 0)
        {
            totalMinutes += 24 * 60;
        }
        totalMinutes = Math.Max(0, totalMinutes - lunchMinutes);
        var standardHours = RoundHours((decimal)(totalMinutes / 60d));

        return new CompanyWorkSettings
        {
            WorkdayStart = workStart,
            WorkdayEnd = workEnd,
            LunchMinutes = lunchMinutes,
            StandardDailyHours = standardHours > 0 ? standardHours : settings.StandardDailyHours,
            OvertimeThresholdHours = overtimeThreshold <= 0 ? settings.OvertimeThresholdHours : overtimeThreshold
        };
    }

    private readonly struct TimesheetEntry
    {
        public TimesheetEntry(DateOnly date, TimeSpan? start, TimeSpan? end, int lunchMinutes, decimal? hours, string? status)
        {
            Date = date;
            Start = start;
            End = end;
            LunchMinutes = lunchMinutes;
            Hours = hours;
            Status = status;
        }

        public DateOnly Date { get; }
        public TimeSpan? Start { get; }
        public TimeSpan? End { get; }
        public int LunchMinutes { get; }
        public decimal? Hours { get; }
        public string? Status { get; }
        public bool IsLocked => !string.IsNullOrWhiteSpace(Status) && LockedTimesheetStatuses.Contains(Status);
    }

    private static string GetJapaneseDayOfWeek(DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Sunday => "日",
        DayOfWeek.Monday => "月",
        DayOfWeek.Tuesday => "火",
        DayOfWeek.Wednesday => "水",
        DayOfWeek.Thursday => "木",
        DayOfWeek.Friday => "金",
        DayOfWeek.Saturday => "土",
        _ => ""
    };

    private static bool IsWeekend(DateOnly date)
        => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private static bool IsJapanesePublicHoliday(DateOnly date)
        => JapaneseHolidayService.Instance.IsNationalHoliday(date);

    private static bool IsHoliday(DateOnly date) => IsWeekend(date) || IsJapanesePublicHoliday(date);

    private static decimal CalculateActualHours(TimesheetEntry entry)
    {
        if (entry.Hours.HasValue && entry.Hours.Value > 0)
        {
            return RoundHours(entry.Hours.Value);
        }

        if (!entry.Start.HasValue || !entry.End.HasValue)
        {
            return 0m;
        }

        var start = entry.Date.ToDateTime(TimeOnly.FromTimeSpan(entry.Start.Value));
        var end = entry.Date.ToDateTime(TimeOnly.FromTimeSpan(entry.End.Value));
        if (end <= start)
        {
            end = end.AddDays(1);
        }

        var minutes = (end - start).TotalMinutes - entry.LunchMinutes;
        if (minutes < 0) minutes = 0;
        return RoundHours((decimal)(minutes / 60d));
    }

    private static decimal CalculateLateNightHours(TimesheetEntry entry)
    {
        if (!entry.Start.HasValue || !entry.End.HasValue)
        {
            return 0m;
        }
        var start = entry.Date.ToDateTime(TimeOnly.FromTimeSpan(entry.Start.Value));
        var end = entry.Date.ToDateTime(TimeOnly.FromTimeSpan(entry.End.Value));
        if (end <= start)
        {
            end = end.AddDays(1);
        }
        var nightStart = entry.Date.ToDateTime(new TimeOnly(22, 0));
        var nightEnd = nightStart.AddHours(7);
        var overlapStart = start > nightStart ? start : nightStart;
        var overlapEnd = end < nightEnd ? end : nightEnd;
        if (overlapEnd <= overlapStart) return 0m;
        var minutes = (overlapEnd - overlapStart).TotalMinutes;
        return RoundHours((decimal)(minutes / 60d));
    }

    private static WorkHourSummary AggregateWorkHours(IReadOnlyList<TimesheetEntry> entries, CompanyWorkSettings workSettings)
    {
        if (entries.Count == 0) return WorkHourSummary.Empty;
        var locked = entries.Where(e => e.IsLocked).ToList();
        var dataset = locked.Count > 0 ? locked : entries;

        decimal total = 0m;
        decimal regular = 0m;
        decimal overtime = 0m;
        decimal holiday = 0m;
        decimal lateNight = 0m;
        decimal absence = 0m;

        foreach (var entry in dataset)
        {
            var actual = CalculateActualHours(entry);
            var isHoliday = IsHoliday(entry.Date);
            if (isHoliday)
            {
                holiday += actual;
            }
            else
            {
                regular += Math.Min(actual, workSettings.StandardDailyHours);
                var ot = Math.Max(0m, actual - workSettings.StandardDailyHours);
                overtime += ot;
                if (actual < workSettings.StandardDailyHours)
                {
                    absence += workSettings.StandardDailyHours - actual;
                }
            }
            total += actual;
            lateNight += CalculateLateNightHours(entry);
        }

        var overtime150 = Math.Max(0m, overtime - workSettings.OvertimeThresholdHours);
        var overtime125 = overtime - overtime150;

        return new WorkHourSummary(
            RoundHours(total),
            RoundHours(regular),
            RoundHours(overtime125),
            RoundHours(overtime150),
            RoundHours(holiday),
            RoundHours(lateNight),
            RoundHours(absence),
            workSettings.StandardDailyHours,
            workSettings.OvertimeThresholdHours,
            dataset.Count,
            locked.Count);
    }

    private static async Task<WorkHourSummary> LoadWorkHourSummaryAsync(
        NpgsqlConnection conn,
        string companyCode,
        string? employeeCode,
        string month,
        CompanyWorkSettings workSettings,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(employeeCode))
        {
            return WorkHourSummary.Empty;
        }

        var userIds = new List<string>();
        await using (var userCmd = conn.CreateCommand())
        {
            userCmd.CommandText = "SELECT id FROM users WHERE company_code=$1 AND employee_code=$2";
            userCmd.Parameters.AddWithValue(companyCode);
            userCmd.Parameters.AddWithValue(employeeCode);
            await using var reader = await userCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0))
                {
                    userIds.Add(reader.GetGuid(0).ToString());
                }
            }
        }
        if (userIds.Count == 0)
        {
            return WorkHourSummary.Empty;
        }

        var targetMonth = string.IsNullOrWhiteSpace(month)
            ? DateTime.Today.ToString("yyyy-MM")
            : month;

        var entries = new List<TimesheetEntry>();
        await using (var tsCmd = conn.CreateCommand())
        {
            tsCmd.CommandText = "SELECT payload FROM timesheets WHERE company_code=$1 AND month=$2 AND created_by = ANY($3)";
            tsCmd.Parameters.AddWithValue(companyCode);
            tsCmd.Parameters.AddWithValue(targetMonth);
            tsCmd.Parameters.AddWithValue(userIds.ToArray());
            await using var reader = await tsCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var payloadJson = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (string.IsNullOrWhiteSpace(payloadJson)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    var root = doc.RootElement;
                    var dateStr = root.TryGetProperty("date", out var de) && de.ValueKind == JsonValueKind.String ? de.GetString() : null;
                    if (!DateOnly.TryParse(dateStr, out var dateOnly)) continue;
                    var start = root.TryGetProperty("startTime", out var st) && st.ValueKind == JsonValueKind.String ? ParseTimeSpan(st.GetString()) : null;
                    var end = root.TryGetProperty("endTime", out var et) && et.ValueKind == JsonValueKind.String ? ParseTimeSpan(et.GetString()) : null;
                    var lunch = workSettings.LunchMinutes;
                    if (root.TryGetProperty("lunchMinutes", out var lm) && lm.ValueKind == JsonValueKind.Number)
                    {
                        lunch = lm.TryGetInt32(out var lmi) ? lmi : lunch;
                    }
                    decimal? hours = null;
                    if (root.TryGetProperty("hours", out var hv) && hv.ValueKind == JsonValueKind.Number)
                    {
                        hours = hv.TryGetDecimal(out var hd) ? hd : Convert.ToDecimal(hv.GetDouble());
                    }
                    var status = root.TryGetProperty("status", out var se) && se.ValueKind == JsonValueKind.String ? se.GetString() : null;
                    entries.Add(new TimesheetEntry(dateOnly, start, end, lunch, hours, status));
                }
                catch
                {
                    // skip malformed rows
                }
            }
        }

        if (entries.Count == 0)
        {
            return WorkHourSummary.Empty;
        }

        return AggregateWorkHours(entries, workSettings);
    }

    private static decimal CalculateHourlyRate(WorkHourSummary workHours, decimal monthlyBase)
    {
        if (!workHours.HasData) return 0m;
        if (monthlyBase <= 0m) return 0m;
        var denom = workHours.StandardDailyHours * Math.Max(1, workHours.SourceEntries);
        if (denom <= 0m) return 0m;
        return Math.Round(monthlyBase / denom, 4, MidpointRounding.AwayFromZero);
    }

    private static decimal GetPolicyMultiplier(JsonElement? policyRoot, string key, decimal fallback)
    {
        if (!policyRoot.HasValue) return fallback;
        var root = policyRoot.Value;
        if (root.ValueKind != JsonValueKind.Object) return fallback;
        if (!root.TryGetProperty("multipliers", out var section) || section.ValueKind != JsonValueKind.Object) return fallback;
        if (!section.TryGetProperty(key, out var value)) return fallback;
        try
        {
            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.TryGetDecimal(out var dec) ? dec : Convert.ToDecimal(value.GetDouble());
            }
            if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }
        catch { }
        return fallback;
    }

    private static void ApplyPolicyRules(
        JsonElement rulesEl,
        List<JsonObject> sheetOut,
        JsonElement emp,
        JsonElement policyBody,
        IReadOnlyList<WithholdingBracket> whtBrackets,
        decimal nlBase,
        decimal nlCommute,
        decimal nlHourlyRate,
        bool debug,
        List<object>? trace,
        LawDatasetService law,
        DateTime monthDate,
        WorkHourSummary workHours,
        JsonElement? policyRoot,
        string? empSalaryDescription = null)
    {
        // 辅助函数：检查员工是否匹配规则的 activation 条件
        bool CheckActivation(JsonElement rule)
        {
            if (!rule.TryGetProperty("activation", out var activation) || activation.ValueKind != JsonValueKind.Object)
            {
                return true; // 没有 activation 条件，默认执行
            }

            // 检查 salaryDescriptionContains 条件
            if (activation.TryGetProperty("salaryDescriptionContains", out var containsArr) && containsArr.ValueKind == JsonValueKind.Array)
            {
                if (string.IsNullOrWhiteSpace(empSalaryDescription))
                {
                    return false; // 员工没有给与情报，不匹配
                }
                bool matches = false;
                foreach (var kw in containsArr.EnumerateArray())
                {
                    if (kw.ValueKind == JsonValueKind.String)
                    {
                        var keyword = kw.GetString();
                        if (!string.IsNullOrWhiteSpace(keyword) && empSalaryDescription.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        {
                            matches = true;
                            break;
                        }
                    }
                }
                if (!matches) return false;
            }

            // 检查 salaryDescriptionNotContains 条件（排除条件）
            if (activation.TryGetProperty("salaryDescriptionNotContains", out var notContainsArr) && notContainsArr.ValueKind == JsonValueKind.Array)
            {
                if (!string.IsNullOrWhiteSpace(empSalaryDescription))
                {
                    foreach (var kw in notContainsArr.EnumerateArray())
                    {
                        if (kw.ValueKind == JsonValueKind.String)
                        {
                            var keyword = kw.GetString();
                            if (!string.IsNullOrWhiteSpace(keyword) && empSalaryDescription.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                            {
                                return false; // 匹配排除关键词，不执行
                            }
                        }
                    }
                }
            }

            // 旧的 isHourlyRateMode 条件已废弃，保留向后兼容
            // 建议使用 salaryDescriptionContains 替代

            return true;
        }
        decimal ReadWorkHoursValue(string key)
        {
            return workHours.GetScalar(string.IsNullOrWhiteSpace(key) ? "total" : key);
        }

        decimal ReadFromPath(string path)
        {
            try
            {
                        if (string.IsNullOrWhiteSpace(path)) return 0m;
                        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return 0m;
                var isEmployee = parts[0].Equals("employee", StringComparison.OrdinalIgnoreCase);
                var finalKey = parts[^1];
                if (isEmployee)
                {
                    if (parts.Length >= 2 && parts[1].Equals("workHours", StringComparison.OrdinalIgnoreCase))
                    {
                        var wk = parts.Length >= 3 ? parts[2] : "total";
                        return ReadWorkHoursValue(wk);
                    }
                    if (PayrollAmountKeys.Contains(finalKey))
                    {
                        return ReadEmployeeOverride(finalKey);
                    }
                }
                        JsonElement cur;
                if (isEmployee) cur = emp;
                else if (parts[0].Equals("policy", StringComparison.OrdinalIgnoreCase)) cur = policyBody;
                        else return 0m;

                for (var i = 1; i < parts.Length; i++)
                {
                    if (!cur.TryGetProperty(parts[i], out var next))
                    {
                        if (isEmployee)
                        {
                            if (parts.Length >= 2 && parts[1].Equals("workHours", StringComparison.OrdinalIgnoreCase))
                            {
                                var wk = parts.Length >= 3 ? parts[2] : "total";
                                return ReadWorkHoursValue(wk);
                            }
                            if (PayrollAmountKeys.Contains(finalKey))
                            {
                                return ReadEmployeeOverride(finalKey);
                            }
                        }
                        return 0m;
                    }
                    cur = next;
                }

                decimal value = 0m;
                if (cur.ValueKind == JsonValueKind.Number)
                {
                    value = cur.TryGetDecimal(out var d) ? d : Convert.ToDecimal(cur.GetDouble());
                }
                else if (cur.ValueKind == JsonValueKind.String && decimal.TryParse(cur.GetString(), out var sd))
                {
                    value = sd;
                }

                if (value == 0m && isEmployee && PayrollAmountKeys.Contains(finalKey))
                {
                    return ReadEmployeeOverride(finalKey);
                }

                return value;
            }
            catch
            {
                    return 0m;
                }
        }

        decimal ReadEmployeeOverride(string key)
        {
            key ??= string.Empty;
            if (key.Equals("baseSalaryMonth", StringComparison.OrdinalIgnoreCase)) return nlBase > 0 ? nlBase : 0m;
            if (key.Equals("commuteAllowance", StringComparison.OrdinalIgnoreCase)) return nlCommute > 0 ? nlCommute : 0m;
            if (key.Equals("salaryTotal", StringComparison.OrdinalIgnoreCase)) return (nlBase > 0 ? nlBase : 0m) + (nlCommute > 0 ? nlCommute : 0m);
            if (key.Equals("healthBase", StringComparison.OrdinalIgnoreCase)) return nlBase > 0 ? nlBase : 0m;
            if (key.Equals("pensionBase", StringComparison.OrdinalIgnoreCase)) return nlBase > 0 ? nlBase : 0m;
            if (key.Equals("hourlyRate", StringComparison.OrdinalIgnoreCase)) return nlHourlyRate > 0 ? nlHourlyRate : 0m;
                    return 0m;
                }

        FormulaEvalResult EvalHourlyPay(JsonElement hourlyNode, JsonElement? roundingOverride)
        {
            if (!workHours.HasData) return new FormulaEvalResult(0m, null, null, null, "hourlyPay:no_workhours");

            static decimal ParseDecimal(JsonElement element)
            {
                if (element.ValueKind != JsonValueKind.Number) return 0m;
                return element.TryGetDecimal(out var dec) ? dec : Convert.ToDecimal(element.GetDouble());
            }

            decimal hours = 0m;
            if (hourlyNode.TryGetProperty("hoursRef", out var hoursRef) && hoursRef.ValueKind == JsonValueKind.String)
            {
                hours = ReadFromPath(hoursRef.GetString()!);
            }
            else if (hourlyNode.TryGetProperty("hoursKind", out var hoursKind) && hoursKind.ValueKind == JsonValueKind.String)
            {
                hours = ReadWorkHoursValue(hoursKind.GetString()!);
            }
            else if (hourlyNode.TryGetProperty("hours", out var hoursValue))
            {
                hours = ParseDecimal(hoursValue);
            }
            if (hours <= 0m) return new FormulaEvalResult(0m, null, hours, null, "hourlyPay:no_hours");

            // 检查是否使用直接时薪模式（directRate: true 或 baseRef 指向 employee.hourlyRate）
            bool useDirectRate = false;
            if (hourlyNode.TryGetProperty("directRate", out var directRateNode) && directRateNode.ValueKind == JsonValueKind.True)
            {
                useDirectRate = true;
            }
            string? baseRefPath = null;
            if (hourlyNode.TryGetProperty("baseRef", out var baseRef) && baseRef.ValueKind == JsonValueKind.String)
            {
                baseRefPath = baseRef.GetString();
                // 如果 baseRef 指向 employee.hourlyRate，则使用直接时薪模式
                if (baseRefPath != null && baseRefPath.Equals("employee.hourlyRate", StringComparison.OrdinalIgnoreCase))
                {
                    useDirectRate = true;
                }
            }

            decimal hourlyRate;
            if (useDirectRate)
            {
                // 直接时薪模式：baseRef 的值就是时薪，不需要从月薪计算
                hourlyRate = baseRefPath != null ? ReadFromPath(baseRefPath) : 0m;
                if (hourlyRate <= 0m)
                {
                    hourlyRate = ReadEmployeeOverride("hourlyRate");
                }
                if (hourlyRate <= 0m) return new FormulaEvalResult(0m, null, hours, null, "hourlyPay:no_direct_rate");
            }
            else
            {
                // 传统模式：从月薪计算时薪
                decimal baseAmount = 0m;
                if (!string.IsNullOrWhiteSpace(baseRefPath))
                {
                    baseAmount = ReadFromPath(baseRefPath);
                }
                if (baseAmount <= 0m)
                {
                    baseAmount = ReadFromPath("employee.baseSalaryMonth");
                }
                if (baseAmount <= 0m)
                {
                    baseAmount = ReadFromPath("employee.salaryTotal");
                }
                if (baseAmount <= 0m)
                {
                    baseAmount = ReadEmployeeOverride("baseSalaryMonth");
                }
                if (baseAmount <= 0m) return new FormulaEvalResult(0m, null, hours, null, "hourlyPay:no_base");

                hourlyRate = CalculateHourlyRate(workHours, baseAmount);
                if (hourlyRate <= 0m) return new FormulaEvalResult(0m, null, hours, null, "hourlyPay:no_rate");
            }

            decimal multiplier = 1m;
            if (hourlyNode.TryGetProperty("multiplierRef", out var multiplierRef) && multiplierRef.ValueKind == JsonValueKind.String)
            {
                var multiplierVal = ReadFromPath(multiplierRef.GetString()!);
                if (multiplierVal > 0m) multiplier = multiplierVal;
            }
            if (hourlyNode.TryGetProperty("multiplier", out var multiplierNode))
            {
                var parsed = ParseDecimal(multiplierNode);
                if (parsed > 0m) multiplier = parsed;
            }

            var rate = Math.Round(hourlyRate * multiplier, 6, MidpointRounding.AwayFromZero);
            var amount = hours * rate;

            var roundingElement = roundingOverride;
            if (!roundingElement.HasValue && hourlyNode.TryGetProperty("rounding", out var localRounding) && localRounding.ValueKind == JsonValueKind.Object)
            {
                roundingElement = localRounding;
            }
            if (roundingElement.HasValue && roundingElement.Value.ValueKind == JsonValueKind.Object)
            {
                var roundingNode = roundingElement.Value;
                var method = roundingNode.TryGetProperty("method", out var me) ? me.GetString() : "round";
                var precision = roundingNode.TryGetProperty("precision", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetInt32() : 0;
                amount = ApplyRounding(amount, method, precision);
            }

            return new FormulaEvalResult(amount, rate, hours, null, useDirectRate ? "hourlyPay:direct" : "hourlyPay");
        }

        FormulaEvalResult evalFormula(JsonElement f, JsonElement? roundingOverride = null)
                {
            try
                        {
                if (f.TryGetProperty("_const", out var cst) && cst.ValueKind == JsonValueKind.Number)
                {
                    var valueConst = cst.TryGetDecimal(out var cd) ? cd : Convert.ToDecimal(cst.GetDouble());
                    return new FormulaEvalResult(valueConst, null, null, null, null);
                        }
                if (f.TryGetProperty("sum", out var sumVal) && sumVal.ValueKind == JsonValueKind.Array)
                        {
                    decimal acc = 0m;
                    foreach (var node in sumVal.EnumerateArray()) { var sub = evalFormula(node); acc += sub.Value; }
                    return new FormulaEvalResult(acc, null, null, null, null);
                        }
                if (f.TryGetProperty("hourlyPay", out var hourlyPayNode) && hourlyPayNode.ValueKind == JsonValueKind.Object)
                {
                    return EvalHourlyPay(hourlyPayNode, roundingOverride);
                }
                if (f.TryGetProperty("withholding", out var wht) && wht.ValueKind == JsonValueKind.Object)
                        {
                            decimal sumEarn = 0m, si = 0m, pens = 0m;
                    foreach (var entry in sheetOut)
                            {
                        var code = ReadJsonString(entry, "itemCode");
                        var amt = ReadJsonDecimal(entry, "amount");
                                if (string.Equals(code, "BASE", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "COMMUTE", StringComparison.OrdinalIgnoreCase)) sumEarn += amt;
                                else if (string.Equals(code, "HEALTH_INS", StringComparison.OrdinalIgnoreCase)) si += amt;
                                else if (string.Equals(code, "PENSION", StringComparison.OrdinalIgnoreCase)) pens += amt;
                            }
                    var taxable = sumEarn - si - pens;
                    if (taxable < 0) taxable = 0;
                    decimal rate = 0m, deduction = 0m; string? version = null;
                    foreach (var bracket in whtBrackets)
                    {
                        var geMin = taxable >= bracket.Min;
                        var ltMax = !bracket.Max.HasValue || taxable < bracket.Max.Value;
                        if (geMin && ltMax)
                        {
                            rate = bracket.Rate;
                            deduction = bracket.Deduction;
                            version = bracket.Version;
                            break;
                        }
                    }
                    if (rate == 0m) return new FormulaEvalResult(0m, null, taxable, null, "withholding:not_found");
                    var tax = ApplyRounding(Math.Max(0m, taxable * rate - deduction), "round", 0);
                    return new FormulaEvalResult(tax, rate, taxable, version, "withholding");
                }
                if (f.TryGetProperty("charRef", out var cref) && cref.ValueKind == JsonValueKind.String)
                        {
                            var key = cref.GetString()!;
                    decimal val;
                            if (key.StartsWith("employee.", StringComparison.OrdinalIgnoreCase))
                            {
                                var segs = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
                                if (segs.Length >= 2 && segs[1].Equals("workHours", StringComparison.OrdinalIgnoreCase))
                                {
                                    var wk = segs.Length >= 3 ? segs[2] : "total";
                                    val = ReadWorkHoursValue(wk);
                                }
                                else
                                {
                                    val = ReadFromPath(key);
                                }
                            }
                            else
                            {
                                val = ReadFromPath(key);
                            }
                    if (f.TryGetProperty("cap", out var cap2) && cap2.ValueKind == JsonValueKind.Number)
                    {
                        var capv = cap2.TryGetDecimal(out var cd2) ? cd2 : Convert.ToDecimal(cap2.GetDouble());
                                if (val > capv) val = capv;
                            }
                    return new FormulaEvalResult(val, null, null, null, null);
                        }
                if (f.TryGetProperty("ref", out var rref) && rref.ValueKind == JsonValueKind.String)
                        {
                            var key = rref.GetString()!;
                    decimal val;
                            if (key.StartsWith("employee.", StringComparison.OrdinalIgnoreCase))
                            {
                                var last = key.Split('.').Last();
                        val = ReadEmployeeOverride(last);
                        if (val == 0m) val = ReadFromPath(key);
                    }
                    else
                    {
                        val = ReadFromPath(key);
                    }
                    if (f.TryGetProperty("cap", out var cap) && cap.ValueKind == JsonValueKind.Number)
                    {
                        var capv = cap.TryGetDecimal(out var cd) ? cd : Convert.ToDecimal(cap.GetDouble());
                                if (val > capv) val = capv;
                            }
                    return new FormulaEvalResult(val, null, null, null, null);
                        }
                decimal baseVal = 0m;
                decimal? baseMark = null;
                if (f.TryGetProperty("_base", out var b) && b.ValueKind == JsonValueKind.Object)
                        {
                            var sub = evalFormula(b);
                    baseVal = sub.Value; baseMark = sub.Value;
                }
                if (baseVal == 0m && f.TryGetProperty("baseRef", out var br) && br.ValueKind == JsonValueKind.String)
                {
                    baseVal = ReadFromPath(br.GetString()!);
                }
                if (baseVal == 0m && f.TryGetProperty("baseRef", out var br2) && br2.ValueKind == JsonValueKind.String)
                        {
                            var key = br2.GetString()!;
                            if (key.StartsWith("employee.", StringComparison.OrdinalIgnoreCase))
                            {
                                var last = key.Split('.').Last();
                        var over = ReadEmployeeOverride(last);
                        if (over > 0) { baseVal = over; baseMark = over; }
                    }
                }
                if (baseVal == 0m && f.TryGetProperty("sum", out var sumEl) && sumEl.ValueKind == JsonValueKind.Array)
                {
                    decimal acc = 0m;
                    foreach (var node in sumEl.EnumerateArray()) { var sub = evalFormula(node); acc += sub.Value; }
                    baseVal = acc;
                    baseMark = acc;
                }
                decimal rateVal = 0m;
                decimal? rateMark = null;
                string? lawVer = null;
                string? lawNote = null;
                        if (f.TryGetProperty("rate", out var rt))
                        {
                    if (rt.ValueKind == JsonValueKind.Number)
                    {
                        rateVal = rt.TryGetDecimal(out var rd) ? rd : Convert.ToDecimal(rt.GetDouble());
                        rateMark = rateVal;
                    }
                    else if (rt.ValueKind == JsonValueKind.String)
                            {
                                var key = rt.GetString()!;
                        if (string.Equals(key, "policy.law.health.rate", StringComparison.OrdinalIgnoreCase))
                        {
                                var t = law.GetHealthRate(emp, policyBody, monthDate, baseVal);
                            rateVal = t.rate; rateMark = t.rate; lawVer = t.version; lawNote = $"health:{t.note}";
                        }
                        else if (string.Equals(key, "policy.law.pension.rate", StringComparison.OrdinalIgnoreCase))
                        {
                                var t = law.GetPensionRate(emp, policyBody, monthDate, baseVal);
                            rateVal = t.rate; rateMark = t.rate; lawVer = t.version; lawNote = $"pension:{t.note}";
                        }
                        else if (string.Equals(key, "policy.law.employment.rate", StringComparison.OrdinalIgnoreCase))
                        {
                            var t = law.GetEmploymentRate(emp, policyBody, monthDate);
                            rateVal = t.rate; rateMark = t.rate; lawVer = t.version; lawNote = $"employment:{t.note}";
                            if (debug)
                            {
                                trace?.Add(new
                                {
                                    step = "employment.rate",
                                    industry = (policyBody.TryGetProperty("law", out var lw) && lw.TryGetProperty("employmentIndustry", out var ei) ? ei.GetString() : null),
                                    resolved = t.version
                                });
                            }
                        }
                        else
                        {
                            rateVal = ReadFromPath(key);
                            rateMark = rateVal;
                        }
                            }
                        }
                        var res = baseVal * rateVal;
                var roundingElement = roundingOverride;
                if (!roundingElement.HasValue && f.TryGetProperty("rounding", out var rnd) && rnd.ValueKind == JsonValueKind.Object)
                {
                    roundingElement = rnd;
                }
                if (roundingElement.HasValue && roundingElement.Value.ValueKind == JsonValueKind.Object)
                {
                    var roundingNode = roundingElement.Value;
                    var method = roundingNode.TryGetProperty("method", out var me) ? me.GetString() : "round";
                    var precision = roundingNode.TryGetProperty("precision", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetInt32() : 2;
                    res = ApplyRounding(res, method, precision);
                }
                return new FormulaEvalResult(res, rateMark, baseMark ?? baseVal, lawVer, lawNote);
            }
            catch
            {
                return new FormulaEvalResult(0m, null, null, null, null);
            }
                }

                foreach (var rule in rulesEl.EnumerateArray())
                {
            var itemCode = rule.TryGetProperty("item", out var it) ? it.GetString() : (rule.TryGetProperty("itemCode", out var ic) ? ic.GetString() : null);
                    if (string.IsNullOrWhiteSpace(itemCode)) continue;

            // 检查 activation 条件（全部在 Policy 中定义）
            if (!CheckActivation(rule))
            {
                if (debug) trace?.Add(new { step = "rule.skip", item = itemCode, reason = "activation condition not met" });
                continue;
            }

            if (debug)
            {
                try
                {
                    trace?.Add(new { step = "rule.raw", item = itemCode, raw = rule.GetRawText() });
                }
                catch { }
            }
            decimal amount = 0m;
            decimal? rateMarkOut = null;
            decimal? baseMarkOut = null;
            string? lawVerOut = null;
            string? lawNoteOut = null;
            if (rule.TryGetProperty("formula", out var form) && form.ValueKind == JsonValueKind.Object)
            {
                JsonElement? roundingOverride = null;
                if (rule.TryGetProperty("rounding", out var ruleRounding) && ruleRounding.ValueKind == JsonValueKind.Object)
                {
                    roundingOverride = ruleRounding;
                }
                var r = evalFormula(form, roundingOverride);
                amount = r.Value;
                rateMarkOut = r.RateUsed;
                baseMarkOut = r.BaseUsed;
                lawVerOut = r.LawVer;
                lawNoteOut = r.LawNote;
            }
            if (debug)
            {
                trace?.Add(new
                {
                    step = "rule.eval",
                    item = itemCode,
                    amount,
                    @base = baseMarkOut,
                    rate = rateMarkOut,
                    lawVersion = lawVerOut,
                    lawNote = lawNoteOut,
                    note = amount == 0m ? "formula evaluated to 0" : null
                });
            }

                    if (amount != 0m)
                    {
                        var meta = new { formula = "policy", rate = rateMarkOut, @base = baseMarkOut, lawVersion = lawVerOut, lawNote = lawNoteOut };
                var node = new JsonObject
                {
                    ["itemCode"] = itemCode,
                    ["itemName"] = GetPayrollItemDisplayName(itemCode),
                    ["amount"] = JsonValue.Create(amount)
                };
                var metaNode = JsonSerializer.SerializeToNode(meta, PayrollJsonSerializerOptions);
                if (metaNode is not null) node["meta"] = metaNode;
                sheetOut.Add(node);
            }
        }

        if (debug)
        {
            trace?.Add(new { step = "rules.done", executed = sheetOut.Count > 0, count = sheetOut.Count });
        }
    }

    private static PayrollService.PayrollManualSaveEntry CreateManualSaveEntry(
        PayrollService.PayrollPreviewEntry entry,
        bool includeTrace,
        JsonObject? metadataOverride = null)
    {
        var sheetElement = JsonSerializer.SerializeToElement(entry.PayrollSheet, PayrollJsonSerializerOptions);
        var draftElement = JsonSerializer.SerializeToElement(entry.AccountingDraft, PayrollJsonSerializerOptions);
        JsonElement? diffElement = entry.DiffSummary is null
            ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(entry.DiffSummary, PayrollJsonSerializerOptions);
        JsonElement? traceElement = includeTrace && entry.Trace is not null
            ? JsonSerializer.SerializeToElement(entry.Trace, PayrollJsonSerializerOptions)
            : (JsonElement?)null;
        JsonElement? metadataElement = metadataOverride is null
            ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(metadataOverride, PayrollJsonSerializerOptions);
        return new PayrollService.PayrollManualSaveEntry
        {
            EmployeeId = entry.EmployeeId,
            EmployeeCode = entry.EmployeeCode,
            EmployeeName = entry.EmployeeName,
            DepartmentCode = entry.DepartmentCode,
            TotalAmount = entry.TotalAmount,
            PayrollSheet = sheetElement,
            AccountingDraft = draftElement,
            DiffSummary = diffElement,
            Trace = traceElement,
            Metadata = metadataElement
        };
    }

    private static bool ShouldFlagDiff(JsonObject? diff, decimal percentThreshold, decimal amountThreshold)
    {
        if (diff is null) return false;
        var amountDiff = ReadJsonDecimal(diff, "difference");
        if (Math.Abs(amountDiff) >= amountThreshold) return true;
        if (percentThreshold <= 0) return false;
        decimal percentDiff = ReadJsonDecimal(diff, "differencePercent");
        if (percentDiff == 0m)
        {
            var prevAvg = ReadJsonDecimal(diff, "previousAverage");
            if (prevAvg != 0m)
            {
                percentDiff = amountDiff / prevAvg;
            }
        }
        return Math.Abs(percentDiff) >= percentThreshold;
    }

    private static bool TryGetPayrollMonthRange(string? monthText, out DateOnly monthStart, out DateOnly monthEnd)
    {
        monthStart = default;
        monthEnd = default;
        if (string.IsNullOrWhiteSpace(monthText)) return false;

        bool TryParseExact(string format, out DateTime dt)
            => DateTime.TryParseExact(monthText, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);

        DateTime dateTime;
        if (TryParseExact("yyyy-MM", out dateTime) ||
            TryParseExact("yyyy/MM", out dateTime) ||
            TryParseExact("yyyyMM", out dateTime))
        {
            // parsed successfully
        }
        else if (DateTime.TryParseExact($"{monthText}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dateTime))
        {
            // month supplied without day, appended -01 worked
        }
        else if (DateTime.TryParse(monthText, CultureInfo.InvariantCulture, DateTimeStyles.None, out dateTime))
        {
            // fallback to general parser
        }
        else
        {
            return false;
        }

        dateTime = new DateTime(dateTime.Year, dateTime.Month, 1);
        monthStart = DateOnly.FromDateTime(dateTime);
        monthEnd = monthStart.AddMonths(1).AddDays(-1);
        return true;
    }

    private static DateOnly? ParseContractDate(JsonElement contract, string property)
    {
        if (contract.ValueKind != JsonValueKind.Object) return null;
        if (!contract.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.String) return null;
        var text = node.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            return exact;
        }
        if (DateOnly.TryParse(text, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static bool IsEmployeeContractActiveForMonth(JsonElement employeePayload, DateOnly monthStart, DateOnly monthEnd, out DateOnly? earliestContractStart)
    {
        earliestContractStart = null;
        if (!employeePayload.TryGetProperty("contracts", out var contracts) || contracts.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        var hasContracts = false;
        foreach (var contract in contracts.EnumerateArray())
        {
            hasContracts = true;
            var from = ParseContractDate(contract, "periodFrom");
            var to = ParseContractDate(contract, "periodTo");
            if (from.HasValue && (!earliestContractStart.HasValue || from.Value < earliestContractStart.Value))
            {
                earliestContractStart = from.Value;
            }

            var effectiveFrom = from ?? new DateOnly(1900, 1, 1);
            if (effectiveFrom > monthEnd) continue;
            if (to.HasValue && to.Value < monthStart) continue;

            if (contract.TryGetProperty("status", out var statusNode) && statusNode.ValueKind == JsonValueKind.String)
            {
                var statusText = statusNode.GetString();
                if (!string.IsNullOrWhiteSpace(statusText) && statusText.Equals("inactive", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            if (contract.TryGetProperty("inactive", out var inactiveNode) && inactiveNode.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            return true;
        }

        if (!hasContracts)
        {
            return true;
        }

        return false;
    }

    private static string NormalizeRunType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var text = raw.Trim().ToLowerInvariant();
        return text switch
        {
            "manual" or "manual_run" or "manual-run" => "manual",
            "auto" or "automatic" or "auto_run" or "auto-run" => "auto",
            "all" => "all",
            _ => text
        };
    }

    private static Guid? ParseGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private static bool ContainsAny(string? source, params string[] tokens)
    {
        if (string.IsNullOrWhiteSpace(source) || tokens is null || tokens.Length == 0) return false;
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            if (source.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    private static string NormalizeNumberText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Normalize(NormalizationForm.FormKC);
    }

    private static string GetPayrollItemDisplayName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        if (PayrollItemDisplayNames.TryGetValue(code, out var name)) return name;
        return code;
    }

    private static decimal ApplyRounding(decimal value, string? methodRaw, int precision)
    {
        var method = string.IsNullOrWhiteSpace(methodRaw) ? "round" : methodRaw.Trim().ToLowerInvariant();
        method = method.Replace("-", "_").Replace(" ", "_");
        var scale = Pow10Decimal(precision);
        decimal RoundHalfDown(decimal scaled)
        {
            var truncated = decimal.Truncate(scaled);
            var fraction = Math.Abs(scaled - truncated);
            if (fraction > 0.5m)
            {
                truncated += scaled >= 0 ? 1 : -1;
            }
            return truncated;
        }

        switch (method)
        {
            case "round":
            case "round_half_down":
            case "roundhalfdown":
                return RoundHalfDown(value * scale) / scale;
            case "round_half_up":
            case "roundhalfup":
                return Math.Round(value, precision, MidpointRounding.AwayFromZero);
            case "floor":
                return Math.Floor(value * scale) / scale;
            case "ceil":
                return Math.Ceiling(value * scale) / scale;
            case "truncate":
                return decimal.Truncate(value * scale) / scale;
            default:
                return Math.Round(value, precision, MidpointRounding.AwayFromZero);
        }
    }

    private static decimal Pow10Decimal(int precision)
    {
        if (precision == 0) return 1m;
        var abs = Math.Abs(precision);
        decimal result = 1m;
        for (var i = 0; i < abs; i++)
        {
            result *= 10m;
        }
        if (precision < 0)
        {
            result = 1m / result;
        }
        return result;
    }

    private static async Task<DepartmentInfo?> TryResolveDepartmentFromDbAsync(
        NpgsqlConnection conn,
        string companyCode,
        string? departmentIdOrCode,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(departmentIdOrCode)) return null;

        await using var cmd = conn.CreateCommand();
        if (Guid.TryParse(departmentIdOrCode, out var deptId))
        {
            cmd.CommandText = "SELECT department_code, COALESCE(name, payload->>'name') FROM departments WHERE company_code=$1 AND id=$2 LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(deptId);
        }
        else
        {
            cmd.CommandText = "SELECT department_code, COALESCE(name, payload->>'name') FROM departments WHERE company_code=$1 AND department_code=$2 LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(departmentIdOrCode);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var code = reader.IsDBNull(0) ? null : reader.GetString(0);
            var name = reader.IsDBNull(1) ? null : reader.GetString(1);
            return new DepartmentInfo(code, name);
        }

        return null;
    }

    private static string? TryGetStringProperty(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.String) return null;
        return node.GetString();
    }

    private static async Task<DepartmentInfo> ResolveDepartmentFromPayloadAsync(
        JsonElement emp,
        DateOnly? periodStart,
        DateOnly? periodEnd,
        NpgsqlConnection conn,
        string companyCode,
        CancellationToken ct)
    {
        string? departmentCode = null;
        string? departmentName = null;
        if (emp.TryGetProperty("primaryDepartmentId", out var pdId) && pdId.ValueKind == JsonValueKind.String)
        {
            var val = pdId.GetString();
            if (!string.IsNullOrWhiteSpace(val) && string.IsNullOrWhiteSpace(departmentCode))
            {
                departmentCode = val;
            }
        }
        if (emp.TryGetProperty("primaryDepartmentCode", out var pdCode) && pdCode.ValueKind == JsonValueKind.String)
        {
            var val = pdCode.GetString();
            if (!string.IsNullOrWhiteSpace(val) && string.IsNullOrWhiteSpace(departmentCode))
            {
                departmentCode = val;
            }
        }
        if (emp.TryGetProperty("primaryDepartmentName", out var pdName) && pdName.ValueKind == JsonValueKind.String)
        {
            var val = pdName.GetString();
            if (!string.IsNullOrWhiteSpace(val) && string.IsNullOrWhiteSpace(departmentName))
            {
                departmentName = val;
            }
        }
        if (emp.TryGetProperty("primaryDepartment", out var primaryDept) && primaryDept.ValueKind == JsonValueKind.Object)
        {
            if (primaryDept.TryGetProperty("code", out var pdCodeObj) && pdCodeObj.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(departmentCode))
            {
                departmentCode = pdCodeObj.GetString();
            }
            if (primaryDept.TryGetProperty("name", out var pdNameObj) && pdNameObj.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(departmentName))
            {
                departmentName = pdNameObj.GetString();
            }
        }

        if (emp.TryGetProperty("departments", out var departments) && departments.ValueKind == JsonValueKind.Array)
        {
            JsonElement? selectedDept = null;
            foreach (var dep in departments.EnumerateArray())
            {
                if (dep.ValueKind != JsonValueKind.Object) continue;
                var from = ParseContractDate(dep, "fromDate") ?? ParseContractDate(dep, "periodFrom");
                var to = ParseContractDate(dep, "toDate") ?? ParseContractDate(dep, "periodTo");
                if (periodStart.HasValue && periodEnd.HasValue)
                {
                    if (!IsRangeOverlap(from, to, periodStart.Value, periodEnd.Value)) continue;
                }
                selectedDept = dep;
                break;
            }

            if (selectedDept.HasValue)
            {
                var deptEl = selectedDept.Value;
                var deptId = TryGetStringProperty(deptEl, "departmentId");
                var code = TryGetStringProperty(deptEl, "departmentCode");
                var name = TryGetStringProperty(deptEl, "departmentName");
                if (!string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(departmentCode))
                {
                    departmentCode = code;
                }
                if (!string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(departmentName))
                {
                    departmentName = name;
                }

                var resolved = await TryResolveDepartmentFromDbAsync(conn, companyCode, deptId ?? code, ct);
                if (resolved is DepartmentInfo info)
                {
                    if (!string.IsNullOrWhiteSpace(info.Code))
                    {
                        departmentCode = info.Code;
                    }
                    if (!string.IsNullOrWhiteSpace(info.Name))
                    {
                        departmentName = info.Name;
                    }
                }
            }
        }

        return new DepartmentInfo(departmentCode, departmentName);
    }

    private static bool IsRangeOverlap(DateOnly? from, DateOnly? to, DateOnly windowStart, DateOnly windowEnd)
    {
        var effectiveFrom = from ?? new DateOnly(1900, 1, 1);
        var effectiveTo = to ?? new DateOnly(9999, 12, 31);
        return effectiveFrom <= windowEnd && effectiveTo >= windowStart;
    }

    private static string BuildPayrollTaskSummary(PayrollTaskCandidateInfo candidate)
    {
        var name = candidate.EmployeeName ?? candidate.EmployeeCode ?? candidate.EmployeeId.ToString();
        var diff = candidate.DiffSummary;
        var diffAmount = diff is null ? 0m : ReadJsonDecimal(diff, "difference");
        var diffPercent = diff is null ? 0m : ReadJsonDecimal(diff, "differencePercent");
        var diffText = diffAmount == 0m ? string.Empty : $"{(diffAmount >= 0 ? "+" : string.Empty)}{Math.Round(diffAmount, 0):N0}円";
        var percentText = diffPercent == 0m ? string.Empty : $" ({diffPercent * 100m:+0.0;-0.0;0}%)";
        if (string.IsNullOrEmpty(diffText) && string.IsNullOrEmpty(percentText))
        {
            return $"{name}：{candidate.PeriodMonth}";
        }
        return $"{name}：差分 {diffText}{percentText}";
    }

    private static async Task<bool> EnsureSessionOwnershipAsync(NpgsqlDataSource ds, Guid sessionId, string companyCode, string userId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT company_code, user_id FROM ai_sessions WHERE id=$1";
        cmd.Parameters.AddWithValue(sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return false;
        var company = reader.GetString(0);
        var owner = reader.GetString(1);
        return string.Equals(company, companyCode, StringComparison.Ordinal) && string.Equals(owner, userId, StringComparison.Ordinal);
    }
}

internal sealed record PayrollTaskCandidateInfo(
    Guid EmployeeId,
    string? EmployeeCode,
    string? EmployeeName,
    string? DepartmentCode,
    string? DepartmentName,
    decimal TotalAmount,
    JsonObject? DiffSummary,
    string PeriodMonth,
    bool NeedsConfirmation);

internal sealed record PayrollAutoRunRequest(
    string Month,
    Guid? PolicyId,
    IReadOnlyList<Guid>? EmployeeIds,
    Guid? SessionId,
    string? TargetUserId,
    bool Overwrite,
    bool? CreateTasks,
    bool? Debug,
    decimal? DiffPercentThreshold,
    decimal? DiffAmountThreshold);
