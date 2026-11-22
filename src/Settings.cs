using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MozaHud
{
    /// <summary>
    /// Which clipping counts for the FFB advice, and how much of it is the target.
    /// A clip is one stretch at the limit (consecutive clipped torque samples). Clips up to SpikeMaxS long are spikes;
    /// longer clips (long clips) always count, weighted by LongWeight. Spikes count only in a burst: at least Count clips within WindowS
    /// seconds (first start to last end).
    /// </summary>
    public struct ClipRule
    {
        public double SpikeMaxS;   // clips up to this long are spikes; longer ones count on their own
        public int Count;          // clips needed for a burst …
        public double WindowS;     // … within this many seconds
        public double LongWeight;  // a long clip counts as its time × this (spikes in a burst count 1×)
        public double TargetPct;   // target: share of the driving time in counted clipping

        public string Name;        // preset name (null for a custom rule)

        public static readonly ClipRule Strict = new ClipRule { Name = "Very little spiking", SpikeMaxS = 0.1, Count = 2, WindowS = 3.0, LongWeight = 2.0, TargetPct = 0.5 };
        public static readonly ClipRule Balanced = new ClipRule { Name = "Balanced", SpikeMaxS = 0.2, Count = 3, WindowS = 2.0, LongWeight = 1.5, TargetPct = 1.0 };
        public static readonly ClipRule Aggressive = new ClipRule { Name = "Aggressive", SpikeMaxS = 0.3, Count = 3, WindowS = 1.5, LongWeight = 1.25, TargetPct = 2.0 };
        public static readonly ClipRule HighGain = new ClipRule { Name = "High gain", SpikeMaxS = 0.4, Count = 4, WindowS = 1.5, LongWeight = 1.0, TargetPct = 3.0 };
        public static readonly ClipRule[] Presets = { Strict, Balanced, Aggressive, HighGain };

        /// <summary>The preset with these values, or null.</summary>
        public string PresetName { get { var me = this; var p = Presets.FirstOrDefault(x => x.SameAs(me)); return p.Name; } }

        /// <summary>Times are set in steps of this (MozaHud measures clips to about 0.03 s: 30–40 torque samples per second).</summary>
        public const double StepS = 0.05;

        static double Snap(double v) { return Math.Round(Math.Round(v / StepS) * StepS, 2); }

        /// <summary>The shortest window that can hold Count spikes of one step with one step between them.</summary>
        public static double MinWindowS(int count) { return Snap((2 * count - 1) * StepS); }

        /// <summary>Clamped to sensible values: spikes 0.05–2.00 s and the window in steps of 0.05 s, 2–20 clips, a window that can hold them (up to 10 s), target 0–50 %.</summary>
        public ClipRule Normalized()
        {
            var r = this;
            r.SpikeMaxS = Snap(Math.Max(StepS, Math.Min(2, r.SpikeMaxS)));
            r.Count = Math.Max(2, Math.Min(20, r.Count));
            r.WindowS = Snap(Math.Max(MinWindowS(r.Count), Math.Min(10, r.WindowS)));
            r.LongWeight = Math.Round(Math.Max(1, Math.Min(5, r.LongWeight == 0 ? 1.5 : r.LongWeight)), 2);
            r.TargetPct = Math.Round(Math.Max(0, Math.Min(50, r.TargetPct)), 1);
            return r;
        }

        public bool SameAs(ClipRule o)
        {
            return Math.Abs(SpikeMaxS - o.SpikeMaxS) < 0.0001 && Count == o.Count && Math.Abs(WindowS - o.WindowS) < 0.0001 &&
                   Math.Abs(LongWeight - o.LongWeight) < 0.0001 && Math.Abs(TargetPct - o.TargetPct) < 0.001;
        }

        /// <summary>Short description: the preset name, or e.g. "3+ clips in 2.00 s, or over 0.20 s".</summary>
        public string Describe()
        {
            return PresetName ?? string.Format(CultureInfo.CurrentCulture, "{0}+ clips in {1:0.00} s, or over {2:0.00} s ×{3:0.##}", Count, WindowS, SpikeMaxS, LongWeight);
        }
    }

    /// <summary>User settings in %APPDATA%\MozaHud\settings.ini (key=value lines).</summary>
    public static class Settings
    {
        static readonly string FilePath = Path.Combine(Log.Dir, "settings.ini");
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly object Gate = new object();
        static readonly Dictionary<string, string> Values = new Dictionary<string, string>();

        /// <summary>The clipping rule and target for the advice.</summary>
        public static ClipRule Rule = ClipRule.Balanced;

        /// <summary>Target clipping (% of driving time, counted by the rule): above it MozaHud recommends less gain, under 60 % of it more gain.</summary>
        public static double TargetClipPct { get { return Rule.TargetPct; } }

        /// <summary>Goes up on every rule change (analyses cache against it).</summary>
        public static int RuleVersion { get; private set; }

        public static event Action Changed;

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    lock (Gate)
                        foreach (var line in File.ReadAllLines(FilePath))
                        {
                            int eq = line.IndexOf('=');
                            if (eq > 0) Values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                        }
                double count, window, target, spike, weight;
                if (TryGet("clip_rule_count", out count))
                {
                    var r = Rule;
                    r.Count = (int)count;
                    if (TryGet("clip_rule_window_s", out window)) r.WindowS = window;
                    if (TryGet("clip_rule_spike_s", out spike)) r.SpikeMaxS = spike;
                    if (TryGet("clip_rule_long_weight", out weight)) r.LongWeight = weight;
                    if (TryGet("clip_target_pct", out target)) r.TargetPct = target;
                    Rule = r.Normalized();
                }
                // Older versions counted every clip against "allowed_clipping_pct"; that setting is no longer used.
            }
            catch (Exception ex) { Log.Write("Reading settings failed: " + ex.Message); }
            RuleVersion++;
        }

        public static void SetRule(ClipRule r)
        {
            r = r.Normalized();
            if (r.SameAs(Rule)) return;
            Rule = r;
            RuleVersion++;
            Set("clip_rule_spike_s", r.SpikeMaxS);
            Set("clip_rule_long_weight", r.LongWeight);
            Set("clip_rule_count", r.Count);
            Set("clip_rule_window_s", r.WindowS);
            Set("clip_target_pct", r.TargetPct);
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
    }
}
