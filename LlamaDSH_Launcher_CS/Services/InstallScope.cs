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
/// 但**安全语气不由这三级的结论决定**：界面上"这份 exe 可不可信 / 提权要不要核对路径"来自
/// 实测的 InstallDirWritable(能不能在原地替换 exe) 与本机 UAC 事实(提权静默还是弹确认)，
/// 否则一旦落到兜底(machine)就会宣称"exe 被 ACL 保护"，而那恰恰是 exe 可写的场景。
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

	/// <summary>
	/// 程序所在目录对当前用户是否可写 —— **实测探测**出来的，不是按安装方式推断的。
	///   true  = 当前用户能在原地替换 exe(删除/改名 + 新建)，"提权运行这份 exe"不能当成可信；
	///   false = 目录对当前用户只读(ACL 保护)；
	///   null  = 探测本身没做成(按"可写"处理：认不出来时不给安全承诺)。
	/// 为什么必须由探测决定(2026-11-11 review)：三级判定认不出来时会兜底成 machine，界面于是
	/// 宣称"exe 被 ACL 保护、提权可信"，而落到兜底的恰恰全是 exe 可被当前用户替换的场景
	/// (直接跑 bin 目录、把安装目录整体复制到别处、标记丢失且注册表 InstallLocation 不匹配)。
	/// </summary>
	public static readonly bool? InstallDirWritable = ProbeInstallDirWritable();

	/// <summary>本机 UAC 策略下 runas 提权是否会静默通过(读 EnableLUA / ConsentPromptBehaviorAdmin)。</summary>
	public static readonly bool UacLikelySilent;

	/// <summary>上面那条判断的事实依据，直接进 tooltip 与日志，避免写死"本机会静默/会弹框"。</summary>
	public static readonly string UacFact;

	/// <summary>标记文件存在但被忽略的原因(正常情况下为空)。写进日志，省得事后猜"标记明明在怎么没用"。</summary>
	public static string MarkerNote { get; private set; } = "";

	static InstallScope()
	{
		ReadUacFacts(out UacLikelySilent, out UacFact);
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

	/// <summary>
	/// 一行判定结果（含判定依据 + 实测出来的目录可写性与本机 UAC 事实），启动日志用它，
	/// 出问题能直接看出是谁判的、界面那句话的依据是哪来的。
	/// </summary>
	public static string Summary
		=> "方式=" + ScopeLabel + "｜目录=" + InstallRoot + "｜判定依据=" + DecidedBy
			+ "｜目录可写=" + WritabilityLabel + "｜UAC=" + UacFact
			+ (string.IsNullOrEmpty(MarkerNote) ? "" : "｜标记文件未采用:" + MarkerNote);

	/// <summary>目录可写性的中文说法（null = 探测没做成）。</summary>
	public static string WritabilityLabel => InstallDirWritable == true ? "是" : (InstallDirWritable == false ? "否" : "未知");

	/// <summary>
	/// 「以管理员身份启动」的补充说明。两种安装方式都能勾，差别必须说清楚：用户级安装下提权
	/// 一定要过 UAC 确认（而且确认框里显示的路径位于用户可写目录，值得核对），机器级安装下
	/// 是否弹 UAC 只由系统的 UAC 设置决定。
	/// </summary>
	public static string ElevationNote
	{
		get
		{
			string head = InstallDirWritable == true
				? "实测：程序目录**当前用户可改写** → 这份 exe 可以被原地替换，替它开管理员令牌等于给「能改文件的人」开提权通道；提权前请在确认窗口里核对程序路径。"
				: (InstallDirWritable == false
					? "实测：程序目录对当前用户**只读**(ACL 保护) → 这份 exe 不是当前用户随手能换的。"
					: "无法确认程序目录的权限(探测没做成) → 按「可改写」对待，不给「提权可信」的承诺。");
			return head + "\n本机提权行为：" + UacFact
				+ (InstallDirWritable == true && UacLikelySilent
					? "（注意：这两条同时成立意味着「任何能改这个目录的人都能静默拿到管理员令牌」，建议改用「为所有用户安装」或收紧该目录权限。）"
					: "");
		}
	}

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
			MarkerNote = "读不了(" + path + ")";
			return null;
		}
		string? scope = null;
		string? dir = null;
		foreach (string line in lines)
		{
			string l = line.Trim();
			if (l.StartsWith("scope=", StringComparison.OrdinalIgnoreCase))
			{
				string v = l.Substring(6).Trim().ToLowerInvariant();
				if (v == Machine || v == User) scope = v;
				// 值不认识(手改坏了/未来新取值)：不瞎猜，继续往下走注册表判定
			}
			else if (l.StartsWith("dir=", StringComparison.OrdinalIgnoreCase))
			{
				dir = l.Substring(4).Trim();
			}
		}
		if (scope == null)
		{
			MarkerNote = "没有可识别的 scope= 行";
			return null;
		}
		// dir= 必须与这份 exe 的目录对得上：整个安装目录被人复制到别处时，标记会跟着跑，
		// 照单全收就会拿"另一处装的"的方式给现在这份定性(而且用户级/机器级的安全含义正好相反)。
		if (!string.IsNullOrWhiteSpace(dir) && !NormPath(dir!).Equals(NormPath(InstallRoot), StringComparison.OrdinalIgnoreCase))
		{
			MarkerNote = "dir=" + dir + " 与当前目录不符(标记可能是随目录被复制过来的)";
			return null;
		}
		return scope;
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

	/// <summary>
	/// 在 exe 所在目录试建一个临时文件(随即删除)来判断当前用户能否在该目录里替换文件。
	/// 只探目录、不探 exe 文件本身：正在运行的 exe 以 GENERIC_WRITE 打开必然失败(共享冲突)，
	/// 拿它当"不可写"会给出相反的错误结论；而原地替换本来就是目录级权限(DELETE/CREATE)决定的。
	/// </summary>
	private static bool? ProbeInstallDirWritable()
	{
		string dir = ResolveInstallRoot();
		string probe = Path.Combine(dir, ".llamadsh-writetest-" + Guid.NewGuid().ToString("N"));
		try
		{
			using (File.Create(probe, 1, FileOptions.None)) { }
			return true;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
		catch (Exception)
		{
			// 目录读不到/磁盘异常：不知道就按"可写"处理，不给"ACL 保护、提权可信"的承诺。
			return null;
		}
		finally
		{
			try { if (File.Exists(probe)) File.Delete(probe); } catch { }
		}
	}

	/// <summary>
	/// 读本机 UAC 策略，把"提权会不会静默通过"变成事实而不是猜测。
	/// EnableLUA=0 → 管理员不弹确认；EnableLUA=1 且 ConsentPromptBehaviorAdmin=0 → 静默通过；
	/// 其它(2/3/5 等) → 弹确认。读不到值时按"弹确认"处理(更安全)。
	/// </summary>
	private static void ReadUacFacts(out bool likelySilent, out string fact)
	{
		likelySilent = false;
		try
		{
			using RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
			if (k == null) { fact = "UAC 策略读不到(按会弹确认对待)"; return; }
			int? enableLua = ToInt(k.GetValue("EnableLUA"));
			int? cpba = ToInt(k.GetValue("ConsentPromptBehaviorAdmin"));
			if (enableLua == 0)
			{
				likelySilent = true;
				fact = "EnableLUA=0(UAC 关闭)：提权不弹确认";
				return;
			}
			if (cpba == 0)
			{
				likelySilent = true;
				fact = "EnableLUA=1 且 ConsentPromptBehaviorAdmin=0：提权静默通过，不弹确认";
				return;
			}
			if (cpba == null) { fact = "EnableLUA=1，ConsentPromptBehaviorAdmin 读不到(按会弹确认对待)"; return; }
			fact = "EnableLUA=1 且 ConsentPromptBehaviorAdmin=" + cpba.Value + "：提权会弹确认框";
		}
		catch (Exception ex)
		{
			fact = "UAC 策略读取异常(" + ex.GetType().Name + "，按会弹确认对待)";
		}
	}

	private static int? ToInt(object? v)
	{
		if (v == null) return null;
		try { return Convert.ToInt32(v); } catch { return null; }
	}
}
