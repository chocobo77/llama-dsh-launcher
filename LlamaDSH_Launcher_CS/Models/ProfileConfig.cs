using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaDSH_Launcher.Models;

public class ProfileConfig
{
	/// <summary>
	/// 未识别字段原样收下、保存时原样写回。Windows 保存 profile 是"反序列化 → 再序列化"，
	/// 没有这一段的话 Linux 侧写的字段（如 LlamaBackend）会在 Windows
	/// 点一次保存后被静默抹掉。
	/// </summary>
	[JsonExtensionData]
	public Dictionary<string, JsonElement> Extra { get; set; } = new Dictionary<string, JsonElement>();

	public string ModelPath { get; set; } = "";

	public string MtpPath { get; set; } = "";

	public string MmprojPath { get; set; } = "";

	public string MmprojUrl { get; set; } = "";

	public bool NoMmprojOffload { get; set; }

	public string LlamaDir { get; set; } = "";

	/// <summary>
	/// llama.cpp 预编译后端: ""=按显卡自动, 可显式 vulkan/rocm/cuda/sycl。
	/// 与 Linux 侧同一个字段同名同义(那边见 state.py 默认方案), 只影响下载哪个预编译包。
	/// </summary>
	public string LlamaBackend { get; set; } = "";

	public string DshDir { get; set; } = "";

	public int DshMode { get; set; }

	// 以管理员身份启动 DSH（runas 静默提权，可读游戏内存）。旧 profile 无此字段时反序列化保留默认值 true。
	public bool DshAdminMode { get; set; } = true;

	public bool MinimizeToTray { get; set; }

	public Dictionary<string, bool> BoolParams { get; set; } = new Dictionary<string, bool>();

	// llama.cpp 变体(分支)列表与当前选中名; Dir 与 LlamaDir 冗余, 以选中项为准
	public List<LlamaVariant> LlamaVariants { get; set; } = new List<LlamaVariant>();

	public string ActiveLlamaVariant { get; set; } = "";

	public Dictionary<string, ValueParamData> ValueParams { get; set; } = new Dictionary<string, ValueParamData>();

	/// <summary>命令预览是派生数据（ApplyConfig 后由 RefreshCmd 重算），不写入配置；旧文件中的该字段读取时忽略。</summary>
	[JsonIgnore]
	public string CmdPreview { get; set; } = "";

	/// <summary>命令预览里手动追加的自定义参数（橙色显示，原样追加到 llama-server 命令行末尾）。</summary>
	public List<string> CustomArgs { get; set; } = new List<string>();

	/// <summary>
	/// 上次成功加载时 llama.cpp 自己报告的显存占用（MiB，只算设备侧）。
	/// 0 表示没测过，显存预估会退回公式粗算。换了模型就作废。
	/// </summary>
	public double VramMeasuredMiB { get; set; }

	/// <summary>上面那个实测值对应的模型路径。</summary>
	public string VramMeasuredModel { get; set; } = "";

	/// <summary>实测时的 K/V 缓存精度与上下文长度，例如 "q8_0/q8_0@65536"；变了实测值就作废。</summary>
	public string VramMeasuredFp { get; set; } = "";

	public bool VramBreakerEnabled { get; set; }

	public int VramBreakerType { get; set; }

	public double VramBreakerValue { get; set; }
}

public class LlamaVariant
{
	public string Name { get; set; } = "";

	/// <summary>GitHub 仓库(owner/repo), 检查更新按此进行。</summary>
	public string Repo { get; set; } = "ggml-org/llama.cpp";

	public string Dir { get; set; } = "";
}
