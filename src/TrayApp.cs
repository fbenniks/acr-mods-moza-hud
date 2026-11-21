using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MozaHud
{
    /// <summary>Tray icon showing the motor temperature; polls the wheelbase on a background thread.</summary>
    public sealed class TrayApp : ApplicationContext
    {
        const int PollMs = 2000;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly NotifyIcon _tray = new NotifyIcon();
        readonly ToolStripMenuItem _status = new ToolStripMenuItem("Starting…") { Enabled = false };
        readonly ToolStripMenuItem _setup = new ToolStripMenuItem("Set up Afterburner…");
        readonly ToolStripMenuItem _autostart = new ToolStripMenuItem("Start with Windows");
        readonly SynchronizationContext _ui;
        readonly Thread _worker;
        volatile bool _stop;
        IntPtr _hIcon;

        public TrayApp()
        {
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

            _setup.Click += (s, e) => RunSetup("--setup");
            _autostart.Click += (s, e) => SetAutostart(!_autostart.Checked);
            var remove = new ToolStripMenuItem("Remove Afterburner setup…");
            remove.Click += (s, e) => RunSetup("--uninstall");
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (s, e) => ExitThread();

            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] { _status, new ToolStripSeparator(), _setup, _autostart,
                new ToolStripSeparator(), remove, exit });
            menu.Opening += (s, e) => RefreshMenu();
            _tray.ContextMenuStrip = menu;
            SetIcon(null, "MozaHud");
            _tray.Visible = true;
            RefreshMenu();

            if (!Counters.Registered() || !AfterburnerSetup.CfgHasSources())
                _tray.ShowBalloonTip(8000, "MozaHud",
                    "Right-click this icon → \"Set up Afterburner…\" to add the wheelbase temperatures to Afterburner.",
                    ToolTipIcon.Info);

            _worker = new Thread(Poll) { IsBackground = true, Name = "moza-poll" };
            _worker.Start();
        }

        void RefreshMenu()
        {
            bool done = Counters.Registered() && AfterburnerSetup.CfgHasSources();
            _setup.Text = done ? "Set up Afterburner (done — redo…)" : "Set up Afterburner…";
            _autostart.Checked = AutostartEnabled();
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
            string port = null;
            Counters counters = null;
            DateTime nextFind = DateTime.MinValue;

            while (!_stop)
            {
                string status;
                Temperatures t = null;
                try
                {
                    if (port == null && DateTime.UtcNow >= nextFind)
                    {
                        port = MozaBase.FindPort();
                        nextFind = DateTime.UtcNow.AddSeconds(10);
                    }
                    if (port == null) status = "No MOZA wheelbase found";
                    else
                    {
                        t = MozaBase.Read(port);
                        status = t == null ? "Wheelbase on " + port + " did not answer" : t.ToString();
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    status = port + " is in use by another program (Moza Pit House?)";
                }
                catch (Exception ex)
                {
                    status = "Error on " + port + ": " + ex.Message;
                    port = null;   // the device may have been unplugged or renumbered; look again
                }

                if (counters == null && Counters.Registered())
                {
                    try { counters = new Counters(); } catch { }
                }
                if (counters != null)
                {
                    try { if (t != null) counters.Set(t); else counters.Clear(); }
                    catch { counters.Dispose(); counters = null; }
                }
                if (counters == null && t != null) status += "\n(Afterburner not set up yet)";

                var temps = t; var st = status;
                _ui.Post(_ => Show(temps, st), null);

                for (int i = 0; i < PollMs / 100 && !_stop; i++) Thread.Sleep(100);
            }
            if (counters != null) counters.Dispose();
        }

        void Show(Temperatures t, string status)
        {
            _status.Text = status.Replace("\n", "  ");
            var tip = "MozaHud — " + status;
            SetIcon(t, tip.Length > 127 ? tip.Substring(0, 127) : tip);
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
