using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// Agent 调试/调用 HTTP 接口(Windows 侧)。端点命名对齐 Linux 侧 main.py 的 ROUTES
/// (status/logs/llama/start/dsh/start/lightweight/logs/clear/...), 全部 JSON 出入。
/// 只绑 127.0.0.1 环回, X-Auth token 鉴权: token 首启自动生成, 与端口一起存到日志目录的
/// agent.json, 内置 `cli` 子命令自动读取, curl 也一行能用。
/// 本类不感知 UI: 路由由宿主(MainWindow)注册为 Func&lt;输入, JSON 字符串&gt; 委托,
/// 线程 marshalling 全部由宿主决定。GET 处理器收到的是 query(去 '?'), POST 是请求体。
/// </summary>
public sealed class AgentApi
{
	/// <summary>默认端口; agent.json / LLAMADSH_AGENT_PORT 可覆盖(>1024 才允许, 无需管理员的环回段)。</summary>
	public const int DefaultPort = 17325;

	/// <summary>GpuDevInfo/SlotInfo 等监控结构用的是公共字段, 序列化必须开 IncludeFields。
	/// Encoder 放松转义: 中文/引号原样输出, agent 读起来不用解 \uXXXX(JSON over HTTP 安全)。</summary>
	public static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
	{
		IncludeFields = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	public int Port { get; private set; }

	public string Token { get; private set; } = "";

	/// <summary>端口+token 的落盘位置(供 cli/curl 读取; 响应里永远不会带出 token 本身)。</summary>
	public string ConfigFile { get; } = Path.Combine(AppLog.LogDir, "agent.json");

	private readonly ConcurrentDictionary<string, Func<string, string>> _get = new ConcurrentDictionary<string, Func<string, string>>();
	private readonly ConcurrentDictionary<string, Func<string, string>> _post = new ConcurrentDictionary<string, Func<string, string>>();
	private HttpListener? _listener;

	/// <summary>请求体上限：超过直接 413，绝不"截断后继续解析"（那会静默损坏大 config）。</summary>
	private const int MaxBodyChars = 262144;

	public void RegisterGet(string path, Func<string, string> handler)
	{
		_get[path] = handler;
	}

	public void RegisterPost(string path, Func<string, string> handler)
	{
		_post[path] = handler;
	}

	/// <summary>读取/生成配置并开始监听; 失败返回 false + 原因(绝不抛穿宿主)。</summary>
	public bool Start(out string? failReason)
	{
		if (_listener != null)
		{
			failReason = null;
			return true;
		}
		int port = DefaultPort;
		string token = "";
		string diskToken = "";
		bool enabled = true;
		try
		{
			if (File.Exists(ConfigFile))
			{
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(ConfigFile));
				if (doc.RootElement.TryGetProperty("port", out JsonElement pe) && pe.TryGetInt32(out int pp) && pp > 1024 && pp < 65536)
				{
					port = pp;
				}
				if (doc.RootElement.TryGetProperty("token", out JsonElement te))
				{
					diskToken = te.GetString() ?? "";
					token = diskToken;
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
		// 环境变量覆盖: CI/多实例不用改文件
		string? envPort = Environment.GetEnvironmentVariable("LLAMADSH_AGENT_PORT");
		if (int.TryParse(envPort, out int ep) && ep > 1024 && ep < 65536)
		{
			port = ep;
		}
		string? envToken = Environment.GetEnvironmentVariable("LLAMADSH_AGENT_TOKEN");
		bool tokenFromEnv = false;
		if (!string.IsNullOrWhiteSpace(envToken))
		{
			token = envToken.Trim();
			tokenFromEnv = true;
		}
		if (Environment.GetEnvironmentVariable("LLAMADSH_AGENT_DISABLE") == "1")
		{
			enabled = false;
		}
		if (!enabled)
		{
			failReason = "已被配置禁用(agent.json 里 enabled=false 或环境变量 LLAMADSH_AGENT_DISABLE=1)";
			return false;
		}
		if (string.IsNullOrWhiteSpace(token))
		{
			token = NewToken();
		}
		HttpListener listener = new HttpListener();
		listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
		try
		{
			listener.Start();
		}
		catch (Exception ex)
		{
			try { listener.Close(); } catch { }
			failReason = "绑定 127.0.0.1:" + port + " 失败(" + ex.Message + "); 可改 agent.json 的 port 或设 LLAMADSH_AGENT_PORT";
			return false;
		}
		_listener = listener;
		Port = port;
		Token = token;
		PersistConfig(port, tokenFromEnv ? diskToken : token);
		failReason = null;
		new Thread((ThreadStart)delegate { Loop(listener); })
		{
			IsBackground = true,
			Name = "AgentApi"
		}.Start();
		return true;
	}

	public void Stop()
	{
		try
		{
			_listener?.Stop();
			_listener?.Close();
		}
		catch
		{
			// 已在关闭中：忽略
		}
		_listener = null;
		// handler 捕获着 MainWindow，清空后窗口才能随本次退出一起回收
		_get.Clear();
		_post.Clear();
	}

	private static string NewToken()
	{
		byte[] buf = new byte[16];
		RandomNumberGenerator.Fill(buf);
		return Convert.ToHexString(buf).ToLowerInvariant();
	}

	private void PersistConfig(int port, string token)
	{
		try
		{
			string json = JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["port"] = port,
				["token"] = token,
				["enabled"] = true
			});
			File.WriteAllText(ConfigFile, json);
			// cli 项目(llamadsh-cli.csproj)是无平台限定的 net8.0, ACL API 只在 Windows 存在
			if (OperatingSystem.IsWindows())
			{
				TightenAcl();
			}
			try { File.SetAttributes(ConfigFile, FileAttributes.Hidden); } catch { }
		}
		catch
		{
			// 写不进去不影响本次监听(token 只存在于进程内)
		}
	}

	/// <summary>
	/// agent.json 里的 token 等于"本机任意进程可启停服务/改写用户配置/退出启动器"，
	/// Hidden 属性不提供任何保护。这里把 DACL 收成 SYSTEM + 管理员 + 当前用户。
	/// </summary>
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private void TightenAcl()
	{
		try
		{
			string? sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
			if (string.IsNullOrEmpty(sid))
			{
				return;
			}
			FileSecurity fs = new FileSecurity();
			fs.SetSecurityDescriptorSddlForm("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;" + sid + ")", AccessControlSections.Access);
			FileInfo fi = new FileInfo(ConfigFile);
			fi.SetAccessControl(fs);
		}
		catch (Exception ex)
		{
			AppLog.WriteError("收紧 agent.json 权限失败(token 仍按默认 ACL 存放): " + ex.Message);
		}
	}

	private void Loop(HttpListener listener)
	{
		while (true)
		{
			HttpListenerContext ctx;
			try
			{
				ctx = listener.GetContext();
			}
			catch
			{
				return; // Stop()/Close() 从这里退出
			}
			// 每个请求走线程池: 慢操作(启停服务)不能挡住其它调用
			ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
		}
	}

	private void Handle(HttpListenerContext ctx)
	{
		string path = ctx.Request.Url?.AbsolutePath ?? "";
		string key = path.StartsWith("/api/", StringComparison.Ordinal) ? path.Substring(5).Trim('/') : "?" + path;
		try
		{
			// 绑定 127.0.0.1 只挡住外部机器，不挡 DNS 重绑定：浏览器把 evil.com 解析到
			// 127.0.0.1 后，请求的 Host 是 evil.com 而连接仍然进得来。核对 Host 才补上这一环。
			if (!HostIsLoopback(ctx.Request.Headers["Host"]))
			{
				Send(ctx, 400, "{\"ok\":false,\"error\":\"Host 不是环回地址(疑似 DNS 重绑定)，已拒绝\"}");
				return;
			}
			bool isGet = string.Equals(ctx.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase);
			ConcurrentDictionary<string, Func<string, string>> table = isGet ? _get : _post;
			if (!table.TryGetValue(key, out Func<string, string>? fn))
			{
				Send(ctx, 404, JsonSerializer.Serialize(new Dictionary<string, object>
				{
					["ok"] = false,
					["error"] = "未知端点: " + ctx.Request.HttpMethod + " " + path,
					["endpoints"] = EndpointList()
				}, JsonOpts));
				return;
			}
			// ping 免 token(探活/端口发现用); 其余一律 X-Auth(或 Authorization: Bearer)
			if (key != "ping")
			{
				string? auth = ctx.Request.Headers["X-Auth"];
				if (string.IsNullOrWhiteSpace(auth))
				{
					auth = ctx.Request.Headers["Authorization"];
					if (auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
					{
						auth = auth.Substring(7);
					}
				}
				if (!TokenMatches((auth ?? "").Trim(), Token))
				{
					Send(ctx, 401, "{\"ok\":false,\"error\":\"token 缺失或不匹配\",\"hint\":\"请求头 X-Auth <token>; token 见 " + JsonSerializer.Serialize(ConfigFile, JsonOpts) + "\",\"cli\":\"llamadsh-cli token\"}");
					return;
				}
			}
			string input = "";
			if (!isGet)
			{
				if (ctx.Request.ContentLength64 > MaxBodyChars)
				{
					Send(ctx, 413, "{\"ok\":false,\"error\":\"请求体超过 " + MaxBodyChars + " 字节，已拒绝\"}");
					return;
				}
				using StreamReader sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
				string raw = sr.ReadToEnd();
				if (raw.Length > MaxBodyChars)
				{
					// 以前是"截断后照样解析"：大 config 会被静默改成残缺内容
					Send(ctx, 413, "{\"ok\":false,\"error\":\"请求体超过 " + MaxBodyChars + " 字符，已拒绝(未解析)\"}");
					return;
				}
				input = raw;
			}
			else if (ctx.Request.Url != null && !string.IsNullOrEmpty(ctx.Request.Url.Query))
			{
				input = ctx.Request.Url.Query.TrimStart('?');
			}
			string result = fn(input) ?? "{\"ok\":false,\"error\":\"处理器返回空\"}";
			Send(ctx, 200, result);
		}
		catch (Exception ex)
		{
			// 只回 HTTP 不留日志会让"接口报错"变成无从查证的现场
			AppLog.WriteError("Agent 端点异常 " + ctx.Request.HttpMethod + " " + path + ": " + ex);
			try
			{
				Send(ctx, 500, JsonSerializer.Serialize(new Dictionary<string, object>
				{
					["ok"] = false,
					["error"] = "internal " + ex.GetType().Name + ": " + ex.Message
				}, JsonOpts));
			}
			catch
			{
				// 响应已经发不出去(连接被对端关闭): 无进一步动作
			}
		}
	}

	/// <summary>Host 头是否只可能是本机环回（允许 127.x、localhost、[::1]，可带端口）。</summary>
	private static bool HostIsLoopback(string? hostHeader)
	{
		string h = (hostHeader ?? "").Trim();
		if (h.Length == 0)
		{
			return false;
		}
		int colon = h.LastIndexOf(':');
		if (colon > 0 && h.IndexOf(']') < colon)
		{
			h = h.Substring(0, colon); // 去掉 :port，保留 [::1] 的方括号
		}
		h = h.Trim('[', ']');
		return h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
			|| h.Equals("::1", StringComparison.Ordinal)
			|| (h.StartsWith("127.", StringComparison.Ordinal) && h.Length <= 15);
	}

	/// <summary>token 比较走固定时间，避免按字符数差异做时序探测。</summary>
	private static bool TokenMatches(string given, string expected)
	{
		byte[] a = Encoding.UTF8.GetBytes(given);
		byte[] b = Encoding.UTF8.GetBytes(expected);
		return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
	}

	private List<string> EndpointList()
	{
		List<string> list = new List<string>();
		foreach (string k in _get.Keys)
		{
			list.Add("GET /api/" + k);
		}
		foreach (string k in _post.Keys)
		{
			list.Add("POST /api/" + k);
		}
		list.Sort(StringComparer.OrdinalIgnoreCase);
		return list;
	}

	private static void Send(HttpListenerContext ctx, int code, string body)
	{
		byte[] data = Encoding.UTF8.GetBytes(body);
		ctx.Response.StatusCode = code;
		ctx.Response.ContentType = "application/json; charset=utf-8";
		ctx.Response.ContentLength64 = data.Length;
		ctx.Response.OutputStream.Write(data, 0, data.Length);
		try { ctx.Response.Close(); } catch { }
	}
}
