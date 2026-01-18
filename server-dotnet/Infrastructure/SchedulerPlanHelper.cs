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

        // Moneytree bank sync
        // Example NL:
        // - "每天8点和18点同步银行明细"
        // - "毎日9時に銀行明細を同期"
        // - "一日两次同步moneytree"
        if (plan is null && (lowered.Contains("moneytree") || lowered.Contains("银行明细") || lowered.Contains("銀行明細") || lowered.Contains("銀行") || lowered.Contains("银行"))
            && (lowered.Contains("同步") || lowered.Contains("同期") || lowered.Contains("sync") || lowered.Contains("連携") || lowered.Contains("连携")))
        {
            plan = new JsonObject
            {
                ["action"] = "moneytree.sync"
            };

            // 同步天数，默认7天
            int daysBack = 7;
            var daysMatch = Regex.Match(text, "(\\d{1,3})\\s*(?:天|日|days?)", RegexOptions.IgnoreCase);
            if (daysMatch.Success && int.TryParse(daysMatch.Groups[1].Value, out var d) && d > 0 && d <= 90)
            {
                daysBack = d;
            }
            plan["daysBack"] = daysBack;

            // 解析执行时间
            var times = ParseMultipleTimes(text);
            if (times.Count == 0)
            {
                // 默认每天8点和18点执行（日本时间）
                times.Add(new TimeSpan(8, 0, 0));
                times.Add(new TimeSpan(18, 0, 0));
            }

            schedule = new JsonObject
            {
                ["kind"] = "daily",
                ["timezone"] = "Asia/Tokyo"
            };

            // 如果有多个时间，使用 times 数组
            if (times.Count > 1)
            {
                var timesArray = new JsonArray();
                foreach (var t in times.OrderBy(t => t))
                {
                    timesArray.Add(t.ToString("hh\\:mm"));
                }
                schedule["times"] = timesArray;
            }
            else
            {
                schedule["time"] = times[0].ToString("hh\\:mm");
            }
        }

        if (plan is null && (lowered.Contains("工资") || lowered.Contains("薪资") || lowered.Contains("payroll")))
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
        var match = Regex.Match(text, "(?:(上午|下午|晚上|早上|傍晚|pm|AM|PM|am)\\s*)?(\\d{1,2})(?:[:：](\\d{1,2}))?\\s*(点|時|:|：)?", RegexOptions.IgnoreCase);
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
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);
        return new TimeSpan(hour, minute, 0);
    }

    private static List<TimeSpan> ParseMultipleTimes(string text)
    {
        var times = new List<TimeSpan>();
        // Pattern: 8点和18点, 8時と18時, 8:00 and 18:00, etc.
        var pattern = @"(?:(上午|下午|晚上|早上|傍晚|pm|AM|PM|am)\s*)?(\d{1,2})(?:[:：](\d{1,2}))?\s*(点|時|時|:)?";
        foreach (Match match in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
        {
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[2].Value, out var hour)) continue;
            if (hour > 23) continue;
            
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
            case "daily":
                // 支持多个时间点
                var times = GetTimesOfDay(schedule);
                if (times.Count > 0)
                {
                    DateTimeOffset? nextTarget = null;
                    foreach (var t in times.OrderBy(x => x))
                    {
                        var candidate = new DateTimeOffset(referenceLocal.Date.Add(t), referenceLocal.Offset);
                        if (candidate > referenceLocal)
                        {
                            nextTarget = candidate;
                            break;
                        }
                    }
                    // 如果今天所有时间点都过了，取明天的第一个时间点
                    if (!nextTarget.HasValue)
                    {
                        var firstTime = times.OrderBy(x => x).First();
                        nextTarget = new DateTimeOffset(referenceLocal.Date.AddDays(1).Add(firstTime), referenceLocal.Offset);
                    }
                    targetLocal = nextTarget.Value;
                }
                else
                {
                    targetLocal = new DateTimeOffset(referenceLocal.Date.Add(time), referenceLocal.Offset);
                    if (targetLocal <= referenceLocal) targetLocal = targetLocal.AddDays(1);
                }
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
        
        // 检查 times 数组
        if (schedule.TryGetPropertyValue("times", out var timesNode) && timesNode is JsonArray timesArray)
        {
            foreach (var node in timesArray)
            {
                if (node is JsonValue val && val.TryGetValue<string>(out var str))
                {
                    if (TimeSpan.TryParseExact(str, "hh\\:mm", CultureInfo.InvariantCulture, out var ts))
                        result.Add(ts);
                    else if (TimeSpan.TryParse(str, CultureInfo.InvariantCulture, out ts))
                        result.Add(ts);
                }
            }
        }
        
        // 如果没有 times 数组，检查单个 time
        if (result.Count == 0)
        {
            var singleTime = GetTimeOfDay(schedule);
            if (singleTime.HasValue)
            {
                result.Add(singleTime.Value);
            }
        }
        
        return result;
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

