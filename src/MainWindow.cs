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
        readonly Label _status = new Label(), _base = new Label();
        readonly StageHeader _header = new StageHeader();
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
            // target clipping setting, top right
            var allowLabel = new Label { Text = "Target clipping", AutoSize = true, ForeColor = Theme.Muted, Font = SmallFont };
            var allow = new NumericUpDown
            {
                DecimalPlaces = 1, Increment = 0.1m, Minimum = 0, Maximum = 50, Width = 58,
                Value = (decimal)Settings.TargetClipPct, BackColor = Theme.PanelAlt, ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Right, Font = BoldFont,
            };
            var pct = new Label { Text = "%", AutoSize = true, ForeColor = Theme.Muted, Font = BoldFont };
            allow.ValueChanged += (s, e) => Settings.SetTargetClipPct((double)allow.Value);
            var tip = new ToolTip();
            const string tipText = "How much of the driving time the motor should be at its limit (clipping).\n" +
                "Above it MozaHud suggests less FFB gain; under 60 % of it (1.5 % for 2.5 %) more gain,\n" +
                "so you don't leave feel on the table. Green in between.";
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
            _header.Dock = DockStyle.Top; _header.Height = 62;
            _advice.Dock = DockStyle.Top; _advice.Height = 104;
            _advice.ApplyClicked += ApplyInStageSwitch;
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
            right.Controls.Add(_header);

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
            StageRun src = item == _liveItem ? _recorder.Live : (StageRun)item;
            string title, line2, badge = ""; Color dot;
            if (item == _liveItem)
            {
                title = src == null ? "Live" : "Live: " + src.Title;
                line2 = "recording";
                dot = Theme.Bad;
            }
            else
            {
                var r = (StageRun)item;
                title = r.Title;
                line2 = r.Start.ToString("d MMM HH:mm") + "  ·  " + StageRecorder.FormatTime(r.Duration.TotalSeconds);
                StageStore.EnsurePoints(r);
                var a = r.Analyse();
                dot = Theme.VerdictColor(a.Verdict);
                if (a.ChangePct != 0) badge = (a.ChangePct > 0 ? "+" : "−") + Math.Abs(a.ChangePct) + " %";
                else if (a.Verdict == Verdict.Good) badge = "OK";
            }
            var b0 = e.Bounds;
            // verdict dot, rally flag, title; car logo + car, date and time below
            using (var db = new SolidBrush(dot)) g.FillEllipse(db, b0.Left + 12, b0.Top + 13, 8, 8);
            float x = b0.Left + 26;
            if (src != null)
            {
                string rallyName;
                RallyStyle.For(Names.RallyOf(src, out rallyName), rallyName).DrawFlag(g, new RectangleF(x, b0.Top + 9, 24, 16));
            }
            float tx = x + 32;
            using (var tb = new SolidBrush(Theme.Text))
            using (var mb = new SolidBrush(Theme.Muted))
            {
                var titleRect = new RectangleF(tx, b0.Top + 7, b0.Right - tx - 54, 20);
                var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                g.DrawString(title, BoldFont, tb, titleRect, sf);
                float lx = x;
                if (src != null && !string.IsNullOrEmpty(src.Car))
                {
                    BrandMark.Draw(g, new RectangleF(x, b0.Top + 30, 24, 16), src.Car);
                    line2 = src.Car + "  ·  " + line2;
                    lx = tx;
                }
                g.DrawString(line2, SmallFont, mb, new RectangleF(lx, b0.Top + 30, b0.Right - lx - 8, 18), sf);
                if (badge != "")
                    using (var bb = new SolidBrush(dot))
                    using (var rf = new StringFormat { Alignment = StringAlignment.Far })
                        g.DrawString(badge, BoldFont, bb, new RectangleF(b0.Right - 64, b0.Top + 7, 54, 20), rf);
            }
            using (var p = new Pen(Theme.Bg)) g.DrawLine(p, b0.Left, b0.Bottom - 1, b0.Right, b0.Bottom - 1);
        }

        // ---------------- StageSwitch ----------------

        bool _stageSwitchRunning;

        void UpdateApplyButton()
        {
            var run = _shown;
            string r, s;
            bool on = _stageSwitchRunning && run != null && run.Analyse().SuggestedGain.HasValue && StageSwitchLink.Keys(run, out r, out s);
            _advice.SetApplyVisible(on);
        }

        void ApplyInStageSwitch()
        {
            var run = _shown;
            if (run == null) return;
            var a = run.Analyse();
            if (!a.SuggestedGain.HasValue) return;
            string rally, stage;
            if (!StageSwitchLink.Keys(run, out rally, out stage))
            {
                MessageBox.Show(this, "MozaHud doesn't know which StageSwitch stage this run was on.", "MozaHud", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            StageSwitchLink.Current now;
            try { now = StageSwitchLink.Get(rally, stage, run.Car); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "StageSwitch: " + ex.Message, "MozaHud", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string why = string.Format("Suggested {0} (was {1}): clipping {2:0.0} % → about {3:0.0} % (target {4:0.0} %).",
                a.SuggestedGain.Value, run.Gain, a.ClipPct, a.PredictedPct, a.TargetPct);
            using (var dlg = new ApplyGainDialog(now, run.Car, a.SuggestedGain.Value, why, Icon))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var msg = StageSwitchLink.SetGain(dlg.Scope, rally, stage, run.Car, dlg.Gain);
                    Log.Write("StageSwitch: " + msg);
                    MessageBox.Show(this, msg, "Saved in StageSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "StageSwitch: " + ex.Message, "MozaHud", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
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
                    Start = run.Start, End = DateTime.Now, Track = run.Track, Stage = run.Stage, Car = run.Car, Gain = run.Gain, Base = run.Base,
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
                _header.Set(null, "No stages recorded yet", null, "Drive a stage in AC Rally with MozaHud running; it is recorded automatically.");
                _advice.Set(null, null);
                _stats.Set(null);
                _chart.SetData(null, 5.5, true);
                return;
            }
            var a = run.Analyse();
            var sub = new List<string>();
            if (!string.IsNullOrEmpty(run.Car)) sub.Add(run.Car);
            sub.Add(run.Start.ToString("ddd d MMM yyyy HH:mm"));
            sub.Add(StageRecorder.FormatTime(run.Points.Count > 0 ? run.Points[run.Points.Count - 1].T : 0));
            if (item != _liveItem && !string.IsNullOrEmpty(run.EndReason))
                sub.Add(run.EndReason == "finished"
                    ? "finished" + (string.IsNullOrEmpty(run.StageTime) ? "" : " in " + run.StageTime.TrimStart('0').TrimStart(':'))
                    : "not finished (" + run.EndReason + ")");
            string rallyName;
            _header.Set(RallyStyle.For(Names.RallyOf(run, out rallyName), rallyName),
                (item == _liveItem ? "● " : "") + run.Title, run.Car, string.Join("   ·   ", sub));
            _advice.Set(run, a);
            UpdateApplyButton();
            _stats.Set(new[]
            {
                new[] { "CLIPPING", a.ClipPct.ToString("0.0") + " %",
                    "Share of the driving time (over 5 km/h, soft lock not counted) in which the motor hit its torque limit.\n" +
                    "Compared with your target zone (" + a.ZoneLowPct.ToString("0.0") + "–" + a.TargetPct.ToString("0.0") + " %) for the advice." },
                new[] { "LONGEST CLIP", a.ClipEpisodes == 0 ? "—" : a.LongestClipS.ToString("0.0") + " s",
                    "The longest single stretch the motor stayed at its limit without a break.\n" +
                    "0.1–0.2 s: a bump, kerb or landing — you barely lose anything.\n" +
                    "0.5 s or more: a whole part of a corner at the limit — the force stays flat there and you lose\n" +
                    "the feel for grip. Long stretches matter more than many short taps.\n" +
                    "Measured in steps of 0.1 s." },
                new[] { "AVG CLIP", a.ClipEpisodes == 0 ? "—" : a.AvgClipS.ToString("0.00") + " s  ×" + a.ClipEpisodes,
                    "How long a clipping moment lasts on average, and how many separate moments there were (×).\n" +
                    "Consecutive clipped 0.1 s steps count as one moment.\n" +
                    "Short average (around 0.1–0.2 s): mostly peaks from bumps and kerbs.\n" +
                    "Long average: the base is at its limit through sustained cornering loads — lowering the gain helps most then." },
                new[] { "TORQUE AVG", a.AvgNm.ToString("0.0") + " Nm", "Average force on the wheel while driving (smoothed torque)." },
                new[] { "STRONGEST", a.P99RawNm.ToString("0.0") + " Nm",
                    "The strongest moments: 99 % of the driving time the raw torque stayed below this." },
                new[] { "SOFT LOCK", (a.LockPoints / 10.0).ToString("0.0") + " s",
                    "Time with the wheel at the end of its rotation (soft lock). Clipping there is the lock pushing back, not FFB, and isn't counted." },
                new[] { "MOTOR MAX", a.MaxMotor.ToString("0") + " °C" },
                new[] { "MOSFET MAX", a.MaxMosfet.ToString("0") + " °C" },
                new[] { "BASE", (string.IsNullOrEmpty(run.Base) ? "" : run.Base + " · ") + run.MaxNm.ToString("0.0") + " Nm" },
                new[] { "ROTATION", run.RotationDeg > 0 ? run.RotationDeg + "°" : "?" },
                new[] { "FFB GAIN", run.Gain.HasValue ? run.Gain.Value.ToString() : "?" },
            });
            _chart.SetData(run.Points, run.MaxNm, newRun);
        }

        void RefreshLive()
        {
            var lines = _baseStatus();
            _status.Text = _recorder.Status ?? "";
            bool ss = StageSwitchLink.IsRunning();
            if (ss != _stageSwitchRunning) { _stageSwitchRunning = ss; UpdateApplyButton(); }
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

    /// <summary>Stage title with the rally's flag; below it the car's logo and the run's details.</summary>
    sealed class StageHeader : Control
    {
        RallyStyle _flag; string _title = "", _car, _sub = "";
        static readonly Font TitleFont = new Font("Segoe UI Semibold", 15f);
        static readonly Font SubFont = new Font("Segoe UI", 9.5f);

        public StageHeader()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        /// <summary>flag: null for none; car: draws its logo before the subtitle (which starts with the car's name).</summary>
        public void Set(RallyStyle flag, string title, string car, string subtitle)
        {
            _flag = flag; _title = title ?? ""; _car = string.IsNullOrEmpty(car) ? null : car; _sub = subtitle ?? "";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float x = 0;
            if (_flag != null) { _flag.DrawFlag(g, new RectangleF(0, 6, 36, 24)); x = 46; }
            var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using (var tb = new SolidBrush(Theme.Text)) g.DrawString(_title, TitleFont, tb, new RectangleF(x - 3, 1, Width - x, 34), sf);
            float sx = 0;
            if (_car != null) { BrandMark.Draw(g, new RectangleF(0, 38, 30, 20), _car); sx = 38; }
            else if (_flag != null) sx = 0;
            using (var mb = new SolidBrush(Theme.Muted)) g.DrawString(_sub, SubFont, mb, new RectangleF(sx, 39, Width - sx, 20), sf);
        }
    }

    /// <summary>
    /// A row of small "LABEL / value" tiles. Each item: label, value, optional hover explanation. Tiles are as wide as
    /// their content, with the spare width spread between them.
    /// </summary>
    sealed class StatTiles : Control
    {
        string[][] _items;
        float[] _x = new float[0], _w = new float[0];
        readonly ToolTip _tip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300, ReshowDelay = 100 };
        int _tipIndex = -1;
        static readonly Font Lab = new Font("Segoe UI", 7.5f);
        static readonly Font Val = new Font("Segoe UI Semibold", 11.5f);

        public StatTiles()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void Set(string[][] items) { _items = items; _tipIndex = -1; _tip.SetToolTip(this, null); Invalidate(); }

        void Layout(Graphics g)
        {
            int n = _items == null ? 0 : _items.Length;
            _x = new float[n]; _w = new float[n];
            float total = 0;
            for (int i = 0; i < n; i++)
            {
                bool help = _items[i].Length > 2 && !string.IsNullOrEmpty(_items[i][2]);
                _w[i] = Math.Max(g.MeasureString(_items[i][0] + (help ? " ⓘ" : ""), Lab).Width, g.MeasureString(_items[i][1], Val).Width) + 4;
                total += _w[i];
            }
            float gap = n > 1 ? Math.Max(6, (Width - total) / (n - 1)) : 0;
            float scale = total + gap * (n - 1) > Width ? (Width - gap * (n - 1)) / total : 1;
            float x = 0;
            for (int i = 0; i < n; i++) { _w[i] *= scale; _x[i] = x; x += _w[i] + gap; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            if (_items == null || _items.Length == 0) return;
            Layout(g);
            using (var lb = new SolidBrush(Theme.Faint))
            using (var vb = new SolidBrush(Theme.Text))
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                for (int i = 0; i < _items.Length; i++)
                {
                    bool help = _items[i].Length > 2 && !string.IsNullOrEmpty(_items[i][2]);
                    g.DrawString(_items[i][0] + (help ? " ⓘ" : ""), Lab, lb, new RectangleF(_x[i], 10, _w[i], 14), sf);
                    g.DrawString(_items[i][1], Val, vb, new RectangleF(_x[i], 24, _w[i], 24), sf);
                }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = -1;
            for (int i = 0; i < _x.Length; i++) if (e.X >= _x[i] && e.X <= _x[i] + _w[i] + 6) { idx = i; break; }
            if (idx == _tipIndex) return;
            _tipIndex = idx;
            string text = idx >= 0 && _items != null && idx < _items.Length && _items[idx].Length > 2 ? _items[idx][2] : null;
            _tip.SetToolTip(this, text);
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _tipIndex = -1; _tip.SetToolTip(this, null); }
    }

    /// <summary>The coloured advice panel: headline, explanation and the suggested gain.</summary>
    sealed class AdviceBox : Control
    {
        StageRun _run; Analysis _a;
        static readonly Font Head = new Font("Segoe UI Semibold", 12.5f);
        static readonly Font Body = new Font("Segoe UI", 9.5f);
        static readonly Font Big = new Font("Segoe UI Semibold", 20f);
        static readonly Font Small = new Font("Segoe UI", 8.5f);

        readonly Button _apply = new Button
        {
            Text = "Apply in StageSwitch…", FlatStyle = FlatStyle.Flat, BackColor = Theme.PanelAlt, ForeColor = Theme.Text,
            Font = new Font("Segoe UI", 8.5f), Size = new Size(150, 24), Visible = false, Cursor = Cursors.Hand,
        };

        /// <summary>The "Apply in StageSwitch…" button was clicked.</summary>
        public event Action ApplyClicked;

        public AdviceBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _apply.FlatAppearance.BorderColor = Theme.Border;
            _apply.Click += (s, e) => { var h = ApplyClicked; if (h != null) h(); };
            Controls.Add(_apply);
            Resize += (s, e) => _apply.Location = new Point(Width - _apply.Width - 14, Height - _apply.Height - 10);
        }

        public void Set(StageRun run, Analysis a) { _run = run; _a = a; Invalidate(); }

        /// <summary>Shown when StageSwitch runs and there is a gain to apply.</summary>
        public void SetApplyVisible(bool on) { if (_apply.Visible != on) { _apply.Visible = on; Invalidate(); } }

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

            // The big number only when the gain should change; green stays calm with at most a small hint.
            float rightW = 0;
            if (_a.ChangePct != 0)
            {
                string gainText = _a.SuggestedGain.HasValue && _run.Gain.HasValue ? _run.Gain + " → " + _a.SuggestedGain
                    : (_a.ChangePct > 0 ? "+" : "−") + Math.Abs(_a.ChangePct) + " %";
                string gainSub = _a.SuggestedGain.HasValue ? "suggested FFB gain" : "FFB gain";
                rightW = Math.Max(Math.Max(g.MeasureString(gainText, Big).Width, g.MeasureString(gainSub, Small).Width) + 24,
                                  _apply.Visible ? _apply.Width + 28 : 0);
                float top = _apply.Visible ? 6 : 18;
                using (var b = new SolidBrush(c)) g.DrawString(gainText, Big, b, Width - rightW, top);
                using (var b = new SolidBrush(Theme.Muted)) g.DrawString(gainSub, Small, b, Width - rightW + 2, top + 38);
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
