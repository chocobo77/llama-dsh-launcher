using System.Collections.Generic;

namespace LlamaDSH_Launcher.Models;

public class ParamDefinition
{
	public string Label { get; set; } = "";

	public string Flag { get; set; } = "";

	public ParamType Type { get; set; }

	public object DefaultValue { get; set; } = "";

	public string Group { get; set; } = "";

	public bool Enabled { get; set; } = true;

	public bool IsAdvanced { get; set; }

	public List<string>? Options { get; set; }

	public string Tooltip { get; set; } = "";

	public ParamDefinition(string label, string flag, ParamType type, object defaultValue, string group, bool enabled, bool isAdvanced, List<string>? options, string tooltip)
	{
		Label = label;
		Flag = flag;
		Type = type;
		DefaultValue = defaultValue;
		Group = group;
		Enabled = enabled;
		IsAdvanced = isAdvanced;
		Options = options;
		Tooltip = tooltip;
	}
}
