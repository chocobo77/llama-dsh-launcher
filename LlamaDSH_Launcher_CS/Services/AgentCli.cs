using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks; // cli 项目(llamadsh-cli.csproj)未开 ImplicitUsings, 这里必须显式引

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// 内置 Agent 命令行客户端: `LlamaDSH_Launcher.exe cli &lt;命令&gt; ...` 一次性调用正在运行的
/// 启动器的 Agent HTTP 接口(见 AgentApi), 原样输出 JSON。退出码: 0=成功, 1=业务失败, 2=不可达/未启用。
/// 设计给 agent/脚本用: 无交互、无本地化修饰、stdout 永远是一行可解析的 JSON。
/// </summary>
public static class AgentCli
{
	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool AttachConsole(int dwProcessId);

	private const int ATTACH_PARENT_PROCESS = -1;

	public static int Run(string[] args)
	{
		// GUI 子系统进程默认没有 stdout: 挂回调用方的控制台, 再重接输出流
		if (AttachConsole(ATTACH_PARENT_PROCESS))
		{
			try
			{
				Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
			}
			catch
			{
			}
		}
		string cmd = (args.Length > 1 ? args[1] : "help").Trim().ToLowerInvariant();
		if (cmd is "help" or "-h" or "--help")
		{
			PrintHelp();
			return 0;
		}
		string cfgPath = Path.Combine(AppLog.LogDir, "agent.json");
		int port = AgentApi.DefaultPort;
		string token = "";
		bool enabled = true;
		bool haveCfg = false;
		try
		{
			if (File.Exists(cfgPath))
			{
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
				if (doc.RootElement.TryGetProperty("port", out JsonElement pe) && pe.TryGetInt32(out int pp))
				{
					port = pp;
					haveCfg = true;
				}
				if (doc.RootElement.TryGetProperty("token", out JsonElement te))
				{
					token = te.GetString() ?? "";
					haveCfg = true;
				}
				if (doc.RootElement.TryGetProperty("enabled", out JsonElement ee) && ee.ValueKind == JsonValueKind.False)
				{
					enabled = false;
				}
			}
		}
		catch
		{
		}
		string envTok = Environment.GetEnvironmentVariable("LLAMADSH_AGENT_TOKEN") ?? "";
		if (!string.IsNullOrWhiteSpace(envTok))
		{
			token = envTok.Trim();
		}
		string envPort = Environment.GetEnvironmentVariable("LLAMADSH_AGENT_PORT") ?? "";
		if (int.TryParse(envPort, out int ep))
		{
			port = ep;
		}
		if (cmd == "url")
		{
			Console.WriteLine("{\"ok\":" + (haveCfg ? "true" : "false") + ",\"base\":\"http://127.0.0.1:" + port + "/api\",\"config\":" + JsonSerializer.Serialize(cfgPath, AgentApi.JsonOpts) + "}");
			return haveCfg ? 0 : 2;
		}
		if (cmd == "token")
		{
			bool ok = haveCfg && !string.IsNullOrEmpty(token) && enabled;
			Console.WriteLine("{\"ok\":" + (ok ? "true" : "false") + ",\"port\":" + port + ",\"token\":" + JsonSerializer.Serialize(token, AgentApi.JsonOpts) + ",\"config\":" + JsonSerializer.Serialize(cfgPath, AgentApi.JsonOpts) + "}");
			return ok ? 0 : 2;
		}
		if (!enabled)
		{
			Console.WriteLine("{\"ok\":false,\"error\":\"Agent 接口已被禁用(agent.json enabled=false 或 LLAMADSH_AGENT_DISABLE=1)\"}");
			return 2;
		}
		if (!TryParse(cmd, args, out string method, out string path, out string body, out string parseErr))
		{
			Console.WriteLine("{\"ok\":false,\"error\":" + JsonSerializer.Serialize(parseErr, AgentApi.JsonOpts) + "}");
			return 1;
		}
		return Call(method, "http://127.0.0.1:" + port + "/api/" + path, body, token);
	}

	/// <summary>命令 → HTTP 请求映射(独立出来便于冒烟测试)。</summary>
	public static bool TryParse(string[] args, out string method, out string path, out string body, out string error)
	{
		return TryParse(args.Length > 1 ? args[1].Trim().ToLowerInvariant() : "help", args, out method, out path, out body, out error);
	}

	public static bool TryParse(string cmd, string[] args, out string method, out string path, out string body, out string error)
	{
		method = "GET";
		path = "status";
		body = "";
		string[] rest = args.Length > 2 ? args.Skip(2).ToArray() : Array.Empty<string>();
		error = "";
		switch (cmd)
		{
			case "status":
				return true;
			case "monitor":
				path = "monitor";
				return true;
			case "ping":
				path = "ping";
				return true;
			case "logs":
			{
				// cli logs [行数] [level=error|warn|info] [grep=文本] [after=序号]
				// after= 是增量游标: 响应里的 boot/gap 用来发现"启动器重启过, 游标已失效"
				int n = 200;
				string q = "";
				foreach (string tok in rest)
				{
					int eq = tok.IndexOf('=');
					if (eq <= 0)
					{
						if (!int.TryParse(tok, out n) || n < 1 || n > 2000)
						{
							error = "logs 行数需为 1~2000";
							return false;
						}
						continue;
					}
					string key = tok.Substring(0, eq).ToLowerInvariant();
					string val = tok.Substring(eq + 1);
					if (key == "level" || key == "grep")
					{
						q += "&" + key + "=" + Uri.EscapeDataString(val);
					}
					else if (key == "after")
					{
						if (!long.TryParse(val, out long af) || af < 0)
						{
							error = "logs after 需为非负整数";
							return false;
						}
						q += "&after=" + af;
					}
					else
					{
						error = "logs 不支持参数 " + key + "（可用: 行数 / level= / grep= / after=）";
						return false;
					}
				}
				path = "logs?n=" + n + q;
				return true;
			}
			case "profiles":
				path = "profiles";
				return true;
			case "profile":
			{
				if (rest.Length < 2 || rest[0].ToLowerInvariant() != "use" || string.IsNullOrWhiteSpace(rest[1]))
				{
					error = "用法: cli profile use <方案名>";
					return false;
				}
				method = "POST";
				path = "profiles/use";
				body = JsonSerializer.Serialize(new Dictionary<string, object> { ["name"] = rest[1] });
				return true;
			}
			case "breaker":
			{
				if (rest.Length == 0)
				{
					path = "breaker";
					return true;
				}
				string sub = rest[0].ToLowerInvariant();
				method = "POST";
				path = "breaker";
				if (sub is "on" or "off")
				{
					body = JsonSerializer.Serialize(new Dictionary<string, object> { ["enabled"] = sub == "on" });
					return true;
				}
				if (sub == "set")
				{
					if (rest.Length < 3 || (rest[1].ToLowerInvariant() is not ("pct" or "percent" or "gb")))
					{
						error = "用法: cli breaker set pct|gb <数值>";
						return false;
					}
					if (!double.TryParse(rest[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double val) || val <= 0.0)
					{
						error = "阈值需为正数";
						return false;
					}
					body = JsonSerializer.Serialize(new Dictionary<string, object>
					{
						["type"] = rest[1].ToLowerInvariant() == "gb" ? "gb" : "pct",
						["value"] = val
					});
					return true;
				}
				error = "breaker 子命令: (空)=查询 | on | off | set pct|gb <数值>";
				return false;
			}
			case "llama":
			case "dsh":
			{
				string sub = rest.Length > 0 ? rest[0].ToLowerInvariant() : "";
				if (sub is not ("start" or "stop"))
				{
					error = "用法: cli " + cmd + " start|stop";
					return false;
				}
				method = "POST";
				path = cmd + "/" + sub;
				return true;
			}
			case "light":
			{
				string sub = rest.Length > 0 ? rest[0].ToLowerInvariant() : "toggle";
				method = "POST";
				path = "lightweight";
				if (sub == "toggle")
				{
					body = "{\"toggle\":true}";
				}
				else if (sub is "on" or "off")
				{
					body = "{\"on\":" + (sub == "on" ? "true" : "false") + "}";
				}
				else
				{
					error = "用法: cli light on|off|toggle";
					return false;
				}
				return true;
			}
			case "window":
			{
				string sub = rest.Length > 0 ? rest[0].ToLowerInvariant() : "show";
				if (sub is not ("show" or "hide" or "minimize"))
				{
					error = "用法: cli window show|hide|minimize";
					return false;
				}
				method = "POST";
				path = "window";
				body = "{\"action\":\"" + sub + "\"}";
				return true;
			}
			case "raw":
			{
				string sub = rest.Length > 0 ? rest[0].ToLowerInvariant() : "slots";
				if (sub is not ("slots" or "props" or "health"))
				{
					error = "raw 仅支持 slots|props|health";
					return false;
				}
				path = "llama/raw?path=" + sub;
				return true;
			}
			case "save":
				method = "POST";
				path = "config/save";
				return true;
			case "quit":
			{
				method = "POST";
				path = "quit";
				bool stop = true;
				foreach (string a in rest)
				{
					if (a.Equals("--no-stop", StringComparison.OrdinalIgnoreCase))
					{
						stop = false;
					}
				}
				body = "{\"stop_services\":" + (stop ? "true" : "false") + "}";
				return true;
			}
			case "api":
			{
				// 逃生舱: cli api GET /api/any  /  cli api POST /api/x {"json"}
				if (rest.Length < 2)
				{
					error = "用法: cli api GET|POST /api/<path> [json]";
					return false;
				}
				method = rest[0].ToUpperInvariant();
				if (method is not ("GET" or "POST"))
				{
					error = "方法只支持 GET|POST";
					return false;
				}
				path = rest[1].TrimStart('/');
				if (path.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
				{
					path = path.Substring(4);
				}
				if (rest.Length > 2)
				{
					body = rest[2];
				}
				return true;
			}
			default:
				error = "未知命令 \"" + cmd + "\"; 见 cli help";
				return false;
		}
	}

	private static int Call(string method, string url, string body, string token)
	{
		// 服务端 AgentOp 对 UI 操作只等 30s，但更新/重建/pnpm 这类操作在后台还会继续跑；
		// 客户端 120s 固定超时等于"前端比服务端先放弃"（同一族问题已在 webui 修过一次）。
		int timeoutSec = 600;
		if (int.TryParse(Environment.GetEnvironmentVariable("LLAMADSH_AGENT_TIMEOUT"), out int ts) && ts > 0)
		{
			timeoutSec = Math.Clamp(ts, 5, 3600);
		}
		try
		{
			using HttpClient hc = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };
			if (!string.IsNullOrEmpty(token))
			{
				hc.DefaultRequestHeaders.TryAddWithoutValidation("X-Auth", token);
			}
			using HttpResponseMessage resp = method == "GET"
				? hc.GetAsync(url).GetAwaiter().GetResult()
				: hc.PostAsync(url, new StringContent(string.IsNullOrEmpty(body) ? "{}" : body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
			string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
			Console.WriteLine(text);
			bool ok = false;
			try
			{
				using JsonDocument d = JsonDocument.Parse(text);
				ok = d.RootElement.TryGetProperty("ok", out JsonElement o) && o.ValueKind == JsonValueKind.True;
			}
			catch
			{
				// 响应不是 JSON: 按业务失败处理, 输出已原样打印
			}
			// 4xx/5xx 是"接口答了但失败"，与"连不上"必须分开：2 只留给不可达
			if (!resp.IsSuccessStatusCode)
			{
				return 1;
			}
			return ok ? 0 : 1;
		}
		catch (TaskCanceledException)
		{
			// 超时 ≠ 失败：服务端很可能仍在执行，直接重试会把同一变更做两遍
			Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["ok"] = false,
				["timeout"] = true,
				["error"] = "等待超过 " + timeoutSec + "s；服务端可能仍在执行，勿直接重试，先用 status/logs 确认",
				["url"] = url,
				["hint"] = "可设 LLAMADSH_AGENT_TIMEOUT=<秒> 放宽等待"
			}, AgentApi.JsonOpts));
			return 3;
		}
		catch (HttpRequestException ex)
		{
			Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["ok"] = false,
				["error"] = "不可达: " + ex.Message,
				["url"] = url,
				["hint"] = "启动器是否在跑? 见 " + Path.Combine(AppLog.LogDir, "agent.json")
			}, AgentApi.JsonOpts));
			return 2;
		}
		catch (Exception ex)
		{
			Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["ok"] = false,
				["error"] = ex.Message
			}, AgentApi.JsonOpts));
			return 2;
		}
	}

	private static void PrintHelp()
	{
		Console.WriteLine(@"llamadsh-cli <命令>  (或 LlamaDSH_Launcher.exe cli <命令>) —— Agent 调用正在运行的启动器, stdout 输出 JSON
退出码: 0=成功  1=业务失败/HTTP 4xx-5xx  2=接口不可达/未启用  3=超时(结果未知, 勿直接重试)

  status                 全量状态快照(llama/dsh/GPU/熔断/轻量档/方案/路径)
  monitor                监控页同款数据(采样字典+GPU+slots+速度历史)
  logs [行数] [level=error|warn|info] [grep=文本] [after=序号]   运行日志(游标增量; 响应含 boot/ring/gap)
  profiles               方案列表与全部配置
  profile use <名>       切换方案
  breaker                查询显存熔断; on|off 开关; set pct|gb <值> 设阈值
  llama start|stop       启停 llama-server(带端口占用/模型路径预检)
  dsh start|stop         启停 DSH
  light on|off|toggle    托盘轻量档(后台低频保活)
  window show|hide|minimize
  raw slots|props|health 直连 llama API 透传
  save                   把当前界面配置写回配置文件
  quit [--no-stop]       退出启动器(默认先停服务)
  token | url            本地读取接口端口/token(供 curl 用)
  api GET|POST /api/x [json]  任意端点逃生舱

curl 示例: curl -H ""X-Auth: <cli token 输出的 token>"" http://127.0.0.1:17325/api/status");
	}
}
