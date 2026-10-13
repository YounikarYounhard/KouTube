using System.Windows;
using System.Windows.Threading;

namespace KouTube;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // a crash must not lose the queue or the history: both are written as they change, so just say what happened
        DispatcherUnhandledException += (_, a) => { MessageBox.Show(a.Exception.Message, "KouTube на ПК — ошибка"); a.Handled = true; };
        Lib.Init();
        Downloader.Load();
        base.OnStartup(e);
    }
}

public static class Ui
{
    public static string Gb(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} ГБ" : $"{b / (double)(1L << 20):0} МБ";
    public static void OnUi(Action a) { var d = Application.Current?.Dispatcher; if (d == null || d.CheckAccess()) a(); else d.BeginInvoke(a); }
}
