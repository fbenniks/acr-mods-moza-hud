using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;

namespace MozaHud
{
    public sealed class Temperatures
    {
        public double Mcu, Mosfet, Motor;   // °C
        public override string ToString()
        {
            return string.Format("Motor {0:0.0} °C · MOSFET {1:0.0} °C · MCU {2:0.0} °C", Motor, Mosfet, Mcu);
        }
    }

    /// <summary>
    /// Talks to a MOZA base over its USB serial port using the MOZA serial protocol (documented by boxflat,
    /// data/serial.yml; the newer command names come from the database embedded in Pit House's FirmwareManager.exe).
    /// Request frame: 7E, payload length, read group, device id, command id, payload..., checksum.
    /// Checksum = (13 + sum of all previous bytes) % 256. Replies have bit 7 set in the group (43 -> 0xAB) and the
    /// device id nibbles swapped (base 0x13 -> 0x31). The base also sends debug text frames (group 0x0E); skipped.
    ///
    /// Reads used (group 43, base device 19, 2-byte big-endian reply):
    ///   4 MCU temp, 5 MOSFET temp, 6 motor temp  (1/100 °C)
    ///   7 MotTorque: live force-feedback torque, 500 = no force, 1 step = 0.1 Nm (R5: clips at 445 / 554 = ±5.5 Nm)
    /// </summary>
    public sealed class MozaLink : IDisposable
    {
        const byte Start = 0x7E, Magic = 13, ReadGroup = 43, BaseId = 19;
        public const byte IdMcu = 4, IdMosfet = 5, IdMotor = 6, IdTorque = 7;
        public const int TorqueZero = 500;

        readonly SerialPort _sp;
        readonly List<byte> _buf = new List<byte>();
        readonly byte[] _chunk = new byte[1024];

        /// <summary>Called for each value reply: command id, raw signed 16-bit value.</summary>
        public Action<byte, int> OnValue;

        public string Port { get; private set; }

        /// <summary>Opens the port. Throws UnauthorizedAccessException when another program (Pit House) has it.</summary>
        public MozaLink(string port)
        {
            Port = port;
            _sp = new SerialPort(port, 115200) { ReadTimeout = 5, WriteTimeout = 500, DtrEnable = true };
            _sp.Open();
        }

        /// <summary>Finds the COM port of a MOZA device (USB vendor id 346E), or null.</summary>
        public static string FindPort()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_346E%'"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        var m = Regex.Match((o["Name"] as string) ?? "", @"\((COM\d+)\)");
                        if (m.Success) return m.Groups[1].Value;
                    }
                }
            }
            catch { }
            return null;
        }

        static byte[] Frame(byte id)
        {
            var b = new byte[] { Start, 3, ReadGroup, BaseId, id, 0, 0, 0 };
            int sum = Magic;
            for (int i = 0; i < b.Length - 1; i++) sum += b[i];
            b[b.Length - 1] = (byte)(sum % 256);
            return b;
        }

        static readonly Dictionary<byte, byte[]> Frames = new Dictionary<byte, byte[]>
        {
            { IdMcu, Frame(IdMcu) }, { IdMosfet, Frame(IdMosfet) }, { IdMotor, Frame(IdMotor) }, { IdTorque, Frame(IdTorque) },
        };

        public void Request(byte id)
        {
            var f = Frames[id];
            _sp.Write(f, 0, f.Length);
        }

        /// <summary>Reads whatever arrives for up to <paramref name="ms"/> milliseconds and reports complete replies.</summary>
        public void Pump(int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            do
            {
                int n;
                try { n = _sp.Read(_chunk, 0, _chunk.Length); }
                catch (TimeoutException) { continue; }
                for (int i = 0; i < n; i++) _buf.Add(_chunk[i]);
                Parse();
            } while (DateTime.UtcNow < until);
        }

        // Frames: 7E, len, group, device, payload(len), checksum.
        void Parse()
        {
            int i = 0;
            while (i < _buf.Count)
            {
                if (_buf[i] != Start) { i++; continue; }
                if (i + 1 >= _buf.Count) break;
                int total = _buf[i + 1] + 5;
                if (i + total > _buf.Count) break;   // wait for the rest
                if (_buf[i + 1] == 3 && _buf[i + 2] == (ReadGroup | 0x80) && _buf[i + 3] == 0x31 && OnValue != null)
                {
                    int raw = (_buf[i + 5] << 8) | _buf[i + 6];
                    if (raw >= 32768) raw -= 65536;
                    OnValue(_buf[i + 4], raw);
                }
                i += total;
            }
            _buf.RemoveRange(0, Math.Min(i, _buf.Count));
        }

        public void Dispose()
        {
            try { _sp.Close(); } catch { }
            _sp.Dispose();
        }

        /// <summary>One-shot read of the temperatures (for --probe).</summary>
        public static Temperatures ReadTemperatures(string port)
        {
            using (var link = new MozaLink(port))
            {
                var got = new Dictionary<byte, double>();
                link.OnValue = (id, raw) => { if (id >= IdMcu && id <= IdMotor) got[id] = raw / 100.0; };
                link.Request(IdMcu); link.Request(IdMosfet); link.Request(IdMotor);
                for (int i = 0; i < 8 && got.Count < 3; i++) link.Pump(100);
                if (got.Count < 3) return null;
                return new Temperatures { Mcu = got[IdMcu], Mosfet = got[IdMosfet], Motor = got[IdMotor] };
            }
        }
    }

    /// <summary>
    /// Turns the ~30 Hz torque samples into overlay values.
    ///  - Now: the torque low-passed with a ~150 ms time constant (the force you hold, without single-sample jitter
    ///    from kerbs and road texture). Follows the wheel within a few tenths of a second.
    ///  - Peak: the highest smoothed value of the last second, so one brief spike doesn't count as a peak.
    ///  - Clipping: share of raw samples in the last second at the base's limit (a spike that hits the limit did clip).
    /// </summary>
    public sealed class TorqueWindow
    {
        /// <summary>Peak torque of the base. R5 = 5.5 Nm. Samples at (or within 0.15 Nm of) it count as clipping.</summary>
        public double MaxNm = 5.5;
        /// <summary>Smoothing time constant in ms.</summary>
        public double SmoothMs = 150;
        const double ClipMargin = 0.15;

        // (time, raw |Nm|, smoothed |Nm|)
        readonly Queue<Sample> _q = new Queue<Sample>();
        struct Sample { public long Ms; public double Raw, Smooth; }
        double _ema;          // signed, so a quick reversal passes through zero like the wheel does
        long _lastMs = -1;

        public void Add(long ms, int raw)
        {
            double nm = (raw - MozaLink.TorqueZero) / 10.0;
            if (_lastMs < 0 || ms - _lastMs > 500) _ema = nm;   // first sample or after a gap: start from here
            else _ema += (1 - Math.Exp(-(ms - _lastMs) / SmoothMs)) * (nm - _ema);
            _lastMs = ms;
            _q.Enqueue(new Sample { Ms = ms, Raw = Math.Abs(nm), Smooth = Math.Abs(_ema) });
        }

        public void Trim(long nowMs, long windowMs = 1000)
        {
            while (_q.Count > 0 && _q.Peek().Ms < nowMs - windowMs) _q.Dequeue();
        }

        public int Count { get { return _q.Count; } }

        public TorqueStats Stats()
        {
            var s = new TorqueStats();
            if (_q.Count == 0) return s;
            int clipped = 0;
            foreach (var x in _q)
            {
                if (x.Smooth > s.PeakNm) s.PeakNm = x.Smooth;
                if (x.Raw >= MaxNm - ClipMargin) clipped++;
            }
            s.NowNm = Math.Abs(_ema);
            s.LoadPct = Math.Min(100, s.PeakNm / MaxNm * 100);
            s.ClipPct = 100.0 * clipped / _q.Count;
            s.Valid = true;
            return s;
        }
    }

    public struct TorqueStats
    {
        public bool Valid;
        public double NowNm, PeakNm, LoadPct, ClipPct;
        public override string ToString()
        {
            return Valid ? string.Format("Torque {0:0.0} Nm · peak {1:0.0} Nm · clipping {2:0} %", NowNm, PeakNm, ClipPct)
                         : "Torque —";
        }
    }
}
