using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FuturesWidget {
    public class Tick {
        public string Symbol;
        public decimal Price, Percent;
        public long Time;
        public DateTime Received;
        public bool IsSnapshot;
    }
    public static class Market {
        public const string RestUrl = "https://fapi.binance.com/fapi/v1/exchangeInfo";
        public const string StreamUrl = "wss://fstream.binance.com/market/stream?streams=";
        public static async Task<HashSet<string>> Catalog(CancellationToken token) { return new HashSet<string>((await CatalogInfo(token).ConfigureAwait(false)).Keys); }
        public static async Task<Dictionary<string, ContractInfo>> CatalogInfo(CancellationToken token) {
            var request = (HttpWebRequest)WebRequest.Create(RestUrl);
            request.UserAgent = "FuturesWidget/1.0";
            request.Timeout = 15000;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                timeout.CancelAfter(15000);
                using (timeout.Token.Register(() => request.Abort()))
                using (var response = await request.GetResponseAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(response.GetResponseStream())) {
                    var root = Storage.Json().Deserialize<Dictionary<string, object>>(await reader.ReadToEndAsync().ConfigureAwait(false));
                    var result = ParseCatalog(root);
                    return result;
                }
            }
        }
        public static Dictionary<string, ContractInfo> ParseCatalog(Dictionary<string, object> root) {
            var result = new Dictionary<string, ContractInfo>();
            foreach (var raw in (System.Collections.IEnumerable)root["symbols"]) {
                var s = (Dictionary<string, object>)raw;
                string type = Convert.ToString(s["contractType"]);
                if ((type != "PERPETUAL" && type != "TRADIFI_PERPETUAL") || Convert.ToString(s["status"]) != "TRADING") continue;
                var info = new ContractInfo { Symbol = Convert.ToString(s["symbol"]), BaseAsset = Convert.ToString(s["baseAsset"]),
                    QuoteAsset = Convert.ToString(s["quoteAsset"]), ContractType = type,
                    UnderlyingType = s.ContainsKey("underlyingType") ? Convert.ToString(s["underlyingType"]) : "" };
                result[info.Symbol] = info;
            }
            if (result.Count == 0) throw new InvalidDataException("没有获取到可交易的永续合约");
            return result;
        }
        public static async Task<Tick> Snapshot(string symbol, CancellationToken token) {
            var request = (HttpWebRequest)WebRequest.Create("https://fapi.binance.com/fapi/v1/ticker/24hr?symbol=" + Uri.EscapeDataString(symbol));
            request.UserAgent = "PerpWatch/2.0";
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                timeout.CancelAfter(10000);
                using (timeout.Token.Register(() => request.Abort()))
                using (var response = await request.GetResponseAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(response.GetResponseStream())) {
                    var data = Storage.Json().Deserialize<Dictionary<string, object>>(await reader.ReadToEndAsync().ConfigureAwait(false));
                    decimal price = decimal.Parse(Convert.ToString(data["lastPrice"]), CultureInfo.InvariantCulture);
                    if (price <= 0) throw new InvalidDataException("价格暂不可用");
                    return new Tick { Symbol = symbol, Price = price,
                        Percent = decimal.Parse(Convert.ToString(data["priceChangePercent"]), CultureInfo.InvariantCulture),
                        Time = Convert.ToInt64(data["closeTime"]), Received = DateTime.UtcNow, IsSnapshot = true };
                }
            }
        }
        public static async Task Snapshots(string[] symbols, Action<Tick> onTick, CancellationToken token) {
            using (var limit = new SemaphoreSlim(4)) {
                await Task.WhenAll(symbols.Select(async symbol => {
                    await limit.WaitAsync(token).ConfigureAwait(false);
                    try { onTick(await Snapshot(symbol, token).ConfigureAwait(false)); }
                    catch (Exception ex) { if (!token.IsCancellationRequested) Storage.Log(symbol + " 价格快照：" + ex.Message); }
                    finally { limit.Release(); }
                })).ConfigureAwait(false);
            }
        }
        public static Tick Parse(string json) {
            var root = Storage.Json().Deserialize<Dictionary<string, object>>(json);
            object data;
            if (root.TryGetValue("data", out data)) root = (Dictionary<string, object>)data;
            object type;
            if (!root.ContainsKey("c") || !root.ContainsKey("o") || !root.ContainsKey("E") || !root.ContainsKey("s")) return null;
            if (root.TryGetValue("st", out type) && Convert.ToInt32(type) != 1) return null;
            decimal price = decimal.Parse(Convert.ToString(root["c"]), CultureInfo.InvariantCulture);
            decimal open = decimal.Parse(Convert.ToString(root["o"]), CultureInfo.InvariantCulture);
            if (price <= 0 || open <= 0) return null;
            return new Tick { Symbol = Convert.ToString(root["s"]), Price = price, Percent = (price / open - 1) * 100,
                Time = Convert.ToInt64(root["E"]), Received = DateTime.UtcNow };
        }
        public static async Task Run(string[] symbols, Action<Tick> onTick, Action<string, bool> status, CancellationToken token) {
            int backoff = 1;
            while (!token.IsCancellationRequested) {
                try {
                    status("正在连接币安行情…", false);
                    using (var ws = new ClientWebSocket()) {
                        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                        // Use the Windows system proxy, if one is configured.
                        ws.Options.Proxy = WebRequest.DefaultWebProxy;
                        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                            connecting.CancelAfter(15000);
                            await ws.ConnectAsync(new Uri(StreamUrl + string.Join("/", symbols.Select(s => s.ToLowerInvariant() + "@miniTicker"))), connecting.Token).ConfigureAwait(false);
                        }
                        status("已连接 · 等待行情", true);
                        var buffer = new byte[8192];
                        while (ws.State == WebSocketState.Open && !token.IsCancellationRequested) {
                            using (var message = new MemoryStream())
                            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                                timeout.CancelAfter(20000);
                                WebSocketReceiveResult received;
                                do {
                                    received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false);
                                    if (received.MessageType == WebSocketMessageType.Close) throw new IOException("服务器断开连接");
                                    message.Write(buffer, 0, received.Count);
                                    if (message.Length > 1024 * 1024) throw new IOException("行情消息过大");
                                } while (!received.EndOfMessage);
                                if (received.MessageType != WebSocketMessageType.Text) continue;
                                var tick = Parse(Encoding.UTF8.GetString(message.ToArray()));
                                if (tick != null && symbols.Contains(tick.Symbol)) { onTick(tick); backoff = 1; }
                            }
                        }
                    }
                } catch (Exception ex) {
                    if (token.IsCancellationRequested) return;
                    Storage.Log("行情连接异常：" + ex.Message);
                }
                if (token.IsCancellationRequested) return;
                status("连接中断 · " + backoff + " 秒后重试", false);
                try { await Task.Delay(backoff * 1000, token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                backoff = Math.Min(30, backoff * 2);
            }
        }
    }
}
