using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BatteryBrightness
{
    static class Program
    {
        const string MutexName = @"Local\BatteryBrightness.SingleInstance";
        const string ShowEventName = @"Local\BatteryBrightness.Show";

        /// <summary>
        /// Usage: BatteryBrightness.exe [--background]
        ///   --background   start in the tray without opening the flyout (used by "Start with Windows")
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--write-icon")
            {
                Icons.WriteAppIcon(args[1]);
                return;
            }
            bool background = args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));

            using var mutex = new Mutex(true, MutexName, out bool firstInstance);
            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            if (!firstInstance)
            {
                // Already running: launching the exe again just opens the flyout of the running copy.
                if (!background)
                {
                    AllowSetForegroundWindow(ASFW_ANY);
                    showEvent.Set();
                }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var context = new TrayContext(showFlyout: !background);
            var registration = ThreadPool.RegisterWaitForSingleObject(
                showEvent, (state, timedOut) => context.ShowFlyoutFromAnotherThread(), null, Timeout.Infinite, false);
            Application.Run(context);
            registration.Unregister(null);
        }

        const int ASFW_ANY = -1;

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);
    }

    /// <summary>Milliseconds on a monotonic clock that never wraps (Environment.TickCount goes negative after 24.9 days of uptime).</summary>
    static class Clock
    {
        public static long Now => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
    }
}
