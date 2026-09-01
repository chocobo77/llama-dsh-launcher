using System.Diagnostics;
using System.IO;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// DSH 维护自动化（依据 DSH_启动操作记录_20260830.md 的遗留建议）:
/// 1. 残留锁清理 —— task-board 插件要求 ledger-v2.lock 可读，DSH 异常退出会残留空锁；
///    在"无 DSH host 运行"时自动移除，免去每次手动删除。
/// 2. 升级重建 —— DSH 仓库 git HEAD 变化后，旧编译产物会导致大面积 MISSING_EXPORT；
///    记录"已重建基线 HEAD"，检测到变化提示自动执行 clean + build:lib + build:web。
/// </summary>
public static class DshMaintenance
{
	public static string LockPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "task-board", "ledger-v2.lock");

	public static string BuildStatePath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llamadsh_dsh_build.json");

	/// <summary>系统真实 node24（PATH 中的沙箱 node v20 不能用于 DSH/pnpm11）。</summary>
	public static string Node24Path => "C:\\Program Files\\nodejs\\node.exe";

	/// <summary>
	/// 是否存在残留锁：锁文件存在，且端口无监听、且无 cmdline 带 DSH 特征的 node 进程。
	/// 三者同时成立才判定为"未干净关闭的残留"，避免误删运行中 host 的锁。
	/// </summary>
	public static bool IsStaleLockPresent(int port)
	{
		try
		{
			if (!File.Exists(LockPath))
			{
				return false;
			}
			if (ProcessHelper.FindProcessByPort(port).HasValue)
			{
				return false;
			}
			if (NodeHostRunning())
			{
				return false;
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>是否存在 DSH 的 node 宿主进程（按命令行特征识别，兼容源码/编译/npx 三种启动方式）。</summary>
	public static bool NodeHostRunning()
	{
		try
		{
			using System.Management.ManagementObjectSearcher searcher = new System.Management.ManagementObjectSearcher(
				"SELECT CommandLine FROM Win32_Process WHERE Name='node.exe'");
			foreach (System.Management.ManagementBaseObject o in searcher.Get())
			{
				string? cl = (o["CommandLine"] as string)?.ToLowerInvariant();
				if (cl != null && (cl.Contains("deepseek-harness") || cl.Contains("@deepseek-ai/dsh") || cl.Contains("apps/cli") || cl.Contains("apps\\cli")))
				{
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	/// <summary>读取 DSH 仓库当前 git HEAD；git 不可用/非仓库时返回 null（跳过升级检测）。</summary>
	public static string? GetGitHead(string repoDir)
	{
		try
		{
			if (!Directory.Exists(Path.Combine(repoDir, ".git")))
			{
				return null;
			}
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName = "cmd.exe",
				Arguments = "/c git rev-parse HEAD",
				WorkingDirectory = repoDir,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			using Process? p = Process.Start(psi);
			if (p == null)
			{
				return null;
			}
			string output = p.StandardOutput.ReadToEnd().Trim();
			p.WaitForExit(8000);
			return (p.HasExited && p.ExitCode == 0 && output.Length == 40) ? output : null;
		}
		catch
		{
			return null;
		}
	}

	public static (string? Head, string? BuiltAt) LoadBuildState()
	{
		try
		{
			if (!File.Exists(BuildStatePath))
			{
				return (null, null);
			}
			using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(BuildStatePath));
			string? head = doc.RootElement.TryGetProperty("head", out var he) ? he.GetString() : null;
			string? at = doc.RootElement.TryGetProperty("builtAt", out var te) ? te.GetString() : null;
			return (head, at);
		}
		catch
		{
			return (null, null);
		}
	}

	public static void SaveBuildState(string head)
	{
		try
		{
			var obj = new { head, builtAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
			File.WriteAllText(BuildStatePath, System.Text.Json.JsonSerializer.Serialize(obj));
		}
		catch
		{
		}
	}
}
