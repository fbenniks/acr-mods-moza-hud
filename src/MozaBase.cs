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
    /// Reads the wheelbase temperatures over the MOZA serial protocol (as documented by boxflat, data/serial.yml).
    /// Request frame: 7E, payload length, read group, device id, command id, payload..., checksum.
    /// Checksum = (13 + sum of all previous bytes) % 256.
    /// Temperatures: group 43, base device 19, ids 4 (MCU) 5 (MOSFET) 6 (motor), 2-byte big-endian value in 1/100 °C.
    /// The reply has bit 7 set in the group (43 -> 0xAB) and the device id nibbles swapped (0x13 -> 0x31).
    /// </summary>
    public static class MozaBase
    {
        const byte Start = 0x7E, Magic = 13, ReadGroup = 43, BaseId = 19;
        const byte IdMcu = 4, IdMosfet = 5, IdMotor = 6;

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

        /// <summary>
        /// Opens the port, asks for the three temperatures and closes it again, so Pit House can still use the port
        /// in between. Throws UnauthorizedAccessException when another program (Pit House) holds the port.
        /// Returns null when the base did not answer.
        /// </summary>
        public static Temperatures Read(string port)
        {
            using (var sp = new SerialPort(port, 115200))
            {
                sp.ReadTimeout = 100;
                sp.WriteTimeout = 500;
                sp.DtrEnable = true;
                sp.Open();

                foreach (var id in new[] { IdMcu, IdMosfet, IdMotor })
                {
                    var f = Frame(id);
                    sp.Write(f, 0, f.Length);
                }

                var got = new Dictionary<byte, double>();
                var buf = new List<byte>();
                var chunk = new byte[512];
                var until = DateTime.UtcNow.AddMilliseconds(800);
                while (got.Count < 3 && DateTime.UtcNow < until)
                {
                    int n;
                    try { n = sp.Read(chunk, 0, chunk.Length); }
                    catch (TimeoutException) { continue; }
                    for (int i = 0; i < n; i++) buf.Add(chunk[i]);
                    Parse(buf, got);
                }

                if (got.Count < 3) return null;
                return new Temperatures { Mcu = got[IdMcu], Mosfet = got[IdMosfet], Motor = got[IdMotor] };
            }
        }

        // Frames: 7E, len, group, device, id, value..., checksum. The base also sends debug log text frames
        // (group 0x0E) on this port; those are skipped by their length/group.
        static void Parse(List<byte> buf, Dictionary<byte, double> got)
        {
            int i = 0;
            while (i < buf.Count)
            {
                if (buf[i] != Start) { i++; continue; }
                if (i + 1 >= buf.Count) break;
                int len = buf[i + 1];
                int total = len + 5;               // start, len, group, device, payload(len), checksum
                if (i + total > buf.Count) break;  // wait for the rest
                if (len == 3 && buf[i + 2] == (ReadGroup | 0x80) && buf[i + 3] == 0x31)
                {
                    byte id = buf[i + 4];
                    int raw = (buf[i + 5] << 8) | buf[i + 6];
                    double c = raw / 100.0;
                    if ((id == IdMcu || id == IdMosfet || id == IdMotor) && c > -40 && c < 200)
                        got[id] = c;
                }
                i += total;
            }
            buf.RemoveRange(0, Math.Min(i, buf.Count));
        }
    }
}
