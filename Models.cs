using System;
using System.Collections.Generic;
using System.Linq;
namespace FuturesWidget {
    public enum DisplayMode { Mini, Peek, Expanded }
    public sealed class DockState {
        public DisplayMode Mode { get; private set; }
        public DockState() { Mode = DisplayMode.Mini; }
        public void Hover() { if (Mode == DisplayMode.Mini) Mode = DisplayMode.Peek; }
        public void Leave() { if (Mode == DisplayMode.Peek) Mode = DisplayMode.Mini; }
        public void Expand() { Mode = DisplayMode.Expanded; }
        public void Collapse() { Mode = DisplayMode.Mini; }
    }
    public class ContractInfo {
        public string Symbol, BaseAsset, QuoteAsset, ContractType, UnderlyingType;
        public bool TradFi { get { return ContractType == "TRADIFI_PERPETUAL"; } }
        public string Category {
            get {
                if (!TradFi) return "加密";
                switch (UnderlyingType) {
                    case "EQUITY": return "美股";
                    case "HK_EQUITY": return "港股";
                    case "KR_EQUITY": return "韩股";
                    case "CN_EQUITY": return "A股";
                    case "COMMODITY": return "商品";
                    case "INDEX": return "指数";
                    default: return "传统金融";
                }
            }
        }
        public override string ToString() { return Symbol + "    " + Category + " · " + QuoteAsset; }
        public void Apply(WatchItem item) { item.BaseAsset = BaseAsset; item.QuoteAsset = QuoteAsset; item.ContractType = ContractType; }
        public static string Quote(WatchItem item) {
            if (!string.IsNullOrEmpty(item.QuoteAsset)) return item.QuoteAsset;
            foreach (var q in new [] { "USDT", "USDC", "USD1", "BTC", "U" }) if (item.Symbol.EndsWith(q, StringComparison.Ordinal)) return q;
            return "";
        }
        public static bool Matches(ContractInfo c, string query, string category) {
            if (category == "USDT" && c.QuoteAsset != "USDT" || category == "USDC" && c.QuoteAsset != "USDC" || category == "传统金融" && !c.TradFi) return false;
            return string.IsNullOrEmpty(query) || (c.Symbol + " " + c.BaseAsset + " " + c.Category + " " + Alias(c.BaseAsset)).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        static string Alias(string asset) {
            switch (asset) {
                case "XAU": return "黄金 gold";
                case "XAG": return "白银 silver";
                case "TSLA": return "特斯拉 tesla";
                case "GOOGL": return "谷歌 google alphabet";
                case "AAPL": return "苹果 apple";
                case "NVDA": return "英伟达 nvidia";
                case "MSFT": return "微软 microsoft";
                case "AMZN": return "亚马逊 amazon";
                case "COPPER": return "铜 copper";
                default: return "";
            }
        }
    }
}
