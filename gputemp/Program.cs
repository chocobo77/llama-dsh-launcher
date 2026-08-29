using System;
using System.Threading;
using LibreHardwareMonitor.Hardware;

bool loop = args.Length > 0 && args[0] == "--loop";
bool diag = args.Length > 0 && args[0] == "--diag";
var computer = new Computer { IsGpuEnabled = true };
computer.Open();
try
{
    do
    {
        foreach (var hw in computer.Hardware)
        {
            hw.Update();
            var gpuType = "Unknown";
            try { gpuType = hw.HardwareType.ToString(); } catch { }
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType == SensorType.Temperature && s.Value is float v)
                    Console.WriteLine($"[{gpuType}] {s.Name}={v:F1}");
                else if (diag)
                    Console.WriteLine($"[DIAG] [{gpuType}] {s.Name} ({s.SensorType}) = {s.Value}");
            }
            foreach (var sub in hw.SubHardware)
            {
                sub.Update();
                foreach (var s in sub.Sensors)
                {
                    if (s.SensorType == SensorType.Temperature && s.Value is float v2)
                        Console.WriteLine($"[{gpuType}/{sub.Name}] {s.Name}={v2:F1}");
                    else if (diag)
                        Console.WriteLine($"[DIAG] [{gpuType}/{sub.Name}] {s.Name} ({s.SensorType}) = {s.Value}");
                }
            }
        }
        if (loop)
        {
            Console.Out.Flush();
            Thread.Sleep(2000);
        }
    } while (loop);
}
finally
{
    computer.Close();
}
