using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MozaHud
{
    /// <summary>
    /// One-time setup that needs admin rights (Afterburner lives in Program Files): adds the MOZA sensors as sources to
    /// Afterburner's AIDA64 plugin config (Plugins\Monitoring\AIDA64.cfg). The plugin reads [Source0], [Source1], ...
    /// until the first missing number, so sources must stay numbered without gaps.
    /// Also removes what older MozaHud versions set up (PerfCounter.cfg sources, the "MOZA Wheelbase" counter category).
    /// </summary>
    public static class AfterburnerSetup
    {
        static readonly string[][] Sources =
        {
            //      id             Afterburner name     units      max    format
            new[] { "MOZA_TMOT",  "MOZA motor temp",   "°C", "100", "%.1f" },
            new[] { "MOZA_TMOS",  "MOZA MOSFET temp",  "°C", "100", "%.1f" },
            new[] { "MOZA_TMCU",  "MOZA MCU temp",     "°C", "100", "%.1f" },
            new[] { "MOZA_TQPK",  "MOZA torque peak",  "Nm",      "6",   "%.1f" },
            new[] { "MOZA_TQAVG", "MOZA torque avg",   "Nm",      "6",   "%.1f" },
            new[] { "MOZA_LOAD",  "MOZA FFB load",     "%",       "100", "%.0f" },
            new[] { "MOZA_CLIP",  "MOZA clipping",     "%",       "100", "%.0f" },
        };

        const string OldCategory = "MOZA Wheelbase";

        public static string FindAfterburner()
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var k = hk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Afterburner"))
                    {
                        var dir = k == null ? null : k.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "MSIAfterburner.exe"))) return dir;
                    }
                }
                catch { }
            }
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var def = Path.Combine(pf, "MSI Afterburner");
            return File.Exists(Path.Combine(def, "MSIAfterburner.exe")) ? def : null;
        }

        static string PluginCfg(string name)
        {
            var dir = FindAfterburner();
            return dir == null ? null : Path.Combine(dir, @"Plugins\Monitoring\" + name);
        }

        public static string CfgPath() { return PluginCfg("AIDA64.cfg"); }

        /// <summary>True when AIDA64.cfg has all current MOZA sources.</summary>
        public static bool CfgHasSources()
        {
            try
            {
                var p = CfgPath();
                if (p == null || !File.Exists(p)) return false;
                var text = File.ReadAllText(p, Encoding.Default);
                return Sources.All(s => Regex.IsMatch(text, @"(?m)^\s*ID\s*=\s*" + Regex.Escape(s[0]) + @"\s*$"));
            }
            catch { return false; }
        }

        // ---- section-level editing of the ini file, keeping everything else as it was ----

        sealed class Section { public string Header; public List<string> Lines = new List<string>(); }

        static List<Section> ReadSections(string text, out List<string> preamble)
        {
            preamble = new List<string>();
            var list = new List<Section>();
            Section cur = null;
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (Regex.IsMatch(line, @"^\s*\[.+\]\s*$")) { cur = new Section { Header = line.Trim() }; list.Add(cur); }
                else if (cur == null) preamble.Add(line);
                else cur.Lines.Add(line);
            }
            return list;
        }

        static bool HasLine(Section s, string pattern)
        {
            return s.Lines.Any(l => Regex.IsMatch(l, pattern));
        }

        static bool IsOurAida(Section s) { return HasLine(s, @"^\s*ID\s*=\s*MOZA_\w+\s*$"); }
        static bool IsOurPerf(Section s) { return HasLine(s, @"^\s*ObjectName\s*=\s*" + Regex.Escape(OldCategory) + @"\s*$"); }

        static string Write(List<string> preamble, List<Section> sections)
        {
            // Renumber [SourceN] sections so there are no gaps.
            int n = 0;
            var sb = new StringBuilder();
            foreach (var l in preamble) sb.Append(l).Append("\r\n");
            foreach (var s in sections)
            {
                var header = Regex.IsMatch(s.Header, @"^\[Source\d+\]$") ? "[Source" + (n++) + "]" : s.Header;
                sb.Append(header).Append("\r\n");
                foreach (var l in s.Lines) sb.Append(l).Append("\r\n");
            }
            return sb.ToString().TrimEnd('\r', '\n') + "\r\n";
        }

        static void SaveCfg(string path, string text)
        {
            var backup = path + ".mozahud-backup";
            if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);
            File.WriteAllText(path, text, Encoding.Default);   // GetPrivateProfileStringA reads ANSI
        }

        static void AddSources(string path)
        {
            var text = File.Exists(path) ? File.ReadAllText(path, Encoding.Default) : "";
            List<string> pre;
            var sections = ReadSections(text, out pre);
            sections.RemoveAll(IsOurAida);
            if (sections.Count > 0 && sections.Last().Lines.Count > 0 && sections.Last().Lines.Last().Trim() != "")
                sections.Last().Lines.Add("");
            foreach (var src in Sources)
            {
                var s = new Section { Header = "[Source0]" };
                s.Lines.Add("ID\t\t= " + src[0]);
                s.Lines.Add("Name\t\t= " + src[1]);
                s.Lines.Add("Units\t\t= " + src[2]);
                s.Lines.Add("MaxLimit\t= " + src[3]);
                s.Lines.Add("MinLimit\t= 0");
                s.Lines.Add("Group\t\t= MOZA");
                s.Lines.Add("Format\t\t= " + src[4]);
                s.Lines.Add("");
                sections.Add(s);
            }
            SaveCfg(path, Write(pre, sections));
        }

        static void RemoveSections(string path, Predicate<Section> match)
        {
            if (path == null || !File.Exists(path)) return;
            List<string> pre;
            var sections = ReadSections(File.ReadAllText(path, Encoding.Default), out pre);
            if (sections.RemoveAll(match) > 0) SaveCfg(path, Write(pre, sections));
        }

        /// <summary>Removes what MozaHud versions with Windows performance counters set up.</summary>
        static void RemoveOldCounters()
        {
            RemoveSections(PluginCfg("PerfCounter.cfg"), IsOurPerf);
            try { if (PerformanceCounterCategory.Exists(OldCategory)) PerformanceCounterCategory.Delete(OldCategory); } catch { }
        }

        /// <summary>Runs elevated (MozaHud.exe --setup). Returns a message for the user.</summary>
        public static string Install()
        {
            RemoveOldCounters();
            var cfg = CfgPath();
            if (cfg == null) return "MSI Afterburner was not found.";
            AddSources(cfg);
            bool running = Process.GetProcessesByName("MSIAfterburner").Length > 0;
            return "Done. These sources were added to Afterburner's AIDA64 plugin:\n" +
                   "  " + string.Join(", ", Sources.Select(s => s[1])) + "\n\n" +
                   (running ? "Restart MSI Afterburner so it loads them.\n\n" : "") +
                   "Then in Afterburner: Settings → Monitoring, click the \"…\" button next to \"Active hardware monitoring " +
                   "graphs\" and tick AIDA64.dll. The MOZA graphs appear at the bottom of the list: tick the ones you want, " +
                   "select one and tick \"Show in On-Screen Display\".";
        }

        /// <summary>Runs elevated (MozaHud.exe --uninstall).</summary>
        public static string Uninstall()
        {
            RemoveSections(CfgPath(), IsOurAida);
            RemoveOldCounters();
            return "The MOZA sources were removed from Afterburner.";
        }

        /// <summary>Starts this exe elevated with the given argument and waits. Returns false when UAC was declined.</summary>
        public static bool RunElevated(string arg)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath, arg) { Verb = "runas", UseShellExecute = true });
                p.WaitForExit();
                return p.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception) { return false; }   // UAC cancelled
        }
    }
}
