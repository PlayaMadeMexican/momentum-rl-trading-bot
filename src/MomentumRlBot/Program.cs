using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

const int Lookback = 252;
const int SkipDays = 21;
const int PortfolioSize = 5;
var key = Required("ALPACA_API_KEY");
var secret = Required("ALPACA_API_SECRET");
var live = Bool("ALPACA_LIVE", false);
var dryRun = Bool("DRY_RUN", true);
var useRl = Bool("USE_RL", false);
var symbols = (Environment.GetEnvironmentVariable("SYMBOLS") ?? "AAPL,MSFT,NVDA,AMZN,META,GOOGL,AVGO,AMD,TSLA")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
var pollMinutes = Int("POLL_MINUTES", 15);
var api = new AlpacaClient(key, secret, live);
var agent = new ExposureAgent(useRl);
var lastRebalanceMonth = "";

Console.WriteLine($"Momentum RL bot started. endpoint={(live ? "LIVE" : "PAPER")}, dryRun={dryRun}, symbols={symbols.Length}");
if (live && dryRun) Console.WriteLine("LIVE endpoint selected, but DRY_RUN remains enabled; no orders will be submitted.");

while (true)
{
    try
    {
        var clock = await api.GetClock();
        var month = clock.Timestamp.ToString("yyyy-MM");
        if (clock.IsOpen && month != lastRebalanceMonth && clock.Timestamp.Day <= 7)
        {
            await Rebalance(api, agent, symbols, dryRun, useRl);
            lastRebalanceMonth = month;
        }
        await Task.Delay(TimeSpan.FromMinutes(pollMinutes));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{DateTimeOffset.UtcNow:u} ERROR: {ex.Message}");
        await Task.Delay(TimeSpan.FromMinutes(pollMinutes));
    }
}

static async Task Rebalance(AlpacaClient api, ExposureAgent agent, string[] symbols, bool dryRun, bool useRl)
{
    var spy = await api.GetDailyBars("SPY", Lookback + 5);
    var state = spy.Count >= Lookback && spy[^1] >= spy.TakeLast(Lookback).Average() ? 1 : 0;
    var action = agent.Select(state);
    var gross = action switch { 0 => 1m, 1 => .5m, _ => 0m };
    Console.WriteLine($"{DateTimeOffset.UtcNow:u} state={(state == 1 ? "UP" : "DOWN")}, action={agent.Name(action)}, gross={gross:P0}");

    var ranked = new List<(string Symbol, decimal Momentum)>();
    foreach (var symbol in symbols)
    {
        var closes = await api.GetDailyBars(symbol, Lookback + SkipDays + 5);
        if (closes.Count > Lookback + SkipDays)
        {
            var momentum = closes[^1 - SkipDays] / closes[^1 - Lookback] - 1m;
            if (momentum > 0) ranked.Add((symbol, momentum));
        }
    }
    var book = ranked.OrderByDescending(x => x.Momentum).Take(PortfolioSize).ToArray();
    Console.WriteLine("Book: " + (book.Length == 0 ? "empty" : string.Join(", ", book.Select(x => $"{x.Symbol} {x.Momentum:P1}"))));

    if (dryRun) { Console.WriteLine("DRY_RUN: no orders submitted."); return; }
    await api.CancelOpenOrders();
    await api.LiquidatePositions();
    if (gross == 0 || book.Length == 0) return;
    var equity = await api.GetEquity();
    foreach (var item in book)
    {
        var dollars = equity * gross / book.Length;
        await api.SubmitMarketBuy(item.Symbol, dollars);
    }
    Console.WriteLine("Orders submitted.");
}

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new InvalidOperationException($"Missing {name}");
static bool Bool(string name, bool fallback) => bool.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
static int Int(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

sealed class AlpacaClient
{
    readonly HttpClient trading = new();
    readonly HttpClient data = new();
    readonly JsonSerializerOptions json = new() { PropertyNameCaseInsensitive = true };
    public AlpacaClient(string key, string secret, bool live)
    {
        var tradeBase = live ? "https://api.alpaca.markets" : "https://paper-api.alpaca.markets";
        trading.BaseAddress = new Uri(tradeBase);
        data.BaseAddress = new Uri("https://data.alpaca.markets");
        foreach (var client in new[] { trading, data })
        {
            client.DefaultRequestHeaders.Add("APCA-API-KEY-ID", key);
            client.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", secret);
        }
    }
    public async Task<Clock> GetClock() => await Get<Clock>(trading, "/v2/clock");
    public async Task<decimal> GetEquity() => decimal.Parse((await Get<Account>(trading, "/v2/account")).Equity, System.Globalization.CultureInfo.InvariantCulture);
    public async Task<List<decimal>> GetDailyBars(string symbol, int limit)
    {
        var end = DateTimeOffset.UtcNow.Date.AddDays(-1).ToString("O");
        var url = $"/v2/stocks/{symbol}/bars?timeframe=1Day&limit={limit}&end={Uri.EscapeDataString(end)}&adjustment=all&feed=iex&sort=asc";
        var result = await Get<BarsResponse>(data, url);
        return result.Bars.Select(x => x.Close).ToList();
    }
    public async Task CancelOpenOrders() => await Send(trading, HttpMethod.Delete, "/v2/orders");
    public async Task LiquidatePositions() => await Send(trading, HttpMethod.Delete, "/v2/positions");
    public async Task SubmitMarketBuy(string symbol, decimal dollars)
    {
        var body = JsonSerializer.Serialize(new { symbol, notional = Math.Round(dollars, 2), side = "buy", type = "market", time_in_force = "day" });
        await Send(trading, HttpMethod.Post, "/v2/orders", body);
    }
    async Task<T> Get<T>(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path); var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new Exception($"GET {path}: {(int)response.StatusCode} {body}");
        return JsonSerializer.Deserialize<T>(body, json)!;
    }
    async Task Send(HttpClient client, HttpMethod method, string path, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request); var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new Exception($"{method} {path}: {(int)response.StatusCode} {text}");
    }
}

sealed record Clock(bool IsOpen, DateTimeOffset Timestamp);
sealed record Account(string Equity);
sealed record BarsResponse(List<Bar> Bars);
sealed record Bar(decimal Close);

sealed class ExposureAgent
{
    readonly decimal[,] q = new decimal[2, 3]; readonly Random random = new(42); readonly bool enabled; int month;
    public ExposureAgent(bool enabled) => this.enabled = enabled;
    public int Select(int state)
    {
        if (!enabled) return 0;
        var epsilon = Math.Max(.05, .35 * Math.Pow(.95, month++));
        if (random.NextDouble() < epsilon) return random.Next(3);
        return Enumerable.Range(0, 3).OrderByDescending(a => q[state, a]).First();
    }
    public string Name(int action) => action == 0 ? "FULL" : action == 1 ? "HALF" : "CASH";
}
