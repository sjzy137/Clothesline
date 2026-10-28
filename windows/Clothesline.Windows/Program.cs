using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Clothesline.Core;

namespace Clothesline.Windows;
internal static class Program
{
    /// The Explorer context menu entry starts a second copy of the app. Only one instance may run, so
    /// that copy leaves the selected paths here and the running instance collects them on its next scan.
    internal static string PendingDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clothesline", "pending");
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--self-test") return WindowsSelfTest.Run(args[1]);
        // Written before the single-instance check, so they survive either way.
        if (args.Length > 0) QueueAttachments(args);
        using var instance = new Mutex(true, "Local\\Clothesline.Windows", out var first);
        if (!first) return 0;
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clothesline", "settings.json");
            var settings = Settings.Load(settingsPath);
            var downloads = Native.Downloads();
            if (!settings.Folders.Contains(downloads, StringComparer.OrdinalIgnoreCase)) settings.Folders.Insert(0, downloads);
            var window = new RopeWindow(settings, () => settings.Save(settingsPath));
            app.DispatcherUnhandledException += (_, e) => { MessageBox.Show(e.Exception.Message, "Clothesline"); e.Handled = true; };
            app.Run(window);
            return 0;
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Clothesline"); return 1; }
    }
    private static void QueueAttachments(string[] paths)
    {
        try
        {
            Directory.CreateDirectory(PendingDirectory);
            // A unique name per invocation avoids colliding with the reader in the running instance.
            File.WriteAllLines(Path.Combine(PendingDirectory, "pending-" + Guid.NewGuid().ToString("N") + ".txt"), paths);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
