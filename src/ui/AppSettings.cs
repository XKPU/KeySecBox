using System;
using System.IO;
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
    private static bool _loaded;

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
                win = new { x = _winX, y = _winY, w = _winW, h = _winH }
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
