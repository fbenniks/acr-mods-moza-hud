using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace MozaHud
{
    /// <summary>
    /// Publishes the values for MSI Afterburner's AIDA64 monitoring plugin, which every poll opens the shared memory
    /// block "AIDA64_SensorValues" and reads XML-like text from it:
    ///   &lt;sys&gt;&lt;id&gt;MOZA_TMOT&lt;/id&gt;&lt;label&gt;…&lt;/label&gt;&lt;value&gt;20.5&lt;/value&gt;&lt;/sys&gt;…
    /// and matches the ids against the sources in Plugins\Monitoring\AIDA64.cfg (added by AfterburnerSetup).
    /// No admin rights or registration needed. (Windows performance counters were used before, but re-registering the
    /// category left readers matching the counters wrongly.)
    /// If AIDA64 itself is running it owns this block; MozaHud then can't publish and says so.
    /// </summary>
    public sealed class SensorFeed : IDisposable
    {
        public const string MapName = "AIDA64_SensorValues";
        const int Size = 16 * 1024;

        // id, label, type tag
        public static readonly string[][] Sensors =
        {
            new[] { "MOZA_TMOT",  "MOZA motor temperature",  "temp" },
            new[] { "MOZA_TMOS",  "MOZA MOSFET temperature", "temp" },
            new[] { "MOZA_TMCU",  "MOZA MCU temperature",    "temp" },
            new[] { "MOZA_TQPK",  "MOZA torque peak",        "sys"  },
            new[] { "MOZA_TQAVG", "MOZA torque",             "sys"  },
            new[] { "MOZA_LOAD",  "MOZA FFB load",           "sys"  },
            new[] { "MOZA_CLIP",  "MOZA clipping",           "sys"  },
        };

        readonly MemoryMappedFile _map;
        readonly MemoryMappedViewAccessor _view;
        readonly Dictionary<string, double> _values = new Dictionary<string, double>();

        /// <summary>Throws IOException when the block already exists (AIDA64 running).</summary>
        public SensorFeed()
        {
            _map = MemoryMappedFile.CreateNew(MapName, Size);
            _view = _map.CreateViewAccessor(0, Size);
        }

        public void SetTemperatures(Temperatures t)
        {
            Set("MOZA_TMOT", t == null ? (double?)null : t.Motor);
            Set("MOZA_TMOS", t == null ? (double?)null : t.Mosfet);
            Set("MOZA_TMCU", t == null ? (double?)null : t.Mcu);
        }

        public void SetTorque(TorqueStats s)
        {
            Set("MOZA_TQPK", s.Valid ? s.PeakNm : (double?)null);
            Set("MOZA_TQAVG", s.Valid ? s.NowNm : (double?)null);   // id kept from the 1 s average it replaced
            Set("MOZA_LOAD", s.Valid ? s.LoadPct : (double?)null);
            Set("MOZA_CLIP", s.Valid ? s.ClipPct : (double?)null);
        }

        void Set(string id, double? v)
        {
            if (v.HasValue) _values[id] = v.Value; else _values.Remove(id);
        }

        /// <summary>Writes the current values. Sensors without a value are left out (Afterburner shows them as N/A).</summary>
        public void Publish()
        {
            var sb = new StringBuilder();
            foreach (var s in Sensors)
            {
                double v;
                if (!_values.TryGetValue(s[0], out v)) continue;
                sb.Append('<').Append(s[2]).Append("><id>").Append(s[0]).Append("</id><label>").Append(s[1])
                  .Append("</label><value>").Append(v.ToString("0.00", CultureInfo.InvariantCulture))
                  .Append("</value></").Append(s[2]).Append('>');
            }
            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            int n = Math.Min(bytes.Length, Size - 1);
            _view.WriteArray(0, bytes, 0, n);
            _view.Write(n, (byte)0);
        }

        public void Dispose()
        {
            try { _view.Write(0, (byte)0); } catch { }
            _view.Dispose();
            _map.Dispose();
        }
    }
}
