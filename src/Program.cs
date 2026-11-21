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
                var port = MozaBase.FindPort();
                Console.WriteLine("Port: " + (port ?? "not found"));
                if (port != null)
                {
                    try
                    {
                        var t = MozaBase.Read(port);
                        Console.WriteLine(t == null ? "No answer" : t.ToString());
                    }
                    catch (Exception ex) { Console.WriteLine("Error: " + ex.GetType().Name + ": " + ex.Message); }
                }
                Console.WriteLine("Counters registered: " + Counters.Registered());
                Console.WriteLine("Afterburner cfg: " + (AfterburnerSetup.CfgPath() ?? "not found") +
                                  (AfterburnerSetup.CfgHasSources() ? " (has MOZA sources)" : ""));
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
                Application.Run(new TrayApp());
            }
            return 0;
        }
    }
}
