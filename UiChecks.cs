using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace FuturesWidget {
    public partial class Widget {
        static void AssertUi(bool result, string label, List<string> report) {
            if (!result) throw new Exception(label);
            report.Add("PASS " + label);
        }
        static RenderTargetBitmap Render(Window window, string path) {
            window.UpdateLayout();
            int width = (int)Math.Ceiling(window.ActualWidth), height = (int)Math.Ceiling(window.ActualHeight);
            if (width <= 0 || height <= 0) throw new Exception("Empty layout");
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path)) encoder.Save(output);
            return bitmap;
        }
        static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T) yield return (T)child;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }
        public static int UiCheck() {
            var report = new List<string>();
            var destination = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification");
            Directory.CreateDirectory(destination);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Widget ui = null;
            try {
                ui = new Widget(true); app.MainWindow = ui.Window;
                ui.Window.ShowInTaskbar = false; ui.Window.ShowActivated = false;
                ui.Window.Left = -20000; ui.Window.Top = -20000; ui.Window.Show(); ui.Window.UpdateLayout();
                ui.connected = true; ui.settings.Sound = false;
                ui.settings.Items.Add(new WatchItem { Symbol = "BTCUSDC", QuoteAsset = "USDC" });
                ui.settings.Items.Add(new WatchItem { Symbol = "GOOGLUSDT", QuoteAsset = "USDT", ContractType = "TRADIFI_PERPETUAL" });
                ui.BuildRows(); ui.Window.Height = 400;
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string[] symbols = { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BTCUSDC", "GOOGLUSDT" };
                decimal[] prices = { 79952.30m, 2372.37m, 102.05m, 79951.20m, 319.92m };
                for (int i = 0; i < symbols.Length; i++) ui.ticks.Enqueue(new Tick { Symbol = symbols[i], Price = prices[i], Percent = i % 2 == 0 ? -1.24m : 2.31m, Time = now, Received = DateTime.UtcNow });
                ui.Drain(); ui.status.Text = "界面验证 · 示例行情";
                AssertUi(ui.quotes.Count == 5 && ui.quotes.Values.All(r => r.LastPrice > 0), "Crypto USDC and TradFi rows rendered", report);
                AssertUi(ui.Window.FindName("OpacitySlider") == null && ui.Window.Opacity == 1, "Old opacity control removed", report);
                AssertUi(ui.Window.Width == 380, "Expanded width reduced to 380", report);
                Render(ui.Window, Path.Combine(destination, "main-preview-v2.png"));

                ui.CollapseToMini(); ui.mini.Left = -20000; ui.mini.Top = -20000; ui.mini.UpdateLayout();
                AssertUi(ui.dock.Mode == DisplayMode.Mini && !ui.Window.IsVisible && ui.mini.IsVisible, "Collapsed mode hides full window", report);
                AssertUi(ui.mini.AllowsTransparency && ui.mini.Opacity == 1 && ((SolidColorBrush)ui.mini.Background).Color.A == 0, "Mini uses native per-pixel transparency", report);
                var miniImage = Render(ui.mini, Path.Combine(destination, "mini-vertical-v2.png"));
                var pixels = new byte[miniImage.PixelWidth * miniImage.PixelHeight * 4]; miniImage.CopyPixels(pixels, miniImage.PixelWidth * 4, 0);
                AssertUi(pixels.Where((v, i) => i % 4 == 3).Any(a => a > 0 && a < 200), "Rendered background contains true alpha", report);
                AssertUi(Math.Abs(ui.mini.Width - 124) < 1 && Math.Abs(ui.mini.Height - 233) < 1, "Five quote vertical layout is compact", report);
                AssertUi(ui.miniPrices["GOOGLUSDT"].Text == "319.92", "Mini receives TradFi price", report);
                ui.ShowPeek();
                AssertUi(ui.dock.Mode == DisplayMode.Peek && ui.Window.IsVisible && !ui.Window.ShowActivated, "Hover preview does not activate window", report);
                ui.dock.Leave(); ui.Window.Hide();
                AssertUi(ui.dock.Mode == DisplayMode.Mini, "Preview leave returns to mini", report);
                ui.ExpandMain(); ui.dock.Leave();
                AssertUi(ui.dock.Mode == DisplayMode.Expanded && ui.Window.IsVisible && !ui.mini.IsVisible, "Click keeps full window expanded", report);
                ui.settings.DockEdge = "Top"; ui.BuildMini(); ui.CollapseToMini(); ui.mini.Left = -20000; ui.mini.Top = -20000;
                AssertUi(Math.Abs(ui.mini.Height - 53) < 1 && ui.miniTiles.Orientation == Orientation.Horizontal, "Top dock uses horizontal tiles", report);
                Render(ui.mini, Path.Combine(destination, "mini-horizontal-v2.png"));
                ui.ExpandMain(); ui.Window.Left = -20000; ui.Window.Top = -20000;
                ui.pin.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                AssertUi(!ui.Window.Topmost && !ui.mini.Topmost, "Pin setting updates both windows", report);
                ui.pin.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

                Exception modalFailure = null;
                var capture = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                capture.Tick += (s, e) => {
                    capture.Stop();
                    var dialog = app.Windows.Cast<Window>().FirstOrDefault(w => w.Title.Contains("提醒设置"));
                    try {
                        AssertUi(dialog != null, "Alert dialog opens", report);
                        AssertUi(Descendants<TextBlock>(dialog).Any(t => t.Text.Contains("价格上限（USDC")), "USDC alert shows correct unit", report);
                        Render(dialog, Path.Combine(destination, "settings-preview-v2.png"));
                        var fields = Descendants<TextBox>(dialog).ToArray();
                        fields[0].Text = "90000";
                        Descendants<Button>(dialog).First(b => Convert.ToString(b.Content) == "保存").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        AssertUi(ui.settings.Items.First(x => x.Symbol == "BTCUSDC").UpperArmed, "USDC target saves and arms", report);
                    } catch (Exception ex) { modalFailure = ex; if (dialog != null) dialog.Close(); }
                };
                capture.Start(); ui.EditDialog(ui.quotes["BTCUSDC"]); if (modalFailure != null) throw modalFailure;
                ui.contracts = new Dictionary<string, ContractInfo>();
                foreach (var info in new [] {
                    new ContractInfo { Symbol = "BNBUSDC", BaseAsset = "BNB", QuoteAsset = "USDC", ContractType = "PERPETUAL" },
                    new ContractInfo { Symbol = "XAUUSDT", BaseAsset = "XAU", QuoteAsset = "USDT", ContractType = "TRADIFI_PERPETUAL", UnderlyingType = "COMMODITY" },
                    new ContractInfo { Symbol = "TSLAUSDT", BaseAsset = "TSLA", QuoteAsset = "USDT", ContractType = "TRADIFI_PERPETUAL", UnderlyingType = "EQUITY" }
                }) ui.contracts[info.Symbol] = info;
                ui.catalog = new HashSet<string>(ui.contracts.Keys);
                var addCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                addCheck.Tick += (s, e) => {
                    addCheck.Stop();
                    var dialog = app.Windows.Cast<Window>().FirstOrDefault(w => w.Title == "添加永续合约");
                    try {
                        var list = Descendants<ListBox>(dialog).Single();
                        AssertUi(list.Items.Count == 3, "All catalog categories available", report);
                        Descendants<Button>(dialog).First(b => Convert.ToString(b.Content) == "传统金融").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        AssertUi(list.Items.Count == 2 && list.Items.Cast<ContractInfo>().All(c => c.TradFi), "TradFi category filters correctly", report);
                        Render(dialog, Path.Combine(destination, "add-preview-v2.png"));
                        Descendants<Button>(dialog).First(b => Convert.ToString(b.Content) == "USDC").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        AssertUi(list.Items.Count == 1 && ((ContractInfo)list.Items[0]).Symbol == "BNBUSDC", "USDC category filters correctly", report);
                        list.SelectedIndex = 0;
                        Descendants<Button>(dialog).First(b => Convert.ToString(b.Content) == "添加").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        AssertUi(ui.settings.Items.Any(x => x.Symbol == "BNBUSDC" && x.QuoteAsset == "USDC"), "Add USDC stores symbol metadata", report);
                    } catch (Exception ex) { modalFailure = ex; if (dialog != null) dialog.Close(); }
                };
                addCheck.Start(); ui.AddDialog(); if (modalFailure != null) throw modalFailure;
                ui.Notify("BTCUSDC", "界面验证：这是一条测试提醒。");
                ui.toast.UpdateLayout();
                Render(ui.toast, Path.Combine(destination, "alert-preview-v2.png"));
                AssertUi(ui.toast.ActualHeight >= 150, "Alert popup has sufficient height", report);
                ui.toast.Close();
                ui.quotes["BTCUSDT"].LastReceived = DateTime.UtcNow.AddSeconds(-30); ui.Drain();
                AssertUi(ui.quotes["BTCUSDT"].Stale, "Expired quotes still marked stale", report);
                var quiet = ui.quotes["GOOGLUSDT"]; quiet.Rule.Upper = 1; quiet.Rule.UpperArmed = true; quiet.Stale = true;
                ui.ticks.Enqueue(new Tick { Symbol = "GOOGLUSDT", Price = 320m, Percent = 1, Time = now, Received = DateTime.UtcNow, IsSnapshot = true });
                ui.Drain();
                AssertUi(quiet.LastPrice == 320m && quiet.Stale && quiet.Rule.UpperArmed && quiet.LastSnapshot != DateTime.MinValue, "Snapshot fills price without firing alerts or claiming live status", report);
                report.Add("ALL UI CHECKS PASSED"); File.WriteAllLines(Path.Combine(destination, "ui-checks-v2.txt"), report); return 0;
            } catch (Exception ex) {
                report.Add("FAIL " + ex); File.WriteAllLines(Path.Combine(destination, "ui-checks-v2.txt"), report); return 1;
            } finally { if (ui != null) ui.Window.Close(); app.Shutdown(); }
        }
    }
}
