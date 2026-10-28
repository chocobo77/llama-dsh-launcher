using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace LlamaDSH_Launcher.Services;

public class GpuMonitor
{
	private const double QueryTtl = 5.0;

	private const double VendorTtl = 300.0;

	private const double PsFallbackTtl = 60.0;

	private Process? _gpuTempProc;

	private Thread? _gpuTempThread;

	private double? _cachedGpuTemp;

	private string? _cachedGpuTempSrc;

	private double _lastGpuTempTime;

	private readonly object _lock = new object();

	private string? _vendor;

	private double _vendorProbeTime;

	private Dictionary<string, string>? _gpuCache;

	private double _gpuCacheTime;

	private double _lastPsFallbackTime = -60.0;

	private const string PerfScript = "\r\n$ErrorActionPreference = 'SilentlyContinue'\r\n$found = $false\r\n\r\n# NVIDIA（若 nvidia-smi 可用）\r\nif (-not $found) {\r\n    try {\r\n        $nsmi = Get-Command nvidia-smi -ErrorAction SilentlyContinue\r\n        if ($nsmi) {\r\n            $csv = & nvidia-smi --query-gpu=temperature.gpu --format=csv,noheader,nounits 2>$null\r\n            $t = [double](@($csv)[0].Trim())\r\n            if ($t -gt 0 -and $t -lt 150) {\r\n                Write-Output ('gpu_temp=' + [math]::Round($t, 1))\r\n                Write-Output 'gpu_temp_src=nvidia'\r\n                $found = $true\r\n            }\r\n        }\r\n    } catch {}\r\n}\r\n\r\n# AMD：amd-smi / rocm-smi\r\nif (-not $found) {\r\n    try {\r\n        $amdSmi = Get-Command 'amd-smi.exe','rocm-smi.exe','rocm-smi' -ErrorAction SilentlyContinue | Select-Object -First 1\r\n        if ($amdSmi) {\r\n            $tempOut = (& $amdSmi.Source --showtemp 2>$null) -join [Environment]::NewLine\r\n            if ($tempOut -match '([\\d.]+)\\s*\\u00B0?\\s*C') {\r\n                $t = [double]$Matches[1]\r\n                if ($t -gt 0 -and $t -lt 150) {\r\n                    Write-Output ('gpu_temp=' + [math]::Round($t, 1))\r\n                    Write-Output 'gpu_temp_src=amd'\r\n                    $found = $true\r\n                }\r\n            }\r\n        }\r\n    } catch {}\r\n}\r\n\r\n# Intel：xpu-smi / intel_gpu_top\r\nif (-not $found) {\r\n    try {\r\n        $intelSmi = Get-Command 'xpu-smi.exe','intel_gpu_top.exe','intel_gpu_top' -ErrorAction SilentlyContinue | Select-Object -First 1\r\n        if ($intelSmi) {\r\n            $tempArgs = @('stats')\r\n            if ($intelSmi.Name -like 'intel_gpu_top*') { $tempArgs = @('-l') }\r\n            $tempOut = (& $intelSmi.Source @tempArgs 2>$null) -join [Environment]::NewLine\r\n            if ($tempOut -match '([\\d.]+)\\s*\\u00B0?\\s*C') {\r\n                $t = [double]$Matches[1]\r\n                if ($t -gt 0 -and $t -lt 150) {\r\n                    Write-Output ('gpu_temp=' + [math]::Round($t, 1))\r\n                    Write-Output 'gpu_temp_src=intel'\r\n                    $found = $true\r\n                }\r\n            }\r\n        }\r\n    } catch {}\r\n}\r\n\r\n# OHM / LibreHardwareMonitor WMI 传感器\r\nif (-not $found) {\r\n    foreach ($ns in @('root\\LibreHardwareMonitor', 'root\\OpenHardwareMonitor')) {\r\n        try {\r\n            $sensors = Get-CimInstance -Namespace $ns -ClassName Sensor -ErrorAction Stop\r\n            $gpuSensors = @($sensors | Where-Object {\r\n                $_.SensorType -match 'Temperature' -and (\r\n                    $_.Name -match 'GPU|Graphics|Video|D3D|Radeon|GeForce|RTX|GTX|Arc|Iris|NVIDIA|AMD|Intel' -or\r\n                    $_.Parent -match 'GPU|Graphics|Video|D3D|Radeon|GeForce|RTX|GTX|Arc|Iris|NVIDIA|AMD|Intel'\r\n                )\r\n            })\r\n            if ($gpuSensors.Count -gt 0) {\r\n                $s = $gpuSensors | Where-Object { $_.Value -gt 0 } | Select-Object -First 1\r\n                if ($s) {\r\n                    Write-Output ('gpu_temp=' + [math]::Round([double]$s.Value, 1))\r\n                    Write-Output 'gpu_temp_src=ohm'\r\n                    $found = $true\r\n                    break\r\n                }\r\n            }\r\n        } catch {}\r\n    }\r\n}\r\n\r\n# ACPI 热区（最后手段，非 GPU 专属）\r\nif (-not $found) {\r\n    try {\r\n        $tz = Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature |\r\n              Where-Object { $_.CurrentTemperature -gt 0 } | Select-Object -First 1\r\n        if ($tz) {\r\n            $tc = ($tz.CurrentTemperature / 10) - 273.15\r\n            if ($tc -gt 0 -and $tc -lt 120) {\r\n                Write-Output ('gpu_temp=' + [math]::Round($tc, 1))\r\n                Write-Output 'gpu_temp_src=acpi'\r\n            }\r\n        }\r\n    } catch {}\r\n}\r\n";

	private static string? _psScriptFile;

	public bool TempEnabled { get; set; } = true;

	/// <summary>QueryGpu 缓存有效期(秒)。llama 运行中由调用方设 5s(熔断时效), 未运行 15s(降频)。</summary>
	public double CacheTtlSeconds { get; set; } = 5.0;

	public string Vendor => DetectVendor();

	private static double EnvTick()
	{
		return (double)Environment.TickCount64 / 1000.0;
	}

	public Dictionary<string, string> QueryGpu()
	{
		double num = EnvTick();
		lock (_lock)
		{
			if (_gpuCache != null && num - _gpuCacheTime < CacheTtlSeconds)
			{
				return new Dictionary<string, string>(_gpuCache);
			}
		}
		Dictionary<string, string> dictionary = new Dictionary<string, string>();
		switch (DetectVendor())
		{
		case "nvidia":
			TryNvidiaSmi(dictionary);
			break;
		case "amd":
			if (!TryAmdSmi(dictionary))
			{
				TryCimGpuCounters(dictionary);
				TryRegistryVramTotal(dictionary);
			}
			else if (!dictionary.ContainsKey("vram_total"))
			{
				// amd-smi metric 输出只有 vram_used；总量从注册表补齐，
				// 否则百分比熔断/显示在 AMD+amd-smi 机器上永不生效
				TryRegistryVramTotal(dictionary);
			}
			break;
		case "intel":
			if (!TryIntelSmi(dictionary))
			{
				TryCimGpuCounters(dictionary);
				TryRegistryVramTotal(dictionary);
			}
			break;
		default:
			TryCimGpuCounters(dictionary);
			TryRegistryVramTotal(dictionary);
			break;
		}
		if (TempEnabled && !dictionary.ContainsKey("gpu_temp"))
		{
			TryGpuTempHelper(dictionary);
		}
		// LibreHardwareMonitorLib(社区成熟方案)统一补齐: AMD/Intel 功率/风扇/频率/负载, 各家缺失的键
		TryLhmMerge(dictionary);
		// CPU 温度兜底: LHM 已提供则跳过, 否则 OHM WMI → ACPI; 60s TTL 独立缓存
		TryCpuTemp(dictionary);
		if ((!dictionary.ContainsKey("gpu_temp") || !dictionary.ContainsKey("gpu_util")) && num - _lastPsFallbackTime >= 60.0)
		{
			_lastPsFallbackTime = num;
			MergePowerShellScript(dictionary);
		}
		lock (_lock)
		{
			_gpuCache = new Dictionary<string, string>(dictionary);
			_gpuCacheTime = num;
			return dictionary;
		}
	}

	private string DetectVendor()
	{
		//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bd: Expected O, but got Unknown
		//IL_00d8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00df: Expected O, but got Unknown
		double num = EnvTick();
		if (_vendor != null && num - _vendorProbeTime < 300.0)
		{
			return _vendor;
		}
		string text = "unknown";
		if (CommandExists("nvidia-smi"))
		{
			text = "nvidia";
		}
		if (text == "unknown" && (CommandExists("amd-smi.exe") || CommandExists("rocm-smi") || CommandExists("rocm-smi.exe")))
		{
			text = "amd";
		}
		if (text == "unknown" && (CommandExists("xpu-smi.exe") || CommandExists("intel_gpu_top.exe")))
		{
			text = "intel";
		}
		if (text == "unknown")
		{
			try
			{
				ManagementObjectSearcher val = new ManagementObjectSearcher("SELECT AdapterCompatibility FROM Win32_VideoController");
				try
				{
					ManagementObjectCollection val2 = val.Get();
					try
					{
						var enumerator = val2.GetEnumerator();
						try
						{
							while (enumerator.MoveNext())
							{
								ManagementObject val3 = (ManagementObject)enumerator.Current;
								try
								{
									string text2 = (((ManagementBaseObject)val3)["AdapterCompatibility"]?.ToString() ?? "").ToLowerInvariant();
									if (text2.Contains("nvidia"))
									{
										text = "nvidia";
										break;
									}
									if (text2.Contains("amd") || text2.Contains("radeon") || text2.Contains("ati"))
									{
										text = "amd";
										break;
									}
									if (text2.Contains("intel"))
									{
										text = "intel";
										break;
									}
								}
								finally
								{
									val3.Dispose();
								}
							}
						}
						finally
						{
							((IDisposable)enumerator)?.Dispose();
						}
					}
					finally
					{
						((IDisposable)val2)?.Dispose();
					}
				}
				finally
				{
					((IDisposable)val)?.Dispose();
				}
			}
			catch
			{
			}
		}
		_vendor = text;
		_vendorProbeTime = num;
		return text;
	}

	private static bool CommandExists(string name)
	{
		try
		{
			using Process process = Process.Start(new ProcessStartInfo("where", name)
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			});
			if (process == null)
			{
				return false;
			}
			process.WaitForExit(3000);
			return process.ExitCode == 0;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryNvidiaSmi(Dictionary<string, string> r)
	{
		try
		{
			// 统一走 ProcessRunner（同时异步读两流 + 5s 超时），避免首行阻塞时超时失效。
			// 一次查询带齐 Strata 同款指标: 利用率/显存/温度/功率/功耗上限/风扇/频率/PCIe(代数×宽度/Rx 吞吐)。
			// 旧驱动缺 pcie.link.* 时对应列为 "N/A"，解析失败即跳过该键（UI 显示"无数据"）。
			ProcessResult runResult = ProcessRunner.Run(
				"nvidia-smi",
				new[] { "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw,enforced.power.limit,fan.speed,clocks.current.graphics,pcie.link.gen.current,pcie.link.width.current,pcie.link.rx.current,name", "--format=csv,noheader,nounits" },
				5000);
			if (!runResult.Started || runResult.TimedOut)
			{
				return false;
			}
			// 多卡聚合(Strata 口径): 利用率/风扇/频率取均值, 显存/功率/功耗上限/Rx 求和, 温度取最高, PCIe 代数×宽度取第一张卡
			double utilSum = 0.0, utilN = 0.0, vramUsed = 0.0, vramTotal = 0.0, tempMax = 0.0;
			double powerSum = 0.0, limitSum = 0.0, fanSum = 0.0, fanN = 0.0, clockSum = 0.0, clockN = 0.0, rxSum = 0.0, rxN = 0.0;
			string? gen = null, width = null;
			string name = "";
			int count = 0;
			bool any = false;
			foreach (string raw in runResult.StdOut.Split('\n'))
			{
				string text = raw.TrimEnd('\r');
				if (string.IsNullOrWhiteSpace(text))
				{
					continue;
				}
				string[] array = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (array.Length < 4)
				{
					continue;
				}
				if (double.TryParse(array[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var util))
				{
					utilSum += Math.Min(100.0, Math.Max(0.0, util));
					utilN += 1.0;
				}
				if (double.TryParse(array[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mu))
				{
					vramUsed += mu;
				}
				if (double.TryParse(array[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var mt))
				{
					vramTotal += mt;
				}
				if (double.TryParse(array[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > 0.0 && t < 150.0 && t > tempMax)
				{
					tempMax = t;
				}
				if (array.Length > 4 && double.TryParse(array[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p >= 0.0)
				{
					powerSum += p;
				}
				if (array.Length > 5 && double.TryParse(array[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var pl) && pl > 0.0)
				{
					limitSum += pl;
				}
				if (array.Length > 6 && double.TryParse(array[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f >= 0.0)
				{
					fanSum += f;
					fanN += 1.0;
				}
				if (array.Length > 7 && double.TryParse(array[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var c) && c > 0.0)
				{
					clockSum += c;
					clockN += 1.0;
				}
				if (gen == null && array.Length > 8 && double.TryParse(array[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var g) && g > 0.0)
				{
					gen = ((int)g).ToString(CultureInfo.InvariantCulture);
				}
				if (width == null && array.Length > 9 && double.TryParse(array[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0.0)
				{
					width = ((int)w).ToString(CultureInfo.InvariantCulture);
				}
				if (array.Length > 10 && double.TryParse(array[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var rx) && rx > 0.0)
				{
					rxSum += rx;
					rxN += 1.0;
				}
				if (array.Length > 11 && string.IsNullOrWhiteSpace(name))
				{
					name = array[11];
				}
				count++;
				any = true;
			}
			if (!any)
			{
				// 旧驱动不认识 pcie.link.* 等新字段时 nvidia-smi 会整体报错, 退回基础四字段查询保证监控不失效
				return TryNvidiaSmiBasic(r);
			}
			if (utilN > 0.0)
			{
				r["gpu_util"] = (utilSum / utilN).ToString("F1", CultureInfo.InvariantCulture);
			}
			if (vramUsed > 0.0)
			{
				r["vram_used"] = ((long)(vramUsed * 1048576.0)).ToString(CultureInfo.InvariantCulture);
			}
			if (vramTotal > 0.0)
			{
				r["vram_total"] = ((long)(vramTotal * 1048576.0)).ToString(CultureInfo.InvariantCulture);
			}
			if (tempMax > 0.0)
			{
				r["gpu_temp"] = tempMax.ToString("F1", CultureInfo.InvariantCulture);
				r["gpu_temp_src"] = "nvidia";
			}
			if (powerSum > 0.0)
			{
				r["power_w"] = powerSum.ToString("F1", CultureInfo.InvariantCulture);
			}
			if (limitSum > 0.0)
			{
				r["power_limit_w"] = limitSum.ToString("F1", CultureInfo.InvariantCulture);
			}
			if (fanN > 0.0)
			{
				r["fan_pct"] = (fanSum / fanN).ToString("F1", CultureInfo.InvariantCulture);
			}
			if (clockN > 0.0)
			{
				r["clock_mhz"] = (clockSum / clockN).ToString("F0", CultureInfo.InvariantCulture);
			}
			if (gen != null)
			{
				r["pcie_gen"] = gen;
			}
			if (width != null)
			{
				r["pcie_width"] = width;
			}
			if (rxN > 0.0)
			{
				r["pcie_rx_kbs"] = (rxSum / rxN).ToString("F0", CultureInfo.InvariantCulture);
			}
			if (count > 0)
			{
				r["gpu_count"] = count.ToString(CultureInfo.InvariantCulture);
			}
			if (!string.IsNullOrWhiteSpace(name))
			{
				r["gpu_name"] = name.Trim();
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	// 基础回退: 仅利用率/显存/温度(旧驱动兼容)
	private static bool TryNvidiaSmiBasic(Dictionary<string, string> r)
	{
		try
		{
			ProcessResult runResult = ProcessRunner.Run(
				"nvidia-smi",
				new[] { "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu", "--format=csv,noheader,nounits" },
				5000);
			if (!runResult.Started || runResult.TimedOut)
			{
				return false;
			}
			string text = runResult.StdOut.Split('\n')[0].TrimEnd('\r');
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			string[] array = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (array.Length < 4 || !double.TryParse(array[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
			{
				return false;
			}
			double.TryParse(array[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var result2);
			double.TryParse(array[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var result3);
			double.TryParse(array[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var result4);
			r["gpu_util"] = Math.Min(100.0, Math.Max(0.0, result)).ToString("F1", CultureInfo.InvariantCulture);
			if (result2 > 0.0)
			{
				r["vram_used"] = ((long)(result2 * 1048576.0)).ToString(CultureInfo.InvariantCulture);
			}
			if (result3 > 0.0)
			{
				r["vram_total"] = ((long)(result3 * 1048576.0)).ToString(CultureInfo.InvariantCulture);
			}
			if (result4 > 0.0)
			{
				r["gpu_temp"] = result4.ToString("F1", CultureInfo.InvariantCulture);
				r["gpu_temp_src"] = "nvidia";
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryAmdSmi(Dictionary<string, string> r)
	{
		try
		{
			// 已知可用的三个参数(新增开关在部分 amd-smi 版本会整体失败); 功率/风扇/频率由 LHM 库补齐
			ProcessResult runResult = ProcessRunner.Run(
				"amd-smi.exe",
				new[] { "metric", "--temperature", "--gpu-utilization", "--vram-usage" },
				5000);
			bool result = false;
			if (runResult.Started && !runResult.TimedOut && !string.IsNullOrWhiteSpace(runResult.StdOut + runResult.StdErr))
			{
				string text = runResult.StdOut + runResult.StdErr;
				result = ParseAmdOutput(text, r, "amd");
			}
			// rocm-smi 回退(旧版 ROCm): 分别带各开关
			if (!result && CommandExists("rocm-smi"))
			{
				ProcessResult rs = ProcessRunner.Run(
					"rocm-smi",
					new[] { "--showuse", "--showmemuse", "--showvram", "--showtemp", "--showpower", "--showfan", "--showclocks", "--showpcie" },
					5000);
				if (rs.Started && !rs.TimedOut)
				{
					result = ParseRocmOutput(rs.StdOut, r);
				}
			}
			if (result && !r.ContainsKey("vram_total"))
			{
				// AMD 输出只有 vram_used；总量从注册表补齐，否则百分比熔断/显示在 AMD 机器上永不生效
				TryRegistryVramTotal(r);
			}
			return result;
		}
		catch
		{
			return false;
		}
	}

	// amd-smi metric 输出解析: 关键字行/列匹配 W/%/MiB/°C/MHz/PCIe
	private static bool ParseAmdOutput(string text, Dictionary<string, string> r, string src)
	{
		bool ok = false;
		Match match = Regex.Match(text, "([\\d.]+)\\s*C", RegexOptions.IgnoreCase);
		if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > 0.0 && t < 150.0)
		{
			r["gpu_temp"] = t.ToString("F1", CultureInfo.InvariantCulture);
			r["gpu_temp_src"] = src;
			ok = true;
		}
		// 利用率: 显式 util/gpu use 字段优先, 避免与风扇 % 混淆
		Match mu = Regex.Match(text, "(?:gpu[_ ]?util|use)\\D{0,10}([\\d.]+)\\s*%", RegexOptions.IgnoreCase);
		if (mu.Success && double.TryParse(mu.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var u))
		{
			r["gpu_util"] = Math.Min(100.0, Math.Max(0.0, u)).ToString("F1", CultureInfo.InvariantCulture);
			ok = true;
		}
		Match mv = Regex.Match(text, "([\\d.]+)\\s*MiB", RegexOptions.IgnoreCase);
		if (mv.Success && double.TryParse(mv.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0.0)
		{
			r["vram_used"] = ((long)(m * 1048576.0)).ToString(CultureInfo.InvariantCulture);
			ok = true;
		}
		Match mp = Regex.Match(text, "(?:power|功耗)\\D{0,12}([\\d.]+)\\s*W", RegexOptions.IgnoreCase);
		if (mp.Success && double.TryParse(mp.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p > 0.0)
		{
			r["power_w"] = p.ToString("F1", CultureInfo.InvariantCulture);
			ok = true;
		}
		Match mf = Regex.Match(text, "(?:fan|风扇)\\D{0,12}([\\d.]+)\\s*%", RegexOptions.IgnoreCase);
		if (mf.Success && double.TryParse(mf.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f >= 0.0)
		{
			r["fan_pct"] = f.ToString("F1", CultureInfo.InvariantCulture);
			ok = true;
		}
		Match mc = Regex.Match(text, "(?:sclk|clock|频率)\\D{0,12}([\\d.]+)\\s*MHz", RegexOptions.IgnoreCase);
		if (mc.Success && double.TryParse(mc.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) && c > 0.0)
		{
			r["clock_mhz"] = c.ToString("F0", CultureInfo.InvariantCulture);
			ok = true;
		}
		Match mg = Regex.Match(text, "Gen\\s*([1-6])\\s*[x×]\\s*(\\d{1,2})", RegexOptions.IgnoreCase);
		if (mg.Success)
		{
			r["pcie_gen"] = mg.Groups[1].Value;
			r["pcie_width"] = mg.Groups[2].Value;
			ok = true;
		}
		return ok;
	}

	// rocm-smi 输出解析: "Power: 100 W" / "Fan: 30%" / "sclk: 2000MHz" / "PCIe: Gen4x16"
	private static bool ParseRocmOutput(string text, Dictionary<string, string> r)
	{
		bool ok = false;
		Match mt = Regex.Match(text, "(?:Temperature|温度)\\D{0,12}([\\d.]+)", RegexOptions.IgnoreCase);
		if (mt.Success && double.TryParse(mt.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > 0.0 && t < 150.0)
		{
			r["gpu_temp"] = t.ToString("F1", CultureInfo.InvariantCulture);
			r["gpu_temp_src"] = "amd";
			ok = true;
		}
		Match mu = Regex.Match(text, "GPU use \\(utilization\\.rate\\)\\s*:\\s*([\\d.]+)", RegexOptions.IgnoreCase);
		if (!mu.Success)
		{
			mu = Regex.Match(text, "(?:use|utilization)\\D{0,20}([\\d.]+)\\s*%", RegexOptions.IgnoreCase);
		}
		if (mu.Success && double.TryParse(mu.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var u))
		{
			r["gpu_util"] = Math.Min(100.0, Math.Max(0.0, u)).ToString("F1", CultureInfo.InvariantCulture);
			ok = true;
		}
		Match mv = Regex.Match(text, "(?:vram used|used vram|VRAM Total Used)\\D{0,16}([\\d.]+)", RegexOptions.IgnoreCase);
		if (mv.Success && double.TryParse(mv.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0.0)
		{
			// rocm-smi 显存单位可能是 MB 或 MiB, 同义处理
			r["vram_used"] = ((long)(m * 1048576.0)).ToString(CultureInfo.InvariantCulture);
			ok = true;
		}
		Match mp = Regex.Match(text, "Power\\D{0,12}([\\d.]+)", RegexOptions.IgnoreCase);
		if (mp.Success && double.TryParse(mp.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p > 0.0)
		{
			r["power_w"] = p.ToString("F1", CultureInfo.InvariantCulture);
			ok = true;
		}
		Match mf = Regex.Match(text, "Fan\\D{0,12}([\\d.]+)", RegexOptions.IgnoreCase);
		if (mf.Success && double.TryParse(mf.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f >= 0.0)
		{
			r["fan_pct"] = f.ToString("F1", CultureInfo.InvariantCulture);
			ok = true;
		}
		Match mc = Regex.Match(text, "sclk\\D{0,12}([\\d.]+)", RegexOptions.IgnoreCase);
		if (mc.Success && double.TryParse(mc.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) && c > 0.0)
		{
			r["clock_mhz"] = c.ToString("F0", CultureInfo.InvariantCulture);
			ok = true;
		}
		Match mg = Regex.Match(text, "Gen\\s*([1-6])\\s*[x×]\\s*(\\d{1,2})", RegexOptions.IgnoreCase);
		if (mg.Success)
		{
			r["pcie_gen"] = mg.Groups[1].Value;
			r["pcie_width"] = mg.Groups[2].Value;
			ok = true;
		}
		return ok;
	}

	private static bool TryIntelSmi(Dictionary<string, string> r)
	{
		try
		{
			// 统一走 ProcessRunner（同时异步读两流 + 5s 超时），避免“先读后等”让超时失效。
			ProcessResult runResult = ProcessRunner.Run("xpu-smi.exe", new[] { "stats" }, 5000);
			if (!runResult.Started || runResult.TimedOut)
			{
				return false;
			}
			string text = runResult.StdOut + runResult.StdErr;
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			bool result = false;
			Match match = Regex.Match(text, "([\\d.]+)\\s*C", RegexOptions.IgnoreCase);
			if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result2) && result2 > 0.0 && result2 < 150.0)
			{
				r["gpu_temp"] = result2.ToString("F1", CultureInfo.InvariantCulture);
				r["gpu_temp_src"] = "intel";
				result = true;
			}
			Match match2 = Regex.Match(text, "([\\d.]+)\\s*%", RegexOptions.IgnoreCase);
			if (match2.Success && double.TryParse(match2.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result3))
			{
				r["gpu_util"] = Math.Min(100.0, Math.Max(0.0, result3)).ToString("F1", CultureInfo.InvariantCulture);
				result = true;
			}
			Match match3 = Regex.Match(text, "([\\d.]+)\\s*MiB", RegexOptions.IgnoreCase);
			if (match3.Success && double.TryParse(match3.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result4) && result4 > 0.0)
			{
				r["vram_used"] = ((long)(result4 * 1048576.0)).ToString(CultureInfo.InvariantCulture);
				result = true;
			}
			// Intel 侧 Strata 指标: xpu-smi stats 自带功率/频率字段
			Match matchP = Regex.Match(text, "(?:GPU[_ ]?Power|功率)\\D{0,12}([\\d.]+)", RegexOptions.IgnoreCase);
			if (matchP.Success && double.TryParse(matchP.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var vP) && vP > 0.0)
			{
				r["power_w"] = vP.ToString("F1", CultureInfo.InvariantCulture);
				result = true;
			}
			Match matchC = Regex.Match(text, "(?:GPU[_ ]?Frequency|频率)\\D{0,12}([\\d.]+)", RegexOptions.IgnoreCase);
			if (matchC.Success && double.TryParse(matchC.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var vC) && vC > 0.0)
			{
				r["clock_mhz"] = vC.ToString("F0", CultureInfo.InvariantCulture);
				result = true;
			}
			return result;
		}
		catch
		{
			return false;
		}
	}

	private static void TryCimGpuCounters(Dictionary<string, string> r)
	{
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Expected O, but got Unknown
		//IL_00b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ba: Expected O, but got Unknown
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Expected O, but got Unknown
		//IL_00e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00eb: Expected O, but got Unknown
		try
		{
			ManagementObjectSearcher val = new ManagementObjectSearcher("SELECT Name, DedicatedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory");
			try
			{
				ManagementObjectCollection val2 = val.Get();
				try
				{
					long num = 0L;
					var enumerator = val2.GetEnumerator();
					try
					{
						while (enumerator.MoveNext())
						{
							ManagementObject val3 = (ManagementObject)enumerator.Current;
							try
							{
								if (((ManagementBaseObject)val3)["DedicatedUsage"] != null)
								{
									long num2 = Convert.ToInt64(((ManagementBaseObject)val3)["DedicatedUsage"]);
									if (num2 > num)
									{
										num = num2;
									}
								}
							}
							finally
							{
								val3.Dispose();
							}
						}
					}
					finally
					{
						((IDisposable)enumerator)?.Dispose();
					}
					if (num > 0)
					{
						r["vram_used"] = num.ToString(CultureInfo.InvariantCulture);
					}
				}
				finally
				{
					((IDisposable)val2)?.Dispose();
				}
			}
			finally
			{
				((IDisposable)val)?.Dispose();
			}
		}
		catch
		{
		}
		try
		{
			ManagementObjectSearcher val4 = new ManagementObjectSearcher("SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
			try
			{
				ManagementObjectCollection val5 = val4.Get();
				try
				{
					double num3 = 0.0;
					bool flag = false;
					var enumerator = val5.GetEnumerator();
					try
					{
						while (enumerator.MoveNext())
						{
							ManagementObject val6 = (ManagementObject)enumerator.Current;
							try
							{
								string text = ((ManagementBaseObject)val6)["Name"]?.ToString() ?? "";
								if ((text.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase) || text.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase)) && ((ManagementBaseObject)val6)["UtilizationPercentage"] != null)
								{
									double num4 = Convert.ToDouble(((ManagementBaseObject)val6)["UtilizationPercentage"]);
									flag = true;
									if (num4 > num3)
									{
										num3 = num4;
									}
								}
							}
							finally
							{
								val6.Dispose();
							}
						}
					}
					finally
					{
						((IDisposable)enumerator)?.Dispose();
					}
					if (flag)
					{
						r["gpu_util"] = Math.Min(100.0, Math.Max(0.0, num3)).ToString("F1", CultureInfo.InvariantCulture);
					}
				}
				finally
				{
					((IDisposable)val5)?.Dispose();
				}
			}
			finally
			{
				((IDisposable)val4)?.Dispose();
			}
		}
		catch
		{
		}
	}

	private static void TryRegistryVramTotal(Dictionary<string, string> r)
	{
		try
		{
			using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}");
			if (registryKey == null)
			{
				return;
			}
			long num = 0L;
			string[] subKeyNames = registryKey.GetSubKeyNames();
			foreach (string name in subKeyNames)
			{
				try
				{
					using RegistryKey registryKey2 = registryKey.OpenSubKey(name);
					if (registryKey2 != null)
					{
						object value = registryKey2.GetValue("HardwareInformation.qwMemorySize");
						if (value != null)
						{
							num = Math.Max(num, Convert.ToInt64(value));
						}
					}
				}
				catch
				{
				}
			}
			if (num > 0)
			{
				r["vram_total"] = num.ToString(CultureInfo.InvariantCulture);
			}
		}
		catch
		{
		}
	}

	public void StartGpuTempLoop()
	{
		if (_gpuTempProc != null && !_gpuTempProc.HasExited)
		{
			return;
		}
		string text = FindGpuTempExe();
		if (text == null)
		{
			return;
		}
		try
		{
			_gpuTempProc = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = text,
					Arguments = "--loop",
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				},
				EnableRaisingEvents = true
			};
			_gpuTempProc.Start();
			_gpuTempThread = new Thread(ReadGpuTempLoop)
			{
				IsBackground = true
			};
			_gpuTempThread.Start();
		}
		catch
		{
			_gpuTempProc = null;
		}
	}

	private void ReadGpuTempLoop()
	{
		try
		{
			while (_gpuTempProc != null && !_gpuTempProc.HasExited)
			{
				// 常驻进程（--loop）不能整体等待退出：用首行看门狗，8s 内没有输出即判定数据源不可用。
				Task<string?> readTask = _gpuTempProc.StandardOutput.ReadLineAsync();
				Task completed = Task.WhenAny(readTask, Task.Delay(8000)).GetAwaiter().GetResult();
				if (completed != readTask)
				{
					try
					{
						_gpuTempProc.Kill(true);
					}
					catch
					{
					}
					lock (_lock)
					{
						_cachedGpuTemp = null;
						_cachedGpuTempSrc = null;
					}
					AppLog.WriteError("gputemp --loop 连续 8 秒无输出，已终止该采样（gpu_temp 数据源不可用）。");
					break;
				}
				string? text = readTask.GetAwaiter().GetResult();
				if (text == null)
				{
					break;
				}
				Match match = Regex.Match(text, "\\[(Gpu\\w+)\\]\\s+([^=]+?)\\s*=\\s*([\\d.]+)");
				if (!match.Success)
				{
					continue;
				}
				try
				{
					string text2 = match.Groups[2].Value.Trim().ToLowerInvariant();
					double num = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
					if (!(num > 0.0) || !(num < 150.0))
					{
						continue;
					}
					lock (_lock)
					{
						if (text2.Contains("core") || !_cachedGpuTemp.HasValue)
						{
							_cachedGpuTemp = num;
							_cachedGpuTempSrc = match.Groups[1].Value.ToLowerInvariant().Replace("gpu", "");
						}
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}

	public void StopGpuTempLoop()
	{
		try
		{
			if (_gpuTempProc != null && !_gpuTempProc.HasExited)
			{
				_gpuTempProc.Kill();
			}
		}
		catch
		{
		}
		if (_gpuTempThread != null && _gpuTempThread.IsAlive)
		{
			_gpuTempThread.Join(2000);
		}
		_gpuTempProc = null;
		_gpuTempThread = null;
	}

	private void TryGpuTempHelper(Dictionary<string, string> result)
	{
		lock (_lock)
		{
			if (_gpuTempProc != null && !_gpuTempProc.HasExited)
			{
				if (_cachedGpuTemp.HasValue)
				{
					result["gpu_temp"] = _cachedGpuTemp.Value.ToString("F1", CultureInfo.InvariantCulture);
					result["gpu_temp_src"] = _cachedGpuTempSrc ?? "gpu";
				}
				return;
			}
			double num = EnvTick();
			if (num - _lastGpuTempTime >= 5.0)
			{
				var (cachedGpuTemp, text) = ReadGpuTempOnce();
				if (cachedGpuTemp.HasValue)
				{
					_cachedGpuTemp = cachedGpuTemp;
					_cachedGpuTempSrc = text ?? "gpu";
				}
				_lastGpuTempTime = num;
			}
			if (_cachedGpuTemp.HasValue)
			{
				result["gpu_temp"] = _cachedGpuTemp.Value.ToString("F1", CultureInfo.InvariantCulture);
				result["gpu_temp_src"] = _cachedGpuTempSrc ?? "gpu";
			}
		}
	}

	private static (double? temp, string? src) ReadGpuTempOnce()
	{
		string text = FindGpuTempExe();
		if (text == null)
		{
			return (temp: null, src: null);
		}
		try
		{
			// 一次性采样：ProcessRunner 同时异步读流并带 8s 超时；原 WaitForExit(8000) 已由其超时机制取代。
			ProcessResult result = ProcessRunner.Run(text, timeoutMs: 8000);
			if (!result.Started)
			{
				return (temp: null, src: null);
			}
			foreach (string line in result.StdOut.Split('\n'))
			{
				string input = line.TrimEnd('\r');
				Match match = Regex.Match(input, "\\[(Gpu\\w+)\\]\\s+([^=]+?)\\s*=\\s*([\\d.]+)");
				if (!match.Success)
				{
					continue;
				}
				try
				{
					double num = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
					if (num > 0.0 && num < 150.0 && match.Groups[2].Value.Trim().ToLowerInvariant().Contains("core"))
					{
						string item = match.Groups[1].Value.ToLowerInvariant().Replace("gpu", "");
						return (temp: num, src: item);
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		return (temp: null, src: null);
	}

	private void MergePowerShellScript(Dictionary<string, string> result)
	{
		try
		{
			string text = Path.Combine(Path.GetTempPath(), "LlamaDSH_Launcher");
			Directory.CreateDirectory(text);
			string text2 = Path.Combine(text, "gpu_perf.ps1");
			lock (_lock)
			{
				if (!File.Exists(text2) || File.ReadAllText(text2) != PerfScript)
				{
					File.WriteAllText(text2, PerfScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
				}
				_psScriptFile = text2;
			}
			// 统一走 ProcessRunner（同时异步读两流 + 10s 超时），避免“先读后等”让超时失效。
			ProcessResult psResult = ProcessRunner.Run(
				"powershell.exe",
				new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", text2 },
				10000);
			if (!psResult.Started)
			{
				return;
			}
			string text3 = psResult.StdOut;
			string[] array = text3.Split('\n', StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < array.Length; i++)
			{
				string text4 = array[i].Trim().TrimEnd('\r');
				int num = text4.IndexOf('=');
				if (num > 0)
				{
					string text5 = text4.Substring(0, num).Trim();
					string value = text4.Substring(num + 1).Trim();
					if (!string.IsNullOrEmpty(text5) && !result.ContainsKey(text5))
					{
						result[text5] = value;
					}
				}
			}
		}
		catch
		{
		}
	}

	// LibreHardwareMonitorLib 库合并: 只填当前缺失的键(命令行通道优先, 库做跨厂商兜底/补充)
	private void TryLhmMerge(Dictionary<string, string> r)
	{
		try
		{
			foreach (KeyValuePair<string, string> kv in HardwareMonitor.Instance.Sample())
			{
				if (!r.ContainsKey(kv.Key))
				{
					r[kv.Key] = kv.Value;
				}
			}
		}
		catch
		{
		}
	}

	private double? _cachedCpuTemp;

	private double _lastCpuTempTime = -60.0;

	private void TryCpuTemp(Dictionary<string, string> r)
	{
		double now = EnvTick();
		lock (_lock)
		{
			if (now - _lastCpuTempTime < 60.0)
			{
				if (_cachedCpuTemp.HasValue)
				{
					r["cpu_temp"] = _cachedCpuTemp.Value.ToString("F1", CultureInfo.InvariantCulture);
					r["cpu_temp_src"] = _cpuTempSrc ?? "";
				}
				return;
			}
		}
		double? temp = ReadCpuTempOhm();
		string src = "ohm";
		if (!temp.HasValue)
		{
			temp = ReadCpuTempAcpi();
			src = "acpi";
		}
		lock (_lock)
		{
			_lastCpuTempTime = now;
			_cachedCpuTemp = temp;
			_cpuTempSrc = src;
			if (temp.HasValue)
			{
				r["cpu_temp"] = temp.Value.ToString("F1", CultureInfo.InvariantCulture);
				r["cpu_temp_src"] = src;
			}
		}
	}

	private string? _cpuTempSrc;

	// OHM/LibreHardwareMonitor WMI 传感器: Name 或 Parent 含 CPU 的温度
	private static double? ReadCpuTempOhm()
	{
		foreach (string ns in new[] { "root\\LibreHardwareMonitor", "root\\OpenHardwareMonitor" })
		{
			try
			{
				ManagementObjectSearcher searcher = new ManagementObjectSearcher(ns, "SELECT Name, Parent, Value FROM Sensor WHERE SensorType='Temperature'");
				using ManagementObjectCollection results = searcher.Get();
				foreach (ManagementObject o in results)
				{
					using (o)
					{
						string name = o["Name"]?.ToString() ?? "";
						string parent = o["Parent"]?.ToString() ?? "";
						if ((name.Contains("CPU", StringComparison.OrdinalIgnoreCase) || parent.Contains("cpu", StringComparison.OrdinalIgnoreCase))
							&& double.TryParse(o["Value"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
							&& v > 0.0 && v < 120.0)
						{
							return v;
						}
					}
				}
			}
			catch
			{
			}
		}
		return null;
	}

	private static double? ReadCpuTempAcpi()
	{
		try
		{
			ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
			using ManagementObjectCollection results = searcher.Get();
			foreach (ManagementObject o in results)
			{
				using (o)
				{
					if (o["CurrentTemperature"] != null && Convert.ToDouble(o["CurrentTemperature"]) > 0.0)
					{
						double tc = Convert.ToDouble(o["CurrentTemperature"]) / 10.0 - 273.15;
						if (tc > 0.0 && tc < 120.0)
						{
							return tc;
						}
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static string? FindGpuTempExe()
	{
		string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
		List<string> list = new List<string>
		{
			Path.Combine(baseDirectory, "gputemp.exe"),
			Path.Combine(baseDirectory, "gputemp", "gputemp.exe")
		};
		try
		{
			DirectoryInfo directoryInfo = new DirectoryInfo(baseDirectory);
			int num = 0;
			while (num < 4 && directoryInfo != null && directoryInfo != null)
			{
				list.Add(Path.Combine(directoryInfo.FullName, "gputemp", "gputemp.exe"));
				list.Add(Path.Combine(directoryInfo.FullName, "gputemp", "publish", "win-x64", "gputemp.exe"));
				num++;
				directoryInfo = directoryInfo.Parent;
			}
		}
		catch
		{
		}
		foreach (string item in list)
		{
			try
			{
				if (!string.IsNullOrEmpty(item) && File.Exists(item))
				{
					return Path.GetFullPath(item);
				}
			}
			catch
			{
			}
		}
		return null;
	}
}
