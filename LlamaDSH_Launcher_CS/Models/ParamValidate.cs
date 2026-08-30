namespace LlamaDSH_Launcher.Models;

/// <summary>
/// 参数值校验规则。Min/Max 为数值边界；Leq 为另一参数的 flag，
/// 表示本参数数值必须 ≤ 该参数数值(如 ubatch-size ≤ batch-size)；Regex 为正则约束。
/// </summary>
public class ParamValidate
{
	public double? Min { get; set; }

	public double? Max { get; set; }

	public string? Leq { get; set; }

	public string? Regex { get; set; }
}
