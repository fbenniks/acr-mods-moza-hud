using System;
using System.Runtime.InteropServices;

namespace MozaHud
{
    /// <summary>
    /// Reads the wheel's steering axis through the classic Windows joystick API (winmm). The base reports the
    /// axis over its whole configured rotation (e.g. 450°), so ±1 is the base's soft lock. Used to recognise
    /// "clipping" that is really the soft lock pushing back.
    /// </summary>
    public sealed class WheelAxis
    {
        const int MozaVendor = 0x346E;
        const int JOY_RETURNALL = 0xFF;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct JOYCAPSW
        {
            public ushort wMid, wPid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax, wNumButtons, wPeriodMin, wPeriodMax,
                        wRmin, wRmax, wUmin, wUmax, wVmin, wVmax, wCaps, wMaxAxes, wNumAxes, wMaxButtons;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOYINFOEX
        {
            public int dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos, dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
        }

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int joyGetDevCapsW(IntPtr id, ref JOYCAPSW caps, int size);
        [DllImport("winmm.dll")] static extern int joyGetPosEx(int id, ref JOYINFOEX info);
        [DllImport("winmm.dll")] static extern int joyGetNumDevs();

        int _id = -1;
        uint _min, _max;
        DateTime _nextFind = DateTime.MinValue;

        public string DeviceName { get; private set; }

        bool Find()
        {
            int n = Math.Min(joyGetNumDevs(), 16);
            for (int i = 0; i < n; i++)
            {
                var caps = new JOYCAPSW();
                if (joyGetDevCapsW((IntPtr)i, ref caps, Marshal.SizeOf(typeof(JOYCAPSW))) != 0) continue;
                if (caps.wMid != MozaVendor) continue;
                var info = new JOYINFOEX { dwSize = Marshal.SizeOf(typeof(JOYINFOEX)), dwFlags = JOY_RETURNALL };
                if (joyGetPosEx(i, ref info) != 0) continue;   // listed but not connected
                _id = i; _min = caps.wXmin; _max = caps.wXmax; DeviceName = caps.szPname;
                return true;
            }
            return false;
        }

        /// <summary>Steering position -1 (full left) … +1 (full right), or null when no MOZA wheel is found.</summary>
        public double? Read()
        {
            if (_id < 0)
            {
                if (DateTime.UtcNow < _nextFind) return null;
                _nextFind = DateTime.UtcNow.AddSeconds(5);
                if (!Find()) return null;
            }
            var info = new JOYINFOEX { dwSize = Marshal.SizeOf(typeof(JOYINFOEX)), dwFlags = JOY_RETURNALL };
            if (joyGetPosEx(_id, ref info) != 0) { _id = -1; return null; }
            double span = (double)_max - _min;
            if (span <= 0) return null;
            return Math.Max(-1, Math.Min(1, ((info.dwXpos - _min) / span) * 2 - 1));
        }
    }
}
