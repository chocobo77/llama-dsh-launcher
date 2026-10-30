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
		// 不设整体超时：API 请求用 30s CTS，预编译包下载用 20min CTS（默认 90s 在慢网/大包时必失败）。
		Timeout = Timeout.InfiniteTimeSpan
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
		public string CudartName = "";
		public long Size;
		public long CudartSize;
		public string CudartUrl = "";
		/// <summary>稳定版标签（如 v0.4.0）；滚动构建时为空。</summary>
		public string StableVersion = "";
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
				if (File.Exists(exe))
				{
					return exe;
				}
				// 有些发行包把二进制放在 bin / build\bin\Release / Release 子目录
				foreach (string sub in new[] { "bin", "build\\bin\\Release", "build\\bin", "Release", "windows" })
				{
					string candidate = Path.Combine(raw, sub, "llama-server.exe");
					if (File.Exists(candidate))
					{
						return candidate;
					}
				}
				return null;
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
			// 统一走 ProcessRunner：内部同时异步读两流并带 8s 超时，避免“先读后等”让超时失效。
			ProcessResult result = ProcessRunner.Run(exe, new[] { "--version" }, 8000);
			if (!result.Started)
			{
				return null;
			}
			string output = result.StdOut + result.StdErr;
			Match m = Regex.Match(output, @"build\s+(\d+)", RegexOptions.IgnoreCase);
			return m.Success ? int.Parse(m.Groups[1].Value) : null;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 查询 llama.cpp 最新可更新版本。
	/// llama.cpp 的 release 结构：
	///  - 稳定版标签（v0.4.0）本身没有二进制，只有 nightly-tag.txt 指向对应的 bNNNNN 构建；
	///  - 真正含预编译二进制的 release 是 bNNNNN（GitHub 标记为 prerelease）。
	/// 因此优先读 stable release 的 nightly-tag.txt → 定位稳定构建资产；
	/// 失败时回退扫描最近 releases 列表取最新 bNNNNN（滚动构建）。
	/// </summary>
	public static LlamaReleaseInfo? FetchLatestLlamaRelease(string repo = "ggml-org/llama.cpp", string backend = "vulkan")
	{
		// 1) 稳定版：latest release 里读 nightly-tag.txt
		try
		{
			string? latestJson = HttpGetString($"https://api.github.com/repos/{repo}/releases/latest", 10);
			if (latestJson != null)
			{
				using (JsonDocument doc = JsonDocument.Parse(latestJson))
				{
					JsonElement root = doc.RootElement;
					string stableTag = root.TryGetProperty("tag_name", out JsonElement se) ? se.GetString() ?? "" : "";
					string? nightlyUrl = null;
					if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
					{
						foreach (JsonElement a in assets.EnumerateArray())
						{
							string name = a.TryGetProperty("name", out JsonElement ne) ? ne.GetString() ?? "" : "";
							if (string.Equals(name, "nightly-tag.txt", StringComparison.OrdinalIgnoreCase))
							{
								nightlyUrl = a.TryGetProperty("browser_download_url", out JsonElement ue) ? ue.GetString() : null;
								break;
							}
						}
					}
					if (!string.IsNullOrEmpty(nightlyUrl))
					{
						string content = HttpGetString(nightlyUrl, 10) ?? "";
						int? build = ParseBuildFromTag(content.Trim());
						if (build != null)
						{
							LlamaReleaseInfo? ri = FetchBuildRelease(build.Value, repo, backend);
							if (ri != null)
							{
								ri.StableVersion = stableTag;
								return ri;
							}
						}
					}
					// 无 nightly-tag 资产的分支(如 PrismML): latest 的 tag 内嵌 build, 直接用其资产
					int? tagBuild = ParseBuildFromTag(stableTag);
					if (tagBuild != null)
					{
						LlamaReleaseInfo? ri = BuildFromReleaseJson(root, tagBuild.Value, backend);
						if (ri != null)
						{
							ri.StableVersion = stableTag;
							return ri;
						}
					}
				}
			}
		}
		catch
		{
		}

		// 2) 回退：滚动构建（最新 bNNNNN）
		try
		{
			string? listJson = HttpGetString($"https://api.github.com/repos/{repo}/releases?per_page=20", 10);
			if (listJson != null)
			{
				using (JsonDocument doc = JsonDocument.Parse(listJson))
				{
					foreach (JsonElement r in doc.RootElement.EnumerateArray())
					{
						string tag = r.TryGetProperty("tag_name", out JsonElement te) ? te.GetString() ?? "" : "";
						// 滚动扫描用严格形态: 纯 bNNNNN 或 内嵌build+hex短哈希(PrismML 形态);
						// 宽松解析会把 topic-b12345-fix 之类普通 tag 误判成 build
						Match sm = Regex.Match(tag, @"^(?:b(\d+)$|^[A-Za-z0-9_-]*b(\d{3,})-[0-9a-fA-F]{6,}$)");
						int? build = sm.Success && int.TryParse(sm.Groups[1].Success ? sm.Groups[1].Value : sm.Groups[2].Value, out int sb2) ? sb2 : null;
						if (build == null)
						{
							continue;
						}
						LlamaReleaseInfo? ri = BuildFromReleaseJson(r, build.Value, backend);
						if (ri != null)
						{
							return ri;
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

	/// <summary>按 bNNNNN 标签拉取对应 release 并解析 Windows 预编译资产。</summary>
	private static LlamaReleaseInfo? FetchBuildRelease(int build, string repo, string backend = "vulkan")
	{
		string? json = HttpGetString($"https://api.github.com/repos/{repo}/releases/tags/b{build}", 10);
		if (json == null)
		{
			return null;
		}
		using (JsonDocument doc = JsonDocument.Parse(json))
		{
			return BuildFromReleaseJson(doc.RootElement, build, backend);
		}
	}

	private static LlamaReleaseInfo? BuildFromReleaseJson(JsonElement release, int build, string backend = "vulkan")
	{
		string tag = release.TryGetProperty("tag_name", out JsonElement te) ? te.GetString() ?? ("b" + build) : ("b" + build);
		// 收集全部 Windows x64 预编译主包候选（排除 cudart 纯运行库与 arm64）
		var cands = new List<(string name, string url)>();
		var sizes = new Dictionary<string, long>();
		if (release.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement a in assets.EnumerateArray())
			{
				string name = a.TryGetProperty("name", out JsonElement ne) ? ne.GetString() ?? "" : "";
				string url = a.TryGetProperty("browser_download_url", out JsonElement ue) ? ue.GetString() ?? "" : "";
				if (!name.Contains("-bin-win-") || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				if (name.Contains("cudart", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				if (!name.Contains("x64", StringComparison.OrdinalIgnoreCase) || name.Contains("arm64", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				long aSize = a.TryGetProperty("size", out JsonElement se2) && se2.ValueKind == JsonValueKind.Number ? se2.GetInt64() : 0;
				cands.Add((name, url));
				sizes[name] = aSize;
			}
		}
		// 按显卡挑后端: NVIDIA=CUDA(取最高版本), Intel=SYCL(原生低比特内核), AMD/默认=Vulkan; 依次回退
		(string name, string url)? chosen = backend switch
		{
			"sycl" => PickAsset(cands, "sycl-x64") ?? PickAsset(cands, "vulkan-x64") ?? PickAsset(cands, "avx2-x64"),
			"cuda" => PickCudaAsset(cands) ?? PickAsset(cands, "vulkan-x64") ?? PickAsset(cands, "avx2-x64"),
			_ => PickAsset(cands, "vulkan-x64") ?? PickAsset(cands, "avx2-x64"),
		};
		chosen ??= cands.Count > 0 ? cands[0] : null;
		if (chosen == null)
		{
			return null;
		}
		// CUDA 主包按 cuda-X.Y 版本号配对 cudart 运行库
		string cudartName = "";
		string cudartUrl = "";
		Match cv = Regex.Match(chosen.Value.name, @"cuda-([\d.]+)-x64", RegexOptions.IgnoreCase);
		if (cv.Success && release.TryGetProperty("assets", out JsonElement assets2) && assets2.ValueKind == JsonValueKind.Array)
		{
			string want = "cudart-llama-bin-win-cuda-" + cv.Groups[1].Value + "-x64.zip";
			foreach (JsonElement a in assets2.EnumerateArray())
			{
				string name = a.TryGetProperty("name", out JsonElement ne2) ? ne2.GetString() ?? "" : "";
				if (a.TryGetProperty("size", out JsonElement se3) && se3.ValueKind == JsonValueKind.Number)
				{
					sizes[name] = se3.GetInt64();
				}
				if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase))
				{
					cudartName = name;
					cudartUrl = a.TryGetProperty("browser_download_url", out JsonElement ue2) ? ue2.GetString() ?? "" : "";
					break;
				}
			}
		}
		sizes.TryGetValue(chosen.Value.name, out long chosenSz);
		sizes.TryGetValue(cudartName, out long cudartSz);
		return new LlamaReleaseInfo { Tag = tag, Build = build, AssetName = chosen.Value.name, AssetUrl = chosen.Value.url, Size = chosenSz, CudartName = cudartName, CudartUrl = cudartUrl, CudartSize = cudartSz };
	}

	private static (string name, string url)? PickAsset(List<(string name, string url)> cands, string token)
	{
		foreach (var c in cands)
		{
			if (c.name.Contains(token, StringComparison.OrdinalIgnoreCase))
			{
				return c;
			}
		}
		return null;
	}

	private static (string name, string url)? PickCudaAsset(List<(string name, string url)> cands)
	{
		(string name, string url)? best = null;
		Version bestV = new Version(0, 0);
		foreach (var c in cands)
		{
			Match m = Regex.Match(c.name, @"cuda-([\d.]+)-x64", RegexOptions.IgnoreCase);
			if (!m.Success)
			{
				continue;
			}
			if (Version.TryParse(m.Groups[1].Value, out Version? v) && v > bestV)
			{
				bestV = v;
				best = c;
			}
		}
		return best;
	}

	private static string? HttpGetString(string url, int timeoutSec = 30)
	{
		// 直连 + GitHub 代理镜像；部分网络只封锁 api.github.com，镜像可直接透传 JSON。
		List<string> candidates = new() { url };
		if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
		{
			candidates.Add("https://gh-proxy.com/" + url);
			candidates.Add("https://ghfast.top/" + url);
		}
		foreach (string candidate in candidates)
		{
			for (int attempt = 0; attempt < 2; attempt++)
			{
				try
				{
					using CancellationTokenSource cts = new(TimeSpan.FromSeconds(timeoutSec));
					using HttpRequestMessage req = new(HttpMethod.Get, candidate);
					req.Headers.TryAddWithoutValidation("User-Agent", "LlamaDSH_Launcher");
					using HttpResponseMessage resp = Http.Send(req, cts.Token);
					if (!resp.IsSuccessStatusCode)
					{
						break;
					}
					return resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
				}
				catch
				{
					if (attempt == 0)
					{
						System.Threading.Thread.Sleep(400);
					}
				}
			}
		}
		return null;
	}

	private static int? ParseBuildFromTag(string tag)
	{
		string t = (tag ?? "").Trim();
		Match m = Regex.Match(t, @"^b(\d+)$", RegexOptions.IgnoreCase);
		if (m.Success && int.TryParse(m.Groups[1].Value, out int build))
		{
			return build;
		}
		// 兼容纯数字内容（如 nightly-tag.txt 只写 10809）
		m = Regex.Match(t, @"^(\d+)$");
		if (m.Success && int.TryParse(m.Groups[1].Value, out build))
		{
			return build;
		}
		// 分支 tag 内嵌 build 号（如 PrismML 的 prism-b10831-9d9efea → 10831）
		m = Regex.Match(t, @"b(\d{3,})", RegexOptions.IgnoreCase);
		if (m.Success && int.TryParse(m.Groups[1].Value, out build))
		{
			return build;
		}
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
			// 目标 exe 被占用(服务运行中)时提前失败, 避免复制到一半留下混合二进制
			string targetExe = Path.Combine(targetDir, "llama-server.exe");
			if (File.Exists(targetExe))
			{
				try
				{
					using (new FileStream(targetExe, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
					{
					}
				}
				catch
				{
					return (false, "目标 llama-server.exe 正在运行或被占用, 请先停止 Llama 服务再更新/安装。");
				}
			}
			Directory.CreateDirectory(UpdateTempRoot);
			// 磁盘空间预检: 主包 + cudart + 解压副本, 按 3 倍估
			long needBytes = Math.Max(release.Size * 3, 512L * 1024 * 1024);
			try
			{
				DriveInfo di = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(UpdateTempRoot))!);
				if (di.AvailableFreeSpace < needBytes)
				{
					return (false, $"磁盘空间不足: 需要约 {needBytes / 1048576.0:F0}MB, 剩余 {di.AvailableFreeSpace / 1048576.0:F0}MB ({di.Name})");
				}
			}
			catch
			{
			}
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
				"https://gh-proxy.com/" + release.AssetUrl,
				"https://ghfast.top/" + release.AssetUrl,
				"https://ghproxy.net/" + release.AssetUrl
			};
			bool downloaded = await DownloadAssetAsync(urls, zipPath, release.Size);
			if (!downloaded)
			{
				return (false, "下载 llama.cpp 预编译包失败（GitHub/镜像均不可达或文件过小）");
			}

			// CUDA 主包需配套 cudart 运行库, 一并下载
			string? cudartDir = null;
			string cudartNote = "";
			if (!string.IsNullOrEmpty(release.CudartUrl))
			{
				string czip = Path.Combine(UpdateTempRoot, release.CudartName);
				string cext = Path.Combine(UpdateTempRoot, "extract_cudart_" + release.Build);
				if (Directory.Exists(cext))
				{
					Directory.Delete(cext, true);
				}
				if (await DownloadAssetAsync(new[] { release.CudartUrl, "https://gh-proxy.com/" + release.CudartUrl, "https://ghfast.top/" + release.CudartUrl, "https://ghproxy.net/" + release.CudartUrl }, czip, release.CudartSize))
				{
					ZipFile.ExtractToDirectory(czip, cext);
					cudartDir = FindDirContainingAnyDll(cext);
				}
				if (cudartDir == null)
				{
					cudartNote = "（警告: 配套 cudart 运行库未就位, 启动 CUDA 版可能报缺 DLL）";
				}
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
			if (cudartDir != null)
			{
				CopyDirOverwrite(cudartDir, targetDir);
			}

			// 落盘自检: 目标目录 llama-server --version 必须报告 build 号,
			// 否则复制可能被占用/失败留下了旧二进制(stock 版会拒载自定义量化类型)
			int? localBuild = GetLocalLlamaBuild(targetDir);
			// 硬校验: build 缺失或不一致都按失败处理 —— 混入旧/异源二进制会导致
			// 加载模型时报 ggml type 错误(此前只拼警告仍返回成功, 假安装藏不住)
			if (!localBuild.HasValue)
			{
				CleanupUpdateTemp();
				return (false, "安装后本地 llama-server --version 未报告 build 号, 二进制可能不完整; 建议删除该目录后重新安装。");
			}
			if (release.Build > 0 && localBuild.Value != release.Build)
			{
				CleanupUpdateTemp();
				return (false, $"安装后本地 build {localBuild.Value} 与上游 release build {release.Build} 不一致, 目录内可能混有旧/异源二进制; 建议删除该目录后重新安装。");
			}
			string verify = "本地验证 build " + localBuild.Value;

			// 清理本次下载/解压临时文件
			CleanupUpdateTemp();
			return (true, "llama.cpp 已更新到 " + release.Tag + "（build " + release.Build + "）[" + release.AssetName + "]" + cudartNote + ", " + verify);
		}
		catch (Exception ex)
		{
			return (false, "更新失败: " + ex.Message);
		}
	}

	/// <summary>
	/// 按顺序尝试下载（直连 + 镜像）。写 .part 临时文件, 成功后原子改名;
	/// expectedSize>0 时校验长度(±1%), 防镜像返回错误页/截断包; 失败删 .part 换下一源。
	/// </summary>
	private static async Task<bool> DownloadAssetAsync(IEnumerable<string> urls, string destPath, long expectedSize = 0)
	{
		string partPath = destPath + ".part";
		foreach (string u in urls)
		{
			try
			{
				using CancellationTokenSource dlCts = new(TimeSpan.FromMinutes(20));
				using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, u))
				{
					req.Headers.TryAddWithoutValidation("User-Agent", "LlamaDSH_Launcher");
					using (HttpResponseMessage resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, dlCts.Token))
					{
						if (!resp.IsSuccessStatusCode)
						{
							continue;
						}
						using (FileStream fs = File.Create(partPath))
						{
							await resp.Content.CopyToAsync(fs, dlCts.Token);
						}
						long len = new FileInfo(partPath).Length;
						if (len < 1000000)
						{
							continue;
						}
						if (expectedSize > 0 && Math.Abs(len - expectedSize) > expectedSize / 100)
						{
							continue;
						}
						File.Move(partPath, destPath, overwrite: true);
						return true;
					}
				}
			}
			catch
			{
			}
			finally
			{
				try
				{
					if (File.Exists(partPath))
					{
						File.Delete(partPath);
					}
				}
				catch
				{
				}
			}
		}
		return false;
	}

	private static string? FindDirContainingAnyDll(string root)
	{
		try
		{
			foreach (string f in Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories))
			{
				return Path.GetDirectoryName(f);
			}
		}
		catch
		{
		}
		return null;
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
			string dstFile = Path.Combine(dst, Path.GetFileName(f));
			try
			{
				File.Copy(f, dstFile, overwrite: true);
			}
			catch (UnauthorizedAccessException)
			{
				// 只读目标(压缩包属性带出/安全软件置位): 解只读后重试一次
				if (File.Exists(dstFile))
				{
					File.SetAttributes(dstFile, FileAttributes.Normal);
					File.Copy(f, dstFile, overwrite: true);
				}
				else
				{
					throw;
				}
			}
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
		/// <summary>实际使用的 remote 名或镜像 URL（origin 表示直连成功）。</summary>
		public string Mirror = "origin";
	}

	/// <summary>
	/// git fetch 上游并返回本地/远程 HEAD 与领先/落后计数。
	/// 直连失败时自动尝试 gh-proxy/ghfast/ghproxy 镜像（HTTP/1.1，规避代理下 git 的 malloc 问题），
	/// 并通过 error 返回具体原因（不是笼统的“网络异常”）。
	/// </summary>
	public static DshUpdateInfo? CheckDshUpdate(string repoDir, out string error)
	{
		error = "";
		string dir = (repoDir ?? "").Trim().Trim('"');
		if (dir.Length == 0)
		{
			error = "DSH 目录为空：请先自动检索或手动选择 deepseek-harness 仓库目录。";
			return null;
		}
		if (!Directory.Exists(Path.Combine(dir, ".git")))
		{
			error = "不是 git 仓库（找不到 " + Path.Combine(dir, ".git") + "）：请确认“DSH 项目目录”指向源码 checkout。";
			return null;
		}
		if (Toolchain.ResolveGitExe() == null)
		{
			error = "未找到 git.exe：请安装 Git for Windows，或把 git 加入 PATH（启动器也会查找 Program Files\\Git\\cmd 等常见位置）。";
			return null;
		}
		string branch = RunGit(dir, "rev-parse --abbrev-ref HEAD", 15).Output.Trim();
		if (string.IsNullOrEmpty(branch) || branch == "HEAD")
		{
			error = "无法确定当前分支（可能处于 detached HEAD）：请先 git switch 到 master/main。";
			return null;
		}
		string local = RunGit(dir, "rev-parse HEAD", 15).Output.Trim();
		if (local.Length != 40)
		{
			error = "无法读取本地 HEAD：仓库可能损坏，或 git 不可用。";
			return null;
		}

		string remote = "";
		string mirror = "origin";
		(int ExitCode, string Output) fetch = RunGit(dir, "fetch origin --prune", 180);
		string lastMirrorError = "";
		if (fetch.ExitCode != 0)
		{
			// 一些代理/网络对 HTTP/2 支持不好，会报 RPC failed / malloc failed；用 HTTP/1.1 重试直连。
			(int ExitCode, string Output) retry = RunGit(dir, "-c http.version=HTTP/1.1 fetch origin --prune", 240);
			if (retry.ExitCode == 0)
			{
				fetch = retry;
			}
		}
		if (fetch.ExitCode == 0)
		{
			remote = RunGit(dir, "rev-parse origin/" + branch, 15).Output.Trim();
			if (remote.Length != 40)
			{
				remote = RunGit(dir, "rev-parse FETCH_HEAD", 15).Output.Trim();
			}
		}
		else
		{
			string origin = RunGit(dir, "remote get-url origin", 15).Output.Trim();
			foreach (string candidate in MirrorUrls(origin))
			{
				(int ExitCode, string Output) mf = RunGit(dir,
					"-c http.version=HTTP/1.1 fetch --no-tags \"" + candidate + "\" " + branch, 120);
				if (mf.ExitCode == 0)
				{
					remote = RunGit(dir, "rev-parse FETCH_HEAD", 15).Output.Trim();
					mirror = candidate;
					break;
				}
				lastMirrorError = mf.Output;
			}
			if (remote.Length != 40)
			{
				error = ExplainGitFailure(fetch.Output + "\n" + lastMirrorError, "fetch");
				return null;
			}
		}
		if (remote.Length != 40)
		{
			error = "fetch 成功但无法解析远端 HEAD（分支 origin/" + branch + " 不存在？）。";
			return null;
		}
		string counts = RunGit(dir, "rev-list --left-right --count HEAD..." + remote, 15).Output.Trim();
		string[] parts = counts.Split('\t', StringSplitOptions.RemoveEmptyEntries);
		int ahead = parts.Length > 0 && int.TryParse(parts[0], out int a) ? a : 0;
		int behind = parts.Length > 1 && int.TryParse(parts[1], out int b) ? b : 0;
		return new DshUpdateInfo
		{
			Branch = branch,
			LocalHead = local,
			RemoteHead = remote,
			Ahead = ahead,
			Behind = behind,
			Mirror = mirror
		};
	}

	/// <summary>执行 git pull --ff-only；直连失败时用镜像 fetch 后本地快进合并（不产生 merge 提交）。</summary>
	public static (bool ok, string message) PullDsh(string repoDir, Action<string>? log = null)
	{
		try
		{
			string dir = (repoDir ?? "").Trim().Trim('"');
			if (Toolchain.ResolveGitExe() == null)
			{
				return (false, "未找到 git.exe：请安装 Git for Windows 或把 git 加入 PATH。");
			}
			(int ExitCode, string Output) pull = RunGit(dir, "pull --ff-only", 240);
			if (pull.ExitCode == 0)
			{
				string head = RunGit(dir, "rev-parse HEAD", 15).Output.Trim();
				return (head.Length == 40, head.Length == 40 ? head : "pull 成功但无法读取新 HEAD");
			}
			// 直连拉取失败：尝试镜像 fetch + merge --ff-only
			string branch = RunGit(dir, "rev-parse --abbrev-ref HEAD", 15).Output.Trim();
			string origin = RunGit(dir, "remote get-url origin", 15).Output.Trim();
			if (!string.IsNullOrEmpty(branch) && branch != "HEAD")
			{
				foreach (string candidate in MirrorUrls(origin))
				{
					(int ExitCode, string Output) mf = RunGit(dir,
						"-c http.version=HTTP/1.1 fetch --no-tags \"" + candidate + "\" " + branch, 120);
					if (mf.ExitCode != 0)
					{
						continue;
					}
					log?.Invoke("[更新] 已通过镜像获取上游: " + candidate);
					(int ExitCode, string Output) merge = RunGit(dir, "merge --ff-only FETCH_HEAD", 120);
					if (merge.ExitCode == 0)
					{
						string head = RunGit(dir, "rev-parse HEAD", 15).Output.Trim();
						return (head.Length == 40, head.Length == 40 ? head : "镜像拉取成功但无法读取新 HEAD");
					}
					return (false, "镜像已获取上游，但本地无法快进合并（可能有未提交改动/本地提交）：\n" + Tail(merge.Output, 800));
				}
			}
			return (false, ExplainGitFailure(pull.Output, "pull"));
		}
		catch (Exception ex)
		{
			return (false, ex.Message);
		}
	}

	/// <summary>从 origin URL 推导 GitHub 镜像地址；支持环境变量 LLAMADSH_GIT_MIRROR 自定义（{url} 占位）。</summary>
	private static IEnumerable<string> MirrorUrls(string? origin)
	{
		string u = (origin ?? "").Trim();
		if (u.Length == 0)
		{
			yield break;
		}
		if (u.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
		{
			u = "https://github.com/" + u.Substring("git@github.com:".Length);
		}
		else if (u.StartsWith("ssh://git@github.com/", StringComparison.OrdinalIgnoreCase))
		{
			u = "https://github.com/" + u.Substring("ssh://git@github.com/".Length);
		}
		if (!u.Contains("github.com", StringComparison.OrdinalIgnoreCase))
		{
			yield break;
		}
		int q = u.IndexOf('?');
		if (q >= 0)
		{
			u = u.Substring(0, q);
		}
		u = u.TrimEnd('/');
		if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
		{
			u = u.Substring(0, u.Length - 4);
		}
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		string? custom = Environment.GetEnvironmentVariable("LLAMADSH_GIT_MIRROR");
		if (!string.IsNullOrWhiteSpace(custom))
		{
			foreach (string item in custom.Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				string c = item.Trim();
				if (c.Length == 0)
				{
					continue;
				}
				string url = c.Contains("{url}", StringComparison.OrdinalIgnoreCase)
					? c.Replace("{url}", u, StringComparison.OrdinalIgnoreCase)
					: c.TrimEnd('/') + "/" + u;
				if (seen.Add(url))
				{
					yield return url;
				}
			}
		}
		foreach (string prefix in new[] { "https://gh-proxy.com/", "https://ghfast.top/", "https://ghproxy.net/" })
		{
			if (u.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string url = prefix + u;
			if (seen.Add(url))
			{
				yield return url;
			}
		}
	}

	private static string ExplainGitFailure(string output, string action)
	{
		string text = (output ?? "").Trim();
		string lower = text.ToLowerInvariant();
		string tail = Tail(text, 800);
		if (lower.Contains("not found") || lower.Contains("不是内部或外部命令") || lower.Contains("no such file"))
		{
			return "未找到 git 命令：请安装 Git for Windows 并重开启动器。" + (tail.Length > 0 ? "\n" + tail : "");
		}
		if (lower.Contains("could not resolve host") || lower.Contains("failed to connect")
			|| lower.Contains("connection timed out") || lower.Contains("operation timed out")
			|| lower.Contains("unable to access") || lower.Contains("schannel")
			|| lower.Contains("ssl") || lower.Contains("tls") || lower.Contains("empty reply")
			|| lower.Contains("rpc failed") || lower.Contains("connection reset")
			|| lower.Contains("network is unreachable") || lower.Contains("proxy"))
		{
			return "网络不可达或被拦截（GitHub 直连失败，已尝试 gh-proxy/ghfast/ghproxy 镜像）：\n" + tail
				+ "\n\n请检查代理/DNS，或设置环境变量 LLAMADSH_GIT_MIRROR 指定可用镜像前缀后重试。";
		}
		if (lower.Contains("authentication") || lower.Contains("permission denied")
			|| lower.Contains("could not read username") || lower.Contains("terminal prompts disabled"))
		{
			return "git 认证失败（需要 GitHub 凭据或 SSH key）：\n" + tail;
		}
		if (lower.Contains("not possible to fast-forward") || lower.Contains("would be overwritten")
			|| lower.Contains("local changes"))
		{
			return "本地有未提交改动或历史分叉，无法快进更新：请先 commit/stash 后重试。\n" + tail;
		}
		return (action == "pull" ? "git pull 失败：" : "git fetch 失败：") + "\n" + tail;
	}

	private static string Tail(string text, int max)
	{
		string t = (text ?? "").Trim();
		return t.Length <= max ? t : t.Substring(t.Length - max);
	}

	private static (int ExitCode, string Output) RunGit(string repoDir, string args, int timeoutSec)
	{
		try
		{
			string? git = Toolchain.ResolveGitExe();
			if (git == null)
			{
				return (-1, "未找到 git.exe");
			}
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName = git,
				Arguments = args,
				WorkingDirectory = repoDir,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
				StandardOutputEncoding = System.Text.Encoding.UTF8,
				StandardErrorEncoding = System.Text.Encoding.UTF8
			};
			// 禁止凭据/SSH 交互式提示，否则无人值守更新会一直挂到超时。
			psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
			psi.Environment["GCM_INTERACTIVE"] = "never";
			psi.Environment["GIT_ASKPASS"] = "echo";
			psi.Environment["SSH_ASKPASS"] = "echo";
			Toolchain.AugmentPath(psi);
			using Process? p = Process.Start(psi);
			if (p == null)
			{
				return (-1, "git 启动失败");
			}
			Task<string> stdout = p.StandardOutput.ReadToEndAsync();
			Task<string> stderr = p.StandardError.ReadToEndAsync();
			if (!p.WaitForExit(timeoutSec * 1000))
			{
				try
				{
					p.Kill(true);
				}
				catch
				{
				}
				// 超时强杀会留下 .git/index.lock, 之后所有 git 命令都会报锁存在:
				// 确认无存活 git 进程时自动清理该锁
				string lockFile = Path.Combine(repoDir, ".git", "index.lock");
				if (File.Exists(lockFile) && Process.GetProcessesByName("git").Length == 0)
				{
					try
					{
						File.Delete(lockFile);
						return (-1, "git 执行超时（" + timeoutSec + " 秒）; 已自动清理残留的 .git/index.lock, 可直接重试");
					}
					catch
					{
					}
				}
				return (-1, "git 执行超时（" + timeoutSec + " 秒）");
			}
			return (p.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
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
