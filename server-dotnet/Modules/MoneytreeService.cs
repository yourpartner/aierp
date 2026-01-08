using System.Linq;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

public class MoneytreeService
{
    private readonly NpgsqlDataSource _ds;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MoneytreeOptions _options;
    private readonly ILogger<MoneytreeService> _logger;

    public MoneytreeService(NpgsqlDataSource ds, IHttpClientFactory httpClientFactory, IOptions<MoneytreeOptions> options, ILogger<MoneytreeService> logger)
    {
        _ds = ds;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public string BuildAuthorizeUrl(string state, string codeChallenge)
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new InvalidOperationException("Moneytree ClientId is not configured.");
        if (string.IsNullOrWhiteSpace(_options.RedirectUri))
            throw new InvalidOperationException("Moneytree RedirectUri is not configured.");

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = _options.RedirectUri,
            ["scope"] = _options.Scopes,
            ["state"] = state,
            ["access_type"] = "offline"
        };

        if (_options.UsePkce)
        {
            query["code_challenge"] = codeChallenge;
            query["code_challenge_method"] = "S256";
        }

        var builder = new StringBuilder(_options.AuthUrl);
        builder.Append(_options.AuthUrl.Contains('?') ? '&' : '?');
        builder.Append(string.Join('&', query
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}")));

        return builder.ToString();
    }

    public async Task<JsonObject> ExchangeAuthorizationCodeAsync(string companyCode, string userId, string code, string codeVerifier, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentNullException(nameof(code));

        var payload = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
            ["client_id"] = _options.ClientId
        };
            if (!string.IsNullOrWhiteSpace(_options.ClientSecret))
            payload["client_secret"] = _options.ClientSecret;
        if (_options.UsePkce)
        {
            if (string.IsNullOrWhiteSpace(codeVerifier))
                throw new InvalidOperationException("code_verifier is required when PKCE is enabled.");
            payload["code_verifier"] = codeVerifier;
        }

        var token = await RequestTokenAsync(payload, ct);
        var connectionId = await UpsertConnectionAsync(companyCode, userId, token, ct);
        return new JsonObject
        {
            ["connectionId"] = connectionId.ToString(),
            ["expiresAt"] = token.ExpiresAt.ToString("O"),
            ["scope"] = token.Scope
        };
    }

    public async Task<JsonObject> RefreshTokenAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await GetConnectionAsync(connectionId, ct) ?? throw new InvalidOperationException("Moneytree connection not found");
        if (string.IsNullOrWhiteSpace(connection.RefreshToken))
            throw new InvalidOperationException("Refresh token missing, please re-authorize Moneytree");

        var payload = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = connection.RefreshToken!,
            ["client_id"] = _options.ClientId
        };
            if (!string.IsNullOrWhiteSpace(_options.ClientSecret))
            payload["client_secret"] = _options.ClientSecret;

        var token = await RequestTokenAsync(payload, ct);
        await UpdateConnectionTokensAsync(connectionId, token, ct);
        return new JsonObject
        {
            ["connectionId"] = connectionId.ToString(),
            ["expiresAt"] = token.ExpiresAt.ToString("O"),
            ["scope"] = token.Scope
        };
    }

    public async Task SyncAsync(string companyCode, string? since, CancellationToken ct = default)
    {
        var connection = await GetActiveConnectionAsync(companyCode, ct) ?? throw new InvalidOperationException("Moneytree connection not found for this company");
        if (connection.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            _logger.LogInformation("Refreshing Moneytree token for company {Company}", companyCode);
            await RefreshTokenAsync(connection.Id, ct);
            connection = await GetConnectionAsync(connection.Id, ct) ?? connection;
        }

        using var client = _httpClientFactory.CreateClient("moneytree");
        client.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.AccessToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var accounts = await FetchAccountsAsync(client, ct);
        foreach (var account in accounts)
        {
            var accountUuid = await UpsertAccountAsync(connection.Id, account, ct);
            await FetchAndStoreTransactionsAsync(client, connection.Id, accountUuid, account, since, ct);
        }
    }

    private async Task<List<JsonObject>> FetchAccountsAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync("v2/accounts", ct);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var list = new List<JsonObject>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    list.Add(JsonNode.Parse(item.GetRawText())!.AsObject());
                }
            }
        }
        return list;
    }

    private async Task FetchAndStoreTransactionsAsync(HttpClient client, Guid connectionId, Guid accountUuid, JsonObject account, string? since, CancellationToken ct)
    {
        var accountId = account["id"]?.GetValue<string>() ?? throw new InvalidOperationException("account id missing");
        var urlBuilder = new StringBuilder();
        urlBuilder.Append("v2/accounts/");
        urlBuilder.Append(Uri.EscapeDataString(accountId));
        urlBuilder.Append("/transactions");
        if (!string.IsNullOrWhiteSpace(since))
        {
            urlBuilder.Append("?since=");
            urlBuilder.Append(Uri.EscapeDataString(since));
        }

        string? next = urlBuilder.ToString();
        while (!string.IsNullOrWhiteSpace(next))
        {
            using var response = await client.GetAsync(next, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Moneytree transaction fetch failed ({Status}): {Body}", response.StatusCode, body);
                throw new InvalidOperationException("Failed to fetch Moneytree transactions");
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
            foreach (var item in data.EnumerateArray())
            {
                    await UpsertTransactionAsync(connectionId, accountUuid, item, ct);
                }
            }

            if (doc.RootElement.TryGetProperty("links", out var links) &&
                links.TryGetProperty("next", out var nextEl) &&
                nextEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(nextEl.GetString()))
            {
                next = nextEl.GetString();
            }
            else
            {
                next = null;
            }
        }
    }

    private record TokenResponse(string AccessToken, string? RefreshToken, string TokenType, string Scope, DateTimeOffset ExpiresAt, JsonObject Raw);

    private async Task<TokenResponse> RequestTokenAsync(Dictionary<string, string> payload, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient("moneytree-token");
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl)
        {
            Content = new FormUrlEncodedContent(payload)
        };
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Moneytree token request failed ({Status}): {Body}", response.StatusCode, body);
            throw new InvalidOperationException("Moneytree token request failed");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var accessToken = root.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("access_token missing");
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresIn = 3600;
        if (root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var secondsValue))
        {
            expiresIn = secondsValue;
        }
        var tokenType = root.TryGetProperty("token_type", out var typeEl) ? typeEl.GetString() ?? "Bearer" : "Bearer";
        var scope = root.TryGetProperty("scope", out var scopeEl) ? scopeEl.GetString() ?? _options.Scopes : _options.Scopes;
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn));
        return new TokenResponse(accessToken, refreshToken, tokenType, scope, expiresAt, JsonNode.Parse(body)!.AsObject());
    }

    private async Task<Guid> UpsertConnectionAsync(string companyCode, string userId, TokenResponse token, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO moneytree_connections(company_code, user_id, access_token, refresh_token, token_type, scope, expires_at, metadata)
                            VALUES ($1,$2,$3,$4,$5,$6,$7,$8)
                            ON CONFLICT (company_code)
                            DO UPDATE SET access_token=EXCLUDED.access_token,
                                          refresh_token=EXCLUDED.refresh_token,
                                          token_type=EXCLUDED.token_type,
                                          scope=EXCLUDED.scope,
                                          expires_at=EXCLUDED.expires_at,
                                          metadata=EXCLUDED.metadata,
                                          updated_at=now()
                            RETURNING id";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(token.AccessToken);
        cmd.Parameters.AddWithValue((object?)token.RefreshToken ?? DBNull.Value);
        cmd.Parameters.AddWithValue(token.TokenType);
        cmd.Parameters.AddWithValue(token.Scope);
        cmd.Parameters.AddWithValue(token.ExpiresAt.UtcDateTime);
        cmd.Parameters.AddWithValue(token.Raw.ToJsonString());
        var result = await cmd.ExecuteScalarAsync(ct);
        return (Guid)result!;
    }

    private async Task UpdateConnectionTokensAsync(Guid connectionId, TokenResponse token, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE moneytree_connections
                             SET access_token=$2,
                                 refresh_token=$3,
                                 token_type=$4,
                                 scope=$5,
                                 expires_at=$6,
                                 metadata=$7,
                                 updated_at=now()
                             WHERE id=$1";
        cmd.Parameters.AddWithValue(connectionId);
        cmd.Parameters.AddWithValue(token.AccessToken);
        cmd.Parameters.AddWithValue((object?)token.RefreshToken ?? DBNull.Value);
        cmd.Parameters.AddWithValue(token.TokenType);
        cmd.Parameters.AddWithValue(token.Scope);
        cmd.Parameters.AddWithValue(token.ExpiresAt.UtcDateTime);
        cmd.Parameters.AddWithValue(token.Raw.ToJsonString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<Guid> UpsertAccountAsync(Guid connectionId, JsonObject account, CancellationToken ct)
    {
        var accountId = account["id"]?.GetValue<string>() ?? throw new InvalidOperationException("account id missing");
        var attributes = account["attributes"] as JsonObject;
        var name = attributes?["display_name"]?.GetValue<string>() ?? attributes?["name"]?.GetValue<string>();
        var currency = attributes?["currency_code"]?.GetValue<string>();
        var category = attributes?["account_type"]?.GetValue<string>();

        await using var conn = await _ds.OpenConnectionAsync(ct);
                await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO moneytree_accounts(connection_id, account_id, display_name, currency, category, payload)
                            VALUES ($1,$2,$3,$4,$5,$6)
                            ON CONFLICT (account_id)
                            DO UPDATE SET display_name=EXCLUDED.display_name,
                                      currency=EXCLUDED.currency,
                                          category=EXCLUDED.category,
                                      payload=EXCLUDED.payload,
                                          updated_at=now()
                            RETURNING id";
        cmd.Parameters.AddWithValue(connectionId);
        cmd.Parameters.AddWithValue(accountId);
        cmd.Parameters.AddWithValue((object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)currency ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)category ?? DBNull.Value);
        cmd.Parameters.AddWithValue(account.ToJsonString());
        var result = await cmd.ExecuteScalarAsync(ct);
        return (Guid)result!;
    }

    private async Task UpsertTransactionAsync(Guid connectionId, Guid accountUuid, JsonElement transaction, CancellationToken ct)
    {
        var transactionId = transaction.GetProperty("id").GetString() ?? throw new InvalidOperationException("transaction id missing");
        var attr = transaction.GetProperty("attributes");

        DateTimeOffset? postedAt = null;
        if (attr.TryGetProperty("posted_at", out var postedEl) && postedEl.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(postedEl.GetString(), out var posted))
            postedAt = posted.ToUniversalTime();

        decimal amount = 0m;
        if (attr.TryGetProperty("amount", out var amountEl))
        {
            if (amountEl.ValueKind == JsonValueKind.Number && amountEl.TryGetDecimal(out var dec))
                amount = dec;
            else if (amountEl.ValueKind == JsonValueKind.String && decimal.TryParse(amountEl.GetString(), out var decStr))
                amount = decStr;
        }

        var currency = attr.TryGetProperty("currency_code", out var currEl) ? currEl.GetString() : null;
        var description = attr.TryGetProperty("description", out var descEl) ? descEl.GetString() : null;
        var status = attr.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;

        await using var conn = await _ds.OpenConnectionAsync(ct);
                await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO moneytree_transactions(account_uuid, connection_id, transaction_id, posted_at, amount, currency, description, status, payload)
                            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
                            ON CONFLICT (transaction_id)
                            DO UPDATE SET posted_at=EXCLUDED.posted_at,
                                          amount=EXCLUDED.amount,
                                          currency=EXCLUDED.currency,
                                          description=EXCLUDED.description,
                                          status=EXCLUDED.status,
                                          payload=EXCLUDED.payload,
                                          updated_at=now()";
        cmd.Parameters.AddWithValue(accountUuid);
        cmd.Parameters.AddWithValue(connectionId);
        cmd.Parameters.AddWithValue(transactionId);
        cmd.Parameters.AddWithValue(postedAt?.UtcDateTime ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue(amount);
        cmd.Parameters.AddWithValue((object?)currency ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue(transaction.GetRawText());
                await cmd.ExecuteNonQueryAsync(ct);
            }

    private async Task<MoneytreeConnection?> GetActiveConnectionAsync(string companyCode, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, company_code, user_id, access_token, refresh_token, token_type, scope, expires_at FROM moneytree_connections WHERE company_code=$1";
        cmd.Parameters.AddWithValue(companyCode);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var expiresAt = reader.IsDBNull(7)
                ? DateTimeOffset.UtcNow
                : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc));
            return new MoneytreeConnection(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                expiresAt);
        }
        return null;
    }

    private async Task<MoneytreeConnection?> GetConnectionAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, company_code, user_id, access_token, refresh_token, token_type, scope, expires_at FROM moneytree_connections WHERE id=$1";
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var expiresAt = reader.IsDBNull(7)
                ? DateTimeOffset.UtcNow
                : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc));
            return new MoneytreeConnection(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                expiresAt);
        }
        return null;
    }

    private sealed record MoneytreeConnection(Guid Id, string CompanyCode, string? UserId, string AccessToken, string? RefreshToken, string? TokenType, string? Scope, DateTimeOffset ExpiresAt);
}

public static class MoneytreePkce
{
    public static string GenerateCodeVerifier()
    {
        var buffer = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(buffer);
    }

    public static string GenerateCodeChallenge(string codeVerifier)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

