using System.Text;

namespace KeySecBox;

public class DiagnosticService : IDiagnosticService
{
    private string? _basePath;
    private bool _enabled;
    private readonly object _lock = new();

    public bool IsEnabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public void Initialize(string basePath, bool enabled)
    {
        _basePath = basePath;
        _enabled = enabled;
    }

    public void Log(string format, params object?[] args)
    {
        if (!_enabled || _basePath == null) return;

        lock (_lock)
        {
            try
            {
                var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var msg = string.Format(format, args);
                var line = $"[{ts}] {msg}\n";
                var path = _basePath + ".diag.log";
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch { }
        }
    }
}