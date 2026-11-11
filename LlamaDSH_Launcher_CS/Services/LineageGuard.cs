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
/// 两条硬约束：
///   ① 兼容层判定走**白名单**。旧写法是 `!string.IsNullOrEmpty(__COMPAT_LAYER)` 非空即认为有
///      安装器血统；「仅为自己安装」(PrivilegesRequired=lowest) 下 Setup 给子进程挂的是
///      RunAsInvoker —— 它既不提权也不影响 WMI，本不该触发切血统。
///   ② 自重启**一次封顶**。旧逻辑默认"重启后的实例判定不再成立"，这个前提依机器：explorer 是否
///      把子进程令牌滤成非提权取决于 UAC 配置（内置管理员 / EnableLUA=0 时永远为真），前提一破
///      就无限拉起。封顶走 %TEMP% 下的标记文件：凭证不能用命令行参数，因为实测
///      `explorer.exe "<exe>" 附加参数` 根本不会把程序拉起来。
/// </summary>
public static class LineageGuard
{
	/// <summary>一次性凭证的标记文件名（调用方决定放在 %TEMP% 等可写位置）。</summary>
	public const string MarkerFileName = "LlamaDSH_launcher_lineage_reset.marker";

	/// <summary>凭证有效期(秒)。超过它算过期，允许新一轮启动再重置一次。</summary>
	public const int MarkerTtlSeconds = 60;

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

	/// <summary>在真正发起重启**之前**落凭证；写失败不阻断启动（顶多少一道保险）。</summary>
	public static void MarkRelaunchAttempted(string markerPath)
	{
		try { File.WriteAllText(markerPath, DateTime.UtcNow.ToFileTimeUtc().ToString()); }
		catch { }
	}
}
