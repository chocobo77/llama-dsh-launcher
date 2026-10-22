using System.Collections.Generic;

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

	public string CmdPreview { get; set; } = "";

	/// <summary>命令预览里手动追加的自定义参数（橙色显示，原样追加到 llama-server 命令行末尾）。</summary>
	public List<string> CustomArgs { get; set; } = new List<string>();

	public bool VramBreakerEnabled { get; set; }

	public int VramBreakerType { get; set; }

	public double VramBreakerValue { get; set; }
}
