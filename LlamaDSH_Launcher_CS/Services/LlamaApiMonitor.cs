using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// llama-server HTTP 采集器(llama-lens LlamaPoller 移植):
/// - /slots 快轮询: 每槽差分算 实时生成/预填充 tps、上下文占用(prompt+decoded)、任务开始/结束事件
/// - /props + /v1/models 慢轮询(30s): 模型路径/名称/量化/参数量/维度/词表/上下文/模态/能力
/// 差分算法与字段兼容(llama-lens backend/pollers/llama_api.py), 含自定义 build 的
/// next_token[0].n_decoded 回退与 n_prompt_tokens 已含 decoded 的口径处理。
/// </summary>
public sealed class LlamaApiMonitor
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

	public void AddEvent(string level, string msg)
	{
		lock (_lock)
		{
			Events.Add(new LlamaEvent { Ts = DateTime.Now, Level = level, Msg = msg });
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
			if (_failCount == 3 && Online)
			{
				Online = false;
				GenTps = 0;
				PromptTps = 0;
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
					if (st.PrevTaskId != null)
					{
						double? dur = st.TaskStartTs.HasValue ? now - st.TaskStartTs.Value : null;
						double? avg = dur > 0 && st.PrevDecoded > 0 ? st.PrevDecoded / dur : null;
						AddEvent("ok", $"任务 #{st.PrevTaskId} 完成 · {st.PrevDecoded} tok{(dur.HasValue ? $" · {dur:F1}s" : "")}{(avg.HasValue ? $" · 均 {avg:F1} tok/s" : "")}");
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
