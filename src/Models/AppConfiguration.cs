namespace KeySecBox;

public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}

public class AppConfiguration
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public int WinX { get; set; } = -1;
    public int WinY { get; set; } = -1;
    public int WinW { get; set; } = -1;
    public int WinH { get; set; } = -1;
    public int FrameRate { get; set; } = 24;
}