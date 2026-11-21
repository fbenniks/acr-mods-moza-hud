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
        public int? Gain;
        public int RotationDeg;
        public double MaxNm = 5.5;
        public List<StagePoint> Points = new List<StagePoint>();
        public bool PointsLoaded;

        public string Title { get { return !string.IsNullOrEmpty(Stage) ? Stage : (string.IsNullOrEmpty(Track) ? "Unknown stage" : Track); } }
        public TimeSpan Duration { get { return End > Start ? End - Start : TimeSpan.Zero; } }

        Analysis _analysis;
        double _analysedFor = -1;

        /// <summary>The analysis against the current allowed-clipping setting (recomputed when it changes).</summary>
        public Analysis Analyse()
        {
            double allowed = Settings.AllowedClipPct;
            if (_analysis == null || _analysedFor != allowed) { _analysis = Analysis.Of(this, allowed); _analysedFor = allowed; }
            return _analysis;
        }

        public void Invalidate() { _analysis = null; }
    }

    public enum Verdict { NotEnoughData, Lower, SlightlyLower, Good }

    /// <summary>
    /// FFB gain advice from a stage run. Only points while driving (over 5 km/h) and not at the soft lock count.
    /// Clipping above the allowed share (setting, default 2.5 %) → lower the gain, more the further above;
    /// at or below it → good (green), with at most a small hint (room to raise, or close to the limit).
    /// </summary>
    public sealed class Analysis
    {
        public int DrivingPoints, ClipPoints, LockClipPoints, LockPoints;
        public double ClipPct, AllowedPct, P95Nm, P99RawNm, MaxSmoothNm, AvgNm, MaxMotor, MaxMosfet;
        public Verdict Verdict;
        public int ChangePct;          // recommended change (lower), in % of the current gain; 0 when green
        public int? SuggestedGain;
        public string Headline = "", Detail = "";
        public string Hint = "";       // small optional recommendation when green

        const double DrivingKmh = 5;

        public static Analysis Of(StageRun run, double allowed)
        {
            var a = new Analysis { AllowedPct = allowed };
            var smooth = new List<double>();
            var raw = new List<double>();
            double sum = 0;
            bool hasSpeed = run.Points.Any(p => p.Speed > 0);   // without speed data, all stage time counts
            foreach (var p in run.Points)
            {
                if (p.TMot > a.MaxMotor) a.MaxMotor = p.TMot;
                if (p.TMos > a.MaxMosfet) a.MaxMosfet = p.TMos;
                if (p.Lock) { a.LockPoints++; if (p.Clip) a.LockClipPoints++; continue; }
                if (hasSpeed && p.Speed < DrivingKmh) continue;
                a.DrivingPoints++;
                if (p.Clip) a.ClipPoints++;
                double s = Math.Abs(p.Tq);
                smooth.Add(s); raw.Add(p.TqMax); sum += s;
                if (s > a.MaxSmoothNm) a.MaxSmoothNm = s;
            }
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

            double max = run.MaxNm;
            double excess = a.ClipPct - allowed;
            int? gain = run.Gain;
            if (excess > 0.0001)
            {
                // the further above the allowed share, the bigger the step: 0.6 points over → −5 %, 2.9 over → −10 %
                a.ChangePct = -Math.Min(30, Math.Max(5, Round5(excess * 2 + 5)));
                a.Verdict = excess >= 1.5 ? Verdict.Lower : Verdict.SlightlyLower;
                a.Headline = a.Verdict == Verdict.Lower ? "Too strong: lower the FFB gain" : "A bit strong: lower the FFB gain slightly";
                a.Detail = string.Format(
                    "Clipping {0:0.0} % of the driving time, above your allowed {1:0.0} %. Where it clips the motor is at its limit, " +
                    "so details of the road and the car are lost there.", a.ClipPct, allowed);
                if (gain.HasValue)
                {
                    int s = Scale(gain.Value, a.ChangePct);
                    if (s != gain.Value) a.SuggestedGain = s;
                }
            }
            else
            {
                a.Verdict = Verdict.Good;
                a.Headline = "Good: the FFB gain fits this stage";
                a.Detail = a.ClipPoints == 0
                    ? string.Format("No clipping while driving (you allow up to {0:0.0} %).", allowed)
                    : string.Format("Clipping {0:0.0} % of the driving time, within your allowed {1:0.0} %: only the heaviest moments touch the limit, " +
                                    "which costs little detail.", a.ClipPct, allowed);

                // small hints only
                int up = Math.Min(40, Round5((0.92 * max / Math.Max(0.1, a.P99RawNm) - 1) * 100));
                if (a.P99RawNm < 0.8 * max && a.ClipPct <= allowed / 2 && up >= 5)
                {
                    if (gain.HasValue && gain.Value >= 200)
                        a.Hint = "Room to spare, but the game's gain is at its maximum (200): raise the base's FFB strength in Pit House if you want more force.";
                    else if (gain.HasValue)
                        a.Hint = string.Format("Room to spare: about +{0} % ({1} → {2}) would still fit.", up, gain.Value, Scale(gain.Value, up));
                    else
                        a.Hint = string.Format("Room to spare: about +{0} % more gain would still fit.", up);
                }
                else if (excess > -0.5 && a.ClipPoints > 0)
                {
                    a.Hint = gain.HasValue
                        ? string.Format("Close to your limit: if it feels harsh, −5 % ({0} → {1}) would reduce the clipping.", gain.Value, Scale(gain.Value, -5))
                        : "Close to your limit: if it feels harsh, about −5 % gain would reduce the clipping.";
                }
            }
            if (a.LockClipPoints > 0)
                a.Detail += string.Format(" Soft-lock time not counted ({0:0.0} s at the rotation limit).", a.LockPoints / 10.0);
            return a;
        }

        static int Scale(int gain, int pct) { return Math.Max(0, Math.Min(200, (int)(Math.Round(gain * (1 + pct / 100.0) / 5) * 5))); }

        static int Round5(double v) { return (int)(Math.Round(v / 5) * 5); }
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
