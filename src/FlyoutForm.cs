using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatteryBrightness
{
    /// <summary>The tray flyout: a brightness slider per power source, refresh-rate options and the startup switch.</summary>
    sealed class FlyoutForm : Form
    {
        static readonly bool IsWindows11 = Environment.OSVersion.Version.Build >= 22000;

        readonly TrayContext app;
        readonly float scale;
        readonly Font regular = new Font("Segoe UI", 9f);
        readonly Font semibold = new Font("Segoe UI Semibold", 9f);
        readonly Font heading = new Font("Segoe UI Semibold", 11f);

        readonly Label title, status, noBrightness, refreshNote;
        readonly Label acLabel, acValue, dcLabel, dcValue;
        readonly FlatSlider acSlider, dcSlider;
        readonly Label learnLabel, refreshLabel, startupLabel;
        readonly ToggleSwitch learnToggle, refreshToggle, startupToggle;
        readonly Label displayLabel, acRateLabel, dcRateLabel;
        readonly ComboBox displayCombo, acRateCombo, dcRateCombo;
        readonly Panel divider1, divider2;
        readonly LinkLabel quitLink;

        Theme theme;
        List<DisplayInfo> displays = new List<DisplayInfo>();
        List<int> rates = new List<int>();
        bool updating;
        long hiddenAt;
        int contentRight;

        public FlyoutForm(TrayContext app, Theme theme)
        {
            this.app = app;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;

            Text = "Battery Brightness";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            Font = regular;

            title = AddLabel("Battery Brightness", heading);
            status = AddLabel("");

            acLabel = AddLabel("Plugged in");
            acValue = AddValueLabel();
            acSlider = AddSlider("Brightness when plugged in");
            dcLabel = AddLabel("On battery");
            dcValue = AddValueLabel();
            dcSlider = AddSlider("Brightness on battery");
            learnLabel = AddLabel("Remember changes made outside this app");
            learnToggle = AddToggle(learnLabel);
            noBrightness = AddLabel("This display's brightness can't be controlled. Refresh rate switching still works.");

            divider1 = AddDivider();
            refreshLabel = AddLabel("Switch refresh rate with power source");
            refreshToggle = AddToggle(refreshLabel);
            displayLabel = AddLabel("Display");
            displayCombo = AddCombo("Display");
            displayCombo.DropDownWidth = S(260);
            acRateLabel = AddLabel("Plugged in");
            acRateCombo = AddCombo("Refresh rate when plugged in");
            dcRateLabel = AddLabel("On battery");
            dcRateCombo = AddCombo("Refresh rate on battery");
            refreshNote = AddLabel("This display only offers one refresh rate at its current resolution.");

            divider2 = AddDivider();
            startupLabel = AddLabel("Start with Windows");
            startupToggle = AddToggle(startupLabel);
            quitLink = new LinkLabel { Text = "Quit", AutoSize = true, Font = regular, LinkBehavior = LinkBehavior.HoverUnderline };
            Controls.Add(quitLink);

            acSlider.ValueChanged += (s, e) => OnSliderChanged(PowerSource.AC, acSlider, acValue);
            dcSlider.ValueChanged += (s, e) => OnSliderChanged(PowerSource.Battery, dcSlider, dcValue);
            learnToggle.CheckedChanged += (s, e) => { if (!updating) app.SetLearnChanges(learnToggle.Checked); };
            refreshToggle.CheckedChanged += (s, e) =>
            {
                if (updating) return;
                app.SetRefreshEnabled(refreshToggle.Checked, rates);
                RefreshState();
                LayoutContent();
            };
            displayCombo.SelectionChangeCommitted += (s, e) =>
            {
                if (!(displayCombo.SelectedItem is DisplayInfo display)) return;
                app.SetRefreshDisplay(display.DevicePath);
                FillRates(display);
                LayoutContent();
            };
            acRateCombo.SelectionChangeCommitted += (s, e) => OnRateChanged(PowerSource.AC, acRateCombo);
            dcRateCombo.SelectionChangeCommitted += (s, e) => OnRateChanged(PowerSource.Battery, dcRateCombo);
            startupToggle.CheckedChanged += (s, e) => { if (!updating) app.SetStartup(startupToggle.Checked); };
            quitLink.LinkClicked += (s, e) => app.Exit();

            ApplyTheme(theme);
            LayoutContent();
        }

        int S(int px) => (int)Math.Round(px * scale);

        // ---- showing and hiding --------------------------------------------------------------

        /// <summary>Opens above the notification area of the screen the mouse is on, or hides if already open.</summary>
        public void Toggle()
        {
            if (Visible) Hide();
            // A click on the tray icon first deactivates (and hides) the open flyout; don't reopen it.
            else if (Clock.Now - hiddenAt > 300) ShowFlyout();
        }

        public void ShowFlyout()
        {
            RebuildDisplayLists();
            RefreshState();
            LayoutContent();
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Right - Width - S(12), area.Bottom - Height - S(12));
            Show();
            Activate();
            SetForegroundWindow(Handle);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Hide();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) hiddenAt = Clock.Now;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape && !(ActiveControl is ComboBox combo && combo.DroppedDown)) Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x80, CS_DROPSHADOW = 0x20000;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW; // keeps it out of Alt+Tab
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyWindowFrame();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!IsWindows11) // Windows 11 draws the border (and rounds the corners) itself
                using (var pen = new Pen(theme.Border))
                    e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        // ---- state -------------------------------------------------------------------------

        /// <summary>Pulls values from the app into the controls without echoing changes back.</summary>
        public void RefreshState()
        {
            var settings = app.Settings;
            bool onAc = app.Source == PowerSource.AC;
            updating = true;
            try
            {
                status.Text = app.StatusText;
                acSlider.Value = settings.AcBrightness;
                dcSlider.Value = settings.DcBrightness;
                acValue.Text = acSlider.Value + "%";
                dcValue.Text = dcSlider.Value + "%";
                MarkActive(onAc, acLabel, acValue, acRateLabel);
                MarkActive(!onAc, dcLabel, dcValue, dcRateLabel);
                learnToggle.Checked = settings.LearnChanges;
                refreshToggle.Checked = settings.RefreshEnabled;
                startupToggle.Checked = Startup.IsEnabled;
                SelectRate(acRateCombo, settings.AcRefresh);
                SelectRate(dcRateCombo, settings.DcRefresh);
            }
            finally { updating = false; }
            PositionStatus();
        }

        void MarkActive(bool active, params Label[] labels)
        {
            foreach (var label in labels)
            {
                label.Font = active ? semibold : regular;
                label.ForeColor = active ? theme.Accent : theme.Fore;
            }
        }

        void OnSliderChanged(PowerSource source, FlatSlider slider, Label value)
        {
            value.Text = slider.Value + "%";
            if (!updating) app.SetBrightness(source, slider.Value);
        }

        void OnRateChanged(PowerSource source, ComboBox combo)
        {
            if (!updating && combo.SelectedItem is RateItem item) app.SetRefreshRate(source, item.Hz);
        }

        void RebuildDisplayLists()
        {
            displays = Displays.GetActive();
            var selected = Displays.Find(displays, app.Settings.RefreshDisplay);
            updating = true;
            try
            {
                displayCombo.Items.Clear();
                foreach (var display in displays) displayCombo.Items.Add(display);
                displayCombo.SelectedItem = selected;
            }
            finally { updating = false; }
            FillRates(selected);
        }

        void FillRates(DisplayInfo display)
        {
            rates = display == null ? new List<int>() : Displays.GetRefreshRates(display.GdiName);
            updating = true;
            try
            {
                FillRateCombo(acRateCombo, app.Settings.AcRefresh);
                FillRateCombo(dcRateCombo, app.Settings.DcRefresh);
            }
            finally { updating = false; }
        }

        void FillRateCombo(ComboBox combo, int selectedHz)
        {
            combo.BeginUpdate();
            combo.Items.Clear();
            combo.Items.Add(new RateItem(0));
            foreach (int hz in rates) combo.Items.Add(new RateItem(hz));
            if (selectedHz > 0 && !rates.Contains(selectedHz)) combo.Items.Add(new RateItem(selectedHz));
            combo.EndUpdate();
            SelectRate(combo, selectedHz);
        }

        static void SelectRate(ComboBox combo, int hz)
        {
            var item = combo.Items.OfType<RateItem>().FirstOrDefault(r => r.Hz == hz);
            if (item != null) combo.SelectedItem = item;
            else if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        sealed class RateItem
        {
            public readonly int Hz;
            public RateItem(int hz) { Hz = hz; }
            public override string ToString() => Hz == 0 ? "Don't change" : Hz + " Hz";
        }

        // ---- layout ------------------------------------------------------------------------

        void LayoutContent()
        {
            int pad = S(16), width = S(340), inner = width - 2 * pad;
            contentRight = width - pad;
            int y = pad;

            title.Location = new Point(pad, y);
            y += title.Height + S(12);

            bool brightness = app.BrightnessSupported;
            foreach (var c in new Control[] { acLabel, acValue, acSlider, dcLabel, dcValue, dcSlider, learnLabel, learnToggle })
                c.Visible = brightness;
            noBrightness.Visible = !brightness;
            if (brightness)
            {
                y = SliderRow(acLabel, acValue, acSlider, pad, inner, y) + S(4);
                y = SliderRow(dcLabel, dcValue, dcSlider, pad, inner, y) + S(10);
                y = ToggleRow(learnLabel, learnToggle, pad, inner, y);
            }
            else
            {
                y = TextBlock(noBrightness, pad, inner, y);
            }

            y = Divider(divider1, pad, inner, y);
            y = ToggleRow(refreshLabel, refreshToggle, pad, inner, y);

            bool refresh = app.Settings.RefreshEnabled;
            bool pickDisplay = refresh && displays.Count > 1;
            bool singleRate = refresh && rates.Count < 2;
            displayLabel.Visible = displayCombo.Visible = pickDisplay;
            acRateLabel.Visible = acRateCombo.Visible = dcRateLabel.Visible = dcRateCombo.Visible = refresh;
            refreshNote.Visible = singleRate;
            if (refresh)
            {
                y += S(12);
                if (pickDisplay) y = ComboRow(displayLabel, displayCombo, pad, inner, y) + S(8);
                y = ComboRow(acRateLabel, acRateCombo, pad, inner, y) + S(8);
                y = ComboRow(dcRateLabel, dcRateCombo, pad, inner, y);
                if (singleRate) y = TextBlock(refreshNote, pad, inner, y + S(8));
            }

            y = Divider(divider2, pad, inner, y);
            y = ToggleRow(startupLabel, startupToggle, pad, inner, y);
            y += S(10);
            quitLink.Location = new Point(contentRight - quitLink.Width, y);
            y += quitLink.Height + pad - S(4);

            int bottom = Bottom;
            ClientSize = new Size(width, y);
            if (Visible) Top = bottom - Height; // grow upwards, staying above the taskbar
            PositionStatus();
            Invalidate();
        }

        void PositionStatus() =>
            status.Location = new Point(contentRight - status.Width, title.Top + (title.Height - status.Height) / 2 + S(1));

        int SliderRow(Label name, Label value, FlatSlider slider, int x, int width, int y)
        {
            name.Location = new Point(x, y);
            value.Size = new Size(S(48), name.Height);
            value.Location = new Point(x + width - value.Width, y);
            y += name.Height;

            // Let the thumb overhang the text column so the rail itself lines up with the labels.
            slider.Height = S(28);
            int inset = slider.RailInset;
            slider.Bounds = new Rectangle(x - inset, y, width + 2 * inset, slider.Height);
            return y + slider.Height;
        }

        int ToggleRow(Label label, ToggleSwitch toggle, int x, int width, int y)
        {
            toggle.Size = new Size(S(40), S(20));
            label.MaximumSize = new Size(width - toggle.Width - S(12), 0);
            int height = Math.Max(label.Height, toggle.Height);
            label.Location = new Point(x, y + (height - label.Height) / 2);
            toggle.Location = new Point(x + width - toggle.Width, y + (height - toggle.Height) / 2);
            return y + height;
        }

        int ComboRow(Label label, ComboBox combo, int x, int width, int y)
        {
            combo.Width = S(170);
            combo.Location = new Point(x + width - combo.Width, y);
            label.Location = new Point(x, y + (combo.Height - label.Height) / 2);
            return y + combo.Height;
        }

        static int TextBlock(Label label, int x, int width, int y)
        {
            label.MaximumSize = new Size(width, 0);
            label.Location = new Point(x, y);
            return y + label.Height;
        }

        int Divider(Panel divider, int x, int width, int y)
        {
            y += S(14);
            divider.Bounds = new Rectangle(x, y, width, 1);
            return y + 1 + S(14);
        }

        // ---- theme -------------------------------------------------------------------------

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = t.Back;
            ForeColor = t.Fore;
            foreach (Control c in Controls)
            {
                c.BackColor = c is ComboBox ? t.ControlBack : c is Panel ? t.Divider : t.Back;
                c.ForeColor = t.Fore;
                if (c is FlatSlider slider) slider.Theme = t;
                if (c is ToggleSwitch toggle) toggle.Theme = t;
                if (c is ComboBox combo && combo.IsHandleCreated) ThemeCombo(combo);
            }
            status.ForeColor = noBrightness.ForeColor = refreshNote.ForeColor = t.Dim;
            quitLink.LinkColor = quitLink.VisitedLinkColor = t.Dim;
            quitLink.ActiveLinkColor = t.Fore;
            if (IsHandleCreated) ApplyWindowFrame();
            RefreshState();
            Invalidate(true);
        }

        void ThemeCombo(ComboBox combo) => SetWindowTheme(combo.Handle, theme.Dark ? "DarkMode_CFD" : null, null);

        void ApplyWindowFrame()
        {
            if (!IsWindows11) return;
            int corners = DWMWCP_ROUND;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
            int border = ColorTranslator.ToWin32(theme.Border);
            DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }

        // ---- control factories -------------------------------------------------------------

        Label AddLabel(string text, Font font = null)
        {
            var label = new Label { Text = text, AutoSize = true, Font = font ?? regular, UseMnemonic = false };
            Controls.Add(label);
            return label;
        }

        Label AddValueLabel()
        {
            var label = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Font = regular, UseMnemonic = false };
            Controls.Add(label);
            return label;
        }

        FlatSlider AddSlider(string name)
        {
            var slider = new FlatSlider { AccessibleName = name };
            Controls.Add(slider);
            return slider;
        }

        ToggleSwitch AddToggle(Label label)
        {
            var toggle = new ToggleSwitch { AccessibleName = label.Text };
            label.Click += (s, e) => toggle.Checked = !toggle.Checked;
            Controls.Add(toggle);
            return toggle;
        }

        ComboBox AddCombo(string name)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = regular, AccessibleName = name };
            combo.HandleCreated += (s, e) => ThemeCombo(combo);
            Controls.Add(combo);
            return combo;
        }

        Panel AddDivider()
        {
            var divider = new Panel();
            Controls.Add(divider);
            return divider;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            regular.Dispose();
            semibold.Dispose();
            heading.Dispose();
        }

        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);
    }
}
