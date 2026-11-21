using System;
using System.Collections.Generic;
using System.Linq;

namespace MozaHud
{
    /// <summary>
    /// Records a stage run from AC Rally's telemetry plus the base's values, one point per 100 ms.
    ///
    /// A run starts when the game goes live (status 2) on a track and ends when it goes back to the menu, the track
    /// changes, the stage is restarted (the stage clock jumps back) or the stage is finished (completed laps goes up;
    /// recorded 5 s further so the finish is in). Paused/replay time isn't recorded. Transitions and a snapshot every
    /// 10 s are logged so the telemetry's behaviour can be checked against real drives.
    ///
    /// Runs on the polling thread; the UI reads Live/LastSaved under the lock.
    /// </summary>
    public sealed class StageRecorder
    {
        public const double LockThreshold = 0.97;   // |wheel| at or above this = at the rotation limit
        const int MinDrivingPoints = 200;           // runs with under 20 s of driving are not saved

        readonly object _gate = new object();
        StageRun _run;
        DateTime _runStartUtc;
        int _startLaps, _lastCurTime;
        DateTime? _finishedAt;
        DateTime _nextSnapshotLog, _nextProcessCheck;
        bool _gameRunning;
        bool _resolved, _waitForReset, _prevOnStage, _physicsTicks;
        int _prevStatus = -1, _lastPhysicsPacket = int.MinValue;
        DateTime _physicsStillSince, _nextRawLog;

        /// <summary>Called on the polling thread when a run was saved (or discarded: null).</summary>
        public Action<StageRun> Saved;

        public StageRun Live { get { lock (_gate) return _run; } }

        public List<StagePoint> LivePoints()
        {
            lock (_gate) return _run == null ? null : new List<StagePoint>(_run.Points);
        }

        public string Status { get; private set; }

        /// <summary>Call every 100 ms with the torque bucket, temperatures, wheel position and rotation (degrees, 0 = unknown).</summary>
        public void Tick(TorqueWindow.Bucket b, Temperatures temps, double? wheel, int rotationDeg, double maxNm)
        {
            var now = DateTime.UtcNow;
            if (now >= _nextProcessCheck)
            {
                _nextProcessCheck = now.AddSeconds(2);
                _gameRunning = GameTelemetry.GameRunning();
            }
            var g = _gameRunning ? GameTelemetry.Read(false) : new GameSnapshot();
            if (g.Status != _prevStatus && g.Memory)
            {
                Log.Write("Game status " + _prevStatus + " -> " + g.Status + ": " + g.Describe());
                _prevStatus = g.Status;
            }
            // AC Rally fills the track name while a stage is loaded and clears it in the menus (its status field
            // doesn't follow ACC's off/live/pause), so the track name is what says "on a stage".
            bool onStage = g.Memory && g.Track.Length > 0 && g.Status != 1;
            if (onStage != _prevOnStage) { Log.Write((onStage ? "On stage: " : "Off stage: ") + g.Describe()); _prevOnStage = onStage; }

            if (_run != null)
            {
                string end = null;
                if (!g.Running || !g.Memory) end = "game closed";
                else if (g.Track.Length == 0) end = "menu";
                else if (g.Track != _run.Track) end = "track changed";
                else if (_lastCurTime > 5000 && g.CurrentTimeMs + 3000 < _lastCurTime) end = "restart";
                else if (_finishedAt.HasValue && now - _finishedAt.Value > TimeSpan.FromSeconds(5)) end = "finished";
                if (end != null) Finish(end, g);
            }

            // after a finish the stage stays loaded on the results screen: wait for the menu or a reset stage clock
            if (_waitForReset && (!onStage || g.CurrentTimeMs + 3000 < _lastCurTime)) _waitForReset = false;

            if (_run == null)
            {
                if (onStage && !_waitForReset) Begin(g, rotationDeg, maxNm);
                else
                {
                    if (g.CurrentTimeMs > 0) _lastCurTime = g.CurrentTimeMs;
                    Status = !_gameRunning ? "Waiting for AC Rally" : _waitForReset ? "Stage finished" : "AC Rally: not on a stage";
                    return;
                }
            }

            // stage name / gain: StageSwitch logs it a moment after the stage loads
            if (!_resolved && now - _runStartUtc > TimeSpan.FromSeconds(4)) Resolve();

            if (!_finishedAt.HasValue && g.CompletedLaps > _startLaps)
            {
                _finishedAt = now;
                Log.Write("Stage finished (completed laps " + _startLaps + " -> " + g.CompletedLaps + "): " + g.Describe());
            }
            if (g.CurrentTimeMs > 0) _lastCurTime = g.CurrentTimeMs;

            if (now >= _nextSnapshotLog)
            {
                _nextSnapshotLog = now.AddSeconds(5);
                Log.Write("Telemetry: " + g.Describe() + " wheel " + (wheel.HasValue ? wheel.Value.ToString("0.00") : "?"));
                if (now >= _nextRawLog) { _nextRawLog = now.AddSeconds(30); Log.Write("Raw telemetry: " + GameTelemetry.RawDump()); }
            }

            // Paused: only when the physics data is known to tick and has stopped (AC Rally may not fill it at all).
            if (g.PhysicsPacket != _lastPhysicsPacket)
            {
                if (_lastPhysicsPacket != int.MinValue) _physicsTicks = true;
                _lastPhysicsPacket = g.PhysicsPacket;
                _physicsStillSince = now;
            }
            bool paused = g.Status == 3 || (_physicsTicks && now - _physicsStillSince > TimeSpan.FromMilliseconds(500));
            if (paused) { Status = "Recording paused"; return; }

            bool lockNow = wheel.HasValue && Math.Abs(wheel.Value) >= LockThreshold;
            var p = new StagePoint
            {
                T = (float)(now - _runStartUtc).TotalSeconds,
                Tq = (float)b.SmoothNm,
                TqMax = (float)b.MaxRawNm,
                Clip = b.Clipped > 0,
                Lock = lockNow,
                Wheel = (float)(wheel ?? 0),
                Speed = Math.Max(0, g.SpeedKmh),
                TMot = temps == null ? 0 : (float)temps.Motor,
                TMos = temps == null ? 0 : (float)temps.Mosfet,
            };
            lock (_gate) _run.Points.Add(p);
            Status = "Recording " + _run.Title + "  " + FormatTime(p.T);
        }

        void Begin(GameSnapshot g, int rotationDeg, double maxNm)
        {
            var run = new StageRun
            {
                Start = DateTime.Now, Track = g.Track, Car = g.Car, Stage = g.Track,
                RotationDeg = rotationDeg, MaxNm = maxNm, PointsLoaded = true,
            };
            lock (_gate) _run = run;
            _runStartUtc = DateTime.UtcNow;
            _startLaps = g.CompletedLaps;
            _lastCurTime = 0;
            _finishedAt = null;
            _resolved = false;
            _nextSnapshotLog = DateTime.UtcNow;
            _nextRawLog = DateTime.UtcNow;
            _physicsTicks = false;
            _lastPhysicsPacket = int.MinValue;
            Log.Write("Stage run started: " + g.Describe() + " rotation " + rotationDeg + "°");
        }

        void Resolve()
        {
            _resolved = true;
            var info = GameTelemetry.ResolveStage(_run.Start);
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(info.Stage)) _run.Stage = info.Stage;
                if (!string.IsNullOrEmpty(info.Car)) _run.Car = info.Car;
                _run.Gain = info.Gain;
                _run.GainSource = info.Source ?? "";
            }
            Log.Write("Stage: " + _run.Stage + ", car " + _run.Car + ", gain " + (info.Gain.HasValue ? info.Gain.ToString() : "?") + " (" + info.Source + ")");
        }

        void Finish(string reason, GameSnapshot g)
        {
            StageRun run;
            lock (_gate) { run = _run; _run = null; }
            if (!_resolved) { _run = run; Resolve(); _run = null; }
            run.End = DateTime.Now;
            run.EndReason = _finishedAt.HasValue ? "finished" : reason;
            _waitForReset = _finishedAt.HasValue && g.Track.Length > 0;
            Log.Write("Stage run ended (" + run.EndReason + "): " + g.Describe() + ", " + run.Points.Count + " points");

            // drop the wait at the start line, keep 3 s before the car first moves
            int first = run.Points.FindIndex(p => p.Speed > 2);
            if (first > 30)
            {
                float shift = run.Points[first - 30].T;
                run.Points.RemoveRange(0, first - 30);
                for (int i = 0; i < run.Points.Count; i++) { var p = run.Points[i]; p.T -= shift; run.Points[i] = p; }
            }

            bool hasSpeed = run.Points.Any(p => p.Speed > 0);
            int driving = hasSpeed ? run.Points.Count(p => p.Speed > 5) : run.Points.Count;
            if (driving < MinDrivingPoints)
            {
                Log.Write("Run not saved: only " + driving / 10 + " s of driving");
                if (Saved != null) Saved(null);
                return;
            }
            try { StageStore.Save(run); Log.Write("Saved " + run.FilePath); }
            catch (Exception ex) { Log.Write("Saving the run failed: " + ex.Message); return; }
            if (Saved != null) Saved(run);
        }

        public static string FormatTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }
    }
}
