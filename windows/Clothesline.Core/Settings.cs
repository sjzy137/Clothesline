using System.Text.Json;
namespace Clothesline.Core;

/// One entry in the Picture Basket. Only the path is stored: copying every image into a private
/// folder would double the disk usage of screenshots the user already owns, and the basket is an
/// archive of what has been hung, not a second copy of it. A missing file therefore degrades to a
/// greyed placeholder instead of an error.
public sealed class BasketPicture
{
    public string Path { get; set; } = "";
    public DateTime Added { get; set; }
    /// Starred pictures are kept when the basket is trimmed and are exempt from auto-expiry.
    public bool Starred { get; set; }
}

public sealed class Settings
{
    public List<string> Folders { get; set; } = [];
    public List<string> Attached { get; set; } = [];
    public List<string> Recent { get; set; } = [];
    public List<string> Pins { get; set; } = [];
    public List<FileRecord> PinSnapshots { get; set; } = [];
    /// Paths currently pinned to the desktop as enlarged floating thumbnails (the "小夹子" feature).
    /// Persisted so the floating pins reopen after a restart.
    public List<string> DeskPins { get; set; } = [];
    /// File names (inside the Notes folder) the user has favourited in the Text Envelope. Favourites
    /// survive restarts and are exempt from the one-week auto-deletion of un-favourited notes.
    public List<string> TextFavorites { get; set; } = [];
    /// The Picture Basket archive. The rope itself only shows a handful of items at a time and taking
    /// one down hides it, so every image that is ever hung is recorded here and stays browsable —
    /// the picture equivalent of the Text Envelope.
    public List<BasketPicture> Pictures { get; set; } = [];
    /// UI appearance theme id (PicRope-derived five themes: classic/linen/porcelain/color/graphite).
    /// Persisted so the chosen rope+clip look survives restarts; falls back to "color".
    public string Appearance { get; set; } = "color";
    public Dictionary<string, string> Dismissed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AlwaysVisible { get; set; }
    // On by default: screenshots and copied images land on the line without opting in.
    public bool ClipboardImages { get; set; } = true;
    public bool ClipboardText { get; set; } = true;
    public bool English { get; set; } = !System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh");
    public int Capacity { get; set; } = 8;
    /// Empty means "auto": pick the best installed OCR language. Otherwise an exact language tag.
    public string OcrLanguage { get; set; } = "";
    /// Which recogniser backs "extract text": "windows" for the built-in one, "v6" for PP-OCRv6.
    /// PP-OCRv6 is the default because its tiny tier ships with the app and needs no download, and
    /// it measured clearly better on the same sample.
    public string OcrEngine { get; set; } = "v6";
    /// PP-OCRv6 tier: "tiny", "small" or "medium". Only read when OcrEngine is "v6".
    public string OcrModel { get; set; } = "tiny";

    // Every optional interaction is switchable, so a behaviour that gets in someone's way can be
    // turned off from the settings window instead of having to be lived with.
    /// Hide the rope while the foreground window covers the whole monitor, e.g. a full-screen video.
    public bool HideFullScreen { get; set; } = true;
    /// Ctrl+Alt+Up toggles the rope. Turning it off releases the combination to other software.
    public bool HotKeyEnabled { get; set; } = true;
    /// A single click on a card copies it: an image as a bitmap, a note as text, anything else as a
    /// file. Turning this off leaves the click doing nothing, so a stray click cannot overwrite the
    /// clipboard.
    public bool ClickToCopy { get; set; } = true;
    /// Ctrl held while dragging a card out makes the drop a move rather than a copy.
    public bool CtrlDragMoves { get; set; } = true;
    /// The pin and take-down buttons drawn on the card.
    public bool CardButtons { get; set; } = true;
    /// Dropping files onto the rope hangs them.
    public bool DropToHang { get; set; } = true;
    /// How long the rope stays out after the pointer leaves, in milliseconds.
    public int RevealDelay { get; set; } = 1800;
    /// How long the pointer must stay in the top-edge trigger zone before the rope slides out, in
    /// milliseconds. 0 reveals immediately on hover (the original behaviour). Range 0–10000 ms.
    public int RevealDwell { get; set; } = 0;
    /// Which monitor the rope hangs on. Empty follows the pointer; otherwise a Win32 device name
    /// ("\\.\DISPLAY2") pins it to one screen.
    public string Display { get; set; } = "";
    /// Enlarge a picture card under the rope while the pointer rests on it.
    public bool HoverPreview { get; set; } = true;
    /// The global hot key that toggles the rope, written as "Ctrl+Alt+Up". Empty disables it
    /// regardless of HotKeyEnabled. The WPF key name is preserved so it round-trips through
    /// Native.TryParseHotKey; only registered when HotKeyEnabled is also true.
    public string? HotKey { get; set; } = "Ctrl+Alt+Up";
    public static Settings Load(string path)
    {
        if (!File.Exists(path)) return new();
        var text = File.ReadAllText(path);
        try
        {
            var value = JsonSerializer.Deserialize<Settings>(text) ?? throw new JsonException();
            if (value.Folders is null || value.Attached is null || value.Recent is null || value.Pins is null || value.PinSnapshots is null || value.Dismissed is null || value.DeskPins is null || value.TextFavorites is null) throw new JsonException();
            if (value.Pictures is null) value.Pictures = [];
            value.Pictures.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Path));
            value.Dismissed = new(value.Dismissed, StringComparer.OrdinalIgnoreCase);
            value.Capacity = Math.Clamp(value.Capacity, 3, 50);
            value.RevealDwell = Math.Clamp(value.RevealDwell, 0, 10000);
            // A file written before PP-OCRv6 existed carries no engine choice, and the fresh default
            // cannot tell it apart from someone having deliberately picked the Windows engine. Those
            // files are upgraded to the more accurate engine; an explicit choice is left alone.
            if (!JsonDocument.Parse(text).RootElement.TryGetProperty(nameof(OcrEngine), out _)) value.OcrEngine = "v6";
            if (string.IsNullOrWhiteSpace(value.Appearance)) value.Appearance = "color";
            return value;
        }
        catch (JsonException)
        {
            // Preserve damaged preferences before creating a replacement. Never touch source files.
            File.Copy(path, path + ".invalid-" + Guid.NewGuid().ToString("N"));
            return new();
        }
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
