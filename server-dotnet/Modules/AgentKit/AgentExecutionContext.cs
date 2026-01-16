using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Server.Infrastructure;

namespace Server.Modules.AgentKit;

/// <summary>
/// 执行上下文（轻量版）：满足工具编译与基本运行需求
/// </summary>
public sealed class AgentExecutionContext
{
    private readonly Func<string, UploadedFileRecord?>? _fileResolver;
    private readonly Dictionary<string, JsonObject> _parsedDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _approvedAccounts = new(StringComparer.OrdinalIgnoreCase);

    public AgentExecutionContext(
        Guid sessionId,
        string companyCode,
        Auth.UserCtx userCtx,
        string apiKey,
        string language,
        Func<string, UploadedFileRecord?>? fileResolver = null)
    {
        SessionId = sessionId;
        CompanyCode = companyCode;
        UserCtx = userCtx;
        ApiKey = apiKey;
        Language = string.IsNullOrWhiteSpace(language) ? "zh" : language.Trim();
        _fileResolver = fileResolver;
    }

    public Guid SessionId { get; }
    public string CompanyCode { get; }
    public Auth.UserCtx UserCtx { get; }
    public string ApiKey { get; }
    public string Language { get; }

    public string? DefaultFileId { get; private set; }
    public string? ActiveDocumentSessionId { get; private set; }

    public UploadedFileRecord? ResolveFile(string fileId)
    {
        if (string.IsNullOrWhiteSpace(fileId) || _fileResolver is null) return null;
        return _fileResolver(fileId);
    }

    public bool TryGetDocument(string fileId, out JsonObject? data)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            data = null;
            return false;
        }
        return _parsedDocuments.TryGetValue(fileId, out data);
    }

    public void RegisterDocument(string? fileId, JsonObject? parsedData)
    {
        if (string.IsNullOrWhiteSpace(fileId) || parsedData is null) return;
        _parsedDocuments[fileId] = parsedData;
    }

    public bool TryResolveAttachmentToken(string? token, out string? fileId)
    {
        fileId = string.IsNullOrWhiteSpace(token) ? null : token;
        return !string.IsNullOrWhiteSpace(fileId);
    }

    public void RegisterLookupAccountResult(string? accountCode)
    {
        if (string.IsNullOrWhiteSpace(accountCode)) return;
        _approvedAccounts.Add(accountCode.Trim());
    }

    public void SetDefaultFileId(string? fileId)
    {
        DefaultFileId = string.IsNullOrWhiteSpace(fileId) ? null : fileId.Trim();
    }

    public void SetActiveDocumentSessionId(string? documentSessionId)
    {
        ActiveDocumentSessionId = string.IsNullOrWhiteSpace(documentSessionId) ? null : documentSessionId.Trim();
    }
}

