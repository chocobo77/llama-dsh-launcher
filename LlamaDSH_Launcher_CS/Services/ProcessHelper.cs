using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;

namespace LlamaDSH_Launcher.Services;

public static class ProcessHelper
{
	private static readonly Dictionary<string, (DateTime expires, List<int> result)> _orphanCache = new Dictionary<string, (DateTime, List<int>)>(StringComparer.OrdinalIgnoreCase);

	private static readonly object _orphanCacheLock = new object();

	private static readonly Dictionary<int, (string cmd, long tick)> _cmdLineCache = new Dictionary<int, (string, long)>();

	private static readonly object _cmdCacheLock = new object();

	public static void KillProcessTree(int pid, int timeoutMs = 3000)
	{
		List<int> ids = new List<int> { pid };
		CollectProcessTree(pid, ids, 0);
		if (ids.Count == 0)
		{
			return;
		}
		// 先杀叶子再杀父进程，递归收集整棵进程树，确保彻底退出（不会留下孤儿进程）
		for (int i = ids.Count - 1; i >= 0; i--)
		{
			TryKillProcess(ids[i]);
		}
		for (int i = ids.Count - 1; i >= 0; i--)
		{
			TryWaitForExit(ids[i], timeoutMs);
		}
	}

	private static void CollectProcessTree(int parentPid, List<int> result, int depth)
	{
		if (depth > 16)
		{
			return;
		}
		foreach (int child in GetChildProcessIds(parentPid))
		{
			if (!result.Contains(child))
			{
				result.Add(child);
				CollectProcessTree(child, result, depth + 1);
			}
		}
	}

	private static List<int> GetChildProcessIds(int parentPid)
	{
		List<int> list = new List<int>();
		try
		{
			using (ManagementObjectSearcher searcher = new ManagementObjectSearcher($"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId={parentPid}"))
			{
				using (ManagementObjectCollection collection = searcher.Get())
				{
					foreach (ManagementObject obj in collection)
					{
						try
						{
							object value = obj["ProcessId"];
							if (value != null && int.TryParse(value.ToString(), out int childId) && childId > 0 && childId != parentPid)
							{
								list.Add(childId);
							}
						}
						catch
						{
						}
						finally
						{
							obj.Dispose();
						}
					}
				}
			}
		}
		catch
		{
		}
		return list;
	}

	private static void TryKillProcess(int pid)
	{
		try
		{
			using (Process process = Process.GetProcessById(pid))
			{
				if (!process.HasExited)
				{
					process.Kill(entireProcessTree: false);
				}
			}
		}
		catch
		{
		}
	}

	private static void TryWaitForExit(int pid, int timeoutMs)
	{
		try
		{
			using (Process process = Process.GetProcessById(pid))
			{
				process.WaitForExit(timeoutMs);
			}
		}
		catch
		{
		}
	}

	public static int? FindProcessByPort(params int[] ports)
	{
		try
		{
			using Process process = Process.Start(new ProcessStartInfo("netstat", "-ano")
			{
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			});
			if (process == null)
			{
				return null;
			}
			string text = process.StandardOutput.ReadToEnd();
			process.WaitForExit();
			string[] array = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < array.Length; i++)
			{
				string[] array2 = array[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
				if (array2.Length < 5 || !array2[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) || !array2[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				string text2 = array2[1];
				int num = text2.LastIndexOf(':');
				if (num < 0 || !int.TryParse(text2.Substring(num + 1), out var result) || !Enumerable.Contains(ports, result) || !int.TryParse(array2[4], out var result2))
				{
					continue;
				}
				try
				{
					string text3 = Process.GetProcessById(result2).ProcessName.ToLowerInvariant();
					if ((text3 == "node" || text3 == "node.exe") ? true : false)
					{
						return result2;
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
		return null;
	}

	public static List<int> FindChildPids(int parentPid)
	{
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Expected O, but got Unknown
		//IL_0048: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Expected O, but got Unknown
		List<int> list = new List<int>();
		try
		{
			ManagementObjectSearcher val = new ManagementObjectSearcher($"SELECT * FROM Win32_Process WHERE ParentProcessId={parentPid}");
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
								int num = Convert.ToInt32(((ManagementBaseObject)val3)["ProcessId"]);
								list.Add(num);
								list.AddRange(FindChildPids(num));
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
		return list;
	}

	public static List<int> FindOrphanedDshProcesses(string? dshDir = null)
	{
		string key = dshDir ?? "";
		lock (_orphanCacheLock)
		{
			if (_orphanCache.TryGetValue(key, out (DateTime, List<int>) value) && value.Item1 > DateTime.Now)
			{
				return new List<int>(value.Item2);
			}
		}
		List<int> list = FindOrphanedCore(dshDir);
		lock (_orphanCacheLock)
		{
			_orphanCache[key] = (DateTime.Now.AddSeconds(15.0), list);
			return list;
		}
	}

	private static List<int> FindOrphanedCore(string? dshDir)
	{
		List<(int, int)> list = new List<(int, int)>();
		string value = (dshDir ?? "").ToLowerInvariant().Replace('/', '\\');
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node", "node.exe", "electron", "electron.exe" };
		try
		{
			Process[] processes = Process.GetProcesses();
			foreach (Process process in processes)
			{
				try
				{
					if (!hashSet.Contains(process.ProcessName))
					{
						continue;
					}
					string text = GetCommandLine(process.Id).ToLowerInvariant();
					string text2 = process.MainModule?.FileName?.ToLowerInvariant().Replace('/', '\\') ?? "";
					if (!text.Contains("pnpm") || !text.Contains("pnpm.mjs"))
					{
						if (text.Contains("tsx") || text.Contains("cli/src/bin") || text.Contains("apps/cli"))
						{
							list.Add((5, process.Id));
						}
						else if (text.Contains("deepseek-harness") || text2.Contains("deepseek-harness"))
						{
							list.Add((4, process.Id));
						}
						else if (text.Contains("dsh") && (text.Contains("web") || text.Contains("dist") || text.Contains("cli") || text.Contains("server") || text.Contains("start")))
						{
							list.Add((3, process.Id));
						}
						else if (!string.IsNullOrEmpty(value) && (text.Contains(value) || text2.Contains(value)))
						{
							list.Add((2, process.Id));
						}
					}
				}
				catch
				{
				}
				finally
				{
					process.Dispose();
				}
			}
		}
		catch
		{
		}
		list.Sort(((int priority, int pid) a, (int priority, int pid) b) => b.priority.CompareTo(a.priority));
		return list.Select<(int, int), int>(((int priority, int pid) c) => c.pid).ToList();
	}

	public static string GetCommandLine(int pid)
	{
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Expected O, but got Unknown
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Expected O, but got Unknown
		long tickCount = Environment.TickCount64;
		lock (_cmdCacheLock)
		{
			if (_cmdLineCache.TryGetValue(pid, out (string, long) value) && tickCount - value.Item2 < 10000)
			{
				return value.Item1;
			}
		}
		string text = "";
		try
		{
			ManagementObjectSearcher val = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId={pid}");
			try
			{
				ManagementObjectCollection val2 = val.Get();
				try
				{
					var enumerator = val2.GetEnumerator();
					try
					{
						if (enumerator.MoveNext())
						{
							ManagementObject val3 = (ManagementObject)enumerator.Current;
							try
							{
								text = ((ManagementBaseObject)val3)["CommandLine"]?.ToString() ?? "";
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
		lock (_cmdCacheLock)
		{
			_cmdLineCache[pid] = (text, tickCount);
			return text;
		}
	}
}
