using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace KeySecBox;

public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}

/// <summary>应用级本地配置（主题、窗口位置/大小等 UI 偏好），与保险库数据分离。</summary>
public static class AppSettings
{
    private static readonly string SettingsPath = AppPaths.ConfigPath;

    private static ThemeMode _theme = ThemeMode.System;
    private static int _winX = -1, _winY = -1, _winW = -1, _winH = -1; // -1 = 未记录，用默认
    private static int _frameRate = 24; // 全局动画帧率，默认 24fps
    private static bool _loaded;

    #region Win32 显示器刷新率

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    private const int VREFRESH = 116;

    /// <summary>当前主显示器刷新率（Hz）；获取失败时返回 60。</summary>
    public static int MonitorRefreshRate
    {
        get
        {
            try
            {
                var hdc = GetDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero) return 60;
                int rate = GetDeviceCaps(hdc, VREFRESH);
                ReleaseDC(IntPtr.Zero, hdc);
                return rate > 0 ? rate : 60;
            }
            catch { return 60; }
        }
    }

    #endregion

    #region 属性

    public static ThemeMode Theme
    {
        get { EnsureLoaded(); return _theme; }
        set { _theme = value; Save(); }
    }

    /// <summary>上次退出时的窗口位置与大小；未记录过则各值为 -1。</summary>
    public static (int X, int Y, int Width, int Height) WindowBounds
    {
        get { EnsureLoaded(); return (_winX, _winY, _winW, _winH); }
        set { _winX = value.X; _winY = value.Y; _winW = value.Width; _winH = value.Height; Save(); }
    }

    /// <summary>全局动画帧率（fps）。默认 24，最低 1，最高为显示器刷新率。</summary>
    public static int FrameRate
    {
        get { EnsureLoaded(); return _frameRate; }
        set
        {
            int max = MonitorRefreshRate;
            _frameRate = Math.Clamp(value, 1, max);
            Save();
        }
    }

    // 动画目标总时长（毫秒，基准）
    public const int DialogAnimMs = 200;      // 对话框入场
    public const int UnlockIntroAnimMs = 450; // 解锁后主界面入场（淡入上滑）
    public const int SortMoveAnimMs = 240;    // 排序滑动
    public const int ScopeExitAnimMs = 200;   // 分类切换退场（向右淡出）
    public const int ScopeEnterAnimMs = 300;  // 分类切换入场（左侧淡入 / 停留条目滑动）
    public static long AlignMsToFrames(long ms)
    {
        int fps = Math.Max(1, FrameRate);
        double intervalMs = 1000.0 / fps;
        long frames = Math.Max(1, (long)Math.Ceiling(ms / intervalMs));
        return (long)Math.Round(frames * intervalMs);
    }

    #endregion

    #region 读写

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(SettingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("theme", out var t))
                _theme = (ThemeMode)Enum.Parse(typeof(ThemeMode), t.GetString() ?? "System");
            if (doc.RootElement.TryGetProperty("win", out var win))
            {
                _winX = TryGetInt(win, "x", _winX);
                _winY = TryGetInt(win, "y", _winY);
                _winW = TryGetInt(win, "w", _winW);
                _winH = TryGetInt(win, "h", _winH);
            }
            if (doc.RootElement.TryGetProperty("frameRate", out var fr) && fr.TryGetInt32(out int rate))
            {
                int max = MonitorRefreshRate;
                _frameRate = Math.Clamp(rate, 1, max);
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }
    }

    private static int TryGetInt(JsonElement obj, string name, int fallback)
        => obj.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : fallback;

    private static void Save()
    {
        try
        {
            AppPaths.EnsureDataDir();
            var json = JsonSerializer.Serialize(new
            {
                theme = _theme.ToString(),
                win = new { x = _winX, y = _winY, w = _winW, h = _winH },
                frameRate = _frameRate
            });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // 写配置失败不致命
        }
    }

    #endregion
}
