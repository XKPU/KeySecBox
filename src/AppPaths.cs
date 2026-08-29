namespace KeySecBox;

internal static class AppPaths
{
    public static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");
    public static readonly string VaultBase = Path.Combine(DataDir, "vault");

    public static readonly string ConfigPath = Path.Combine(DataDir, "appconfig.json");
    public static readonly string CrashLog = Path.Combine(DataDir, "crash.log");
    public static readonly string MasterRecoveryFile = Path.Combine(DataDir, "master.recovery");

    public static void EnsureDataDir()
    {
        Directory.CreateDirectory(DataDir);
    }
}