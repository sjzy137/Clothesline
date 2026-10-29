using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Clothesline.Windows;

/// Lets a hovered picture card show itself at full size without ever taking the rope apart: after a
/// short delay a borderless overlay slides out under the card. Ported from PicRope's HoverPreview
/// (MIT) and reduced to what the clothesline needs — there is no capture pipeline here, so there is
/// nothing to wait for between the card and its file.
///
/// The overlay is click-through by construction. A preview that swallowed the pointer would fight
/// the card underneath it for every click, drag and right-click, which is why the window is both
/// IsHitTestVisible=false and WS_EX_TRANSPARENT rather than merely borderless.
internal sealed class HoverPreview : IDisposable
{
    private readonly RopeWindow rope;
    private readonly DispatcherTimer delay = new();
    private readonly DispatcherTimer leave = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly PreviewSurface surface;
    private FrameworkElement? hoveredCard;
    private string? hoveredPath;
    private int revision;
    private bool enabled = true, disposed;
    private DateTime lastShown = DateTime.MinValue;

    internal HoverPreview(RopeWindow rope)
    {
        this.rope = rope;
        surface = new PreviewSurface(rope);
        delay.Tick += (_, _) => { delay.Stop(); _ = ShowAsync(); };
        leave.Tick += (_, _) => { leave.Stop(); hoveredCard = null; hoveredPath = null; surface.ClosePreview(); };
    }

    /// The setting lives outside; toggling it off must also take any visible preview away at once.
    internal bool Enabled
    {
        set { enabled = value; if (!value) Dismiss(); }
    }

    internal void Enter(FrameworkElement card, string path)
    {
        if (disposed || !enabled || string.IsNullOrEmpty(path)) return;
        // A preview already up, or one that only just went away, reopens quickly; the first one waits
        // long enough that sweeping the pointer across the rope shows nothing at all.
        bool quick = surface.IsPreviewVisible || (DateTime.UtcNow - lastShown).TotalMilliseconds < 1000;
        if (ReferenceEquals(hoveredCard, card) && (surface.IsPreviewVisible || delay.IsEnabled)) { leave.Stop(); return; }
        delay.Stop(); leave.Stop(); revision++; hoveredPath = null; hoveredCard = card; surface.ClosePreview();
        hoveredPath = path;
        delay.Interval = TimeSpan.FromMilliseconds(quick ? 150 : 400);
        delay.Start();
    }

    internal void Leave(FrameworkElement card)
    {
        if (!ReferenceEquals(hoveredCard, card)) return;
        delay.Stop(); revision++; hoveredPath = null;
        leave.Stop(); leave.Start();
    }

    internal void Dismiss()
    {
        delay.Stop(); leave.Stop(); revision++; hoveredPath = null; hoveredCard = null; surface.ClosePreview();
    }

    public void Dispose()
    {
        if (disposed) return;
        Dismiss(); disposed = true;
        surface.Close();
    }

    private async Task ShowAsync()
    {
        var card = hoveredCard;
        var path = hoveredPath;
        if (disposed || !enabled || card is null || path is null) return;
        int mine = revision;
        // Reading and decoding happens off the dispatcher thread: a large picture on the UI thread
        // would stall the rope animation, and mis-sizing the preview would be worse than waiting.
        BitmapSource? picture = null;
        try { picture = await Task.Run(() => File.Exists(path) ? Native.ReadImage(path) : null); }
        catch { /* an unreadable file must not stop the hover; the overlay says so instead */ }
        // Anything that happened while decoding invalidates the request: another card hovered, a card
        // dragged out, or the rope tucking itself away.
        if (disposed || mine != revision || hoveredCard != card) return;
        Rect anchor;
        try
        {
            anchor = new Rect(card.PointToScreen(new Point(0, 0)), card.PointToScreen(new Point(card.RenderSize.Width, card.RenderSize.Height)));
        }
        catch { return; }
        if (anchor.Width <= 0 || anchor.Height <= 0) return;
        // Where the card actually is decides both which screen it belongs to and how much room there
        // is, so the preview never lands off-screen or on the wrong monitor.
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)anchor.Left, (int)anchor.Bottom));
        var scale = Native.MonitorScale(screen);
        if (scale <= 0) scale = 1;
        // Everything below works in device-independent units; the Win32 screen values are in pixels.
        var area = new Rect(screen.WorkingArea.Left / scale, screen.WorkingArea.Top / scale, screen.WorkingArea.Width / scale, screen.WorkingArea.Height / scale);
        anchor = new Rect(anchor.X / scale, anchor.Y / scale, anchor.Width / scale, anchor.Height / scale);
        surface.Open(picture, anchor, area);
        lastShown = DateTime.UtcNow;
    }

    internal static Size PreviewSize(int width, int height, Rect anchor, Rect area)
    {
        double maxWidth = Math.Max(1, Math.Min(640, area.Width - 72));
        double maxHeight = Math.Max(1, Math.Min(480, area.Height - 72));
        double ratio = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));
        // Prefer below the card; only fall back to the height above it when there is genuinely no
        // room there, and never shrink below a size worth showing.
        double below = area.Bottom - anchor.Bottom - 20;
        double above = anchor.Top - area.Top - 20;
        if (height * ratio + 56 > Math.Max(below, above) && below >= above && below >= 256)
            ratio = Math.Min(ratio, (below - 56) / height);
        return new Size(Math.Max(1, width * ratio), Math.Max(1, height * ratio));
    }

    internal static Rect Place(Rect anchor, Rect area, double width, double height)
    {
        const double margin = 8, gap = 12;
        width = Math.Min(width, Math.Max(1, area.Width - 2 * margin));
        height = Math.Min(height, Math.Max(1, area.Height - 2 * margin));
        double x = Math.Max(area.Left + margin, Math.Min(area.Right - margin - width, anchor.Left + (anchor.Width - width) / 2));
        double y = anchor.Bottom + gap;
        if (y + height > area.Bottom - margin) y = anchor.Top - gap - height;
        y = Math.Max(area.Top + margin, Math.Min(area.Bottom - margin - height, y));
        return new Rect(x, y, width, height);
    }

    /// The overlay window itself. One instance is reused: creating a window per hover leaves a
    /// visible gap while WPF builds and positions it, which reads as a flicker.
    private sealed class PreviewSurface : Window
    {
        private readonly Image picture = new() { Stretch = Stretch.Uniform, Margin = new Thickness(24) };
        private readonly TextBlock failure = new()
        {
            Text = "图片暂时无法预览", FontSize = 12, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };

        internal PreviewSurface(RopeWindow owner)
        {
            Owner = owner; Title = "Clothesline · 图片预览";
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; ShowActivated = false; Topmost = true; IsHitTestVisible = false;
            picture.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = .7 };
            var holder = new Grid();
            holder.Children.Add(picture); holder.Children.Add(failure);
            Content = new Border
            {
                Child = holder, CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(236, 28, 34, 42))
            };
        }

        internal bool IsPreviewVisible => IsVisible;

        internal void Open(BitmapSource? image, Rect anchor, Rect area)
        {
            picture.Source = null;
            if (image is null)
            {
                failure.Visibility = Visibility.Visible; picture.Visibility = Visibility.Collapsed;
                Width = 200; Height = 74;
            }
            else
            {
                failure.Visibility = Visibility.Collapsed; picture.Visibility = Visibility.Visible;
                var size = PreviewSize(image.PixelWidth, image.PixelHeight, anchor, area);
                picture.Width = size.Width; picture.Height = size.Height;
                picture.Source = image;
                Width = size.Width + 48; Height = size.Height + 48;
            }
            var bounds = Place(anchor, area, Width, Height);
            Left = bounds.X; Top = bounds.Y;
            if (!IsVisible) { Show(); Native.ClickThrough(this, true); }
            UpdateLayout();
        }

        internal void ClosePreview()
        {
            if (!IsVisible) return;
            Hide(); picture.Source = null;
        }
    }
}
