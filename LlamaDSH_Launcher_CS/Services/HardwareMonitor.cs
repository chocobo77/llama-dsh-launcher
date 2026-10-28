using System;
using System.Collections.Generic;
using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// 跨厂商硬件传感采集(社区成熟方案 LibreHardwareMonitorLib, FanControl/Sidebar Diagnostics 同源)。
/// 单例持有 Computer, 每次采样 Update() 后聚合 NVIDIA/AMD/Intel GPU 的温度/功率/风扇/频率/负载。
/// 非管理员时部分传感器(尤其 AMD/Intel 依赖内核驱动)不可见, 返回值按缺失处理, 由上层回退命令行通道。
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
	private static readonly HardwareMonitor _inst = new();

	public static HardwareMonitor Instance => _inst;

	private Computer? _computer;

	private readonly object _lock = new();

	private DateTime _lastUpdateUtc = DateTime.MinValue;

	private HardwareMonitor()
	{
		try
		{
			_computer = new Computer
			{
				IsGpuEnabled = true,
				IsCpuEnabled = true,
				IsMemoryEnabled = false,
				IsMotherboardEnabled = false,
				IsStorageEnabled = true,
				IsNetworkEnabled = false,
				IsControllerEnabled = false,
				IsPsuEnabled = false
			};
			_computer.Open();
		}
		catch
		{
			_computer = null;
		}
	}

	/// <summary>聚合所有 GPU 的指标键: gpu_util/gpu_temp/gpu_temp_src/power_w/fan_pct/clock_mhz/cpu_temp/cpu_temp_src。minIntervalMs 控制硬件刷新频率。</summary>
	public Dictionary<string, string> Sample(int minIntervalMs = 800)
	{
		Dictionary<string, string> r = new();
		Computer? c = _computer;
		if (c == null)
		{
			return r;
		}
		lock (_lock)
		{
			if ((DateTime.UtcNow - _lastUpdateUtc).TotalMilliseconds < minIntervalMs)
			{
				return r;
			}
			_lastUpdateUtc = DateTime.UtcNow;
		}
		try
		{
			// 多卡聚合: 负载/风扇/频率均值, 功率求和, 温度取最高
			double utilSum = 0, utilN = 0, powerSum = 0, fanSum = 0, fanN = 0, clockSum = 0, clockN = 0, tempMax = 0;
			bool anyGpu = false;
			string? cpuTemp = null;
			foreach (IHardware hw in c.Hardware)
			{
				try { hw.Update(); } catch { }
				if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
				{
					anyGpu = true;
					string vendor = hw.HardwareType switch
					{
						HardwareType.GpuNvidia => "nvidia",
						HardwareType.GpuAmd => "amd",
						_ => "intel"
					};
					foreach (ISensor s in hw.Sensors)
					{
						if (!s.Value.HasValue)
						{
							continue;
						}
						double v = s.Value.Value;
						switch (s.SensorType)
						{
							case SensorType.Temperature:
								if (v > 0 && v < 150 && v > tempMax)
								{
									tempMax = v;
									r["gpu_temp_src"] = vendor;
								}
								break;
							case SensorType.Power:
								if (v > 0)
								{
									powerSum += v;
								}
								break;
							case SensorType.Load:
								if (v >= 0)
								{
									utilSum += v;
									utilN += 1;
								}
								break;
							// LHM 的 Control 是风扇目标百分比, Fan 是转速; 百分比口径才与 UI 对齐
							case SensorType.Control:
								if (v >= 0 && v <= 100)
								{
									fanSum += v;
									fanN += 1;
								}
								break;
							case SensorType.Clock:
								// "GPU Core" 核心频率; 显存/总线频率不计入
								if (v > 0 && s.Name.Contains("core", StringComparison.OrdinalIgnoreCase))
								{
									clockSum += v;
									clockN += 1;
								}
								break;
						}
					}
				}
				else if (hw.HardwareType == HardwareType.Cpu && cpuTemp == null)
				{
					// CPU 温度: Package 优先, 否则任意核心温度
					double best = -1;
					foreach (ISensor s in hw.Sensors)
					{
						if (s.SensorType != SensorType.Temperature || !s.Value.HasValue)
						{
							continue;
						}
						double v = s.Value.Value;
						if (v <= 0 || v >= 120)
						{
							continue;
						}
						if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
						{
							best = v;
							break;
						}
						if (best < 0 || v > best)
						{
							best = v;
						}
					}
					if (best > 0)
					{
						cpuTemp = best.ToString("F1", CultureInfo.InvariantCulture);
					}
				}
			}
			if (anyGpu)
			{
				if (utilN > 0)
				{
					r["gpu_util"] = (utilSum / utilN).ToString("F1", CultureInfo.InvariantCulture);
				}
				if (tempMax > 0)
				{
					r["gpu_temp"] = tempMax.ToString("F1", CultureInfo.InvariantCulture);
				}
				if (powerSum > 0)
				{
					r["power_w"] = powerSum.ToString("F1", CultureInfo.InvariantCulture);
				}
				if (fanN > 0)
				{
					r["fan_pct"] = (fanSum / fanN).ToString("F1", CultureInfo.InvariantCulture);
				}
				if (clockN > 0)
				{
					r["clock_mhz"] = (clockSum / clockN).ToString("F0", CultureInfo.InvariantCulture);
				}
			}
			if (cpuTemp != null)
			{
				r["cpu_temp"] = cpuTemp;
				r["cpu_temp_src"] = "lhm";
			}
			// SSD 温度(Strata Readings): 存储硬件取最高温度
			double ssdMax = 0;
			foreach (IHardware hw in c.Hardware)
			{
				if (hw.HardwareType != HardwareType.Storage)
				{
					continue;
				}
				try { hw.Update(); } catch { }
				foreach (ISensor s in hw.Sensors)
				{
					if (s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value > ssdMax && s.Value.Value < 120)
					{
						ssdMax = s.Value.Value;
					}
				}
			}
			if (ssdMax > 0)
			{
				r["ssd_temp"] = ssdMax.ToString("F1", CultureInfo.InvariantCulture);
			}
		}
		catch
		{
		}
		return r;
	}

	public void Dispose()
	{
		try { _computer?.Close(); } catch { }
		_computer = null;
	}
}
