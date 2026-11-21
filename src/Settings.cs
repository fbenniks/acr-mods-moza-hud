using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MozaHud
{
    /// <summary>User settings in %APPDATA%\MozaHud\settings.ini (key=value lines).</summary>
    public static class Settings
    {
        static readonly string FilePath = Path.Combine(Log.Dir, "settings.ini");
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly object Gate = new object();
        static readonly Dictionary<string, string> Values = new Dictionary<string, string>();

        public const double DefaultTargetClipPct = 2.5;

        /// <summary>Target clipping (% of driving time): above it MozaHud recommends less gain, under 60 % of it more gain (ini key kept as allowed_clipping_pct).</summary>
        public static double TargetClipPct = DefaultTargetClipPct;

        public static event Action Changed;

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                lock (Gate)
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) Values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                double d;
                if (TryGet("allowed_clipping_pct", out d)) TargetClipPct = Clamp(d);
            }
            catch (Exception ex) { Log.Write("Reading settings failed: " + ex.Message); }
        }

        public static void SetTargetClipPct(double v)
        {
            v = Clamp(v);
            if (Math.Abs(v - TargetClipPct) < 0.001) return;
            TargetClipPct = v;
            Set("allowed_clipping_pct", v);
            var h = Changed; if (h != null) h();
        }

        public static bool TryGet(string key, out double value)
        {
            string s;
            lock (Gate) if (!Values.TryGetValue(key, out s)) { value = 0; return false; }
            return double.TryParse(s, NumberStyles.Float, Inv, out value);
        }

        public static void Set(string key, double value)
        {
            lock (Gate)
            {
                Values[key] = value.ToString("0.0##", Inv);
                try
                {
                    Directory.CreateDirectory(Log.Dir);
                    File.WriteAllLines(FilePath, Values.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));
                }
                catch (Exception ex) { Log.Write("Saving settings failed: " + ex.Message); }
            }
        }

        static double Clamp(double v) { return Math.Round(Math.Max(0, Math.Min(50, v)), 1); }
    }
}
