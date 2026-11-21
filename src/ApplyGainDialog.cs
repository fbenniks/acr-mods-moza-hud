using System;
using System.Drawing;
using System.Windows.Forms;

namespace MozaHud
{
    /// <summary>
    /// "Where should StageSwitch use this gain?" — this car on this stage, this car on all the rally's stages, this
    /// stage for all cars, or all the rally's stages for all cars. Shows what StageSwitch has now; the gain can still be
    /// adjusted. Result: Scope ("car"/"rallycar"/"stage"/"rally") and Gain.
    /// </summary>
    public sealed class ApplyGainDialog : Form
    {
        public string Scope { get; private set; }
        public int Gain { get { return (int)_gain.Value; } }

        readonly NumericUpDown _gain;
        static readonly Font Head = new Font("Segoe UI Semibold", 12.5f);
        static readonly Font Body = new Font("Segoe UI", 9.5f);
        static readonly Font Small = new Font("Segoe UI", 8.5f);

        public ApplyGainDialog(StageSwitchLink.Current now, string carName, int suggested, string why, Icon icon)
        {
            Text = "Apply in StageSwitch";
            Icon = icon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Body;
            ClientSize = new Size(540, 452);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };

            var title = new Label { Text = "Where should StageSwitch use this FFB gain?", Font = Head, AutoSize = true, Location = new Point(20, 16) };
            var sub = new Label { Text = why, ForeColor = Theme.Muted, Font = Small, Location = new Point(22, 46), Size = new Size(460, 18) };
            var gl = new Label { Text = "FFB gain", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(22, 76) };
            _gain = new NumericUpDown
            {
                Minimum = 0, Maximum = 200, Increment = 5, Value = Math.Max(0, Math.Min(200, suggested)), Width = 70,
                Location = new Point(90, 73), BackColor = Theme.PanelAlt, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Right, Font = new Font("Segoe UI Semibold", 10f),
            };
            Controls.AddRange(new Control[] { title, sub, gl, _gain });

            string car = string.IsNullOrEmpty(now.CarName) ? carName : now.CarName;
            int y = 112;
            AddChoice("car", "Only " + car + " on " + now.Stage,
                now.CarGain.HasValue ? "Now " + now.CarGain + " (its own value on this stage)" : "Now uses the stage value " + now.StageGain +
                (now.PerCar ? "" : "  ·  turns on \"Different gain per car\""), ref y);
            AddChoice("rallycar", car + " on all " + now.Rally + " stages",
                "Its own value on every " + now.Rally + " stage" + (now.PerCar ? "" : "  ·  turns on \"Different gain per car\""), ref y);
            AddChoice("stage", now.Stage + ", all cars",
                "Now " + now.StageGain + (now.CarGain.HasValue ? "  ·  " + car + "'s own value " + now.CarGain + " is removed" : ""), ref y);
            AddChoice("rally", "All " + now.Rally + " stages, all cars",
                "Rally now " + now.RallyGain + "  ·  every stage gets this value" +
                (now.CarGain.HasValue ? "  ·  " + car + "'s own values are removed" : ""), ref y);

            var cancel = new Button
            {
                Text = "Cancel", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Muted,
                Size = new Size(90, 30), Location = new Point(ClientSize.Width - 110, ClientSize.Height - 44),
            };
            cancel.FlatAppearance.BorderColor = Theme.Border;
            Controls.Add(cancel);
            CancelButton = cancel;
        }

        void AddChoice(string scope, string main, string detail, ref int y)
        {
            var b = new ChoiceButton(main, detail) { Location = new Point(20, y), Size = new Size(ClientSize.Width - 40, 62) };
            b.Click += (s, e) => { Scope = scope; DialogResult = DialogResult.OK; Close(); };
            Controls.Add(b);
            y += 70;
        }

        /// <summary>Two-line button: choice in bold, what it changes below.</summary>
        sealed class ChoiceButton : Control
        {
            readonly string _main, _detail;
            bool _hover;

            public ChoiceButton(string main, string detail)
            {
                _main = main; _detail = detail;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
                Cursor = Cursors.Hand;
                TabStop = true;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
            protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) OnClick(EventArgs.Empty); base.OnKeyDown(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Theme.Bg);
                var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                using (var path = Theme.Round(r, 7))
                using (var bg = new SolidBrush(_hover ? Theme.Hover : Theme.Panel))
                using (var border = new Pen(_hover || Focused ? Theme.Accent : Theme.Border))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(border, path);
                }
                using (var tb = new SolidBrush(Theme.Text)) g.DrawString(_main, Head, tb, 14, 9);
                using (var mb = new SolidBrush(Theme.Muted)) g.DrawString(_detail, Small, mb, 15, 37);
            }
        }
    }
}
