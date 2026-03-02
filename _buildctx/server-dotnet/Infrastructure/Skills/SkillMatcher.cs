using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Server.Modules;

namespace Server.Infrastructure.Skills;

/// <summary>
/// 根据消息内容、文件类型、渠道等条件匹配最佳 Skill
/// </summary>
public sealed class SkillMatcher
{
    private readonly AgentSkillService _skillService;

    public SkillMatcher(AgentSkillService skillService) => _skillService = skillService;

    /// <summary>匹配结果</summary>
    public sealed record MatchResult(
        AgentSkillService.AgentSkillRecord Skill,
        double Score,
        string Reason);

    /// <summary>
    /// 根据消息内容和上下文匹配最佳 Skill
    /// </summary>
    public async Task<MatchResult?> MatchAsync(
        string companyCode,
        string message,
        string? fileContentType,
        string channel,
        CancellationToken ct)
    {
        var skills = await _skillService.ListAsync(companyCode, includeInactive: false, ct);
        if (skills.Count == 0) return null;

        MatchResult? best = null;

        foreach (var skill in skills)
        {
            var score = CalculateScore(skill, message, fileContentType, channel);
            if (score > 0 && (best == null || score > best.Score))
            {
                var reason = score >= 0.8 ? "strong_match" : score >= 0.4 ? "partial_match" : "weak_match";
                best = new MatchResult(skill, score, reason);
            }
        }

        return best;
    }

    /// <summary>
    /// 根据已知的 Skill Key 直接获取有效配置
    /// </summary>
    public async Task<AgentSkillService.AgentSkillRecord?> GetSkillAsync(
        string companyCode,
        string skillKey,
        CancellationToken ct)
    {
        return await _skillService.GetEffectiveSkillAsync(companyCode, skillKey, ct);
    }

    // ====================== Scoring ======================

    private static double CalculateScore(
        AgentSkillService.AgentSkillRecord skill,
        string message,
        string? fileContentType,
        string channel)
    {
        var triggers = skill.Triggers;
        if (triggers == null || triggers.Count == 0) return 0;

        double score = 0;
        var checks = 0;

        // 1. 渠道匹配
        var channels = GetStringArray(triggers, "channels");
        if (channels.Length > 0)
        {
            checks++;
            if (channels.Any(c => string.Equals(c, channel, StringComparison.OrdinalIgnoreCase)))
                score += 0.1;
            else
                return 0; // 渠道不匹配直接排除
        }

        // 2. 文件类型匹配
        var fileTypes = GetStringArray(triggers, "fileTypes");
        if (fileTypes.Length > 0 && !string.IsNullOrWhiteSpace(fileContentType))
        {
            checks++;
            foreach (var pattern in fileTypes)
            {
                if (pattern.EndsWith("/*"))
                {
                    var prefix = pattern[..^2];
                    if (fileContentType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 0.5;
                        break;
                    }
                }
                else if (string.Equals(pattern, fileContentType, StringComparison.OrdinalIgnoreCase))
                {
                    score += 0.5;
                    break;
                }
            }
        }

        // 3. 关键词匹配
        var keywords = GetStringArray(triggers, "keywords");
        if (keywords.Length > 0 && !string.IsNullOrWhiteSpace(message))
        {
            checks++;
            var lowerMsg = message.ToLowerInvariant();
            var matchedKeywords = keywords.Count(k => lowerMsg.Contains(k.ToLowerInvariant()));
            if (matchedKeywords > 0)
                score += 0.3 * Math.Min(matchedKeywords, 3) / 3.0; // 最多 0.3
        }

        // 4. 意图匹配（通配符模式）
        var intents = GetStringArray(triggers, "intents");
        if (intents.Length > 0 && !string.IsNullOrWhiteSpace(message))
        {
            checks++;
            // 意图匹配较复杂，目前用关键词近似
            // 后续可接入 WeComIntentClassifier 做精确分类
            var lowerMsg = message.ToLowerInvariant();
            foreach (var intent in intents)
            {
                var intentBase = intent.Replace(".*", "").Replace("*", "");
                if (lowerMsg.Contains(intentBase.ToLowerInvariant()))
                {
                    score += 0.2;
                    break;
                }
            }
        }

        // 如果没有任何触发条件匹配检查，返回 0
        if (checks == 0) return 0;

        // 优先级加权（priority 越小越优先，100 是默认）
        var priorityBonus = Math.Max(0, (200 - skill.Priority) / 2000.0); // 最多 +0.1
        score += priorityBonus;

        return Math.Min(score, 1.0);
    }

    private static string[] GetStringArray(JsonObject? obj, string key)
    {
        if (obj == null) return Array.Empty<string>();
        if (!obj.TryGetPropertyValue(key, out var node)) return Array.Empty<string>();
        if (node is JsonArray arr)
            return arr.Select(n => n?.ToString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        return Array.Empty<string>();
    }
}
