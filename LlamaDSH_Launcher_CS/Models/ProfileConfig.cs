using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LlamaDSH_Launcher.Models;

public class ProfileConfig
{
	public string ModelPath { get; set; } = "";

	public string MtpPath { get; set; } = "";

	public string MmprojPath { get; set; } = "";

	public string MmprojUrl { get; set; } = "";

	public bool NoMmprojOffload { get; set; }

	public string LlamaDir { get; set; } = "";

	public string DshDir { get; set; } = "";

	public int DshMode { get; set; }

	// 以管理员身份启动 DSH（runas 静默提权，可读游戏内存）。旧 profile 无此字段时反序列化保留默认值 true。
	public bool DshAdminMode { get; set; } = true;

	public bool MinimizeToTray { get; set; }

	public Dictionary<string, bool> BoolParams { get; set; } = new Dictionary<string, bool>();

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

	public bool VramBreakerEnabled { get; set; }

	public int VramBreakerType { get; set; }

	public double VramBreakerValue { get; set; }
}
