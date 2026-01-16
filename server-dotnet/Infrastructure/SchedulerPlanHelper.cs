using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Server.Infrastructure;

public static class SchedulerPlanHelper
{
    public static (JsonObject? Plan, JsonObject? Schedule, string[] Notes) Interpret(string companyCode, string? nlSpec)
    {
        var notes = new List<string>();
        if (string.IsNullOrWhiteSpace(nlSpec))
        {
            notes.Add("缺少自然语言描述，无法解析计划");
            return (null, null, notes.ToArray());
        }

        var text = nlSpec.Trim();
        var lowered = text.ToLowerInvariant();
        JsonObject? plan = null;
        JsonObject? schedule = null;

        // Moneytree bank sync
        // Example NL:
        // - "每天12点和0点自动同步银行明细"
        // - "毎日12時と0時に銀行明細を連携"
        // - "每天午间12点和深夜0点自动连携Moneytree银行数据，失败重试3次"
        if (plan is null && (
            lowered.Contains("moneytree") || lowered.Contains("银行明细") || lowered.Contains("银行连携") ||
            lowered.Contains("銀行明細") || lowered.Contains("銀行連携") || lowered.Contains("bank sync") ||
            lowered.Contains("银行同步") || lowered.Contains("銀行同期")))
        {
            plan = new JsonObject
            {
                ["action"] = "moneytree.sync"
            };

            // Date range: default dynamic (last_success to today)
            plan["dateRange"] = new JsonObject
            {
                ["kind"] = "dynamic",
                ["start"] = "last_success",
                ["end"] = "today"
            };

            // Parse multiple times for daily schedule
            var times = ParseMultipleTimes(text);
            if (times.Count == 0)
            {
                // Default: 12:00 and 00:00 JST
                times.Add(new TimeSpan(12, 0, 0));
                times.Add(new TimeSpan(0, 0, 0));
            }

            schedule = new JsonObject
            {
                ["kind"] = times.Count > 1 ? "daily_multi" : "daily",
                ["timezone"] = "Asia/Tokyo"
            };

            if (times.Count > 1)
            {
                var timesArray = new JsonArray();
                foreach (var t in times.OrderBy(x => x))
                {
                    timesArray.Add(t.ToString(@"hh\:mm"));
                }
                schedule["times"] = timesArray;
            }
            else
            {
                schedule["time"] = times[0].ToString(@"hh\:mm");
            }

            // Retry settings
            int maxRetry = 3;
            int retryInterval = 10;
            var retryMatch = Regex.Match(text, @"(?:重试|リトライ|retry)\s*(\d+)\s*(?:次|回|times)?", RegexOptions.IgnoreCase);
            if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out var retryCount) && retryCount > 0)
            {
                maxRetry = Math.Min(retryCount, 10);
            }
            var intervalMatch = Regex.Match(text, @"(?:间隔|間隔|interval)\s*(\d+)\s*(?:分钟|分|分鐘|min)", RegexOptions.IgnoreCase);
            if (intervalMatch.Success && int.TryParse(intervalMatch.Groups[1].Value, out var interval) && interval > 0)
            {
                retryInterval = Math.Clamp(interval, 1, 60);
            }
            schedule["retry"] = new JsonObject
            {
                ["maxAttempts"] = maxRetry,
                ["intervalMinutes"] = retryInterval
            };

            notes.Add($"识别为银行明细同步任务，每日 {string.Join("、", times.Select(t => t.ToString(@"hh\:mm")))} (日本时间) 执行，失败最多重试 {maxRetry} 次");
        }

        // Timesheet compliance check (Japan common management lines)
        // Example NL:
        // - "毎日9時に勤怠をチェック。月残業45時間超は警告、80超は重大、100超は危険。通知はADMIN。"
        if (plan is null && (lowered.Contains("勤怠") || lowered.Contains("工数") || lowered.Contains("工时") || lowered.Contains("timesheet"))
            && (lowered.Contains("労基") || lowered.Contains("36") || lowered.Contains("３６") || lowered.Contains("残業") || lowered.Contains("overwork") || lowered.Contains("overtime") || lowered.Contains("超過") || lowered.Contains("警告") || lowered.Contains("alert")))
        {
            plan = new JsonObject
            {
                ["action"] = "timesheet.compliance_check"
            };

            // Month scope: default current month-to-date; allow explicit "上月/上个月/先月/前月"
            string month = DateTime.UtcNow.ToString("yyyy-MM");
            if (lowered.Contains("上月") || lowered.Contains("上个月") || lowered.Contains("上個月") || lowered.Contains("先月") || lowered.Contains("前月"))
            {
                var dt = DateTime.UtcNow.AddMonths(-1);
                month = new DateTime(dt.Year, dt.Month, 1).ToString("yyyy-MM");
            }
            else
            {
                var monthMatch = Regex.Match(text, "(20\\d{2})[年/-]\\s*(0?[1-9]|1[0-2])");
                if (monthMatch.Success)
                {
                    var y = int.Parse(monthMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                    var m = int.Parse(monthMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                    month = new DateTime(y, m, 1).ToString("yyyy-MM");
                }
            }
            plan["month"] = month;

            // Thresholds (hours/month). Defaults aligned with common JP management lines:
            // warn: 45h, high: 80h, critical: 100h
            int warn = 45, high = 80, critical = 100;
            // Parse threshold numbers with context to avoid capturing time-of-day like "09:00".
            var numsRaw = new List<int>();
            void addMatches(string pattern)
            {
                foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
                {
                    if (!m.Success) continue;
                    if (!int.TryParse(m.Groups[1].Value, out var v)) continue;
                    if (v <= 0 || v > 300) continue;
                    numsRaw.Add(v);
                }
            }
            // "45時間" / "80h"
            addMatches("(\\d{1,3})\\s*(?:時間|h)");
            // ">45" / ">=45" / "＞45"
            addMatches("(?:>=|=>|≤|>=|>|＜|<|＞=|＞)\\s*(\\d{1,3})");
            // "45超" / "45以上" / "45超え"
            addMatches("(\\d{1,3})\\s*(?:超|以上|超过|超え)");

            var nums = numsRaw
                .Where(v => v >= 10) // guard
                .Distinct()
                .OrderBy(v => v)
                .ToList();

            // Heuristic: pick warn as the smallest in [30,60] if present; otherwise keep default 45.
            var warnCandidate = nums.FirstOrDefault(v => v >= 30 && v <= 60);
            if (warnCandidate > 0) warn = warnCandidate;
            // high as the smallest > warn (prefer around 70-90), else default 80.
            var highCandidate = nums.FirstOrDefault(v => v > warn && v <= 90);
            if (highCandidate > 0) high = highCandidate;
            else
            {
                var anyHigh = nums.FirstOrDefault(v => v > warn);
                if (anyHigh > 0) high = anyHigh;
            }
            // critical as the smallest > high, else default 100.
            var critCandidate = nums.FirstOrDefault(v => v > high);
            if (critCandidate > 0) critical = critCandidate;
            plan["thresholds"] = new JsonObject
            {
                ["warnOvertimeHours"] = warn,
                ["highOvertimeHours"] = high,
                ["criticalOvertimeHours"] = critical
            };

            // Notification target: default ADMIN role.
            plan["notify"] = new JsonObject
            {
                ["by"] = "role",
                ["roleCode"] = "ADMIN"
            };

            // Schedule: default daily 09:00 JST unless NL specifies.
            schedule = new JsonObject
            {
                ["kind"] = "daily",
                ["timezone"] = "Asia/Tokyo"
            };
            var time = ParseTime(text) ?? new TimeSpan(9, 0, 0);
            schedule["time"] = time.ToString("hh\\:mm");
            if (lowered.Contains("每月") || lowered.Contains("每个月") || lowered.Contains("毎月") || lowered.Contains("monthly"))
            {
                schedule["kind"] = "monthly";
                var dayMatch = Regex.Match(text, "([0-9]{1,2})[日号號]");
                int day = dayMatch.Success ? Math.Clamp(int.Parse(dayMatch.Groups[1].Value, CultureInfo.InvariantCulture), 1, 28) : 5;
                schedule["dayOfMonth"] = day;
            }
        }

        if (lowered.Contains("工资") || lowered.Contains("薪资") || lowered.Contains("payroll"))
        {
            plan = new JsonObject
            {
                ["action"] = "payroll.batch_execute"
            };

            string month = DateTime.UtcNow.ToString("yyyy-MM");
            var monthMatch = Regex.Match(text, "(20\\d{2})[年/-]\\s*(0?[1-9]|1[0-2])");
            if (monthMatch.Success)
            {
                var y = int.Parse(monthMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                var m = int.Parse(monthMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                month = new DateTime(y, m, 1).ToString("yyyy-MM");
            }
            else if (lowered.Contains("上月") || lowered.Contains("上个月") || lowered.Contains("上個月"))
            {
                var dt = DateTime.UtcNow.AddMonths(-1);
                month = new DateTime(dt.Year, dt.Month, 1).ToString("yyyy-MM");
            }
            else if (lowered.Contains("下月") || lowered.Contains("下个月") || lowered.Contains("下個月"))
            {
                var dt = DateTime.UtcNow.AddMonths(1);
                month = new DateTime(dt.Year, dt.Month, 1).ToString("yyyy-MM");
            }
            plan["month"] = month;

            plan["employeeFilter"] = new JsonObject
            {
                ["kind"] = "all_active"
            };

            schedule = new JsonObject();
            bool hasMonthly = lowered.Contains("每月") || lowered.Contains("每个月") || lowered.Contains("每月度") || lowered.Contains("每個月");
            bool hasDaily = lowered.Contains("每天") || lowered.Contains("每日");

            var time = ParseTime(text) ?? new TimeSpan(20, 0, 0);

            if (hasDaily && !hasMonthly)
            {
                schedule["kind"] = "daily";
                schedule["time"] = time.ToString("hh\\:mm");
            }
            else
            {
                schedule["kind"] = "monthly";
                schedule["time"] = time.ToString("hh\\:mm");
                var dayMatch = Regex.Match(text, "([0-9]{1,2})[日号號]");
                int day = dayMatch.Success ? Math.Clamp(int.Parse(dayMatch.Groups[1].Value, CultureInfo.InvariantCulture), 1, 28) : 25;
                schedule["dayOfMonth"] = day;
            }
        }

        return (plan, schedule, notes.ToArray());
    }

    private static TimeSpan? ParseTime(string text)
    {
        var match = Regex.Match(text, "(?:(上午|下午|晚上|早上|傍晚|午间|深夜|pm|AM|PM|am)\\s*)?(\\d{1,2})(?:[:：](\\d{1,2}))?\\s*(点|時|:|：)?", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        int hour = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        int minute = 0;
        if (match.Groups[3].Success)
        {
            _ = int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out minute);
        }
        var prefix = match.Groups[1].Value.ToLowerInvariant();
        if (prefix is "下午" or "晚上" or "pm")
        {
            if (hour < 12) hour += 12;
        }
        else if (prefix is "上午" or "早上" or "am")
        {
            if (hour == 12) hour = 0;
        }
        else if (prefix is "午间")
        {
            if (hour < 12) hour = 12; // 午间默认12点
        }
        else if (prefix is "深夜")
        {
            if (hour > 6) hour = 0; // 深夜默认0点
        }
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);
        return new TimeSpan(hour, minute, 0);
    }

    /// <summary>
    /// Parse multiple times from text like "12点和0点" or "12時と0時"
    /// </summary>
    private static List<TimeSpan> ParseMultipleTimes(string text)
    {
        var times = new List<TimeSpan>();
        // Match patterns like "12点和0点", "12時と0時", "12:00, 00:00"
        var pattern = @"(?:(上午|下午|晚上|早上|午间|深夜|pm|AM|PM|am)\s*)?(\d{1,2})(?:[:：](\d{1,2}))?\s*(?:点|時|时)?";
        var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase);
        
        foreach (Match match in matches)
        {
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[2].Value, out var hour)) continue;
            
            int minute = 0;
            if (match.Groups[3].Success)
            {
                _ = int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out minute);
            }
            
            var prefix = match.Groups[1].Value.ToLowerInvariant();
            if (prefix is "下午" or "晚上" or "pm")
            {
                if (hour < 12) hour += 12;
            }
            else if (prefix is "上午" or "早上" or "am")
            {
                if (hour == 12) hour = 0;
            }
            else if (prefix is "午间")
            {
                if (hour < 12) hour = 12;
            }
            else if (prefix is "深夜")
            {
                if (hour > 6) hour = 0;
            }
            
            hour = Math.Clamp(hour, 0, 23);
            minute = Math.Clamp(minute, 0, 59);
            var ts = new TimeSpan(hour, minute, 0);
            
            if (!times.Contains(ts))
            {
                times.Add(ts);
            }
        }
        
        return times;
    }

    public static DateTimeOffset? ComputeNextOccurrence(JsonObject? schedule, DateTimeOffset fromUtc)
    {
        if (schedule is null) return null;
        var kind = schedule.TryGetPropertyValue("kind", out var kindNode) && kindNode is JsonValue kv && kv.TryGetValue<string>(out var kindStr)
            ? kindStr.ToLowerInvariant()
            : "once";

        var tzInfo = TimeZoneInfo.Utc;
        if (schedule.TryGetPropertyValue("timezone", out var tzNode) && tzNode is JsonValue tzVal && tzVal.TryGetValue<string>(out var tzId) && !string.IsNullOrWhiteSpace(tzId))
        {
            try { tzInfo = TimeZoneInfo.FindSystemTimeZoneById(tzId); }
            catch { }
        }

        var referenceLocal = TimeZoneInfo.ConvertTime(fromUtc, tzInfo);
        var time = GetTimeOfDay(schedule) ?? new TimeSpan(2, 0, 0);

        DateTimeOffset targetLocal;
        switch (kind)
        {
            case "daily_multi":
                // Multiple times per day
                var times = GetTimesOfDay(schedule);
                if (times.Count == 0) times.Add(new TimeSpan(12, 0, 0));
                
                // Find next occurrence: check today's remaining times first, then tomorrow's first time
                DateTimeOffset? nextMulti = null;
                foreach (var t in times.OrderBy(x => x))
                {
                    var candidate = new DateTimeOffset(referenceLocal.Date.Add(t), referenceLocal.Offset);
                    if (candidate > referenceLocal)
                    {
                        nextMulti = candidate;
                        break;
                    }
                }
                if (!nextMulti.HasValue)
                {
                    // All today's times passed, use tomorrow's first time
                    var firstTime = times.OrderBy(x => x).First();
                    nextMulti = new DateTimeOffset(referenceLocal.Date.AddDays(1).Add(firstTime), referenceLocal.Offset);
                }
                return TimeZoneInfo.ConvertTime(nextMulti.Value, TimeZoneInfo.Utc);
                
            case "daily":
                targetLocal = new DateTimeOffset(referenceLocal.Date.Add(time), referenceLocal.Offset);
                if (targetLocal <= referenceLocal) targetLocal = targetLocal.AddDays(1);
                break;
            case "weekly":
                var dayOfWeek = DayOfWeek.Monday;
                if (schedule.TryGetPropertyValue("dayOfWeek", out var dowNode))
                {
                    if (dowNode is JsonValue dval && dval.TryGetValue<int>(out var dowInt))
                    {
                        dayOfWeek = (DayOfWeek)Math.Clamp(dowInt, 0, 6);
                    }
                    else if (dowNode is JsonValue dvalStr && dvalStr.TryGetValue<string>(out var dowStr) && Enum.TryParse<DayOfWeek>(dowStr, true, out var parsed))
                    {
                        dayOfWeek = parsed;
                    }
                }
                targetLocal = new DateTimeOffset(referenceLocal.Date.Add(time), referenceLocal.Offset);
                int delta = ((int)dayOfWeek - (int)referenceLocal.DayOfWeek + 7) % 7;
                if (delta == 0 && targetLocal <= referenceLocal) delta = 7;
                targetLocal = targetLocal.AddDays(delta);
                break;
            case "monthly":
                int day = 1;
                if (schedule.TryGetPropertyValue("dayOfMonth", out var domNode) && domNode is JsonValue domVal && domVal.TryGetValue<int>(out var domInt))
                {
                    day = Math.Clamp(domInt, 1, 28);
                }
                targetLocal = BuildMonthly(referenceLocal, day, time);
                if (targetLocal <= referenceLocal)
                {
                    var nextMonth = referenceLocal.AddMonths(1);
                    targetLocal = BuildMonthly(nextMonth, day, time);
                }
                break;
            default:
                if (schedule.TryGetPropertyValue("runAt", out var runAtNode) && runAtNode is JsonValue runVal && runVal.TryGetValue<string>(out var runAtText) && DateTimeOffset.TryParse(runAtText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var runAtDt))
                {
                    if (runAtDt > fromUtc) return runAtDt.ToUniversalTime();
                }
                return null;
        }

        return TimeZoneInfo.ConvertTime(targetLocal, TimeZoneInfo.Utc);
    }

    private static TimeSpan? GetTimeOfDay(JsonObject schedule)
    {
        if (schedule.TryGetPropertyValue("time", out var timeNode) && timeNode is JsonValue timeVal && timeVal.TryGetValue<string>(out var timeStr))
        {
            if (TimeSpan.TryParseExact(timeStr, "hh\\:mm", CultureInfo.InvariantCulture, out var ts))
                return ts;
            if (TimeSpan.TryParse(timeStr, CultureInfo.InvariantCulture, out ts))
                return ts;
        }
        return null;
    }

    private static List<TimeSpan> GetTimesOfDay(JsonObject schedule)
    {
        var result = new List<TimeSpan>();
        if (schedule.TryGetPropertyValue("times", out var timesNode) && timesNode is JsonArray timesArray)
        {
            foreach (var item in timesArray)
            {
                if (item is JsonValue val && val.TryGetValue<string>(out var timeStr))
                {
                    if (TimeSpan.TryParseExact(timeStr, "hh\\:mm", CultureInfo.InvariantCulture, out var ts))
                        result.Add(ts);
                    else if (TimeSpan.TryParse(timeStr, CultureInfo.InvariantCulture, out ts))
                        result.Add(ts);
                }
            }
        }
        else
        {
            var single = GetTimeOfDay(schedule);
            if (single.HasValue) result.Add(single.Value);
        }
        return result;
    }

    /// <summary>
    /// Compute next retry time based on retry settings in schedule.
    /// </summary>
    public static DateTimeOffset? ComputeRetryTime(JsonObject? schedule, int currentAttempt, DateTimeOffset fromUtc)
    {
        if (schedule is null) return null;
        
        int maxAttempts = 3;
        int intervalMinutes = 10;
        
        if (schedule.TryGetPropertyValue("retry", out var retryNode) && retryNode is JsonObject retry)
        {
            if (retry.TryGetPropertyValue("maxAttempts", out var maxNode) && maxNode is JsonValue maxVal && maxVal.TryGetValue<int>(out var maxInt))
                maxAttempts = maxInt;
            if (retry.TryGetPropertyValue("intervalMinutes", out var intNode) && intNode is JsonValue intVal && intVal.TryGetValue<int>(out var intInt))
                intervalMinutes = intInt;
        }
        
        if (currentAttempt >= maxAttempts)
        {
            return null; // Max retries reached
        }
        
        return fromUtc.AddMinutes(intervalMinutes);
    }

    /// <summary>
    /// Get retry settings from schedule.
    /// </summary>
    public static (int MaxAttempts, int IntervalMinutes) GetRetrySettings(JsonObject? schedule)
    {
        int maxAttempts = 3;
        int intervalMinutes = 10;
        
        if (schedule?.TryGetPropertyValue("retry", out var retryNode) == true && retryNode is JsonObject retry)
        {
            if (retry.TryGetPropertyValue("maxAttempts", out var maxNode) && maxNode is JsonValue maxVal && maxVal.TryGetValue<int>(out var maxInt))
                maxAttempts = maxInt;
            if (retry.TryGetPropertyValue("intervalMinutes", out var intNode) && intNode is JsonValue intVal && intVal.TryGetValue<int>(out var intInt))
                intervalMinutes = intInt;
        }
        
        return (maxAttempts, intervalMinutes);
    }

    private static DateTimeOffset BuildMonthly(DateTimeOffset source, int day, TimeSpan time)
    {
        var year = source.Year;
        var month = source.Month;
        var maxDay = DateTime.DaysInMonth(year, month);
        var targetDay = Math.Clamp(day, 1, maxDay);
        var date = new DateTime(year, month, targetDay).Add(time);
        return new DateTimeOffset(date, source.Offset);
    }
}

