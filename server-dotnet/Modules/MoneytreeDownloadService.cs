using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using OtpNet;

namespace Server.Modules;

public sealed class MoneytreeDownloadService
{
    private readonly ILogger<MoneytreeDownloadService> _logger;
    private readonly IConfiguration _configuration;
    
    public MoneytreeDownloadService(ILogger<MoneytreeDownloadService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }
    
    private const string LandingUrl = "https://getmoneytree.com/jp/app/moneytree-business-login";
    private const string TransactionsNavXPath = "//*[@id=\"root\"]/div/div[1]/div/a[3]";
    private const string ExportButtonXPath = "//*[@id=\"root\"]/div/div[2]/div[2]/div[2]/div/div[2]/div[1]/div[2]/button";
    private const string DownloadButtonXPath = "//button[contains(text(), 'ダウンロード')]";

    private const string SettingsTemplate =
    "{\"value\":\"{\\\"showUnverifiedSmartTags\\\":true,\\\"refreshDataOnLogin\\\":true,\\\"accountsGroupsDisplay\\\":[],\\\"accountsGroupsDisplayChange\\\":{\\\"count\\\":0,\\\"lastUpdated\\\":0},\\\"accountGroupAndAccountsOrder\\\":[],\\\"viewSettings\\\":{\\\"selectedCustomDates\\\":{\\\"type\\\":\\\"custom\\\",\\\"startDate\\\":\\\"START_DATE\\\",\\\"endDate\\\":\\\"END_DATE\\\"},\\\"aiEnhancedForecast\\\":false},\\\"onboardingSteps\\\":{\\\"completed\\\":false,\\\"lastDisplayed\\\":0},\\\"cashFlowQuickInsight\\\":{\\\"hasShownCongratulationBanner\\\":false},\\\"accountGroupHiddenStatus\\\":{},\\\"showedOnboardingTutorial\\\":false,\\\"showedInvoiceSettingsMessage\\\":false,\\\"showedPaymentCycleModal\\\":false,\\\"forecastSettings\\\":{}}\"}";
    public sealed record MoneytreeDownloadRequest(string Email, string Password, string? OtpSecret, DateTimeOffset StartDate, DateTimeOffset EndDate);
    public sealed record MoneytreeDownloadResult(byte[] Content, string FileName, string ContentType, bool IsEmpty = false);

    public async Task<MoneytreeDownloadResult> DownloadCsvAsync(MoneytreeDownloadRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation("[Moneytree] 开始下载 CSV, 日期范围: {StartDate} ~ {EndDate}",
            request.StartDate.ToString("yyyy-MM-dd"),
            request.EndDate.ToString("yyyy-MM-dd"));
            
        try
        {
            var navigationTimeoutMs = _configuration.GetValue<int?>("Moneytree:NavigationTimeoutMs") ?? 120_000;
            var selectorTimeoutMs = _configuration.GetValue<int?>("Moneytree:SelectorTimeoutMs") ?? 90_000;
            var loginFormTimeoutMs = _configuration.GetValue<int?>("Moneytree:LoginFormTimeoutMs") ?? 30_000;
            var downloadTimeoutMs = _configuration.GetValue<int?>("Moneytree:DownloadTimeoutMs") ?? 180_000;

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                // Azure App Service Linux 容器环境下 Chromium 常见需要关闭 sandbox，否则会在启动/渲染阶段失败
                //（本地一般没问题，所以只做最小兼容性参数，不改爬虫业务流程）
                Args = new[]
                {
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-dev-shm-usage",
                    "--disable-gpu"
                }
            });

            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                AcceptDownloads = true,
                // 尽量贴近真实浏览器环境，减少线上环境差异
                Locale = "ja-JP",
                TimezoneId = "Asia/Tokyo",
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36"
            });

            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(selectorTimeoutMs);
            page.SetDefaultNavigationTimeout(navigationTimeoutMs);
            page.Console += (_, msg) =>
            {
                try
                {
                    if (msg.Type is "error" or "warning")
                        _logger.LogWarning("[Moneytree][console:{Type}] {Text}", msg.Type, msg.Text);
                }
                catch { }
            };
            page.PageError += (_, err) =>
            {
                try { _logger.LogWarning("[Moneytree][pageerror] {Error}", err); } catch { }
            };

            // 导航到登录页面
            await page.GotoAsync(LandingUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = navigationTimeoutMs });

            // 点击登录入口按钮（带重试机制）
            var loginEntrySelector = "a[custom-id='login-button']";
            var emailInputSelector = "input#guest\\[email\\]";
            
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await page.WaitForSelectorAsync(loginEntrySelector, new() { State = WaitForSelectorState.Visible, Timeout = selectorTimeoutMs });
                }
                catch (TimeoutException)
                {
                    await DumpDiagnosticsAsync(page, "login-entry-timeout", ct);
                    throw;
                }

                await page.ClickAsync(loginEntrySelector);
                
                try
                {
                    await page.WaitForSelectorAsync(emailInputSelector, new() { State = WaitForSelectorState.Visible, Timeout = loginFormTimeoutMs });
                    break;
                }
                catch (TimeoutException)
                {
                    if (attempt == 3)
                    {
                        await DumpDiagnosticsAsync(page, "login-form-timeout", ct);
                        throw new InvalidOperationException("无法进入登录表单页面（Azure 环境超时）。可能原因：网站改版/加载变慢/被风控或验证码拦截。请查看服务器日志里的 [Moneytree][diag] 输出。");
                    }
                    await Task.Delay(2000);
                }
            }
            
            // 填写登录凭据
            await page.WaitForSelectorAsync(emailInputSelector, new() { State = WaitForSelectorState.Visible, Timeout = loginFormTimeoutMs });
            await page.FillAsync("input#guest\\[email\\]", request.Email);
            await page.FillAsync("input#guest\\[password\\]", request.Password);
            await Task.Delay(2000);

            // 点击登录按钮
            var loginButtonSelector = "button.login-form-button";
            try 
            {
                await page.WaitForSelectorAsync($"{loginButtonSelector}:not([disabled])", new() { Timeout = 20_000 });
            }
            catch (Exception)
            {
                // 按钮可能没有 disabled 状态，继续尝试点击
            }

            await page.ClickAsync(loginButtonSelector);
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            
            // 增加等待时间，让登录完成
            await Task.Delay(5000);
            
            // 诊断：登录后状态
            _logger.LogInformation("[Moneytree][diag] Post-login URL: {Url}", page.Url);
            
            // 检查是否有 OTP 输入框（可能需要二次验证）
            var otpInputVisible = await page.Locator("input[name='otp'], input[placeholder*='認証'], input[type='tel']").IsVisibleAsync();
            if (otpInputVisible)
            {
                _logger.LogWarning("[Moneytree][diag] OTP input detected - two-factor authentication required");
                
                // 如果有 OTP secret，尝试生成并输入
                if (!string.IsNullOrWhiteSpace(request.OtpSecret))
                {
                    _logger.LogInformation("[Moneytree] Generating OTP code...");
                    var otpCode = GenerateTotp(request.OtpSecret);
                    _logger.LogInformation("[Moneytree] Generated OTP code (first 2 chars): {OtpPrefix}**", otpCode.Substring(0, 2));
                    
                    // 填写 OTP
                    await page.FillAsync("input[name='otp'], input[placeholder*='認証'], input[type='tel']", otpCode);
                    await Task.Delay(1000);
                    
                    // 提交 OTP（可能有确认按钮）
                    var otpSubmitButton = page.Locator("button[type='submit'], button:has-text('確認'), button:has-text('認証')");
                    if (await otpSubmitButton.IsVisibleAsync())
                    {
                        await otpSubmitButton.ClickAsync();
                        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                        await Task.Delay(3000);
                        _logger.LogInformation("[Moneytree][diag] Post-OTP URL: {Url}", page.Url);
                    }
                }
                else
                {
                    throw new InvalidOperationException("Moneytree requires OTP but no OtpSecret was provided in credentials.");
                }
            }
            
            // 检查是否有登录错误提示
            var errorText = await page.EvaluateAsync<string>(@"() => {
                const errorEls = document.querySelectorAll('.error, .alert-danger, [class*=""error""], [class*=""Error""]');
                for (const el of errorEls) {
                    if (el.innerText && el.innerText.trim()) return el.innerText.trim();
                }
                return '';
            }");
            if (!string.IsNullOrWhiteSpace(errorText))
            {
                _logger.LogError("[Moneytree][diag] Login error message on page: {Error}", errorText);
            }
            
            // 检查 sessionStorage 里有什么
            var sessionKeys = await page.EvaluateAsync<string>(@"() => Object.keys(sessionStorage).join(', ')");
            _logger.LogInformation("[Moneytree][diag] sessionStorage keys: {Keys}", sessionKeys);
            
            var localKeys = await page.EvaluateAsync<string>(@"() => Object.keys(localStorage).join(', ')");
            _logger.LogInformation("[Moneytree][diag] localStorage keys: {Keys}", localKeys);

            // 获取 token
            var token = await page.EvaluateAsync<string>(@"() => {
                const tokenStr = sessionStorage.getItem('mtb');
                if (!tokenStr) { return ''; }
                try {
                    const token = JSON.parse(tokenStr);
                    return token && token.t ? token.t : '';
                } catch (err) {
                    return '';
                }
            }");
            if (string.IsNullOrWhiteSpace(token))
            {
                // 最后一次尝试：截图保存诊断
                await DumpDiagnosticsAsync(page, "no-token", ct);
                throw new InvalidOperationException("Unable to read Moneytree sessionStorage token.");
            }
            _logger.LogInformation("[Moneytree] 登录成功");

            // 设置日期范围
            var settingsPayload = BuildSettingsPayload(
                request.StartDate.ToUniversalTime(),
                request.EndDate.ToUniversalTime());

            var response = await context.APIRequest.PutAsync("https://myaccount.getmoneytree.com/internal/app_state/settings.json", new()
            {
                Data = settingsPayload,
                Headers = new Dictionary<string, string>
                {
                    { "Content-Type", "application/json" },
                    { "Authorization", "Bearer " + token }
                }
            });

            if (!response.Ok)
            {
                var responseBody = await response.TextAsync();
                _logger.LogError("[Moneytree] 设置日期范围失败: HTTP {Status}", (int)response.Status);
                throw new InvalidOperationException($"Failed to update Moneytree date range: HTTP {(int)response.Status}");
            }

            await Task.Delay(2000);

            // 导航到交易页面
            await page.ClickAsync(TransactionsNavXPath);
            await page.WaitForLoadStateAsync();
            await Task.Delay(1000);

            // 点击导出按钮
            await page.ClickAsync(ExportButtonXPath);
            await Task.Delay(500);
            
            // 选择残高データ
            await page.ClickAsync("//button[contains(text(), '残高データ')]");
            await Task.Delay(1000);

            // ========== 增强的「0件」检测逻辑 ==========
            // 配置开关：设为 true 使用增强检测，设为 false 使用原有逻辑
            // 如果增强逻辑出现问题，可将此值改为 false 立即恢复原有行为
            var useEnhancedEmptyCheck = _configuration.GetValue<bool?>("Moneytree:UseEnhancedEmptyCheck") ?? true;
            
            var downloadButton = page.Locator(DownloadButtonXPath);
            
            if (useEnhancedEmptyCheck)
            {
                // ===== 增强逻辑（2026-02-03 添加）=====
                // 增强检测：先等待按钮可见，检查文本和禁用状态，避免超时等待
                
                // 等待按钮可见
                try
                {
                    await downloadButton.WaitForAsync(new LocatorWaitForOptions 
                    { 
                        State = WaitForSelectorState.Visible, 
                        Timeout = 10_000 
                    });
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("[Moneytree] 下载按钮未找到，可能页面加载问题");
                }
                
                // 等待一小段时间让按钮完全渲染（包括文本和状态）
                await Task.Delay(1000);
                
                // 获取按钮文本
                var buttonText = await downloadButton.TextContentAsync();
                _logger.LogInformation("[Moneytree] 下载按钮文本: {Text}", buttonText);
                
                // 检查按钮是否被禁用
                var isDisabled = await downloadButton.IsDisabledAsync();
                _logger.LogInformation("[Moneytree] 下载按钮禁用状态: {IsDisabled}", isDisabled);
                
                // 如果文本包含「0件」或按钮被禁用，则认为没有数据
                var hasZeroItems = !string.IsNullOrWhiteSpace(buttonText) && 
                    (buttonText.Contains("（0件）") || buttonText.Contains("(0件)") || buttonText.Contains("0件"));
                
                if (hasZeroItems || isDisabled)
                {
                    _logger.LogInformation("[Moneytree] 指定日期范围内没有新的银行明细（按钮文本={Text}, 禁用={IsDisabled}），无需下载", 
                        buttonText, isDisabled);
                    return new MoneytreeDownloadResult(Array.Empty<byte>(), "empty.csv", "text/csv", IsEmpty: true);
                }

                // 等待按钮可点击（enabled 状态）
                try
                {
                    await downloadButton.WaitForAsync(new LocatorWaitForOptions 
                    { 
                        State = WaitForSelectorState.Visible, 
                        Timeout = 30_000 
                    });
                    
                    // 额外检查：等待按钮变为 enabled
                    var waitStart = DateTime.UtcNow;
                    while (await downloadButton.IsDisabledAsync())
                    {
                        if ((DateTime.UtcNow - waitStart).TotalSeconds > 30)
                        {
                            _logger.LogWarning("[Moneytree] 下载按钮持续处于禁用状态，判定为无数据");
                            return new MoneytreeDownloadResult(Array.Empty<byte>(), "empty.csv", "text/csv", IsEmpty: true);
                        }
                        await Task.Delay(500);
                    }
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("[Moneytree] 等待下载按钮启用超时，判定为无数据");
                    return new MoneytreeDownloadResult(Array.Empty<byte>(), "empty.csv", "text/csv", IsEmpty: true);
                }
                // ===== 增强逻辑结束 =====
            }
            else
            {
                // ===== 原有逻辑（保留用于回退）=====
                // 检查下载按钮状态 - 如果显示"（0件）"则表示没有数据
                var buttonText = await downloadButton.TextContentAsync();
                _logger.LogInformation("[Moneytree] 下载按钮文本: {Text}", buttonText);
                
                if (!string.IsNullOrWhiteSpace(buttonText) && buttonText.Contains("（0件）"))
                {
                    _logger.LogInformation("[Moneytree] 指定日期范围内没有新的银行明细（0件），无需下载");
                    return new MoneytreeDownloadResult(Array.Empty<byte>(), "empty.csv", "text/csv", IsEmpty: true);
                }
                // ===== 原有逻辑结束 =====
            }

            // 等待下载
            var download = await page.RunAndWaitForDownloadAsync(async () =>
            {
                await page.ClickAsync(DownloadButtonXPath);
            }, new PageRunAndWaitForDownloadOptions
            {
                Timeout = downloadTimeoutMs
            });

            await using var downloadStream = await download.CreateReadStreamAsync();
            using var memory = new MemoryStream();
            if (downloadStream is null)
            {
                throw new InvalidOperationException("Unable to read Moneytree download stream.");
            }
            await downloadStream.CopyToAsync(memory, ct);

            var suggestedFileName = download.SuggestedFilename ?? $"moneytree-transactions-{DateTimeOffset.Now:yyyyMMddHHmmss}.csv";
            
            _logger.LogInformation("[Moneytree] 下载完成, 文件名: {FileName}, 大小: {Size} bytes", suggestedFileName, memory.Length);

            return new MoneytreeDownloadResult(memory.ToArray(), suggestedFileName, "text/csv");
        }
        catch (PlaywrightException ex)
        {
            _logger.LogError(ex, "[Moneytree] Playwright 执行失败");
            throw new InvalidOperationException("Moneytree CSV download failed at playwright. Please check server logs for [Moneytree][diag] details.", ex);
        }
    }

    private static string BuildSettingsPayload(DateTimeOffset startDate, DateTimeOffset endDate)
    {
        if (startDate > endDate)
        {
            throw new ArgumentException("startDate must be earlier than endDate");
        }

        var start = startDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var end = endDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        return SettingsTemplate.Replace("START_DATE", start).Replace("END_DATE", end);
    }

    private async Task DumpDiagnosticsAsync(IPage page, string stage, CancellationToken ct)
    {
        try
        {
            var url = string.Empty;
            var title = string.Empty;
            try { url = page.Url; } catch { }
            try { title = await page.TitleAsync(); } catch { }

            _logger.LogError("[Moneytree][diag] stage={Stage} url={Url} title={Title}", stage, url, title);

            string html = string.Empty;
            try
            {
                html = await page.ContentAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Moneytree][diag] stage={Stage} failed to read page content", stage);
            }

            if (!string.IsNullOrWhiteSpace(html))
            {
                var snippet = html.Length > 4000 ? html.Substring(0, 4000) + "...(truncated)" : html;
                _logger.LogError("[Moneytree][diag] stage={Stage} html_head={Html}", stage, snippet);

                // 粗略识别“风控/验证码/被拦截”页面
                if (Regex.IsMatch(html, "(captcha|cloudflare|access denied|アクセス|制限|robot|bot|不正)", RegexOptions.IgnoreCase))
                {
                    _logger.LogError("[Moneytree][diag] stage={Stage} hint=可能被风控/验证码拦截（Azure IP/Headless 环境）", stage);
                }
            }

            try
            {
                var hasLoginEntry = await page.Locator("a[custom-id='login-button']").CountAsync();
                var hasEmail = await page.Locator("input#guest\\[email\\]").CountAsync();
                _logger.LogError("[Moneytree][diag] stage={Stage} hasLoginEntry={HasLoginEntry} hasEmailInput={HasEmail}", stage, hasLoginEntry, hasEmail);
            }
            catch { }
        }
        catch (Exception ex)
        {
            try { _logger.LogWarning(ex, "[Moneytree][diag] stage={Stage} diagnostics failed", stage); } catch { }
        }
    }

    private static string GenerateTotp(string secret)
    {
        // 移除空格和连字符，转为大写
        var cleanedSecret = secret.Replace(" ", "").Replace("-", "").ToUpperInvariant();
        var secretBytes = Base32Encoding.ToBytes(cleanedSecret);
        var totp = new Totp(secretBytes);
        return totp.ComputeTotp();
    }
}
