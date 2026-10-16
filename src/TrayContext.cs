using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace BatteryBrightness
{
    /// <summary>
    /// Owns the tray icon and does the actual work: tracks the power source, applies the stored brightness and
    /// refresh rate when it changes, and learns brightness changes made with the keyboard or Windows itself.
    /// </summary>
    sealed class TrayContext : ApplicationContext
    {
        // After a power-source switch or resume Windows often restores its own level a moment later;
        // for this long any such change is overridden instead of being remembered.
        const int EnforceMs = 3000;
        // Brightness events caused by our own slider lag slightly behind it; don't learn those.
        const int OwnChangeMs = 1000;

        readonly NotifyIcon tray;
        readonly ToolStripMenuItem startupItem;
        readonly FlyoutForm flyout;
        readonly BrightnessWatcher watcher;
        readonly Timer pollTimer = new Timer { Interval = 5000 };
        readonly Timer saveTimer = new Timer { Interval = 1000 };
        readonly Timer reapplyTimer = new Timer { Interval = 1500 };
        Theme theme;
        Icon trayIcon;
        bool trayIconDark;
        long enforceUntil, ignoreUntil;

        public AppSettings Settings { get; }
        public PowerSource Source { get; private set; }
        public bool BrightnessSupported { get; }

        public TrayContext(bool showFlyout)
        {
            Settings = AppSettings.Load();
            Source = Power.GetSource();

            int? current = Brightness.Get();
            BrightnessSupported = current.HasValue;
            if (current.HasValue)
            {
                // First run: start both sliders at whatever the screen is set to now.
                if (Settings.AcBrightness < 0) Settings.AcBrightness = current.Value;
                if (Settings.DcBrightness < 0) Settings.DcBrightness = current.Value;
            }
            Settings.Save();
            Startup.Repair();

            theme = Theme.Load();
            flyout = new FlyoutForm(this, theme);
            _ = flyout.Handle; // a window handle to marshal WMI events onto, even before it is first shown

            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("Open", null, (s, e) => flyout.ShowFlyout());
            open.Font = new Font(open.Font, FontStyle.Bold);
            startupItem = new ToolStripMenuItem("Start with Windows", null, (s, e) => SetStartup(!Startup.IsEnabled));
            menu.Items.AddRange(new ToolStripItem[]
            {
                open, new ToolStripSeparator(), startupItem, new ToolStripSeparator(),
                new ToolStripMenuItem("Exit", null, (s, e) => Exit()),
            });
            menu.Opening += (s, e) => startupItem.Checked = Startup.IsEnabled;

            tray = new NotifyIcon { ContextMenuStrip = menu };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) flyout.Toggle(); };
            UpdateTray();
            tray.Visible = true;

            saveTimer.Tick += (s, e) => { saveTimer.Stop(); Settings.Save(); };
            reapplyTimer.Tick += (s, e) =>
            {
                reapplyTimer.Stop();
                if (BrightnessSupported) Brightness.SetAsync(TargetBrightness);
                ApplyRefreshRate();
            };
            pollTimer.Tick += (s, e) => CheckSource(); // backstop in case a power broadcast is missed
            pollTimer.Start();

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            if (BrightnessSupported)
            {
                watcher = new BrightnessWatcher();
                watcher.Changed += level => RunOnUiThread(() => OnBrightnessChanged(level));
                watcher.Start();
            }

            ApplyCurrentSource();
            if (showFlyout) flyout.ShowFlyout();
        }

        public int TargetBrightness => Source == PowerSource.AC ? Settings.AcBrightness : Settings.DcBrightness;

        public string StatusText
        {
            get
            {
                string source = Source == PowerSource.AC ? "Plugged in" : "On battery";
                int? percent = Power.BatteryPercent;
                return percent.HasValue ? $"{source} · {percent}%" : source;
            }
        }

        // ---- called by the flyout ----------------------------------------------------------

        public void SetBrightness(PowerSource source, int level)
        {
            if (source == PowerSource.AC) Settings.AcBrightness = level;
            else Settings.DcBrightness = level;
            SaveSoon();
            if (source == Source && BrightnessSupported)
            {
                ignoreUntil = Clock.Now + OwnChangeMs;
                Brightness.SetAsync(level);
            }
            UpdateTray();
        }

        public void SetLearnChanges(bool enabled)
        {
            Settings.LearnChanges = enabled;
            SaveSoon();
        }

        public void SetRefreshEnabled(bool enabled, IList<int> availableRates)
        {
            Settings.RefreshEnabled = enabled;
            if (enabled && Settings.AcRefresh == 0 && Settings.DcRefresh == 0 && availableRates.Count > 1)
            {
                // Sensible first-time choice: fastest when plugged in, 60 Hz (or the slowest offered) on battery.
                Settings.AcRefresh = availableRates.Max();
                Settings.DcRefresh = availableRates.Contains(60) ? 60 : availableRates.Min();
            }
            SaveSoon();
            if (enabled) ApplyRefreshRate();
        }

        public void SetRefreshDisplay(string devicePath)
        {
            Settings.RefreshDisplay = devicePath ?? "";
            SaveSoon();
            ApplyRefreshRate();
        }

        public void SetRefreshRate(PowerSource source, int hz)
        {
            if (source == PowerSource.AC) Settings.AcRefresh = hz;
            else Settings.DcRefresh = hz;
            SaveSoon();
            if (source == Source) ApplyRefreshRate();
        }

        public void SetStartup(bool enabled)
        {
            Startup.Set(enabled);
            startupItem.Checked = enabled;
            if (flyout.Visible) flyout.RefreshState();
        }

        public void ShowFlyoutFromAnotherThread() => RunOnUiThread(flyout.ShowFlyout);

        public void Exit() => ExitThread();

        // ---- power source ------------------------------------------------------------------

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                Source = Power.GetSource();
                ApplyCurrentSource();
                RefreshUi();
            }
            else if (e.Mode == PowerModes.StatusChange)
            {
                CheckSource();
            }
        }

        void CheckSource()
        {
            var source = Power.GetSource();
            if (source != Source)
            {
                Source = source;
                ApplyCurrentSource();
                RefreshUi();
            }
            else if (flyout.Visible)
            {
                flyout.RefreshState(); // battery percentage
            }
        }

        void ApplyCurrentSource()
        {
            if (BrightnessSupported)
            {
                enforceUntil = Clock.Now + EnforceMs;
                Brightness.SetAsync(TargetBrightness);
            }
            ApplyRefreshRate();
            reapplyTimer.Stop();
            reapplyTimer.Start();
        }

        void ApplyRefreshRate()
        {
            if (!Settings.RefreshEnabled) return;
            int hz = Source == PowerSource.AC ? Settings.AcRefresh : Settings.DcRefresh;
            if (hz <= 0) return;
            var display = Displays.Find(Displays.GetActive(), Settings.RefreshDisplay);
            if (display != null) Displays.SetRefreshRate(display.GdiName, hz);
        }

        // ---- brightness changes from outside -----------------------------------------------

        void OnBrightnessChanged(int level)
        {
            // The brightness event can beat the power broadcast; treat it as part of the switch.
            var source = Power.GetSource();
            if (source != Source)
            {
                Source = source;
                ApplyCurrentSource();
                RefreshUi();
                return;
            }

            long now = Clock.Now;
            int target = TargetBrightness;
            if (now < enforceUntil)
            {
                if (level != target) Brightness.SetAsync(target);
                return;
            }
            if (now < ignoreUntil || level == target) return;
            // Battery saver dims the screen on its own; that isn't the user's preference.
            if (!Settings.LearnChanges || Power.BatterySaverOn) return;

            if (Source == PowerSource.AC) Settings.AcBrightness = level;
            else Settings.DcBrightness = level;
            SaveSoon();
            RefreshUi();
        }

        // ---- tray and housekeeping ---------------------------------------------------------

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            var latest = Theme.Load();
            if (!latest.SameAs(theme))
            {
                theme = latest;
                flyout.ApplyTheme(theme);
            }
            UpdateTray();
        }

        void RefreshUi()
        {
            flyout.RefreshState();
            UpdateTray();
        }

        void UpdateTray()
        {
            bool dark = Theme.SystemIsDark();
            if (trayIcon == null || dark != trayIconDark)
            {
                var old = trayIcon;
                trayIcon = Icons.CreateTrayIcon(dark);
                trayIconDark = dark;
                tray.Icon = trayIcon;
                old?.Dispose();
            }

            string source = Source == PowerSource.AC ? "Plugged in" : "On battery";
            tray.Text = BrightnessSupported
                ? $"Battery Brightness\n{source} · {TargetBrightness}%"
                : $"Battery Brightness\n{source}";
        }

        void SaveSoon()
        {
            saveTimer.Stop();
            saveTimer.Start();
        }

        void RunOnUiThread(Action action)
        {
            try
            {
                if (!flyout.IsDisposed && flyout.IsHandleCreated) flyout.BeginInvoke(action);
            }
            catch (InvalidOperationException) { } // shutting down (includes ObjectDisposedException)
        }

        protected override void ExitThreadCore()
        {
            pollTimer.Stop();
            reapplyTimer.Stop();
            saveTimer.Stop();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            watcher?.Dispose();
            Settings.Save();
            tray.Visible = false;
            tray.Dispose();
            trayIcon?.Dispose();
            flyout.Dispose();
            base.ExitThreadCore();
        }
    }
}
