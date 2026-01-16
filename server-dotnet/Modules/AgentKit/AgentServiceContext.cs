using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Net.Http;
using Server.Infrastructure;

namespace Server.Modules.AgentKit;

/// <summary>
/// Agent 服务上下文 - 聚合工具执行所需的所有服务依赖
/// </summary>
public sealed class AgentServiceContext
{
    public NpgsqlDataSource DataSource { get; }
    public FinanceService Finance { get; }
    public InvoiceRegistryService InvoiceRegistry { get; }
    public InvoiceTaskService InvoiceTaskService { get; }
    public SalesOrderTaskService SalesOrderTaskService { get; }
    public AgentScenarioService ScenarioService { get; }
    public AgentAccountingRuleService RuleService { get; }
    public MoneytreePostingRuleService MoneytreeRuleService { get; }
    public AzureBlobService BlobService { get; }
    public IHttpClientFactory HttpClientFactory { get; }
    public IConfiguration Configuration { get; }
    public ILoggerFactory LoggerFactory { get; }

    public AgentServiceContext(
        NpgsqlDataSource dataSource,
        FinanceService finance,
        InvoiceRegistryService invoiceRegistry,
        InvoiceTaskService invoiceTaskService,
        SalesOrderTaskService salesOrderTaskService,
        AgentScenarioService scenarioService,
        AgentAccountingRuleService ruleService,
        MoneytreePostingRuleService moneytreeRuleService,
        AzureBlobService blobService,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        DataSource = dataSource;
        Finance = finance;
        InvoiceRegistry = invoiceRegistry;
        InvoiceTaskService = invoiceTaskService;
        SalesOrderTaskService = salesOrderTaskService;
        ScenarioService = scenarioService;
        RuleService = ruleService;
        MoneytreeRuleService = moneytreeRuleService;
        BlobService = blobService;
        HttpClientFactory = httpClientFactory;
        Configuration = configuration;
        LoggerFactory = loggerFactory;
    }

    /// <summary>
    /// 创建带 OpenAI 配置的 HttpClient
    /// </summary>
    public HttpClient CreateOpenAiClient()
    {
        return HttpClientFactory.CreateClient("openai");
    }

    /// <summary>
    /// 获取 OpenAI API Key
    /// </summary>
    public string GetOpenAiApiKey()
    {
        return Configuration["OpenAI:ApiKey"] ?? "";
    }
}



