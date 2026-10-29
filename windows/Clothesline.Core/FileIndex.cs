namespace Clothesline.Core;

public sealed record FileRecord(string Path, bool IsDirectory, long Size, DateTime Modified, DateTime Created)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Fingerprint => $"{IsDirectory}:{Size}:{Modified.Ticks}:{Created.Ticks}";
    public static FileRecord? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) return null;
            var directory = attributes.HasFlag(FileAttributes.Directory);
            return new FileRecord(System.IO.Path.GetFullPath(path), directory, directory ? 0 : info.Length,
                File.GetLastWriteTimeUtc(path), File.GetCreationTimeUtc(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}

public sealed class FileIndex
{
    private sealed record Sample(string Fingerprint, DateTime Since, int Count);
    private readonly Dictionary<string, Sample> samples = new(StringComparer.OrdinalIgnoreCase);
    public static readonly string[] TemporarySuffixes = [".crdownload", ".download", ".part", ".partial", ".tmp", ".temp"];
    public static bool IsCandidate(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return !string.IsNullOrWhiteSpace(name) && !name.StartsWith('.') && !name.StartsWith("~$") &&
            !TemporarySuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }
    public IReadOnlyList<FileRecord> Scan(IEnumerable<string> folders, DateTime now, out List<string> failures)
    {
        failures = [];
        var records = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // Snapshot directory entries so an enumeration failure does not masquerade as an empty folder.
                foreach (var path in Directory.GetFileSystemEntries(folder))
                {
                    if (!IsCandidate(path) || TemporarySuffixes.Any(s => File.Exists(path + s) || Directory.Exists(path + s))) continue;
                    var file = FileRecord.Read(path);
                    if (file is null) continue;
                    visited.Add(file.Path);
                    if (!samples.TryGetValue(file.Path, out var sample) || sample.Fingerprint != file.Fingerprint)
                        sample = new Sample(file.Fingerprint, now, 0);
                    sample = sample with { Count = sample.Count + 1 };
                    samples[file.Path] = sample;
                    if (sample.Count >= 2 && now - sample.Since >= TimeSpan.FromSeconds(2) && now - file.Modified >= TimeSpan.FromSeconds(2))
                        records[file.Path] = file;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { failures.Add(folder + ": " + e.Message); }
        }
        foreach (var path in samples.Keys.Where(p => !visited.Contains(p)).ToArray()) samples.Remove(path);
        return records.Values.OrderByDescending(f => f.Modified).ToArray();
    }
}

public sealed class RopeState
{
    public Dictionary<string, string> Dismissed { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Pinned { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// File paths pinned to the desktop as enlarged floating thumbnails (the "小夹子" feature).
    public HashSet<string> DeskPins { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FileRecord> Select(IEnumerable<FileRecord> records, int capacity)
    {
        var files = records.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Where(f => !Dismissed.TryGetValue(f.Path, out var fingerprint) || fingerprint != f.Fingerprint).ToArray();
        var pins = files.Where(f => Pinned.Contains(f.Path)).OrderByDescending(f => f.Modified).ToList();
        return pins.Concat(files.Where(f => !Pinned.Contains(f.Path)).OrderByDescending(f => f.Modified)
            .Take(Math.Max(0, capacity - pins.Count))).ToList();
    }
    public void Dismiss(FileRecord file) { Dismissed[file.Path] = file.Fingerprint; Pinned.Remove(file.Path); }
    public static string Rename(FileRecord expected, string stem)
    {
        var fresh = FileRecord.Read(expected.Path);
        if (fresh is null || fresh.Fingerprint != expected.Fingerprint) throw new IOException("The file has changed or is unavailable.");
        stem = stem.Trim();
        if (stem.Length == 0 || stem.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || stem.EndsWith('.') || stem.EndsWith(' '))
            throw new IOException("Invalid file name.");
        var extension = expected.IsDirectory ? "" : System.IO.Path.GetExtension(expected.Path);
        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(expected.Path)!, stem + extension);
        if (target == expected.Path) return target;
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("An item with that name already exists. Nothing was overwritten.");
        if (expected.IsDirectory) Directory.Move(expected.Path, target); else File.Move(expected.Path, target, false);
        return target;
    }
}
