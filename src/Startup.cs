using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MozaHud
{
    /// <summary>"Start with Windows" (HKCU Run key) and "Start minimized" (settings.ini), shared by the tray menu and the window.</summary>
    public static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "MozaHud";
        const string MinimizedKey = "start_minimized";

        public static event Action Changed;

        public static bool WithWindows
        {
            get
            {
                try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(ValueName) != null; }
                catch { return false; }
            }
        }

        public static bool Minimized
        {
            get { double d; return Settings.TryGet(MinimizedKey, out d) && d != 0; }
        }

        public static void SetWithWindows(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(ValueName, Command());
                    else k.DeleteValue(ValueName, false);
                }
            }
            catch (Exception ex) { Log.Write("Start with Windows failed: " + ex.Message); }
            Raise();
        }

        public static void SetMinimized(bool on)
        {
            if (on == Minimized) return;
            Settings.Set(MinimizedKey, on ? 1 : 0);
            Raise();
        }

        /// <summary>
        /// At launch: older versions registered "--tray" for Windows startup (always without the window). Keep that
        /// behaviour by turning it into the "Start minimized" setting, and point the Run entry at this exe.
        /// </summary>
        public static void Migrate()
        {
            try
            {
                string current;
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) current = k == null ? null : k.GetValue(ValueName) as string;
                if (current == null) return;
                double d;
                if (current.Contains("--tray") && !Settings.TryGet(MinimizedKey, out d)) Settings.Set(MinimizedKey, 1);
                if (current != Command())
                    using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.SetValue(ValueName, Command());
            }
            catch (Exception ex) { Log.Write("Checking the startup entry failed: " + ex.Message); }
        }

        static string Command() { return "\"" + Application.ExecutablePath + "\""; }

        static void Raise() { var h = Changed; if (h != null) h(); }
    }
}
