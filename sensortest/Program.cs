using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        bool verbose = args.Any(a => a == "-v");
        Computer computer = null;
        try
        {
            computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsBatteryEnabled = true,
                IsPsuEnabled = true,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
            };
            computer.Open();

            Console.WriteLine("=== Enumerated hardware groups ===");
            if (computer.Hardware.Count == 0)
                Console.WriteLine("  (none detected)");

            int tempFound = 0;
            foreach (var hw in computer.Hardware)
            {
                hw.Update();
                Console.WriteLine($"[{hw.HardwareType}] {hw.Name}  (id={hw.Identifier})");
                var sensors = hw.Sensors;
                if (verbose)
                {
                    foreach (var s in sensors)
                        Console.WriteLine($"    - {s.SensorType,-14} {s.Name,-24} value={(s.Value.HasValue ? s.Value.Value.ToString("F2") : "null")}");
                }
                foreach (var s in sensors)
                {
                    if (s.SensorType == SensorType.Temperature && s.Value.HasValue)
                    {
                        tempFound++;
                        Console.WriteLine($"    ** TEMP {s.Name}: {s.Value.Value:F1} C");
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Total hardware groups: {computer.Hardware.Count}");
            Console.WriteLine($"Total temperature sensors with values: {tempFound}");
            if (tempFound == 0)
                Console.WriteLine("RESULT: NO temperature sensors readable in this environment.");
            else
                Console.WriteLine("RESULT: temperature sensors ARE readable.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION: " + ex);
            return 1;
        }
        finally
        {
            try { computer?.Close(); } catch { }
        }
    }
}
