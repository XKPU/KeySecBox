using Microsoft.UI.Xaml;
using System.IO;

namespace KeySecBox;

public partial class App : Application
{
    private Window? _window;

    #region 生命周期

    public App()
    {
        UnhandledException += OnUnhandled;
        // 线程池/非 UI 线程与 AppDomain 级异常也需落盘，便于排查崩溃
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash("TaskScheduler", e.Exception);
            e.SetObserved();
        };
        System.AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            WriteCrash("AppDomain", e.ExceptionObject as System.Exception);
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    #endregion

    #region 崩溃日志

    private void OnUnhandled(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        WriteCrash("WinUI", e.Exception);
    }

    private static void WriteCrash(string source, System.Exception? ex)
    {
        try
        {
            var path = AppPaths.CrashLog;
            AppPaths.EnsureDataDir();
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex?.GetType().Name}: {ex?.Message}\n{ex?.ToString()}\n\n";
            // FileStream 直写 UTF-8，避免特殊字符/编码下 File.AppendAllText 失败
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush();
        }
        catch { }
    }

    #endregion
}
