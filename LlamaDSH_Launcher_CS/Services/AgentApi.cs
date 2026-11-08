using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
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
					token = te.GetString() ?? "";
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
		if (!string.IsNullOrWhiteSpace(envToken))
		{
			token = envToken.Trim();
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
		PersistConfig(port, token);
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
		}
		catch
		{
		}
		_listener = null;
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
			try { File.SetAttributes(ConfigFile, FileAttributes.Hidden); } catch { }
		}
		catch
		{
			// 写不进去不影响本次监听(token 只存在于进程内)
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
				if (!string.Equals((auth ?? "").Trim(), Token, StringComparison.Ordinal))
				{
					Send(ctx, 401, "{\"ok\":false,\"error\":\"token 缺失或不匹配\",\"hint\":\"请求头 X-Auth <token>; token 见 " + JsonSerializer.Serialize(ConfigFile, JsonOpts) + "\",\"cli\":\"llamadsh-cli token\"}");
					return;
				}
			}
			string input = "";
			if (!isGet)
			{
				using StreamReader sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
				string raw = sr.ReadToEnd();
				input = raw.Length > 262144 ? raw.Substring(0, 262144) : raw;
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
			}
		}
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
