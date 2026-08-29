namespace LlamaDSH_Launcher.Models;

public class StateFile
{
	public int? LlamaPid { get; set; }

	public int? DshPid { get; set; }

	public string DshDir { get; set; } = "";
}
