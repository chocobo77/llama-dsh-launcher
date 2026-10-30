using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;

using System.Text.Json;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// llama-server HTTP 采集器(llama-lens LlamaPoller 移植):
/// - /slots 快轮询: 每槽差分算 实时生成/预填充 tps、上下文占用(prompt+decoded)、任务开始/结束事件
/// - /props + /v1/models 慢轮询(30s): 模型路径/名称/量化/参数量/维度/词表/上下文/模态/能力
/// 差分算法与字段兼容(llama-lens backend/pollers/llama_api.py), 含自定义 build 的
/// next_token[0].n_decoded 回退与 n_prompt_tokens 已含 decoded 的口径处理。
/// </summary>
public sealed partial class LlamaApiMonitor
{
	private sealed class SlotState
	{
		public long? PrevTaskId;
		public long PrevDecoded;
		public long PrevPromptProcessed;
		public double? PrevTs;
		public double? TaskStartTs;
		public double GenSpeed;
		public double PromptSpeed;
	}

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

	public sealed class LlamaEvent
	{
		public DateTime Ts;
		public string Level = "info";
		public string Msg = "";
	}

	private readonly object _lock = new();
	private readonly HttpClient _http;
	private readonly Dictionary<int, SlotState> _slotStates = new();
	private int _failCount;
	private double _lastSlowPoll;
	private int _llamaPortCache = -1;

	public bool Online { get; private set; }
	public List<SlotInfo> Slots { get; } = new();
	public ModelInfo Model { get; } = new();
	public long CtxUsed { get; private set; } = -1;
	public long CtxTotal { get; private set; }
	public double GenTps { get; private set; }
	public double PromptTps { get; private set; }
	public string SpeedSource { get; private set; } = "";
	public List<LlamaEvent> Events { get; } = new();

	private readonly Func<int> _portGetter;

	public LlamaApiMonitor(Func<int> portGetter)
	{
		_portGetter = portGetter;
		_http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
	}

	public long TotalEvents { get; private set; }

	public void AddEvent(string level, string msg)
	{
		lock (_lock)
		{
			Events.Add(new LlamaEvent { Ts = DateTime.Now, Level = level, Msg = msg });
			TotalEvents++;
			if (Events.Count > 200)
			{
				Events.RemoveAt(0);
			}
		}
	}

	public List<SlotInfo> SlotsSnapshot()
	{
		lock (_lock)
		{
			return new List<SlotInfo>(Slots);
		}
	}

	public List<LlamaEvent> EventsSnapshot()
	{
		lock (_lock)
		{
			return new List<LlamaEvent>(Events);
		}
	}

	/// <summary>快轮询: /slots。llama 未起或失败达 3 次 → 离线。返回 false 表示本轮无数据。</summary>
	public bool PollFast()
	{
		int port = _portGetter();
		if (port <= 0)
		{
			return false;
		}
		string json;
		try
		{
			json = _http.GetStringAsync($"http://127.0.0.1:{port}/slots").GetAwaiter().GetResult();
		}
		catch
		{
			_failCount++;
			if (_failCount >= 3 && Online)
			{
				Online = false;
				GenTps = 0;
				PromptTps = 0;
				lock (Slots)
				{
					foreach (var sl in Slots)
					{
						sl.GenSpeed = 0;
						sl.PromptSpeed = 0;
					}
				}
				Phase = "idle";
				TgTps = Tg3sTps = null;
				PromptProgress = LogPromptSpeed = null;
				AddEvent("error", "llama-server 离线(连续 3 次采集失败)");
			}
			return false;
		}
		_failCount = 0;
		if (!Online)
		{
			Online = true;
			AddEvent("ok", "llama-server 在线");
		}
		try
		{
			using var doc = JsonDocument.Parse(json);
			JsonElement slotsEl;
			var root = doc.RootElement;
			if (root.ValueKind == JsonValueKind.Array)
			{
				slotsEl = root.Clone();
			}
			else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("slots", out var sl))
			{
				slotsEl = sl.Clone();
			}
			else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("default", out var dv))
			{
				var arr = JsonDocument.Parse("[" + dv.GetRawText() + "]");
				slotsEl = arr.RootElement.Clone();
			}
			else
			{
				return true;
			}
			ProcessSlots(slotsEl);
			return true;
		}
		catch
		{
			return true;
		}
	}

	/// <summary>慢轮询: /props + /v1/models, 30s 间隔(内部节流, 由快轮询路径驱动)。</summary>
	public void PollSlowIfNeeded()
	{
		double now = Environment.TickCount64 / 1000.0;
		if (now - _lastSlowPoll < 30.0)
		{
			return;
		}
		_lastSlowPoll = now;
		int port = _portGetter();
		if (port <= 0)
		{
			return;
		}
		try
		{
			string json = _http.GetStringAsync($"http://127.0.0.1:{port}/props").GetAwaiter().GetResult();
			using var doc = JsonDocument.Parse(json);
			var p = doc.RootElement;
			lock (_lock)
			{
				string oldPath = Model.Path;
				if (p.TryGetProperty("model_path", out var mp) && mp.ValueKind == JsonValueKind.String)
				{
					Model.Path = mp.GetString() ?? "";
				}
				if (p.TryGetProperty("model_alias", out var ma) && ma.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(ma.GetString()))
				{
					Model.Name = ma.GetString() ?? "";
				}
				else if (!string.IsNullOrEmpty(Model.Path))
				{
					Model.Name = Model.Path[(Model.Path.LastIndexOf('/') + 1)..].Replace('\\', '/');
					int slash = Model.Name.LastIndexOf('/');
					if (slash >= 0)
					{
						Model.Name = Model.Name[(slash + 1)..];
					}
				}
				TryLong(p, "n_embd", v => Model.NEmbd = v);
				TryLong(p, "n_vocab", v => Model.NVocab = v);
				TryLong(p, "n_ctx", v => Model.NCtx = v);
				TryLong(p, "n_ctx_train", v => Model.NCtxTrain = v);
				if (p.TryGetProperty("vocab_type", out var vt) && vt.ValueKind == JsonValueKind.String)
				{
					Model.VocabType = vt.GetString() ?? "";
				}
				if (p.TryGetProperty("modalities", out var mods) && mods.ValueKind == JsonValueKind.Array)
				{
					foreach (var m in mods.EnumerateArray())
					{
						string mv = m.GetString() ?? "";
						if (mv == "image")
						{
							Model.Vision = true;
						}
						if (mv == "video")
						{
							Model.Video = true;
						}
						if (mv == "audio")
						{
							Model.Audio = true;
						}
					}
				}
				if (p.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
				{
					Model.Capabilities.Clear();
					foreach (var c in caps.EnumerateArray())
					{
						if (c.ValueKind == JsonValueKind.String)
						{
							Model.Capabilities.Add(c.GetString() ?? "");
						}
					}
				}
				if (oldPath != Model.Path && !string.IsNullOrEmpty(oldPath) && !string.IsNullOrEmpty(Model.Path))
				{
					AddEvent("warn", "模型变更 → " + Model.Path);
				}
			}
		}
		catch
		{
		}
		try
		{
			string json = _http.GetStringAsync($"http://127.0.0.1:{port}/v1/models").GetAwaiter().GetResult();
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
			{
				var m = data[0];
				lock (_lock)
				{
					TryLong(m, "n_params", v => Model.NParams = v);
					TryLong(m, "size", v => Model.FileSize = v);
					if (m.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
					{
						TryLong(meta, "n_params", v => Model.NParams ??= v);
						TryLong(meta, "size", v => Model.FileSize ??= v);
					}
				}
			}
		}
		catch
		{
		}
	}

	private static void TryLong(JsonElement e, string name, Action<long> set)
	{
		if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v))
		{
			try
			{
				if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l))
				{
					set(l);
				}
			}
			catch
			{
			}
		}
	}

	private void ProcessSlots(JsonElement slotsEl)
	{
		double now = Environment.TickCount64 / 1000.0;
		var list = new List<SlotInfo>();
		double totalGen = 0, totalPrompt = 0;
		long aggUsed = -1, aggTotal = 0;
		lock (_lock)
		{
			foreach (var slot in slotsEl.EnumerateArray())
			{
				if (slot.ValueKind != JsonValueKind.Object)
				{
					continue;
				}
				int sid = slot.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
				if (!_slotStates.TryGetValue(sid, out var st))
				{
					st = new SlotState();
					_slotStates[sid] = st;
				}
				bool processing = slot.TryGetProperty("is_processing", out var ip) && ip.ValueKind == JsonValueKind.True;
				long? taskId = slot.TryGetProperty("id_task", out var it) && it.ValueKind == JsonValueKind.Number ? it.GetInt64() : null;
				long nDecoded = GetDecoded(slot);
				long nPromptProcessed = LongOf(slot, "n_prompt_tokens_processed");
				long nPromptCache = LongOf(slot, "n_prompt_tokens_cache");
				long nPrompt = LongOf(slot, "n_prompt_tokens");
				long nRemain = LongOf(slot, "n_remain");
				long nCtx = LongOf(slot, "n_ctx");
				double dt = st.PrevTs.HasValue ? now - st.PrevTs.Value : 0.0;
				if (processing)
				{
					if (st.PrevTaskId == null)
					{
						st.TaskStartTs = now;
						AddEvent("info", $"任务 #{taskId} 开始 · prompt {nPrompt} tok");
					}
					if (taskId != st.PrevTaskId || nDecoded < st.PrevDecoded)
					{
						st.PrevTaskId = taskId;
						st.PrevDecoded = nDecoded;
						st.PrevPromptProcessed = nPromptProcessed;
						st.GenSpeed = 0;
						st.PromptSpeed = 0;
					}
					else if (dt > 0)
					{
						st.GenSpeed = Math.Max(0.0, (nDecoded - st.PrevDecoded) / dt);
						st.PromptSpeed = Math.Max(0.0, (nPromptProcessed - st.PrevPromptProcessed) / dt);
						st.PrevDecoded = nDecoded;
					}
					st.PrevPromptProcessed = nPromptProcessed;
				}
				else
				{
					// 完成事件由 release 行(日志源, 统计完整)统一发出; 此处只复位差分基线
					if (st.PrevTaskId != null)
					{
						st.PrevTaskId = null;
						st.TaskStartTs = null;
					}
					st.GenSpeed = 0;
					st.PromptSpeed = 0;
				}
				st.PrevTs = now;
				// 上下文占用: 标准版 prompt+decoded; 自定义 build 的 n_prompt_tokens 已含 decoded
				long ctxUsed = slot.TryGetProperty("n_decoded", out var _)
					? nPrompt + nDecoded
					: nPrompt;
				var info = new SlotInfo
				{
					Id = sid,
					Processing = processing,
					TaskId = taskId,
					NCtx = nCtx,
					NPromptTokens = nPrompt,
					NPromptProcessed = nPromptProcessed,
					NPromptCache = nPromptCache,
					NDecoded = nDecoded,
					NRemain = nRemain,
					CtxUsed = ctxUsed,
					CtxPct = nCtx > 0 ? Math.Round(ctxUsed * 100.0 / nCtx, 2) : null,
					GenSpeed = st.GenSpeed,
					PromptSpeed = st.PromptSpeed,
					Speculative = slot.TryGetProperty("params", out var prm) && prm.ValueKind == JsonValueKind.Object
						&& prm.TryGetProperty("speculative", out var spec) && spec.ValueKind == JsonValueKind.Object
						&& spec.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array && types.GetArrayLength() > 0,
				};
				list.Add(info);
				if (aggUsed < 0 || ctxUsed > aggUsed)
				{
					aggUsed = ctxUsed;
				}
				if (nCtx > aggTotal)
				{
					aggTotal = nCtx;
				}
				if (processing)
				{
					totalGen = Math.Max(totalGen, st.GenSpeed);
					totalPrompt = Math.Max(totalPrompt, st.PromptSpeed);
				}
			}
			Slots.Clear();
			Slots.AddRange(list);
			GenTps = Math.Round(totalGen, 2);
			PromptTps = Math.Round(totalPrompt, 2);
			SpeedSource = totalGen > 0 || totalPrompt > 0 ? "api" : "";
			if (aggUsed >= 0)
			{
				CtxUsed = aggUsed;
			}
			CtxTotal = aggTotal;
		}
	}

	private static long LongOf(JsonElement slot, string name)
	{
		return slot.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
	}

	private static long GetDecoded(JsonElement slot)
	{
		if (slot.TryGetProperty("n_decoded", out var n))
		{
			return n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var l) ? l : 0;
		}
		if (slot.TryGetProperty("next_token", out var nt) && nt.ValueKind == JsonValueKind.Array && nt.GetArrayLength() > 0 && nt[0].ValueKind == JsonValueKind.Object && nt[0].TryGetProperty("n_decoded", out var n2))
		{
			return n2.ValueKind == JsonValueKind.Number && n2.TryGetInt64(out var l2) ? l2 : 0;
		}
		return 0;
	}
}

// ============================ 日志状态机(llama-lens LogPoller 移植) ============================
public sealed class LastTaskInfo
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

// 11 类日志行(与 llama-lens log_poller.py 同一正则集)
public static partial class LlamaLogRules
{
	public static readonly Regex Decode = new Regex(
		"slot\\s+print_timing:\\s+id\\s+(\\d+)\\s*\\|\\s*task\\s+(\\d+)\\s*\\|\\s*" +
		"n_decoded\\s*=\\s*(\\d+),\\s*tg\\s*=\\s*([\\d.]+)\\s*t/s,\\s*tg_3s\\s*=\\s*([\\d.]+)\\s*t/s");
	public static readonly Regex Prompt = new Regex(
		"slot\\s+print_timing:\\s+id\\s+(\\d+)\\s*\\|\\s*task\\s+(\\d+)\\s*\\|\\s*" +
		"prompt processing,\\s*n_tokens\\s*=\\s*(\\d+),\\s*progress\\s*=\\s*([\\d.]+),\\s*" +
		"t\\s*=\\s*([\\d.]+)\\s*s\\s*/\\s*([\\d.]+)\\s*tokens per second");
	public static readonly Regex PromptSummary = new Regex(
		"prompt eval time\\s*=\\s*([\\d.]+)\\s*ms\\s*/\\s*(\\d+)\\s*tokens\\s*" +
		"\\(\\s*([\\d.]+)\\s*ms per token,\\s*([\\d.]+)\\s*tokens per second\\)");
	public static readonly Regex EvalSummary = new Regex(
		"(?<!prompt )eval time\\s*=\\s*([\\d.]+)\\s*ms\\s*/\\s*(\\d+)\\s*tokens\\s*" +
		"\\(\\s*([\\d.]+)\\s*ms per token,\\s*([\\d.]+)\\s*tokens per second\\)");
	public static readonly Regex TotalSummary = new Regex("total time\\s*=\\s*([\\d.]+)\\s*ms\\s*/\\s*(\\d+)\\s*tokens");
	public static readonly Regex Mtp = new Regex(
		"draft acceptance\\s*=\\s*([\\d.]+)\\s*\\(\\s*(\\d+) accepted\\s*/\\s*(\\d+) generated\\),\\s*" +
		"mean len\\s*=\\s*([\\d.]+)");
	public static readonly Regex Graphs = new Regex("graphs reused\\s*=\\s*(\\d+)");
	public static readonly Regex Release = new Regex(
		"slot\\s+release:\\s+id\\s+(\\d+)\\s*\\|\\s*task\\s+(\\d+)\\s*\\|\\s*" +
		"stop processing:\\s*n_tokens\\s*=\\s*(\\d+),\\s*truncated\\s*=\\s*(\\d+)");
	public static readonly Regex Launch = new Regex(
		"slot\\s+launch_slot_:\\s+id\\s+(\\d+)\\s*\\|\\s*task\\s+(\\d+)\\s*\\|\\s*" +
		"processing task,\\s*is_child\\s*=\\s*(\\d+)");
	public static readonly Regex SlotSelect = new Regex(
		"slot\\s+get_availabl:\\s+id\\s+(\\d+)\\s*\\|\\s*task\\s+-1\\s*\\|\\s*" +
		"selected slot by\\s+(LRU|LCP similarity)" +
		"(?:,\\s*f_sim_best\\s*=\\s*([\\d.]+)\\s*\\(>\\s*[\\d.]+\\s+thold\\),\\s*f_keep\\s*=\\s*([\\d.]+))?");
	public static readonly Regex BootModel = new Regex("loading model\\s+'(.+?)'");
	public static readonly Regex BootSlots = new Regex("n_slots\\s*=\\s*(\\d+),\\s*n_ctx_slot\\s*=\\s*(\\d+),\\s*kv_unified\\s*=\\s*'(\\w+)'");
	public static readonly Regex BootListen = new Regex("listening on\\s+(\\S+)");
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

partial class LlamaApiMonitor
{
	// 相位/实时状态(日志优先, llama-lens state 字段同名同义)
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

	// 持久记录
	public LastTaskInfo? LastTask { get; private set; }
	public double? LastPrefillSpeed { get; private set; }
	public long? LastPrefillTokens { get; private set; }

	// 启动块
	public BootInfo Boot { get; } = new();

	// graphs reused(CUDA graph 复用次数, 上次任务后保留)
	public long? GraphsReused { get; private set; }

	// MTP 投机解码接受率(draft acceptance 行, 无投机时 null)
	public double? MtpAcceptance { get; private set; }
	public long? MtpAccepted { get; private set; }
	public long? MtpGenerated { get; private set; }
	public double? MtpMeanLen { get; private set; }

	// ctx(日志 release 行, slots API 优先合并)
	public long? LogCtxUsed { get; private set; }
	public bool LogCtxTruncated { get; private set; }

	private long? _prefillTask;
	private double _promptMs, _evalMs;
	private long _promptTokens, _decodedTokens, _totalTokens;
	private double _promptSpd, _genSpd;
	private double _totalMs;
	private int? _relSlot;
	private long? _relTask;
	private long _relTokens;
	private bool _relTruncated;

	private static double? F(string? x)
	{
		return double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
	}

	private static long I(string? x)
	{
		return long.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
	}

	private static string G(Regex re, string input, int g)
	{
		var m = re.Match(input);
		return m.Success && m.Groups[g].Success ? m.Groups[g].Value : "";
	}

	/// <summary>llama stdout 行喂入(每行, 顺序调用)。PID 前缀的 syslog 场景不存在(本机直读 stdout)。</summary>
	public void FeedLogLine(string line)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return;
		}
		try
		{
			ParseLogLine(line);
		}
		catch
		{
		}
	}

	/// <summary>llama 重启时重置状态机(等价 _reset_machine)。</summary>
	public void ResetLogMachine()
	{
		MtpAcceptance = MtpAccepted = null;
		MtpGenerated = null;
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
		_prefillTask = null;
		LogCtxUsed = null;
		LogCtxTruncated = false;
		_promptMs = _evalMs = _promptSpd = _genSpd = _totalMs = 0;
		_promptTokens = _decodedTokens = _totalTokens = 0;
		Online = false;
		_failCount = 0;
		_slotStates.Clear();
		lock (Slots)
		{
			Slots.Clear();
		}
		Boot.Warnings.Clear();
	}

	private void ParseLogLine(string msg)
	{
		var m = LlamaLogRules.Decode.Match(msg);
		if (m.Success)
		{
			Phase = "decoding";
			LogTaskId = I(G(LlamaLogRules.Decode, msg, 2));
			LogNDecoded = I(m.Groups[3].Value);
			TgTps = F(m.Groups[4].Value);
			Tg3sTps = F(m.Groups[5].Value);
			return;
		}
		m = LlamaLogRules.Prompt.Match(msg);
		if (m.Success)
		{
			Phase = "prompt_processing";
			LogTaskId = I(m.Groups[2].Value);
			PromptProgress = F(m.Groups[4].Value);
			LogPromptSpeed = F(m.Groups[6].Value);
			PromptTotalTokens = I(m.Groups[3].Value);
			PromptElapsedS = F(m.Groups[5].Value);
			LastPrefillSpeed = LogPromptSpeed;
			LastPrefillTokens = PromptTotalTokens;
			if (_prefillTask != LogTaskId)
			{
				_prefillTask = LogTaskId;
				AddEvent("info", $"任务 #{LogTaskId} 预填充开始{(PromptTotalTokens.HasValue ? $" (prompt {PromptTotalTokens} tokens)" : "")}");
			}
			return;
		}
		m = LlamaLogRules.Release.Match(msg);
		if (m.Success)
		{
			_relSlot = (int)I(m.Groups[1].Value);
			_relTask = I(m.Groups[2].Value);
			_relTokens = I(m.Groups[3].Value);
			_relTruncated = m.Groups[4].Value == "1";
			OnTaskEnd();
			return;
		}
		m = LlamaLogRules.Launch.Match(msg);
		if (m.Success)
		{
			LogTaskId = I(m.Groups[2].Value);
			IsChild = m.Groups[3].Value != "0";
			TaskStartedAt = DateTime.Now;
			_promptMs = _evalMs = _totalMs = 0;
			_promptTokens = _decodedTokens = _totalTokens = 0;
			_promptSpd = _genSpd = 0;
			AddEvent("info", $"任务 #{LogTaskId} 开始{(IsChild == true ? " · 子任务" : "")}");
			return;
		}
		m = LlamaLogRules.PromptSummary.Match(msg);
		if (m.Success)
		{
			_promptMs = F(m.Groups[1].Value) ?? 0;
			_promptTokens = I(m.Groups[2].Value);
			_promptSpd = F(m.Groups[4].Value) ?? 0;
			return;
		}
		m = LlamaLogRules.EvalSummary.Match(msg);
		if (m.Success)
		{
			_evalMs = F(m.Groups[1].Value) ?? 0;
			_decodedTokens = I(m.Groups[2].Value);
			_genSpd = F(m.Groups[4].Value) ?? 0;
			return;
		}
		m = LlamaLogRules.TotalSummary.Match(msg);
		if (m.Success)
		{
			_totalMs = F(m.Groups[1].Value) ?? 0;
			_totalTokens = I(m.Groups[2].Value);
			return;
		}
		m = LlamaLogRules.Mtp.Match(msg);
		if (m.Success)
		{
			MtpAcceptance = F(m.Groups[1].Value);
			MtpAccepted = (long)I(m.Groups[2].Value);
			MtpGenerated = (long)I(m.Groups[3].Value);
			MtpMeanLen = F(m.Groups[4].Value);
			return;
		}
		m = LlamaLogRules.Graphs.Match(msg);
		if (m.Success)
		{
			GraphsReused = I(m.Groups[1].Value);
			return;
		}
		m = LlamaLogRules.BootSlots.Match(msg);
		if (m.Success)
		{
			Boot.NSlots = (int)I(m.Groups[1].Value);
			Boot.NCtxSlot = (int)I(m.Groups[2].Value);
			Boot.KvUnified = m.Groups[3].Value == "true";
			return;
		}
		m = LlamaLogRules.BootModel.Match(msg);
		if (m.Success)
		{
			Boot.Model = m.Groups[1].Value;
			return;
		}
		m = LlamaLogRules.BootListen.Match(msg);
		if (m.Success)
		{
			Boot.Listening = m.Groups[1].Value;
			AddEvent("ok", "llama-server 监听 " + Boot.Listening);
			return;
		}
	}

	private void OnTaskEnd()
	{
		LogCtxUsed = _relTokens;
		LogCtxTruncated = _relTruncated;
		// 中断检测(= upstream): 无汇总行(eval/total time)说明任务被手动停止/断连
		bool interrupted = _totalMs <= 0 && _evalMs <= 0;
		double dur = TaskStartedAt.HasValue ? (DateTime.Now - TaskStartedAt.Value).TotalSeconds : 0;
		var t = new LastTaskInfo
		{
			Slot = _relSlot,
			TaskId = _relTask,
			PromptMs = _promptMs > 0 ? _promptMs : null,
			PromptTokens = _promptTokens > 0 ? _promptTokens : null,
			PromptSpeed = _promptSpd > 0 ? _promptSpd : null,
			EvalMs = _evalMs > 0 ? _evalMs : null,
			DecodedTokens = _decodedTokens > 0 ? _decodedTokens : null,
			GenSpeed = _genSpd > 0 ? _genSpd : null,
			TotalMs = _totalMs > 0 ? _totalMs : null,
			TotalTokens = _totalTokens > 0 ? _totalTokens : null,
			Duration = dur,
			AvgTps = dur > 0 && _decodedTokens > 0 ? _decodedTokens / dur : null,
			Truncated = _relTruncated,
			EndTime = DateTime.Now,
		};
		LastTask = t;
		Phase = "idle";
		LogNDecoded = 0;
		Tg3sTps = TgTps = null;
		PromptProgress = LogPromptSpeed = null;
		PromptTotalTokens = null;
		PromptElapsedS = null;
		TaskStartedAt = null;
		_prefillTask = null;
		_promptMs = _evalMs = _totalMs = _promptSpd = _genSpd = 0;
		_promptTokens = _decodedTokens = _totalTokens = 0;
		long outTok = t.TotalTokens ?? t.DecodedTokens ?? 0;
		AddEvent(interrupted ? "warn" : "ok",
			$"任务 #{_relTask} 完成 · {outTok} tok"
			+ (t.Duration > 0 ? $" · {t.Duration:F1}s" : "")
			+ (t.AvgTps.HasValue ? $" · 均 {t.AvgTps:F1} tok/s" : "")
			+ (interrupted ? " · 已中断" : "")
			+ (MtpAcceptance is double ma ? $" · MTP {ma * 100.0:F1}%" : ""));
	}

	/// <summary>速度来源优先级(= llama-lens engine.speeds()): 日志 tg_3s / prompt 行 > /slots 差分。</summary>
	public (double gen, double prompt, string source) Speeds()
	{
		double gen = GenTps;
		double prompt = PromptTps;
		string source = string.IsNullOrEmpty(SpeedSource) ? "api" : SpeedSource;
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
