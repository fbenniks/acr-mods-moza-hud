using System;
using System.Threading;
using System.Windows.Forms;

namespace MozaHud
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var arg = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            // Elevated one-time actions, started from the tray menu.
            if (arg == "--setup" || arg == "--uninstall")
            {
                try
                {
                    var msg = arg == "--setup" ? AfterburnerSetup.Install() : AfterburnerSetup.Uninstall();
                    MessageBox.Show(msg, "MozaHud", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Failed: " + ex.Message, "MozaHud", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }

            // Read once and print (for testing from a shell: MozaHud.exe --probe | more).
            if (arg == "--probe")
            {
                var port = MozaLink.FindPort();
                Console.WriteLine("Port: " + (port ?? "not found"));
                if (port != null)
                {
                    try
                    {
                        var t = MozaLink.ReadTemperatures(port);
                        Console.WriteLine(t == null ? "No answer" : t.ToString());

                        // one second of torque samples
                        var win = new TorqueWindow();
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        using (var link = new MozaLink(port))
                        {
                            link.OnValue = (id, raw) => { if (id == MozaLink.IdTorque) win.Add(clock.ElapsedMilliseconds, raw); };
                            while (clock.ElapsedMilliseconds < 1000) { link.Request(MozaLink.IdTorque); link.Pump(25); }
                        }
                        Console.WriteLine(win.Stats() + "  (" + win.Count + " samples in 1 s)");
                    }
                    catch (Exception ex) { Console.WriteLine("Error: " + ex.GetType().Name + ": " + ex.Message); }
                }
                Console.WriteLine("Afterburner cfg: " + (AfterburnerSetup.CfgPath() ?? "not found") +
                                  (AfterburnerSetup.CfgHasSources() ? " (has MOZA sources)" : " (no MOZA sources yet)"));
                return 0;
            }

            bool created;
            using (new Mutex(true, "MozaHud_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("MozaHud is already running (check the system tray).", "MozaHud");
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // A UI glitch must not take the tray app (and the overlay values) down.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log.Write("UI error: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Fatal: " + e.ExceptionObject);
                Log.Start();
                Application.Run(new TrayApp());
            }
            return 0;
        }
    }
}
