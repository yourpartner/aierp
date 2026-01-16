using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Server.Infrastructure;
using Server.Modules;

namespace Server.Modules.AgentKit;

/// <summary>
/// Agent 执行上下文 - 管理执行过程中的状态
/// </summary>
public sealed class AgentExecutionContext
{
    private readonly Func<string, UploadedFileRecord?> _fileResolver;
    private readonly Dictionary<string, JsonObject> _parsedDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _documentSessionByFileId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _fileIdsByDocumentSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _documentSessionLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownFileIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _approvedAccounts = new(StringComparer.OrdinalIgnoreCase);
    private Guid? _taskId;
    private string? _pendingFieldName;
    private string? _pendingFieldValue;
    private string? _defaultFileId;
    private string? _activeDocumentSessionId;
    private bool _voucherCreated;
    private readonly List<string> _createdVoucherNos = new();
    private bool _enforceAccountWhitelist;
    private bool _lookupAccountCalled;

    public AgentExecutionContext(
        Guid sessionId,
        string companyCode,
        Auth.UserCtx userCtx,
        string apiKey,
        string language,
        IReadOnlyList<AgentScenarioService.AgentScenario> scenarios,
        Func<string, UploadedFileRecord?> fileResolver,
        Guid? taskId = null)
    {
        SessionId = sessionId;
        CompanyCode = companyCode;
        UserCtx = userCtx;
        ApiKey = apiKey;
        Language = NormalizeLanguage(language);
        Scenarios = scenarios;
        _fileResolver = fileResolver;
        _taskId = taskId;
    }

    public string? PendingFieldName => _pendingFieldName;
    public string? PendingFieldValue => _pendingFieldValue;

    public void SetPendingFieldAnswer(string? fieldName, string? value)
    {
        if (string.IsNullOrWhiteSpace(fieldName) || string.IsNullOrWhiteSpace(value)) return;
        _pendingFieldName = fieldName.Trim();
        _pendingFieldValue = value.Trim();
    }

    public Guid SessionId { get; }
    public string CompanyCode { get; }
    public Auth.UserCtx UserCtx { get; }
    public string ApiKey { get; }
    public string Language { get; }
    public IReadOnlyList<AgentScenarioService.AgentScenario> Scenarios { get; }

    public string? DefaultFileId => _defaultFileId;
    public string? ActiveDocumentSessionId => _activeDocumentSessionId;
    public bool HasVoucherCreated => _voucherCreated;
    public IReadOnlyList<string> CreatedVoucherNos => _createdVoucherNos;
    public IReadOnlyDictionary<string, string> DocumentSessionLabels => _documentSessionLabels;
    public Guid? TaskId => _taskId;
    public bool ShouldEnforceAccountWhitelist => _enforceAccountWhitelist;
    public IReadOnlyCollection<string> ApprovedAccounts => _approvedAccounts;
    public bool HasLookupAccountInvocation => _lookupAccountCalled;

    public UploadedFileRecord? ResolveFile(string fileId) => _fileResolver(fileId);

    public void EnableAccountWhitelist(IEnumerable<string> accountCodes)
    {
        _enforceAccountWhitelist = true;
        if (accountCodes is null) return;
        foreach (var code in accountCodes)
        {
            RegisterApprovedAccount(code, false);
        }
    }

    public void RegisterApprovedAccount(string? accountCode, bool markLookupUsed = false)
    {
        if (string.IsNullOrWhiteSpace(accountCode)) return;
        _approvedAccounts.Add(accountCode.Trim());
        if (markLookupUsed)
        {
            _lookupAccountCalled = true;
        }
    }

    public void RegisterLookupAccountResult(string? accountCode) => RegisterApprovedAccount(accountCode, true);

    public bool IsAccountAllowed(string? accountCode)
    {
        if (!_enforceAccountWhitelist) return true;
        if (string.IsNullOrWhiteSpace(accountCode)) return false;
        return _approvedAccounts.Contains(accountCode.Trim());
    }

    public void RegisterDocument(string? fileId, JsonObject? parsedData, string? documentSessionId = null)
    {
        if (string.IsNullOrWhiteSpace(fileId)) return;
        if (parsedData is not null)
        {
            _parsedDocuments[fileId] = parsedData;
        }
        _knownFileIds.Add(fileId);
        if (string.IsNullOrWhiteSpace(documentSessionId))
        {
            if (_documentSessionByFileId.TryGetValue(fileId, out var existing))
            {
                documentSessionId = existing;
            }
            else if (!string.IsNullOrWhiteSpace(_activeDocumentSessionId))
            {
                documentSessionId = _activeDocumentSessionId;
            }
        }
        if (!string.IsNullOrWhiteSpace(documentSessionId))
        {
            _documentSessionByFileId[fileId] = documentSessionId;
            if (!_fileIdsByDocumentSession.TryGetValue(documentSessionId, out var list))
            {
                list = new List<string>();
                _fileIdsByDocumentSession[documentSessionId] = list;
            }
            if (!list.Any(existing => string.Equals(existing, fileId, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(fileId);
            }
        }
    }

    public void AssignDocumentLabel(string? documentSessionId, string? label)
    {
        if (string.IsNullOrWhiteSpace(documentSessionId)) return;
        if (string.IsNullOrWhiteSpace(label)) return;
        _documentSessionLabels[documentSessionId] = label;
    }

    public string? GetDocumentSessionLabel(string? documentSessionId)
    {
        if (string.IsNullOrWhiteSpace(documentSessionId)) return null;
        return _documentSessionLabels.TryGetValue(documentSessionId, out var label) ? label : null;
    }

    public bool TryGetDocument(string fileId, out JsonObject? parsedData)
    {
        return _parsedDocuments.TryGetValue(fileId, out parsedData);
    }

    public void SetDefaultFileId(string? fileId)
    {
        _defaultFileId = string.IsNullOrWhiteSpace(fileId) ? null : fileId;
        if (_defaultFileId is not null && _documentSessionByFileId.TryGetValue(_defaultFileId, out var docSession))
        {
            _activeDocumentSessionId = docSession;
        }
    }

    public void ClearDefaultFileId()
    {
        _defaultFileId = null;
        _activeDocumentSessionId = null;
    }

    public void SetActiveDocumentSession(string? documentSessionId)
    {
        _activeDocumentSessionId = string.IsNullOrWhiteSpace(documentSessionId) ? null : documentSessionId;
        if (_activeDocumentSessionId is not null &&
            _fileIdsByDocumentSession.TryGetValue(_activeDocumentSessionId, out var files) &&
            files.Count > 0)
        {
            _defaultFileId = files[0];
        }
    }

    public string? GetDocumentSessionIdByFileId(string fileId)
    {
        return _documentSessionByFileId.TryGetValue(fileId, out var sessionId) ? sessionId : null;
    }

    public IReadOnlyList<string> GetFileIdsByDocumentSession(string documentSessionId)
    {
        if (_fileIdsByDocumentSession.TryGetValue(documentSessionId, out var files))
        {
            return files;
        }
        return Array.Empty<string>();
    }

    public IReadOnlyCollection<string> GetRegisteredFileIds()
    {
        return _knownFileIds;
    }

    public bool TryResolveAttachmentToken(string? token, out string resolvedFileId)
    {
        resolvedFileId = string.Empty;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var normalized = token.Trim();
        if (normalized.StartsWith("[", StringComparison.Ordinal) && normalized.EndsWith("]", StringComparison.Ordinal) && normalized.Length > 2)
        {
            normalized = normalized.Substring(1, normalized.Length - 2).Trim();
        }
        if (normalized.StartsWith("#", StringComparison.Ordinal))
        {
            var idxText = normalized.Substring(1).Trim();
            if (int.TryParse(idxText, out var idx) && idx > 0)
            {
                var sessionId = _activeDocumentSessionId;
                if (!string.IsNullOrWhiteSpace(sessionId) &&
                    _fileIdsByDocumentSession.TryGetValue(sessionId, out var files) &&
                    idx <= files.Count)
                {
                    resolvedFileId = files[idx - 1];
                    return true;
                }
            }
        }
        if (int.TryParse(normalized, out var idx2) && idx2 > 0)
        {
            var sessionId = _activeDocumentSessionId;
            if (!string.IsNullOrWhiteSpace(sessionId) &&
                _fileIdsByDocumentSession.TryGetValue(sessionId, out var files) &&
                idx2 <= files.Count)
            {
                resolvedFileId = files[idx2 - 1];
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(_activeDocumentSessionId) &&
            _activeDocumentSessionId.StartsWith("doc_", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = _activeDocumentSessionId.Substring(4);
            if (string.Equals(suffix, normalized, StringComparison.OrdinalIgnoreCase))
            {
                if (_fileIdsByDocumentSession.TryGetValue(_activeDocumentSessionId, out var files) && files.Count > 0)
                {
                    resolvedFileId = files[0];
                    return true;
                }
            }
        }
        if (_knownFileIds.Contains(normalized))
        {
            resolvedFileId = normalized;
            return true;
        }
        if (_documentSessionByFileId.ContainsKey(normalized))
        {
            resolvedFileId = normalized;
            return true;
        }
        if (_fileIdsByDocumentSession.TryGetValue(normalized, out var sessionFiles) && sessionFiles.Count > 0)
        {
            resolvedFileId = sessionFiles[0];
            return true;
        }
        foreach (var kvp in _documentSessionLabels)
        {
            if (string.Equals(kvp.Value, normalized, StringComparison.OrdinalIgnoreCase))
            {
                if (_fileIdsByDocumentSession.TryGetValue(kvp.Key, out var labelFiles) && labelFiles.Count > 0)
                {
                    resolvedFileId = labelFiles[0];
                    return true;
                }
            }
        }
        if (normalized.StartsWith("#", StringComparison.Ordinal))
        {
            foreach (var kvp in _documentSessionLabels)
            {
                if (string.Equals(kvp.Value, normalized, StringComparison.OrdinalIgnoreCase) &&
                    _fileIdsByDocumentSession.TryGetValue(kvp.Key, out var labelFiles) &&
                    labelFiles.Count > 0)
                {
                    resolvedFileId = labelFiles[0];
                    return true;
                }
            }
        }
        return false;
    }

    public void MarkVoucherCreated(string? voucherNo)
    {
        _voucherCreated = true;
        if (!string.IsNullOrWhiteSpace(voucherNo))
        {
            _createdVoucherNos.Add(voucherNo);
        }
    }

    public void SetTaskId(Guid? taskId)
    {
        _taskId = taskId;
    }

    private static string NormalizeLanguage(string? language)
    {
        if (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase)) return "zh";
        return "ja";
    }
}
 

