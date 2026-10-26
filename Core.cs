using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace FuturesWidget {
    public class WatchItem {
        public string Symbol { get; set; }
        public string QuoteAsset { get; set; }
        public string BaseAsset { get; set; }
        public string ContractType { get; set; }
        public decimal? Upper { get; set; }
        public decimal? Lower { get; set; }
        public bool UpperArmed { get; set; }
        public bool LowerArmed { get; set; }
        public bool RapidEnabled { get; set; }
        public int WindowSeconds { get; set; }
        public decimal RapidPercent { get; set; }
        public int CooldownSeconds { get; set; }
        public WatchItem() { Symbol = "BTCUSDT"; RapidEnabled = true; WindowSeconds = 60; RapidPercent = 1; CooldownSeconds = 120; }
        public WatchItem Copy() { return (WatchItem)MemberwiseClone(); }
        public void Validate() {
            if (!System.Text.RegularExpressions.Regex.IsMatch(Symbol ?? "", @"^[\p{L}\p{N}]{2,60}$")) throw new ArgumentException("请输入正确的永续合约代码。");
            if (Upper.HasValue && Upper <= 0 || Lower.HasValue && Lower <= 0) throw new ArgumentException("报警价格必须大于 0。");
            if (Upper.HasValue && Lower.HasValue && Lower >= Upper) throw new ArgumentException("下限必须低于上限。");
            if (WindowSeconds < 10 || WindowSeconds > 3600) throw new ArgumentException("观察时间需在 10～3600 秒之间。");
            if (RapidPercent <= 0 || RapidPercent > 100) throw new ArgumentException("涨跌阈值需大于 0，且不超过 100%。");
            if (CooldownSeconds < 10 || CooldownSeconds > 86400) throw new ArgumentException("冷却时间需在 10～86400 秒之间。");
        }
    }
    public class Preferences {
        public List<WatchItem> Items { get; set; }
        public bool Topmost { get; set; }
        public bool Sound { get; set; }
        public int Version { get; set; }
        public string DockEdge { get; set; }
        public double MiniLeft { get; set; }
        public double MiniTop { get; set; }
        public bool HasMiniPosition { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public Preferences() { Items = new List<WatchItem>(); Topmost = true; Sound = true; DockEdge = "Right"; Left = 100; Top = 100; Width = 380; Height = 320; }
        public static Preferences Defaults() {
            var p = new Preferences();
            foreach (string s in new [] { "BTCUSDT", "ETHUSDT", "SOLUSDT" }) p.Items.Add(new WatchItem { Symbol = s });
            return p;
        }
    }
    public static class Storage {
        public static readonly string DirectoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        public static readonly string ConfigPath = Path.Combine(DirectoryPath, "settings.json");
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }; }
        public static Preferences Load(out string warning) {
            warning = null;
            if (!File.Exists(ConfigPath)) return Preferences.Defaults();
            try {
                var p = Json().Deserialize<Preferences>(File.ReadAllText(ConfigPath));
                if (p == null || p.Items == null || p.Items.Count > 50) throw new InvalidDataException("设置内容无效");
                foreach (var w in p.Items) w.Validate();
                if (p.Items.Select(w => w.Symbol).Distinct().Count() != p.Items.Count) throw new InvalidDataException("交易对重复");
                if (p.Version < 2) { p.Width = 380; p.Height = Math.Min(560, 150 + p.Items.Count * 50); p.Version = 2; }
                if (!new[] { "Left", "Right", "Top", "Bottom" }.Contains(p.DockEdge)) p.DockEdge = "Right";
                return p;
            } catch (Exception ex) {
                warning = "设置无法读取，已使用默认值。原文件已保留：" + ex.Message;
                try { File.Copy(ConfigPath, ConfigPath + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
                return Preferences.Defaults();
            }
        }
        public static void Save(Preferences p) {
            Directory.CreateDirectory(DirectoryPath);
            string temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, Json().Serialize(p), new System.Text.UTF8Encoding(false));
            if (File.Exists(ConfigPath)) File.Replace(temp, ConfigPath, ConfigPath + ".bak");
            else File.Move(temp, ConfigPath);
        }
        public static void Log(string message) {
            try {
                Directory.CreateDirectory(DirectoryPath);
                string path = Path.Combine(DirectoryPath, "events.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) { File.Copy(path, path + ".previous", true); File.WriteAllText(path, ""); }
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            } catch { }
        }
    }
    public class AlertEvent {
        public string Kind, Message;
        public AlertEvent(string kind, string message) { Kind = kind; Message = message; }
    }
    public class Sample {
        public long Time; public decimal Price;
        public Sample(long t, decimal p) { Time = t; Price = p; }
    }
    // Owned by the UI thread; exchange event time in milliseconds.
    public class AlertEngine {
        readonly LinkedList<Sample> history = new LinkedList<Sample>();
        long lastTime = -1;
        long lastUp = long.MinValue / 2, lastDown = long.MinValue / 2;
        bool upActive, downActive;
        public decimal? Change { get; private set; }
        public int CollectedSeconds { get; private set; }
        public void ResetHistory() { history.Clear(); lastTime = -1; Change = null; CollectedSeconds = 0; upActive = downActive = false; }
        public List<AlertEvent> Push(WatchItem rule, decimal price, long time) {
            var events = new List<AlertEvent>();
            if (price <= 0 || time <= lastTime) return events;
            if (lastTime >= 0 && time - lastTime > 15000) ResetHistory();
            lastTime = time;
            if (rule.UpperArmed && rule.Upper.HasValue && price >= rule.Upper.Value) {
                rule.UpperArmed = false;
                events.Add(new AlertEvent("upper", "价格达到上限 " + FormatPrice(rule.Upper.Value) + "，现价 " + FormatPrice(price)));
            }
            if (rule.LowerArmed && rule.Lower.HasValue && price <= rule.Lower.Value) {
                rule.LowerArmed = false;
                events.Add(new AlertEvent("lower", "价格达到下限 " + FormatPrice(rule.Lower.Value) + "，现价 " + FormatPrice(price)));
            }
            history.AddLast(new Sample(time, price));
            long cutoff = time - rule.WindowSeconds * 1000L;
            while (history.Count > 1 && history.First.Next.Value.Time <= cutoff) history.RemoveFirst();
            CollectedSeconds = (int)((time - history.First.Value.Time) / 1000);
            Change = null;
            if (history.First.Value.Time <= cutoff && cutoff - history.First.Value.Time <= 15000) {
                Change = (price / history.First.Value.Price - 1) * 100;
                bool up = Change.Value >= rule.RapidPercent, down = Change.Value <= -rule.RapidPercent;
                if (rule.RapidEnabled && up && !upActive && time - lastUp >= rule.CooldownSeconds * 1000L) {
                    lastUp = time; events.Add(new AlertEvent("rapid-up", rule.WindowSeconds + " 秒内快速上涨 " + Change.Value.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + "%"));
                }
                if (rule.RapidEnabled && down && !downActive && time - lastDown >= rule.CooldownSeconds * 1000L) {
                    lastDown = time; events.Add(new AlertEvent("rapid-down", rule.WindowSeconds + " 秒内快速下跌 " + Change.Value.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + "%"));
                }
                if (up) upActive = true; else if (Change.Value < rule.RapidPercent * .8m) upActive = false;
                if (down) downActive = true; else if (Change.Value > -rule.RapidPercent * .8m) downActive = false;
            }
            return events;
        }
        public static string FormatPrice(decimal p) { return p.ToString("#,0.########", CultureInfo.InvariantCulture); }
        public static string ChartUrl(string symbol) { return "https://www.tradingview.com/chart/?symbol=" + Uri.EscapeDataString("BINANCE:" + symbol + ".P"); }
    }
}
