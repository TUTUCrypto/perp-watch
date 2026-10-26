using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace FuturesWidget {
    public partial class Widget {
        Window mini;
        StackPanel miniTiles;
        ScrollViewer miniScroll;
        readonly DockState dock = new DockState();
        readonly Dictionary<string, TextBlock> miniPrices = new Dictionary<string, TextBlock>();
        readonly Dictionary<string, Border> miniCards = new Dictionary<string, Border>();
        DispatcherTimer hoverTimer, leaveTimer;
        bool draggingMini, suppressHover, mousePressed;
        Point pressPoint;

        bool HorizontalDock { get { return settings.DockEdge == "Top" || settings.DockEdge == "Bottom"; } }

        void InitializeDock() {
            mini = new Window { Title = "币安行情小格", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true, BorderThickness = new Thickness(0), BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, Opacity = 1, ShowInTaskbar = false,
                ShowActivated = false, Topmost = settings.Topmost, FontFamily = new FontFamily("Microsoft YaHei UI"),
                UseLayoutRounding = true, SnapsToDevicePixels = true };
            miniTiles = new StackPanel();
            miniScroll = new ScrollViewer { Content = miniTiles, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, PanningMode = PanningMode.Both };
            // Only the backdrop has alpha; prices and labels remain fully opaque.
            mini.Content = new Border { Background = B("#78101720"), BorderBrush = B("#35FFFFFF"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(3), Child = miniScroll };
            mini.ContextMenu = DockMenu();
            mini.PreviewMouseWheel += (s, e) => { if (HorizontalDock) { miniScroll.ScrollToHorizontalOffset(miniScroll.HorizontalOffset - e.Delta / 2.0); e.Handled = true; } };
            mini.MouseEnter += (s, e) => {
                leaveTimer.Stop();
                if (!suppressHover && !draggingMini && dock.Mode == DisplayMode.Mini) { hoverTimer.Stop(); hoverTimer.Start(); }
            };
            mini.MouseLeave += (s, e) => { suppressHover = false; hoverTimer.Stop(); ScheduleCollapse(); };
            mini.MouseLeftButtonDown += (s, e) => { mousePressed = true; pressPoint = e.GetPosition(mini); };
            mini.MouseMove += (s, e) => {
                if (!mousePressed || e.LeftButton != MouseButtonState.Pressed || draggingMini) return;
                var p = e.GetPosition(mini);
                if ((p - pressPoint).Length < 5) return;
                draggingMini = true; hoverTimer.Stop(); leaveTimer.Stop();
                if (dock.Mode == DisplayMode.Peek) { dock.Collapse(); Window.Hide(); }
                try { mini.DragMove(); }
                finally {
                    draggingMini = false; mousePressed = false; suppressHover = true;
                    SnapMini(); Save();
                }
                e.Handled = true;
            };
            mini.MouseLeftButtonUp += (s, e) => {
                if (!mousePressed) return;
                mousePressed = false; hoverTimer.Stop(); ExpandMain(); e.Handled = true;
            };
            mini.Closed += (s, e) => { if (!closing) Window.Close(); };
            Window.MouseEnter += (s, e) => leaveTimer.Stop();
            Window.MouseLeave += (s, e) => ScheduleCollapse();
            Window.PreviewMouseDown += (s, e) => { if (dock.Mode == DisplayMode.Peek) { dock.Expand(); mini.Hide(); } };
            Window.KeyDown += (s, e) => { if (e.Key == Key.Escape) CollapseToMini(); };
            hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            hoverTimer.Tick += (s, e) => {
                hoverTimer.Stop();
                if (!closing && mini.IsMouseOver && !draggingMini && !suppressHover && (mini.ContextMenu == null || !mini.ContextMenu.IsOpen)) ShowPeek();
            };
            leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            leaveTimer.Tick += (s, e) => {
                leaveTimer.Stop();
                if (dock.Mode != DisplayMode.Peek || mini.IsMouseOver || Window.IsMouseOver || mini.ContextMenu.IsOpen) return;
                dock.Leave(); Window.Hide();
            };
            Find<Button>("DockButton").Click += (s, e) => {
                var menu = DockMenu(); menu.PlacementTarget = Find<Button>("DockButton"); menu.IsOpen = true;
            };
            BuildMini();
            var work = SystemParameters.WorkArea;
            mini.Left = settings.HasMiniPosition && Finite(settings.MiniLeft) ? settings.MiniLeft : work.Right - mini.Width - 2;
            mini.Top = settings.HasMiniPosition && Finite(settings.MiniTop) ? settings.MiniTop : work.Top + 80;
        }

        ContextMenu DockMenu() {
            var menu = new ContextMenu();
            foreach (var choice in new[] { new[] { "Left", "靠左 · 竖排" }, new[] { "Right", "靠右 · 竖排" },
                new[] { "Top", "靠上 · 横排" }, new[] { "Bottom", "靠下 · 横排" } }) {
                string edge = choice[0];
                var item = new MenuItem { Header = choice[1], IsCheckable = true, IsChecked = settings.DockEdge == edge };
                item.Click += (s, e) => { settings.DockEdge = edge; BuildMini(); PlaceMini(true); CollapseToMini(); Save(); };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var open = new MenuItem { Header = "展开窗口" }; open.Click += (s, e) => ExpandMain(); menu.Items.Add(open);
            var exit = new MenuItem { Header = "退出" }; exit.Click += (s, e) => Window.Close(); menu.Items.Add(exit);
            menu.Opened += (s, e) => {
                hoverTimer.Stop(); leaveTimer.Stop();
                for (int i = 0; i < 4; i++) ((MenuItem)menu.Items[i]).IsChecked = new[] { "Left", "Right", "Top", "Bottom" }[i] == settings.DockEdge;
            };
            menu.Closed += (s, e) => ScheduleCollapse();
            return menu;
        }
        void BuildMini() {
            if (mini == null) return;
            miniTiles.Children.Clear(); miniPrices.Clear(); miniCards.Clear();
            miniTiles.Orientation = HorizontalDock ? Orientation.Horizontal : Orientation.Vertical;
            foreach (var rule in settings.Items) {
                var price = Text("—", 19, "#CBD5E1"); price.FontFamily = new FontFamily("Consolas");
                price.HorizontalAlignment = HorizontalAlignment.Center;
                var number = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                    Height = 23, Width = 108, Child = price, HorizontalAlignment = HorizontalAlignment.Center };
                var symbol = Text(rule.Symbol, 10, "#CBD5E1"); symbol.HorizontalAlignment = HorizontalAlignment.Center;
                symbol.TextTrimming = TextTrimming.CharacterEllipsis; symbol.MaxWidth = 108;
                var cell = new StackPanel(); cell.Children.Add(number); cell.Children.Add(symbol);
                var card = new Border { Width = 116, Height = 45, Padding = new Thickness(4, 3, 4, 3), Child = cell,
                    Cursor = Cursors.Hand, Background = Brushes.Transparent };
                miniTiles.Children.Add(card); miniPrices[rule.Symbol] = price; miniCards[rule.Symbol] = card;
            }
            if (settings.Items.Count == 0) {
                var empty = Text("＋ 添加合约", 12, "#E8EDF5"); empty.Margin = new Thickness(6, 10, 6, 10); miniTiles.Children.Add(empty);
            }
            Rect area = MiniWorkArea();
            int count = Math.Max(1, settings.Items.Count);
            mini.Width = HorizontalDock ? Math.Min(count * 116 + 8, area.Width - 12) : 124;
            mini.Height = HorizontalDock ? 53 : Math.Min(count * 45 + 8, area.Height - 12);
            UpdateMini();
            if (mini.IsVisible) PlaceMini(false);
        }
        void UpdateMini() {
            if (mini == null) return;
            foreach (var row in quotes.Values) {
                TextBlock price; Border card;
                if (!miniPrices.TryGetValue(row.Rule.Symbol, out price) || !miniCards.TryGetValue(row.Rule.Symbol, out card)) continue;
                price.Text = row.LastPrice > 0 ? row.LastPrice.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture) : "—";
                price.Foreground = B(row.Stale ? "#92A0B5" : row.LastPercent >= 0 ? "#65E3B1" : "#FF6B85");
                card.ToolTip = row.Rule.Symbol + (row.Stale ? " · 行情待更新" : " · 24h " + row.LastPercent.ToString("+0.00;-0.00;0.00") + "%")
                    + "\n悬停预览 · 单击保持展开 · 拖动停靠 · 右键选方向";
            }
        }
        Rect MiniWorkArea() {
            if (mini == null || !mini.IsVisible || !Finite(mini.Left) || !Finite(mini.Top)) return SystemParameters.WorkArea;
            var source = PresentationSource.FromVisual(mini);
            var toDevice = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice : Matrix.Identity;
            var fromDevice = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
            var point = toDevice.Transform(new Point(mini.Left + mini.Width / 2, mini.Top + mini.Height / 2));
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y)).WorkingArea;
            var origin = fromDevice.Transform(new Point(screen.Left, screen.Top));
            var end = fromDevice.Transform(new Point(screen.Right, screen.Bottom));
            return new Rect(origin, end);
        }
        void PlaceMini(bool centerAlongEdge) {
            if (mini == null) return;
            var a = MiniWorkArea();
            if (centerAlongEdge) {
                if (HorizontalDock) mini.Left = a.Left + (a.Width - mini.Width) / 2;
                else mini.Top = a.Top + (a.Height - mini.Height) / 2;
            }
            mini.Left = Math.Max(a.Left, Math.Min(mini.Left, a.Right - mini.Width));
            mini.Top = Math.Max(a.Top, Math.Min(mini.Top, a.Bottom - mini.Height));
            if (settings.DockEdge == "Left") mini.Left = a.Left + 2;
            if (settings.DockEdge == "Right") mini.Left = a.Right - mini.Width - 2;
            if (settings.DockEdge == "Top") mini.Top = a.Top + 2;
            if (settings.DockEdge == "Bottom") mini.Top = a.Bottom - mini.Height - 2;
        }
        void SnapMini() {
            var a = MiniWorkArea();
            var distances = new[] { Math.Abs(mini.Left - a.Left), Math.Abs(a.Right - mini.Left - mini.Width),
                Math.Abs(mini.Top - a.Top), Math.Abs(a.Bottom - mini.Top - mini.Height) };
            int nearest = Array.IndexOf(distances, distances.Min());
            settings.DockEdge = new[] { "Left", "Right", "Top", "Bottom" }[nearest];
            BuildMini(); PlaceMini(false);
        }
        void PositionExpanded() {
            var a = MiniWorkArea();
            double x = mini.Left, y = mini.Top;
            if (settings.DockEdge == "Left") x = mini.Left + mini.Width;
            if (settings.DockEdge == "Right") x = mini.Left - Window.Width;
            if (settings.DockEdge == "Top") y = mini.Top + mini.Height;
            if (settings.DockEdge == "Bottom") y = mini.Top - Window.Height;
            Window.Left = Math.Max(a.Left, Math.Min(x, a.Right - Window.Width));
            Window.Top = Math.Max(a.Top, Math.Min(y, a.Bottom - Window.Height));
        }
        void ShowPeek() {
            if (dock.Mode != DisplayMode.Mini || closing) return;
            dock.Hover(); PositionExpanded(); Window.ShowActivated = false; Window.Show();
        }
        void ExpandMain() {
            hoverTimer.Stop(); leaveTimer.Stop();
            if (dock.Mode == DisplayMode.Mini) PositionExpanded();
            dock.Expand(); Window.ShowActivated = true; Window.Show(); Window.WindowState = WindowState.Normal;
            mini.Hide(); Window.Activate();
        }
        void CollapseToMini() {
            if (closing || mini == null) return;
            hoverTimer.Stop(); leaveTimer.Stop(); dock.Collapse();
            Window.Hide(); mini.Show(); PlaceMini(false); suppressHover = mini.IsMouseOver; UpdateMini(); Save();
        }
        void ScheduleCollapse() { if (dock.Mode == DisplayMode.Peek) { leaveTimer.Stop(); leaveTimer.Start(); } }
        void CloseDock() {
            if (hoverTimer != null) hoverTimer.Stop();
            if (leaveTimer != null) leaveTimer.Stop();
            if (mini != null) mini.Close();
        }
        public void Start() {
            if (testing) return;
            RestartFeed(); LoadCatalog(); CollapseToMini();
            if (warning != null) { recent.Text = warning; Storage.Log(warning); }
        }
    }
}
