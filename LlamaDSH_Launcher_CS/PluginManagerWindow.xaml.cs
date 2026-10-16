using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LlamaDSH_Launcher.Services;

namespace LlamaDSH_Launcher;

/// <summary>
/// DSH 插件管理窗口：基于 DshCompatService.Audit 的插件清单，
/// 提供 停用并锁定/启用、升级、版本豁免、卸载、打开目录 等行内操作。
/// 修改类操作要求 DSH 已停止（pnpm 写 node_modules 期间运行会损坏环境）。
/// </summary>
public partial class PluginManagerWindow : Window
{
	private readonly string _dshDir;
	private readonly string _profile;
	private readonly Func<bool> _isDshRunning;
	private readonly Action<string>? _hostLog;
	private DshCompatReport? _report;
	private bool _busy;

	public PluginManagerWindow(string dshDir, string profile, Func<bool> isDshRunning, Action<string>? hostLog = null)
	{
		InitializeComponent();
		_dshDir = dshDir;
		_profile = profile;
		_isDshRunning = isDshRunning;
		_hostLog = hostLog;
		_ = RefreshAsync();
	}

	private void Log(string msg)
	{
		// 审计在后台线程运行, 日志须切回 UI 线程
		Dispatcher.BeginInvoke((Action)delegate
		{
			MgrLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\r\n");
			MgrLog.ScrollToEnd();
		});
	}

	private sealed class Row
	{
		public string Name { get; set; } = "";
		public string Version { get; set; } = "";
		public string StateLabel { get; set; } = "";
		public string Spec { get; set; } = "";
		public string PeerText { get; set; } = "";
		public DshPluginInfo Info { get; set; } = new();
	}

	private async Task RefreshAsync()
	{
		if (_busy)
		{
			return;
		}
		_busy = true;
		BtnRefresh.IsEnabled = false;
		TxtHeader.Text = "正在审计插件状态（含 dump-config 与探针，约十几秒）...";
		try
		{
			_report = await Task.Run(delegate
			{
				return DshCompatService.Audit(_dshDir, _profile, delegate (string m) { Log(m); });
			});
			List<Row> rows = _report.Plugins
				.OrderBy(p => p.StateLabel == "已启用" ? 1 : 0)
				.ThenBy(p => p.Name, System.StringComparer.Ordinal)
				.Select(p => new Row
				{
					Name = p.Name,
					Version = p.Version,
					StateLabel = p.StateLabel,
					Spec = p.Spec.Length > 0 ? p.Spec : (p.IsBundle ? "(bundle)" : ""),
					PeerText = string.Join("; ", p.PeerIssues.Take(2).Select(kv => kv.Key + " " + kv.Value)),
					Info = p
				})
				.ToList();
			PluginList.ItemsSource = rows;
			TxtHeader.Text = "DSH " + _report.DshVersion + " · 插件 " + rows.Count + " 个 · 错误 " + _report.ErrorCount + " · 警告 " + _report.WarningCount + " · 锁定停用 " + DshCompatService.ReadPinnedInactive(_profile).Count + " 个";
		}
		catch (System.Exception ex)
		{
			Log("审计失败: " + ex.Message);
			TxtHeader.Text = "审计失败";
		}
		finally
		{
			_busy = false;
			BtnRefresh.IsEnabled = true;
			UpdateButtons();
		}
	}

	private DshPluginInfo? Selected()
	{
		return (PluginList.SelectedItem as Row)?.Info;
	}

	private void UpdateButtons()
	{
		DshPluginInfo? p = Selected();
		bool has = p != null;
		BtnToggle.IsEnabled = has && p!.Installed;
		BtnToggle.Content = p != null && DshCompatService.ReadPinnedInactive(_profile).Contains(p.Name, System.StringComparer.Ordinal)
			? "⏻ 启用并解锁"
			: "⏻ 停用并锁定";
		BtnUpgrade.IsEnabled = has && p!.Installed && p.Spec.Length > 0 && !p.Spec.StartsWith("file:", System.StringComparison.OrdinalIgnoreCase);
		BtnExempt.IsEnabled = has && p!.Installed && p.Compatible == false && !p.Exempted;
		BtnUninstall.IsEnabled = has && p!.Installed;
		BtnOpenDir.IsEnabled = has && p!.Installed && p.Dir.Length > 0;
	}

	private void PluginList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		UpdateButtons();
	}

	private void PluginList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		DshPluginInfo? p = Selected();
		if (p != null && p.Dir.Length > 0)
		{
			OpenDir(p.Dir);
		}
	}

	private static void OpenDir(string dir)
	{
		try
		{
			Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
		}
		catch
		{
		}
	}

	private bool RequireDshStopped()
	{
		if (!_isDshRunning())
		{
			return true;
		}
		MessageBox.Show("该操作需要先停止 DSH（会运行 pnpm 修改插件目录）。", "插件管理", MessageBoxButton.OK, MessageBoxImage.Warning);
		return false;
	}

	private async void Refresh_Click(object sender, RoutedEventArgs e)
	{
		await RefreshAsync();
	}

	private async void Toggle_Click(object sender, RoutedEventArgs e)
	{
		DshPluginInfo? p = Selected();
		if (p == null || _busy)
		{
			return;
		}
		bool pinned = DshCompatService.ReadPinnedInactive(_profile).Contains(p.Name, System.StringComparer.Ordinal);
		bool activating = pinned;
		if (!activating)
		{
			MessageBoxResult confirm = MessageBox.Show(
				"停用并锁定 \"" + p.Name + "\"？\n\n将从 dsh.profile.bundles 移除并写入锁定列表：\n· 即使被 DSH 网页端插件管理/市场插件写回，启动器审计也会自动移除\n· 适合 peer 版本错位、client 模块加载失败等需要长期停用的插件",
				"停用并锁定", MessageBoxButton.YesNo, MessageBoxImage.Question);
			if (confirm != MessageBoxResult.Yes)
			{
				return;
			}
		}
		_busy = true;
		string msg = await Task.Run(delegate { return DshCompatService.SetBundleActive(_profile, p.Name, activating, delegate (string m) { Log(m); }); });
		Log("[操作] " + p.Name + " → " + msg);
		_hostLog?.Invoke("[插件管理] " + p.Name + " → " + msg);
		_busy = false;
		await RefreshAsync();
	}

	private async void Upgrade_Click(object sender, RoutedEventArgs e)
	{
		DshPluginInfo? p = Selected();
		if (p == null || _busy || !RequireDshStopped())
		{
			return;
		}
		string pName = p.Name;
		string pVersion = p.Version;
		// 升级前预检：缓存里最新版的门禁/死依赖，避免升完才发现被拒（市场会 exit=1 后回滚）
		string dshVersion = _report?.DshVersion ?? DshCompatService.GetDshVersion(_dshDir) ?? "";
		DshUpdatePreview? preview = null;
		try
		{
			string profileDir = DshCompatService.ResolveProfileDir(_profile);
			preview = await Task.Run(delegate { return DshCompatService.PreviewPluginUpdate(pName, pVersion, _dshDir, profileDir, dshVersion); });
		}
		catch (System.Exception ex)
		{
			Log("[升级预检] 跳过: " + ex.Message);
		}
		string upgradeHead = "将 \"" + pName + "\" 升级到最新版？\n（pnpm add " + pName + "@latest，可能需要数分钟）";
		if (preview != null && preview.HasConcern)
		{
			MessageBoxResult risky = MessageBox.Show(
				upgradeHead + "\n\n升级预检发现风险（最新 " + preview.LatestVersion + "，当前 DSH " + dshVersion + "）：\n"
				+ preview.Describe() + "\n\n建议先不要升级。仍要继续吗？",
				preview.GateBlocked ? "升级预检：可能被门禁拒绝" : "升级预检：最新版仍有已删除依赖",
				MessageBoxButton.YesNo, MessageBoxImage.Warning);
			Log("[升级预检] " + pName + " → " + preview.LatestVersion
				+ (preview.GateBlocked ? "；门禁 " + preview.GateName + "=" + preview.GateRange + " 不满足" : "")
				+ (preview.DeadPeers.Count > 0 ? "；死依赖 " + string.Join(",", preview.DeadPeers) : "")
				+ "；用户" + (risky == MessageBoxResult.Yes ? "仍选择升级" : "取消升级"));
			if (risky != MessageBoxResult.Yes)
			{
				return;
			}
		}
		else
		{
			MessageBoxResult confirm = MessageBox.Show(upgradeHead, "升级插件", MessageBoxButton.YesNo, MessageBoxImage.Question);
			if (confirm != MessageBoxResult.Yes)
			{
				return;
			}
		}
		_busy = true;
		string msg = await Task.Run(delegate { return DshCompatService.UpgradePlugin(_profile, p.Name, delegate (string m) { Log(m); }); });
		Log("[操作] " + p.Name + " → " + msg);
		_hostLog?.Invoke("[插件管理] " + p.Name + " 升级: " + msg);
		_busy = false;
		await RefreshAsync();
	}

	private void Exempt_Click(object sender, RoutedEventArgs e)
	{
		DshPluginInfo? p = Selected();
		if (p == null || _report == null || _busy)
		{
			return;
		}
		MessageBoxResult confirm = MessageBox.Show(
			"为 \"" + p.Name + "@" + p.Version + "\" 写入版本豁免（dsh " + _report.DshVersion + "）？\n\n注意：豁免仅跳过 peer 版本检查；若插件 client bundle 与当前 DSH 构建期模块表不匹配（如 0.1.x 插件跑在 0.2.x 上），激活仍会失败，此类插件请使用\"停用并锁定\"。",
			"版本豁免", MessageBoxButton.YesNo, MessageBoxImage.Question);
		if (confirm != MessageBoxResult.Yes)
		{
			return;
		}
		string msg = DshCompatService.ExemptPlugin(_profile, p.Name, p.Version, _report.DshVersion);
		Log("[操作] " + msg);
		_hostLog?.Invoke("[插件管理] " + msg);
		_ = RefreshAsync();
	}

	private async void Uninstall_Click(object sender, RoutedEventArgs e)
	{
		DshPluginInfo? p = Selected();
		if (p == null || _busy || !RequireDshStopped())
		{
			return;
		}
		MessageBoxResult confirm = MessageBox.Show(
			"卸载 \"" + p.Name + "\"？\n\n将从 dsh.profile.bundles 移除并执行 pnpm remove " + p.Name + "。\n若其他插件依赖它（peer），那些插件会转为\"缺依赖\"状态。",
			"卸载插件", MessageBoxButton.YesNo, MessageBoxImage.Warning);
		if (confirm != MessageBoxResult.Yes)
		{
			return;
		}
		_busy = true;
		string msg = await Task.Run(delegate { return DshCompatService.UninstallPlugin(_profile, p.Name, delegate (string m) { Log(m); }); });
		Log("[操作] " + p.Name + " → " + msg);
		_hostLog?.Invoke("[插件管理] " + p.Name + " 卸载: " + msg);
		_busy = false;
		await RefreshAsync();
	}

	private void OpenDir_Click(object sender, RoutedEventArgs e)
	{
		DshPluginInfo? p = Selected();
		if (p != null && p.Dir.Length > 0)
		{
			OpenDir(p.Dir);
		}
	}
}
