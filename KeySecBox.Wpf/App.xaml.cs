using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace KeySecBox;

public partial class App : Application
{
    public App()
    {
        // 启动期异常若无人处理会直接结束进程且无任何提示，
        // 这里统一记录到 startup-error.log 便于定位（XAML 资源缺失等只在运行期暴露）。
        DispatcherUnhandledException += (_, e) => Report(e.Exception, "Dispatcher");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, "AppDomain");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var win = new MainWindow();
            MainWindow = win;
            win.Show();
        }
        catch (Exception ex)
        {
            Report(ex, "Startup");
            MessageBox.Show(ex.ToString(), "KeySecBox 启动失败");
            Shutdown(1);
        }
    }

    private static void Report(Exception? ex, string source)
    {
        if (ex == null) return;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({source}) {ex}\n\n");
        }
        catch { }
    }
}
