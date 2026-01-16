using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Server.Modules.AgentKit;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// 税额计算工具
/// </summary>
public sealed class CalculateTaxTool : AgentToolBase
{
    public CalculateTaxTool(ILogger<CalculateTaxTool> logger) : base(logger) { }

    public override string Name => "calculate_tax";

    public override Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var amount = GetDecimal(args, "amount");
        var taxRate = GetDecimal(args, "tax_rate") ?? GetDecimal(args, "taxRate") ?? 10m;
        var includeTax = GetBool(args, "include_tax") ?? GetBool(args, "includeTax") ?? true;

        if (!amount.HasValue || amount.Value <= 0)
        {
            return Task.FromResult(ErrorResult(Localize(context.Language, "amount が必要です", "amount 必填")));
        }

        Logger.LogInformation("[CalculateTaxTool] 计算税额: Amount={Amount}, TaxRate={TaxRate}, IncludeTax={IncludeTax}", 
            amount, taxRate, includeTax);

        decimal taxAmount, netAmount, grossAmount;

        if (includeTax)
        {
            // 含税金额 -> 计算税额和不含税金额
            grossAmount = amount.Value;
            taxAmount = Math.Round(grossAmount * taxRate / (100 + taxRate), 0, MidpointRounding.AwayFromZero);
            netAmount = grossAmount - taxAmount;
        }
        else
        {
            // 不含税金额 -> 计算税额和含税金额
            netAmount = amount.Value;
            taxAmount = Math.Round(netAmount * taxRate / 100, 0, MidpointRounding.AwayFromZero);
            grossAmount = netAmount + taxAmount;
        }

        return Task.FromResult(SuccessResult(new
        {
            grossAmount,
            netAmount,
            taxAmount,
            taxRate,
            includeTax
        }));
    }
}

/// <summary>
/// 货币转换工具（简单版本）
/// </summary>
public sealed class ConvertCurrencyTool : AgentToolBase
{
    public ConvertCurrencyTool(ILogger<ConvertCurrencyTool> logger) : base(logger) { }

    public override string Name => "convert_currency";

    public override Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var amount = GetDecimal(args, "amount");
        var fromCurrency = GetString(args, "from_currency") ?? GetString(args, "fromCurrency") ?? "JPY";
        var toCurrency = GetString(args, "to_currency") ?? GetString(args, "toCurrency") ?? "JPY";
        var rate = GetDecimal(args, "rate");

        if (!amount.HasValue)
        {
            return Task.FromResult(ErrorResult(Localize(context.Language, "amount が必要です", "amount 必填")));
        }

        Logger.LogInformation("[ConvertCurrencyTool] 货币转换: {Amount} {From} -> {To}", amount, fromCurrency, toCurrency);

        // 如果提供了汇率，使用提供的汇率
        if (rate.HasValue && rate.Value > 0)
        {
            var converted = Math.Round(amount.Value * rate.Value, 2, MidpointRounding.AwayFromZero);
            return Task.FromResult(SuccessResult(new
            {
                originalAmount = amount.Value,
                fromCurrency,
                toCurrency,
                rate = rate.Value,
                convertedAmount = converted
            }));
        }

        // 如果相同货币，直接返回
        if (string.Equals(fromCurrency, toCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(SuccessResult(new
            {
                originalAmount = amount.Value,
                fromCurrency,
                toCurrency,
                rate = 1m,
                convertedAmount = amount.Value
            }));
        }

        // 否则提示需要汇率
        return Task.FromResult(ErrorResult(Localize(context.Language,
            "異なる通貨間の変換には rate の指定が必要です",
            "不同货币之间的转换需要指定 rate")));
    }
}

/// <summary>
/// 日期格式化工具
/// </summary>
public sealed class FormatDateTool : AgentToolBase
{
    public FormatDateTool(ILogger<FormatDateTool> logger) : base(logger) { }

    public override string Name => "format_date";

    public override Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var dateStr = GetString(args, "date");
        var format = GetString(args, "format") ?? "yyyy-MM-dd";

        if (string.IsNullOrWhiteSpace(dateStr))
        {
            return Task.FromResult(ErrorResult(Localize(context.Language, "date が必要です", "date 必填")));
        }

        if (!DateTime.TryParse(dateStr, out var date))
        {
            return Task.FromResult(ErrorResult(Localize(context.Language, "日付形式が無効です", "日期格式无效")));
        }

        Logger.LogInformation("[FormatDateTool] 格式化日期: {Date} -> {Format}", dateStr, format);

        try
        {
            var formatted = date.ToString(format);
            return Task.FromResult(SuccessResult(new
            {
                original = dateStr,
                formatted,
                format,
                year = date.Year,
                month = date.Month,
                day = date.Day
            }));
        }
        catch (FormatException)
        {
            return Task.FromResult(ErrorResult(Localize(context.Language, "フォーマットが無効です", "格式字符串无效")));
        }
    }
}




