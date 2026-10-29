using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Clothesline.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Clothesline.Windows;
internal sealed partial class RopeWindow : Window
{
    /// Open and collapsed heights. The collapsed strip is what the pointer has to reach to bring the
    /// rope down, and it is deliberately thin so it never covers anything underneath.
    private const int RevealHeight = 178;
    private const int CollapsedHeight = 8;
    private const int VisibilityHotKeyId = 0x434C; // 'CL'
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".ico"];
    private static readonly string[] TextExtensions = [".txt", ".md", ".json", ".xml", ".csv", ".log", ".ini", ".cfg", ".yaml", ".yml"];
    private readonly Settings settings;
    private readonly Action save;
    private readonly FileIndex index = new();
    private readonly RopeState state = new();
    private readonly Dictionary<string, FileRecord> monitored = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NoteEntry?> clipboardItems = new(StringComparer.OrdinalIgnoreCase);
    private LineTheme CurrentTheme => LineTheme.FromId(settings.Appearance);
    private readonly HashSet<string> clipboardHashes = new();
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Canvas surface = new();
    private readonly StackPanel cards = new() { Orientation = Orientation.Horizontal };
    private readonly List<Border> cardViews = [];
    private readonly Dictionary<string, Border> cardFlashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer scanTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer pointerTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Forms.NotifyIcon? tray;
    private bool busy, scanning, closing;
    private string signature = "", failure = "";
    // Kept apart from failure, which Scan() rewrites on every pass and would erase this at once.
    private string hotKeyNotice = "";
    private double scrollOffset;
    private uint clipboardSequence;
    private bool revealed;
    // True while a card is being dragged out by the user, so the rope does not re-attach the same file
    // to itself when the drag is released back over the rope.
    private bool selfDragging;
    private DateTime edgeSince = DateTime.MaxValue;
    private DateTime awaySince = DateTime.MaxValue;
    private bool avoidingCaption;
    /// Top-edge summon band: the screen strip (including a top taskbar, if present) plus this many
    /// pixels into the work area that triggers the rope. In screen coordinates so it works no matter
    /// where the rope window sits — the robust half of PicRope's reveal logic.
    private const int TriggerBand = 10;
    /// A held-button drag entering this top screen band (in SCREEN coordinates) makes the rope slide
    /// down to catch the drop, well before the collapsed strip could be reached. This is the
    /// "drag a file to the top of the screen" gesture from Explorer / the desktop.
    private const int DragBand = 90;
    private Window? settingsWindow;
    private readonly Dictionary<string, PinWindow> deskWindows = [];
    private DispatcherTimer? clickTimer;
    private IntPtr handle;
    private const string OwnCopy = "Clothesline.OwnCopy";
    internal string T(string zh, string en) => settings.English ? en : zh;
    internal RopeWindow(Settings settings, Action save)
    {
        this.settings = settings; this.save = save;
        foreach (var pair in settings.Dismissed) state.Dismissed[pair.Key] = pair.Value;
        foreach (var path in settings.Pins) state.Pinned.Add(path);
        foreach (var path in settings.DeskPins) state.DeskPins.Add(path);
        Title = "Clothesline"; Width = SystemParameters.WorkArea.Width; Height = CollapsedHeight;
        Left = SystemParameters.WorkArea.Left; Top = SystemParameters.WorkArea.Top;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/icon.png"));
        surface.ClipToBounds=true;
        surface.PreviewMouseWheel+=(_,e)=> { scrollOffset=Math.Clamp(scrollOffset-e.Delta,0,Math.Max(0,cards.Children.Count*140-Width+20)); Canvas.SetLeft(cards,10-scrollOffset); e.Handled=true; };
        Content = surface; surface.Children.Add(cards); Canvas.SetTop(cards, 18);
        AllowDrop = true;
        // The rope is the drop target when it slides down to catch a drag. Making the surface and the
        // card row drop-enabled too means a file released anywhere on the rope — over a card or in a
        // gap — bubbles the DragDrop events up to the window handler and gets hung.
        surface.        AllowDrop = true;
        PreviewDragOver += (_, e) => { e.Effects = settings.DropToHang && !selfDragging && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { if (settings.DropToHang && !selfDragging && e.Data.GetData(DataFormats.FileDrop) is string[] paths) Guard(() => Attach(paths)); e.Handled = true; };
        Loaded += (_, _) => Start();
        Closed += (_, _) => { closing = true; if (handle != IntPtr.Zero) Native.UnregisterHotKey(handle, VisibilityHotKeyId); scanTimer.Stop(); pointerTimer.Stop(); foreach (var watcher in watchers) watcher.Dispose(); foreach (var w in deskWindows.Values) w.Close(); tray?.Dispose(); ClearClipboard(false, false); Application.Current.Shutdown(); };
    }
    private void Start()
    {
        Theme.Install();
        clipboardSequence = Native.GetClipboardSequenceNumber();
        tray = new Forms.NotifyIcon { Text = "Clothesline", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!), Visible = true };
        var menu = new Forms.ContextMenuStrip(); menu.Opening += (_, _) => BuildTray(menu); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => { settings.AlwaysVisible = !settings.AlwaysVisible; Persist(); };
        RebuildWatchers();
        RegisterHotKey();
        scanTimer.Tick += async (_, _) => { await Scan(); PollClipboard(); };
        pointerTimer.Tick += (_, _) => PollPointer();
        scanTimer.Start(); pointerTimer.Start(); _ = Scan();
        ReopenDeskPins();
        LoadNotes();
    }
    private void RegisterHotKey()
    {
        handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        if (HwndSource.FromHwnd(handle) is { } source) source.AddHook(WndProc);
        ApplyHotKey();
    }
    /// Separated from registration so the settings window can release and reclaim the combination
    /// without restarting: Ctrl+Alt+Up is also claimed by some Intel graphics drivers, and being
    /// able to give it back is the only way to leave those alone.
    internal void ApplyHotKey()
    {
        if (handle == IntPtr.Zero) return;
        Native.UnregisterHotKey(handle, VisibilityHotKeyId);
        hotKeyNotice = "";
        if (!settings.HotKeyEnabled) return;
        if (!Native.TryParseHotKey(settings.HotKey, out var modifiers, out var key))
        { hotKeyNotice = T("快捷键设置无效或为空，请在设置里重新设置。", "The hot key setting is invalid or empty; set it again in Settings."); return; }
        if (!Native.RegisterHotKey(handle, VisibilityHotKeyId, modifiers, key))
            hotKeyNotice = T("无法注册快捷键，可能已被其他程序占用。", "Could not register the hot key; another application may already use it.");
    }
    private IntPtr WndProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Native.HotKeyMessage && wParam.ToInt32() == VisibilityHotKeyId)
        { handled = true; Guard(ToggleVisibility); }
        return IntPtr.Zero;
    }
    private void ToggleVisibility()
    {
        settings.AlwaysVisible = !settings.AlwaysVisible;
        Persist();
        signature = ""; Render();
    }
    private void RebuildWatchers()
    {
        foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear();
        foreach (var folder in settings.Folders)
        {
            try
            {
                var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (!closing) _ = Scan(); }));
                watcher.Changed += changed; watcher.Created += changed; watcher.Deleted += changed;
                watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (!closing) _ = Scan(); }));
                watcher.Error += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (!closing) _ = Scan(); }));
                watcher.EnableRaisingEvents = true; watchers.Add(watcher);
            }
            catch (Exception e) { failure = folder + ": " + e.Message; }
        }
    }
    private async Task Scan()
    {
        if (scanning || closing) return;
        scanning = true;
        try
        {
            DrainPendingAttachments();
            var folders = settings.Folders.ToArray();
            var result = await Task.Run(() => { var files = index.Scan(folders, DateTime.UtcNow, out var errors); return (files, errors); });
            if (closing) return;
            if (busy) return; // Never replace a drag source or a card with an open menu.
            failure = string.Join("\n", result.errors);
            // Preserve last records when a watched folder is inaccessible, rather than treating it as empty.
            if (result.errors.Count == 0) monitored.Clear();
            foreach (var file in result.files) monitored[file.Path] = file;
            foreach (var key in monitored.Keys.Where(p => FileRecord.Read(p) is null).ToArray())
                if (!state.Pinned.Contains(key)) monitored.Remove(key);
            var recent = result.files.Select(f => f.Path).Concat(settings.Recent).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList();
            if (!recent.SequenceEqual(settings.Recent)) { settings.Recent = recent; Persist(); }
            Render();
        }
        catch (Exception e) { failure = e.Message; }
        finally { scanning = false; }
    }
    private List<FileRecord> Records()
    {
        var records = new Dictionary<string, FileRecord>(monitored, StringComparer.OrdinalIgnoreCase);
        foreach (var path in settings.Attached.Concat(state.Pinned).Concat(clipboardItems.Keys))
        {
            var fresh = FileRecord.Read(path);
            if (fresh is not null) records[path] = fresh;
            else if (state.Pinned.Contains(path) && settings.PinSnapshots.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) is {} old) records[path] = old;
        }
        return records.Values.ToList();
    }
    private void Persist()
    {
        settings.Dismissed = state.Dismissed.Where(p=>!clipboardItems.ContainsKey(p.Key)).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase); settings.Pins = state.Pinned.ToList(); settings.DeskPins = state.DeskPins.ToList();
        settings.PinSnapshots = state.Pinned.Select(p => FileRecord.Read(p) ?? settings.PinSnapshots.FirstOrDefault(f => string.Equals(f.Path,p,StringComparison.OrdinalIgnoreCase))).OfType<FileRecord>().ToList();
        try { save(); } catch (Exception e) { failure = e.Message; }
    }
    private void Attach(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var file = FileRecord.Read(path); if (file is null) continue;
            if (!settings.Attached.Contains(file.Path, StringComparer.OrdinalIgnoreCase)) settings.Attached.Insert(0, file.Path);
            state.Dismissed.Remove(file.Path);
            settings.Recent.RemoveAll(p=>string.Equals(p,file.Path,StringComparison.OrdinalIgnoreCase)); settings.Recent.Insert(0,file.Path);
            settings.Recent=settings.Recent.Take(200).ToList();
        }
        Persist(); signature = ""; Render();
    }
    /// The Explorer context menu entry starts a second copy that exits at once, because only one
    /// instance may run. Those copies leave their file paths here and the running instance picks them
    /// up on its next scan, so a multi-file right-click still hangs everything that was selected.
    private void DrainPendingAttachments()
    {
        var directory = Program.PendingDirectory;
        if (!Directory.Exists(directory)) return;
        var pending = new List<string>();
        foreach (var file in Directory.GetFiles(directory, "pending-*.txt"))
        {
            try
            {
                pending.AddRange(File.ReadAllLines(file).Where(p => !string.IsNullOrWhiteSpace(p)));
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (pending.Count > 0) Attach(pending);
    }
    private void Render()
    {
        if (busy) return;
        var files = state.Select(Records(), settings.Capacity);
        var key = settings.English + ":" + Width + string.Join("|", files.Select(f => f.Path + f.Fingerprint + state.Pinned.Contains(f.Path) + (FileRecord.Read(f.Path) is null))) + clipboardItems.Count;
        if (signature == key) return; signature = key;
        cards.Children.Clear(); cardViews.Clear(); cardFlashes.Clear();
        foreach (var file in files) cards.Children.Add(CreateCard(file, cards.Children.Count));
        for (var i = surface.Children.Count - 1; i >= 0; i--) if (surface.Children[i] != cards) surface.Children.RemoveAt(i);
        var theme = CurrentTheme;
        var rope = new System.Windows.Shapes.Path {
            Data = new PathGeometry([new PathFigure(new Point(0,10), [new QuadraticBezierSegment(new Point(Width/2,32),new Point(Width,10),true)],false)]) };
        RopeLine.Apply(rope, theme);
        surface.Children.Insert(0, rope);
        // Wheel scrolling keeps overflow and pinned cards reachable.
        cards.Width = double.NaN;
        Canvas.SetLeft(cards, 10-scrollOffset);
    }
    private UIElement CreateCard(FileRecord file, int position)
    {
        var stack = new StackPanel { Width = 130, Margin = new Thickness(5,0,5,12) };
        var clip = new ClothesClip(CurrentTheme, position) { HorizontalAlignment = HorizontalAlignment.Center };
        var clipBox = new Viewbox { Child = clip, Width = 13, Height = 24, Margin = new Thickness(0,0,0,-7), HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        stack.Children.Add(clipBox);
        var body = new StackPanel();
        var missing = FileRecord.Read(file.Path) is null;
        var pinned = state.Pinned.Contains(file.Path);
        if (clipboardItems.TryGetValue(file.Path,out var note) && note is not null)
            body.Children.Add(new TextBlock { Text=note.Plain, TextWrapping=TextWrapping.Wrap, Height=80, Margin=new Thickness(5), Background=Theme.Brush(CurrentTheme.NotePaper) });
        else body.Children.Add(new Image { Source=Native.Thumbnail(file.Path), Width=106, Height=85, Stretch=Stretch.Uniform });
        body.Children.Add(new TextBlock { Text=note is not null ? T("文字便签","Text Note") : file.Name, TextTrimming=TextTrimming.CharacterEllipsis, FontSize=10, TextAlignment=TextAlignment.Center, Margin=new Thickness(3) });
        if (missing || pinned) body.Children.Add(new TextBlock { Text=missing ? T("文件失效","Unavailable") : T("已固定","Pinned"), FontSize=10, Foreground=missing ? Brushes.Firebrick : Brushes.SlateGray, TextAlignment=TextAlignment.Center });
        var card = new Border { Child=body, Background=Theme.Brush(CurrentTheme.CardSurface), BorderBrush=Theme.Brush(CurrentTheme.CardBorder), BorderThickness=new Thickness(1), CornerRadius=new CornerRadius(12), Padding=new Thickness(7),
            Effect=new DropShadowEffect { BlurRadius=12, ShadowDepth=4, Opacity=.2 }, AllowDrop=true };
        var name = note is not null ? T("文字便签","Text Note") : file.Name;
        card.ToolTip = name + "\n" + (clipboardItems.ContainsKey(file.Path) ? T("剪贴板（本次会话）","Clipboard (this session)") : System.IO.Path.GetDirectoryName(file.Path)) +
            "\n" + (file.IsDirectory ? T("文件夹（不递归计算大小）","Folder (contents not counted)") : $"{file.Size:N0} bytes") + "\n" + T("创建：","Created: ") + file.Created.ToLocalTime() + "\n" + T("修改：","Modified: ") + file.Modified.ToLocalTime() + (missing ? "\n" + T("源文件不存在或无法访问","Original is missing or inaccessible") : "");
        ToolTipService.SetInitialShowDelay(card,400); ToolTipService.SetShowDuration(card,30000);

        var flash = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(9,3,9,3),
            Background = new SolidColorBrush(Color.FromArgb(215,32,36,42)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            Child = new TextBlock { Foreground = Brushes.White, FontSize = 11 }
        };
        var holder = new Grid();
        holder.Children.Add(card); holder.Children.Add(flash);
        if (settings.CardButtons)
        {
            var pinButton = CardButton(pinned ? "●" : "○", pinned ? T("取消固定","Unpin") : T("固定","Pin"),
                () => Guard(() => { if (!state.Pinned.Remove(file.Path)) state.Pinned.Add(file.Path); Persist(); signature=""; Render(); }));
            var closeButton = CardButton("×", T("取下（保留原文件）","Take Down (Keep Original)"),
                () => Guard(() => { state.Dismiss(file); Persist(); signature=""; Render(); }));
            var pinBar = new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Left, VerticalAlignment=VerticalAlignment.Top, Margin=new Thickness(3,3,0,0) };
            pinBar.Children.Add(pinButton);
            var closeBar = new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right, VerticalAlignment=VerticalAlignment.Top, Margin=new Thickness(0,3,3,0) };
            closeBar.Children.Add(closeButton);
            if (IsImage(file.Path) && !file.IsDirectory)
            {
                var desk = CardButton("钉", T("钉到桌面（小夹子）","Pin to desktop (clip)"),
                    () => Guard(() => DeskPin(file)));
                pinBar.Children.Add(desk);
            }
            // Buttons are siblings of the card, not children, so their clicks never route through
            // the card handlers and cannot start a drag.
            holder.Children.Add(pinBar); holder.Children.Add(closeBar);
        }
        stack.Children.Add(holder);

        Point down = new(); bool pressed = false;
        card.MouseLeftButtonDown += (_,e) => {
            down=e.GetPosition(card); pressed=true; busy=true; card.CaptureMouse();
            if (e.ClickCount==2) { CancelPendingClick(); pressed=false; card.ReleaseMouseCapture(); busy=false; Guard(()=>Open(file)); }
        };
        card.MouseLeftButtonUp += (_,e) => {
            pressed=false; card.ReleaseMouseCapture(); busy=false;
            if (e.ClickCount==1 && (e.GetPosition(card)-down).Length<6) ScheduleClick(file);
        };
        card.LostMouseCapture += (_,_) => { if (pressed) { pressed=false; busy=false; } };
        card.MouseMove += (_,e) => {
            if (!pressed || e.LeftButton!=MouseButtonState.Pressed) return;
            var here=e.GetPosition(card); if ((here-down).Length<6) return;
            pressed=false; card.ReleaseMouseCapture(); CancelPendingClick();
            Guard(()=> {
                try
                {
                    Validate(file); Native.ClickThrough(this,false);
                    selfDragging=true;
                    var moving=settings.CtrlDragMoves && (Keyboard.Modifiers & ModifierKeys.Control)!=0;
                    var payload=FilePayload(file.Path);
                    // CF_HDROP alone only ever copies. This asks the drop target to move the file.
                    if (moving) Native.MarkAsMove(payload);
                    var outcome=DragDrop.DoDragDrop(card,payload,moving ? DragDropEffects.Copy|DragDropEffects.Move : DragDropEffects.Copy);
                    // A drop that went nowhere must leave everything alone. Anything the target
                    // accepted is then finished as a move, which may mean doing it here.
                    if (moving && outcome is DragDropEffects.Copy or DragDropEffects.Move) CompleteMove(file);
                }
                finally { selfDragging=false; busy=false; signature=""; Render(); }
            });
        };
        card.ContextMenu = CardMenu(file);
        card.ContextMenuOpening += (_,_) => { CancelPendingClick(); busy=true; };
        card.ContextMenuClosing += (_,_) => { busy=false; signature=""; Dispatcher.BeginInvoke(new Action(Render)); };
        cardViews.Add(card); cardFlashes[file.Path]=flash; return stack;
    }

    /// Finishes a Ctrl+drag once the drop target has accepted the file.
    ///
    /// The preferred drop effect is only a request. Explorer acts on it between its own windows but
    /// not for a CF_HDROP handed over by another application, so the usual outcome of a Ctrl+drag
    /// out of the clothesline is a copy with the original still in place. There is no way to ask
    /// where the copy landed, so the source has to remove the original itself.
    ///
    /// Either the target already moved it, in which case the original is gone and there is nothing
    /// left to do, or it copied, in which case the original goes to the Recycle Bin. The bin is
    /// deliberate: the app cannot verify the target's copy exists, and a bin entry keeps the one
    /// unrecoverable case recoverable.
    private void CompleteMove(FileRecord file)
    {
        if (FileRecord.Read(file.Path) is null)
        {
            state.Dismiss(file); Persist();
            Notice(T("已移动", "Moved"), T("文件已移动到目标位置。", "The file was moved to the drop target."));
            return;
        }
        if (!Native.Recycle(file.Path))
        {
            Notice(T("未能完成移动", "Move not completed"),
                T("目标位置已收到副本，但原文件无法移入回收站，卡片保留在晾衣绳上。",
                  "The drop target received a copy, but the original could not be sent to the Recycle Bin, so the card stays on the clothesline."));
            return;
        }
        state.Dismiss(file); Persist();
        Notice(T("已移动到目标位置", "Moved to the drop target"),
            T("原文件已移入回收站，需要时可从中恢复。",
              "The original was sent to the Recycle Bin and can be restored from there."));
    }

    /// The clothesline owns no window of its own, so the tray balloon is the only place a completed
    /// background action can report itself without stealing the pointer.
    private void Notice(string title, string message)
    {
        if (tray is null) return;
        tray.BalloonTipTitle = title;
        tray.BalloonTipText = message;
        tray.ShowBalloonTip(4000);
    }

    /// Pins an image to the desktop as an enlarged floating thumbnail (the "小夹子" feature). The
    /// card stays on the rope, so its right-click menu keeps working. Re-pinning an already-pinned
    /// file just brings its window to the front.
    private void DeskPin(FileRecord file)
    {
        if (deskWindows.TryGetValue(file.Path, out var existing))
        {
            existing.Activate();
            return;
        }
        var window = new PinWindow(file, CurrentTheme, path =>
        {
            if (deskWindows.TryGetValue(path, out var w)) { deskWindows.Remove(path); w.Close(); }
            state.DeskPins.Remove(path); Persist();
        });
        deskWindows[file.Path] = window;
        state.DeskPins.Add(file.Path); Persist();
        window.Show();
    }

    /// Reopens the floating desktop pins that were persisted the last time the app ran.
    private void ReopenDeskPins()
    {
        foreach (var path in state.DeskPins.ToArray())
        {
            var file = FileRecord.Read(path);
            if (file is null) { state.DeskPins.Remove(path); continue; }
            if (deskWindows.ContainsKey(path)) continue;
            var window = new PinWindow(file, CurrentTheme, p =>
            {
                if (deskWindows.TryGetValue(p, out var w)) { deskWindows.Remove(p); w.Close(); }
                state.DeskPins.Remove(p); Persist();
            });
            deskWindows[path] = window;
            window.Show();
        }
        if (state.DeskPins.Count > 0) Persist();
    }

    private static bool IsImage(string path) => Array.Exists(ImageExtensions, e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    private Button CardButton(string glyph, string tooltip, Action click)
    {
        var theme = CurrentTheme;
        var button = new Button
        {
            Content=glyph, FontSize=11, FontWeight=FontWeights.Bold, Width=18, Height=18,
            Padding=new Thickness(0), BorderThickness=new Thickness(0),
            Background=new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.CardSurface)) { Opacity = 0.92 },
            Foreground=Theme.Brush(theme.CardText),
            Cursor=Cursors.Hand, ToolTip=tooltip, Opacity=.6
        };
        button.Click += (_,_) => click();
        button.MouseEnter += (_,_) => { button.Opacity=1; button.Background=Theme.Brush(theme.Accent); button.Foreground=Brushes.White; };
        button.MouseLeave += (_,_) => { button.Opacity=.6; button.Background=new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.CardSurface)) { Opacity = 0.92 }; button.Foreground=Theme.Brush(theme.CardText); };
        return button;
    }
    /// Waits out the system double-click window, so opening a file never also overwrites the clipboard.
    private void ScheduleClick(FileRecord file)
    {
        if (!settings.ClickToCopy) return;
        CancelPendingClick();
        clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Forms.SystemInformation.DoubleClickTime + 60) };
        clickTimer.Tick += (_,_) => { CancelPendingClick(); Guard(()=> { CopyToClipboard(file); FlashCard(file.Path,T("已复制","Copied")); }); };
        clickTimer.Start();
    }
    private void CancelPendingClick()
    {
        if (clickTimer is null) return;
        clickTimer.Stop(); clickTimer=null;
    }
    private void CopyToClipboard(FileRecord file)
    {
        Validate(file);
        var data = new DataObject();
        if (IsImage(file.Path)) data.SetImage(Native.ReadImage(file.Path));
        else if (clipboardItems.TryGetValue(file.Path, out var text) && text is not null)
        {
            data.SetText(text.Plain);
            if (text.IsRtf && File.Exists(file.Path)) data.SetData(DataFormats.Rtf, File.ReadAllText(file.Path));
        }
        else data.SetFileDropList(new StringCollection { file.Path });
        // Stops the clipboard monitor from hanging the app's own copy straight back on the line.
        data.SetData(OwnCopy, new MemoryStream(new byte[] { 1 }));
        Clipboard.SetDataObject(data,true); clipboardSequence=Native.GetClipboardSequenceNumber();
    }
    private void FlashCard(string path,string message)
    { if (cardFlashes.TryGetValue(path, out var flash)) FlashLabel(flash,message); }
    private static void FlashLabel(Border flash, string message)
    {
        if (flash.Child is not TextBlock label) return;
        label.Text=message; label.Opacity=1; flash.Visibility=Visibility.Visible;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        var held = 0;
        timer.Tick += (_,_) => {
            if (held++ < 8) return;
            label.Opacity -= .25;
            if (label.Opacity > 0) return;
            timer.Stop(); flash.Visibility=Visibility.Collapsed;
        };
        timer.Start();
    }
    /// SetFileDropList is deliberate: it reliably produces CF_HDROP, which is what Explorer and
    /// every other drop target understands.
    internal static DataObject FilePayload(string path)
    { var data = new DataObject(); data.SetFileDropList(new StringCollection { path }); return data; }
    internal static bool IsText(string path) => TextExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
    private void Validate(FileRecord file)
    { if (FileRecord.Read(file.Path) is null) throw new IOException(T("源文件不存在或无法访问。","The original file is missing or inaccessible.")); }
    private void Open(FileRecord file)
    { Validate(file); if (clipboardItems.TryGetValue(file.Path,out var note) && note is not null) ShowTextNote(file); else Native.Open(file.Path); }
    private void Guard(Action action) { try { action(); } catch (Exception e) { MessageBox.Show(e.Message,"Clothesline"); } }
    private void PollPointer()
    {
        if (busy) return;
        var cursor = Native.Cursor;
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)cursor.X, (int)cursor.Y));
        bool down = Native.MousePressed;
        // Top-edge summon zone in SCREEN coordinates: from the very top of the monitor, through any top
        // taskbar, plus a few pixels into the work area. Robust regardless of where the rope window
        // sits (the fix for "can't summon at the top"); this is the reliable half of PicRope's logic.
        bool nearTop = cursor.X >= screen.Bounds.Left && cursor.X < screen.Bounds.Right
            && cursor.Y >= screen.Bounds.Top && cursor.Y <= screen.WorkingArea.Top + TriggerBand;
        // A held button (a real drag from Explorer / the desktop) entering the top screen band makes the
        // rope slide down to catch it — in SCREEN coordinates, so it triggers well before the cursor
        // could reach the collapsed strip. This is the "drag a file to the top of the screen" gesture.
        bool dragAtTop = down && settings.DropToHang
            && cursor.X >= screen.Bounds.Left && cursor.X < screen.Bounds.Right
            && cursor.Y >= screen.Bounds.Top && cursor.Y <= screen.Bounds.Top + DragBand;
        // Poll faster while the rope is in play, like PicRope, to keep the summon snappy without
        // burning CPU while it is tucked away.
        var interval = revealed || nearTop || dragAtTop ? 30 : 100;
        if (pointerTimer.Interval.TotalMilliseconds != interval) pointerTimer.Interval = TimeSpan.FromMilliseconds(interval);
        var local = PointFromScreen(cursor);
        bool overCard = revealed && local.X >= 0 && local.X <= Width && local.Y >= 0 && local.Y <= Height
            && cardViews.Any(v => { var p = v.TranslatePoint(new Point(), this); return new Rect(p, v.RenderSize).Contains(local); });
        // A held button in the rope's drop zone means a file is being dragged onto it. It is not gated
        // on the rope already being open, because a collapsed rope offers nothing to aim at — keeping
        // it open lets the drop land (a feature beyond PicRope, which only drags out).
        bool dragging = down && settings.DropToHang && (dragAtTop || (local.X >= 0 && local.X <= Width && local.Y >= 0 && local.Y <= RevealHeight));
        var now = DateTime.UtcNow;
        bool show;

        if (settings.AlwaysVisible) show = true;
        else if ((revealed || nearTop || avoidingCaption) && Native.OverCaptionButtons(handle))
        {
            // Step aside from a maximised window's min/max/close buttons so the rope never covers them
            // or flickers while the user clicks — mirrors PicRope's caption avoidance; kills the flash.
            edgeSince = DateTime.MaxValue;
            if (revealed) avoidingCaption = true;
            show = false;
        }
        else if (avoidingCaption && !down)
        {
            avoidingCaption = false;
            show = local.X >= 0 && local.X <= Width && local.Y >= 0 && local.Y <= RevealHeight;
        }
        else
        {
            // A true exclusive full-screen window (game / video) spans the whole monitor including the
            // taskbar, so unlike a maximised window it hides the rope. Gated by the adjustable HideFullScreen.
            bool covered = settings.HideFullScreen && Native.ForegroundCoversMonitor();
            if (covered) show = false;
            else if (dragging) { awaySince = DateTime.MaxValue; show = true; }
            else if (nearTop && !down)
            {
                if (edgeSince == DateTime.MaxValue) edgeSince = now;
                var dwell = settings.RevealDwell > 0 ? settings.RevealDwell : 0;
                show = now - edgeSince >= TimeSpan.FromMilliseconds(dwell);
            }
            else
            {
                edgeSince = DateTime.MaxValue;
                if (revealed)
                {
                    bool inside = local.X >= 0 && local.X <= Width && local.Y >= 0 && local.Y <= RevealHeight;
                    if (inside) { awaySince = DateTime.MaxValue; show = true; }
                    else
                    {
                        if (awaySince == DateTime.MaxValue) awaySince = now;
                        var grace = settings.RevealDelay > 0 ? settings.RevealDelay : 1200;
                        show = now - awaySince <= TimeSpan.FromMilliseconds(grace);
                    }
                }
                else show = false;
            }
        }

        SetReveal(show, overCard, dragging);
    }

    private void SetReveal(bool shown, bool overCard, bool dragging)
    {
        if (shown != revealed)
        {
            revealed = shown;
            var target = shown ? RevealHeight : CollapsedHeight;
            // A short slide so the rope visibly "comes down to catch" a drag; the logical state flips at
            // once so the drop target and click-through are already correct for the whole animation.
            if (PresentationSource.FromVisual(this) is not null)
                BeginAnimation(HeightProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(shown ? 150 : 110))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else Height = target;
        }
        // Capture only while revealed over a card or while a drag is in flight, so empty rope areas
        // stay click-through and a window title underneath can still be grabbed; the drop target is
        // live exactly when a file is being dragged over.
        Native.ClickThrough(this, !revealed || !(overCard || dragging));
    }
    private void ClearClipboard(bool refresh=true, bool deleteFiles=true)
    {
        foreach (var path in clipboardItems.Keys)
        {
            state.Dismissed.Remove(path); state.Pinned.Remove(path);
            // Favourited notes are kept on disk; only un-favourited ones are removed.
            if (deleteFiles && !IsFavorite(path)) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        }
        clipboardItems.Clear(); clipboardHashes.Clear();
        if (refresh) { signature=""; Persist(); Render(); }
    }
    private void PollClipboard()
    {
        if (!settings.ClipboardImages && !settings.ClipboardText) { clipboardSequence=Native.GetClipboardSequenceNumber(); return; }
        var sequence=Native.GetClipboardSequenceNumber(); if (sequence==clipboardSequence) return;
        try
        {
            if (Clipboard.ContainsData(OwnCopy) || Clipboard.ContainsFileDropList()) { clipboardSequence=sequence; return; }
            byte[]? bytes=null; string? plain=null; string? rtf=null; string extension=".png";
            if (settings.ClipboardImages && Clipboard.ContainsImage())
            {
                var bitmap=Clipboard.GetImage(); if (bitmap is null || (long)bitmap.PixelWidth*bitmap.PixelHeight>32000000) { clipboardSequence=sequence; return; }
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream=new MemoryStream(); encoder.Save(stream); bytes=stream.ToArray();
                if (bytes.Length>32000000) { clipboardSequence=sequence; return; }
            }
            else if (settings.ClipboardText && !Clipboard.ContainsImage() && Clipboard.ContainsText())
            {
                plain=Clipboard.GetText();
                if (Clipboard.GetData(DataFormats.Rtf) is string rtfRaw && !string.IsNullOrWhiteSpace(rtfRaw)) rtf=rtfRaw;
                extension=rtf is not null ? ".rtf" : ".txt";
                bytes=Encoding.UTF8.GetBytes(plain);
                if (string.IsNullOrWhiteSpace(plain) || bytes.Length>200000) { clipboardSequence=sequence; return; }
            }
            if (bytes is null) { clipboardSequence=sequence; return; }
            var hash=extension+Convert.ToHexString(SHA256.HashData(bytes));
            if (clipboardHashes.Contains(hash) || clipboardItems.Count>=100) { clipboardSequence=sequence; return; }
            var notesDir=NotesDir; Directory.CreateDirectory(notesDir);
            var security=new DirectorySecurity(); security.SetAccessRuleProtection(true,false);
            var owner=WindowsIdentity.GetCurrent().User!; security.SetOwner(owner);
            security.AddAccessRule(new FileSystemAccessRule(owner,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            new DirectoryInfo(notesDir).SetAccessControl(security);
            var path=System.IO.Path.Combine(notesDir,Guid.NewGuid()+extension); File.WriteAllBytes(path,bytes);
            if (extension==".png") clipboardItems[path]=null;
            else clipboardItems[path]=new NoteEntry { Plain=plain!, IsRtf=rtf is not null };
            clipboardHashes.Add(hash); clipboardSequence=sequence; signature=""; Render();
        }
        catch (ExternalException) { } // Retry the same sequence if another app temporarily owns the clipboard.
        catch (Exception e) { clipboardSequence=sequence; failure=e.Message; }
    }
}
