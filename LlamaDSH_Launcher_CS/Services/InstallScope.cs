using System;
using System.IO;
using Microsoft.Win32;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// 本机这份启动器到底是「仅为自己安装」还是「为所有用户安装」。安装包 installer.iss 里两种装法
/// 装的是同一个程序，区别在装到哪、需不需要管理员权限，以及「以管理员身份启动」的安全含义：
///   机器级（默认 D:\Program Files）—— exe 由 ACL 保护，普通账户改不动，把它提权运行是可信的；
///   用户级（用户目录\Programs）    —— exe 当前用户随时能替换，替它开管理员令牌等于给"能改文件的人"
///                                    开一条提权通道，所以这条路径下提权必须经 UAC 确认，界面上也
///                                    不能再说"静默提权、无 UAC 弹窗"。
/// 判定顺序（拿不到标记也要能跑）：
///   ① exe 同目录的 install-scope.txt（安装程序在 ssPostInstall 写）；
///   ② 卸载注册表：InstallLocation 与当前运行目录对得上才算数（残留的旧键不能拿来判现在这份）；
///   ③ 兜底按机器级 —— 开发环境直接跑 bin 目录、或旧安装包没有标记文件时，维持原有体验，
///      不因为"认不出来"就改变用户已经习惯的行为（DecidedBy 会写明是兜底判的）。
/// </summary>
public static class InstallScope
{
	/// <summary>安装程序写在 exe 同目录的标记文件（逐行 key=value，纯 ASCII）。</summary>
	public const string MarkerFileName = "install-scope.txt";

	/// <summary>
	/// 卸载注册表子键 = installer.iss 里 [Setup] AppId 的 GUID（那边用 "{{" 转义，这边是明文）+ "_is1"。
	/// 两处只能同步改，否则"另一种安装方式已经装过"的判定会看不见对面那一份。
	/// </summary>
	private const string UninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{A7F8B3C4-D5E6-4F78-9A0B-1C2D3E4F5A6B}_is1";

	public const string Machine = "machine";
	public const string User = "user";

	public static readonly string Scope;
	public static readonly string DecidedBy;
	public static readonly string InstallRoot;

	/// <summary>
	/// 另一种安装方式在本机的安装目录（认不出则空串）。两种方式互不覆盖，控制面板里会有两条卸载项，
	/// 不提醒的话用户只会觉得"我明明卸载了怎么还在跑"。
	/// </summary>
	public static readonly string OtherScopeRoot;

	static InstallScope()
	{
		InstallRoot = ResolveInstallRoot();
		string? s = null;
		string? other = null;
		try
		{
			s = ReadMarkerScope();
			if (s != null)
			{
				DecidedBy = "标记文件";
			}
			else
			{
				s = ReadRegistryScope();
				if (s != null)
				{
					DecidedBy = "卸载注册表";
				}
			}
		}
		catch
		{
			s = null;
		}
		if (s == null)
		{
			s = Machine;
			DecidedBy = "兜底(无标记且注册表里认不出这份)";
		}
		Scope = s;
		try
		{
			// 对面那份：用户级去找 HKLM，机器级去找 HKCU
			other = ReadUninstallLocation(Scope == User ? Registry.LocalMachine : Registry.CurrentUser);
		}
		catch
		{
			other = null;
		}
		if (string.IsNullOrWhiteSpace(other))
		{
			OtherScopeRoot = "";
		}
		else
		{
			OtherScopeRoot = NormPath(other!);
			if (OtherScopeRoot.Equals(NormPath(InstallRoot), StringComparison.OrdinalIgnoreCase)) OtherScopeRoot = "";
		}
	}

	/// <summary>对面那份安装方式的中文说法（配合 OtherScopeRoot 用）。</summary>
	public static string OtherScopeLabel => IsUserScoped ? "为所有用户安装" : "仅为自己安装";

	/// <summary>true = 装在用户目录里：这份 exe 当前用户可以直接改写。</summary>
	public static bool IsUserScoped => Scope == User;

	/// <summary>界面/日志里的中文说法。</summary>
	public static string ScopeLabel => IsUserScoped ? "仅为自己安装" : "为所有用户安装";

	/// <summary>一行判定结果（含判定依据），启动日志用它，出问题能直接看出是谁判的。</summary>
	public static string Summary
		=> "方式=" + ScopeLabel + "｜目录=" + InstallRoot + "｜判定依据=" + DecidedBy;

	/// <summary>
	/// 「以管理员身份启动」的补充说明。两种安装方式都能勾，差别必须说清楚：用户级安装下提权
	/// 一定要过 UAC 确认（而且确认框里显示的路径位于用户可写目录，值得核对），机器级安装下
	/// 是否弹 UAC 只由系统的 UAC 设置决定。
	/// </summary>
	public static string ElevationNote
		=> IsUserScoped
			? "当前是「仅为自己安装」：程序位于当前用户可以改写的目录，提权必须经 UAC 确认（不是静默提权），请在确认窗口里核对程序路径。"
			: "当前是「为所有用户安装」：程序目录通常由系统 ACL 保护（安装时若自选了当前用户可写的目录，就以该目录的实际权限为准）；提权是否静默由系统 UAC 设置决定。";

	private static string ResolveInstallRoot()
	{
		// 单文件发布下 AppContext.BaseDirectory/Assembly.Location 都不可靠（见 MainWindow 里探针那段注释），
		// Environment.ProcessPath 给的才是真正被执行的那个 exe。
		try
		{
			string? exe = Environment.ProcessPath;
			if (!string.IsNullOrEmpty(exe))
			{
				string? d = Path.GetDirectoryName(exe);
				if (!string.IsNullOrEmpty(d)) return d;
			}
		}
		catch { }
		return AppContext.BaseDirectory;
	}

	private static string? ReadMarkerScope()
	{
		string path = Path.Combine(InstallRoot, MarkerFileName);
		if (!File.Exists(path)) return null;
		string[] lines;
		try
		{
			lines = File.ReadAllLines(path);
		}
		catch
		{
			return null;
		}
		foreach (string line in lines)
		{
			string l = line.Trim();
			if (!l.StartsWith("scope=", StringComparison.OrdinalIgnoreCase)) continue;
			string v = l.Substring(6).Trim().ToLowerInvariant();
			if (v == Machine || v == User) return v;
			// 值不认识(手改坏了/未来新取值)：继续往下走注册表判定，不瞎猜
			return null;
		}
		return null;
	}

	private static string? ReadRegistryScope()
	{
		if (UninstallKeyMatchesRoot(Registry.LocalMachine)) return Machine;
		if (UninstallKeyMatchesRoot(Registry.CurrentUser)) return User;
		return null;
	}

	private static bool UninstallKeyMatchesRoot(RegistryKey root)
	{
		string? loc = ReadUninstallLocation(root);
		return !string.IsNullOrWhiteSpace(loc)
			&& NormPath(loc!).Equals(NormPath(InstallRoot), StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>读某个根键里本程序的 InstallLocation（读不到/无权限一律 null，不抛）。</summary>
	private static string? ReadUninstallLocation(RegistryKey root)
	{
		try
		{
			using RegistryKey? k = root.OpenSubKey(UninstallSubKey);
			if (k == null) return null;
			string? loc = k.GetValue("InstallLocation") as string;
			if (string.IsNullOrWhiteSpace(loc)) return null;
			return loc;
		}
		catch
		{
			return null;
		}
	}

	private static string NormPath(string p)
	{
		string s = p.Trim().TrimEnd('\\', '/');
		try { s = Path.GetFullPath(s); } catch { }
		return s.TrimEnd('\\', '/');
	}
}
