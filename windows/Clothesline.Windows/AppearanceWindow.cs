using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clothesline.Windows;

/// <summary>
/// 外观切换窗口：五套 PicRope 主题（绳子 + 夹子）的预览与选择。
/// 移植自 Renly1994/PicRope（MIT），其本身派生自 Tendedero（MIT）。
/// </summary>
internal sealed class AppearanceWindow : Window
{
    readonly Action<LineTheme> apply;
    readonly System.Collections.Generic.List<TextBlock> selectionMarks = new();
    readonly System.Collections.Generic.List<Button> options = new();

    public AppearanceWindow(LineTheme selected, Action<LineTheme> apply)
    {
        this.apply = apply;
        Title = "外观";
        Width = 654; Height = 462;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var body = new StackPanel();
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        heading.Children.Add(new TextBlock { Text = "外观", FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("#283343") });
        var close = Theme.Button("", Close);
        close.Content = "×"; close.Width = close.Height = 28; close.MinWidth = 0;
        close.Padding = new Thickness(0); close.Tag = "quiet"; close.HorizontalAlignment = HorizontalAlignment.Right;
        System.Windows.Automation.AutomationProperties.SetName(close, "关闭外观");
        heading.Children.Add(close);
        heading.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args) { if (args.OriginalSource is TextBlock) DragMove(); };
        body.Children.Add(heading);
        body.Children.Add(new TextBlock { Text = "绳子与夹子整套切换", FontSize = 12, Foreground = Theme.Brush("#65717F"), Margin = new Thickness(0, 0, 0, 18) });

        var choices = new WrapPanel();
        foreach (var theme in LineTheme.All)
        {
            var content = new StackPanel();
            content.Children.Add(Preview(theme));
            var label = new Grid { Margin = new Thickness(11, 0, 11, 5) };
            label.Children.Add(new TextBlock { Text = theme.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("#283343") });
            var mark = new TextBlock { Text = "已选", FontSize = 10, Foreground = Theme.Brush("#2463AE"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            selectionMarks.Add(mark); label.Children.Add(mark); content.Children.Add(label);
            content.Children.Add(new TextBlock { Text = theme.Description, FontSize = 10, Foreground = Theme.Brush("#65717F"), Margin = new Thickness(11, 0, 0, 10) });
            var button = new Button { Content = content, Style = Theme.ButtonStyle, Width = 180, Height = 139, Padding = new Thickness(0), Margin = new Thickness(0, 0, 10, 12), Tag = theme };
            System.Windows.Automation.AutomationProperties.SetName(button, theme.Name);
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "theme-" + theme.Id);
            button.Click += delegate { Select(theme); };
            options.Add(button); choices.Children.Add(button);
        }
        body.Children.Add(choices);
        var card = Theme.Card(body); card.Padding = new Thickness(24); Content = card;
        Selected(selected);
        PreviewKeyDown += delegate(object sender, KeyEventArgs args) { if (args.Key == Key.Escape) Close(); };
    }

    void Select(LineTheme theme) { apply(theme); Selected(theme); }

    void Selected(LineTheme theme)
    {
        for (int i = 0; i < options.Count; i++)
        {
            var button = options[i];
            bool current = button.Tag == theme;
            selectionMarks[i].Visibility = current ? Visibility.Visible : Visibility.Collapsed;
            button.BorderBrush = Theme.Brush(current ? "#6E97CB" : "#DCE2EA");
            button.BorderThickness = new Thickness(current ? 2 : 1);
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, current ? "已选择" : "");
        }
    }

    static Canvas Preview(LineTheme theme)
    {
        var canvas = new Canvas { Width = 174, Height = 81, IsHitTestVisible = false };
        var rope = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,20 Q87,30 174,20") };
        RopeLine.Apply(rope, theme); canvas.Children.Add(rope);
        for (int i = 0; i < 3; i++)
        {
            var frame = new Border { Width = 44, Height = 32, CornerRadius = new CornerRadius(4),
                Background = Brushes.White, BorderBrush = Theme.Brush("#DCE2EA"), BorderThickness = new Thickness(0.7), Padding = new Thickness(2) };
            frame.Child = new Border { Background = Theme.Brush("#DCE5F1"), CornerRadius = new CornerRadius(2) };
            double x = 6 + i * 59, y = 27 + (i == 1 ? 2 : 0);
            canvas.Children.Add(frame); Canvas.SetLeft(frame, x); Canvas.SetTop(frame, y);
            var clip = new ClothesClip(theme, i) { RenderTransform = new ScaleTransform(0.42, 0.42) };
            canvas.Children.Add(clip); Canvas.SetLeft(clip, x + 22 - 8 * 0.42); Canvas.SetTop(clip, y - 8.4);
        }
        return canvas;
    }
}
