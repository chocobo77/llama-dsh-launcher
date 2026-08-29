using System;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LlamaDSH_Launcher.Models;
using LlamaDSH_Launcher.Services;
using Microsoft.Win32;

namespace LlamaDSH_Launcher;

public partial class MainWindow : Window, IComponentConnector
{
	private const string DSH_DIR = "%DSH%";

	private static readonly string CFG_DIR = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal));

	private const string CFG_FILE = "LlamaDSH_Launcher_config.json";

	private Process? _llamaProc;

	private ProcessTracker? _dshTracker;

	private Process? _dshProc;

	private readonly GpuMonitor _gpuMonitor;

	private CancellationTokenSource? _perfCts;

	private Thread? _perfThread;

	private PerformanceCounter? _cpuCounter;

	private PerformanceCounter? _memCounter;

	private readonly ConcurrentQueue<double> _tpsSamples;

	private string _lastLogLine;

	private int _dshHiddenCount;

	private int _dshRetriesLeft;

	private bool _dshTaskMode;

	private string? _dshBootLogPath;

	private CancellationTokenSource? _dshTailCts;

	private DispatcherTimer? _dshVerifyTimer;

	private DateTime _dshVerifyDeadline;

	private bool _vramBreakerTriggered;

	private readonly ConcurrentQueue<double> _ttftSamples;

	private readonly ConcurrentQueue<double> _pfSamples;

	private string _perfLineBuf;

	private static readonly HttpClient _http = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(2.0)
	};

	private readonly string _stateFile;

	private readonly string _cfgPathFile;

	private readonly string _windowStateFile;

	private readonly StringBuilder _runLog;

	private readonly object _runLogLock;

	private int _runLogCount;

	private const int RunLogMaxLines = 5000;

	private string _configDir;

	private string _configFilename;

	private Dictionary<string, ProfileConfig> _profiles;

	private string _currentProfile;

	private string _lastCmd;

	private readonly DispatcherTimer _healthTimer;

	private readonly ConcurrentQueue<string> _logPending;

	private readonly DispatcherTimer _logTimer;

	private const int LogMaxLines = 2000;

	private const int LogTrimKeep = 1700;

	private NotifyIcon? _trayIcon;

	private readonly List<ParamDefinition> _paramDefs;

	private readonly Dictionary<string, (System.Windows.Controls.CheckBox ec, System.Windows.Controls.Control vw)> _pw;

	private readonly Dictionary<string, System.Windows.Controls.CheckBox> _pbw;

	private static readonly Regex _rePromptTps = new Regex("prompt eval time\\s+=\\s*([\\d.]+)\\s*ms\\s*/\\s*(\\d+)\\s*tokens", RegexOptions.Compiled);

	private static readonly Regex _rePromptMs = new Regex("prompt eval time\\s+=\\s*([\\d.]+)\\s*ms", RegexOptions.Compiled);

	private static readonly Regex _reEvalTps = new Regex("(?<!prompt )eval time\\s+=\\s*[\\d.]+\\s*ms\\s*/\\s*\\d+\\s*(?:new tokens|tokens|runs)\\s*\\([^)]*?([\\d.]+)\\s*(?:tokens? per second|tok/s)", RegexOptions.Compiled);

	private static readonly Regex _reEvalRaw = new Regex("(?<!prompt )eval time\\s+=\\s*([\\d.]+)\\s*ms\\s*/\\s*(\\d+)\\s*(?:new tokens|tokens|runs)", RegexOptions.Compiled);

	public MainWindow()
	{
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dd: Expected O, but got Unknown
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_0107: Expected O, but got Unknown
		//IL_01c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ca: Expected O, but got Unknown
		_gpuMonitor = new GpuMonitor();
		_tpsSamples = new ConcurrentQueue<double>();
		_lastLogLine = "";
		_ttftSamples = new ConcurrentQueue<double>();
		_pfSamples = new ConcurrentQueue<double>();
		_perfLineBuf = "";
		_stateFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh_launcher_state.json");
		_cfgPathFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llamadsh_config_path.json");
		_windowStateFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llamadsh_window.json");
		_runLog = new StringBuilder();
		_runLogLock = new object();
		_profiles = new Dictionary<string, ProfileConfig>();
		_currentProfile = "默认方案";
		_lastCmd = "";
		_healthTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(5.0)
		};
		_logPending = new ConcurrentQueue<string>();
		_logTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(100.0)
		};
		_pw = new Dictionary<string, (System.Windows.Controls.CheckBox, System.Windows.Controls.Control)>();
		_pbw = new Dictionary<string, System.Windows.Controls.CheckBox>();
		
		_configDir = CFG_DIR;
		_configFilename = "LlamaDSH_Launcher_config.json";
		_paramDefs = BuildParamDefs();
		LoadConfigDirFromFile();
		InitializeComponent();
		BuildParamGroups();
		SetupTray();
		WireLiveRefresh();
		RefreshCmd();
		StartPerfMonitor();
		LoadState();
		_healthTimer.Tick += HealthCheck;
		_healthTimer.Start();
		_logTimer.Tick += delegate
		{
			FlushLogs();
		};
		_logTimer.Start();
		base.IsVisibleChanged += new DependencyPropertyChangedEventHandler(OnVisibilityChanged);
	}

	private static List<ParamDefinition> BuildParamDefs()
	{
		int processorCount = Environment.ProcessorCount;
		return new List<ParamDefinition>
		{
			new ParamDefinition("别名 (Alias)", "alias", ParamType.String, "qwen3.8-27b", "基础设置", enabled: true, isAdvanced: false, null, "模型别名，用于 API 返回中的 model 字段标识。建议设为模型简称，方便上层应用识别。"),
			new ParamDefinition("主机 (Host)", "host", ParamType.String, "127.0.0.1", "基础设置", enabled: true, isAdvanced: false, null, "监听 IP 地址。127.0.0.1 仅本机可访问；0.0.0.0 允许局域网访问（注意安全风险）。也支持 UNIX socket 路径（以 .sock 结尾）。建议：本地使用保持 127.0.0.1。"),
			new ParamDefinition("端口 (Port)", "port", ParamType.String, "1234", "基础设置", enabled: true, isAdvanced: false, null, "HTTP 监听端口，范围 1-65535。llama-server 默认 8080，此启动器默认 1234 以避免与其他服务冲突。"),
			new ParamDefinition("超时 (Timeout)", "timeout", ParamType.String, "600", "基础设置", enabled: true, isAdvanced: false, null, "llama-server 读写超时（秒）。超时后断开空闲连接。长文本生成建议 >=300s，多轮对话建议 600s。设为 0 表示永不超时。"),
			new ParamDefinition("上下文大小 (Context)", "ctx-size", ParamType.String, "49152", "基础设置", enabled: true, isAdvanced: false, null, "Prompt 上下文窗口长度（token 数）。决定单次对话能处理的最大 token 量。支持 K/M 后缀（如 100K=102400）。越大越占显存：27B Q4 模型约 16GB，24GB 显卡 + q4_0 KV 时建议 <=48K；100K 以上预计总占用超 23GB，极易 OOM 崩溃。启动前会自动估算显存并预警。默认 0 = 从模型元数据读取。"),
			new ParamDefinition("批处理大小 (Batch Size)", "batch-size", ParamType.String, "2048", "基础设置", enabled: true, isAdvanced: false, null, "逻辑最大批处理大小。控制 prompt 处理时一次送入模型的 token 上限。增大可加速 prompt 处理但增加显存占用。默认 2048，24GB 显卡建议 2048~4096。"),
			new ParamDefinition("微批大小 (Ubatch)", "ubatch-size", ParamType.String, "512", "基础设置", enabled: true, isAdvanced: false, null, "物理微批处理大小。实际每次送入推理引擎的 token 数，必须 <= batch-size。较小值降低延迟但降低吞吐。默认 512，显存充裕可提至 1024。"),
			new ParamDefinition("线程数 (Threads)", "threads", ParamType.String, processorCount.ToString(), "基础设置", enabled: true, isAdvanced: false, null, "生成阶段使用的 CPU 线程数。默认 -1 = 自动（使用全部物理核心）。建议设为 CPU 物理核心数（不含超线程），过多反而因上下文切换降低性能。"),
			new ParamDefinition("GPU 层数", "n-gpu-layers", ParamType.String, "99", "基础设置", enabled: true, isAdvanced: false, null, "卸载到 GPU 的模型层数。0=纯 CPU；99=尽可能全部卸载。27B Q4 模型约需 99 层全卸载到 24GB 显卡。设为 auto 可自动计算。显存不足时逐层减少。"),
			new ParamDefinition("主 GPU 编号", "main-gpu", ParamType.String, "0", "基础设置", enabled: true, isAdvanced: false, null, "主 GPU 设备编号（从 0 开始）。单卡用户保持 0。多卡时指定存放中间结果和 KV 缓存的 GPU。"),
			new ParamDefinition("HTTP 线程数", "threads-http", ParamType.String, "-1", "基础设置", enabled: false, isAdvanced: false, null, "处理 HTTP 请求的线程数。默认 -1 = 自动（通常为 8）。高并发场景可适当增加，但过多会与推理线程争抢 CPU。建议：单用户保持 -1；多用户可设 4~8。"),
			new ParamDefinition("API 密钥", "api-key", ParamType.String, "", "基础设置", enabled: false, isAdvanced: false, null, "API 认证密钥，多个用逗号分隔。留空表示无需认证。暴露在公网时务必设置，否则任何人都能调用你的模型。"),
			new ParamDefinition("SSL 私钥文件", "ssl-key-file", ParamType.String, "", "基础设置", enabled: false, isAdvanced: false, null, "PEM 格式的 SSL 私钥文件路径。与证书文件配合启用 HTTPS 加密通信。本地使用无需设置。"),
			new ParamDefinition("SSL 证书文件", "ssl-cert-file", ParamType.String, "", "基础设置", enabled: false, isAdvanced: false, null, "PEM 格式的 SSL 证书文件路径。需与私钥文件配对使用。可用 Let''s Encrypt 等免费证书。"),
			new ParamDefinition("CORS 允许来源", "cors-origins", ParamType.String, "", "基础设置", enabled: false, isAdvanced: false, null, "跨域资源共享（CORS）允许的来源列表，逗号分隔。留空默认允许所有来源（*）。Web 前端跨域访问时需设置为前端域名。设为 localhost 只反射本机 Origin。"),
			new ParamDefinition("Flash Attention", "flash-attn", ParamType.Combo, "on", "性能与内存", enabled: true, isAdvanced: false, new List<string> { "on", "off", "auto" }, "Flash Attention 加速。需要 GPU 硬件支持（AMD RDNA2+/NVIDIA Ampere+）。可显著降低长上下文的显存占用并加速推理。推荐 on；若启动报错则改 auto。"),
			new ParamDefinition("张量拆分", "tensor-split", ParamType.String, "", "性能与内存", enabled: false, isAdvanced: false, null, "多 GPU 张量拆分比例，逗号分隔。如 ''3,5'' 表示第一块 GPU 占 3/8、第二块占 5/8。按显存大小比例分配。单卡无需设置。"),
			new ParamDefinition("批处理线程数", "threads-batch", ParamType.String, "", "性能与内存", enabled: false, isAdvanced: false, null, "Prompt 处理（批处理）时的 CPU 线程数。默认与 --threads 相同。Prompt 处理是计算密集型，可适当增加线程数以加速首次响应。"),
			new ParamDefinition("禁用 mmap", "no-mmap", ParamType.Bool, false, "性能与内存", enabled: false, isAdvanced: true, null, "禁用内存映射（mmap）加载模型。mmap 允许操作系统按需加载模型片段，节省内存。禁用后加载更慢但可减少系统交换。一般保持关闭。已废弃，推荐使用 --load-mode。"),
			new ParamDefinition("禁用 mlock", "no-mlock", ParamType.Bool, false, "性能与内存", enabled: false, isAdvanced: true, null, "锁定模型到物理内存（mlock），防止被操作系统交换到磁盘。可避免长时间运行后的性能抖动，但需要足够的物理内存。已废弃，推荐使用 --load-mode mlock。"),
			new ParamDefinition("NUMA 策略", "numa", ParamType.Combo, "disabled", "性能与内存", enabled: false, isAdvanced: false, new List<string> { "disabled", "distribute", "isolate", "numactl" }, "NUMA 内存分配策略。仅多路 CPU（服务器）有意义。distribute=均匀分配；isolate=限制在启动 CPU 节点；numactl=使用 numactl 工具的映射。单路 CPU/桌面用户保持 disabled。"),
			new ParamDefinition("RoPE 基础频率", "rope-freq-base", ParamType.String, "", "性能与内存", enabled: false, isAdvanced: false, null, "RoPE 旋转位置编码的基础频率。默认从模型元数据读取。NTK-aware 缩放时可调大此值以扩展上下文。一般无需手动设置，除非模型训练时使用了非标准值。"),
			new ParamDefinition("RoPE 频率缩放", "rope-freq-scale", ParamType.String, "1.0", "性能与内存", enabled: false, isAdvanced: false, null, "RoPE 频率缩放因子。值为 N 时上下文扩展 N 倍。与 rope-scale 互为倒数关系。1.0 = 不缩放。长上下文扩展时配合 rope-scaling 方法使用。"),
			new ParamDefinition("Cache 类型 K", "cache-type-k", ParamType.Combo, "q8_0", "KV 缓存", enabled: true, isAdvanced: false, new List<string> { "f16", "q8_0", "q4_0", "q4_1", "iq4_nl", "q5_0", "q5_1", "q6_0" }, "KV 缓存 K 分量的数据类型。精度越低越省显存，但可能影响生成质量。f16=高精度高显存；q8_0=平衡；q4_0=省显存。24GB 显卡跑 27B Q4 建议配 q4_0 以留出上下文空间；48K 上下文下 f16 亦可。"),
			new ParamDefinition("Cache 类型 V", "cache-type-v", ParamType.Combo, "q4_0", "KV 缓存", enabled: true, isAdvanced: false, new List<string> { "f16", "q8_0", "q4_0", "q4_1", "iq4_nl", "q5_0", "q5_1", "q6_0" }, "KV 缓存 V 分量的数据类型。V 对精度不如 K 敏感，可更激进地压缩。24GB 显卡跑 27B Q4 建议 q4_0。f16 仅在显存充裕且追求最高质量时使用。"),
			new ParamDefinition("缓存 Prompt", "cache-prompt", ParamType.Bool, false, "KV 缓存", enabled: false, isAdvanced: true, null, "缓存 prompt 的 KV 状态，避免重复计算相同前缀。默认开启。多轮对话时可显著加速后续请求的首 token 延迟。建议保持开启。"),
			new ParamDefinition("缓存重用阈值", "cache-reuse", ParamType.String, "0", "KV 缓存", enabled: false, isAdvanced: true, null, "通过 KV 偏移复用缓存的最小 chunk 大小。需要启用 prompt 缓存。默认 0=禁用。设为 256~512 可在相似 prompt 间复用 KV，加速多轮对话。"),
			new ParamDefinition("碎片整理阈值", "defrag-thold", ParamType.String, "-1.0", "KV 缓存", enabled: false, isAdvanced: true, null, "KV 缓存碎片整理触发阈值。当碎片率超过此值时自动整理。默认 -1=自动。设为 0.1~0.5 可手动控制触发频率。注意：此参数已废弃，新版 llama.cpp 自动处理。"),
			new ParamDefinition("随机种子 (Seed)", "seed", ParamType.String, "-1", "采样控制", enabled: true, isAdvanced: false, null, "RNG 随机种子。-1 = 每次随机（默认）。设为固定值可使相同输入产生相同输出，便于调试和复现。测试时建议固定种子，生产环境保持 -1。"),
			new ParamDefinition("最大生成 Token", "predict", ParamType.String, "-1", "采样控制", enabled: false, isAdvanced: false, null, "单次请求最大生成 token 数。-1 = 无限（直到模型决定停止或达到上下文上限）。设为 512~2048 可防止模型过度生成。API 调用时通常由客户端的 max_tokens 参数覆盖。"),
			new ParamDefinition("温度 (Temp)", "temp", ParamType.String, "0.8", "采样控制", enabled: true, isAdvanced: false, null, "采样温度，控制输出随机性。0=确定性（贪心解码）；0.1~0.4=保守精确；0.6~0.8=平衡创意与连贯（推荐）；1.0+=高随机性。代码生成建议 0.1~0.3；对话建议 0.6~0.8；创意写作 0.8~1.2。"),
			new ParamDefinition("Top-K", "top-k", ParamType.String, "40", "采样控制", enabled: true, isAdvanced: false, null, "Top-K 采样：仅从概率最高的 K 个 token 中采样。K 越小输出越保守。默认 40。设为 1=贪心；10~40=平衡；0=禁用（仅用 Top-P/Min-P）。建议配合 Top-P 使用，保持默认 40。"),
			new ParamDefinition("Top-P", "top-p", ParamType.String, "0.9", "采样控制", enabled: true, isAdvanced: false, null, "核采样（Nucleus Sampling）：从累计概率达到 P 的最小 token 集合中采样。P 越小输出越集中。默认 0.95。0.9=保守；1.0=禁用。建议 0.9~0.95，配合 Min-P 效果更佳。"),
			new ParamDefinition("Min-P", "min-p", ParamType.String, "0.1", "采样控制", enabled: true, isAdvanced: false, null, "Min-P 采样：过滤掉概率低于最高概率 * min_p 的 token。比 Top-P 更直观的截断方式。默认 0.05。0.05~0.1=温和过滤；0.3~0.5=较强过滤。推荐与 Top-P 配合使用，保持 0.05~0.1。"),
			new ParamDefinition("局部典型采样", "typical", ParamType.String, "1.0", "采样控制", enabled: false, isAdvanced: true, null, "Locally Typical Sampling：基于信息量的采样策略，使输出更典型。1.0=禁用（默认）。0.5~0.9=启用。一般场景无需开启，对特定任务（如翻译）可能有帮助。"),
			new ParamDefinition("重复惩罚", "repeat-penalty", ParamType.String, "1.1", "采样控制", enabled: true, isAdvanced: false, null, "重复惩罚系数，抑制连续重复。1.0=禁用；>1.0=惩罚重复。默认 1.1。1.05~1.1=轻度（推荐）；1.2~1.3=较强。过高会导致用词贫乏。对中文模型建议 1.0~1.1。"),
			new ParamDefinition("频率惩罚", "frequency-penalty", ParamType.String, "0.0", "采样控制", enabled: true, isAdvanced: false, null, "频率惩罚：基于 token 出现频率的惩罚。频率越高的 token 被惩罚越重。0.0=禁用（默认）。0.1~0.5=轻度；1.0~2.0=较强。与 repeat-penalty 互补，可同时使用。"),
			new ParamDefinition("存在惩罚", "presence-penalty", ParamType.String, "0.0", "采样控制", enabled: true, isAdvanced: false, null, "存在惩罚：只要 token 出现过就施加固定惩罚（不关心频率）。鼓励模型谈论新话题。0.0=禁用（默认）。0.1~0.5=轻度；1.0~2.0=较强。适合长对话/写作场景。"),
			new ParamDefinition("Mirostat 模式", "mirostat", ParamType.Combo, "0", "采样控制", enabled: false, isAdvanced: false, new List<string> { "0", "1", "2" }, "Mirostat 自适应采样。自动调节温度以维持目标困惑度。0=禁用；1=Mirostat 1.0；2=Mirostat 2.0（推荐）。启用后 Top-K/Top-P/Min-P 被忽略。适合追求稳定质量的场景。"),
			new ParamDefinition("Mirostat 学习率", "mirostat-lr", ParamType.String, "0.1", "采样控制", enabled: false, isAdvanced: false, null, "Mirostat 学习率（eta）。控制对目标熵的追踪速度。越大调整越快但可能不稳定。默认 0.1。0.05~0.2 均可。仅在 Mirostat 模式 != 0 时生效。"),
			new ParamDefinition("Mirostat 目标熵", "mirostat-ent", ParamType.String, "5.0", "采样控制", enabled: false, isAdvanced: false, null, "Mirostat 目标熵（tau）。控制输出的困惑度/多样性。值越大输出越多样。默认 5.0。3.0~8.0 均可。仅在 Mirostat 模式 != 0 时生效。"),
			new ParamDefinition("BNF 语法", "grammar", ParamType.String, "", "采样控制", enabled: false, isAdvanced: false, null, "BNF-like 语法约束。强制模型输出符合指定语法结构。用于结构化输出（如 SQL、代码）。语法文件在 grammars/ 目录。一般场景用 JSON Schema 更方便。"),
			new ParamDefinition("JSON Schema", "json-schema", ParamType.String, "", "采样控制", enabled: false, isAdvanced: false, null, "JSON Schema 约束。强制模型输出符合指定的 JSON Schema。用于 API 返回结构化数据。如 {type:object,properties:{name:{type:string}}}。"),
			new ParamDefinition("推测类型", "spec-type", ParamType.Combo, "draft-mtp", "推测解码", enabled: true, isAdvanced: false, new List<string> { "draft", "draft-mtp", "draft-mtp-v3" }, "推测解码类型。draft=外部草稿模型；draft-mtp=模型自带 MTP 层（Qwen3 等原生支持）；draft-mtp-v3=MTP v3 版本。Qwen3 系列推荐 draft-mtp，可加速 2~3 倍。无 MTP 的模型用 draft + 独立草稿模型。"),
			new ParamDefinition("最大草稿 Token", "spec-draft-n-max", ParamType.String, "2", "推测解码", enabled: true, isAdvanced: false, null, "每次推测生成的最大草稿 token 数。越大越快但接受率可能下降。默认 3。2~5=平衡（推荐）；8~10=激进。24GB 显卡跑 27B 建议 2~3。接受率低于 50% 时应减小此值。"),
			new ParamDefinition("草稿接受阈值", "spec-draft-p-min", ParamType.String, "", "推测解码", enabled: false, isAdvanced: false, null, "草稿 token 的最低接受概率。低于此值的草稿 token 被拒绝。默认 0.0（全部接受）。带宽受限时设为 0.60~0.75 可提升质量。一般保持默认。"),
			new ParamDefinition("草稿线程数", "threads-draft", ParamType.String, "", "推测解码", enabled: false, isAdvanced: false, null, "草稿模型的 CPU 生成线程数。默认与 --threads 相同。使用外部草稿模型时可单独设置以平衡 CPU 负载。使用 draft-mtp 时无需设置。"),
			new ParamDefinition("多模态投影", "mmproj", ParamType.String, "", "多模态", enabled: false, isAdvanced: false, null, "多模态投影仪 GGUF 文件路径。启用视觉/音频等多模态能力。需与基础语言模型配对使用。如 Qwen2.5-VL 的 mmproj 文件。本地文件路径或 HuggingFace 均可。"),
			new ParamDefinition("媒体根目录", "media-path", ParamType.String, "", "多模态", enabled: false, isAdvanced: false, null, "本地媒体文件的根目录。API 可通过 file:// 相对路径访问此目录下的图片/视频。用于多模态推理时加载本地媒体资源。"),
			new ParamDefinition("并行序列数", "parallel", ParamType.String, "1", "并行与序列", enabled: true, isAdvanced: false, null, "服务器并发槽位数（同时处理的请求数）。默认 -1=自动。1=单用户（推荐本地使用）；2~4=多用户并发。每个槽位占用独立的 KV 缓存空间，增加会线性增加显存占用。MTP 推测解码建议保持 1。"),
			new ParamDefinition("序列数 (旧版)", "sequences", ParamType.String, "1", "并行与序列", enabled: false, isAdvanced: true, null, "旧版并行序列数参数，已被 --parallel 取代。保持默认值即可。"),
			new ParamDefinition("启用 Jinja", "jinja", ParamType.Bool, true, "模板与对话", enabled: true, isAdvanced: false, null, "启用 Jinja2 模板引擎处理聊天模板。默认开启。大多数模型需要 Jinja 正确格式化对话。禁用后使用内置的简单模板，可能导致对话格式错误。建议保持开启。"),
			new ParamDefinition("自定义模板", "chat-template", ParamType.String, "", "模板与对话", enabled: false, isAdvanced: false, null, "自定义 Jinja 聊天模板字符串。覆盖模型自带的模板。支持的内置模板名：chatml, llama2, llama3, gemma, mistral, deepseek 等。一般无需设置，使用模型自带模板即可。"),
			new ParamDefinition("模板文件", "chat-template-file", ParamType.String, "", "模板与对话", enabled: false, isAdvanced: false, null, "从文件加载自定义聊天模板。与 --chat-template 功能相同但内容从文件读取。适合模板较长时使用。"),
			new ParamDefinition("日志详细度", "verbosity", ParamType.String, "3", "扩展功能", enabled: false, isAdvanced: true, null, "日志输出详细程度。0=仅关键信息；1=错误；2=警告；3=信息（默认）；4=追踪；5=调试。调试时设 4~5；日常使用 2~3；安静模式 0~1。"),
			new ParamDefinition("启用 Embedding", "embedding", ParamType.Bool, false, "扩展功能", enabled: false, isAdvanced: true, null, "启用 Embeddings API 端点（/embedding）。需要使用专门的 Embedding 模型（如 nomic-embed、bge 等）。启用后只能用于 Embedding，不能用于文本生成。普通对话场景不要开启。"),
			new ParamDefinition("启用 Metrics", "metrics", ParamType.Bool, true, "扩展功能", enabled: false, isAdvanced: true, null, "启用 Prometheus 兼容的 /metrics 监控端点。启动器用它在运行中实时读取 TPS / Prefill / 首Token（也可在接管已有 Llama 进程时显示指标）。仅监听本机，安全无虞。"),
			new ParamDefinition("显示槽位", "slots", ParamType.Bool, false, "扩展功能", enabled: false, isAdvanced: true, null, "暴露 /slots 端点，显示各推理槽位的状态（正在处理、空闲等）。默认开启。调试并发问题时有用。安全敏感环境建议关闭。"),
			new ParamDefinition("LoRA", "lora", ParamType.String, "", "扩展功能", enabled: false, isAdvanced: true, null, "LoRA 适配器路径。在基础模型上叠加微调效果。支持多个适配器（逗号分隔）。格式：文件路径。无需重启可通过 API 热切换。"),
			new ParamDefinition("LoRA (带比例)", "lora-scaled", ParamType.String, "", "扩展功能", enabled: false, isAdvanced: true, null, "带缩放因子的 LoRA。格式：路径:比例，如 adapter.safetensors:0.8。比例控制 LoRA 影响强度，0.0~1.0。多个适配器用逗号分隔。"),
			new ParamDefinition("控制向量", "control-vector", ParamType.String, "", "扩展功能", enabled: false, isAdvanced: true, null, "控制向量（Control Vector）路径。通过向量运算引导模型行为（如更积极/更谨慎）。多个向量可逗号分隔叠加使用。"),
			new ParamDefinition("控制向量 (带比例)", "control-vector-scaled", ParamType.String, "", "扩展功能", enabled: false, isAdvanced: true, null, "带缩放因子的控制向量。格式：路径:比例。比例控制向量影响强度。负值可反转效果。多个用逗号分隔。"),
			new ParamDefinition("休眠空闲秒数", "sleep-idle-seconds", ParamType.String, "-1", "扩展功能", enabled: false, isAdvanced: false, null, "空闲多少秒后服务器自动休眠（卸载模型释放显存）。-1=禁用（默认）。设为 300~600 可在闲置时释放显存给其他程序。收到新请求时自动唤醒（需重新加载模型）。")
		};
	}

	private void BuildParamGroups()
	{
		string[] array = new string[9] { "基础设置", "性能与内存", "KV 缓存", "采样控制", "推测解码", "多模态", "并行与序列", "模板与对话", "扩展功能" };
		foreach (string gn in array)
		{
			Expander expander = new Expander
			{
				Header = gn,
				IsExpanded = true,
				Margin = new Thickness(0.0, 4.0, 0.0, 0.0),
				Foreground = System.Windows.Media.Brushes.White
			};
			Grid grid = new Grid
			{
				Margin = new Thickness(4.0)
			};
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(1.0, GridUnitType.Star)
			});
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(12.0)
			});
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(1.0, GridUnitType.Star)
			});
			int idx = 0;
			foreach (ParamDefinition item in _paramDefs.Where((ParamDefinition p) => p.Group == gn))
			{
				if (item.Type == ParamType.Bool)
				{
					System.Windows.Controls.CheckBox checkBox = new System.Windows.Controls.CheckBox
					{
						Content = item.Label,
						IsChecked = (bool)item.DefaultValue,
						ToolTip = item.Tooltip,
						Foreground = System.Windows.Media.Brushes.White,
						Margin = new Thickness(0.0, 2.0, 0.0, 2.0)
					};
					checkBox.Checked += delegate
					{
						RefreshCmd();
					};
					checkBox.Unchecked += delegate
					{
						RefreshCmd();
					};
					_pbw[item.Flag] = checkBox;
					PlaceParamRow(grid, checkBox, ref idx);
					continue;
				}
				DockPanel dockPanel = new DockPanel
				{
					Margin = new Thickness(0.0, 2.0, 0.0, 2.0)
				};
				System.Windows.Controls.CheckBox checkBox2 = new System.Windows.Controls.CheckBox
				{
					IsChecked = item.Enabled,
					ToolTip = "--" + item.Flag,
					VerticalAlignment = VerticalAlignment.Center,
					Margin = new Thickness(0.0, 0.0, 6.0, 0.0)
				};
				System.Windows.Controls.Label element = new System.Windows.Controls.Label
				{
					Content = item.Label + ":",
					ToolTip = item.Tooltip,
					Width = 160.0,
					Foreground = System.Windows.Media.Brushes.White,
					VerticalAlignment = VerticalAlignment.Center,
					Padding = new Thickness(0.0)
				};
				System.Windows.Controls.Control control;
				if (item.Type == ParamType.Combo && item.Options != null)
				{
					NoScrollComboBox noScrollComboBox = new NoScrollComboBox
					{
						IsEditable = true,
						ToolTip = item.Tooltip,
						MinWidth = 70.0,
						HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
					};
					foreach (string option in item.Options)
					{
						noScrollComboBox.Items.Add(option);
					}
					noScrollComboBox.Text = item.DefaultValue.ToString() ?? "";
					control = noScrollComboBox;
					checkBox2.Checked += delegate
					{
						RefreshCmd();
					};
					checkBox2.Unchecked += delegate
					{
						RefreshCmd();
					};
					noScrollComboBox.SelectionChanged += delegate
					{
						RefreshCmd();
					};
				}
				else
				{
					System.Windows.Controls.TextBox obj = new System.Windows.Controls.TextBox
					{
						Text = (item.DefaultValue.ToString() ?? ""),
						ToolTip = item.Tooltip,
						MinWidth = 70.0,
						HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
					};
					control = obj;
					checkBox2.Checked += delegate
					{
						RefreshCmd();
					};
					checkBox2.Unchecked += delegate
					{
						RefreshCmd();
					};
					obj.TextChanged += delegate
					{
						RefreshCmd();
					};
				}
				DockPanel.SetDock(checkBox2, Dock.Left);
				dockPanel.Children.Add(checkBox2);
				DockPanel.SetDock(element, Dock.Left);
				dockPanel.Children.Add(element);
				dockPanel.Children.Add(control);
				_pw[item.Flag] = (checkBox2, control);
				PlaceParamRow(grid, dockPanel, ref idx);
			}
			expander.Content = grid;
			ParamGroupsPanel.Children.Add(expander);
		}
	}

	private static void PlaceParamRow(Grid g, UIElement el, ref int idx)
	{
		int num = idx / 2;
		int value = idx % 2 * 2;
		while (g.RowDefinitions.Count <= num)
		{
			g.RowDefinitions.Add(new RowDefinition
			{
				Height = GridLength.Auto
			});
		}
		Grid.SetRow(el, num);
		Grid.SetColumn(el, value);
		g.Children.Add(el);
		idx++;
	}

	private void SetupTray()
	{
		string text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "app.ico");
		Icon icon;
		try
		{
			icon = (File.Exists(text) ? new Icon(text) : SystemIcons.Application);
		}
		catch (Exception value)
		{
			AppLog.WriteError($"托盘图标加载失败: {value}");
			icon = SystemIcons.Application;
		}
		_trayIcon = new NotifyIcon
		{
			Icon = icon,
			Text = "Llama 启动器",
			Visible = true
		};
		_trayIcon.DoubleClick += delegate
		{
			ShowFromTray();
		};
		ContextMenuStrip contextMenuStrip = new ContextMenuStrip();
		contextMenuStrip.Items.Add("显示窗口", null, delegate
		{
			ShowFromTray();
		});
		contextMenuStrip.Items.Add("退出程序", null, delegate
		{
			QuitApp();
		});
		_trayIcon.ContextMenuStrip = contextMenuStrip;
	}

	private void ShowFromTray()
	{
		Show();
		base.WindowState = WindowState.Normal;
		Activate();
	}

	private void UpdateTray()
	{
		if (_trayIcon != null)
		{
			string text = ((_llamaProc != null && !_llamaProc.HasExited) ? "运行中" : "已停止");
			string text2 = (HasDsh() ? "运行中" : "已停止");
			_trayIcon.Text = "Llama:" + text + " | DSH:" + text2;
		}
	}

	private void OnVisibilityChanged(object? s, DependencyPropertyChangedEventArgs e)
	{
		if (base.IsVisible)
		{
			_gpuMonitor.TempEnabled = true;
			_gpuMonitor.StartGpuTempLoop();
		}
		else
		{
			_gpuMonitor.TempEnabled = false;
			_gpuMonitor.StopGpuTempLoop();
		}
	}

	private void QuitApp()
	{
		bool flag = _llamaProc != null && !_llamaProc.HasExited;
		bool flag2 = HasDsh();
		bool flag3 = true;
		if (flag | flag2)
		{
			string text = "检测到以下服务正在运行：\n\n";
			if (flag)
			{
				text += "  • Llama Server\n";
			}
			if (flag2)
			{
				text += "  • DSH Web\n";
			}
			text += "\n请选择操作：\n\n  是(Y) — 停止所有服务并退出\n  否(N) — 不停止服务直接退出\n  取消 — 返回程序";
			MessageBoxResult messageBoxResult = System.Windows.MessageBox.Show(text, "确认退出", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
			if (messageBoxResult == MessageBoxResult.Cancel)
			{
				return;
			}
			flag3 = messageBoxResult == MessageBoxResult.Yes;
		}
		if (flag3)
		{
			StopLlama();
			StopDsh();
			ClearStateFile();
		}
		else
		{
			SaveState();
		}
		StopPerf();
		_trayIcon?.Dispose();
		System.Windows.Application.Current.Shutdown();
	}

	private void BrowseModel_Click(object s, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Filter = "GGUF|*.gguf"
		};
		if (openFileDialog.ShowDialog() == true)
		{
			InputModelPath.Text = openFileDialog.FileName;
		}
	}

	private void BrowseMtp_Click(object s, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Filter = "GGUF|*.gguf"
		};
		if (openFileDialog.ShowDialog() == true)
		{
			InputMtpPath.Text = openFileDialog.FileName;
		}
	}

	private void BrowseLlama_Click(object s, RoutedEventArgs e)
	{
		FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog();
		if (folderBrowserDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
		{
			InputLlamaDir.Text = folderBrowserDialog.SelectedPath;
		}
	}

	private void BrowseDsh_Click(object s, RoutedEventArgs e)
	{
		FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog();
		if (folderBrowserDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
		{
			InputDshDir.Text = folderBrowserDialog.SelectedPath;
		}
	}

	private void SearchLlama_Click(object s, RoutedEventArgs e)
	{
		System.Windows.Controls.Button b = (System.Windows.Controls.Button)s;
		b.IsEnabled = false;
		b.Content = "⏳ 检索中...";
		Task.Run(delegate
		{
			string r = FileSearcher.FindLlamaServer(delegate(string m)
			{
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					Log(m);
				}, Array.Empty<object>());
			});
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				b.IsEnabled = true;
				b.Content = "\ud83d\udd0d 自动检索";
				if (r != null)
				{
					InputLlamaDir.Text = r;
					Log("Found:" + r);
				}
				else
				{
					Log("Not found");
				}
			}, Array.Empty<object>());
		});
	}

	private void SearchDsh_Click(object s, RoutedEventArgs e)
	{
		System.Windows.Controls.Button b = (System.Windows.Controls.Button)s;
		b.IsEnabled = false;
		b.Content = "⏳ 检索中...";
		Task.Run(delegate
		{
			string r = FileSearcher.FindDshProject(delegate(string m)
			{
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					Log(m);
				}, Array.Empty<object>());
			});
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				b.IsEnabled = true;
				b.Content = "\ud83d\udd0d 自动检索";
				if (r != null)
				{
					InputDshDir.Text = r;
					Log("Found:" + r);
				}
				else
				{
					Log("Not found");
				}
			}, Array.Empty<object>());
		});
	}

	private void DshModeChanged(object s, SelectionChangedEventArgs e)
	{
		if (InputDshDir != null && BtnSearchDsh != null)
		{
			bool isEnabled = ComboDshMode.SelectedIndex == 0;
			InputDshDir.IsEnabled = isEnabled;
			BtnSearchDsh.IsEnabled = isEnabled;
		}
	}

	private void WireLiveRefresh()
	{
		InputModelPath.TextChanged += delegate
		{
			AutoFill(InputModelPath.Text);
			RefreshCmd();
		};
		InputMtpPath.TextChanged += delegate
		{
			RefreshCmd();
		};
		InputLlamaDir.TextChanged += delegate
		{
			RefreshCmd();
		};
		InputDshDir.TextChanged += delegate
		{
			RefreshCmd();
		};
	}

	private void AutoFill(string mp)
	{
		if (_pw.TryGetValue("alias", out (System.Windows.Controls.CheckBox, System.Windows.Controls.Control) value))
		{
			string text = (string.IsNullOrWhiteSpace(mp) ? "" : Path.GetFileNameWithoutExtension(mp.Trim().Trim('"')));
			if (value.Item2 is System.Windows.Controls.TextBox textBox && textBox.Text != text)
			{
				textBox.Text = text;
			}
		}
	}

	private List<string> BuildCmd()
	{
		string text = InputLlamaDir.Text.Trim().Trim('"');
		if (string.IsNullOrEmpty(text))
		{
			return new List<string>();
		}
		DirectoryInfo directoryInfo = new DirectoryInfo(text);
		FileInfo fileInfo = (directoryInfo.Exists ? new FileInfo(Path.Combine(directoryInfo.FullName, "llama-server.exe")) : ((!string.IsNullOrEmpty(Path.GetExtension(text))) ? new FileInfo(text) : new FileInfo(text + ".exe")));
		if (!fileInfo.Exists)
		{
			throw new FileNotFoundException($"llama-server.exe not found:{fileInfo}");
		}
		List<string> list = new List<string> { fileInfo.FullName };
		string text2 = Cl(InputModelPath.Text);
		if (!string.IsNullOrEmpty(text2))
		{
			list.AddRange(new string[2] { "--model", text2 });
		}
		string text3 = Cl(InputMtpPath.Text);
		if (!string.IsNullOrEmpty(text3))
		{
			list.AddRange(new string[2] { "--model-draft", text3 });
		}
		foreach (ParamDefinition paramDef in _paramDefs)
		{
			string flag = paramDef.Flag;
			if ((flag == "model" || flag == "model-draft") ? true : false)
			{
				continue;
			}
			(System.Windows.Controls.CheckBox, System.Windows.Controls.Control) value2;
			if (paramDef.Type == ParamType.Bool)
			{
				if (_pbw.TryGetValue(paramDef.Flag, out System.Windows.Controls.CheckBox value) && value.IsChecked == true)
				{
					list.Add("--" + paramDef.Flag);
				}
			}
			else if (_pw.TryGetValue(paramDef.Flag, out value2) && value2.Item1.IsChecked == true)
			{
				string text4 = ((value2.Item2 is System.Windows.Controls.ComboBox comboBox) ? comboBox.Text.Trim() : ((System.Windows.Controls.TextBox)value2.Item2).Text.Trim());
				if (paramDef.Flag == "ctx-size")
				{
					text4 = NormTok(text4);
				}
				if (!string.IsNullOrEmpty(text4))
				{
					list.AddRange(new string[2]
					{
						"--" + paramDef.Flag,
						text4
					});
				}
			}
		}
		return list;
		static string Cl(string s)
		{
			s = s.Trim();
			int num2;
			if (s.Length >= 2)
			{
				char num = s[0];
				string text5 = s;
				num2 = ((num == text5[text5.Length - 1]) ? 1 : 0);
			}
			else
			{
				num2 = 0;
			}
			bool flag2 = (byte)num2 != 0;
			if (flag2)
			{
				char c = s[0];
				bool flag3 = ((c == '"' || c == '\'') ? true : false);
				flag2 = flag3;
			}
			if (flag2)
			{
				string text6 = s;
				return text6.Substring(1, text6.Length - 1 - 1);
			}
			return s;
		}
	}

	private static string NormTok(string v)
	{
		string text = v.Trim();
		if (string.IsNullOrEmpty(text))
		{
			return text;
		}
		Match match = Regex.Match(text, "^(\\d+(?:\\.\\d+)?)\\s*([KkMm]?)$");
		if (!match.Success)
		{
			return text;
		}
		double num = double.Parse(match.Groups[1].Value);
		string text2 = match.Groups[2].Value.ToLowerInvariant();
		if (text2 == "k")
		{
			num *= 1024.0;
		}
		else if (text2 == "m")
		{
			num *= 1048576.0;
		}
		return ((int)Math.Round(num)).ToString();
	}

	private bool TryGetParamValue(string flag, out string value)
	{
		value = "";
		if (_pw.TryGetValue(flag, out (System.Windows.Controls.CheckBox, System.Windows.Controls.Control) pair) && pair.Item1.IsChecked == true)
		{
			value = (pair.Item2 is System.Windows.Controls.ComboBox cb) ? cb.Text.Trim() : ((System.Windows.Controls.TextBox)pair.Item2).Text.Trim();
			return true;
		}
		return false;
	}

	private static double CacheBytesPerElem(string cacheType)
	{
		switch (cacheType.Trim().ToLowerInvariant())
		{
		case "f16":
			return 2.0;
		case "q8_0":
			return 1.0625;
		case "q4_0":
		case "iq4_nl":
			return 0.5625;
		case "q4_1":
			return 0.625;
		case "q5_0":
			return 0.6875;
		case "q5_1":
			return 0.75;
		case "q6_0":
			return 0.875;
		default:
			return 2.0;
		}
	}

	private sealed class GgufArchInfo
	{
		public bool Ok;
		public int Layers;
		public int KvHeads;
		public int HeadDim;
		public long ContextLen;
		public string Architecture = "";
	}

	private static GgufArchInfo ReadGgufArch(string modelPath)
	{
		GgufArchInfo r = new GgufArchInfo();
		try
		{
			using FileStream fs = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			using BinaryReader br = new BinaryReader(fs);
			if (br.ReadUInt32() != 0x46554747u)
			{
				return r;
			}
			_ = br.ReadUInt32();
			_ = br.ReadUInt64();
			ulong kvCount = br.ReadUInt64();
			int embed = 0;
			int heads = 0;
			for (ulong i = 0; i < kvCount; i++)
			{
				string key = ReadGgufString(br);
				uint vt = br.ReadUInt32();
				switch (vt)
				{
				case 4:
				case 5:
				{
					uint v = br.ReadUInt32();
					if (key.EndsWith(".block_count", StringComparison.Ordinal))
					{
						r.Layers = (int)v;
					}
					else if (key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal))
					{
						r.KvHeads = (int)v;
					}
					else if (key.EndsWith(".attention.head_count", StringComparison.Ordinal))
					{
						heads = (int)v;
					}
					else if (key.EndsWith(".attention.key_length", StringComparison.Ordinal))
					{
						r.HeadDim = (int)v;
					}
					else if (key.EndsWith(".attention.value_length", StringComparison.Ordinal) && r.HeadDim == 0)
					{
						r.HeadDim = (int)v;
					}
					else if (key.EndsWith(".embedding_length", StringComparison.Ordinal))
					{
						embed = (int)v;
					}
					else if (key.EndsWith(".context_length", StringComparison.Ordinal))
					{
						r.ContextLen = v;
					}
					break;
				}
				case 10:
				case 11:
				{
					ulong v = br.ReadUInt64();
					if (key.EndsWith(".context_length", StringComparison.Ordinal))
					{
						r.ContextLen = (long)v;
					}
					break;
				}
				case 8:
				{
					string v = ReadGgufString(br);
					if (key == "general.architecture")
					{
						r.Architecture = v;
					}
					break;
				}
				default:
					SkipGgufValue(br, vt);
					break;
				}
			}
			if (r.HeadDim == 0 && heads > 0 && embed > 0)
			{
				r.HeadDim = (int)Math.Round((double)embed / heads);
			}
			if (r.Layers > 0 && r.KvHeads > 0 && r.HeadDim > 0)
			{
				r.Ok = true;
			}
		}
		catch
		{
			r.Ok = false;
		}
		return r;
	}

	private static string ReadGgufString(BinaryReader br)
	{
		ulong len = br.ReadUInt64();
		if (len > 4194304UL)
		{
			throw new InvalidDataException("bad gguf string length");
		}
		return System.Text.Encoding.UTF8.GetString(br.ReadBytes((int)len));
	}

	private static void SkipGgufValue(BinaryReader br, uint type)
	{
		switch (type)
		{
		case 0:
		case 1:
		case 7:
			br.BaseStream.Position += 1;
			break;
		case 2:
		case 3:
			br.BaseStream.Position += 2;
			break;
		case 4:
		case 5:
		case 6:
			br.BaseStream.Position += 4;
			break;
		case 8:
			_ = ReadGgufString(br);
			break;
		case 9:
		{
			uint elemType = br.ReadUInt32();
			ulong count = br.ReadUInt64();
			for (ulong j = 0; j < count; j++)
			{
				SkipGgufValue(br, elemType);
			}
			break;
		}
		case 10:
		case 11:
		case 12:
			br.BaseStream.Position += 8;
			break;
		default:
			throw new InvalidDataException("unknown gguf value type: " + type);
		}
	}

	private (double estBytes, double modelBytes, double kvBytes, double availBytes, string detail)? EstimateVramNeeded()
	{
		try
		{
			string modelPath = InputModelPath.Text.Trim().Trim('"');
			if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
			{
				return null;
			}
			double modelBytes = new FileInfo(modelPath).Length;
			if (modelBytes <= 0.0)
			{
				return null;
			}
			long ctx = 0;
			if (TryGetParamValue("ctx-size", out string ctxStr))
			{
				ctxStr = NormTok(ctxStr);
				if (!long.TryParse(ctxStr, out ctx))
				{
					ctx = 0;
				}
			}
			string ck = TryGetParamValue("cache-type-k", out string ckv) ? ckv : "f16";
			string cv = TryGetParamValue("cache-type-v", out string cvv) ? cvv : "f16";
			double bytesK = CacheBytesPerElem(ck);
			double bytesV = CacheBytesPerElem(cv);
			GgufArchInfo gi = ReadGgufArch(modelPath);
			double gpuFrac = 1.0;
			if (TryGetParamValue("n-gpu-layers", out string ngl) && double.TryParse(ngl, out double nglV) && nglV >= 0.0 && gi.Ok && gi.Layers > 0)
			{
				gpuFrac = Math.Clamp(nglV / gi.Layers, 0.0, 1.0);
			}
			double kvPerToken;
			if (gi.Ok)
			{
				kvPerToken = (double)gi.KvHeads * gi.HeadDim * (bytesK + bytesV) * gi.Layers;
			}
			else
			{
				kvPerToken = 64.0 * 8.0 * 128.0 * (bytesK + bytesV);
			}
			double kvBytes = kvPerToken * ctx * gpuFrac;
			double bufferBytes = 1.5 * 1073741824.0;
			double estVram = modelBytes * gpuFrac + kvBytes + bufferBytes;
			double total = 0.0;
			double used = 0.0;
			Dictionary<string, string> g = _gpuMonitor.QueryGpu();
			if (g.TryGetValue("vram_total", out string ts) && double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out total) && total > 0.0)
			{
				if (g.TryGetValue("vram_used", out string us))
				{
					double.TryParse(us, NumberStyles.Float, CultureInfo.InvariantCulture, out used);
				}
			}
			double avail = Math.Max(0.0, total - used);
			long safeCtx = 0;
			if (kvPerToken > 0.0 && avail > 0.0)
			{
				safeCtx = (long)((avail - modelBytes * gpuFrac - bufferBytes) / kvPerToken);
				if (safeCtx < 0)
				{
					safeCtx = 0;
				}
				safeCtx = (safeCtx / 1024L) * 1024L;
			}
			string detail = "模型文件: " + (modelBytes / 1073741824.0).ToString("F1") + " GiB\n"
				+ "上下文: " + ctx + " token (" + (ctx / 1024L) + "K)\n"
				+ "KV 缓存(按当前缓存类型估算): " + (kvBytes / 1073741824.0).ToString("F2") + " GiB\n"
				+ "预计总占用: " + (estVram / 1073741824.0).ToString("F1") + " GiB\n"
				+ "可用显存: " + (avail / 1073741824.0).ToString("F1") + " GiB";
			if (safeCtx > 0)
			{
				detail += "\n建议: 当前显存下安全上下文约 " + safeCtx + " token (" + (safeCtx / 1024L) + "K)";
			}
			return (estVram, modelBytes, kvBytes, avail, detail);
		}
		catch
		{
			return null;
		}
	}

	private bool ConfirmVramFit()
	{
		(double, double, double, double, string)? est = EstimateVramNeeded();
		if (!est.HasValue)
		{
			return true;
		}
		(double estBytes, double modelBytes, double kvBytes, double availBytes, string detail) = est.Value;
		if (availBytes <= 0.0)
		{
			return true;
		}
		if (estBytes <= availBytes)
		{
			return true;
		}
		string msg = "⚠ 预计显存不足，llama-server 很可能因显存耗尽而崩溃（bad allocation / GGML_ASSERT）。\n\n"
			+ detail
			+ "\n\n仍要尝试启动吗？\n（建议：减小“上下文大小”，或将 KV 缓存类型改为 q4_0/q4_1）";
		System.Windows.MessageBoxResult r = System.Windows.MessageBox.Show(msg, "显存可能不足", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
		return r == System.Windows.MessageBoxResult.Yes;
	}

	private static string FmtCmd(List<string> cl)
	{
		if (cl.Count == 0)
		{
			return "";
		}
		List<string> list = new List<string> { QA(cl[0]) };
		int num = 1;
		while (num < cl.Count)
		{
			string text = cl[num];
			string text2 = QA(text);
			if (text.StartsWith("--") && num + 1 < cl.Count && !cl[num + 1].StartsWith("--"))
			{
				list.Add("    " + text2 + " " + QA(cl[num + 1]));
				num += 2;
			}
			else
			{
				list.Add("    " + text2);
				num++;
			}
		}
		return string.Join(Environment.NewLine, list);
	}

	private static string QA(string a)
	{
		if (a.Contains(' ') || a.Contains('"'))
		{
			return "\"" + a.Replace("\"", "\\\"") + "\"";
		}
		return a;
	}

	private void RefreshCmd()
	{
		try
		{
			List<string> list = BuildCmd();
			_lastCmd = string.Join(" ", list.Select(QA));
			CmdPreview.Text = FmtCmd(list);
		}
		catch (Exception ex)
		{
			_lastCmd = "";
			CmdPreview.Text = "Error:" + ex.Message;
			LogError(ex, "命令构建失败");
		}
	}

	private void RefreshCmd_Click(object s, RoutedEventArgs e)
	{
		RefreshCmd();
	}

	private void CopyCmd_Click(object s, RoutedEventArgs e)
	{
		string text = CmdPreview.Text;
		if (!string.IsNullOrEmpty(text))
		{
			System.Windows.Clipboard.SetText(text);
			Log("Copied");
		}
	}

	private void StartLlama_Click(object s, RoutedEventArgs e)
	{
		StartLlama();
	}

	private void StopLlama_Click(object s, RoutedEventArgs e)
	{
		StopLlama();
	}

	private void StartLlama()
	{
		//IL_0224: Unknown result type (might be due to invalid IL or missing references)
		//IL_0229: Unknown result type (might be due to invalid IL or missing references)
		//IL_0242: Expected O, but got Unknown
		if (_llamaProc != null && !_llamaProc.HasExited)
		{
			System.Windows.MessageBox.Show("Already running");
			return;
		}
		_vramBreakerTriggered = false;
		double result;
		while (_tpsSamples.TryDequeue(out result))
		{
		}
		while (_ttftSamples.TryDequeue(out result))
		{
		}
		while (_pfSamples.TryDequeue(out result))
		{
		}
		_perfLineBuf = "";
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			PerfPrefill.Text = "—";
			PerfTps.Text = "—";
			PerfTtft.Text = "—";
		}, Array.Empty<object>());
		List<string> list;
		try
		{
			list = BuildCmd();
		}
		catch (Exception ex)
		{
			LogError(ex, "命令构建失败");
			System.Windows.MessageBox.Show("Build failed:" + ex.Message);
			return;
		}
		if (list.Count == 0)
		{
			System.Windows.MessageBox.Show("Empty");
			return;
		}
		if (!ConfirmVramFit())
		{
			Log("--- 启动已取消：预计显存不足 ---");
			return;
		}
		try
		{
			ProcessStartInfo processStartInfo = new ProcessStartInfo
			{
				FileName = list[0],
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			for (int num = 1; num < list.Count; num++)
			{
				processStartInfo.ArgumentList.Add(list[num]);
			}
			processStartInfo.Environment["GGML_CUDA_GRAPH_OPT"] = "1";
			_llamaProc = Process.Start(processStartInfo);
			if (_llamaProc == null)
			{
				throw new Exception("● 启动失败");
			}
			SaveState();
		}
		catch (Exception ex2)
		{
			LogError(ex2, "Llama 启动失败");
			return;
		}
		PumpProcessStreams(_llamaProc, "[llama] ", delegate
		{
			OnLlamaDone();
		});
		BtnLlamaStart.IsEnabled = false;
		BtnLlamaStop.IsEnabled = true;
		LblLlamaStatus.Text = "● 启动中...";
		LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 136, 62));
		Log("--- 正在启动 Llama ---\n" + string.Join(" ", list.Select(QA)));
		DispatcherTimer vt = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(3.0)
		};
		vt.Tick += delegate
		{
			vt.Stop();
			VerifyLlama();
		};
		vt.Start();
	}

	private void VerifyLlama()
	{
		if (_llamaProc != null)
		{
			if (_llamaProc.HasExited)
			{
				Log("Llama failed");
				DisposeProc(ref _llamaProc);
				BtnLlamaStart.IsEnabled = true;
				BtnLlamaStop.IsEnabled = false;
				LblLlamaStatus.Text = "● 启动失败";
				LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 54, 51));
				ClearStateFor("llama");
			}
			else
			{
				LblLlamaStatus.Text = "● 运行中";
				LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 185, 80));
			}
		}
	}

	private void OnLlamaDone()
	{
		if (_llamaProc != null && _llamaProc.HasExited)
		{
			int exitCode = 0;
			try
			{
				exitCode = _llamaProc.ExitCode;
			}
			catch
			{
			}
			DisposeProc(ref _llamaProc);
			BtnLlamaStart.IsEnabled = true;
			BtnLlamaStop.IsEnabled = false;
			if (exitCode != 0)
			{
				LblLlamaStatus.Text = "● 启动失败/崩溃";
				LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 54, 51));
				Log("--- Llama 异常退出 (退出码 " + exitCode + ") --- 请查看上方日志，通常是显存不足或参数错误导致 ---");
			}
			else
			{
				LblLlamaStatus.Text = "● 已停止";
				LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));
				Log("--- Llama 已退出 ---");
			}
			ClearStateFor("llama");
		}
	}

	private void StopLlama()
	{
		if (_llamaProc != null)
		{
			Log("--- 正在停止 Llama ---");
			ProcessHelper.KillProcessTree(_llamaProc.Id);
			DisposeProc(ref _llamaProc);
			BtnLlamaStart.IsEnabled = true;
			BtnLlamaStop.IsEnabled = false;
			LblLlamaStatus.Text = "● 已停止";
			LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));
			Log("--- 已停止 ---");
			ClearStateFor("llama");
		}
	}

	private void StartDsh_Click(object s, RoutedEventArgs e)
	{
		int? existing = ProcessHelper.FindProcessByPort(3080);
		if (existing.HasValue)
		{
			_dshRetriesLeft = 0;
			_dshProc = null;
			_dshTracker = null;
			AttachDsh(existing.Value, "检测到端口 3080 已有 DSH 在运行，直接接管", saveState: true);
			return;
		}
		_dshRetriesLeft = 2;
		StartDsh();
	}

	private void StopDsh_Click(object s, RoutedEventArgs e)
	{
		StopDsh();
	}

	private void StartDsh()
	{
		if (HasDsh())
		{
			System.Windows.MessageBox.Show("Already running");
			return;
		}
		try
		{
			string text2;
			string text3;
			string dshDir = "";
			if (ComboDshMode.SelectedIndex == 0)
			{
				dshDir = InputDshDir.Text.Trim().Trim('"');
				if (string.IsNullOrEmpty(dshDir))
				{
					dshDir = "%DSH%";
				}
				text3 = ((File.Exists(Path.Combine(dshDir, "apps", "cli", "lib", "bin.js")) && _dshRetriesLeft > 0) ? "compiled-js" : "compiled-tsx");
			}
			else
			{
				text3 = "npx";
			}
			BtnDshStart.IsEnabled = false;
			BtnDshStop.IsEnabled = true;
			BtnOpenDsh.IsEnabled = false;
			LblDshStatus.Text = "Starting...";
			LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 136, 62));
				// 优先：WMI 分离启动。进程由 WmiPrvSE.exe 服务拉起，与启动器无进程亲缘，
				// 可绕过杀软对"启动器直接派生 node"的行为拦截
				//（症状：终端/脚本里启动一切正常，启动器里 100% 报 ERR_MODULE_NOT_FOUND）
			if (ComboDshMode.SelectedIndex == 0 && TryStartDshDetached(dshDir, text3))
			{
				_dshTaskMode = true;
				_dshProc = null;
				_dshTracker = null;
				_dshVerifyDeadline = DateTime.Now.AddSeconds(30.0);
				Log("--- 正在启动 DSH (" + text3 + " · 分离模式) ---");
				StartDshVerifyLoop();
				return;
			}
			_dshTaskMode = false;
			StopDshLogTailer();
			if (ComboDshMode.SelectedIndex == 0)
			{
				if (text3 == "compiled-js")
				{
					text2 = "cd /d \"" + dshDir + "\" && node apps/cli/lib/bin.js web";
				}
				else
				{
					if (File.Exists(Path.Combine(dshDir, "apps", "cli", "lib", "bin.js")))
					{
						Log("--- 编译版连续启动失败，本次回退到 pnpm dsh web ---");
					}
					text2 = "cd /d \"" + dshDir + "\" && pnpm dsh web";
				}
			}
			else
			{
				text2 = "npx --yes @deepseek-ai/dsh web";
			}
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = "cmd.exe",
				Arguments = "/c " + text2,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			_dshProc = Process.Start(startInfo);
			if (_dshProc == null)
			{
				throw new Exception("● 启动失败");
			}
			SaveState();
			PumpProcessStreams(_dshProc, "[dsh] ", delegate
			{
				OnDshDone();
			});
			Log("--- 正在启动 DSH (" + text3 + ") ---");
			DispatcherTimer dispatcherTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromSeconds(3.0)
			};
			dispatcherTimer.Tick += delegate
			{
				dispatcherTimer.Stop();
				VerifyDsh();
			};
			dispatcherTimer.Start();
		}
		catch (Exception ex)
		{
			LogError(ex, "DSH 启动失败");
			BtnDshStart.IsEnabled = true;
			BtnDshStop.IsEnabled = false;
		}
	}

		// 通过 WMI Win32_Process.Create 分离启动 DSH：父进程是 WmiPrvSE.exe（WMI 服务宿主），
	// 与启动器完全无亲缘；node 输出重定向到日志文件，由启动器尾随显示
	private bool TryStartDshDetached(string dshDir, string modeLabel)
	{
		try
		{
			string text = ((modeLabel == "compiled-js") ? "node apps/cli/lib/bin.js web" : "pnpm dsh web");
			string text2 = Path.Combine(Path.GetTempPath(), "LlamaDSH_dsh_boot.log");
			try
			{
				File.WriteAllText(text2, "");
			}
			catch
			{
			}
			_dshBootLogPath = text2;
			bool elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
			File.AppendAllText(text2, "[diag] launcher elevated=" + elevated + " temp=" + Path.GetTempPath() + "\r\n");
			string text3 = "cmd.exe /c cd /d \"" + dshDir + "\" & echo ===CHILD-ENV=== >> \"" + text2 + "\" 2>&1 & set >> \"" + text2 + "\" 2>&1 & where node >> \"" + text2 + "\" 2>&1 & node -v >> \"" + text2 + "\" 2>&1 & echo ===BOOT=== >> \"" + text2 + "\" 2>&1 & " + text + " >> \"" + text2 + "\" 2>&1";
			using (System.Management.ManagementClass managementClass = new System.Management.ManagementClass("Win32_Process"))
			{
				System.Management.ManagementBaseObject methodParameters = managementClass.GetMethodParameters("Create");
				methodParameters["CommandLine"] = text3;
				methodParameters["CurrentDirectory"] = dshDir;
				// 显式传递环境变量：不依赖 WmiPrvSE 宿主按调用者上下文派生的环境，
				// 保证子进程拿到与用户 PowerShell 一致的环境（矩阵测试验证过该环境下启动必成功）
				try
				{
					System.Collections.IDictionary envVars = Environment.GetEnvironmentVariables();
					string[] envArray = new string[envVars.Count];
					int envIndex = 0;
					foreach (System.Collections.DictionaryEntry entry in envVars)
					{
						if (entry.Key == null || entry.Value == null) continue;
						// strip installer compat layer marker (would force-elevate every child)
						if (string.Equals((string)entry.Key, "__COMPAT_LAYER", StringComparison.OrdinalIgnoreCase)) continue;
						envArray[envIndex++] = entry.Key + "=" + entry.Value;
					}
					Array.Resize(ref envArray, envIndex);
					using (System.Management.ManagementClass startupClass = new System.Management.ManagementClass("Win32_ProcessStartup"))
					{
						System.Management.ManagementBaseObject startup = startupClass.CreateInstance();
						startup["EnvironmentVariables"] = envArray;
						methodParameters["ProcessStartupInformation"] = startup;
					}
					File.AppendAllText(text2, "[diag] explicit env entries=" + envIndex + "\r\n");
				}
				catch (Exception envEx)
				{
					File.AppendAllText(text2, "[diag] explicit env failed: " + envEx.Message + "\r\n");
				}
				System.Management.ManagementBaseObject managementBaseObject = managementClass.InvokeMethod("Create", methodParameters, null);
				if (Convert.ToInt32(managementBaseObject["ReturnValue"]) != 0)
				{
					Log("--- 分离模式创建失败 (WMI rc=" + managementBaseObject["ReturnValue"] + ")，改用直接启动 ---");
					return false;
				}
			}
			StartDshLogTailer(text2);
			return true;
		}
		catch (Exception ex)
		{
			LogError(ex, "DSH 分离启动失败，改用直接启动");
			return false;
		}
	}

	private void StartDshLogTailer(string path)
	{
		StopDshLogTailer();
		_dshTailCts = new CancellationTokenSource();
		CancellationToken token = _dshTailCts.Token;
		long pos = 0L;
		Task.Run(async delegate
		{
			while (!token.IsCancellationRequested)
			{
				try
				{
					if (File.Exists(path))
					{
						using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
						{
							if (stream.Length < pos)
							{
								pos = 0L;
							}
							if (stream.Length > pos)
							{
								stream.Seek(pos, SeekOrigin.Begin);
								using (StreamReader streamReader = new StreamReader(stream))
								{
									string text = streamReader.ReadToEnd();
									int num = text.LastIndexOf('\n');
									if (num >= 0)
									{
										string text2 = text.Substring(0, num + 1);
										pos += Encoding.UTF8.GetByteCount(text2);
										string[] array = text2.Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
										foreach (string value in array)
										{
											_logPending.Enqueue("[dsh] " + value);
										}
									}
								}
							}
						}
					}
				}
				catch
				{
				}
				try
				{
					await Task.Delay(400, token);
				}
				catch
				{
					break;
				}
			}
		}, token);
	}

	private void StopDshLogTailer()
	{
		try
		{
			_dshTailCts?.Cancel();
		}
		catch
		{
		}
		_dshTailCts = null;
	}

		// 分离模式下的启动验证：每2秒查一次端口3080；启动日志出现致命错误则快速失败
	private void StartDshVerifyLoop()
	{
		StopDshVerifyLoop();
		_dshVerifyTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(2.0)
		};
		_dshVerifyTimer.Tick += delegate
		{
			int? num = ProcessHelper.FindProcessByPort(3080);
			bool flag = false;
			if (!num.HasValue && _dshBootLogPath != null)
			{
				try
				{
					if (File.Exists(_dshBootLogPath))
					{
						string text = File.ReadAllText(_dshBootLogPath);
						if (text.Contains("ERR_MODULE_NOT_FOUND") || text.Contains("Command failed with exit code"))
						{
							flag = true;
						}
					}
				}
				catch
				{
				}
			}
			if (num.HasValue)
			{
				StopDshVerifyLoop();
				_dshTracker = new ProcessTracker(num.Value);
				LblDshStatus.Text = "● 运行中";
				LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 185, 80));
				BtnOpenDsh.IsEnabled = true;
				SaveState();
				Log("DSH real PID:" + num.Value);
			}
			else if (flag || DateTime.Now >= _dshVerifyDeadline)
			{
				StopDshVerifyLoop();
				Log(flag ? "DSH failed (启动日志报错)" : "DSH failed (启动超时)");
				StopDshLogTailer();
				_dshTracker = null;
				if (_dshRetriesLeft > 0)
				{
					_dshRetriesLeft--;
					Log("--- DSH 启动失败，自动重试（剩余 " + _dshRetriesLeft + " 次）---");
					StartDsh();
				}
				else
				{
					BtnDshStart.IsEnabled = true;
					BtnDshStop.IsEnabled = false;
					BtnOpenDsh.IsEnabled = false;
					LblDshStatus.Text = "● 启动失败";
					LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 54, 51));
					ClearStateFor("dsh");
				}
			}
		};
		_dshVerifyTimer.Start();
	}

	private void StopDshVerifyLoop()
	{
		if (_dshVerifyTimer != null)
		{
			_dshVerifyTimer.Stop();
			_dshVerifyTimer = null;
		}
	}

	private void VerifyDsh()
	{
		if (_dshProc == null)
		{
			return;
		}
		int id = _dshProc.Id;
		string text = ((ComboDshMode.SelectedIndex == 0) ? InputDshDir.Text.Trim().Trim('"') : "");
		if (string.IsNullOrEmpty(text))
		{
			text = "%DSH%";
		}
		int? num = ResolveDshPid(id, text);
		if (num.HasValue)
		{
			if (num.Value != id)
			{
				_dshTracker = new ProcessTracker(num.Value);
				SaveState();
				Log($"DSH real PID:{num.Value}");
			}
			else
			{
				_dshTracker = new ProcessTracker(id);
			}
			LblDshStatus.Text = "● 运行中";
			LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 185, 80));
			BtnOpenDsh.IsEnabled = true;
		}
		else
		{
			Log("DSH failed");
			bool dshExited = _dshProc != null && _dshProc.HasExited;
			DisposeProc(ref _dshProc);
			_dshTracker = null;
			if (dshExited && _dshRetriesLeft > 0)
			{
				_dshRetriesLeft--;
				Log("--- DSH 异常退出，自动重试（剩余 " + _dshRetriesLeft + " 次）---");
				StartDsh();
				return;
			}
			BtnDshStart.IsEnabled = true;
			BtnDshStop.IsEnabled = false;
			BtnOpenDsh.IsEnabled = false;
			LblDshStatus.Text = "● 启动失败";
			LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 54, 51));
			ClearStateFor("dsh");
		}
	}

	private void OnDshDone()
	{
		if (HasDsh())
		{
			int num = _dshProc?.Id ?? _dshTracker?.Pid ?? 0;
			string dd = ((ComboDshMode.SelectedIndex == 0) ? InputDshDir.Text.Trim().Trim('"') : "%DSH%");
			int? num2 = ResolveDshPid(num, dd);
			if (num2.HasValue && num2.Value != num)
			{
				_dshTracker = new ProcessTracker(num2.Value);
				DisposeProc(ref _dshProc);
				SaveState();
				BtnOpenDsh.IsEnabled = true;
				Log($"DSH wrapper {num}->{num2.Value}");
			}
			else
			{
				DisposeProc(ref _dshProc);
				_dshTracker = null;
				BtnDshStart.IsEnabled = true;
				BtnDshStop.IsEnabled = false;
				BtnOpenDsh.IsEnabled = false;
				LblDshStatus.Text = "● 已停止";
				LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));
				Log("--- DSH 已退出 ---");
				ClearStateFor("dsh");
			}
		}
	}

	private void StopDsh()
	{
		StopDshVerifyLoop();
		StopDshLogTailer();
		_dshTaskMode = false;
		if (_dshProc != null)
		{
			Log("--- 正在停止 DSH ---");
			ProcessHelper.KillProcessTree(_dshProc.Id);
		}
		if (_dshTracker != null)
		{
			try
			{
				ProcessHelper.KillProcessTree(_dshTracker.Pid);
			}
			catch (Exception ex)
			{
				LogError(ex, "停止 DSH 进程失败");
			}
		}
		DisposeProc(ref _dshProc);
		_dshTracker = null;
		BtnDshStart.IsEnabled = true;
		BtnDshStop.IsEnabled = false;
		BtnOpenDsh.IsEnabled = false;
		LblDshStatus.Text = "● 已停止";
		LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));
		Log("--- 已停止 DSH ---");
		ClearStateFor("dsh");
	}

	private static void DisposeProc(ref Process? p)
	{
		if (p != null)
		{
			try
			{
				p.Dispose();
			}
			catch
			{
			}
			p = null;
		}
	}

	private void OpenDsh_Click(object s, RoutedEventArgs e)
	{
		string text = "http://127.0.0.1:3080";
		Log("Open:" + text);
		Process.Start(new ProcessStartInfo(text)
		{
			UseShellExecute = true
		});
	}

	private bool HasDsh()
	{
		if (_dshProc == null || _dshProc.HasExited)
		{
			if (_dshTracker != null)
			{
				return !_dshTracker.Poll().HasValue;
			}
			return false;
		}
		return true;
	}

	private bool HasRunning()
	{
		if (_llamaProc == null || _llamaProc.HasExited)
		{
			return HasDsh();
		}
		return true;
	}

	private int? ResolveDshPid(int wp, string? dd)
	{
		int? result = ProcessHelper.FindProcessByPort(3080);
		if (result.HasValue)
		{
			return result;
		}
		foreach (int item in ProcessHelper.FindChildPids(wp))
		{
			try
			{
				using Process process = Process.GetProcessById(item);
				bool flag;
				switch (process.ProcessName.ToLowerInvariant())
				{
				case "node":
				case "node.exe":
				case "electron":
				case "electron.exe":
					flag = true;
					break;
				default:
					flag = false;
					break;
				}
				if (flag)
				{
					return item;
				}
			}
			catch
			{
			}
		}
		using (List<int>.Enumerator enumerator = ProcessHelper.FindOrphanedDshProcesses(dd).GetEnumerator())
		{
			if (enumerator.MoveNext())
			{
				return enumerator.Current;
			}
		}
		return null;
	}

	private void PumpProcessStreams(Process proc, string tag, Action onExited)
	{
		Action<StreamReader> pump = delegate(StreamReader r)
		{
			try
			{
				string text;
				while ((text = r.ReadLine()) != null)
				{
					_logPending.Enqueue(tag + text);
				}
			}
			catch
			{
			}
		};
		Task task = Task.Run(delegate
		{
			pump(proc.StandardOutput);
		});
		Task task2 = Task.Run(delegate
		{
			pump(proc.StandardError);
		});
		Task.WhenAll(task, task2).ContinueWith((Task _) => ((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)onExited, Array.Empty<object>()));
	}

	private void FlushLogs()
	{
		StringBuilder stringBuilder = new StringBuilder();
		if (!_logPending.IsEmpty)
		{
			int num = 0;
			string result;
			while (num < 400 && _logPending.TryDequeue(out result))
			{
				num++;
				string text = result.TrimEnd();
				if (text.StartsWith("[dsh] ", StringComparison.Ordinal))
				{
					string content = text.Substring("[dsh] ".Length);
					if (IsDshNoise(content))
					{
						_dshHiddenCount++;
						continue;
					}
					FlushDshHiddenSummary(stringBuilder);
					if (text.Length > 600)
					{
						text = text.Substring(0, 600) + " ……[已截断]";
					}
				}
				if (!string.IsNullOrWhiteSpace(text) && (!(text == _lastLogLine) || text.StartsWith("[Perf] Error")))
				{
					_lastLogLine = text;
					stringBuilder.AppendLine(text);
					AppendRunLog($"[{DateTime.Now:HH:mm:ss}] {text}");
					ParseTimings(text);
				}
			}
			if (_logPending.IsEmpty)
			{
				FlushDshHiddenSummary(stringBuilder);
			}
		}
		else
		{
			FlushDshHiddenSummary(stringBuilder);
		}
		if (stringBuilder.Length != 0)
		{
			LogArea.AppendText(stringBuilder.ToString());
			TruncateLogIfNeeded();
			if (ChkAutoScroll.IsChecked == true)
			{
				LogArea.ScrollToEnd();
			}
		}
	}

	private void FlushDshHiddenSummary(StringBuilder stringBuilder)
	{
		if (_dshHiddenCount <= 0)
		{
			return;
		}
		string summary = "[dsh] ……(已隐藏 " + _dshHiddenCount + " 行非日志内容)";
		_dshHiddenCount = 0;
		_lastLogLine = summary;
		stringBuilder.AppendLine(summary);
		AppendRunLog($"[{DateTime.Now:HH:mm:ss}] {summary}");
	}

	private static bool IsDshNoise(string content)
	{
		if (string.IsNullOrWhiteSpace(content))
		{
			return false;
		}
		string trimmed = content.Trim();
		if (trimmed.Length == 0)
		{
			return false;
		}
		// 错误/诊断相关行一律保留（堆栈、模块解析错误等），避免隐藏排查信息
		string lowerTrimmed = trimmed.ToLowerInvariant();
		if (trimmed.StartsWith("at ", StringComparison.Ordinal) ||
			trimmed.StartsWith("^", StringComparison.Ordinal) ||
			lowerTrimmed.Contains("node:internal") ||
			lowerTrimmed.Contains("file:///") ||
			lowerTrimmed.Contains("imported from") ||
			lowerTrimmed.Contains("cannot find") ||
			lowerTrimmed.Contains("module") ||
			lowerTrimmed.Contains("require") ||
			lowerTrimmed.Contains("resolve") ||
			lowerTrimmed.Contains("throw"))
		{
			return false;
		}
		string lower = content.ToLowerInvariant();
		if (lower.Contains("dsh web") || lower.Contains("http") || lower.Contains("127.0.0.1") ||
			lower.Contains("localhost") || lower.Contains("listen") || lower.Contains("port") ||
			lower.Contains("eaddrinuse") || lower.Contains("error") || lower.Contains("fail") ||
			lower.Contains("exception") || lower.Contains("warn") || lower.Contains("started") ||
			lower.Contains("ready") || lower.Contains("loaded") || lower.Contains("plugin") ||
			lower.Contains("exit") || lower.Contains("browser") || lower.Contains("open") ||
			lower.Contains("server") || lower.Contains("version") || lower.Contains("status") ||
			lower.Contains("web"))
		{
			return false;
		}
		return true;
	}

	private void TruncateLogIfNeeded()
	{
		if (LogArea.LineCount <= 2000)
		{
			return;
		}
		try
		{
			int characterIndexFromLineIndex = LogArea.GetCharacterIndexFromLineIndex(300);
			if (characterIndexFromLineIndex > 0)
			{
				LogArea.Text = LogArea.Text.Remove(0, characterIndexFromLineIndex);
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "日志截断失败");
			string[] source = LogArea.Text.Split('\n');
			LogArea.Text = string.Join('\n', source.TakeLast(1700));
		}
	}

	private void AppendRunLog(string line)
	{
		lock (_runLogLock)
		{
			_runLog.AppendLine(line);
			if (++_runLogCount > 5000)
			{
				string text = _runLog.ToString();
				int num = text.IndexOf('\n', text.Length / 2);
				if (num > 0)
				{
					_runLog.Clear();
					_runLog.Append(text.Substring(num + 1));
					_runLogCount = 2500;
				}
			}
		}
	}

	private void LogError(Exception ex, string context)
	{
		if (ex == null)
		{
			return;
		}
		string msg = $"{context}: {ex.GetType().Name}: {ex.Message}";
		string text = (string.IsNullOrWhiteSpace(ex.StackTrace) ? "" : ("    " + string.Join("    ", from x in ex.StackTrace.Split('\n').Take(3)
			select x.TrimEnd())));
		string text2 = msg + ((text.Length == 0) ? "" : ("\n" + text));
		AppLog.WriteError(text2);
		AppendRunLog($"[{DateTime.Now:HH:mm:ss}] [ERROR] {text2}");
		try
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				Log("[ERROR] " + msg);
			}, Array.Empty<object>());
		}
		catch
		{
		}
	}

	private void FlushRunLog()
	{
		try
		{
			lock (_runLogLock)
			{
				AppLog.WriteRun(_runLog.ToString());
				_runLog.Clear();
				_runLogCount = 0;
			}
		}
		catch
		{
		}
	}

	private void OpenLogDir_Click(object s, RoutedEventArgs e)
	{
		try
		{
			Directory.CreateDirectory(AppLog.LogDir);
			Process.Start(new ProcessStartInfo("explorer.exe", AppLog.LogDir)
			{
				UseShellExecute = true
			});
			Log("打开日志目录:" + AppLog.LogDir);
		}
		catch (Exception ex)
		{
			LogError(ex, "打开日志目录失败");
		}
	}

	private void Log(string text)
	{
		if (!string.IsNullOrWhiteSpace(text) && (!(text == _lastLogLine) || text.StartsWith("[Perf] Error")))
		{
			_lastLogLine = text;
			LogArea.AppendText(text + Environment.NewLine);
			AppendRunLog($"[{DateTime.Now:HH:mm:ss}] {text}");
			TruncateLogIfNeeded();
			if (ChkAutoScroll.IsChecked == true)
			{
				LogArea.ScrollToEnd();
			}
		}
	}

	private void ClearLog_Click(object s, RoutedEventArgs e)
	{
		LogArea.Clear();
	}

	private void ParseTimings(string text)
	{
		if (text.IndexOf("eval time", StringComparison.Ordinal) < 0)
		{
			return;
		}
		if (_perfLineBuf.Length > 0)
		{
			text = _perfLineBuf + text;
			_perfLineBuf = "";
		}
		if ((text.Contains("prompt eval time") || text.Contains("eval time")) && text.Contains("(") && !text.Contains(")"))
		{
			_perfLineBuf = text;
			return;
		}
		string[] array = text.Split('\n');
		foreach (string input in array)
		{
			Match match = _rePromptTps.Match(input);
			if (match.Success)
			{
				double num = double.Parse(match.Groups[1].Value);
				int num2 = int.Parse(match.Groups[2].Value);
				AddS(_ttftSamples, num, 20);
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Func<string>)(() => PerfTtft.Text = $"{_ttftSamples.ToArray().Average() / 1000.0:F2} s"), Array.Empty<object>());
				if (num > 0.0 && num2 > 0)
				{
					AddS(_pfSamples, (double)num2 / (num / 1000.0), 20);
					((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Func<string>)(() => PerfPrefill.Text = FT(_pfSamples.ToArray().Average()) + " tok/s"), Array.Empty<object>());
				}
			}
			else
			{
				Match match2 = _rePromptMs.Match(input);
				if (match2.Success)
				{
					AddS(_ttftSamples, double.Parse(match2.Groups[1].Value), 20);
					((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Func<string>)(() => PerfTtft.Text = $"{_ttftSamples.ToArray().Average() / 1000.0:F2} s"), Array.Empty<object>());
				}
			}
			Match match3 = _reEvalTps.Match(input);
			if (match3.Success)
			{
				AddS(_tpsSamples, double.Parse(match3.Groups[1].Value), 20);
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Func<string>)(() => PerfTps.Text = FT(_tpsSamples.ToArray().Average()) + " tok/s"), Array.Empty<object>());
				continue;
			}
			Match match4 = _reEvalRaw.Match(input);
			if (!match4.Success)
			{
				continue;
			}
			double num3 = double.Parse(match4.Groups[1].Value);
			int num4 = int.Parse(match4.Groups[2].Value);
			if (num3 > 0.0)
			{
				AddS(_tpsSamples, (double)num4 / (num3 / 1000.0), 20);
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Func<string>)(() => PerfTps.Text = FT(_tpsSamples.ToArray().Average()) + " tok/s"), Array.Empty<object>());
			}
		}
	}

	private static void AddS(ConcurrentQueue<double> q, double v, int m)
	{
		q.Enqueue(v);
		while (q.Count > m)
		{
			q.TryDequeue(out var _);
		}
	}

	private static string FT(double t)
	{
		t = Math.Max(0.0, t);
		if (!(t >= 10.0))
		{
			return (Math.Round(t * 10.0) / 10.0).ToString("F1");
		}
		return Math.Round(t).ToString();
	}

	private void StartPerfMonitor()
	{
		if (_perfCts != null)
		{
			return;
		}
		_perfCts = new CancellationTokenSource();
		_perfThread = new Thread((ThreadStart)delegate
		{
			try
			{
				_cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
				_cpuCounter.NextValue();
			}
			catch (Exception ex)
			{
				LogError(ex, "CPU 性能计数器初始化失败");
			}
			try
			{
				_memCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use");
				_memCounter.NextValue();
			}
			catch (Exception ex2)
			{
				LogError(ex2, "内存性能计数器初始化失败");
			}
			int num = 0;
			while (!_perfCts.Token.IsCancellationRequested)
			{
				try
				{
					Dictionary<string, string> d = new Dictionary<string, string>();
					try
					{
						if (_cpuCounter != null)
						{
							d["cpu"] = _cpuCounter.NextValue().ToString("F1");
						}
					}
					catch (Exception ex3)
					{
						d["cpu_err"] = ex3.Message;
					}
					try
					{
						if (_memCounter != null)
						{
							d["mem"] = _memCounter.NextValue().ToString("F1");
						}
					}
					catch (Exception ex4)
					{
						d["mem_err"] = ex4.Message;
					}
					if (num % 2 == 0)
					{
						foreach (KeyValuePair<string, string> item in _gpuMonitor.QueryGpu())
						{
							d[item.Key] = item.Value;
						}
					}
					if (IsAlive(_llamaProc) && _tpsSamples.IsEmpty && _ttftSamples.IsEmpty)
					{
						(double, double)? tuple = FetchLlamaMetrics();
						if (tuple.HasValue)
						{
							d["httptps"] = tuple.Value.Item1.ToString("F2", CultureInfo.InvariantCulture);
							d["httppf"] = tuple.Value.Item2.ToString("F2", CultureInfo.InvariantCulture);
						}
					}
					((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
					{
						OnPerf(d);
					}, Array.Empty<object>());
					num++;
				}
				catch (Exception ex5)
				{
					LogError(ex5, "性能监控异常");
				}
				Thread.Sleep(2000);
			}
		})
		{
			IsBackground = true
		};
		_perfThread.Start();
	}

	private void StopPerf()
	{
		_perfCts?.Cancel();
		_perfThread?.Join(3000);
		_perfCts = null;
		_perfThread = null;
		_gpuMonitor.StopGpuTempLoop();
		try
		{
			_cpuCounter?.Dispose();
		}
		catch (Exception ex)
		{
			LogError(ex, "释放 CPU 计数器失败");
		}
		_cpuCounter = null;
		try
		{
			_memCounter?.Dispose();
		}
		catch (Exception ex2)
		{
			LogError(ex2, "释放内存计数器失败");
		}
		_memCounter = null;
	}

	private int GetLlamaPort()
	{
		Match match = Regex.Match(_lastCmd, "--port\\s+(\\d+)");
		if (!match.Success)
		{
			return 1234;
		}
		return int.Parse(match.Groups[1].Value);
	}

	private (double tps, double prefill)? FetchLlamaMetrics()
	{
		try
		{
			string result = _http.GetStringAsync($"http://127.0.0.1:{GetLlamaPort()}/metrics").Result;
			double num = 0.0;
			double num2 = 0.0;
			double num3 = 0.0;
			double num4 = 0.0;
			string[] array = result.Split('\n');
			for (int i = 0; i < array.Length; i++)
			{
				string s = array[i].Trim();
				double v2;
				double v3;
				double v4;
				if (GetVal(s, "llamacpp:prompt_tokens_total ", out var v))
				{
					num = v;
				}
				else if (GetVal(s, "llamacpp:prompt_seconds_total ", out v2))
				{
					num2 = v2;
				}
				else if (GetVal(s, "llamacpp:tokens_predicted_total ", out v3))
				{
					num3 = v3;
				}
				else if (GetVal(s, "llamacpp:tokens_predicted_seconds_total ", out v4))
				{
					num4 = v4;
				}
			}
			double num5 = ((num4 > 0.0) ? (num3 / num4) : 0.0);
			double num6 = ((num2 > 0.0) ? (num / num2) : 0.0);
			if (num5 > 0.0 || num6 > 0.0)
			{
				return (num5, num6);
			}
		}
		catch
		{
		}
		return null;
		static bool GetVal(string text, string prefix, out double reference)
		{
			reference = 0.0;
			if (!text.StartsWith(prefix, StringComparison.Ordinal))
			{
				return false;
			}
			return double.TryParse(text.AsSpan(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out reference);
		}
	}

	private void OnPerf(Dictionary<string, string> d)
	{
		if (d.TryGetValue("cpu", out string value))
		{
			PerfCpu.Text = FP(value);
		}
		if (d.TryGetValue("mem", out string value2))
		{
			PerfMem.Text = FP(value2);
		}
		if (d.TryGetValue("gpu_util", out string value3))
		{
			if (double.TryParse(value3, out var result))
			{
				PerfGpu.Text = $"{Math.Min(100.0, Math.Max(0.0, result)):F0}%";
			}
			else
			{
				PerfGpu.Text = "N/A";
			}
		}
		string value7;
		double result4;
		if (d.TryGetValue("vram_used", out string value4) && d.TryGetValue("vram_total", out string value5))
		{
			if (double.TryParse(value4, out var result2) && double.TryParse(value5, out var result3))
			{
				double num = result2 / 1073741824.0;
				double num2 = result3 / 1073741824.0;
				double value6 = ((num2 > 0.0) ? (num / num2 * 100.0) : 0.0);
				PerfVram.Text = $"{num:F1}/{num2:F0}G";
				PerfVram.ToolTip = $"显存已用 {num:F1} / {num2:F1} GB（{value6:F1}%）";
			}
			else
			{
				PerfVram.Text = "N/A";
				PerfVram.ToolTip = null;
			}
		}
		else if (d.TryGetValue("vram_total", out value7) && double.TryParse(value7, out result4))
		{
			double value8 = result4 / 1073741824.0;
			PerfVram.Text = $"{value8:F0}G";
			PerfVram.ToolTip = $"显存总量 {value8:F1} GB";
		}
		if (d.TryGetValue("gpu_temp", out string value9))
		{
			if (double.TryParse(value9, out var result5) && result5 > 0.0 && result5 < 150.0)
			{
				d.TryGetValue("gpu_temp_src", out string value10);
				Dictionary<string, string> dictionary = new Dictionary<string, string>
				{
					["nvidia"] = "NVIDIA",
					["amd"] = "AMD",
					["intel"] = "Intel",
					["ohm"] = "Sensor",
					["acpi"] = "ACPI"
				};
				string text = ((!string.IsNullOrEmpty(value10) && dictionary.TryGetValue(value10, out var value11)) ? value11 : "");
				PerfGpuTemp.Text = $"{result5:F0}°C";
				PerfGpuTemp.ToolTip = (string.IsNullOrEmpty(text) ? null : ("温度来源：" + text));
			}
			else
			{
				PerfGpuTemp.Text = "N/A";
				PerfGpuTemp.ToolTip = null;
			}
		}
		if (d.TryGetValue("httptps", out string value12) && double.TryParse(value12, NumberStyles.Float, CultureInfo.InvariantCulture, out var result6) && result6 > 0.0)
		{
			PerfTps.Text = FT(result6) + " tok/s";
		}
		if (d.TryGetValue("httppf", out string value13) && double.TryParse(value13, NumberStyles.Float, CultureInfo.InvariantCulture, out var result7) && result7 > 0.0)
		{
			PerfPrefill.Text = FT(result7) + " tok/s";
		}
		if (d.TryGetValue("cpu_err", out string value14))
		{
			Log("[Perf] CPU error: " + value14);
		}
		if (d.TryGetValue("mem_err", out string value15))
		{
			Log("[Perf] Memory error: " + value15);
		}
		try
		{
			System.Windows.Controls.CheckBox chkVramBreaker = ChkVramBreaker;
			if (chkVramBreaker == null || chkVramBreaker.IsChecked != true || _vramBreakerTriggered || !IsAlive(_llamaProc) || !d.TryGetValue("vram_used", out string value16) || !d.TryGetValue("vram_total", out string value17) || !double.TryParse(value16, out var vUsed) || !double.TryParse(value17, out var vTotal) || !(vTotal > 0.0))
			{
				return;
			}
			double pct = vUsed / vTotal * 100.0;
			NoScrollComboBox comboVramBreakerType = ComboVramBreakerType;
			bool flag = comboVramBreakerType != null && comboVramBreakerType.SelectedIndex == 0;
			double num3 = (double.TryParse(TxtVramBreakerVal?.Text, out var result8) ? result8 : ((double)(flag ? 90 : 20)));
			bool hit = (flag ? (pct >= num3) : (vUsed / 1073741824.0 >= num3));
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				LblVramBreaker.Text = (hit ? $"⚠ {pct:F1}% ({vUsed / 1073741824.0:F1}/{vTotal / 1073741824.0:F1} GB) 已熔断" : $"{pct:F1}% 正常");
				LblVramBreaker.Foreground = System.Windows.Media.Brushes.OrangeRed;
			}, Array.Empty<object>());
			if (hit)
			{
				_vramBreakerTriggered = true;
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					Log("[熔断] 显存使用超限，自动停止 Llama 服务");
					StopLlama();
					LblVramBreaker.Text += " (已停止)";
				}, Array.Empty<object>());
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "显存熔断检查异常");
		}
		static string FP(string v)
		{
			if (double.TryParse(v, out var result9))
			{
				return $"{result9:F1}%";
			}
			return "N/A";
		}
	}

	private void HealthCheck(object? s, EventArgs e)
	{
		if (!IsAlive(_llamaProc) && _llamaProc != null)
		{
			OnLlamaDone();
		}
		if (_dshProc != null && !IsAlive(_dshProc))
		{
			int id = _dshProc.Id;
			string dd = ((ComboDshMode.SelectedIndex == 0) ? InputDshDir.Text.Trim().Trim('"') : "%DSH%");
			int? num = ResolveDshPid(id, dd);
			if (num.HasValue && num.Value != id)
			{
				_dshTracker = new ProcessTracker(num.Value);
				DisposeProc(ref _dshProc);
				SaveState();
				Log($"DSH wrapper {id}->{num.Value}");
			}
			else if (!num.HasValue)
			{
				OnDshDone();
			}
		}
		else if (_dshTracker != null && _dshTracker.Poll().HasValue)
		{
			OnDshDone();
		}
		UpdateTray();
	}

	private static bool IsAlive(Process? p)
	{
		try
		{
			return p != null && !p.HasExited;
		}
		catch
		{
			return false;
		}
	}

	private void SaveState()
	{
		StateFile stateFile = new StateFile();
		if (IsAlive(_llamaProc))
		{
			stateFile.LlamaPid = _llamaProc.Id;
		}
		if (IsAlive(_dshProc))
		{
			stateFile.DshPid = _dshProc.Id;
			stateFile.DshDir = InputDshDir.Text.Trim().Trim('"');
		}
		if (_dshTracker != null && !_dshTracker.Poll().HasValue)
		{
			stateFile.DshPid = _dshTracker.Pid;
			stateFile.DshDir = InputDshDir.Text.Trim().Trim('"');
		}
		if (stateFile.LlamaPid.HasValue || stateFile.DshPid.HasValue)
		{
			try
			{
				File.WriteAllText(_stateFile, JsonSerializer.Serialize(stateFile, new JsonSerializerOptions
				{
					WriteIndented = true
				}));
				return;
			}
			catch (Exception ex)
			{
				LogError(ex, "状态保存失败");
				return;
			}
		}
		ClearStateFile();
	}

	private void LoadState()
	{
		StateFile stateFile = null;
		if (File.Exists(_stateFile))
		{
			try
			{
				stateFile = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_stateFile));
			}
			catch (Exception ex)
			{
				LogError(ex, "读取状态文件失败");
			}
		}
		if (stateFile != null && stateFile.LlamaPid.HasValue)
		{
			try
			{
				_llamaProc = Process.GetProcessById(stateFile.LlamaPid.Value);
				BtnLlamaStart.IsEnabled = false;
				BtnLlamaStop.IsEnabled = true;
				LblLlamaStatus.Text = "● 接管运行中";
				LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 185, 80));
				Log($"Resumed Llama:{stateFile.LlamaPid.Value}");
			}
			catch (Exception ex2)
			{
				LogError(ex2, "接管 Llama 进程失败");
			}
		}
		string dd = stateFile?.DshDir ?? InputDshDir.Text.Trim().Trim('"');
		if (string.IsNullOrEmpty(dd))
		{
			dd = "%DSH%";
		}
		StateFile stRef = stateFile;
		Task.Run(delegate
		{
			try
			{
				int? pp = ProcessHelper.FindProcessByPort(3080);
				if (pp.HasValue)
				{
					((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
					{
						AttachDsh(pp.Value, "Resumed DSH", saveState: true);
					}, Array.Empty<object>());
				}
				else
				{
					StateFile stateFile2 = stRef;
					if (stateFile2 != null && stateFile2.DshPid.HasValue)
					{
						int dp = stRef.DshPid.Value;
						try
						{
							using (Process.GetProcessById(dp))
							{
								int? rp = ResolveDshPid(dp, dd);
								int tp = rp.GetValueOrDefault(dp);
								((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
								{
									AttachDsh(tp, "Resumed DSH", saveState: false);
									if (rp.HasValue && rp.Value != dp)
									{
										SaveState();
									}
								}, Array.Empty<object>());
								return;
							}
						}
						catch (Exception ex3)
						{
							LogError(ex3, "接管 DSH 进程失败");
						}
					}
					List<int> or = ProcessHelper.FindOrphanedDshProcesses(dd);
					if (or.Count > 0)
					{
						((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
						{
							AttachDsh(or[0], "Orphan DSH", saveState: false);
						}, Array.Empty<object>());
					}
					else
					{
						((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
						{
							if (_llamaProc == null && _dshProc == null && _dshTracker == null)
							{
								ClearStateFile();
							}
						}, Array.Empty<object>());
					}
				}
			}
			catch (Exception ex4)
			{
				LogError(ex4, "DSH 接管后台任务异常");
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					if (_llamaProc == null && _dshProc == null && _dshTracker == null)
					{
						ClearStateFile();
					}
				}, Array.Empty<object>());
			}
		});
	}

	private void AttachDsh(int pid, string logPrefix, bool saveState)
	{
		_dshTracker = new ProcessTracker(pid);
		BtnDshStart.IsEnabled = false;
		BtnDshStop.IsEnabled = true;
		BtnOpenDsh.IsEnabled = true;
		LblDshStatus.Text = "● 接管运行中";
		LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 185, 80));
		Log($"{logPrefix}:{pid}");
		if (saveState)
		{
			SaveState();
		}
	}

	private void ClearStateFor(string svc)
	{
		if (!File.Exists(_stateFile))
		{
			return;
		}
		try
		{
			StateFile stateFile = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_stateFile));
			if (stateFile != null)
			{
				if (svc == "llama")
				{
					stateFile.LlamaPid = null;
				}
				else if (svc == "dsh")
				{
					stateFile.DshPid = null;
				}
				if (stateFile.LlamaPid.HasValue || stateFile.DshPid.HasValue)
				{
					File.WriteAllText(_stateFile, JsonSerializer.Serialize(stateFile, new JsonSerializerOptions
					{
						WriteIndented = true
					}));
				}
				else
				{
					ClearStateFile();
				}
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "清理状态失败");
		}
	}

	private void ClearStateFile()
	{
		try
		{
			if (File.Exists(_stateFile))
			{
				File.Delete(_stateFile);
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "删除状态文件失败");
		}
	}

	private void LoadConfigDirFromFile()
	{
		try
		{
			if (File.Exists(_cfgPathFile))
			{
				ConfigPathFile configPathFile = JsonSerializer.Deserialize<ConfigPathFile>(File.ReadAllText(_cfgPathFile));
				if (!string.IsNullOrEmpty(configPathFile?.Dir) && Directory.Exists(configPathFile.Dir))
				{
					_configDir = configPathFile.Dir;
				}
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "读取配置路径文件失败");
		}
	}

	private void SaveConfigDirToFile()
	{
		try
		{
			File.WriteAllText(_cfgPathFile, JsonSerializer.Serialize(new ConfigPathFile
			{
				Dir = _configDir
			}, new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
		catch (Exception ex)
		{
			LogError(ex, "保存配置路径文件失败");
		}
	}

	private void ChangeConfigPath_Click(object s, RoutedEventArgs e)
	{
		FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog
		{
			SelectedPath = _configDir
		};
		if (folderBrowserDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
		{
			_configDir = folderBrowserDialog.SelectedPath;
			SaveConfigDirToFile();
			Log("Config dir:" + _configDir);
		}
	}

	private ProfileConfig BuildConfig()
	{
		ProfileConfig profileConfig = new ProfileConfig
		{
			ModelPath = InputModelPath.Text,
			MtpPath = InputMtpPath.Text,
			LlamaDir = InputLlamaDir.Text,
			DshDir = InputDshDir.Text,
			DshMode = ComboDshMode.SelectedIndex,
			MinimizeToTray = (ChkMinimizeToTray.IsChecked == true)
		};
		foreach (KeyValuePair<string, System.Windows.Controls.CheckBox> item3 in _pbw)
		{
			profileConfig.BoolParams[item3.Key] = item3.Value.IsChecked == true;
		}
		foreach (KeyValuePair<string, (System.Windows.Controls.CheckBox, System.Windows.Controls.Control)> item4 in _pw)
		{
			(System.Windows.Controls.CheckBox, System.Windows.Controls.Control) value = item4.Value;
			System.Windows.Controls.CheckBox item = value.Item1;
			System.Windows.Controls.Control item2 = value.Item2;
			string value2 = ((item2 is System.Windows.Controls.ComboBox comboBox) ? comboBox.Text : ((System.Windows.Controls.TextBox)item2).Text);
			profileConfig.ValueParams[item4.Key] = new ValueParamData
			{
				Enabled = (item.IsChecked == true),
				Value = value2
			};
		}
		profileConfig.CmdPreview = (string.IsNullOrEmpty(_lastCmd) ? CmdPreview.Text : _lastCmd);
		profileConfig.VramBreakerEnabled = ChkVramBreaker?.IsChecked == true;
		profileConfig.VramBreakerType = ComboVramBreakerType?.SelectedIndex ?? 0;
		double vramBreakerValue;
		if (!double.TryParse(TxtVramBreakerVal?.Text, out var result))
		{
			NoScrollComboBox comboVramBreakerType = ComboVramBreakerType;
			vramBreakerValue = ((comboVramBreakerType != null && comboVramBreakerType.SelectedIndex == 0) ? 90 : 20);
		}
		else
		{
			vramBreakerValue = result;
		}
		profileConfig.VramBreakerValue = vramBreakerValue;
		return profileConfig;
	}

	private void ApplyConfig(ProfileConfig c)
	{
		InputModelPath.Text = c.ModelPath;
		InputMtpPath.Text = c.MtpPath;
		InputLlamaDir.Text = c.LlamaDir;
		InputDshDir.Text = c.DshDir;
		ComboDshMode.SelectedIndex = c.DshMode;
		ChkMinimizeToTray.IsChecked = c.MinimizeToTray;
		if (ChkVramBreaker != null)
		{
			ChkVramBreaker.IsChecked = c.VramBreakerEnabled;
		}
		if (ComboVramBreakerType != null)
		{
			ComboVramBreakerType.SelectedIndex = c.VramBreakerType;
		}
		if (TxtVramBreakerVal != null)
		{
			TxtVramBreakerVal.Text = c.VramBreakerValue.ToString();
		}
		foreach (KeyValuePair<string, bool> boolParam in c.BoolParams)
		{
			if (_pbw.TryGetValue(boolParam.Key, out System.Windows.Controls.CheckBox value))
			{
				value.IsChecked = boolParam.Value;
			}
		}
		foreach (KeyValuePair<string, ValueParamData> valueParam in c.ValueParams)
		{
			if (_pw.TryGetValue(valueParam.Key, out (System.Windows.Controls.CheckBox, System.Windows.Controls.Control) value2))
			{
				value2.Item1.IsChecked = valueParam.Value.Enabled;
				if (value2.Item2 is System.Windows.Controls.ComboBox comboBox)
				{
					comboBox.Text = valueParam.Value.Value;
				}
				else
				{
					((System.Windows.Controls.TextBox)value2.Item2).Text = valueParam.Value.Value;
				}
			}
		}
		RefreshCmd();
	}

	private void SaveConfigFile()
	{
		string path = Path.Combine(_configDir, _configFilename);
		try
		{
			if (!Directory.Exists(_configDir))
			{
				Directory.CreateDirectory(_configDir);
			}
			File.WriteAllText(path, JsonSerializer.Serialize(new ConfigFile
			{
				Profiles = _profiles,
				CurrentProfile = _currentProfile,
				WindowWidth = base.ActualWidth,
				WindowHeight = base.ActualHeight
			}, new JsonSerializerOptions
			{
				WriteIndented = true,
				Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
			}));
		}
		catch (Exception ex)
		{
			LogError(ex, "保存配置失败");
			System.Windows.MessageBox.Show("Save failed:" + ex.Message);
		}
	}

	private void RestoreWindowSize()
	{
		try
		{
			if (File.Exists(_windowStateFile))
			{
				ConfigFile configFile = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(_windowStateFile));
				if (configFile != null && configFile.WindowWidth > 100.0 && configFile.WindowHeight > 100.0)
				{
					base.Width = Math.Min(configFile.WindowWidth, SystemParameters.PrimaryScreenWidth);
					base.Height = Math.Min(configFile.WindowHeight, SystemParameters.PrimaryScreenHeight);
				}
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "恢复窗口尺寸失败");
		}
	}

	private void SaveWindowSize()
	{
		try
		{
			if (base.WindowState != WindowState.Minimized)
			{
				File.WriteAllText(_windowStateFile, JsonSerializer.Serialize(new ConfigFile
				{
					WindowWidth = base.ActualWidth,
					WindowHeight = base.ActualHeight
				}, new JsonSerializerOptions
				{
					WriteIndented = true
				}));
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "保存窗口尺寸失败");
		}
	}

	private (Dictionary<string, ProfileConfig>, string)? LoadConfigFile()
	{
		string path = Path.Combine(_configDir, _configFilename);
		if (!File.Exists(path))
		{
			return null;
		}
		try
		{
			string json = File.ReadAllText(path);
			ConfigFile configFile = JsonSerializer.Deserialize<ConfigFile>(json);
			if (configFile == null)
			{
				return null;
			}
			if (configFile.Profiles == null || configFile.Profiles.Count == 0)
			{
				try
				{
					ProfileConfig profileConfig = JsonSerializer.Deserialize<ProfileConfig>(json);
					if (profileConfig != null)
					{
						return (new Dictionary<string, ProfileConfig> { { "Default", profileConfig } }, "Default");
					}
				}
				catch (Exception ex)
				{
					LogError(ex, "旧格式配置解析失败");
				}
			}
			return (configFile.Profiles ?? new Dictionary<string, ProfileConfig>(), configFile.CurrentProfile ?? "Default");
		}
		catch (Exception ex2)
		{
			LogError(ex2, "读取配置文件失败");
			return null;
		}
	}

	private void AutoLoadConfig()
	{
		(Dictionary<string, ProfileConfig>, string)? tuple = LoadConfigFile();
		if (!tuple.HasValue)
		{
			RefreshProfileCombo();
			return;
		}
		(Dictionary<string, ProfileConfig>, string) value = tuple.Value;
		Dictionary<string, ProfileConfig> item = value.Item1;
		string item2 = value.Item2;
		_profiles = item;
		_currentProfile = item2;
		RefreshProfileCombo();
		if (item.TryGetValue(item2, out var value2))
		{
			ApplyConfig(value2);
			Log("Loaded:" + item2);
		}
		RestoreWindowSize();
	}

	private void QuickSave_Click(object s, RoutedEventArgs e)
	{
		_profiles[_currentProfile] = BuildConfig();
		SaveConfigFile();
		Log("Saved:" + _currentProfile);
	}

	private void SaveAs_Click(object s, RoutedEventArgs e)
	{
		_profiles[_currentProfile] = BuildConfig();
		Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
		{
			FileName = _configFilename,
			InitialDirectory = _configDir,
			Filter = "JSON|*.json"
		};
		if (saveFileDialog.ShowDialog() == true)
		{
			try
			{
				File.WriteAllText(saveFileDialog.FileName, JsonSerializer.Serialize(new ConfigFile
				{
					Profiles = _profiles,
					CurrentProfile = _currentProfile,
					WindowWidth = base.ActualWidth,
					WindowHeight = base.ActualHeight
				}, new JsonSerializerOptions
				{
					WriteIndented = true,
					Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
				}));
				Log("Exported:" + saveFileDialog.FileName);
			}
			catch (Exception ex)
			{
				LogError(ex, "导出配置失败");
				System.Windows.MessageBox.Show("Export failed:" + ex.Message);
			}
		}
	}

	private void LoadConfig_Click(object s, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			InitialDirectory = _configDir,
			Filter = "JSON|*.json"
		};
		if (openFileDialog.ShowDialog() != true)
		{
			return;
		}
		try
		{
			string json = File.ReadAllText(openFileDialog.FileName);
			Dictionary<string, ProfileConfig> dictionary;
			string text;
			try
			{
				ConfigFile? configFile = JsonSerializer.Deserialize<ConfigFile>(json);
				dictionary = configFile?.Profiles ?? new Dictionary<string, ProfileConfig>();
				text = configFile?.CurrentProfile ?? dictionary.Keys.FirstOrDefault() ?? "Default";
			}
			catch (Exception ex)
			{
				LogError(ex, "旧格式配置解析失败");
				ProfileConfig value = JsonSerializer.Deserialize<ProfileConfig>(json);
				dictionary = new Dictionary<string, ProfileConfig> { 
				{
					Path.GetFileNameWithoutExtension(openFileDialog.FileName),
					value
				} };
				text = dictionary.Keys.First();
			}
			foreach (KeyValuePair<string, ProfileConfig> item in dictionary)
			{
				_profiles[item.Key] = item.Value;
			}
			_currentProfile = text;
			RefreshProfileCombo();
			if (_profiles.TryGetValue(text, out ProfileConfig value2))
			{
				ApplyConfig(value2);
			}
			Log("Loaded:" + openFileDialog.FileName);
		}
		catch (Exception ex2)
		{
			LogError(ex2, "加载配置失败");
			System.Windows.MessageBox.Show("Load failed:" + ex2.Message);
		}
	}

	private void RefreshProfileCombo()
	{
		ComboProfile.SelectionChanged -= ProfileChanged;
		ComboProfile.Items.Clear();
		foreach (string key in _profiles.Keys)
		{
			ComboProfile.Items.Add(key);
		}
		if (_profiles.ContainsKey(_currentProfile))
		{
			ComboProfile.SelectedItem = _currentProfile;
		}
		else if (ComboProfile.Items.Count > 0)
		{
			_currentProfile = ComboProfile.SelectedItem?.ToString() ?? "";
		}
		ComboProfile.SelectionChanged += ProfileChanged;
	}

	private void ProfileChanged(object s, SelectionChangedEventArgs e)
	{
		string text = ComboProfile.SelectedItem?.ToString();
		if (!string.IsNullOrEmpty(text) && _profiles.ContainsKey(text))
		{
			_currentProfile = text;
			ApplyConfig(_profiles[text]);
			Log("Switched:" + text);
		}
	}

	private void SaveProfile_Click(object s, RoutedEventArgs e)
	{
		_profiles[_currentProfile] = BuildConfig();
		SaveConfigFile();
		Log("Saved:" + _currentProfile);
	}

	private void NewProfile_Click(object s, RoutedEventArgs e)
	{
		Window dlg = new Window
		{
			Title = "新建方案",
			Width = 350.0,
			Height = 150.0,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Owner = this,
			Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 27, 34)),
			ResizeMode = ResizeMode.NoResize
		};
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(16.0)
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = "方案名称:",
			Foreground = System.Windows.Media.Brushes.White
		});
		System.Windows.Controls.TextBox textBox = new System.Windows.Controls.TextBox
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 16.0)
		};
		stackPanel.Children.Add(textBox);
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Right
		};
		System.Windows.Controls.Button button = new System.Windows.Controls.Button
		{
			Content = "确定",
			Width = 80.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
		};
		System.Windows.Controls.Button button2 = new System.Windows.Controls.Button
		{
			Content = "取消",
			Width = 80.0
		};
		button.Click += delegate
		{
			dlg.DialogResult = true;
			dlg.Close();
		};
		button2.Click += delegate
		{
			dlg.DialogResult = false;
			dlg.Close();
		};
		stackPanel2.Children.Add(button);
		stackPanel2.Children.Add(button2);
		stackPanel.Children.Add(stackPanel2);
		dlg.Content = stackPanel;
		if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(textBox.Text))
		{
			string text = textBox.Text.Trim();
			if (_profiles.ContainsKey(text))
			{
				System.Windows.MessageBox.Show("已存在同名方案", "提示");
				return;
			}
			_profiles[text] = BuildConfig();
			_currentProfile = text;
			RefreshProfileCombo();
			SaveConfigFile();
			Log("新建方案:" + text);
		}
	}

	private void RenameProfile_Click(object s, RoutedEventArgs e)
	{
		string currentProfile = _currentProfile;
		Window dlg = new Window
		{
			Title = "重命名方案",
			Width = 350.0,
			Height = 150.0,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Owner = this,
			Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 27, 34)),
			ResizeMode = ResizeMode.NoResize
		};
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(16.0)
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = "将 \"" + currentProfile + "\" 重命名为:",
			Foreground = System.Windows.Media.Brushes.White
		});
		System.Windows.Controls.TextBox textBox = new System.Windows.Controls.TextBox
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 16.0),
			Text = currentProfile
		};
		stackPanel.Children.Add(textBox);
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Right
		};
		System.Windows.Controls.Button button = new System.Windows.Controls.Button
		{
			Content = "确定",
			Width = 80.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
		};
		System.Windows.Controls.Button button2 = new System.Windows.Controls.Button
		{
			Content = "取消",
			Width = 80.0
		};
		button.Click += delegate
		{
			dlg.DialogResult = true;
			dlg.Close();
		};
		button2.Click += delegate
		{
			dlg.DialogResult = false;
			dlg.Close();
		};
		stackPanel2.Children.Add(button);
		stackPanel2.Children.Add(button2);
		stackPanel.Children.Add(stackPanel2);
		dlg.Content = stackPanel;
		if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(textBox.Text))
		{
			return;
		}
		string text = textBox.Text.Trim();
		if (!(text == currentProfile))
		{
			if (_profiles.ContainsKey(text))
			{
				System.Windows.MessageBox.Show("已存在同名方案", "提示");
				return;
			}
			_profiles[text] = _profiles[currentProfile];
			_profiles.Remove(currentProfile);
			_currentProfile = text;
			RefreshProfileCombo();
			SaveConfigFile();
			Log("重命名方案: " + currentProfile + " → " + text);
		}
	}

	private void DeleteProfile_Click(object s, RoutedEventArgs e)
	{
		if (_profiles.Count <= 1)
		{
			System.Windows.MessageBox.Show("至少需要保留一个方案", "提示");
		}
		else if (System.Windows.MessageBox.Show("确定删除方案 \"" + _currentProfile + "\"？", "确认删除", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
		{
			string currentProfile = _currentProfile;
			_profiles.Remove(_currentProfile);
			_currentProfile = _profiles.Keys.First();
			RefreshProfileCombo();
			if (_profiles.TryGetValue(_currentProfile, out ProfileConfig value))
			{
				ApplyConfig(value);
			}
			SaveConfigFile();
			Log("已删除方案: " + currentProfile + ", 当前: " + _currentProfile);
		}
	}

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
		AutoLoadConfig();
		ClampToWorkArea();
		string text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "app.ico");
		if (File.Exists(text))
		{
			try
			{
				base.Icon = new BitmapImage(new Uri(text));
			}
			catch (Exception ex)
			{
				LogError(ex, "加载窗口图标失败");
			}
		}
	}

	private void ClampToWorkArea()
	{
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			double num = Math.Max(VisualTreeHelper.GetDpi(this).DpiScaleX, 0.5);
			Rect workArea = SystemParameters.WorkArea;
			double num2 = workArea.Width / num;
			double num3 = workArea.Height / num;
			double num4 = workArea.Left / num;
			double num5 = workArea.Top / num;
			if (base.Width > num2)
			{
				base.Width = num2;
			}
			if (base.Height > num3)
			{
				base.Height = num3;
			}
			if (base.Left < num4)
			{
				base.Left = num4;
			}
			if (base.Top < num5)
			{
				base.Top = num5;
			}
			if (base.Left + base.Width > num4 + num2)
			{
				base.Left = num4 + num2 - base.Width;
			}
			if (base.Top + base.Height > num5 + num3)
			{
				base.Top = num5 + num3 - base.Height;
			}
		}
		catch (Exception ex)
		{
			AppLog.WriteError("窗口钳制到工作区失败: " + ex.Message);
		}
	}

	private void Window_Closing(object sender, CancelEventArgs e)
	{
		if (ChkMinimizeToTray.IsChecked == true && _trayIcon != null)
		{
			Hide();
			_trayIcon.ShowBalloonTip(2000, "Llama 启动器", "程序已最小化到系统托盘", ToolTipIcon.Info);
			e.Cancel = true;
			return;
		}
		bool flag = _llamaProc != null && !_llamaProc.HasExited;
		bool flag2 = HasDsh();
		bool flag3 = true;
		if (flag | flag2)
		{
			string text = "检测到以下服务正在运行：\n\n";
			if (flag)
			{
				text += "  • Llama Server\n";
			}
			if (flag2)
			{
				text += "  • DSH Web\n";
			}
			text += "\n请选择操作：\n\n  是(Y) — 停止所有服务并退出\n  否(N) — 不停止服务直接退出\n  取消 — 返回程序";
			MessageBoxResult messageBoxResult = System.Windows.MessageBox.Show(text, "确认退出", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
			if (messageBoxResult == MessageBoxResult.Cancel)
			{
				e.Cancel = true;
				return;
			}
			flag3 = messageBoxResult == MessageBoxResult.Yes;
		}
		if (flag3)
		{
			StopLlama();
			StopDsh();
			ClearStateFile();
		}
		else
		{
			SaveState();
		}
		StopPerf();
		FlushRunLog();
		_trayIcon?.Dispose();
		System.Windows.Application.Current.Shutdown();
	}

}
