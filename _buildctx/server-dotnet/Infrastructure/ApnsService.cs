using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Server.Infrastructure;

public sealed class ApnsService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ApnsService> _logger;
    private readonly string? _teamId;
    private readonly string? _keyId;
    private readonly string? _p8KeyPem;

    private string? _cachedJwt;
    private DateTimeOffset _cachedJwtExp;

    public ApnsService(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<ApnsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _teamId = config["APNS:TeamId"] ?? Environment.GetEnvironmentVariable("APNS_TEAM_ID");
        _keyId = config["APNS:KeyId"] ?? Environment.GetEnvironmentVariable("APNS_KEY_ID");
        _p8KeyPem = config["APNS:P8"] ?? Environment.GetEnvironmentVariable("APNS_P8_KEY");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_teamId) && !string.IsNullOrWhiteSpace(_keyId) && !string.IsNullOrWhiteSpace(_p8KeyPem);

    public async Task<(bool ok, string? id, string? error)> SendAsync(
        string bundleId,
        string deviceToken,
        string title,
        string body,
        bool sandbox,
        string? category = null,
        JsonElement? extra = null)
    {
        if (!IsConfigured)
        {
            return (false, null, "APNs not configured: set APNS_TEAM_ID/APNS_KEY_ID/APNS_P8_KEY");
        }

        var jwt = GetOrCreateJwt();
        var host = sandbox ? "https://api.sandbox.push.apple.com" : "https://api.push.apple.com";
        var url = $"{host}/3/device/{deviceToken}";

        using var client = _httpClientFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Version = new Version(2, 0);
        req.Headers.Authorization = new AuthenticationHeaderValue("bearer", jwt);
        req.Headers.TryAddWithoutValidation("apns-topic", bundleId);
        req.Headers.TryAddWithoutValidation("apns-push-type", "alert");
        req.Headers.TryAddWithoutValidation("apns-priority", "10");

        using var doc = BuildPayload(title, body, category, extra);
        req.Content = new StringContent(JsonSerializer.Serialize(doc.RootElement), Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req);
        var content = await resp.Content.ReadAsStringAsync();
        if (resp.IsSuccessStatusCode)
        {
            var apnsId = resp.Headers.TryGetValues("apns-id", out var vals) ? vals.FirstOrDefault() : null;
            return (true, apnsId, null);
        }
        _logger.LogWarning("APNs send failed: {Status} {Content}", (int)resp.StatusCode, content);
        return (false, null, content);
    }

    private JwtSecurityToken CreateJwtToken(ECDsa ecdsa)
    {
        // Per APNs spec, JWT valid up to 60 minutes; we cache ~50 minutes
        var now = DateTimeOffset.UtcNow;
        var handler = new JwtSecurityTokenHandler();
        var secKey = new ECDsaSecurityKey(ecdsa) { KeyId = _keyId };
        var creds = new SigningCredentials(secKey, SecurityAlgorithms.EcdsaSha256);

        var token = new JwtSecurityToken(
            issuer: _teamId,
            audience: "https://appleid.apple.com",
            claims: null,
            notBefore: now.UtcDateTime,
            expires: now.AddMinutes(55).UtcDateTime,
            signingCredentials: creds);
        return token;
    }

    private string GetOrCreateJwt()
    {
        if (!string.IsNullOrEmpty(_cachedJwt) && DateTimeOffset.UtcNow < _cachedJwtExp)
        {
            return _cachedJwt!;
        }

        using var ecdsa = LoadPrivateKey(_p8KeyPem!);
        var token = CreateJwtToken(ecdsa);
        var handler = new JwtSecurityTokenHandler();
        _cachedJwt = handler.WriteToken(token);
        _cachedJwtExp = DateTimeOffset.UtcNow.AddMinutes(50);
        return _cachedJwt!;
    }

    private static ECDsa LoadPrivateKey(string pem)
    {
        // Accept both with headers or raw base64
        var base64 = pem
            .Replace("-----BEGIN PRIVATE KEY-----", string.Empty)
            .Replace("-----END PRIVATE KEY-----", string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty)
            .Trim();
        var keyBytes = Convert.FromBase64String(base64);
        var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(keyBytes, out _);
        return ecdsa;
    }

    private static JsonDocument BuildPayload(string title, string body, string? category, JsonElement? extra)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("aps");
            writer.WriteStartObject();
            writer.WritePropertyName("alert");
            writer.WriteStartObject();
            writer.WriteString("title", title);
            writer.WriteString("body", body);
            writer.WriteEndObject();
            writer.WriteString("sound", "default");
            writer.WriteNumber("badge", 1);
            if (!string.IsNullOrWhiteSpace(category))
            {
                writer.WriteString("category", category);
            }
            writer.WriteEndObject();
            if (extra.HasValue && extra.Value.ValueKind != JsonValueKind.Undefined && extra.Value.ValueKind != JsonValueKind.Null)
            {
                foreach (var prop in extra.Value.EnumerateObject())
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray());
    }
}


