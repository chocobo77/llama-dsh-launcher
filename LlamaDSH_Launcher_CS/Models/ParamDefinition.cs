using System.Collections.Generic;

namespace LlamaDSH_Launcher.Models;

/// <summary>
/// 单个 llama.cpp 启动参数的定义。来源于 Resources\llama_params.yml（或 exe 旁同名文件 / 用户 override）。
/// 启动器内部逻辑（显存估算、ctx 归一化等）通过 Role 绑定参数，而不是硬编码 flag，
/// 这样 llama.cpp 改参数名时只需改配置文件。
/// </summary>
public class ParamDefinition
{
	public string Label { get; set; } = "";

	public string Flag { get; set; } = "";

	/// <summary>语义角色，如 ctx_size / cache_type_k / ngl / cpu_moe。启动器内部逻辑按此绑定。</summary>
	public string? Role { get; set; }

	public ParamType Type { get; set; }

	/// <summary>默认值（统一字符串；bool 参数用 "true"/"false"）。YAML 键为 default。</summary>
	[YamlDotNet.Serialization.YamlMember(Alias = "default")]
	public string DefaultValue { get; set; } = "";

	public string Group { get; set; } = "";

	/// <summary>组内子组标题（渲染时插入小节标题行，形成两级层级）。</summary>
	public string? Subgroup { get; set; }

	/// <summary>
	/// "关闭值"（枚举参数）：未勾选时显式发射 --flag &lt;offValue&gt; 而不是省略，
	/// 用于 llama 默认值不等于关闭的选项（如 flash-attn 默认 auto、fit 默认 on）。
	/// 依赖规则比较值在未勾选时同样回退到 offValue，保证"取消勾选=关闭"全链路一致。
	/// </summary>
	public string? OffValue { get; set; }

	/// <summary>否定旗标（布尔参数）：未勾选时发射它（如 --no-jinja），
	/// 用于 llama 默认启用、省略参数无法关闭的选项。</summary>
	public string? OffFlag { get; set; }

	/// <summary>值参数的启用复选框初始状态。</summary>
	public bool Enabled { get; set; }

	public bool IsAdvanced { get; set; }

	public List<string>? Options { get; set; }

	public string Tooltip { get; set; } = "";

	/// <summary>前置条件：全部满足才允许生效（UI 灰显 + 启动时剔除并确认）。</summary>
	public List<ParamRule>? Requires { get; set; }

	/// <summary>互斥参数：对方生效时本参数被剔除。</summary>
	public List<string>? Conflicts { get; set; }

	/// <summary>联动：本参数生效时自动勾选目标参数（bool 勾选 / 值参数勾启用框）。</summary>
	public List<string>? Implies { get; set; }

	/// <summary>软性建议：不阻止生效，仅在日志提示。</summary>
	public List<string>? Suggests { get; set; }

	public ParamValidate? Validate { get; set; }

	/// <summary>llama.cpp 改名后的旧 flag 列表，用于旧 profile 配置自动迁移。</summary>
	public List<string>? Aliases { get; set; }

	/// <summary>隐藏：不渲染到 UI、不参与命令组装（如 mmproj 由专用输入行管理）。</summary>
	public bool Hidden { get; set; }

	/// <summary>已废弃参数（仅提示，仍可使用）。</summary>
	public bool Deprecated { get; set; }
}
