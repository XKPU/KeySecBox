using Microsoft.UI.Xaml;

namespace KeySecBox;

public partial class App : Application
{
    private Window? _window;

    private readonly Dictionary<Type, Func<object>> _services = new();
    private readonly Dictionary<Type, object?> _singletons = new();

    public App()
    {
        UnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash("TaskScheduler", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            WriteCrash("AppDomain", e.ExceptionObject as Exception);
        };
        InitializeComponent();
        ConfigureServices();
    }

    private void ConfigureServices()
    {
        _services[typeof(ICryptoService)] = () => new CryptoService();
        _services[typeof(IFileIOService)] = () => new FileIOService();
        _services[typeof(IBinaryFormatService)] = () => new BinaryFormatService();
        _services[typeof(IJsonSerializationService)] = () => new JsonSerializationService();
        _services[typeof(IDiagnosticService)] = () => new DiagnosticService();
        _services[typeof(IAppConfigurationService)] = () => new AppConfigurationService();
        _services[typeof(IClipboardService)] = () => new ClipboardService();
        _services[typeof(ICsvService)] = () => new CsvService();
        _services[typeof(IMasterRecoveryService)] = () => new MasterRecoveryService(
            GetService<ICryptoService>(), GetService<IFileIOService>());
        _services[typeof(IRecoveryService)] = () => new RecoveryService(
            GetService<IVaultService>());
        _services[typeof(IVaultService)] = () => new VaultService(
            GetService<ICryptoService>(),
            GetService<IFileIOService>(),
            GetService<IBinaryFormatService>(),
            GetService<IJsonSerializationService>(),
            GetService<IDiagnosticService>());
    }

    private T GetService<T>() where T : class
    {
        var type = typeof(T);
        if (_singletons.TryGetValue(type, out var existing) && existing is T typedExisting)
            return typedExisting;

        if (!_services.TryGetValue(type, out var factory))
            throw new InvalidOperationException($"Service {type.Name} is not registered.");

        var instance = factory();
        _singletons[type] = instance;
        return (T)instance;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var crypto = GetService<ICryptoService>();
        var fileIO = GetService<IFileIOService>();
        var binary = GetService<IBinaryFormatService>();
        var json = GetService<IJsonSerializationService>();
        var diagSvc = GetService<IDiagnosticService>();
        var vault = GetService<IVaultService>();
        var clipboard = GetService<IClipboardService>();
        var csv = GetService<ICsvService>();
        var appConfig = GetService<IAppConfigurationService>();
        var masterRecovery = GetService<IMasterRecoveryService>();
        var recovery = GetService<IRecoveryService>();
        _window = new MainWindow(vault, clipboard, csv, appConfig, masterRecovery, recovery,
            crypto, fileIO, binary, json, diagSvc);
        _window.Activate();
    }

    private void OnUnhandled(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        WriteCrash("WinUI", e.Exception);
    }

    private static void WriteCrash(string source, Exception? ex)
    {
        try
        {
            var path = AppPaths.CrashLog;
            AppPaths.EnsureDataDir();
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex?.GetType().Name}: 0x{ex?.HResult:X8}: {ex?.Message}\n{ex}\n\n";
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush();
        }
        catch { }
    }
}