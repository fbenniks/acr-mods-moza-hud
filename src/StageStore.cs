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
        public string Track = "", Stage = "", Car = "", GainSource = "", EndReason = "";
        public int? Gain;
        public int RotationDeg;
        public double MaxNm = 5.5;
        public List<StagePoint> Points = new List<StagePoint>();
        public bool PointsLoaded;

        public string Title { get { return !string.IsNullOrEmpty(Stage) ? Stage : (string.IsNullOrEmpty(Track) ? "Unknown stage" : Track); } }
        public TimeSpan Duration { get { return End > Start ? End - Start : TimeSpan.Zero; } }

        Analysis _analysis;
        public Analysis Analyse() { return _analysis ?? (_analysis = Analysis.Of(this)); }
        public void Invalidate() { _analysis = null; }
    }

    public enum Verdict { NotEnoughData, Lower, SlightlyLower, Good, Raise }

    /// <summary>FFB gain advice from a stage run. Only points while driving (over 5 km/h) and not at the soft lock count.</summary>
    public sealed class Analysis
    {
        public int DrivingPoints, ClipPoints, LockClipPoints, LockPoints;
        public double ClipPct, P95Nm, P99RawNm, MaxSmoothNm, AvgNm, MaxMotor, MaxMosfet;
        public Verdict Verdict;
        public int ChangePct;          // + raise / - lower, in % of the current gain
        public int? SuggestedGain;
        public string Headline = "", Detail = "";

        const double DrivingKmh = 5;

        public static Analysis Of(StageRun run)
        {
            var a = new Analysis();
            var smooth = new List<double>();
            var raw = new List<double>();
            double sum = 0;
            foreach (var p in run.Points)
            {
                if (p.TMot > a.MaxMotor) a.MaxMotor = p.TMot;
                if (p.TMos > a.MaxMosfet) a.MaxMosfet = p.TMos;
                if (p.Lock) { a.LockPoints++; if (p.Clip) a.LockClipPoints++; continue; }
                if (p.Speed < DrivingKmh) continue;
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
            if (a.ClipPct >= 5)
            {
                a.Verdict = Verdict.Lower;
                a.ChangePct = -Math.Min(30, Math.Max(10, Round5(a.ClipPct * 1.5)));
                a.Headline = "Too strong: lower the FFB gain";
                a.Detail = string.Format("The motor was at its limit {0:0.0} % of the time while driving. Details in the road and car are lost there.", a.ClipPct);
            }
            else if (a.ClipPct >= 1.5)
            {
                a.Verdict = Verdict.SlightlyLower;
                a.ChangePct = a.ClipPct >= 3 ? -10 : -5;
                a.Headline = "A bit strong: lower the FFB gain slightly";
                a.Detail = string.Format("Clipping {0:0.0} % of the time while driving: mostly in the heaviest moments.", a.ClipPct);
            }
            else if (a.P99RawNm < 0.8 * max)
            {
                int up = Math.Min(40, Round5((0.92 * max / Math.Max(0.1, a.P99RawNm) - 1) * 100));
                if (up >= 5)
                {
                    a.Verdict = Verdict.Raise;
                    a.ChangePct = up;
                    a.Headline = "Headroom: you can raise the FFB gain";
                    a.Detail = string.Format("Even the strongest moments stayed at {0:0.0} Nm of {1:0.0} Nm ({2:0} %).", a.P99RawNm, max, 100 * a.P99RawNm / max);
                }
            }
            if (a.Verdict == Verdict.NotEnoughData)
            {
                a.Verdict = Verdict.Good;
                a.Headline = "Good: the FFB gain fits this stage";
                a.Detail = string.Format("Clipping {0:0.0} % of the time, strongest moments {1:0.0} Nm of {2:0.0} Nm.", a.ClipPct, a.P99RawNm, max);
            }

            if (a.ChangePct != 0 && run.Gain.HasValue)
            {
                int g = run.Gain.Value;
                int suggested = Math.Max(0, Math.Min(200, (int)(Math.Round(g * (1 + a.ChangePct / 100.0) / 5) * 5)));
                if (suggested != g) a.SuggestedGain = suggested;
                if (a.Verdict == Verdict.Raise && g >= 200)
                    a.Detail += " The game's gain is already at its maximum (200): raise the base's FFB strength in Pit House instead.";
                else if (a.Verdict == Verdict.Raise && suggested >= 200 && g < 200)
                    a.Detail += " That is the game's maximum (200).";
            }
            if (a.LockClipPoints > 0)
                a.Detail += string.Format(" Soft-lock time not counted ({0:0.0} s at the rotation limit).", a.LockPoints / 10.0);
            return a;
        }

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
