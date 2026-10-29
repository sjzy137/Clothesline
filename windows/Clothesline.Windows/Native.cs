using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Clothesline.Windows;
internal static class Native
{
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowLong(IntPtr hwnd, int index, IntPtr value);
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct ShellInfo
    {
        public IntPtr Icon; public int Index; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=80)] public string TypeName;
    }
    public static string Downloads()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr) == 0)
        { try { return Marshal.PtrToStringUni(ptr)!; } finally { Marshal.FreeCoTaskMem(ptr); } }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
    public static Point Cursor { get { GetCursorPos(out var p); return new Point(p.X,p.Y); } }
    public static bool MousePressed => GetAsyncKeyState(1) < 0;
    public static void ClickThrough(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var old = GetWindowLong(hwnd, -20).ToInt64();
        var value = enabled ? old | 0x20 : old & ~0x20;
        if (old != value) SetWindowLong(hwnd, -20, new IntPtr(value));
    }
    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    public static void Reveal(string path) => Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
    public static BitmapSource ReadImage(string path)
    {
        using var stream=File.OpenRead(path);
        var image=BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
        image.Freeze(); return image;
    }
    public static ImageSource? Thumbnail(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")
        {
            try
            {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 240; image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); return image;
            } catch { }
        }
        var shell = new ShellInfo();
        if (SHGetFileInfo(path, 0, ref shell, (uint)Marshal.SizeOf<ShellInfo>(), 0x100) == IntPtr.Zero) return null;
        try { var bitmap = Imaging.CreateBitmapSourceFromHIcon(shell.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); bitmap.Freeze(); return bitmap; }
        finally { if (shell.Icon != IntPtr.Zero) DestroyIcon(shell.Icon); }
    }
    // ---- Global hot key ------------------------------------------------------
    public const int HotKeyMessage = 0x0312;
    public const uint ModifierAlt = 0x0001, ModifierControl = 0x0002, ModifierShift = 0x0004, ModifierWindows = 0x0008;
    public const uint VirtualKeyUp = 0x26;
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(IntPtr window, int id);

    /// Turns a "Ctrl+Alt+Up" style string into the Win32 modifiers + virtual key that
    /// RegisterHotKey expects. The non-modifier part is parsed as a WPF Key name and converted with
    /// KeyInterop, so it round-trips the value the settings capture control produces. Returns false
    /// for an empty string or anything that is not exactly one real key plus zero or more modifiers.
    public static bool TryParseHotKey(string? value, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0; virtualKey = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        Key? mainKey = null;
        foreach (var raw in value.Split('+'))
        {
            var part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= ModifierControl; break;
                case "alt": modifiers |= ModifierAlt; break;
                case "shift": modifiers |= ModifierShift; break;
                case "win": case "windows": case "super": modifiers |= ModifierWindows; break;
                default:
                    if (!Enum.TryParse<Key>(part, true, out var key)) return false;
                    if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return false;
                    mainKey = key;
                    break;
            }
        }
        if (mainKey is null) return false;
        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(mainKey.Value);
        return true;
    }

    // ---- Full-screen detection -----------------------------------------------
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowRectangle rectangle);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint policy);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInformation info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInformation
    {
        public int Size; public WindowRectangle Monitor; public WindowRectangle Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    /// A foreground window that covers the whole monitor means something is playing or gaming full screen.
    /// Maximised windows stop at the taskbar and therefore do not match.
    public static bool ForegroundCoversMonitor()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        var name = new StringBuilder(256);
        var className = GetClassName(window, name, 256) > 0 ? name.ToString() : "";
        // The desktop and the taskbar are not full-screen applications.
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (!GetWindowRect(window, out var bounds)) return false;
        if (bounds.Right - bounds.Left <= 0 || bounds.Bottom - bounds.Top <= 0) return false;
        var monitor = MonitorFromWindow(window, 2); // MONITOR_DEFAULTTONEAREST
        if (monitor == IntPtr.Zero) return false;
        var info = new MonitorInformation { Size = Marshal.SizeOf<MonitorInformation>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        // Compare against the work area (which excludes the taskbar), so a maximised window is also
        // treated as covering the screen and suppresses the auto-reveal. With the whole monitor the
        // comparison used before, a maximised browser/Excel/Word stops at the taskbar, failed the
        // test, and let its title bar/ribbon sit in the top trigger strip and pop the rope up unasked.
        return bounds.Left <= info.Work.Left && bounds.Top <= info.Work.Top &&
               bounds.Right >= info.Work.Right && bounds.Bottom >= info.Work.Bottom;
    }

    // ---- Editing and move semantics ------------------------------------------
    public static bool Edit(string path)
    {
        if (TryVerb(path, "edit")) return true;
        // mspaint lives in the real System32. SpecialFolder.System can resolve to SysWOW64 under a
        // redirected (32-bit) process, where mspaint does not exist, so resolve via SystemRoot first.
        var windows = Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var paint = Path.Combine(windows, "System32", "mspaint.exe");
        if (!File.Exists(paint)) paint = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mspaint.exe");
        if (File.Exists(paint) && TryRun(paint, "\"" + path + "\"")) return true;
        // Last resort: open with the default association so something always happens.
        return TryVerb(path, "open");
    }
    private static bool TryVerb(string path, string verb)
    { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = verb }); return true; } catch { return false; } }
    private static bool TryRun(string file, string arguments)
    { try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Arguments = arguments }); return true; } catch { return false; } }

    /// Asks a well behaved drop target to move instead of copy. It must be written as a stream: a
    /// byte array is marshalled differently and arrives as garbage. Explorer does not act on this
    /// when the CF_HDROP comes from outside the shell, so a Ctrl+drag still has to be finished by
    /// the source — see RopeWindow.CompleteMove.
    public const string PreferredDropEffect = "Preferred DropEffect";
    public const int DropEffectMove = 2;
    public static void MarkAsMove(System.Windows.DataObject payload)
    { payload.SetData(PreferredDropEffect, new MemoryStream(new byte[] { DropEffectMove, 0, 0, 0 })); }

    /// Moves a file or folder to the Recycle Bin rather than deleting it outright. Ctrl+drag needs
    /// this because the app cannot see where the drop target put its copy, so an unrecoverable
    /// delete would risk losing the only copy if the target's copy did not in fact land.
    public static bool Recycle(string path)
    {
        // pFrom has to be terminated by two nulls because it can carry several paths.
        var operation = new ShellFileOperation
        {
            Function = FileOperationDelete,
            From = path + "\0\0",
            Flags = AllowUndo | NoConfirmation | Silent | NoErrorUi
        };
        return SHFileOperation(ref operation) == 0 && !operation.AnyOperationsAborted;
    }

    private const ushort FileOperationDelete = 3;
    private const ushort AllowUndo = 0x0040, NoConfirmation = 0x0010, Silent = 0x0004, NoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileOperation
    {
        public IntPtr Owner;
        public ushort Function;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShellFileOperation operation);

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(IntPtr handle);
    /// Reads the drop effect back so the smoke test can prove a move stays a move. Returns -1 when the
    /// clipboard is unavailable or the marker is missing.
    public static int ReadPreferredDropEffect()
    {
        if (!OpenClipboard(IntPtr.Zero)) return -1;
        try
        {
            var memory = GetClipboardData(RegisterClipboardFormatW(PreferredDropEffect));
            if (memory == IntPtr.Zero) return -1;
            var view = GlobalLock(memory);
            if (view == IntPtr.Zero) return -1;
            try { return GlobalSize(memory) < 4 ? -1 : Marshal.ReadInt32(view); }
            finally { GlobalUnlock(memory); }
        }
        finally { CloseClipboard(); }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string StartupCommand => "\"" + Environment.ProcessPath + "\"";
    public static bool LaunchAtLogin
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return string.Equals(key?.GetValue("Clothesline") as string, StartupCommand, StringComparison.OrdinalIgnoreCase); }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("Clothesline", StartupCommand);
            else if (string.Equals(key.GetValue("Clothesline") as string, StartupCommand, StringComparison.OrdinalIgnoreCase)) key.DeleteValue("Clothesline", false);
        }
    }
}
