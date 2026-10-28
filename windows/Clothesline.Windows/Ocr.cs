using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Clothesline.Core;
// Inside the app's own Clothesline.Windows namespace the bare name "Windows" binds to the app
// namespace, not to the WinRT root, so the platform namespaces must be reached via the global root.
using Wrt = global::Windows;

namespace Clothesline.Windows;
internal sealed partial class RopeWindow
{
    internal readonly record struct OcrLanguage(string Tag, string Label);

    /// Windows only ships the recognisers the user has actually installed under
    /// 设置 > 时间和语言 > 语言和区域, so the list must be read at runtime and never assumed.
    /// A machine with no language pack at all is a real and common state.
    internal static IReadOnlyList<OcrLanguage> OcrLanguages()
    {
        var languages = new List<OcrLanguage>();
        try
        {
            foreach (var language in Wrt.Media.Ocr.OcrEngine.AvailableRecognizerLanguages)
                languages.Add(new OcrLanguage(language.LanguageTag, language.DisplayName));
        }
        catch (Exception) { return languages; }
        return languages;
    }

    internal static string? ResolveOcrLanguage(string? preferred)
    {
        var installed = OcrLanguages();
        if (installed.Count == 0) return null;
        if (!string.IsNullOrEmpty(preferred))
        {
            var wanted = installed.FirstOrDefault(x => string.Equals(x.Tag, preferred, StringComparison.OrdinalIgnoreCase));
            if (wanted.Tag is not null) return wanted.Tag;
        }
        // A Chinese engine still reads Latin text, but an English engine cannot read Chinese,
        // so Chinese is the better automatic choice whenever it happens to be installed.
        var chinese = installed.FirstOrDefault(x => x.Tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        return chinese.Tag is not null ? chinese.Tag : installed[0].Tag;
    }

    /// Returns the recognised text, or throws IOException when Windows cannot decode the file as an image.
    internal static async Task<string> OcrRecognize(string path, string? preferred)
    {
        // Windows reports undecodable files as a bare COM failure. Translate it so callers see one
        // predictable exception type instead of having to know about WinRT error codes.
        try { return await OcrRecognizeCore(path, preferred); }
        catch (COMException e) { throw new IOException("OCR could not decode the file.", e); }
    }

    private static async Task<string> OcrRecognizeCore(string path, string? preferred)
    {
        var tag = ResolveOcrLanguage(preferred) ?? throw new InvalidOperationException("No OCR language is installed.");
        var engine = Wrt.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Wrt.Globalization.Language(tag))
                     ?? throw new InvalidOperationException("The OCR engine could not be created for " + tag + ".");
        var file = await Wrt.Storage.StorageFile.GetFileFromPathAsync(path);
        Wrt.Graphics.Imaging.SoftwareBitmap bitmap;
        using (var stream = await file.OpenAsync(Wrt.Storage.FileAccessMode.Read))
        {
            var decoder = await Wrt.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            // The engine rejects anything past MaxImageDimension, and screenshots of tall pages,
            // scans and phone photos do exceed it, so scale down instead of failing.
            var limit = (double)Wrt.Media.Ocr.OcrEngine.MaxImageDimension;
            var scale = Math.Min(1d, Math.Min(limit / decoder.PixelWidth, limit / decoder.PixelHeight));
            var transform = new Wrt.Graphics.Imaging.BitmapTransform();
            if (scale < 1d)
            {
                transform.ScaledWidth = Math.Max(1u, (uint)Math.Floor(decoder.PixelWidth * scale));
                transform.ScaledHeight = Math.Max(1u, (uint)Math.Floor(decoder.PixelHeight * scale));
            }
            bitmap = await decoder.GetSoftwareBitmapAsync(
                Wrt.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Wrt.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                transform,
                Wrt.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Wrt.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        }
        using (bitmap) { return Compose(await engine.RecognizeAsync(bitmap)); }
    }

    /// The Windows engine reports each Han character as its own word, so joining words with a space
    /// turns 晾衣绳 into "晾 衣 绳". Spacing is therefore decided per character boundary rather than
    /// per word, which keeps mixed lines such as "Clothesline 晾衣绳 2026" readable.
    private static string Compose(Wrt.Media.Ocr.OcrResult result)
    {
        var text = new StringBuilder();
        foreach (var line in result.Lines)
        {
            var previous = '\0';
            var wrote = false;
            foreach (var word in line.Words)
            {
                if (word.Text.Length == 0) continue;
                if (wrote && NeedsSpace(previous, word.Text[0])) text.Append(' ');
                text.Append(word.Text);
                previous = word.Text[word.Text.Length - 1];
                wrote = true;
            }
            if (wrote) text.Append('\n');
        }
        return text.ToString().Trim();
    }
    /// Only a CJK-to-CJK boundary is left unspaced. Latin words keep their spaces, and a Han run
    /// next to Latin text or digits gets one, which is the usual Chinese typographic convention:
    /// "晾衣绳 2026" rather than "晾衣绳2026" or "晾 衣 绳".
    private static bool NeedsSpace(char left, char right) => !(IsCjk(left) && IsCjk(right));
    private static bool IsCjk(char value) => value switch
    {
        >= '\u1100' and <= '\u11FF' => true,   // Hangul Jamo
        >= '\u2E80' and <= '\u2EFF' => true,   // CJK radicals
        >= '\u3000' and <= '\u303F' => true,   // CJK punctuation
        >= '\u3040' and <= '\u30FF' => true,   // Kana
        >= '\u3400' and <= '\u4DBF' => true,   // CJK extension A
        >= '\u4E00' and <= '\u9FFF' => true,   // CJK unified ideographs
        >= '\uA000' and <= '\uA4CF' => true,   // Yi
        >= '\uAC00' and <= '\uD7A3' => true,   // Hangul syllables
        >= '\uF900' and <= '\uFAFF' => true,   // CJK compatibility ideographs
        >= '\uFE30' and <= '\uFE4F' => true,   // CJK compatibility forms
        >= '\uFF00' and <= '\uFF60' => true,   // full-width forms
        >= '\uFFE0' and <= '\uFFE6' => true,   // full-width symbols
        _ => false
    };

    private async Task ExtractText(FileRecord file)
    {
        Validate(file);
        if (file.IsDirectory) throw new IOException(T("文件夹没有可提取的文字。", "A folder has no text to extract."));
        if (!IsImage(file.Path)) throw new IOException(T("只能从图片中提取文字。", "Text can only be extracted from images."));
        var usePaddle = settings.OcrEngine == "v6";
        if (!usePaddle && ResolveOcrLanguage(settings.OcrLanguage) is null)
        {
            // Without a language pack every attempt fails identically, so send the user to the one
            // place that actually fixes it rather than letting them retry into the same wall.
            var message = T("系统还没有安装 OCR 语言包，无法提取文字。请在 Windows“设置 → 时间和语言 → 语言和区域”里添加中文（简体），并确保勾选了“光学字符识别”。",
                "No OCR language pack is installed, so text cannot be extracted. In Windows Settings > Time & language > Language & region, add Chinese (Simplified) and make sure Optical character recognition is enabled.");
            if (MessageBox.Show(message + "\n\n" + T("现在打开 Windows 语言设置？", "Open Windows language settings now?"), "Clothesline", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                Native.Open("ms-settings:regionlanguage");
            return;
        }
        // PP-OCRv6 loads a model and runs two networks, which is long enough that staying silent
        // reads as a menu item that did nothing.
        var wait = ShowWait();
        string text;
        try
        {
            text = usePaddle
                ? await PaddleOcr.RecognizeAsync(file.Path, settings.OcrModel)
                : await ExtractWithWindows(file);
        }
        finally { wait.Close(); }
        if (text.Length == 0) throw new IOException(T("这张图片里没有识别到文字。", "No text was recognised in this image."));
        var data = new DataObject();
        data.SetText(text);
        // Stops the clipboard monitor from hanging the freshly extracted text back on the line.
        data.SetData(OwnCopy, new MemoryStream(new byte[] { 1 }));
        Clipboard.SetDataObject(data, true); clipboardSequence = Native.GetClipboardSequenceNumber();
        FlashCard(file.Path, T("已复制文字", "Text copied"));
        ShowOcrText(text);
    }

    private async Task<string> ExtractWithWindows(FileRecord file)
    {
        try { return await OcrRecognize(file.Path, settings.OcrLanguage); }
        catch (IOException) { throw new IOException(T("无法读取这张图片，它可能已损坏或不是有效的图片格式。", "This image could not be read; it may be corrupt or not a valid image format.")); }
    }

    private Window ShowWait()
    {
        var window = new Window
        {
            Title = "Clothesline", Width = 320, Height = 130, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow, ShowInTaskbar = false
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = T("正在识别文字…", "Recognising text…"), FontSize = 13, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6 });
        window.Content = panel;
        window.Show();
        return window;
    }

    /// Recognition is never perfect, so the result is shown as well as copied: the user can check
    /// mistakes and copy by hand instead of discovering them at the paste destination.
    private void ShowOcrText(string text)
    {
        var window = new Window
        {
            Title = T("提取的文字（已复制到剪贴板）", "Extracted text (copied to clipboard)"),
            Width = 560, Height = 420, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var panel = new DockPanel { Margin = new Thickness(14) };
        var close = new Button { Content = T("关闭", "Close"), IsCancel = true, IsDefault = true, Padding = new Thickness(18, 5, 18, 5), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        panel.Children.Add(new TextBox
        {
            Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei")
        });
        window.Content = panel;
        window.Show();
    }
    private MenuItem AsyncItem(string title, Func<Task> action)
    {
        var item = new MenuItem { Header = title };
        // GuardAsync reports every failure, so nothing escapes an async void handler unobserved.
        item.Click += async (_, _) => await GuardAsync(action);
        return item;
    }
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { MessageBox.Show(e.Message, "Clothesline"); }
    }
}
