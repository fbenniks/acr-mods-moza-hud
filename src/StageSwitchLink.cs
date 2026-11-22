using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace MozaHud
{
    /// <summary>
    /// Talks to AC Rally StageSwitch (the companion app that sets the FFB gain per stage) over its local named pipe
    /// "ACRallyStageSwitch": one tab-separated request line, one reply line ("OK …" or "ERR …").
    /// </summary>
    public static class StageSwitchLink
    {
        const string PipeName = "ACRallyStageSwitch";
        public const int GameMenuMaxGain = 200;
        public const string RepositoryUrl = "https://github.com/fbenniks/acr-mods-stageswitch";

        public static bool IsRunning()
        {
            try { return Process.GetProcessesByName("ACRallyStageSwitch").Length > 0; } catch { return false; }
        }

        /// <summary>Sends a request; returns the reply fields (first = "OK"/"ERR …") or throws with a readable message.</summary>
        public static string[] Request(params string[] fields)
        {
            using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
            {
                try { pipe.Connect(1500); }
                catch (TimeoutException) { throw new IOException("StageSwitch doesn't answer. Update StageSwitch to a version that accepts requests from MozaHud."); }
                var w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                var r = new StreamReader(pipe, new UTF8Encoding(false));
                w.WriteLine(string.Join("\t", fields));
                var line = r.ReadLine();
                if (line == null) throw new IOException("StageSwitch closed the connection.");
                return line.Split('\t');
            }
        }

        public sealed class Current
        {
            public string Rally, Stage, CarName;
            public int RallyGain, StageGain;
            public int? CarGain;
            public bool PerCar;
        }

        public static Current Get(string rally, string stage, string car)
        {
            var f = Request("GET", rally, stage, car ?? "");
            if (f[0] != "OK") throw new IOException(f[0].StartsWith("ERR ") ? f[0].Substring(4) : f[0]);
            int carGain;
            return new Current
            {
                Rally = f[1], Stage = f[2], RallyGain = int.Parse(f[3]), StageGain = int.Parse(f[4]),
                CarGain = int.TryParse(f[5], out carGain) ? carGain : (int?)null, CarName = f[6], PerCar = f[7] == "1",
            };
        }

        /// <summary>scope: "car", "stage" or "rally". Returns StageSwitch's confirmation text.</summary>
        public static string SetGain(string scope, string rally, string stage, string car, int gain)
        {
            var f = Request("SETGAIN", scope, rally, stage, car ?? "", gain.ToString());
            if (f[0] != "OK") throw new IOException(f[0].StartsWith("ERR ") ? f[0].Substring(4) : f[0]);
            return f.Length > 1 ? f[1] : "Saved.";
        }

        /// <summary>Rally and stage keys of a run: StageSwitch's ids when recorded, else the "Rally · Stage" title.</summary>
        public static bool Keys(StageRun run, out string rally, out string stage)
        {
            rally = stage = null;
            if (!string.IsNullOrEmpty(run.StageId) && run.StageId.Contains("/"))
            {
                var p = run.StageId.Split('/');
                rally = p[0]; stage = p[1];
                return true;
            }
            var parts = (run.Stage ?? "").Split('·');
            if (parts.Length < 2) return false;
            rally = parts[0].Trim(); stage = parts[1].Trim();
            return rally.Length > 0 && stage.Length > 0;
        }
    }
}
