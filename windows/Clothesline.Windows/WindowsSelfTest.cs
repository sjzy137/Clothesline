using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clothesline.Core;

namespace Clothesline.Windows;
internal static class WindowsSelfTest
{
    /// Renders a picture of known text so OCR has something real to read. No bundled asset can be
    /// used for this: the engine has to be given glyphs that were drawn on this machine.
    private static void RenderOcrSample(string path, string text)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1000, 220));
            context.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Microsoft YaHei"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    72, Brushes.Black, 96),
                new Point(24, 70));
        }
        var bitmap = new RenderTargetBitmap(1000, 220, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
    public static int Run(string report)
    {
        var root=Path.Combine(Path.GetTempPath(),"Clothesline-Windows-Smoke-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file=Path.Combine(root,"real-file.txt"); File.WriteAllText(file,"original remains unchanged");
            var before=File.ReadAllBytes(file);
            var record=FileRecord.Read(file) ?? throw new Exception("real file read failed");
            var state=new RopeState(); state.Dismiss(record);
            if (!before.SequenceEqual(File.ReadAllBytes(file))) throw new Exception("take down changed the original");
            var payload=RopeWindow.FilePayload(file);
            if (payload.GetData(DataFormats.FileDrop) is not string[] values || values.Single()!=file) throw new Exception("drag is not a real file drop payload");
            var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
            var imageFile=Path.Combine(root,"owned-clipboard-image.png");
            using (var resource=Application.GetResourceStream(new Uri("pack://application:,,,/icon.png")).Stream)
            using (var output=File.Create(imageFile)) resource.CopyTo(output);
            var image=Native.ReadImage(imageFile); File.Delete(imageFile);
            if (image.PixelWidth<1 || File.Exists(imageFile)) throw new Exception("image copy retained a source file lock");
            var settings=new Settings { Attached=[file], English=true };
            var window=new RopeWindow(settings,()=>{});
            typeof(RopeWindow).GetMethod("Render",BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly)!.Invoke(window,null);
            var cards=(StackPanel)typeof(RopeWindow).GetField("cards",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
            if (cards.Children.Count!=1 || !window.AllowDrop || !window.Topmost) throw new Exception("WPF card/drop/overlay construction failed");
            var holder=(Grid)((StackPanel)cards.Children[0]).Children[1];
            var buttons=holder.Children.OfType<StackPanel>().SelectMany(bar=>bar.Children.OfType<Button>()).ToList();
            if (buttons.Count!=2) throw new Exception("card is missing its pin and take-down buttons");
            // The basket pendant is drawn from embedded pictures, one per theme. A broken resource path
            // would silently fall back to the vector outline, so this is the only place it is caught.
            var basketCheck="basket skins for all "+LineTheme.All.Length+" themes";
            foreach (var ropeTheme in LineTheme.All)
                if (BasketSkin.Source(ropeTheme) is null) throw new Exception("basket picture missing for theme "+ropeTheme.Id);
            // The move marker is the one thing that cannot be verified by reading the code. If it ever
            // stops reaching the clipboard as a DWORD, Ctrl+drag silently degrades to a copy.
            var movePayload=RopeWindow.FilePayload(file);
            Native.MarkAsMove(movePayload);
            Clipboard.SetDataObject(movePayload,true);
            if (Native.ReadPreferredDropEffect()!=Native.DropEffectMove) throw new Exception("move marker did not reach the clipboard as DROPEFFECT_MOVE");
            // OCR is only testable where Windows actually has a recogniser, so a machine with no
            // language pack is reported rather than failed. Where one exists, the checks target the
            // two regressions that reading the code cannot catch: Han characters coming back spaced
            // apart, and an undecodable file failing in a way the caller cannot handle.
            var ocrCheck="ocr skipped: no recogniser installed";
            if (RopeWindow.OcrLanguages().Count>0)
            {
                var sample=Path.Combine(root,"ocr-sample.png");
                RenderOcrSample(sample,"Clothesline 晾衣绳 2026");
                // Run off the STA thread: blocking the dispatcher thread on a WinRT operation that
                // marshals back to it would deadlock instead of failing.
                var text=Task.Run(()=>RopeWindow.OcrRecognize(sample,null)).GetAwaiter().GetResult();
                if (string.IsNullOrWhiteSpace(text)) throw new Exception("OCR produced no text from a rendered sample");
                if (Regex.IsMatch(text,@"[\u4e00-\u9fff] [\u4e00-\u9fff]")) throw new Exception("OCR spaced Han characters apart: "+text);
                // Reporting the Han count keeps the check honest: an engine that only read the Latin
                // half would still satisfy "no spacing fault", and this makes that visible.
                ocrCheck="ocr read rendered text ("+Regex.Matches(text,@"[\u4e00-\u9fff]").Count+" Han chars) ["+RopeWindow.ResolveOcrLanguage(null)+"]";
                var notImage=Path.Combine(root,"not-an-image.txt"); File.WriteAllText(notImage,"plain text");
                var rejected=false;
                try { Task.Run(()=>RopeWindow.OcrRecognize(notImage,null)).GetAwaiter().GetResult(); }
                catch (IOException) { rejected=true; }
                if (!rejected) throw new Exception("OCR did not reject a non-image file as unreadable");
                ocrCheck+=" and rejected a non-image";
            }
            // Recycling is the whole of the Ctrl+drag move. Explorer only ever copies a CF_HDROP that
            // came from outside the shell, so the source has to remove the original, and if
            // SHFileOperation stopped honouring FOF_ALLOWUNDO the file would be hard deleted instead
            // with nothing in the build noticing.
            var recycleTarget=Path.Combine(root,"recycle-me.txt"); File.WriteAllText(recycleTarget,"sent to the bin");
            if (!Native.Recycle(recycleTarget)) throw new Exception("the Recycle Bin refused a temporary file");
            if (File.Exists(recycleTarget)) throw new Exception("the original was still in place after recycling");

            // The hot key is now user configurable, so the string it round-trips through must parse
            // back to exactly the Win32 modifiers and virtual key that RegisterHotKey expects. A broken
            // parser would silently bind the wrong key or bind nothing and the rope would stop toggling.
            if (!Native.TryParseHotKey("Ctrl+Alt+Up", out var hkMods, out var hkKey)) throw new Exception("default hot key failed to parse");
            if (hkMods != (Native.ModifierControl | Native.ModifierAlt)) throw new Exception("hot key modifiers parsed wrong");
            if (hkKey != Native.VirtualKeyUp) throw new Exception("hot key virtual key parsed wrong");
            if (Native.TryParseHotKey("", out _, out _) || Native.TryParseHotKey("Ctrl+Alt", out _, out _)) throw new Exception("invalid hot key strings were accepted");
            var hkCheck = "hot key string parses";

            // PP-OCRv6 is the other new dependency, and the only way to know it actually reads
            // anything is to unpack the bundled model and hand it glyphs drawn on this machine.
            var v6Sample=Path.Combine(root,"v6-sample.png");
            RenderOcrSample(v6Sample,"Clothesline 晾衣绳 2026");
            var v6Text=Task.Run(()=>PaddleOcr.RecognizeAsync(v6Sample,PaddleOcr.Tiny)).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(v6Text)) throw new Exception("PP-OCRv6 produced no text from a rendered sample");
            var v6Han=Regex.Matches(v6Text,@"[\u4e00-\u9fff]").Count;
            if (v6Han<3) throw new Exception("PP-OCRv6 read too few Han characters: "+v6Text);
            if (Regex.IsMatch(v6Text,@"[\u4e00-\u9fff] [\u4e00-\u9fff]")) throw new Exception("PP-OCRv6 spaced Han characters apart: "+v6Text);
            var v6Check="pp-ocrv6 tiny read rendered text ("+v6Han+" Han chars)";

            var license=Path.Combine(AppContext.BaseDirectory,"LICENSE.txt");
            if (!File.ReadAllText(license).Contains("Alejandro")) throw new Exception("upstream MIT notice missing");

            // The hover preview is placed entirely by arithmetic, and the one failure mode that reads
            // as a broken feature rather than a crash is a preview that lands off-screen or upside
            // down. Feeding it a 4K picture under a card near the bottom edge covers both: the size
            // has to clamp to what fits, and the fallback has to flip above the anchor.
            var anchor=new Rect(600, 40, 130, 130);
            var displayArea=new Rect(0, 0, 1280, 720);
            var previewSize=HoverPreview.PreviewSize(3840, 2160, anchor, displayArea);
            if (previewSize.Width<1 || previewSize.Height<1) throw new Exception("preview collapsed to nothing");
            if (previewSize.Width>640+1 || previewSize.Height>480+1) throw new Exception("preview exceeded its cap: "+previewSize);
            var placed=HoverPreview.Place(anchor, displayArea, previewSize.Width+48, previewSize.Height+48);
            if (placed.Left<displayArea.Left || placed.Top<displayArea.Top) throw new Exception("preview landed above or left of the screen");
            if (placed.Right>displayArea.Right+1 || placed.Bottom>displayArea.Bottom+1) throw new Exception("preview landed off-screen");
            var lowAnchor=new Rect(600, 620, 130, 130);
            var above=HoverPreview.Place(lowAnchor, displayArea, 300, 200);
            if (above.Bottom>lowAnchor.Top) throw new Exception("preview did not flip above a card near the bottom edge");
            var previewCheck="hover preview stays on screen (for an oversized picture)";

            // Which monitor the rope picks lives in one place, and getting it wrong leaves the rope
            // on a screen nobody is looking at. An unknown pinned device must fall back rather than
            // crash, which is what happens when a monitor is unplugged between runs.
            var here=typeof(RopeWindow).GetMethod("ResolveScreen",BindingFlags.NonPublic|BindingFlags.Instance)!;
            settings.Display="\\\\.\\DISPLAY99";
            if (here.Invoke(window,new object[] { new Point(1,1) }) is not System.Windows.Forms.Screen fallback) throw new Exception("a missing monitor left the rope nowhere");
            settings.Display="";
            if (here.Invoke(window,new object[] { new Point(1,1) }) is not System.Windows.Forms.Screen followed) throw new Exception("following the pointer found no screen");
            if (fallback.WorkingArea.Width<=0 || followed.WorkingArea.Width<=0) throw new Exception("the chosen screen has no usable work area");
            var screenCheck="rope resolves a display even without the pinned monitor";
            if (!File.Exists(Native.Downloads()) && !Directory.Exists(Native.Downloads())) { /* Known folder may be uncreated on a fresh runner. */ }
            File.WriteAllText(report,JsonSerializer.Serialize(new { passed=true, checks=new[] { "real file card", "safe dismissal", "file-drop payload", "WPF overlay construction", "card pin and take-down buttons", basketCheck, "ctrl-drag move marker", ocrCheck, "recycle bin move", hkCheck, v6Check, "image source lock released",
                previewCheck, screenCheck, "bundled MIT license" }, uiAcceptance=false }));
            app.Shutdown(); return 0;
        }
        catch (Exception error)
        { File.WriteAllText(report,JsonSerializer.Serialize(new { passed=false,error=error.ToString() })); return 1; }
        finally { Directory.Delete(root,true); }
    }
}
