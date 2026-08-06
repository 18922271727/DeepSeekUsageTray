using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekUsageTray;

public sealed class DeepSeekException : Exception
{
    public DeepSeekException(string message) : base(message)
    {
    }
}

public sealed class UsageSummary
{
    public bool BalanceConfigured { get; set; }
    public double Balance { get; set; }
    public bool IsAvailable { get; set; }
    public string Currency { get; set; } = "CNY";
    public bool UsageConfigured { get; set; }
    public long TodayTokens { get; set; }
    public long TodayRequests { get; set; }
    public double TodayCost { get; set; }
    public long MonthTokens { get; set; }
    public long MonthRequests { get; set; }
    public double MonthCost { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>按模型名的当月累计明细（命中/未命中/输出 token），供实时页做差值采样。</summary>
    public Dictionary<string, ModelTokenUsage> Models { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModelTokenUsage
{
    public long HitTokens { get; set; }
    public long MissTokens { get; set; }
    public long ResponseTokens { get; set; }
    public long Requests { get; set; }
    public long TotalTokens => HitTokens + MissTokens + ResponseTokens;
}

public sealed class DeepSeekClient
{
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://platform.deepseek.com");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://platform.deepseek.com/");
        return client;
    }

    public async Task<UsageSummary> FetchAsync(
        string platformToken,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.Now;
        var summary = new UsageSummary { UpdatedAt = now };

        if (string.IsNullOrWhiteSpace(platformToken))
        {
            // 未配置 Token：余额和用量都保持“未设置”
            return summary;
        }

        summary.BalanceConfigured = true;
        summary.UsageConfigured = true;
        var token = platformToken.Trim();
        var month = now.Month;
        var year = now.Year;

        using var summaryRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "https://platform.deepseek.com/api/v0/users/get_user_summary");
        using var amountRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://platform.deepseek.com/api/v0/usage/amount?month={month}&year={year}");
        using var costRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://platform.deepseek.com/api/v0/usage/cost?month={month}&year={year}");
        summaryRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        amountRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        costRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        var summaryTask = Http.SendAsync(summaryRequest, cancellationToken);
        var amountTask = Http.SendAsync(amountRequest, cancellationToken);
        var costTask = Http.SendAsync(costRequest, cancellationToken);
        await Task.WhenAll(summaryTask, amountTask, costTask).ConfigureAwait(false);

        var summaryBody = await summaryTask.Result.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var amountBody = await amountTask.Result.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var costBody = await costTask.Result.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        ParseBalance(summaryBody, summary);
        ParseUsage(amountBody, costBody, summary, now);
        return summary;
    }

    private static void ParseBalance(string body, UsageSummary summary)
    {
        var envelope = JsonSerializer.Deserialize<ApiEnvelope<BizEnvelope<UserSummaryBizData>>>(body);
        ThrowIfError(envelope?.Code, envelope?.Msg, "余额查询失败");
        ThrowIfError(envelope?.Data?.BizCode, envelope?.Data?.BizMsg, "余额查询失败");

        var bizData = envelope?.Data?.BizData;
        if (bizData == null)
        {
            return;
        }

        var currency = PickCurrency(bizData);
        double total = 0;
        foreach (var wallet in Concat(bizData.NormalWallets, bizData.BonusWallets))
        {
            if (wallet == null || wallet.Currency != currency)
            {
                continue;
            }
            total += TryGetDouble(wallet.Balance, out var value) ? value : 0;
        }

        summary.Balance = total;
        summary.Currency = currency;
        summary.IsAvailable = total > 0;
    }

    private static string PickCurrency(UserSummaryBizData bizData)
    {
        var currencies = Concat(bizData.NormalWallets, bizData.BonusWallets)
            .Select(w => w?.Currency)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (currencies.Contains("CNY", StringComparer.OrdinalIgnoreCase))
        {
            return "CNY";
        }
        if (currencies.Contains("USD", StringComparer.OrdinalIgnoreCase))
        {
            return "USD";
        }
        return currencies.FirstOrDefault() ?? "CNY";
    }

    private static IEnumerable<T> Concat<T>(IEnumerable<T>? first, IEnumerable<T>? second)
    {
        if (first != null)
        {
            foreach (var item in first)
            {
                yield return item;
            }
        }
        if (second != null)
        {
            foreach (var item in second)
            {
                yield return item;
            }
        }
    }

    private static void ParseUsage(string amountBody, string costBody, UsageSummary summary, DateTime now)
    {
        var amount = JsonSerializer.Deserialize<ApiEnvelope<BizEnvelope<AmountData>>>(amountBody);
        ThrowIfError(amount?.Code, amount?.Msg, "用量查询失败");
        ThrowIfError(amount?.Data?.BizCode, amount?.Data?.BizMsg, "用量查询失败");

        var cost = JsonSerializer.Deserialize<ApiEnvelope<BizEnvelope<List<CostBizData>>>>(costBody);
        ThrowIfError(cost?.Code, cost?.Msg, "消费查询失败");
        ThrowIfError(cost?.Data?.BizCode, cost?.Data?.BizMsg, "消费查询失败");

        var costData = cost?.Data?.BizData?.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(costData?.Currency))
        {
            summary.Currency = costData!.Currency!;
        }

        var todayKey = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var total = amount?.Data?.BizData?.Total ?? new List<ModelUsage>();
        (summary.MonthTokens, summary.MonthRequests) = SumUsage(total);
        var today = amount?.Data?.BizData?.Days?.FirstOrDefault(d => d.Date == todayKey);
        (summary.TodayTokens, summary.TodayRequests) = SumUsage(today?.Data ?? new List<ModelUsage>());

        summary.MonthCost = SumCost(costData?.Total ?? new List<CostModelUsage>());
        var todayCost = costData?.Days?.FirstOrDefault(d => d.Date == todayKey);
        summary.TodayCost = SumCost(todayCost?.Data ?? new List<CostModelUsage>());

        summary.Models.Clear();
        foreach (var model in total)
        {
            if (string.IsNullOrWhiteSpace(model?.Model) || model.Usage == null)
            {
                continue;
            }

            var detail = new ModelTokenUsage();
            foreach (var item in model.Usage)
            {
                if (item?.Type == null || !TryParseAmount(item.Amount, out var value))
                {
                    continue;
                }

                switch (item.Type.ToUpperInvariant())
                {
                    case "PROMPT_CACHE_HIT_TOKEN":
                        detail.HitTokens += (long)value;
                        break;
                    case "PROMPT_CACHE_MISS_TOKEN":
                        detail.MissTokens += (long)value;
                        break;
                    case "RESPONSE_TOKEN":
                        detail.ResponseTokens += (long)value;
                        break;
                    case "REQUEST":
                        detail.Requests += (long)value;
                        break;
                }
            }
            summary.Models[model.Model!] = detail;
        }
    }

    private static (long Tokens, long Requests) SumUsage(IEnumerable<ModelUsage> models)
    {
        long tokens = 0;
        long requests = 0;
        foreach (var model in models)
        {
            if (model?.Usage == null)
            {
                continue;
            }

            foreach (var item in model.Usage)
            {
                if (item?.Type == null || !TryParseAmount(item.Amount, out var amount))
                {
                    continue;
                }

                var type = item.Type.ToUpperInvariant();
                if (type == "REQUEST")
                {
                    requests += (long)amount;
                }
                else if (type.EndsWith("TOKEN", StringComparison.Ordinal))
                {
                    tokens += (long)amount;
                }
            }
        }
        return (tokens, requests);
    }

    private static double SumCost(IEnumerable<CostModelUsage> models)
    {
        double total = 0;
        foreach (var model in models)
        {
            if (model?.Usage == null)
            {
                continue;
            }

            foreach (var item in model.Usage)
            {
                if (item?.Amount != null && TryParseAmount(item.Amount, out var amount))
                {
                    total += amount;
                }
            }
        }
        return total;
    }

    private static bool TryParseAmount(string? text, out double value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = 0;
            return false;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetDouble(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
        {
            return true;
        }
        if (element.ValueKind == JsonValueKind.String)
        {
            return TryParseAmount(element.GetString(), out value);
        }
        value = 0;
        return false;
    }

    private static void ThrowIfError(int? code, string? message, string prefix)
    {
        if (code is null || code.Value == 0)
        {
            return;
        }
        if (code.Value is 40002 or 40003)
        {
            throw new DeepSeekException("网页登录已过期，请右键托盘图标重新扫码登录");
        }
        throw new DeepSeekException($"{prefix}：{message ?? ("code " + code.Value)}");
    }

    private sealed class ApiEnvelope<T>
    {
        [JsonPropertyName("code")]
        public int? Code { get; set; }

        [JsonPropertyName("msg")]
        public string? Msg { get; set; }

        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }

    private sealed class BizEnvelope<T>
    {
        [JsonPropertyName("biz_code")]
        public int? BizCode { get; set; }

        [JsonPropertyName("biz_msg")]
        public string? BizMsg { get; set; }

        [JsonPropertyName("biz_data")]
        public T? BizData { get; set; }
    }

    private sealed class UserSummaryBizData
    {
        [JsonPropertyName("normal_wallets")]
        public List<Wallet>? NormalWallets { get; set; }

        [JsonPropertyName("bonus_wallets")]
        public List<Wallet>? BonusWallets { get; set; }
    }

    private sealed class Wallet
    {
        [JsonPropertyName("balance")]
        public JsonElement Balance { get; set; }

        [JsonPropertyName("currency")]
        public string? Currency { get; set; }
    }

    private sealed class AmountData
    {
        [JsonPropertyName("total")]
        public List<ModelUsage>? Total { get; set; }

        [JsonPropertyName("days")]
        public List<DayUsage>? Days { get; set; }
    }

    private sealed class DayUsage
    {
        [JsonPropertyName("date")]
        public string? Date { get; set; }

        [JsonPropertyName("data")]
        public List<ModelUsage>? Data { get; set; }
    }

    private sealed class ModelUsage
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("usage")]
        public List<UsageItem>? Usage { get; set; }
    }

    private sealed class UsageItem
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("amount")]
        public string? Amount { get; set; }
    }

    private sealed class CostBizData
    {
        [JsonPropertyName("total")]
        public List<CostModelUsage>? Total { get; set; }

        [JsonPropertyName("days")]
        public List<CostDayUsage>? Days { get; set; }

        [JsonPropertyName("currency")]
        public string? Currency { get; set; }
    }

    private sealed class CostDayUsage
    {
        [JsonPropertyName("date")]
        public string? Date { get; set; }

        [JsonPropertyName("data")]
        public List<CostModelUsage>? Data { get; set; }
    }

    private sealed class CostModelUsage
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("usage")]
        public List<CostItem>? Usage { get; set; }
    }

    private sealed class CostItem
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("amount")]
        public string? Amount { get; set; }
    }
}
