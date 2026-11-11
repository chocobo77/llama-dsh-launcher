using System;
using System.IO;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// 进程血统自重启的判定：这个实例该不该换一次干净血统重启自己，以及最多只能重启几次。
///
/// 之所以从 App.OnStartup 里抽出来单独成类：2026-11-10「仅为自己安装」装完无限反复启动的
/// 事故就在这一段，而它当时既不可测也没有封顶。抽出后 selftest.ps1 能真的把它跑一遍
/// （假 marker 路径 + 真假兼容层值），注释里的约束才变成有测试背书的代码结构。
///
/// 三条硬约束：
///   ① 兼容层判定走**白名单**。旧写法是 `!string.IsNullOrEmpty(__COMPAT_LAYER)` 非空即认为有
///      安装器血统；「仅为自己安装」(PrivilegesRequired=lowest) 下 Setup 给子进程挂的是
///      RunAsInvoker —— 它既不提权也不影响 WMI，本不该触发切血统。
///   ② 自重启**一次封顶**。旧逻辑默认"重启后的实例判定不再成立"，这个前提依机器：explorer 是否
///      把子进程令牌滤成非提权取决于 UAC 配置（内置管理员 / EnableLUA=0 时永远为真），前提一破
///      就无限拉起。封顶走磁盘上的标记文件：凭证不能用命令行参数，因为实测
///      `explorer.exe "<exe>" 附加参数` 根本不会把程序拉起来。
///      注意 [Run] 的 runasoriginaluser **挡不住这条链**：它只换令牌、不改继承来的环境块，
///      2026-11-11 09:38 实测装完自动启动的那一份日志里仍是 compat=ElevateCreateProcess。
///      所以白名单 + 封顶才是仅有的两道保险。
///   ③ 封顶 fail-closed：凭证写不出来(或写完读不回来)就**不重启**，见 TryMarkRelaunchAttempted。
///      在"判定持续成立"的机器上封顶是唯一的下限，写失败还照样重启 = 缺陷原样复活，而且事后
///      日志里只有一句 relaunching clean，看不出封顶已经失效。
/// </summary>
public static class LineageGuard
{
	/// <summary>一次性凭证的标记文件名。</summary>
	public const string MarkerFileName = "LlamaDSH_launcher_lineage_reset.marker";

	/// <summary>
	/// 凭证有效期(秒)。超过它算过期，允许新一轮启动再重置一次。
	/// 从 60 降到 30：写凭证与读凭证之间只隔一次 explorer 拉起(实测 1-2 秒)，60 秒窗口只会
	/// 把"这 60 秒内另一次真正需要切血统的启动"也一并压掉(= 当初那个 WMI 必失败的症状回来)。
	/// 配套：被封顶的那次启动会主动 ClearRelaunchMarker，见下。
	/// </summary>
	public const int MarkerTtlSeconds = 30;

	/// <summary>
	/// 标记文件所在目录。优先 %LOCALAPPDATA%\LlamaDSH_Launcher(当前用户独占 ACL)，
	/// 建不出来才退回 %TEMP%。
	/// 不是一开始就用 %TEMP% 的理由(2026-11-11 review 实测)：本机 TEMP 被重定向到 D:\Temp，
	/// 其 ACL 是「Authenticated Users: Modify」—— 多个登录用户共享同一个可写目录，而这个
	/// marker 名字固定可预测：别人预置它就能永久压制本机其他人的血统重置，或把它做成符号链接，
	/// 让写 marker 的(往往是提权)实例把文件写到别处去。
	/// </summary>
	public static string MarkerDirectory { get; } = ResolveMarkerDirectory();

	/// <summary>一次性凭证的完整路径(调用方一般直接用这个，不必自己拼)。</summary>
	public static string MarkerPath => Path.Combine(MarkerDirectory, MarkerFileName);

	public enum Decision
	{
		/// <summary>判定不成立，正常启动。</summary>
		NoResetNeeded,

		/// <summary>判定成立且本窗口内还没重置过：允许重启一次。</summary>
		Relaunch,

		/// <summary>判定仍成立，但窗口内已经重置过一次：绝不再重启，按当前血统直接启动。</summary>
		CappedAlreadyReset,
	}

	/// <summary>
	/// 兼容层值是否代表真会改变子进程行为的安装器上下文。白名单而不是"非空即算"：
	/// RunAsInvoker 只是禁止提权，不会造成 WMI/子进程血统问题。（RunAsAdmin 那类会被
	/// elevated 判定抓到，不需要在这里点名。）
	/// </summary>
	public static bool LooksLikeInstallerLayer(string? compatLayer)
		=> compatLayer != null
			&& (compatLayer.IndexOf("Elevate", StringComparison.OrdinalIgnoreCase) >= 0
				|| compatLayer.IndexOf("InstallMonitorWritableFiles", StringComparison.OrdinalIgnoreCase) >= 0);

	/// <summary>标记文件是否在有效期内（即最近已经重置过一次）。读不到/内容不认识都算没有。</summary>
	public static bool ResetAlreadyAttempted(string markerPath)
	{
		try
		{
			if (!File.Exists(markerPath)) return false;
			long stamp;
			if (!long.TryParse(File.ReadAllText(markerPath).Trim(), out stamp)) return false;
			// 必须同一时间基准：写用 UtcNow、读也按 UTC 比。用 DateTime.Now 配 FileTimeUtc 会差出
			// 整个时区(UTC+8 ≈ 8 小时)，标记永远判成过期 → 封顶形同不存在(selftest 实测覆盖)。
			return (DateTime.UtcNow - DateTime.FromFileTimeUtc(stamp)).TotalSeconds < MarkerTtlSeconds;
		}
		catch
		{
			return false;
		}
	}

	public static Decision Decide(bool elevated, string? compatLayer, string markerPath)
	{
		if (!elevated && !LooksLikeInstallerLayer(compatLayer)) return Decision.NoResetNeeded;
		return ResetAlreadyAttempted(markerPath) ? Decision.CappedAlreadyReset : Decision.Relaunch;
	}

	/// <summary>
	/// 在真正发起重启**之前**落凭证，并回读校验。**返回 false = 凭证不可信，调用方必须放弃重启**
	/// (fail-closed)：封顶是"判定持续成立"那种机器上唯一不下无限循环的保险，写不进去还照样重启
	/// 就等于把这次修掉的缺陷原样放回去，而且日志上只看得到一句 relaunching clean，事后无从发现
	/// 封顶已经失效。失败原因通过 failureReason 回给调用方落日志。
	/// </summary>
	public static bool TryMarkRelaunchAttempted(string markerPath, out string failureReason)
	{
		failureReason = "";
		try
		{
			string? dir = Path.GetDirectoryName(markerPath);
			if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
			FileInfo fi = new FileInfo(markerPath);
			// marker 路径固定可预测，退化到共享 TEMP 时目录还可能别人可写：先拒绝 reparse point，
			// 否则这次写入会顺着符号链接写到别的位置(而此时进程往往是提权实例)。
			if (fi.Exists && (fi.Attributes & FileAttributes.ReparsePoint) != 0)
			{
				failureReason = markerPath + " 是 reparse point(符号链接/junction)，不予写入";
				return false;
			}
			// 只用 FileStream 本身写: 再包一层 StreamWriter 会在它自己 Dispose 时把底层
			// FileStream 一起关掉, 外层 using 再 Dispose 就抛 ObjectDisposedException
			// (写完的内容其实是好的, 但会被当成"写失败"而放弃切血统)。
			using (FileStream fs = fi.Open(FileMode.Create, FileAccess.Write, FileShare.None))
			{
				byte[] bytes = System.Text.Encoding.ASCII.GetBytes(DateTime.UtcNow.ToFileTimeUtc().ToString());
				fs.Write(bytes, 0, bytes.Length);
				fs.Flush(true);
			}
			// 回读校验：写进去的东西必须能被"下一次启动"认成有效凭证，否则封顶形同不存在。
			if (!ResetAlreadyAttempted(markerPath))
			{
				failureReason = "写入后回读校验失败(内容不可识别或被判过期)";
				return false;
			}
			return true;
		}
		catch (Exception ex)
		{
			failureReason = ex.GetType().Name + ": " + ex.Message;
			return false;
		}
	}

	/// <summary>
	/// 清掉凭证：由"判定仍成立但已被封顶放过"的那次启动调用。不删的话，凭证会一直躺在磁盘上只靠
	/// TTL 失效，安装完 30 秒内另一次确实带着脏血统的启动会被误判成"已经重置过"而不重置。
	/// 删除不影响"每个实例最多重启一次"这个不变量：每个实例都在重启前先自己写凭证。
	/// </summary>
	public static void ClearRelaunchMarker(string markerPath)
	{
		try { if (File.Exists(markerPath)) File.Delete(markerPath); }
		catch { }
	}

	private static string ResolveMarkerDirectory()
	{
		try
		{
			string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			if (!string.IsNullOrEmpty(local))
			{
				string dir = Path.Combine(local, "LlamaDSH_Launcher");
				Directory.CreateDirectory(dir);
				return dir;
			}
		}
		catch { }
		return Path.GetTempPath();
	}
}
