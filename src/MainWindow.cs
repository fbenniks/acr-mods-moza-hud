using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace MozaHud
{
    /// <summary>
    /// Stage list on the left (live run on top while recording), the selected run on the right:
    /// FFB gain advice, key numbers and the torque/temperature graph.
    /// </summary>
    public sealed class MainWindow : Form
    {
        readonly StageRecorder _recorder;
        readonly Func<string[]> _baseStatus;
        readonly Label _status = new Label(), _base = new Label(), _title = new Label(), _subtitle = new Label();
        readonly StatTiles _stats = new StatTiles();
        readonly ListBox _list = new ListBox();
        readonly AdviceBox _advice = new AdviceBox();
        readonly StageChart _chart = new StageChart();
        readonly Timer _timer = new Timer { Interval = 1000 };
        readonly object _liveItem = new object();
        StageRun _shown;
        DateTime _nextLiveAnalysis;

        static readonly Font TitleFont = new Font("Segoe UI Semibold", 15f);
        static readonly Font BodyFont = new Font("Segoe UI", 9.5f);
        static readonly Font BoldFont = new Font("Segoe UI Semibold", 10f);
        static readonly Font SmallFont = new Font("Segoe UI", 8.5f);

        public MainWindow(StageRecorder recorder, Func<string[]> baseStatus, Icon icon)
        {
            _recorder = recorder;
            _baseStatus = baseStatus;
            Text = "MozaHud";
            Icon = icon;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = BodyFont;
            Size = new Size(1180, 760);
            MinimumSize = new Size(820, 520);
            StartPosition = FormStartPosition.CenterScreen;

            // ---- header ----
            var header = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Theme.Panel, Padding = new Padding(16, 8, 16, 8) };
            var logo = new Label { Text = "MozaHud", Font = new Font("Segoe UI Semibold", 14f), ForeColor = Theme.Accent, AutoSize = true, Location = new Point(16, 14) };
            _status.SetBounds(140, 10, 600, 20); _status.ForeColor = Theme.Text; _status.Font = BoldFont;
            _base.SetBounds(140, 31, 900, 18); _base.ForeColor = Theme.Muted; _base.Font = SmallFont;
            _status.Anchor = _base.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            // allowed clipping setting, top right
            var allowLabel = new Label { Text = "Allowed clipping", AutoSize = true, ForeColor = Theme.Muted, Font = SmallFont };
            var allow = new NumericUpDown
            {
                DecimalPlaces = 1, Increment = 0.1m, Minimum = 0, Maximum = 50, Width = 58,
                Value = (decimal)Settings.AllowedClipPct, BackColor = Theme.PanelAlt, ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Right, Font = BoldFont,
            };
            var pct = new Label { Text = "%", AutoSize = true, ForeColor = Theme.Muted, Font = BoldFont };
            allow.ValueChanged += (s, e) => Settings.SetAllowedClipPct((double)allow.Value);
            var tip = new ToolTip();
            const string tipText = "Share of the driving time the motor may be at its limit (clipping) before MozaHud recommends a lower FFB gain.";
            tip.SetToolTip(allow, tipText); tip.SetToolTip(allowLabel, tipText);
            Action placeAllow = () =>
            {
                pct.Location = new Point(header.Width - 16 - pct.Width, 20);
                allow.Location = new Point(pct.Left - 2 - allow.Width, 16);
                allowLabel.Location = new Point(allow.Left - 6 - allowLabel.Width, 21);
                _status.Width = _base.Width = Math.Max(100, allowLabel.Left - 150);
            };
            header.Controls.AddRange(new Control[] { logo, _status, _base, allowLabel, allow, pct });
            header.Resize += (s, e) => placeAllow();
            Load += (s, e) => placeAllow();
            _status.Anchor = _base.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            Settings.Changed += OnSettingsChanged;

            // ---- stage list ----
            var left = new Panel { Dock = DockStyle.Left, Width = 300, BackColor = Theme.Panel, Padding = new Padding(0, 8, 0, 0) };
            var listTitle = new Label { Text = "STAGES", Dock = DockStyle.Top, Height = 24, ForeColor = Theme.Faint, Font = SmallFont, Padding = new Padding(14, 4, 0, 0) };
            _list.Dock = DockStyle.Fill;
            _list.BorderStyle = BorderStyle.None;
            _list.BackColor = Theme.Panel;
            _list.ForeColor = Theme.Text;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.ItemHeight = 54;
            _list.IntegralHeight = false;
            _list.DrawItem += DrawStageItem;
            _list.SelectedIndexChanged += (s, e) => ShowSelected(true);
            var menu = new ContextMenuStrip();
            var del = new ToolStripMenuItem("Delete this run");
            del.Click += (s, e) => DeleteSelected();
            menu.Items.Add(del);
            menu.Opening += (s, e) => { e.Cancel = !(_list.SelectedItem is StageRun); };
            _list.ContextMenuStrip = menu;
            _list.MouseDown += (s, e) => { if (e.Button == MouseButtons.Right) { int i = _list.IndexFromPoint(e.Location); if (i >= 0) _list.SelectedIndex = i; } };
            var hint = new Label
            {
                Dock = DockStyle.Bottom, Height = 34, ForeColor = Theme.Faint, Font = SmallFont, Padding = new Padding(14, 2, 8, 0),
                Text = "Keeps the last " + StageStore.MaxRuns + " runs, up to " + StageStore.MaxAgeDays + " days.\nRight-click a run to delete it.",
            };
            left.Controls.Add(_list); left.Controls.Add(hint); left.Controls.Add(listTitle);

            // ---- details ----
            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 14, 20, 16), BackColor = Theme.Bg };
            _title.Dock = DockStyle.Top; _title.Height = 32; _title.Font = TitleFont; _title.ForeColor = Theme.Text;
            _subtitle.Dock = DockStyle.Top; _subtitle.Height = 24; _subtitle.ForeColor = Theme.Muted;
            _advice.Dock = DockStyle.Top; _advice.Height = 104;
            var gap1 = new Panel { Dock = DockStyle.Top, Height = 10 };
            _stats.Dock = DockStyle.Top; _stats.Height = 52;
            var help = new Label
            {
                Dock = DockStyle.Bottom, Height = 22, ForeColor = Theme.Faint, Font = SmallFont, TextAlign = ContentAlignment.BottomLeft,
                Text = "Scroll to zoom, drag to move, double-click for the whole stage. Clipping at the rotation limit (soft lock) is shown grey and not counted.",
            };
            _chart.Dock = DockStyle.Fill;
            right.Controls.Add(_chart);
            right.Controls.Add(help);
            right.Controls.Add(_stats);
            right.Controls.Add(gap1);
            right.Controls.Add(_advice);
            right.Controls.Add(_subtitle);
            right.Controls.Add(_title);

            Controls.Add(right);
            Controls.Add(left);
            Controls.Add(header);

            _timer.Tick += (s, e) => RefreshLive();
            _timer.Start();
            ReloadList(null);
            RefreshLive();
        }

        // ---------------- list ----------------

        public void ReloadList(string selectFile)
        {
            var runs = StageStore.List();
            _list.BeginUpdate();
            _list.Items.Clear();
            if (_recorder.Live != null) _list.Items.Add(_liveItem);
            foreach (var r in runs) _list.Items.Add(r);
            _list.EndUpdate();
            int sel = 0;
            if (selectFile != null)
                for (int i = 0; i < _list.Items.Count; i++)
                {
                    var r = _list.Items[i] as StageRun;
                    if (r != null && string.Equals(r.FilePath, selectFile, StringComparison.OrdinalIgnoreCase)) { sel = i; break; }
                }
            if (_list.Items.Count > 0) _list.SelectedIndex = sel;
            else ShowSelected(true);
        }

        /// <summary>From the polling thread when a run was saved (null: discarded).</summary>
        public void OnRunSaved(StageRun run)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)(() => ReloadList(run == null ? null : run.FilePath)));
        }

        void DrawStageItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.Hover : Theme.Panel)) g.FillRectangle(b, e.Bounds);
            if (sel) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, e.Bounds.Left, e.Bounds.Top + 6, 3, e.Bounds.Height - 12);

            var item = _list.Items[e.Index];
            string title, line2, badge = ""; Color dot;
            if (item == _liveItem)
            {
                var live = _recorder.Live;
                title = live == null ? "Live" : "● Live: " + live.Title;
                line2 = live == null ? "" : live.Car + "  ·  recording";
                dot = Theme.Bad;
            }
            else
            {
                var r = (StageRun)item;
                title = r.Title;
                line2 = (string.IsNullOrEmpty(r.Car) ? "" : r.Car + "  ·  ") + r.Start.ToString("d MMM HH:mm") + "  ·  " + StageRecorder.FormatTime(r.Duration.TotalSeconds);
                StageStore.EnsurePoints(r);
                var a = r.Analyse();
                dot = Theme.VerdictColor(a.Verdict);
                if (a.ChangePct != 0) badge = "−" + Math.Abs(a.ChangePct) + " %";
                else if (a.Verdict == Verdict.Good) badge = "OK";
            }
            var b0 = e.Bounds;
            using (var db = new SolidBrush(dot)) g.FillEllipse(db, b0.Left + 14, b0.Top + 13, 9, 9);
            using (var tb = new SolidBrush(Theme.Text))
            using (var mb = new SolidBrush(Theme.Muted))
            {
                var titleRect = new RectangleF(b0.Left + 30, b0.Top + 7, b0.Width - 30 - 54, 20);
                var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                g.DrawString(title, BoldFont, tb, titleRect, sf);
                g.DrawString(line2, SmallFont, mb, new RectangleF(b0.Left + 30, b0.Top + 29, b0.Width - 40, 18), sf);
                if (badge != "")
                    using (var bb = new SolidBrush(dot))
                    using (var rf = new StringFormat { Alignment = StringAlignment.Far })
                        g.DrawString(badge, BoldFont, bb, new RectangleF(b0.Right - 64, b0.Top + 7, 54, 20), rf);
            }
            using (var p = new Pen(Theme.Bg)) g.DrawLine(p, b0.Left, b0.Bottom - 1, b0.Right, b0.Bottom - 1);
        }

        void OnSettingsChanged()
        {
            if (IsDisposed) return;
            _list.Invalidate();
            ShowSelected(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Settings.Changed -= OnSettingsChanged;
            base.Dispose(disposing);
        }

        void DeleteSelected()
        {
            var r = _list.SelectedItem as StageRun;
            if (r == null) return;
            if (MessageBox.Show(this, "Delete the run \"" + r.Title + "\" of " + r.Start.ToString("g") + "?", "MozaHud",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            StageStore.Delete(r);
            ReloadList(null);
        }

        // ---------------- details ----------------

        void ShowSelected(bool newRun)
        {
            var item = _list.SelectedItem;
            StageRun run;
            List<StagePoint> pts;
            if (item == _liveItem)
            {
                run = _recorder.Live;
                pts = _recorder.LivePoints();
                if (run == null) return;
                run = new StageRun
                {
                    Start = run.Start, End = DateTime.Now, Track = run.Track, Stage = run.Stage, Car = run.Car, Gain = run.Gain,
                    GainSource = run.GainSource, RotationDeg = run.RotationDeg, MaxNm = run.MaxNm, Points = pts ?? new List<StagePoint>(),
                };
            }
            else
            {
                run = item as StageRun;
                if (run != null) StageStore.EnsurePoints(run);
            }
            _shown = run;
            if (run == null)
            {
                _title.Text = "No stages recorded yet";
                _subtitle.Text = "Drive a stage in AC Rally with MozaHud running; it is recorded automatically.";
                _advice.Set(null, null);
                _stats.Set(null);
                _chart.SetData(null, 5.5, true);
                return;
            }
            var a = run.Analyse();
            _title.Text = (item == _liveItem ? "● " : "") + run.Title;
            var sub = new List<string>();
            if (!string.IsNullOrEmpty(run.Car)) sub.Add(run.Car);
            sub.Add(run.Start.ToString("ddd d MMM yyyy HH:mm"));
            sub.Add(StageRecorder.FormatTime(run.Points.Count > 0 ? run.Points[run.Points.Count - 1].T : 0));
            if (item != _liveItem && !string.IsNullOrEmpty(run.EndReason))
                sub.Add(run.EndReason == "finished"
                    ? "finished" + (string.IsNullOrEmpty(run.StageTime) ? "" : " in " + run.StageTime.TrimStart('0').TrimStart(':'))
                    : "not finished (" + run.EndReason + ")");
            _subtitle.Text = string.Join("   ·   ", sub);
            _advice.Set(run, a);
            _stats.Set(new[]
            {
                new[] { "CLIPPING", a.ClipPct.ToString("0.0") + " %" },
                new[] { "TORQUE AVG", a.AvgNm.ToString("0.0") + " Nm" },
                new[] { "STRONGEST", a.P99RawNm.ToString("0.0") + " Nm" },
                new[] { "SOFT LOCK", (a.LockPoints / 10.0).ToString("0.0") + " s" },
                new[] { "MOTOR MAX", a.MaxMotor.ToString("0") + " °C" },
                new[] { "MOSFET MAX", a.MaxMosfet.ToString("0") + " °C" },
                new[] { "ROTATION", run.RotationDeg > 0 ? run.RotationDeg + "°" : "?" },
                new[] { "FFB GAIN", run.Gain.HasValue ? run.Gain.Value.ToString() : "?" },
            });
            _chart.SetData(run.Points, run.MaxNm, newRun);
        }

        void RefreshLive()
        {
            var lines = _baseStatus();
            _status.Text = _recorder.Status ?? "";
            _base.Text = lines == null ? "" : string.Join("     ", lines.Where(l => !string.IsNullOrEmpty(l)));

            bool liveNow = _recorder.Live != null;
            bool listed = _list.Items.Count > 0 && _list.Items[0] == _liveItem;
            if (liveNow && !listed)
            {
                _list.Items.Insert(0, _liveItem);
                _list.SelectedIndex = 0;   // follow the new stage
            }
            else if (!liveNow && listed)
            {
                bool wasSelected = _list.SelectedIndex == 0;
                _list.Items.RemoveAt(0);
                if (wasSelected && _list.Items.Count > 0) _list.SelectedIndex = 0;
            }
            if (liveNow && _list.SelectedItem == _liveItem)
            {
                _list.Invalidate(_list.GetItemRectangle(0));
                if (DateTime.UtcNow >= _nextLiveAnalysis) { _nextLiveAnalysis = DateTime.UtcNow.AddSeconds(1); ShowSelected(false); }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // closing the window keeps MozaHud running in the tray
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            base.OnFormClosing(e);
        }
    }

    /// <summary>A row of small "LABEL / value" tiles, spread over the width.</summary>
    sealed class StatTiles : Control
    {
        string[][] _items;
        static readonly Font Lab = new Font("Segoe UI", 7.5f);
        static readonly Font Val = new Font("Segoe UI Semibold", 11.5f);

        public StatTiles()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void Set(string[][] items) { _items = items; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            if (_items == null || _items.Length == 0) return;
            float w = Width / (float)_items.Length;
            using (var lb = new SolidBrush(Theme.Faint))
            using (var vb = new SolidBrush(Theme.Text))
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                for (int i = 0; i < _items.Length; i++)
                {
                    float x = i * w;
                    g.DrawString(_items[i][0], Lab, lb, new RectangleF(x, 10, w - 6, 14), sf);
                    g.DrawString(_items[i][1], Val, vb, new RectangleF(x, 24, w - 6, 24), sf);
                }
        }
    }

    /// <summary>The coloured advice panel: headline, explanation and the suggested gain.</summary>
    sealed class AdviceBox : Control
    {
        StageRun _run; Analysis _a;
        static readonly Font Head = new Font("Segoe UI Semibold", 12.5f);
        static readonly Font Body = new Font("Segoe UI", 9.5f);
        static readonly Font Big = new Font("Segoe UI Semibold", 20f);
        static readonly Font Small = new Font("Segoe UI", 8.5f);

        public AdviceBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void Set(StageRun run, Analysis a) { _run = run; _a = a; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            if (_a == null) return;
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            var c = Theme.VerdictColor(_a.Verdict);
            using (var path = Theme.Round(r, 8))
            using (var bg = new SolidBrush(Theme.Panel))
                g.FillPath(bg, path);
            using (var bar = new SolidBrush(c)) g.FillRectangle(bar, 0, 10, 4, Height - 20);

            // The big number only when the gain should come down; green stays calm with at most a small hint.
            float rightW = 0;
            if (_a.ChangePct != 0)
            {
                string gainText = _a.SuggestedGain.HasValue && _run.Gain.HasValue ? _run.Gain + " → " + _a.SuggestedGain : "−" + Math.Abs(_a.ChangePct) + " %";
                string gainSub = _a.SuggestedGain.HasValue ? "suggested FFB gain" : "FFB gain";
                rightW = Math.Max(g.MeasureString(gainText, Big).Width, g.MeasureString(gainSub, Small).Width) + 24;
                using (var b = new SolidBrush(c)) g.DrawString(gainText, Big, b, Width - rightW, 18);
                using (var b = new SolidBrush(Theme.Muted)) g.DrawString(gainSub, Small, b, Width - rightW + 2, 56);
            }
            using (var b = new SolidBrush(c)) g.DrawString(_a.Headline, Head, b, 18, 10);
            float textW = Width - 36 - rightW;
            float hintH = string.IsNullOrEmpty(_a.Hint) ? 0 : g.MeasureString(_a.Hint, Small, (int)textW).Height;
            using (var b = new SolidBrush(Theme.Muted))
                g.DrawString(_a.Detail, Body, b, new RectangleF(18, 38, textW, Height - 44 - hintH));
            if (hintH > 0)
                using (var b = new SolidBrush(Color.FromArgb(150, 205, 170)))
                    g.DrawString("→ " + _a.Hint, Small, b, new RectangleF(18, Height - 8 - hintH, textW, hintH + 2));
        }
    }
}
