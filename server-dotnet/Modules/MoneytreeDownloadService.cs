using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Server.Modules;

public sealed class MoneytreeDownloadService
{
    private readonly ILogger<MoneytreeDownloadService> _logger;
    
    public MoneytreeDownloadService(ILogger<MoneytreeDownloadService> logger)
    {
        _logger = logger;
    }
    
    private const string LandingUrl = "https://getmoneytree.com/jp/app/moneytree-business-login";
    private const string TransactionsNavXPath = "//*[@id=\"root\"]/div/div[1]/div/a[3]";
    private const string ExportButtonXPath = "//*[@id=\"root\"]/div/div[2]/div[2]/div[2]/div/div[2]/div[1]/div[2]/button";
    private const string DownloadButtonXPath = "//button[contains(text(), 'ダウンロード')]";

    private const string SettingsTemplate =
    "{\"value\":\"{\\\"showUnverifiedSmartTags\\\":true,\\\"refreshDataOnLogin\\\":true,\\\"accountsGroupsDisplay\\\":[],\\\"accountsGroupsDisplayChange\\\":{\\\"count\\\":0,\\\"lastUpdated\\\":0},\\\"accountGroupAndAccountsOrder\\\":[],\\\"viewSettings\\\":{\\\"selectedCustomDates\\\":{\\\"type\\\":\\\"custom\\\",\\\"startDate\\\":\\\"START_DATE\\\",\\\"endDate\\\":\\\"END_DATE\\\"},\\\"aiEnhancedForecast\\\":false},\\\"onboardingSteps\\\":{\\\"completed\\\":false,\\\"lastDisplayed\\\":0},\\\"cashFlowQuickInsight\\\":{\\\"hasShownCongratulationBanner\\\":false},\\\"accountGroupHiddenStatus\\\":{},\\\"showedOnboardingTutorial\\\":false,\\\"showedInvoiceSettingsMessage\\\":false,\\\"showedPaymentCycleModal\\\":false,\\\"forecastSettings\\\":{}}\"}";
    public sealed record MoneytreeDownloadRequest(string Email, string Password, string? OtpSecret, DateTimeOffset StartDate, DateTimeOffset EndDate);
    public sealed record MoneytreeDownloadResult(byte[] Content, string FileName, string ContentType);

    public async Task<MoneytreeDownloadResult> DownloadCsvAsync(MoneytreeDownloadRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation("[Moneytree] 开始下载 CSV, 日期范围: {StartDate} ~ {EndDate}",
            request.StartDate.ToString("yyyy-MM-dd"),
            request.EndDate.ToString("yyyy-MM-dd"));
            
        try
        {
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
                AcceptDownloads = true
            });

            var page = await context.NewPageAsync();

            // 导航到登录页面
            await page.GotoAsync(LandingUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60000
            });

            // 点击登录入口按钮（带重试机制）
            var loginEntrySelector = "a[custom-id='login-button']";
            var emailInputSelector = "input#guest\\[email\\]";
            
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                await page.WaitForSelectorAsync(loginEntrySelector, new() { State = WaitForSelectorState.Visible, Timeout = 30000 });
                await page.ClickAsync(loginEntrySelector);
                
                try
                {
                    await page.WaitForSelectorAsync(emailInputSelector, new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
                    break;
                }
                catch (TimeoutException)
                {
                    if (attempt == 3)
                    {
                        throw new InvalidOperationException("无法进入登录表单页面，请检查 Moneytree 网站是否有变化");
                    }
                    await Task.Delay(2000);
                }
            }
            
            // 填写登录凭据
            await page.WaitForSelectorAsync(emailInputSelector, new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
            await page.FillAsync("input#guest\\[email\\]", request.Email);
            await page.FillAsync("input#guest\\[password\\]", request.Password);
            await Task.Delay(2000);

            // 点击登录按钮
            var loginButtonSelector = "button.login-form-button";
            try 
            {
                await page.WaitForSelectorAsync($"{loginButtonSelector}:not([disabled])", new() { Timeout = 10000 });
            }
            catch (Exception)
            {
                // 按钮可能没有 disabled 状态，继续尝试点击
            }

            await page.ClickAsync(loginButtonSelector);
            await page.WaitForLoadStateAsync();
            await Task.Delay(2000);

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

            // 等待下载
            var download = await page.RunAndWaitForDownloadAsync(async () =>
            {
                await page.ClickAsync(DownloadButtonXPath);
            }, new PageRunAndWaitForDownloadOptions
            {
                Timeout = 120000
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
            throw new InvalidOperationException("Moneytree CSV download failed. Please verify credentials or page changes.", ex);
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
}
