using System;
using System.Diagnostics;

namespace MozaHud
{
    /// <summary>
    /// Publishes the temperatures as Windows performance counters, which MSI Afterburner's PerfCounter plugin reads.
    /// Values are stored in 1/100 °C (integer counters); the Afterburner source divides by 100.
    /// Creating the category needs admin rights once (see AfterburnerSetup); writing does not.
    /// </summary>
    public sealed class Counters : IDisposable
    {
        public const string Category = "MOZA Wheelbase";
        public const string Motor = "Motor temperature";
        public const string Mosfet = "MOSFET temperature";
        public const string Mcu = "MCU temperature";

        PerformanceCounter _motor, _mosfet, _mcu;

        public static bool Registered()
        {
            try { return PerformanceCounterCategory.Exists(Category); } catch { return false; }
        }

        /// <summary>Creates (or recreates) the category. Needs admin rights.</summary>
        public static void Register()
        {
            if (PerformanceCounterCategory.Exists(Category))
                PerformanceCounterCategory.Delete(Category);
            var c = new CounterCreationDataCollection
            {
                new CounterCreationData(Motor, "Wheelbase motor temperature in 1/100 °C", PerformanceCounterType.NumberOfItems32),
                new CounterCreationData(Mosfet, "Wheelbase MOSFET (motor driver) temperature in 1/100 °C", PerformanceCounterType.NumberOfItems32),
                new CounterCreationData(Mcu, "Wheelbase MCU (controller chip) temperature in 1/100 °C", PerformanceCounterType.NumberOfItems32),
            };
            PerformanceCounterCategory.Create(Category, "MOZA wheelbase temperatures from MozaHud", PerformanceCounterCategoryType.SingleInstance, c);
        }

        public static void Unregister()
        {
            if (PerformanceCounterCategory.Exists(Category))
                PerformanceCounterCategory.Delete(Category);
        }

        public Counters()
        {
            _motor = new PerformanceCounter(Category, Motor, false);
            _mosfet = new PerformanceCounter(Category, Mosfet, false);
            _mcu = new PerformanceCounter(Category, Mcu, false);
        }

        public void Set(Temperatures t)
        {
            _motor.RawValue = (long)Math.Round(t.Motor * 100);
            _mosfet.RawValue = (long)Math.Round(t.Mosfet * 100);
            _mcu.RawValue = (long)Math.Round(t.Mcu * 100);
        }

        public void Clear()
        {
            _motor.RawValue = 0; _mosfet.RawValue = 0; _mcu.RawValue = 0;
        }

        public void Dispose()
        {
            try { Clear(); } catch { }
            foreach (var c in new[] { _motor, _mosfet, _mcu }) { try { c.RemoveInstance(); } catch { } c.Dispose(); }
        }
    }
}
