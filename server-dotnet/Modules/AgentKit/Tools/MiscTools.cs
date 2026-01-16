using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Server.Modules.AgentKit;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// 绋庨璁＄畻宸ュ叿
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
            return Task.FromResult(ErrorResult(Localize(context.Language, "amount 銇屽繀瑕併仹銇?, "amount 蹇呭～")));
        }

        Logger.LogInformation("[CalculateTaxTool] 璁＄畻绋庨: Amount={Amount}, TaxRate={TaxRate}, IncludeTax={IncludeTax}", 
            amount, taxRate, includeTax);

        decimal taxAmount, netAmount, grossAmount;

        if (includeTax)
        {
            // 鍚◣閲戦 -> 璁＄畻绋庨鍜屼笉鍚◣閲戦
            grossAmount = amount.Value;
            taxAmount = Math.Round(grossAmount * taxRate / (100 + taxRate), 0, MidpointRounding.AwayFromZero);
            netAmount = grossAmount - taxAmount;
        }
        else
        {
            // 涓嶅惈绋庨噾棰?-> 璁＄畻绋庨鍜屽惈绋庨噾棰?            netAmount = amount.Value;
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
/// 璐у竵杞崲宸ュ叿锛堢畝鍗曠増鏈級
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
            return Task.FromResult(ErrorResult(Localize(context.Language, "amount 銇屽繀瑕併仹銇?, "amount 蹇呭～")));
        }

        Logger.LogInformation("[ConvertCurrencyTool] 璐у竵杞崲: {Amount} {From} -> {To}", amount, fromCurrency, toCurrency);

        // 濡傛灉鎻愪緵浜嗘眹鐜囷紝浣跨敤鎻愪緵鐨勬眹鐜?        if (rate.HasValue && rate.Value > 0)
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

        // 濡傛灉鐩稿悓璐у竵锛岀洿鎺ヨ繑鍥?        if (string.Equals(fromCurrency, toCurrency, StringComparison.OrdinalIgnoreCase))
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

        // 鍚﹀垯鎻愮ず闇€瑕佹眹鐜?        return Task.FromResult(ErrorResult(Localize(context.Language,
            "鐣般仾銈嬮€氳波闁撱伄澶夋彌銇伅 rate 銇寚瀹氥亴蹇呰銇с仚",
            "涓嶅悓璐у竵涔嬮棿鐨勮浆鎹㈤渶瑕佹寚瀹?rate")));
    }
}

/// <summary>
/// 鏃ユ湡鏍煎紡鍖栧伐鍏?/// </summary>
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
            return Task.FromResult(ErrorResult(Localize(context.Language, "date 銇屽繀瑕併仹銇?, "date 蹇呭～")));
        }

        if (!DateTime.TryParse(dateStr, out var date))
        {
            return Task.FromResult(ErrorResult(Localize(context.Language, "鏃ヤ粯褰㈠紡銇岀劇鍔广仹銇?, "鏃ユ湡鏍煎紡鏃犳晥")));
        }

        Logger.LogInformation("[FormatDateTool] 鏍煎紡鍖栨棩鏈? {Date} -> {Format}", dateStr, format);

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
            return Task.FromResult(ErrorResult(Localize(context.Language, "銉曘偐銉笺優銉冦儓銇岀劇鍔广仹銇?, "鏍煎紡瀛楃涓叉棤鏁?)));
        }
    }
}



using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;


