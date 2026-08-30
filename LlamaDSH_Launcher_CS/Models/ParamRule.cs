namespace LlamaDSH_Launcher.Models;

/// <summary>
/// 参数前置/条件规则。Op 取值:
///   enabled  — 目标参数处于生效状态(bool 已勾选 / 值参数已勾选且值非空)
///   disabled — 目标参数未生效
///   == != > < >= <= — 与目标参数的有效值比较(未生效时取其默认值)
/// </summary>
public class ParamRule
{
	public string Flag { get; set; } = "";

	public string Op { get; set; } = "enabled";

	public string Value { get; set; } = "";

	public string Describe()
	{
		return Op switch
		{
			"enabled" => "--" + Flag + " 已启用",
			"disabled" => "--" + Flag + " 未启用",
			_ => "--" + Flag + " " + Op + " " + Value
		};
	}
}
