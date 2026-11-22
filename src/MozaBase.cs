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

        static byte[] Frame(byte group, byte id)
        {
            var b = new byte[] { Start, 3, group, BaseId, id, 0, 0, 0 };
            int sum = Magic;
            for (int i = 0; i < b.Length - 1; i++) sum += b[i];
            b[b.Length - 1] = (byte)(sum % 256);
            return b;
        }

        static readonly Dictionary<byte, byte[]> Frames = new Dictionary<byte, byte[]>
        {
            { IdMcu, Frame(ReadGroup, IdMcu) }, { IdMosfet, Frame(ReadGroup, IdMosfet) }, { IdMotor, Frame(ReadGroup, IdMotor) },
            { IdTorque, Frame(ReadGroup, IdTorque) },
        };

        public void Request(byte id)
        {
            var f = Frames[id];
            _sp.Write(f, 0, f.Length);
        }

        // Settings (read group 40). Id 1 = rotation limit in half degrees (225 = 450°), as boxflat's "base-limit".
        const byte SettingsGroup = 40;
        public const byte SettingLimit = 1;
        static readonly byte[] LimitFrame = Frame(SettingsGroup, SettingLimit);

        /// <summary>Called for each settings reply: setting id, raw value.</summary>
        public Action<byte, int> OnSetting;

        public void RequestRotationLimit() { _sp.Write(LimitFrame, 0, LimitFrame.Length); }

        // Max torque setting ("MotGetSteer_PeakTorque" [40,18], % of the base's rated torque).
        public const byte SettingPeakTorque = 18;
        static readonly byte[] PeakTorqueFrame = Frame(SettingsGroup, SettingPeakTorque);
        public void RequestPeakTorque() { _sp.Write(PeakTorqueFrame, 0, PeakTorqueFrame.Length); }

        // Identity strings, read like Pit House does on connect (pure queries: the request carries only a sector
        // number 1..4, the reply that sector number plus 16 characters):
        //   group 7 = product type ("R5 Black # MOT-1-V03"), 8 = hardware version ("RS21-D05-HW BM-CU-V10");
        //   group 4 = firmware version (4 bytes).
        public const byte GroupProductType = 7, GroupHardwareVersion = 8, GroupFirmware = 4;

        public void RequestText(byte group, byte sector)
        {
            var b = new byte[] { Start, 1, group, BaseId, sector, 0 };
            int sum = Magic;
            for (int i = 0; i < b.Length - 1; i++) sum += b[i];
            b[b.Length - 1] = (byte)(sum % 256);
            _sp.Write(b, 0, b.Length);
        }

        public void RequestFirmware()
        {
            var b = new byte[] { Start, 4, GroupFirmware, BaseId, 0, 0, 0, 0, 0 };
            int sum = Magic;
            for (int i = 0; i < b.Length - 1; i++) sum += b[i];
            b[b.Length - 1] = (byte)(sum % 256);
            _sp.Write(b, 0, b.Length);
        }

        /// <summary>Called for other replies from the base: group (without the reply bit) and payload.</summary>
        public Action<byte, byte[]> OnFrame;

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

        // Frames: 7E, len, group, device, payload(len), checksum. Inside a frame the base sends a 0x7E byte twice (7E 7E);
        // a single 7E always starts a frame. Found 2026-11-22: the torque reply for +2.3 Nm (raw 523) has checksum 7E,
        // and before this was undone, each one made the parser read the doubled 7E as a new frame with length 7E and
        // swallow the next ~0.4 s of replies (7–11 % of the torque readings on a stage were lost).

        /// <summary>Result of <see cref="TakeFrame"/>.</summary>
        public enum Take { NeedMore, Frame, Broken }

        /// <summary>
        /// Reads the frame starting at buf[i] (a 7E), undoing doubled 7E bytes. frame = len, group, device,
        /// payload, checksum (without the start byte); next = index after it. Broken: a lone 7E (a new frame) came
        /// before this one was complete; next is that 7E.
        /// </summary>
        public static Take TakeFrame(IList<byte> buf, int i, out byte[] frame, out int next)
        {
            frame = null; next = i;
            var f = new List<byte>(16);
            int j = i + 1, need = -1;
            while (need < 0 || f.Count < need)
            {
                if (j >= buf.Count) return Take.NeedMore;
                byte b = buf[j];
                if (b == Start)
                {
                    if (j + 1 >= buf.Count) return Take.NeedMore;   // can't tell yet: doubled, or a new frame
                    if (buf[j + 1] != Start) { next = j; return Take.Broken; }
                    f.Add(Start); j += 2;
                }
                else { f.Add(b); j++; }
                if (need < 0) need = f[0] + 4;   // len, group, device, payload(len), checksum
            }
            frame = f.ToArray(); next = j;
            return Take.Frame;
        }

        /// <summary>The checksum the frame should have: (13 + 7E + every byte before the checksum) % 256.</summary>
        public static byte CheckSumOf(byte[] frame)
        {
            int sum = Magic + Start;
            for (int k = 0; k < frame.Length - 1; k++) sum += frame[k];
            return (byte)(sum & 0xFF);
        }

        int _frames, _badSums, _broken;

        void Parse()
        {
            int i = 0;
            while (i < _buf.Count)
            {
                if (_buf[i] != Start) { i++; continue; }
                byte[] f; int next;
                var r = TakeFrame(_buf, i, out f, out next);
                if (r == Take.NeedMore) break;
                i = next;
                if (r == Take.Broken) { if (++_broken <= 5) Log.Write("Frame cut off by a new frame (" + _broken + ")"); continue; }
                _frames++;
                if (CheckSumOf(f) != f[f.Length - 1])
                {
                    if (++_badSums <= 5)
                        Log.Write(string.Format("Frame checksum mismatch, dropped ({0} of {1} frames): 7E {2}(expected {3:X2})",
                            _badSums, _frames, BitConverter.ToString(f).Replace('-', ' ') + " ", CheckSumOf(f)));
                    continue;
                }
                Handle(f);
            }
            _buf.RemoveRange(0, Math.Min(i, _buf.Count));
        }

        void Handle(byte[] f)
        {
            byte len = f[0], group = f[1];
            bool fromBase = f[2] == 0x31;
            if (!fromBase) return;
            if (len == 3 && (group == (ReadGroup | 0x80) || group == (SettingsGroup | 0x80)))
            {
                int raw = (f[4] << 8) | f[5];
                if (raw >= 32768) raw -= 65536;
                if (group == (ReadGroup | 0x80)) { if (OnValue != null) OnValue(f[3], raw); }
                else if (OnSetting != null) OnSetting(f[3], raw);
            }
            else if ((group & 0x80) != 0 && group != 0x8E && OnFrame != null)   // 0x0E = debug text
            {
                var payload = new byte[len];
                Array.Copy(f, 3, payload, 0, len);
                OnFrame((byte)(group & 0x7F), payload);
            }
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
            _bMax = Math.Max(_bMax, Math.Abs(nm));
            _bCount++;
            if (Math.Abs(nm) >= MaxNm - ClipMargin) _bClipped++;
            _bRaw.Add((byte)Math.Min(255, Math.Abs(raw - MozaLink.TorqueZero)));
        }

        // Accumulators for the stage recorder (reset by TakeBucket).
        double _bMax; int _bCount, _bClipped;
        readonly List<byte> _bRaw = new List<byte>();

        /// <summary>
        /// One recorder point: sample count, clipped samples, highest raw |Nm|, current smoothed (signed), and every
        /// raw sample in order as |torque| in 0.1 Nm steps (so clip lengths can be measured per sample, not per point).
        /// </summary>
        public struct Bucket { public int Samples, Clipped; public double MaxRawNm, SmoothNm; public byte[] Raw; }

        /// <summary>What happened since the previous call.</summary>
        public Bucket TakeBucket()
        {
            var b = new Bucket { Samples = _bCount, Clipped = _bClipped, MaxRawNm = _bMax, SmoothNm = _ema, Raw = _bRaw.ToArray() };
            _bMax = 0; _bCount = 0; _bClipped = 0; _bRaw.Clear();
            return b;
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
