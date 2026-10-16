using System;
using System.Drawing;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace BatteryBrightness
{
    /// <summary>Flyout colors, following the Windows system (taskbar) light/dark setting and accent color.</summary>
    sealed class Theme
    {
        public bool Dark;
        public Color Back, Fore, Dim, Divider, Border, ControlBack;
        public Color Accent, OnAccent, Track, ThumbFill, ThumbBorder, Off;

        public static bool SystemIsDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") is int v && v == 0;
            }
            catch (Exception e) when (e is IOException || e is SecurityException) { return false; }
        }

        public static Theme Load()
        {
            // AccentPalette holds the Windows accent ramp: index 1 is the light shade used on dark
            // surfaces, index 4 the dark shade used on light surfaces.
            return SystemIsDark()
                ? new Theme
                {
                    Dark = true,
                    Back = Rgb(0x2B2B2B), Fore = Color.White, Dim = Rgb(0xA8A8A8),
                    Divider = Rgb(0x3D3D3D), Border = Rgb(0x474747), ControlBack = Rgb(0x373737),
                    Accent = AccentShade(1) ?? Rgb(0x4CC2FF), OnAccent = Color.Black,
                    Track = Rgb(0x9A9A9A), ThumbFill = Rgb(0x454545), ThumbBorder = Rgb(0x505050), Off = Rgb(0xCFCFCF),
                }
                : new Theme
                {
                    Dark = false,
                    Back = Rgb(0xF3F3F3), Fore = Rgb(0x1B1B1B), Dim = Rgb(0x5F5F5F),
                    Divider = Rgb(0xDDDDDD), Border = Rgb(0xC8C8C8), ControlBack = Color.White,
                    Accent = AccentShade(4) ?? Rgb(0x005FB8), OnAccent = Color.White,
                    Track = Rgb(0x868686), ThumbFill = Color.White, ThumbBorder = Rgb(0xD0D0D0), Off = Rgb(0x5D5D5D),
                };
        }

        public bool SameAs(Theme other) => other != null && other.Dark == Dark && other.Accent.ToArgb() == Accent.ToArgb();

        static Color Rgb(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

        static Color? AccentShade(int index)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
                if (key?.GetValue("AccentPalette") is byte[] p && p.Length >= (index + 1) * 4)
                    return Color.FromArgb(p[index * 4], p[index * 4 + 1], p[index * 4 + 2]);
            }
            catch (Exception e) when (e is IOException || e is SecurityException) { }
            return null;
        }
    }
}
