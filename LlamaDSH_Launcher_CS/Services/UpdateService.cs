using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// 更新自动化：
/// 1. llama.cpp —— 检查 GitHub Releases（bNNNNN），下载对应预编译包（优先 Vulkan-x64，
///    找不到则回退 AVX2-x64）覆盖到启动器配置的实际 llama.cpp 目录；不碰模型。
/// 2. deepseek-harness —— git fetch 对比上游，落后时 git pull --ff-only，随后由调用方
///    走既有全量重建（clean + build:lib + build:web）。
/// 3. 更新后清理本启动器产生的临时垃圾（下载包、解压目录、旧重建/转换 cmd 与日志等）。
/// </summary>
public static class UpdateService
{
	private static readonly HttpClient Http = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(90)
	};

	private static readonly string UpdateTempRoot = Path.Combine(
		Path.GetTempPath(), "LlamaDSH_Launcher", "updates");

	// ==================== llama.cpp ====================

	public sealed class LlamaReleaseInfo
	{
		public string Tag = "";
		public int Build;
		public string AssetName = "";
		public string AssetUrl = "";
	}

	/// <summary>从配置目录解析 llama-server.exe 完整路径（兼容目录或 exe 直填）。</summary>
	public static string? FindLlamaServerExe(string dir)
	{
		try
		{
			string raw = (dir ?? "").Trim().Trim('"');
			if (string.IsNullOrEmpty(raw))
			{
				return null;
			}
			if (Directory.Exists(raw))
			{
				string exe = Path.Combine(raw, "llama-server.exe");
				return File.Exists(exe) ? exe : null;
			}
			if (File.Exists(raw))
			{
				return raw;
			}
			if (File.Exists(raw + ".exe"))
			{
				return raw + ".exe";
			}
		}
		catch
		{
		}
		return null;
	}

	/// <summary>运行 llama-server --version，解析 build 号（如 10605）。失败返回 null。</summary>
	public static int? GetLocalLlamaBuild(string dir)
	{
		try
		{
			string? exe = FindLlamaServerExe(dir);
			if (exe == null)
			{
				return null;
			}
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName = exe,
				Arguments = "--version",
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
			string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
			p.WaitForExit(8000);
			Match m = Regex.Match(output, @"build\s+(\d+)", RegexOptions.IgnoreCase);
			return m.Success ? int.Parse(m.Groups[1].Value) : null;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>查询 GitHub 最新 llama.cpp release，返回标签/build/资产 URL。失败返回 null。</summary>
	public static LlamaReleaseInfo? FetchLatestLlamaRelease()
	{
		string[] urls = new string[]
		{
			"https://api.github.com/repos/ggml-org/llama.cpp/releases/latest"
		};
		foreach (string url in urls)
		{
			try
			{
				using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url))
				{
					req.Headers.TryAddWithoutValidation("User-Agent", "LlamaDSH_Launcher");
					using (HttpResponseMessage resp = Http.Send(req))
					{
						if (!resp.IsSuccessStatusCode)
						{
							continue;
						}
						using (JsonDocument doc = JsonDocument.Parse(resp.Content.ReadAsStream()))
						{
							JsonElement root = doc.RootElement;
							string tag = root.TryGetProperty("tag_name", out JsonElement te) ? te.GetString() ?? "" : "";
							int? build = ParseBuildFromTag(tag);
							if (build == null)
							{
								continue;
							}
							string assetName = "";
							string assetUrl = "";
							if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
							{
								foreach (JsonElement a in assets.EnumerateArray())
								{
									string name = a.TryGetProperty("name", out JsonElement ne) ? ne.GetString() ?? "" : "";
									if (!name.Contains("-bin-win-") || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
									{
										continue;
									}
									// 优先 Vulkan-x64；其次 AVX2-x64（Vulkan 包缺失时的保守回退）
									if (name.Contains("vulkan-x64"))
									{
										assetName = name;
										assetUrl = a.TryGetProperty("browser_download_url", out JsonElement ue) ? ue.GetString() ?? "" : "";
										break;
									}
									if (string.IsNullOrEmpty(assetName) && name.Contains("avx2-x64"))
									{
										assetName = name;
										assetUrl = a.TryGetProperty("browser_download_url", out JsonElement ue2) ? ue2.GetString() ?? "" : "";
									}
								}
							}
							if (!string.IsNullOrEmpty(assetUrl))
							{
								return new LlamaReleaseInfo { Tag = tag, Build = build.Value, AssetName = assetName, AssetUrl = assetUrl };
							}
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

	private static int? ParseBuildFromTag(string tag)
	{
		Match m = Regex.Match(tag ?? "", @"^b(\d+)$", RegexOptions.IgnoreCase);
		if (m.Success && int.TryParse(m.Groups[1].Value, out int build))
		{
			return build;
		}
		// 兼容形如 v0.2.0 的标签：无 build 号时视为未知，不参与比较
		return null;
	}

	/// <summary>下载 llama.cpp 预编译 zip 并覆盖到目标目录（最小更新：只换二进制，不碰模型）。</summary>
	public static async Task<(bool ok, string message)> ApplyLlamaUpdateAsync(string dir, LlamaReleaseInfo release)
	{
		try
		{
			string targetDir = (dir ?? "").Trim().Trim('"');
			if (!Directory.Exists(targetDir))
			{
				return (false, "llama.cpp 目录不存在: " + targetDir);
			}
			Directory.CreateDirectory(UpdateTempRoot);
			string zipPath = Path.Combine(UpdateTempRoot, release.AssetName);
			string extractDir = Path.Combine(UpdateTempRoot, "extract_" + release.Build);
			if (Directory.Exists(extractDir))
			{
				Directory.Delete(extractDir, true);
			}

			// 下载：直连 GitHub，失败依次尝试 ghproxy/ghfast 镜像
			string[] urls = new string[]
			{
				release.AssetUrl,
				"https://ghproxy.net/" + release.AssetUrl,
				"https://ghfast.top/" + release.AssetUrl,
				"https://gh-proxy.com/" + release.AssetUrl
			};
			bool downloaded = false;
			foreach (string u in urls)
			{
				try
				{
					using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, u))
					{
						req.Headers.TryAddWithoutValidation("User-Agent", "LlamaDSH_Launcher");
						using (HttpResponseMessage resp = await Http.SendAsync(req))
						{
							if (!resp.IsSuccessStatusCode)
							{
								continue;
							}
							using (FileStream fs = File.Create(zipPath))
							{
								await resp.Content.CopyToAsync(fs);
							}
							if (new FileInfo(zipPath).Length < 1000000)
							{
								continue;
							}
							downloaded = true;
							break;
						}
					}
				}
				catch
				{
				}
			}
			if (!downloaded)
			{
				return (false, "下载 llama.cpp 预编译包失败（GitHub/镜像均不可达或文件过小）");
			}

			ZipFile.ExtractToDirectory(zipPath, extractDir);
			string? srcBin = extractDir;
			// 官方包解压后通常直接是 exe/dll，也可能有一层子目录，自动定位含 llama-server.exe 的目录
			if (!File.Exists(Path.Combine(srcBin, "llama-server.exe")))
			{
				string? found = FindDirContaining(extractDir, "llama-server.exe");
				if (found == null)
				{
					return (false, "预编译包内未找到 llama-server.exe，可能不是 Windows 二进制包");
				}
				srcBin = found;
			}

			// 覆盖：全部文件/子目录复制到目标目录
			CopyDirOverwrite(srcBin, targetDir);

			// 清理本次下载/解压临时文件
			CleanupUpdateTemp();
			return (true, "llama.cpp 已更新到 " + release.Tag + "（build " + release.Build + "）");
		}
		catch (Exception ex)
		{
			return (false, "更新失败: " + ex.Message);
		}
	}

	private static string? FindDirContaining(string root, string fileName)
	{
		try
		{
			foreach (string d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
			{
				if (File.Exists(Path.Combine(d, fileName)))
				{
					return d;
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static void CopyDirOverwrite(string src, string dst)
	{
		Directory.CreateDirectory(dst);
		foreach (string f in Directory.GetFiles(src))
		{
			File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
		}
		foreach (string d in Directory.GetDirectories(src))
		{
			CopyDirOverwrite(d, Path.Combine(dst, Path.GetFileName(d)));
		}
	}

	// ==================== deepseek-harness ====================

	public sealed class DshUpdateInfo
	{
		public string Branch = "";
		public string LocalHead = "";
		public string RemoteHead = "";
		public int Ahead;
		public int Behind;
	}

	/// <summary>git fetch 上游并返回本地/远程 HEAD 与领先/落后计数；不可更新（非仓库/无 remote）返回 null。</summary>
	public static DshUpdateInfo? CheckDshUpdate(string repoDir)
	{
		try
		{
			string dir = (repoDir ?? "").Trim().Trim('"');
			if (!Directory.Exists(Path.Combine(dir, ".git")))
			{
				return null;
			}
			if (RunGit(dir, "fetch origin --prune", 120).ExitCode != 0)
			{
				return null;
			}
			string branch = RunGit(dir, "rev-parse --abbrev-ref HEAD", 15).Output.Trim();
			if (string.IsNullOrEmpty(branch))
			{
				return null;
			}
			string local = RunGit(dir, "rev-parse HEAD", 15).Output.Trim();
			string remote = RunGit(dir, "rev-parse origin/" + branch, 15).Output.Trim();
			if (local.Length != 40 || remote.Length != 40)
			{
				return null;
			}
			// 输出格式: "领先数\t落后数"（HEAD...origin/branch）
			string counts = RunGit(dir, "rev-list --left-right --count HEAD...origin/" + branch, 15).Output.Trim();
			string[] parts = counts.Split('\t', StringSplitOptions.RemoveEmptyEntries);
			int ahead = parts.Length > 0 && int.TryParse(parts[0], out int a) ? a : 0;
			int behind = parts.Length > 1 && int.TryParse(parts[1], out int b) ? b : 0;
			return new DshUpdateInfo { Branch = branch, LocalHead = local, RemoteHead = remote, Ahead = ahead, Behind = behind };
		}
		catch
		{
			return null;
		}
	}

	/// <summary>执行 git pull --ff-only，返回 (是否成功, 新 HEAD/错误信息)。</summary>
	public static (bool ok, string message) PullDsh(string repoDir)
	{
		try
		{
			string dir = (repoDir ?? "").Trim().Trim('"');
			(var code, string output) = RunGit(dir, "pull --ff-only", 180);
			if (code != 0)
			{
				return (false, output.Trim());
			}
			string head = RunGit(dir, "rev-parse HEAD", 15).Output.Trim();
			return (head.Length == 40, head.Length == 40 ? head : "pull 成功但无法读取新 HEAD");
		}
		catch (Exception ex)
		{
			return (false, ex.Message);
		}
	}

	private static (int ExitCode, string Output) RunGit(string repoDir, string args, int timeoutSec)
	{
		try
		{
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName = "git",
				Arguments = args,
				WorkingDirectory = repoDir,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
				StandardOutputEncoding = System.Text.Encoding.UTF8,
				StandardErrorEncoding = System.Text.Encoding.UTF8
			};
			using Process? p = Process.Start(psi);
			if (p == null)
			{
				return (-1, "git 启动失败");
			}
			string stdout = p.StandardOutput.ReadToEnd();
			string stderr = p.StandardError.ReadToEnd();
			if (!p.WaitForExit(timeoutSec * 1000))
			{
				try
				{
					p.Kill();
				}
				catch
				{
				}
				return (-1, "git 执行超时");
			}
			return (p.ExitCode, stdout + stderr);
		}
		catch (Exception ex)
		{
			return (-1, ex.Message);
		}
	}

	// ==================== 垃圾清理 ====================

	/// <summary>
	/// 清理启动器更新/转换/重建过程产生的临时垃圾：
	/// updates 下载与解压根目录、旧重建 cmd/log、旧转换 cmd、gpu_perf.ps1。
	/// 不删除正在使用的转换/重建日志（带锁保护），不碰模型、node_modules、配置。
	/// </summary>
	public static void CleanupLauncherTemp()
	{
		try
		{
			CleanupUpdateTemp();
		}
		catch
		{
		}
		try
		{
			string launcherTemp = Path.Combine(Path.GetTempPath(), "LlamaDSH_Launcher");
			if (Directory.Exists(launcherTemp))
			{
				// gpu_perf.ps1 每次写入前会自校验，删除无影响
				foreach (string f in Directory.GetFiles(launcherTemp, "*.ps1"))
				{
					TryDelete(f);
				}
				foreach (string f in Directory.GetFiles(launcherTemp, "*.cmd"))
				{
					TryDelete(f);
				}
				foreach (string f in Directory.GetFiles(launcherTemp, "*.log"))
				{
					TryDelete(f);
				}
			}
		}
		catch
		{
		}
		// 根临时目录下的旧重建/转换引导文件
		string tmp = Path.GetTempPath();
		foreach (string f in new string[]
		{
			Path.Combine(tmp, "LlamaDSH_dsh_rebuild.cmd"),
			Path.Combine(tmp, "LlamaDSH_dsh_rebuild.log"),
			Path.Combine(tmp, "llamadsh_convert.cmd"),
			Path.Combine(tmp, "conversion_kit.zip")
		})
		{
			TryDelete(f);
		}
	}

	public static void CleanupUpdateTemp()
	{
		try
		{
			if (Directory.Exists(UpdateTempRoot))
			{
				Directory.Delete(UpdateTempRoot, true);
			}
		}
		catch
		{
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}
}
