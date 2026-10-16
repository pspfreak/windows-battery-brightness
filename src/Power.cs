using System.Runtime.InteropServices;

namespace BatteryBrightness
{
    enum PowerSource { AC, Battery }

    static class Power
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        /// <summary>Battery only when Windows positively reports the charger as unplugged; desktops and unknown states count as AC.</summary>
        public static PowerSource GetSource() =>
            GetSystemPowerStatus(out var s) && s.ACLineStatus == 0 ? PowerSource.Battery : PowerSource.AC;

        public static bool BatterySaverOn => GetSystemPowerStatus(out var s) && s.SystemStatusFlag == 1;

        public static int? BatteryPercent =>
            GetSystemPowerStatus(out var s) && (s.BatteryFlag & 128) == 0 && s.BatteryLifePercent <= 100
                ? s.BatteryLifePercent
                : (int?)null;
    }
}
