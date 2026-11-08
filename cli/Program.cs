using LlamaDSH_Launcher.Services;

namespace LlamaDSH_Launcher.Cli;

/// <summary>
/// llamadsh-cli —— 控制台版 Agent 客户端: `llamadsh-cli status` / `llamadsh-cli llama start` ...
/// 逻辑全部复用 AgentCli(与 GUI 主程序内嵌的 `cli` 子命令一字不差), 这里只是把它放进
/// 控制台子系统进程, 让脚本调用能直接拿到 stdout 与退出码。
/// </summary>
internal static class Program
{
	private static int Main(string[] args)
	{
		string[] shifted = new string[args.Length + 1];
		shifted[0] = "llamadsh-cli.exe";
		System.Array.Copy(args, 0, shifted, 1, args.Length);
		return AgentCli.Run(shifted);
	}
}
