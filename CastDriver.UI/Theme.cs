using System.Windows.Media;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace CastDriver.UI;

public enum AppTheme { Dark, Light }

// Switches the app between dark and light by recoloring the shared brush objects in place.
// Because every window/style references the same brush instances (defined once at the App
// level), changing a brush's Color updates the whole UI live — no window rebuild needed.
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static AppTheme Parse(string? s) =>
        string.Equals(s, "Light", StringComparison.OrdinalIgnoreCase) ? AppTheme.Light : AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        if (theme == AppTheme.Light)
        {
            Set("BgBrush",           0xFF, 0xFF, 0xFF);
            Set("SurfaceBrush",      0xEC, 0xEC, 0xEE);
            Set("BorderBrush",       0xD0, 0xD0, 0xD4);
            Set("TextPrimaryBrush",  0x1C, 0x1C, 0x1E);
            Set("TextSecondaryBrush",0x6E, 0x6E, 0x73);
            Set("TextTertiaryBrush", 0xAE, 0xAE, 0xB2);
        }
        else
        {
            Set("BgBrush",           0x1C, 0x1C, 0x1E);
            Set("SurfaceBrush",      0x2C, 0x2C, 0x2E);
            Set("BorderBrush",       0x3A, 0x3A, 0x3C);
            Set("TextPrimaryBrush",  0xFF, 0xFF, 0xFF);
            Set("TextSecondaryBrush",0x8E, 0x8E, 0x93);
            Set("TextTertiaryBrush", 0x48, 0x48, 0x4A);
        }
    }

    private static void Set(string key, byte r, byte g, byte b)
    {
        if (Application.Current?.Resources[key] is SolidColorBrush { IsFrozen: false } brush)
            brush.Color = Color.FromRgb(r, g, b);
    }
}
