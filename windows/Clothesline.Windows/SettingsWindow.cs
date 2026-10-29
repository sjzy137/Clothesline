using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clothesline.Core;
using Microsoft.Win32;

namespace Clothesline.Windows;

internal enum SettingsPage { General, Features, Folders, Clipboard, Ocr }

internal sealed partial class RopeWindow
{
    /// The tray menu can only ever offer a handful of toggles. Everything that needs explanation, a
    /// list, or a download lives here instead.
    private void ShowSettings(SettingsPage start)
    {
        // Only one settings window at a time: reopening from the tray (or after minimising) focuses
        // the existing one instead of stacking another copy on top of it.
        if (settingsWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var current = start;
        var window = new Window
        {
            Title = T("设置","Settings"), Width = 780, Height = 560, MinWidth = 640, MinHeight = 420,
            Owner = this, WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(250, 250, 250)), Icon = this.Icon
        };

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var navigation = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(8, 10, 8, 10) };
        Grid.SetColumn(navigation, 0);
        var body = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 20, 24, 20) };
        Grid.SetColumn(body, 1);

        void Show(SettingsPage page)
        {
            current = page;
            body.Content = BuildPage(page, window, () => Show(page));
            var labels = Labels();
            navigation.Items.Clear();
            foreach (var (value, label) in labels)
            {
                var item = new ListBoxItem
                {
                    Content = label, Tag = value, Padding = new Thickness(14, 10, 14, 10), FontSize = 13,
                    BorderThickness = new Thickness(0), Background = Brushes.Transparent
                };
                if (value == page)
                {
                    item.Background = new SolidColorBrush(Color.FromRgb(233, 240, 250));
                    item.FontWeight = FontWeights.SemiBold;
                }
                navigation.Items.Add(item);
            }
        }

        navigation.SelectionChanged += (_, _) =>
        { if (navigation.SelectedItem is ListBoxItem item && (SettingsPage)item.Tag != current) Show((SettingsPage)item.Tag); };

        root.Children.Add(navigation); root.Children.Add(body); window.Content = root;

        // Reopening after a language switch is the only way to retranslate the window itself: the
        // navigation labels and the title cannot be refreshed from inside the page that changed them.
        void Reload()
        {
            var page = current; window.Close(); ShowSettings(page);
        }

        Show(start);
        var wasBusy = busy; busy = true;
        settingsWindow = window;
        window.Closed += (_, _) => { settingsWindow = null; busy = wasBusy; reopenSettings = null; };
        // Reached by the page builders through a field, so a language change can reopen at the same
        // page without threading a callback through every control.
        reopenSettings = Reload;
        window.Show();
    }

    private Action? reopenSettings;

    private List<(SettingsPage Page, string Label)> Labels() =>
    [
        (SettingsPage.General, T("常规","General")),
        (SettingsPage.Features, T("功能开关","Features")),
        (SettingsPage.Folders, T("监控文件夹","Watched Folders")),
        (SettingsPage.Clipboard, T("剪贴板","Clipboard")),
        (SettingsPage.Ocr, T("文字识别","Text Recognition"))
    ];

    private UIElement BuildPage(SettingsPage page, Window window, Action refresh) => page switch
    {
        SettingsPage.Features => BuildFeatures(window, refresh),
        SettingsPage.Folders => BuildFolders(window, refresh),
        SettingsPage.Clipboard => BuildClipboard(refresh),
        SettingsPage.Ocr => BuildOcr(refresh),
        _ => BuildGeneral(refresh)
    };

    // ---- Page: feature switches ----------------------------------------------

    private UIElement BuildFeatures(Window window, Action refresh)
    {
        var panel = Page();
        panel.Children.Add(Heading(T("功能开关","Feature switches")));
        panel.Children.Add(Hint(T("每一项都能单独关闭。关掉的功能不再占用快捷键，也不会对文件做任何事。",
            "Every optional behaviour can be switched off on its own. A switched off feature claims no shortcut and never touches a file.")));

        panel.Children.Add(Toggle(T("拖到晾衣绳上时挂起","Hang files dropped on the rope"),
            T("关闭后拖到晾衣绳上的文件不再挂起，指针下方的窗口照常接收这次拖放。",
              "When off, files dropped over the rope are ignored and the window underneath receives the drop as usual."),
            settings.DropToHang, value => { settings.DropToHang = value; Persist(); }));

        panel.Children.Add(Toggle(T("按住 Ctrl 拖动为移动","Ctrl+drag moves the file"),
            T("关闭后 Ctrl+拖动与普通拖动一样，只复制不移动。原文件不会被移入回收站。",
              "When off, Ctrl+drag copies like a plain drag and the original is never sent to the Recycle Bin."),
            settings.CtrlDragMoves, value => { settings.CtrlDragMoves = value; Persist(); }));

        panel.Children.Add(Toggle(T("单击卡片即复制","Single click copies"),
            T("图片复制为位图，文字便签复制为文本，其余复制为文件。关闭后单击不做任何事，误点也就不会覆盖剪贴板。",
              "Images copy as bitmaps, notes as text, everything else as a file. When off, a click does nothing, so a stray click cannot overwrite the clipboard."),
            settings.ClickToCopy, value => { settings.ClickToCopy = value; Persist(); }));

        panel.Children.Add(Toggle(T("卡片上的固定 / 取下按钮","Pin and take-down buttons on cards"),
            T("关闭后卡片更干净，固定和取下仍可在右键菜单里完成。",
              "When off the cards look plainer; pinning and taking down remain available from the right-click menu."),
            settings.CardButtons, value => { settings.CardButtons = value; Persist(); signature = ""; Render(); }));

        panel.Children.Add(Toggle(T("全屏时自动隐身","Hide while something is full screen"),
            T("播放视频、玩游戏或窗口最大化铺满屏幕时收起晾衣绳。拖动文件时仍然会滑出，否则文件无法放上来。",
              "Retracts the rope during full-screen video, games, or a maximised window. It still slides out while a file is being dragged, otherwise the drop could never land."),
            settings.HideFullScreen, value => { settings.HideFullScreen = value; Persist(); }));

        panel.Children.Add(Toggle(T("显示 / 隐藏快捷键","Show / hide hot key"),
            T("关掉可以把这个组合键让给其他程序，例如英特尔显卡自带的快捷键。组合键本身可在下方自定义。",
              "Turning this off releases the combination to other software, such as the shortcuts built into Intel graphics drivers. The combination itself can be customised below."),
            settings.HotKeyEnabled, value => { settings.HotKeyEnabled = value; Persist(); ApplyHotKey(); }));

        // Custom hot key capture: click the button, then press a combination. It is recorded from the
        // real key event, so no manual text entry is needed and the value round-trips through
        // Native.TryParseHotKey. At least one modifier is required so a bare key can never hijack
        // normal typing system-wide.
        var hotkeyStatus = new TextBlock { FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 3, 0, 0) };
        var hotkeyButton = new Button { Content = T("点击后按下组合键…","Click then press a combination…"), Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Left };
        var capturing = false;
        void RefreshHotkeyStatus() => hotkeyStatus.Text = T("当前组合键：","Current shortcut: ") + (string.IsNullOrWhiteSpace(settings.HotKey) ? T("（未设置）","(none)") : settings.HotKey);
        RefreshHotkeyStatus();
        hotkeyButton.Click += (_, _) => { capturing = true; hotkeyButton.Content = T("请按下组合键（含 Ctrl/Alt/Shift/Win），Esc 取消…","Press a combination (with Ctrl/Alt/Shift/Win), Esc to cancel…"); hotkeyButton.Focus(); };
        window.PreviewKeyDown += (_, e) =>
        {
            if (!capturing) return;
            capturing = false;
            hotkeyButton.Content = T("点击后按下组合键…","Click then press a combination…");
            e.Handled = true;
            if (e.Key == Key.Escape) return;
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            var mods = e.KeyboardDevice.Modifiers;
            if (mods == ModifierKeys.None) { hotkeyStatus.Text = T("至少需要一个修饰键（Ctrl/Alt/Shift/Win）。","At least one modifier key (Ctrl/Alt/Shift/Win) is required."); return; }
            var parts = new List<string>();
            if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((mods & ModifierKeys.Windows) != 0) parts.Add("Win");
            parts.Add(e.Key.ToString());
            settings.HotKey = string.Join("+", parts); Persist(); ApplyHotKey(); RefreshHotkeyStatus();
        };
        var hotkeyReset = new Button { Content = T("恢复默认","Reset"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
        hotkeyReset.Click += (_, _) => { settings.HotKey = "Ctrl+Alt+Up"; Persist(); ApplyHotKey(); RefreshHotkeyStatus(); };
        var hotkeyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        hotkeyRow.Children.Add(hotkeyButton); hotkeyRow.Children.Add(hotkeyReset);
        panel.Children.Add(Field(T("自定义快捷键","Custom shortcut"), hotkeyRow, hotkeyStatus));

        var delay = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        var options = new[] { 800, 1800, 3000, 5000 };
        var match = -1;
        foreach (var value in options)
        {
            delay.Items.Add((value / 1000.0).ToString("0.0") + T(" 秒"," s"));
            if (Math.Abs(settings.RevealDelay - value) < 200) match = delay.Items.Count - 1;
        }
        if (delay.SelectedIndex < 0) delay.SelectedIndex = match >= 0 ? match : 1;
        delay.SelectionChanged += (_, _) => { settings.RevealDelay = options[delay.SelectedIndex]; Persist(); };
        panel.Children.Add(Field(T("自动收回延时","Delay before retracting"), delay,
            Hint(T("指针离开晾衣绳后等多久再收起。","How long the rope waits after the pointer leaves before sliding back up."))));

        // Summon dwell time: how long the pointer must linger at the top edge before the rope slides
        // out. 0 reveals immediately on hover (the original behaviour); the slider goes up to 10 s.
        var dwell = new Slider { Width = 240, HorizontalAlignment = HorizontalAlignment.Left, Minimum = 0, Maximum = 10000, TickFrequency = 500, SmallChange = 100, LargeChange = 1000 };
        dwell.Value = settings.RevealDwell;
        var dwellLabel = new TextBlock { FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 3, 0, 0) };
        void RefreshDwell() => dwellLabel.Text = settings.RevealDwell <= 0
            ? T("0 秒（指针靠近立即滑出，保持原行为）","0 s (slides out on hover immediately, original behaviour)")
            : (settings.RevealDwell / 1000.0).ToString("0.0") + T(" 秒"," s");
        RefreshDwell();
        dwell.ValueChanged += (_, _) => { settings.RevealDwell = (int)Math.Round(dwell.Value); Persist(); RefreshDwell(); };
        panel.Children.Add(Field(T("召唤停留时长","Summon dwell time"), dwell,
            Hint(T("指针在屏幕最顶部停留多久，晾衣绳才滑出。0 表示贴顶即显；最大 10 秒，适合误触频繁时降低灵敏度。",
              "How long the pointer must linger at the very top of the screen before the rope slides out. 0 reveals on hover; up to 10 s, useful when the edge is brushed by accident often."))));
        return panel;
    }

    /// A labelled switch with its explanation underneath, which is the shape every optional
    /// behaviour here needs: the name alone never says what turning it off actually costs.
    private static StackPanel Toggle(string label, string hint, bool value, Action<bool> set)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 7, 0, 7) };
        var box = new CheckBox { Content = label, IsChecked = value, FontSize = 13 };
        box.Checked += (_, _) => set(true);
        box.Unchecked += (_, _) => set(false);
        stack.Children.Add(box);
        stack.Children.Add(new TextBlock
        {
            Text = hint, FontSize = 11, Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 3, 0, 0)
        });
        return stack;
    }

    // ---- Page: general -------------------------------------------------------

    private UIElement BuildGeneral(Action refresh)
    {
        var panel = Page();
        panel.Children.Add(Heading(T("常规","General")));

        var visibility = new ComboBox { Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
        visibility.Items.Add(T("自动隐藏（指针靠近时滑出）","Auto-hide (slide out on hover)"));
        visibility.Items.Add(T("一直显示","Always show"));
        visibility.SelectedIndex = settings.AlwaysVisible ? 1 : 0;
        visibility.SelectionChanged += (_, _) => { settings.AlwaysVisible = visibility.SelectedIndex == 1; Persist(); };
        panel.Children.Add(Field(T("晾衣绳显示方式","Clothesline visibility"), visibility,
            Hint(T("按 Ctrl+Alt+↑ 可随时切换。全屏播放或游戏时会自动隐身。",
                "Ctrl+Alt+Up toggles this at any time. It also hides itself while something is full screen."))));

        var capacity = new ComboBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var number in new[] { 5, 8, 12, 25, 50 })
        {
            capacity.Items.Add(number.ToString(CultureInfo.InvariantCulture));
            if (settings.Capacity == number) capacity.SelectedIndex = capacity.Items.Count - 1;
        }
        if (capacity.SelectedIndex < 0) capacity.SelectedIndex = 1;
        capacity.SelectionChanged += (_, _) => { settings.Capacity = int.Parse((string)capacity.SelectedItem, CultureInfo.InvariantCulture); Persist(); };
        panel.Children.Add(Field(T("保留数量","Items to keep"), capacity,
            Hint(T("超出后最早挂上的项目会被取下，原文件不受影响。",
                "The oldest items are taken down past this number. The files themselves are never touched."))));

        var startup = new CheckBox { Content = T("开机启动","Launch at login"), IsChecked = Native.LaunchAtLogin, VerticalAlignment = VerticalAlignment.Center };
        startup.Checked += (_, _) => Native.LaunchAtLogin = true;
        startup.Unchecked += (_, _) => Native.LaunchAtLogin = false;
        panel.Children.Add(Field(T("启动","Startup"), startup));

        var language = new ComboBox { Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
        language.Items.Add(T("简体中文","Chinese (Simplified)"));
        language.Items.Add("English");
        language.SelectedIndex = settings.English ? 1 : 0;
        var applying = false;
        language.SelectionChanged += (_, _) =>
        {
            if (applying) return;
            settings.English = language.SelectedIndex == 1; Persist();
            applying = true; reopenSettings?.Invoke();
        };
        panel.Children.Add(Field(T("语言","Language"), language));
        return panel;
    }

    // ---- Page: watched folders ----------------------------------------------

    private UIElement BuildFolders(Window window, Action refresh)
    {
        var panel = Page();
        panel.Children.Add(Heading(T("监控文件夹","Watched Folders")));
        panel.Children.Add(Hint(T("这些文件夹顶层的文件会自动挂到晾衣绳上。移除只会停止监控，磁盘上的文件夹不会被删除。",
            "Files at the top level of these folders are hung automatically. Removing a folder only stops watching it; nothing on disk is deleted.")));

        if (settings.Folders.Count == 0)
            panel.Children.Add(new TextBlock
            {
                Text = T("还没有监控任何文件夹。","No folders are being watched yet."),
                FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 16, 0, 0)
            });

        foreach (var folder in settings.Folders.ToList())
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = System.IO.Path.GetFileName(folder);
            if (name.Length == 0) name = folder;
            var text = new StackPanel { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = name, FontSize = 13 });
            text.Children.Add(new TextBlock
            {
                Text = folder, FontSize = 11, Foreground = Brushes.Gray,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = folder
            });
            Grid.SetColumn(text, 0);

            var remove = new Button { Content = T("移除","Remove"), Padding = new Thickness(14, 5, 14, 5), VerticalAlignment = VerticalAlignment.Center };
            remove.Click += (_, _) => Guard(() =>
            {
                settings.Folders.Remove(folder);
                // Watchers hold the folder open, so they are rebuilt rather than left running for a
                // path that no longer exists in the preferences.
                Persist(); RebuildWatchers(); signature = ""; Render(); refresh();
            });
            Grid.SetColumn(remove, 1);
            row.Children.Add(text); row.Children.Add(remove);
            panel.Children.Add(row);
        }

        var add = new Button { Content = T("添加文件夹…","Add Folder…"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(16, 5, 16, 5) };
        add.Click += (_, _) => Guard(() =>
        {
            var picker = new OpenFolderDialog(); if (picker.ShowDialog(window) != true) return;
            if (!settings.Folders.Contains(picker.FolderName, StringComparer.OrdinalIgnoreCase)) settings.Folders.Add(picker.FolderName);
            Persist(); RebuildWatchers(); signature = ""; Render(); refresh();
        });
        panel.Children.Add(add);
        return panel;
    }

    // ---- Page: clipboard -----------------------------------------------------

    private UIElement BuildClipboard(Action refresh)
    {
        var panel = Page();
        panel.Children.Add(Heading(T("剪贴板","Clipboard")));
        panel.Children.Add(Hint(T("开启后，新复制的图片或文字会自动挂到晾衣绳上。晾衣绳自己复制的内容不会回流。",
            "When enabled, newly copied images or text are hung automatically. Clothesline's own copies are ignored.")));

        var images = new CheckBox { Content = T("自动挂上图片 / 截图","Automatically hang images / screenshots"), IsChecked = settings.ClipboardImages, VerticalAlignment = VerticalAlignment.Center };
        images.Checked += (_, _) => { settings.ClipboardImages = true; clipboardSequence = Native.GetClipboardSequenceNumber(); Persist(); };
        images.Unchecked += (_, _) => { settings.ClipboardImages = false; Persist(); };
        panel.Children.Add(Field(T("图片","Images"), images));

        var text = new CheckBox { Content = T("自动挂上文字便签","Automatically hang text notes"), IsChecked = settings.ClipboardText, VerticalAlignment = VerticalAlignment.Center };
        text.Checked += (_, _) => { settings.ClipboardText = true; clipboardSequence = Native.GetClipboardSequenceNumber(); Persist(); };
        text.Unchecked += (_, _) => { settings.ClipboardText = false; Persist(); };
        panel.Children.Add(Field(T("文字","Text"), text));

        var clear = new Button { Content = T("清空剪贴板挂载内容","Clear clipboard items"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(16, 5, 16, 5) };
        clear.Click += (_, _) => Guard(() => { ClearClipboard(); refresh(); });
        panel.Children.Add(clear);
        return panel;
    }

    // ---- Page: text recognition ----------------------------------------------

    private UIElement BuildOcr(Action refresh)
    {
        var panel = Page();
        panel.Children.Add(Heading(T("文字识别","Text Recognition")));

        var useV6 = settings.OcrEngine == "v6";
        var engine = new ComboBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        engine.Items.Add(T("Windows 自带识别（无需下载，识别率一般）","Windows built-in (no download, moderate accuracy)"));
        engine.Items.Add(T("PP-OCRv6（识别率明显更高）","PP-OCRv6 (much more accurate)"));
        engine.SelectedIndex = useV6 ? 1 : 0;
        engine.SelectionChanged += (_, _) =>
        {
            settings.OcrEngine = engine.SelectedIndex == 1 ? "v6" : "windows";
            Persist(); PaddleOcr.Reset(); refresh();
        };
        panel.Children.Add(Field(T("识别引擎","Engine"), engine,
            Hint(T("引擎只影响右键菜单里的“提取文字”，不影响其他功能。",
                "This only affects the \"Extract Text\" menu item. Nothing else uses it."))));

        panel.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 12) });
        return useV6 ? BuildOcrV6(panel, refresh) : BuildOcrWindows(panel, refresh);
    }

    private UIElement BuildOcrWindows(StackPanel panel, Action refresh)
    {
        panel.Children.Add(Heading(T("Windows 自带识别","Windows built-in recognition")));
        var installed = OcrLanguages();
        if (installed.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = T("系统还没有安装任何 OCR 语言包。请在 Windows“设置 → 时间和语言 → 语言和区域”里添加语言，并确保勾选了“光学字符识别”。",
                    "No OCR language pack is installed. In Windows Settings > Time & language > Language & region, add a language and make sure Optical character recognition is enabled."),
                FontSize = 12, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap
            });
            var open = new Button { Content = T("打开 Windows 语言设置","Open Windows language settings"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(16, 5, 16, 5) };
            open.Click += (_, _) => Native.Open("ms-settings:regionlanguage");
            panel.Children.Add(open);
            return panel;
        }

        var language = new ComboBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        language.Items.Add(T("自动选择","Auto"));
        foreach (var item in installed) language.Items.Add(item.Label);
        var chosen = -1;
        for (var i = 0; i < installed.Count; i++)
            if (string.Equals(installed[i].Tag, settings.OcrLanguage, StringComparison.OrdinalIgnoreCase)) { chosen = i; break; }
        language.SelectedIndex = chosen >= 0 ? chosen + 1 : 0;
        language.SelectionChanged += (_, _) =>
        {
            settings.OcrLanguage = language.SelectedIndex == 0 ? "" : installed[language.SelectedIndex - 1].Tag;
            Persist();
        };
        panel.Children.Add(Field(T("识别语言","Language"), language,
            Hint(T("中文引擎也能读英文，英文引擎读不了中文，所以有中文包时优先选中文。",
                "A Chinese engine also reads Latin text but not the other way round, so Chinese is preferred when installed."))));
        return panel;
    }

    private UIElement BuildOcrV6(StackPanel panel, Action refresh)
    {
        panel.Children.Add(Heading(T("PP-OCRv6 模型","PP-OCRv6 model")));
        panel.Children.Add(Hint(T("档位越高识别越准，体积和耗时也越大。Tiny 已随程序附带，无需下载。",
            "Higher tiers are more accurate but larger and slower. Tiny ships with the app and needs no download.")));

        var tiers = PaddleOcr.Tiers;
        var index = Array.IndexOf(tiers, settings.OcrModel);
        if (index < 0) { index = 0; settings.OcrModel = PaddleOcr.Tiny; Persist(); }
        var tier = tiers[index];

        var picker = new ComboBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var value in tiers)
            picker.Items.Add(PaddleOcr.DisplayName(value) + "　" + Bytes(PaddleOcr.InstalledBytes(value)));
        picker.SelectedIndex = index;
        picker.SelectionChanged += (_, _) =>
        {
            settings.OcrModel = tiers[picker.SelectedIndex];
            Persist(); PaddleOcr.Reset(); refresh();
        };
        panel.Children.Add(Field(T("模型档位","Model tier"), picker));

        var ready = PaddleOcr.IsReady(tier);
        // Tiny ships inside the installer, so asking it to "download" would be a lie.
        var needsNetwork = PaddleOcr.DownloadBytes(tier) > 0;
        var status = new TextBlock
        {
            Text = ready ? T("已就绪，可以直接使用。","Ready to use.")
                 : needsNetwork ? T("尚未下载，需要 ","Not downloaded yet. Requires ") + Bytes(PaddleOcr.DownloadBytes(tier)) + T(" 下载流量。"," of download.")
                                : T("首次使用时会自动从程序内解包，无需下载。","Unpacked from the installation on first use; no download needed."),
            FontSize = 12, Foreground = ready ? Brushes.DimGray : Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap
        };
        var bar = new ProgressBar { Height = 6, Margin = new Thickness(0, 10, 0, 4), Visibility = Visibility.Collapsed, Maximum = 1 };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var download = new Button
        {
            Content = ready ? T("重新准备","Prepare again")
                    : needsNetwork ? T("下载模型","Download model") : T("解包模型","Unpack model"),
            Padding = new Thickness(16, 5, 16, 5)
        };
        var delete = new Button { Content = T("删除","Delete"), Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(8, 0, 0, 0), IsEnabled = ready };
        buttons.Children.Add(download); buttons.Children.Add(delete);

        download.Click += async (_, _) => await GuardAsync(async () =>
        {
            download.IsEnabled = false; delete.IsEnabled = false; bar.Visibility = Visibility.Visible; bar.Value = 0;
            status.Text = T("正在下载…","Downloading…"); status.Foreground = Brushes.DimGray;
            try
            {
                // Progress<T> captures the dispatcher here, so the callback runs back on the UI thread.
                var progress = new Progress<double>(value => bar.Value = value);
                await PaddleOcr.EnsureAsync(settings.OcrModel, progress);
                PaddleOcr.Reset();
                status.Text = T("已就绪，可以直接使用。","Ready to use."); status.Foreground = Brushes.DimGray;
                delete.IsEnabled = true;
            }
            catch (Exception e)
            {
                bar.Visibility = Visibility.Collapsed;
                status.Text = e.Message; status.Foreground = Brushes.Firebrick;
                delete.IsEnabled = PaddleOcr.IsReady(settings.OcrModel);
            }
            finally { download.IsEnabled = true; }
        });

        delete.Click += (_, _) => Guard(() =>
        {
            PaddleOcr.Delete(tier); PaddleOcr.Reset(); refresh();
        });

        panel.Children.Add(status);
        panel.Children.Add(bar);
        panel.Children.Add(buttons);
        return panel;
    }

    // ---- Shared layout helpers -----------------------------------------------

    private static StackPanel Page() => new() { Margin = new Thickness(0) };
    private static TextBlock Heading(string text) => new()
    { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
    private static TextBlock Hint(string text) => new()
    { Text = text, FontSize = 12, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };

    private static Grid Field(string label, UIElement control, UIElement? hint = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 16, 0) };
        Grid.SetColumn(name, 0);
        var host = hint is null ? control : new StackPanel { Children = { control, hint } };
        Grid.SetColumn(host, 1);
        grid.Children.Add(name); grid.Children.Add(host);
        return grid;
    }

    private static string Bytes(long size) =>
        size >= 1L << 30 ? (size / (double)(1L << 30)).ToString("F1") + " GB"
                         : Math.Max(1, (long)Math.Round(size / (double)(1L << 20))) + " MB";
}
