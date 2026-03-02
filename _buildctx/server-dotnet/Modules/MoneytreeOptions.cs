namespace Server.Modules;

public class MoneytreeOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string AuthBaseUrl { get; set; } = "https://moneytree.jp/oauth/authorize";
    public string TokenUrl { get; set; } = "https://moneytree.jp/oauth/token";
    public string ApiBaseUrl { get; set; } = "https://moneytree.jp/api/v2";
    public string Scope { get; set; } = "accounts:read transactions:read";
    public bool UsePkce { get; set; } = true;
    public TimeSpan TokenRefreshSkew { get; set; } = TimeSpan.FromMinutes(2);
    public string? WebhookSecret { get; set; }

    // 兼容旧代码字段名（MoneytreeService 使用这些属性）
    public string Scopes => Scope;
    public string BaseUrl => ApiBaseUrl.TrimEnd('/');
    public string AuthUrl => AuthBaseUrl.TrimEnd('/');
}

