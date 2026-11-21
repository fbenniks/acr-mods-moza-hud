using System;
using System.Globalization;
using System.IO;

namespace MozaHud
{
    /// <summary>User settings in %APPDATA%\MozaHud\settings.ini (key=value lines).</summary>
    public static class Settings
    {
        static readonly string FilePath = Path.Combine(Log.Dir, "settings.ini");
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const double DefaultAllowedClipPct = 2.5;

        /// <summary>Clipping (% of driving time) that is still fine; above it MozaHud recommends lowering the gain.</summary>
        public static double AllowedClipPct = DefaultAllowedClipPct;

        public static event Action Changed;

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var val = line.Substring(eq + 1).Trim();
                    double d;
                    if (key == "allowed_clipping_pct" && double.TryParse(val, NumberStyles.Float, Inv, out d))
                        AllowedClipPct = Clamp(d);
                }
            }
            catch (Exception ex) { Log.Write("Reading settings failed: " + ex.Message); }
        }

        public static void SetAllowedClipPct(double v)
        {
            v = Clamp(v);
            if (Math.Abs(v - AllowedClipPct) < 0.001) return;
            AllowedClipPct = v;
            try
            {
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllText(FilePath, "allowed_clipping_pct=" + v.ToString("0.0", Inv) + Environment.NewLine);
            }
            catch (Exception ex) { Log.Write("Saving settings failed: " + ex.Message); }
            var h = Changed; if (h != null) h();
        }

        static double Clamp(double v) { return Math.Round(Math.Max(0, Math.Min(50, v)), 1); }
    }
}
