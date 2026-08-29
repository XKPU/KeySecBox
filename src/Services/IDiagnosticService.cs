namespace KeySecBox;

public interface IDiagnosticService
{
    void Initialize(string basePath, bool enabled);
    void Log(string format, params object?[] args);
    bool IsEnabled { get; set; }
}