using System;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using FuturesWidget;
class Tests {
    static int total;
    static void Check(bool ok, string label) { total++; if (!ok) throw new Exception("FAILED: " + label); Console.WriteLine("PASS " + label); }
    static void Invalid(Action a, string label) { bool threw = false; try { a(); } catch (ArgumentException) { threw = true; } Check(threw, label); }
    static List<AlertEvent> Fill(AlertEngine e, WatchItem r, int begin, int end, decimal p) {
        var all = new List<AlertEvent>(); for (int t = begin; t <= end; t++) all.AddRange(e.Push(r, p, t * 1000L)); return all;
    }
    static int Main() {
        try {
            var r = new WatchItem { Upper = 100, UpperArmed = true, Lower = 90, LowerArmed = true };
            var e = new AlertEngine();
            Check(e.Push(r, 99, 0).Count == 0, "no premature target alert");
            Check(e.Push(r, 100, 1000).Single().Kind == "upper" && !r.UpperArmed, "inclusive upper and one shot");
            Check(e.Push(r, 101, 2000).Count == 0, "upper does not repeat");
            Check(e.Push(r, 90, 3000).Single().Kind == "lower" && !r.LowerArmed, "inclusive lower");
            r.UpperArmed = true;
            Check(e.Push(r, 110, 2500).Count == 0 && r.UpperArmed, "out of order tick ignored");
            Check(e.Push(r, 110, 4000).Single().Kind == "upper", "manual rearm");
            e = new AlertEngine(); r = new WatchItem { Upper = 100, UpperArmed = true };
            Check(e.Push(r, 120, 0).Count == 1, "first quote already past target");
            e = new AlertEngine(); r = new WatchItem();
            Fill(e, r, 0, 58, 100);
            Check(e.Push(r, 102, 59000).Count == 0 && !e.Change.HasValue, "warmup required");
            Check(e.Push(r, 102, 60000).Single().Kind == "rapid-up" && e.Change == 2, "rolling upward move");
            Check(Fill(e, r, 61, 70, 102).Count == 0, "persistent breach no flood");
            Check(e.Push(r, 98, 71000).Single().Kind == "rapid-down", "down direction independent");
            Fill(e, r, 72, 75, 100);
            Check(e.Push(r, 102, 76000).Count == 0, "same direction cooldown");
            e = new AlertEngine(); r = new WatchItem { WindowSeconds = 10, CooldownSeconds = 10 };
            Fill(e, r, 0, 9, 100); Check(e.Push(r, 102, 10000).Count == 1, "short window works");
            Fill(e, r, 11, 20, 100); Check(e.Push(r, 102, 21000).Count == 1, "rearm after recovery and cooldown");
            e = new AlertEngine(); r = new WatchItem();
            Fill(e, r, 0, 60, 100);
            Check(e.Push(r, 120, 80000).Count == 0 && !e.Change.HasValue, "gap clears rapid baseline");
            Fill(e, r, 81, 140, 120);
            Check(e.Change == 0, "new complete window after gap");
            e.ResetHistory(); Check(!e.Change.HasValue && e.CollectedSeconds == 0, "reconnect clears history");
            e = new AlertEngine(); r = new WatchItem { RapidEnabled = false };
            Fill(e, r, 0, 59, 100); Check(e.Push(r, 110, 60000).Count == 0, "disabled rapid alert");
            Invalid(() => new WatchItem { Upper = 90, Lower = 100 }.Validate(), "inverted limits rejected");
            Invalid(() => new WatchItem { WindowSeconds = 0 }.Validate(), "zero window rejected");
            Invalid(() => new WatchItem { Symbol = "../INVALID" }.Validate(), "invalid symbol rejected");
            Invalid(() => new WatchItem { RapidPercent = 0 }.Validate(), "zero threshold rejected");
            Check(AlertEngine.ChartUrl("BTCUSDT") == "https://www.tradingview.com/chart/?symbol=BINANCE%3ABTCUSDT.P", "perpetual TradingView mapping");
            var tick = Market.Parse("{\"stream\":\"btcusdt@miniTicker\",\"data\":{\"s\":\"BTCUSDT\",\"c\":\"102.5\",\"o\":\"100\",\"E\":12345,\"st\":1}}");
            Check(tick.Symbol == "BTCUSDT" && tick.Price == 102.5m && tick.Percent == 2.5m && tick.Time == 12345, "combined stream parser");
            Check(Market.Parse("{\"s\":\"BTCUSD_PERP\",\"c\":\"100\",\"o\":\"100\",\"E\":12345,\"st\":2}") == null, "coin margined rejected");
            Check(Market.Parse("{\"result\":null,\"id\":1}") == null, "subscription response ignored");
            var p = Preferences.Defaults(); p.Items[0].Upper = 123m; p.Items[0].UpperArmed = false;
            var restored = Storage.Json().Deserialize<Preferences>(Storage.Json().Serialize(p));
            Check(restored.Items.Count == 3 && restored.Items[0].Upper == 123m && !restored.Items[0].UpperArmed, "settings roundtrip preserves disarmed alerts");
            new WatchItem { Symbol = "TUSDT" }.Validate(); new WatchItem { Symbol = "币安人生USDT" }.Validate(); Check(true, "single letter and Unicode symbols accepted");
            var mode = new DockState();
            Check(mode.Mode == DisplayMode.Mini, "starts in mini mode");
            mode.Hover(); Check(mode.Mode == DisplayMode.Peek, "hover peeks");
            mode.Leave(); Check(mode.Mode == DisplayMode.Mini, "leave collapses preview");
            mode.Hover(); mode.Expand(); mode.Leave(); Check(mode.Mode == DisplayMode.Expanded, "clicked preview persists after leave");
            mode.Collapse(); Check(mode.Mode == DisplayMode.Mini, "explicit collapse");
            new WatchItem { Symbol = "BTCUSDC", QuoteAsset = "USDC" }.Validate();
            Check(ContractInfo.Quote(new WatchItem { Symbol = "BTCUSDC" }) == "USDC", "quote fallback for USDC");
            Check(AlertEngine.ChartUrl("GOOGLUSDT").EndsWith("BINANCE%3AGOOGLUSDT.P"), "TradFi chart mapping");
            Check(AlertEngine.ChartUrl("BTCUSDC").EndsWith("BINANCE%3ABTCUSDC.P"), "USDC chart mapping");
            var stock = new ContractInfo { Symbol = "GOOGLUSDT", BaseAsset = "GOOGL", QuoteAsset = "USDT", ContractType = "TRADIFI_PERPETUAL", UnderlyingType = "EQUITY" };
            Check(ContractInfo.Matches(stock, "谷歌", "传统金融"), "Chinese TradFi search");
            Check(!ContractInfo.Matches(stock, "", "USDC"), "quote filter excludes USDT");
            var fixturePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tests", "exchange-info.json");
            var catalog = Market.ParseCatalog(Storage.Json().Deserialize<Dictionary<string, object>>(System.IO.File.ReadAllText(fixturePath)));
            Check(catalog.ContainsKey("BTCUSDC") && catalog.ContainsKey("GOOGLUSDT") && catalog.ContainsKey("XAUUSDT"), "catalog includes USDC stock and gold");
            Check(!catalog.ContainsKey("BTCUSDT_261225") && catalog.Values.All(x => x.ContractType == "PERPETUAL" || x.TradFi), "catalog excludes dated futures");
            Check(catalog.Values.Any(x => x.QuoteAsset == "USD1"), "additional perpetual quote currencies supported");
            Check(!catalog.ContainsKey("HALTEDUSDT") && catalog.Count == 5, "catalog excludes non-trading contracts");
            Console.WriteLine("ALL " + total + " TESTS PASSED"); return 0;
        } catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
