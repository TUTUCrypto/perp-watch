using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace FuturesWidget {
    public static class Program {
        [STAThread]
        public static int Main(string[] args) {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            if (args.Contains("--ui-test")) return Widget.UiCheck();
            if (args.Contains("--probe")) return Probe.Run().GetAwaiter().GetResult();
            bool created;
            using (var mutex = new Mutex(true, "Local\\BinanceFuturesWidget", out created)) {
                if (!created) { MessageBox.Show("浮窗已经在运行，请双击系统托盘中的图标打开。", "币安合约浮窗"); return 0; }
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (s, e) => { Storage.Log(e.Exception.ToString()); MessageBox.Show("发生错误：" + e.Exception.Message, "币安合约浮窗"); e.Handled = true; };
                try { var ui = new Widget(); app.MainWindow = ui.Window; ui.Start(); app.Run(); }
                catch (Exception ex) { Storage.Log(ex.ToString()); MessageBox.Show(ex.Message, "浮窗启动失败"); return 1; }
            }
            return 0;
        }
    }
    public class QuoteRow {
        public WatchItem Rule;
        public AlertEngine Engine = new AlertEngine();
        public TextBlock Price, Percent, Detail;
        public Border Card;
        public DateTime LastReceived = DateTime.MinValue;
        public DateTime LastSnapshot = DateTime.MinValue;
        public long LastEvent = -1;
        public decimal LastPrice, LastPercent;
        public bool Stale = true;
    }
    public partial class Widget {
        public Window Window;
        Preferences settings;
        StackPanel rows;
        TextBlock status, recent;
        Button pin, sound;
        Dictionary<string, QuoteRow> quotes = new Dictionary<string, QuoteRow>();
        ConcurrentQueue<Tick> ticks = new ConcurrentQueue<Tick>();
        CancellationTokenSource feed;
        CancellationTokenSource lifetime = new CancellationTokenSource();
        HashSet<string> catalog;
        Dictionary<string, ContractInfo> contracts = new Dictionary<string, ContractInfo>();
        bool catalogLoading, connected, closing;
        int generation;
        bool snapshotBusy;
        DateTime nextSnapshot = DateTime.MinValue;
        string connectionText = "正在连接…", catalogError;
        Forms.NotifyIcon tray;
        DispatcherTimer timer;
        List<string> logs = new List<string>();
        Window toast;
        DispatcherTimer toastTimer;
        DateTime lastSound = DateTime.MinValue;
        string warning;
        readonly bool testing;
        static Brush B(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
        static TextBlock Text(string text, double size, string color) { return new TextBlock { Text = text, FontSize = size, Foreground = B(color), VerticalAlignment = VerticalAlignment.Center }; }
        static Button Button(string text, Action action) { var b = new Button { Content = text }; b.Click += (s, e) => action(); return b; }
        T Find<T>(string name) where T : class { return Window.FindName(name) as T; }

        public Widget(bool testMode = false) {
            testing = testMode;
            settings = testing ? Preferences.Defaults() : Storage.Load(out warning);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) Window = (Window)XamlReader.Load(stream);
            rows = Find<StackPanel>("Rows"); status = Find<TextBlock>("StatusLabel"); recent = Find<TextBlock>("RecentLabel");
            pin = Find<Button>("PinButton"); sound = Find<Button>("SoundButton");
            Window.Width = ValidSize(settings.Width, 350, 750, 380); Window.Height = ValidSize(settings.Height, 240, 900, 320);
            var area = SystemParameters.WorkArea;
            Window.Left = Finite(settings.Left) ? Math.Max(area.Left, Math.Min(settings.Left, area.Right - Window.Width)) : area.Left + 50;
            Window.Top = Finite(settings.Top) ? Math.Max(area.Top, Math.Min(settings.Top, area.Bottom - Window.Height)) : area.Top + 50;
            Window.Topmost = settings.Topmost; Window.Opacity = 1;
            pin.Click += (s, e) => { settings.Topmost = !settings.Topmost; Window.Topmost = settings.Topmost; UpdateButtons(); Save(); };
            sound.Click += (s, e) => { settings.Sound = !settings.Sound; UpdateButtons(); Save(); };
            Find<Button>("HideButton").Click += (s, e) => CollapseToMini();
            Find<Button>("CloseButton").Click += (s, e) => Window.Close();
            Find<Button>("AddButton").Click += (s, e) => AddDialog();
            Find<Button>("LogButton").Click += (s, e) => LogDialog();
            Find<Grid>("DragBar").MouseLeftButtonDown += (s, e) => {
                DependencyObject source = e.OriginalSource as DependencyObject;
                while (source != null) { if (source is Button) return; source = VisualTreeHelper.GetParent(source); }
                if (e.ButtonState == MouseButtonState.Pressed) Window.DragMove();
            };
            Window.KeyDown += (s, e) => { if (e.Key == Key.F5) { RestartFeed(); LoadCatalog(); } };
            Window.Closing += (s, e) => {
                closing = true; Save(); CloseDock(); lifetime.Cancel(); if (feed != null) feed.Cancel();
                timer.Stop(); if (toastTimer != null) toastTimer.Stop(); if (toast != null) toast.Close();
                tray.Visible = false; tray.Dispose();
            };
            tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Text = "币安合约浮窗", Visible = !testing };
            tray.DoubleClick += (s, e) => ShowMain();
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("展开窗口", null, (s, e) => ShowMain());
            menu.Items.Add("显示行情小格", null, (s, e) => Window.Dispatcher.BeginInvoke(new Action(CollapseToMini)));
            menu.Items.Add("重新连接", null, (s, e) => Window.Dispatcher.BeginInvoke(new Action(RestartFeed)));
            menu.Items.Add("退出", null, (s, e) => Window.Dispatcher.BeginInvoke(new Action(() => Window.Close())));
            tray.ContextMenuStrip = menu;
            BuildRows(); InitializeDock(); UpdateButtons();
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (s, e) => Drain(); timer.Start();
        }
        static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        static double ValidSize(double x, double low, double high, double fallback) { return Finite(x) ? Math.Max(low, Math.Min(high, x)) : fallback; }
        void ShowMain() { Window.Dispatcher.BeginInvoke(new Action(ExpandMain)); }
        void UpdateButtons() { if (mini != null) mini.Topmost = settings.Topmost; pin.Content = settings.Topmost ? "已置顶" : "置顶"; pin.Foreground = B(settings.Topmost ? "#F4C95D" : "#E8EDF5"); sound.Content = settings.Sound ? "声音 开" : "声音 关"; }
        void Save() {
            if (testing) return;
            settings.Version = 2; if (mini != null) { settings.MiniLeft = mini.Left; settings.MiniTop = mini.Top; settings.HasMiniPosition = true; }
            settings.Left = Window.Left; settings.Top = Window.Top; settings.Width = Window.Width; settings.Height = Window.Height;
            try { Storage.Save(settings); }
            catch (Exception ex) { Storage.Log("保存失败：" + ex.Message); recent.Text = "设置保存失败：" + ex.Message; recent.Foreground = B("#FFB86B"); }
        }
        void BuildRows() {
            rows.Children.Clear();
            var old = quotes; quotes = new Dictionary<string, QuoteRow>();
            foreach (var rule in settings.Items) {
                QuoteRow row;
                if (!old.TryGetValue(rule.Symbol, out row)) row = new QuoteRow();
                row.Rule = rule;
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var name = Text(rule.Symbol, 12, "#E8EDF5"); name.FontWeight = FontWeights.SemiBold; name.TextTrimming = TextTrimming.CharacterEllipsis;
                row.Price = Text(row.LastPrice > 0 ? AlertEngine.FormatPrice(row.LastPrice) : "—", 14, "#E8EDF5"); row.Price.FontFamily = new FontFamily("Consolas"); row.Price.HorizontalAlignment = HorizontalAlignment.Right;
                row.Percent = Text(row.LastPrice > 0 ? row.LastPercent.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%" : "—", 12, row.LastPercent >= 0 ? "#57D9A3" : "#FF7B8B"); row.Percent.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(row.Price, 1); Grid.SetColumn(row.Percent, 2);
                var captured = row;
                var edit = Button("⋯", () => EditDialog(captured));
                edit.Padding = new Thickness(5, 3, 5, 3); edit.Margin = new Thickness(4, 0, 0, 0); edit.ToolTip = "设置提醒 / 删除合约"; Grid.SetColumn(edit, 3);
                row.Detail = Text("", 10, "#8D9DB4"); row.Detail.Margin = new Thickness(0, 3, 0, 0); row.Detail.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetRow(row.Detail, 1); Grid.SetColumnSpan(row.Detail, 4);
                g.Children.Add(name); g.Children.Add(row.Price); g.Children.Add(row.Percent); g.Children.Add(edit); g.Children.Add(row.Detail);
                row.Card = new Border { Background = B("#192230"), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(0, 0, 0, 3), Child = g };
                row.Card.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) { OpenChart(captured.Rule.Symbol); e.Handled = true; } };
                row.Card.ToolTip = rule.Symbol + " 永续合约 · 双击打开 TradingView";
                rows.Children.Add(row.Card); quotes.Add(rule.Symbol, row); UpdateDetail(row);
            }
            if (quotes.Count == 0) rows.Children.Add(Text("点击「添加合约」开始监控", 14, "#8D9DB4")); BuildMini();
        }
        void UpdateDetail(QuoteRow row) {
            var r = row.Rule;
            string rapid = !r.RapidEnabled ? "异动提醒关" : row.Engine.Change.HasValue
                ? r.WindowSeconds + "s " + row.Engine.Change.Value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%"
                : "采集中 " + Math.Min(row.Engine.CollectedSeconds, r.WindowSeconds) + "/" + r.WindowSeconds + "s";
            string upper = r.Upper.HasValue ? "↑" + AlertEngine.FormatPrice(r.Upper.Value) + (r.UpperArmed ? "" : " 已停") : "";
            string lower = r.Lower.HasValue ? "↓" + AlertEngine.FormatPrice(r.Lower.Value) + (r.LowerArmed ? "" : " 已停") : "";
            row.Detail.Text = (row.Stale ? (row.LastSnapshot == DateTime.MinValue ? "行情待更新 · " : "快照 " + row.LastSnapshot.ToLocalTime().ToString("HH:mm") + " · 待推送 · ") : "") + rapid + "   " + upper + " " + lower;
            if (catalog != null && !catalog.Contains(r.Symbol)) row.Detail.Text = "该合约当前不可交易，请检查或删除";
            row.Detail.ToolTip = row.Detail.Text;
        }
        void RestartFeed() {
            if (testing) return;
            generation++; int gen = generation;
            if (feed != null) { feed.Cancel(); feed.Dispose(); }
            feed = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            ticks = new ConcurrentQueue<Tick>(); var targetQueue = ticks;
            connected = false;
            foreach (var row in quotes.Values) { row.Engine.ResetHistory(); row.Stale = true; row.LastReceived = DateTime.MinValue; }
            if (quotes.Count == 0) { connectionText = "尚未添加合约"; return; }
            var symbols = quotes.Keys.ToArray(); var token = feed.Token; nextSnapshot = DateTime.MinValue; RefreshSnapshots();
            Task.Run(() => Market.Run(symbols, tick => targetQueue.Enqueue(tick), (text, online) => {
                if (closing) return;
                Window.Dispatcher.BeginInvoke(new Action(() => {
                    if (gen != generation || closing) return;
                    connectionText = text; connected = online;
                    if (!online) foreach (var row in quotes.Values) { row.Engine.ResetHistory(); row.Stale = true; }
                }));
            }, token));
        }
        async void LoadCatalog() {
            if (testing) return;
            if (catalogLoading || closing) return;
            catalogLoading = true; catalogError = null;
            try { contracts = await Market.CatalogInfo(lifetime.Token); catalog = new HashSet<string>(contracts.Keys); foreach (var item in settings.Items) { ContractInfo info; if (contracts.TryGetValue(item.Symbol, out info)) info.Apply(item); } BuildRows(); Save(); }
            catch (Exception ex) { if (!closing) { catalogError = "合约列表暂不可用，请检查网络后重试。"; Storage.Log("合约列表：" + ex.Message); } }
            finally { catalogLoading = false; }
        }
        async void RefreshSnapshots() {
            if (testing || closing || snapshotBusy || feed == null || DateTime.UtcNow < nextSnapshot) return;
            var symbols = quotes.Values.Where(r => r.Stale).Select(r => r.Rule.Symbol).ToArray();
            if (symbols.Length == 0) return;
            snapshotBusy = true; nextSnapshot = DateTime.UtcNow.AddSeconds(30);
            var queue = ticks; var token = feed.Token;
            try { await Task.Run(() => Market.Snapshots(symbols, t => queue.Enqueue(t), token)); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Storage.Log("价格快照失败：" + ex.Message); }
            finally { snapshotBusy = false; }
        }
        void Drain() {
            Tick tick; bool changed = false; int count = 0;
            while (count++ < 1000 && ticks.TryDequeue(out tick)) {
                QuoteRow row;
                if (!quotes.TryGetValue(tick.Symbol, out row)) continue;
                if (tick.IsSnapshot) {
                    if (row.Stale && (row.LastSnapshot == DateTime.MinValue || tick.Received > row.LastSnapshot)) {
                        row.LastSnapshot = tick.Received; row.LastPrice = tick.Price; row.LastPercent = tick.Percent;
                        row.Price.Text = AlertEngine.FormatPrice(tick.Price); row.Price.Foreground = B("#92A0B5");
                        row.Percent.Text = tick.Percent.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%";
                        row.Percent.Foreground = B("#92A0B5");
                    }
                    continue;
                }
                if (tick.Time <= row.LastEvent) continue;
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - tick.Time > 15000 || tick.Time - now > 60000) continue;
                row.LastEvent = tick.Time; row.LastReceived = tick.Received; row.Stale = false;
                row.Price.Foreground = B(row.LastPrice == 0 || tick.Price == row.LastPrice ? "#E8EDF5" : tick.Price > row.LastPrice ? "#57D9A3" : "#FF7B8B");
                row.LastPrice = tick.Price; row.LastPercent = tick.Percent; row.Price.Text = AlertEngine.FormatPrice(tick.Price);
                row.Percent.Text = tick.Percent.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%";
                row.Percent.Foreground = B(tick.Percent >= 0 ? "#57D9A3" : "#FF7B8B");
                foreach (var alert in row.Engine.Push(row.Rule, tick.Price, tick.Time)) { Notify(row.Rule.Symbol, alert.Message); changed = true; }
            }
            if (changed) Save();
            foreach (var row in quotes.Values) {
                bool stale = !connected || (DateTime.UtcNow - row.LastReceived).TotalSeconds > 15;
                if (stale && !row.Stale) { row.Engine.ResetHistory(); row.Stale = true; }
                row.Card.Opacity = row.Stale ? .65 : 1;
                UpdateDetail(row);
            }
            UpdateMini(); RefreshSnapshots(); int fresh = quotes.Values.Count(r => !r.Stale);
            status.Text = fresh > 0 ? "● 实时 " + fresh + "/" + quotes.Count + " · " + DateTime.Now.ToString("HH:mm:ss") : connectionText;
            if (connected && fresh == 0) status.Text = "行情待更新 · 检查网络 / 系统时间";
            status.Foreground = B(fresh == quotes.Count && fresh > 0 ? "#57D9A3" : "#FFB86B");
            status.ToolTip = connectionText + "\nF5 重新连接；行情 15 秒未更新即标记过期。";
        }
        void OpenChart(string symbol) {
            try { Process.Start(new ProcessStartInfo(AlertEngine.ChartUrl(symbol)) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(Window, "无法打开默认浏览器：" + ex.Message); }
        }
        void Notify(string symbol, string message) {
            string entry = DateTime.Now.ToString("HH:mm:ss") + "  " + symbol + "  " + message;
            logs.Insert(0, entry); if (logs.Count > 200) logs.RemoveAt(logs.Count - 1);
            if (!testing) Storage.Log(entry); recent.Text = entry; recent.Foreground = B("#F4C95D");
            if (settings.Sound && (DateTime.UtcNow - lastSound).TotalSeconds >= 2) { System.Media.SystemSounds.Exclamation.Play(); lastSound = DateTime.UtcNow; }
            if (toastTimer != null) toastTimer.Stop();
            if (toast != null) toast.Close();
            toast = new Window { Title = "价格提醒", Width = 360, MinHeight = 150, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true, Background = B("#202A39"), Foreground = B("#E8EDF5"),
                FontFamily = new FontFamily("Microsoft YaHei UI"), Resources = Window.Resources };
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(Text(symbol + " · 价格提醒", 16, "#F4C95D"));
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 14), FontSize = 13 });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var current = toast;
            actions.Children.Add(Button("查看图表", () => OpenChart(symbol)));
            actions.Children.Add(Button("关闭", () => current.Close())); panel.Children.Add(actions); toast.Content = panel;
            toast.Closed += (s, e) => { if (toast == current) { toast = null; if (toastTimer != null) toastTimer.Stop(); } };
            toast.Loaded += (s, e) => { var a = SystemParameters.WorkArea; current.Left = a.Right - current.ActualWidth - 20; current.Top = a.Bottom - current.ActualHeight - 20; };
            toast.Show();
            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            toastTimer.Tick += (s, e) => { toastTimer.Stop(); current.Close(); }; toastTimer.Start();
        }
    }
    public static class Probe {
        public static async Task<int> Run() {
            var report = new Dictionary<string, object>();
            var received = new ConcurrentDictionary<string, Dictionary<string, object>>();
            var snapshots = new ConcurrentDictionary<string, decimal>();
            string[] symbols = { "BTCUSDT", "BTCUSDC", "GOOGLUSDT", "XAUUSDT" };
            try {
                var all = await Market.CatalogInfo(CancellationToken.None);
                report["catalogCount"] = all.Count;
                report["USDC"] = all.Values.Count(c => c.QuoteAsset == "USDC");
                report["TradFi"] = all.Values.Count(c => c.TradFi);
                report["requestedSymbols"] = symbols;
                report["listedSymbols"] = symbols.Where(s => all.ContainsKey(s)).ToArray();
            } catch (Exception ex) { report["catalogError"] = ex.ToString(); }
            try {
                using (var initial = new CancellationTokenSource(15000)) await Market.Snapshots(symbols, t => snapshots[t.Symbol] = t.Price, initial.Token);
                report["snapshots"] = snapshots.ToDictionary(x => x.Key, x => x.Value);
                using (var stop = new CancellationTokenSource(18000)) {
                    await Market.Run(symbols, t => {
                        received[t.Symbol] = new Dictionary<string, object> { { "price", t.Price }, { "eventTime", t.Time },
                            { "ageMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - t.Time } };
                    }, (s, b) => { }, stop.Token);
                }
                report["quotes"] = received.ToDictionary(x => x.Key, x => x.Value);
            } catch (Exception ex) { report["streamError"] = ex.ToString(); }
            report["noPushDuringProbe"] = symbols.Where(s => !received.ContainsKey(s)).ToArray();
            report["success"] = snapshots.Count == symbols.Length && received.ContainsKey("BTCUSDT") && report.ContainsKey("catalogCount");
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-result-v2.json"), Storage.Json().Serialize(report));
            return (bool)report["success"] ? 0 : 1;
        }
    }
}
