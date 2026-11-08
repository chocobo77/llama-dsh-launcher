using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// "3080 上那个进程到底是不是 DSH"的分层判定。端口被占用本身不是身份证据（无校验接管后点
/// 「停止」会对它 KillProcessTree，用户的无关进程树直接被杀），而命令行又不是总能读到：
/// 启动器以中完整性运行、DSH 以管理员（高完整性）运行时，WMI 取不到它的 CommandLine，
/// 只按命令行匹配就会把真 DSH 判成"不像 DSH"并拒绝接管（提权启动过一次就永久接管不到）。
/// 因此按 命令行 → DSH 自己的 HTTP 指纹 两层证明，并把判定依据回报给界面：
/// "读不到"不再等于"不是"。
/// </summary>
public static class DshIdentity
{
	/// <summary>DSH Web 的固定端口（启动器各处都按它探测）。</summary>
	public const int DshPort = 3080;

	public sealed class Verdict
	{
		/// <summary>true = 可以接管/可以停止。</summary>
		public bool Ok;

		/// <summary>给用户看的一句话依据（拒绝时说明为什么，以及下一步怎么办）。</summary>
		public string Reason = "";

		/// <summary>命令行是否读得到；false 时拒绝接管多半是权限问题而不是真不是 DSH。</summary>
		public bool CommandLineReadable;
	}

	/// <summary>命令行/镜像路径里是否含 DSH 标志（不含 HTTP 兜底，可单独复用）。</summary>
	public static bool CommandLineMatches(string? commandLine, string imagePath, string? dshDir)
	{
		string text = (commandLine ?? "").Trim().ToLowerInvariant();
		string image = (imagePath ?? "").ToLowerInvariant().Replace('/', '\\');
		string key = (dshDir ?? "").ToLowerInvariant().Replace('/', '\\');
		return text.Contains("cli/src/bin") || text.Contains("apps/cli") || text.Contains("@deepseek-ai")
			|| text.Contains("deepseek-harness") || image.Contains("deepseek-harness")
			|| (text.Contains("dsh") && (text.Contains("web") || text.Contains("dist") || text.Contains("cli") || text.Contains("server") || text.Contains("start")))
			|| (key.Length > 0 && (text.Contains(key) || image.Contains(key)));
	}

	/// <summary>
	/// 纯判定：不读进程、不发请求，便于冒烟测试把整张决策表钉住。
	/// httpFingerprint: true=该端口的 HTTP 应答确认是 DSH；false=应答明确不是 DSH；null=连不上/未知。
	/// </summary>
	public static Verdict Judge(string processName, string? commandLine, string imagePath, string? dshDir, bool? httpFingerprint)
	{
		Verdict v = new Verdict();
		HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node", "node.exe", "electron", "electron.exe" };
		string name = processName ?? "";
		if (!names.Contains(name))
		{
			v.Reason = "进程名 \"" + name + "\" 不是 node/electron";
			return v;
		}
		v.CommandLineReadable = !string.IsNullOrWhiteSpace(commandLine);
		if (CommandLineMatches(commandLine, imagePath, dshDir))
		{
			v.Ok = true;
			v.Reason = "命令行含 DSH 标志";
			return v;
		}
		if (httpFingerprint == true)
		{
			v.Ok = true;
			v.Reason = v.CommandLineReadable
				? "命令行不含 DSH 标志，但 3080 应答的是 DSH 自己的 HTTP 指纹"
				: "命令行读不到（跨权限），3080 应答的是 DSH 自己的 HTTP 指纹";
			return v;
		}
		if (!v.CommandLineReadable)
		{
			v.Reason = "命令行读不到（DSH 很可能以管理员权限运行，而本启动器没有提权），且该端口的 HTTP 应答也没能确认是 DSH";
			return v;
		}
		v.Reason = "进程名是 " + name + "，但命令行与端口 HTTP 指纹都不含 DSH 标志";
		return v;
	}

	/// <summary>
	/// DSH 的 HTTP 指纹：未带 token 时 GET / 回 401 + 正文 "dsh web authentication required…"，
	/// 这句文案只有 DSH 的 Web 服务会发。true=确认是 DSH；false=有 HTTP 应答但不是 DSH；
	/// null=连不上或超时（未知，不能据此判定"不是"）。必须在非 UI 线程调用。
	/// </summary>
	public static bool? HttpFingerprint(int port = DshPort, int timeoutMs = 1200)
	{
		try
		{
			using HttpClient hc = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
			using HttpResponseMessage resp = hc.GetAsync("http://127.0.0.1:" + port + "/").GetAwaiter().GetResult();
			string head = "";
			try
			{
				head = (resp.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "").ToLowerInvariant();
			}
			catch (Exception ex)
			{
				AppLog.WriteError("读取 3080 应答正文失败(按状态码判定): " + ex.Message);
			}
			if (head.Contains("dsh web authentication required") || head.Contains("cordis") || head.Contains("dsh"))
			{
				return true;
			}
			// 应答了 HTTP 却没提 DSH：区分"确属别的 HTTP 服务"与"空正文"（404 空正文仍算应答过）
			return false;
		}
		catch (TaskCanceledException)
		{
			return null;
		}
		catch (HttpRequestException)
		{
			return null;
		}
		catch (Exception ex)
		{
			AppLog.WriteError("探测 3080 HTTP 指纹失败(按未知处理): " + ex.Message);
			return null;
		}
	}
}
