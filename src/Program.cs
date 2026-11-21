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

                        // identity + one second of torque samples
                        var win = new TorqueWindow();
                        var info = new BaseInfo();
                        int rotation = 0;
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        using (var link = new MozaLink(port))
                        {
                            link.OnValue = (id, raw) => { if (id == MozaLink.IdTorque) win.Add(clock.ElapsedMilliseconds, raw); };
                            link.OnFrame = (g, p) => { if (g == MozaLink.GroupFirmware) info.SetFirmware(p); else info.AddText(g, p); };
                            link.OnSetting = (id, raw) => { if (id == MozaLink.SettingPeakTorque) info.PeakTorquePct = raw; else if (id == MozaLink.SettingLimit) rotation = raw * 2; };
                            for (byte s = 1; s <= 2; s++) { link.RequestText(MozaLink.GroupProductType, s); link.RequestText(MozaLink.GroupHardwareVersion, s); }
                            link.RequestFirmware(); link.RequestPeakTorque(); link.RequestRotationLimit();
                            while (clock.ElapsedMilliseconds < 1000) { link.Request(MozaLink.IdTorque); link.Pump(25); }
                        }
                        Console.WriteLine("Base: " + info.Describe() + "  rotation " + rotation + "°");
                        Console.WriteLine("  product type '" + info.ProductType + "', hardware '" + info.HardwareVersion + "', firmware " + info.Firmware + ", max torque setting " + info.PeakTorquePct + " %");
                        Console.WriteLine(win.Stats() + "  (" + win.Count + " samples in 1 s)");
                    }
                    catch (Exception ex) { Console.WriteLine("Error: " + ex.GetType().Name + ": " + ex.Message); }
                }
                Console.WriteLine("Afterburner cfg: " + (AfterburnerSetup.CfgPath() ?? "not found") +
                                  (AfterburnerSetup.CfgHasSources() ? " (has MOZA sources)" : " (no MOZA sources yet)"));
                return 0;
            }

            // FFB gain advice for every saved run, plus the predicted clipping a few gain steps around it
            // (for checking the advice from a shell: MozaHud.exe --advice | more).
            if (arg == "--advice")
            {
                Settings.Load();
                foreach (var run in StageStore.List())
                {
                    StageStore.EnsurePoints(run);
                    var a = run.Analyse();
                    Console.WriteLine(run.Start.ToString("yyyy-MM-dd HH:mm") + "  " + run.Title + "  ·  " + run.Car + "  ·  gain " + (run.Gain.HasValue ? run.Gain.ToString() : "?"));
                    Console.WriteLine("  " + a.Verdict + ": clipping " + a.ClipPct.ToString("0.00") + " %" +
                        (a.ChangePct != 0 ? "  →  " + (a.SuggestedGain.HasValue ? a.SuggestedGain.ToString() : a.ChangePct.ToString("+0;-0") + " %") +
                                            " (about " + a.PredictedPct.ToString("0.00") + " %" + (a.Capped ? ", capped" : "") + ")" : ""));
                    Console.WriteLine("  " + a.Headline + " | " + a.Detail + (a.Hint == "" ? "" : " | " + a.Hint));
                    if (run.Gain.HasValue && run.Gain.Value > 0 && a.Verdict != Verdict.NotEnoughData)
                    {
                        var sb = new System.Text.StringBuilder("  predicted:");
                        foreach (int d in new[] { -20, -10, -5, 5, 10, 20 })
                        {
                            int g = run.Gain.Value + d;
                            if (g > 0 && g <= Analysis.MaxGain) sb.Append("  ").Append(g).Append(": ").Append(a.Predict(g / (double)run.Gain.Value).ToString("0.00")).Append(" %");
                        }
                        Console.WriteLine(sb);
                    }
                }
                return 0;
            }

            // Turn a tools\torque-log.ps1 capture into a stage run (for trying the window without the game).
            if (arg == "--import-log" && args.Length > 1)
            {
                var run = ImportTorqueLog(args[1]);
                StageStore.Save(run);
                Console.WriteLine("Saved " + run.FilePath + " (" + run.Points.Count + " points)");
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
                Settings.Load();
                Application.Run(new TrayApp(showWindow: Array.IndexOf(args, "--tray") < 0));
            }
            return 0;
        }

        static StageRun ImportTorqueLog(string path)
        {
            var lines = System.IO.File.ReadAllLines(path);
            var head = lines[0].Split(',');
            int ti = Array.IndexOf(head, "t_ms"), qi = Array.IndexOf(head, "mot_torque");
            var win = new TorqueWindow();
            var run = new StageRun
            {
                Start = System.IO.File.GetLastWriteTime(path), Stage = "Imported torque log", Track = "", Car = System.IO.Path.GetFileName(path),
                EndReason = "imported", RotationDeg = 450, PointsLoaded = true,
            };
            long next = 100;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 1; i < lines.Length; i++)
            {
                var f = lines[i].Split(',');
                long ms = (long)double.Parse(f[ti], inv);
                int raw;
                if (!int.TryParse(f[qi], out raw)) continue;
                win.Add(ms, raw);
                if (ms >= next)
                {
                    next += 100;
                    var b = win.TakeBucket();
                    run.Points.Add(new StagePoint { T = ms / 1000f, Tq = (float)b.SmoothNm, TqMax = (float)b.MaxRawNm, Clip = b.Clipped > 0, Speed = 50, TMot = 25 + ms / 20000f, TMos = 30 + ms / 15000f });
                }
            }
            run.End = run.Start.AddSeconds(run.Points.Count / 10.0);
            run.Start = run.End.AddSeconds(-run.Points.Count / 10.0);
            return run;
        }
    }
}
