using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace MozaHud
{
    /// <summary>
    /// Clipping settings: which clipping counts for the FFB advice (spike length, burst, target) with four presets.
    /// Left: the idea, an example built from the entered values and coloured by the rule, the inputs and presets.
    /// Right: what the target means in seconds on the user's last stage, and the recent runs re-judged live.
    /// </summary>
    public sealed class ClipSettingsDialog : Form
    {
        ClipRule _rule;
        readonly NumericUpDown _spike, _count, _window, _weight, _target;
        readonly ExamplePanel _example = new ExamplePanel();
        readonly TargetPanel _targetPanel = new TargetPanel();
        readonly RunsPanel _runs = new RunsPanel();
        readonly List<PresetButton> _presets = new List<PresetButton>();
        bool _syncing;

        static readonly Font Head = new Font("Segoe UI Semibold", 13.5f);
        static readonly Font Body = new Font("Segoe UI", 9.5f);
        static readonly Font Small = new Font("Segoe UI", 8.5f);
        static readonly Font Num = new Font("Segoe UI Semibold", 10f);

        const int LeftX = 24, LeftW = 580, RightX = 628, RightW = 560;

        public ClipSettingsDialog(Icon icon)
        {
            _rule = Settings.Rule;
            Text = "Clipping settings";
            Icon = icon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Body;
            ClientSize = new Size(RightX + RightW + 24, 790);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };

            // ================= left column =================
            var title = new Label { Text = "Which clipping counts?", Font = Head, AutoSize = true, Location = new Point(LeftX - 2, 16) };
            var intro = new Label
            {
                Location = new Point(LeftX, 50), Size = new Size(LeftW, 118), ForeColor = Theme.Muted,
                Text = "Clipping means the motor is at its torque limit: the force can't go higher, so everything the game sends " +
                       "above it is lost. How much that matters depends on how long it lasts.\n" +
                       "• A lone spike (a bump, a kerb, a landing) only cuts the top off one hit. You hardly feel it.\n" +
                       "• A burst of clips close together, or one long clip, keeps the force flat for a whole stretch. There you " +
                       "lose the road and the grip. That is the clipping MozaHud counts for its FFB gain advice.",
            };

            var exTitle = SectionLabel("How your rule sees clips (drawn from your values)", LeftX, 176);
            _example.SetBounds(LeftX, 198, LeftW, 196);

            var ruleTitle = SectionLabel("Your rule", LeftX, 410);
            int y = 434;
            _spike = Spinner(2, 0.05m, 2, 0.05m, (decimal)_rule.SpikeMaxS);
            AddRow(y, Lbl("Spike:", 60), Lbl("a clip up to"), _spike, Lbl("s. Longer is a"), Bold("long clip"), Lbl("and always counts."));
            y += 34;
            _count = Spinner(0, 2, 20, 1, _rule.Count);
            _window = Spinner(2, 0.15m, 10, 0.05m, (decimal)_rule.WindowS);
            AddRow(y, Lbl("Burst:", 60), Lbl("at least"), _count, Lbl("clips within"), _window, Lbl("s. Spikes only count in a burst."));
            y += 34;
            _weight = Spinner(2, 1, 5, 0.05m, (decimal)_rule.LongWeight);
            AddRow(y, Lbl("Weight:", 60), Lbl("a long clip counts"), _weight, Lbl("× its time (a burst counts 1×)."));
            y += 34;
            _target = Spinner(1, 0, 50, 0.1m, (decimal)_rule.TargetPct);
            AddRow(y, Lbl("Target:", 60), _target, Lbl("% of the driving time in counted clipping (see the right)."));
            var presetTitle = SectionLabel("Presets", LeftX, y + 40);
            int pw = (LeftW - 8) / 2;
            for (int i = 0; i < ClipRule.Presets.Length; i++)
            {
                var p = ClipRule.Presets[i];
                var b = new PresetButton(p, PresetBlurb(p))
                {
                    Location = new Point(LeftX + (i % 2) * (pw + 8), y + 62 + (i / 2) * 70), Size = new Size(pw, 62),
                };
                b.Click += (s, e) => { _rule = p; Apply(); };
                _presets.Add(b);
                Controls.Add(b);
            }

            // ================= right column =================
            var targetTitle = SectionLabel("What the target means", RightX, 26);
            _targetPanel.SetBounds(RightX, 50, RightW, 330);
            var runsTitle = SectionLabel("Your recent runs with this rule", RightX, 410);
            _runs.SetBounds(RightX, 434, RightW, 262);

            // ---- buttons ----
            var save = DialogButton("Save", true);
            var cancel = DialogButton("Cancel", false);
            save.Location = new Point(ClientSize.Width - 24 - save.Width, ClientSize.Height - 46);
            cancel.Location = new Point(save.Left - 8 - cancel.Width, save.Top);
            save.Click += (s, e) => { Settings.SetRule(_rule); DialogResult = DialogResult.OK; Close(); };
            cancel.DialogResult = DialogResult.Cancel;
            CancelButton = cancel; AcceptButton = save;

            Controls.AddRange(new Control[] { title, intro, exTitle, _example, ruleTitle, presetTitle,
                                              targetTitle, _targetPanel, runsTitle, _runs, save, cancel });

            foreach (var n in new[] { _spike, _count, _window, _weight, _target }) n.ValueChanged += (s, e) => FromControls();
            Shown += (s, e) =>
            {
                Cursor = Cursors.WaitCursor;
                _runs.Load();
                _targetPanel.SetStage(_runs.LatestTitle, _runs.LatestDrivingS);
                Cursor = Cursors.Default;
                Apply();
            };
            Apply();
        }

        static string PresetBlurb(ClipRule p)
        {
            string why = p.Name == "Very little spiking" ? "Keeps the most detail."
                       : p.Name == "Balanced" ? "Recommended start."
                       : p.Name == "Aggressive" ? "More force, a few flat moments."
                       : "Most force, accepts clipping.";
            return why + "\n" + string.Format("Spikes ≤ {0:0.00} s · {1} in {2:0.00} s · long ×{3:0.00} · {4:0.0} %", p.SpikeMaxS, p.Count, p.WindowS, p.LongWeight, p.TargetPct);
        }

        void FromControls()
        {
            if (_syncing) return;
            _rule = new ClipRule
            {
                SpikeMaxS = (double)_spike.Value, Count = (int)_count.Value, WindowS = (double)_window.Value,
                LongWeight = (double)_weight.Value, TargetPct = (double)_target.Value,
            }.Normalized();
            Apply();
        }

        /// <summary>Puts the rule into the controls (the window can't be shorter than the clips it must hold) and redraws.</summary>
        void Apply()
        {
            _syncing = true;
            _spike.Value = (decimal)_rule.SpikeMaxS;
            _count.Value = _rule.Count;
            _window.Minimum = (decimal)ClipRule.MinWindowS(_rule.Count);
            _window.Value = (decimal)_rule.WindowS;
            _weight.Value = (decimal)_rule.LongWeight;
            _target.Value = (decimal)_rule.TargetPct;
            _syncing = false;

            string active = _rule.PresetName;
            foreach (var b in _presets) b.Active = b.Preset.Name == active;
            _example.SetRule(_rule);
            _targetPanel.SetRule(_rule);
            _runs.SetRule(_rule);
        }

        void AddRow(int y, params Control[] items)
        {
            int x = LeftX;
            foreach (var c in items)
            {
                Controls.Add(c);
                c.Location = new Point(x, c is NumericUpDown ? y : y + 3);
                int cw = c is Label && ((Label)c).AutoSize ? c.PreferredSize.Width : c.Width;
                x += cw + 4;
            }
        }

        static Label Lbl(string text, int width = 0)
        {
            var l = new Label { Text = text };
            if (width > 0) { l.AutoSize = false; l.Size = new Size(width, 20); l.ForeColor = Theme.Muted; }
            else l.AutoSize = true;
            return l;
        }

        static Label Bold(string text) { return new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f) }; }

        static Label SectionLabel(string text, int x, int y)
        {
            return new Label { Text = text.ToUpperInvariant(), Font = Small, ForeColor = Theme.Faint, AutoSize = true, Location = new Point(x, y) };
        }

        static NumericUpDown Spinner(int decimals, decimal min, decimal max, decimal step, decimal value)
        {
            return new NumericUpDown
            {
                DecimalPlaces = decimals, Minimum = min, Maximum = max, Increment = step, Width = decimals == 0 ? 48 : decimals == 1 ? 56 : 64,
                Value = Math.Max(min, Math.Min(max, value)), BackColor = Theme.PanelAlt, ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Right, Font = Num,
            };
        }

        static Button DialogButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat, Size = new Size(96, 30), Cursor = Cursors.Hand,
                BackColor = primary ? Theme.Accent : Theme.Panel, ForeColor = primary ? Color.Black : Theme.Muted,
                Font = primary ? new Font("Segoe UI Semibold", 9.5f) : Body,
            };
            b.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Border;
            return b;
        }

        static void Card(Graphics g, Control c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            var box = new RectangleF(0.5f, 0.5f, c.Width - 1.5f, c.Height - 1.5f);
            using (var path = Theme.Round(box, 8)) using (var bg = new SolidBrush(Theme.Panel)) g.FillPath(bg, path);
        }

        static string Clock(double s) { return string.Format("{0}:{1:00}", (int)(s / 60), (int)(s % 60)); }

        /// <summary>A preset: name in bold, what it is for and its values below; outlined when the rule matches it.</summary>
        sealed class PresetButton : Control
        {
            public ClipRule Preset { get; private set; }
            readonly string _blurb;
            bool _hover, _active;
            static readonly Font NameFont = new Font("Segoe UI Semibold", 10f);
            static readonly Font Line = new Font("Segoe UI", 8.25f);

            public bool Active { get { return _active; } set { if (_active != value) { _active = value; Invalidate(); } } }

            public PresetButton(ClipRule preset, string blurb)
            {
                Preset = preset; _blurb = blurb;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Theme.Bg);
                var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                using (var path = Theme.Round(r, 7))
                using (var bg = new SolidBrush(_hover ? Theme.Hover : Theme.Panel))
                using (var border = new Pen(_active ? Theme.Accent : _hover ? Theme.Muted : Theme.Border, _active ? 2f : 1f))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(border, path);
                }
                using (var tb = new SolidBrush(_active ? Theme.Accent : Theme.Text)) g.DrawString(Preset.Name, NameFont, tb, 10, 6);
                using (var mb = new SolidBrush(Theme.Muted)) g.DrawString(_blurb, Line, mb, new RectangleF(10, 27, Width - 14, Height - 28));
            }
        }

        /// <summary>
        /// A made-up stretch of torque built from the rule's values, with exact clip lengths: a lone spike as long as a
        /// spike may be, Count spikes spread just wider than the window, Count spikes within the window, and a long clip
        /// a bit longer than a spike. Coloured by the rule itself (red counts, amber is a lone spike), with each group's
        /// lengths and a time scale.
        /// </summary>
        sealed class ExamplePanel : Control
        {
            ClipRule _rule;
            const double Limit = 5.5;
            static readonly Font Small = new Font("Segoe UI", 8.25f);
            static readonly Font Bold = new Font("Segoe UI Semibold", 8.25f);

            sealed class Group { public string Name; public List<double> Starts = new List<double>(); public List<double> Ends = new List<double>(); public double From, To; }

            public ExamplePanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            public void SetRule(ClipRule r) { _rule = r; Invalidate(); }

            /// <summary>
            /// Length of each spike in the example's spike groups: the spike length, or less when Count of them with at
            /// least one step between them don't fit in the window (in whole steps).
            /// </summary>
            public static double BurstSpikeS(ClipRule r)
            {
                double fit = (r.WindowS - (r.Count - 1) * ClipRule.StepS) / r.Count;
                return Math.Max(ClipRule.StepS, Math.Round(Math.Floor(Math.Min(r.SpikeMaxS, fit) / ClipRule.StepS + 1e-6) * ClipRule.StepS, 2));
            }

            /// <summary>The example's spread-out group: a third wider than the window, so it just misses being a burst.</summary>
            static double WideS(ClipRule r) { return Up(Math.Max(r.WindowS * 1.3, r.WindowS + 0.3)); }

            /// <summary>The example's long clip: half again as long as a spike may be.</summary>
            public static double LongClipS(ClipRule r) { return Up(Math.Max(r.SpikeMaxS * 1.5, r.SpikeMaxS + ClipRule.StepS)); }

            /// <summary>Up to the next step, so example lengths are values you could also enter.</summary>
            static double Up(double v) { return Math.Round(Math.Ceiling(v / ClipRule.StepS - 1e-6) * ClipRule.StepS, 2); }

            static double Base(double t) { return 2.4 + 0.9 * Math.Sin(t * 0.9) + 0.35 * Math.Sin(t * 4.3) + 0.15 * Math.Sin(t * 11); }

            List<Group> Build(out double span)
            {
                var r = _rule;
                double gap = Math.Max(1.2, r.WindowS + 0.4);   // far enough apart that groups never form a burst together
                var groups = new List<Group>();
                double t = 0.6;
                Func<string, Group> add = name => { var gr = new Group { Name = name, From = t }; groups.Add(gr); return gr; };
                Action<Group, double, double> clip = (gr, s, len) => { gr.Starts.Add(s); gr.Ends.Add(s + len); };

                var lone = add(string.Format("Lone spike\n{0:0.00} s", r.SpikeMaxS));
                clip(lone, t, r.SpikeMaxS);
                t += r.SpikeMaxS; lone.To = t; t += gap;

                // the spikes are as long as a spike may be, unless that many don't fit in the window
                double spikeLen = BurstSpikeS(r);
                double wide = WideS(r);   // too spread out for a burst
                var spread = add(string.Format("{0} spikes of {1:0.00} s\nover {2:0.00} s", r.Count, spikeLen, wide));
                for (int i = 0; i < r.Count; i++) clip(spread, t + i * (wide - spikeLen) / (r.Count - 1), spikeLen);
                t += wide; spread.To = t; t += gap;

                var burst = add(string.Format("{0} spikes of {1:0.00} s\nwithin {2:0.00} s (a burst)", r.Count, spikeLen, r.WindowS));
                for (int i = 0; i < r.Count; i++) clip(burst, t + i * (r.WindowS - spikeLen) / (r.Count - 1), spikeLen);
                t += r.WindowS; burst.To = t; t += gap;

                double longLen = LongClipS(r);
                var lng = add(Math.Abs(r.LongWeight - 1) > 0.001
                    ? string.Format("Long clip\n{0:0.00} s × {1:0.00}", longLen, r.LongWeight)
                    : string.Format("Long clip\n{0:0.00} s", longLen));
                clip(lng, t, longLen);
                t += longLen; lng.To = t;

                span = t + 0.6;
                return groups;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                Card(g, this);
                if (_rule.Count < 2) return;

                double span;
                var groups = Build(out span);
                var plot = new RectangleF(12, 30, Width - 24, Height - 30 - 76);
                Func<double, float> X = t => plot.Left + (float)(t / span * plot.Width);
                Func<double, float> Y = v => plot.Bottom - (float)(Math.Min(v, Limit + 0.6) / (Limit + 0.6) * plot.Height);

                // all clips in time order, judged by the real rule
                var clips = groups.SelectMany((gr, k) => gr.Starts.Select((s, i) => new { S = s, E = gr.Ends[i], G = k })).OrderBy(c => c.S).ToList();
                var counts = Analysis.CountClips(clips.Select(c => c.S).ToList(), clips.Select(c => c.E).ToList(), _rule);
                Func<double, int> clipAt = t => { for (int i = 0; i < clips.Count; i++) if (t >= clips[i].S && t < clips[i].E) return i; return -1; };

                // time scale
                double tick = span > 40 ? 10 : span > 20 ? 5 : span > 8 ? 2 : 1;
                using (var grid = new Pen(Theme.Grid))
                using (var fb = new SolidBrush(Theme.Faint))
                    for (double t = 0; t <= span; t += tick)
                    {
                        g.DrawLine(grid, X(t), plot.Top, X(t), plot.Bottom);
                        g.DrawString(t.ToString("0") + " s", Small, fb, X(t) + 2, plot.Bottom + 1);
                    }

                using (var pen = new Pen(Theme.Bad, 1f) { DashStyle = DashStyle.Dash })
                    g.DrawLine(pen, plot.Left, Y(Limit), plot.Right, Y(Limit));
                using (var b = new SolidBrush(Theme.Bad)) g.DrawString("motor limit", Small, b, plot.Right - 62, Y(Limit) - 16);

                // bars where it clipped
                using (var red = new SolidBrush(Color.FromArgb(200, Theme.Bad)))
                using (var amber = new SolidBrush(Color.FromArgb(200, Theme.LoneClip)))
                    for (int i = 0; i < clips.Count; i++)
                    {
                        float x0 = X(clips[i].S), x1 = X(clips[i].E);
                        g.FillRectangle(counts[i] ? red : amber, x0, Y(Limit), Math.Max(2, x1 - x0), plot.Bottom - Y(Limit));
                    }

                // torque line: green, flat at the limit while clipping (red / amber there)
                var pts = new List<PointF>(); int state = 0;
                Action<int> flush = s =>
                {
                    if (pts.Count >= 2)
                        using (var pen = new Pen(s == 0 ? Theme.TorqueOk : s == 1 ? Theme.Bad : Theme.LoneClip, s == 0 ? 1.6f : 2.6f) { LineJoin = LineJoin.Round })
                            g.DrawLines(pen, pts.ToArray());
                };
                for (int px = 0; px <= (int)plot.Width; px++)
                {
                    double t = px / plot.Width * span;
                    int i = clipAt(t);
                    double v; int s;
                    if (i >= 0) { v = Limit; s = counts[i] ? 1 : 2; }
                    else
                    {
                        double near = clips.Min(c => Math.Min(Math.Abs(t - c.S), Math.Abs(t - c.E)));
                        v = Base(t) + (Limit - Base(t)) * Math.Exp(-near * near / 0.004);
                        s = 0;
                    }
                    var p = new PointF(plot.Left + px, Y(v));
                    if (s != state) { pts.Add(p); flush(state); var last = pts[pts.Count - 1]; pts.Clear(); pts.Add(last); state = s; }
                    else pts.Add(p);
                }
                flush(state);

                // group names, lengths and verdicts
                using (var mb = new SolidBrush(Theme.Muted))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                    for (int k = 0; k < groups.Count; k++)
                    {
                        bool counted = false; double worth = 0;
                        for (int i = 0; i < clips.Count; i++)
                        {
                            if (clips[i].G != k || !counts[i]) continue;
                            counted = true;
                            double len = clips[i].E - clips[i].S;
                            bool lng = Analysis.IsLong(clips[i].S, clips[i].E, _rule);
                            worth += lng ? len * _rule.LongWeight : len;
                        }
                        string verdict = !counted ? "not counted"
                            : string.Format("counts {0:0.00} s", worth);
                        float cx = (X(groups[k].From) + X(groups[k].To)) / 2;
                        cx = Math.Max(plot.Left + 72, Math.Min(plot.Right - 72, cx));
                        g.DrawString(groups[k].Name, Small, mb, new RectangleF(cx - 80, plot.Bottom + 18, 160, 32), sf);
                        using (var vb = new SolidBrush(counted ? Theme.Bad : Theme.LoneClip))
                            g.DrawString(verdict, Bold, vb, new RectangleF(cx - 80, plot.Bottom + 50, 160, 16), sf);
                    }

                // the spread group's width and the window to scale over the burst
                for (int k = 1; k <= 2; k++)
                {
                    float wx0 = X(groups[k].From), wx1 = X(groups[k].To), wy = 18;
                    using (var pen = new Pen(Theme.Text, 1f))
                    {
                        g.DrawLine(pen, wx0, wy, wx1, wy);
                        g.DrawLine(pen, wx0, wy - 4, wx0, wy + 4);
                        g.DrawLine(pen, wx1, wy - 4, wx1, wy + 4);
                    }
                    using (var tb = new SolidBrush(Theme.Text))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                        g.DrawString((groups[k].To - groups[k].From).ToString("0.00") + " s", Small, tb, new RectangleF(wx0 - 30, wy - 16, wx1 - wx0 + 60, 14), sf);
                }
            }
        }

        /// <summary>
        /// The target in seconds: the user's last stage as a strip with the target's worth of counted clipping spread
        /// over it, the green zone as a gauge in seconds, and what counts as driving time.
        /// </summary>
        sealed class TargetPanel : Control
        {
            ClipRule _rule;
            string _stage;
            double _drivingS = 420;
            static readonly Font Big = new Font("Segoe UI Semibold", 11.5f);
            static readonly Font Body = new Font("Segoe UI", 9f);
            static readonly Font Small = new Font("Segoe UI", 8.25f);
            static readonly Font Bold = new Font("Segoe UI Semibold", 8.25f);

            public TargetPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            public void SetRule(ClipRule r) { _rule = r; Invalidate(); }

            /// <summary>The stage to explain with (null: a made-up 7-minute stage).</summary>
            public void SetStage(string title, double drivingS)
            {
                _stage = drivingS >= 60 ? title : null;
                _drivingS = drivingS >= 60 ? drivingS : 420;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                Card(g, this);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                float x = 16, w = Width - 32;
                double target = _rule.TargetPct, low = Math.Round(target * Analysis.ZoneFraction, 1);
                double targetS = _drivingS * target / 100, lowS = _drivingS * low / 100;
                // one counted moment: a burst as in the example (Count spikes of the spike length, or shorter if they must fit)
                double moment = _rule.Count * ExamplePanel.BurstSpikeS(_rule);
                int moments = (int)Math.Round(targetS / moment);

                using (var tb = new SolidBrush(Theme.Text))
                using (var mb = new SolidBrush(Theme.Muted))
                using (var fb = new SolidBrush(Theme.Faint))
                {
                    g.DrawString(string.Format("{0:0.0} % = {1:0.0} s of counted clipping per stage", target, targetS), Big, tb, x - 2, 12);
                    g.DrawString(_stage != null
                            ? string.Format("On {0}: {1} of driving (your last run).", _stage, Clock(_drivingS))
                            : "On a 7-minute stage (no runs yet).", Body, mb, x, 38);

                    // ---- the stage as a strip, with the target's worth of counted moments spread over it ----
                    var strip = new RectangleF(x, 66, w, 26);
                    using (var sb = new SolidBrush(Color.FromArgb(60, Theme.TorqueOk))) g.FillRectangle(sb, strip);
                    var rnd = new Random(7);
                    using (var rb = new SolidBrush(Theme.Bad))
                        for (int i = 0; i < moments; i++)
                        {
                            double t = (i + 0.5 + (rnd.NextDouble() - 0.5) * 0.8) / moments * _drivingS;
                            float mx = strip.Left + (float)(t / _drivingS * strip.Width);
                            g.FillRectangle(rb, mx, strip.Top, Math.Max(2f, (float)(moment / _drivingS * strip.Width)), strip.Height);
                        }
                    double tick = _drivingS > 600 ? 120 : 60;
                    for (double t = 0; t <= _drivingS + 0.1; t += tick)
                    {
                        float tx = strip.Left + (float)(t / _drivingS * strip.Width);
                        g.DrawString(Clock(t), Small, fb, Math.Min(tx - 2, strip.Right - 26), strip.Bottom + 2);
                    }
                    g.DrawString(string.Format(
                            "Red: {0:0.0} s in all, as about {1} moments of {2:0.00} s (a burst or a long clip). The rest of the stage, " +
                            "{3:0.0} %, the motor has room above the force you feel.",
                            targetS, moments, moment, 100 - target),
                        Body, mb, new RectangleF(x, strip.Bottom + 20, w, 36));

                    // ---- gauge in seconds: raise / green zone / lower ----
                    float gy = 196;
                    g.DrawString("What MozaHud advises, by counted clipping on this stage:", Bold, tb, x, gy - 22);
                    double max = Math.Max(targetS * 2, 1);
                    Func<double, float> gx = s => x + (float)(Math.Min(s, max) / max * w);
                    var gauge = new RectangleF(x, gy, w, 14);
                    using (var blue = new SolidBrush(Color.FromArgb(110, Theme.Blue))) g.FillRectangle(blue, gauge.Left, gy, gx(lowS) - gauge.Left, gauge.Height);
                    using (var green = new SolidBrush(Color.FromArgb(150, Theme.Good))) g.FillRectangle(green, gx(lowS), gy, gx(targetS) - gx(lowS), gauge.Height);
                    using (var red = new SolidBrush(Color.FromArgb(110, Theme.Bad))) g.FillRectangle(red, gx(targetS), gy, gauge.Right - gx(targetS), gauge.Height);
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                    {
                        g.DrawString(string.Format("under {0:0.0} s ({1:0.0} %)\nraise the gain", lowS, low), Small, mb,
                            new RectangleF(gauge.Left, gy + 17, gx(lowS) - gauge.Left, 30), sf);
                        g.DrawString(string.Format("{0:0.0}–{1:0.0} s\ngood", lowS, targetS), Small, mb,
                            new RectangleF(gx(lowS) - 30, gy + 17, gx(targetS) - gx(lowS) + 60, 30), sf);
                        g.DrawString(string.Format("over {0:0.0} s ({1:0.0} %)\nlower the gain", targetS, target), Small, mb,
                            new RectangleF(gx(targetS), gy + 17, gauge.Right - gx(targetS), 30), sf);
                    }

                    // ---- what counts as driving time ----
                    g.DrawString(
                        "• Driving time is the time over 5 km/h, away from the soft lock: the start, the finish and stops don't count.\n" +
                        "• Only counted clipping adds up: lone spikes (amber) don't. A per cent stays fair between a 3- and a 12-minute stage.\n" +
                        string.Format("• Long clips count ×{0:0.00}: {1:0.0} s counted is {2:0.0} s of long clips, or {1:0.0} s of bursts, or a mix.",
                            _rule.LongWeight, targetS, targetS / _rule.LongWeight),
                        Small, mb, new RectangleF(x, gy + 58, w, 70));
                }
            }
        }

        /// <summary>The latest runs re-judged with the rule being edited: counted clipping (thick), all clipping (thin), target zone.</summary>
        sealed class RunsPanel : Control
        {
            readonly List<StageRun> _list = new List<StageRun>();
            readonly List<Analysis> _a = new List<Analysis>();
            ClipRule _rule;
            const int MaxRows = 8, RowH = 22;
            static readonly Font Small = new Font("Segoe UI", 8.25f);
            static readonly Font Bold = new Font("Segoe UI Semibold", 8.25f);

            public RunsPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            public string LatestTitle { get { return _list.Count > 0 ? _list[0].Title : null; } }
            public double LatestDrivingS { get { return _a.Count > 0 ? _a[0].DrivingPoints / 10.0 : 0; } }

            public void Load()
            {
                _list.Clear();
                foreach (var r in StageStore.List())
                {
                    StageStore.EnsurePoints(r);
                    if (r.Points.Count(p => p.Speed >= 5 && !p.Lock) < 200) continue;
                    _list.Add(r);
                    if (_list.Count == MaxRows) break;
                }
                SetRule(_rule);
            }

            public void SetRule(ClipRule r)
            {
                _rule = r;
                _a.Clear();
                foreach (var run in _list) _a.Add(Analysis.Of(run, r));
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                Card(g, this);
                if (_list.Count == 0)
                {
                    using (var mb = new SolidBrush(Theme.Muted))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("No runs yet: drive a stage and they show up here.", Small, mb, ClientRectangle, sf);
                    return;
                }

                float nameW = 214, valueW = 112;
                var bars = new RectangleF(14 + nameW, 28, Width - 28 - nameW - valueW, _list.Count * RowH);
                double max = Math.Max(_rule.TargetPct * 2, 3);
                foreach (var a in _a) max = Math.Max(max, a.ClipPct);
                max = Math.Ceiling(max);
                Func<double, float> X = v => bars.Left + (float)(Math.Min(v, max) / max * bars.Width);

                // zone, target, scale
                double low = Math.Round(_rule.TargetPct * Analysis.ZoneFraction, 1);
                using (var zb = new SolidBrush(Color.FromArgb(40, Theme.Good))) g.FillRectangle(zb, X(low), bars.Top - 4, X(_rule.TargetPct) - X(low), bars.Height + 4);
                using (var pen = new Pen(Theme.Good, 1f) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, X(_rule.TargetPct), bars.Top - 6, X(_rule.TargetPct), bars.Bottom);
                using (var gb = new SolidBrush(Theme.Good)) g.DrawString("target " + _rule.TargetPct.ToString("0.0") + " %", Small, gb, X(_rule.TargetPct) + 3, 7);
                using (var fb = new SolidBrush(Theme.Faint))
                using (var grid = new Pen(Theme.Grid))
                    for (int v = 0; v <= max; v += max > 8 ? 2 : 1)
                    {
                        g.DrawLine(grid, X(v), bars.Bottom, X(v), bars.Bottom + 4);
                        g.DrawString(v + " %", Small, fb, X(v) - 8, bars.Bottom + 4);
                    }

                using (var tb = new SolidBrush(Theme.Text))
                using (var mb = new SolidBrush(Theme.Muted))
                using (var allBrush = new SolidBrush(Color.FromArgb(150, Theme.Muted)))
                using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                    for (int i = 0; i < _list.Count; i++)
                    {
                        var r = _list[i]; var a = _a[i];
                        float y = bars.Top + i * RowH;
                        g.DrawString(r.Title + (r.Gain.HasValue ? " · " + r.Gain : ""), Small, tb, new RectangleF(14, y + 3, nameW - 76, 16), sf);
                        g.DrawString(r.Start.ToString("d MMM HH:mm"), Small, mb, new RectangleF(14 + nameW - 74, y + 3, 72, 16), sf);
                        g.FillRectangle(allBrush, bars.Left, y + 13, X(a.ClipPct) - bars.Left, 3);
                        using (var cb = new SolidBrush(Theme.VerdictColor(a.Verdict)))
                            g.FillRectangle(cb, bars.Left, y + 5, Math.Max(1.5f, X(a.CountedPct) - bars.Left), 8);
                        g.DrawString(a.CountedPct.ToString("0.0") + " %", Small, mb, bars.Right + 8, y + 3);
                        string advice = a.SuggestedGain.HasValue ? "→ " + a.SuggestedGain : a.ChangePct != 0 ? (a.ChangePct > 0 ? "+" : "−") + Math.Abs(a.ChangePct) + " %" : a.Verdict == Verdict.Good ? "OK" : "";
                        using (var vb = new SolidBrush(Theme.VerdictColor(a.Verdict)))
                        using (var rf = new StringFormat { Alignment = StringAlignment.Far })
                            g.DrawString(advice, Bold, vb, new RectangleF(Width - 62, y + 3, 50, 16), rf);
                    }

                // legend
                float ly = Height - 22, lx = 14;
                using (var mb = new SolidBrush(Theme.Muted))
                {
                    using (var b = new SolidBrush(Theme.Good)) g.FillRectangle(b, lx, ly + 4, 14, 8);
                    g.DrawString("counted (colour = advice)", Small, mb, lx + 18, ly); lx += 170;
                    using (var b = new SolidBrush(Color.FromArgb(150, Theme.Muted))) g.FillRectangle(b, lx, ly + 7, 14, 3);
                    g.DrawString("all clipping", Small, mb, lx + 18, ly); lx += 96;
                    using (var b = new SolidBrush(Color.FromArgb(40, Theme.Good))) g.FillRectangle(b, lx, ly + 2, 14, 12);
                    g.DrawString("green zone", Small, mb, lx + 18, ly); lx += 88;
                    g.DrawString("→ suggested gain", Small, mb, lx, ly);
                }
            }
        }
    }
}
