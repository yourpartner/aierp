using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Server.Modules.AgentKit;

/// <summary>
/// Agent 结果消息
/// </summary>
public sealed record AgentResultMessage(string Role, string Content, string? Status, object? Tag);

/// <summary>
/// 工具执行结果
/// </summary>
public sealed record ToolExecutionResult(string ContentForModel, IReadOnlyList<AgentResultMessage> Messages, bool ShouldBreakLoop = false)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static ToolExecutionResult FromModel(object model, IReadOnlyList<AgentResultMessage>? messages = null, bool shouldBreakLoop = false)
    {
        var content = JsonSerializer.Serialize(model, JsonOptions);
        return new ToolExecutionResult(content, messages ?? Array.Empty<AgentResultMessage>(), shouldBreakLoop);
    }
}

/// <summary>
/// 科目查询结果
/// </summary>
public sealed record LookupAccountResult(
    bool Found, 
    string Query, 
    string? AccountCode, 
    string? AccountName, 
    IReadOnlyList<string> Aliases);

/// <summary>
/// 客户查询结果
/// </summary>
public sealed record LookupCustomerResult(
    bool Found,
    string Query,
    string? CustomerCode,
    string? CustomerName,
    object? Payload);

/// <summary>
/// 物料查询结果
/// </summary>
public sealed record LookupMaterialResult(
    bool Found,
    string Query,
    string? MaterialCode,
    string? MaterialName,
    decimal? UnitPrice,
    string? Uom);

/// <summary>
/// 供应商查询结果
/// </summary>
public sealed record LookupVendorResult(
    bool Found,
    string Query,
    string? VendorCode,
    string? VendorName,
    object? Payload);


/// <summary>
/// 工具执行结果
/// </summary>
public sealed class ToolExecutionResult
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string Content { get; }
    public IReadOnlyList<AgentResultMessage> Messages { get; }

    public ToolExecutionResult(string content, IReadOnlyList<AgentResultMessage>? messages = null)
    {
        Content = content;
        Messages = messages ?? Array.Empty<AgentResultMessage>();
    }

    public static ToolExecutionResult FromModel(object model, IReadOnlyList<AgentResultMessage>? messages = null)
    {
        var content = JsonSerializer.Serialize(model, JsonOptions);
        return new ToolExecutionResult(content, messages);
    }
}

/// <summary>
/// 科目查询结果
/// </summary>
public sealed record LookupAccountResult(
    bool Found, 
    string Query, 
    string? AccountCode, 
    string? AccountName, 
    IReadOnlyList<string> Aliases);

/// <summary>
/// 客户查询结果
/// </summary>
public sealed record LookupCustomerResult(
    bool Found,
    string Query,
    string? CustomerCode,
    string? CustomerName,
    object? Payload);

/// <summary>
/// 物料查询结果
/// </summary>
public sealed record LookupMaterialResult(
    bool Found,
    string Query,
    string? MaterialCode,
    string? MaterialName,
    decimal? UnitPrice,
    string? Uom);

/// <summary>
/// 供应商查询结果
/// </summary>
public sealed record LookupVendorResult(
    bool Found,
    string Query,
    string? VendorCode,
    string? VendorName,
    object? Payload);
