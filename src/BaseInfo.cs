using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MozaHud
{
    /// <summary>
    /// What MozaHud knows about the connected base: identity read from the base (product type, hardware version,
    /// firmware), the model that matches, its rated peak torque and the max torque setting, plus a check of the real
    /// torque ceiling from clipping.
    /// </summary>
    public sealed class BaseInfo
    {
        // Models as in Pit House's bin\config\device_config.json (product type + optional hardware version pattern),
        // most specific first. Rated peak torque: the number in the name is the Nm, except R3 (3.9) and the R5
        // family (5.5). Uncertain ratings are corrected by the ceiling check below.
        static readonly ModelDef[] Models =
        {
            new ModelDef("R21 Ultra",   @"^R21 Black$",          @"^RS21-D11-",   21),
            new ModelDef("R16S Ultra",  @"^R16S Black$",         @"^RS21-D12-",   16),
            new ModelDef("R5S Pro",     @"^R5S Black$",          @"^RS21-D13-",   5.5),
            new ModelDef("R5 Pro",      @"^R5 Black$",           @"^RS21-D13-",   5.5),
            new ModelDef("R12 Pro",     @"^R12 Black$",          @"^RS21-D14-",   12),
            new ModelDef("R9 Pro",      @"^R9 Black$",           @"^RS21-D15-",   9),
            new ModelDef("R27 Ultra",   @"^R27 Black$",          null,            27),
            new ModelDef("R25 Ultra",   @"^R25 Black$",          null,            25),
            new ModelDef("R16S Ultra",  @"^R16S Black$",         null,            16),
            new ModelDef("R21",         @"^R21 (Black|Orange)$", null,            21),
            new ModelDef("R16",         @"^R16 (Black|Orange)$", null,            16),
            new ModelDef("R12",         @"^R12 Black$",          null,            12),
            new ModelDef("R9",          @"^R9 (Black|White|Base)$", null,         9),
            new ModelDef("R5",          @"^R5 Black$",           null,            5.5),
            new ModelDef("R5S",         @"^R5S Black$",          null,            5.5),
            new ModelDef("R3",          @"^R3 Black$",           null,            3.9),
        };

        sealed class ModelDef
        {
            public readonly string Name; public readonly Regex Type, Hw; public readonly double Nm;
            public ModelDef(string name, string type, string hw, double nm)
            {
                Name = name; Type = new Regex(type); Hw = hw == null ? null : new Regex(hw); Nm = nm;
            }
        }

        readonly Dictionary<int, string>[] _text = { new Dictionary<int, string>(), new Dictionary<int, string>() };

        public string ProductType = "", HardwareVersion = "", Firmware = "";
        public string Model = "";          // e.g. "R5"; empty while unknown
        public double RatedNm;             // 0 while unknown
        public int PeakTorquePct = 100;    // base setting "max torque"
        public double? MeasuredNm;         // ceiling seen in clipping, when it differs from rated × setting

        public const double FallbackNm = 5.5;

        /// <summary>The torque at which the base clips: measured ceiling if known, else rated × max torque setting.</summary>
        public double MaxNm
        {
            get
            {
                if (MeasuredNm.HasValue) return MeasuredNm.Value;
                double rated = RatedNm > 0 ? RatedNm : FallbackNm;
                return Math.Round(rated * Math.Max(10, Math.Min(100, PeakTorquePct)) / 100.0, 1);
            }
        }

        public string Describe()
        {
            if (Model.Length == 0) return "";
            var s = Model + " · " + MaxNm.ToString("0.0") + " Nm";
            if (MeasuredNm.HasValue) s += " (measured)";
            else if (PeakTorquePct != 100) s += " (" + PeakTorquePct + " % of " + RatedNm.ToString("0.#") + ")";
            return s;
        }

        /// <summary>A text reply (group 7 or 8): sector number + 16 characters. Returns true when the model changed.</summary>
        public bool AddText(byte group, byte[] payload)
        {
            if (payload.Length < 2) return false;
            int idx = group == MozaLink.GroupProductType ? 0 : group == MozaLink.GroupHardwareVersion ? 1 : -1;
            if (idx < 0) return false;
            var chars = Encoding.ASCII.GetString(payload, 1, payload.Length - 1);
            int z = chars.IndexOf('\0'); if (z >= 0) chars = chars.Substring(0, z);
            _text[idx][payload[0]] = chars;
            var full = new StringBuilder();
            for (int s = 1; s <= 4 && _text[idx].ContainsKey(s); s++) full.Append(_text[idx][s]);
            if (idx == 0) ProductType = full.ToString().Trim(); else HardwareVersion = full.ToString().Trim();
            // Pro/Ultra variants differ only in the hardware version: wait for both before deciding
            if (!_text[0].ContainsKey(1) || !_text[1].ContainsKey(1)) return false;
            return Identify();
        }

        public void SetFirmware(byte[] payload)
        {
            if (payload.Length >= 4) Firmware = payload[0] + "." + payload[1] + "." + payload[2] + "." + payload[3];
        }

        bool Identify()
        {
            // "R5 Black # MOT-1-V03" -> "R5 Black" (Pit House matches the part before " #")
            var type = ProductType;
            int hash = type.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) type = type.Substring(0, hash);
            type = type.Trim();
            foreach (var m in Models)
            {
                if (!m.Type.IsMatch(type)) continue;
                if (m.Hw != null && !m.Hw.IsMatch(HardwareVersion)) continue;
                if (m.Name == Model) return false;
                Model = m.Name; RatedNm = m.Nm;
                return true;
            }
            if (type.Length > 0 && Model.Length == 0)
            {
                // unknown model: take the number in the name as Nm ("R30 Black" -> 30)
                var num = Regex.Match(type, @"^R(\d+)");
                Model = type;
                RatedNm = num.Success ? double.Parse(num.Groups[1].Value) : FallbackNm;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Finds the real torque ceiling from clipping. When the base clips, its torque reading sticks at exactly one
    /// value (R5: 554 → 5.4 Nm, hit 21 times in two minutes while the value below it was hit 3 times). A plateau
    /// like that tells where the limit really is.
    /// </summary>
    public sealed class CeilingCheck
    {
        readonly int[] _counts = new int[1001];   // by |raw - 500| (0.1 Nm steps)
        public int Samples { get; private set; }

        public void Reset() { Array.Clear(_counts, 0, _counts.Length); Samples = 0; }

        public void Add(int raw)
        {
            int v = Math.Abs(raw - MozaLink.TorqueZero);
            if (v > 1000) return;
            _counts[v]++;
            Samples++;
        }

        /// <summary>The plateau value in Nm, or null when there isn't a clear one (yet).</summary>
        public double? Plateau()
        {
            for (int v = 1000; v > 10; v--)
            {
                if (_counts[v] < 10) continue;   // the highest value that occurs often
                return _counts[v] >= 3 * Math.Max(1, _counts[v - 1]) ? v / 10.0 : (double?)null;
            }
            return null;
        }
    }
}
