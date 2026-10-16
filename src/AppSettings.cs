using System;
using System.IO;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryBrightness
{
    /// <summary>User settings, kept in HKCU\Software\BatteryBrightness so the exe needs no files beside it.</summary>
    sealed class AppSettings
    {
        const string KeyPath = @"Software\BatteryBrightness";

        public int AcBrightness = -1;   // -1 = not set yet (initialised from the current level)
        public int DcBrightness = -1;
        public bool LearnChanges = true;
        public bool RefreshEnabled;
        public int AcRefresh;           // Hz; 0 = don't change
        public int DcRefresh;
        public string RefreshDisplay = ""; // monitor device path; "" = built-in display

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                if (key == null) return s;
                s.AcBrightness = Int(key, "AcBrightness", s.AcBrightness);
                s.DcBrightness = Int(key, "DcBrightness", s.DcBrightness);
                s.LearnChanges = Int(key, "LearnChanges", 1) != 0;
                s.RefreshEnabled = Int(key, "RefreshEnabled", 0) != 0;
                s.AcRefresh = Int(key, "AcRefresh", 0);
                s.DcRefresh = Int(key, "DcRefresh", 0);
                s.RefreshDisplay = key.GetValue("RefreshDisplay") as string ?? "";
            }
            catch (Exception e) when (e is IOException || e is SecurityException || e is UnauthorizedAccessException) { }
            return s;
        }

        public void Save()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key.SetValue("AcBrightness", AcBrightness, RegistryValueKind.DWord);
                key.SetValue("DcBrightness", DcBrightness, RegistryValueKind.DWord);
                key.SetValue("LearnChanges", LearnChanges ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("RefreshEnabled", RefreshEnabled ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("AcRefresh", AcRefresh, RegistryValueKind.DWord);
                key.SetValue("DcRefresh", DcRefresh, RegistryValueKind.DWord);
                key.SetValue("RefreshDisplay", RefreshDisplay ?? "", RegistryValueKind.String);
            }
            catch (Exception e) when (e is IOException || e is SecurityException || e is UnauthorizedAccessException) { }
        }

        static int Int(RegistryKey key, string name, int fallback) => key.GetValue(name) is int v ? v : fallback;
    }

    /// <summary>The per-user Run entry that starts the app (in the tray) at sign-in.</summary>
    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "BatteryBrightness";

        static string Command => "\"" + Application.ExecutablePath + "\" --background";

        public static bool IsEnabled
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
        }

        public static void Set(bool enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, false);
        }

        /// <summary>Points an existing Run entry at this exe, in case it was moved.</summary>
        public static void Repair()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key?.GetValue(ValueName) is string current && current != Command)
                key.SetValue(ValueName, Command);
        }
    }
}
