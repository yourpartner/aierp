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

