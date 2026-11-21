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
        volatile bool _stop, _paused;
        IntPtr _hIcon;

        public TrayApp()
        {
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

            _setup.Click += (s, e) => RunSetup("--setup");
            _release.Click += (s, e) => { _paused = !_paused; _release.Checked = _paused; };
            _autostart.Click += (s, e) => SetAutostart(!_autostart.Checked);
            var remove = new ToolStripMenuItem("Remove Afterburner setup…");
            remove.Click += (s, e) => RunSetup("--uninstall");
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (s, e) => ExitThread();

            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] { _status, _status2, new ToolStripSeparator(), _setup, _release, _autostart,
                new ToolStripSeparator(), remove, exit });
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

            var seen = new System.Collections.Generic.HashSet<byte>();
            Action<byte, int> onValue = (id, raw) =>
            {
                long now = clock.ElapsedMilliseconds;
                if (seen.Add(id)) Log.Write("first reply for id " + id + ": raw " + raw);
                switch (id)
                {
                    case MozaLink.IdTorque: window.Add(now, raw); break;
                    case MozaLink.IdMcu: temps.Mcu = raw / 100.0; break;
                    case MozaLink.IdMosfet: temps.Mosfet = raw / 100.0; break;
                    case MozaLink.IdMotor: temps.Motor = raw / 100.0; tempAt = now; break;
                }
            };

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
                            try { link = new MozaLink(port) { OnValue = onValue }; linkStatus = null; nextTemp = 0; }
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
                    s.Valid ? string.Format("Torque {0:0.0} Nm · clip {1:0} %", s.PeakNm, s.ClipPct) : "Torque –");
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
                if (on) k.SetValue("MozaHud", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("MozaHud", false);
            }
        }

        protected override void ExitThreadCore()
        {
            _stop = true;
            _worker.Join(3000);
            _tray.Visible = false;
            _tray.Dispose();
            if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
            base.ExitThreadCore();
        }
    }
}
