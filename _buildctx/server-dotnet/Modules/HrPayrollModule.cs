using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using ClosedXML.Excel;
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
        ["CARE_INS"] = "介護保険",
        ["PENSION"] = "厚生年金",
        ["EMP_INS"] = "雇用保険",
        ["WHT"] = "源泉徴収税",
        ["RESIDENT_TAX"] = "住民税",
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

    // 社会保険（健康保険+厚生年金）の除外キーワード
    private static readonly string[] SocialInsuranceExcludeKeywords = new[]
    {
        "社会保険加入しません", "社会保険に加入しません", "社会保険なし", "社会保険対象外",
        "社会保険免除", "社会保険不要", "社会保険不加入", "社会保険に不加入", "社会保険除外",
        "社保加入しません", "社保に加入しません", "社保なし", "社保対象外", "社保免除", "社保不要", "社保不加入",
        "健康保険加入しません", "健康保険に加入しません", "健康保険なし", "健康保険対象外", "健康保険免除",
        "厚生年金加入しません", "厚生年金に加入しません", "厚生年金なし", "厚生年金対象外", "厚生年金免除",
        "不加入社会保险", "社会保险不加入", "不需要社保", "不要社保", "社保不加入"
    };

    // 雇用保険の除外キーワード（これらが含まれる場合、雇用保険は計算しない）
    private static readonly string[] EmploymentInsuranceExcludeKeywords = new[]
    {
        "雇用保険対象外", "雇用保険なし", "雇用保険免除", "雇用保険しません",
        "雇用保険に加入しません", "雇用保険加入しません", "雇用保険不要", "雇用保険不加入",
        "雇用保険に不加入", "不需要雇佣保险", "不要雇佣保险", "雇佣保险不加入",
        "雇用保険除外", "雇佣保险免除", "雇佣保险対象外"
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

    // 別表第一甲欄 税額表エントリ（查表/公式共用）
    private readonly struct WithholdingTableEntry
    {
        public WithholdingTableEntry(
            decimal min,
            decimal? max,
            int dependents,
            decimal taxAmount,
            string calcType,
            decimal? baseTax,
            decimal? rate,
            string version)
        {
            Min = min;
            Max = max;
            Dependents = dependents;
            TaxAmount = taxAmount;
            CalcType = calcType;
            BaseTax = baseTax;
            Rate = rate;
            Version = version;
        }

        public decimal Min { get; }
        public decimal? Max { get; }
        public int Dependents { get; }
        public decimal TaxAmount { get; }
        public string CalcType { get; }
        public decimal? BaseTax { get; }
        public decimal? Rate { get; }
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
            || code.Equals("CARE_INS", StringComparison.OrdinalIgnoreCase)
            || code.Equals("PENSION", StringComparison.OrdinalIgnoreCase)
            || code.Equals("EMP_INS", StringComparison.OrdinalIgnoreCase)
            || code.Equals("WHT", StringComparison.OrdinalIgnoreCase)
            || code.Equals("RESIDENT_TAX", StringComparison.OrdinalIgnoreCase)
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

    private static List<JournalLine> BuildJournalLinesFromDsl(JsonElement journalRulesEl, List<JsonObject> sheetOut,
        out List<(string Code, string? Name, decimal Amount)>? unmappedItems)
    {
        unmappedItems = null;
        var amountByItem = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var nameByItem = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in sheetOut)
        {
            var code = ReadJsonString(entry, "itemCode");
            if (string.IsNullOrWhiteSpace(code)) continue;
            var amount = ReadJsonDecimal(entry, "amount");
            if (amount == 0m) continue;
            if (amountByItem.TryGetValue(code, out var existing)) amountByItem[code] = existing + amount;
            else amountByItem[code] = amount;
            if (!nameByItem.ContainsKey(code))
                nameByItem[code] = ReadJsonString(entry, "itemName");
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

        var mappedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                mappedCodes.Add(code);
                if (!amountByItem.TryGetValue(code, out var amt)) continue;
                sum += amt * sign;
            }
            if (sum <= 0m) continue;
            Acc("DR", debitAccount!, sum);
            Acc("CR", creditAccount!, sum);
        }

        // 检测有金额但未被任何 journalRule 映射的项目
        var missed = amountByItem.Keys
            .Where(k => !mappedCodes.Contains(k))
            .Select(k => (Code: k, Name: nameByItem.GetValueOrDefault(k), Amount: amountByItem[k]))
            .ToList();
        if (missed.Count > 0)
            unmappedItems = missed;

        // 未映射项目中如果 sheetOut 带有 overrideAccountCode，正常纳入分录
        string? salaryPayableAccount = null;
        foreach (var rule in journalRulesEl.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object) continue;
            if (rule.TryGetProperty("items", out var ritems) && ritems.ValueKind == JsonValueKind.Array && ritems.GetArrayLength() > 0)
            {
                var da2 = rule.TryGetProperty("debitAccount", out var dv2) && dv2.ValueKind == JsonValueKind.String ? dv2.GetString() : null;
                var ca2 = rule.TryGetProperty("creditAccount", out var cv2) && cv2.ValueKind == JsonValueKind.String ? cv2.GetString() : null;
                if (!string.IsNullOrWhiteSpace(da2) && !string.IsNullOrWhiteSpace(ca2)) { salaryPayableAccount = ca2; break; }
            }
        }
        salaryPayableAccount ??= "315";
        var kindByItem = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in sheetOut)
        {
            var code = ReadJsonString(entry, "itemCode");
            if (string.IsNullOrWhiteSpace(code) || kindByItem.ContainsKey(code)) continue;
            kindByItem[code] = ReadJsonString(entry, "kind");
        }
        var stillUnmapped = new List<(string Code, string? Name, decimal Amount)>();
        foreach (var m in missed)
        {
            var overrideAcct = sheetOut
                .Where(e => string.Equals(ReadJsonString(e, "itemCode"), m.Code, StringComparison.OrdinalIgnoreCase))
                .Select(e => ReadJsonString(e, "overrideAccountCode"))
                .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));

            if (string.IsNullOrWhiteSpace(overrideAcct))
            {
                // Adjust salaryPayableAccount now; the specific contra-account
                // will be filled in by the user via the needsAccount placeholder row.
                var kindU = kindByItem.GetValueOrDefault(m.Code);
                var isDeductionU = string.Equals(kindU, "deduction", StringComparison.OrdinalIgnoreCase);
                if (isDeductionU)
                    Acc("DR", salaryPayableAccount, m.Amount);
                else
                    Acc("CR", salaryPayableAccount, m.Amount);
                stillUnmapped.Add(m);
                continue;
            }
            var kind = kindByItem.GetValueOrDefault(m.Code);
            var isDeduction = string.Equals(kind, "deduction", StringComparison.OrdinalIgnoreCase);
            if (isDeduction)
            {
                Acc("DR", salaryPayableAccount, m.Amount);
                Acc("CR", overrideAcct!, m.Amount);
            }
            else
            {
                Acc("DR", overrideAcct!, m.Amount);
                Acc("CR", salaryPayableAccount, m.Amount);
            }
        }
        unmappedItems = stillUnmapped.Count > 0 ? stillUnmapped : null;

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
        if (Has("住民税", "住民稅", "市民税", "県民税", "地方税", "特別徴収"))
        {
            var debit = Debit("residentTax", "3185");
            var credit = Credit("residentTax", "315");
            list.Add(new
            {
                name = "residentTax",
                debitAccount = debit,
                creditAccount = credit,
                items = new[] { new { code = "RESIDENT_TAX" } },
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
        // POST /payroll/parse-salary-description
        // キーワードベースで給与説明テキストを解析し構造化 payrollConfig を返す。
        // LLM が設定されている場合は後から LLM でマージして精度向上。
        // Body: { description: string }
        // Returns: { payrollConfig: { baseSalary, commuteAllowance, insuranceBase, socialInsurance, ... } }
        app.MapPost("/payroll/parse-salary-description", async (HttpRequest req, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            var description = doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(description))
                return Results.BadRequest(new { error = "description is required" });

            // ── キーワードベース解析（LLM不要、常に動作） ──────────────────────
            var config = ParseSalaryConfigByKeyword(description);

            // ── LLM による補完（OpenAI が設定されている場合のみ） ────────────────
            var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                try
                {
                    var systemPrompt = @"あなたは日本の給与計算の専門家です。
従業員の給与説明テキスト（自然言語）を受け取り、構造化された給与設定JSONを返してください。

出力は以下のJSON形式のみ（余計なテキスト不要）:
{
  ""baseSalary"": <number|null>,
  ""commuteAllowance"": <number|null>,
  ""insuranceBase"": <number|null>,
  ""socialInsurance"": <boolean>,
  ""employmentInsurance"": <boolean>,
  ""incomeTax"": <boolean>,
  ""residentTax"": <boolean>,
  ""overtime"": <boolean>,
  ""holidayWork"": <boolean>,
  ""lateNight"": <boolean>,
  ""absenceDeduction"": <boolean>
}

重要なルール:
- 明示的に言及されていない項目はfalseにする
- 「〜しません」「〜なし」「〜対象外」等の否定表現があればfalse
- 金額の「万」は×10000に変換（例：30万→300000）";
                    var http = httpFactory.CreateClient("openai");
                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                    var requestBody = new
                    {
                        model = "gpt-4o-mini",
                        temperature = 0.0,
                        messages = new object[]
                        {
                            new { role = "system", content = systemPrompt },
                            new { role = "user", content = description }
                        },
                        response_format = new { type = "json_object" }
                    };
                    var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                    using var response = await http.PostAsync("chat/completions",
                        new StringContent(json, System.Text.Encoding.UTF8, "application/json"), ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var responseText = await response.Content.ReadAsStringAsync(ct);
                        using var respDoc = JsonDocument.Parse(responseText);
                        var llmContent = respDoc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                        if (!string.IsNullOrWhiteSpace(llmContent))
                        {
                            // LLM 結果でキーワード結果を上書き（より精度が高い）
                            using var llmDoc = JsonDocument.Parse(llmContent);
                            config = MergeLlmIntoKeywordConfig(config, llmDoc.RootElement);
                        }
                    }
                }
                catch { /* LLM 失敗時はキーワード結果をそのまま使用 */ }
            }

            return Results.Ok(new { payrollConfig = config });
        });

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
            // 負向キーワードで除外（例：「雇用保険対象外」「雇用保険なし」など）
            if (wantsEmployment && ContainsAny(s, EmploymentInsuranceExcludeKeywords)) wantsEmployment = false;
            bool wantsWithholding = s.Contains("源泉", StringComparison.OrdinalIgnoreCase) || s.Contains("月額表", StringComparison.OrdinalIgnoreCase) || s.Contains("甲欄", StringComparison.OrdinalIgnoreCase);
            bool wantsOvertime = ContainsAny(s, "残業", "時間外", "加班");
            bool wantsOvertime60 = ContainsAny(s, "60時間", "６０時間", "60h", "60小时");
            bool wantsHolidayWork = ContainsAny(s, "休日", "祝日", "节假日", "節假日", "法定休日");
            bool wantsLateNight = ContainsAny(s, "深夜", "22時", "22点", "22點", "late night", "夜間");
            bool wantsAbsence = ContainsAny(s, "欠勤", "欠勤控除", "欠勤・遅刻", "勤怠控除", "工时不足", "工時不足", "遅刻", "早退");
            bool wantsResidentTax = ContainsAny(s, "住民税", "住民稅", "市民税", "県民税", "地方税", "特別徴収");
            if (wantsHealth) {
                EnsureDeductionItem("HEALTH_INS", "健康保険");
                object baseExpr = new { charRef = "employee.baseSalaryMonth" };
                rules.Add(new { item = "HEALTH_INS", type = "deduction", activation = BuildActivation(), formula = new { _base = baseExpr, rate = "policy.law.health.rate", rounding = new { method = "round_half_down", precision = 0 } } });
                hints.Add("已生成健康保险计算规则：按都道府県与月额区间匹配费率");
                dependencies.AddRange(new[]{
                    "jp.health.standardMonthly",
                    "jp.health.rate.employee",
                    "jp.health.rate.employer"
                });
                if (!(s.Contains("都道府", StringComparison.OrdinalIgnoreCase) || s.Contains("都", StringComparison.OrdinalIgnoreCase) || s.Contains("県", StringComparison.OrdinalIgnoreCase))) riskFlags.Add("health.missing_prefecture");
                if (!(s.Contains("協会", StringComparison.OrdinalIgnoreCase) || s.Contains("組合", StringComparison.OrdinalIgnoreCase))) riskFlags.Add("health.missing_scheme");
                // 介护保险：40岁~64岁员工自动计算
                EnsureDeductionItem("CARE_INS", "介護保険");
                rules.Add(new { item = "CARE_INS", type = "deduction", activation = BuildActivation(), formula = new { _base = baseExpr, rate = "policy.law.care.rate", rounding = new { method = "round_half_down", precision = 0 } } });
                hints.Add("已生成介护保险计算规则：40岁~64岁员工自动计算");
                dependencies.AddRange(new[]{
                    "jp.health.care.rate.employee",
                    "jp.health.care.rate.employer"
                });
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
            if (wantsResidentTax)
            {
                EnsureDeductionItem("RESIDENT_TAX", "住民税");
                // 住民税从 resident_tax_schedules 表根据当前月份查询
                rules.Add(new { item = "RESIDENT_TAX", type = "deduction", activation = BuildActivation(), formula = new { residentTax = new { source = "db" } } });
                hints.Add("已生成住民税计算规则：从住民税管理数据中根据当前月份查询扣除金额。");
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
            // 以下扣除项：从未払費用中扣除，转入预提金
            // 会计分录：DR 315 未払費用（减少应付员工）/ CR 預り金（增加代扣负债）
            if (wantsHealth)
            {
                var healthDebit = ResolveJournalDebit("health", "315");
                var healthCredit = ResolveJournalCredit("health", "3181");
                // 社会保険（健康保険+介護保険を合算）
                var healthItems = new List<object> { BuildJournalItem("HEALTH_INS"), BuildJournalItem("CARE_INS") };
                AddJournalRule("health", healthDebit, healthCredit, healthItems, FormatJournalDescription(healthDebit, healthCredit));
            }
            if (wantsPension)
            {
                var pensionDebit = ResolveJournalDebit("pension", "315");
                var pensionCredit = ResolveJournalCredit("pension", "3182");
                AddJournalRule("pension", pensionDebit, pensionCredit, new[] { BuildJournalItem("PENSION") }, FormatJournalDescription(pensionDebit, pensionCredit));
            }
            if (wantsEmployment)
            {
                var employmentDebit = ResolveJournalDebit("employment", "315");
                var employmentCredit = ResolveJournalCredit("employment", "3183");
                AddJournalRule("employment", employmentDebit, employmentCredit, new[] { BuildJournalItem("EMP_INS") }, FormatJournalDescription(employmentDebit, employmentCredit));
            }
            if (wantsWithholding)
            {
                var withholdingDebit = ResolveJournalDebit("withholding", "315");
                var withholdingCredit = ResolveJournalCredit("withholding", "3184");
                AddJournalRule("withholding", withholdingDebit, withholdingCredit, new[] { BuildJournalItem("WHT") }, FormatJournalDescription(withholdingDebit, withholdingCredit));
            }
            if (wantsResidentTax)
            {
                var residentTaxDebit = ResolveJournalDebit("residentTax", "315");
                var residentTaxCredit = ResolveJournalCredit("residentTax", "3185");
                AddJournalRule("residentTax", residentTaxDebit, residentTaxCredit, new[] { BuildJournalItem("RESIDENT_TAX") }, FormatJournalDescription(residentTaxDebit, residentTaxCredit));
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
        
        // POST /payroll/regenerate-journal
        // Regenerates accounting journal entries from an adjusted payroll sheet.
        // Body: { payrollSheet: [...], employeeCode?, departmentCode?, departmentName? }
        // Returns: { accountingDraft: [...] }
        app.MapPost("/payroll/regenerate-journal", async (HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var companyCode = req.HttpContext.User.FindFirst("company")?.Value ?? "default";
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            var root = doc.RootElement;
            Guid? employeeId = null;
            if (root.TryGetProperty("employeeId", out var empIdEl) && empIdEl.ValueKind == JsonValueKind.String)
            {
                if (Guid.TryParse(empIdEl.GetString(), out var empId)) employeeId = empId;
            }
            
            // 获取 payrollSheet
            if (!root.TryGetProperty("payrollSheet", out var sheetEl) || sheetEl.ValueKind != JsonValueKind.Array)
            {
                return Results.BadRequest(new { error = "payrollSheet is required" });
            }
            
            // 转换为 List<JsonObject>
            var sheetOut = new List<JsonObject>();
            foreach (var item in sheetEl.EnumerateArray())
            {
                var itemCode = item.TryGetProperty("itemCode", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString() : null;
                var amount = item.TryGetProperty("amount", out var am) ? 
                    (am.ValueKind == JsonValueKind.Number ? am.GetDecimal() : 0m) : 0m;
                // 如果有 finalAmount 字段，使用它（这是调整后的金额）
                if (item.TryGetProperty("finalAmount", out var fa) && fa.ValueKind == JsonValueKind.Number)
                {
                    amount = fa.GetDecimal();
                }
                if (string.IsNullOrWhiteSpace(itemCode)) continue;
                var itemName = item.TryGetProperty("itemName", out var inm) && inm.ValueKind == JsonValueKind.String ? inm.GetString() : null;
                var kind = item.TryGetProperty("kind", out var kv) && kv.ValueKind == JsonValueKind.String ? kv.GetString() : null;
                var overrideAcc = item.TryGetProperty("overrideAccountCode", out var oa) && oa.ValueKind == JsonValueKind.String ? oa.GetString() : null;
                var node = new JsonObject
                {
                    ["itemCode"] = itemCode,
                    ["itemName"] = itemName,
                    ["amount"] = JsonValue.Create(amount)
                };
                if (!string.IsNullOrWhiteSpace(kind)) node["kind"] = kind;
                if (!string.IsNullOrWhiteSpace(overrideAcc)) node["overrideAccountCode"] = overrideAcc;
                sheetOut.Add(node);
            }
            
            // 获取活跃的 Policy 的 journalRules
            await using var conn = await ds.OpenConnectionAsync(ct);
            
            // 如果没有 company claim，则通过 employeeId 获取 company_code
            if ((string.IsNullOrWhiteSpace(companyCode) || companyCode == "default") && employeeId.HasValue)
            {
                await using var qe = conn.CreateCommand();
                qe.CommandText = "SELECT company_code FROM employees WHERE id=$1 LIMIT 1";
                qe.Parameters.AddWithValue(employeeId.Value);
                var cc = (string?)await qe.ExecuteScalarAsync(ct);
                if (!string.IsNullOrWhiteSpace(cc)) companyCode = cc;
            }
            string? policyJson = null;
            await using (var q = conn.CreateCommand())
            {
                q.CommandText = "SELECT payload FROM payroll_policies WHERE company_code=$1 AND is_active=true LIMIT 1";
                q.Parameters.AddWithValue(companyCode);
                policyJson = (string?)await q.ExecuteScalarAsync(ct);
            }
            
            JsonElement? journalRulesEl = null;
            
            if (!string.IsNullOrEmpty(policyJson))
            {
                using var policyDoc = JsonDocument.Parse(policyJson);
                var policyRoot = policyDoc.RootElement;
                
                // 优先使用 Policy 中定义的 journalRules（包括 dsl.journalRules）
                if (policyRoot.TryGetProperty("journalRules", out var jr) && jr.ValueKind == JsonValueKind.Array && jr.GetArrayLength() > 0)
                {
                    journalRulesEl = jr.Clone();
                }
                else if (policyRoot.TryGetProperty("dsl", out var dslEl) && dslEl.ValueKind == JsonValueKind.Object &&
                         dslEl.TryGetProperty("journalRules", out var dslJr) && dslJr.ValueKind == JsonValueKind.Array && dslJr.GetArrayLength() > 0)
                {
                    journalRulesEl = dslJr.Clone();
                }
                
                // 如果 Policy 中没有定义 journalRules，则基于 DSL rules 自动生成（作为 fallback）
                if (!journalRulesEl.HasValue && policyRoot.TryGetProperty("rules", out var rulesEl) && rulesEl.ValueKind == JsonValueKind.Array)
                {
                    var dslItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rule in rulesEl.EnumerateArray())
                    {
                        if (rule.TryGetProperty("item", out var itemProp) && itemProp.ValueKind == JsonValueKind.String)
                        {
                            var code = itemProp.GetString();
                            if (!string.IsNullOrWhiteSpace(code)) dslItems.Add(code);
                        }
                    }
                    
                    // 同时检查 payrollSheet 中的手动添加项目
                    foreach (var item in sheetOut)
                    {
                        var code = ReadJsonString(item, "itemCode");
                        if (!string.IsNullOrWhiteSpace(code)) dslItems.Add(code);
                    }
                    
                    var autoJournalRules = new List<object>();
                    object BuildJI(string code, decimal? sign = null) => sign.HasValue ? new { code, sign } : new { code };
                    
                    // 基本給/各種手当 → 給与手当/未払費用
                    var wageItems = new List<object>();
                    if (dslItems.Contains("BASE")) wageItems.Add(BuildJI("BASE"));
                    if (dslItems.Contains("COMMUTE")) wageItems.Add(BuildJI("COMMUTE"));
                    if (dslItems.Contains("OVERTIME_STD")) wageItems.Add(BuildJI("OVERTIME_STD"));
                    if (dslItems.Contains("OVERTIME_60")) wageItems.Add(BuildJI("OVERTIME_60"));
                    if (dslItems.Contains("HOLIDAY_PAY")) wageItems.Add(BuildJI("HOLIDAY_PAY"));
                    if (dslItems.Contains("LATE_NIGHT_PAY")) wageItems.Add(BuildJI("LATE_NIGHT_PAY"));
                    if (dslItems.Contains("ABSENCE_DEDUCT")) wageItems.Add(BuildJI("ABSENCE_DEDUCT", -1m));
                    // 手动添加的收入项目（不含年末調整還付，它有独立的分录规则）
                    foreach (var code in new[] { "BONUS", "ALLOWANCE_SPECIAL", "ALLOWANCE_HOUSING", "ALLOWANCE_FAMILY", "ALLOWANCE_POSITION", "ADJUST_OTHER" })
                    {
                        if (dslItems.Contains(code)) wageItems.Add(BuildJI(code));
                    }
                    if (wageItems.Count > 0)
                        autoJournalRules.Add(new { name = "wages", debitAccount = "832", creditAccount = "315", items = wageItems.ToArray() });
                    
                    // 年末調整還付（返还之前多扣的源泉税：借 源泉所得税預り金、贷 未払費用）
                    // 会计分录：DR 3184 源泉所得税預り金（减少负债）/ CR 315 未払費用（增加应付员工）
                    if (dslItems.Contains("NENMATSU_KANPU"))
                        autoJournalRules.Add(new { name = "nenmatsu_kanpu", debitAccount = "3184", creditAccount = "315", items = new[] { BuildJI("NENMATSU_KANPU") } });
                    
                    // 以下扣除项：从未払費用中扣除，转入预提金
                    // 会计分录：DR 315 未払費用（减少应付员工）/ CR 預り金（增加代扣负债）
                    
                    // 社会保険（健康保険+介護保険を合算）
                    if (dslItems.Contains("HEALTH_INS") || dslItems.Contains("CARE_INS"))
                    {
                        var healthItems = new List<object>();
                        if (dslItems.Contains("HEALTH_INS")) healthItems.Add(BuildJI("HEALTH_INS"));
                        if (dslItems.Contains("CARE_INS")) healthItems.Add(BuildJI("CARE_INS"));
                        autoJournalRules.Add(new { name = "health", debitAccount = "315", creditAccount = "3181", items = healthItems.ToArray() });
                    }
                    
                    // 厚生年金
                    if (dslItems.Contains("PENSION"))
                        autoJournalRules.Add(new { name = "pension", debitAccount = "315", creditAccount = "3182", items = new[] { BuildJI("PENSION") } });
                    
                    // 雇用保険
                    if (dslItems.Contains("EMP_INS"))
                        autoJournalRules.Add(new { name = "employment", debitAccount = "315", creditAccount = "3183", items = new[] { BuildJI("EMP_INS") } });
                    
                    // 源泉徴収税
                    if (dslItems.Contains("WHT"))
                        autoJournalRules.Add(new { name = "withholding", debitAccount = "315", creditAccount = "3184", items = new[] { BuildJI("WHT") } });
                    
                    // 住民税
                    if (dslItems.Contains("RESIDENT_TAX"))
                        autoJournalRules.Add(new { name = "residentTax", debitAccount = "315", creditAccount = "3185", items = new[] { BuildJI("RESIDENT_TAX") } });
                    
                    // 年末調整徴収（追缴之前少扣的税：从未払費用扣除，转入源泉税）
                    // 会计分录：DR 315 未払費用（减少应付员工）/ CR 3184 源泉所得税預り金（增加代扣负债）
                    if (dslItems.Contains("NENMATSU_CHOSHU"))
                        autoJournalRules.Add(new { name = "nenmatsu_choshu", debitAccount = "315", creditAccount = "3184", items = new[] { BuildJI("NENMATSU_CHOSHU") } });
                    
                    // 手动控除项目
                    var deductItems = new List<object>();
                    foreach (var code in new[] { "DEDUCT_LOAN", "DEDUCT_ADVANCE", "DEDUCT_OTHER" })
                    {
                        if (dslItems.Contains(code)) deductItems.Add(BuildJI(code));
                    }
                    if (deductItems.Count > 0)
                        autoJournalRules.Add(new { name = "deductions", debitAccount = "315", creditAccount = "318", items = deductItems.ToArray() });
                    
                    if (autoJournalRules.Count > 0)
                    {
                        var jrArray = new System.Text.Json.Nodes.JsonArray(autoJournalRules.Select(o => JsonSerializer.SerializeToNode(o)!).ToArray());
                        using var jrDoc = JsonDocument.Parse(jrArray.ToJsonString());
                        journalRulesEl = jrDoc.RootElement.Clone();
                    }
                }
            }
            
            if (!journalRulesEl.HasValue)
            {
                return Results.BadRequest(new { error = "No journal rules found in active policy" });
            }
            
            // 生成会计分录
            var journal = BuildJournalLinesFromDsl(journalRulesEl.Value, sheetOut, out var previewUnmapped);
            
            // 获取科目名称
            var codes = journal.Select(j => j.AccountCode).Distinct().ToArray();
            var accountNameByCode = new Dictionary<string, string?>();
            foreach (var code in codes)
            {
                await using var qa = conn.CreateCommand();
                qa.CommandText = "SELECT name FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1";
                qa.Parameters.AddWithValue(companyCode);
                qa.Parameters.AddWithValue(code);
                var name = (string?)await qa.ExecuteScalarAsync(ct);
                accountNameByCode[code] = name;
            }
            
            // 构建返回结果
            var employeeCode = root.TryGetProperty("employeeCode", out var ec) && ec.ValueKind == JsonValueKind.String ? ec.GetString() : null;
            var departmentCode = root.TryGetProperty("departmentCode", out var dc) && dc.ValueKind == JsonValueKind.String ? dc.GetString() : null;
            var departmentName = root.TryGetProperty("departmentName", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() : null;
            
            var accountingDraft = journal.Select(entry =>
            {
                accountNameByCode.TryGetValue(entry.AccountCode, out var an);
                return new JsonObject
                {
                    ["lineNo"] = entry.LineNo,
                    ["accountCode"] = entry.AccountCode,
                    ["accountName"] = an,
                    ["drcr"] = entry.DrCr,
                    ["amount"] = JsonValue.Create(entry.Amount),
                    ["employeeCode"] = employeeCode,
                    ["departmentCode"] = departmentCode,
                    ["departmentName"] = departmentName
                };
            }).ToList();

            // 未映射项目追加占位行，科目列留空让前端编辑
            if (previewUnmapped is { Count: > 0 })
            {
                int nextLine = accountingDraft.Count > 0
                    ? accountingDraft.Max(o => o["lineNo"]?.GetValue<int>() ?? 0) + 1
                    : 1;
                foreach (var u in previewUnmapped)
                {
                    var kind = sheetOut
                        .Where(e => string.Equals(ReadJsonString(e, "itemCode"), u.Code, StringComparison.OrdinalIgnoreCase))
                        .Select(e => ReadJsonString(e, "kind"))
                        .FirstOrDefault();
                    var isDeduction = string.Equals(kind, "deduction", StringComparison.OrdinalIgnoreCase);
                    accountingDraft.Add(new JsonObject
                    {
                        ["lineNo"] = nextLine++,
                        ["accountCode"] = "",
                        ["accountName"] = $"⚠ {u.Name ?? u.Code}",
                        ["drcr"] = isDeduction ? "CR" : "DR",
                        ["amount"] = JsonValue.Create(u.Amount),
                        ["employeeCode"] = employeeCode,
                        ["departmentCode"] = departmentCode,
                        ["departmentName"] = departmentName,
                        ["needsAccount"] = true,
                        ["itemCode"] = u.Code,
                        ["itemName"] = u.Name
                    });
                }
            }
            
            return Results.Ok(new { accountingDraft });
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

            var runType = reader.GetString(2);
            var periodMonth = reader.GetString(1);
            var entryIdVal = reader.GetGuid(3);
            var employeeId = reader.GetGuid(4);
            var employeeCode = reader.IsDBNull(5) ? null : reader.GetString(5);
            var employeeName = reader.IsDBNull(6) ? null : reader.GetString(6);
            var departmentCode = reader.IsDBNull(7) ? null : reader.GetString(7);
            var totalAmount = reader.GetDecimal(8);
            var payrollSheet = reader.IsDBNull(9) ? null : JsonNode.Parse(reader.GetString(9));
            var accountingDraft = reader.IsDBNull(10) ? null : JsonNode.Parse(reader.GetString(10));
            var diffSummary = reader.IsDBNull(11) ? null : JsonNode.Parse(reader.GetString(11));
            var metadata = reader.IsDBNull(12) ? null : JsonNode.Parse(reader.GetString(12));
            var trace = reader.IsDBNull(13) ? null : JsonNode.Parse(reader.GetString(13));
            var voucherId = reader.IsDBNull(14) ? (Guid?)null : reader.GetGuid(14);
            var voucherNo = reader.IsDBNull(15) ? null : reader.GetString(15);
            await reader.CloseAsync();

            var canEdit = true;
            if (voucherId.HasValue)
            {
                await using var oiCmd = conn.CreateCommand();
                oiCmd.CommandText = "SELECT EXISTS(SELECT 1 FROM open_items WHERE company_code=$1 AND voucher_id=$2 AND cleared_flag=true)";
                oiCmd.Parameters.AddWithValue(companyCode);
                oiCmd.Parameters.AddWithValue(voucherId.Value);
                var hasCleared = (bool)(await oiCmd.ExecuteScalarAsync(ct))!;
                if (hasCleared) canEdit = false;
            }

            if (accountingDraft is JsonArray draftArr)
            {
                var codesToLookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in draftArr)
                {
                    if (line is not JsonObject obj) continue;
                    var code = obj["accountCode"]?.GetValue<string>();
                    var name = obj["accountName"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(name))
                        codesToLookup.Add(code);
                }
                if (codesToLookup.Count > 0)
                {
                    var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var code in codesToLookup)
                    {
                        await using var naCmd = conn.CreateCommand();
                        naCmd.CommandText = "SELECT name FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1";
                        naCmd.Parameters.AddWithValue(companyCode);
                        naCmd.Parameters.AddWithValue(code);
                        var n = (string?)await naCmd.ExecuteScalarAsync(ct);
                        if (!string.IsNullOrWhiteSpace(n)) nameMap[code] = n;
                    }
                    foreach (var line in draftArr)
                    {
                        if (line is not JsonObject obj) continue;
                        var code = obj["accountCode"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(code) && nameMap.TryGetValue(code, out var resolvedName))
                        {
                            var existingName = obj["accountName"]?.GetValue<string>();
                            if (string.IsNullOrWhiteSpace(existingName))
                                obj["accountName"] = resolvedName;
                        }
                    }
                }
            }

            return Results.Json(new
            {
                runType,
                periodMonth,
                runId,
                entryId = entryIdVal,
                employeeId,
                employeeCode,
                employeeName,
                departmentCode,
                totalAmount,
                payrollSheet,
                accountingDraft,
                diffSummary,
                metadata,
                trace,
                voucherId,
                voucherNo,
                canEdit
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
       e.voucher_no,
       d.name AS department_name
FROM payroll_run_entries e
JOIN payroll_runs r ON e.run_id = r.id
LEFT JOIN payroll_policies p ON p.id = r.policy_id AND p.company_code = r.company_code
LEFT JOIN departments d ON d.company_code = r.company_code AND d.department_code = e.department_code
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
                    voucherNo = reader.IsDBNull(15) ? null : reader.GetString(15),
                    departmentName = reader.IsDBNull(16) ? null : reader.GetString(16)
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

        // GET /payroll/run-entries/calculated-employees?month=YYYY-MM
        // Returns the set of employee_id values that already have payroll entries for the given month.
        app.MapGet("/payroll/run-entries/calculated-employees", async (HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString();
            var month = req.Query["month"].ToString();
            if (string.IsNullOrWhiteSpace(month))
                return Results.BadRequest(new { error = "month is required" });

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT DISTINCT e.employee_id
FROM payroll_run_entries e
JOIN payroll_runs r ON e.run_id = r.id
WHERE r.company_code = @company AND r.period_month = @month";
            cmd.Parameters.AddWithValue("company", companyCode);
            cmd.Parameters.AddWithValue("month", month);
            var ids = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                ids.Add(reader.GetGuid(0).ToString());
            return Results.Json(ids);
        }).RequireAuthorization();

        // GET /payroll/run-entries/export
        app.MapGet("/payroll/run-entries/export", async (HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString();
            var month = req.Query["month"].ToString();

            await using var conn = await ds.OpenConnectionAsync(ct);
            var filters = new List<string> { "r.company_code = @company" };
            var parameters = new Dictionary<string, object?> { ["company"] = companyCode };
            if (!string.IsNullOrWhiteSpace(month))
            {
                filters.Add("r.period_month = @month");
                parameters["month"] = month;
            }
            var whereClause = string.Join(" AND ", filters);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
SELECT e.employee_code, e.employee_name, e.department_code, d.name AS department_name,
       e.total_amount, e.payroll_sheet, e.voucher_no, e.created_at, r.period_month
FROM payroll_run_entries e
JOIN payroll_runs r ON e.run_id = r.id
LEFT JOIN departments d ON d.company_code = r.company_code AND d.department_code = e.department_code
WHERE {whereClause}
ORDER BY r.period_month DESC, e.employee_code ASC";
            foreach (var kvp in parameters)
                cmd.Parameters.AddWithValue(kvp.Key, kvp.Value ?? DBNull.Value);

            var rows = new List<(string? EmpCode, string? EmpName, string? DeptCode, string? DeptName,
                                 decimal Total, JsonArray? Sheet, string? VoucherNo, DateTimeOffset CreatedAt, string PeriodMonth)>();
            var allItemCodes = new List<string>();
            var allItemNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var empCode = reader.IsDBNull(0) ? null : reader.GetString(0);
                var empName = reader.IsDBNull(1) ? null : reader.GetString(1);
                var deptCode = reader.IsDBNull(2) ? null : reader.GetString(2);
                var deptName = reader.IsDBNull(3) ? null : reader.GetString(3);
                var total = reader.GetDecimal(4);
                JsonArray? sheet = null;
                if (!reader.IsDBNull(5))
                {
                    var node = JsonNode.Parse(reader.GetString(5));
                    sheet = node as JsonArray;
                }
                var voucherNo = reader.IsDBNull(6) ? null : reader.GetString(6);
                var createdAt = reader.GetFieldValue<DateTimeOffset>(7);
                var periodMonth = reader.GetString(8);
                rows.Add((empCode, empName, deptCode, deptName, total, sheet, voucherNo, createdAt, periodMonth));

                if (sheet != null)
                {
                    foreach (var item in sheet)
                    {
                        if (item is not JsonObject obj) continue;
                        var code = obj["itemCode"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(code)) continue;
                        if (!allItemNames.ContainsKey(code))
                        {
                            allItemCodes.Add(code);
                            allItemNames[code] = obj["itemName"]?.GetValue<string>() ?? code;
                        }
                    }
                }
            }
            await reader.CloseAsync();

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("給与明細");
            int col = 1;
            ws.Cell(1, col++).Value = "年月";
            ws.Cell(1, col++).Value = "社員コード";
            ws.Cell(1, col++).Value = "社員名";
            ws.Cell(1, col++).Value = "部門コード";
            ws.Cell(1, col++).Value = "部門名";
            foreach (var code in allItemCodes)
                ws.Cell(1, col++).Value = allItemNames[code];
            ws.Cell(1, col++).Value = "差引支給額";
            ws.Cell(1, col++).Value = "伝票番号";
            ws.Cell(1, col++).Value = "計算日時";

            var headerRange = ws.Range(1, 1, 1, col - 1);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.LightSteelBlue;

            int row = 2;
            foreach (var r in rows)
            {
                col = 1;
                ws.Cell(row, col++).Value = r.PeriodMonth;
                ws.Cell(row, col++).Value = r.EmpCode ?? "";
                ws.Cell(row, col++).Value = r.EmpName ?? "";
                ws.Cell(row, col++).Value = r.DeptCode ?? "";
                ws.Cell(row, col++).Value = r.DeptName ?? "";

                var amountByItem = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                if (r.Sheet != null)
                {
                    foreach (var item in r.Sheet)
                    {
                        if (item is not JsonObject obj) continue;
                        var code = obj["itemCode"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(code)) continue;
                        var amt = obj["amount"]?.GetValue<decimal>() ?? 0m;
                        amountByItem[code] = amt;
                    }
                }
                foreach (var code in allItemCodes)
                {
                    var cell = ws.Cell(row, col++);
                    if (amountByItem.TryGetValue(code, out var amt))
                    {
                        cell.Value = amt;
                        cell.Style.NumberFormat.Format = "#,##0";
                    }
                }
                var totalCell = ws.Cell(row, col++);
                totalCell.Value = r.Total;
                totalCell.Style.NumberFormat.Format = "#,##0";
                ws.Cell(row, col++).Value = r.VoucherNo ?? "";
                ws.Cell(row, col++).Value = r.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                row++;
            }

            // 合計行
            if (rows.Count > 0)
            {
                int sumRow = row;
                col = 1;
                ws.Cell(sumRow, col).Value = "合計";
                ws.Cell(sumRow, col).Style.Font.Bold = true;
                col = 6; // 工資項目列の開始位置（年月,社員コード,社員名,部門コード,部門名 の次）
                int firstDataRow = 2;
                int lastDataRow = sumRow - 1;
                foreach (var _ in allItemCodes)
                {
                    var cell = ws.Cell(sumRow, col);
                    cell.FormulaA1 = $"SUM({ws.Cell(firstDataRow, col).Address}:{ws.Cell(lastDataRow, col).Address})";
                    cell.Style.NumberFormat.Format = "#,##0";
                    col++;
                }
                // 差引支給額の合計
                var totalSumCell = ws.Cell(sumRow, col);
                totalSumCell.FormulaA1 = $"SUM({ws.Cell(firstDataRow, col).Address}:{ws.Cell(lastDataRow, col).Address})";
                totalSumCell.Style.NumberFormat.Format = "#,##0";

                int lastCol = 5 + allItemCodes.Count + 3; // 年月～計算日時
                var sumRange = ws.Range(sumRow, 1, sumRow, lastCol);
                sumRange.Style.Font.Bold = true;
                sumRange.Style.Fill.BackgroundColor = XLColor.LightGreen;
            }

            ws.Columns().AdjustToContents();

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            ms.Position = 0;
            var fileName = $"payroll_{month ?? "all"}.xlsx";
            return Results.File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }).RequireAuthorization();

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

        // ============================================================
        // 住民税管理 API（Resident Tax Management）
        // ============================================================

        // POST /resident-tax/parse-image
        // 通过 AI 识别住民税税单图片，提取结构化数据
        app.MapPost("/resident-tax/parse-image", async (HttpRequest req, NpgsqlDataSource ds, IConfiguration cfg, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString();

            var apiKey = cfg["OpenAI:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
                return Results.StatusCode(500);

            // 解析上传的图片
            var imgs = new List<string>();
            bool autoSave = true;
            if (req.HasFormContentType)
            {
                var form = await req.ReadFormAsync(ct);
                foreach (var f in form.Files)
                {
                    using var ms = new MemoryStream();
                    await f.CopyToAsync(ms, ct);
                    var bytes = ms.ToArray();
                    var mime = string.IsNullOrWhiteSpace(f.ContentType) ? "image/jpeg" : f.ContentType;
                    var b64 = Convert.ToBase64String(bytes);
                    imgs.Add($"data:{mime};base64,{b64}");
                }
                if (form.TryGetValue("autoSave", out var autoSaveVal) && !string.IsNullOrWhiteSpace(autoSaveVal))
                {
                    if (bool.TryParse(autoSaveVal.ToString(), out var parsed)) autoSave = parsed;
                }
            }
            else
            {
                using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
                var root = body.RootElement;
                if (root.TryGetProperty("images", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in arr.EnumerateArray())
                    {
                        if (it.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(it.GetString()))
                            imgs.Add(it.GetString()!);
                    }
                }
                else if (root.TryGetProperty("imageBase64", out var b64) && b64.ValueKind == JsonValueKind.String)
                {
                    var val = b64.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) imgs.Add(val!);
                }
                if (root.TryGetProperty("autoSave", out var autoSaveNode) && autoSaveNode.ValueKind == JsonValueKind.True)
                {
                    autoSave = true;
                }
                else if (root.TryGetProperty("autoSave", out autoSaveNode) && autoSaveNode.ValueKind == JsonValueKind.False)
                {
                    autoSave = false;
                }
            }
            if (imgs.Count == 0)
                return Results.BadRequest(new { error = "images required" });

            // 获取员工列表供 AI 匹配
            var employees = new List<object>();
            await using (var conn = await ds.OpenConnectionAsync(ct))
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, employee_code, payload->>'nameKanji' as name_kanji, payload->>'nameKana' as name_kana FROM employees WHERE company_code=$1";
                cmd.Parameters.AddWithValue(companyCode);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    employees.Add(new
                    {
                        id = rd.GetGuid(0).ToString(),
                        code = rd.IsDBNull(1) ? "" : rd.GetString(1),
                        nameKanji = rd.IsDBNull(2) ? "" : rd.GetString(2),
                        nameKana = rd.IsDBNull(3) ? "" : rd.GetString(3)
                    });
                }
            }

            var sysPrompt = @"你是住民税税单（特別徴収税額通知書）解析助手。请从图片中提取住民税信息。

员工列表（用于匹配）：
" + JsonSerializer.Serialize(employees) + @"

请识别图片中的以下信息并返回 JSON：
{
  ""success"": true/false,
  ""rawText"": ""识别的原始文本"",
  ""entries"": [
    {
      ""employeeId"": ""匹配到的员工ID（从员工列表中匹配）"",
      ""employeeCode"": ""员工编号"",
      ""employeeName"": ""纳税义务者姓名"",
      ""fiscalYear"": 2025,  // 年度（如 2025 代表 2025年6月~2026年5月）
      ""municipalityCode"": ""市区町村コード"",
      ""municipalityName"": ""市区町村名"",
      ""annualAmount"": 240000,  // 年税額
      ""juneAmount"": 20800,     // 6月
      ""julyAmount"": 19900,     // 7月
      ""augustAmount"": 19900,   // 8月
      ""septemberAmount"": 19900, // 9月
      ""octoberAmount"": 19900,  // 10月
      ""novemberAmount"": 19900, // 11月
      ""decemberAmount"": 19900, // 12月
      ""januaryAmount"": 19900,  // 1月
      ""februaryAmount"": 19900, // 2月
      ""marchAmount"": 19900,    // 3月
      ""aprilAmount"": 19900,    // 4月
      ""mayAmount"": 19900,      // 5月
      ""confidence"": 0.95,
      ""matchReason"": ""匹配原因说明""
    }
  ],
  ""warnings"": [""任何警告信息""]
}

注意：
1. 住民税年度从6月开始到次年5月结束
2. 6月的金额通常与其他月份不同（有余数调整）
3. 尽量匹配员工列表中的员工，如果无法匹配则 employeeId 留空
4. 一张税单可能包含多个员工的数据";

            var http = httpClientFactory.CreateClient("openai");
            Server.Infrastructure.OpenAiApiHelper.SetOpenAiHeaders(http, apiKey);

            var contentParts = new List<object>();
            contentParts.Add(new { type = "text", text = "请解析这张住民税税单图片" });
            foreach (var im in imgs)
            {
                var url = im.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? im : ("data:image/jpeg;base64," + im);
                contentParts.Add(new { type = "image_url", image_url = new { url } });
            }

            var messages = new object[]
            {
                new { role = "system", content = sysPrompt },
                new { role = "user", content = contentParts.ToArray() }
            };

            var openAiResponse = await Server.Infrastructure.OpenAiApiHelper.CallOpenAiAsync(
                http, apiKey, "gpt-4o", messages, temperature: 0, maxTokens: 4096, jsonMode: true, ct: ct);

            if (string.IsNullOrWhiteSpace(openAiResponse.Content))
                return Results.StatusCode(500);

            try
            {
                var parseOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var parseResult = JsonSerializer.Deserialize<ResidentTaxParseResult>(openAiResponse.Content, parseOptions)
                                  ?? new ResidentTaxParseResult { Success = false };
                parseResult.Entries ??= new List<ResidentTaxParseEntry>();
                parseResult.Warnings ??= new List<string>();

                if (autoSave)
                {
                    int savedCount = 0, duplicateCount = 0, errorCount = 0;
                    await using var conn = await ds.OpenConnectionAsync(ct);

                    foreach (var entry in parseResult.Entries)
                    {
                        var resolvedEmployeeId = await ResolveResidentTaxEmployeeIdAsync(conn, companyCode, entry, ct);
                        if (!resolvedEmployeeId.HasValue)
                        {
                            entry.SaveStatus = "error";
                            entry.SaveMessage = "employee_not_matched";
                            errorCount++;
                            continue;
                        }
                        entry.EmployeeId = resolvedEmployeeId.Value.ToString();

                        if (entry.FiscalYear < 2000 || entry.FiscalYear > 2100)
                        {
                            entry.SaveStatus = "error";
                            entry.SaveMessage = "invalid_fiscal_year";
                            errorCount++;
                            continue;
                        }

                        var insertResult = await TryInsertResidentTaxAsync(conn, companyCode, resolvedEmployeeId.Value, entry, ct);
                        if (insertResult.insertedId.HasValue)
                        {
                            entry.SaveStatus = "saved";
                            entry.SavedId = insertResult.insertedId.Value.ToString();
                            savedCount++;
                        }
                        else
                        {
                            entry.SaveStatus = "duplicate";
                            entry.ExistingId = insertResult.existingId?.ToString();
                            duplicateCount++;
                        }
                    }

                    parseResult.AutoSaved = true;
                    parseResult.SavedCount = savedCount;
                    parseResult.DuplicateCount = duplicateCount;
                    parseResult.ErrorCount = errorCount;
                }

                return Results.Text(JsonSerializer.Serialize(parseResult, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "application/json");
            }
            catch
            {
                return Results.Text("{\"success\":false,\"error\":\"解析失败\"}", "application/json");
            }
        }).RequireAuthorization();

        // GET /resident-tax
        // 列出住民税数据，支持筛选
        app.MapGet("/resident-tax", async (HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString();

            var fiscalYear = req.Query["fiscalYear"].ToString();
            var employeeId = req.Query["employeeId"].ToString();
            var status = req.Query["status"].ToString();
            var page = int.TryParse(req.Query["page"], out var p) ? p : 1;
            var pageSize = int.TryParse(req.Query["pageSize"], out var ps) ? ps : 50;

            var conditions = new List<string> { "rt.company_code = $1" };
            var parameters = new List<object> { companyCode };
            var paramIndex = 2;

            if (!string.IsNullOrWhiteSpace(fiscalYear) && int.TryParse(fiscalYear, out var fy))
            {
                conditions.Add($"rt.fiscal_year = ${paramIndex++}");
                parameters.Add(fy);
            }
            if (!string.IsNullOrWhiteSpace(employeeId) && Guid.TryParse(employeeId, out var eid))
            {
                conditions.Add($"rt.employee_id = ${paramIndex++}");
                parameters.Add(eid);
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                conditions.Add($"rt.status = ${paramIndex++}");
                parameters.Add(status);
            }

            var whereClause = string.Join(" AND ", conditions);
            var offset = (page - 1) * pageSize;

            await using var conn = await ds.OpenConnectionAsync(ct);

            // 获取总数
            int total;
            await using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = $"SELECT COUNT(*) FROM resident_tax_schedules rt WHERE {whereClause}";
                for (int i = 0; i < parameters.Count; i++)
                    countCmd.Parameters.AddWithValue(parameters[i]);
                total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));
            }

            // 获取数据
            var data = new List<object>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT rt.id, rt.employee_id, rt.fiscal_year, rt.municipality_code, rt.municipality_name,
                           rt.annual_amount, rt.june_amount, rt.july_amount, rt.august_amount, rt.september_amount,
                           rt.october_amount, rt.november_amount, rt.december_amount, rt.january_amount,
                           rt.february_amount, rt.march_amount, rt.april_amount, rt.may_amount,
                           rt.status, rt.metadata->>'notes' as notes, rt.created_at, rt.updated_at,
                           e.employee_code, e.payload->>'nameKanji' as employee_name
                    FROM resident_tax_schedules rt
                    LEFT JOIN employees e ON e.id = rt.employee_id AND e.company_code = rt.company_code
                    WHERE {whereClause}
                    ORDER BY rt.fiscal_year DESC, e.employee_code ASC
                    LIMIT {pageSize} OFFSET {offset}";
                for (int i = 0; i < parameters.Count; i++)
                    cmd.Parameters.AddWithValue(parameters[i]);

                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    data.Add(new
                    {
                        id = rd.GetGuid(0),
                        employeeId = rd.GetGuid(1),
                        fiscalYear = rd.GetInt32(2),
                        municipalityCode = rd.IsDBNull(3) ? null : rd.GetString(3),
                        municipalityName = rd.IsDBNull(4) ? null : rd.GetString(4),
                        annualAmount = rd.GetDecimal(5),
                        juneAmount = rd.GetDecimal(6),
                        julyAmount = rd.GetDecimal(7),
                        augustAmount = rd.GetDecimal(8),
                        septemberAmount = rd.GetDecimal(9),
                        octoberAmount = rd.GetDecimal(10),
                        novemberAmount = rd.GetDecimal(11),
                        decemberAmount = rd.GetDecimal(12),
                        januaryAmount = rd.GetDecimal(13),
                        februaryAmount = rd.GetDecimal(14),
                        marchAmount = rd.GetDecimal(15),
                        aprilAmount = rd.GetDecimal(16),
                        mayAmount = rd.GetDecimal(17),
                        status = rd.IsDBNull(18) ? "active" : rd.GetString(18),
                        notes = rd.IsDBNull(19) ? null : rd.GetString(19),
                        createdAt = rd.GetDateTime(20),
                        updatedAt = rd.GetDateTime(21),
                        employeeCode = rd.IsDBNull(22) ? null : rd.GetString(22),
                        employeeName = rd.IsDBNull(23) ? null : rd.GetString(23)
                    });
                }
            }

            return Results.Ok(new { data, total, page, pageSize });
        }).RequireAuthorization();

        // POST /resident-tax
        // 创建住民税记录（带重复检测）
        app.MapPost("/resident-tax", async (HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString();

            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            var root = doc.RootElement;

            var employeeIdStr = root.TryGetProperty("employeeId", out var eid) && eid.ValueKind == JsonValueKind.String ? eid.GetString() : null;
            var fiscalYear = root.TryGetProperty("fiscalYear", out var fy) && fy.ValueKind == JsonValueKind.Number ? fy.GetInt32() : 0;

            if (string.IsNullOrWhiteSpace(employeeIdStr) || !Guid.TryParse(employeeIdStr, out var employeeId))
                return Results.BadRequest(new { error = "employeeId required" });
            if (fiscalYear < 2000 || fiscalYear > 2100)
                return Results.BadRequest(new { error = "fiscalYear invalid" });

            await using var conn = await ds.OpenConnectionAsync(ct);

            // 重复检测：检查是否已存在该员工该年度的住民税记录
            await using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT id, annual_amount FROM resident_tax_schedules WHERE company_code=$1 AND employee_id=$2 AND fiscal_year=$3";
                checkCmd.Parameters.AddWithValue(companyCode);
                checkCmd.Parameters.AddWithValue(employeeId);
                checkCmd.Parameters.AddWithValue(fiscalYear);
                await using var rd = await checkCmd.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct))
                {
                    var existingId = rd.GetGuid(0);
                    var existingAmount = rd.GetDecimal(1);
                    return Results.Conflict(new
                    {
                        error = "duplicate",
                        message = $"该员工 {fiscalYear} 年度的住民税记录已存在",
                        existingId = existingId.ToString(),
                        existingAnnualAmount = existingAmount,
                        suggestion = "如需更新，请使用 PUT 方法或先删除现有记录"
                    });
                }
            }

            // 解析各月金额
            decimal GetAmount(string prop) => root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0;
            var annualAmount = GetAmount("annualAmount");
            var juneAmount = GetAmount("juneAmount");
            var julyAmount = GetAmount("julyAmount");
            var augustAmount = GetAmount("augustAmount");
            var septemberAmount = GetAmount("septemberAmount");
            var octoberAmount = GetAmount("octoberAmount");
            var novemberAmount = GetAmount("novemberAmount");
            var decemberAmount = GetAmount("decemberAmount");
            var januaryAmount = GetAmount("januaryAmount");
            var februaryAmount = GetAmount("februaryAmount");
            var marchAmount = GetAmount("marchAmount");
            var aprilAmount = GetAmount("aprilAmount");
            var mayAmount = GetAmount("mayAmount");

            var municipalityCode = root.TryGetProperty("municipalityCode", out var mc) && mc.ValueKind == JsonValueKind.String ? mc.GetString() : null;
            var municipalityName = root.TryGetProperty("municipalityName", out var mn) && mn.ValueKind == JsonValueKind.String ? mn.GetString() : null;
            var notes = root.TryGetProperty("notes", out var nt) && nt.ValueKind == JsonValueKind.String ? nt.GetString() : null;

            // 插入记录
            Guid newId;
            await using (var insertCmd = conn.CreateCommand())
            {
                insertCmd.CommandText = @"
                    INSERT INTO resident_tax_schedules (
                        company_code, employee_id, fiscal_year, municipality_code, municipality_name,
                        annual_amount, june_amount, july_amount, august_amount, september_amount,
                        october_amount, november_amount, december_amount, january_amount,
                        february_amount, march_amount, april_amount, may_amount, notes
                    ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19)
                    RETURNING id";
                insertCmd.Parameters.AddWithValue(companyCode);
                insertCmd.Parameters.AddWithValue(employeeId);
                insertCmd.Parameters.AddWithValue(fiscalYear);
                insertCmd.Parameters.AddWithValue((object?)municipalityCode ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue((object?)municipalityName ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue(annualAmount);
                insertCmd.Parameters.AddWithValue(juneAmount);
                insertCmd.Parameters.AddWithValue(julyAmount);
                insertCmd.Parameters.AddWithValue(augustAmount);
                insertCmd.Parameters.AddWithValue(septemberAmount);
                insertCmd.Parameters.AddWithValue(octoberAmount);
                insertCmd.Parameters.AddWithValue(novemberAmount);
                insertCmd.Parameters.AddWithValue(decemberAmount);
                insertCmd.Parameters.AddWithValue(januaryAmount);
                insertCmd.Parameters.AddWithValue(februaryAmount);
                insertCmd.Parameters.AddWithValue(marchAmount);
                insertCmd.Parameters.AddWithValue(aprilAmount);
                insertCmd.Parameters.AddWithValue(mayAmount);
                insertCmd.Parameters.AddWithValue((object?)notes ?? DBNull.Value);

                newId = (Guid)(await insertCmd.ExecuteScalarAsync(ct))!;
            }

            return Results.Ok(new { id = newId, message = "住民税记录创建成功" });
        }).RequireAuthorization();

        // PUT /resident-tax/{id}
        // 更新住民税记录
        app.MapPut("/resident-tax/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString();

            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            var root = doc.RootElement;

            await using var conn = await ds.OpenConnectionAsync(ct);

            // 检查记录是否存在
            await using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT 1 FROM resident_tax_schedules WHERE id=$1 AND company_code=$2";
                checkCmd.Parameters.AddWithValue(id);
                checkCmd.Parameters.AddWithValue(companyCode);
                var exists = await checkCmd.ExecuteScalarAsync(ct);
                if (exists is null)
                    return Results.NotFound(new { error = "记录不存在" });
            }

            // 构建更新语句
            var updates = new List<string>();
            var parameters = new List<object> { id, companyCode };
            var paramIndex = 3;

            void TryAddDecimal(string jsonProp, string dbCol)
            {
                if (root.TryGetProperty(jsonProp, out var v) && v.ValueKind == JsonValueKind.Number)
                {
                    updates.Add($"{dbCol} = ${paramIndex++}");
                    parameters.Add(v.GetDecimal());
                }
            }
            void TryAddString(string jsonProp, string dbCol)
            {
                if (root.TryGetProperty(jsonProp, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    updates.Add($"{dbCol} = ${paramIndex++}");
                    parameters.Add(v.GetString()!);
                }
            }

            TryAddDecimal("annualAmount", "annual_amount");
            TryAddDecimal("juneAmount", "june_amount");
            TryAddDecimal("julyAmount", "july_amount");
            TryAddDecimal("augustAmount", "august_amount");
            TryAddDecimal("septemberAmount", "september_amount");
            TryAddDecimal("octoberAmount", "october_amount");
            TryAddDecimal("novemberAmount", "november_amount");
            TryAddDecimal("decemberAmount", "december_amount");
            TryAddDecimal("januaryAmount", "january_amount");
            TryAddDecimal("februaryAmount", "february_amount");
            TryAddDecimal("marchAmount", "march_amount");
            TryAddDecimal("aprilAmount", "april_amount");
            TryAddDecimal("mayAmount", "may_amount");
            TryAddString("municipalityCode", "municipality_code");
            TryAddString("municipalityName", "municipality_name");
            TryAddString("status", "status");
            TryAddString("notes", "notes");

            if (updates.Count == 0)
                return Results.BadRequest(new { error = "没有要更新的字段" });

            updates.Add("updated_at = now()");

            await using (var updateCmd = conn.CreateCommand())
            {
                updateCmd.CommandText = $"UPDATE resident_tax_schedules SET {string.Join(", ", updates)} WHERE id=$1 AND company_code=$2";
                for (int i = 0; i < parameters.Count; i++)
                    updateCmd.Parameters.AddWithValue(parameters[i]);
                await updateCmd.ExecuteNonQueryAsync(ct);
            }

            return Results.Ok(new { message = "更新成功" });
        }).RequireAuthorization();

        // DELETE /resident-tax/{id}
        // 删除住民税记录
        app.MapDelete("/resident-tax/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString();

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM resident_tax_schedules WHERE id=$1 AND company_code=$2";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(companyCode);
            var affected = await cmd.ExecuteNonQueryAsync(ct);

            if (affected == 0)
                return Results.NotFound(new { error = "记录不存在" });

            return Results.Ok(new { message = "删除成功" });
        }).RequireAuthorization();

        // GET /resident-tax/employee/{employeeId}/current
        // 获取员工当前适用的住民税（根据当前月份自动判断年度）
        app.MapGet("/resident-tax/employee/{employeeId:guid}/current", async (Guid employeeId, HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString();

            // 根据当前月份判断住民税年度（6月~次年5月）
            var now = DateTime.Now;
            var fiscalYear = now.Month >= 6 ? now.Year : now.Year - 1;

            // 可选：从查询参数指定月份
            var monthStr = req.Query["month"].ToString();
            if (!string.IsNullOrWhiteSpace(monthStr) && DateTime.TryParse(monthStr + "-01", out var monthDate))
            {
                fiscalYear = monthDate.Month >= 6 ? monthDate.Year : monthDate.Year - 1;
            }

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, fiscal_year, municipality_code, municipality_name, annual_amount,
                       june_amount, july_amount, august_amount, september_amount,
                       october_amount, november_amount, december_amount, january_amount,
                       february_amount, march_amount, april_amount, may_amount,
                       status, notes
                FROM resident_tax_schedules
                WHERE company_code=$1 AND employee_id=$2 AND fiscal_year=$3 AND status='active'
                LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(employeeId);
            cmd.Parameters.AddWithValue(fiscalYear);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct))
                return Results.Ok(new { found = false, fiscalYear, message = "未找到该员工的住民税记录" });

            return Results.Ok(new
            {
                found = true,
                id = rd.GetGuid(0),
                fiscalYear = rd.GetInt32(1),
                municipalityCode = rd.IsDBNull(2) ? null : rd.GetString(2),
                municipalityName = rd.IsDBNull(3) ? null : rd.GetString(3),
                annualAmount = rd.GetDecimal(4),
                juneAmount = rd.GetDecimal(5),
                julyAmount = rd.GetDecimal(6),
                augustAmount = rd.GetDecimal(7),
                septemberAmount = rd.GetDecimal(8),
                octoberAmount = rd.GetDecimal(9),
                novemberAmount = rd.GetDecimal(10),
                decemberAmount = rd.GetDecimal(11),
                januaryAmount = rd.GetDecimal(12),
                februaryAmount = rd.GetDecimal(13),
                marchAmount = rd.GetDecimal(14),
                aprilAmount = rd.GetDecimal(15),
                mayAmount = rd.GetDecimal(16),
                status = rd.IsDBNull(17) ? "active" : rd.GetString(17),
                notes = rd.IsDBNull(18) ? null : rd.GetString(18)
            });
        }).RequireAuthorization();

        // GET /resident-tax/summary
        // 获取住民税汇总统计
        app.MapGet("/resident-tax/summary", async (HttpRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var companyCode = cc.ToString();

            var fiscalYearStr = req.Query["fiscalYear"].ToString();
            var now = DateTime.Now;
            var fiscalYear = now.Month >= 6 ? now.Year : now.Year - 1;
            if (!string.IsNullOrWhiteSpace(fiscalYearStr) && int.TryParse(fiscalYearStr, out var fy))
                fiscalYear = fy;

            await using var conn = await ds.OpenConnectionAsync(ct);

            // 统计信息
            var summary = new
            {
                fiscalYear,
                totalEmployees = 0,
                registeredCount = 0,
                totalAnnualAmount = 0m,
                byMonth = new Dictionary<string, decimal>()
            };

            // 获取员工总数
            int totalEmployees;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM employees WHERE company_code=$1";
                cmd.Parameters.AddWithValue(companyCode);
                totalEmployees = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            }

            // 获取已登记的住民税记录
            int registeredCount;
            decimal totalAnnual;
            var monthlyTotals = new Dictionary<string, decimal>
            {
                ["june"] = 0, ["july"] = 0, ["august"] = 0, ["september"] = 0,
                ["october"] = 0, ["november"] = 0, ["december"] = 0, ["january"] = 0,
                ["february"] = 0, ["march"] = 0, ["april"] = 0, ["may"] = 0
            };

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT COUNT(*), COALESCE(SUM(annual_amount), 0),
                           COALESCE(SUM(june_amount), 0), COALESCE(SUM(july_amount), 0),
                           COALESCE(SUM(august_amount), 0), COALESCE(SUM(september_amount), 0),
                           COALESCE(SUM(october_amount), 0), COALESCE(SUM(november_amount), 0),
                           COALESCE(SUM(december_amount), 0), COALESCE(SUM(january_amount), 0),
                           COALESCE(SUM(february_amount), 0), COALESCE(SUM(march_amount), 0),
                           COALESCE(SUM(april_amount), 0), COALESCE(SUM(may_amount), 0)
                    FROM resident_tax_schedules
                    WHERE company_code=$1 AND fiscal_year=$2 AND status='active'";
                cmd.Parameters.AddWithValue(companyCode);
                cmd.Parameters.AddWithValue(fiscalYear);

                await using var rd = await cmd.ExecuteReaderAsync(ct);
                await rd.ReadAsync(ct);
                registeredCount = rd.GetInt32(0);
                totalAnnual = rd.GetDecimal(1);
                monthlyTotals["june"] = rd.GetDecimal(2);
                monthlyTotals["july"] = rd.GetDecimal(3);
                monthlyTotals["august"] = rd.GetDecimal(4);
                monthlyTotals["september"] = rd.GetDecimal(5);
                monthlyTotals["october"] = rd.GetDecimal(6);
                monthlyTotals["november"] = rd.GetDecimal(7);
                monthlyTotals["december"] = rd.GetDecimal(8);
                monthlyTotals["january"] = rd.GetDecimal(9);
                monthlyTotals["february"] = rd.GetDecimal(10);
                monthlyTotals["march"] = rd.GetDecimal(11);
                monthlyTotals["april"] = rd.GetDecimal(12);
                monthlyTotals["may"] = rd.GetDecimal(13);
            }

            return Results.Ok(new
            {
                fiscalYear,
                totalEmployees,
                registeredCount,
                unregisteredCount = totalEmployees - registeredCount,
                totalAnnualAmount = totalAnnual,
                byMonth = monthlyTotals
            });
        }).RequireAuthorization();
    }

    private static async Task<Guid?> ResolveResidentTaxEmployeeIdAsync(NpgsqlConnection conn, string companyCode, ResidentTaxParseEntry entry, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(entry.EmployeeId) && Guid.TryParse(entry.EmployeeId, out var parsed))
            return parsed;

        if (!string.IsNullOrWhiteSpace(entry.EmployeeCode))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM employees WHERE company_code=$1 AND (employee_code=$2 OR payload->>'code'=$2) LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(entry.EmployeeCode);
            var id = await cmd.ExecuteScalarAsync(ct);
            if (id is Guid g) return g;
        }

        if (!string.IsNullOrWhiteSpace(entry.EmployeeName))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id FROM employees 
                WHERE company_code=$1 
                  AND (payload->>'nameKanji'=$2 OR payload->>'name'=$2 OR payload->>'nameKana'=$2)
                LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(entry.EmployeeName);
            var id = await cmd.ExecuteScalarAsync(ct);
            if (id is Guid g) return g;
        }

        return null;
    }

    private static async Task<(Guid? insertedId, Guid? existingId)> TryInsertResidentTaxAsync(
        NpgsqlConnection conn,
        string companyCode,
        Guid employeeId,
        ResidentTaxParseEntry entry,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO resident_tax_schedules (
                company_code, employee_id, fiscal_year, municipality_code, municipality_name,
                annual_amount, june_amount, july_amount, august_amount, september_amount,
                october_amount, november_amount, december_amount, january_amount,
                february_amount, march_amount, april_amount, may_amount, metadata
            ) VALUES (
                $1,$2,$3,$4,$5,
                $6,$7,$8,$9,$10,
                $11,$12,$13,$14,
                $15,$16,$17,$18,$19
            )
            ON CONFLICT (company_code, employee_id, fiscal_year) DO NOTHING
            RETURNING id";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(employeeId);
        cmd.Parameters.AddWithValue(entry.FiscalYear);
        cmd.Parameters.AddWithValue((object?)entry.MunicipalityCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)entry.MunicipalityName ?? DBNull.Value);
        cmd.Parameters.AddWithValue(entry.AnnualAmount);
        cmd.Parameters.AddWithValue(entry.JuneAmount);
        cmd.Parameters.AddWithValue(entry.JulyAmount);
        cmd.Parameters.AddWithValue(entry.AugustAmount);
        cmd.Parameters.AddWithValue(entry.SeptemberAmount);
        cmd.Parameters.AddWithValue(entry.OctoberAmount);
        cmd.Parameters.AddWithValue(entry.NovemberAmount);
        cmd.Parameters.AddWithValue(entry.DecemberAmount);
        cmd.Parameters.AddWithValue(entry.JanuaryAmount);
        cmd.Parameters.AddWithValue(entry.FebruaryAmount);
        cmd.Parameters.AddWithValue(entry.MarchAmount);
        cmd.Parameters.AddWithValue(entry.AprilAmount);
        cmd.Parameters.AddWithValue(entry.MayAmount);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(new
        {
            entry.EmployeeCode,
            entry.EmployeeName,
            entry.Confidence,
            entry.MatchReason
        }));

        var inserted = await cmd.ExecuteScalarAsync(ct);
        if (inserted is Guid insertedId)
            return (insertedId, null);

        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT id FROM resident_tax_schedules WHERE company_code=$1 AND employee_id=$2 AND fiscal_year=$3";
        checkCmd.Parameters.AddWithValue(companyCode);
        checkCmd.Parameters.AddWithValue(employeeId);
        checkCmd.Parameters.AddWithValue(entry.FiscalYear);
        var existing = await checkCmd.ExecuteScalarAsync(ct);
        return (null, existing as Guid?);
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
        (string? Description, JsonElement? PayrollConfig) GetActiveSalaryEntry(JsonElement employee, string targetMonth)
        {
            if (employee.TryGetProperty("salaries", out var salariesArr) && salariesArr.ValueKind == JsonValueKind.Array)
            {
                var monthStart = targetMonth + "-01";
                string? activeSalary = null;
                string? latestStartDate = null;
                JsonElement? activeConfig = null;
                
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
                    
                    if (string.Compare(startDate, monthStart, StringComparison.Ordinal) <= 0)
                    {
                        if (latestStartDate == null || string.Compare(startDate, latestStartDate, StringComparison.Ordinal) > 0)
                        {
                            latestStartDate = startDate;
                            activeSalary = description;
                            activeConfig = sal.TryGetProperty("payrollConfig", out var pc) && pc.ValueKind == JsonValueKind.Object
                                ? pc : null;
                        }
                    }
                }
                
                if (!string.IsNullOrWhiteSpace(activeSalary))
                    return (activeSalary, activeConfig);
            }
            
            if (employee.TryGetProperty("nlPayrollDescription", out var nl) && nl.ValueKind == JsonValueKind.String)
                return (nl.GetString(), null);
            
            return (null, null);
        }
        
        var (empNlDescription, empPayrollConfig) = GetActiveSalaryEntry(emp, month);
        
        var hasStructuredConfig = empPayrollConfig.HasValue;
        if (debug)
        {
            trace?.Add(new { step="input.employee", employeeId = employeeId.ToString(), month, nl = empNlDescription, hasStructuredConfig });
        }

        decimal nlBase = 0m; decimal nlCommute = 0m; decimal nlInsuranceBase = 0m;
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
                // 社保・年金の計算基数（標準報酬月額）を独立して指定可能
                // 例: "基本給35万円、標準報酬30万円" → 給与は35万、社保・年金は30万で計算
                nlInsuranceBase = ParseAmountNearLocal(empNl, new[]{
                    "標準報酬", "標準報酬月額", "報酬月額", "算定基礎",  // 日本語正式用語
                    "社保基数", "保険基数", "社会保険基数", "年金基数"   // 中国語表現
                });
            }
        } catch {}
        
        // 时薪判断和金额解析已移至 Policy 规则中处理
        // 后端不再硬编码时薪关键词，由 Policy 规则的 activation.salaryDescriptionContains 定义
        decimal nlHourlyRate = 0m;
        
        if (debug) trace?.Add(new { step="input.employee.nlParsed", baseAmount = nlBase, commuteAmount = nlCommute, insuranceBase = nlInsuranceBase, salaryDescription = empNlDescription });

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

            // 检查是否有显式的 DSL rules
            bool hasExplicitRules = pBody.TryGetProperty("rules", out var explicitRules) && explicitRules.ValueKind == JsonValueKind.Array && explicitRules.GetArrayLength() > 0;
            
            if (hasExplicitRules)
            {
                rulesElement = explicitRules;
                
                // 优先使用 Policy 中定义的 journalRules（包括 dsl.journalRules）
                if (pBody.TryGetProperty("journalRules", out var jr) && jr.ValueKind == JsonValueKind.Array && jr.GetArrayLength() > 0)
                {
                    compiledJournalRulesElement = jr;
                    if (debug) trace?.Add(new { step = "journal.rules.fromPolicy", source = "policy.journalRules", count = jr.GetArrayLength() });
                }
                else if (pBody.TryGetProperty("dsl", out var dslEl) && dslEl.ValueKind == JsonValueKind.Object &&
                         dslEl.TryGetProperty("journalRules", out var dslJr) && dslJr.ValueKind == JsonValueKind.Array && dslJr.GetArrayLength() > 0)
                {
                    compiledJournalRulesElement = dslJr;
                    if (debug) trace?.Add(new { step = "journal.rules.fromPolicy", source = "dsl.journalRules", count = dslJr.GetArrayLength() });
                }
                else
                {
                    // 如果 Policy 中没有定义 journalRules，则基于 DSL rules 自动生成（作为 fallback）
                    var dslItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rule in explicitRules.EnumerateArray())
                    {
                        if (rule.TryGetProperty("item", out var itemProp) && itemProp.ValueKind == JsonValueKind.String)
                        {
                            var itemCode = itemProp.GetString();
                            if (!string.IsNullOrWhiteSpace(itemCode)) dslItems.Add(itemCode);
                        }
                    }
                    
                    var autoJournalRules = new List<object>();
                    object BuildJI(string code, decimal? sign = null) => sign.HasValue ? new { code, sign } : new { code };
                    
                    // 基本給/各種手当 → 給与手当/未払費用
                    var wageItems = new List<object>();
                    if (dslItems.Contains("BASE")) wageItems.Add(BuildJI("BASE"));
                    if (dslItems.Contains("COMMUTE")) wageItems.Add(BuildJI("COMMUTE"));
                    if (dslItems.Contains("OVERTIME_STD")) wageItems.Add(BuildJI("OVERTIME_STD"));
                    if (dslItems.Contains("OVERTIME_60")) wageItems.Add(BuildJI("OVERTIME_60"));
                    if (dslItems.Contains("HOLIDAY_PAY")) wageItems.Add(BuildJI("HOLIDAY_PAY"));
                    if (dslItems.Contains("LATE_NIGHT_PAY")) wageItems.Add(BuildJI("LATE_NIGHT_PAY"));
                    if (dslItems.Contains("ABSENCE_DEDUCT")) wageItems.Add(BuildJI("ABSENCE_DEDUCT", -1m));
                    if (wageItems.Count > 0)
                        autoJournalRules.Add(new { name = "wages", debitAccount = "832", creditAccount = "315", items = wageItems.ToArray(), description = "基本給および各種手当を給与手当／未払費用で仕訳" });
                    
                    // 以下扣除项：从未払費用中扣除，转入预提金
                    // 会计分录：DR 315 未払費用（减少应付员工）/ CR 預り金（增加代扣负债）
                    
                    // 社会保険（健康保険+介護保険を合算）
                    if (dslItems.Contains("HEALTH_INS") || dslItems.Contains("CARE_INS"))
                    {
                        var healthItems = new List<object>();
                        if (dslItems.Contains("HEALTH_INS")) healthItems.Add(BuildJI("HEALTH_INS"));
                        if (dslItems.Contains("CARE_INS")) healthItems.Add(BuildJI("CARE_INS"));
                        autoJournalRules.Add(new { name = "health", debitAccount = "315", creditAccount = "3181", items = healthItems.ToArray(), description = "未払費用／社会保険預り金" });
                    }
                    
                    // 厚生年金
                    if (dslItems.Contains("PENSION"))
                        autoJournalRules.Add(new { name = "pension", debitAccount = "315", creditAccount = "3182", items = new[] { BuildJI("PENSION") }, description = "未払費用／厚生年金預り金" });
                    
                    // 雇用保険
                    if (dslItems.Contains("EMP_INS"))
                        autoJournalRules.Add(new { name = "employment", debitAccount = "315", creditAccount = "3183", items = new[] { BuildJI("EMP_INS") }, description = "未払費用／雇用保険預り金" });
                    
                    // 源泉徴収税
                    if (dslItems.Contains("WHT"))
                        autoJournalRules.Add(new { name = "withholding", debitAccount = "315", creditAccount = "3184", items = new[] { BuildJI("WHT") }, description = "未払費用／源泉所得税預り金" });
                    
                    if (autoJournalRules.Count > 0)
                    {
                        var jrArray = new System.Text.Json.Nodes.JsonArray(autoJournalRules.Select(o => System.Text.Json.JsonSerializer.SerializeToNode(o)!).ToArray());
                        compiledJournalRulesDoc = JsonDocument.Parse(jrArray.ToJsonString());
                        compiledJournalRulesElement = compiledJournalRulesDoc.RootElement;
                        if (debug) trace?.Add(new { step = "journal.rules.auto", source = "dsl-items-fallback", count = autoJournalRules.Count, items = dslItems.ToArray() });
                    }
                }
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
            // === 構造化設定（payrollConfig）があればそれを優先、なければ従来のキーワード判定 ===
            bool wantsHealth, wantsPension, wantsEmployment, wantsWithholding, wantsResidentTax;
            bool wantsOvertime, wantsOvertime60, wantsHolidayWork, wantsLateNight, wantsAbsence;

            bool CfgBool(string prop) => hasStructuredConfig
                && empPayrollConfig!.Value.TryGetProperty(prop, out var v)
                && v.ValueKind == JsonValueKind.True;
            decimal CfgNum(string prop) => hasStructuredConfig
                && empPayrollConfig!.Value.TryGetProperty(prop, out var v)
                && (v.ValueKind == JsonValueKind.Number) ? v.GetDecimal() : 0m;

            if (hasStructuredConfig)
            {
                // 構造化設定から直接読み取り
                if (CfgNum("baseSalary") > 0) nlBase = CfgNum("baseSalary");
                if (CfgNum("commuteAllowance") > 0) nlCommute = CfgNum("commuteAllowance");
                if (CfgNum("insuranceBase") > 0) nlInsuranceBase = CfgNum("insuranceBase");

                wantsHealth = CfgBool("socialInsurance");
                wantsPension = CfgBool("socialInsurance");
                wantsEmployment = CfgBool("employmentInsurance");
                wantsWithholding = CfgBool("incomeTax");
                wantsResidentTax = CfgBool("residentTax");
                wantsOvertime = CfgBool("overtime");
                wantsOvertime60 = wantsOvertime; // overtime60 follows overtime setting
                wantsHolidayWork = CfgBool("holidayWork");
                wantsLateNight = CfgBool("lateNight");
                wantsAbsence = CfgBool("absenceDeduction");

                if (debug) trace?.Add(new { step = "structuredConfig.applied",
                    nlBase, nlCommute, nlInsuranceBase,
                    wantsHealth, wantsPension, wantsEmployment, wantsWithholding,
                    wantsResidentTax, wantsOvertime, wantsHolidayWork, wantsLateNight, wantsAbsence });
            }
            else
            {
                // フォールバック：従来のキーワードベース判定
                bool excludeSocialIns = ContainsAny(empText, SocialInsuranceExcludeKeywords);
                bool wantsHealthRaw = (!string.IsNullOrWhiteSpace(empText) && (empText.Contains("社会保険", StringComparison.OrdinalIgnoreCase) || empText.Contains("社保", StringComparison.OrdinalIgnoreCase) || empText.Contains("健康保険", StringComparison.OrdinalIgnoreCase)))
                                   || (!string.IsNullOrWhiteSpace(companyTextInPolicy) && (companyTextInPolicy.Contains("社会保険", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("社保", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("健康保険", StringComparison.OrdinalIgnoreCase)));
                wantsHealth = wantsHealthRaw && !excludeSocialIns;
                bool wantsPensionRaw = (!string.IsNullOrWhiteSpace(empText) && (empText.Contains("厚生年金", StringComparison.OrdinalIgnoreCase) || empText.Contains("年金", StringComparison.OrdinalIgnoreCase)))
                                   || (!string.IsNullOrWhiteSpace(companyTextInPolicy) && (companyTextInPolicy.Contains("厚生年金", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("年金", StringComparison.OrdinalIgnoreCase)));
                wantsPension = wantsPensionRaw && !excludeSocialIns;
                if (debug) trace?.Add(new { step = "socialInsurance.check", empText, wantsHealthRaw, wantsPensionRaw, excludeSocialIns, wantsHealth, wantsPension });
                bool wantsEmploymentEmp = !string.IsNullOrWhiteSpace(empText) && (empText.Contains("雇用保険", StringComparison.OrdinalIgnoreCase) || empText.Contains("雇佣保险", StringComparison.OrdinalIgnoreCase));
                bool wantsEmploymentCompany = !string.IsNullOrWhiteSpace(companyTextInPolicy) && (companyTextInPolicy.Contains("雇用保険", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("雇佣保险", StringComparison.OrdinalIgnoreCase));
                bool excludeEmployment = ContainsAny(empText, EmploymentInsuranceExcludeKeywords);
                wantsEmployment = (wantsEmploymentEmp || wantsEmploymentCompany) && !excludeEmployment;
                if (debug) trace?.Add(new { step = "employment.check", empText, wantsEmploymentEmp, wantsEmploymentCompany, excludeEmployment, wantsEmployment });
                wantsWithholding = (!string.IsNullOrWhiteSpace(empText) && (empText.Contains("源泉", StringComparison.OrdinalIgnoreCase) || empText.Contains("月額表", StringComparison.OrdinalIgnoreCase) || empText.Contains("甲欄", StringComparison.OrdinalIgnoreCase)))
                                   || (!string.IsNullOrWhiteSpace(companyTextInPolicy) && (companyTextInPolicy.Contains("源泉", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("月額表", StringComparison.OrdinalIgnoreCase) || companyTextInPolicy.Contains("甲欄", StringComparison.OrdinalIgnoreCase)));
                wantsResidentTax = ContainsAny(empText, "住民税", "住民稅", "市民税", "県民税", "地方税", "特別徴収")
                                   || ContainsAny(companyTextInPolicy, "住民税", "住民稅", "市民税", "県民税", "地方税", "特別徴収");
                wantsOvertime = ContainsAny(empText, "残業", "時間外", "加班") || ContainsAny(companyTextInPolicy, "残業", "時間外", "加班");
                wantsOvertime60 = ContainsAny(empText, "60時間", "６０時間", "60h", "60小时") || ContainsAny(companyTextInPolicy, "60時間", "６０時間", "60h", "60小时");
                wantsHolidayWork = ContainsAny(empText, "休日", "祝日", "节假日", "節假日", "法定休日") || ContainsAny(companyTextInPolicy, "休日", "祝日", "节假日", "節假日", "法定休日");
                wantsLateNight = ContainsAny(empText, "深夜", "22時", "22点", "22點", "late night", "夜間") || ContainsAny(companyTextInPolicy, "深夜", "22時", "22点", "22點", "late night", "夜間");
                wantsAbsence = ContainsAny(empText, "欠勤", "欠勤控除", "欠勤・遅刻", "勤怠控除", "工时不足", "工時不足", "遅刻", "早退")
                                    || ContainsAny(companyTextInPolicy, "欠勤", "欠勤控除", "欠勤・遅刻", "勤怠控除", "工时不足", "工時不足", "遅刻", "早退");
            }

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
                // 社保基数が指定されている場合はそれを使用、なければ基本給を使用
                var insuranceBaseVal = nlInsuranceBase > 0 ? nlInsuranceBase : nlBase;
                object baseExpr; if (insuranceBaseVal > 0) baseExpr = new { _const = insuranceBaseVal }; else baseExpr = new { baseRef = "employee.healthBase" };
                compiled.Add(new { item = "HEALTH_INS", type = "deduction", formula = new { _base = baseExpr, rate = "policy.law.health.rate" } });
                // 介护保险：40岁~64岁员工自动计算
                compiled.Add(new { item = "CARE_INS", type = "deduction", formula = new { _base = baseExpr, rate = "policy.law.care.rate" } });
            }
            if (wantsPension)
            {
                // 社保基数が指定されている場合はそれを使用、なければ基本給を使用
                var insuranceBaseVal = nlInsuranceBase > 0 ? nlInsuranceBase : nlBase;
                object baseExpr; if (insuranceBaseVal > 0) baseExpr = new { _const = insuranceBaseVal }; else baseExpr = new { baseRef = "employee.pensionBase" };
                compiled.Add(new { item = "PENSION", type = "deduction", formula = new { _base = baseExpr, rate = "policy.law.pension.rate" } });
            }
            // 雇用保険：計算基数 = 賃金 + 通勤手当（日本の雇用保険法に基づく）
            if (wantsEmployment)
            {
                // 明示的に基本給・通勤手当がある場合は sum 式、なければ salaryTotal（時給制も対応）
                object baseExpr; if (nlBase>0 || nlCommute>0) baseExpr = new { sum = new object[]{ new { _const = nlBase }, new { _const = nlCommute } } }; else baseExpr = new { baseRef = "employee.salaryTotal" };
                compiled.Add(new { item = "EMP_INS", type = "deduction", formula = new { _base = baseExpr, rate = "policy.law.employment.rate" }, description = "雇用保険（基数=賃金+通勤手当）" });
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

            // 如果 Policy 有 DSL rules，默认生成所有标准会计凭证（不再依赖自然语言描述）
            bool hasDslRules = pBody.TryGetProperty("rules", out var dslRulesCheck) && dslRulesCheck.ValueKind == JsonValueKind.Array && dslRulesCheck.GetArrayLength() > 0;
            
            // 检查 DSL rules 中定义了哪些项目
            var dslItemCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (hasDslRules)
            {
                foreach (var rule in dslRulesCheck.EnumerateArray())
                {
                    if (rule.TryGetProperty("item", out var itemProp) && itemProp.ValueKind == JsonValueKind.String)
                    {
                        var itemCode = itemProp.GetString();
                        if (!string.IsNullOrWhiteSpace(itemCode)) dslItemCodes.Add(itemCode);
                    }
                }
            }
            
            // 如果有 DSL rules，根据 DSL 中定义的项目来决定生成哪些会计凭证
            bool useDefaultJournal = hasDslRules || hasBaseEmp || hasBaseCompany || isHourlyRateMode;
            bool dslHasHealth = dslItemCodes.Contains("HEALTH_INS");
            bool dslHasPension = dslItemCodes.Contains("PENSION");
            bool dslHasEmployment = dslItemCodes.Contains("EMP_INS");
            bool dslHasWithholding = dslItemCodes.Contains("WHT");
            bool dslHasResidentTax = dslItemCodes.Contains("RESIDENT_TAX");
            bool dslHasBase = dslItemCodes.Contains("BASE");
            
            var wageJournalItems = new List<object>();
            // 如果 DSL 有 BASE 规则，或者自然语言描述中有基本给关键字，或者是时薪制
            if (dslHasBase || hasBaseEmp || hasBaseCompany || isHourlyRateMode) wageJournalItems.Add(BuildJournalItem("BASE"));
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
            // 以下扣除项：从未払費用中扣除，转入预提金
            // 会计分录：DR 315 未払費用（减少应付员工）/ CR 預り金（增加代扣负债）
            
            // 社会保険（健康保険+介護保険）：DSL 有定义，或自然语言描述中有关键字
            if (dslHasHealth || wantsHealth)
            {
                var healthJournalItems = new List<object> { BuildJournalItem("HEALTH_INS"), BuildJournalItem("CARE_INS") };
                AddJournalRuleLocal("health", ResolveJournalDebitLocal("health", "315"), ResolveJournalCreditLocal("health", "3181"), healthJournalItems, "未払費用／社会保険預り金");
                if (debug) trace?.Add(new { step = "journal.builder", scope = "fallback", rule = "health" });
            }
            // 厚生年金：DSL 有定义，或自然语言描述中有关键字
            if (dslHasPension || wantsPension)
            {
                AddJournalRuleLocal("pension", ResolveJournalDebitLocal("pension", "315"), ResolveJournalCreditLocal("pension", "3182"), new[] { BuildJournalItem("PENSION") }, "未払費用／厚生年金預り金");
                if (debug) trace?.Add(new { step = "journal.builder", scope = "fallback", rule = "pension" });
            }
            // 雇用保険：DSL 有定义，或自然语言描述中有关键字
            if (dslHasEmployment || wantsEmployment)
            {
                AddJournalRuleLocal("employment", ResolveJournalDebitLocal("employment", "315"), ResolveJournalCreditLocal("employment", "3183"), new[] { BuildJournalItem("EMP_INS") }, "未払費用／雇用保険預り金");
                if (debug) trace?.Add(new { step = "journal.builder", scope = "fallback", rule = "employment" });
            }
            // 源泉徴収税：DSL 有定义，或自然语言描述中有关键字
            if (dslHasWithholding || wantsWithholding)
            {
                AddJournalRuleLocal("withholding", ResolveJournalDebitLocal("withholding", "315"), ResolveJournalCreditLocal("withholding", "3184"), new[] { BuildJournalItem("WHT") }, "未払費用／源泉所得税預り金");
                if (debug) trace?.Add(new { step = "journal.builder", scope = "fallback", rule = "withholding" });
            }
            // 住民税：DSL 有定义，或自然语言描述中有关键字
            if (dslHasResidentTax || wantsResidentTax)
            {
                AddJournalRuleLocal("residentTax", ResolveJournalDebitLocal("residentTax", "315"), ResolveJournalCreditLocal("residentTax", "3185"), new[] { BuildJournalItem("RESIDENT_TAX") }, "未払費用／住民税預り金");
                if (debug) trace?.Add(new { step = "journal.builder", scope = "fallback", rule = "residentTax" });
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

        // 如果没有 compiledJournalRulesElement，从文本生成（仅在 Policy/DSL 未提供 journalRules 时）
        if (compiledJournalRulesElement is null)
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
            var whtTable = await LoadWithholdingTableAsync(ds, monthDate, ct);
            // 加载住民税金额
            var residentTaxAmount = await LoadResidentTaxAmountAsync(conn, companyCode, employeeId, monthDate, ct);
            ApplyPolicyRules(
                rulesEl,
                sheetOut,
                emp,
                policyBody.Value,
                whtTable,
                nlBase,
                nlCommute,
                nlHourlyRate,
                nlInsuranceBase,
                debug,
                trace,
                law,
                monthDate,
                workHourSummary,
                policyBody,
                empNlDescription,
                residentTaxAmount);
        }
        var journal = new List<JournalLine>();
        
        // 使用 journalRules（优先从 Policy 读取，fallback 到自动生成）
        if (compiledJournalRulesElement.HasValue &&
            compiledJournalRulesElement.Value.ValueKind == JsonValueKind.Array &&
            compiledJournalRulesElement.Value.GetArrayLength() > 0)
        {
            journal = BuildJournalLinesFromDsl(compiledJournalRulesElement.Value, sheetOut, out var runUnmapped);
            if (runUnmapped is { Count: > 0 })
            {
                var items = string.Join("、", runUnmapped.Select(u => $"{u.Name ?? u.Code}({u.Amount:N0}円)"));
                throw new PayrollExecutionException(StatusCodes.Status400BadRequest,
                    new { error = $"以下の給与項目に対応する会計科目が設定されていません: {items}", unmappedItems = runUnmapped.Select(u => new { u.Code, u.Name, u.Amount }) },
                    $"Unmapped payroll items: {items}");
            }
            if (debug)
            {
                trace?.Add(new
                {
                    step = "journal.rules.applied",
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
            var msg = "会計科目が存在しません: " + string.Join(",", missing);
            if (debug)
                throw new PayrollExecutionException(StatusCodes.Status400BadRequest, new { error = msg, missingAccounts = missing, hint = "「会計科目」マスタで登録するか、/admin/accounts/seed/payroll で初期化してください。" }, msg);
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
        
        // 检查员工是否缺少工资信息
        bool hasNoSalaryInfo = string.IsNullOrWhiteSpace(empNlDescription) && nlBase <= 0;
        bool hasEmptySheet = sheetOut.Count == 0 || sheetOut.All(item => 
        {
            var amt = ReadJsonDecimal(item, "amount");
            return amt == 0m;
        });
        
        if (hasNoSalaryInfo)
        {
            var displayName = employeeNameOut ?? employeeCodeOut ?? employeeId.ToString();
            if (hasEmptySheet)
            {
                // 没有工资信息且没有计算结果 - 这是一个错误
                warnings.Add(new JsonObject
                {
                    ["code"] = "noSalaryInfo",
                    ["severity"] = "error",
                    ["message"] = $"{displayName} の従業員マスタに給与情報（基本給・時給など）が登録されていないため、給与を計算できません。従業員マスタの「給与情報」を設定してください。"
                });
            }
            else
            {
                // 没有工资信息但有计算结果（可能来自 Policy 默认值）- 这是一个警告
                warnings.Add(new JsonObject
                {
                    ["code"] = "noSalaryInfo",
                    ["severity"] = "warning",
                    ["message"] = $"{displayName} の従業員マスタに給与情報が登録されていません。計算結果はポリシーのデフォルト値を使用しています。"
                });
            }
        }

        return new PayrollExecutionResult(sheetOut, enriched, traceJson, employeeCodeOut, employeeNameOut, departmentCodeOut, departmentNameOut, netAmount, workHoursJson, warnings);
    }

    // 加载別表第一甲欄税額表（查表方式）
    private static async Task<List<WithholdingTableEntry>> LoadWithholdingTableAsync(
        NpgsqlDataSource ds,
        DateTime monthDate,
        CancellationToken ct)
    {
        var list = new List<WithholdingTableEntry>();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT COALESCE(min_amount,0), max_amount, dependents, tax_amount,
                                       COALESCE(calc_type,'table'), base_tax, rate, COALESCE(version,'')
                                FROM withholding_table
                                WHERE category = 'monthly_ko_table'
                                  AND effective_from <= $1
                                  AND (effective_to IS NULL OR effective_to >= $1)
                                ORDER BY dependents, min_amount NULLS FIRST";
            cmd.Parameters.AddWithValue(new DateTime(monthDate.Year, monthDate.Month, 1));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var min = reader.GetDecimal(0);
                var max = reader.IsDBNull(1) ? (decimal?)null : reader.GetDecimal(1);
                var dependents = reader.GetInt32(2);
                var taxAmount = reader.GetDecimal(3);
                var calcType = reader.GetString(4);
                var baseTax = reader.IsDBNull(5) ? (decimal?)null : reader.GetDecimal(5);
                var rate = reader.IsDBNull(6) ? (decimal?)null : reader.GetDecimal(6);
                var version = reader.GetString(7);
                list.Add(new WithholdingTableEntry(min, max, dependents, taxAmount, calcType, baseTax, rate, version));
            }
        }
        catch
        {
            // ignore load errors; proceed with empty list
        }

        return list;
    }

    // 加载住民税金额（根据员工和月份从 resident_tax_schedules 表查询）
    private static async Task<decimal> LoadResidentTaxAmountAsync(
        NpgsqlConnection conn,
        string companyCode,
        Guid employeeId,
        DateTime monthDate,
        CancellationToken ct)
    {
        try
        {
            // 住民税年度：6月~次年5月为一个年度
            // 例如：2025年6月~2026年5月属于 fiscal_year=2025
            var fiscalYear = monthDate.Month >= 6 ? monthDate.Year : monthDate.Year - 1;
            
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT june_amount, july_amount, august_amount, september_amount,
                                       october_amount, november_amount, december_amount,
                                       january_amount, february_amount, march_amount, april_amount, may_amount
                                FROM resident_tax_schedules
                                WHERE company_code = $1 AND employee_id = $2 AND fiscal_year = $3 AND status = 'active'
                                LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(employeeId);
            cmd.Parameters.AddWithValue(fiscalYear);
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return 0m; // 没有住民税记录
            
            // 根据月份返回对应的金额
            // 字段顺序：june(0), july(1), august(2), september(3), october(4), november(5), 
            //          december(6), january(7), february(8), march(9), april(10), may(11)
            var monthIndex = monthDate.Month switch
            {
                6 => 0,  // june
                7 => 1,  // july
                8 => 2,  // august
                9 => 3,  // september
                10 => 4, // october
                11 => 5, // november
                12 => 6, // december
                1 => 7,  // january
                2 => 8,  // february
                3 => 9,  // march
                4 => 10, // april
                5 => 11, // may
                _ => -1
            };
            
            if (monthIndex < 0) return 0m;
            
            return reader.IsDBNull(monthIndex) ? 0m : reader.GetDecimal(monthIndex);
        }
        catch
        {
            return 0m;
        }
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
        IReadOnlyList<WithholdingTableEntry> whtTable,
        decimal nlBase,
        decimal nlCommute,
        decimal nlHourlyRate,
        decimal nlInsuranceBase,
        bool debug,
        List<object>? trace,
        LawDatasetService law,
        DateTime monthDate,
        WorkHourSummary workHours,
        JsonElement? policyRoot,
        string? empSalaryDescription = null,
        decimal residentTaxAmount = 0m)
    {
        // 辅助函数：检查员工是否匹配规则的 activation 条件
        bool CheckActivation(JsonElement rule)
        {
            if (rule.TryGetProperty("item", out var itemEl) && itemEl.ValueKind == JsonValueKind.String)
            {
                var item = itemEl.GetString();
                if (string.Equals(item, "EMP_INS", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(empSalaryDescription) && ContainsAny(empSalaryDescription, EmploymentInsuranceExcludeKeywords))
                        return false;
                }
                if (string.Equals(item, "HEALTH_INS", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item, "CARE_INS", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item, "PENSION", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(empSalaryDescription) && ContainsAny(empSalaryDescription, SocialInsuranceExcludeKeywords))
                        return false;
                }
            }

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
            // salaryTotal: 雇用保険計算基数 = 賃金 + 通勤手当（日本の雇用保険法に基づく）
            // 月給制：基本給 + 通勤手当
            // 時給制：時給 × 工時 + 通勤手当
            if (key.Equals("salaryTotal", StringComparison.OrdinalIgnoreCase))
            {
                var baseFromNl = (nlBase > 0 ? nlBase : 0m) + (nlCommute > 0 ? nlCommute : 0m);
                if (baseFromNl > 0) return baseFromNl;
                // 時給制員工：使用 時給 × 工時 + 通勤手当 作為 salaryTotal（用於雇用保険計算）
                if (nlHourlyRate > 0 && workHours.HasData)
                {
                    return nlHourlyRate * workHours.TotalHours + (nlCommute > 0 ? nlCommute : 0m);
                }
                return 0m;
            }
            // 社保・年金基数：優先使用指定的社保基数、なければ基本給を使用
            if (key.Equals("healthBase", StringComparison.OrdinalIgnoreCase)) return nlInsuranceBase > 0 ? nlInsuranceBase : (nlBase > 0 ? nlBase : 0m);
            if (key.Equals("pensionBase", StringComparison.OrdinalIgnoreCase)) return nlInsuranceBase > 0 ? nlInsuranceBase : (nlBase > 0 ? nlBase : 0m);
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
                            decimal sumEarn = 0m, si = 0m, pens = 0m, empIns = 0m;
                    foreach (var entry in sheetOut)
                            {
                        var code = ReadJsonString(entry, "itemCode");
                        var amt = ReadJsonDecimal(entry, "amount");
                                if (string.Equals(code, "BASE", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "COMMUTE", StringComparison.OrdinalIgnoreCase)) sumEarn += amt;
                                else if (string.Equals(code, "HEALTH_INS", StringComparison.OrdinalIgnoreCase)) si += amt;
                                else if (string.Equals(code, "PENSION", StringComparison.OrdinalIgnoreCase)) pens += amt;
                                else if (string.Equals(code, "EMP_INS", StringComparison.OrdinalIgnoreCase)) empIns += amt;
                            }
                    // 課税給与 = 総支給額 - 社会保険料等（健康保険 + 厚生年金 + 雇用保険）
                    var taxable = sumEarn - si - pens - empIns;
                    if (taxable < 0) taxable = 0;
                    
                    // 获取扶养人数（从公式配置或员工信息中读取，默认0）
                    // 支持数组形式（扶養親族リスト）或数字形式（直接指定人数）
                    int dependents = 0;
                    if (wht.TryGetProperty("dependents", out var depNode) && depNode.ValueKind == JsonValueKind.Number)
                    {
                        dependents = depNode.GetInt32();
                    }
                    else if (emp.TryGetProperty("dependents", out var empDep))
                    {
                        if (empDep.ValueKind == JsonValueKind.Array)
                        {
                            // 扶養親族リストの場合、控除対象の人数を計算
                            int CountEligibleDependents(JsonElement list)
                            {
                                var count = 0;
                                var cutoffDate = new DateTime(monthDate.Year, 12, 31);
                                foreach (var dep in list.EnumerateArray())
                                {
                                    if (dep.ValueKind != JsonValueKind.Object) continue;
                                    var nameKana = dep.TryGetProperty("nameKana", out var nk) ? nk.GetString() : null;
                                    var nameKanji = dep.TryGetProperty("nameKanji", out var nj) ? nj.GetString() : null;
                                    var birthDate = dep.TryGetProperty("birthDate", out var bd) ? bd.GetString() : null;
                                    var gender = dep.TryGetProperty("gender", out var gd) ? gd.GetString() : null;
                                    var relation = dep.TryGetProperty("relation", out var rl) ? rl.GetString() : null;
                                    var address = dep.TryGetProperty("address", out var ad) ? ad.GetString() : null;

                                    // 空行はスキップ
                                    if (string.IsNullOrWhiteSpace(nameKana)
                                        && string.IsNullOrWhiteSpace(nameKanji)
                                        && string.IsNullOrWhiteSpace(birthDate)
                                        && string.IsNullOrWhiteSpace(gender)
                                        && string.IsNullOrWhiteSpace(relation)
                                        && string.IsNullOrWhiteSpace(address))
                                    {
                                        continue;
                                    }

                                    // 必須項目が欠けている場合は控除対象外
                                    if ((string.IsNullOrWhiteSpace(nameKana) && string.IsNullOrWhiteSpace(nameKanji))
                                        || string.IsNullOrWhiteSpace(birthDate)
                                        || string.IsNullOrWhiteSpace(gender)
                                        || string.IsNullOrWhiteSpace(relation)
                                        || string.IsNullOrWhiteSpace(address))
                                    {
                                        continue;
                                    }

                                    // 年齢判定：12/31 時点で 16 歳未満は控除対象外
                                    if (!DateTime.TryParse(birthDate, out var bdDate)) continue;
                                    var age = cutoffDate.Year - bdDate.Year;
                                    if (bdDate > cutoffDate.AddYears(-age)) age--;
                                    if (age < 16) continue;

                                    count++;
                                }
                                return count;
                            }

                            dependents = CountEligibleDependents(empDep);
                        }
                        else if (empDep.ValueKind == JsonValueKind.Number)
                        {
                            dependents = empDep.GetInt32();
                        }
                    }
                    else if (emp.TryGetProperty("扶養人数", out var empDepJp) && empDepJp.ValueKind == JsonValueKind.Number)
                    {
                        dependents = empDepJp.GetInt32();
                    }
                    if (dependents < 0) dependents = 0;
                    int extraDependents = 0;
                    if (dependents > 7)
                    {
                        extraDependents = dependents - 7;
                        dependents = 7; // 7人超は7人の税額から控除
                    }
                    
                    // 使用別表第一甲欄（税額表方式）查表
                    foreach (var tableEntry in whtTable)
                    {
                        if (tableEntry.Dependents != dependents) continue;
                        if (!string.Equals(tableEntry.CalcType, "table", StringComparison.OrdinalIgnoreCase)) continue;
                        var geMin = taxable >= tableEntry.Min;
                        var ltMax = !tableEntry.Max.HasValue || taxable < tableEntry.Max.Value;
                        if (geMin && ltMax)
                        {
                            var tax = tableEntry.TaxAmount;
                            var totalDependents = dependents + extraDependents;
                            var extraDeduction = extraDependents > 0 ? extraDependents * 1610m : 0m;
                            if (extraDependents > 0)
                            {
                                tax = Math.Max(0m, tax - extraDeduction);
                            }
                            var note = $"withholding:扶養親族{totalDependents}人";
                            if (extraDependents > 0) note += $"(7人超{extraDependents}人×1610円控除)";
                            return new FormulaEvalResult(tax, null, taxable, tableEntry.Version, note);
                        }
                    }
                    // 高額帯は算式（公式）で計算（同表内の formula 行を利用）
                    foreach (var formula in whtTable)
                    {
                        if (formula.Dependents != dependents) continue;
                        if (!string.Equals(formula.CalcType, "formula", StringComparison.OrdinalIgnoreCase)) continue;
                        var geMin = taxable >= formula.Min;
                        var ltMax = !formula.Max.HasValue || taxable < formula.Max.Value;
                        if (geMin && ltMax)
                        {
                            if (!formula.BaseTax.HasValue || !formula.Rate.HasValue)
                            {
                                continue;
                            }
                            var tax = formula.BaseTax.Value + (taxable - formula.Min) * formula.Rate.Value;
                            tax = ApplyRounding(Math.Max(0m, tax), "round", 0);
                            var totalDependents = dependents + extraDependents;
                            var extraDeduction = extraDependents > 0 ? extraDependents * 1610m : 0m;
                            if (extraDependents > 0)
                            {
                                tax = Math.Max(0m, tax - extraDeduction);
                            }
                            var note = $"withholding:formula:扶養親族{totalDependents}人";
                            if (extraDependents > 0) note += $"(7人超{extraDependents}人×1610円控除)";
                            return new FormulaEvalResult(tax, formula.Rate, taxable, formula.Version, note);
                        }
                    }
                    // 警告：未找到匹配的税额表记录，可能是数据不完整或扶养人数超出范围
                    // 返回特殊标记以便调用方识别
                    return new FormulaEvalResult(0m, null, taxable, null, $"withholding:not_found:dep={dependents}:taxable={taxable}");
                }
                // 住民税计算：从 resident_tax_schedules 表加载的金额
                if (f.TryGetProperty("residentTax", out var rtNode) && rtNode.ValueKind == JsonValueKind.Object)
                {
                    // 直接返回已加载的住民税金额
                    return new FormulaEvalResult(residentTaxAmount, null, null, null, residentTaxAmount > 0 ? "residentTax:db" : "residentTax:not_found");
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
                            if (t.baseOverride.HasValue) { baseVal = t.baseOverride.Value; baseMark = baseVal; }
                        }
                        else if (string.Equals(key, "policy.law.pension.rate", StringComparison.OrdinalIgnoreCase))
                        {
                                var t = law.GetPensionRate(emp, policyBody, monthDate, baseVal);
                            rateVal = t.rate; rateMark = t.rate; lawVer = t.version; lawNote = $"pension:{t.note}";
                            if (t.baseOverride.HasValue) { baseVal = t.baseOverride.Value; baseMark = baseVal; }
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
                        else if (string.Equals(key, "policy.law.care.rate", StringComparison.OrdinalIgnoreCase))
                        {
                            var t = law.GetCareInsuranceRate(emp, policyBody, monthDate, baseVal);
                            rateVal = t.rate; rateMark = t.rate; lawVer = t.version; lawNote = $"care:{t.note}";
                            if (t.baseOverride.HasValue) { baseVal = t.baseOverride.Value; baseMark = baseVal; }
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

    /// <summary>
    /// キーワードマッチングで給与説明テキストを解析し payrollConfig を生成する。
    /// LLM 不要で常に動作し、/payroll/parse-salary-description の主処理として使用される。
    /// </summary>
    private static object ParseSalaryConfigByKeyword(string description)
    {
        var s = description.Normalize(NormalizationForm.FormKC); // 全角→半角正規化

        // ── 金額抽出ヘルパー ─────────────────────────────────────────────────────
        decimal? ExtractAmountNear(string[] anchors)
        {
            var normalizedAnchors = anchors.Select(a => a.Normalize(NormalizationForm.FormKC)).ToArray();
            int anchorPos = -1;
            foreach (var anchor in normalizedAnchors)
            {
                var pos = s.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
                if (pos >= 0 && (anchorPos < 0 || pos < anchorPos)) anchorPos = pos;
            }
            if (anchorPos < 0) return null;

            // アンカーの前後 40 文字を検索対象に
            var start = Math.Max(0, anchorPos - 10);
            var end = Math.Min(s.Length, anchorPos + 40);
            var seg = s[start..end];

            // パターン1: 数値 + 万円 / 万 (e.g. "30万円", "30万", "３０万")
            var mMan = System.Text.RegularExpressions.Regex.Match(seg, @"([\d,]+)\s*万\s*円?");
            if (mMan.Success && decimal.TryParse(mMan.Groups[1].Value.Replace(",", ""), out var manVal))
                return manVal * 10000m;

            // パターン2: 数値 + 円 (e.g. "300,000円", "300000円")
            var mYen = System.Text.RegularExpressions.Regex.Match(seg, @"([\d,]+)\s*円");
            if (mYen.Success && decimal.TryParse(mYen.Groups[1].Value.Replace(",", ""), out var yenVal))
                return yenVal;

            // パターン3: 数値のみ（1000以上） (e.g. "300000")
            var mNum = System.Text.RegularExpressions.Regex.Match(seg, @"\b([\d]{4,})\b");
            if (mNum.Success && decimal.TryParse(mNum.Groups[1].Value, out var numVal) && numVal >= 1000m)
                return numVal;

            return null;
        }

        // ── 金額フィールド ────────────────────────────────────────────────────────
        var baseSalary      = ExtractAmountNear(new[] { "基本給", "月給", "月给", "基本工资", "固定給", "固定", "月額", "基本给" });
        var commuteAllowance = ExtractAmountNear(CommuteKeywords);
        var insuranceBase   = ExtractAmountNear(new[] { "標準報酬", "標準報酬月額", "報酬月額", "算定基礎", "社保基数", "保険基数" });

        // ── ブール判定 ────────────────────────────────────────────────────────────
        bool socialInsurance =
            ContainsAny(s, "社会保険", "社保", "健康保険", "厚生年金") &&
            !ContainsAny(s, SocialInsuranceExcludeKeywords);

        bool employmentInsurance =
            ContainsAny(s, "雇用保険", "雇佣保险") &&
            !ContainsAny(s, EmploymentInsuranceExcludeKeywords);

        bool incomeTax =
            ContainsAny(s, "源泉", "源泉徴収", "所得税", "月額表", "甲欄");

        bool residentTax =
            ContainsAny(s, "住民税", "市民税", "県民税", "地方税", "特別徴収");

        bool overtime =
            ContainsAny(s, "残業", "時間外", "残業手当", "時間外手当", "加班");

        bool holidayWork =
            ContainsAny(s, "休日手当", "休日労働", "法定休日", "休日出勤", "节假日");

        bool lateNight =
            ContainsAny(s, "深夜", "深夜手当", "22時", "夜間手当");

        bool absenceDeduction =
            ContainsAny(s, "欠勤", "欠勤控除", "遅刻", "早退", "勤怠控除");

        return new
        {
            baseSalary       = baseSalary.HasValue       ? (object)baseSalary.Value       : null,
            commuteAllowance = commuteAllowance.HasValue ? (object)commuteAllowance.Value : null,
            insuranceBase    = insuranceBase.HasValue    ? (object)insuranceBase.Value    : null,
            socialInsurance,
            employmentInsurance,
            incomeTax,
            residentTax,
            overtime,
            holidayWork,
            lateNight,
            absenceDeduction
        };
    }

    /// <summary>
    /// LLM 解析結果をキーワード解析結果にマージする。
    /// LLM が null でないフィールドのみ上書きし、キーワード結果を補完する。
    /// </summary>
    private static object MergeLlmIntoKeywordConfig(object keywordConfig, JsonElement llmElement)
    {
        decimal? GetNum(string key) =>
            llmElement.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number
                ? (decimal?)p.GetDecimal() : null;
        bool? GetBool(string key) =>
            llmElement.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True  ? true
          : llmElement.TryGetProperty(key, out var p2) && p2.ValueKind == JsonValueKind.False ? false
          : (bool?)null;

        // キーワード結果をリフレクションで取得してマージ
        var kType = keywordConfig.GetType();
        decimal? KNum(string name) { var v = kType.GetProperty(name)?.GetValue(keywordConfig); return v is decimal d ? d : (decimal?)null; }
        bool KBool(string name) { var v = kType.GetProperty(name)?.GetValue(keywordConfig); return v is bool b && b; }

        return new
        {
            baseSalary       = (object?)(GetNum("baseSalary")       ?? KNum("baseSalary")),
            commuteAllowance = (object?)(GetNum("commuteAllowance") ?? KNum("commuteAllowance")),
            insuranceBase    = (object?)(GetNum("insuranceBase")    ?? KNum("insuranceBase")),
            socialInsurance      = GetBool("socialInsurance")      ?? KBool("socialInsurance"),
            employmentInsurance  = GetBool("employmentInsurance")  ?? KBool("employmentInsurance"),
            incomeTax            = GetBool("incomeTax")            ?? KBool("incomeTax"),
            residentTax          = GetBool("residentTax")          ?? KBool("residentTax"),
            overtime             = GetBool("overtime")             ?? KBool("overtime"),
            holidayWork          = GetBool("holidayWork")          ?? KBool("holidayWork"),
            lateNight            = GetBool("lateNight")            ?? KBool("lateNight"),
            absenceDeduction     = GetBool("absenceDeduction")     ?? KBool("absenceDeduction")
        };
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

internal sealed class ResidentTaxParseResult
{
    public bool Success { get; set; }
    public string? RawText { get; set; }
    public List<ResidentTaxParseEntry>? Entries { get; set; }
    public List<string>? Warnings { get; set; }
    public bool AutoSaved { get; set; }
    public int SavedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int ErrorCount { get; set; }
}

internal sealed class ResidentTaxParseEntry
{
    public string? EmployeeId { get; set; }
    public string? EmployeeCode { get; set; }
    public string? EmployeeName { get; set; }
    public int FiscalYear { get; set; }
    public string? MunicipalityCode { get; set; }
    public string? MunicipalityName { get; set; }
    public decimal AnnualAmount { get; set; }
    public decimal JuneAmount { get; set; }
    public decimal JulyAmount { get; set; }
    public decimal AugustAmount { get; set; }
    public decimal SeptemberAmount { get; set; }
    public decimal OctoberAmount { get; set; }
    public decimal NovemberAmount { get; set; }
    public decimal DecemberAmount { get; set; }
    public decimal JanuaryAmount { get; set; }
    public decimal FebruaryAmount { get; set; }
    public decimal MarchAmount { get; set; }
    public decimal AprilAmount { get; set; }
    public decimal MayAmount { get; set; }
    public decimal Confidence { get; set; }
    public string? MatchReason { get; set; }

    public string? SaveStatus { get; set; }
    public string? SaveMessage { get; set; }
    public string? SavedId { get; set; }
    public string? ExistingId { get; set; }
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
