using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using LlamaDSH_Launcher.Models;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// 参数生效状态查询。由 UI 层实现（读复选框/输入框），使规则引擎与命令行组装可脱离 WPF 单测。
/// </summary>
public interface IParamState
{
	/// <summary>参数是否"生效"：bool 已勾选；值参数已勾选且值非空。</summary>
	bool IsOn(ParamDefinition d);

	/// <summary>用于 ==/!= 比较的有效值：未生效时回退 offValue(如 spec-type 视为 none)，否则默认值。</summary>
	string EffectiveValue(ParamDefinition d);

	ParamDefinition? ByFlag(string flag);
}

public sealed class ParamBuildResult
{
	public List<string> Args { get; } = new List<string>();

	/// <summary>被剔除的参数及原因（前置不满足/互斥/校验失败）。</summary>
	public List<string> Violations { get; } = new List<string>();
}

/// <summary>
/// 参数依赖引擎（纯逻辑）：收集生效参数 → 逐项校验前置/互斥/取值 → 组装命令行；
/// 对配置了 off 语义（offValue/offFlag）的参数，未勾选时显式发射"关闭"值。
///
/// 关键约束（本类存在的理由）：**前置未满足的参数整条不参与命令行，连关闭值/否定旗标也不发射**。
/// 否则会出现"前置条件已取消勾选，后置参数仍被注入启动行"（如 spec-type=none 时仍发
/// --no-spec-draft-backend-sampling）。
/// </summary>
public static class ParamRules
{
	public static bool RuleMet(ParamRule r, IParamState st)
	{
		ParamDefinition? def = st.ByFlag(r.Flag);
		if (def == null)
		{
			return true;
		}
		return r.Op switch
		{
			"enabled" => st.IsOn(def),
			"disabled" => !st.IsOn(def),
			_ => CompareValues(st.EffectiveValue(def), r.Op, r.Value)
		};
	}

	public static bool CompareValues(string v, string op, string target)
	{
		bool isNumA = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double a);
		bool isNumB = double.TryParse(target, NumberStyles.Float, CultureInfo.InvariantCulture, out double b);
		int c = (isNumA && isNumB) ? a.CompareTo(b) : string.CompareOrdinal(v, target);
		return op switch
		{
			"==" => c == 0,
			"!=" => c != 0,
			">" => c > 0,
			"<" => c < 0,
			">=" => c >= 0,
			"<=" => c <= 0,
			_ => true
		};
	}

	/// <summary>全部前置条件满足？无前置视为满足；前置指向未定义参数视为满足（不误伤）。</summary>
	public static bool RequiresMet(ParamDefinition d, IParamState st)
	{
		return d.Requires == null || d.Requires.Count == 0 || d.Requires.All((ParamRule r) => RuleMet(r, st));
	}

	/// <summary>单参数依赖/互斥/校验检查。返回 null=通过，否则返回剔除原因。</summary>
	public static string? CheckParam(ParamDefinition d, HashSet<string> on, IParamState st)
	{
		if (d.Requires is { Count: > 0 })
		{
			List<ParamRule> unmet = d.Requires.Where((ParamRule r) => !RuleMet(r, st)).ToList();
			if (unmet.Count > 0)
			{
				return "前置不满足（" + string.Join(" 且 ", unmet.Select((ParamRule r) => r.Describe())) + "）";
			}
		}
		if (d.Conflicts is { Count: > 0 })
		{
			string? hit = d.Conflicts.FirstOrDefault((string c) => on.Contains(c));
			if (hit != null)
			{
				return "与 --" + hit + " 互斥";
			}
		}
		if (d.Validate != null)
		{
			string v = (d.Type == ParamType.Bool) ? "true" : st.EffectiveValue(d);
			ParamValidate val = d.Validate;
			if (d.Type != ParamType.Bool)
			{
				string vn = NormTok(v);
				bool isNum = double.TryParse(vn, NumberStyles.Float, CultureInfo.InvariantCulture, out double n);
				if ((val.Min.HasValue || val.Max.HasValue) && !isNum)
				{
					return "值 '" + v + "' 不是数字";
				}
				if (val.Min.HasValue && isNum && n < val.Min.Value)
				{
					return "值 " + vn + " 小于下限 " + val.Min.Value;
				}
				if (val.Max.HasValue && isNum && n > val.Max.Value)
				{
					return "值 " + vn + " 大于上限 " + val.Max.Value;
				}
				if (val.Leq != null)
				{
					ParamDefinition? other = st.ByFlag(val.Leq);
					if (other != null && on.Contains(other.Flag)
						&& double.TryParse(NormTok(st.EffectiveValue(other)), NumberStyles.Float, CultureInfo.InvariantCulture, out double cap)
						&& isNum && n > cap)
					{
						return "值 " + vn + " 超过 --" + other.Flag + " (" + cap + ")";
					}
				}
				if (val.Regex != null)
				{
					bool matched;
					try
					{
						matched = Regex.IsMatch(v, val.Regex);
					}
					catch (ArgumentException ex)
					{
						// 兜底：即使加载期校验没跑到，也不让坏正则把刷新/启动打断
						LlamaDSH_Launcher.AppLog.WriteError("正则无效：" + d.Flag + " -> " + val.Regex + "（" + ex.Message + "），该参数已改为不校验");
						val.Regex = null;
						matched = true;
					}
					if (!matched)
					{
						return "值 '" + v + "' 不匹配格式 " + val.Regex;
					}
				}
			}
		}
		return null;
	}

	public static HashSet<string> CollectOn(IEnumerable<ParamDefinition> defs, IParamState st)
	{
		HashSet<string> on = new HashSet<string>();
		foreach (ParamDefinition d in defs)
		{
			if (!d.Hidden && d.Flag != "model" && d.Flag != "model-draft" && st.IsOn(d))
			{
				on.Add(d.Flag);
			}
		}
		return on;
	}

	/// <summary>组装参数区命令行（不含 model/mmproj 等由 UI 专门管理的行）。</summary>
	public static ParamBuildResult Build(IEnumerable<ParamDefinition> defs, IParamState st)
	{
		List<ParamDefinition> list = defs as List<ParamDefinition> ?? defs.ToList();
		ParamBuildResult res = new ParamBuildResult();
		HashSet<string> on = CollectOn(list, st);
		foreach (ParamDefinition d in list)
		{
			if (d.Flag == "model" || d.Flag == "model-draft" || d.Hidden || !on.Contains(d.Flag))
			{
				continue;
			}
			string? reject = CheckParam(d, on, st);
			if (reject != null)
			{
				res.Violations.Add("--" + d.Flag + "（" + reject + "）");
				continue;
			}
			if (d.Type == ParamType.Bool)
			{
				res.Args.Add("--" + d.Flag);
			}
			else
			{
				string v = st.EffectiveValue(d);
				if (d.Role == "ctx_size")
				{
					v = NormTok(v);
				}
				if (!string.IsNullOrEmpty(v))
				{
					res.Args.Add("--" + d.Flag);
					res.Args.Add(v);
				}
			}
		}
		// "取消勾选=关闭"：对配置了 off 语义的参数显式发射关闭值/否定旗标。
		// 前置未满足者跳过——否则"前置已取消、后置仍被注入"（本方法的头号回归点）。
		foreach (ParamDefinition d in list)
		{
			if (d.Hidden || d.Flag == "model" || d.Flag == "model-draft" || on.Contains(d.Flag))
			{
				continue;
			}
			if (!RequiresMet(d, st))
			{
				continue;
			}
			if (d.Type == ParamType.Bool)
			{
				if (!string.IsNullOrEmpty(d.OffFlag))
				{
					res.Args.Add("--" + d.OffFlag);
				}
			}
			else if (!string.IsNullOrEmpty(d.OffValue))
			{
				res.Args.Add("--" + d.Flag);
				res.Args.Add(d.OffValue!);
			}
		}
		return res;
	}

	/// <summary>数值 token 归一化：128k / 1M → 数字字符串（ctx-size 等用）。</summary>
	public static string NormTok(string v)
	{
		string text = (v ?? "").Trim();
		if (text.Length == 0)
		{
			return text;
		}
		Match match = Regex.Match(text, "^(\\d+(?:\\.\\d+)?)\\s*([KkMm]?)$");
		if (!match.Success)
		{
			return text;
		}
		double num = double.Parse(match.Groups[1].Value);
		string unit = match.Groups[2].Value.ToLowerInvariant();
		if (unit == "k")
		{
			num *= 1024.0;
		}
		else if (unit == "m")
		{
			num *= 1048576.0;
		}
		return ((int)Math.Round(num)).ToString();
	}
}
