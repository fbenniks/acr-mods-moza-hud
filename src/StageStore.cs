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
        public byte[] Samples; // every raw torque sample in this 100 ms, in order, as |torque| in 0.1 Nm (null in older runs)
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
        int _analysedFor = -1;

        /// <summary>The analysis with the current clipping rule (recomputed when it changes).</summary>
        public Analysis Analyse()
        {
            int v = Settings.RuleVersion;
            if (_analysis == null || _analysedFor != v) { _analysis = Analysis.Of(this, Settings.Rule); _analysedFor = v; }
            return _analysis;
        }

        public void Invalidate() { _analysis = null; }
    }

    public enum Verdict { NotEnoughData, Lower, SlightlyLower, Good, Raise }

    /// <summary>
    /// FFB gain advice from a stage run. Only points while driving (over 5 km/h) and not at the soft lock count.
    /// Which clipping counts is set by the clipping rule (see ClipRule): long clips count, spikes only in a burst of N
    /// clips within W seconds (lone spikes from a bump or kerb only cut the top off one hit; bursts and long clips flatten the
    /// force for a whole stretch). The share of the driving time in counted clipping is compared with the target, with
    /// a green zone below it (from 60 % of the target up to the target): above the target → lower the gain; below the
    /// zone → raise it, so no feel is left on the table.
    /// The suggested gain (in steps of 1) is the highest whose predicted counted clipping stays a little under the target.
    /// Prediction: the run's raw torque peaks scaled by new gain / old gain (AC Rally's FFB scales linearly with the
    /// gain; checked on runs of the same stage at two gains), then the rule applied again, so spikes that would join up
    /// into a burst are counted. Going up this is exact for every peak below the limit; going down, how far the clipped
    /// peaks went past the limit is estimated from how fast the peaks just below it thin out, and which clips stay is
    /// drawn at random (the average of a fixed set of draws, so the result is repeatable).
    /// </summary>
    public sealed class Analysis
    {
        public ClipRule Rule;
        public int DrivingPoints, ClipPoints, LockClipPoints, LockPoints;
        public int ClipEpisodes;                 // separate clips (runs of clipped samples)
        public double LongestClipS, AvgClipS;    // longest / average clip in seconds (sample resolution; 0.1 s in older runs)
        public double ClipPct;                   // all clipping, % of the driving time
        public double CountedPct;                // clipping that counts by the rule, % of the driving time
        public double TargetPct, ZoneLowPct, P95Nm, P99RawNm, MaxSmoothNm, AvgNm, MaxMotor, MaxMosfet;
        public bool[] Clipped;                   // per run point: hit the limit
        public bool[] Counted;                   // per run point: clipped and counted by the rule
        public double LimitNm;                   // the base's limit used (the saved one, or the measured one when that was wrong)
        public bool LimitCorrected;              // the saved limit was too low for this run's torque
        public Verdict Verdict;
        public int ChangePct;          // recommended change in % of the current gain (negative = lower); 0 = keep
        public int? SuggestedGain;
        public double PredictedPct;    // expected counted clipping after the recommended change
        public bool Capped;            // the change was limited to MaxRaisePct / MaxLowerPct
        public string Headline = "", Detail = "";
        public string Hint = "";       // small optional note

        const double DrivingKmh = 5;
        public const double ZoneFraction = 0.6;   // green from 60 % of the target up to the target
        public const double AimMargin = 0.3;      // suggestions aim this far under the target (at most 20 % of it)
        public const int MaxRaisePct = 15;        // per recommendation: one careful run shouldn't cause a big jump
        public const int MaxLowerPct = 25;
        public const int MaxGain = 400;           // StageSwitch's supported FFB gain maximum
        const int RaiseMinPoints = 600;           // 60 s of driving before suggesting more gain
        const double ClipMarginNm = 0.15;         // as TorqueWindow: within this of the limit counts as clipping
        const double TailFitNm = 1.0;             // peaks this far below the limit set the estimated tail
        const int Draws = 32;                     // random draws when predicting a lower gain

        // Prediction inputs, over the driving points in order. Each point holds its raw torque samples (Nm, in order;
        // older runs only have the point's peak, as one sample lasting the whole 0.1 s).
        float[] _t;                // start time of each driving point
        float[][] _s;              // its samples
        bool[] _join;              // its first sample directly follows the previous driving point's last one
        bool[] _ptClip;            // it clipped in this run
        int[] _clipPts, _clipPos;  // driving indices of the clipped points; per driving point its place in that list (-1)
        int[] _cand;               // driving points with a peak that clips by a gain of at most CandidateK
        float[] _u;                // Draws × clipped points, uniform 0–1
        double _clipNm, _tailRate;
        const double CandidateK = 1.35;

        /// <summary>A clip longer than a spike may be: a long clip.</summary>
        public static bool IsLong(double start, double end, ClipRule rule) { return end - start > rule.SpikeMaxS + 0.005; }

        /// <summary>
        /// Which clips count by the rule. Clips are given by start and end in seconds, in order. A clip longer than
        /// rule.SpikeMaxS (a long clip) counts on its own (weighted by the caller); any clip counts when it is one of at
        /// least rule.Count clips whose first start and last end lie within rule.WindowS seconds (a burst).
        /// </summary>
        public static bool[] CountClips(IList<double> starts, IList<double> ends, ClipRule rule)
        {
            int clips = starts.Count;
            var counts = new bool[clips];
            for (int c = 0; c < clips; c++) if (IsLong(starts[c], ends[c], rule)) counts[c] = true;
            int n = rule.Count;
            for (int k = 0; k + n - 1 < clips; k++)
                if (ends[k + n - 1] - starts[k] <= rule.WindowS + 0.005)
                    for (int c = k; c <= k + n - 1; c++) counts[c] = true;
            return counts;
        }

        sealed class Tally { public double AllS, CountedS, LongestS; public int Clips; }

        /// <summary>
        /// Builds clips from the samples of the given driving points (in order; points not given don't clip): a clip is
        /// a run of clipped samples, each lasting 0.1 s / the point's sample count, continuing into the next point when
        /// that one follows directly. Counts them by the rule, long clips weighted. markPoints (optional) gets the
        /// driving indices of every point that holds part of a counted clip.
        /// </summary>
        Tally Count(IList<int> pts, Func<int, int, bool> clipped, List<int> markPoints)
        {
            var starts = new List<double>(); var ends = new List<double>(); var owners = new List<List<int>>();
            int lastI = -2, lastJ = -2;
            foreach (int i in pts)
            {
                var s = _s[i];
                double d = 0.1 / s.Length;
                for (int j = 0; j < s.Length; j++)
                {
                    if (!clipped(i, j)) continue;
                    double ts = _t[i] + j * d;
                    bool cont = (i == lastI && j == lastJ + 1) ||
                                (i == lastI + 1 && _join[i] && j == 0 && lastJ == _s[lastI].Length - 1);
                    if (cont)
                    {
                        ends[ends.Count - 1] = ts + d;
                        var own = owners[owners.Count - 1];
                        if (own[own.Count - 1] != i) own.Add(i);
                    }
                    else { starts.Add(ts); ends.Add(ts + d); owners.Add(new List<int> { i }); }
                    lastI = i; lastJ = j;
                }
            }
            var counts = CountClips(starts, ends, Rule);
            var r = new Tally { Clips = starts.Count };
            for (int c = 0; c < starts.Count; c++)
            {
                double len = ends[c] - starts[c];
                r.AllS += len;
                r.LongestS = Math.Max(r.LongestS, len);
                if (!counts[c]) continue;
                r.CountedS += IsLong(starts[c], ends[c], Rule) ? len * Rule.LongWeight : len;
                if (markPoints != null) markPoints.AddRange(owners[c]);
            }
            return r;
        }

        /// <summary>Expected counted clipping (% of the driving time) with the gain multiplied by k.</summary>
        public double Predict(double k)
        {
            if (DrivingPoints == 0 || k <= 0 || _t == null) return 0;
            double drivingS = DrivingPoints / 10.0;
            if (k >= 1)
            {
                // everything that clipped still clips; a sample below the limit clips once sample × k reaches it
                IList<int> pts = k <= CandidateK ? (IList<int>)_cand : Enumerable.Range(0, _t.Length).ToList();
                return 100.0 * Count(pts, (i, j) => _s[i][j] * k >= _clipNm, null).CountedS / drivingS;
            }
            // lower: only clipped points can still clip, each with the chance that it went past the limit by 1/k or
            // more; a point that stays keeps the samples that clipped
            double q = Math.Exp(-_tailRate * (_clipNm / k - _clipNm));
            int m = _clipPts.Length;
            double sum = 0;
            for (int d = 0; d < Draws; d++)
            {
                int dd = d;
                sum += Count(_clipPts, (i, j) => _u[dd * m + _clipPos[i]] < q && _s[i][j] >= _clipNm, null).CountedS;
            }
            return 100.0 * sum / Draws / drivingS;
        }

        string What { get { return "Counted clipping"; } }

        public static Analysis Of(StageRun run, ClipRule rule)
        {
            double target = rule.TargetPct;
            var a = new Analysis { Rule = rule, TargetPct = target, ZoneLowPct = Math.Round(target * ZoneFraction, 1) };
            var smooth = new List<double>();
            var raw = new List<double>();
            var peaks = new List<float>();
            var t = new List<float>(); var samples = new List<float[]>(); var join = new List<bool>(); var runIdx = new List<int>();
            double sum = 0;
            bool hasSpeed = run.Points.Any(p => p.Speed > 0);   // without speed data, all stage time counts

            // Clipping is counted from the raw torque samples against the limit (the same test as the live clip flag when
            // the limit stayed right). The limit itself can be wrong: MozaHud once misread the base's max torque, during
            // or before a run. If the torque clearly went past it, the real ceiling is the highest raw peak.
            a.LimitNm = run.MaxNm;
            if (run.Points.Count(p => !p.Lock && p.TqMax > run.MaxNm + 0.25) >= 3)
            {
                a.LimitNm = Math.Round(run.Points.Where(p => !p.Lock).Max(p => p.TqMax), 1);
                a.LimitCorrected = true;
            }
            double clipAt = a.LimitNm - ClipMarginNm - 0.001;
            a._clipNm = clipAt;
            a.Clipped = new bool[run.Points.Count];
            var ptClip = new List<bool>();
            int prevRun = -2; float prevT = -1;
            for (int i = 0; i < run.Points.Count; i++)
            {
                var p = run.Points[i];
                if (p.TMot > a.MaxMotor) a.MaxMotor = p.TMot;
                if (p.TMos > a.MaxMosfet) a.MaxMosfet = p.TMos;
                var s = p.Samples != null && p.Samples.Length > 0 ? p.Samples.Select(b => b / 10f).ToArray() : new[] { p.TqMax };
                bool clip = s.Any(x => x >= clipAt);
                a.Clipped[i] = clip;
                if (p.Lock) { a.LockPoints++; if (clip) a.LockClipPoints++; continue; }
                if (hasSpeed && p.Speed < DrivingKmh) continue;
                a.DrivingPoints++;
                join.Add(prevRun == i - 1 && p.T - prevT < 0.15f);
                prevRun = i; prevT = p.T;
                t.Add(p.T - 0.1f); samples.Add(s); runIdx.Add(i); ptClip.Add(clip);
                if (clip) a.ClipPoints++; else peaks.Add(p.TqMax);
                double sm = Math.Abs(p.Tq);
                smooth.Add(sm); raw.Add(p.TqMax); sum += sm;
                if (sm > a.MaxSmoothNm) a.MaxSmoothNm = sm;
            }
            a._t = t.ToArray(); a._s = samples.ToArray(); a._join = join.ToArray(); a._ptClip = ptClip.ToArray();
            var clipPts = new List<int>(); a._clipPos = new int[a._t.Length];
            for (int i = 0; i < a._t.Length; i++) { a._clipPos[i] = a._ptClip[i] ? clipPts.Count : -1; if (a._ptClip[i]) clipPts.Add(i); }
            a._clipPts = clipPts.ToArray();
            a._cand = Enumerable.Range(0, a._t.Length).Where(i => a._s[i].Max() * CandidateK >= clipAt).ToArray();

            // the clips of this run, and which count by the rule
            var marked = new List<int>();
            var tally = a.Count(a._clipPts, (i, j) => a._s[i][j] >= clipAt, marked);
            a.Counted = new bool[run.Points.Count];
            foreach (int i in marked) a.Counted[runIdx[i]] = true;
            a.ClipEpisodes = tally.Clips;
            a.LongestClipS = tally.LongestS;
            a.AvgClipS = tally.Clips > 0 ? tally.AllS / tally.Clips : 0;

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
            double drivingS = a.DrivingPoints / 10.0;
            a.ClipPct = 100.0 * tally.AllS / drivingS;
            a.CountedPct = 100.0 * tally.CountedS / drivingS;

            // lowering: how far past the limit the clipped points went, from how fast the peaks thin out below it
            a._u = new float[Draws * a._clipPts.Length];
            var rnd = new Random(12345);
            for (int j = 0; j < a._u.Length; j++) a._u[j] = (float)rnd.NextDouble();
            int nearLimit = a.ClipPoints + peaks.Count(p => p >= a._clipNm - TailFitNm);
            a._tailRate = a.ClipPoints > 0 ? Math.Max(0.5, Math.Log((double)nearLimit / a.ClipPoints) / TailFitNm) : 0;

            // Search in steps of 1. Without a known gain, work on a base of 100 and only give the change in %.
            int? gain = run.Gain.HasValue && run.Gain.Value > 0 ? run.Gain : null;
            int baseGain = gain ?? 100;
            double aim = Math.Max(a.ZoneLowPct, target - Math.Min(AimMargin, target * 0.2));
            string zone = string.Format("{0:0.0}–{1:0.0} %", a.ZoneLowPct, target);
            string all = string.Format(" Lone spikes aren't counted (all clipping {0:0.0} %).", a.ClipPct);
            double v = a.CountedPct;

            if (v > target + 0.0001)
            {
                double excess = v - target;
                int floor = (int)Math.Ceiling(baseGain * (1 - MaxLowerPct / 100.0));
                int to = floor;
                for (int g = baseGain - 1; g >= floor; g--)
                    if (a.Predict((double)g / baseGain) <= aim) { to = g; break; }
                a.Capped = to == floor && a.Predict((double)to / baseGain) > aim;
                a.SetChange(to, baseGain, gain);
                a.Verdict = excess >= target * 0.6 ? Verdict.Lower : Verdict.SlightlyLower;
                a.Headline = a.Verdict == Verdict.Lower ? "Too strong: lower the FFB gain" : "A bit strong: lower the FFB gain slightly";
                a.Detail = string.Format(
                    "{0} {1:0.0} %, above your target {2:0.0} %: the force stays flat at the motor's limit there and " +
                    "road and car details are lost. {3}", a.What, v, target, a.Outcome());
                if (a.Capped) a.Hint = string.Format("MozaHud lowers at most {0} % at a time: drive the stage again to check.", MaxLowerPct);
            }
            else if (v < a.ZoneLowPct - 0.0001)
            {
                string low = string.Format("{0} only {1:0.0} %, under your target zone ({2}): ", a.What, v, zone);
                if (gain.HasValue && gain.Value >= MaxGain)
                {
                    a.Verdict = Verdict.Raise;
                    a.Headline = "Room to spare, but the gain is at its maximum";
                    a.Detail = low + "the motor has force you aren't feeling.";
                    a.Hint = "MozaHud supports gain up to " + MaxGain + "%.";
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
                a.Detail += all;
            }
            else
            {
                a.Verdict = Verdict.Good;
                a.Headline = "Good: the FFB gain fits this stage";
                a.Detail = string.Format("{0} {1:0.0} %, in your target zone ({2}): only the heaviest moments touch the limit " +
                                         "and little force is left unused.", a.What, v, zone) + all;
            }
            if (a.LockClipPoints > 0)
                a.Detail += string.Format(" Soft-lock time not counted ({0:0.0} s at the rotation limit).", a.LockPoints / 10.0);
            if (a.LimitCorrected)
                a.Hint = (a.Hint.Length > 0 ? a.Hint + " " : "") + string.Format(
                    "This run was saved with a limit of {0:0.0} Nm, but the torque reached {1:0.0} Nm: judged at {1:0.0} Nm.", run.MaxNm, a.LimitNm);
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
            sb.Append("t,tq,tqmax,clip,lock,wheel,speed,tmot,tmos,samples\n");
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
                  .Append(p.TMos.ToString("0.0", Inv)).Append(',');
                if (p.Samples != null) foreach (var s in p.Samples) sb.Append(s.ToString("X2"));   // 2 hex digits per sample
                sb.Append('\n');
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
                        Samples = f.Length > 9 ? Hex(f[9]) : null,
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

        /// <summary>Samples written as 2 hex digits each; null when empty or not hex.</summary>
        static byte[] Hex(string s)
        {
            s = s.Trim();
            if (s.Length < 2 || s.Length % 2 != 0) return null;
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++)
                if (!byte.TryParse(s.Substring(i * 2, 2), NumberStyles.HexNumber, Inv, out b[i])) return null;
            return b;
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
