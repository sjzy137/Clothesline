using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Clothesline.Core;

namespace Clothesline.Windows;
internal sealed partial class RopeWindow
{
    /// One clipboard text note, kept in memory and mirrored to a file under NotesDir.
    /// Plain holds the de-formatted text used for the card preview, search and plain-text copy;
    /// IsRtf marks notes that also carry rich formatting (stored as a .rtf file).
    private sealed class NoteEntry
    {
        public string Plain { get; set; } = "";
        public bool IsRtf { get; set; }
    }

    /// The persistent home for text notes. Unlike the old %TEMP% clipboard folder, notes here
    /// survive a restart; only un-favourited ones expire after a week.
    private string NotesDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clothesline", "Notes");

    /// Loads previously saved notes on startup and deletes un-favourited notes older than a week.
    /// Favourites are exempt from deletion and stay until the user removes them.
    private void LoadNotes()
    {
        var dir = NotesDir;
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir))
        {
            try
            {
                var name = Path.GetFileName(file);
                var isRtf = Path.GetExtension(file).Equals(".rtf", StringComparison.OrdinalIgnoreCase);
                var favourite = settings.TextFavorites.Contains(name, StringComparer.OrdinalIgnoreCase);
                if (!favourite && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(7))
                {
                    File.Delete(file);
                    continue;
                }
                var raw = File.ReadAllText(file);
                var plain = isRtf ? RtfToPlain(raw) : raw;
                if (plain.Length > 200000) plain = plain[..200000];
                clipboardItems[file] = new NoteEntry { Plain = plain, IsRtf = isRtf };
                var bytes = File.ReadAllBytes(file);
                clipboardHashes.Add((isRtf ? ".rtf" : ".txt") + Convert.ToHexString(SHA256.HashData(bytes)));
            }
            catch { /* a single corrupt note must not stop the rest from loading */ }
        }
        signature = ""; Render();
    }

    private bool IsFavorite(string path) => settings.TextFavorites.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    private void ToggleFavorite(string path)
    {
        Validate(new FileRecord(path, false, 0, DateTime.UtcNow, DateTime.UtcNow));
        var name = Path.GetFileName(path);
        if (IsFavorite(path))
        {
            settings.TextFavorites.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            // Dropping a favourite restarts its one-week countdown from now.
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
        }
        else settings.TextFavorites.Add(name);
        Persist(); signature = ""; Render();
    }

    /// Strips RTF control words down to readable text. Used only for the card preview and search;
    /// the full formatting is preserved when the note is viewed (RichTextBox loads the real .rtf).
    private static string RtfToPlain(string rtf)
    {
        if (string.IsNullOrWhiteSpace(rtf)) return "";
        var outChars = new List<char>();
        var n = rtf.Length;
        var i = 0;
        while (i < n)
        {
            var c = rtf[i];
            if (c == '\\')
            {
                i++;
                if (i >= n) break;
                var d = rtf[i];
                if (d == (char)39) { i += 3; continue; } // hexadecimal escape
                if (d == '{' || d == '}' || d == '\\') { outChars.Add(d); i++; continue; }
                while (i < n && char.IsLetter(rtf[i])) i++;      // control word name
                while (i < n && char.IsDigit(rtf[i])) i++;        // optional parameter
                if (i < n && (rtf[i] == ' ' || rtf[i] == '\n' || rtf[i] == '\r' || rtf[i] == '\t')) i++; // delimiter
                continue;
            }
            if (c == '{')
            {
                if (i + 1 < n && rtf[i + 1] == '*') // invisible {\*...} destination group
                {
                    var depth = 1; i += 2;
                    while (i < n && depth > 0) { if (rtf[i] == '{') depth++; else if (rtf[i] == '}') depth--; i++; }
                    continue;
                }
                i++; continue;
            }
            if (c == '}') { i++; continue; }
            outChars.Add(c); i++;
        }
        return new string(outChars.ToArray()).Replace("\0", "").Trim();
    }

    /// Read-only viewer for a text note. RTF notes load their formatting; plain notes load verbatim.
    private void ShowTextNote(FileRecord file)
    {
        Validate(file);
        var isRtf = clipboardItems.TryGetValue(file.Path, out var note) && note is not null && note.IsRtf;
        var window = new Window { Title = T("文字便签", "Text Note"), Width = 640, Height = 540, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Theme.Brush(CurrentTheme.CardSurface) };
        var panel = new DockPanel { Margin = new Thickness(14) };
        var rtb = new RichTextBox { IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0) };
        DockPanel.SetDock(rtb, Dock.Top); panel.Children.Add(rtb);
        if (isRtf)
        {
            try
            {
                using var stream = new MemoryStream(File.ReadAllBytes(file.Path));
                rtb.SelectAll();
                rtb.Selection.Load(stream, DataFormats.Rtf);
            }
            catch { rtb.AppendText(note!.Plain); }
        }
        else rtb.AppendText(File.ReadAllText(file.Path));
        window.Content = panel; window.Show();
    }

    /// Formats a note's age the way a list read at a glance wants it: relative while recent, then
    /// absolute with the date implied for today.
    private string NoteTime(DateTime time)
    {
        var local = time.ToLocalTime();
        if (DateTime.Now - local < TimeSpan.FromMinutes(1)) return T("刚刚", "just now");
        return (local.Date == DateTime.Today ? T("今天 ", "today ") : local.ToString("MM-dd ")) + local.ToString("HH:mm");
    }

    /// Removes a note for good: its file goes first, then every trace the rope keeps of it.
    private void DeleteNote(string path)
    {
        try { File.Delete(path); } catch { /* already gone is the outcome we wanted */ }
        clipboardItems.Remove(path);
        settings.TextFavorites.RemoveAll(n => string.Equals(n, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
        Persist(); signature = ""; Render();
    }

    /// The Text Envelope, drawn the way PicRope draws it: a card hanging beneath the rope with a notch
    /// pointing back at it, holding every collected note — searchable, with per-row actions for the
    /// things a note is actually for. Deliberately not a modal dialog: it belongs to the rope, closes
    /// with it, and reading or copying a note never means dismissing anything first.
    private void ShowTextEnvelope()
    {
        var theme = CurrentTheme;
        var text = Theme.Brush(theme.CardText);
        // A dimmed copy of the theme's text colour for things that support rather than carry meaning.
        var quiet = new SolidColorBrush(Color.FromArgb(170, text.Color.R, text.Color.G, text.Color.B));
        var closed = false;

        var window = new Window
        {
            Title = T("文字信封", "Text Envelope"), Owner = this, ShowInTaskbar = false, Topmost = true,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent
        };
        var root = new Grid();
        // The notch pointing back at the rope, so the card reads as hanging from it.
        var pointer = new System.Windows.Shapes.Polygon
        {
            Points = new PointCollection { new Point(0, 12), new Point(12, 0), new Point(24, 12) },
            Fill = Theme.Brush(theme.CardSurface), Width = 24, Height = 12,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        root.Children.Add(pointer);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        root.Children.Add(new Border
        {
            Child = body, Background = Theme.Brush(theme.CardSurface),
            BorderBrush = Theme.Brush(theme.CardBorder), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Margin = new Thickness(16, 11, 16, 16),
            Effect = new DropShadowEffect { Color = Color.FromRgb(38, 57, 76), BlurRadius = 22, ShadowDepth = 6, Opacity = .22 }
        });
        window.Content = root;

        // Header: a title, how many notes there are, and a place to report what just happened.
        var header = new Grid { Margin = new Thickness(18, 0, 14, 0) };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = T("文字信封", "Text Envelope"), FontSize = 16, FontWeight = FontWeights.Bold, Foreground = text });
        var countLabel = new TextBlock { FontSize = 12, Foreground = quiet };
        heading.Children.Add(new Border
        {
            Child = countLabel, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(8, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(28, text.Color.R, text.Color.G, text.Color.B))
        });
        var feedbackLabel = new TextBlock { FontSize = 11, Foreground = quiet, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(feedbackLabel);
        header.Children.Add(heading);
        var close = RowAction(ThemeIcons.Close(theme), T("关闭", "Close"), () => window.Close(), theme);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(close);
        body.Children.Add(header);

        // Search: a rounded box with its own placeholder, so no hint text can end up as a real value.
        var searchPanel = new Grid { Margin = new Thickness(18, 0, 18, 8) };
        var search = new TextBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(10, 5, 10, 5),
            VerticalContentAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = text };
        var placeholder = new TextBlock { Text = T("搜索文字", "Search text"), IsHitTestVisible = false, Margin = new Thickness(11, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = quiet };
        searchPanel.Children.Add(new Border { CornerRadius = new CornerRadius(7), BorderBrush = Theme.Brush(theme.CardBorder), BorderThickness = new Thickness(1), Child = search });
        searchPanel.Children.Add(placeholder);
        Grid.SetRow(searchPanel, 1); body.Children.Add(searchPanel);

        var list = new ListBox
        {
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(10, 0, 12, 4),
            SelectionMode = SelectionMode.Extended, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var empty = new TextBlock { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Foreground = quiet, Margin = new Thickness(24), FontSize = 13, Visibility = Visibility.Collapsed };
        var listPanel = new Grid(); listPanel.Children.Add(list); listPanel.Children.Add(empty);
        Grid.SetRow(listPanel, 2); body.Children.Add(listPanel);

        var batch = new Grid { Margin = new Thickness(18, 0, 18, 8) };
        var selectAll = Theme.Button(T("全选", "Select all"), () => { }, "quiet");
        selectAll.HorizontalAlignment = HorizontalAlignment.Left;
        var deleteSelected = Theme.Button(T("删除所选", "Delete selected"), () => { }, "quiet");
        deleteSelected.HorizontalAlignment = HorizontalAlignment.Right;
        deleteSelected.Foreground = Brushes.Firebrick;
        batch.Children.Add(selectAll); batch.Children.Add(deleteSelected);
        Grid.SetRow(batch, 3); body.Children.Add(batch);

        // A second click is what actually deletes. "Delete" alone is too easy to press by accident for
        // notes that took effort to collect, and there is no undo for a file that has gone.
        var confirming = false;
        void ResetConfirm()
        {
            if (!confirming) return;
            confirming = false;
            var picked = list.SelectedItems.Count;
            deleteSelected.Content = picked == 0 ? T("删除所选", "Delete selected") : T("删除所选（", "Delete selected (") + picked + "）";
        }
        void Report(string message)
        {
            feedbackLabel.Text = message;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { feedbackLabel.Text = ""; timer.Stop(); };
            timer.Start();
        }
        void Refresh()
        {
            if (closed) return;
            ResetConfirm();
            var query = search.Text.Trim();
            var notes = NoteRows();
            list.Items.Clear();
            foreach (var row in notes)
            {
                if (query.Length > 0 && !row.Plain.Contains(query, StringComparison.OrdinalIgnoreCase)
                    && !row.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                list.Items.Add(BuildNoteRow(row, theme, text, quiet, Refresh, Report));
            }
            countLabel.Text = notes.Length.ToString();
            selectAll.IsEnabled = list.Items.Count > 0;
            selectAll.Content = list.SelectedItems.Count > 0 && list.SelectedItems.Count == list.Items.Count
                ? T("取消全选", "Select none") : T("全选", "Select all");
            deleteSelected.IsEnabled = list.SelectedItems.Count > 0;
            empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            empty.Text = notes.Length == 0
                ? T("还没有文字便签。\n复制一段文字，它会自动收集到这里。", "No text notes yet.\nCopy some text and it is collected here.")
                : T("没有找到匹配的文字。", "No notes match.");
        }

        search.TextChanged += (_, _) => { placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Refresh(); };
        list.SelectionChanged += (_, _) => { ResetConfirm(); selectAll.Content = list.SelectedItems.Count > 0 && list.SelectedItems.Count == list.Items.Count ? T("取消全选", "Select none") : T("全选", "Select all"); deleteSelected.IsEnabled = list.SelectedItems.Count > 0; };
        selectAll.Click += (_, _) => { if (list.SelectedItems.Count > 0 && list.SelectedItems.Count == list.Items.Count) list.UnselectAll(); else list.SelectAll(); Refresh(); };
        deleteSelected.Click += (_, _) =>
        {
            var chosen = list.SelectedItems.OfType<Border>().Select(row => (string)row.Tag!).ToList();
            if (chosen.Count == 0) return;
            if (!confirming)
            {
                confirming = true;
                deleteSelected.Content = T("确认删除 ", "Confirm delete ") + chosen.Count + T(" 条", "");
                return;
            }
            foreach (var path in chosen) DeleteNote(path);
            confirming = false;
            if (window.IsVisible) Report(T("已删除 ", "Deleted ") + chosen.Count + T(" 条", ""));
            Refresh();
        };

        void Position()
        {
            if (closed) return;
            var screen = CurrentScreen();
            var scale = Native.MonitorScale(screen);
            if (scale <= 0) scale = 1;
            var area = screen.WorkingArea;
            // Hang it off the bottom edge of whatever the rope is currently drawing, centred on the
            // rope so the notch lands where the eye expects it. Screen values are pixels; a WPF window
            // is positioned in device-independent units, hence the monitor scale.
            var anchor = PointToScreen(new Point(Math.Min(Width / 2, 400), Math.Max(RenderSize.Height, CollapsedHeight)));
            var width = Math.Min(460, area.Width / scale - 16);
            var height = Math.Min(456, Math.Max(180, (area.Bottom - anchor.Y) / scale - 8));
            var left = Math.Max(area.Left / scale + 8, Math.Min(area.Right / scale - (width + 8), anchor.X / scale - (width - 76)));
            var top = Math.Max(area.Top / scale + 4, Math.Min(area.Bottom / scale - height, anchor.Y / scale + 4));
            window.Width = width; window.Height = height; window.Left = left; window.Top = top;
            pointer.Margin = new Thickness(Math.Max(30, Math.Min(width - 54, anchor.X / scale - left - 12)), 0, 0, 0);
        }
        // Several conflicting ideas of placement survive one way or another through the first layout
        // pass, so the final position is applied once the window really exists.
        window.Loaded += (_, _) => { Position(); Dispatcher.BeginInvoke(new Action(() => { Position(); window.Activate(); search.Focus(); }), System.Windows.Threading.DispatcherPriority.Loaded); };
        void RopeGone() { if (window.IsVisible) window.Close(); }
        EventHandler? locationMoved = null;
        SizeChangedEventHandler? sizeChanged = null;
        locationMoved = (_, _) => Dispatcher.BeginInvoke(new Action(Position));
        sizeChanged = (_, _) => Dispatcher.BeginInvoke(new Action(Position));
        LocationChanged += locationMoved;
        SizeChanged += sizeChanged;
        window.Deactivated += (_, _) => { if (window.IsVisible) window.Close(); };
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; window.Close(); } };
        RopeHidden += RopeGone;
        window.Closed += (_, _) => { closed = true; LocationChanged -= locationMoved; SizeChanged -= sizeChanged; RopeHidden -= RopeGone; };
        Refresh();
        window.Show();
    }
    /// One row of the envelope: the note itself, then the four things you ever do with one.
    private Border BuildNoteRow(NoteRow note, LineTheme theme, SolidColorBrush text, SolidColorBrush quiet, Action refresh, Action<string> report)
    {
        var grid = new Grid { Margin = new Thickness(12, 10, 8, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var textColumn = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        textColumn.Children.Add(new TextBlock
        {
            Text = note.Title, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 42, FontSize = 13, Foreground = text
        });
        textColumn.Children.Add(new TextBlock
        {
            Text = NoteTime(note.Stamp) + (IsFavorite(note.Path) ? "  ·  " + T("已收藏", "starred") : ""),
            FontSize = 11, Foreground = quiet, Margin = new Thickness(0, 4, 0, 0)
        });
        grid.Children.Add(textColumn);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(actions, 1); grid.Children.Add(actions);
        FileRecord OpenNote() => FileRecord.Read(note.Path) ?? throw new IOException(T("源文件不存在或无法访问。", "The original is missing or inaccessible."));
        actions.Children.Add(RowAction(ThemeIcons.Star(theme, IsFavorite(note.Path)), IsFavorite(note.Path) ? T("取消收藏", "Unstar") : T("收藏", "Star"),
            () => { ToggleFavorite(note.Path); report(IsFavorite(note.Path) ? T("已收藏", "Starred") : T("已取消收藏", "Unstarred")); refresh(); }, theme));
        actions.Children.Add(RowAction(ThemeIcons.Expand(theme), T("查看全文", "View full note"),
            () => Guard(() => ShowTextNote(OpenNote())), theme));
        actions.Children.Add(RowAction(ThemeIcons.Copy(theme), T("复制文字", "Copy text"),
            () => Guard(() => { CopyText(OpenNote()); report(T("已复制", "Copied")); }), theme));
        actions.Children.Add(RowAction(ThemeIcons.Trash(theme), T("删除", "Delete"),
            () => { DeleteNote(note.Path); report(T("已删除", "Deleted")); refresh(); }, theme));

        var row = new Border
        {
            Child = grid, Tag = note.Path, Background = Brushes.Transparent,
            BorderBrush = Theme.Brush(theme.CardBorder), BorderThickness = new Thickness(0, 0, 0, 1)
        };
        // A double click anywhere on the row opens the note in full, which is what the row is for.
        row.MouseLeftButtonUp += (_, e) => { if (e.ClickCount == 2) Guard(() => ShowTextNote(OpenNote())); };
        return row;
    }

    /// An icon-only row button. Vector rather than text so it stays crisp and reads the same at every
    /// size; the theme's accent fills it on hover.
    private static Button RowAction(UIElement icon, string tooltip, Action click, LineTheme theme)
    {
        var button = new Button
        {
            Content = icon, ToolTip = tooltip, Width = 26, Height = 28, Cursor = Cursors.Hand,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0)
        };
        button.MouseEnter += (_, _) => button.Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.Click += (_, _) => click();
        return button;
    }

    private NoteRow[] NoteRows()
    {
        return clipboardItems.Where(kv => kv.Value is not null).Select(kv =>
        {
            var flat = kv.Value!.Plain.Replace("\r", " ").Replace("\n", " ").Trim();
            DateTime stamp;
            try { stamp = File.GetLastWriteTime(kv.Key); } catch { stamp = DateTime.Now; }
            return new NoteRow { Path = kv.Key, Plain = kv.Value!.Plain, Title = flat.Length > 60 ? flat[..60] + "…" : flat, Stamp = stamp };
        }).OrderByDescending(row => row.Stamp).ToArray();
    }

    private sealed class NoteRow
    {
        public string Path = "", Plain = "", Title = "";
        public DateTime Stamp;
    }
}
