using System;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

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
    private static int _dialogCornerRadius = 12;      // 0~32，0=直角

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
                // VREFRESH 在远程桌面 / 虚拟机 / 部分驱动下会返回 0 或 1，
                // 直接采用会把帧率钳死到 1，这里对异常低值统一兜底为 60。
                return rate >= 30 ? rate : 60;
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

    public static int DialogCornerRadius
    {
        get { EnsureLoaded(); return _dialogCornerRadius; }
        set { _dialogCornerRadius = Math.Clamp(value, 0, 32); Save(); }
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

    private sealed class WinRect
    {
        public int x { get; set; } = -1;
        public int y { get; set; } = -1;
        public int w { get; set; } = -1;
        public int h { get; set; } = -1;
    }

    // 落盘字段名保持小写 camelCase，与既有配置文件完全一致
    private sealed class SettingsData
    {
        public string? theme { get; set; }
        public WinRect? win { get; set; }
        public int? frameRate { get; set; }
        public int? dialogCornerRadius { get; set; }
    }

    private static readonly JsonSerializerSettings JsonOpts = VaultJson.CreatePersistSettings();

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var data = VaultJson.DeserializeOrDefault<SettingsData>(File.ReadAllText(SettingsPath), JsonOpts);
            if (data == null) return;

            if (data.theme != null)
                _theme = (ThemeMode)Enum.Parse(typeof(ThemeMode), data.theme);
            if (data.win is { } win)
            {
                _winX = win.x;
                _winY = win.y;
                _winW = win.w;
                _winH = win.h;
            }
            if (data.frameRate is { } rate)
                _frameRate = Math.Clamp(rate, 1, MonitorRefreshRate);
            if (data.dialogCornerRadius is { } dcr)
                _dialogCornerRadius = Math.Clamp(dcr, 0, 32);
        }
        catch
        {
            // 配置损坏时回退默认值
        }
    }

    private static void Save()
    {
        try
        {
            AppPaths.EnsureDataDir();
            var json = VaultJson.Serialize(new SettingsData
            {
                theme = _theme.ToString(),
                win = new WinRect { x = _winX, y = _winY, w = _winW, h = _winH },
                frameRate = _frameRate,
                dialogCornerRadius = _dialogCornerRadius
            }, JsonOpts);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // 写配置失败不致命
        }
    }

    #endregion
}
