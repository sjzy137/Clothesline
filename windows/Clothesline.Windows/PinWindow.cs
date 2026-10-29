using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using Clothesline.Core;

namespace Clothesline.Windows;

/// <summary>
/// A topmost, borderless floating window that shows one image enlarged on the desktop — the
/// "小夹子" (clip) feature borrowed from PicRope. The card stays on the rope, so the rope's
/// right-click menu keeps working; this window is an extra, draggable, zoomable view. Clicking the
/// "收回" pin at the top returns the image to just-the-card state on the rope.
/// </summary>
internal sealed partial class PinWindow : Window
{
    private readonly Image image = new() { Stretch = Stretch.Uniform, Cursor = Cursors.SizeAll };
    private readonly ScaleTransform zoom = new(1, 1);
    private double baseWidth;
    private double baseHeight;

    internal PinWindow(FileRecord file, LineTheme theme, Action<string> onDismiss)
    {
        var work = SystemParameters.WorkArea;
        // ~1/8 of the desktop by area → side ≈ 0.35 of the smaller screen dimension.
        baseWidth = Math.Min(work.Width, work.Height) * 0.34;
        ImageSource? source = Native.Thumbnail(file.Path);
        try { source = Native.ReadImage(file.Path) ?? source; } catch { /* fall back to the shell thumbnail */ }
        if (source is BitmapSource bitmap)
        {
            var ratio = bitmap.PixelHeight / (double)Math.Max(1, bitmap.PixelWidth);
            baseHeight = baseWidth * ratio;
            image.Source = bitmap;
        }
        else
        {
            baseHeight = baseWidth * 0.75;
            image.Source = null;
        }
        image.Width = baseWidth;
        image.Height = baseHeight;
        image.LayoutTransform = zoom;

        var border = new Border
        {
            Child = image,
            Background = Theme.Brush(theme.CardSurface),
            BorderBrush = Theme.Brush(theme.CardBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = .28 }
        };

        var pin = new Button
        {
            Content = "收回",
            FontSize = 11,
            Foreground = Brushes.White,
            Background = Theme.Brush(theme.Accent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2, 8, 2),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -14, 0, 0),
            ToolTip = "收回晾衣绳（取消桌面钉图）"
        };
        pin.Click += (_, _) => { onDismiss(file.Path); Close(); };

        var grid = new Grid();
        grid.Children.Add(border);
        grid.Children.Add(pin);
        Content = grid;

        // Drag anywhere except the pin button.
        grid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == pin) return;
            DragMove();
        };
        // Scroll wheel zooms; the window auto-sizes to the scaled content.
        PreviewMouseWheel += (_, e) =>
        {
            var next = Math.Clamp(zoom.ScaleX * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.3, 3.0);
            zoom.ScaleX = zoom.ScaleY = next;
            e.Handled = true;
        };

        Title = file.Name;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = false;
        Left = work.Left + (work.Width - baseWidth) / 2;
        Top = work.Top + (work.Height - baseHeight) / 2;
    }
}
