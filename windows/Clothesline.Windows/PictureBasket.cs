using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Clothesline.Core;

namespace Clothesline.Windows;

/// The basket hanging off the end of the rope, drawn from PicRope's basket pictures (MIT). Each
/// theme ships its own picture; they are pre-cropped and downscaled so five of them add only
/// ~220 KB instead of the multi-megabyte originals.
internal static class BasketSkin
{
    static readonly Dictionary<string, BitmapSource> pictures = new(StringComparer.OrdinalIgnoreCase);
    /// Where the handle meets the hook, as a fraction of the picture. Hanging the pendant by this
    /// point keeps it attached to the rope even as the picture is scaled or a theme is swapped.
    static readonly Dictionary<string, (double x, double y)> hooks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["classic"] = (0.4718, 0.0622), ["linen"] = (0.4643, 0.0600), ["porcelain"] = (0.4333, 0.0800),
        ["color"] = (0.4746, 0.0597), ["graphite"] = (0.4509, 0.0588)
    };

    public static ImageSource? Source(LineTheme theme)
    {
        if (pictures.TryGetValue(theme.Id, out var cached)) return cached;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // Lower case: the compiler lower-cases resource paths, and matching is far safer than
            // relying on ResourceManager's case-insensitive fallback.
            image.UriSource = new Uri("pack://application:,,,/assets/picrope/basket-" + theme.Id + ".png");
            image.EndInit();
            image.Freeze();
            pictures[theme.Id] = image;
            return image;
        }
        catch (Exception)
        {
            // A missing or unreadable picture must never take the rope down; the pendant simply falls
            // back to the vector outline below.
            return null;
        }
    }

    public static Point Hook(LineTheme theme) =>
        hooks.TryGetValue(theme.Id, out var point) ? new Point(point.x, point.y) : new Point(0.47, 0.06);

    public static double Aspect(LineTheme theme) =>
        Source(theme) is BitmapSource bitmap && bitmap.PixelWidth > 0 ? bitmap.PixelHeight / (double)bitmap.PixelWidth : 0.9;

    /// Drawn stand-in used when the packaged picture is unavailable.
    public static UIElement Fallback(LineTheme theme, double width)
    {
        var body = new System.Windows.Shapes.Path
        {
            Width = width, Height = width * 0.8, Stretch = Stretch.Uniform,
            Stroke = Theme.Brush(theme.RopeColor), StrokeThickness = 2, Fill = Theme.Brush(theme.ClipColors[0]),
            Data = Geometry.Parse("M2,2 L38,2 L34,40 L6,40 Z")
        };
        return new Border { Child = body, Background = Brushes.Transparent };
    }
}

internal sealed partial class RopeWindow
{
    private Window? basketWindow;
    private UIElement? basketPendant;

    /// The archive caps itself so it can never grow without bound on a machine that hangs hundreds of
    /// screenshots. Starred pictures are spared; the oldest un-starred ones go first.
    private const int BasketCapacity = 400;

    /// Record a picture in the basket. Called the moment anything image-shaped is hung, so the basket
    /// fills itself without the user having to do anything. Re-hanging the same file moves it back to
    /// the front rather than duplicating it.
    private void RememberPicture(string path)
    {
        if (!IsImage(path)) return;
        var existing = settings.Pictures.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { settings.Pictures.Remove(existing); settings.Pictures.Insert(0, existing); }
        else settings.Pictures.Insert(0, new BasketPicture { Path = path, Added = DateTime.UtcNow });
        TrimPictures();
    }
    private void TrimPictures()
    {
        var overflow = settings.Pictures.Count - BasketCapacity;
        // Newest first, so counting backwards removes the oldest un-starred pictures first.
        for (var i = settings.Pictures.Count - 1; i >= 0 && overflow > 0; i--)
            if (!settings.Pictures[i].Starred) { settings.Pictures.RemoveAt(i); overflow--; }
    }

    internal void ShowPictureBasket()
    {
        if (basketWindow is { } open) { open.Activate(); return; }
        var theme = CurrentTheme;
        var window = new Window
        {
            Title = T("图片篮子", "Picture Basket"), Owner = this, Width = 920, Height = 620, MinWidth = 640, MinHeight = 440,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Theme.Brush(theme.CardSurface), FontFamily = new FontFamily("Microsoft YaHei UI"),
            ResizeMode = ResizeMode.CanResize
        };
        try { window.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/icon.png")); } catch (Exception) { }

        var root = new Grid();
        // Declared up front: the lambdas and local functions below all capture these, and C# requires
        // a captured local to be declared before the code that closes over it.
        bool starredOnly = false;
        BasketPicture? selectedPicture = null;
        // Rebuilding touches nearly every control below, so the first rebuild has to wait until they
        // all exist. Holding it behind a delegate keeps the buttons declarative and avoids forcing a
        // pile of controls to be declared before the layout that owns them.
        Action? rebuild = null;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        window.Content = root;

        // ── header ────────────────────────────────────────────────────────────────────────────────
        var header = new Grid { Margin = new Thickness(20, 16, 20, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = T("图片篮子", "Picture Basket"), FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Theme.Brush(theme.CardText), VerticalAlignment = VerticalAlignment.Center });
        var summary = new TextBlock { FontSize = 12, Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 2, 0, 0) };
        heading.Children.Add(summary);
        header.Children.Add(heading);
        var search = new TextBox { Width = 190, Padding = new Thickness(8, 5, 8, 5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), ToolTip = T("按文件名搜索", "Search by file name") };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(search);
        tools.Children.Add(Theme.Button(T("关闭", "Close"), () => window.Close()));
        Grid.SetColumn(tools, 1); header.Children.Add(tools);
        Grid.SetRow(header, 0); root.Children.Add(header);

        // ── toolbar ───────────────────────────────────────────────────────────────────────────────
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 20, 10) };
        var allTab = Theme.Button(T("全部", "All"), () => { starredOnly = false; rebuild?.Invoke(); });
        var starTab = Theme.Button(T("收藏", "Starred"), () => { starredOnly = true; rebuild?.Invoke(); });
        allTab.Margin = new Thickness(0, 0, 8, 0);
        toolbar.Children.Add(allTab); toolbar.Children.Add(starTab);
        Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);

        // ── content: thumbnails + preview ─────────────────────────────────────────────────────────
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        var thumbnails = new WrapPanel { Margin = new Thickness(16, 4, 12, 12) };
        var scroller = new ScrollViewer { Content = thumbnails, Margin = new Thickness(20, 0, 8, 12), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var empty = new TextBlock { Text = T("篮子还是空的。\n挂到绳子上的图片会自动收进这里。", "The basket is empty.\nPictures you hang on the rope are collected here automatically."),
            TextAlignment = TextAlignment.Center, FontSize = 13, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var left = new Grid { Margin = new Thickness(0) };
        left.Children.Add(scroller); left.Children.Add(empty);
        split.Children.Add(left);

        var previewImage = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(12) };
        var previewMeta = new TextBlock { FontSize = 11, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 14) };
        var previewHint = new TextBlock { Text = T("选择图片查看大图", "Select a picture to preview it"), FontSize = 12, Foreground = Brushes.DimGray,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var previewBody = new Grid { ClipToBounds = true };
        previewBody.RowDefinitions.Add(new RowDefinition());
        previewBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewBody.Children.Add(previewImage); previewBody.Children.Add(previewHint);
        Grid.SetRow(previewMeta, 1); previewBody.Children.Add(previewMeta);
        previewBody.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && selectedPicture is { } picture) OpenPicture(picture); };
        previewBody.ToolTip = T("双击打开原图", "Double-click to open the original");
        var previewPanel = new Border { Background = Theme.Brush(theme.NotePaper), CornerRadius = new CornerRadius(10), Margin = new Thickness(8, 0, 20, 12), Child = previewBody };
        Grid.SetColumn(previewPanel, 1); split.Children.Add(previewPanel);
        Grid.SetRow(split, 2); root.Children.Add(split);

        // ── footer ────────────────────────────────────────────────────────────────────────────────
        var footer = new TextBlock { Margin = new Thickness(20, 6, 20, 14), FontSize = 12, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
            Text = T("单击选中并预览，双击或拖出使用；右键可挂回绳子、钉到桌面或收藏。", "Click to preview, double-click or drag out to use; right-click to hang back on the rope, pin to the desktop or star.") };
        Grid.SetRow(footer, 3); root.Children.Add(footer);

        IEnumerable<BasketPicture> VisiblePictures()
        {
            var query = settings.Pictures.AsEnumerable();
            if (starredOnly) query = query.Where(p => p.Starred);
            var term = search.Text.Trim();
            if (term.Length > 0) query = query.Where(p => Path.GetFileName(p.Path).Contains(term, StringComparison.OrdinalIgnoreCase));
            // The archive keeps its own newest-first order, which is what makes sense to browse.
            return query.Take(500);
        }

        void ShowPreview(BasketPicture? picture)
        {
            selectedPicture = picture;
            previewHint.Visibility = picture is null ? Visibility.Visible : Visibility.Collapsed;
            previewMeta.Text = picture is null ? "" : Path.GetFileName(picture.Path) + "\n" + picture.Added.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            if (picture is null) { previewImage.Source = null; return; }
            try { previewImage.Source = Native.ReadImage(picture.Path); }
            catch (Exception) { previewImage.Source = null; previewMeta.Text = Path.GetFileName(picture.Path) + "\n" + T("原文件已不存在", "The original file is gone"); }
        }

        void RefreshBasket()
        {
            allTab.Tag = starredOnly ? null : "primary";
            starTab.Tag = starredOnly ? "primary" : null;
            thumbnails.Children.Clear();
            var pictures = VisiblePictures().ToList();
            empty.Visibility = pictures.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            scroller.Visibility = pictures.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            summary.Text = string.Format(T("共 {0} 张，已收藏 {1} 张", "{0} pictures, {1} starred"), settings.Pictures.Count, settings.Pictures.Count(p => p.Starred));
            foreach (var picture in pictures) thumbnails.Children.Add(CreateThumb(picture));
            if (selectedPicture is { } keep && pictures.Contains(keep)) ShowPreview(keep);
            else ShowPreview(pictures.FirstOrDefault());
            RefreshPendant();
        }

        UIElement CreateThumb(BasketPicture picture)
        {
            var exists = File.Exists(picture.Path);
            var frame = new Border
            {
                Width = 132, Height = 112, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(10),
                Background = exists ? Theme.Brush(theme.CardSurface) : new SolidColorBrush(Color.FromArgb(90, 160, 160, 160)),
                BorderBrush = Theme.Brush(theme.CardBorder), BorderThickness = new Thickness(1), Padding = new Thickness(4),
                Effect = new DropShadowEffect { Color = Color.FromRgb(38, 57, 76), BlurRadius = 14, ShadowDepth = 3, Opacity = 0.14 }
            };
            if (ReferenceEquals(picture, selectedPicture)) frame.BorderBrush = Theme.Brush(theme.Accent);
            var stack = new StackPanel();
            try
            {
                if (exists) stack.Children.Add(new Image { Source = Native.Thumbnail(picture.Path), Height = 74, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 3) });
            }
            catch (Exception) { }
            if (stack.Children.Count == 0)
                stack.Children.Add(new TextBlock { Text = T("原文件已不存在", "Gone"), Height = 74, FontSize = 11, Foreground = Brushes.DimGray, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { Text = Path.GetFileName(picture.Path), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Theme.Brush(theme.CardText), HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(label);
            var panel = new Grid();
            if (picture.Starred)
                panel.Children.Add(new TextBlock { Text = "★", FontSize = 12, Foreground = Theme.Brush(theme.Accent), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 4, 0) });
            panel.Children.Add(stack);
            frame.Child = panel;

            var button = new Button
            {
                Content = frame, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                Cursor = Cursors.Hand, Focusable = false, ToolTip = picture.Path
            };
            button.Click += (_, _) => Guard(() => { ShowPreview(picture); RefreshSelection(); });
            button.MouseMove += (_, e) =>
            {
                // Dragging a thumbnail out hands the original file straight to whatever accepts it.
                if (e.LeftButton != MouseButtonState.Pressed || !exists) return;
                Guard(() => DragDrop.DoDragDrop(button, FilePayload(picture.Path), DragDropEffects.Copy));
            };
            button.ContextMenu = ThumbMenu(picture, RefreshBasket);
            button.Tag = picture;
            return button;
        }

        void RefreshSelection()
        {
            foreach (var child in thumbnails.Children.OfType<Button>())
                if (child.Tag is BasketPicture picture && child.Content is Border border)
                    border.BorderBrush = ReferenceEquals(picture, selectedPicture) ? Theme.Brush(theme.Accent) : Theme.Brush(theme.CardBorder);
        }

        rebuild = RefreshBasket;
        search.TextChanged += (_, _) => rebuild?.Invoke();
        rebuild();
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };
        basketWindow = window;
        window.Closed += (_, _) => { basketWindow = null; signature = ""; Render(); };
        window.Show();
    }

    /// The per-picture menu. Each entry leaves the source file alone — taking a picture out of the
    /// basket removes the record, never the picture itself.
    private ContextMenu ThumbMenu(BasketPicture picture, Action refresh)
    {
        var menu = new ContextMenu();
        void Add(string title, Action click)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => Guard(click);
            menu.Items.Add(item);
        }
        Add(T("打开", "Open"), () => OpenPicture(picture));
        Add(T("复制图片", "Copy image"), () => { if (FileRecord.Read(picture.Path) is { } file) CopyToClipboard(file); });
        Add(T("挂回绳子", "Hang on the rope"), () => { if (File.Exists(picture.Path)) Attach([picture.Path]); });
        Add(T("钉到桌面", "Pin to desktop"), () => { if (FileRecord.Read(picture.Path) is { } file) DeskPin(file); });
        Add(picture.Starred ? T("取消收藏", "Unstar") : T("收藏", "Star"), () => { picture.Starred = !picture.Starred; Persist(); refresh(); });
        menu.Items.Add(new Separator());
        Add(T("从篮子移出", "Remove from basket"), () => { settings.Pictures.Remove(picture); Persist(); signature = ""; Render(); refresh(); });
        return menu;
    }

    private void OpenPicture(BasketPicture picture)
    {
        if (!File.Exists(picture.Path)) { MessageBox.Show(T("原文件已不存在。", "The original file is gone."), "Clothesline"); return; }
        Native.Open(picture.Path);
    }

    /// The pendant hanging from the right end of the rope. Kept deliberately small so it reads as part
    /// of the rope rather than another window to manage.
    private UIElement CreateBasketPendant(LineTheme theme)
    {
        const double size = 108;
        var hook = BasketSkin.Hook(theme);
        var content = new Grid { Width = size, Height = size * BasketSkin.Aspect(theme) };
        if (BasketSkin.Source(theme) is { } picture)
            content.Children.Add(new Image { Source = picture, Width = size, Stretch = Stretch.Uniform, SnapsToDevicePixels = true, UseLayoutRounding = true });
        else
            content.Children.Add(BasketSkin.Fallback(theme, size));

        var count = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var badge = new Border
        {
            Width = 24, Height = 18, CornerRadius = new CornerRadius(9), Child = count,
            Background = Theme.Brush(theme.Accent), BorderBrush = Theme.Brush(theme.CardSurface), BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 4, 6)
        };
        var total = settings.Pictures.Count;
        count.Text = total > 99 ? "99+" : total.ToString();
        badge.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        content.Children.Add(badge);

        var button = new Button
        {
            Content = content, Cursor = Cursors.Hand, Focusable = false, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0), ToolTip = T("打开图片篮子", "Open the picture basket")
        };
        button.Click += (_, _) => Guard(ShowPictureBasket);
        basketPendant = button;

        // Hang it from the rope itself: solve the sag curve for the x we want and put the handle's hook
        // exactly there, so a wider monitor or a different theme still leaves it attached.
        var anchor = RopeYAt(Width - 96);
        Canvas.SetLeft(button, Width - 96 - size * hook.X);
        Canvas.SetTop(button, anchor - size * BasketSkin.Aspect(theme) * hook.Y);
        return button;
    }

    /// The sagging rope is one quadratic Bezier from (0,10) through (Width/2,32) to (Width,10); this
    /// walks it to find the height at a given x, which is where a pendant has to be hung from.
    private double RopeYAt(double x)
    {
        var best = 10d;
        for (var i = 0; i <= 60; i++)
        {
            var t = i / 60d;
            var px = 2 * (1 - t) * t * (Width / 2) + t * t * Width;
            if (px < x) continue;
            best = (1 - t) * (1 - t) * 10 + 2 * (1 - t) * t * 32 + t * t * 10;
            break;
        }
        return best;
    }

    /// Force the pendant to be rebuilt, which is the only way its badge can pick up a new count.
    private void RefreshPendant() { signature = ""; Render(); }
}
