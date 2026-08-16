using System;
using System.IO;

namespace KeySecBox;

/// <summary>
/// 运行期生成文件的统一目录：数据/日志集中在 <运行目录>\data 下，整体拷贝即可迁移。
/// </summary>
internal static class AppPaths
{
    #region 路径

    public static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");
    public static readonly string VaultBase = Path.Combine(DataDir, "vault"); // 保险库 basename

    public static readonly string ConfigPath = Path.Combine(DataDir, "appconfig.json");
    public static readonly string TraceLog = Path.Combine(DataDir, "trace.log");
    public static readonly string CrashLog = Path.Combine(DataDir, "crash.log");
    public static readonly string MasterRecoveryFile = Path.Combine(DataDir, "master.recovery");

    #endregion

    /// <summary>
    /// 运行期追踪开关：仅当保险库开启诊断模式时 UI 才写 trace.log，
    /// 解锁成功后由 Store.GetDiagnostics() 同步。
    /// </summary>
    public static bool TraceEnabled;

    public static void EnsureDataDir()
    {
        Directory.CreateDirectory(DataDir);
    }
}
