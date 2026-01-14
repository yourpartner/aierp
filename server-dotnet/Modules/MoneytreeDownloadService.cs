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
                Headless = true
            });

            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                AcceptDownloads = true
            });

            var page = await context.NewPageAsync();

            _logger.LogInformation("[Moneytree] 导航到登录页面");
            await page.GotoAsync(LandingUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 60000
            });

            await page.WaitForLoadStateAsync();

            await page.ClickAsync("body > div.split-container > div.mtb-landing-hero-div.moneytree-business-lp > div > div.mtb-landing-hero-content.toppage > div.buttoncontainer > a.moneytree-button.button-secondary-hero.ga-track.moneytree-business-gray-button.w-button");
            await page.WaitForLoadStateAsync();

            _logger.LogInformation("[Moneytree] 填写登录凭据");
            await page.FillAsync("input#guest\\[email\\]", request.Email);
            await page.FillAsync("input#guest\\[password\\]", request.Password);
            await Task.Delay(2000);

            await page.ClickAsync("body > nil > div.app-page > div > div.login-form-container > form > button");
        
            await page.WaitForLoadStateAsync();

            await Task.Delay(2000);

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
            _logger.LogInformation("[Moneytree] 登录成功, 获取到 token");

            var settingsPayload = BuildSettingsPayload(
                request.StartDate.ToUniversalTime(),
                request.EndDate.ToUniversalTime());

            _logger.LogInformation("[Moneytree] 设置日期范围 API 请求: {Payload}", settingsPayload);

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
                _logger.LogError("[Moneytree] 设置日期范围失败: HTTP {Status}, Body: {Body}", (int)response.Status, responseBody);
                throw new InvalidOperationException($"Failed to update Moneytree date range: HTTP {(int)response.Status}");
            }
            _logger.LogInformation("[Moneytree] 日期范围设置成功");

            // 设置完日期范围后，刷新页面以应用新设置
            _logger.LogInformation("[Moneytree] 刷新页面以应用日期范围设置");
            
            await Task.Delay(2000);

            _logger.LogInformation("[Moneytree] 导航到交易页面");
            await page.ClickAsync(TransactionsNavXPath);
            await page.WaitForLoadStateAsync();
            await Task.Delay(1000);

            _logger.LogInformation("[Moneytree] 点击导出按钮");
            await page.ClickAsync(ExportButtonXPath);

            await Task.Delay(500);
            await page.ClickAsync("//button[contains(text(), '残高データ')]");

            _logger.LogInformation("[Moneytree] 等待下载");
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

