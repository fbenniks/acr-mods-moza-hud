using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MozaHud
{
    public static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(23, 25, 31);
        public static readonly Color Panel = Color.FromArgb(31, 34, 42);
        public static readonly Color PanelAlt = Color.FromArgb(38, 42, 52);
        public static readonly Color Hover = Color.FromArgb(44, 48, 60);
        public static readonly Color Border = Color.FromArgb(52, 57, 70);
        public static readonly Color Text = Color.FromArgb(232, 234, 240);
        public static readonly Color Muted = Color.FromArgb(146, 153, 170);
        public static readonly Color Faint = Color.FromArgb(96, 102, 118);
        public static readonly Color Accent = Color.FromArgb(255, 122, 26);
        public static readonly Color Good = Color.FromArgb(72, 199, 116);
        public static readonly Color Bad = Color.FromArgb(232, 72, 72);
        public static readonly Color Warn = Color.FromArgb(240, 180, 40);
        public static readonly Color Blue = Color.FromArgb(90, 160, 255);
        public static readonly Color Grid = Color.FromArgb(40, 44, 54);
        public static readonly Color Track = Color.FromArgb(60, 65, 80);

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color VerdictColor(Verdict v)
        {
            switch (v)
            {
                case Verdict.Lower: return Bad;
                case Verdict.SlightlyLower: return Warn;
                case Verdict.Good: return Good;
                default: return Faint;
            }
        }
    }

    /// <summary>
    /// Two stacked graphs over the stage time: torque (top) and temperatures (bottom).
    /// Torque: smoothed |torque| line, raw 100 ms peaks as a faint band, the base's limit dashed, red marks where it
    /// clipped, grey where the wheel was at the rotation limit (soft lock; not counted as clipping).
    /// Mouse wheel zooms around the cursor, drag pans, double-click shows the whole stage.
    /// </summary>
    public sealed class StageChart : Control
    {
        List<StagePoint> _pts = new List<StagePoint>();
        double _maxNm = 5.5;
        double _viewFrom, _viewTo = 10;   // seconds
        int _hoverX = -1;
        int _dragX = -1; double _dragFrom, _dragTo;

        static readonly Font Small = new Font("Segoe UI", 8.25f);
        static readonly Font Label = new Font("Segoe UI Semibold", 8.5f);
        const int PadL = 44, PadR = 14, PadT = 26, Gap = 46, PadB = 22;   // PadT / Gap leave room for unit + legend

        public StageChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
        }

        /// <summary>
        /// newRun: show the whole run. Otherwise (live updates of the same run) the view follows the end when it was
        /// showing the end: the whole run stays whole, a zoomed view scrolls along.
        /// </summary>
        public void SetData(List<StagePoint> pts, double maxNm, bool newRun)
        {
            double prevEnd = End;
            bool atEnd = _viewTo >= prevEnd - 0.5;
            bool whole = _viewFrom <= 0.01 && atEnd;
            _pts = pts ?? new List<StagePoint>();
            _maxNm = maxNm;
            double end = End;
            if (newRun || whole) { _viewFrom = 0; _viewTo = end; }
            else if (atEnd) { double span = _viewTo - _viewFrom; _viewTo = end; _viewFrom = Math.Max(0, end - span); }
            Invalidate();
        }

        double End { get { return Math.Max(10, _pts.Count > 0 ? _pts[_pts.Count - 1].T : 0); } }

        Rectangle TorqueRect { get { int h = Height - PadT - Gap - PadB; return new Rectangle(PadL, PadT, Math.Max(10, Width - PadL - PadR), Math.Max(10, (int)(h * 0.62))); } }
        Rectangle TempRect
        {
            get
            {
                var t = TorqueRect;
                int y = t.Bottom + Gap;
                return new Rectangle(PadL, y, t.Width, Math.Max(10, Height - PadB - y));
            }
        }

        float X(double t, Rectangle r) { return r.Left + (float)((t - _viewFrom) / (_viewTo - _viewFrom) * r.Width); }
        double T(int x, Rectangle r) { return _viewFrom + (x - r.Left) / (double)r.Width * (_viewTo - _viewFrom); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var tr = TorqueRect; var pr = TempRect;
            if (_pts.Count < 2)
            {
                using (var b = new SolidBrush(Theme.Muted))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("No data yet", Label, b, ClientRectangle, sf);
                return;
            }

            // ---- per-pixel-column aggregation of the visible points ----
            int cols = tr.Width;
            var tq = new float[cols]; var tqMax = new float[cols]; var mot = new float[cols]; var mos = new float[cols];
            var clip = new bool[cols]; var lockc = new bool[cols]; var lockClip = new bool[cols]; var has = new bool[cols];
            int i0 = Math.Max(0, LowerBound(_viewFrom) - 1);
            for (int i = i0; i < _pts.Count; i++)
            {
                var p = _pts[i];
                if (p.T > _viewTo) break;
                int c = (int)((p.T - _viewFrom) / (_viewTo - _viewFrom) * cols);
                if (c < 0 || c >= cols) continue;
                has[c] = true;
                tq[c] = Math.Max(tq[c], Math.Abs(p.Tq));
                tqMax[c] = Math.Max(tqMax[c], p.TqMax);
                if (p.TMot > 0) mot[c] = Math.Max(mot[c], p.TMot);
                if (p.TMos > 0) mos[c] = Math.Max(mos[c], p.TMos);
                if (p.Lock) { lockc[c] = true; if (p.Clip) lockClip[c] = true; }
                else if (p.Clip) clip[c] = true;
            }

            // ---- torque ----
            double yMax = Math.Ceiling(_maxNm + 0.5);
            Func<double, float> ty = v => tr.Bottom - (float)(Math.Min(v, yMax) / yMax * tr.Height);
            DrawFrame(g, tr, 0, yMax, _maxNm > 14 ? 5 : _maxNm > 7 ? 2 : 1, "Nm");
            using (var lockBrush = new SolidBrush(Color.FromArgb(46, 140, 150, 170)))
                for (int c = 0; c < cols; c++) if (lockc[c]) g.FillRectangle(lockBrush, tr.Left + c, tr.Top, 1, tr.Height);
            // per column: 0 normal (orange), 1 clipping (red), 2 at the soft lock (grey) — same colours as the marks on top
            // exactly the columns whose 100 ms points clipped / were at the soft lock (no widening)
            var state = new int[cols];
            for (int c = 0; c < cols; c++) state[c] = lockc[c] ? 2 : clip[c] ? 1 : 0;
            float pointPx = (float)(cols / ((_viewTo - _viewFrom) * 10));   // width of one 100 ms point
            DrawTorque(g, tq, tqMax, has, state, cols, tr.Left, ty, tr.Bottom, Math.Max(1.5f, pointPx));
            using (var pen = new Pen(Theme.Bad, 1f) { DashStyle = DashStyle.Dash })
            {
                float ly = ty(_maxNm);
                g.DrawLine(pen, tr.Left, ly, tr.Right, ly);
                using (var b = new SolidBrush(Theme.Bad)) g.DrawString("limit " + _maxNm.ToString("0.0") + " Nm", Small, b, tr.Right - 78, tr.Top - 19);
            }
            using (var clipBrush = new SolidBrush(Theme.Bad))
            using (var lockClipBrush = new SolidBrush(StateColors[2]))
                for (int c = 0; c < cols; c++)
                {
                    if (clip[c]) g.FillRectangle(clipBrush, tr.Left + c, tr.Top, 1.5f, 7);
                    else if (lockClip[c]) g.FillRectangle(lockClipBrush, tr.Left + c, tr.Top, 1.5f, 7);
                }
            DrawLegend(g, tr, new[] { "Torque", "Raw peaks", "Clipping", "Soft lock" },
                new[] { Theme.Accent, Color.FromArgb(110, Theme.Accent), Theme.Bad, StateColors[2] });

            // ---- temperatures ----
            float lo = float.MaxValue, hi = float.MinValue;
            for (int c = 0; c < cols; c++)
            {
                if (mot[c] > 0) { lo = Math.Min(lo, mot[c]); hi = Math.Max(hi, mot[c]); }
                if (mos[c] > 0) { lo = Math.Min(lo, mos[c]); hi = Math.Max(hi, mos[c]); }
            }
            if (lo > hi) { lo = 20; hi = 40; }
            double tlo = Math.Floor((lo - 2) / 5) * 5, thi = Math.Ceiling((hi + 2) / 5) * 5;
            if (thi - tlo < 10) thi = tlo + 10;
            Func<double, float> py = v => pr.Bottom - (float)((v - tlo) / (thi - tlo) * pr.Height);
            DrawFrame(g, pr, tlo, thi, thi - tlo > 30 ? 10 : 5, "°C");
            var hasMot = new bool[cols]; var hasMos = new bool[cols];
            for (int c = 0; c < cols; c++) { hasMot[c] = mot[c] > 0; hasMos[c] = mos[c] > 0; }
            DrawSeries(g, mos, hasMos, cols, pr.Left, py, Theme.Blue, 1.4f, false, 0);
            DrawSeries(g, mot, hasMot, cols, pr.Left, py, Theme.Warn, 1.6f, false, 0);
            DrawLegend(g, pr, new[] { "Motor", "MOSFET" }, new[] { Theme.Warn, Theme.Blue });

            // ---- time axis ----
            double span = _viewTo - _viewFrom;
            double step = span > 1200 ? 300 : span > 600 ? 120 : span > 240 ? 60 : span > 90 ? 30 : span > 40 ? 10 : 5;
            using (var b = new SolidBrush(Theme.Muted))
                for (double t = Math.Ceiling(_viewFrom / step) * step; t <= _viewTo; t += step)
                {
                    float x = X(t, tr);
                    g.DrawString(StageRecorder.FormatTime(t), Small, b, x - 12, pr.Bottom + 4);
                }

            // ---- hover ----
            if (_hoverX >= tr.Left && _hoverX <= tr.Right) DrawHover(g, tr, pr);
        }

        int LowerBound(double t)
        {
            int lo = 0, hi = _pts.Count;
            while (lo < hi) { int mid = (lo + hi) / 2; if (_pts[mid].T < t) lo = mid + 1; else hi = mid; }
            return lo;
        }

        void DrawFrame(Graphics g, Rectangle r, double lo, double hi, double step, string unit)
        {
            using (var grid = new Pen(Theme.Grid))
            using (var b = new SolidBrush(Theme.Faint))
            {
                for (double v = lo; v <= hi + 1e-6; v += step)
                {
                    float y = r.Bottom - (float)((v - lo) / (hi - lo) * r.Height);
                    g.DrawLine(grid, r.Left, y, r.Right, y);
                    g.DrawString(v.ToString("0"), Small, b, 4, y - 7);
                }
                using (var ub = new SolidBrush(Theme.Muted)) g.DrawString(unit, Label, ub, 4, r.Top - 20);
            }
        }

        static void DrawSeries(Graphics g, float[] v, bool[] has, int cols, int x0, Func<double, float> y, Color color, float width, bool fill, float baseY)
        {
            var pts = new List<PointF>();
            using (var pen = new Pen(color, width) { LineJoin = LineJoin.Round })
            using (var brush = new SolidBrush(color))
            {
                Action flush = () =>
                {
                    if (pts.Count >= 2)
                    {
                        if (fill)
                        {
                            var poly = new List<PointF>(pts) { new PointF(pts[pts.Count - 1].X, baseY), new PointF(pts[0].X, baseY) };
                            g.FillPolygon(brush, poly.ToArray());
                        }
                        else g.DrawLines(pen, pts.ToArray());
                    }
                    pts.Clear();
                };
                for (int c = 0; c < cols; c++)
                {
                    if (!has[c]) continue;   // columns without a point are bridged
                    pts.Add(new PointF(x0 + c, y(v[c])));
                }
                flush();
            }
        }

        static readonly Color[] StateColors = { Theme.Accent, Theme.Bad, Color.FromArgb(150, 160, 178) };

        /// <summary>
        /// Raw-peak band (orange) with a red/grey bar exactly where a 100 ms point clipped / was at the soft lock,
        /// from the bottom up to that point's raw peak; then the smoothed torque line, which is red/grey only between
        /// two consecutive points that both clipped / were at the soft lock. Nothing next to them is coloured.
        /// </summary>
        static void DrawTorque(Graphics g, float[] tq, float[] tqMax, bool[] has, int[] state, int cols, int x0,
                               Func<double, float> y, float baseY, float barW)
        {
            var band = new List<PointF>();
            for (int c = 0; c < cols; c++) if (has[c]) band.Add(new PointF(x0 + c, y(tqMax[c])));
            if (band.Count >= 2)
                using (var brush = new SolidBrush(Color.FromArgb(70, StateColors[0])))
                {
                    var poly = new List<PointF>(band) { new PointF(band[band.Count - 1].X, baseY), new PointF(band[0].X, baseY) };
                    g.FillPolygon(brush, poly.ToArray());
                }

            using (var clipBar = new SolidBrush(Color.FromArgb(190, StateColors[1])))
            using (var lockBar = new SolidBrush(Color.FromArgb(150, StateColors[2])))
                for (int c = 0; c < cols; c++)
                {
                    if (!has[c] || state[c] == 0) continue;
                    float top = y(tqMax[c]);
                    g.FillRectangle(state[c] == 1 ? clipBar : lockBar, x0 + c - barW / 2, top, barW, baseY - top);
                }

            // line: runs of segments with the same colour; a segment is red/grey only if both ends are
            var pens = new[]
            {
                new Pen(StateColors[0], 1.6f) { LineJoin = LineJoin.Round },
                new Pen(StateColors[1], 2.2f) { LineJoin = LineJoin.Round },
                new Pen(StateColors[2], 2f) { LineJoin = LineJoin.Round },
            };
            try
            {
                var run = new List<PointF>();
                int runState = -1, prevState = -1;
                PointF? prev = null;
                for (int c = 0; c < cols; c++)
                {
                    if (!has[c]) continue;
                    var p = new PointF(x0 + c, y(tq[c]));
                    if (prev.HasValue)
                    {
                        int seg = state[c] != 0 && state[c] == prevState ? state[c] : 0;
                        if (seg != runState)
                        {
                            if (run.Count >= 2) g.DrawLines(pens[runState], run.ToArray());
                            run.Clear(); run.Add(prev.Value); runState = seg;
                        }
                        run.Add(p);
                    }
                    prev = p; prevState = state[c];
                }
                if (run.Count >= 2) g.DrawLines(pens[runState], run.ToArray());
            }
            finally { foreach (var pen in pens) pen.Dispose(); }
        }

        // Legend in the strip above the graph, after the unit label.
        static void DrawLegend(Graphics g, Rectangle r, string[] names, Color[] colors)
        {
            float x = r.Left + 8, y = r.Top - 19;
            using (var tb = new SolidBrush(Theme.Muted))
                for (int i = 0; i < names.Length; i++)
                {
                    using (var b = new SolidBrush(colors[i])) g.FillRectangle(b, x, y + 7, 10, 3);
                    g.DrawString(names[i], Small, tb, x + 13, y);
                    x += 22 + g.MeasureString(names[i], Small).Width;
                }
        }

        void DrawHover(Graphics g, Rectangle tr, Rectangle pr)
        {
            double t = T(_hoverX, tr);
            int i = Math.Min(_pts.Count - 1, LowerBound(t));
            if (i < 0) return;
            var p = _pts[i];
            float x = X(p.T, tr);
            using (var pen = new Pen(Color.FromArgb(120, Theme.Text)))
            {
                g.DrawLine(pen, x, tr.Top, x, tr.Bottom);
                g.DrawLine(pen, x, pr.Top, x, pr.Bottom);
            }
            var lines = new List<string>
            {
                StageRecorder.FormatTime(p.T) + "   " + p.Speed.ToString("0") + " km/h",
                "Torque " + Math.Abs(p.Tq).ToString("0.0") + " Nm   peak " + p.TqMax.ToString("0.0") + " Nm",
                "Wheel " + (p.Wheel * 100).ToString("0") + " %" + (p.Lock ? "  (soft lock)" : ""),
                p.Clip && !p.Lock ? "CLIPPING: a raw peak hit the limit in this 0.1 s" : null,
                "Motor " + p.TMot.ToString("0.0") + " °C   MOSFET " + p.TMos.ToString("0.0") + " °C",
            };
            var size = new SizeF(0, 0);
            lines.RemoveAll(l => l == null);
            foreach (var l in lines) { var s = g.MeasureString(l, Small); size = new SizeF(Math.Max(size.Width, s.Width), size.Height + s.Height); }
            var box = new RectangleF(x + 10, tr.Top + 22, size.Width + 14, size.Height + 10);
            if (box.Right > Width - 4) box.X = x - 10 - box.Width;
            using (var path = Theme.Round(box, 5))
            using (var bg = new SolidBrush(Color.FromArgb(235, 20, 22, 28)))
            using (var border = new Pen(Theme.Border))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }
            float y = box.Top + 5;
            using (var b = new SolidBrush(Theme.Text))
                foreach (var l in lines) { g.DrawString(l, Small, b, box.Left + 7, y); y += g.MeasureString(l, Small).Height; }
        }

        // ---------------- mouse ----------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var tr = TorqueRect;
            if (_dragX >= 0)
            {
                double dt = (e.X - _dragX) / (double)tr.Width * (_dragTo - _dragFrom);
                Pan(_dragFrom - dt, _dragTo - dt);
            }
            _hoverX = e.X;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverX = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { _dragX = e.X; _dragFrom = _viewFrom; _dragTo = _viewTo; Focus(); }
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragX = -1; }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            _viewFrom = 0; _viewTo = End; Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_pts.Count < 2) return;
            var tr = TorqueRect;
            double at = T(e.X, tr);
            double factor = e.Delta > 0 ? 0.7 : 1 / 0.7;
            double from = at - (at - _viewFrom) * factor, to = at + (_viewTo - at) * factor;
            if (to - from < 5) return;
            Pan(from, to);
        }

        void Pan(double from, double to)
        {
            double end = End;
            double span = Math.Min(to - from, end);
            if (from < 0) from = 0;
            if (from + span > end) from = end - span;
            _viewFrom = Math.Max(0, from); _viewTo = _viewFrom + span;
            Invalidate();
        }
    }
}
