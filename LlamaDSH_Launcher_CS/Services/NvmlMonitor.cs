using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// NVML 直采(Strata serve/telemetry.py 同源): nvml.dll 由 NVIDIA 驱动自带,
/// 提供 nvidia-smi 拿不到/常返 N/A 的 PCIe 吞吐(nvmlDeviceGetPcieThroughput, KB/s),
/// 以及利用率/显存/温度/功率/功耗上限/频率/风扇/链路代数宽度。多卡聚合口径同 UI。
/// 初始化失败(无驱动/非 NVIDIA)时 TryFill 返回 false, 上层回退 nvidia-smi。
/// </summary>
public static partial class NvmlMonitor
{
	private static bool _tried;

	private static bool _ok;

	private static IntPtr _lib;

	private static readonly object Lock = new();

	[DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
	private static extern IntPtr LoadLibrary(string name);

	[UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private delegate int IntFn();

	// NVML 官方头为 cdecl; x64 下 cdecl 与 stdcall 栈清理等价, 本程序仅 x64 部署
	private static T GetFn<T>(string name) where T : Delegate
	{
	 IntPtr p = GetProcAddressCompat(name);
		return p == IntPtr.Zero ? null : (T)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
	}

	private static IntPtr GetProcAddressCompat(string name)
	{
		IntPtr p = GetProcAddress(_lib, name);
		if (p == IntPtr.Zero)
		{
			// 旧驱动只导出无 _v2 后缀版本
			p = GetProcAddress(_lib, name.EndsWith("_v2", StringComparison.Ordinal) ? name[..^3] : name + "_v2");
		}
		return p;
	}

	[DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
	private static extern IntPtr GetProcAddress(IntPtr h, string name);

	[StructLayout(LayoutKind.Sequential)]
	private struct NvmlUtilization
	{
		public uint Gpu;
		public uint Memory;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NvmlMemory
	{
		public ulong Total;
		public ulong Used;
		public ulong Free;
	}

	private static double _lastTry;

	private static void EnsureInit()
	{
		if (_ok)
		{
			return;
		}
		// 失败 60s 冷却后重试(驱动可能后装); NVML 常见位置: System32 或旧版驱动目录
		if (_tried && Environment.TickCount64 - _lastTry < 60000)
		{
			return;
		}
		_tried = true;
		_lastTry = Environment.TickCount64;
		try
		{
			string[] candidates =
			{
				"nvml.dll",
				System.IO.Path.Combine(Environment.SystemDirectory, "nvml.dll"),
				@"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll"
			};
			foreach (string path in candidates)
			{
				_lib = LoadLibrary(path);
				if (_lib != IntPtr.Zero)
				{
					break;
				}
			}
			if (_lib == IntPtr.Zero)
			{
				AppLog.WriteError("NVML 加载失败: 未找到 nvml.dll(PCIe 吞吐/功率将回退 nvidia-smi)");
				return;
			}
			var init = GetFn<IntFn>("nvmlInit_v2");
			if (init == null || init() != 0)
			{
				_lib = IntPtr.Zero;
				AppLog.WriteError("NVML 初始化失败: nvmlInit 非零返回");
				return;
			}
			_ok = true;
		}
		catch (Exception ex)
		{
			_ok = false;
			_lib = IntPtr.Zero;
			AppLog.WriteError("NVML 初始化异常: " + ex.Message);
		}
	}

	[UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private delegate int DeviceCount(out uint count);

	[UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private delegate int GetHandle(uint index, out IntPtr dev);

	[UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private delegate int GetUint(IntPtr dev, out uint value);

	[UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private delegate int GetUintWith(IntPtr dev, uint arg, out uint value);

	[UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private delegate int GetUtil(IntPtr dev, out NvmlUtilization u);

	[UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private delegate int GetMem(IntPtr dev, out NvmlMemory m);

	[UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private delegate int GetName(IntPtr dev, [Out] byte[] name, uint len);

	[UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private delegate int SysVersion([Out] byte[] version, uint length);

	/// <summary>聚合全部 GPU: 均值(利用率/风扇/频率), 求和(显存/功率/上限/吞吐), 最热温度, 设备0 的链路代数宽度与名称。</summary>
	public static bool TryFill(Dictionary<string, string> r)
	{
		lock (Lock)
		{
			EnsureInit();
			if (!_ok)
			{
				return false;
			}
			try
			{
				var getCount = GetFn<DeviceCount>("nvmlDeviceGetCount_v2");
				var getHandle = GetFn<GetHandle>("nvmlDeviceGetHandleByIndex_v2");
				if (getCount == null || getHandle == null || getCount(out uint n) != 0 || n == 0)
				{
					return false;
				}
				var missing = new[] { "nvmlDeviceGetUtilizationRates", "nvmlDeviceGetMemoryInfo", "nvmlDeviceGetTemperature", "nvmlDeviceGetPowerUsage", "nvmlDeviceGetEnforcedPowerLimit", "nvmlDeviceGetCurrPcieLinkGeneration", "nvmlDeviceGetCurrPcieLinkWidth", "nvmlDeviceGetPcieThroughput", "nvmlDeviceGetClockInfo", "nvmlDeviceGetFanSpeed", "nvmlDeviceGetName" }.Where(f => GetProcAddress(_lib, f) == IntPtr.Zero && GetProcAddress(_lib, f.EndsWith("_v2", StringComparison.Ordinal) ? f[..^3] : f + "_v2") == IntPtr.Zero).ToList();
				if (missing.Count > 0)
				{
					AppLog.WriteError("NVML 缺失导出: " + string.Join(",", missing));
				}
				var getUtil = GetFn<GetUtil>("nvmlDeviceGetUtilizationRates");
				var getMem = GetFn<GetMem>("nvmlDeviceGetMemoryInfo");
				var getTemp = GetFn<GetUintWith>("nvmlDeviceGetTemperature");
				var getPower = GetFn<GetUint>("nvmlDeviceGetPowerUsage");
				var getLimit = GetFn<GetUint>("nvmlDeviceGetEnforcedPowerLimit");
				var getGen = GetFn<GetUint>("nvmlDeviceGetCurrPcieLinkGeneration");
				var getGenMax = GetFn<GetUint>("nvmlDeviceGetMaxPcieLinkGeneration");
				var getWidth = GetFn<GetUint>("nvmlDeviceGetCurrPcieLinkWidth");
				var getRx = GetFn<GetUintWith>("nvmlDeviceGetPcieThroughput");
				var getClock = GetFn<GetUintWith>("nvmlDeviceGetClockInfo");
				var getFan = GetFn<GetUint>("nvmlDeviceGetFanSpeed");
				var getName = GetFn<GetName>("nvmlDeviceGetName");
				double utilS = 0, utilN = 0, fanS = 0, fanN = 0, clockS = 0, clockN = 0;
				ulong memUsed = 0, memTotal = 0;
				double powerS = 0, limitS = 0, rxKbS = 0, tempMax = 0;
				string? gen = null, width = null, name0 = null;
				for (uint i = 0; i < n; i++)
				{
					if (getHandle(i, out IntPtr dev) != 0)
					{
						continue;
					}
					if (getUtil != null && getUtil(dev, out var u) == 0)
					{
						utilS += u.Gpu;
						utilN++;
					}
					if (getMem != null && getMem(dev, out var m) == 0)
					{
						memUsed += m.Used;
						memTotal += m.Total;
					}
					if (getTemp != null && getTemp(dev, 0, out var t) == 0 && t > tempMax && t < 150)
					{
						tempMax = t;
					}
					if (getPower != null && getPower(dev, out var mw) == 0 && mw > 0)
					{
						powerS += mw / 1000.0;
					}
					if (getLimit != null && getLimit(dev, out var lim) == 0 && lim > 0)
					{
						limitS += lim / 1000.0;
					}
					if (getFan != null && getFan(dev, out var f) == 0 && f <= 100)
					{
						fanS += f;
						fanN++;
					}
					if (getClock != null && getClock(dev, 0, out var ck) == 0 && ck > 0)
					{
						clockS += ck;
						clockN++;
					}
					if (gen == null)
					{
						// 当前代数空闲时可能降为低功耗值, 取 max(当前, 最大) 与 Strata 一致
						if (getGen != null && getGen(dev, out var g) == 0 && g > 0)
						{
							gen = g.ToString(CultureInfo.InvariantCulture);
						}
						else if (getGenMax != null && getGenMax(dev, out var gm) == 0 && gm > 0)
						{
							gen = gm.ToString(CultureInfo.InvariantCulture);
						}
						if (getWidth != null && getWidth(dev, out var w) == 0 && w > 0)
						{
							width = w.ToString(CultureInfo.InvariantCulture);
						}
					}
					if (getRx != null && getRx(dev, 1, out var rx) == 0)
					{
						rxKbS += rx;
					}
					if (name0 == null && getName != null)
					{
						var buf = new byte[96];
						if (getName(dev, buf, 96) == 0)
						{
							name0 = System.Text.Encoding.UTF8.GetString(buf, 0, Array.IndexOf(buf, (byte)0) is int z and >= 0 ? z : 0).Trim();
						}
					}
				}
				string Inv = CultureInfo.InvariantCulture.Name ?? "";
				if (utilN > 0)
				{
					r["gpu_util"] = (utilS / utilN).ToString("F1", CultureInfo.InvariantCulture);
				}
				// 显存全部释放(used=0)是合法读数, 只要总量可读就写键
				if (memTotal > 0)
				{
					r["vram_used"] = memUsed.ToString(CultureInfo.InvariantCulture);
					r["vram_total"] = memTotal.ToString(CultureInfo.InvariantCulture);
				}
				if (tempMax > 0)
				{
					r["gpu_temp"] = tempMax.ToString("F1", CultureInfo.InvariantCulture);
					r["gpu_temp_src"] = "nvidia";
				}
				if (powerS > 0)
				{
					r["power_w"] = powerS.ToString("F1", CultureInfo.InvariantCulture);
				}
				if (limitS > 0)
				{
					r["power_limit_w"] = limitS.ToString("F1", CultureInfo.InvariantCulture);
				}
				if (fanN > 0)
				{
					r["fan_pct"] = (fanS / fanN).ToString("F1", CultureInfo.InvariantCulture);
				}
				if (clockN > 0)
				{
					r["clock_mhz"] = (clockS / clockN).ToString("F0", CultureInfo.InvariantCulture);
				}
				if (gen != null)
				{
					r["pcie_gen"] = gen;
				}
				if (width != null)
				{
					r["pcie_width"] = width;
				}
				// PCIe 空闲吞吐=0 是合法读数, 去掉 >0 门
				// 单卡循环逐卡累加, 走到这里即至少有一张卡; PCIe 空闲吞吐=0 合法, 不设 >0 门
				if (r.ContainsKey("gpu_count"))
				{
					r["pcie_rx_kbs"] = rxKbS.ToString("F0", CultureInfo.InvariantCulture);
				}
				r["gpu_count"] = n.ToString(CultureInfo.InvariantCulture);
				if (!string.IsNullOrWhiteSpace(name0))
				{
					r["gpu_name"] = name0;
				}
				return true;
			}
			catch
			{
				return false;
			}
		}
	}
}

/// <summary>单卡读数(GpuPanel 用)。</summary>
public sealed class GpuDevInfo
{
	public int Index;
	public string Name = "";
	public double Util;
	public double MemUsedMb;
	public double MemTotalMb;
	public double Temp;
	public double Power;
	public double Limit;
	public double Fan;
	public double Clock;
	public int? Gen;
	public int? Width;
	public string Pstate = "";
	public string Driver = "";
}

partial class NvmlMonitor
{
	private static string? _driver;

	public static List<GpuDevInfo> SamplePerDevice()
	{
		var list = new List<GpuDevInfo>();
		lock (Lock)
		{
			EnsureInit();
			if (!_ok)
			{
				return list;
			}
			try
			{
				var getCount = GetFn<DeviceCount>("nvmlDeviceGetCount_v2");
				var getHandle = GetFn<GetHandle>("nvmlDeviceGetHandleByIndex_v2");
				if (getCount == null || getHandle == null || getCount(out uint n) != 0)
				{
					return list;
				}
				var getUtil = GetFn<GetUtil>("nvmlDeviceGetUtilizationRates");
				var getMem = GetFn<GetMem>("nvmlDeviceGetMemoryInfo");
				var getTemp = GetFn<GetUintWith>("nvmlDeviceGetTemperature");
				var getPower = GetFn<GetUint>("nvmlDeviceGetPowerUsage");
				var getLimit = GetFn<GetUint>("nvmlDeviceGetEnforcedPowerLimit");
				var getGen = GetFn<GetUint>("nvmlDeviceGetCurrPcieLinkGeneration");
				var getGenMax = GetFn<GetUint>("nvmlDeviceGetMaxPcieLinkGeneration");
				var getWidth = GetFn<GetUint>("nvmlDeviceGetCurrPcieLinkWidth");
				var getClock = GetFn<GetUintWith>("nvmlDeviceGetClockInfo");
				var getFan = GetFn<GetUint>("nvmlDeviceGetFanSpeed");
				var getName = GetFn<GetName>("nvmlDeviceGetName");
				var getPstate = GetFn<GetUint>("nvmlDeviceGetPerformanceState");
				if (_driver == null)
				{
					var sysVer = GetFn<SysVersion>("nvmlSystemGetDriverVersion");
					if (sysVer != null)
					{
						var buf = new byte[80];
						if (sysVer(buf, 80) == 0)
						{
							int z = Array.IndexOf(buf, (byte)0);
							_driver = z >= 0 ? System.Text.Encoding.UTF8.GetString(buf, 0, z) : "";
						}
					}
					_driver ??= "";
				}
				for (uint i = 0; i < n; i++)
				{
					if (getHandle(i, out IntPtr dev) != 0)
					{
						continue;
					}
					var d = new GpuDevInfo { Index = (int)i, Driver = _driver };
					if (getName != null)
					{
						var buf = new byte[96];
						if (getName(dev, buf, 96) == 0)
						{
							int z = Array.IndexOf(buf, (byte)0);
							d.Name = z >= 0 ? System.Text.Encoding.UTF8.GetString(buf, 0, z) : "";
						}
					}
					if (getUtil != null && getUtil(dev, out var u) == 0)
					{
						d.Util = u.Gpu;
					}
					if (getMem != null && getMem(dev, out var m) == 0)
					{
						d.MemUsedMb = m.Used / 1048576.0;
						d.MemTotalMb = m.Total / 1048576.0;
					}
					if (getTemp != null && getTemp(dev, 0, out var t) == 0)
					{
						d.Temp = t;
					}
					if (getPower != null && getPower(dev, out var mw) == 0)
					{
						d.Power = mw / 1000.0;
					}
					if (getLimit != null && getLimit(dev, out var lim) == 0)
					{
						d.Limit = lim / 1000.0;
					}
					if (getFan != null && getFan(dev, out var f) == 0 && f <= 100)
					{
						d.Fan = f;
					}
					if (getClock != null && getClock(dev, 0, out var ck) == 0)
					{
						d.Clock = ck;
					}
					if (getGen != null && getGen(dev, out var g) == 0 && g > 0)
					{
						d.Gen = (int)g;
					}
					else if (getGenMax != null && getGenMax(dev, out var gm) == 0 && gm > 0)
					{
						d.Gen = (int)gm;
					}
					if (getWidth != null && getWidth(dev, out var w) == 0 && w > 0)
					{
						d.Width = (int)w;
					}
					if (getPstate != null && getPstate(dev, out var ps) == 0)
					{
						d.Pstate = "P" + ps;
					}
					list.Add(d);
				}
			}
			catch
			{
			}
			return list;
		}
	}
}

partial class NvmlMonitor
{
	private static double _smiNextTry;

	/// <summary>NVML 不可用时的回退: nvidia-smi 每卡一行解析(含驱动/P-State)。</summary>
	public static List<GpuDevInfo> SampleViaNvidiaSmi()
	{
		var list = new List<GpuDevInfo>();
		// 失败节流: NVML 已不可用的机器上避免每 3s 一拍都拉起 nvidia-smi(最长 5s)
		if (Environment.TickCount64 < _smiNextTry)
		{
			return list;
		}
		try
		{
			var r = ProcessRunner.Run(
				"nvidia-smi",
				new[] { "--query-gpu=index,name,driver_version,pstate,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw,enforced.power.limit,fan.speed,clocks.current.graphics,pcie.link.gen.current,pcie.link.width.current", "--format=csv,noheader,nounits" },
				5000);
			if (!r.Started || r.TimedOut)
			{
				_smiNextTry = Environment.TickCount64 + 30000;
				return list;
			}
			foreach (var raw in r.StdOut.Split('\n'))
			{
				var parts = raw.TrimEnd('\r').Split(',');
				if (parts.Length < 14)
				{
					continue;
				}
				var d = new GpuDevInfo
				{
					Index = int.TryParse(parts[0].Trim(), out var ix) ? ix : 0,
					Name = parts[1].Trim(),
					Driver = parts[2].Trim(),
					Pstate = parts[3].Trim(),
				};
				double Num(int i) => double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
				d.Util = Num(4);
				d.MemUsedMb = Num(5);
				d.MemTotalMb = Num(6);
				d.Temp = Num(7);
				d.Power = Num(8);
				d.Limit = Num(9);
				d.Fan = Num(10);
				d.Clock = Num(11);
				if (double.TryParse(parts[12].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var g) && g > 0)
				{
					d.Gen = (int)g;
				}
				if (double.TryParse(parts[13].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0)
				{
					d.Width = (int)w;
				}
				list.Add(d);
			}
		}
		catch
		{
			_smiNextTry = Environment.TickCount64 + 30000;
		}
		return list;
	}
}
