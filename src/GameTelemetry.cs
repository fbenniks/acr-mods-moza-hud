using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;

namespace MozaHud
{
    public sealed class GameSnapshot
    {
        public bool Running;        // game process found
        public bool Memory;         // shared memory readable
        public int Status;          // 0 off/menu, 1 replay, 2 live, 3 pause
        public int GraphicsPacket, PhysicsPacket;
        public int CompletedLaps, CurrentTimeMs, LastTimeMs;
        public float Distance, SpeedKmh, Steer;
        public string Track = "", Car = "";

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "status {0} gfx#{1} phy#{2} laps {3} cur {4} last {5} dist {6:0} speed {7:0.0} steer {8:0.00} track '{9}' car '{10}'",
                Status, GraphicsPacket, PhysicsPacket, CompletedLaps, CurrentTimeMs, LastTimeMs, Distance, SpeedKmh, Steer, Track, Car);
        }
    }

    /// <summary>
    /// AC Rally's shared memory, which uses the ACC layout (Local\acpmf_physics / _graphics / _static).
    /// Offsets as in ACC's SPageFilePhysics / SPageFileGraphic / SPageFileStatic.
    /// </summary>
    public static class GameTelemetry
    {
        static readonly string[] ProcessNames = { "acr-Win64-Shipping", "acr" };

        public static bool GameRunning()
        {
            foreach (var n in ProcessNames)
                foreach (var p in Process.GetProcessesByName(n))
                {
                    try { if (!p.HasExited) return true; } catch { }
                }
            return false;
        }

        public static GameSnapshot Read(bool checkProcess)
        {
            var s = new GameSnapshot { Running = !checkProcess || GameRunning() };
            if (!s.Running) return s;
            try
            {
                using (var m = MemoryMappedFile.OpenExisting("Local\\acpmf_graphics", MemoryMappedFileRights.Read))
                using (var a = m.CreateViewAccessor(0, 256, MemoryMappedFileAccess.Read))
                {
                    s.GraphicsPacket = a.ReadInt32(0);
                    s.Status = a.ReadInt32(4);
                    s.CompletedLaps = a.ReadInt32(132);
                    s.CurrentTimeMs = a.ReadInt32(140);
                    s.LastTimeMs = a.ReadInt32(144);
                    s.Distance = a.ReadSingle(156);
                }
                using (var m = MemoryMappedFile.OpenExisting("Local\\acpmf_physics", MemoryMappedFileRights.Read))
                using (var a = m.CreateViewAccessor(0, 64, MemoryMappedFileAccess.Read))
                {
                    s.PhysicsPacket = a.ReadInt32(0);
                    s.Steer = a.ReadSingle(24);
                    s.SpeedKmh = a.ReadSingle(28);
                }
                using (var m = MemoryMappedFile.OpenExisting("Local\\acpmf_static", MemoryMappedFileRights.Read))
                using (var a = m.CreateViewAccessor(0, 512, MemoryMappedFileAccess.Read))
                {
                    s.Car = ReadWide(a, 68, 33);
                    s.Track = ReadWide(a, 134, 33);
                }
                s.Memory = true;
            }
            catch { }
            return s;
        }

        /// <summary>
        /// Every non-zero 4-byte word of the physics and graphics blocks (offset=int/float), to learn which fields
        /// AC Rally actually fills. Logged during stages.
        /// </summary>
        public static string RawDump()
        {
            var sb = new StringBuilder();
            foreach (var part in new[] { new { Name = "physics", Len = 800 }, new { Name = "graphics", Len = 1600 } })
            {
                try
                {
                    using (var m = MemoryMappedFile.OpenExisting("Local\\acpmf_" + part.Name, MemoryMappedFileRights.Read))
                    using (var a = m.CreateViewAccessor(0, part.Len, MemoryMappedFileAccess.Read))
                    {
                        sb.Append(part.Name).Append(':');
                        int shown = 0;
                        for (int off = 0; off < part.Len && shown < 80; off += 4)
                        {
                            int i = a.ReadInt32(off);
                            if (i == 0) continue;
                            float f = a.ReadSingle(off);
                            bool looksFloat = !float.IsNaN(f) && Math.Abs(f) > 1e-6 && Math.Abs(f) < 1e7 && (i < -100000 || i > 100000);
                            sb.Append(' ').Append(off).Append('=').Append(looksFloat ? f.ToString("0.###", CultureInfo.InvariantCulture) + "f" : i.ToString(CultureInfo.InvariantCulture));
                            shown++;
                        }
                        sb.Append("  ");
                    }
                }
                catch (Exception ex) { sb.Append(part.Name).Append(": ").Append(ex.Message).Append("  "); }
            }
            return sb.ToString();
        }

        static string ReadWide(MemoryMappedViewAccessor a, int offset, int chars)
        {
            var buf = new byte[chars * 2];
            a.ReadArray(offset, buf, 0, buf.Length);
            var str = Encoding.Unicode.GetString(buf);
            int z = str.IndexOf('\0');
            return (z >= 0 ? str.Substring(0, z) : str).Trim();
        }

        // ---------------- which stage, which gain ----------------

        public sealed class StageInfo { public string Stage, Car, Source; public int? Gain; }

        static readonly Regex StageSwitchApplied = new Regex(
            @"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\s+Applied FFB gain (\d+) for (.+?)\s*$");

        /// <summary>
        /// The stage and gain for a stage that was just loaded. First choice: AC Rally StageSwitch's log
        /// ("Applied FFB gain 200 for Sweden · Fredriksberg · Hyundai i20N Rally2"), written within the last few
        /// minutes; it knows the exact stage and sets the gain. Otherwise the game's save file.
        /// </summary>
        public static StageInfo ResolveStage(DateTime loadedAt)
        {
            var info = FromStageSwitch(loadedAt);
            if (info != null) return info;
            info = new StageInfo { Source = "save file" };
            var save = ReadSave();
            if (save != null)
            {
                info.Stage = StageFromSave(save);
                info.Gain = SavedGain(save);
            }
            return info;
        }

        static StageInfo FromStageSwitch(DateTime loadedAt)
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"AC Rally StageSwitch\log.txt");
                if (!File.Exists(path)) return null;
                string tail;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(Math.Max(0, fs.Length - 32 * 1024), SeekOrigin.Begin);
                    using (var r = new StreamReader(fs, Encoding.UTF8)) tail = r.ReadToEnd();
                }
                var lines = tail.Replace("\r", "").Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    var m = StageSwitchApplied.Match(lines[i]);
                    if (!m.Success) continue;
                    DateTime at;
                    if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out at)) continue;
                    if (at < loadedAt.AddMinutes(-3) || at > loadedAt.AddMinutes(2)) return null;
                    var parts = m.Groups[3].Value.Split(new[] { " · " }, StringSplitOptions.None);
                    var info = new StageInfo { Gain = int.Parse(m.Groups[2].Value), Source = "StageSwitch" };
                    if (parts.Length >= 3) { info.Stage = parts[0] + " · " + parts[1]; info.Car = string.Join(" · ", parts, 2, parts.Length - 2); }
                    else info.Stage = m.Groups[3].Value;
                    return info;
                }
            }
            catch { }
            return null;
        }

        static string SavePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"acr\Saved\SaveGames\PlayerDataSaveSlot.sav"); }
        }

        static byte[] ReadSave()
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    using (var fs = new FileStream(SavePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var ms = new MemoryStream()) { fs.CopyTo(ms); return ms.ToArray(); }
                }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
                catch { System.Threading.Thread.Sleep(150); }
            }
            return null;
        }

        // The event the game writes when a stage is started, e.g. "SwedenS3FredriksbergFullForward".
        static readonly Regex SaveStageRx = new Regex(@"(?<![A-Za-z])([A-Z][a-z]+(?:[A-Z][a-z]+)?)S\d+([A-Za-z0-9]+?(?:Forward|Reverse)[A-Za-z0-9]*)");

        static string StageFromSave(byte[] save)
        {
            var s = Encoding.GetEncoding(28591).GetString(save);
            var m = SaveStageRx.Match(s);
            if (!m.Success) return null;
            return m.Groups[1].Value + " · " + SplitWords(m.Groups[2].Value);
        }

        static string SplitWords(string id)
        {
            return Regex.Replace(id, "(?<=[a-z0-9])(?=[A-Z])", " ");
        }

        // FFB gain in the save (float multiplier, 2.0 = 200), found relative to two string anchors (game 2026-11),
        // as in StageSwitch's GainWriter.
        static int? SavedGain(byte[] save)
        {
            var s = Encoding.GetEncoding(28591).GetString(save);
            int normal = s.IndexOf("Normal\0", StringComparison.Ordinal);
            int custom = s.IndexOf("Custom\0", StringComparison.Ordinal);
            foreach (int k in new[] { normal > 0 ? normal + 7 + 96 : -1, custom > 4 ? custom - 4 - 261 : -1 })
            {
                if (k < 0 || k + 4 > save.Length) continue;
                float g = BitConverter.ToSingle(save, k);
                if (g >= 0f && g <= 2.0001f) return (int)Math.Round(g * 100);
            }
            return null;
        }
    }
}
