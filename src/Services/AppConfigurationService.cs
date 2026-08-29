using System.Runtime.InteropServices;
using System.Text.Json;

namespace KeySecBox;

public interface IAppConfigurationService
{
    ThemeMode Theme { get; set; }
    (int X, int Y, int Width, int Height) WindowBounds { get; set; }
    int FrameRate { get; set; }
    int MonitorRefreshRate { get; }
    void Save();

    // 动画目标总时长（毫秒，基准）
    int DialogAnimMs { get; }      // 对话框入场
    int UnlockIntroAnimMs { get; } // 解锁后主界面入场（淡入上滑）
    int SortMoveAnimMs { get; }    // 排序滑动
    int ScopeExitAnimMs { get; }   // 分类切换退场（向右淡出）
    int ScopeEnterAnimMs { get; }  // 分类切换入场（左侧淡入 / 停留条目滑动）
    long AlignMsToFrames(long ms);
}

public class AppConfigurationService : IAppConfigurationService
{
    private ThemeMode _theme = ThemeMode.System;
    private int _winX = -1, _winY = -1, _winW = -1, _winH = -1;
    private int _frameRate = 24;
    private bool _loaded;
    
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    public int MonitorRefreshRate
    {
        get
        {
            try
            {
                var hdc = GetDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero) return 60;
                int rate = GetDeviceCaps(hdc, 116);
                ReleaseDC(IntPtr.Zero, hdc);
                return rate > 0 ? rate : 60;
            }
            catch { return 60; }
        }
    }

    public ThemeMode Theme
    {
        get { EnsureLoaded(); return _theme; }
        set { _theme = value; }
    }

    public (int X, int Y, int Width, int Height) WindowBounds
    {
        get { EnsureLoaded(); return (_winX, _winY, _winW, _winH); }
        set { _winX = value.X; _winY = value.Y; _winW = value.Width; _winH = value.Height; }
    }

    public int FrameRate
    {
        get { EnsureLoaded(); return _frameRate; }
        set
        {
            int max = MonitorRefreshRate;
            _frameRate = Math.Clamp(value, 1, max);
        }
    }

    public int DialogAnimMs => 200;
    public int UnlockIntroAnimMs => 450;
    public int SortMoveAnimMs => 240;
    public int ScopeExitAnimMs => 200;
    public int ScopeEnterAnimMs => 300;

    public long AlignMsToFrames(long ms)
    {
        int fps = Math.Max(1, FrameRate);
        double intervalMs = 1000.0 / fps;
        long frames = Math.Max(1, (long)Math.Ceiling(ms / intervalMs));
        return (long)Math.Round(frames * intervalMs);
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(AppPaths.ConfigPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.ConfigPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("theme", out var t))
                Enum.TryParse(t.GetString(), true, out _theme);
            if (root.TryGetProperty("win", out var win))
            {
                if (win.TryGetProperty("x", out var x)) _winX = x.GetInt32();
                if (win.TryGetProperty("y", out var y)) _winY = y.GetInt32();
                if (win.TryGetProperty("w", out var w)) _winW = w.GetInt32();
                if (win.TryGetProperty("h", out var h)) _winH = h.GetInt32();
            }
            if (root.TryGetProperty("frameRate", out var fr) && fr.TryGetInt32(out int rate))
            {
                _frameRate = Math.Clamp(rate, 1, MonitorRefreshRate);
            }
        }
        catch { }
    }

    public void Save()
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
            File.WriteAllText(AppPaths.ConfigPath, json);
        }
        catch { }
    }
}