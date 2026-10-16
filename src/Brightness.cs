using System;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace BatteryBrightness
{
    /// <summary>Built-in panel brightness through the WMI monitor classes in root\wmi.</summary>
    static class Brightness
    {
        internal static readonly ManagementScope Scope = new ManagementScope(@"\\.\root\wmi");

        static readonly object gate = new object();
        static int pending = -1;
        static bool busy;

        /// <summary>Current brightness (0–100), or null when no display exposes WMI brightness control.</summary>
        public static int? Get()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(Scope, new ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE"));
                using var results = searcher.Get();
                int? level = null;
                foreach (ManagementObject mo in results)
                    using (mo)
                        level ??= Convert.ToInt32(mo["CurrentBrightness"]);
                return level;
            }
            catch (ManagementException) { }
            catch (COMException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        public static void Set(int percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            try
            {
                using var searcher = new ManagementObjectSearcher(Scope, new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE"));
                using var results = searcher.Get();
                foreach (ManagementObject mo in results)
                    using (mo)
                        mo.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)percent });
            }
            catch (ManagementException) { }
            catch (COMException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Sets brightness on a worker thread. Rapid calls (a slider drag) collapse to the latest value.</summary>
        public static void SetAsync(int percent)
        {
            lock (gate)
            {
                pending = percent;
                if (busy) return;
                busy = true;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                while (true)
                {
                    int value;
                    lock (gate)
                    {
                        if (pending < 0) { busy = false; return; }
                        value = pending;
                        pending = -1;
                    }
                    Set(value);
                }
            });
        }
    }

    /// <summary>Raises <see cref="Changed"/> (on a WMI thread) whenever the panel brightness changes, whoever changed it.</summary>
    sealed class BrightnessWatcher : IDisposable
    {
        readonly ManagementEventWatcher watcher;

        public event Action<int> Changed;

        public BrightnessWatcher()
        {
            watcher = new ManagementEventWatcher(Brightness.Scope, new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            watcher.EventArrived += (s, e) =>
            {
                int level;
                try { level = Convert.ToInt32(e.NewEvent["Brightness"]); }
                catch (ManagementException) { return; }
                Changed?.Invoke(level);
            };
        }

        public bool Start()
        {
            try { watcher.Start(); return true; }
            catch (ManagementException) { return false; }
            catch (COMException) { return false; }
        }

        public void Dispose()
        {
            try { watcher.Stop(); }
            catch (ManagementException) { }
            catch (COMException) { }
            watcher.Dispose();
        }
    }
}
