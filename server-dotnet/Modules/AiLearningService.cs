using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

/// <summary>
/// AI学习服务 - 支持AI客服自我进化
/// 通过收集反馈、分析失败案例、生成优质样本来持续提高准确率
/// </summary>
public class AiLearningService
{
    private readonly ILogger<AiLearningService> _logger;
    private readonly NpgsqlDataSource _ds;
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public AiLearningService(
        ILogger<AiLearningService> logger,
        NpgsqlDataSource ds,
        IHttpClientFactory httpClientFactory,
        IConfiguration config)
    {
        _logger = logger;
        _ds = ds;
        _httpClient = httpClientFactory.CreateClient("openai");
        _apiKey = config["OpenAI:ApiKey"] ?? "";
    }

    /// <summary>
    /// 记录用户对AI回复的修正
    /// </summary>
    public async Task RecordCorrectionAsync(
        string companyCode,
        Guid sessionId,
        Guid messageId,
        string originalIntent,
        JsonObject? originalEntities,
        string correctedIntent,
        JsonObject? correctedEntities,
        string correctedBy,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO wecom_ai_feedback
            (company_code, session_id, message_id, feedback_type,
             ai_intent, ai_entities, corrected_intent, corrected_entities, corrected_by,
             accuracy_score)
            VALUES ($1, $2, $3, 'correction', $4, $5::jsonb, $6, $7::jsonb, $8, 0)
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(messageId);
        cmd.Parameters.AddWithValue(originalIntent);
        cmd.Parameters.AddWithValue(originalEntities?.ToJsonString() ?? "{}");
        cmd.Parameters.AddWithValue(correctedIntent);
        cmd.Parameters.AddWithValue(correctedEntities?.ToJsonString() ?? "{}");
        cmd.Parameters.AddWithValue(correctedBy);

        await cmd.ExecuteNonQueryAsync(ct);
        
        _logger.LogInformation("[AiLearning] Recorded correction for message {MessageId}: {Original} -> {Corrected}",
            messageId, originalIntent, correctedIntent);
    }

    /// <summary>
    /// 记录用户满意度评分
    /// </summary>
    public async Task RecordUserFeedbackAsync(
        string companyCode,
        Guid sessionId,
        int satisfaction,
        string? note,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO wecom_ai_feedback
            (company_code, session_id, feedback_type, user_satisfaction, note)
            VALUES ($1, $2, 'user_feedback', $3, $4)
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(satisfaction);
        cmd.Parameters.AddWithValue(string.IsNullOrEmpty(note) ? (object)DBNull.Value : note);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 分析失败案例并生成改进建议
    /// </summary>
    public async Task<LearningAnalysisResult> AnalyzeFailedCasesAsync(
        string companyCode,
        int dayRange = 7,
        CancellationToken ct = default)
    {
        var result = new LearningAnalysisResult();

        // 1. 获取失败/修正的案例
        var failedCases = await GetFailedCasesAsync(companyCode, dayRange, ct);
        result.TotalFailedCases = failedCases.Count;

        if (failedCases.Count == 0)
        {
            result.Summary = "过去 {dayRange} 天没有失败或修正的案例，AI表现良好。";
            return result;
        }

        // 2. 统计失败类型
        var intentMistakes = failedCases
            .Where(c => c.OriginalIntent != c.CorrectedIntent)
            .GroupBy(c => $"{c.OriginalIntent} -> {c.CorrectedIntent}")
            .Select(g => new { Pattern = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(5)
            .ToList();

        foreach (var mistake in intentMistakes)
        {
            result.CommonMistakes.Add($"意图识别错误: {mistake.Pattern} ({mistake.Count}次)");
        }

        // 3. 使用AI分析失败模式
        var analysis = await AnalyzeWithAiAsync(failedCases, ct);
        result.AiSuggestions = analysis.Suggestions;
        result.ProposedSamples = analysis.ProposedSamples;

        // 4. 计算准确率
        var totalCases = await GetTotalCasesAsync(companyCode, dayRange, ct);
        result.AccuracyRate = totalCases > 0 
            ? Math.Round((decimal)(totalCases - failedCases.Count) / totalCases * 100, 2) 
            : 100;

        result.Summary = $"过去 {dayRange} 天处理了 {totalCases} 条消息，" +
                        $"失败/修正 {failedCases.Count} 条，准确率 {result.AccuracyRate}%。";

        return result;
    }

    /// <summary>
    /// 从成功的对话中自动生成训练样本
    /// </summary>
    public async Task<int> GenerateTrainingSamplesAsync(
        string companyCode,
        int dayRange = 30,
        int maxSamples = 50,
        CancellationToken ct = default)
    {
        // 1. 获取成功创建订单的会话
        var successfulSessions = await GetSuccessfulSessionsAsync(companyCode, dayRange, ct);
        
        var generatedCount = 0;

        foreach (var session in successfulSessions.Take(maxSamples))
        {
            // 2. 获取会话的消息
            var messages = await GetSessionMessagesAsync(session.Id, ct);
            if (messages.Count == 0) continue;

            // 3. 找到关键的用户输入（导致订单创建的消息）
            var keyMessages = messages
                .Where(m => m.Direction == "in" && !string.IsNullOrEmpty(m.Content))
                .ToList();

            foreach (var msg in keyMessages)
            {
                // 检查是否已有类似的样本
                if (await HasSimilarSampleAsync(companyCode, msg.Content!, ct))
                    continue;

                // 4. 创建样本
                var sample = new TrainingSampleCreate
                {
                    CompanyCode = companyCode,
                    SampleType = DetermineSampleType(msg, session),
                    InputText = msg.Content!,
                    ExpectedIntent = session.Intent ?? "create_order",
                    ExpectedEntities = msg.AiAnalysis,
                    Source = "auto_generated",
                    SourceSessionId = session.Id
                };

                if (await SaveTrainingSampleAsync(sample, ct))
                {
                    generatedCount++;
                }
            }
        }

        _logger.LogInformation("[AiLearning] Generated {Count} training samples from successful sessions",
            generatedCount);

        return generatedCount;
    }

    /// <summary>
    /// 添加商品别名（从用户输入中学习）
    /// </summary>
    public async Task LearnProductAliasAsync(
        string companyCode,
        string userInput,
        string materialCode,
        string materialName,
        string? customerCode,
        CancellationToken ct)
    {
        // 清理用户输入
        var alias = CleanProductAlias(userInput);
        if (string.IsNullOrEmpty(alias) || alias == materialName)
            return;

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // 检查是否已存在
        await using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = """
                SELECT 1 FROM product_aliases
                WHERE company_code = $1 AND material_code = $2 AND alias = $3
                LIMIT 1
                """;
            checkCmd.Parameters.AddWithValue(companyCode);
            checkCmd.Parameters.AddWithValue(materialCode);
            checkCmd.Parameters.AddWithValue(alias);

            var exists = await checkCmd.ExecuteScalarAsync(ct);
            if (exists != null) return;
        }

        // 插入新别名
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO product_aliases
            (company_code, material_code, material_name, alias, alias_type, customer_code, match_count)
            VALUES ($1, $2, $3, $4, $5, $6, 1)
            ON CONFLICT DO NOTHING
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(materialCode);
        cmd.Parameters.AddWithValue(materialName);
        cmd.Parameters.AddWithValue(alias);
        cmd.Parameters.AddWithValue(string.IsNullOrEmpty(customerCode) ? "common" : "customer_specific");
        cmd.Parameters.AddWithValue(string.IsNullOrEmpty(customerCode) ? (object)DBNull.Value : customerCode);

        await cmd.ExecuteNonQueryAsync(ct);
        
        _logger.LogInformation("[AiLearning] Learned product alias: '{Alias}' -> {MaterialCode} ({MaterialName})",
            alias, materialCode, materialName);
    }

    /// <summary>
    /// 增加别名匹配计数
    /// </summary>
    public async Task IncrementAliasMatchCountAsync(
        string companyCode,
        string alias,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE product_aliases
            SET match_count = match_count + 1
            WHERE company_code = $1 AND alias = $2
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(alias);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 获取学习统计信息
    /// </summary>
    public async Task<LearningStatistics> GetStatisticsAsync(
        string companyCode,
        CancellationToken ct)
    {
        var stats = new LearningStatistics();

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // 训练样本统计
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT sample_type, COUNT(*)
                FROM wecom_ai_training_samples
                WHERE company_code = $1 AND is_active = true
                GROUP BY sample_type
                """;
            cmd.Parameters.AddWithValue(companyCode);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                stats.SamplesByType[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        // 反馈统计
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT feedback_type, COUNT(*),
                       AVG(CASE WHEN accuracy_score IS NOT NULL THEN accuracy_score ELSE NULL END)
                FROM wecom_ai_feedback
                WHERE company_code = $1 AND created_at > now() - interval '30 days'
                GROUP BY feedback_type
                """;
            cmd.Parameters.AddWithValue(companyCode);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                stats.FeedbackByType[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        // 别名统计
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*), SUM(match_count)
                FROM product_aliases
                WHERE company_code = $1 AND is_active = true
                """;
            cmd.Parameters.AddWithValue(companyCode);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                stats.TotalAliases = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                stats.TotalAliasMatches = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            }
        }

        // 最近7天准确率
        var analysis = await AnalyzeFailedCasesAsync(companyCode, 7, ct);
        stats.RecentAccuracyRate = analysis.AccuracyRate;

        return stats;
    }

    /// <summary>
    /// 手动添加训练样本
    /// </summary>
    public async Task<bool> AddTrainingSampleAsync(
        string companyCode,
        string sampleType,
        string inputText,
        string expectedIntent,
        JsonObject? expectedEntities,
        string? expectedResponse,
        CancellationToken ct)
    {
        var sample = new TrainingSampleCreate
        {
            CompanyCode = companyCode,
            SampleType = sampleType,
            InputText = inputText,
            ExpectedIntent = expectedIntent,
            ExpectedEntities = expectedEntities?.ToJsonString(),
            ExpectedResponse = expectedResponse,
            Source = "manual"
        };

        return await SaveTrainingSampleAsync(sample, ct);
    }

    #region 私有方法

    private async Task<List<FailedCase>> GetFailedCasesAsync(string companyCode, int dayRange, CancellationToken ct)
    {
        var cases = new List<FailedCase>();

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.id, f.session_id, f.user_input, f.ai_output, f.ai_intent, f.ai_entities,
                   f.corrected_intent, f.corrected_entities, f.feedback_type, f.created_at
            FROM wecom_ai_feedback f
            WHERE f.company_code = $1 
              AND f.feedback_type IN ('failure', 'correction')
              AND f.created_at > now() - ($2::text || ' days')::interval
            ORDER BY f.created_at DESC
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(dayRange.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            cases.Add(new FailedCase
            {
                Id = reader.GetGuid(0),
                SessionId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
                UserInput = reader.IsDBNull(2) ? null : reader.GetString(2),
                AiOutput = reader.IsDBNull(3) ? null : reader.GetString(3),
                OriginalIntent = reader.IsDBNull(4) ? "" : reader.GetString(4),
                OriginalEntities = reader.IsDBNull(5) ? null : reader.GetString(5),
                CorrectedIntent = reader.IsDBNull(6) ? null : reader.GetString(6),
                CorrectedEntities = reader.IsDBNull(7) ? null : reader.GetString(7),
                FeedbackType = reader.GetString(8),
                CreatedAt = reader.GetDateTime(9)
            });
        }

        return cases;
    }

    private async Task<int> GetTotalCasesAsync(string companyCode, int dayRange, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM wecom_chat_messages
            WHERE company_code = $1 
              AND direction = 'in'
              AND created_at > now() - ($2::text || ' days')::interval
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(dayRange.ToString());

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result ?? 0);
    }

    private async Task<AiAnalysisResult> AnalyzeWithAiAsync(List<FailedCase> cases, CancellationToken ct)
    {
        var result = new AiAnalysisResult();

        if (cases.Count == 0) return result;

        // 构建分析请求
        var sb = new StringBuilder();
        sb.AppendLine("请分析以下AI客服的失败案例，找出模式并提供改进建议：");
        sb.AppendLine();

        foreach (var c in cases.Take(20)) // 最多分析20个
        {
            sb.AppendLine($"输入: {c.UserInput}");
            sb.AppendLine($"AI识别意图: {c.OriginalIntent}");
            if (!string.IsNullOrEmpty(c.CorrectedIntent))
            {
                sb.AppendLine($"正确意图: {c.CorrectedIntent}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("请提供：");
        sb.AppendLine("1. 常见失败模式分析");
        sb.AppendLine("2. 改进建议（以JSON格式返回）");
        sb.AppendLine("3. 建议添加的训练样本");

        var messages = new List<object>
        {
            new { role = "system", content = "你是一个AI系统优化专家，帮助分析和改进AI客服系统。" },
            new { role = "user", content = sb.ToString() }
        };

        var response = await OpenAiApiHelper.CallOpenAiAsync(
            _httpClient,
            _apiKey,
            "gpt-4o",
            messages,
            0.2,
            2048,
            false,
            ct);

        if (!string.IsNullOrEmpty(response.Content))
        {
            // 解析AI的建议（简单处理）
            result.Suggestions = response.Content.Split('\n')
                .Where(l => l.Trim().StartsWith("-") || l.Trim().StartsWith("•"))
                .Select(l => l.Trim().TrimStart('-', '•', ' '))
                .ToList();
        }

        return result;
    }

    private async Task<List<SessionSummary>> GetSuccessfulSessionsAsync(string companyCode, int dayRange, CancellationToken ct)
    {
        var sessions = new List<SessionSummary>();

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, user_id, intent, sales_order_no, pending_order_data, created_at
            FROM wecom_chat_sessions
            WHERE company_code = $1 
              AND status = 'completed'
              AND sales_order_id IS NOT NULL
              AND created_at > now() - ($2::text || ' days')::interval
            ORDER BY created_at DESC
            LIMIT 100
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(dayRange.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            sessions.Add(new SessionSummary
            {
                Id = reader.GetGuid(0),
                UserId = reader.GetString(1),
                Intent = reader.IsDBNull(2) ? null : reader.GetString(2),
                SalesOrderNo = reader.IsDBNull(3) ? null : reader.GetString(3),
                OrderData = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt = reader.GetDateTime(5)
            });
        }

        return sessions;
    }

    private async Task<List<MessageSummary>> GetSessionMessagesAsync(Guid sessionId, CancellationToken ct)
    {
        var messages = new List<MessageSummary>();

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, direction, content, ai_analysis
            FROM wecom_chat_messages
            WHERE session_id = $1
            ORDER BY created_at
            """;
        cmd.Parameters.AddWithValue(sessionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            messages.Add(new MessageSummary
            {
                Id = reader.GetGuid(0),
                Direction = reader.GetString(1),
                Content = reader.IsDBNull(2) ? null : reader.GetString(2),
                AiAnalysis = reader.IsDBNull(3) ? null : reader.GetString(3)
            });
        }

        return messages;
    }

    private async Task<bool> HasSimilarSampleAsync(string companyCode, string inputText, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM wecom_ai_training_samples
            WHERE company_code = $1 
              AND (input_text = $2 OR input_text ILIKE '%' || $2 || '%')
              AND is_active = true
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(inputText);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result != null;
    }

    private async Task<bool> SaveTrainingSampleAsync(TrainingSampleCreate sample, CancellationToken ct)
    {
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO wecom_ai_training_samples
                (company_code, sample_type, input_text, expected_intent, expected_entities,
                 expected_response, source, source_session_id, quality_score)
                VALUES ($1, $2, $3, $4, $5::jsonb, $6, $7, $8, 0.8)
                ON CONFLICT DO NOTHING
                """;
            cmd.Parameters.AddWithValue(sample.CompanyCode);
            cmd.Parameters.AddWithValue(sample.SampleType);
            cmd.Parameters.AddWithValue(sample.InputText);
            cmd.Parameters.AddWithValue(sample.ExpectedIntent ?? "");
            cmd.Parameters.AddWithValue(sample.ExpectedEntities ?? "{}");
            cmd.Parameters.AddWithValue(string.IsNullOrEmpty(sample.ExpectedResponse) ? (object)DBNull.Value : sample.ExpectedResponse);
            cmd.Parameters.AddWithValue(sample.Source);
            cmd.Parameters.AddWithValue(sample.SourceSessionId.HasValue ? (object)sample.SourceSessionId.Value : DBNull.Value);

            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AiLearning] Failed to save training sample");
            return false;
        }
    }

    private static string DetermineSampleType(MessageSummary msg, SessionSummary session)
    {
        if (!string.IsNullOrEmpty(msg.AiAnalysis))
        {
            try
            {
                var analysis = JsonDocument.Parse(msg.AiAnalysis);
                var isComplete = analysis.RootElement.TryGetProperty("isComplete", out var ic) && ic.GetBoolean();
                return isComplete ? "order_complete" : "order_partial";
            }
            catch
            {
                // ignore
            }
        }

        return session.Intent switch
        {
            "create_order" => "order_complete",
            "inquiry_delivery" => "inquiry",
            "greeting" => "greeting",
            _ => "other"
        };
    }

    private static string CleanProductAlias(string input)
    {
        // 移除数量信息，只保留商品名称
        var cleaned = input.Trim();
        
        // 移除常见的数量模式
        var patterns = new[] { 
            @"\d+\s*箱", @"\d+\s*瓶", @"\d+\s*件", @"\d+\s*个", @"\d+\s*袋",
            @"\d+\s*盒", @"\d+\s*包", @"\d+\s*罐", @"\d+\s*桶"
        };
        
        foreach (var pattern in patterns)
        {
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, pattern, "").Trim();
        }

        return cleaned;
    }

    #endregion
}

/// <summary>
/// AI学习后台服务 - 定期执行学习任务
/// </summary>
public class AiLearningBackgroundService : BackgroundService
{
    private readonly ILogger<AiLearningBackgroundService> _logger;
    private readonly IServiceProvider _services;
    private readonly TimeSpan _interval = TimeSpan.FromHours(6); // 每6小时执行一次

    public AiLearningBackgroundService(
        ILogger<AiLearningBackgroundService> logger,
        IServiceProvider services)
    {
        _logger = logger;
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[AiLearning] Background service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, stoppingToken);
                
                using var scope = _services.CreateScope();
                var learningService = scope.ServiceProvider.GetRequiredService<AiLearningService>();
                
                // 为每个公司执行学习任务
                // TODO: 从配置或数据库获取公司列表
                var companies = new[] { "JP01" };
                
                foreach (var company in companies)
                {
                    try
                    {
                        // 1. 分析失败案例
                        var analysis = await learningService.AnalyzeFailedCasesAsync(company, 7, stoppingToken);
                        _logger.LogInformation(
                            "[AiLearning] Company {Company}: {Summary}",
                            company, analysis.Summary);

                        // 2. 从成功会话生成训练样本
                        var samplesGenerated = await learningService.GenerateTrainingSamplesAsync(
                            company, 30, 20, stoppingToken);
                        
                        if (samplesGenerated > 0)
                        {
                            _logger.LogInformation(
                                "[AiLearning] Company {Company}: Generated {Count} new training samples",
                                company, samplesGenerated);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[AiLearning] Error processing company {Company}", company);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AiLearning] Background service error");
            }
        }

        _logger.LogInformation("[AiLearning] Background service stopped");
    }
}

#region 数据模型

public class LearningAnalysisResult
{
    public int TotalFailedCases { get; set; }
    public decimal AccuracyRate { get; set; }
    public string Summary { get; set; } = "";
    public List<string> CommonMistakes { get; set; } = new();
    public List<string> AiSuggestions { get; set; } = new();
    public List<ProposedSample> ProposedSamples { get; set; } = new();
}

public class ProposedSample
{
    public string InputText { get; set; } = "";
    public string ExpectedIntent { get; set; } = "";
    public string? ExpectedResponse { get; set; }
}

public class LearningStatistics
{
    public Dictionary<string, int> SamplesByType { get; set; } = new();
    public Dictionary<string, int> FeedbackByType { get; set; } = new();
    public int TotalAliases { get; set; }
    public long TotalAliasMatches { get; set; }
    public decimal RecentAccuracyRate { get; set; }
}

public class FailedCase
{
    public Guid Id { get; set; }
    public Guid? SessionId { get; set; }
    public string? UserInput { get; set; }
    public string? AiOutput { get; set; }
    public string OriginalIntent { get; set; } = "";
    public string? OriginalEntities { get; set; }
    public string? CorrectedIntent { get; set; }
    public string? CorrectedEntities { get; set; }
    public string FeedbackType { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class AiAnalysisResult
{
    public List<string> Suggestions { get; set; } = new();
    public List<ProposedSample> ProposedSamples { get; set; } = new();
}

public class SessionSummary
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string? Intent { get; set; }
    public string? SalesOrderNo { get; set; }
    public string? OrderData { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class MessageSummary
{
    public Guid Id { get; set; }
    public string Direction { get; set; } = "";
    public string? Content { get; set; }
    public string? AiAnalysis { get; set; }
}

public class TrainingSampleCreate
{
    public string CompanyCode { get; set; } = "";
    public string SampleType { get; set; } = "";
    public string InputText { get; set; } = "";
    public string? ExpectedIntent { get; set; }
    public string? ExpectedEntities { get; set; }
    public string? ExpectedResponse { get; set; }
    public string Source { get; set; } = "manual";
    public Guid? SourceSessionId { get; set; }
}

#endregion

