using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MozaHud
{
    /// <summary>
    /// Tray icon showing the motor temperature. A background thread keeps the base's COM port open, asks for the torque
    /// about every 25 ms and the temperatures every 2 s, and publishes everything to the performance counters.
    /// </summary>
    public sealed class TrayApp : ApplicationContext
    {
        const int TempEveryMs = 2000, PublishEveryMs = 100, UiEveryMs = 1000, RetryMs = 5000, TempStaleMs = 6000;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly NotifyIcon _tray = new NotifyIcon();
        readonly ToolStripMenuItem _status = new ToolStripMenuItem("Starting…") { Enabled = false };
        readonly ToolStripMenuItem _status2 = new ToolStripMenuItem("") { Enabled = false, Visible = false };
        readonly ToolStripMenuItem _setup = new ToolStripMenuItem("Set up Afterburner…");
        readonly ToolStripMenuItem _release = new ToolStripMenuItem("Release COM port (for Pit House)");
        readonly ToolStripMenuItem _autostart = new ToolStripMenuItem("Start with Windows");
        readonly SynchronizationContext _ui;
        readonly Thread _worker;
        readonly StageRecorder _recorder = new StageRecorder();
        readonly Icon _appIcon = MakeAppIcon();
        MainWindow _window;
        volatile string[] _baseLines = new string[0];
        volatile int _rotationDeg;
        volatile bool _stop, _paused;
        IntPtr _hIcon;

        public TrayApp(bool showWindow)
        {
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _recorder.Saved = run => { var w = _window; if (w != null) w.OnRunSaved(run); };

            var open = new ToolStripMenuItem("Open MozaHud") { Font = new Font(SystemFonts.MenuFont, FontStyle.Bold) };
            open.Click += (s, e) => ShowWindow();
            _tray.DoubleClick += (s, e) => ShowWindow();
            _setup.Click += (s, e) => RunSetup("--setup");
            _release.Click += (s, e) => { _paused = !_paused; _release.Checked = _paused; };
            _autostart.Click += (s, e) => SetAutostart(!_autostart.Checked);
            var remove = new ToolStripMenuItem("Remove Afterburner setup…");
            remove.Click += (s, e) => RunSetup("--uninstall");
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (s, e) => ExitThread();

            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] { open, new ToolStripSeparator(), _status, _status2, new ToolStripSeparator(),
                _setup, _release, _autostart, new ToolStripSeparator(), remove, exit });
            menu.Opening += (s, e) => RefreshMenu();
            _tray.ContextMenuStrip = menu;
            SetIcon(null, "MozaHud");
            _tray.Visible = true;
            RefreshMenu();

            if (!AfterburnerSetup.CfgHasSources())
                _tray.ShowBalloonTip(8000, "MozaHud",
                    "Right-click this icon → \"Set up Afterburner…\" to add the wheelbase values to Afterburner.",
                    ToolTipIcon.Info);

            _worker = new Thread(Poll) { IsBackground = true, Name = "moza-poll" };
            _worker.Start();
            if (showWindow) ShowWindow();
        }

        void ShowWindow()
        {
            if (_window == null || _window.IsDisposed) _window = new MainWindow(_recorder, () => _baseLines, _appIcon);
            _window.Show();
            if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
            _window.Activate();
        }

        static Icon MakeAppIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Color.FromArgb(255, 122, 26)))
                using (var path = Rounded(new Rectangle(0, 0, 31, 31), 7)) g.FillPath(b, path);
                using (var p = new Pen(Color.White, 2.6f) { LineJoin = LineJoin.Round })
                    g.DrawLines(p, new[] { new PointF(5, 22), new PointF(11, 14), new PointF(16, 19), new PointF(21, 9), new PointF(27, 15) });
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        void RefreshMenu()
        {
            bool done = AfterburnerSetup.CfgHasSources();
            _setup.Text = done ? "Set up Afterburner (done — redo…)" : "Set up Afterburner…";
            _autostart.Checked = AutostartEnabled();
            _release.Checked = _paused;
        }

        void RunSetup(string arg)
        {
            if (!AfterburnerSetup.RunElevated(arg))
                MessageBox.Show("Setup was cancelled or failed.", "MozaHud", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshMenu();
        }

        // ---------------- polling ----------------

        void Poll()
        {
            var clock = Stopwatch.StartNew();
            MozaLink link = null;
            string port = null, linkStatus = "Starting…", lastLogged = "";
            long nextTry = 0, nextTemp = 0, nextPublish = 0, nextUi = 0, nextFeedTry = 0;
            var window = new TorqueWindow();
            var temps = new Temperatures();
            long tempAt = -TempStaleMs;
            SensorFeed feed = null;
            string feedStatus = null;

            var baseInfo = new BaseInfo();
            var ceiling = new CeilingCheck();
            long nextIdentity = 0, nextCeilingCheck = 30000;
            // which base, what limit: the measured ceiling is remembered per model and max torque setting
            Action applyBase = () =>
            {
                double measured;
                string key = "measured_nm." + baseInfo.Model.Replace(' ', '_') + "." + baseInfo.PeakTorquePct;
                baseInfo.MeasuredNm = Settings.TryGet(key, out measured) && measured > 0 ? measured : (double?)null;
                window.MaxNm = baseInfo.MaxNm;
                _recorder.BaseName = baseInfo.Model;
                Log.Write("Base: " + baseInfo.Describe() + " (type '" + baseInfo.ProductType + "', hw '" + baseInfo.HardwareVersion +
                          "', fw " + baseInfo.Firmware + ", max torque " + baseInfo.PeakTorquePct + " %)");
            };

            var seen = new System.Collections.Generic.HashSet<byte>();
            Action<byte, int> onValue = (id, raw) =>
            {
                long now = clock.ElapsedMilliseconds;
                if (seen.Add(id)) Log.Write("first reply for id " + id + ": raw " + raw);
                switch (id)
                {
                    case MozaLink.IdTorque: window.Add(now, raw); ceiling.Add(raw); break;
                    case MozaLink.IdMcu: temps.Mcu = raw / 100.0; break;
                    case MozaLink.IdMosfet: temps.Mosfet = raw / 100.0; break;
                    case MozaLink.IdMotor: temps.Motor = raw / 100.0; tempAt = now; break;
                }
            };

            // base rotation setting (half degrees): the soft lock is at ±half of it
            Action<byte, int> onSetting = (id, raw) =>
            {
                if (id == MozaLink.SettingPeakTorque && raw > 0 && raw <= 100 && raw != baseInfo.PeakTorquePct)
                {
                    baseInfo.PeakTorquePct = raw;
                    ceiling.Reset();
                    if (baseInfo.Model.Length > 0) applyBase();
                    return;
                }
                if (id != MozaLink.SettingLimit || raw <= 0) return;
                int deg = raw * 2;
                if (deg != _rotationDeg) Log.Write("Base rotation: " + deg + "°");
                _rotationDeg = deg;
            };
            Action<byte, byte[]> onFrame = (group, payload) =>
            {
                if (group == MozaLink.GroupFirmware) { baseInfo.SetFirmware(payload); return; }
                if (baseInfo.AddText(group, payload)) { ceiling.Reset(); applyBase(); }
            };
            var wheel = new WheelAxis();
            long nextRotation = 0;
            bool recorderFailed = false;

            while (!_stop)
            {
                long ms = clock.ElapsedMilliseconds;

                if (_paused)
                {
                    if (link != null) { link.Dispose(); link = null; }
                    linkStatus = "Paused — COM port released for Pit House";
                    Thread.Sleep(50);
                }
                else if (link == null)
                {
                    if (ms >= nextTry)
                    {
                        nextTry = ms + RetryMs;
                        if (port == null) port = MozaLink.FindPort();
                        if (port == null) linkStatus = "No MOZA wheelbase found";
                        else
                        {
                            try
                            {
                                link = new MozaLink(port) { OnValue = onValue, OnSetting = onSetting, OnFrame = onFrame };
                                linkStatus = null; nextTemp = 0; nextIdentity = 0; nextRotation = 0;
                            }
                            catch (UnauthorizedAccessException) { linkStatus = port + " is in use by another program (Moza Pit House?)"; }
                            catch (Exception ex) { linkStatus = "Can't open " + port + ": " + ex.Message; port = null; }
                        }
                    }
                    Thread.Sleep(50);
                }
                else
                {
                    try
                    {
                        link.Request(MozaLink.IdTorque);
                        if (ms >= nextTemp)
                        {
                            nextTemp = ms + TempEveryMs;
                            link.Request(MozaLink.IdMcu); link.Request(MozaLink.IdMosfet); link.Request(MozaLink.IdMotor);
                        }
                        if (ms >= nextRotation) { nextRotation = ms + 5000; link.RequestRotationLimit(); link.RequestPeakTorque(); }
                        if (ms >= nextIdentity)
                        {
                            // until the model is known; afterwards once a minute (another base may be plugged in)
                            nextIdentity = ms + (baseInfo.Model.Length == 0 ? 5000 : 60000);
                            for (byte s = 1; s <= 2; s++)
                            {
                                link.RequestText(MozaLink.GroupProductType, s);
                                link.RequestText(MozaLink.GroupHardwareVersion, s);
                            }
                            link.RequestFirmware();
                        }
                        link.Pump(25);
                    }
                    catch (Exception ex)
                    {
                        // unplugged or renumbered: look for the port again
                        link.Dispose(); link = null; port = null;
                        linkStatus = "Lost the wheelbase: " + ex.Message;
                        nextTry = ms + 2000;
                    }
                }

                ms = clock.ElapsedMilliseconds;
                window.Trim(ms);

                // real torque ceiling from clipping, when it differs from rated × max torque setting
                if (ms >= nextCeilingCheck && baseInfo.Model.Length > 0)
                {
                    nextCeilingCheck = ms + 30000;
                    var plateau = ceiling.Plateau();
                    if (plateau.HasValue)
                    {
                        double expected = Math.Round((baseInfo.RatedNm > 0 ? baseInfo.RatedNm : BaseInfo.FallbackNm) * baseInfo.PeakTorquePct / 100.0, 1);
                        string key = "measured_nm." + baseInfo.Model.Replace(' ', '_') + "." + baseInfo.PeakTorquePct;
                        bool off = Math.Abs(plateau.Value - expected) > 0.3;
                        if (off && (!baseInfo.MeasuredNm.HasValue || Math.Abs(baseInfo.MeasuredNm.Value - plateau.Value) > 0.05))
                        {
                            Log.Write("Torque ceiling measured at " + plateau.Value + " Nm (expected " + expected + " Nm): using the measured value");
                            Settings.Set(key, plateau.Value);
                            applyBase();
                        }
                        else if (!off && baseInfo.MeasuredNm.HasValue)
                        {
                            Log.Write("Torque ceiling " + plateau.Value + " Nm matches the expected " + expected + " Nm again");
                            Settings.Set(key, 0);
                            applyBase();
                        }
                    }
                }

                if (ms < nextPublish) continue;
                nextPublish = ms + PublishEveryMs;

                var stats = window.Stats();
                var t = ms - tempAt < TempStaleMs ? temps : null;

                // shared memory for Afterburner's AIDA64 plugin
                if (feed == null && ms >= nextFeedTry)
                {
                    nextFeedTry = ms + 10000;
                    try { feed = new SensorFeed(); feedStatus = null; Log.Write("Publishing to " + SensorFeed.MapName); }
                    catch (Exception ex)
                    {
                        if (feedStatus == null) Log.Write("Can't create " + SensorFeed.MapName + ": " + ex.Message);
                        feedStatus = "Can't publish to Afterburner (is AIDA64 running?)";
                    }
                }
                if (feed != null)
                {
                    try { feed.SetTemperatures(t); feed.SetTorque(stats); feed.Publish(); }
                    catch (Exception ex) { Log.Write("Publishing failed: " + ex.Message); feed.Dispose(); feed = null; }
                }
                if (linkStatus != lastLogged) { lastLogged = linkStatus; Log.Write("Status: " + (linkStatus ?? "connected to " + port)); }

                // stage recording (AC Rally telemetry + base values), one point per 100 ms
                try { _recorder.Tick(window.TakeBucket(), t, wheel.Read(), _rotationDeg, window.MaxNm); recorderFailed = false; }
                catch (Exception ex) { if (!recorderFailed) Log.Write("Recorder error: " + ex); recorderFailed = true; }

                if (ms >= nextUi)
                {
                    nextUi = ms + UiEveryMs;
                    string line1, line2 = null;
                    if (linkStatus != null) line1 = linkStatus;
                    else
                    {
                        line1 = t != null ? t.ToString() : "Waiting for temperatures…";
                        line2 = stats.ToString();
                    }
                    if (feedStatus != null) line2 = (line2 == null ? "" : line2 + "  ") + feedStatus;
                    _baseLines = new[] { baseInfo.Describe(), line1, line2, _rotationDeg > 0 ? "Rotation " + _rotationDeg + "°" : null };
                    var tShow = t == null ? null : new Temperatures { Mcu = t.Mcu, Mosfet = t.Mosfet, Motor = t.Motor };
                    var st = stats;
                    _ui.Post(_ => Show(tShow, st, line1, line2), null);
                }
            }
            if (link != null) link.Dispose();
            if (feed != null) feed.Dispose();
        }

        void Show(Temperatures t, TorqueStats s, string line1, string line2)
        {
            _status.Text = line1;
            _status2.Text = line2 ?? "";
            _status2.Visible = line2 != null;
            // NotifyIcon.Text on .NET Framework throws above 63 characters: keep it compact.
            string tip;
            if (t != null || s.Valid)
                tip = string.Format("Motor {0}° MOS {1}° MCU {2}°\n{3}",
                    t == null ? "–" : t.Motor.ToString("0.0"),
                    t == null ? "–" : t.Mosfet.ToString("0.0"),
                    t == null ? "–" : t.Mcu.ToString("0"),
                    s.Valid ? string.Format("Torque {0:0.0} Nm · clip {1:0} %", s.NowNm, s.ClipPct) : "Torque –");
            else
                tip = "MozaHud: " + line1;
            try { SetIcon(t, tip.Length > 63 ? tip.Substring(0, 63) : tip); } catch { }
        }

        // ---------------- tray icon: motor temperature as a number ----------------

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        void SetIcon(Temperatures t, string tip)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                Color bg = t == null ? Color.FromArgb(90, 90, 90)
                    : t.Motor >= 70 ? Color.FromArgb(200, 40, 40)
                    : t.Motor >= 55 ? Color.FromArgb(220, 130, 20)
                    : Color.FromArgb(30, 140, 70);
                using (var b = new SolidBrush(bg))
                using (var path = Rounded(new Rectangle(0, 0, 31, 31), 7))
                    g.FillPath(b, path);
                var text = t == null ? "–" : Math.Round(t.Motor).ToString("0");
                using (var f = new Font("Segoe UI", text.Length > 2 ? 13f : 17f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(text, f, Brushes.White, new RectangleF(0, 1, 32, 32), sf);

                var h = bmp.GetHicon();
                var old = _tray.Icon;
                _tray.Icon = Icon.FromHandle(h);
                if (old != null) old.Dispose();
                if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
                _hIcon = h;
            }
            _tray.Text = tip;
        }

        static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------------- start with Windows ----------------

        static bool AutostartEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("MozaHud") != null;
        }

        static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("MozaHud", "\"" + Application.ExecutablePath + "\" --tray");
                else k.DeleteValue("MozaHud", false);
            }
        }

        protected override void ExitThreadCore()
        {
            _stop = true;
            _worker.Join(3000);
            if (_window != null && !_window.IsDisposed) _window.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
            base.ExitThreadCore();
        }
    }
}
