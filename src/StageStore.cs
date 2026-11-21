using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MozaHud
{
    /// <summary>One 100 ms point of a stage run.</summary>
    public struct StagePoint
    {
        public float T;        // seconds since the run started
        public float Tq;       // smoothed torque, signed (Nm)
        public float TqMax;    // highest raw |torque| in this 100 ms (Nm)
        public bool Clip;      // a raw sample hit the base's limit
        public bool Lock;      // wheel at the rotation limit (soft lock): clipping here is not FFB clipping
        public float Wheel;    // steering position -1 … +1 of the base's rotation range
        public float Speed;    // km/h
        public float TMot, TMos;   // °C (0 = unknown)
    }

    public sealed class StageRun
    {
        public string FilePath;
        public DateTime Start, End;
        public string Track = "", Stage = "", Car = "", GainSource = "", EndReason = "", StageTime = "", Base = "";
        public string StageId = "";   // StageSwitch's "Rally/Stage" ids, e.g. "MonteCarlo/Sisteron"
        public int? Gain;
        public int RotationDeg;
        public double MaxNm = 5.5;
        public List<StagePoint> Points = new List<StagePoint>();
        public bool PointsLoaded;

        public string Title { get { return !string.IsNullOrEmpty(Stage) ? Stage : (string.IsNullOrEmpty(Track) ? "Unknown stage" : Track); } }
        public TimeSpan Duration { get { return End > Start ? End - Start : TimeSpan.Zero; } }

        Analysis _analysis;
        double _analysedFor = -1;

        /// <summary>The analysis against the current target-clipping setting (recomputed when it changes).</summary>
        public Analysis Analyse()
        {
            double target = Settings.TargetClipPct;
            if (_analysis == null || _analysedFor != target) { _analysis = Analysis.Of(this, target); _analysedFor = target; }
            return _analysis;
        }

        public void Invalidate() { _analysis = null; }
    }

    public enum Verdict { NotEnoughData, Lower, SlightlyLower, Good, Raise }

    /// <summary>
    /// FFB gain advice from a stage run. Only points while driving (over 5 km/h) and not at the soft lock count.
    /// The clipping setting is a target with a green zone below it (from 60 % of the target up to the target):
    /// above the target → lower the gain; below the zone → raise it, so no feel is left on the table.
    /// The suggested gain (in steps of 1) is the highest whose predicted clipping stays a little under the target.
    /// Prediction: the run's raw torque peaks scaled by new gain / old gain (AC Rally's FFB scales linearly with the
    /// gain; checked on runs of the same stage at two gains). Going up this is exact for every peak below the limit;
    /// going down, how far the clipped peaks went past the limit is estimated from how fast the peaks just below it thin out.
    /// </summary>
    public sealed class Analysis
    {
        public int DrivingPoints, ClipPoints, LockClipPoints, LockPoints;
        public int ClipEpisodes;                 // separate clipping moments (consecutive clipped 0.1 s count as one)
        public double LongestClipS, AvgClipS;    // longest / average clipping moment in seconds (0.1 s resolution)
        public double ClipPct, TargetPct, ZoneLowPct, P95Nm, P99RawNm, MaxSmoothNm, AvgNm, MaxMotor, MaxMosfet;
        public Verdict Verdict;
        public int ChangePct;          // recommended change in % of the current gain (negative = lower); 0 = keep
        public int? SuggestedGain;
        public double PredictedPct;    // expected clipping after the recommended change
        public bool Capped;            // the change was limited to MaxRaisePct / MaxLowerPct
        public string Headline = "", Detail = "";
        public string Hint = "";       // small optional note

        const double DrivingKmh = 5;
        public const double ZoneFraction = 0.6;   // green from 60 % of the target up to the target
        public const double AimMargin = 0.3;      // suggestions aim this many points under the target
        public const int MaxRaisePct = 15;        // per recommendation: one careful run shouldn't cause a big jump
        public const int MaxLowerPct = 25;
        public const int MaxGain = 200;           // AC Rally's FFB gain range is 0–200
        const int RaiseMinPoints = 600;           // 60 s of driving before suggesting more gain
        const double ClipMarginNm = 0.15;         // as TorqueWindow: within this of the limit counts as clipping
        const double TailFitNm = 1.0;             // peaks this far below the limit set the estimated tail

        float[] _peaks;            // raw peak of every driving 0.1 s that did not clip
        double _clipNm, _tailRate;

        /// <summary>Expected clipping (% of the driving time) with the gain multiplied by k.</summary>
        public double Predict(double k)
        {
            if (DrivingPoints == 0 || k <= 0) return 0;
            if (k >= 1)
            {
                // everything that clipped still clips; a peak below the limit clips once peak × k reaches it
                double thr = _clipNm / k;
                int n = ClipPoints;
                foreach (var p in _peaks) if (p >= thr) n++;
                return 100.0 * n / DrivingPoints;
            }
            // lower: only clipped moments can still clip, the ones that went past the limit by 1/k or more
            return ClipPct * Math.Exp(-_tailRate * (_clipNm / k - _clipNm));
        }

        public static Analysis Of(StageRun run, double target)
        {
            var a = new Analysis { TargetPct = target, ZoneLowPct = Math.Round(target * ZoneFraction, 1) };
            var smooth = new List<double>();
            var raw = new List<double>();
            var peaks = new List<float>();
            double sum = 0;
            bool hasSpeed = run.Points.Any(p => p.Speed > 0);   // without speed data, all stage time counts
            int streak = 0, longest = 0;
            Action endStreak = () =>
            {
                if (streak == 0) return;
                a.ClipEpisodes++;
                longest = Math.Max(longest, streak);
                streak = 0;
            };
            foreach (var p in run.Points)
            {
                if (p.TMot > a.MaxMotor) a.MaxMotor = p.TMot;
                if (p.TMos > a.MaxMosfet) a.MaxMosfet = p.TMos;
                if (p.Lock) { a.LockPoints++; if (p.Clip) a.LockClipPoints++; endStreak(); continue; }
                if (hasSpeed && p.Speed < DrivingKmh) { endStreak(); continue; }
                a.DrivingPoints++;
                if (p.Clip) { a.ClipPoints++; streak++; } else { endStreak(); peaks.Add(p.TqMax); }
                double s = Math.Abs(p.Tq);
                smooth.Add(s); raw.Add(p.TqMax); sum += s;
                if (s > a.MaxSmoothNm) a.MaxSmoothNm = s;
            }
            endStreak();
            // one point = 0.1 s
            a.LongestClipS = longest / 10.0;
            a.AvgClipS = a.ClipEpisodes > 0 ? a.ClipPoints / 10.0 / a.ClipEpisodes : 0;
            if (a.DrivingPoints < 200)   // under 20 s of driving
            {
                a.Verdict = Verdict.NotEnoughData;
                a.Headline = "Not enough driving to judge";
                a.Detail = "At least 20 seconds of driving are needed.";
                return a;
            }
            smooth.Sort(); raw.Sort();
            a.P95Nm = smooth[(int)(0.95 * (smooth.Count - 1))];
            a.P99RawNm = raw[(int)(0.99 * (raw.Count - 1))];
            a.AvgNm = sum / a.DrivingPoints;
            a.ClipPct = 100.0 * a.ClipPoints / a.DrivingPoints;

            // prediction inputs
            a._peaks = peaks.ToArray();
            a._clipNm = run.MaxNm - ClipMarginNm;
            int nearLimit = a.ClipPoints + peaks.Count(p => p >= a._clipNm - TailFitNm);
            a._tailRate = a.ClipPoints > 0 ? Math.Max(0.5, Math.Log((double)nearLimit / a.ClipPoints) / TailFitNm) : 0;

            // Search in steps of 1. Without a known gain, work on a base of 100 and only give the change in %.
            int? gain = run.Gain.HasValue && run.Gain.Value > 0 ? run.Gain : null;
            int baseGain = gain ?? 100;
            double aim = Math.Max(a.ZoneLowPct, target - AimMargin);
            string zone = string.Format("{0:0.0}–{1:0.0} %", a.ZoneLowPct, target);

            if (a.ClipPct > target + 0.0001)
            {
                double excess = a.ClipPct - target;
                int floor = (int)Math.Ceiling(baseGain * (1 - MaxLowerPct / 100.0));
                int to = floor;
                for (int g = baseGain - 1; g >= floor; g--)
                    if (a.Predict((double)g / baseGain) <= aim) { to = g; break; }
                a.Capped = to == floor && a.Predict((double)to / baseGain) > aim;
                a.SetChange(to, baseGain, gain);
                a.Verdict = excess >= 1.5 ? Verdict.Lower : Verdict.SlightlyLower;
                a.Headline = a.Verdict == Verdict.Lower ? "Too strong: lower the FFB gain" : "A bit strong: lower the FFB gain slightly";
                a.Detail = string.Format(
                    "Clipping {0:0.0} %, above your target {1:0.0} %: the motor is at its limit there and " +
                    "road and car details are lost. {2}", a.ClipPct, target, a.Outcome());
                if (a.Capped) a.Hint = string.Format("MozaHud lowers at most {0} % at a time: drive the stage again to check.", MaxLowerPct);
            }
            else if (a.ClipPct < a.ZoneLowPct - 0.0001)
            {
                string low = string.Format("Clipping only {0:0.0} %, under your target zone ({1}): ", a.ClipPct, zone);
                if (gain.HasValue && gain.Value >= MaxGain)
                {
                    a.Verdict = Verdict.Raise;
                    a.Headline = "Room to spare, but the gain is at its maximum";
                    a.Detail = low + "the motor has force you aren't feeling.";
                    a.Hint = "The game's gain is at its maximum (200): raise the base's FFB strength in Pit House if you want more force.";
                }
                else if (a.DrivingPoints < RaiseMinPoints)
                {
                    a.Verdict = Verdict.Good;
                    a.Headline = "Good so far: no sign of too much force";
                    a.Detail = low + "there may be room for more gain.";
                    a.Hint = "Drive at least a minute for a raise suggestion.";
                }
                else
                {
                    int ceiling = Math.Min(gain.HasValue ? MaxGain : int.MaxValue, (int)Math.Floor(baseGain * (1 + MaxRaisePct / 100.0)));
                    int to = baseGain;
                    for (int g = ceiling; g > baseGain; g--)
                        if (a.Predict((double)g / baseGain) <= aim) { to = g; break; }
                    if (to == baseGain)
                    {
                        a.Verdict = Verdict.Good;
                        a.Headline = "Good: the FFB gain fits this stage";
                        a.Detail = low + string.Format("but even one step more gain would take it to about {0:0.0} %.",
                            a.Predict((double)(baseGain + 1) / baseGain));
                    }
                    else
                    {
                        a.Capped = to == ceiling && ceiling < (gain.HasValue ? MaxGain : int.MaxValue);
                        a.SetChange(to, baseGain, gain);
                        a.Verdict = Verdict.Raise;
                        a.Headline = "Room to spare: raise the FFB gain";
                        a.Detail = low + "the motor has force you aren't feeling. " + a.Outcome();
                        if (a.Capped) a.Hint = string.Format("MozaHud raises at most {0} % at a time: drive the stage again to see if more fits.", MaxRaisePct);
                    }
                }
            }
            else
            {
                a.Verdict = Verdict.Good;
                a.Headline = "Good: the FFB gain fits this stage";
                a.Detail = string.Format("Clipping {0:0.0} %, in your target zone ({1}): only the heaviest moments touch the limit " +
                                         "and little force is left unused.", a.ClipPct, zone);
            }
            if (a.LockClipPoints > 0)
                a.Detail += string.Format(" Soft-lock time not counted ({0:0.0} s at the rotation limit).", a.LockPoints / 10.0);
            return a;
        }

        void SetChange(int to, int baseGain, int? gain)
        {
            ChangePct = (int)Math.Round((to / (double)baseGain - 1) * 100);
            if (ChangePct == 0) ChangePct = to > baseGain ? 1 : -1;
            PredictedPct = Predict((double)to / baseGain);
            if (gain.HasValue) SuggestedGain = to;
        }

        string Outcome()
        {
            return SuggestedGain.HasValue
                ? string.Format("At {0} it would be about {1:0.0} %.", SuggestedGain.Value, PredictedPct)
                : string.Format("About {0}{1} % gain would bring it to about {2:0.0} %.", ChangePct > 0 ? "+" : "−", Math.Abs(ChangePct), PredictedPct);
        }
    }

    /// <summary>
    /// Stage runs as CSV files in %APPDATA%\MozaHud\stages (header lines "# key: value", then 10 points per second).
    /// Keeps at most MaxRuns runs and none older than MaxAgeDays.
    /// </summary>
    public static class StageStore
    {
        public const int MaxRuns = 100;
        public const int MaxAgeDays = 60;
        public static readonly string Dir = Path.Combine(Log.Dir, "stages");
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Save(StageRun run)
        {
            Directory.CreateDirectory(Dir);
            if (run.FilePath == null) run.FilePath = Path.Combine(Dir, run.Start.ToString("yyyyMMdd-HHmmss", Inv) + ".csv");
            var sb = new StringBuilder();
            sb.Append("# MozaHud stage v1\n");
            sb.Append("# start: ").Append(run.Start.ToString("s", Inv)).Append('\n');
            sb.Append("# end: ").Append(run.End.ToString("s", Inv)).Append('\n');
            sb.Append("# track: ").Append(run.Track).Append('\n');
            sb.Append("# stage: ").Append(run.Stage).Append('\n');
            sb.Append("# car: ").Append(run.Car).Append('\n');
            sb.Append("# gain: ").Append(run.Gain.HasValue ? run.Gain.Value.ToString(Inv) : "").Append('\n');
            sb.Append("# gain_source: ").Append(run.GainSource).Append('\n');
            sb.Append("# rotation: ").Append(run.RotationDeg.ToString(Inv)).Append('\n');
            sb.Append("# max_nm: ").Append(run.MaxNm.ToString(Inv)).Append('\n');
            sb.Append("# end_reason: ").Append(run.EndReason).Append('\n');
            sb.Append("# stage_time: ").Append(run.StageTime).Append('\n');
            sb.Append("# base: ").Append(run.Base).Append('\n');
            sb.Append("# stage_id: ").Append(run.StageId).Append('\n');
            sb.Append("t,tq,tqmax,clip,lock,wheel,speed,tmot,tmos\n");
            foreach (var p in run.Points)
            {
                sb.Append(p.T.ToString("0.0", Inv)).Append(',')
                  .Append(p.Tq.ToString("0.00", Inv)).Append(',')
                  .Append(p.TqMax.ToString("0.0", Inv)).Append(',')
                  .Append(p.Clip ? '1' : '0').Append(',')
                  .Append(p.Lock ? '1' : '0').Append(',')
                  .Append(p.Wheel.ToString("0.000", Inv)).Append(',')
                  .Append(p.Speed.ToString("0", Inv)).Append(',')
                  .Append(p.TMot.ToString("0.0", Inv)).Append(',')
                  .Append(p.TMos.ToString("0.0", Inv)).Append('\n');
            }
            File.WriteAllText(run.FilePath, sb.ToString(), new UTF8Encoding(false));
            Cleanup();
        }

        /// <summary>All saved runs, newest first, headers only (points load on demand).</summary>
        public static List<StageRun> List()
        {
            Cleanup();
            var list = new List<StageRun>();
            if (!Directory.Exists(Dir)) return list;
            foreach (var f in Directory.GetFiles(Dir, "*.csv"))
            {
                try { var r = Load(f, false); if (r != null) list.Add(r); } catch { }
            }
            return list.OrderByDescending(r => r.Start).ToList();
        }

        public static StageRun Load(string path, bool points)
        {
            var run = new StageRun { FilePath = path };
            using (var rd = new StreamReader(path, Encoding.UTF8))
            {
                string line;
                while ((line = rd.ReadLine()) != null)
                {
                    if (line.StartsWith("#"))
                    {
                        int c = line.IndexOf(':');
                        if (c < 0) continue;
                        var key = line.Substring(1, c - 1).Trim();
                        var val = line.Substring(c + 1).Trim();
                        switch (key)
                        {
                            case "start": DateTime.TryParse(val, Inv, DateTimeStyles.None, out run.Start); break;
                            case "end": DateTime.TryParse(val, Inv, DateTimeStyles.None, out run.End); break;
                            case "track": run.Track = val; break;
                            case "stage": run.Stage = val; break;
                            case "car": run.Car = val; break;
                            case "gain": int g; if (int.TryParse(val, NumberStyles.Integer, Inv, out g)) run.Gain = g; break;
                            case "gain_source": run.GainSource = val; break;
                            case "rotation": int.TryParse(val, NumberStyles.Integer, Inv, out run.RotationDeg); break;
                            case "max_nm": double m; if (double.TryParse(val, NumberStyles.Float, Inv, out m)) run.MaxNm = m; break;
                            case "end_reason": run.EndReason = val; break;
                            case "stage_time": run.StageTime = val; break;
                            case "base": run.Base = val; break;
                            case "stage_id": run.StageId = val; break;
                        }
                        continue;
                    }
                    if (!points) break;
                    if (line.StartsWith("t,")) continue;
                    var f = line.Split(',');
                    if (f.Length < 9) continue;
                    run.Points.Add(new StagePoint
                    {
                        T = F(f[0]), Tq = F(f[1]), TqMax = F(f[2]), Clip = f[3] == "1", Lock = f[4] == "1",
                        Wheel = F(f[5]), Speed = F(f[6]), TMot = F(f[7]), TMos = F(f[8]),
                    });
                }
            }
            run.PointsLoaded = points;
            return run;
        }

        public static void EnsurePoints(StageRun run)
        {
            if (run.PointsLoaded || run.FilePath == null) return;
            var full = Load(run.FilePath, true);
            run.Points = full.Points;
            run.PointsLoaded = true;
            run.Invalidate();
        }

        static float F(string s)
        {
            float v; return float.TryParse(s, NumberStyles.Float, Inv, out v) ? v : 0;
        }

        public static void Delete(StageRun run)
        {
            try { if (run.FilePath != null) File.Delete(run.FilePath); } catch { }
        }

        /// <summary>Removes runs beyond MaxRuns (oldest first) and older than MaxAgeDays.</summary>
        public static void Cleanup()
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                var files = new DirectoryInfo(Dir).GetFiles("*.csv").OrderByDescending(f => f.Name).ToList();
                var cutoff = DateTime.Now.AddDays(-MaxAgeDays);
                for (int i = 0; i < files.Count; i++)
                {
                    if (i >= MaxRuns || files[i].LastWriteTime < cutoff)
                    {
                        try { files[i].Delete(); Log.Write("Cleanup: removed " + files[i].Name); } catch { }
                    }
                }
            }
            catch { }
        }
    }
}
