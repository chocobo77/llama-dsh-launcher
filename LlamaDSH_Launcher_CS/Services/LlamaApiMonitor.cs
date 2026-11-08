using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// llama-server 运行态采集器。2026-11-04 起为本仓库独立实现(clean-room 重写):
/// 仅依据 llama.cpp 公开文档化的 HTTP 端点(/slots /props /v1/models)与其公开
/// stdout 日志行格式(print_timing / eval time 汇总 / slot release 等)编写,
/// 不参照任何第三方监控项目的代码结构、命名或算法。
/// 线程模型: PollFast/PollSlowIfNeeded 由性能采集线程按拍调用; FeedLogLine 由
/// stdout 泵线程调用; 所有可变状态统一用 _lock 保护, UI 侧一律走 *Snapshot()。
/// </summary>
public sealed class LlamaApiMonitor : IDisposable
{
	// ============ 对外数据契约(语义 = llama.cpp 公开 API 字段) ============

	public sealed class SlotInfo
	{
		public int Id;
		public bool Processing;
		public long? TaskId;
		public long NCtx;
		public long NPromptTokens;
		public long NPromptProcessed;
		public long NPromptCache;
		public long NDecoded;
		public long NRemain;
		public long CtxUsed;
		public double? CtxPct;
		public double GenSpeed;
		public double PromptSpeed;
		public bool Speculative;
	}

	public sealed class ModelInfo
	{
		public string Path = "";
		public string Name = "";
		public string Ftype = "";
		public string VocabType = "";
		public long? NParams;
		public long? NEmbd;
		public long? NVocab;
		public long? NCtx;
		public long? NCtxTrain;
		public long? FileSize;
		public bool Vision;
		public bool Video;
		public bool Audio;
		public List<string> Capabilities = new();
	}

	public sealed class WatchEvent
	{
		public DateTime Ts;
		public string Level = "info";
		public string Msg = "";
	}

	public sealed class BootInfo
	{
		public string Model = "";
		public int? NSlots;
		public int? NCtxSlot;
		public bool? KvUnified;
		public string Listening = "";
		public List<string> Warnings = new();
	}

	public sealed class TaskSummary
	{
		public int? Slot;
		public long? TaskId;
		public double? PromptMs;
		public long? PromptTokens;
		public double? PromptSpeed;
		public double? EvalMs;
		public long? DecodedTokens;
		public double? GenSpeed;
		public double? TotalMs;
		public long? TotalTokens;
		public double Duration;
		public double? AvgTps;
		public bool Truncated;
		public DateTime EndTime;
	}

	// ============ 公开状态(UI 只读) ============

	public bool Online { get; private set; }
	/// <summary>当前实际连通的 llama-server 端口(未连通为 0)。</summary>
	public int ActivePort { get; private set; }

	// 相位与实时任务状态(日志行优先, slots 差分兜底)
	public string Phase { get; private set; } = "idle";
	public long? LogTaskId { get; private set; }
	public long LogNDecoded { get; private set; }
	public double? TgTps { get; private set; }
	public double? Tg3sTps { get; private set; }
	public double? PromptProgress { get; private set; }
	public double? LogPromptSpeed { get; private set; }
	public long? PromptTotalTokens { get; private set; }
	public double? PromptElapsedS { get; private set; }
	public bool? IsChild { get; private set; }
	public DateTime? TaskStartedAt { get; private set; }
	public long? LogCtxUsed { get; private set; }
	public bool LogCtxTruncated { get; private set; }

	// slots 差分聚合
	public long CtxUsed { get; private set; } = -1;
	public long CtxTotal { get; private set; }
	public double GenTps { get; private set; }
	public double PromptTps { get; private set; }

	// 持久记录
	public TaskSummary? LastTask { get; private set; }
	public double? LastPrefillSpeed { get; private set; }
	public long? LastPrefillTokens { get; private set; }
	public BootInfo Boot { get; } = new();
	public ModelInfo Model { get; } = new();

	// MTP 投机解码(draft acceptance 行; 无投机为 null)
	public double? MtpAcceptance { get; private set; }
	public long? MtpAccepted { get; private set; }
	public long? MtpGenerated { get; private set; }
	public double? MtpMeanLen { get; private set; }
	public long? GraphsReused { get; private set; }

	public long TotalEvents { get; private set; }

	// ============ 内部 ============

	private const int FailLimit = 3;
	private const double SlowPollEveryS = 30.0;
	private const double RescanEveryS = 10.0;
	private const int EventCapacity = 200;

	private readonly object _lock = new();
	private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
	private readonly Func<int> _portGetter;
	private readonly Dictionary<int, SlotDiff> _diffs = new();
	private readonly List<SlotInfo> _slots = new();
	private readonly LinkedList<WatchEvent> _events = new();

	private int _failStreak;

	/// <summary>释放内部 HttpClient（连接池与非托管句柄）。调用方须先停掉 perf 轮询线程。</summary>
	public void Dispose()
	{
		try
		{
			_http.Dispose();
		}
		catch (Exception ex)
		{
			AppLog.WriteError("释放 llama API HttpClient 失败: " + ex.Message);
		}
	}
	private double _nextSlowPollAt;
	private double _nextRescanAt;
	private bool _slowInFlight;

	private sealed class SlotDiff
	{
		public bool Busy;
		public long? Task;
		public long Decoded;
		public long PromptProcessed;
		public double? LastSeenAt;
		public double? StartedAt;
		public double GenSpeed;
		public double PromptSpeed;
	}

	public LlamaApiMonitor(Func<int> portGetter)
	{
		_portGetter = portGetter;
	}

	private void Emit(string level, string msg)
	{
		lock (_lock)
		{
			_events.AddLast(new WatchEvent { Ts = DateTime.Now, Level = level, Msg = msg });
			while (_events.Count > EventCapacity)
			{
				_events.RemoveFirst();
			}
			TotalEvents++;
		}
	}

	public List<SlotInfo> SlotsSnapshot()
	{
		lock (_lock)
		{
			return new List<SlotInfo>(_slots);
		}
	}

	public List<WatchEvent> EventsSnapshot()
	{
		lock (_lock)
		{
			return new List<WatchEvent>(_events);
		}
	}

	// ============ HTTP 采集 ============

	/// <summary>候选端口: 配置端口 → 已连通端口 → llama-server 常见默认。去重保序。</summary>
	private List<int> CandidatePorts(bool includeDefaults)
	{
		var ordered = new List<int>();
		void Push(int p)
		{
			if (p > 0 && p <= 65535 && !ordered.Contains(p))
			{
				ordered.Add(p);
			}
		}
		int cfg = 0;
		try
		{
			cfg = _portGetter();
		}
		catch
		{
		}
		Push(cfg);
		Push(ActivePort);
		if (includeDefaults)
		{
			Push(8080);
			Push(8081);
			Push(1234);
		}
		return ordered;
	}

	/// <summary>快轮询一拍: GET /slots, 成功后做逐槽差分。返回 false = 本拍无数据。</summary>
	public bool PollFast()
	{
		double now = Environment.TickCount64 / 1000.0;
		bool rescan = now >= _nextRescanAt;
		string body = null;
		int hitPort = 0;
		foreach (int p in CandidatePorts(rescan))
		{
			try
			{
				body = _http.GetStringAsync($"http://127.0.0.1:{p}/slots").GetAwaiter().GetResult();
				hitPort = p;
				break;
			}
			catch
			{
			}
		}
		if (body == null)
		{
			if (rescan)
			{
				_nextRescanAt = now + RescanEveryS;
			}
			_failStreak++;
			if (_failStreak >= FailLimit && Online)
			{
				GoOffline();
			}
			return false;
		}
		_failStreak = 0;
		ActivePort = hitPort;
		if (!Online)
		{
			Online = true;
			Emit("ok", $"llama-server API 已连通 (端口 {hitPort})");
		}

		try
		{
			using var doc = JsonDocument.Parse(body);
			JsonElement arr;
			JsonElement root = doc.RootElement;
			if (root.ValueKind == JsonValueKind.Array)
			{
				arr = root;
			}
			else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("slots", out var s) && s.ValueKind == JsonValueKind.Array)
			{
				arr = s;
			}
			else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.Object)
			{
				// 旧版 llama.cpp 把单槽字段直接挂在 "default" 下
				arr = JsonDocument.Parse("[" + d.GetRawText() + "]").RootElement;
			}
			else
			{
				return true;
			}
			DiffSlots(arr, now);
			return true;
		}
		catch
		{
			return true;
		}
	}

	private void GoOffline()
	{
		Online = false;
		GenTps = 0;
		PromptTps = 0;
		lock (_lock)
		{
			foreach (SlotInfo sl in _slots)
			{
				sl.GenSpeed = 0;
				sl.PromptSpeed = 0;
			}
		}
		Phase = "idle";
		TgTps = Tg3sTps = null;
		PromptProgress = LogPromptSpeed = null;
		Emit("error", "llama-server API 连续采集失败, 判定离线(端口探测无响应)");
	}

	private static long JsonLong(JsonElement e, string prop)
	{
		return e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
	}

	/// <summary>已解码 token 数: 标准字段 n_decoded; 个别自定义 build 只给 next_token[0].n_decoded。</summary>
	private static long DecodedOf(JsonElement slot)
	{
		if (slot.TryGetProperty("n_decoded", out var n))
		{
			return n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var l) ? l : 0;
		}
		if (slot.TryGetProperty("next_token", out var nt)
			&& nt.ValueKind == JsonValueKind.Array && nt.GetArrayLength() > 0
			&& nt[0].ValueKind == JsonValueKind.Object
			&& nt[0].TryGetProperty("n_decoded", out var alt)
			&& alt.ValueKind == JsonValueKind.Number && alt.TryGetInt64(out var l2))
		{
			return l2;
		}
		return 0;
	}

	private void DiffSlots(JsonElement slotsArr, double now)
	{
		var fresh = new List<SlotInfo>();
		double bestGen = 0, bestPrompt = 0;
		long usedMax = -1, ctxMax = 0;

		lock (_lock)
		{
			foreach (JsonElement el in slotsArr.EnumerateArray())
			{
				if (el.ValueKind != JsonValueKind.Object)
				{
					continue;
				}
				int sid = (int)JsonLong(el, "id");
				if (!_diffs.TryGetValue(sid, out SlotDiff st))
				{
					st = new SlotDiff();
					_diffs[sid] = st;
				}
				bool busy = el.TryGetProperty("is_processing", out var ip) && ip.ValueKind == JsonValueKind.True;
				long? task = el.TryGetProperty("id_task", out var it) && it.ValueKind == JsonValueKind.Number ? it.GetInt64() : null;
				long decoded = DecodedOf(el);
				long pfDone = JsonLong(el, "n_prompt_tokens_processed");
				long pfCache = JsonLong(el, "n_prompt_tokens_cache");
				long pf = JsonLong(el, "n_prompt_tokens");
				long remain = JsonLong(el, "n_remain");
				long nctx = JsonLong(el, "n_ctx");

				if (busy)
				{
					if (!st.Busy)
					{
						st.Busy = true;
						st.Task = task;
						st.Decoded = decoded;
						st.PromptProcessed = pfDone;
						st.StartedAt = now;
						Emit("info", $"槽 {sid} 任务 #{task} 开始 (prompt {pf} tok)");
					}
					if (task != st.Task || decoded < st.Decoded)
					{
						// 换了任务或计数回绕: 差分基线重置, 不产生速度
						st.Task = task;
						st.Decoded = decoded;
						st.PromptProcessed = pfDone;
						st.GenSpeed = 0;
						st.PromptSpeed = 0;
					}
					else if (st.LastSeenAt.HasValue && now > st.LastSeenAt.Value)
					{
						double dt = now - st.LastSeenAt.Value;
						st.GenSpeed = Math.Max(0.0, (decoded - st.Decoded) / dt);
						st.PromptSpeed = Math.Max(0.0, (pfDone - st.PromptProcessed) / dt);
						st.Decoded = decoded;
					}
					st.PromptProcessed = pfDone;
				}
				else
				{
					// 任务结束事件统一由日志 slot release 行发出(那里统计最完整);
					// 这里只把差分状态归零。
					if (st.Busy)
					{
						st.Busy = false;
						st.Task = null;
						st.StartedAt = null;
					}
					st.GenSpeed = 0;
					st.PromptSpeed = 0;
				}
				st.LastSeenAt = now;

				// 上下文占用: 标准 build n_prompt_tokens 不含已解码; 个别自定义 build 已含
				bool hasStdDecoded = el.TryGetProperty("n_decoded", out var _);
				long used = hasStdDecoded ? pf + decoded : pf;

				var info = new SlotInfo
				{
					Id = sid,
					Processing = busy,
					TaskId = task,
					NCtx = nctx,
					NPromptTokens = pf,
					NPromptProcessed = pfDone,
					NPromptCache = pfCache,
					NDecoded = decoded,
					NRemain = remain,
					CtxUsed = used,
					CtxPct = nctx > 0 ? Math.Round(used * 100.0 / nctx, 2) : null,
					GenSpeed = st.GenSpeed,
					PromptSpeed = st.PromptSpeed,
					Speculative = el.TryGetProperty("params", out var prm) && prm.ValueKind == JsonValueKind.Object
						&& prm.TryGetProperty("speculative", out var spc) && spc.ValueKind == JsonValueKind.Object
						&& spc.TryGetProperty("types", out var tys) && tys.ValueKind == JsonValueKind.Array && tys.GetArrayLength() > 0,
				};
				fresh.Add(info);
				usedMax = Math.Max(usedMax, used);
				ctxMax = Math.Max(ctxMax, nctx);
				if (busy)
				{
					bestGen = Math.Max(bestGen, st.GenSpeed);
					bestPrompt = Math.Max(bestPrompt, st.PromptSpeed);
				}
			}
			_slots.Clear();
			_slots.AddRange(fresh);
			GenTps = Math.Round(bestGen, 2);
			PromptTps = Math.Round(bestPrompt, 2);
			if (usedMax >= 0)
			{
				CtxUsed = usedMax;
			}
			CtxTotal = ctxMax;
		}
	}

	/// <summary>/props + /v1/models, 30s 节流; 在后台线程执行避免拉长快轮询拍间隔。</summary>
	public void PollSlowIfNeeded()
	{
		double now = Environment.TickCount64 / 1000.0;
		if (now < _nextSlowPollAt || _slowInFlight)
		{
			return;
		}
		_nextSlowPollAt = now + SlowPollEveryS;
		_slowInFlight = true;
		Task.Run(() =>
		{
			try
			{
				FetchPropsAndModels();
			}
			catch
			{
			}
			finally
			{
				_slowInFlight = false;
			}
		});
	}

	private void FetchPropsAndModels()
	{
		List<int> ports = CandidatePorts(false);
		if (ports.Count == 0)
		{
			return;
		}
		int port = ports[0];
		string propsJson = null;
		foreach (int p in ports)
		{
			try
			{
				propsJson = _http.GetStringAsync($"http://127.0.0.1:{p}/props").GetAwaiter().GetResult();
				port = p;
				break;
			}
			catch
			{
			}
		}
		if (propsJson != null)
		{
			try
			{
				using var doc = JsonDocument.Parse(propsJson);
				JsonElement p = doc.RootElement;
				lock (_lock)
				{
					string before = Model.Path;
					if (p.TryGetProperty("model_path", out var mp) && mp.ValueKind == JsonValueKind.String)
					{
						Model.Path = mp.GetString() ?? "";
					}
					string alias = p.TryGetProperty("model_alias", out var ma) && ma.ValueKind == JsonValueKind.String ? ma.GetString() ?? "" : "";
					if (!string.IsNullOrEmpty(alias))
					{
						Model.Name = alias;
					}
					else if (!string.IsNullOrEmpty(Model.Path))
					{
						string leaf = Model.Path.Replace('\\', '/');
						Model.Name = leaf[(leaf.LastIndexOf('/') + 1)..];
					}
					Num(p, "n_embd", v => Model.NEmbd = v);
					Num(p, "n_vocab", v => Model.NVocab = v);
					Num(p, "n_ctx", v => Model.NCtx = v);
					Num(p, "n_ctx_train", v => Model.NCtxTrain = v);
					if (p.TryGetProperty("vocab_type", out var vt) && vt.ValueKind == JsonValueKind.String)
					{
						Model.VocabType = vt.GetString() ?? "";
					}
					if (p.TryGetProperty("modalities", out var mods) && mods.ValueKind == JsonValueKind.Array)
					{
						foreach (JsonElement mi in mods.EnumerateArray())
						{
							switch (mi.GetString())
							{
								case "image":
									Model.Vision = true;
									break;
								case "video":
									Model.Video = true;
									break;
								case "audio":
									Model.Audio = true;
									break;
							}
						}
					}
					if (p.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
					{
						Model.Capabilities.Clear();
						foreach (JsonElement c in caps.EnumerateArray())
						{
							if (c.ValueKind == JsonValueKind.String)
							{
								Model.Capabilities.Add(c.GetString() ?? "");
							}
						}
					}
					if (!string.IsNullOrEmpty(before) && !string.IsNullOrEmpty(Model.Path) && before != Model.Path)
					{
						Emit("warn", "服务加载的模型路径已变化 → " + Model.Path);
					}
				}
			}
			catch
			{
			}
		}
		try
		{
			string mj = _http.GetStringAsync($"http://127.0.0.1:{port}/v1/models").GetAwaiter().GetResult();
			using var doc = JsonDocument.Parse(mj);
			if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
			{
				JsonElement m0 = data[0];
				lock (_lock)
				{
					Num(m0, "n_params", v => Model.NParams = v);
					Num(m0, "size", v => Model.FileSize = v);
					if (m0.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
					{
						Num(meta, "n_params", v => Model.NParams ??= v);
						Num(meta, "size", v => Model.FileSize ??= v);
					}
				}
			}
		}
		catch
		{
		}
	}

	private static void Num(JsonElement e, string prop, Action<long> apply)
	{
		if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
			&& v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l))
		{
			apply(l);
		}
	}

	// ============ stdout 日志状态机(llama.cpp 公开日志格式) ============

	private static readonly Regex ReTimingDecode = new Regex(
		@"print_timing:\s*id\s+\d+\s*\|\s*task\s+(?<task>\d+)\s*\|\s*n_decoded\s*=\s*(?<nd>\d+)\s*,\s*tg\s*=\s*(?<tg>[\d.]+)\s*t/s\s*,\s*tg_3s\s*=\s*(?<tg3>[\d.]+)",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex ReTimingPrompt = new Regex(
		@"print_timing:\s*id\s+\d+\s*\|\s*task\s+(?<task>\d+)\s*\|\s*prompt processing\s*,\s*n_tokens\s*=\s*(?<n>\d+)\s*,\s*progress\s*=\s*(?<prog>[\d.]+)\s*,\s*t\s*=\s*(?<t>[\d.]+)\s*s\s*/\s*(?<spd>[\d.]+)\s*tokens per second",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex ReLaunch = new Regex(
		@"launch_slot\w*:\s*id\s+\d+\s*\|\s*task\s+(?<task>\d+)\s*\|\s*processing task\s*,\s*is_child\s*=\s*(?<child>\d)",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex ReRelease = new Regex(
		@"slot release:\s*id\s+(?<slot>\d+)\s*\|\s*task\s+(?<task>\d+)\s*\|\s*stop processing\s*:\s*n_tokens\s*=\s*(?<n>\d+)\s*,\s*truncated\s*=\s*(?<tr>\d)",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex RePromptEval = new Regex(
		@"prompt eval time\s*=\s*(?<ms>[\d.]+)\s*ms\s*/\s*(?<n>\d+) tokens\s*\(\s*[\d.]+\s*ms per token\s*,\s*(?<spd>[\d.]+)\s*tokens per second\s*\)",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex ReGenEval = new Regex(
		@"(?<!prompt )eval time\s*=\s*(?<ms>[\d.]+)\s*ms\s*/\s*(?<n>\d+) tokens\s*\(\s*[\d.]+\s*ms per token\s*,\s*(?<spd>[\d.]+)\s*tokens per second\s*\)",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex ReTotal = new Regex(
		@"total time\s*=\s*(?<ms>[\d.]+)\s*ms\s*/\s*(?<n>\d+) tokens",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex ReDraft = new Regex(
		@"draft acceptance\s*=\s*(?<acc>[\d.]+)\s*\(\s*(?<ok>\d+) accepted\s*/\s*(?<gen>\d+) generated\s*\)\s*,\s*mean len\s*=\s*(?<len>[\d.]+)",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex ReGraphs = new Regex(@"graphs reused\s*=\s*(?<n>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex ReBootModel = new Regex(@"loading model\s+'(?<path>[^']+)'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex ReBootSlots = new Regex(
		@"n_slots\s*=\s*(?<slots>\d+)\s*,\s*n_ctx_slot\s*=\s*(?<ctx>\d+)\s*,\s*kv_unified\s*=\s*'(?<kv>\w+)'",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex ReListen = new Regex(@"listening on\s+(?<addr>\S+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	// 任务汇总暂存(按日志到达顺序累积, release 行收尾)
	private long? _pfTaskSeen;
	private double _sumPromptMs, _sumEvalMs, _sumTotalMs, _sumPromptSpd, _sumGenSpd;
	private long _sumPromptN, _sumDecodedN, _sumTotalN;
	private int? _relSlot;
	private long? _relTask;
	private long _relTokens;
	private bool _relTrunc;

	/// <summary>stdout 逐行喂入(泵线程调用)。</summary>
	public void FeedLogLine(string line)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return;
		}
		try
		{
			Consume(line);
		}
		catch
		{
		}
	}

	/// <summary>llama 进程重启后清空状态机, 防止旧任务残影进入 UI。</summary>
	public void ResetLogMachine()
	{
		MtpAcceptance = MtpAccepted = MtpGenerated = null;
		MtpMeanLen = null;
		Phase = "idle";
		LogTaskId = null;
		LogNDecoded = 0;
		TgTps = Tg3sTps = null;
		PromptProgress = LogPromptSpeed = null;
		PromptTotalTokens = null;
		PromptElapsedS = null;
		IsChild = null;
		TaskStartedAt = null;
		LogCtxUsed = null;
		LogCtxTruncated = false;
		_pfTaskSeen = null;
		_sumPromptMs = _sumEvalMs = _sumTotalMs = _sumPromptSpd = _sumGenSpd = 0;
		_sumPromptN = _sumDecodedN = _sumTotalN = 0;
		Online = false;
		_failStreak = 0;
		lock (_lock)
		{
			_diffs.Clear();
			_slots.Clear();
		}
		Boot.Warnings.Clear();
	}

	private static double Df(string s)
	{
		return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
	}

	private static long Dl(string s)
	{
		return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
	}

	// 按 llama.cpp 日志的固定前缀分派; 同一行只可能命中一类格式。
	private void Consume(string line)
	{
		if (line.Contains("print_timing", StringComparison.Ordinal))
		{
			Match mt = ReTimingDecode.Match(line);
			if (mt.Success)
			{
				Phase = "decoding";
				LogTaskId = Dl(mt.Groups["task"].Value);
				LogNDecoded = Dl(mt.Groups["nd"].Value);
				TgTps = Df(mt.Groups["tg"].Value);
				Tg3sTps = Df(mt.Groups["tg3"].Value);
				return;
			}
			mt = ReTimingPrompt.Match(line);
			if (mt.Success)
			{
				Phase = "prompt_processing";
				LogTaskId = Dl(mt.Groups["task"].Value);
				PromptTotalTokens = Dl(mt.Groups["n"].Value);
				PromptProgress = Df(mt.Groups["prog"].Value);
				PromptElapsedS = Df(mt.Groups["t"].Value);
				LogPromptSpeed = Df(mt.Groups["spd"].Value);
				LastPrefillSpeed = LogPromptSpeed;
				LastPrefillTokens = PromptTotalTokens;
				if (_pfTaskSeen != LogTaskId)
				{
					_pfTaskSeen = LogTaskId;
					Emit("info", $"任务 #{LogTaskId} 预填充启动 ({PromptTotalTokens} tok)");
				}
				return;
			}
			// 汇总行(prompt eval time / eval time / total time / draft acceptance /
			// graphs reused)同样以 "slot print_timing:" 打头 —— 两条逐拍正则都不中时
			// 必须落到下面的分派, 直接 return 会把任务落账/MTP/graphs 整段吞掉
		}
		if (line.Contains("launch_slot", StringComparison.Ordinal))
		{
			Match mt = ReLaunch.Match(line);
			if (mt.Success)
			{
				LogTaskId = Dl(mt.Groups["task"].Value);
				IsChild = Dl(mt.Groups["child"].Value) != 0;
				TaskStartedAt = DateTime.Now;
				_sumPromptMs = _sumEvalMs = _sumTotalMs = _sumPromptSpd = _sumGenSpd = 0;
				_sumPromptN = _sumDecodedN = _sumTotalN = 0;
				Emit("info", $"任务 #{LogTaskId} 开始" + (IsChild == true ? " (子任务)" : ""));
			}
			return;
		}
		if (line.Contains("slot release", StringComparison.Ordinal))
		{
			Match mt = ReRelease.Match(line);
			if (mt.Success)
			{
				_relSlot = (int)Dl(mt.Groups["slot"].Value);
				_relTask = Dl(mt.Groups["task"].Value);
				_relTokens = Dl(mt.Groups["n"].Value);
				_relTrunc = mt.Groups["tr"].Value == "1";
				CloseTask();
			}
			return;
		}
		if (line.Contains("eval time", StringComparison.Ordinal))
		{
			if (line.Contains("prompt eval time", StringComparison.Ordinal))
			{
				Match mt = RePromptEval.Match(line);
				if (mt.Success)
				{
					_sumPromptMs = Df(mt.Groups["ms"].Value);
					_sumPromptN = Dl(mt.Groups["n"].Value);
					_sumPromptSpd = Df(mt.Groups["spd"].Value);
				}
			}
			else
			{
				Match mt = ReGenEval.Match(line);
				if (mt.Success)
				{
					_sumEvalMs = Df(mt.Groups["ms"].Value);
					_sumDecodedN = Dl(mt.Groups["n"].Value);
					_sumGenSpd = Df(mt.Groups["spd"].Value);
				}
			}
			return;
		}
		if (line.Contains("total time", StringComparison.Ordinal))
		{
			Match mt = ReTotal.Match(line);
			if (mt.Success)
			{
				_sumTotalMs = Df(mt.Groups["ms"].Value);
				_sumTotalN = Dl(mt.Groups["n"].Value);
			}
			return;
		}
		if (line.Contains("draft acceptance", StringComparison.Ordinal))
		{
			Match mt = ReDraft.Match(line);
			if (mt.Success)
			{
				MtpAcceptance = Df(mt.Groups["acc"].Value);
				MtpAccepted = Dl(mt.Groups["ok"].Value);
				MtpGenerated = Dl(mt.Groups["gen"].Value);
				MtpMeanLen = Df(mt.Groups["len"].Value);
			}
			return;
		}
		if (line.Contains("graphs reused", StringComparison.Ordinal))
		{
			Match mt = ReGraphs.Match(line);
			if (mt.Success)
			{
				GraphsReused = Dl(mt.Groups["n"].Value);
			}
			return;
		}
		if (line.Contains("loading model", StringComparison.Ordinal))
		{
			Match mt = ReBootModel.Match(line);
			if (mt.Success)
			{
				Boot.Model = mt.Groups["path"].Value;
			}
			return;
		}
		if (line.Contains("n_slots", StringComparison.Ordinal))
		{
			Match mt = ReBootSlots.Match(line);
			if (mt.Success)
			{
				Boot.NSlots = (int)Dl(mt.Groups["slots"].Value);
				Boot.NCtxSlot = (int)Dl(mt.Groups["ctx"].Value);
				Boot.KvUnified = mt.Groups["kv"].Value == "true";
			}
			return;
		}
		if (line.Contains("listening on", StringComparison.Ordinal))
		{
			Match mt = ReListen.Match(line);
			if (mt.Success)
			{
				Boot.Listening = mt.Groups["addr"].Value;
				Emit("ok", "llama-server 已监听 " + Boot.Listening);
			}
		}
	}

	private void CloseTask()
	{
		LogCtxUsed = _relTokens;
		LogCtxTruncated = _relTrunc;
		// 没收到 eval/total 汇总行 = 任务被停止或客户端断连(非正常收尾)
		bool abrupt = _sumTotalMs <= 0 && _sumEvalMs <= 0;
		double wall = TaskStartedAt.HasValue ? (DateTime.Now - TaskStartedAt.Value).TotalSeconds : 0.0;
		LastTask = new TaskSummary
		{
			Slot = _relSlot,
			TaskId = _relTask,
			PromptMs = _sumPromptMs > 0 ? _sumPromptMs : null,
			PromptTokens = _sumPromptN > 0 ? _sumPromptN : null,
			PromptSpeed = _sumPromptSpd > 0 ? _sumPromptSpd : null,
			EvalMs = _sumEvalMs > 0 ? _sumEvalMs : null,
			DecodedTokens = _sumDecodedN > 0 ? _sumDecodedN : null,
			GenSpeed = _sumGenSpd > 0 ? _sumGenSpd : null,
			TotalMs = _sumTotalMs > 0 ? _sumTotalMs : null,
			TotalTokens = _sumTotalN > 0 ? _sumTotalN : null,
			Duration = wall,
			AvgTps = wall > 0 && _sumDecodedN > 0 ? _sumDecodedN / wall : null,
			Truncated = _relTrunc,
			EndTime = DateTime.Now,
		};
		Phase = "idle";
		LogNDecoded = 0;
		Tg3sTps = TgTps = null;
		PromptProgress = LogPromptSpeed = null;
		PromptTotalTokens = null;
		PromptElapsedS = null;
		TaskStartedAt = null;
		_pfTaskSeen = null;
		_sumPromptMs = _sumEvalMs = _sumTotalMs = _sumPromptSpd = _sumGenSpd = 0;
		_sumPromptN = _sumDecodedN = _sumTotalN = 0;

		long outTok = LastTask.TotalTokens ?? LastTask.DecodedTokens ?? 0;
		Emit(abrupt ? "warn" : "ok",
			$"任务 #{_relTask} 结束 · {outTok} tok"
			+ (LastTask.Duration > 0 ? $" · 历时 {LastTask.Duration:F1}s" : "")
			+ (LastTask.AvgTps.HasValue ? $" · 平均 {LastTask.AvgTps:F1} t/s" : "")
			+ (abrupt ? " · 未正常收尾" : "")
			+ (MtpAcceptance is double m ? $" · 草稿接受 {m * 100.0:F1}%" : ""));
	}

	/// <summary>UI 展示用速度: 日志行(服务端自报)优先, /slots 差分兜底。</summary>
	public (double gen, double prompt, string source) Speeds()
	{
		double gen = GenTps;
		double prompt = PromptTps;
		string source = "api";
		if (Phase == "decoding" && Tg3sTps.HasValue)
		{
			gen = Tg3sTps.Value;
			source = "log";
		}
		if (Phase == "prompt_processing" && LogPromptSpeed.HasValue)
		{
			prompt = LogPromptSpeed.Value;
			source = "log";
		}
		return (gen, prompt, source);
	}
}
