using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatteryBrightness
{
    sealed class DisplayInfo
    {
        public string GdiName;    // \\.\DISPLAY1 – can change between sessions
        public string DevicePath; // stable monitor id, used in settings
        public string Name;
        public bool BuiltIn;
        public bool Primary;

        public override string ToString() => Name;
    }

    /// <summary>Active displays and their refresh rates.</summary>
    static class Displays
    {
        public static List<DisplayInfo> GetActive()
        {
            var list = QueryActive() ?? Screen.AllScreens
                .Select(s => new DisplayInfo { GdiName = s.DeviceName, DevicePath = s.DeviceName, Name = s.DeviceName.TrimStart('\\', '.') })
                .ToList();

            string primary = Screen.PrimaryScreen?.DeviceName;
            foreach (var d in list)
                d.Primary = string.Equals(d.GdiName, primary, StringComparison.OrdinalIgnoreCase);

            foreach (var group in list.GroupBy(d => d.Name).Where(g => g.Count() > 1))
            {
                int n = 1;
                foreach (var d in group) d.Name += $" ({n++})";
            }
            return list;
        }

        /// <summary>The display a stored device path refers to, else the built-in panel, else the primary display.</summary>
        public static DisplayInfo Find(List<DisplayInfo> displays, string devicePath) =>
            displays.FirstOrDefault(d => !string.IsNullOrEmpty(devicePath) && string.Equals(d.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase))
            ?? displays.FirstOrDefault(d => d.BuiltIn)
            ?? displays.FirstOrDefault(d => d.Primary)
            ?? displays.FirstOrDefault();

        /// <summary>Refresh rates offered at the display's current resolution and color depth.</summary>
        public static List<int> GetRefreshRates(string gdiName)
        {
            var rates = new SortedSet<int>();
            var current = NewDevMode();
            if (EnumDisplaySettings(gdiName, ENUM_CURRENT_SETTINGS, ref current))
            {
                var mode = NewDevMode();
                for (int i = 0; EnumDisplaySettings(gdiName, i, ref mode); i++)
                {
                    if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight &&
                        mode.dmBitsPerPel == current.dmBitsPerPel && (mode.dmDisplayFlags & DM_INTERLACED) == 0 &&
                        mode.dmDisplayFrequency > 1)
                        rates.Add((int)mode.dmDisplayFrequency);
                }
            }
            return rates.ToList();
        }

        public static int GetRefreshRate(string gdiName)
        {
            var mode = NewDevMode();
            return EnumDisplaySettings(gdiName, ENUM_CURRENT_SETTINGS, ref mode) ? (int)mode.dmDisplayFrequency : 0;
        }

        public static bool SetRefreshRate(string gdiName, int hz)
        {
            var mode = NewDevMode();
            if (!EnumDisplaySettings(gdiName, ENUM_CURRENT_SETTINGS, ref mode)) return false;
            if (mode.dmDisplayFrequency == hz) return true;

            mode.dmDisplayFrequency = (uint)hz;
            mode.dmFields = DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            if (ChangeDisplaySettingsEx(gdiName, ref mode, IntPtr.Zero, CDS_TEST, IntPtr.Zero) != DISP_CHANGE_SUCCESSFUL) return false;
            return ChangeDisplaySettingsEx(gdiName, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero) == DISP_CHANGE_SUCCESSFUL;
        }

        static DEVMODE NewDevMode() => new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

        /// <summary>Uses QueryDisplayConfig so the built-in panel can be told apart from external monitors.</summary>
        static List<DisplayInfo> QueryActive()
        {
            DISPLAYCONFIG_PATH_INFO[] paths;
            DISPLAYCONFIG_MODE_INFO[] modes;
            uint pathCount, modeCount;
            int err;
            do
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount) != 0) return null;
                paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            } while (err == ERROR_INSUFFICIENT_BUFFER);
            if (err != 0) return null;

            var result = new List<DisplayInfo>();
            for (int i = 0; i < pathCount; i++)
            {
                var path = paths[i];

                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                source.header.adapterId = path.sourceInfo.adapterId;
                source.header.id = path.sourceInfo.id;
                if (DisplayConfigGetDeviceInfo(ref source) != 0) continue;
                if (result.Any(d => d.GdiName == source.viewGdiDeviceName)) continue; // cloned outputs share a source

                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                target.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                target.header.adapterId = path.targetInfo.adapterId;
                target.header.id = path.targetInfo.id;
                bool haveTarget = DisplayConfigGetDeviceInfo(ref target) == 0;

                bool builtIn = IsBuiltIn(path.targetInfo.outputTechnology);
                string friendly = haveTarget ? target.monitorFriendlyDeviceName : null;
                result.Add(new DisplayInfo
                {
                    GdiName = source.viewGdiDeviceName,
                    DevicePath = haveTarget && !string.IsNullOrEmpty(target.monitorDevicePath) ? target.monitorDevicePath : source.viewGdiDeviceName,
                    BuiltIn = builtIn,
                    Name = builtIn ? "Built-in display"
                         : !string.IsNullOrWhiteSpace(friendly) ? friendly
                         : source.viewGdiDeviceName.TrimStart('\\', '.'),
                });
            }
            return result.Count > 0 ? result : null;
        }

        static bool IsBuiltIn(uint outputTechnology) =>
            outputTechnology == OUTPUT_TECHNOLOGY_LVDS ||
            outputTechnology == OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED ||
            outputTechnology == OUTPUT_TECHNOLOGY_UDI_EMBEDDED ||
            outputTechnology == OUTPUT_TECHNOLOGY_INTERNAL;

        #region Native

        const int ENUM_CURRENT_SETTINGS = -1;
        const uint DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
        const uint DM_INTERLACED = 0x2;
        const uint CDS_UPDATEREGISTRY = 0x1, CDS_TEST = 0x2;
        const int DISP_CHANGE_SUCCESSFUL = 0;

        const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
        const int ERROR_INSUFFICIENT_BUFFER = 122;
        const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1, DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
        const uint OUTPUT_TECHNOLOGY_LVDS = 6, OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11, OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13, OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        // Only passed through to QueryDisplayConfig; 64 bytes is the native size of the mode union.
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        struct DISPLAYCONFIG_MODE_INFO { public uint infoType; public uint id; public LUID adapterId; }

        [StructLayout(LayoutKind.Sequential)]
        struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        #endregion
    }
}
