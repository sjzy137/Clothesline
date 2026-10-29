using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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

    /// The Text Envelope: every collected note in one searchable list, with view / copy / favourite.
    private void ShowTextEnvelope()
    {
        var window = new Window { Title = T("文字信封", "Text Envelope"), Width = 660, Height = 540, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Theme.Brush(CurrentTheme.CardSurface) };
        var panel = new DockPanel { Margin = new Thickness(15) };
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 10), ToolTip = T("搜索文字内容", "Search note text") };
        DockPanel.SetDock(search, Dock.Top); panel.Children.Add(search);
        var list = new ListBox(); DockPanel.SetDock(list, Dock.Top); panel.Children.Add(list);
        window.Content = panel;
        void Populate()
        {
            list.Items.Clear();
            var q = search.Text.Trim();
            foreach (var pair in clipboardItems.Where(k => k.Value is not null))
            {
                var path = pair.Key; var note = pair.Value!;
                var title = note.Plain.Replace("\r", " ").Replace("\n", " ").Trim();
                if (title.Length > 48) title = title[..48] + "…";
                if (!string.IsNullOrEmpty(q) &&
                    !note.Plain.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !title.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                var row = new DockPanel { Width = 580, ToolTip = path };
                var view = new Button { Content = T("查看", "View"), Padding = new Thickness(10, 3, 10, 3), Style = Theme.ButtonStyle };
                view.Click += (_, _) => Guard(() => { var fr = FileRecord.Read(path); if (fr is not null) ShowTextNote(fr); });
                var copy = new Button { Content = T("复制", "Copy"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), Style = Theme.ButtonStyle };
                copy.Click += (_, _) => Guard(() => { var fr = FileRecord.Read(path); if (fr is not null) CopyText(fr); });
                var fav = new Button { Content = IsFavorite(path) ? T("取消收藏", "Unfavorite") : T("收藏", "Favorite"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), Style = Theme.ButtonStyle };
                fav.Click += (_, _) => { ToggleFavorite(path); Populate(); };
                DockPanel.SetDock(view, Dock.Right); row.Children.Add(view);
                DockPanel.SetDock(copy, Dock.Right); row.Children.Add(copy);
                DockPanel.SetDock(fav, Dock.Right); row.Children.Add(fav);
                row.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4) });
                list.Items.Add(row);
            }
            if (list.Items.Count == 0) list.Items.Add(new TextBlock { Text = T("暂无文字便签。复制一段文字即可收集到这里。", "No text notes yet. Copy some text and it lands here."), Margin = new Thickness(4), Foreground = System.Windows.Media.Brushes.Gray });
        }
        search.TextChanged += (_, _) => Populate();
        Populate(); window.Show();
    }
}
