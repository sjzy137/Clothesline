using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

#nullable disable
namespace Clothesline.Windows;

/// <summary>
/// PicRope 衍生的视觉主题系统：主题化绳子、夹子、图钉，以及统一的白卡 / 圆角按钮样式。
/// 移植自 Renly1994/PicRope（MIT），其本身派生自 Tendedero（MIT）。仅做渲染，不改交互与命中。
/// </summary>
internal static class Theme
{
    static readonly ResourceDictionary styles = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='Action' TargetType='Button'>
    <Setter Property='FontFamily' Value='Microsoft YaHei UI'/><Setter Property='FontSize' Value='13'/>
    <Setter Property='Foreground' Value='#343D49'/><Setter Property='Background' Value='White'/>
    <Setter Property='BorderBrush' Value='#DCE2EA'/><Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='14,0'/><Setter Property='Height' Value='34'/><Setter Property='MinWidth' Value='80'/>
    <Setter Property='Cursor' Value='Hand'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
      <Border x:Name='Surface' CornerRadius='7' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
        <ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='True'/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='#F0F2F5'/></Trigger>
        <Trigger Property='IsPressed' Value='True'><Setter TargetName='Surface' Property='Background' Value='#E8EDF3'/></Trigger>
        <Trigger Property='Tag' Value='primary'><Setter Property='Background' Value='#2463AE'/><Setter Property='BorderBrush' Value='#2463AE'/><Setter Property='Foreground' Value='White'/><Setter Property='FontWeight' Value='SemiBold'/></Trigger>
        <MultiTrigger><MultiTrigger.Conditions><Condition Property='Tag' Value='primary'/><Condition Property='IsMouseOver' Value='True'/></MultiTrigger.Conditions><Setter TargetName='Surface' Property='Background' Value='#1B5498'/></MultiTrigger>
        <Trigger Property='Tag' Value='quiet'><Setter Property='BorderBrush' Value='Transparent'/><Setter Property='Background' Value='Transparent'/></Trigger>
        <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='#6E97CB'/><Setter TargetName='Surface' Property='BorderThickness' Value='2'/></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Surface' Property='Opacity' Value='0.4'/><Setter Property='Cursor' Value='Arrow'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='Hint' TargetType='ToolTip'>
    <Setter Property='FontFamily' Value='Microsoft YaHei UI'/><Setter Property='FontSize' Value='12'/><Setter Property='Foreground' Value='#536172'/>
    <Setter Property='MaxWidth' Value='320'/><Setter Property='HasDropShadow' Value='False'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ToolTip'>
      <Border Background='White' BorderBrush='#E0E4EA' BorderThickness='1' CornerRadius='7' Padding='10,7' Margin='8'>
        <Border.Effect><DropShadowEffect BlurRadius='12' ShadowDepth='2' Opacity='0.12'/></Border.Effect>
        <TextBlock Text='{TemplateBinding Content}' TextWrapping='Wrap'/>
      </Border>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
</ResourceDictionary>");

    public static Style ButtonStyle { get { return (Style)styles["Action"]; } }
    public static Style HintStyle { get { return (Style)styles["Hint"]; } }
    public static SolidColorBrush Brush(string value) { return (SolidColorBrush)new BrushConverter().ConvertFromString(value); }
    public static void Install()
    {
        // 透明分层窗口（AllowsTransparency）在软件渲染下曲线/渐变/柔影的 alpha 合成更稳定干净。
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        if (Application.Current is { } app) app.Resources[typeof(ToolTip)] = styles["Hint"];
    }

    public static Button Button(string title, Action action, string tag = null)
    {
        var button = new Button { Content = title, Style = ButtonStyle, Tag = tag };
        button.Click += delegate { action(); };
        return button;
    }

    public static Border Card(UIElement content)
    {
        return new Border {
            Child = content, Margin = new Thickness(16), Padding = new Thickness(24), CornerRadius = new CornerRadius(14),
            Background = Brushes.White, BorderBrush = Brush("#E0E4EA"), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { Color = Color.FromRgb(38, 57, 76), BlurRadius = 22, ShadowDepth = 6, Opacity = 0.16 }
        };
    }
}

/// <summary>一套外观：绳子颜色/高光/粗细 + 夹子配色。对应 PicRope 的五套主题。</summary>
internal sealed class LineTheme
{
    public readonly string Id, Name, Description;
    public readonly string RopeColor, RopeHighlight;
    public readonly double RopeWidth;
    public readonly string[] ClipColors;

    LineTheme(string id, string name, string description, string rope, string highlight, double width, params string[] colors)
    {
        Id = id; Name = name; Description = description;
        RopeColor = rope; RopeHighlight = highlight; RopeWidth = width; ClipColors = colors;
    }

    public static readonly LineTheme[] All = {
        new LineTheme("classic", "经典银灰", "细灰钢索 · 银灰窄夹", "#8A96A7", "#C8CFD8", 1.6, "#DCE3EC"),
        new LineTheme("linen", "棉麻木夹", "天然棉绳 · 浅榉木夹", "#BAA58A", "#EBDFC9", 2.3, "#DFC5A3"),
        new LineTheme("porcelain", "奶白轻盈", "白色编织线 · 奶白夹", "#AEB1B0", "#F3F0E8", 1.9, "#F3F0E8"),
        new LineTheme("color", "柔彩日常", "浅灰棉线 · 柔彩夹", "#A8ACAA", "#DADCD8", 1.9, "#A6BBA1", "#DCAAAA", "#F1D490"),
        new LineTheme("graphite", "石墨金属", "炭灰钢索 · 石墨窄夹", "#565D64", "#949BA3", 1.65, "#575E66")
    };

    public static LineTheme FromId(string id) => Array.Find(All, theme => theme.Id == id) ?? All[3];
}

/// <summary>把主题应用到绳子 Path：经典主题为纯色，其余用编织纤维笔刷模拟拧股绳。</summary>
internal static class RopeLine
{
    public static void Apply(Path rope, LineTheme theme)
    {
        rope.IsHitTestVisible = false;
        rope.StrokeThickness = theme.RopeWidth;
        rope.StrokeStartLineCap = rope.StrokeEndLineCap = PenLineCap.Round;
        if (theme.Id == "classic") { rope.Stroke = Theme.Brush(theme.RopeColor); return; }
        double width = theme.Id == "porcelain" ? 4 : 5;
        var fiber = new DrawingGroup();
        fiber.Children.Add(new GeometryDrawing(Theme.Brush(theme.RopeColor), null,
            new RectangleGeometry(new Rect(0, 0, width, theme.RopeWidth))));
        var thread = new Pen(Theme.Brush(theme.RopeHighlight), theme.Id == "graphite" ? 0.45 : 0.65);
        for (int i = -1; i < 3; i++)
            fiber.Children.Add(new GeometryDrawing(null, thread,
                new LineGeometry(new Point(i * width / 2, 0), new Point((i + 1) * width / 2, theme.RopeWidth))));
        var brush = new DrawingBrush(fiber) {
            TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, width, theme.RopeWidth), Viewbox = new Rect(0, 0, width, theme.RopeWidth)
        };
        brush.Freeze();
        rope.Stroke = brush;
    }
}

/// <summary>拟物衣夹（小夹子）：前后两层渐变 + 铰链 + 可选开合动画。</summary>
internal sealed class ClothesClip : Canvas
{
    readonly RotateTransform front = new RotateTransform(0, 10, 14), back = new RotateTransform(0, 10, 14);
    bool opened;
    public LineTheme Appearance { get; private set; }

    public ClothesClip() : this(LineTheme.All[0], 0) { }

    public ClothesClip(LineTheme theme, int colorIndex)
    {
        Width = 16; Height = 30; IsHitTestVisible = false;
        Apply(theme, colorIndex);
    }

    public void Apply(LineTheme theme, int colorIndex)
    {
        Appearance = theme;
        Children.Clear();
        string color = theme.ClipColors[colorIndex % theme.ClipColors.Length];
        bool wood = theme.Id == "linen", metal = theme.Id == "classic" || theme.Id == "graphite";
        double x = wood ? 1.5 : (theme.Id == "color" ? 1.5 : 3), width = wood ? 10 : (theme.Id == "color" ? 11.5 : 9.5);
        double height = theme.Id == "classic" || theme.Id == "graphite" ? 27 : 30;
        string edge = wood ? "#AD8E69" : (theme.Id == "graphite" ? "#424950" : (theme.Id == "classic" ? "#8A98AA" : (theme.Id == "porcelain" ? "#C2BFB5" : "#B4B3A9")));
        var rear = new Canvas { Width = 16, Height = 30, RenderTransform = back };
        rear.Children.Add(new Rectangle {
            Width = width - 1, Height = height - 0.5, RadiusX = wood ? 1.8 : 3, RadiusY = wood ? 1.8 : 3,
            Fill = Shade(color, -0.13), Stroke = Theme.Brush(edge), StrokeThickness = 0.5
        });
        SetLeft(rear.Children[0], x + 2.7); SetTop(rear.Children[0], 0.5);
        Children.Add(rear);
        var face = new Canvas { Width = 16, Height = 30, RenderTransform = front };
        var finish = new LinearGradientBrush();
        finish.StartPoint = new Point(0, 0); finish.EndPoint = new Point(1, 0.3);
        finish.GradientStops.Add(new GradientStop(((SolidColorBrush)Shade(color, metal ? 0.36 : 0.18)).Color, 0));
        finish.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(color), 0.55));
        finish.GradientStops.Add(new GradientStop(((SolidColorBrush)Shade(color, theme.Id == "porcelain" ? -0.04 : -0.08)).Color, 1));
        finish.Freeze();
        face.Children.Add(new Rectangle {
            Width = width, Height = height, RadiusX = wood ? 1.8 : 3, RadiusY = wood ? 1.8 : 3,
            Fill = finish, Stroke = Theme.Brush(edge), StrokeThickness = wood ? 0.6 : 0.55
        });
        SetLeft(face.Children[0], x);
        if (wood)
        {
            for (int i = 0; i < 3; i++)
                face.Children.Add(new Path {
                    Data = Geometry.Parse("M" + (x + 2 + i * 2).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",3 q-0.6,4 0.1,8 m0,6 q0.6,4 -0.2,10"),
                    Stroke = Theme.Brush(i == 1 ? "#C5A87F" : "#F3E2C8"), StrokeThickness = 0.35, Opacity = 0.65
                });
            face.Children.Add(new Line { X1 = x + 0.8, X2 = x + width - 0.8, Y1 = 16.2, Y2 = 16.2,
                Stroke = Theme.Brush("#AB8E6D"), StrokeThickness = 0.65 });
        }
        else
            face.Children.Add(new Line { X1 = x + 1.2, X2 = x + 1.2, Y1 = 3.5, Y2 = height - 3.5,
                Stroke = Shade(color, 0.48), StrokeThickness = 0.55, Opacity = metal ? 0.6 : 0.3 });
        if (theme.Id == "color")
            face.Children.Add(new Line { X1 = x + width * 0.55, X2 = x + width * 0.55, Y1 = 2.5, Y2 = height - 3,
                Stroke = Shade(color, -0.25), StrokeThickness = 0.55, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        Children.Add(face);
        var hinge = new Ellipse { Width = wood ? 3.2 : 2.2, Height = wood ? 3.8 : 2.6,
            Fill = Theme.Brush(metal ? "#ADB5BF" : "#E0DFD7"), Stroke = Theme.Brush(wood ? "#7D807B" : edge), StrokeThickness = 0.55 };
        Children.Add(hinge); SetLeft(hinge, x + width - 0.4); SetTop(hinge, 12.5);
        if (wood)
        {
            Children.Add(new Path { Data = Geometry.Parse("M12.3,14.5 Q15.3,12.8 14.2,16 L12,19 H3"),
                Stroke = Theme.Brush("#8A8C85"), StrokeThickness = 0.8, StrokeLineJoin = PenLineJoin.Round });
        }
        front.BeginAnimation(RotateTransform.AngleProperty, null); back.BeginAnimation(RotateTransform.AngleProperty, null);
        front.Angle = opened ? 7 : 0; back.Angle = opened ? -5 : 0;
    }

    static Brush Shade(string value, double amount)
    {
        Color color = (Color)ColorConverter.ConvertFromString(value);
        Func<byte, byte> shift = channel => (byte)Math.Round(amount >= 0 ? channel + (255 - channel) * amount : channel * (1 + amount));
        var brush = new SolidColorBrush(Color.FromRgb(shift(color.R), shift(color.G), shift(color.B)));
        brush.Freeze(); return brush;
    }

    public void Open()
    {
        opened = true;
        front.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(7, TimeSpan.FromSeconds(0.10)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        back.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-5, TimeSpan.FromSeconds(0.10)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    public void Reset()
    {
        opened = false;
        front.BeginAnimation(RotateTransform.AngleProperty, null); back.BeginAnimation(RotateTransform.AngleProperty, null);
        front.Angle = back.Angle = 0;
    }
}

/// <summary>拟物图钉（桌面贴图用），P4 接入 PinWindow 时启用。</summary>
internal sealed class Thumbtack : Canvas
{
    public LineTheme Appearance { get; private set; }

    public Thumbtack()
    {
        Width = Height = 32;
        IsHitTestVisible = false;
    }

    public void Apply(LineTheme theme, int colorIndex)
    {
        Appearance = theme;
        Children.Clear();
        Color color = (Color)ColorConverter.ConvertFromString(theme.ClipColors[colorIndex % theme.ClipColors.Length]);
        Color light = Color.FromRgb((byte)(color.R + (255 - color.R) * 0.65), (byte)(color.G + (255 - color.G) * 0.65), (byte)(color.B + (255 - color.B) * 0.65));
        Color dark = Color.FromRgb((byte)(color.R * 0.67), (byte)(color.G * 0.67), (byte)(color.B * 0.67));
        var finish = new LinearGradientBrush(light, dark, new Point(0, 0), new Point(1, 0.8));
        finish.GradientStops.Insert(1, new GradientStop(color, 0.55));
        finish.Freeze();
        var edge = new SolidColorBrush(dark); edge.Freeze();
        var body = new Canvas { Width = 32, Height = 32, RenderTransform = new RotateTransform(-12, 16, 27) };
        var shadow = new Ellipse { Width = 20, Height = 7, Fill = new SolidColorBrush(Color.FromArgb(35, 34, 44, 54)) };
        body.Children.Add(shadow); SetLeft(shadow, 7.5); SetTop(shadow, 19);
        body.Children.Add(new Path { Data = Geometry.Parse("M15,21 L15.4,28.5 Q16,30 16.6,28.5 L17,21 Z"),
            Fill = new LinearGradientBrush(Color.FromRgb(235, 240, 245), Color.FromRgb(114, 128, 141), 0) });
        body.Children.Add(new Path { Data = Geometry.Parse("M11,8 C12.5,11 13,15 10,18 L22,18 C19,15 19.5,11 21,8 Z"),
            Fill = finish, Stroke = edge, StrokeThickness = 0.6 });
        var foot = new Ellipse { Width = 19, Height = 7, Fill = finish, Stroke = edge, StrokeThickness = 0.6 };
        body.Children.Add(foot); SetLeft(foot, 6.5); SetTop(foot, 16.5);
        var head = new Ellipse { Width = 17, Height = 8, Fill = finish, Stroke = edge, StrokeThickness = 0.6 };
        body.Children.Add(head); SetLeft(head, 7.5); SetTop(head, 3.5);
        body.Children.Add(new Path { Data = Geometry.Parse("M10,6 Q13,4.5 17,5 M9,19 Q13,17.5 17,18"),
            Stroke = Brushes.White, Opacity = 0.65, StrokeThickness = 1, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        Children.Add(body);
    }
}
