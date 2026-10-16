using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BatteryBrightness
{
    /// <summary>A Windows 11-style 0–100 slider that follows the flyout theme (the stock TrackBar can't go dark).</summary>
    sealed class FlatSlider : Control
    {
        public const int Maximum = 100;

        int value, wheelDelta;
        bool dragging, hover;
        Theme theme;

        public event EventHandler ValueChanged;

        public FlatSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
        }

        public Theme Theme { get => theme; set { theme = value; Invalidate(); } }

        public int Value
        {
            get => value;
            set
            {
                int v = Math.Max(0, Math.Min(Maximum, value));
                if (v == this.value) return;
                this.value = v;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        float ThumbRadius => Height * 0.34f;

        /// <summary>Gap between the control edge and the ends of the rail, so callers can line the rail up with text.</summary>
        public int RailInset => (int)Math.Ceiling(ThumbRadius + 3);

        float RailWidth => Math.Max(1, Width - 2 * RailInset);
        float ThumbX => RailInset + RailWidth * value / Maximum;

        void SetFromMouse(int x) => Value = (int)Math.Round((x - RailInset) / RailWidth * Maximum);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (theme == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float cy = Height / 2f, x = ThumbX, r = ThumbRadius, rail = Math.Max(3f, Height * 0.14f);
            Color fill = Enabled ? theme.Accent : theme.Dim;

            using (var pen = new Pen(theme.Track, rail) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, RailInset, cy, RailInset + RailWidth, cy);
            if (x > RailInset)
                using (var pen = new Pen(fill, rail) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, RailInset, cy, x, cy);

            using (var brush = new SolidBrush(theme.ThumbFill))
                g.FillEllipse(brush, x - r, cy - r, 2 * r, 2 * r);
            using (var pen = new Pen(theme.ThumbBorder))
                g.DrawEllipse(pen, x - r, cy - r, 2 * r, 2 * r);
            float inner = r * (dragging ? 0.45f : hover ? 0.7f : 0.55f);
            using (var brush = new SolidBrush(fill))
                g.FillEllipse(brush, x - inner, cy - inner, 2 * inner, 2 * inner);

            if (Focused && ShowFocusCues)
                using (var pen = new Pen(theme.Fore) { DashStyle = DashStyle.Dot })
                    g.DrawEllipse(pen, x - r - 2, cy - r - 2, 2 * r + 4, 2 * r + 4);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            Capture = true;
            dragging = true;
            SetFromMouse(e.X);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) { SetFromMouse(e.X); return; }
            bool over = Math.Abs(e.X - ThumbX) <= ThumbRadius + 2;
            if (over != hover) { hover = over; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging) return;
            dragging = false;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover) { hover = false; Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            wheelDelta += e.Delta;
            int notches = wheelDelta / 120;
            if (notches == 0) return;
            wheelDelta -= notches * 120;
            Value += notches * 5;
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: Value -= 1; break;
                case Keys.Right: case Keys.Up: Value += 1; break;
                case Keys.PageDown: Value -= 10; break;
                case Keys.PageUp: Value += 10; break;
                case Keys.Home: Value = 0; break;
                case Keys.End: Value = Maximum; break;
                default: return;
            }
            e.Handled = true;
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override AccessibleObject CreateAccessibilityInstance() => new SliderAccessible(this);

        sealed class SliderAccessible : ControlAccessibleObject
        {
            readonly FlatSlider owner;
            public SliderAccessible(FlatSlider owner) : base(owner) { this.owner = owner; }
            public override AccessibleRole Role => AccessibleRole.Slider;
            public override string Value
            {
                get => owner.Value + "%";
                set { if (int.TryParse(value?.TrimEnd('%', ' '), out int v)) owner.Value = v; }
            }
        }
    }

    /// <summary>A Windows 11-style on/off switch.</summary>
    sealed class ToggleSwitch : Control
    {
        bool isOn, hover;
        Theme theme;

        public event EventHandler CheckedChanged;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.StandardDoubleClick, false); // every click toggles
            TabStop = true;
        }

        public Theme Theme { get => theme; set { theme = value; Invalidate(); } }

        public bool Checked
        {
            get => isOn;
            set
            {
                if (isOn == value) return;
                isOn = value;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (theme == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var r = new RectangleF(0.5f, 0.5f, Width - 2f, Height - 2f);
            float cy = r.Y + r.Height / 2, knob = r.Height * (hover ? 0.35f : 0.3f);
            using var track = Icons.RoundedRect(r, r.Height / 2);
            if (isOn)
            {
                float cx = r.Right - r.Height / 2;
                using (var brush = new SolidBrush(theme.Accent)) g.FillPath(brush, track);
                using (var brush = new SolidBrush(theme.OnAccent)) g.FillEllipse(brush, cx - knob, cy - knob, 2 * knob, 2 * knob);
            }
            else
            {
                float cx = r.X + r.Height / 2;
                knob *= 0.85f;
                using (var pen = new Pen(theme.Off)) g.DrawPath(pen, track);
                using (var brush = new SolidBrush(theme.Off)) g.FillEllipse(brush, cx - knob, cy - knob, 2 * knob, 2 * knob);
            }

            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, ClientRectangle);
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); }
        protected override void OnClick(EventArgs e) { base.OnClick(e); Checked = !Checked; }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space) Checked = !Checked;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override AccessibleObject CreateAccessibilityInstance() => new ToggleAccessible(this);

        sealed class ToggleAccessible : ControlAccessibleObject
        {
            readonly ToggleSwitch owner;
            public ToggleAccessible(ToggleSwitch owner) : base(owner) { this.owner = owner; }
            public override AccessibleRole Role => AccessibleRole.CheckButton;
            public override AccessibleStates State => base.State | (owner.Checked ? AccessibleStates.Checked : 0);
            public override string DefaultAction => owner.Checked ? "Turn off" : "Turn on";
            public override void DoDefaultAction() => owner.Checked = !owner.Checked;
        }
    }
}
