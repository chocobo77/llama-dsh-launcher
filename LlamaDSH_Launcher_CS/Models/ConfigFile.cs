using System.Collections.Generic;

namespace LlamaDSH_Launcher.Models;

public class ConfigFile
{
	public Dictionary<string, ProfileConfig> Profiles { get; set; } = new Dictionary<string, ProfileConfig>();

	public string CurrentProfile { get; set; } = "默认方案";

	public double WindowWidth { get; set; }

	public double WindowHeight { get; set; }
}
