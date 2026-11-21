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
    /// One-time setup that needs admin rights: registers the performance counters and adds them as sources to
    /// Afterburner's PerfCounter plugin config (Plugins\Monitoring\PerfCounter.cfg). The plugin reads [Source0],
    /// [Source1], ... until the first missing number, so sources must stay numbered without gaps.
    /// </summary>
    public static class AfterburnerSetup
    {
        static readonly string[][] Sources =
        {
            //           counter name        Afterburner name     max
            new[] { Counters.Motor,  "MOZA motor temp",  "100" },
            new[] { Counters.Mosfet, "MOZA MOSFET temp", "100" },
            new[] { Counters.Mcu,    "MOZA MCU temp",    "100" },
        };

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

        public static string CfgPath()
        {
            var dir = FindAfterburner();
            return dir == null ? null : Path.Combine(dir, @"Plugins\Monitoring\PerfCounter.cfg");
        }

        /// <summary>True when PerfCounter.cfg already has our sources.</summary>
        public static bool CfgHasSources()
        {
            try
            {
                var p = CfgPath();
                return p != null && File.Exists(p) && File.ReadAllText(p, Encoding.Default).Contains(Counters.Category);
            }
            catch { return false; }
        }

        // ---- section-level editing of the ini file, keeping everything else byte-for-byte ----

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

        static bool IsOurs(Section s)
        {
            return s.Lines.Any(l => Regex.IsMatch(l, @"^\s*ObjectName\s*=\s*" + Regex.Escape(Counters.Category) + @"\s*$"));
        }

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
            sections.RemoveAll(IsOurs);
            // a blank line between sections, like the rest of the file
            if (sections.Count > 0 && sections.Last().Lines.Count > 0 && sections.Last().Lines.Last().Trim() != "")
                sections.Last().Lines.Add("");
            foreach (var src in Sources)
            {
                var s = new Section { Header = "[Source0]" };
                s.Lines.Add("ObjectName\t= " + Counters.Category);
                s.Lines.Add("CounterName\t= " + src[0]);
                s.Lines.Add("Div\t\t= 100");
                s.Lines.Add("Name\t\t= " + src[1]);
                s.Lines.Add("Units\t\t= °C");
                s.Lines.Add("MaxLimit\t= " + src[2]);
                s.Lines.Add("MinLimit\t= 0");
                s.Lines.Add("Group\t\t= MOZA");
                s.Lines.Add("Format\t\t= %.1f");
                s.Lines.Add("");
                sections.Add(s);
            }
            SaveCfg(path, Write(pre, sections));
        }

        static void RemoveSources(string path)
        {
            if (!File.Exists(path)) return;
            List<string> pre;
            var sections = ReadSections(File.ReadAllText(path, Encoding.Default), out pre);
            if (sections.RemoveAll(IsOurs) > 0) SaveCfg(path, Write(pre, sections));
        }

        /// <summary>Runs elevated (MozaHud.exe --setup). Returns a message for the user.</summary>
        public static string Install()
        {
            Counters.Register();
            var cfg = CfgPath();
            if (cfg == null)
                return "Performance counters registered, but MSI Afterburner was not found.\n" +
                       "Add the \"" + Counters.Category + "\" counters yourself in Afterburner's PerfCounter plugin.";
            AddSources(cfg);
            bool running = Process.GetProcessesByName("MSIAfterburner").Length > 0;
            return "Done. Three sources were added to Afterburner's PerfCounter plugin:\n" +
                   "  MOZA motor temp, MOZA MOSFET temp, MOZA MCU temp\n\n" +
                   (running ? "Restart MSI Afterburner so it loads them.\n\n" : "") +
                   "Then in Afterburner: Settings → Monitoring. If the MOZA graphs are missing, click the \"…\" button next to " +
                   "\"Active hardware monitoring graphs\" and tick PerfCounter.dll. Select a MOZA graph and tick " +
                   "\"Show in On-Screen Display\".";
        }

        /// <summary>Runs elevated (MozaHud.exe --uninstall).</summary>
        public static string Uninstall()
        {
            var cfg = CfgPath();
            if (cfg != null) RemoveSources(cfg);
            Counters.Unregister();
            return "The MOZA sources were removed from Afterburner and the performance counters were unregistered.";
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
