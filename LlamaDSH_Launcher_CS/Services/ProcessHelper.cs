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
		// Kill(entireProcessTree) 由内核按父子关系整树终止, 无"先收集后击杀"窗口——
		// 旧 WMI 递归收集在收集与击杀之间子进程退出且 PID 被复用时, 会误杀无辜进程
		try
		{
			using Process p = Process.GetProcessById(pid);
			p.Kill(entireProcessTree: true);
		}
		catch
		{
			// 进程已退出: 无需处理
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
				if (ProcLiveness.Alive(process))
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
		// 优先托管 GetExtendedTcpTable: 不派生 netstat 进程, UI 线程高频调用零开销;
		// 原生调用失败再回退 netstat 解析
		try
		{
			int? managed = FindPidByPortNative(ports);
			if (managed.HasValue)
			{
				return managed;
			}
		}
		catch
		{
		}
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
					// 不限进程名: 本回退路径与 iphlpapi 主路径语义一致 ——
					// llama 端口预检也走这里, 按 node 过滤会让预检在降级机器上失效
					return result2;
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

	#region 端口属主查询(iphlpapi)

	private const int AF_INET = 2;
	private const int TCP_TABLE_OWNER_PID_LISTENING = 3;

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private struct MIB_TCPTABLE_OWNER_PID
	{
		public uint dwNumEntries;
		// MIB_TCPROW_OWNER_PID 数组首元素占位, 实际按字节数组解析
	}

	[System.Runtime.InteropServices.DllImport("iphlpapi.dll", SetLastError = true)]
	private static extern uint GetExtendedTcpTable(byte[] pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tableClass, int reserved = 0);

	/// <summary>返回任一目标端口 LISTENING 套接字的属主 PID; 不限定进程名(端口被谁占就报谁)。</summary>
	private static int? FindPidByPortNative(int[] ports)
	{
		int size = 0;
		// 首次调用取所需缓冲区大小
		uint ret = GetExtendedTcpTable(null, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENING);
		if (ret != 0 && ret != 122 /* ERROR_INSUFFICIENT_BUFFER */)
		{
			return null;
		}
		if (size <= 0)
		{
			return null;
		}
		byte[] buf = new byte[size];
		ret = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENING);
		if (ret != 0)
		{
			return null;
		}
		uint entries = BitConverter.ToUInt32(buf, 0);
		int offset = 4;
		for (uint i = 0; i < entries && offset + 24 <= buf.Length; i++, offset += 24)
		{
			uint state = BitConverter.ToUInt32(buf, offset);
			if (state != 0x0A) // MIB_TCP_STATE_LISTEN
			{
				continue;
			}
			// dwLocalPort 为网络字节序 16 位端口
			uint rawPort = BitConverter.ToUInt32(buf, offset + 12);
			int port = (int)(((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF));
			if (!ports.Contains(port))
			{
				continue;
			}
			int pid = (int)BitConverter.ToUInt32(buf, offset + 20);
			if (pid > 0)
			{
				return pid;
			}
		}
		return null;
	}

	#endregion

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

	/// <summary>
	/// 判定 pid 是否像本程序要接管的 DSH：进程名必须是 node/electron，且命令行或镜像路径含 DSH 特征。
	/// 端口探测不构成身份证据——用户自己跑在 3080 的任意进程若不校验，会被标成"接管运行中"，
	/// 点"停止"就把它的整棵进程树杀掉（llama 侧已有同样的 ProcessName 闸门，这里补齐对称）。
	/// </summary>
	public static bool LooksLikeDshProcess(int pid, string? dshDir = null, int port = DshIdentity.DshPort)
	{
		return DshIdentityProbe(pid, dshDir, port).Ok;
	}

	/// <summary>
	/// 分层身份判定：进程名 → 命令行 → DSH 的 HTTP 指纹（见 DshIdentity）。
	/// port 传 0 表示不做 HTTP 兜底（只用于"绝不能阻塞"的调用点）。
	/// </summary>
	public static DshIdentity.Verdict DshIdentityProbe(int pid, string? dshDir = null, int port = DshIdentity.DshPort)
	{
		try
		{
			using Process process = Process.GetProcessById(pid);
			string cmd = GetCommandLine(process.Id);
			string image = "";
			try
			{
				image = process.MainModule?.FileName ?? "";
			}
			catch
			{
				// 跨完整性级别/32-64 位读不到 MainModule：命令行与 HTTP 指纹两层还在
			}
			// 命令行已经证明是 DSH 就不必再发 HTTP 探测（最多 1.2 秒），只有需要兜底时才探测
			bool? fp = (port > 0 && !DshIdentity.CommandLineMatches(cmd, image, dshDir)) ? DshIdentity.HttpFingerprint(port) : null;
			return DshIdentity.Judge(process.ProcessName, cmd, image, dshDir, fp);
		}
		catch (Exception ex)
		{
			// 进程已退出或 PID 被复用：一律不接管
			DshIdentity.Verdict v = new DshIdentity.Verdict { Reason = "进程已退出或 PID 已被复用" };
			AppLog.WriteError("DSH 身份判定失败(pid=" + pid + "): " + ex.Message);
			return v;
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
						if (text.Contains("cli/src/bin") || text.Contains("apps/cli") || text.Contains("@deepseek-ai"))
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
			if (_cmdLineCache.TryGetValue(pid, out (string, long) value) && tickCount - value.Item2 < (string.IsNullOrEmpty(value.Item1) ? 2000 : 10000))
			{
				// 空结果只缓存 2 秒: WMI 偶发失败(或跨完整性级别读不到)若按 10 秒缓存,
				// 会把"读不到"固化成一段时间内的判定依据
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
