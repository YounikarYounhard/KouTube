using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace KouTube;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!Alone()) { Shutdown(); return; }
        // a crash must not lose the queue or the history: both are written as they change, so just say what happened
        DispatcherUnhandledException += (_, a) => { MessageBox.Show(a.Exception.Message, "KouTube на ПК — ошибка"); a.Handled = true; };
        Lib.Init();
        Downloader.Load();
        base.OnStartup(e);
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

    static void Front(Process p) { ShowWindow(p.MainWindowHandle, 9); SetForegroundWindow(p.MainWindowHandle); }     // 9 = restore if minimised

    /// <summary>
    /// One KouTube at a time — two would write the same queue and library. The same copy started again just comes to the
    /// front; a copy from another folder (an old download, say) is offered to be closed, with its version and place.
    /// </summary>
    static bool Alone()
    {
        var me = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(me.ProcessName).Where(p => p.Id != me.Id))
        {
            string? path = null;
            try { path = other.MainModule?.FileName; } catch (Exception) { }
            if (path == null || string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) { Front(other); return false; }
            var ver = FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+')[0];
            if (MessageBox.Show($"Уже открыт другой KouTube {ver}:\n{path}\n\nЗакрыть его и открыть этот, {Ui.Version}?", "KouTube",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { Front(other); return false; }
            try { other.Kill(); other.WaitForExit(5000); } catch (Exception) { }
        }
        return true;
    }
}

public static class Ui
{
    /// <summary>"0.0.6" — in the window title, so it is always clear which KouTube is open.</summary>
    public static string Version => typeof(Ui).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
    public static string Gb(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} ГБ" : $"{b / (double)(1L << 20):0} МБ";
    public static void OnUi(Action a) { var d = Application.Current?.Dispatcher; if (d == null || d.CheckAccess()) a(); else d.BeginInvoke(a); }
}
