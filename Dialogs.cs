using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FuturesWidget {
    public partial class Widget {
        Window Dialog(string title, int width, StackPanel panel) {
            return new Window { Title = title, Width = width, SizeToContent = SizeToContent.Height, MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 40),
                ResizeMode = ResizeMode.NoResize, Owner = Window, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = B("#10151F"),
                Foreground = B("#E8EDF5"), FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"), FontSize = 13, Resources = Window.Resources,
                Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, ShowInTaskbar = false };
        }
        void AddDialog() {
            dock.Expand(); mini.Hide();
            var panel = new StackPanel { Margin = new Thickness(16) };
            var dialog = Dialog("添加永续合约", 440, panel);
            panel.Children.Add(Text("添加合约", 18, "#E8EDF5"));
            var search = new TextBox { Margin = new Thickness(0, 10, 0, 8) };
            search.ToolTip = "币名、合约代码或传统资产名，例如 BTC、GOOGL、黄金";
            panel.Children.Add(search);
            string category = "全部";
            var categories = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(categories);
            var list = new ListBox { Height = 260, Background = B("#192230"), Foreground = B("#E8EDF5"), BorderThickness = new Thickness(0),
                Padding = new Thickness(3), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            panel.Children.Add(list);
            var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = B("#A7B7CD"), Margin = new Thickness(0, 8, 0, 10), FontSize = 11 };
            panel.Children.Add(hint);
            Action filter = () => {
                string query = search.Text.Trim().Replace("/", "").Replace(".P", "");
                var matches = contracts.Values.Where(c => !quotes.ContainsKey(c.Symbol) && ContractInfo.Matches(c, query, category)).OrderBy(c => c.Symbol).ToArray();
                list.ItemsSource = matches;
                hint.Text = catalog == null ? (catalogError ?? "正在获取交易所合约列表…") : matches.Length + " 个可添加 · 价格单位以各合约报价币种为准";
                foreach (Button tab in categories.Children) tab.Background = B(Convert.ToString(tab.Content) == category ? "#465335" : "#202A39");
            };
            foreach (string label in new[] { "全部", "USDT", "USDC", "传统金融" }) {
                string selected = label;
                categories.Children.Add(Button(label, () => { category = selected; filter(); }));
            }
            search.TextChanged += (s, e) => filter();
            Action add = () => {
                var info = list.SelectedItem as ContractInfo;
                if (info == null) { hint.Text = "请先选择一个合约。"; return; }
                if (settings.Items.Count >= 50) { hint.Text = "最多监控 50 个合约。"; return; }
                if (quotes.ContainsKey(info.Symbol)) return;
                var item = new WatchItem { Symbol = info.Symbol }; info.Apply(item); item.Validate();
                settings.Items.Add(item); BuildRows(); Save(); RestartFeed(); dialog.Close();
            };
            list.MouseDoubleClick += (s, e) => { if (list.SelectedItem != null) add(); };
            var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            controls.Children.Add(Button("刷新列表", () => LoadCatalog()));
            controls.Children.Add(Button("添加", add)); panel.Children.Add(controls);
            var refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            HashSet<string> previous = catalog;
            refresh.Tick += (s, e) => { if (previous != catalog || catalog == null) { previous = catalog; filter(); } };
            dialog.Closed += (s, e) => refresh.Stop(); dialog.Loaded += (s, e) => search.Focus();
            if (catalog == null) LoadCatalog(); filter(); refresh.Start(); dialog.ShowDialog();
        }
        TextBox Field(StackPanel panel, string label, string value) {
            panel.Children.Add(new TextBlock { Text = label, Foreground = B("#A7B7CD"), Margin = new Thickness(0, 10, 0, 5) });
            var input = new TextBox { Text = value }; panel.Children.Add(input); return input;
        }
        static decimal Number(string value, string label) {
            decimal result;
            if (!decimal.TryParse(value.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result))
                throw new ArgumentException(label + "请输入数字，小数点使用英文句点。");
            return result;
        }
        void EditDialog(QuoteRow row) {
            dock.Expand(); mini.Hide();
            var r = row.Rule;
            var panel = new StackPanel { Margin = new Thickness(20) }; var dialog = Dialog(r.Symbol + " · 提醒设置", 450, panel);
            panel.Children.Add(Text(r.Symbol + "  提醒设置", 19, "#E8EDF5"));
            panel.Children.Add(new TextBlock { Text = "到价提醒触发一次后自动关闭，可勾选重新启用。\n启用时若现价已满足条件，会在下一条行情立即提醒。", TextWrapping = TextWrapping.Wrap, Foreground = B("#8D9DB4"), Margin = new Thickness(0, 9, 0, 0), FontSize = 11 });
            var upper = Field(panel, "价格上限（" + ContractInfo.Quote(r) + "，留空不设置）", r.Upper.HasValue ? r.Upper.Value.ToString(CultureInfo.InvariantCulture) : "");
            var upperOn = new CheckBox { Content = "启用上限报警", IsChecked = r.UpperArmed }; panel.Children.Add(upperOn);
            var lower = Field(panel, "价格下限（" + ContractInfo.Quote(r) + "，留空不设置）", r.Lower.HasValue ? r.Lower.Value.ToString(CultureInfo.InvariantCulture) : "");
            var lowerOn = new CheckBox { Content = "启用下限报警", IsChecked = r.LowerArmed }; panel.Children.Add(lowerOn);
            upper.TextChanged += (s, e) => upperOn.IsChecked = upper.Text.Trim().Length > 0;
            lower.TextChanged += (s, e) => lowerOn.IsChecked = lower.Text.Trim().Length > 0;
            var rapidOn = new CheckBox { Content = "启用快速上涨 / 下跌提醒", IsChecked = r.RapidEnabled, Margin = new Thickness(0, 16, 0, 0) }; panel.Children.Add(rapidOn);
            var seconds = Field(panel, "观察时间（10～3600 秒）", r.WindowSeconds.ToString());
            var percent = Field(panel, "涨跌幅阈值（%，上涨下跌均监控）", r.RapidPercent.ToString(CultureInfo.InvariantCulture));
            var cooldown = Field(panel, "同方向提醒冷却时间（秒）", r.CooldownSeconds.ToString());
            var error = new TextBlock { Foreground = B("#FF7B8B"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) }; panel.Children.Add(error);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(Button("移除合约", () => { settings.Items.Remove(r); BuildRows(); Save(); RestartFeed(); dialog.Close(); }));
            actions.Children.Add(Button("测试提醒", () => Notify(r.Symbol, "这是一条测试提醒，当前报警规则未触发。")));
            actions.Children.Add(Button("保存", () => {
                try {
                    var next = r.Copy();
                    next.Upper = upper.Text.Trim() == "" ? (decimal?)null : Number(upper.Text, "价格上限");
                    next.Lower = lower.Text.Trim() == "" ? (decimal?)null : Number(lower.Text, "价格下限");
                    next.UpperArmed = next.Upper.HasValue && upperOn.IsChecked == true;
                    next.LowerArmed = next.Lower.HasValue && lowerOn.IsChecked == true;
                    decimal sec = Number(seconds.Text, "观察时间"), cool = Number(cooldown.Text, "冷却时间");
                    if (sec != Math.Truncate(sec) || cool != Math.Truncate(cool)) throw new ArgumentException("秒数必须是整数。");
                    next.WindowSeconds = checked((int)sec); next.CooldownSeconds = checked((int)cool); next.RapidPercent = Number(percent.Text, "阈值"); next.RapidEnabled = rapidOn.IsChecked == true;
                    next.Validate();
                    settings.Items[settings.Items.IndexOf(r)] = next; row.Rule = next; row.Engine.ResetHistory();
                    BuildRows(); Save(); dialog.Close();
                } catch (Exception ex) { error.Text = ex.Message; }
            }));
            panel.Children.Add(actions); dialog.ShowDialog();
        }
        void LogDialog() {
            var panel = new StackPanel { Margin = new Thickness(18) }; var dialog = Dialog("提醒记录", 650, panel);
            panel.Children.Add(Text("提醒与连接记录", 18, "#E8EDF5"));
            string text;
            try { text = File.Exists(Path.Combine(Storage.DirectoryPath, "events.log")) ? string.Join(Environment.NewLine, File.ReadLines(Path.Combine(Storage.DirectoryPath, "events.log")).Reverse().Take(200)) : "还没有提醒记录。"; }
            catch { text = string.Join(Environment.NewLine, logs); }
            panel.Children.Add(new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 340, Margin = new Thickness(0, 12, 0, 12) });
            panel.Children.Add(Button("关闭", () => dialog.Close())); dialog.ShowDialog();
        }
    }
}
