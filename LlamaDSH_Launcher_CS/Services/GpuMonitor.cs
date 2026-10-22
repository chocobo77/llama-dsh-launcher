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
			if (_gpuCache != null && num - _gpuCacheTime < 5.0)
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
			// 统一走 ProcessRunner（同时异步读两流 + 5s 超时），避免首行阻塞时超时失效；仍只取第一行。
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
			if (array.Length < 4)
			{
				return false;
			}
			if (!double.TryParse(array[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
			{
				return false;
			}
			if (!double.TryParse(array[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var result2))
			{
				return false;
			}
			if (!double.TryParse(array[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var result3))
			{
				return false;
			}
			double.TryParse(array[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var result4);
			r["gpu_util"] = Math.Min(100.0, Math.Max(0.0, result)).ToString("F1", CultureInfo.InvariantCulture);
			r["vram_used"] = ((long)(result2 * 1048576.0)).ToString(CultureInfo.InvariantCulture);
			r["vram_total"] = ((long)(result3 * 1048576.0)).ToString(CultureInfo.InvariantCulture);
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
			// 统一走 ProcessRunner（同时异步读两流 + 5s 超时），避免“先读后等”让超时失效。
			ProcessResult runResult = ProcessRunner.Run(
				"amd-smi.exe",
				new[] { "metric", "--temperature", "--gpu-utilization", "--vram-usage" },
				5000);
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
				r["gpu_temp_src"] = "amd";
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
			return result;
		}
		catch
		{
			return false;
		}
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
