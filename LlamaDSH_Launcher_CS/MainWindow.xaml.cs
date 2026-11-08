using System;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using UniformGrid = System.Windows.Controls.Primitives.UniformGrid;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LlamaDSH_Launcher.Models;
using LlamaDSH_Launcher.Services;
using Microsoft.Win32;

namespace LlamaDSH_Launcher;

public partial class MainWindow : Window, IComponentConnector, IParamState
{
	private static readonly string CFG_DIR = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal));

	private const string CFG_FILE = "LlamaDSH_Launcher_config.json";

	private Process? _llamaProc;

	private ProcessTracker? _dshTracker;

	private Process? _dshProc;
	private bool _dshAdminMode; // DSH 是否以管理员提权方式运行（影响停止回退与状态显示）

	private readonly GpuMonitor _gpuMonitor;

	private CancellationTokenSource? _perfCts;

	private Thread? _perfThread;

	private PerformanceCounter? _cpuCounter;



	private string _lastLogLine;

	private int _dshHiddenCount;

	private int _dshRetriesLeft;

	private string? _dshBootLogPath;

	private CancellationTokenSource? _dshTailCts;

	// 最近一次 AutoFill 提示集合（用于去重，避免击键刷屏）
	private string? _lastAutoFillHints;

	// 上一次由 AutoFill 写入的 alias 值(区分用户手改与自动值)
	private string? _lastAutoFilledAlias;

	// DSH 升级自动重建 (git HEAD 变化 → clean + build:lib + build:web)
	private DispatcherTimer? _dshRebuildTimer;
	private string _dshRebuildLogPath = "";
	private long _dshRebuildLogPos;
	private string _dshRebuildHead = "";
	private Action? _dshRebuildDone;
	private DateTime _dshRebuildDeadline;

	private string _dshRebuildDir = "";

	private bool _dshCompatRunning;
	private bool _dshPluginsRunning;
	private bool _dirSearchRunning;

	private DispatcherTimer? _dshVerifyTimer;

	private DateTime _dshVerifyDeadline;

	private bool _vramBreakerTriggered;





	private readonly string _stateFile;

	private readonly string _cfgPathFile;

	private readonly string _windowStateFile;

	private readonly StringBuilder _runLog;

	private readonly object _runLogLock;

	private int _runLogCount;

	private const int RunLogMaxLines = 5000;

	private string _configDir;

	private string _configFilename;

	private Dictionary<string, ProfileConfig> _profiles;

	private string _currentProfile;

	private string _lastCmd;

	/// <summary>命令预览里手动追加的自定义参数（橙色显示，实际启动时追加到末尾）。</summary>
	private List<string> _customArgs = new();

	/// <summary>命令预览是否处于编辑态；编辑期间 RefreshCmd 不覆盖用户输入。</summary>
	private bool _cmdEditing;

	/// <summary>进入编辑态时的基础命令 token（自定义参数只能是它后面的追加部分）。</summary>
	private List<string> _cmdEditBase = new();

	/// <summary>日志是否跟随到底部：用户向上滚动后置 false，滚回底部再置 true。</summary>
	private bool _logAutoFollow = true;

	private readonly DispatcherTimer _healthTimer;

	private readonly ConcurrentQueue<string> _logPending;

	private readonly DispatcherTimer _logTimer;

	private const int LogMaxLines = 2000;

	private const int LogTrimKeep = 1700;

	private NotifyIcon? _trayIcon;

	/// <summary>提示条自动隐藏计时器（懒创建，见 ShowNotice）。</summary>
	private System.Windows.Threading.DispatcherTimer? _noticeTimer;

	/// <summary>本次点击"启动"后是否已经尝试过"重连 node_modules 链接"自愈，避免反复触发。</summary>
	private bool _dshModuleHealTried;

	/// <summary>测试启动时临时打开了 --log-verbose，停止后需要还原。</summary>
	private bool _vramTestVerboseRestore;

	private List<ParamDefinition> _paramDefs;

	private ParamSchemaLoadResult _paramSchema = new();

	private List<ParamGroupDef> _paramGroups = new();

	/// <summary>最近一次 BuildCmd 依赖/校验未通过的参数清单（启动时弹窗确认）。</summary>
	private List<string> _lastViolations = new();

	private bool _inDepEval;

	private readonly Dictionary<string, (System.Windows.Controls.CheckBox ec, System.Windows.Controls.Control vw)> _pw;

	private readonly Dictionary<string, System.Windows.Controls.CheckBox> _pbw;

	/// <summary>参数行索引（过滤用）：每行记录所属组、子组与行元素。</summary>
	private readonly List<(string GrpId, string GrpTitle, string Sub, UIElement Row, ParamDefinition Def)> _paramRows = new();
	// 每个参数行下方的真实启动参数回显标签
	private readonly Dictionary<string, System.Windows.Controls.TextBlock> _argLabels = new Dictionary<string, System.Windows.Controls.TextBlock>();
	private ParamBuildResult? _lastParamBuild;

	/// <summary>子组内容面板缓存（key = 组id + 子组名），导航切换时挂到 ParamContent。</summary>
	private readonly Dictionary<string, FrameworkElement> _subPanels = new();

	/// <summary>左侧导航条目（用于选中态与过滤徽章更新）。</summary>
	private readonly List<(Border Item, TextBlock Title, TextBlock Badge, string GrpId, string GrpTitle, string Sub, int Count)> _navItems = new();

	private (string GrpId, string Sub)? _currentSub;


	public MainWindow()
	{
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dd: Expected O, but got Unknown
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_0107: Expected O, but got Unknown
		//IL_01c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ca: Expected O, but got Unknown
		_gpuMonitor = new GpuMonitor();
		_lastLogLine = "";
		_stateFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh_launcher_state.json");
		_cfgPathFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llamadsh_config_path.json");
		_windowStateFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llamadsh_window.json");
		_runLog = new StringBuilder();
		_runLogLock = new object();
		_profiles = new Dictionary<string, ProfileConfig>();
		_currentProfile = "默认方案";
		_lastCmd = "";
		_healthTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(5.0)
		};
		_logPending = new ConcurrentQueue<string>();
		_logTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(100.0)
		};
		_pw = new Dictionary<string, (System.Windows.Controls.CheckBox, System.Windows.Controls.Control)>();
		_pbw = new Dictionary<string, System.Windows.Controls.CheckBox>();
		
		_configDir = CFG_DIR;
		_configFilename = "LlamaDSH_Launcher_config.json";
		_paramSchema = ParamSchemaService.Load();
		_paramDefs = _paramSchema.Defs;
		_paramGroups = _paramSchema.Groups;
		if (_paramSchema.Errors.Count > 0 || _paramSchema.Warnings.Count > 0)
		{
			string schemaMsg = string.Join("\n", _paramSchema.Errors.Concat(_paramSchema.Warnings));
			Dispatcher.BeginInvoke(new Action(delegate
			{
				ShowNotice(schemaMsg, isError: true);
			}));
		}
		LoadConfigDirFromFile();
		InitializeComponent();
		LogArea.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(LogArea_ScrollChanged));
		ChkAutoScroll.Checked += delegate
		{
			_logAutoFollow = true;
			LogArea.ScrollToEnd();
		};
		ChkAutoScroll.Unchecked += delegate
		{
			_logAutoFollow = false;
		};
		BuildParamGroups();
		ShowSection("path");
		SetupTray();
		WireLiveRefresh();
		RefreshCmdNow();
		StartPerfMonitor();
		LoadState();
		TryStartAgentApi();
		_healthTimer.Tick += HealthCheck;
		_healthTimer.Start();
		_logTimer.Tick += delegate
		{
			FlushLogs();
		};
		_logTimer.Start();
		base.IsVisibleChanged += new DependencyPropertyChangedEventHandler(OnVisibilityChanged);
		// 最小化到任务栏同样进轻量模式(托盘路径在 Window_Closing 的 Hide 分支)
		StateChanged += (_, _) =>
		{
			if (base.WindowState == WindowState.Minimized)
			{
				EnterTrayLightMode();
			}
			else
			{
				ExitTrayLightMode();
			}
		};
	}

	private void BuildParamGroups()
	{
		_paramRows.Clear();
		_argLabels.Clear();
		_subPanels.Clear();
		_navItems.Clear();
		_currentSub = null;
		InputParamFilter.TextChanged -= InputParamFilter_TextChanged;
		InputParamFilter.Clear();
		InputParamFilter.TextChanged += InputParamFilter_TextChanged;
		ParamNavPanel.Children.Clear();
		ParamContent.Children.Clear();
		foreach (ParamGroupDef g in _paramGroups.OrderBy((ParamGroupDef x) => x.Order))
		{
			List<ParamDefinition> items = _paramDefs.Where((ParamDefinition p) => p.Group == g.Id && !p.Hidden).ToList();
			if (items.Count == 0)
			{
				continue;
			}
			// 左侧导航: 组标题(一级标题 = 大号 + 加粗 + accent 色, 与子组条目拉开层级;
			// 与 Linux 端 webui 的 .navGrpTitle 同一套规格)
			ParamNavPanel.Children.Add(new System.Windows.Controls.TextBlock
			{
				Text = g.Title,
				Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3a, 0xa9, 0xff)),
				FontSize = 13.0,
				FontWeight = FontWeights.Bold,
				Margin = new Thickness(4, 12, 0, 4)
			});
			// 子组聚合: 同一子组的参数聚拢（即使 yml 中不连续）
			foreach (IGrouping<string, ParamDefinition> sg in items.GroupBy((ParamDefinition p) => p.Subgroup ?? ""))
			{
				Grid grid = new Grid();
				// 一参数一行：单列布局，输入框可横跨整行（原先左右两列各塞一个参数，长值与下拉都过窄）
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
				int idx = 0;
				foreach (ParamDefinition item in sg)
				{
					if (item.Type == ParamType.Bool)
					{
						System.Windows.Controls.CheckBox checkBox = new System.Windows.Controls.CheckBox
						{
							Content = item.Label,
							IsChecked = string.Equals(item.DefaultValue, "true", StringComparison.OrdinalIgnoreCase),
							ToolTip = item.Tooltip,
							Foreground = System.Windows.Media.Brushes.White,
							Margin = new Thickness(0.0, 1.0, 0.0, 1.0)
						};
						checkBox.Checked += delegate
						{
							RefreshCmd();
						};
						checkBox.Unchecked += delegate
						{
							RefreshCmd();
						};
						_pbw[item.Flag] = checkBox;
						PlaceParamRow(grid, checkBox, ref idx);
						_paramRows.Add((g.Id, g.Title, sg.Key, checkBox, item));
						System.Windows.Controls.TextBlock boolArg = new System.Windows.Controls.TextBlock
						{
							Text = "--" + item.Flag,
							FontFamily = new System.Windows.Media.FontFamily("Consolas"),
							FontSize = 11.0,
							TextWrapping = TextWrapping.Wrap,
							Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(110, 118, 129)),
							Margin = new Thickness(20.0, 0.0, 0.0, 4.0)
						};
						_argLabels[item.Flag] = boolArg;
						PlaceParamRow(grid, boolArg, ref idx);
						continue;					}
					DockPanel dockPanel = new DockPanel
					{
						Margin = new Thickness(0.0, 1.0, 0.0, 1.0)
					};
					System.Windows.Controls.CheckBox checkBox2 = new System.Windows.Controls.CheckBox
					{
						IsChecked = item.Enabled,
						ToolTip = "--" + item.Flag,
						VerticalAlignment = VerticalAlignment.Center,
						Margin = new Thickness(0.0, 0.0, 6.0, 0.0)
					};
					System.Windows.Controls.Label element = new System.Windows.Controls.Label
					{
						Content = item.Label + ":",
						ToolTip = item.Tooltip,
						Width = 160.0,
						Foreground = System.Windows.Media.Brushes.White,
						VerticalAlignment = VerticalAlignment.Center,
						Padding = new Thickness(0.0)
					};
					System.Windows.Controls.Control control;
					if (item.Type == ParamType.Combo && item.Options != null)
					{
						NoScrollComboBox noScrollComboBox = new NoScrollComboBox
						{
							IsEditable = true,
							ToolTip = item.Tooltip,
							MinWidth = 70.0,
							MaxWidth = item.Wide ? double.PositiveInfinity : 420.0,
							HorizontalAlignment = item.Wide ? System.Windows.HorizontalAlignment.Stretch : System.Windows.HorizontalAlignment.Left
						};
						foreach (string option in item.Options)
						{
							noScrollComboBox.Items.Add(option);
						}
						noScrollComboBox.Text = item.DefaultValue.ToString() ?? "";
						control = noScrollComboBox;
						checkBox2.Checked += delegate
						{
							RefreshCmd();
						};
						checkBox2.Unchecked += delegate
						{
							RefreshCmd();
						};
						noScrollComboBox.SelectionChanged += delegate
						{
							RefreshCmd();
						};
					}
					else
					{
						System.Windows.Controls.TextBox obj = new System.Windows.Controls.TextBox
						{
							Text = (item.DefaultValue.ToString() ?? ""),
							TextWrapping = TextWrapping.Wrap,
							ToolTip = item.Tooltip,
							MinWidth = 70.0,
							MaxWidth = item.Wide ? double.PositiveInfinity : 420.0,
							HorizontalAlignment = item.Wide ? System.Windows.HorizontalAlignment.Stretch : System.Windows.HorizontalAlignment.Left
						};
						control = obj;
						checkBox2.Checked += delegate
						{
							RefreshCmd();
						};
						checkBox2.Unchecked += delegate
						{
							RefreshCmd();
						};
						obj.TextChanged += delegate
						{
							RefreshCmd();
						};
					}
					DockPanel.SetDock(checkBox2, Dock.Left);
					dockPanel.Children.Add(checkBox2);
					DockPanel.SetDock(element, Dock.Left);
					dockPanel.Children.Add(element);
					dockPanel.Children.Add(control);
					_pw[item.Flag] = (checkBox2, control);
					PlaceParamRow(grid, dockPanel, ref idx, item.Wide);
					_paramRows.Add((g.Id, g.Title, sg.Key, dockPanel, item));
					System.Windows.Controls.TextBlock argLbl = new System.Windows.Controls.TextBlock
					{
						FontFamily = new System.Windows.Media.FontFamily("Consolas"),
						FontSize = 11.0,
						TextWrapping = TextWrapping.Wrap,
						Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(110, 118, 129)),
						Margin = new Thickness(184.0, 0.0, 0.0, 4.0)
					};
					_argLabels[item.Flag] = argLbl;
					PlaceParamRow(grid, argLbl, ref idx);
				}
				_subPanels[g.Id + "\u0001" + sg.Key] = grid;
				AddNavItem(g, sg.Key, sg.Count());
			}
		}
		if (_navItems.Count > 0)
		{
			SelectSubgroup(_navItems[0].GrpId, _navItems[0].Sub);
		}
		ApplyParamFilter();
	}

	/// <summary>添加左侧导航子组条目（名称 + 参数计数徽章），点击切换右侧内容。</summary>
	private void AddNavItem(ParamGroupDef g, string sub, int count)
	{
		System.Windows.Controls.TextBlock title = new System.Windows.Controls.TextBlock
		{
			Text = sub,
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(201, 209, 217)),
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		System.Windows.Controls.TextBlock badge = new System.Windows.Controls.TextBlock
		{
			Text = count.ToString(),
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158)),
			FontSize = 11.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		title.FontSize = 12.0;                     // 二级标题: 比组标题(13)小一号、常规色
		DockPanel.SetDock(badge, Dock.Right);
		DockPanel dp = new DockPanel();
		dp.Children.Add(badge);
		dp.Children.Add(title);
		Border item = new Border
		{
			Child = dp,
			CornerRadius = new CornerRadius(4),
			Padding = new Thickness(9, 5, 8, 5),
			Margin = new Thickness(12, 1, 0, 1),   // 二级条目相对一级标题缩进

			Background = System.Windows.Media.Brushes.Transparent,
			Cursor = System.Windows.Input.Cursors.Hand,
			ToolTip = g.Title + " ▸ " + sub + "（" + count + " 个参数）"
		};
		string grpId = g.Id;
		string grpTitle = g.Title;
		item.MouseEnter += delegate
		{
			if (_currentSub != (grpId, sub))
			{
				item.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 38, 45));
			}
		};
		item.MouseLeave += delegate
		{
			if (_currentSub != (grpId, sub))
			{
				item.Background = System.Windows.Media.Brushes.Transparent;
			}
		};
		item.MouseLeftButtonUp += delegate
		{
			SelectSubgroup(grpId, sub);
		};
		ParamNavPanel.Children.Add(item);
		_navItems.Add((item, title, badge, grpId, grpTitle, sub, count));
	}

	private static string SubKey(string grpId, string sub)
	{
		return grpId + "\u0001" + sub;
	}

	/// <summary>切换右侧显示的子组，并更新导航选中态与面包屑。</summary>
	private void SelectSubgroup(string grpId, string sub)
	{
		_currentSub = (grpId, sub);
		string? grpTitle = _navItems.FirstOrDefault(n => n.GrpId == grpId && n.Sub == sub).GrpTitle;
		ParamContent.Children.Clear();
		if (_subPanels.TryGetValue(SubKey(grpId, sub), out FrameworkElement? panel))
		{
			ParamContent.Children.Add(panel);
		}
		int count = _navItems.FirstOrDefault(n => n.GrpId == grpId && n.Sub == sub).Count;
		// 面包屑按层级着色: 组名一级(accent/加粗/13px) ▸ 子组二级(亮色) · 计数弱色,
		// 与左侧导航的层级规格一致(Linux 端 breadcrumb 同步这套样式)
		ParamBreadcrumb.Inlines.Clear();
		ParamBreadcrumb.Inlines.Add(new System.Windows.Documents.Run(grpTitle ?? grpId)
		{
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3a, 0xa9, 0xff)),
			FontSize = 13.0,
			FontWeight = FontWeights.Bold
		});
		ParamBreadcrumb.Inlines.Add(new System.Windows.Documents.Run("  ▸  ")
		{
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158)),
			FontSize = 12.0
		});
		ParamBreadcrumb.Inlines.Add(new System.Windows.Documents.Run(sub)
		{
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 237, 243)),
			FontSize = 12.0
		});
		ParamBreadcrumb.Inlines.Add(new System.Windows.Documents.Run("   ·   " + count + " 个参数")
		{
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158)),
			FontSize = 11.0
		});
		foreach (var n in _navItems)
		{
			bool sel = n.GrpId == grpId && n.Sub == sub;
			n.Item.Background = sel ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 79, 120)) : System.Windows.Media.Brushes.Transparent;
			n.Title.Foreground = new SolidColorBrush(sel ? System.Windows.Media.Color.FromRgb(255, 255, 255) : System.Windows.Media.Color.FromRgb(201, 209, 217));
			n.Badge.Foreground = new SolidColorBrush(sel ? System.Windows.Media.Color.FromRgb(255, 255, 255) : System.Windows.Media.Color.FromRgb(139, 148, 158));
		}
	}

	private static bool ParamMatches(ParamDefinition d, string sub, string grpTitle, string q)
	{
		return d.Flag.Contains(q, StringComparison.OrdinalIgnoreCase)
			|| d.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
			|| sub.Contains(q, StringComparison.OrdinalIgnoreCase)
			|| d.Tooltip.Contains(q, StringComparison.OrdinalIgnoreCase)
			|| grpTitle.Contains(q, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>按关键词全局过滤：右侧聚合显示所有命中的子组（分节标题 + 命中行），
	/// 左侧导航显示命中数、无命中的条目淡化；空关键词恢复选中子组视图。</summary>
	private void ApplyParamFilter()
	{
		string q = (InputParamFilter.Text ?? "").Trim();
		bool filtering = q.Length > 0;
		BtnClearParamFilter.Visibility = filtering ? Visibility.Visible : Visibility.Collapsed;
		if (!filtering)
		{
			foreach (var r in _paramRows)
			{
				r.Row.Visibility = Visibility.Visible;
			}
			foreach (var n in _navItems)
			{
				n.Item.Opacity = 1;
				n.Badge.Text = n.Count.ToString();
			}
			if (_currentSub.HasValue)
			{
				SelectSubgroup(_currentSub.Value.GrpId, _currentSub.Value.Sub);
			}
			return;
		}
		ParamContent.Children.Clear();
		int total = 0;
		bool any = false;
		foreach (var n in _navItems)
		{
			var rows = _paramRows.Where(r => r.GrpId == n.GrpId && r.Sub == n.Sub).ToList();
			var hits = rows.Where(r => ParamMatches(r.Def, n.Sub, n.GrpTitle, q)).ToList();
			if (hits.Count == 0)
			{
				n.Item.Opacity = 0.35;
				n.Badge.Text = "0";
				continue;
			}
			any = true;
			total += hits.Count;
			n.Item.Opacity = 1;
			n.Badge.Text = hits.Count.ToString();
			ParamContent.Children.Add(new System.Windows.Controls.TextBlock
			{
				Text = n.GrpTitle + "  ▸  " + n.Sub + "   ·   " + hits.Count + " 项",
				Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 168, 83)),
				FontSize = 12.0,
				FontWeight = FontWeights.SemiBold,
				Margin = new Thickness(0, 8, 0, 3)
			});
			string key = SubKey(n.GrpId, n.Sub);
			if (_subPanels.TryGetValue(key, out FrameworkElement? panel))
			{
				foreach (var r in rows)
				{
					r.Row.Visibility = hits.Contains(r) ? Visibility.Visible : Visibility.Collapsed;
					// 回显行是独立 Grid 行, 不随主行折叠会变成无归属的孤儿
					if (_argLabels.TryGetValue(r.Def.Flag, out System.Windows.Controls.TextBlock? lbl))
					{
						lbl.Visibility = r.Row.Visibility;
					}
				}
				panel.Visibility = Visibility.Visible;
				ParamContent.Children.Add(panel);
			}
		}
		if (!any)
		{
			ParamContent.Children.Add(new System.Windows.Controls.TextBlock
			{
				Text = "没有匹配 \"" + q + "\" 的参数",
				Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158)),
				Margin = new Thickness(0, 12, 0, 0)
			});
		}
		ParamBreadcrumb.Text = "🔍  \"" + q + "\"   ·   命中 " + total + " 个参数 / " + _navItems.Count(n => n.Item.Opacity == 1) + " 个分类";
	}

	private void InputParamFilter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
	{
		ApplyParamFilter();
	}

	private void ClearParamFilter_Click(object sender, RoutedEventArgs e)
	{
		InputParamFilter.Clear();
	}


	/// <summary>一参数一行：每个参数独占一行（原先两列每行塞两个，长值/下拉过窄）。wide 仅表达“值较长”，由控件 MaxWidth 决定铺满与否。</summary>
	private static void PlaceParamRow(Grid g, UIElement el, ref int idx, bool wide = false)
	{
		while (g.RowDefinitions.Count <= idx)
		{
			g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		}
		Grid.SetRow(el, idx);
		Grid.SetColumn(el, 0);
		g.Children.Add(el);
		idx++;
	}

	// ==================== 参数依赖引擎（配置驱动） ====================

	// IParamState 显式实现：规则引擎（ParamRules）与 UI 状态之间的唯一接线点
	// —— 参数面板的勾选/输入即状态源，规则判定本身不依赖 WPF，可单测。
	bool IParamState.IsOn(ParamDefinition d)
	{
		return IsEffectivelyOn(d);
	}

	string IParamState.EffectiveValue(ParamDefinition d)
	{
		return EffectiveValue(d);
	}

	ParamDefinition? IParamState.ByFlag(string flag)
	{
		return _paramDefs.FirstOrDefault((ParamDefinition x) => x.Flag == flag);
	}

	/// <summary>参数是否处于"生效"状态：bool 已勾选；值参数已勾选且值非空。</summary>
	private bool IsEffectivelyOn(ParamDefinition d)
	{
		if (d.Type == ParamType.Bool)
		{
			return _pbw.TryGetValue(d.Flag, out System.Windows.Controls.CheckBox cb) && cb.IsChecked == true;
		}
		if (_pw.TryGetValue(d.Flag, out (System.Windows.Controls.CheckBox, System.Windows.Controls.Control) p) && p.Item1.IsChecked == true)
		{
			string v = ((p.Item2 is System.Windows.Controls.ComboBox c) ? c.Text.Trim() : ((System.Windows.Controls.TextBox)p.Item2).Text.Trim());
			return v.Length > 0;
		}
		return false;
	}

	/// <summary>用于 ==/!= 比较的有效值：未生效时回退 offValue（如 spec-type 未勾选视为 "none"），否则默认值。</summary>
	private string EffectiveValue(ParamDefinition d)
	{
		if (d.Type == ParamType.Bool)
		{
			return (_pbw.TryGetValue(d.Flag, out System.Windows.Controls.CheckBox cb) && cb.IsChecked == true) ? "true" : "false";
		}
		string text = "";
		bool hasCtl = _pw.TryGetValue(d.Flag, out (System.Windows.Controls.CheckBox, System.Windows.Controls.Control) p);
		if (hasCtl)
		{
			text = ((p.Item2 is System.Windows.Controls.ComboBox c) ? c.Text.Trim() : ((System.Windows.Controls.TextBox)p.Item2).Text.Trim());
		}
		bool on = hasCtl && p.Item1.IsChecked == true && text.Length > 0;
		if (on)
		{
			return text;
		}
		if (!string.IsNullOrEmpty(d.OffValue))
		{
			return d.OffValue;
		}
		return d.DefaultValue ?? "";
	}

	/// <summary>
	/// 推测解码是否激活：spec-type 已勾选且有效值 != none（none 为其 offValue）。
	/// --model-draft 的发射以此为准，避免"填了草稿模型路径但推测已关闭"时 llama-server 自行启用推测。
	/// </summary>
	private bool IsSpecDecodingActive()
	{
		ParamDefinition? d = RoleDef("spec_type");
		if (d == null || !IsEffectivelyOn(d))
		{
			return false;
		}
		return !string.Equals(EffectiveValue(d), "none", StringComparison.OrdinalIgnoreCase);
	}

	private bool RuleMet(ParamRule r)
	{
		return ParamRules.RuleMet(r, this);
	}

	/// <summary>按 role 查找参数定义（启动器内部逻辑的绑定入口）。</summary>
	private ParamDefinition? RoleDef(string role)
	{
		return _paramDefs.FirstOrDefault((ParamDefinition d) => d.Role == role);
	}

	private bool RoleText(string role, out string value)
	{
		value = "";
		ParamDefinition? d = RoleDef(role);
		return d != null && TryGetParamValue(d.Flag, out value);
	}

	private bool RoleBool(string role)
	{
		ParamDefinition? d = RoleDef(role);
		return d != null && _pbw.TryGetValue(d.Flag, out System.Windows.Controls.CheckBox cb) && cb.IsChecked == true;
	}

	/// <summary>
	/// UI 层依赖评估：前置条件不满足 → 控件灰显并在悬浮提示标注原因。
	/// 在 RefreshCmd 中调用（任何勾选/输入变化都会触发），收敛无递归。
	/// </summary>
	private void EvaluateDepUI()
	{
		if (_inDepEval || _paramDefs == null)
		{
			return;
		}
		_inDepEval = true;
		try
		{
			// 联动：本参数生效 → 自动勾选目标（bool 勾选 / 值参数勾启用框）
			foreach (ParamDefinition dImp in _paramDefs)
			{
				if (dImp.Hidden || dImp.Implies == null || dImp.Implies.Count == 0 || !IsEffectivelyOn(dImp))
				{
					continue;
				}
				foreach (string t in dImp.Implies)
				{
					if (_pbw.TryGetValue(t, out System.Windows.Controls.CheckBox tcb) && tcb.IsChecked != true)
					{
						tcb.IsChecked = true;
					}
					if (_pw.TryGetValue(t, out (System.Windows.Controls.CheckBox, System.Windows.Controls.Control) tp) && tp.Item1.IsChecked != true)
					{
						tp.Item1.IsChecked = true;
					}
				}
			}
			// 多轮收敛：A 依赖 B、B 又依赖 C 时，一轮可能只清掉一层。
			for (int pass = 0; pass < 4; pass++)
			{
				bool changed = false;
				foreach (ParamDefinition d in _paramDefs)
				{
					if (d.Hidden || d.Requires == null || d.Requires.Count == 0)
					{
						continue;
					}
					bool ok = d.Requires.All(RuleMet);
					if (_pbw.TryGetValue(d.Flag, out System.Windows.Controls.CheckBox bcb))
					{
						if (!ok && bcb.IsChecked == true)
						{
							// 前置未满足时后置参数必须失效：清掉勾选，避免“灰显但仍被注入命令行”。
							bcb.IsChecked = false;
							changed = true;
						}
						bcb.IsEnabled = ok;
						bcb.ToolTip = ok ? d.Tooltip : d.Tooltip + "\n⚠ 前置未满足: " + string.Join(" 且 ", d.Requires.Select((ParamRule r) => r.Describe()));
					}
					if (_pw.TryGetValue(d.Flag, out (System.Windows.Controls.CheckBox, System.Windows.Controls.Control) pair))
					{
						if (!ok && pair.Item1.IsChecked == true)
						{
							pair.Item1.IsChecked = false;
							changed = true;
						}
						pair.Item1.IsEnabled = ok;
						pair.Item2.IsEnabled = ok;
						pair.Item2.ToolTip = ok ? d.Tooltip : d.Tooltip + "\n⚠ 前置未满足: " + string.Join(" 且 ", d.Requires.Select((ParamRule r) => r.Describe()));
					}
				}
				if (!changed)
				{
					break;
				}
			}
		}
		catch
		{
		}
		finally
		{
			_inDepEval = false;
		}
	}

	/// <summary>启动时输出软性提示：废弃参数在用、suggests 依赖缺失。仅日志，不阻断。</summary>
	private void LogParamHints()
	{
		try
		{
			HashSet<string> on = new HashSet<string>();
			foreach (ParamDefinition d in _paramDefs)
			{
				if (!d.Hidden && d.Flag != "model" && d.Flag != "model-draft" && IsEffectivelyOn(d))
				{
					on.Add(d.Flag);
				}
			}
			foreach (ParamDefinition d in _paramDefs)
			{
				if (!on.Contains(d.Flag))
				{
					continue;
				}
				if (d.Deprecated)
				{
					Log("[参数] ⚠ --" + d.Flag + " 已废弃，建议从参数面板取消勾选");
				}
				if (d.Suggests is { Count: > 0 } && !d.Suggests.Any((string s) => on.Contains(s)))
				{
					Log("[参数] 提示: --" + d.Flag + " 通常配合 " + string.Join(" / ", d.Suggests.Select((string s) => "--" + s)) + " 使用");
				}
			}
		}
		catch
		{
		}
	}

	/// <summary>单参数依赖/互斥/校验检查。返回 null=通过，否则返回剔除原因。</summary>
	private string? CheckParam(ParamDefinition d, HashSet<string> on)
	{
		return ParamRules.CheckParam(d, on, this);
	}

	private void SetupTray()
	{
		string text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "app.ico");
		Icon icon;
		try
		{
			icon = (File.Exists(text) ? new Icon(text) : SystemIcons.Application);
		}
		catch (Exception value)
		{
			AppLog.WriteError($"托盘图标加载失败: {value}");
			icon = SystemIcons.Application;
		}
		_trayIcon = new NotifyIcon
		{
			Icon = icon,
			Text = "Llama 启动器",
			Visible = true
		};
		_trayIcon.DoubleClick += delegate
		{
			ShowFromTray();
		};
		ContextMenuStrip contextMenuStrip = new ContextMenuStrip();
		contextMenuStrip.Items.Add("显示窗口", null, delegate
		{
			ShowFromTray();
		});
		// 轻量化模式复选(对齐 Linux 侧 tray.py "轻量化模式(后台低频保活)")
		System.Windows.Forms.ToolStripMenuItem lightItem = new System.Windows.Forms.ToolStripMenuItem("轻量化模式(后台低频保活)");
		lightItem.Checked = _lightOn;
		lightItem.Click += delegate
		{
			bool on = !_lightOn;
			_lightOn = on;
			lightItem.Checked = on;
			_perfPulse = true;
			Log("[轻量化] " + (on ? "开: 重采样降至 30s(全空闲 60s 零采样), 熔断保持 3s 探针" : "关: 恢复正常采样节奏"));
		};
		_trayLightItem = lightItem;
		contextMenuStrip.Items.Add(lightItem);
		contextMenuStrip.Items.Add("退出程序", null, delegate
		{
			QuitApp();
		});
		_trayIcon.ContextMenuStrip = contextMenuStrip;
	}

	private void ShowFromTray()
	{
		ExitTrayLightMode();
		Show();
		base.WindowState = WindowState.Normal;
		Activate();
	}

	/// <summary>进入托盘轻量模式: _trayMode + 自动开轻量化(_lightOn, 对齐 Linux 侧关窗自动置位),
	/// 一次性 GC 清理 + 提示条隐藏; 采样走 30s/60s 低频档。</summary>
	private void EnterTrayLightMode()
	{
		if (_trayMode)
		{
			return;
		}
		_trayMode = true;
		_lightOn = true;
		SyncTrayLightItem();
		// 进轻量档前把熔断配置缓存拉齐(UI 线程直读), 3s 探针第一拍就能正确门控
		SyncBreakerProbeCache();
		try
		{
			// 软件渲染 + 长时间运行积累的托管对象, 隐藏后无 UI 压力, 一次性回收
			GC.Collect(2, GCCollectionMode.Forced, blocking: true);
			GC.WaitForPendingFinalizers();
			GC.Collect(2, GCCollectionMode.Forced, blocking: true);
		}
		catch
		{
		}
	}

	private void ExitTrayLightMode()
	{
		if (!_trayMode)
		{
			return;
		}
		_trayMode = false;
		// 恢复窗口 = 有人在看了: 关轻量化并打断长休眠立刻补拍(Linux 侧 _watched 的等价物)
		_lightOn = false;
		SyncTrayLightItem();
		_perfPulse = true;
		// 立即用最近一拍全量刷新(轻量模式期间跳过了视觉重建)
		if (_lastPerfDict != null)
		{
			OnPerf(_lastPerfDict, _lastPerfGpus ?? new List<GpuDevInfo>());
		}
	}

	/// <summary>托盘菜单"轻量化模式"勾与真实开关同步(Linux 侧 tray._sync_lightweight 同理)。</summary>
	private void SyncTrayLightItem()
	{
		System.Windows.Forms.ToolStripMenuItem? item = _trayLightItem;
		if (item != null)
		{
			item.Checked = _lightOn;
		}
	}

	private void UpdateTray()
	{
		if (_trayIcon != null)
		{
			string text = ((_llamaProc != null && !_llamaProc.HasExited) ? "运行中" : "已停止");
			string text2 = (HasDsh() ? "运行中" : "已停止");
			_trayIcon.Text = "Llama:" + text + " | DSH:" + text2;
		}
	}

	private void OnVisibilityChanged(object? s, DependencyPropertyChangedEventArgs e)
	{
		if (base.IsVisible)
		{
			_gpuMonitor.TempEnabled = true;
			_gpuMonitor.StartGpuTempLoop();
		}
		else
		{
			_gpuMonitor.TempEnabled = false;
			_gpuMonitor.StopGpuTempLoop();
		}
	}

	private void QuitApp()
	{
		bool flag = _llamaProc != null && !_llamaProc.HasExited;
		bool flag2 = HasDsh();
		bool flag3 = true;
		if (flag | flag2)
		{
			string text = "检测到以下服务正在运行：\n\n";
			if (flag)
			{
				text += "  • Llama Server\n";
			}
			if (flag2)
			{
				text += "  • DSH Web\n";
			}
			text += "\n请选择操作：\n\n  是(Y) — 停止所有服务并退出\n  否(N) — 不停止服务直接退出\n  取消 — 返回程序";
			MessageBoxResult messageBoxResult = System.Windows.MessageBox.Show(text, "确认退出", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
			if (messageBoxResult == MessageBoxResult.Cancel)
			{
				return;
			}
			flag3 = messageBoxResult == MessageBoxResult.Yes;
		}
		if (flag3)
		{
			StopLlama();
			StopDsh();
			ClearStateFile();
		}
		else
		{
			SaveState();
		}
		StopPerf();
		_exiting = true;
		FlushRunLog();
		_trayIcon?.Dispose();
		_trayIcon = null;
		System.Windows.Application.Current.Shutdown();
	}

	private void BrowseModel_Click(object s, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Filter = "模型文件|*.gguf;*.safetensors|GGUF|*.gguf"
		};
		if (openFileDialog.ShowDialog() == true)
		{
			InputModelPath.Text = openFileDialog.FileName;
		}
	}

	private void BrowseModelDir_Click(object s, RoutedEventArgs e)
	{
		string? dir = PickFolder("选择模型目录：分片 GGUF 自动取第一片，safetensors 需先转 GGUF", InputModelPath.Text);
		if (dir != null)
		{
			InputModelPath.Text = dir;
		}
	}

	/// <summary>
	/// 用 WPF 原生目录选择器（.NET 8 自带 Microsoft.Win32.OpenFolderDialog），
	/// 取代 WinForms 的 FolderBrowserDialog——后者是 Win95 风格弹窗，且会引入 WinForms 依赖。
	/// 返回 null 表示用户取消。
	/// </summary>
	private static string? PickFolder(string title, string? initialDir)
	{
		Microsoft.Win32.OpenFolderDialog dialog = new Microsoft.Win32.OpenFolderDialog
		{
			Title = title
		};
		if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
		{
			dialog.InitialDirectory = initialDir;
		}
		return dialog.ShowDialog() == true ? dialog.FolderName : null;
	}

	private void BrowseMtp_Click(object s, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Filter = "GGUF|*.gguf"
		};
		if (openFileDialog.ShowDialog() == true)
		{
			InputMtpPath.Text = openFileDialog.FileName;
		}
	}

	private void BrowseMmproj_Click(object s, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Filter = "GGUF|*.gguf"
		};
		if (openFileDialog.ShowDialog() == true)
		{
			InputMmprojPath.Text = openFileDialog.FileName;
		}
	}

	private void BrowseLlama_Click(object s, RoutedEventArgs e)
	{
		string? dir = PickFolder("选择 llama.cpp 程序目录（含 llama-server.exe）", InputLlamaDir.Text);
		if (dir != null)
		{
			InputLlamaDir.Text = dir;
		}
	}

	private void BrowseDsh_Click(object s, RoutedEventArgs e)
	{
		string? dir = PickFolder("选择 DeepSeek Harness 目录（含 apps/cli）", InputDshDir.Text);
		if (dir != null)
		{
			InputDshDir.Text = dir;
		}
	}

	private void SearchLlama_Click(object s, RoutedEventArgs e)
	{
		System.Windows.Controls.Button b = (System.Windows.Controls.Button)s;
		b.IsEnabled = false;
		b.Content = "⏳ 检索中...";
		Task.Run(delegate
		{
			string r = FileSearcher.FindLlamaServer(delegate(string m)
			{
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					Log(m);
				}, Array.Empty<object>());
			});
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				b.IsEnabled = true;
				b.Content = "\ud83d\udd0d 自动检索";
				if (r != null)
				{
					InputLlamaDir.Text = r;
					Log("Found:" + r);
				}
				else
				{
					Log("Not found");
				}
			}, Array.Empty<object>());
		});
	}

	private void SearchDsh_Click(object s, RoutedEventArgs e)
	{
		System.Windows.Controls.Button b = (System.Windows.Controls.Button)s;
		b.IsEnabled = false;
		b.Content = "⏳ 检索中...";
		Task.Run(delegate
		{
			string r = FileSearcher.FindDshProject(delegate(string m)
			{
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					Log(m);
				}, Array.Empty<object>());
			});
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				b.IsEnabled = true;
				b.Content = "\ud83d\udd0d 自动检索";
				if (r != null)
				{
					InputDshDir.Text = r;
					Log("Found:" + r);
				}
				else
				{
					Log("Not found");
				}
			}, Array.Empty<object>());
		});
	}

	// ==================== 更新：llama.cpp / deepseek-harness ====================

	// ══════════ llama.cpp 变体管理(标准/PrismML 等分支并存切换) ══════════
	private void LoadLlamaVariants(Models.ProfileConfig c)
	{
		_variantGuard = true;
		try
		{
			_llamaVariants = c.LlamaVariants ?? new List<Models.LlamaVariant>();
			if (_llamaVariants.Count == 0)
			{
				// 旧配置迁移: 现有目录注册为标准变体
				_llamaVariants.Add(new Models.LlamaVariant { Name = "标准 llama.cpp", Repo = "ggml-org/llama.cpp", Dir = c.LlamaDir ?? "" });
			}
			_activeVariant = string.IsNullOrEmpty(c.ActiveLlamaVariant) ? _llamaVariants[0].Name : c.ActiveLlamaVariant;
			if (_llamaVariants.All(v => v.Name != _activeVariant))
			{
				_activeVariant = _llamaVariants[0].Name;
			}
			var cur = _llamaVariants.FirstOrDefault(v => v.Name == _activeVariant);
			if (cur != null && !string.IsNullOrWhiteSpace(cur.Dir) && Toolchain.NormalizeDir(cur.Dir) != Toolchain.NormalizeDir(InputLlamaDir.Text))
			{
				InputLlamaDir.Text = cur.Dir;
			}
			RefreshLlamaVariantCombo();
		}
		finally
		{
			_variantGuard = false;
		}
	}

	private void RefreshLlamaVariantCombo()
	{
		_variantGuard = true;
		try
		{
			ComboLlamaVariant.Items.Clear();
			foreach (var v in _llamaVariants)
			{
				ComboLlamaVariant.Items.Add(v.Name + (string.IsNullOrEmpty(v.Dir) ? "（未安装）" : ""));
			}
			int i = _llamaVariants.FindIndex(v => v.Name == _activeVariant);
			ComboLlamaVariant.SelectedIndex = i >= 0 ? i : 0;
		}
		finally
		{
			_variantGuard = false;
		}
	}

	private string CurrentLlamaRepo()
	{
		return _llamaVariants.FirstOrDefault(v => v.Name == _activeVariant)?.Repo ?? "ggml-org/llama.cpp";
	}

	private void LlamaVariant_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
	{
		if (_variantGuard)
		{
			return;
		}
		int i = ComboLlamaVariant.SelectedIndex;
		if (i < 0 || i >= _llamaVariants.Count)
		{
			return;
		}
		var v = _llamaVariants[i];
		string prevName = _activeVariant;
		if (IsLlamaRunning())
		{
			ShowNotice("Llama 服务运行中，请先停止再切换变体（切换即切换程序目录）。", isError: true);
			_variantGuard = true;
			int pi = _llamaVariants.FindIndex(x => x.Name == prevName);
			if (pi >= 0)
			{
				ComboLlamaVariant.SelectedIndex = pi;
			}
			_variantGuard = false;
			return;
		}
		_activeVariant = v.Name;
		if (!string.IsNullOrWhiteSpace(v.Dir))
		{
			InputLlamaDir.Text = v.Dir;
		}
		else
		{
			// 未安装变体不同步目录会让 UI 标注与实际启动目录不一致: 显式清空
			InputLlamaDir.Text = "";
			Log($"[变体] 「{v.Name}」尚未安装，请点“＋ 安装新变体”完成安装后再启动。");
		}
		Log($"[变体] 切换 llama.cpp 版本: {v.Name}（仓库 {v.Repo}）");
	}

	private void InputLlamaDir_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
	{
		if (_variantGuard)
		{
			return;
		}
		var v = _llamaVariants.FirstOrDefault(x => x.Name == _activeVariant);
		if (v != null)
		{
			v.Dir = InputLlamaDir.Text;
		}
	}

	private void RemoveLlamaVariant_Click(object sender, RoutedEventArgs e)
	{
		if (_llamaVariants.Count <= 1)
		{
			ShowNotice("至少保留一个变体。", isError: true);
			return;
		}
		int i = ComboLlamaVariant.SelectedIndex;
		if (i < 0 || i >= _llamaVariants.Count)
		{
			return;
		}
		string name = _llamaVariants[i].Name;
		if (System.Windows.MessageBox.Show($"从列表移除变体「{name}」？\n（不删除磁盘上的程序文件）", "移除变体", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			return;
		}
		_llamaVariants.RemoveAt(i);
		_activeVariant = _llamaVariants[0].Name;
		RefreshLlamaVariantCombo();
		if (!string.IsNullOrWhiteSpace(_llamaVariants[0].Dir))
		{
			InputLlamaDir.Text = _llamaVariants[0].Dir;
		}
		Log($"[变体] 已移除 {name}");
	}

	private void AddLlamaVariant_Click(object sender, RoutedEventArgs e)
	{
		var win = new System.Windows.Window
		{
			Title = "安装 llama.cpp 变体",
			Width = 620,
			SizeToContent = System.Windows.SizeToContent.Height,
			WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
			Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x2e)),
			ResizeMode = System.Windows.ResizeMode.NoResize,
		};
		var root = new StackPanel { Margin = new Thickness(16) };
		var urlBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 4, 0, 0) };
		var nameBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 4, 0, 0) };
		var dirRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
		var dirBox = new System.Windows.Controls.TextBox();
		var browse = new System.Windows.Controls.Button { Content = "浏览...", Margin = new Thickness(6, 0, 0, 0) };
		dirRow.Children.Add(browse);
		dirRow.Children.Add(dirBox);
		var btnRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
		var cancel = new System.Windows.Controls.Button { Content = "取消", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
		var ok = new System.Windows.Controls.Button { Content = "⬇ 检查并安装", Padding = new Thickness(14, 4, 14, 4), Background = new SolidColorBrush(Color.FromRgb(0x23, 0x86, 0x36)), Foreground = System.Windows.Media.Brushes.White };
		btnRow.Children.Add(cancel);
		btnRow.Children.Add(ok);
		root.Children.Add(new TextBlock
		{
			Text = "仓库链接（GitHub 仓库主页地址）",
			Foreground = new SolidColorBrush(Color.FromRgb(0x8e, 0x97, 0x9f)),
			FontSize = 11,
		});
		root.Children.Add(urlBox);
		root.Children.Add(new TextBlock
		{
			Text = "示例: https://github.com/PrismML-Eng/llama.cpp",
			Foreground = new SolidColorBrush(Color.FromRgb(0x8e, 0x97, 0x9f)),
			FontSize = 10,
			Margin = new Thickness(0, 2, 0, 0),
		});
		root.Children.Add(new TextBlock { Text = "显示名称", Foreground = new SolidColorBrush(Color.FromRgb(0x8e, 0x97, 0x9f)), FontSize = 11, Margin = new Thickness(0, 10, 0, 0) });
		root.Children.Add(nameBox);
		root.Children.Add(new TextBlock { Text = "安装目录（下载解压到此处, 与其它变体并存互不覆盖）", Foreground = new SolidColorBrush(Color.FromRgb(0x8e, 0x97, 0x9f)), FontSize = 11, Margin = new Thickness(0, 10, 0, 0) });
		root.Children.Add(dirRow);
		root.Children.Add(btnRow);
		win.Content = root;

		// 名称/目录随仓库链接自动带出(用户手动编辑后不再覆盖)
		bool nameEdited = false;
		bool dirEdited = false;
		nameBox.TextChanged += (_, _) => nameEdited = true;
		dirBox.TextChanged += (_, _) => dirEdited = true;

		string DefaultDir(string owner)
		{
			string parent = "";
			try
			{
				string nd = Toolchain.NormalizeDir(InputLlamaDir.Text);
				if (!string.IsNullOrWhiteSpace(nd))
				{
					parent = System.IO.Directory.GetParent(nd.TrimEnd('\\', '/'))?.FullName ?? "";
				}
			}
			catch
			{
			}
			return string.IsNullOrEmpty(parent) ? "" : System.IO.Path.Combine(parent, "llama.cpp-" + owner);
		}

		urlBox.TextChanged += (_, _) =>
		{
			var m = ParseRepoInput(urlBox.Text);
			if (m == null)
			{
				return;
			}
			if (!nameEdited)
			{
				nameBox.Text = m.Value.owner;
			}
			if (!dirEdited)
			{
				dirBox.Text = DefaultDir(m.Value.owner);
			}
		};
		browse.Click += (_, _) =>
		{
			string? d = PickFolder("选择变体安装目录", dirBox.Text);
			if (d != null)
			{
				dirBox.Text = d;
				dirEdited = true;
			}
		};
		cancel.Click += (_, _) => win.Close();
		ok.Click += async (_, _) =>
		{
			try
			{
			var parsed = ParseRepoInput(urlBox.Text);
			if (parsed == null)
			{
				ShowNotice("仓库链接无法识别。请填 GitHub 仓库主页地址，例如:\nhttps://github.com/PrismML-Eng/llama.cpp", isError: true);
				return;
			}
			string repo = parsed.Value.repo;
			Log($"[变体] 查询 {repo} 最新版本...(网络不通时最多约 1 分钟, 请留意按钮状态)");
			string name = string.IsNullOrWhiteSpace(nameBox.Text) ? parsed.Value.owner : nameBox.Text.Trim();
			string dir = dirBox.Text.Trim();
			if (string.IsNullOrEmpty(dir))
			{
				ShowNotice("请填写安装目录。", isError: true);
				return;
			}
			if (_llamaVariants.Any(v => v.Name == name && v.Dir != dir))
			{
				ShowNotice($"名称「{name}」已被其它变体占用, 换一个名称。", isError: true);
				return;
			}
			if (IsLlamaRunning())
			{
				ShowNotice("Llama 服务运行中，请先停止再安装/更新变体。", isError: true);
				return;
			}
			if (_llamaUpdBusy)
			{
				ShowNotice("已有更新/安装任务进行中，请稍候。", isError: true);
				return;
			}
			_llamaUpdBusy = true;
			ok.IsEnabled = false;
			ok.Content = "⏳ 获取版本...";
			var latest = await Task.Run(() => UpdateService.FetchLatestLlamaRelease(repo, LlamaBackendPreference()));
			if (latest == null)
			{
				_llamaUpdBusy = false;
				ok.IsEnabled = true;
				ok.Content = "⬇ 检查并安装";
				ShowNotice($"无法获取 {repo} 的最新版本（网络不通/GitHub 限流, 或该仓库未提供 win-x64 预编译包）。", isError: true);
				return;
			}
			ok.Content = "⏳ 下载安装中...";
			try
			{
				System.IO.Directory.CreateDirectory(dir);
			}
			catch (Exception ex)
			{
				ok.IsEnabled = true;
				ok.Content = "⬇ 检查并安装";
				ShowNotice("创建目录失败: " + ex.Message, isError: true);
				return;
			}
			if (System.IO.File.Exists(System.IO.Path.Combine(dir, "llama-server.exe")))
			{
				Log("[变体] 目标目录已存在 llama-server.exe, 将直接覆盖。若之前装过非 release 渠道的构建(官网/CI), 建议先清空目录避免新旧文件混杂。");
			}
			var (okR, msgR) = await UpdateService.ApplyLlamaUpdateAsync(dir, latest);
			UpdateService.CleanupLauncherTemp();
			if (!okR)
			{
				_llamaUpdBusy = false;
				ok.IsEnabled = true;
				ok.Content = "⬇ 检查并安装";
				ShowNotice("安装失败: " + msgR, isError: true);
				return;
			}
			_llamaUpdBusy = false;
			win.Close();
			var exist = _llamaVariants.FirstOrDefault(v => v.Name == name);
			if (exist != null)
			{
				exist.Repo = repo;
				exist.Dir = dir;
			}
			else
			{
				_llamaVariants.Add(new Models.LlamaVariant { Name = name, Repo = repo, Dir = dir });
			}
			_activeVariant = name;
			RefreshLlamaVariantCombo();
			InputLlamaDir.Text = dir;
			Log($"[变体] 已安装 {name}（{repo}）→ {dir}");
			Log("[变体] " + msgR);
			// PrismML 模型卡明确其低比特内核只覆盖 CUDA/Metal/CPU，AMD/Intel 无法 GPU 加速
			string note = repo.Contains("PrismML", StringComparison.OrdinalIgnoreCase)
				? "\n\n⚠ 注意: PrismML 低比特内核仅覆盖 CUDA/Metal/CPU——NVIDIA/Apple 可 GPU 加速, AMD/Intel 显卡将走 CPU 回退(较慢)。"
				: "";
			ShowNotice($"变体「{name}」安装完成。\n目录: {dir}\n\n{msgR}{note}\n\n可随时在「Llama 版本」下拉里切换。");
			}
			catch (Exception ex)
			{
				_llamaUpdBusy = false;
				ok.IsEnabled = true;
				ok.Content = "⬇ 检查并安装";
				LogError(ex, "安装 llama.cpp 变体");
				ShowNotice("安装出错: " + ex.Message, isError: true);
			}
		};
		win.Show();
	}

	/// <summary>仓库链接 → (owner, owner/repo)。支持 主页地址/.git/tree/branch/releases 等深链/裸 owner/repo。</summary>
	private static (string owner, string repo)? ParseRepoInput(string? input)
	{
		string t = (input ?? "").Trim();
		if (t.Length == 0)
		{
			return null;
		}
		foreach (var pre in new[] { "https://github.com/", "http://github.com/", "https://www.github.com/", "http://www.github.com/", "github.com/", "www.github.com/" })
		{
			if (t.StartsWith(pre, StringComparison.OrdinalIgnoreCase))
			{
				t = t[pre.Length..];
				break;
			}
		}
		int hash = t.IndexOf('#');
		if (hash >= 0)
		{
			t = t[..hash];
		}
		var seg = t.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (seg.Length < 2)
		{
			return null;
		}
		string owner = seg[0];
		string repo = seg[1];
		if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
		{
			repo = repo[..^4];
		}
		if (owner.Length == 0 || repo.Length == 0)
		{
			return null;
		}
		return (owner, owner + "/" + repo);
	}


	// ══════════ llama.cpp 变体管理结束 ══════════

	private void CheckLlamaUpdate_Click(object s, RoutedEventArgs e)
	{
		_ = CheckLlamaUpdateAsync(fromButton: true);
	}

	private void CheckDshUpdate_Click(object s, RoutedEventArgs e)
	{
		_ = CheckDshUpdateAsync(fromButton: true);
	}

	private string EffectiveLlamaDir()
	{
		string dir = Toolchain.NormalizeDir(InputLlamaDir.Text);
		// 不同机器可能装在任意盘符/目录：配置为空或失效时按已知位置快速探测，不写死 D:\llama.cpp。
		string resolved = Toolchain.ResolveLlamaDir(dir) ?? dir;
		// 统一返回“llama-server.exe 所在目录”，兼容 exe 直填和 bin/ 子目录布局；更新时只覆盖该目录。
		string? exe = UpdateService.FindLlamaServerExe(resolved);
		if (exe != null)
		{
			return Path.GetDirectoryName(exe) ?? resolved;
		}
		if (File.Exists(resolved))
		{
			return Path.GetDirectoryName(resolved) ?? resolved;
		}
		return resolved;
	}

	private string EffectiveDshDir()
	{
		string dir = Toolchain.NormalizeDir(InputDshDir.Text);
		// 不同机器安装路径不同：优先用配置，失效时按状态/Running 进程/profile junction/常见位置探测。
		return Toolchain.ResolveDshDir(dir) ?? dir;
	}

	/// <summary>
	/// 启动时校正目录：配置为空/失效时先快速探测（状态文件、运行进程、profile junction、常见位置），
	/// 仍找不到再后台全盘检索；找到后写回输入框。不同电脑安装路径不同也不会卡在写死路径上。
	/// </summary>
	private void EnsureDirsDetected()
	{
		try
		{
			string dsh = Toolchain.NormalizeDir(InputDshDir.Text);
			if (!Toolchain.IsDshDir(dsh))
			{
				string? found = Toolchain.ResolveDshDir(dsh);
				if (found != null && !string.Equals(found, dsh, StringComparison.OrdinalIgnoreCase))
				{
					InputDshDir.Text = found;
					Log("[目录] 自动定位 DSH 安装目录: " + found);
				}
				else
				{
					Log("[目录] 未找到 DSH 安装目录，后台全盘检索中（也可点“🔍 自动检索”）...");
					StartBackgroundDshSearch();
				}
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "自动定位 DSH 目录");
		}
		try
		{
			string llama = Toolchain.NormalizeDir(InputLlamaDir.Text);
			if (!Toolchain.IsLlamaDir(llama))
			{
				string? found = Toolchain.ResolveLlamaDir(llama);
				if (found != null && !string.Equals(found, llama, StringComparison.OrdinalIgnoreCase))
				{
					InputLlamaDir.Text = found;
					Log("[目录] 自动定位 llama.cpp 目录: " + found);
				}
				else
				{
					Log("[目录] 未找到 llama-server.exe，后台全盘检索中（也可点“🔍 自动检索”）...");
					StartBackgroundLlamaSearch();
				}
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "自动定位 llama.cpp 目录");
		}
	}

	private void StartBackgroundDshSearch()
	{
		if (_dirSearchRunning)
		{
			return;
		}
		_dirSearchRunning = true;
		Task.Run(delegate
		{
			string? found = null;
			try
			{
				found = FileSearcher.FindDshProject(delegate (string m)
				{
					Dispatcher.BeginInvoke((Action)delegate { Log(m); }, Array.Empty<object>());
				});
			}
			catch (Exception ex)
			{
				Dispatcher.BeginInvoke((Action)delegate { LogError(ex, "后台检索 DSH 目录"); }, Array.Empty<object>());
			}
			Dispatcher.BeginInvoke((Action)delegate
			{
				_dirSearchRunning = false;
				if (found != null && !Toolchain.IsDshDir(InputDshDir.Text))
				{
					InputDshDir.Text = found;
					Log("[目录] 后台检索到 DSH 安装目录: " + found);
				}
			}, Array.Empty<object>());
		});
	}

	private void StartBackgroundLlamaSearch()
	{
		Task.Run(delegate
		{
			string? found = null;
			try
			{
				found = FileSearcher.FindLlamaServer(delegate (string m)
				{
					Dispatcher.BeginInvoke((Action)delegate { Log(m); }, Array.Empty<object>());
				});
			}
			catch (Exception ex)
			{
				Dispatcher.BeginInvoke((Action)delegate { LogError(ex, "后台检索 llama.cpp 目录"); }, Array.Empty<object>());
			}
			Dispatcher.BeginInvoke((Action)delegate
			{
				if (found != null && !Toolchain.IsLlamaDir(InputLlamaDir.Text))
				{
					InputLlamaDir.Text = found;
					Log("[目录] 后台检索到 llama.cpp 目录: " + found);
				}
			}, Array.Empty<object>());
		});
	}

	private bool IsLlamaRunning()
	{
		if (_llamaProc != null && IsAlive(_llamaProc))
		{
			return true;
		}
		try
		{
			return ProcessHelper.FindProcessByPort(GetLlamaPort()).HasValue;
		}
		catch
		{
			return false;
		}
	}

	private bool IsDshRunning()
	{
		try
		{
			return (_dshProc != null && IsAlive(_dshProc))
				|| ProcessHelper.FindProcessByPort(3080).HasValue
				|| Services.DshMaintenance.NodeHostRunning();
		}
		catch
		{
			return false;
		}
	}

	/// <summary>打开插件管理窗口（停用并锁定/启用、升级、豁免、卸载）。</summary>
	private void OpenPluginManager_Click(object s, RoutedEventArgs e)
	{
		PluginManagerWindow dlg = new PluginManagerWindow(EffectiveDshDir(), DshCompatService.DefaultProfile, IsDshRunning, delegate (string m)
		{
			Dispatcher.BeginInvoke((Action)delegate { Log(m); }, Array.Empty<object>());
		});
		dlg.Owner = this;
		dlg.ShowDialog();
	}

	private void SetLlamaUpdBtn(bool busy, string busyText = "⏳ 处理中...")
	{
		if (BtnCheckLlamaUpdate == null)
		{
			return;
		}
		BtnCheckLlamaUpdate.IsEnabled = !busy;
		BtnCheckLlamaUpdate.Content = busy ? busyText : "🔄 检查更新";
	}

	private void SetDshUpdBtn(bool busy, string busyText = "⏳ 处理中...")
	{
		if (BtnCheckDshUpdate == null)
		{
			return;
		}
		BtnCheckDshUpdate.IsEnabled = !busy;
		BtnCheckDshUpdate.Content = busy ? busyText : "🔄 检查更新";
	}

	/// <summary>
	/// llama.cpp 检查/更新。fromButton=true 时完整交互（错误与已是最新都提示）；
	/// fromButton=false 供启动后台检查，仅在发现新版且 Llama 未运行时弹确认框。
	/// </summary>
	private async Task CheckLlamaUpdateAsync(bool fromButton)
	{
		try
		{
			if (IsLlamaRunning())
			{
				if (fromButton)
				{
					ShowNotice("Llama 服务正在运行，请先停止再更新。", isError: true);
				}
				else
				{
					Log("[更新] 后台检测到 llama.cpp 有新版本，但 Llama 服务正在运行，跳过（请手动停止后点“检查更新”）");
				}
				return;
			}
			string dir = EffectiveLlamaDir();
			if (UpdateService.FindLlamaServerExe(dir) == null)
			{
				if (fromButton)
				{
					ShowNotice("未找到 llama-server.exe，请先在“Llama 程序目录”填写实际路径。", isError: true);
				}
				return;
			}
			if (_llamaUpdBusy)
			{
				SetLlamaUpdBtn(false);
				return;
			}
			_llamaUpdBusy = true;
			SetLlamaUpdBtn(true, "⏳ 检查中...");
			int? local = await Task.Run(() => UpdateService.GetLocalLlamaBuild(dir));
			string repo = CurrentLlamaRepo();
			Services.UpdateService.LlamaReleaseInfo? latest = await Task.Run(() => UpdateService.FetchLatestLlamaRelease(repo, LlamaBackendPreference()));
			SetLlamaUpdBtn(false);
			if (latest == null)
			{
				if (fromButton)
				{
					ShowNotice("无法获取 llama.cpp 最新版本（网络不通, 或 GitHub 限流——匿名 API 每小时 60 次）。", isError: true);
				}
				return;
			}
			if (latest.Build > 0 && local.HasValue && latest.Build <= local.Value)
			{
				string upstream = string.IsNullOrEmpty(latest.StableVersion)
					? latest.Tag + "（滚动构建）"
					: latest.Tag + "（" + latest.StableVersion + " 稳定版）";
				string mismatch = local.Value != latest.Build
					? "\n\n⚠ 本地 build 与上游 release 不一致——目录里可能是分支/官网 CI 构建（不含该仓库 release 的专属量化类型）。若加载模型报 ggml type 错误, 请删除该目录后重新安装。"
					: "";
				Log("[更新] llama.cpp 已是最新: build " + local.Value + "（上游 " + upstream + "）");
				if (fromButton)
				{
					ShowNotice("llama.cpp 已是最新版本。\n当前: build " + local.Value + "\n上游: " + upstream + mismatch);
				}
				return;
			}
			string current = local.HasValue ? "build " + local.Value : "未知";
			string latestDesc = string.IsNullOrEmpty(latest.StableVersion)
				? latest.Tag + "（滚动构建, build " + latest.Build + "）"
				: latest.Tag + "（" + latest.StableVersion + " 稳定版, build " + latest.Build + "）";
			string msg = "发现 llama.cpp 新版本:\n\n"
				+ "  当前: " + current + "\n"
				+ "  来源仓库: " + repo + "\n"
				+ "  上游: " + latestDesc + "\n"
				+ "  预编译包: " + latest.AssetName + "\n\n"
				+ "将下载并覆盖更新到:\n  " + dir + "\n\n"
				+ "（仅更新二进制，不触碰模型；若上游参数有变，更新后点“⟳ 重载参数配置”即可）";
			if (System.Windows.MessageBox.Show(msg, "更新 llama.cpp", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
			{
				Log("[更新] 用户取消 llama.cpp 更新");
				return;
			}
			// 确认框期间服务可能已被启动: 复检, 不带锁覆盖运行中的 exe
			if (IsLlamaRunning())
			{
				SetLlamaUpdBtn(false);
				ShowNotice("Llama 已在运行，更新已取消。请先停止服务再更新。", isError: true);
				return;
			}
			SetLlamaUpdBtn(true, "⏳ 下载更新中...");
			(bool ok, string m) = await UpdateService.ApplyLlamaUpdateAsync(dir, latest);
			SetLlamaUpdBtn(false);
			UpdateService.CleanupLauncherTemp();
			Log("[更新] " + m);
			if (ok)
			{
				ShowNotice(m + "\n\n可点“⟳ 重载参数配置”加载新版本参数。");
			}
			else
			{
				ShowNotice("更新失败:\n" + m, isError: true);
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "llama.cpp 更新");
			SetLlamaUpdBtn(false);
		}
		finally
		{
			_llamaUpdBusy = false;
		}
	}

	/// <summary>
	/// deepseek-harness 检查/更新。仅“编译版”模式支持 git 更新；
	/// 落后上游时 git pull --ff-only + 复用既有全量重建（clean + build:lib + build:web）。
	/// </summary>
	private async Task CheckDshUpdateAsync(bool fromButton)
	{
		try
		{
			if (ComboDshMode.SelectedIndex != 0)
			{
				if (fromButton)
				{
					ShowNotice("当前为 npx 一键部署模式，无法 git 更新。请切换到“编译版”模式。", isError: true);
				}
				return;
			}
			if (IsDshRunning())
			{
				if (fromButton)
				{
					ShowNotice("DSH 正在运行，请先停止再更新。", isError: true);
				}
				else
				{
					Log("[更新] 后台检测到 deepseek-harness 有更新，但 DSH 正在运行，跳过（请手动停止后点“检查更新”）");
				}
				return;
			}
			string dir = EffectiveDshDir();
			if (string.IsNullOrEmpty(dir))
			{
				if (fromButton)
				{
					ShowNotice("未找到 deepseek-harness 安装目录。\n\n请点“🔍 自动检索”，或在“DSH 项目目录”填写实际源码路径。", isError: true);
				}
				return;
			}
			if (!Directory.Exists(Path.Combine(dir, ".git")))
			{
				if (fromButton)
				{
					ShowNotice("DSH 目录不是 git 仓库: " + dir + "\n请先在“DSH 项目目录”填写实际仓库路径。", isError: true);
				}
				return;
			}
			SetDshUpdBtn(true, "⏳ 检查中...");
			Services.UpdateService.DshUpdateInfo? info = null;
			string checkError = "";
			await Task.Run(delegate
			{
				info = UpdateService.CheckDshUpdate(dir, out checkError);
			});
			SetDshUpdBtn(false);
			if (info == null)
			{
				Log("[更新] DSH 上游检查失败: " + checkError);
				if (fromButton)
				{
					ShowNotice("无法检查 DSH 上游：\n" + checkError
						+ "\n\n启动器已自动尝试直连与 gh-proxy 镜像；若仍失败，请检查网络/代理，或先手动 git fetch 验证。", isError: true);
				}
				return;
			}
			if (!string.Equals(info.Mirror, "origin", StringComparison.OrdinalIgnoreCase))
			{
				Log("[更新] 直连 GitHub 失败，已通过镜像获取上游: " + info.Mirror);
			}
			if (info.Ahead > 0)
			{
				if (fromButton)
				{
					ShowNotice("本地领先上游 " + info.Ahead + " 个提交（存在本地改动），为避免冲突不会自动更新。\n请自行处理本地提交后重试。", isError: true);
				}
				else
				{
					Log("[更新] DSH 本地领先上游 " + info.Ahead + " 个提交，跳过自动更新");
				}
				return;
			}
			if (info.Behind <= 0)
			{
				Log("[更新] DSH 已是最新: " + info.LocalHead.Substring(0, 7) + "（分支 " + info.Branch + "）");
				if (fromButton)
				{
					ShowNotice("deepseek-harness 已是最新。\n当前: " + info.LocalHead.Substring(0, 7) + " (" + info.Branch + ")");
				}
				return;
			}
			string msg = "发现 deepseek-harness 上游更新（落后 " + info.Behind + " 个提交）:\n\n"
				+ "  当前: " + info.LocalHead.Substring(0, 7) + "\n"
				+ "  上游: " + info.RemoteHead.Substring(0, 7) + " (" + info.Branch + ")\n\n"
				+ "将执行:\n  1. git pull --ff-only\n  2. 全量重建 (clean + build:lib + build:web)\n\n继续？";
			if (System.Windows.MessageBox.Show(msg, "更新 deepseek-harness", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
			{
				Log("[更新] 用户取消 DSH 更新");
				return;
			}
			SetDshUpdBtn(true, "⏳ 拉取中...");
			(bool ok, string m) = await Task.Run(() => UpdateService.PullDsh(dir, delegate (string msg) { Log(msg); }));
			if (!ok)
			{
				SetDshUpdBtn(false);
				Log("[更新] DSH git pull 失败: " + m);
				ShowNotice("git pull 失败:\n" + m + "\n\n（可能有本地未提交改动，或上游历史已改写。请手动处理。）", isError: true);
				return;
			}
			SetDshUpdBtn(false);
			string newHead = m;
			Log("[更新] DSH 已拉取到 " + newHead.Substring(0, 7) + "，开始全量重建...");
			StartDshRebuild(dir, newHead, delegate
			{
				UpdateService.CleanupLauncherTemp();
				Log("[更新] DSH 更新完成，临时文件已清理");
			});
		}
		catch (Exception ex)
		{
			LogError(ex, "DSH 更新");
			SetDshUpdBtn(false);
		}
	}

	// ==================== DSH 兼容性检查/修复 ====================

	private void CheckDshCompat_Click(object s, RoutedEventArgs e)
	{
		_ = CheckDshCompatAsync(fromButton: true);
	}

	private void SetDshCompatBtn(bool busy, string busyText = "⏳ 处理中...")
	{
		if (BtnDshCompat == null)
		{
			return;
		}
		BtnDshCompat.IsEnabled = !busy;
		BtnDshCompat.Content = busy ? busyText : "🩺 兼容性检查/修复";
	}

	private void LogCompatReport(DshCompatReport report)
	{
		Log("--- [兼容] 错误 " + report.ErrorCount + " · 警告 " + report.WarningCount + " · 可修复 " + report.RepairableCount + " ---");
		foreach (DshCompatItem item in report.Items)
		{
			if (item.Level == DshCompatLevel.Info)
			{
				continue;
			}
			Log("    " + item);
		}
	}

	private string BuildCompatSummary(DshCompatReport report)
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("DSH " + report.DshVersion + " · profile " + report.Profile);
		sb.AppendLine("错误 " + report.ErrorCount + " · 警告 " + report.WarningCount + " · 可自动修复 " + report.RepairableCount);
		sb.AppendLine();
		int shown = 0;
		foreach (DshCompatItem item in report.Items)
		{
			if (item.Level == DshCompatLevel.Info && !item.Repairable)
			{
				continue;
			}
			if (shown++ >= 28)
			{
				sb.AppendLine("… 其余见启动器日志");
				break;
			}
			sb.AppendLine(item.ToString());
		}
		if (report.LinkIssues.Count > 0)
		{
			int relink = 0;
			foreach (DshLinkIssue l in report.LinkIssues)
			{
				if (l.Action == "relink")
				{
					relink++;
				}
			}
			sb.AppendLine("· 失效链接 " + report.LinkIssues.Count + " 条（可重连 " + relink + " / 可清理 " + (report.LinkIssues.Count - relink) + "）");
		}
		return sb.ToString().TrimEnd();
	}

	/// <summary>手动入口：完整检查 + 一键修复（需先停止 DSH）。</summary>
	private async Task CheckDshCompatAsync(bool fromButton)
	{
		if (_dshCompatRunning)
		{
			return;
		}
		try
		{
			bool dshRunning = IsDshRunning();
			if (dshRunning)
			{
				Log("[兼容] 检测到 DSH 正在运行：本次仅执行只读检查；如需自动修复请先停止 DSH。");
			}
			string dir = EffectiveDshDir();
			string profile = DshCompatService.DefaultProfile;
			_dshCompatRunning = true;
			SetDshCompatBtn(true, "⏳ 检查中...");
			Log("--- [兼容] 开始 DSH 兼容性检查 (" + profile + ") ---");
			DshCompatReport report = await Task.Run(delegate
			{
				return DshCompatService.Audit(dir, profile, delegate (string m)
				{
					Dispatcher.BeginInvoke((Action)delegate { Log(m); }, Array.Empty<object>());
				});
			});
			LogCompatReport(report);
			SetDshCompatBtn(false);
			if (!report.HasRepairable)
			{
				ShowNotice(BuildCompatSummary(report) + "\n\n未发现可自动修复的问题。", isError: true);
				return;
			}
			if (System.Windows.MessageBox.Show(BuildCompatSummary(report) + "\n\n是否立即执行自动修复？\n（会先备份 package.json / cordis.patch.yml）", "DSH 兼容性修复", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
			{
				Log("[兼容] 用户取消自动修复");
				return;
			}
			await RunDshCompatRepairAsync(dir, profile, report);
		}
		catch (Exception ex)
		{
			LogError(ex, "DSH 兼容性检查/修复");
			SetDshCompatBtn(false);
		}
		finally
		{
			_dshCompatRunning = false;
		}
	}

	private async Task RunDshCompatRepairAsync(string dir, string profile, DshCompatReport report)
	{
		if (IsDshRunning())
		{
			ShowNotice("DSH 正在运行，无法写入修复。\n\n请先在启动器中停止 DSH，再执行自动修复。", isError: true);
			Log("[兼容] DSH 正在运行，已取消自动修复。");
			return;
		}
		SetDshCompatBtn(true, "⏳ 修复中...");
		Log("--- [兼容] 开始自动修复 ---");
		DshCompatRepairResult res = await Task.Run(delegate
		{
			return DshCompatService.Repair(dir, profile, report, delegate (string m)
			{
				Dispatcher.BeginInvoke((Action)delegate { Log(m); }, Array.Empty<object>());
			});
		});
		foreach (string m in res.Messages)
		{
			Log("    " + m);
		}
		LogCompatReport(res.Report);
		SetDshCompatBtn(false);
		string verdict = res.Completed
			? "兼容性修复完成：复检无错误、无待修复项。"
			: "自动修复已执行，但复检仍有 错误 " + res.RemainingErrors + " · 可修复 " + res.RemainingRepairable + "。\n"
				+ "可修复项会在下次启动/再次修复时重试；上游 peer 不兼容需插件作者发版或显式放行。";
		ShowNotice(verdict + "\n\n" + string.Join("\n", res.Messages) + "\n\n" + BuildCompatSummary(res.Report), isError: true);
		// 修复后仍不兼容的插件：放行与否由用户逐次决定（工具不代替用户接受风险）
		List<DshPluginInfo> stillBad = DshCompatService.ExemptCandidates(res.Report);
		if (stillBad.Count > 0 && !string.IsNullOrEmpty(res.Report.DshVersion))
		{
			string list = string.Join("\n", stillBad.Select((DshPluginInfo p) => "  · " + p.Name + "@" + p.Version));
			MessageBoxResult decide = System.Windows.MessageBox.Show(
				"以下插件在本次修复后仍与 DSH " + res.Report.DshVersion + " 不兼容：\n" + list
				+ "\n\n是否放行（写入精确版本豁免，允许其在当前 DSH 上运行）？\n"
				+ "放行只跳过版本校验：若插件与当前运行时真实不兼容，仍可能崩溃或丢数据。\n"
				+ "选“否”即保持现状，可稍后在插件管理窗口逐个放行或停用。",
				"修复后放行确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
			if (decide == MessageBoxResult.Yes)
			{
				foreach (DshPluginInfo p in stillBad)
				{
					string ex = DshCompatService.ExemptPlugin(profile, p.Name, p.Version, res.Report.DshVersion);
					Log("    [放行] " + ex);
				}
				ShowNotice("已放行 " + stillBad.Count + " 个插件（写入 compatibility.json）。\n下次兼容性检查将不再报其版本不兼容。");
			}
			else
			{
				Log("    [放行] 用户选择不放行（保持现状）");
			}
		}
	}

	// ==================== 插件智能检测 / 修复 ====================

	private void CheckDshPlugins_Click(object s, RoutedEventArgs e)
	{
		_ = CheckDshPluginsAsync();
	}

	private void SetDshPluginsBtn(bool busy, string busyText = "⏳ 处理中...")
	{
		if (BtnDshPlugins == null)
		{
			return;
		}
		BtnDshPlugins.IsEnabled = !busy;
		BtnDshPlugins.Content = busy ? busyText : "🧩 插件智能检测/修复";
	}

	/// <summary>把插件状态整理成人类可读的清单文本。</summary>
	private string BuildPluginSummary(DshCompatReport report)
	{
		StringBuilder sb = new StringBuilder();
		List<DshPluginInfo> plugins = report.Plugins ?? new List<DshPluginInfo>();
		int abnormal = 0;
		foreach (DshPluginInfo p in plugins)
		{
			if (p.Installed && p.StateLabel != "已启用")
			{
				abnormal++;
			}
		}
		sb.AppendLine("DSH " + report.DshVersion + " · profile " + report.Profile + " · 插件 " + plugins.Count + " 个");
		sb.AppendLine("异常 " + abnormal + " 个 · 市场停用 " + report.MarketDisabled.Count + " 条 · 失效补丁目标 " + report.PatchIssues.Count + " 个 · 失效链接 " + report.LinkIssues.Count + " 条");
		sb.AppendLine();
		foreach (DshPluginInfo p in plugins)
		{
			string mark = !p.Installed ? "○" : (p.StateLabel == "已启用" ? "✔" : "✖");
			string extra = "";
			if (p.MissingSpecs.Count > 0)
			{
				extra += "  缺依赖: " + string.Join(", ", p.MissingSpecs);
			}
			if (p.Compatible == false)
			{
				extra += "  peer 不兼容: " + string.Join(", ", p.PeerIssues.Select(delegate (KeyValuePair<string, string> kv) { return kv.Key + "@" + kv.Value; }));
			}
			if (p.DisabledByPatch)
			{
				extra += "  补丁停用";
			}
			if (p.DisabledByMarket)
			{
				extra += "  市场停用";
			}
			sb.AppendLine(mark + " " + p.Name + "  [" + p.StateLabel + "]" + (p.Version.Length > 0 ? "  v" + p.Version : "") + extra);
		}
		if (report.PatchIssues.Count > 0)
		{
			sb.AppendLine();
			sb.AppendLine("补丁目标异常 " + report.PatchIssues.Count + " 个（示例）:");
			int shown = 0;
			foreach (DshPatchIssue issue in report.PatchIssues)
			{
				if (shown++ >= 8)
				{
					sb.AppendLine("  ... 其余见启动器日志");
					break;
				}
				sb.AppendLine("  - " + issue.Id + " (" + issue.Kind + ")" + (issue.Detail.Length > 0 ? "  " + issue.Detail : ""));
			}
		}
		return sb.ToString().TrimEnd();
	}

	/// <summary>智能检测插件状态。检测为只读，DSH 运行中也可执行；修复前需停止 DSH。</summary>
	private async Task CheckDshPluginsAsync()
	{
		if (_dshPluginsRunning)
		{
			return;
		}
		try
		{
			_dshPluginsRunning = true;
			SetDshPluginsBtn(true, "⏳ 检测中...");
			string dir = EffectiveDshDir();
			string profile = DshCompatService.DefaultProfile;
			Log("--- [插件] 开始智能检测插件状态 (" + profile + ") ---");
			DshCompatReport report = await Task.Run(delegate
			{
				return DshCompatService.Audit(dir, profile, delegate (string m)
				{
					Dispatcher.BeginInvoke((Action)delegate { Log(m); }, Array.Empty<object>());
				});
			});
			foreach (DshPluginInfo p in report.Plugins)
			{
				Log("    " + p.Name + " [" + p.StateLabel + "]" + (p.Installed ? "" : " (未安装)"));
			}
			SetDshPluginsBtn(false);
			bool repairable = report.HasRepairable;
			string tail = repairable
				? "\n\n检测到可自动修复的问题，是否立即修复？\n（修复会先备份 package.json / cordis.patch.yml / state.json，需先停止 DSH）"
				: "\n\n未发现可自动修复的问题。";
			MessageBoxResult r = System.Windows.MessageBox.Show(BuildPluginSummary(report) + tail, "DSH 插件智能检测", repairable ? MessageBoxButton.YesNo : MessageBoxButton.OK, report.ErrorCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
			if (repairable && r == MessageBoxResult.Yes)
			{
				await RunDshCompatRepairAsync(dir, profile, report);
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "插件智能检测/修复");
			SetDshPluginsBtn(false);
		}
		finally
		{
			_dshPluginsRunning = false;
		}
	}

	/// <summary>重建完成后自动做一次兼容性检查；发现可修复问题时询问是否修复，然后继续原回调。</summary>
	private async Task CompleteRebuildWithCompatAsync(Action? done)
	{
		try
		{
			string dir = string.IsNullOrEmpty(_dshRebuildDir) ? EffectiveDshDir() : _dshRebuildDir;
			string profile = DshCompatService.DefaultProfile;
			Log("--- [兼容] 重建完成，开始 DSH 升级后兼容性检查 ---");
			DshCompatReport report = await Task.Run(delegate
			{
				return DshCompatService.Audit(dir, profile, delegate (string m)
				{
					Dispatcher.BeginInvoke((Action)delegate { Log(m); }, Array.Empty<object>());
				});
			});
			LogCompatReport(report);
			if (report.HasRepairable && System.Windows.MessageBox.Show(BuildCompatSummary(report) + "\n\n是否立即自动修复？", "DSH 升级后兼容性修复", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
			{
				await RunDshCompatRepairAsync(dir, profile, report);
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "重建后兼容性检查");
		}
		finally
		{
			done?.Invoke();
		}
	}

	/// <summary>启动后延迟几秒做一次后台检查，发现新版弹窗确认；无更新不打扰。</summary>
	private void StartBackgroundUpdateCheck()
	{
		DispatcherTimer bg = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(6.0)
		};
		bg.Tick += async delegate
		{
			bg.Stop();
			Log("[更新] 后台检查 llama.cpp / deepseek-harness ...");
			try
			{
				await CheckLlamaUpdateAsync(fromButton: false);
			}
			catch (Exception ex)
			{
				LogError(ex, "后台检查 llama.cpp 更新");
			}
			try
			{
				await CheckDshUpdateAsync(fromButton: false);
			}
			catch (Exception ex)
			{
				LogError(ex, "后台检查 DSH 更新");
			}
		};
		bg.Start();
	}

	private void DshModeChanged(object s, SelectionChangedEventArgs e)
	{
		if (InputDshDir != null && BtnSearchDsh != null)
		{
			bool isEnabled = ComboDshMode.SelectedIndex == 0;
			InputDshDir.IsEnabled = isEnabled;
			BtnSearchDsh.IsEnabled = isEnabled;
		}
	}

	private void WireLiveRefresh()
	{
		InputModelPath.TextChanged += delegate
		{
			// AutoFill(目录扫描)与 RefreshCmd(建命令含磁盘探测)统一延后合并执行
			_pendingAutoFillPath = InputModelPath.Text;
			RefreshCmd();
		};
		InputMtpPath.TextChanged += delegate
		{
			RefreshCmd();
		};
		InputMmprojPath.TextChanged += delegate
		{
			RefreshCmd();
		};
		InputMmprojUrl.TextChanged += delegate
		{
			RefreshCmd();
		};
		ChkNoMmprojOffload.Checked += delegate
		{
			RefreshCmd();
		};
		ChkNoMmprojOffload.Unchecked += delegate
		{
			RefreshCmd();
		};
		InputLlamaDir.TextChanged += delegate
		{
			RefreshCmd();
		};
		InputDshDir.TextChanged += delegate
		{
			RefreshCmd();
		};
	}

	private void AutoFill(string mp)
	{
		if (_pw.TryGetValue("alias", out (System.Windows.Controls.CheckBox, System.Windows.Controls.Control) value))
		{
			string text = (string.IsNullOrWhiteSpace(mp) ? "" : Path.GetFileNameWithoutExtension(mp.Trim().Trim('"')));
			if (value.Item2 is System.Windows.Controls.TextBox textBox)
			{
				string cur = textBox.Text ?? "";
				// 仅当 alias 为空或仍是上一次自动填充的值时才覆盖;
				// 用户手改过的 alias 不再被模型路径变化静默冲掉。
				if (cur != text && (string.IsNullOrWhiteSpace(cur) || cur == _lastAutoFilledAlias))
				{
					textBox.Text = text;
					_lastAutoFilledAlias = text;
				}
				else if (cur == text)
				{
					_lastAutoFilledAlias = text;
				}
			}
		}
		try
		{
			string p2 = mp.Trim().Trim('"');
			bool mfOk = false;
			List<string> hints = new List<string>();
			if (!string.IsNullOrEmpty(p2) && Directory.Exists(p2))
			{
				string[] fs = Directory.GetFiles(p2, "*-00001-of-*.gguf");
				string[] ggs0 = Directory.GetFiles(p2, "*.gguf");
				string[] sts0 = Directory.GetFiles(p2, "*.safetensors");
				if (fs.Length > 0)
				{
					Array.Sort(fs, StringComparer.OrdinalIgnoreCase);
					hints.Add("[提示] 目录模式: 使用分片第一片 " + Path.GetFileName(fs[0]) + "(llama.cpp 自动加载其余分片)");
					p2 = fs[0];
					mfOk = true;
				}
				else if (ggs0.Length == 1)
				{
					hints.Add("[提示] 目录模式: 使用 " + Path.GetFileName(ggs0[0]));
					p2 = ggs0[0];
					mfOk = true;
				}
				else if (ggs0.Length > 1)
				{
					hints.Add("[提示] 目录中存在多个 GGUF(" + ggs0.Length + " 个)，请直接选择主模型文件");
				}
				else if (sts0.Length > 0)
				{
					hints.Add("[提示] 目录中是 safetensors 分片(" + sts0.Length + " 个文件)，llama.cpp 不能直接加载。请先转换 GGUF：python convert_hf_to_gguf.py \"" + p2 + "\" --outfile model.gguf");
				}
				else
				{
					hints.Add("[提示] 目录中未找到模型文件");
				}
			}
			else if (!string.IsNullOrEmpty(p2) && File.Exists(p2))
			{
				mfOk = true;
				if (p2.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
				{
					hints.Add("[提示] llama.cpp 不能直接加载 .safetensors，请先转换为 GGUF：python convert_hf_to_gguf.py \"" + (Path.GetDirectoryName(p2) ?? ".") + "\" --outfile model.gguf");
				}
			}
			if (mfOk)
			{
				string dir = Path.GetDirectoryName(p2) ?? "";
				Match shm = Regex.Match(Path.GetFileName(p2), "-(\\d{5})-of-(\\d{5})\\.gguf$", RegexOptions.IgnoreCase);
				if (shm.Success)
				{
					hints.Add("[提示] 检测到分片 GGUF (" + shm.Groups[2].Value + " 片)：llama.cpp 会自动加载同目录其余分片，请勿移动/改名");
				}
				if (string.IsNullOrWhiteSpace(InputMmprojPath.Text))
				{
					string[] mms = Directory.GetFiles(dir, "mmproj*.gguf");
					if (mms.Length == 1)
					{
						InputMmprojPath.Text = mms[0];
						hints.Add("[提示] 已自动关联同目录多模态投影仪: " + Path.GetFileName(mms[0]));
					}
					else if (mms.Length > 1)
					{
						hints.Add("[提示] 同目录发现 " + mms.Length + " 个 mmproj 文件，请手动选择(浏览...)");
					}
				}
			}
			// 只在一组提示发生变化时写日志，避免每次击键都重复刷屏
			string joinedHints = string.Join("\n", hints);
			if (joinedHints != _lastAutoFillHints)
			{
				_lastAutoFillHints = joinedHints;
				foreach (string h in hints)
				{
					Log(h);
				}
			}
		}
		catch
		{
		}
	}


	// 模型路径解析：支持 ①单 gguf 文件 ②分片第一片 ③模型目录(MoE 大模型的分片场景)。
	private static bool stringComparerOl(string a, string b)
	{
		return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0;
	}

	// 目录解析顺序：分片第一片(*-00001-of-*.gguf) → 目录中唯一 gguf → safetensors 报错提示转换。
	// throwOnError=true 供 BuildCmd 使用(错误显示在命令预览/日志)；false 供显存估算静默失败。
	private string? ResolveModelPath(bool throwOnError)
	{
		string raw = InputModelPath.Text.Trim().Trim('"');
		if (string.IsNullOrEmpty(raw))
		{
			return null;
		}
		if (Directory.Exists(raw))
		{
			string[] firstShards = Directory.GetFiles(raw, "*-00001-of-*.gguf");
			if (firstShards.Length > 0)
			{
				// 同目录多套分片时字典序第一片可能属于另一套模型:
				// 按"同前缀分片数量最多"选家族, 平手再按文件名
				string best = firstShards[0];
				int bestCount = -1;
				foreach (string f in firstShards)
				{
					string famDir = Path.GetDirectoryName(f) ?? "";
					string stem = Path.GetFileNameWithoutExtension(f);
					int cut = stem.LastIndexOf("-00001-of-", StringComparison.Ordinal);
					if (cut <= 0)
					{
						continue;
					}
					int cnt = Directory.GetFiles(famDir, stem.Substring(0, cut) + "-*-of-*.gguf").Length;
					if (cnt > bestCount || (cnt == bestCount && stringComparerOl(f, best)))
					{
						bestCount = cnt;
						best = f;
					}
				}
				return best;
			}
			string[] ggu = Directory.GetFiles(raw, "*.gguf");
			if (ggu.Length == 1)
			{
				return ggu[0];
			}
			if (ggu.Length > 1)
			{
				string msg = "目录中存在多个 GGUF(" + ggu.Length + " 个)，请直接选择主模型文件: " + raw;
				if (throwOnError) throw new FileNotFoundException(msg);
				Log("[提示] " + msg);
				return null;
			}
			string[] st = Directory.GetFiles(raw, "*.safetensors");
			if (st.Length > 0)
			{
				string msg2 = "目录中是 safetensors 分片(" + st.Length + " 个文件)，llama.cpp 不能直接加载。请先转换为 GGUF：python convert_hf_to_gguf.py \"" + raw + "\" --outfile model.gguf";
				if (throwOnError) throw new NotSupportedException(msg2);
				Log("[提示] " + msg2);
				return null;
			}
			string msg3 = "目录中未找到 GGUF 模型文件: " + raw;
			if (throwOnError) throw new FileNotFoundException(msg3);
			return null;
		}
		if (File.Exists(raw))
		{
			if (raw.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
			{
				string msg4 = "llama.cpp 不能直接加载 .safetensors 文件。请先转换为 GGUF：python convert_hf_to_gguf.py \"" + (Path.GetDirectoryName(raw) ?? ".") + "\" --outfile model.gguf";
				if (throwOnError) throw new NotSupportedException(msg4);
				Log("[提示] " + msg4);
				return null;
			}
			return raw;
		}
		return raw;
	}

	private List<string> BuildCmd(bool includeCustomArgs = true)
	{
		string text = EffectiveLlamaDir();
		if (string.IsNullOrEmpty(text))
		{
			return new List<string>();
		}
		DirectoryInfo directoryInfo = new DirectoryInfo(text);
		FileInfo fileInfo = (directoryInfo.Exists ? new FileInfo(Path.Combine(directoryInfo.FullName, "llama-server.exe")) : ((!string.IsNullOrEmpty(Path.GetExtension(text))) ? new FileInfo(text) : new FileInfo(text + ".exe")));
		if (!fileInfo.Exists)
		{
			throw new FileNotFoundException($"llama-server.exe not found:{fileInfo}");
		}
		List<string> list = new List<string> { fileInfo.FullName };
		string text2 = ResolveModelPath(throwOnError: true) ?? "";
		if (!string.IsNullOrEmpty(text2))
		{
			list.AddRange(new string[2] { "--model", text2 });
		}
		string text3 = Cl(InputMtpPath.Text);
		if (!string.IsNullOrEmpty(text3) && IsSpecDecodingActive())
		{
			list.AddRange(new string[2] { "--model-draft", text3 });
		}
		string mmprojFile = Cl(InputMmprojPath.Text);
		string mmprojUrl = Cl(InputMmprojUrl.Text);
		if (!string.IsNullOrEmpty(mmprojFile))
		{
			list.AddRange(new string[2] { "--mmproj", mmprojFile });
		}
		else if (!string.IsNullOrEmpty(mmprojUrl))
		{
			list.AddRange(new string[2] { "--mmproj-url", mmprojUrl });
		}
		if ((ChkNoMmprojOffload.IsChecked == true) && (!string.IsNullOrEmpty(mmprojFile) || !string.IsNullOrEmpty(mmprojUrl)))
		{
			list.Add("--no-mmproj-offload");
		}
		// 参数依赖引擎：纯逻辑在 ParamRules（可单测）。前置未满足的参数连 off 语义都不发射，
		// 避免"前置已取消勾选、后置仍被注入启动行"（如 spec-type=none 时仍发 offFlag）。
		ParamBuildResult paramBuild = ParamRules.Build(_paramDefs, this);
		_lastParamBuild = paramBuild;
		list.AddRange(paramBuild.Args);
		_lastViolations = paramBuild.Violations;
		if (includeCustomArgs && _customArgs.Count > 0)
		{
			list.AddRange(_customArgs);
		}
		return list;
		static string Cl(string s)
		{
			s = s.Trim();
			int num2;
			if (s.Length >= 2)
			{
				char num = s[0];
				string text5 = s;
				num2 = ((num == text5[text5.Length - 1]) ? 1 : 0);
			}
			else
			{
				num2 = 0;
			}
			bool flag2 = (byte)num2 != 0;
			if (flag2)
			{
				char c = s[0];
				bool flag3 = ((c == '"' || c == '\'') ? true : false);
				flag2 = flag3;
			}
			if (flag2)
			{
				string text6 = s;
				return text6.Substring(1, text6.Length - 1 - 1);
			}
			return s;
		}
	}

	private static string NormTok(string v)
	{
		return ParamRules.NormTok(v);
	}

	private bool TryGetParamValue(string flag, out string value)
	{
		value = "";
		if (_pw.TryGetValue(flag, out (System.Windows.Controls.CheckBox, System.Windows.Controls.Control) pair) && pair.Item1.IsChecked == true)
		{
			value = (pair.Item2 is System.Windows.Controls.ComboBox cb) ? cb.Text.Trim() : ((System.Windows.Controls.TextBox)pair.Item2).Text.Trim();
			return true;
		}
		return false;
	}

	private static double CacheBytesPerElem(string cacheType)
	{
		// 每元素字节数按 ggml.h 各 block 结构定义换算:
		// Q4_0=20B/32, Q4_1=24B/32, Q5_0=28B/32, Q5_1=32B/32, Q8_0=34B/32,
		// IQ4_NL=20B/32, Q6_K=210B/256(0.8203), F16/BF16=2B, F32=4B
		switch (cacheType.Trim().ToLowerInvariant())
		{
		case "f32":
			return 4.0;
		case "f16":
		case "bf16":
			return 2.0;
		case "q8_0":
			return 1.0625;
		case "q4_0":
		case "iq4_nl":
			return 0.625;
		case "q4_1":
			return 0.75;
		case "q5_0":
			return 0.875;
		case "q5_1":
			return 1.0;
		case "q6_0":
			return 0.8203125;
		default:
			return 2.0;
		}
	}

	private sealed class GgufArchInfo
	{
		public bool Ok;
		public int Layers;
		public int KvHeads;
		public int HeadDim;
		public long ContextLen;
		public string Architecture = "";
		/// <summary>隐藏层维度，用于估算计算图缓冲大小。</summary>
		public long Embed;
		/// <summary>注意力头数，key_length 缺失时用来反推 head_dim。</summary>
		public int HeadCount;
		/// <summary>滑动窗口长度，0 表示没有 SWA。</summary>
		public int SlidingWindow;
		/// <summary>每隔多少层出现一个全注意力层（混合架构模型才有），0 表示全是全注意力。</summary>
		public int FullAttentionInterval;
		/// <summary>内置 MTP 头层数，大于 0 表示模型自带多 token 预测。</summary>
		public int NextNPredictLayers;
		/// <summary>每层专家数（MoE），用于按 --n-cpu-moe 折算显存里的专家权重。</summary>
		public int Experts;
		/// <summary>词表大小（tokenizer.ggml.tokens 数组长度），用于估算输出 logits 缓冲。</summary>
		public long VocabSize;
		/// <summary>线性注意力（SSM/Gated Delta Net）参数：state 不随 ctx 线性增长。</summary>
		public int SsmInnerSize;
		public int SsmStateSize;
		public int SsmConvKernel;
	}

	private static GgufArchInfo ReadGgufArch(string modelPath)
	{
		GgufArchInfo r = new GgufArchInfo();
		try
		{
			using FileStream fs = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			using BinaryReader br = new BinaryReader(fs);
			if (br.ReadUInt32() != 0x46554747u)
			{
				return r;
			}
			_ = br.ReadUInt32();
			_ = br.ReadUInt64();
			ulong kvCount = br.ReadUInt64();
			int embed = 0;
			int heads = 0;
			for (ulong i = 0; i < kvCount; i++)
			{
				string key = ReadGgufString(br);
				uint vt = br.ReadUInt32();
				switch (vt)
				{
				case 4:
				case 5:
				{
					uint v = br.ReadUInt32();
					if (key.EndsWith(".block_count", StringComparison.Ordinal))
					{
						r.Layers = (int)v;
					}
					else if (key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal))
					{
						r.KvHeads = (int)v;
					}
					else if (key.EndsWith(".attention.head_count", StringComparison.Ordinal))
					{
						heads = (int)v;
						r.HeadCount = (int)v;
					}
					else if (key.EndsWith(".attention.key_length", StringComparison.Ordinal))
					{
						r.HeadDim = (int)v;
					}
					else if (key.EndsWith(".attention.value_length", StringComparison.Ordinal) && r.HeadDim == 0)
					{
						r.HeadDim = (int)v;
					}
					else if (key.EndsWith(".embedding_length", StringComparison.Ordinal))
					{
						embed = (int)v;
						r.Embed = v;
					}
					else if (key.EndsWith(".context_length", StringComparison.Ordinal))
					{
						r.ContextLen = v;
					}
					else if (key.EndsWith(".sliding_window", StringComparison.Ordinal))
					{
						r.SlidingWindow = (int)v;
					}
					else if (key.EndsWith(".full_attention_interval", StringComparison.Ordinal))
					{
						r.FullAttentionInterval = (int)v;
					}
					else if (key.EndsWith(".nextn_predict_layers", StringComparison.Ordinal))
					{
						r.NextNPredictLayers = (int)v;
					}
					else if (key.EndsWith(".expert_count", StringComparison.Ordinal))
					{
						r.Experts = (int)v;
					}
					else if (key.EndsWith(".ssm.inner_size", StringComparison.Ordinal))
					{
						r.SsmInnerSize = (int)v;
					}
					else if (key.EndsWith(".ssm.state_size", StringComparison.Ordinal))
					{
						r.SsmStateSize = (int)v;
					}
					else if (key.EndsWith(".ssm.conv_kernel", StringComparison.Ordinal))
					{
						r.SsmConvKernel = (int)v;
					}
					break;
				}
				case 10:
				case 11:
				{
					ulong v = br.ReadUInt64();
					if (key.EndsWith(".context_length", StringComparison.Ordinal))
					{
						r.ContextLen = (long)v;
					}
					break;
				}
				case 8:
				{
					string v = ReadGgufString(br);
					if (key == "general.architecture")
					{
						r.Architecture = v;
					}
					break;
				}
				case 9:
				{
					// 数组: tokenizer.ggml.tokens 的长度即词表大小, 输出 logits 缓冲估算需要;
					// 字符串元素只读长度后 seek, 免去十几万次 UTF-8 解码
					uint elemType = br.ReadUInt32();
					ulong count = br.ReadUInt64();
					if (key == "tokenizer.ggml.tokens")
					{
						r.VocabSize = (long)count;
					}
					for (ulong j = 0; j < count; j++)
					{
						if (elemType == 8)
						{
							ulong len = br.ReadUInt64();
							br.BaseStream.Position += (long)len;
						}
						else
						{
							SkipGgufValue(br, elemType);
						}
					}
					break;
				}
				default:
					SkipGgufValue(br, vt);
					break;
				}
			}
			if (r.HeadDim == 0 && heads > 0 && embed > 0)
			{
				r.HeadDim = (int)Math.Round((double)embed / heads);
			}
			if (r.Layers > 0 && r.KvHeads > 0 && r.HeadDim > 0)
			{
				r.Ok = true;
			}
		}
		catch
		{
			r.Ok = false;
		}
		return r;
	}

	private static string ReadGgufString(BinaryReader br)
	{
		ulong len = br.ReadUInt64();
		if (len > 4194304UL)
		{
			throw new InvalidDataException("bad gguf string length");
		}
		return System.Text.Encoding.UTF8.GetString(br.ReadBytes((int)len));
	}

	private static void SkipGgufValue(BinaryReader br, uint type)
	{
		switch (type)
		{
		case 0:
		case 1:
		case 7:
			br.BaseStream.Position += 1;
			break;
		case 2:
		case 3:
			br.BaseStream.Position += 2;
			break;
		case 4:
		case 5:
		case 6:
			br.BaseStream.Position += 4;
			break;
		case 8:
			_ = ReadGgufString(br);
			break;
		case 9:
		{
			uint elemType = br.ReadUInt32();
			ulong count = br.ReadUInt64();
			for (ulong j = 0; j < count; j++)
			{
				SkipGgufValue(br, elemType);
			}
			break;
		}
		case 10:
		case 11:
		case 12:
			br.BaseStream.Position += 8;
			break;
		default:
			throw new InvalidDataException("unknown gguf value type: " + type);
		}
	}

	/// <summary>
	/// GGUF 分片权重求和: --model 传第一片是 llama 的分片加载约定,
	/// 只取单片会漏掉其余 N-1 片的权重(27B 五分片会被低估到 1/5)。
	/// </summary>
	private static double GgufShardBytes(string firstShardPath)
	{
		return GgufShardPaths(firstShardPath).Sum((string p) => new FileInfo(p).Length);
	}

	/// <summary>枚举 GGUF 分片路径（--model 只传第一片，其余分片按命名约定补全）。</summary>
	private static List<string> GgufShardPaths(string firstShardPath)
	{
		List<string> paths = new List<string> { firstShardPath };
		Match m = Regex.Match(Path.GetFileName(firstShardPath), @"-(\d{5})-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase);
		if (!m.Success)
		{
			return paths;
		}
		string dir = Path.GetDirectoryName(firstShardPath) ?? ".";
		string stem = Path.GetFileName(firstShardPath).Substring(0, m.Index);
		if (!int.TryParse(m.Groups[2].Value, out int total))
		{
			return paths;
		}
		for (int i = 2; i <= total; i++)
		{
			string p = Path.Combine(dir, string.Format("{0}-{1:D5}-of-{2:D5}.gguf", stem, i, total));
			if (File.Exists(p))
			{
				paths.Add(p);
			}
		}
		return paths;
	}

	/// <summary>GGUF 张量构成的精确统计（按层拆分专家/非专家字节）。</summary>
	private sealed class GgufTensorStats
	{
		public bool Ok;
		/// <summary>部分分片解析成功、部分失败: 统计不完整, 预估偏乐观, 必须向用户明示。</summary>
		public bool Partial;
		public int LayerCount;
		/// <summary>token_embd / output 等非层张量（ngl>0 时在 GPU）。</summary>
		public double EmbedOutputBytes;
		/// <summary>每层的非专家权重（attention + 共享路由等）。</summary>
		public double[] DensePerLayer = Array.Empty<double>();
		/// <summary>每层的专家 FFN 权重（--n-cpu-moe 按层移 CPU 的就是它们）。</summary>
		public double[] ExpertPerLayer = Array.Empty<double>();
		public double ExpertTotalBytes;
	}

	/// <summary>GGML 量化类型的每元素字节数（来源 ggml.h 各 block 结构定义；未知类型按 1.0 保守）。</summary>
	private static double GgmlBpe(uint type)
	{
		return type switch
		{
			0 => 4.0,                                   // F32
			1 => 2.0,                                   // F16
			2 => 20.0 / 32,                             // Q4_0
			3 => 24.0 / 32,                             // Q4_1
			4 or 5 => 24.0 / 32,                        // Q4_2/Q4_3(遗留)
			6 => 24.0 / 32,                             // Q5_0
			7 => 28.0 / 32,                             // Q5_1
			8 => 34.0 / 32,                             // Q8_0
			9 => 36.0 / 32,                             // Q8_1
			10 => 84.0 / 256,                           // Q2_K
			11 => 110.0 / 256,                          // Q3_K
			12 => 144.0 / 256,                          // Q4_K
			13 => 176.0 / 256,                          // Q5_K
			14 => 210.0 / 256,                          // Q6_K
			15 => 200.0 / 256,                          // Q8_K
			16 => 66.0 / 256,                           // IQ2_XXS
			17 => 74.0 / 256,                           // IQ2_XS
			18 => 98.0 / 256,                           // IQ3_XXS
			19 => 50.0 / 256,                           // IQ1_S
			20 => 20.0 / 32,                            // IQ4_NL
			21 => 110.0 / 256,                          // IQ3_S
			22 => 98.0 / 256,                           // IQ2_S
			23 => 136.0 / 256,                          // IQ4_XS
			24 => 1.0,                                  // I8
			25 => 2.0,                                  // I16
			26 => 4.0,                                  // I32
			27 => 8.0,                                  // I64
			28 => 8.0,                                  // F64
			29 => 56.0 / 256,                           // IQ1_M
			30 => 2.0,                                  // BF16
			31 or 32 or 33 => 20.0 / 32,                // Q4_0 交错遗留
			34 => 18.0 / 64,                            // TQ1_0
			35 => 84.0 / 256,                           // TQ2_0
			36 => 17.0 / 32,                            // MXFP4
			_ => 1.0,
		};
	}

	/// <summary>
	/// 解析 GGUF 张量表（官方规范: KV 之后 alignment, 再 tensor_count 条
	/// name/n_dims/shape/type/offset），按张量名的层号与是否专家精确拆分字节。
	/// 分片模型的张量表在各分片头部各自列出，逐片累加。
	/// </summary>
	private static GgufTensorStats ReadGgufTensorStats(string modelPath, int layerCount)
	{
		GgufTensorStats r = new GgufTensorStats();
		if (layerCount > 0)
		{
			r.LayerCount = layerCount;
			r.DensePerLayer = new double[layerCount];
			r.ExpertPerLayer = new double[layerCount];
		}
		int shardsTotal = 0;
		int shardsOk = 0;
		foreach (string shard in GgufShardPaths(modelPath))
		{
			shardsTotal++;
			bool parsed = false;
			try
			{
				using FileStream fs = new FileStream(shard, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
				using BinaryReader br = new BinaryReader(fs);
				if (br.ReadUInt32() != 0x46554747u)
				{
					continue;
				}
				uint version = br.ReadUInt32();
				ulong tensorCount = br.ReadUInt64();
				ulong kvCount = br.ReadUInt64();
				for (ulong k = 0; k < kvCount; k++)
				{
					_ = ReadGgufString(br);
					uint vt = br.ReadUInt32();
					SkipGgufValue(br, vt);
				}
				// KV 之后直接是 tensor infos（对齐由 general.alignment KV 键声明, 默认 32,
				// 只影响数据区起点; llama.cpp 写出的 tensor infos 紧跟 KV, 无填充）
				for (ulong t = 0; t < tensorCount; t++)
				{
					string name = ReadGgufString(br);
					uint nDims = br.ReadUInt32();
					ulong elems = 1;
					for (uint d = 0; d < nDims; d++)
					{
						elems *= br.ReadUInt64();
					}
					uint type = br.ReadUInt32();
					_ = br.ReadUInt64();
					double bytes = elems * GgmlBpe(type);
					bool expert = name.Contains("ffn_", StringComparison.Ordinal)
						&& (name.Contains("exps", StringComparison.Ordinal) || name.Contains("shexp", StringComparison.Ordinal));
					Match lm = Regex.Match(name, @"(?:^|\.)blk\.(\d+)\.");
					if (lm.Success)
					{
						int l = int.Parse(lm.Groups[1].Value);
						if (expert)
						{
							r.ExpertTotalBytes += bytes;
							if (l < r.LayerCount)
							{
								r.ExpertPerLayer[l] += bytes;
							}
						}
						else if (l < r.LayerCount)
						{
							r.DensePerLayer[l] += bytes;
						}
					}
					else
					{
						r.EmbedOutputBytes += bytes;
					}
				}
				parsed = true;
			}
			catch
			{
				// 单分片解析失败继续其余分片; 部分成功会标记 Partial, 不再冒充 Ok
			}
			if (parsed)
			{
				shardsOk++;
			}
		}
		// Ok 仅在所有声明分片全部解析成功后置位: 部分成功若被当成功,
		// 显存预估偏乐观 → 启动后 OOM 且用户无从知道估算基于残缺数据。
		r.Ok = shardsOk > 0 && shardsOk == shardsTotal;
		if (shardsOk > 0 && shardsOk < shardsTotal)
		{
			r.Partial = true;
		}
		return r;
	}

	private (double estBytes, double modelBytes, double kvBytes, double availBytes, string detail)? EstimateVramNeeded()
	{
		try
		{
			string modelPath = ResolveModelPath(throwOnError: false) ?? "";
			if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
			{
				return null;
			}
			// 权重: 按 GGUF 分片求和(官方分片加载约定下 --model 只给第一片)
			double fileBytes = GgufShardBytes(modelPath);
			if (fileBytes <= 0.0)
			{
				return null;
			}
			double mmprojBytes = 0.0;
			string mmpp = InputMmprojPath.Text.Trim().Trim('"');
			if (ChkNoMmprojOffload.IsChecked != true && !string.IsNullOrEmpty(mmpp) && File.Exists(mmpp))
			{
				mmprojBytes = new FileInfo(mmpp).Length;
			}
			GgufArchInfo gi = ReadGgufArch(modelPath);
			int layers = gi.Layers;
			int headDim = gi.HeadDim > 0 ? gi.HeadDim : (gi.Embed > 0 && gi.HeadCount > 0 ? (int)(gi.Embed / gi.HeadCount) : 128);
			int fullLayers = layers;
			if (layers > 0 && gi.FullAttentionInterval > 0)
			{
				fullLayers = Math.Max(1, (int)Math.Ceiling(layers / (double)gi.FullAttentionInterval));
			}
			// 元数据体检：专家数解析异常会让 MoE 折算严重跑偏，先把解析结果摆出来。
			Log("[显存] 元数据: layers=" + layers + " experts=" + gi.Experts + " kvHeads=" + gi.KvHeads
				+ " headDim=" + headDim + " interval=" + gi.FullAttentionInterval + " ctx=" + (RoleText("ctx_size", out string ctxDbg) ? NormTok(ctxDbg) : "?"));
			// 权重：解析 GGUF 张量表按层精确拆分专家/非专家（替代"层数比例×文件大小"近似），
			// --n-cpu-moe / --cpu-moe / --ngl / --moe-cache-mib 任意组合下都能算准每层归属
			string metaWarn = "";
			GgufTensorStats ts = ReadGgufTensorStats(modelPath, layers);
			if (ts.Partial)
			{
				metaWarn += "⚠ 张量表仅解析到部分分片, 显存预估可能偏乐观; ";
				Log("[显存] 警告: 分片张量表解析不完整, 估算基于部分数据");
			}
			double ncmV = 0.0;
			bool hasNcm = RoleText("n_cpu_moe", out string ncmStr) && double.TryParse(ncmStr, out ncmV) && ncmV > 0.0;
			// 上 GPU 的层数: ngl=-1/auto 视为全层(fit 会下调, 预算偏保守), "all" 同
			int gpuLayers = layers;
			double gpuFrac = 1.0;
			if (RoleText("ngl", out string nglStr))
			{
				string ns = nglStr.Trim();
				if (double.TryParse(ns, out double nglV) && nglV >= 0.0)
				{
					gpuLayers = (int)Math.Min(nglV, layers);
					if (layers > 0)
					{
						gpuFrac = Math.Clamp(nglV / layers, 0.0, 1.0);
					}
				}
			}
			// 移 CPU 的专家层数: --cpu-moe=全部; --n-cpu-moe N=前 N 层专家(层号 0..N-1)
			int moeCpuLayers = 0;
			if (RoleBool("cpu_moe"))
			{
				moeCpuLayers = layers;
			}
			else if (hasNcm)
			{
				moeCpuLayers = (int)Math.Clamp(Math.Round(ncmV), 0, layers);
			}
			double gpuWeights;
			double expertGpuBytes = 0.0;
			double expertCpuBytes = 0.0;
			string moeDetail = "";
			if (ts.Ok)
			{
				// 非 blk 张量(token_embd/output 等)在 ngl>0 时上 GPU
				double embedOut = gpuLayers > 0 ? ts.EmbedOutputBytes : 0.0;
				double denseOnGpu = 0.0;
				for (int l = 0; l < Math.Min(gpuLayers, ts.LayerCount); l++)
				{
					denseOnGpu += ts.DensePerLayer[l];
					if (l >= moeCpuLayers)
					{
						expertGpuBytes += ts.ExpertPerLayer[l];
					}
				}
				// moe-cache-mib: 专家权重显存缓存上限(MiB), 设了它超出部分驻留 CPU
				if (TryGetParamValue("moe-cache-mib", out string mcmStr) && double.TryParse(mcmStr, out double mcmV) && mcmV > 0.0 && expertGpuBytes > mcmV * 1048576.0)
				{
					expertGpuBytes = mcmV * 1048576.0;
					moeDetail = "（受 --moe-cache-mib 限制）";
				}
				expertCpuBytes = ts.ExpertTotalBytes - expertGpuBytes;
				gpuWeights = embedOut + denseOnGpu + expertGpuBytes;
				if (ts.ExpertTotalBytes > 0.0)
				{
					moeDetail = "（专家 " + (ts.ExpertTotalBytes / 1073741824.0).ToString("F2") + " GiB，其中 CPU 保留 "
						+ (expertCpuBytes / 1073741824.0).ToString("F2") + " GiB" + (moeDetail.Length > 0 ? " " + moeDetail : "") + "）";
				}
				else
				{
					moeDetail = "（dense 模型，无专家张量）";
				}
			}
			else
			{
				// 张量表解析失败回退: 旧的层数比例近似
				double weights = fileBytes;
				if (layers > 0 && hasNcm)
				{
					weights *= Math.Clamp(1.0 - ncmV / layers, 0.02, 1.0);
				}
				else if (RoleBool("cpu_moe"))
				{
					weights *= 0.12;
				}
				gpuWeights = weights * gpuFrac;
				moeDetail = "（张量表解析失败，按层比例近似）";
			}
			// KV：只有全注意力层随上下文线性增长；V 量化依赖 flash attention，
			// 显式关闭 FA 时 llama 会把 V 回退到 f16（llama.cpp cache 类型回退规则）
			string ck = RoleText("cache_type_k", out string ckv) && !string.IsNullOrEmpty(ckv) ? ckv : "f16";
			string cv = RoleText("cache_type_v", out string cvv) && !string.IsNullOrEmpty(cvv) ? cvv : "f16";
			if (TryGetParamValue("flash-attn", out string faStr) && faStr.Trim() == "off" && cv != "f16")
			{
				cv = "f16";
			}
			long ctx = 0L;
			if (RoleText("ctx_size", out string ctxStr))
			{
				long.TryParse(NormTok(ctxStr), out ctx);
			}
			double kvPerTok = fullLayers * gi.KvHeads * headDim * (CacheBytesPerElem(ck) + CacheBytesPerElem(cv));
			double kvBytes = kvPerTok * ctx * gpuFrac;
			// SWA/混合注意力层：三类情况——滑动窗口（KV=min(ctx,窗口)，--swa-full 时全 ctx）、
			// 线性注意力（SSM/Gated Delta Net，state 不随 ctx 线性但随槽数增长）、未知（警告）
			int swaLayers = Math.Max(0, layers - fullLayers);
			double swaKvBytes = 0.0;
			double recStateBytes = 0.0;
			string swaNote = "";
			bool swaFull = _pbw.TryGetValue("swa-full", out System.Windows.Controls.CheckBox? sfb) && sfb.IsChecked == true;
			if (swaLayers > 0)
			{
				if (gi.SlidingWindow > 0 && gi.KvHeads > 0 && headDim > 0 && ctx > 0)
				{
					// --swa-full 时 SWA 层也保留全 ctx 的 KV（官方语义: use full-size SWA cache）
					long swaCtx = swaFull ? ctx : Math.Min(ctx, gi.SlidingWindow);
					swaKvBytes = swaLayers * gi.KvHeads * headDim * (CacheBytesPerElem(ck) + CacheBytesPerElem(cv)) * swaCtx * gpuFrac;
					swaNote = "（含 SWA " + swaLayers + " 层 × " + (swaFull ? "全 ctx" : "窗口 " + gi.SlidingWindow) + ": " + (swaKvBytes / 1048576.0).ToString("F0") + " MiB）";
				}
				else if (gi.SsmInnerSize > 0 && gi.SsmStateSize > 0)
				{
					// conv 状态 + recurrent 状态（f32），常数级/层，不随 ctx 线性但随槽数增长
					recStateBytes = swaLayers * ((double)gi.SsmInnerSize * gi.SsmConvKernel + (double)gi.SsmInnerSize * gi.SsmStateSize) * 4.0 * gpuFrac;
					swaNote = "（含线性注意力 " + swaLayers + " 层，state 不随 ctx 增长: " + (recStateBytes / 1048576.0).ToString("F0") + " MiB）";
				}
				else
				{
					metaWarn += "⚠ " + swaLayers + " 个混合注意力层（窗口/状态未知）KV 未计入预算（如遇 OOM 请实测）\n";
				}
			}
			// 线性注意力 state 按槽数线性增长（实测: 149.62 MiB = 1 seq；--parallel N → N 份）
			if (recStateBytes > 0.0 && TryGetParamValue("parallel", out string parStr) && int.TryParse(parStr.Trim(), out int parN))
			{
				if (parN > 1)
				{
					recStateBytes *= parN;
					swaNote = swaNote.Replace("MiB）", "MiB × " + parN + " 槽）");
				}
				else if (parN < 0)
				{
					metaWarn += "⚠ --parallel -1（auto）：槽数由 ctx 自动推导，线性注意力 state 会成倍增长（预算按 1 槽计）\n";
				}
			}
			kvBytes += swaKvBytes + recStateBytes;
			// 计算图：n_ubatch × n_embd × 173B——系数按实测 breakdown 校准
			// (qwen35/ubatch512 实测 432 MiB = 512×5120×173B；不同架构 ±50%)
			double ubatch = 512.0;
			if (RoleText("ubatch_size", out string ubStr) && double.TryParse(ubStr, out double ubVal) && ubVal > 0.0)
			{
				ubatch = ubVal;
			}
			double embed = gi.Embed > 0 ? gi.Embed : 4096.0;
			double computeBytes = ubatch * embed * 173.0;
			// 输出 logits 缓冲不占显卡 VRAM：0.7.0 把 output buffer 放在 host 侧
			// 且按实际 n_outputs 动态分配（实测 decode 单 token 仅 0.95 MiB），不计入预算
			// 草稿模型（推测解码激活时）整体进显存，按文件大小计入；
			// --spec-draft-ngl 0 时草稿全部驻留 CPU，不计入
			double draftBytes = 0.0;
			if (IsSpecDecodingActive())
			{
				string dp = InputMtpPath.Text.Trim().Trim('"');
				if (TryGetParamValue("spec-draft-model", out string sdm) && !string.IsNullOrEmpty(sdm))
				{
					dp = sdm;
				}
				bool draftOnCpu = TryGetParamValue("spec-draft-ngl", out string dngl) && dngl.Trim() == "0";
				if (!draftOnCpu && !string.IsNullOrEmpty(dp) && File.Exists(dp))
				{
					draftBytes = GgufShardBytes(dp);
				}
			}
			// 运行时基线（后端 context/队列等）：按 Vulkan 实测口径 ~300 MiB；
			// 旧的 1 GiB 基线 + 7.6% 权重副本是高估来源，官方 breakdown 无此项
			double runtimeBytes = 300.0 * 1048576.0;
			// 权重显存 = 张量字节 × 0.95：部分张量会被 CPU 回退（如 Vulkan 上 iq2_s 的
			// token_embd 走 CPU_REPACK）且存在对齐损耗，实测 breakdown 为张量字节的 ~95%
			double total = gpuWeights * 0.95 + kvBytes + computeBytes + runtimeBytes + mmprojBytes + draftBytes;
			double totalVram = 0.0;
			double usedVram = 0.0;
			Dictionary<string, string> g = _gpuMonitor.QueryGpu();
			if (g.TryGetValue("vram_total", out string gts) && double.TryParse(gts, NumberStyles.Float, CultureInfo.InvariantCulture, out totalVram) && totalVram > 0.0
				&& g.TryGetValue("vram_used", out string us))
			{
				double.TryParse(us, NumberStyles.Float, CultureInfo.InvariantCulture, out usedVram);
			}
			double avail = Math.Max(0.0, totalVram - usedVram);
			// 元数据缺失时 KV 会算成 0，必须标出来，否则"预算很小"会被误当成结论。
			// （混合注意力层的窗口/状态警告在 SWA 三分支内处理，此处不重复）
			if (layers <= 0 || gi.KvHeads <= 0 || ctx <= 0)
			{
				metaWarn = "⚠ KV 元数据不完整（层数=" + layers + " KV头=" + gi.KvHeads + " ctx=" + ctx + "），KV 可能算成 0，预算仅供参考\n";
			}
			if (TryGetParamValue("flash-attn", out string faAuto) && faAuto.Trim() == "auto" && cv != "f16")
			{
				metaWarn += "⚠ FA=auto：若后端不支持 FA，V 缓存会回退 f16，实际占用更高\n";
			}
			if (RoleText("lora", out string loraStr) && !string.IsNullOrEmpty(loraStr))
			{
				metaWarn += "⚠ LoRA 适配器占用未计入（按文件大小预留）\n";
			}
			string detail = metaWarn + "显存预算（公式，对齐 llama breakdown）\n"
				+ "权重(GPU): " + (gpuWeights * 0.95 / 1073741824.0).ToString("F2") + " GiB" + moeDetail + "\n"
				+ "KV(" + ck + "/" + cv + ", " + fullLayers + " 层全注意力): " + (kvBytes / 1073741824.0).ToString("F2") + " GiB"
				+ (swaKvBytes > 0.0 ? swaNote : "") + "\n"
				+ "计算图(ubatch " + ubatch.ToString("F0") + "): " + (computeBytes / 1048576.0).ToString("F0") + " MiB\n"
				+ (draftBytes > 0.0 ? "草稿模型: " + (draftBytes / 1073741824.0).ToString("F2") + " GiB\n" : "")
				+ "运行时基线: " + (runtimeBytes / 1048576.0).ToString("F0") + " MiB\n"
				+ (mmprojBytes > 0 ? "视觉编码器: " + (mmprojBytes / 1048576.0).ToString("F0") + " MiB\n" : "")
				+ "预计总占用: " + (total / 1073741824.0).ToString("F2") + " GiB\n"
				+ "可用显存: " + (avail / 1073741824.0).ToString("F2") + " GiB";
			return (total, fileBytes, kvBytes, avail, detail);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>启动前的显存判断：装得下直接走，装不下才问。</summary>
	private bool ConfirmVramFit()
	{
		(double, double, double, double, string)? est = EstimateVramNeeded();
		if (!est.HasValue)
		{
			return true;
		}
		(double estBytes, double modelBytes, double kvBytes, double availBytes, string detail) = est.Value;
		bool fits = availBytes <= 0.0 || estBytes <= availBytes;
		string verdict = availBytes <= 0.0
			? "判断: 未取到显卡显存信息，无法判断"
			: (fits
				? "判断: 在可用范围内（预计 " + (estBytes / 1073741824.0).ToString("F2") + " GiB / 可用 " + (availBytes / 1073741824.0).ToString("F2") + " GiB）"
				: "判断: 超出可用范围（预计 " + (estBytes / 1073741824.0).ToString("F2") + " GiB / 可用 " + (availBytes / 1073741824.0).ToString("F2") + " GiB）");
		System.Windows.MessageBoxResult r = System.Windows.MessageBox.Show(
			detail + "\n" + verdict + "\n\n" + (fits ? "继续启动吗？" : "仍然启动吗？"),
			fits ? "显存预算" : "显存不足",
			System.Windows.MessageBoxButton.YesNo,
			fits ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
		return r == System.Windows.MessageBoxResult.Yes;
	}

	// qwen35（Gated Delta Net 线性注意力）部分卸载风险提示：
	// 混合 CPU/GPU 层会禁用 fused op，compute/KV 缓冲可能整段驻留显存 → OOM/崩溃。
	// 返回警告文案；非 qwen35 或全量卸载时返回 null。
	private string? Qwen35PartialOffloadWarning()
	{
		try
		{
			string? modelPath = ResolveModelPath(throwOnError: false) ?? "";
			if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
			{
				return null;
			}
			GgufArchInfo gi = ReadGgufArch(modelPath);
			if (!gi.Ok || gi.Layers <= 0 || !string.Equals(gi.Architecture, "qwen35", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			if (RoleText("ngl", out string ngl) && double.TryParse(ngl, out double nglV) && nglV >= 0.0 && nglV < (double)gi.Layers)
			{
				return "模型架构 " + gi.Architecture + "（Gated Delta Net 线性注意力）检测到部分卸载（--gpu-layers " + nglV.ToString("F0") + " < " + gi.Layers + " 层）。部分卸载会禁用 fused op，compute/KV 缓冲可能整段驻留显存，极易 OOM 崩溃。建议 --gpu-layers 设为 " + gi.Layers + "（全量卸载）。";
			}
			// MoE 专家卸载到 CPU（cpu_moe / n_cpu_moe）同样会破坏 fused op 的全量卸载前提
			if (RoleBool("cpu_moe"))
			{
				return "模型架构 " + gi.Architecture + "（Gated Delta Net 线性注意力）检测到 MoE 专家卸载到 CPU（--cpu-moe 生效）。该架构需要全量 GPU 卸载才能启用 fused op，否则 compute/KV 缓冲可能整段驻留显存，极易 OOM 崩溃。建议关闭 --cpu-moe。";
			}
			if (RoleText("n_cpu_moe", out string ncm) && double.TryParse(ncm, out double ncmV) && ncmV > 0.0)
			{
				return "模型架构 " + gi.Architecture + "（Gated Delta Net 线性注意力）检测到部分 MoE 专家卸载到 CPU（--n-cpu-moe " + ncmV.ToString("F0") + "）。该架构需要全量 GPU 卸载才能启用 fused op，否则 compute/KV 缓冲可能整段驻留显存，极易 OOM 崩溃。建议 --n-cpu-moe 设为 0。";
			}
		}
		catch
		{
		}
		return null;
	}

	private string FmtCmd(List<string> cl)
	{
		if (cl.Count == 0)
		{
			return "";
		}
		// 旗标是否取值: schema 已知时以 schema 为准(值本身以 -- 开头时纯启发式必然错位)
		bool TakesValue(string flag)
		{
			string f = flag.TrimStart('-');
			ParamDefinition? d = _paramDefs?.FirstOrDefault((ParamDefinition x) => x.Flag == f
				|| x.OffFlag == f
				|| (x.Aliases != null && x.Aliases.Contains(f)));
			return d != null && d.Type != ParamType.Bool;
		}
		List<string> list = new List<string> { QA(cl[0]) };
		int num = 1;
		while (num < cl.Count)
		{
			string text = cl[num];
			string text2 = QA(text);
			bool paired = text.StartsWith("--") && num + 1 < cl.Count
				&& (TakesValue(text) || !cl[num + 1].StartsWith("--"));
			if (paired)
			{
				list.Add("    " + text2 + " " + QA(cl[num + 1]));
				num += 2;
			}
			else
			{
				list.Add("    " + text2);
				num++;
			}
		}
		return string.Join(Environment.NewLine, list);
	}

	private static string QA(string a)
	{
		// MSVCRT/CommandLineToArgvW 引号规则: 值含空白/引号时加引号;
		// 引号前的连续反斜杠翻倍并追加转义引号; 结尾引号前的反斜杠翻倍。
		// 旧实现只把 " 换成 \" —— 路径以 \ 结尾时 "D:\dir\" 的闭引号被吃掉。
		if (string.IsNullOrEmpty(a))
		{
			return "\"\"";
		}
		bool needQuote = a.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0;
		var sb = new System.Text.StringBuilder();
		if (needQuote)
		{
			sb.Append('"');
		}
		int backslashes = 0;
		foreach (char c in a)
		{
			if (c == '\\')
			{
				backslashes++;
				sb.Append('\\');
				continue;
			}
			if (c == '"')
			{
				sb.Append('\\', backslashes * 2 + 1);
				sb.Append('"');
				backslashes = 0;
				continue;
			}
			backslashes = 0;
			sb.Append(c);
		}
		if (needQuote)
		{
			sb.Append('\\', backslashes * 2);
			sb.Append('"');
		}
		return sb.ToString();
	}

	private System.Windows.Threading.DispatcherTimer? _cmdDebounceTimer;

	private string? _pendingAutoFillPath;

	/// <summary>去抖入口: 原先每次击键都在 UI 线程做目录枚举 + GGUF 头解析(ResolveModelPath/ReadGgufArch),
	/// 网络盘/移动介质上会逐键卡顿假死。250ms 内只生效最后一次; AutoFill 的 3 次 GetFiles 一并延后。</summary>
	private void RefreshCmd()
	{
		if (_cmdDebounceTimer == null)
		{
			_cmdDebounceTimer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(250)
			};
			_cmdDebounceTimer.Tick += delegate
			{
				_cmdDebounceTimer!.Stop();
				string? p = _pendingAutoFillPath;
				_pendingAutoFillPath = null;
				if (p != null)
				{
					AutoFill(p);
				}
				RefreshCmdNow();
			};
		}
		_cmdDebounceTimer.Stop();
		_cmdDebounceTimer.Start();
	}

	private void RefreshCmdNow()
	{
		// EvaluateDepUI 内部会改勾选状态并触发 Checked/Unchecked；此时不要递归重建命令。
		if (_inDepEval)
		{
			return;
		}
		if (_cmdEditing)
		{
			// 编辑期间不要用配置区状态覆盖用户正在改的文本；保存/取消时再刷新。
			return;
		}
		EvaluateDepUI();
		try
		{
			// baseList 严格来自配置区；自定义参数只追加在末尾，绝不参与配置参数的增删改。
			List<string> baseList = BuildCmd(includeCustomArgs: false);
			List<string> allArgs = new List<string>(baseList);
			allArgs.AddRange(_customArgs);
			_lastCmd = string.Join(" ", allArgs.Select(QA));
			CmdPreview.Text = FmtCmd(baseList);
			if (_customArgs.Count > 0)
			{
				CmdCustomPreview.Text = "# 自定义参数（橙色，追加到命令末尾）:" + Environment.NewLine
					+ "  " + string.Join(" ", _customArgs.Select(QA));
				CmdCustomPreview.Visibility = Visibility.Visible;
			}
			else
			{
				CmdCustomPreview.Text = "";
				CmdCustomPreview.Visibility = Visibility.Collapsed;
			}
			// qwen35 部分卸载风险：只显示在命令预览，不刷日志（避免每次击键刷屏）
			string? qw = Qwen35PartialOffloadWarning();
			if (qw != null)
			{
				CmdPreview.Text += Environment.NewLine + "⚠ " + qw;
			}
			UpdateParamArgLabels(_lastParamBuild);
		}
		catch (Exception ex)
		{
			_lastCmd = "";
			CmdPreview.Text = "Error:" + ex.Message;
			CmdCustomPreview.Text = "";
			CmdCustomPreview.Visibility = Visibility.Collapsed;
			UpdateParamArgLabels(null);
			LogError(ex, "命令构建失败");
		}
	}

	/// <summary>逐参数回显真实启动参数：绿色=已发射进命令行，灰色=未发射（显示的是"若启用会是什么"）。</summary>
	private void UpdateParamArgLabels(ParamBuildResult? res)
	{
		if (_paramDefs == null)
		{
			return;
		}
		foreach (KeyValuePair<string, System.Windows.Controls.TextBlock> kv in _argLabels)
		{
			ParamDefinition? d = _paramDefs.FirstOrDefault((ParamDefinition p) => p.Flag == kv.Key);
			if (d == null)
			{
				continue;
			}
			bool emitted = res != null && res.FlagArgs.ContainsKey(kv.Key);
			string args = emitted && res != null
				? string.Join(" ", res.FlagArgs[kv.Key])
				: (d.Type == ParamType.Bool
					? "--" + kv.Key
					: ("--" + kv.Key + " " + (((IParamState)this).EffectiveValue(d) ?? "").Trim()).TrimEnd());
			kv.Value.Text = args;
			kv.Value.Foreground = new SolidColorBrush(emitted
				? System.Windows.Media.Color.FromRgb(63, 185, 80)
				: System.Windows.Media.Color.FromRgb(110, 118, 129));
		}
	}

	private void RefreshCmd_Click(object s, RoutedEventArgs e)
	{
		RefreshCmdNow();
	}

	/// <summary>进入/退出命令预览编辑态。编辑只允许在基础命令后追加自定义参数。</summary>
	private void EditCmd_Click(object s, RoutedEventArgs e)
	{
		if (_cmdEditing)
		{
			ApplyCmdEdit();
			return;
		}
		try
		{
			_cmdEditBase = BuildCmd(includeCustomArgs: false);
			_cmdEditing = true;
			CmdPreview.IsReadOnly = false;
			CmdPreview.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 237, 243));
			CmdPreview.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 27, 34));
			List<string> allArgs = new List<string>(_cmdEditBase);
			allArgs.AddRange(_customArgs);
			CmdPreview.Text = string.Join(" ", allArgs.Select(QA));
			CmdPreview.CaretIndex = CmdPreview.Text.Length;
			CmdPreview.Focus();
			CmdCustomPreview.Visibility = Visibility.Collapsed;
			BtnEditCmd.Content = "✅ 保存自定义参数";
			BtnCancelCmd.Visibility = Visibility.Visible;
		}
		catch (Exception ex)
		{
			_cmdEditing = false;
			LogError(ex, "进入命令编辑");
			ShowNotice("无法进入命令编辑: " + ex.Message, isError: true);
		}
	}

	private void CancelCmd_Click(object s, RoutedEventArgs e)
	{
		_cmdEditing = false;
		CmdPreview.IsReadOnly = true;
		CmdPreview.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(126, 231, 135));
		CmdPreview.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(13, 17, 23));
		BtnEditCmd.Content = "✏ 编辑命令（仅追加自定义参数）";
		BtnCancelCmd.Visibility = Visibility.Collapsed;
		RefreshCmdNow();
	}

	private void ApplyCmdEdit()
	{
		List<string> tokens;
		try
		{
			tokens = SplitCommandLine(CmdPreview.Text);
		}
		catch (Exception ex)
		{
			ShowNotice("命令解析失败: " + ex.Message, isError: true);
			return;
		}
		if (!TokensStartWith(tokens, _cmdEditBase))
		{
			ShowNotice(
				"不能修改或删除上方已配置的参数。\n\n" +
				"这里只允许在基础命令末尾追加自定义参数：请保留前面的配置参数不变，把新增内容写在最后。\n" +
				"配置参数请回到上方“参数配置”区勾选/修改。", isError: true);
			return;
		}
		List<string> custom = tokens.Skip(_cmdEditBase.Count).ToList();
		string? invalid = ValidateCustomArgs(custom);
		if (invalid != null)
		{
			ShowNotice(invalid, isError: true);
			return;
		}
		_customArgs = custom;
		_cmdEditing = false;
		CmdPreview.IsReadOnly = true;
		CmdPreview.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(126, 231, 135));
		CmdPreview.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(13, 17, 23));
		BtnEditCmd.Content = "✏ 编辑命令（仅追加自定义参数）";
		BtnCancelCmd.Visibility = Visibility.Collapsed;
		RefreshCmdNow();
	}

	/// <summary>自定义参数不能使用配置区已管理的旗标（含旧别名/否定旗标），防止从这里覆盖配置。</summary>
	private string? ValidateCustomArgs(List<string> args)
	{
		foreach (string token in args)
		{
			if (!token.StartsWith("--", StringComparison.Ordinal))
			{
				continue;
			}
			string name = token.Substring(2);
			int eq = name.IndexOf('=');
			if (eq >= 0)
			{
				name = name.Substring(0, eq);
			}
			foreach (ParamDefinition d in _paramDefs)
			{
				bool managed = string.Equals(d.Flag, name, StringComparison.OrdinalIgnoreCase)
					|| (d.Aliases != null && d.Aliases.Any((string a) => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
					|| string.Equals(d.OffFlag, name, StringComparison.OrdinalIgnoreCase);
				if (managed)
				{
					return "自定义参数不能包含 --" + name + "：该参数由上方配置区管理，请在那里勾选/修改，避免重复或冲突。";
				}
			}
		}
		return null;
	}

	private static bool TokensStartWith(List<string> tokens, List<string> prefix)
	{
		if (tokens.Count < prefix.Count)
		{
			return false;
		}
		for (int i = 0; i < prefix.Count; i++)
		{
			if (!string.Equals(tokens[i], prefix[i], StringComparison.Ordinal))
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// 按 CommandLineToArgvW 规则拆分（与 QA 严格互逆）：反斜杠成对时取一半、
	/// 反斜杠+引号按奇偶决定字面引号或引号切换；引号在片段中段切换而非必须包裹整段。
	/// </summary>
	private static List<string> SplitCommandLine(string text)
	{
		List<string> result = new List<string>();
		int i = 0;
		while (i < text.Length)
		{
			while (i < text.Length && char.IsWhiteSpace(text[i]))
			{
				i++;
			}
			if (i >= text.Length)
			{
				break;
			}
			StringBuilder current = new StringBuilder();
			bool quoted = text[i] == '"';
			if (quoted)
			{
				i++;
			}
			while (i < text.Length)
			{
				char c = text[i];
				if (c == '\\')
				{
					int n = 0;
					while (i < text.Length && text[i] == '\\')
					{
						n++;
						i++;
					}
					if (i < text.Length && text[i] == '"')
					{
						// N 个反斜杠 + 引号: 偶数 → N/2 个反斜杠, 引号生效; 奇数 → N/2 个反斜杠 + 字面引号
						current.Append('\\', n / 2);
						if (n % 2 == 1)
						{
							current.Append('"');
							i++;
						}
						else
						{
							quoted = !quoted;
							i++;
						}
						continue;
					}
					current.Append('\\', n);
					continue;
				}
				if (c == '"')
				{
					quoted = !quoted;
					i++;
					continue;
				}
				if (!quoted && char.IsWhiteSpace(c))
				{
					break;
				}
				current.Append(c);
				i++;
			}
			result.Add(current.ToString());
		}
		return result;
	}

	// 重载参数配置（llama_params.yml / override 修改后即时生效，无需重启启动器）
	private void ReloadSchema_Click(object s, RoutedEventArgs e)
	{
		try
		{
			ProfileConfig snap = BuildConfig();
			ParamSchemaLoadResult r = ParamSchemaService.Load();
			_paramSchema = r;
			_paramDefs = r.Defs;
			_paramGroups = r.Groups;
			_pw.Clear();
			_pbw.Clear();
			BuildParamGroups();
			ApplyConfig(snap);
			Log("[参数] 配置已重载，来源: " + r.Source + "，参数 " + r.Defs.Count + " 项 / 组 " + r.Groups.Count + " 个");
			if (r.Errors.Count > 0 || r.Warnings.Count > 0)
			{
				ShowNotice(string.Join("\n", r.Errors.Concat(r.Warnings)), isError: true);
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "重载参数配置失败");
			ShowNotice("重载失败: " + ex.Message, isError: true);
		}
	}

	private void CopyCmd_Click(object s, RoutedEventArgs e)
	{
		string text = string.IsNullOrEmpty(_lastCmd) ? CmdPreview.Text : _lastCmd;
		if (!string.IsNullOrEmpty(text))
		{
			System.Windows.Clipboard.SetText(text);
			Log("Copied");
		}
	}

	private void StartLlama_Click(object s, RoutedEventArgs e)
	{
		StartLlama();
	}

	private void StopLlama_Click(object s, RoutedEventArgs e)
	{
		if (!IsLlamaRunning())
		{
			return;
		}
		if (System.Windows.MessageBox.Show("确定要停止 Llama 服务吗？\n\n正在生成的内容会中断，模型加载/推理进度将丢失。",
			"停止 Llama", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
		{
			return;
		}
		StopLlama();
	}

	// 检测当前模型选择是否为未转换的 safetensors（目录含分片，或直接选了 .safetensors 文件）
	private string? DetectSafetensorsDir()
	{
		try
		{
			string raw = InputModelPath.Text.Trim().Trim('"');
			if (string.IsNullOrEmpty(raw))
			{
				return null;
			}
			if (Directory.Exists(raw))
			{
				return ((Directory.GetFiles(raw, "*.safetensors").Length > 0) ? raw : null);
			}
			if (File.Exists(raw) && raw.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
			{
				return Path.GetDirectoryName(raw);
			}
		}
		catch
		{
		}
		return null;
	}

	// 解压内置转换工具包(嵌入式资源 conversion_kit.zip：入口脚本+sitecustomize 垫片+conversion 包)
	private void ExtractConversionKit()
	{
		try
		{
			string? resName = null;
			foreach (string n in typeof(MainWindow).Assembly.GetManifestResourceNames())
			{
				if (n.EndsWith("conversion_kit.zip"))
				{
					resName = n;
					break;
				}
			}
			if (resName == null)
			{
				Log("[转换] 内置转换工具包缺失(打包问题)");
				return;
			}
			Directory.CreateDirectory(ToolsDir);
			string zipPath = Path.Combine(Path.GetTempPath(), "conversion_kit.zip");
			using (System.IO.Stream src = typeof(MainWindow).Assembly.GetManifestResourceStream(resName)!)
			using (FileStream dst = File.Create(zipPath))
			{
				src.CopyTo(dst);
			}
			using (System.IO.Compression.ZipArchive za = System.IO.Compression.ZipFile.OpenRead(zipPath))
			{
				int count = 0;
				foreach (System.IO.Compression.ZipArchiveEntry e in za.Entries)
				{
					if (string.IsNullOrEmpty(e.Name))
					{
						continue;
					}
					string dest = Path.Combine(ToolsDir, e.FullName.Replace('/', Path.DirectorySeparatorChar));
					Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
					using (System.IO.Stream es = e.Open())
					using (FileStream fs = File.Create(dest))
					{
						es.CopyTo(fs);
					}
					count++;
				}
				Log("[转换] 内置转换工具包已解压(" + count + " 个文件)");
			}
			try
			{
				File.Delete(zipPath);
			}
			catch
			{
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "解压内置转换工具包");
		}
	}

	private void ConvertSafetensors(string dir, string outtype, string pyExe, string pyPre, Action<string?> onDone)
	{
		string? outFile = null;
		try
		{
			string? script = FindConvertScript();
			if (script == null)
			{
				Log("[转换] 未找到 convert_hf_to_gguf.py(内置工具包解压失败)。");
				onDone(null);
				return;
			}
			double srcBytes = 0.0;
			string[] srcFiles = Directory.GetFiles(dir, "*.safetensors");
			foreach (string f in srcFiles)
			{
				srcBytes += new FileInfo(f).Length;
			}
			string dirName = dir.TrimEnd('\\', '/');
			outFile = Path.Combine(dir, Path.GetFileName(dirName) + "-" + outtype + ".gguf");
			// 已有产物需完整性校验：GGUF 魔数 + 文件大小达到源的合理比例(上次中断会留下截断文件)
			if (File.Exists(outFile))
			{
				long existLen = new FileInfo(outFile).Length;
				bool headerOk = false;
				if (existLen > 24)
				{
					try
					{
						using (FileStream fs0 = File.OpenRead(outFile))
						using (BinaryReader br0 = new BinaryReader(fs0))
						{
							headerOk = (br0.ReadInt32() == 0x46554747);
						}
					}
					catch
					{
					}
				}
				double expectFactor = ((outtype == "f16") ? 0.9 : 0.45);
				if (headerOk && existLen >= (double)srcBytes * expectFactor)
				{
					Log("[转换] 已存在有效的转换产物(" + (existLen / 1073741824.0).ToString("F1") + " GiB)，跳过转换: " + outFile);
					onDone(outFile);
					return;
				}
				Log("[转换] 检测到不完整的转换产物(" + (existLen / 1073741824.0).ToString("F1") + " GiB，疑似上次中断)，删除后重新转换");
				try
				{
					File.Delete(outFile);
				}
				catch
				{
				}
			}
			double needBytes = srcBytes * ((outtype == "f16") ? 1.05 : 0.62);
			DriveInfo di = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(outFile)) ?? "C:\\");
			Log("[转换] 源 safetensors 共 " + (srcBytes / 1073741824.0).ToString("F1") + " GiB，预计需要磁盘 " + (needBytes / 1073741824.0).ToString("F1") + " GiB，" + di.Name + " 剩余 " + (di.AvailableFreeSpace / 1073741824.0).ToString("F1") + " GiB");
			if (di.AvailableFreeSpace < needBytes)
			{
				Log("[转换] 磁盘空间不足，已取消。请清理磁盘或把输出放到空间足够的分区。");
				onDone(null);
				return;
			}
			StartDetachedConversion(dir, outtype, pyExe, pyPre, script, outFile, srcBytes, onDone);
		}
		catch (Exception ex)
		{
			LogError(ex, "safetensors 自动转换");
			onDone(null);
		}
	}

	// 启动器工具目录：存放自动下载的 convert_hf_to_gguf.py 等(避免写入 Program Files 无权限)
	public string ToolsDir { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh_launcher_tools");


	private string? FindConvertScript()
	{
		string inTools = Path.Combine(ToolsDir, "convert_hf_to_gguf.py");
		if (File.Exists(inTools))
		{
			return inTools;
		}
		string llamaDir = InputLlamaDir.Text.Trim().Trim('"');
		string inLlama = Path.Combine(llamaDir, "convert_hf_to_gguf.py");
		if (File.Exists(inLlama))
		{
			return inLlama;
		}
		return null;
	}

	// 检测 python：返回 (exe, 参数前缀)。python 失败则尝试 py -3
	private (string exe, string pre)? DetectPython()
	{
		string[][] cands = new string[2][]
		{
			new string[2] { "python", "" },
			new string[2] { "py", "-3" }
		};
		foreach (string[] cand in cands)
		{
			try
			{
				ProcessStartInfo psi = new ProcessStartInfo
				{
					FileName = cand[0],
					Arguments = ((cand[1].Length > 0) ? (cand[1] + " ") : "") + "--version",
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				};
				Process? p = Process.Start(psi);
				if (p == null)
				{
					continue;
				}
				string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
				p.WaitForExit(20000);
				if (p.ExitCode == 0)
				{
					Log("[环境] python 就绪: " + o.Trim());
					return (cand[0], cand[1]);
				}
			}
			catch
			{
			}
		}
		return null;
	}

	// 启动进程并实时把输出泵进日志，返回退出码
	private System.Threading.Tasks.Task<int> RunProcessWithLog(string exe, string args, string cwd, string tag)
	{
		System.Threading.Tasks.TaskCompletionSource<int> tcs = new System.Threading.Tasks.TaskCompletionSource<int>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
		try
		{
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName = exe,
				Arguments = args,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			if (cwd != null)
			{
				psi.WorkingDirectory = cwd;
			}
			Process? proc = Process.Start(psi);
			if (proc == null)
			{
				tcs.TrySetResult(-1);
				return tcs.Task;
			}
			Process? p = proc;
			PumpProcessStreams(p, tag + " ", delegate
			{
				int code = 0;
				try
				{
					code = p.ExitCode;
				}
				catch
				{
				}
				DisposeProc(ref p);
				tcs.TrySetResult(code);
			});
		}
		catch (Exception ex)
		{
			LogError(ex, tag + " 进程启动失败");
			tcs.TrySetResult(-1);
		}
		return tcs.Task;
	}

	// 环境自动化总入口：python 检测 → 下载转换脚本 → 安装 pip 依赖 → 转换 → 继续启动
	private async void EnsureConvertAndLaunchAsync(string dir, string outtype)
	{
		try
		{
			string? py = null;
			string pyPre = "";
			(string exe, string pre)? detected = await Task.Run(() => DetectPython());
			if (detected.HasValue)
			{
				py = detected.Value.exe;
				pyPre = detected.Value.pre;
			}
			if (py == null)
			{
				Log("[环境] 未检测到 python。请安装 Python 3.10+ 后重试: https://www.python.org/downloads/");
				try
				{
					Process.Start(new ProcessStartInfo("https://www.python.org/downloads/") { UseShellExecute = true });
				}
				catch
				{
				}
				BtnLlamaStart.IsEnabled = true;
				return;
			}
			ExtractConversionKit();
			Log("[环境] 检查/安装 python 依赖: gguf torch transformers sentencepiece (首次可能需下载数 GB，请耐心等待)");
			string mirror = " -i https://pypi.tuna.tsinghua.edu.cn/simple";
			string pipArgs = ((pyPre.Length > 0) ? (pyPre + " ") : "") + "-m pip install --disable-pip-version-check --progress-bar off gguf torch transformers sentencepiece";
			int rc = await RunProcessWithLog(py, pipArgs + mirror, null, "[pip]");
			if (rc != 0)
			{
				Log("[环境] 清华镜像安装失败，改用官方 PyPI 重试 ...");
				rc = await RunProcessWithLog(py, pipArgs, null, "[pip]");
			}
			if (rc != 0)
			{
				Log("[环境] python 依赖安装失败，已取消启动。请检查网络后重试。");
				BtnLlamaStart.IsEnabled = true;
				return;
			}
			Log("[环境] 依赖就绪，开始转换 safetensors → GGUF (" + outtype + ")");
			ConvertSafetensors(dir, outtype, py, pyPre, delegate (string? outFile)
			{
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					BtnLlamaStart.IsEnabled = true;
					if (string.IsNullOrEmpty(outFile))
					{
						Log("--- 转换失败，启动已取消。修复问题后重新点击启动 ---");
						return;
					}
					InputModelPath.Text = outFile;
					Log("--- 转换完成，继续启动 llama ---");
					StartLlama();
				}, Array.Empty<object>());
			});
		}
		catch (Exception ex)
		{
			LogError(ex, "自动准备转换环境");
			BtnLlamaStart.IsEnabled = true;
		}
	}


    private static readonly string Q = ((char)34).ToString();

    private System.Windows.Threading.DispatcherTimer? _convertPollTimer;

    private string? _convertLogPath;

    private long _convertLogPos;

    private string? _convertOutFile;

    private double _convertSrcBytes;

    private DateTime _convertDeadline;

    // WMI 分离启动转换：进程由 WmiPrvSE 托管、输出走文件——
    // 断开远程/关闭启动器均不中断(修复二次远程场景下转换意外退出)
    private void StartDetachedConversion(string dir, string outtype, string pyExe, string pyPre, string script, string outFile, double srcBytes, Action<string?> onDone)
    {
        if (outtype != "f16" && outtype != "f32" && outtype != "q8_0")
        {
            Log("[转换] 非法 outtype: " + outtype + "(仅支持 f16/f32/q8_0)");
            onDone(null);
            return;
        }
        string logPath = Path.Combine(dir, "llamadsh_convert.log");
        // GUID 目录内 GUID 名: 固定名在写入与 cmd 执行之间可被同用户进程改写;
        // 单独子目录避免 CleanupLauncherTemp 按通配误删正在逐行解析中的 .cmd。
        string cmdDir = Path.Combine(Path.GetTempPath(), "LlamaDSH_Launcher", "convert");
        try
        {
            Directory.CreateDirectory(cmdDir);
        }
        catch
        {
            cmdDir = Path.GetTempPath();
        }
        string cmdPath = Path.Combine(cmdDir, "convert_" + Guid.NewGuid().ToString("N") + ".cmd");
        try
        {
            File.WriteAllText(logPath, "");
        string pyCmd = Q + pyExe + Q + " " + ((pyPre.Length > 0) ? (pyPre + " ") : "") + Q + script + Q + " " + Q + dir + Q + " --outfile " + Q + outFile + Q + " --outtype " + outtype;
        // chcp 65001 + UTF-8(无 BOM): 原先 UTF-8 无 BOM 被 cmd 按 OEM(GBK) 解析, 中文路径必失效;
        // PYTHONIOENCODING/PYTHONUTF8 固定 python 输出为 UTF-8, 与轮询解码及字节记账一致。
        string cmdText = "@echo off" + Environment.NewLine
            + "chcp 65001 >nul" + Environment.NewLine
            + "set \"PYTHONIOENCODING=utf-8\"" + Environment.NewLine
            + "set \"PYTHONUTF8=1\"" + Environment.NewLine
            + "cd /d " + Q + dir + Q + Environment.NewLine
            + pyCmd + " >> " + Q + logPath + Q + " 2>&1" + Environment.NewLine
            + "echo EXITCODE:%errorlevel% >> " + Q + logPath + Q;
        File.WriteAllText(cmdPath, cmdText, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            LogError(ex, "生成转换引导脚本");
            onDone(null);
            return;
        }
        try
        {
            using (System.Management.ManagementClass mc = new System.Management.ManagementClass("Win32_Process"))
            {
                System.Management.ManagementBaseObject inParams = mc.GetMethodParameters("Create");
                inParams["CommandLine"] = "cmd.exe /c " + Q + cmdPath + Q;
                inParams["CurrentDirectory"] = dir;
                using (System.Management.ManagementClass suClass = new System.Management.ManagementClass("Win32_ProcessStartup"))
                {
                    System.Management.ManagementBaseObject su = suClass.CreateInstance();
                    su["ShowWindow"] = 0;
                    inParams["ProcessStartupInformation"] = su;
                }
                System.Management.ManagementBaseObject outParams = mc.InvokeMethod("Create", inParams, null);
                int rc = Convert.ToInt32(outParams["ReturnValue"]);
                if (rc != 0)
                {
                    Log("[转换] 分离启动失败(WMI rc=" + rc + ")");
                    onDone(null);
                    return;
                }
                // 降低转换进程优先级：转换吃 CPU/内存/IO 较重，BelowNormal 让位给
                // 机器上的其他实例(DSH/llama 等)，避免资源争抢导致其他进程异常
                try
                {
                    int childPid = Convert.ToInt32(outParams["ProcessId"]);
                    using (System.Diagnostics.Process cp = System.Diagnostics.Process.GetProcessById(childPid))
                    {
                        cp.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
                    }
                    Log("[转换] 转换进程优先级已设为 BelowNormal(减少对其他实例的影响)");
                }
                catch (Exception prioEx)
                {
                    Log("[转换] 优先级设置失败(不影响转换): " + prioEx.Message);
                }
            }
        }
        catch (Exception ex)
        {
            LogError(ex, "转换分离启动");
            onDone(null);
            return;
        }
        _convertLogPath = logPath;
        _convertLogPos = 0;
        _convertOutFile = outFile;
        _convertSrcBytes = srcBytes;
        _convertDeadline = DateTime.Now.AddHours(3);
        Log("[转换] 已分离到后台运行——断开远程/关闭启动器都不会中断转换");
        Log("[转换] 转换日志: " + logPath);
        _convertPollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _convertPollTimer.Tick += delegate
        {
            PollDetachedConversion(srcBytes, outtype, onDone);
        };
        _convertPollTimer.Start();
    }

    private void PollDetachedConversion(double srcBytes, string outtype, Action<string?> onDone)
    {
        try
        {
            if (_convertLogPath != null && File.Exists(_convertLogPath))
            {
                using (FileStream fs = File.OpenRead(_convertLogPath))
                {
                    if (fs.Length < _convertLogPos)
                    {
                        _convertLogPos = 0;
                    }
                    if (fs.Length > _convertLogPos)
                    {
                        fs.Seek(_convertLogPos, SeekOrigin.Begin);
                        // 按真实字节记账: 原先解码后反推 UTF-8 字节数, 遇到 GBK 混入(替换符)即发散,
                        // 后续轮询错位导致日志重复/丢失。现在直接切字节到最后一个换行符。
                        using (MemoryStream ms = new MemoryStream())
                        {
                            fs.CopyTo(ms);
                            byte[] raw = ms.ToArray();
                            int lastNl = Array.LastIndexOf(raw, (byte)10);
                            if (lastNl >= 0)
                            {
                                _convertLogPos += lastNl + 1;
                                string done = Encoding.UTF8.GetString(raw, 0, lastNl + 1);
                                string[] parts = done.Split((char)10);
                                foreach (string value in parts)
                                {
                                    string v = value.TrimEnd((char)13);
                                    if (v.Length > 0)
                                    {
                                        Log("[转换] " + v);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }
        string? exitCodeStr = null;
        try
        {
            if (_convertLogPath != null && File.Exists(_convertLogPath))
            {
                foreach (string line in File.ReadAllLines(_convertLogPath))
                {
                    if (line.StartsWith("EXITCODE:"))
                    {
                        exitCodeStr = line.Substring("EXITCODE:".Length).Trim();
                    }
                }
            }
        }
        catch
        {
        }
        if (exitCodeStr == null && DateTime.Now < _convertDeadline)
        {
            return;
        }
        StopConvertPoll();
        if (exitCodeStr == null)
        {
            Log("[转换] 超时(3 小时)未完成，停止跟踪。转换可能仍在后台继续。");
            onDone(null);
            return;
        }
        int exitCode = -1;
        int.TryParse(exitCodeStr, out exitCode);
        bool valid = false;
        if (exitCode == 0 && _convertOutFile != null && File.Exists(_convertOutFile))
        {
            try
            {
                double expectFactor = ((outtype == "f16") ? 0.9 : 0.45);
                using (FileStream fs0 = File.OpenRead(_convertOutFile))
                using (BinaryReader br0 = new BinaryReader(fs0))
                {
                    if (br0.ReadInt32() == 0x46554747 && fs0.Length >= _convertSrcBytes * expectFactor)
                    {
                        valid = true;
                    }
                }
            }
            catch
            {
            }
        }
        if (valid)
        {
            Log("[转换] 完成: " + _convertOutFile);
            onDone(_convertOutFile);
            return;
        }
        Log("[转换] 转换失败(退出码 " + exitCode + ")");
        onDone(null);
    }

    private void StopConvertPoll()
    {
        if (_convertPollTimer != null)
        {
            _convertPollTimer.Stop();
            _convertPollTimer = null;
        }
    }
	private void StartLlama()
	{
		//IL_0224: Unknown result type (might be due to invalid IL or missing references)
		//IL_0229: Unknown result type (might be due to invalid IL or missing references)
		//IL_0242: Expected O, but got Unknown
		if (_llamaProc != null && !_llamaProc.HasExited)
		{
			ShowNotice("Already running");
			return;
		}
		// 预检 1: 端口被占(上次会话遗留的孤儿 llama-server) → 直接启动必然 bind 失败
		int prePort;
		try { prePort = GetLlamaPort(); } catch { prePort = 1234; }
		if (ProcessHelper.FindProcessByPort(prePort).HasValue)
		{
			ShowNotice($"端口 {prePort} 已被占用（可能是遗留的 llama-server 进程）。\n请重启启动器以接管，或手动结束后再启动。", isError: true);
			Log("[启动] 端口 " + prePort + " 已被占用, 放弃启动");
			return;
		}
		// 预检 2: 本地模型路径不存在时提前失败, 而不是等 llama 运行期报错
		try
		{
			string? preModel = ResolveModelPath(throwOnError: false);
			if (!string.IsNullOrEmpty(preModel) && !preModel.StartsWith("http", StringComparison.OrdinalIgnoreCase)
				&& !File.Exists(preModel) && !Directory.Exists(preModel))
			{
				ShowNotice("模型路径不存在: " + preModel, isError: true);
				return;
			}
		}
		catch
		{
		}
		_vramBreakerTriggered = false;
		LblVramBreaker.Text = "";
		_oomSeenInRun = false;
		if (!_oomRetryActive)
		{
			_oomRetryLeft = 3; // 用户手动启动时重置重试额度
		}
		_llamaApi?.ResetLogMachine();
		string? stDir = DetectSafetensorsDir();
		if (stDir != null)
		{
			System.Windows.MessageBoxResult mbResult = System.Windows.MessageBox.Show("检测到 safetensors 模型分片，llama.cpp 无法直接加载。\n\n是否自动转换为 GGUF？(大模型耗时较长，30B 级约 10~40 分钟)\n\n是 = F16（精度优先，磁盘≈源文件大小）\n否 = Q8_0（磁盘约省一半）\n取消 = 放弃启动", "自动转换 safetensors → GGUF", System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Question);
			if (mbResult != System.Windows.MessageBoxResult.Yes && mbResult != System.Windows.MessageBoxResult.No)
			{
				Log("--- 已取消：模型为 safetensors，未转换 ---");
				return;
			}
			string outtype = ((mbResult == System.Windows.MessageBoxResult.Yes) ? "f16" : "q8_0");
			BtnLlamaStart.IsEnabled = false;
			Log("--- 检测到 safetensors，将自动准备环境(下载脚本/安装依赖)并转换，目录: " + stDir + " ---");
			EnsureConvertAndLaunchAsync(stDir, outtype);
			return;
		}
		List<string> list;
		try
		{
			list = BuildCmd();
		}
		catch (Exception ex)
		{
			LogError(ex, "命令构建失败");
			ShowNotice("Build failed:" + ex.Message, isError: true);
			return;
		}
		if (list.Count == 0)
		{
			ShowNotice("Empty");
			return;
		}
		// 参数依赖/校验弹窗确认（用户选择：不满足时列出并确认，而非静默剔除）
		if (_lastViolations.Count > 0)
		{
			string msg = "以下参数未通过依赖/校验检查，将被剔除：\n\n" + string.Join("\n", _lastViolations.Select((string v) => " • " + v)) + "\n\n是 = 剔除这些参数并继续启动\n否 = 取消启动";
			if (System.Windows.MessageBox.Show(msg, "参数依赖检查", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
			{
				Log("--- 启动已取消：参数依赖检查未通过（" + _lastViolations.Count + " 项）---");
				return;
			}
			foreach (string v2 in _lastViolations)
			{
				Log("[参数] 剔除 " + v2);
			}
		}
		LogParamHints();
		// OOM 自动重试路径跳过预算弹窗: ngl 已下调、数字只会更宽松，弹窗只会打扰
		if (!_oomRetryActive && !ConfirmVramFit())
		{
			Log("--- 启动已取消 ---");
			return;
		}
		// qwen35(Gated Delta Net) 部分卸载风险：启动前强制确认，避免 fused op 禁用导致 OOM/崩溃
		string? qw = Qwen35PartialOffloadWarning();
		if (qw != null)
		{
			string msg = "⚠ " + qw + "\n\n是 = 仍然按当前设置启动（风险自负）\n否 = 取消启动，并把 --gpu-layers 改为全量卸载";
			if (System.Windows.MessageBox.Show(msg, "线性注意力模型部分卸载风险", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
			{
				Log("--- 启动已取消：qwen35 部分卸载风险 ---");
				return;
			}
		}
		try
		{
			ProcessStartInfo processStartInfo = new ProcessStartInfo
			{
				FileName = list[0],
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			for (int num = 1; num < list.Count; num++)
			{
				processStartInfo.ArgumentList.Add(list[num]);
			}
			processStartInfo.Environment["GGML_CUDA_GRAPH_OPT"] = "1";
			_llamaProc = Process.Start(processStartInfo);
			if (_llamaProc == null)
			{
				throw new Exception("● 启动失败");
			}
			SaveState();
		}
		catch (Exception ex2)
		{
			LogError(ex2, "Llama 启动失败");
			return;
		}
		PumpProcessStreams(_llamaProc, "[llama] ", delegate
		{
			OnLlamaDone();
		});
		BtnLlamaStart.IsEnabled = false;
		BtnLlamaStop.IsEnabled = true;
		LblLlamaStatus.Text = "● 启动中...";
		LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 136, 62));
		Log("--- 正在启动 Llama ---\n" + string.Join(" ", list.Select(QA)));
		DispatcherTimer vt = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(3.0)
		};
		vt.Tick += delegate
		{
			vt.Stop();
			VerifyLlama();
		};
		vt.Start();
	}

	private void VerifyLlama()
	{
		if (_llamaProc != null)
		{
			if (_llamaProc.HasExited)
			{
				Log("Llama failed");
				DisposeProc(ref _llamaProc);
				BtnLlamaStart.IsEnabled = true;
				BtnLlamaStop.IsEnabled = false;
				LblLlamaStatus.Text = "● 启动失败";
				LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 54, 51));
				ClearStateFor("llama");
			}
			else
			{
				LblLlamaStatus.Text = "● 运行中";
				LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 185, 80));
			}
		}
	}

	private void OnLlamaDone()
	{
		if (_llamaProc != null && _llamaProc.HasExited)
		{
			int exitCode = 0;
			try
			{
				exitCode = _llamaProc.ExitCode;
			}
			catch
			{
			}
			DisposeProc(ref _llamaProc);
			_llamaApi?.ResetLogMachine(); // 进程退出即清采集状态, 防止"假在线"冻结数据继续进图表
			BtnLlamaStart.IsEnabled = true;
			BtnLlamaStop.IsEnabled = false;
			if (exitCode != 0)
			{
				LblLlamaStatus.Text = "● 启动失败/崩溃";
				LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 54, 51));
				Log("--- Llama 异常退出 (退出码 " + exitCode + ") --- 请查看上方日志，通常是显存不足或参数错误导致 ---");
				// OOM 自动降层重试（ollama 同款闭环）: 显存不足信号 + 开关开启 + 还有重试额度
				if (_oomSeenInRun && ChkOomRetry.IsChecked == true && _oomRetryLeft > 0)
				{
					if (TryAutoReduceNglAndRelaunch())
					{
						return;
					}
				}
				// 重试未执行(额度耗尽/开关关闭/降层失败): 必须复位, 否则
				// _oomRetryActive 卡死 → 之后手动启动永久跳过显存预算确认弹窗
				_oomRetryActive = false;
			}
			else
			{
				LblLlamaStatus.Text = "● 已停止";
				LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));
				Log("--- Llama 已退出 ---");
				_oomRetryLeft = 3; // 干净退出(用户手动停止)才重置重试额度
				_oomRetryActive = false;
			}
			ClearStateFor("llama");
		}
	}

	/// <summary>
	/// OOM 自动降层重试（ollama 同款闭环）: 把 --gpu-layers 降到上一轮实际上 GPU 层数的 85%
	/// （auto/全层首次重试直接从总层数起算），改写输入框后自动重新启动。成功进入运行态后
	/// 重试额度重置；连续 3 次失败或层数低于 4 时放弃，保留用户可手动调整的空间。
	/// </summary>
	private bool TryAutoReduceNglAndRelaunch()
	{
		if (_pw.TryGetValue("gpu-layers", out (System.Windows.Controls.CheckBox ec, System.Windows.Controls.Control vw) pair))
		{
			string cur = ((pair.vw is System.Windows.Controls.ComboBox c) ? c.Text : ((System.Windows.Controls.TextBox)pair.vw).Text).Trim();
			int baseNgl;
			if (!int.TryParse(cur, out int curNgl) || curNgl <= 0)
			{
				// auto/全层: 优先用上一轮实际 offload 的层数, 更符合"上次实际的 85%"语义
				baseNgl = _offloadedLayers > 0 ? _offloadedLayers : (_totalLayers > 0 ? _totalLayers : 0);
				if (baseNgl <= 0)
				{
					return false;
				}
			}
			else
			{
				baseNgl = curNgl;
			}
			int newNgl = Math.Max(4, (int)Math.Floor(baseNgl * 0.85));
			if (newNgl >= baseNgl)
			{
				newNgl = baseNgl - 1;
			}
			if (newNgl < 4)
			{
				Log("[OOM 重试] 层数已降到下限以下，放弃自动重试（当前 " + cur + "）。请调小 ctx、量化 KV 或换更小的量化。");
				return false;
			}
			_oomRetryLeft--;
			_oomRetryActive = true;
			pair.ec.IsChecked = true;
			if (pair.vw is System.Windows.Controls.ComboBox cb)
			{
				cb.Text = newNgl.ToString();
			}
			else
			{
				((System.Windows.Controls.TextBox)pair.vw).Text = newNgl.ToString();
			}
			Log("[OOM 重试] 显存不足，已把 --gpu-layers 从 " + cur + " 降到 " + newNgl + "（剩余自动重试 " + _oomRetryLeft + " 次），自动重启...");
			StartLlama();
			return true;
		}
		return false;
	}

	/// <summary>加载一次当前配置的模型，读到显存实测值就自动停止。</summary>

	private void StopLlama()
	{
		// 测试启动临时打开的 verbose 在这里统一还原，正常启动不受影响
		if (_vramTestVerboseRestore)
		{
			_vramTestVerboseRestore = false;
			if (_pbw.TryGetValue("verbose", out System.Windows.Controls.CheckBox? cbV) && cbV.IsChecked == true)
			{
				cbV.IsChecked = false;
				Log("测试结束，已关闭 --log-verbose。");
			}
		}

		if (_llamaProc == null && !IsLlamaRunning())
		{
			return;
		}
		int? pid = _llamaProc?.Id ?? ProcessHelper.FindProcessByPort(GetLlamaPort());
		Log("--- 正在停止 Llama ---");
		// 进程彻底退出前不允许再点启动，避免端口/显存还没释放就重复拉起。
		BtnLlamaStart.IsEnabled = false;
		BtnLlamaStop.IsEnabled = false;
		LblLlamaStatus.Text = "● 停止中...";
		LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 136, 62));
		if (pid.HasValue)
		{
			// WMI 杀树可达数秒, 移出 UI 线程(后续收尾由 FinishStop 轮询驱动)
			int killPid = pid.Value;
			Task.Run(() => ProcessHelper.KillProcessTree(killPid));
		}
		DisposeProc(ref _llamaProc);
		ClearStateFor("llama");
		_ = FinishStopLlamaAsync(pid);
	}

	/// <summary>等待 llama 进程和端口都真正释放后再恢复启动按钮。</summary>
	private async Task FinishStopLlamaAsync(int? pid)
	{
		try
		{
			for (int i = 0; i < 100; i++)
			{
				bool processAlive = false;
				if (pid.HasValue)
				{
					try
					{
						using Process p = Process.GetProcessById(pid.Value);
						processAlive = !p.HasExited;
					}
					catch
					{
					}
				}
				if (!processAlive && !IsLlamaRunning())
				{
					break;
				}
				await Task.Delay(200);
			}
			if (!IsLlamaRunning())
			{
				BtnLlamaStart.IsEnabled = true;
				BtnLlamaStop.IsEnabled = false;
				LblLlamaStatus.Text = "● 已停止";
				LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));
				Log("--- Llama 已彻底停止 ---");
			}
			else
			{
				BtnLlamaStart.IsEnabled = false;
				BtnLlamaStop.IsEnabled = true;
				LblLlamaStatus.Text = "● 停止失败/仍在运行";
				LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 54, 51));
				Log("--- Llama 进程未在 20 秒内退出，启动按钮保持禁用；可再次点停止或手动结束进程 ---");
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "等待 Llama 退出");
		}
	}

	private void StartDsh_Click(object s, RoutedEventArgs e)
	{
		// 目录含引号会破坏 cmd 命令行拼接(且可被构造成注入), 提前拒绝
		string dshDirGuard = EffectiveDshDir();
		if (dshDirGuard != null && dshDirGuard.Contains('"'))
		{
			ShowNotice("DSH 目录路径含引号字符，无法用于命令行，请更换目录。", isError: true);
			return;
		}
		// 每次手动点击启动都允许重新尝试一次"链接自愈"
		_dshModuleHealTried = false;
		int? existing = ProcessHelper.FindProcessByPort(3080);
		if (existing.HasValue)
		{
			_dshRetriesLeft = 0;
			_dshProc = null;
			_dshTracker = null;
			AttachDsh(existing.Value, "检测到端口 3080 已有 DSH 在运行，直接接管", saveState: true);
			return;
		}
		_dshRetriesLeft = 2;
		PrepareDshThenStart();
	}

	/// <summary>独立全量重建入口（升级时跳过重建/旧产物状态的恢复手段）：若 DSH 在运行则先停止 → clean + build:lib + build:web → 兼容性检查 → 自动启动。</summary>
	private async void RebuildDsh_Click(object s, RoutedEventArgs e)
	{
		if (ComboDshMode.SelectedIndex != 0)
		{
			ShowNotice("仅本地目录模式支持重建。");
			return;
		}
		string dshDir = EffectiveDshDir();
		if (!Directory.Exists(Path.Combine(dshDir, "apps", "cli")))
		{
			ShowNotice("DSH 目录无效: " + dshDir);
			return;
		}
		string? head = null;
		try
		{
			head = DshMaintenance.GetGitHead(dshDir);
		}
		catch (Exception ex)
		{
			LogError(ex, "读取 git HEAD");
		}
		if (head == null)
		{
			ShowNotice("无法读取仓库 git HEAD，请确认是 deepseek-harness 的 git checkout。", isError: true);
			return;
		}
		string msg = "将执行全量重建:\n  1/3 pnpm clean\n  2/3 pnpm build:lib\n  3/3 build:web (vite)\n"
				+ (HasDsh() ? "\nDSH 正在运行，会先停止 DSH。" : "")
				+ "\n重建完成后自动做兼容性检查并启动 DSH。\n继续？";
		if (System.Windows.MessageBox.Show(msg, "重建 DSH", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
		{
			return;
		}
		BtnRebuildDsh.IsEnabled = false;
		LblDshStatus.Text = "准备重建...";
		LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 136, 62));
		if (HasDsh())
		{
			StopDsh();
			bool stillRunning = await Task.Run(() =>
			{
				DateTime stopDeadline = DateTime.Now.AddSeconds(20.0);
				while ((HasDsh() || ProcessHelper.FindProcessByPort(3080).HasValue) && DateTime.Now < stopDeadline)
				{
					System.Threading.Thread.Sleep(500);
				}
				return HasDsh() || ProcessHelper.FindProcessByPort(3080).HasValue;
			});
			if (stillRunning)
			{
				BtnRebuildDsh.IsEnabled = true;
				LblDshStatus.Text = "已停止";
				ShowNotice("DSH 进程未完全退出，请手动结束后再点重建。", isError: true);
				return;
			}
		}
		StartDshRebuild(dshDir, head, PrepareDshThenStart);
	}

	/// <summary>
	/// DSH 启动前的维护步骤（依据 DSH_启动操作记录_20260830.md 遗留建议）:
	/// 1) 无 host 运行时自动清理 task-board 残留锁; 2) git HEAD 变化时提示自动重建编译产物。
	/// </summary>
	private void PrepareDshThenStart()
	{
		// 1) 残留锁: 锁存在 + 端口无监听 + 无 DSH node 进程 → 判定为未干净关闭的残留
		try
		{
			if (DshMaintenance.IsStaleLockPresent(3080))
			{
				File.Delete(DshMaintenance.LockPath);
				Log("[DSH] 已自动清理残留锁: " + DshMaintenance.LockPath);
			}
		}
		catch (Exception lockEx)
		{
			Log("[DSH] 清理残留锁失败(不影响启动): " + lockEx.Message);
		}
		// 2) 升级检测: 仅本地目录模式(npx 安装模式无仓库可检测)
		if (ComboDshMode.SelectedIndex == 0)
		{
			try
			{
				string dshDir = EffectiveDshDir();
				string? head = DshMaintenance.GetGitHead(dshDir);
				(string? builtHead, string? builtAt) = DshMaintenance.LoadBuildState();
				if (head != null && builtHead != null && builtHead != head)
				{
					string msg = "检测到 DSH 仓库已升级 (基线 " + builtHead.Substring(0, 7) + " → 当前 " + head.Substring(0, 7) + ")。\n\n"
						+ "旧编译产物会导致启动失败(大面积 MISSING_EXPORT)，需要全量重建:\n"
						+ "  1/3 pnpm clean\n  2/3 pnpm build:lib\n  3/3 build:web (vite)\n\n"
						+ "是 = 现在自动重建 (后台运行，约几分钟，断开远程不中断)\n"
						+ "否 = 跳过重建直接启动 (大概率报旧产物错误)";
					if (System.Windows.MessageBox.Show(msg, "DSH 升级重建", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
					{
						StartDshRebuild(dshDir, head, PrepareDshThenStart);
						return;
					}
					Log("[DSH] 跳过重建直接启动 (上次重建基线 " + builtAt + ")，旧产物可能报错");
				}
				else if (head != null && builtHead == null)
				{
					DshMaintenance.SaveBuildState(head);
					Log("[DSH] 已记录 DSH 构建基线 HEAD " + head.Substring(0, 7) + " (之后仓库升级将提示自动重建)");
				}
			}
			catch (Exception upEx)
			{
				Log("[DSH] 升级检测失败(不影响启动): " + upEx.Message);
			}
		}
		StartDsh();
	}

	/// <summary>
	/// DSH 编译产物全量重建: WMI 分离运行 cmd 脚本(clean → build:lib → node24 vite build)，
	/// 输出重定向到日志文件由轮询器尾随显示。断开远程/关闭启动器都不会中断重建。
	/// </summary>
	private void StartDshRebuild(string dshDir, string head, Action onDone)
	{
		try
		{
			string? nodeExe = Toolchain.ResolveNode24Exe();
			if (nodeExe == null)
			{
				string anyNode = Toolchain.ResolveNodeExe(require24: false) ?? "(未找到)";
				ShowNotice("未找到 node v24+（当前: " + anyNode + "）\n"
					+ "DSH 重建必须使用 node24。请安装 node24，或把它的目录加入 PATH。\n\n无法自动重建，将直接启动。");
				BtnRebuildDsh.IsEnabled = true;
				StartDsh();
				return;
			}
			string? pnpmExe = Toolchain.ResolvePnpmExe();
			if (pnpmExe == null)
			{
				ShowNotice("未找到 pnpm：无法执行 pnpm clean/build:lib。\n请安装 pnpm（或 corepack enable）后重试。");
				BtnRebuildDsh.IsEnabled = true;
				return;
			}
			string logPath = Path.Combine(Path.GetTempPath(), "LlamaDSH_dsh_rebuild.log");
			string cmdPath = Path.Combine(Path.GetTempPath(), "LlamaDSH_dsh_rebuild.cmd");
			string nodeDir = Path.GetDirectoryName(nodeExe) ?? "";
			string pnpmDir = Path.GetDirectoryName(pnpmExe) ?? "";
			string pathPrefix = string.Join(";", new[] { nodeDir, pnpmDir }.Where(delegate (string x) { return !string.IsNullOrEmpty(x); }));
			string q = "\"";
			string redirect = " >> " + q + logPath + q + " 2>&1";
			string cmdText = "@echo off" + Environment.NewLine
				+ "set \"PATH=" + pathPrefix + ";%PATH%\"" + Environment.NewLine
				+ "cd /d " + q + dshDir + q + Environment.NewLine
				+ "echo [REBUILD] 1/3 pnpm clean" + redirect + Environment.NewLine
				+ "call pnpm clean" + redirect + Environment.NewLine
				+ "if errorlevel 1 goto :fail" + Environment.NewLine
				+ "echo [REBUILD] 2/3 pnpm build:lib" + redirect + Environment.NewLine
				+ "call pnpm build:lib" + redirect + Environment.NewLine
				+ "if errorlevel 1 goto :fail" + Environment.NewLine
				+ "echo [REBUILD] 3/3 build:web (node24 vite)" + redirect + Environment.NewLine
				+ "cd /d " + q + dshDir + "\\apps\\web" + q + Environment.NewLine
				+ q + nodeExe + q + " node_modules\\vite\\bin\\vite.js build" + redirect + Environment.NewLine
				+ "if errorlevel 1 goto :fail" + Environment.NewLine
				+ "echo EXITCODE:0" + redirect + Environment.NewLine
				+ "exit /b 0" + Environment.NewLine
				+ ":fail" + Environment.NewLine
				+ "echo EXITCODE:1" + redirect + Environment.NewLine
				+ "exit /b 1";
			File.WriteAllText(cmdPath, cmdText);
			File.WriteAllText(logPath, "");
			bool spawned = false;
			using (System.Management.ManagementClass mc = new System.Management.ManagementClass("Win32_Process"))
			{
				System.Management.ManagementBaseObject inParams = mc.GetMethodParameters("Create");
				inParams["CommandLine"] = "cmd.exe /c " + q + cmdPath + q;
				inParams["CurrentDirectory"] = dshDir;
				using (System.Management.ManagementClass suClass = new System.Management.ManagementClass("Win32_ProcessStartup"))
				{
					System.Management.ManagementBaseObject su = suClass.CreateInstance();
					su["ShowWindow"] = 0;
					try
					{
						System.Collections.IDictionary envVars = Environment.GetEnvironmentVariables();
						string[] envArray = new string[envVars.Count];
						int envIndex = 0;
						foreach (System.Collections.DictionaryEntry entry in envVars)
						{
							if (entry.Key == null || entry.Value == null) continue;
							if (string.Equals((string)entry.Key, "__COMPAT_LAYER", StringComparison.OrdinalIgnoreCase)) continue;
							envArray[envIndex++] = entry.Key + "=" + entry.Value;
						}
						Array.Resize(ref envArray, envIndex);
						su["EnvironmentVariables"] = envArray;
					}
					catch
					{
					}
					inParams["ProcessStartupInformation"] = su;
				}
				System.Management.ManagementBaseObject outParams = mc.InvokeMethod("Create", inParams, null);
				int rc = Convert.ToInt32(outParams["ReturnValue"]);
				if (rc != 0)
				{
					Log("[DSH重建] 分离启动失败(WMI rc=" + rc + ")，放弃重建直接启动");
					BtnRebuildDsh.IsEnabled = true;
					StartDsh();
					return;
				}
				spawned = true;
				try
				{
					int childPid = Convert.ToInt32(outParams["ProcessId"]);
					using (System.Diagnostics.Process cp = System.Diagnostics.Process.GetProcessById(childPid))
					{
						cp.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
					}
					Log("[DSH重建] 重建进程优先级 BelowNormal(减少对其他实例的影响)");
				}
				catch
				{
				}
			}
			if (!spawned)
			{
				StartDsh();
				return;
			}
			_dshRebuildLogPath = logPath;
			_dshRebuildLogPos = 0;
			_dshRebuildHead = head;
			_dshRebuildDone = onDone;
			_dshRebuildDeadline = DateTime.Now.AddMinutes(30.0);
			_dshRebuildDir = dshDir;
			BtnDshStart.IsEnabled = false;
			BtnDshStop.IsEnabled = false;
			LblDshStatus.Text = "Rebuilding...";
			LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 136, 62));
			Log("--- 检测到 DSH 仓库升级，开始后台重建 (clean + build:lib + build:web) ---");
			Log("[DSH重建] 日志: " + logPath);
			_dshRebuildTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromSeconds(2.0)
			};
			_dshRebuildTimer.Tick += delegate
			{
				PollDshRebuild();
			};
			_dshRebuildTimer.Start();
		}
		catch (Exception ex)
		{
			LogError(ex, "DSH 重建启动失败");
			BtnRebuildDsh.IsEnabled = true;
			StartDsh();
		}
	}

	private void StopDshRebuildTimer()
	{
		_dshRebuildTimer?.Stop();
		_dshRebuildTimer = null;
	}

	private void PollDshRebuild()
	{
		try
		{
			if (File.Exists(_dshRebuildLogPath))
			{
				using (FileStream fs = new FileStream(_dshRebuildLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				{
					if (fs.Length < _dshRebuildLogPos)
					{
						_dshRebuildLogPos = 0;
					}
					if (fs.Length > _dshRebuildLogPos)
					{
						fs.Seek(_dshRebuildLogPos, SeekOrigin.Begin);
						using (StreamReader sr = new StreamReader(fs))
						{
							string chunk = sr.ReadToEnd();
							_dshRebuildLogPos = fs.Length;
							string[] lines = chunk.Replace("\r\n", "\n").Split('\n');
							string[] array = lines;
							foreach (string line in array)
							{
								string t = line.TrimEnd();
								if (t.Length > 0)
								{
									Log("[DSH重建] " + ((t.Length > 300) ? (t.Substring(0, 300) + "…") : t));
								}
							}
						}
					}
				}
				string? exitCode = null;
				using (FileStream ef = new FileStream(_dshRebuildLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				using (StreamReader er = new StreamReader(ef))
				{
					string line2;
					while ((line2 = er.ReadLine()) != null)
					{
						if (line2.StartsWith("EXITCODE:"))
						{
							exitCode = line2.Substring(9).Trim();
						}
					}
				}
				if (exitCode != null)
				{
					StopDshRebuildTimer();
					BtnDshStart.IsEnabled = true;
					BtnRebuildDsh.IsEnabled = true;
					BtnDshStop.IsEnabled = false;
					if (exitCode == "0")
					{
						DshMaintenance.SaveBuildState(_dshRebuildHead);
						Log("--- [DSH重建] ✅ 重建完成，基线已更新为 " + _dshRebuildHead.Substring(0, 7) + "，继续启动 DSH ---");
						LblDshStatus.Text = "Stopped";
						LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 185, 80));
						Action? done = _dshRebuildDone;
						_dshRebuildDone = null;
						_ = CompleteRebuildWithCompatAsync(done);
					}
					else
					{
						Log("--- [DSH重建] ❌ 重建失败 (EXITCODE=1)，请查看上方日志 ---");
						LblDshStatus.Text = "Rebuild failed";
						LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 81, 73));
						ShowNotice("DSH 重建失败，详见启动器日志。\n\n常见原因: node24/pnpm 不可用、依赖安装网络问题、TS 编译错误。", isError: true);
					}
					return;
				}
			}
			if (DateTime.Now > _dshRebuildDeadline)
			{
				StopDshRebuildTimer();
				BtnDshStart.IsEnabled = true;
				BtnRebuildDsh.IsEnabled = true;
				Log("[DSH重建] ⚠ 等待超时(30分钟)，停止监视。重建进程仍在后台运行，完成后可直接再点启动。");
			}
		}
		catch (Exception ex2)
		{
			LogError(ex2, "DSH 重建轮询");
		}
	}

	private void StopDsh_Click(object s, RoutedEventArgs e)
	{
		if (!HasDsh() && !ProcessHelper.FindProcessByPort(3080).HasValue)
		{
			return;
		}
		if (System.Windows.MessageBox.Show("确定要停止 DSH Web 吗？\n\n正在运行的任务/会话会中断。",
			"停止 DSH Web", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
		{
			return;
		}
		StopDsh();
	}

	/// <summary>llama.cpp 报告 "failed to fit params" 时提示用户：当前参数实际已超出可用显存。</summary>
	// llama-server 0.7 显存汇总行的实测值（对齐 ollama 的日志解析方式）：
	// 启动后展示 llama 实际计算的 breakdown，与启动前预估形成对照
	private double _obsModelMiB = -1.0;
	private double _obsKvMiB = -1.0;
	private double _obsRsMiB = -1.0;
	private double _obsComputeMiB = -1.0;
	private bool _obsLogged;
	// OOM 自动降层重试状态
	private bool _oomSeenInRun;
	private int _offloadedLayers;
	private int _totalLayers;
	private int _oomRetryLeft = 3;
	private bool _oomRetryActive;

	private void NoteLlamaMemoryLine(string text)
	{
		// llama 自己说"适配不下"时，说明当前参数已超出可用显存，静态占用再好看也没用。
		if (text.Contains("failed to fit params", StringComparison.OrdinalIgnoreCase))
		{
			_oomSeenInRun = true;
			Log("[显存] llama 报告无法把参数适配进可用显存（failed to fit params）：实际上已经超了，建议调小 ubatch 或把更多专家放到 CPU");
		}
		else if (text.Contains("out of memory", StringComparison.OrdinalIgnoreCase) || text.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase))
		{
			_oomSeenInRun = true;
		}
		// offloaded X/Y layers to GPU: 记录总层数与实际上 GPU 层数（OOM 降层重试的基准）
		Match offload = Regex.Match(text, @"offloaded\s+(\d+)/(\d+)\s+layers", RegexOptions.IgnoreCase);
		if (offload.Success)
		{
			_offloadedLayers = int.TryParse(offload.Groups[1].Value, out int offN) ? offN : 0;
			_totalLayers = int.TryParse(offload.Groups[2].Value, out int totN) ? totN : 0;
		}
		// buffer 汇总行解析（ollama 同款思路：从日志读取 llama 实际计算结果）
		double mib = -1.0;
		bool model = text.Contains(" model buffer size = ", StringComparison.Ordinal);
		bool kv = text.Contains(" KV buffer size = ", StringComparison.Ordinal);
		bool rs = text.Contains(" RS buffer size = ", StringComparison.Ordinal);
		bool compute = text.Contains(" compute buffer size = ", StringComparison.Ordinal);
		if (model || kv || rs || compute)
		{
			int eq = text.LastIndexOf("= ", StringComparison.Ordinal);
			if (eq >= 0 && double.TryParse(text.Substring(eq + 2).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out mib) && mib >= 0.0)
			{
				// 排除 CPU/host 侧缓冲（CPU_*/..._Host/CPU_REPACK/BLAS），只统计设备显存
				bool hostSide = text.Contains("_Host ", StringComparison.Ordinal)
					|| text.Contains("CPU_", StringComparison.Ordinal)
					|| text.Contains("CPU model", StringComparison.Ordinal)
					|| text.Contains("BLAS", StringComparison.Ordinal);
				if (!hostSide)
				{
					if (model) _obsModelMiB = mib;
					else if (kv) _obsKvMiB = mib;
					else if (rs) _obsRsMiB = mib;
					else if (compute) _obsComputeMiB = mib;
				}
			}
			return;
		}
		// 四段齐了（compute 通常最后 reserve）→ 输出实测对照
		if (!_obsLogged && _obsModelMiB >= 0.0 && _obsComputeMiB >= 0.0)
		{
			_obsLogged = true;
			double sum = Math.Max(0.0, _obsModelMiB) + Math.Max(0.0, _obsKvMiB) + Math.Max(0.0, _obsRsMiB) + Math.Max(0.0, _obsComputeMiB);
			Log(string.Format("[显存实测] llama 实际分配: 权重 {0:F0} + KV {1:F0} + RS {2:F0} + 计算图 {3:F0} = {4:F2} GiB（可与启动前预估对照）",
				_obsModelMiB, _obsKvMiB, _obsRsMiB, _obsComputeMiB, sum / 1024.0));
		}
	}

	/// <summary>启动日志里是否出现模块缺失（pnpm workspace 链接悬空的典型症状）。</summary>
	private bool DshBootLogHasModuleMissing()
	{
		try
		{
			if (_dshBootLogPath == null || !File.Exists(_dshBootLogPath))
			{
				return false;
			}
			return File.ReadAllText(_dshBootLogPath).Contains("ERR_MODULE_NOT_FOUND", StringComparison.Ordinal);
		}
		catch
		{
			return false;
		}
	}

	private void StartDsh()
	{
		if (HasDsh())
		{
			ShowNotice("Already running");
			return;
		}
		try
		{
			string text2;
			string text3;
			string dshDir = "";
			if (ComboDshMode.SelectedIndex == 0)
			{
				dshDir = EffectiveDshDir();
				if (string.IsNullOrEmpty(dshDir))
				{
					ShowNotice("未找到 DSH 安装目录。\n\n请点“🔍 自动检索”或手动选择 deepseek-harness 目录。", isError: true);
					return;
				}
				text3 = ((File.Exists(Path.Combine(dshDir, "apps", "cli", "lib", "bin.js")) && _dshRetriesLeft > 0) ? "compiled-js" : "compiled-tsx");
			}
			else
			{
				text3 = "npx";
			}
			if (ComboDshMode.SelectedIndex == 0)
			{
				if (Toolchain.ResolveNode24Exe() == null)
				{
					string anyNode = Toolchain.ResolveNodeExe(require24: false) ?? "(未找到)";
					ShowNotice("未找到 node v24+（当前: " + anyNode + "）。\n\n"
						+ "DSH 必须使用 node24。请安装 node24 或把它的目录加入 PATH；也可点“🔍 自动检索”确认目录。", isError: true);
					return;
				}
				if (text3 == "compiled-tsx" && Toolchain.ResolvePnpmExe() == null)
				{
					ShowNotice("编译产物不存在，需要 pnpm 以 compiled-tsx 方式启动，但未找到 pnpm。\n\n"
						+ "请安装 pnpm（或 corepack enable）后重试。", isError: true);
					return;
				}
			}
			BtnDshStart.IsEnabled = false;
			BtnDshStop.IsEnabled = true;
			BtnOpenDsh.IsEnabled = false;
			LblDshStatus.Text = "Starting...";
			LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 136, 62));
				// 优先：WMI 分离启动。进程由 WmiPrvSE.exe 服务拉起，与启动器无进程亲缘，
				// 可绕过杀软对"启动器直接派生 node"的行为拦截
				//（症状：终端/脚本里启动一切正常，启动器里 100% 报 ERR_MODULE_NOT_FOUND）
			// 管理员提权启动（runas 静默提权）：子进程拿到 admin 令牌 + 高完整性，可读游戏内存。
			// WMI 分离路径不能用于提权（Win32_Process.Create 继承调用者非提权上下文），故走 ShellExecute runas；
			// 提权失败不静默回退普通启动——那会让管理员模式名存实亡。
			string? nodeExe = Toolchain.ResolveNode24Exe();
			string? pnpmExe = Toolchain.ResolvePnpmExe();
			string nodeCmd = string.IsNullOrEmpty(nodeExe) ? "node" : "\"" + nodeExe + "\"";
			string pnpmCmd = string.IsNullOrEmpty(pnpmExe) ? "pnpm" : "\"" + pnpmExe + "\"";
			if (ChkDshAdmin.IsChecked == true)
			{
				string innerCmd = ((ComboDshMode.SelectedIndex == 0) ? (((text3 == "compiled-js") ? nodeCmd + " apps/cli/lib/bin.js web" : pnpmCmd + " dsh web")) : "npx --yes @deepseek-ai/dsh web");
				if (TryStartDshElevated(dshDir, innerCmd))
				{
					_dshProc = null;
					_dshTracker = null;
					_dshVerifyDeadline = DateTime.Now.AddSeconds(30.0);
					Log("--- 正在启动 DSH (" + text3 + " · 管理员提权) ---");
					StartDshVerifyLoop();
					return;
				}
				BtnDshStart.IsEnabled = true;
				BtnDshStop.IsEnabled = false;
				LblDshStatus.Text = "● 启动失败";
				LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 54, 51));
				ClearStateFor("dsh");
				return;
			}
			_dshAdminMode = false; // 非管理员路径（WMI 分离 / 直接启动）
			if (ComboDshMode.SelectedIndex == 0 && TryStartDshDetached(dshDir, text3))
			{
				_dshProc = null;
				_dshTracker = null;
				_dshVerifyDeadline = DateTime.Now.AddSeconds(30.0);
				Log("--- 正在启动 DSH (" + text3 + " · 分离模式) ---");
				StartDshVerifyLoop();
				return;
			}
			StopDshLogTailer();
			if (ComboDshMode.SelectedIndex == 0)
			{
				if (text3 == "compiled-js")
				{
					text2 = "cd /d \"" + dshDir + "\" && " + nodeCmd + " apps/cli/lib/bin.js web";
				}
				else
				{
					if (File.Exists(Path.Combine(dshDir, "apps", "cli", "lib", "bin.js")))
					{
						Log("--- 编译版连续启动失败，本次回退到 pnpm dsh web ---");
					}
					text2 = "cd /d \"" + dshDir + "\" && " + pnpmCmd + " dsh web";
				}
			}
			else
			{
				text2 = "npx --yes @deepseek-ai/dsh web";
			}
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = "cmd.exe",
				Arguments = "/c " + text2,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			Toolchain.AugmentPath(startInfo);
			_dshProc = Process.Start(startInfo);
			if (_dshProc == null)
			{
				throw new Exception("● 启动失败");
			}
			SaveState();
			PumpProcessStreams(_dshProc, "[dsh] ", delegate
			{
				OnDshDone();
			});
			Log("--- 正在启动 DSH (" + text3 + ") ---");
			DispatcherTimer dispatcherTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromSeconds(3.0)
			};
			dispatcherTimer.Tick += delegate
			{
				dispatcherTimer.Stop();
				VerifyDsh();
			};
			dispatcherTimer.Start();
		}
		catch (Exception ex)
		{
			LogError(ex, "DSH 启动失败");
			BtnDshStart.IsEnabled = true;
			BtnDshStop.IsEnabled = false;
		}
	}

		// 通过 WMI Win32_Process.Create 分离启动 DSH：父进程是 WmiPrvSE.exe（WMI 服务宿主），
	// 与启动器完全无亲缘；node 输出重定向到日志文件，由启动器尾随显示
	private bool TryStartDshDetached(string dshDir, string modeLabel)
	{
		try
		{
			string? detNode = Toolchain.ResolveNode24Exe();
			string? detPnpm = Toolchain.ResolvePnpmExe();
			string detNodeCmd = string.IsNullOrEmpty(detNode) ? "node" : "\"" + detNode + "\"";
			string detPnpmCmd = string.IsNullOrEmpty(detPnpm) ? "pnpm" : "\"" + detPnpm + "\"";
			string text = ((modeLabel == "compiled-js") ? detNodeCmd + " apps/cli/lib/bin.js web" : detPnpmCmd + " dsh web");
			string text2 = Path.Combine(Path.GetTempPath(), "LlamaDSH_dsh_boot.log");
			try
			{
				File.WriteAllText(text2, "");
			}
			catch
			{
			}
			_dshBootLogPath = text2;
			bool elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
			File.AppendAllText(text2, "[diag] launcher elevated=" + elevated + " temp=" + Path.GetTempPath() + "\r\n");
			string detPrefix = Toolchain.PathPrefix;
			string detSetPath = string.IsNullOrEmpty(detPrefix) ? "" : ("set \"PATH=" + detPrefix + ";%PATH%\" & ");
			string text3 = "cmd.exe /c " + detSetPath + "cd /d \"" + dshDir + "\" & echo ===CHILD-ENV=== >> \"" + text2 + "\" 2>&1 & set >> \"" + text2 + "\" 2>&1 & where node >> \"" + text2 + "\" 2>&1 & " + detNodeCmd + " -v >> \"" + text2 + "\" 2>&1 & echo ===BOOT=== >> \"" + text2 + "\" 2>&1 & " + text + " >> \"" + text2 + "\" 2>&1";
			using (System.Management.ManagementClass managementClass = new System.Management.ManagementClass("Win32_Process"))
			{
				System.Management.ManagementBaseObject methodParameters = managementClass.GetMethodParameters("Create");
				methodParameters["CommandLine"] = text3;
				methodParameters["CurrentDirectory"] = dshDir;
				// 显式传递环境变量：不依赖 WmiPrvSE 宿主按调用者上下文派生的环境，
				// 保证子进程拿到与用户 PowerShell 一致的环境（矩阵测试验证过该环境下启动必成功）
				try
				{
					System.Collections.IDictionary envVars = Environment.GetEnvironmentVariables();
					string[] envArray = new string[envVars.Count];
					int envIndex = 0;
					foreach (System.Collections.DictionaryEntry entry in envVars)
					{
						if (entry.Key == null || entry.Value == null) continue;
						// strip installer compat layer marker (would force-elevate every child)
						if (string.Equals((string)entry.Key, "__COMPAT_LAYER", StringComparison.OrdinalIgnoreCase)) continue;
						envArray[envIndex++] = entry.Key + "=" + entry.Value;
					}
					Array.Resize(ref envArray, envIndex);
					using (System.Management.ManagementClass startupClass = new System.Management.ManagementClass("Win32_ProcessStartup"))
					{
						System.Management.ManagementBaseObject startup = startupClass.CreateInstance();
						startup["ShowWindow"] = 0;
						// SW_HIDE: WMI Create shows a visible console window by default - force hide
						if (envArray != null) startup["EnvironmentVariables"] = envArray;
						methodParameters["ProcessStartupInformation"] = startup;
					}
					File.AppendAllText(text2, "[diag] explicit env entries=" + envIndex + "\r\n");
				}
				catch (Exception envEx)
				{
					File.AppendAllText(text2, "[diag] explicit env failed: " + envEx.Message + "\r\n");
				}
				System.Management.ManagementBaseObject managementBaseObject = managementClass.InvokeMethod("Create", methodParameters, null);
				if (Convert.ToInt32(managementBaseObject["ReturnValue"]) != 0)
				{
					Log("--- 分离模式创建失败 (WMI rc=" + managementBaseObject["ReturnValue"] + ")，改用直接启动 ---");
					return false;
				}
			}
			StartDshLogTailer(text2);
			return true;
		}
		catch (Exception ex)
		{
			LogError(ex, "DSH 分离启动失败，改用直接启动");
			return false;
		}
	}

	// 探针脚本以嵌入资源随 exe 发布（单文件发布下 AppContext.BaseDirectory 指向 bundle 自解压临时目录，旁边的落盘文件不可依赖），
	// 每次提权启动前写到 %TEMP%，交给提权子进程调用；任何失败都返回 null（跳过探针，不影响 DSH 启动）。
	private static string? ExtractGameAccessProbe()
	{
		try
		{
			// GUID 名: 固定名在写入与提权子进程读取之间可被改写(powershell 语义);
			// 探针由分离子进程异步消费不能就地删, 残留由 UpdateService.CleanupLauncherTemp 按龄清扫。
			string text = Path.Combine(Path.GetTempPath(), "LlamaDSH_GameAccessProbe_" + Guid.NewGuid().ToString("N") + ".ps1");
			using (Stream manifestResourceStream = typeof(MainWindow).Assembly.GetManifestResourceStream("LlamaDSH_Launcher.Resources.GameAccessProbe.ps1")!)
			{
				if (manifestResourceStream == null)
				{
					return null;
				}
				using (FileStream fileStream = File.Create(text))
				{
					manifestResourceStream.CopyTo(fileStream);
				}
			}
			return text;
		}
		catch
		{
			return null;
		}
	}

	// 管理员提权启动：ShellExecute runas 把子进程提升到 admin（本机 ConsentPromptBehaviorAdmin=0 → 静默提权，无 UAC 弹窗）；
	// 子进程为高完整性，可 OpenProcess 游戏进程内存（中完整性进程会被 UIPI 拦截）。
	// 注意：WMI 分离路径无法提权（Win32_Process.Create 继承调用者非提权上下文），故提权必须走 ShellExecute runas。
	// innerCmd 由调用方按启动模式拼好（node lib/bin.js web 或 pnpm dsh web 或 npx ...）。
	private bool TryStartDshElevated(string dshDir, string innerCmd)
	{
		try
		{
			string text = Path.Combine(Path.GetTempPath(), "LlamaDSH_dsh_boot.log");
			try
			{
				File.WriteAllText(text, "");
			}
			catch
			{
			}
			_dshBootLogPath = text;
			bool elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
			// 启动前先用探针确认高完整性进程能否打开游戏进程句柄（探针文件缺失则跳过，不影响启动）
			string text2 = ExtractGameAccessProbe();
			string elevPrefix = Toolchain.PathPrefix;
			string elevSetPath = string.IsNullOrEmpty(elevPrefix) ? "" : ("set \"PATH=" + elevPrefix + ";%PATH%\" & ");
			string text3 = elevSetPath + (string.IsNullOrEmpty(dshDir) ? "" : ("cd /d \"" + dshDir + "\" & "));
			string text4 = ((text2 != null) ? ("powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"" + text2 + "\" >> \"" + text + "\" 2>&1 & ") : "");
			File.AppendAllText(text, "[diag] launcher elevated=" + elevated + " admin-mode=True temp=" + Path.GetTempPath() + "\r\n");
			ProcessStartInfo processStartInfo = new ProcessStartInfo
			{
				UseShellExecute = true,
				Verb = "runas",
				FileName = "cmd.exe",
				// WindowStyle 在 ShellExecute 下被传给 ShellExecuteEx 的 nShow，用于避免留下常驻黑色控制台窗口
				WindowStyle = ProcessWindowStyle.Hidden,
				Arguments = "/c " + text3 + text4 + innerCmd + " >> \"" + text + "\" 2>&1"
			};
			Process.Start(processStartInfo);
			_dshAdminMode = true;
			StartDshLogTailer(text);
			return true;
		}
		catch (Exception ex)
		{
			LogError(ex, "DSH 管理员提权启动失败（提权被拒绝或 UAC 取消）");
			return false;
		}
	}

	private void StartDshLogTailer(string path)
	{
		StopDshLogTailer();
		_dshTailCts = new CancellationTokenSource();
		CancellationToken token = _dshTailCts.Token;
		long pos = 0L;
		Task.Run(async delegate
		{
			while (!token.IsCancellationRequested)
			{
				try
				{
					if (File.Exists(path))
					{
						using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
						{
							if (stream.Length < pos)
							{
								pos = 0L;
							}
							if (stream.Length > pos)
							{
								stream.Seek(pos, SeekOrigin.Begin);
								using (StreamReader streamReader = new StreamReader(stream))
								{
									string text = streamReader.ReadToEnd();
									int num = text.LastIndexOf('\n');
									if (num >= 0)
									{
										string text2 = text.Substring(0, num + 1);
										pos += Encoding.UTF8.GetByteCount(text2);
										string[] array = text2.Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
										foreach (string value in array)
										{
											_logPending.Enqueue("[dsh] " + value);
										}
									}
								}
							}
						}
					}
				}
				catch
				{
				}
				try
				{
					await Task.Delay(400, token);
				}
				catch
				{
					break;
				}
			}
		}, token);
	}

	private void StopDshLogTailer()
	{
		try
		{
			_dshTailCts?.Cancel();
		}
		catch
		{
		}
		_dshTailCts = null;
	}

		// 分离模式下的启动验证：每2秒查一次端口3080；启动日志出现致命错误则快速失败
	private void StartDshVerifyLoop()
	{
		StopDshVerifyLoop();
		_dshVerifyTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(2.0)
		};
		_dshVerifyTimer.Tick += delegate
		{
			int? num = ProcessHelper.FindProcessByPort(3080);
			bool flag = false;
			if (!num.HasValue && _dshBootLogPath != null)
			{
				try
				{
					if (File.Exists(_dshBootLogPath))
					{
						string text = File.ReadAllText(_dshBootLogPath);
						if (text.Contains("ERR_MODULE_NOT_FOUND") || text.Contains("Command failed with exit code"))
						{
							flag = true;
						}
					}
				}
				catch
				{
				}
			}
			if (num.HasValue)
			{
				StopDshVerifyLoop();
				_dshTracker = new ProcessTracker(num.Value);
				LblDshStatus.Text = (_dshAdminMode ? "● 运行中(管理员)" : "● 运行中");
				LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 185, 80));
				BtnOpenDsh.IsEnabled = true;
				SaveState();
				Log("DSH real PID:" + num.Value);
			}
			else if (flag || DateTime.Now >= _dshVerifyDeadline)
			{
				StopDshVerifyLoop();
				Log(flag ? "DSH failed (启动日志报错)" : "DSH failed (启动超时)");
				StopDshLogTailer();
				_dshTracker = null;

				// 模块缺失自愈：pnpm 的 workspace 链接存的是绝对路径，DSH 目录搬家/复制后全部悬空，
				// 表现为 "Cannot find package '@deepseek-ai/xxx'"。这里只重连链接并重试一次，
				// 比直接回落 compiled-tsx（会触发 pnpm install 直到超时）快得多，也不会改动源码。
				if (!_dshModuleHealTried && DshBootLogHasModuleMissing())
				{
					_dshModuleHealTried = true;
					LblDshStatus.Text = "● 修复 node_modules 链接…";
					LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 168, 83));
					Log("--- 启动日志出现 ERR_MODULE_NOT_FOUND：尝试重连 node_modules 失效链接 ---");
					string healDir = EffectiveDshDir();
					string healProfile = _currentProfile;
					Task.Run(() => DshCompatService.RepairBrokenLinksOnly(healDir, healProfile)).ContinueWith(t =>
					{
						Dispatcher.BeginInvoke((Action)(() =>
						{
							if (t.IsFaulted)
							{
								Log("[自愈] 重连链接失败: " + t.Exception?.GetBaseException().Message);
							}
							else
							{
								(int Detected, int Relinked, int Removed, int Failed) r = t.Result;
								Log("[自愈] 失效链接 " + r.Detected + " 条 → 重连 " + r.Relinked + " / 清理 " + r.Removed + " / 失败 " + r.Failed);
							}
							_dshRetriesLeft = 2;
							Log("--- 修复后重新启动 DSH ---");
							StartDsh();
						}));
					});
					return;
				}

				if (_dshRetriesLeft > 0)
				{
					_dshRetriesLeft--;
					Log("--- DSH 启动失败，自动重试（剩余 " + _dshRetriesLeft + " 次）---");
					StartDsh();
				}
				else
				{
					BtnDshStart.IsEnabled = true;
					BtnDshStop.IsEnabled = false;
					BtnOpenDsh.IsEnabled = false;
					LblDshStatus.Text = "● 启动失败";
					LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 54, 51));
					ClearStateFor("dsh");
				}
			}
		};
		_dshVerifyTimer.Start();
	}

	private void StopDshVerifyLoop()
	{
		if (_dshVerifyTimer != null)
		{
			_dshVerifyTimer.Stop();
			_dshVerifyTimer = null;
		}
	}

	private void VerifyDsh()
	{
		if (_dshProc == null)
		{
			return;
		}
		int id = _dshProc.Id;
		string text = EffectiveDshDir();
		int? num = ResolveDshPid(id, text);
		if (num.HasValue)
		{
			if (num.Value != id)
			{
				_dshTracker = new ProcessTracker(num.Value);
				SaveState();
				Log($"DSH real PID:{num.Value}");
			}
			else
			{
				_dshTracker = new ProcessTracker(id);
			}
			LblDshStatus.Text = (_dshAdminMode ? "● 运行中(管理员)" : "● 运行中");
			LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 185, 80));
			BtnOpenDsh.IsEnabled = true;
		}
		else
		{
			Log("DSH failed");
			bool dshExited = _dshProc != null && _dshProc.HasExited;
			DisposeProc(ref _dshProc);
			_dshTracker = null;
			if (dshExited && _dshRetriesLeft > 0)
			{
				_dshRetriesLeft--;
				Log("--- DSH 异常退出，自动重试（剩余 " + _dshRetriesLeft + " 次）---");
				StartDsh();
				return;
			}
			BtnDshStart.IsEnabled = true;
			BtnDshStop.IsEnabled = false;
			BtnOpenDsh.IsEnabled = false;
			LblDshStatus.Text = "● 启动失败";
			LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 54, 51));
			ClearStateFor("dsh");
		}
	}

	private void OnDshDone()
	{
		if (HasDsh())
		{
			int num = _dshProc?.Id ?? _dshTracker?.Pid ?? 0;
			string dd = EffectiveDshDir();
			int? num2 = ResolveDshPid(num, dd);
			if (num2.HasValue && num2.Value != num)
			{
				_dshTracker = new ProcessTracker(num2.Value);
				DisposeProc(ref _dshProc);
				SaveState();
				BtnOpenDsh.IsEnabled = true;
				Log($"DSH wrapper {num}->{num2.Value}");
			}
			else
			{
				DisposeProc(ref _dshProc);
				_dshTracker = null;
				BtnDshStart.IsEnabled = true;
				BtnDshStop.IsEnabled = false;
				BtnOpenDsh.IsEnabled = false;
				LblDshStatus.Text = "● 已停止";
				LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));
				Log("--- DSH 已退出 ---");
				ClearStateFor("dsh");
			}
		}
	}

	private void StopDsh()
	{
		if (!HasDsh() && !ProcessHelper.FindProcessByPort(3080).HasValue)
		{
			return;
		}
		StopDshVerifyLoop();
		StopDshLogTailer();
		BtnDshStart.IsEnabled = false;
		BtnDshStop.IsEnabled = false;
		LblDshStatus.Text = "● 停止中...";
		LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 136, 62));
		// 提权模式下 _dshProc 恒为 null，被托管的 pid 在 _dshTracker 里；先记下来供 UIPI 回退使用
		int? adminKillPid = (_dshTracker?.Pid ?? _dshProc?.Id ?? ProcessHelper.FindProcessByPort(3080)); // 接管来的提权/外部进程同样需要 UIPI 回退
		if (_dshProc != null)
		{
			Log("--- 正在停止 DSH ---");
			int killPid1 = _dshProc.Id;
			Task.Run(() => ProcessHelper.KillProcessTree(killPid1));
		}
		if (_dshTracker != null)
		{
			int killPid2 = _dshTracker.Pid;
			Task.Run(() =>
			{
				try
				{
					ProcessHelper.KillProcessTree(killPid2);
				}
				catch (Exception ex)
				{
					Console.WriteLine(ex.Message); // 后台线程无处 Log, 降级
				}
			});
		}
		if (adminKillPid.HasValue)
		{
			// UIPI：中完整性的启动器无法 TerminateProcess 高完整性（提权）子进程，普通停止会失败；
			// 此时用同样的 runas 静默提权起一个 taskkill 收尾。
			bool stillAlive = false;
			try
			{
				using (Process process = Process.GetProcessById(adminKillPid.Value))
				{
					stillAlive = !process.HasExited;
				}
			}
			catch
			{
			}
			if (stillAlive)
			{
				try
				{
					Process.Start(new ProcessStartInfo
					{
						UseShellExecute = true,
						Verb = "runas",
						FileName = "taskkill.exe",
						WindowStyle = ProcessWindowStyle.Hidden,
						Arguments = "/F /T /PID " + adminKillPid.Value
					});
					Log("--- 提权 DSH 进程普通停止无效（UIPI），已下发提权 taskkill ---");
				}
				catch (Exception ex2)
				{
					LogError(ex2, "下发提权 taskkill 失败，请手动结束该 DSH 进程");
				}
			}
		}
		_dshAdminMode = false;
		DisposeProc(ref _dshProc);
		_dshTracker = null;
		BtnOpenDsh.IsEnabled = false;
		Log("--- 已下发停止 DSH 指令，等待进程彻底退出 ---");
		ClearStateFor("dsh");
		_ = FinishStopDshAsync(adminKillPid);
	}

	/// <summary>等待 3080 端口/DSH 进程真正退出后再恢复启动按钮；提权 taskkill 是异步的。</summary>
	private static bool IsProcessAlive(int? pid)
	{
		if (!pid.HasValue)
		{
			return false;
		}
		try
		{
			using Process p = Process.GetProcessById(pid.Value);
			return !p.HasExited;
		}
		catch
		{
			return false;
		}
	}

	private async Task FinishStopDshAsync(int? pid)
	{
		try
		{
			for (int i = 0; i < 150; i++)
			{
				if (!ProcessHelper.FindProcessByPort(3080).HasValue && !HasDsh() && !IsProcessAlive(pid))
				{
					break;
				}
				await Task.Delay(200);
			}
			bool stillAlive = ProcessHelper.FindProcessByPort(3080).HasValue || HasDsh() || IsProcessAlive(pid);
			if (!stillAlive)
			{
				// 重建期间 StartDshRebuild 已锁住启动按钮，停止收尾任务不能把它重新打开。
				BtnDshStart.IsEnabled = _dshRebuildTimer == null;
				BtnDshStop.IsEnabled = false;
				BtnOpenDsh.IsEnabled = false;
				LblDshStatus.Text = "● 已停止";
				LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));
				Log("--- DSH 已彻底停止 ---");
			}
			else
			{
				BtnDshStart.IsEnabled = false;
				BtnDshStop.IsEnabled = true;
				LblDshStatus.Text = "● 停止失败/仍在运行";
				LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 54, 51));
				Log("--- DSH 进程未在 30 秒内退出，启动按钮保持禁用；可再次点停止或手动结束进程 ---");
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "等待 DSH 退出");
		}
	}

	private static void DisposeProc(ref Process? p)
	{
		if (p != null)
		{
			try
			{
				p.Dispose();
			}
			catch
			{
			}
			p = null;
		}
	}

	private void OpenDsh_Click(object s, RoutedEventArgs e)
	{
		string text = "http://127.0.0.1:3080";
		Log("Open:" + text);
		Process.Start(new ProcessStartInfo(text)
		{
			UseShellExecute = true
		});
	}

	private bool HasDsh()
	{
		if (_dshProc == null || _dshProc.HasExited)
		{
			if (_dshTracker != null)
			{
				return !_dshTracker.Poll().HasValue;
			}
			return false;
		}
		return true;
	}

	private bool HasRunning()
	{
		if (_llamaProc == null || _llamaProc.HasExited)
		{
			return HasDsh();
		}
		return true;
	}

	private int? ResolveDshPid(int wp, string? dd)
	{
		int? result = ProcessHelper.FindProcessByPort(3080);
		if (result.HasValue)
		{
			return result;
		}
		foreach (int item in ProcessHelper.FindChildPids(wp))
		{
			try
			{
				using Process process = Process.GetProcessById(item);
				bool flag;
				switch (process.ProcessName.ToLowerInvariant())
				{
				case "node":
				case "node.exe":
				case "electron":
				case "electron.exe":
					flag = true;
					break;
				default:
					flag = false;
					break;
				}
				if (flag)
				{
					return item;
				}
			}
			catch
			{
			}
		}
		using (List<int>.Enumerator enumerator = ProcessHelper.FindOrphanedDshProcesses(dd).GetEnumerator())
		{
			if (enumerator.MoveNext())
			{
				return enumerator.Current;
			}
		}
		return null;
	}

	private void PumpProcessStreams(Process proc, string tag, Action onExited)
	{
		Action<StreamReader> pump = delegate(StreamReader r)
		{
			try
			{
				string text;
				while ((text = r.ReadLine()) != null)
				{
					_logPending.Enqueue(tag + text);
				}
			}
			catch
			{
			}
		};
		Task task = Task.Run(delegate
		{
			pump(proc.StandardOutput);
		});
		Task task2 = Task.Run(delegate
		{
			pump(proc.StandardError);
		});
		Task.WhenAll(task, task2).ContinueWith((Task _) => ((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)onExited, Array.Empty<object>()));
	}

	private void FlushLogs()
	{
		StringBuilder stringBuilder = new StringBuilder();
		if (!_logPending.IsEmpty)
		{
			int num = 0;
			string result;
			while (num < 400 && _logPending.TryDequeue(out result))
			{
				num++;
				string text = result.TrimEnd();
				if (text.StartsWith("[dsh] ", StringComparison.Ordinal))
				{
					string content = text.Substring("[dsh] ".Length);
					if (IsDshNoise(content))
					{
						_dshHiddenCount++;
						continue;
					}
					FlushDshHiddenSummary(stringBuilder);
					if (text.Length > 600)
					{
						text = text.Substring(0, 600) + " ……[已截断]";
					}
				}
				if (text.StartsWith("[llama] ", StringComparison.Ordinal))
				{
					NoteLlamaMemoryLine(text);
				}
				if (!string.IsNullOrWhiteSpace(text) && (!(text == _lastLogLine) || text.StartsWith("[Perf] Error")))
				{
					_lastLogLine = text;
					stringBuilder.AppendLine(text);
					AppendRunLog($"[{DateTime.Now:HH:mm:ss}] {text}");
					ParseTimings(text);
				}
			}
			if (_logPending.IsEmpty)
			{
				FlushDshHiddenSummary(stringBuilder);
			}
		}
		else
		{
			FlushDshHiddenSummary(stringBuilder);
		}
		if (stringBuilder.Length != 0)
		{
			bool follow = ShouldAutoScroll();
			double keepOffset = LogArea.VerticalOffset;
			LogArea.AppendText(stringBuilder.ToString());
			TruncateLogIfNeeded();
			if (follow)
			{
				LogArea.ScrollToEnd();
			}
			else
			{
				// 用户正在向上翻看：保持原滚动位置，不让新日志把视口拉回底部。
				LogArea.ScrollToVerticalOffset(keepOffset);
			}
		}
	}

	/// <summary>日志跟随开关：勾选自动滚动且用户当前没有向上翻看时才滚到底部。</summary>
	private bool ShouldAutoScroll()
	{
		return ChkAutoScroll.IsChecked == true && _logAutoFollow;
	}

	/// <summary>用户向上滚动后暂停自动跟随；滚回底部（或内容不足一屏）时恢复。</summary>
	private void LogArea_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		if (e.ExtentHeight <= e.ViewportHeight)
		{
			_logAutoFollow = true;
			return;
		}
		bool atBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 4.0;
		if (atBottom)
		{
			_logAutoFollow = true;
		}
		else if (e.VerticalChange < 0.0)
		{
			_logAutoFollow = false;
		}
	}

	private void FlushDshHiddenSummary(StringBuilder stringBuilder)
	{
		if (_dshHiddenCount <= 0)
		{
			return;
		}
		string summary = "[dsh] ……(已隐藏 " + _dshHiddenCount + " 行非日志内容)";
		_dshHiddenCount = 0;
		_lastLogLine = summary;
		stringBuilder.AppendLine(summary);
		AppendRunLog($"[{DateTime.Now:HH:mm:ss}] {summary}");
	}

	private static bool IsDshNoise(string content)
	{
		if (string.IsNullOrWhiteSpace(content))
		{
			return false;
		}
		string trimmed = content.Trim();
		if (trimmed.Length == 0)
		{
			return false;
		}
		// 错误/诊断相关行一律保留（堆栈、模块解析错误等），避免隐藏排查信息
		string lowerTrimmed = trimmed.ToLowerInvariant();
		if (trimmed.StartsWith("at ", StringComparison.Ordinal) ||
			trimmed.StartsWith("^", StringComparison.Ordinal) ||
			lowerTrimmed.Contains("node:internal") ||
			lowerTrimmed.Contains("file:///") ||
			lowerTrimmed.Contains("imported from") ||
			lowerTrimmed.Contains("cannot find") ||
			lowerTrimmed.Contains("module") ||
			lowerTrimmed.Contains("require") ||
			lowerTrimmed.Contains("resolve") ||
			lowerTrimmed.Contains("throw"))
		{
			return false;
		}
		// 白名单收敛为强特征: 旧表含 started/ready/server/web/plugin 等宽子串,
		// node 启动横幅几乎全部命中, 隐藏功能形同虚设(2000 行上限很快滚掉关键报错)
		string lower = content.ToLowerInvariant();
		if (lower.Contains("dsh web") ||
			lower.Contains("listening on") ||
			lower.Contains("eaddrinuse") ||
			lower.Contains("127.0.0.1") ||
			lower.Contains("localhost") ||
			trimmed.StartsWith("ready in", StringComparison.Ordinal) ||
			lower.Contains("http://") ||
			lower.Contains("https://"))
		{
			return false;
		}
		return true;
	}

	private void TruncateLogIfNeeded()
	{
		if (LogArea.LineCount <= 2000)
		{
			return;
		}
		try
		{
			int characterIndexFromLineIndex = LogArea.GetCharacterIndexFromLineIndex(300);
			if (characterIndexFromLineIndex > 0)
			{
				LogArea.Text = LogArea.Text.Remove(0, characterIndexFromLineIndex);
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "日志截断失败");
			string[] source = LogArea.Text.Split('\n');
			LogArea.Text = string.Join('\n', source.TakeLast(1700));
		}
	}

	private void AppendRunLog(string line)
	{
		lock (_runLogLock)
		{
			_runLog.AppendLine(line);
			if (++_runLogCount > 5000)
			{
				string text = _runLog.ToString();
				int num = text.IndexOf('\n', text.Length / 2);
				if (num > 0)
				{
					_runLog.Clear();
					_runLog.Append(text.Substring(num + 1));
					_runLogCount = 2500;
				}
			}
		}
	}

	private void LogError(Exception ex, string context)
	{
		if (ex == null)
		{
			return;
		}
		string msg = $"{context}: {ex.GetType().Name}: {ex.Message}";
		string text = (string.IsNullOrWhiteSpace(ex.StackTrace) ? "" : ("    " + string.Join("    ", from x in ex.StackTrace.Split('\n').Take(3)
			select x.TrimEnd())));
		string text2 = msg + ((text.Length == 0) ? "" : ("\n" + text));
		AppLog.WriteError(text2);
		AppendRunLog($"[{DateTime.Now:HH:mm:ss}] [ERROR] {text2}");
		try
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				Log("[ERROR] " + msg);
			}, Array.Empty<object>());
		}
		catch
		{
		}
	}

	private void FlushRunLog()
	{
		try
		{
			lock (_runLogLock)
			{
				AppLog.WriteRun(_runLog.ToString());
				_runLog.Clear();
				_runLogCount = 0;
			}
		}
		catch
		{
		}
	}

	/// <summary>
	/// 把"当前 Node 日志 + 最近错误/崩溃日志尾部 + 环境信息"一次性复制到剪贴板。
	/// 剪贴板可能被其它进程短暂占用，这里重试 3 次而不是直接失败。
	/// </summary>
	private void CopyDiagInfo_Click(object sender, RoutedEventArgs e)
	{
		string text = AppLog.BuildDiagnostics(NodeLog.Text);
		Exception? last = null;
		for (int attempt = 0; attempt < 3; attempt++)
		{
			try
			{
				System.Windows.Clipboard.SetText(text);
				NodeLogLine("📋 诊断信息已复制到剪贴板（" + text.Length + " 字符），可直接粘贴给维护者。");
				return;
			}
			catch (Exception ex)
			{
				last = ex;
				System.Threading.Thread.Sleep(120);
			}
		}
		NodeLogLine("复制到剪贴板失败：" + (last == null ? "未知错误" : last.Message) + "。可点【日志】按钮打开日志目录手动复制。");
	}

	/// <summary>非模态提示条：不阻塞调用流（finally 能立刻执行），到期自动隐藏。</summary>
	private void ShowNotice(string message, bool isError = false)
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke((Action)(() => ShowNotice(message, isError)));
			return;
		}
		NoticeText.Text = message;
		NoticeBar.Background = new SolidColorBrush(isError
			? System.Windows.Media.Color.FromRgb(0x4a, 0x1f, 0x1f)
			: System.Windows.Media.Color.FromRgb(0x1f, 0x29, 0x37));
		NoticeBar.BorderBrush = new SolidColorBrush(isError
			? System.Windows.Media.Color.FromRgb(0x8b, 0x2d, 0x2d)
			: System.Windows.Media.Color.FromRgb(0x3d, 0x44, 0x4d));
		NoticeBar.Visibility = Visibility.Visible;
		if (_noticeTimer == null)
		{
			_noticeTimer = new System.Windows.Threading.DispatcherTimer();
			_noticeTimer.Tick += (_, _) =>
			{
				_noticeTimer?.Stop();
				NoticeBar.Visibility = Visibility.Collapsed;
			};
		}
		_noticeTimer.Stop();
		_noticeTimer.Interval = TimeSpan.FromSeconds(isError ? 12 : 8);
		_noticeTimer.Start();
	}

	private void CloseNotice_Click(object s, RoutedEventArgs e)
	{
		_noticeTimer?.Stop();
		NoticeBar.Visibility = Visibility.Collapsed;
	}

	private void OpenLogDir_Click(object s, RoutedEventArgs e)
	{
		try
		{
			Directory.CreateDirectory(AppLog.LogDir);
			Process.Start(new ProcessStartInfo("explorer.exe", AppLog.LogDir)
			{
				UseShellExecute = true
			});
			Log("打开日志目录:" + AppLog.LogDir);
		}
		catch (Exception ex)
		{
			LogError(ex, "打开日志目录失败");
		}
	}

	private void Log(string text)
	{
		// 后台线程一律转发 UI 线程: LogArea 是 WPF 依赖对象, 跨线程直摸会抛
		// "调用线程无法访问此对象"——PullDsh 等后台回调传进来的 Log 曾因此
		// 把成功的 git fetch 判成失败
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke((Action)(() => Log(text)));
			return;
		}
		if (!string.IsNullOrWhiteSpace(text) && (!(text == _lastLogLine) || text.StartsWith("[Perf] Error")))
		{
			_lastLogLine = text;
			bool follow = ShouldAutoScroll();
			double keepOffset = LogArea.VerticalOffset;
			LogArea.AppendText(text + Environment.NewLine);
			AppendRunLog($"[{DateTime.Now:HH:mm:ss}] {text}");
			TruncateLogIfNeeded();
			if (follow)
			{
				LogArea.ScrollToEnd();
			}
			else
			{
				LogArea.ScrollToVerticalOffset(keepOffset);
			}
		}
	}

	private void ClearLog_Click(object s, RoutedEventArgs e)
	{
		LogArea.Clear();
	}

	private void ParseTimings(string text)
	{
		// 日志状态机接管全部日志解析(相位/速度/进度/任务汇总/启动块)
		_llamaApi?.FeedLogLine(text);
	}

	private static string FT(double t)
	{
		t = Math.Max(0.0, t);
		if (!(t >= 10.0))
		{
			return (Math.Round(t * 10.0) / 10.0).ToString("F1");
		}
		return Math.Round(t).ToString();
	}

	private void StartPerfMonitor()
	{
		if (_perfCts != null)
		{
			return;
		}
		_perfCts = new CancellationTokenSource();
		_perfThread = new Thread((ThreadStart)delegate
		{
			try
			{
				_cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
				_cpuCounter.NextValue();
			}
			catch (Exception ex)
			{
				LogError(ex, "CPU 性能计数器初始化失败");
			}
			try
			{
				_diskReadCounter = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", "_Total");
				_diskReadCounter.NextValue();
				_diskWriteCounter = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", "_Total");
				_diskWriteCounter.NextValue();
			}
			catch (Exception ex2b)
			{
				LogError(ex2b, "磁盘计数器初始化失败");
			}
			try
			{
				using (System.Management.ManagementObjectSearcher mos = new System.Management.ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
				{
					foreach (System.Management.ManagementObject o in mos.Get())
					{
						using (o)
						{
							string nm = (o["Name"]?.ToString() ?? "").Trim();
							if (!string.IsNullOrEmpty(nm))
							{
								_cpuName = $"{nm} · {o["NumberOfCores"]} 核 {o["NumberOfLogicalProcessors"]} 线程";
							}
							break;
						}
					}
				}
			}
			catch (Exception exCpu)
			{
				LogError(exCpu, "CPU 型号采集失败");
			}
			try
			{
				_ramAvailCounter = new PerformanceCounter("Memory", "Available MBytes");
				_ramAvailCounter.NextValue();
				using (System.Management.ManagementObjectSearcher mos = new System.Management.ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem"))
				{
					foreach (System.Management.ManagementObject o in mos.Get())
					{
						using (o)
						{
							_ramTotalBytes = Convert.ToDouble(o["TotalVisibleMemorySize"]) * 1024.0;
							break;
						}
					}
				}
			}
			catch (Exception ex2c)
			{
				LogError(ex2c, "物理内存统计初始化失败");
			}
			try
			{
				var cat = new PerformanceCounterCategory("Processor");
				var inst = cat.GetInstanceNames().Where(x => x != "_Total").OrderBy(x => int.TryParse(x, out var n) ? n : int.MaxValue).ToArray();
				_coreCounters = inst.Select(x => new PerformanceCounter("Processor", "% Processor Time", x)).ToArray();
				foreach (var c in _coreCounters)
				{
					c.NextValue();
				}
			}
			catch (Exception exCore)
			{
				LogError(exCore, "每核 CPU 计数器初始化失败");
			}
			try
			{
				var catNet = new PerformanceCounterCategory("Network Interface");
				_netCounters = catNet.GetInstanceNames()
					.Select(name => (name, rx: new PerformanceCounter("Network Interface", "Bytes Received/sec", name), tx: new PerformanceCounter("Network Interface", "Bytes Sent/sec", name)))
					.ToList();
				foreach (var (_, rx, tx) in _netCounters)
				{
					rx.NextValue();
					tx.NextValue();
				}
			}
			catch (Exception exNet)
			{
				LogError(exNet, "网卡计数器初始化失败");
			}
			_llamaApi = new LlamaApiMonitor(() =>
			{
				try
				{
					return GetLlamaPort();
				}
				catch
				{
					return 0;
				}
			});
			// 捕获局部引用: StopPerf 的 Join(3s) 超时后把 _perfCts 置 null, 而本线程
			// 可能还在 Sleep(监测页隐藏时最长 30s) —— 循环条件重读共享字段会 NRE 崩进程
			var perfCts = _perfCts;
			// 重采样的到期时刻(Environment.TickCount64, 无回绕): 0 = 立即可拍(首拍/被打断/档位切换)
			long heavyDueAt = 0;
			// 3s 熔断探针可用性: NVML 进程内取空 → 停用(nvidia-smi 绝不在轻量下 3s 一轮),
			// 熔断退回重拍节奏; 60s 后重试, NVML 晚装好也能恢复
			bool probeOk = true;
			long probeRetryAt = 0;
			bool prevIdle = false;
			while (perfCts != null && !perfCts.IsCancellationRequested)
			{
				// 档位计算(对齐 Linux 侧 core.Monitor._cadence/_watched):
				//   llama 存活判定 = 进程对象 或 API 在线(外部/接管实例, 与熔断轮询一致)
				//   轻量化(_trayMode 自动 或 托盘菜单手动)且无人看监测页 → 低频档; 否则正常档
				bool llamaNow = IsAlive(_llamaProc) || (_llamaApi != null && _llamaApi.Online);
				bool busyNow = llamaNow || DshAliveForPerf();
				bool watchedNow = !_trayMode && _monitorTabVisible;
				bool lightNow = _lightOn && !watchedNow;
				bool lightIdle = lightNow && !busyNow;
				// 全空闲轻量化驻留: 进入时把重拍推到 60s 后, 期间零采样(后台几乎零占用,
				// Linux light 分支同款语义); 一旦有服务出现, 退出时立即补拍
				if (lightIdle != prevIdle)
				{
					heavyDueAt = lightIdle ? Environment.TickCount64 + 60000 : 0;
					prevIdle = lightIdle;
				}
				if (!lightIdle && Environment.TickCount64 >= heavyDueAt)
				{
					try
					{
						HeavySample(llamaNow);
					}
					catch (Exception ex5)
					{
						LogError(ex5, "性能监控异常");
					}
					// 重拍节奏(对齐 Linux 侧 core.Monitor._cadence):
					//   轻量化 30s 一拍; 正常: 服务存活或监测页可见 3s, 否则 30s(Strata #4066)
					//   熔断不依赖重拍: 下面另有 3s 探针, 轻量下响应速度不降级
					int heavyGap;
					double cacheTtl;
					if (lightNow)
					{
						heavyGap = 30000;
						cacheTtl = 28.0;
					}
					else if (busyNow || watchedNow)
					{
						heavyGap = 3000;
						cacheTtl = 2.5;
					}
					else
					{
						heavyGap = 30000;
						cacheTtl = 24.0;
					}
					_gpuMonitor.CacheTtlSeconds = cacheTtl;
					heavyDueAt = Environment.TickCount64 + heavyGap;
				}
				else if (lightNow && llamaNow && _brkEnabledCache && (probeOk || Environment.TickCount64 >= probeRetryAt))
				{
					// 轻量化下的 3s 熔断轻探针: 只 NVML 进程内读显存(毫秒级、零子进程、零分配压力),
					// 其余采样保持低频。阈值走 UI 同步缓存; 命中只是门控, 最终判定由 UI 线程
					// CheckVramBreaker 读真实控件复核(配置刚改也不会误杀)
					try
					{
						List<GpuDevInfo> probe = NvmlMonitor.SamplePerDevice();
						if (probe.Count == 0)
						{
							probeOk = false;
							probeRetryAt = Environment.TickCount64 + 60000;
						}
						else
						{
							probeOk = true;
							if (!_vramBreakerTriggered)
							{
								double usedMb = 0.0;
								double totalMb = 0.0;
								foreach (GpuDevInfo g in probe)
								{
									usedMb += g.MemUsedMb;
									totalMb += g.MemTotalMb;
								}
								double thr = BitConverter.Int64BitsToDouble(_brkThrBits);
								bool hit = _brkTypeGbCache
									? usedMb / 1024.0 >= thr
									: totalMb > 0.0 && usedMb / totalMb * 100.0 >= thr;
								if (hit)
								{
									List<GpuDevInfo> probeSnap = probe;
									((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
									{
										CheckVramBreaker(probeSnap);
									}, Array.Empty<object>());
								}
							}
						}
					}
					catch (Exception exBrk)
					{
						LogError(exBrk, "熔断探针异常");
					}
				}
				if (lightNow)
				{
					SyncBreakerProbeCache();
				}
				// 打盹: 轻量档 3s(熔断探针节奏), 正常档 ≤5s(Linux _sleep_until_next 同理);
				// 且不睡过重拍到期时刻
				int nap = (int)Math.Min(Math.Max(heavyDueAt - Environment.TickCount64, 1), lightNow ? 3000 : 5000);
				if (SleepChunked(nap, lightNow))
				{
					_perfPulse = false;
					heavyDueAt = 0; // 提前醒(档位变了/有人开页/要求补拍): 立刻重算并重采一拍
				}
			}
		})
		{
			IsBackground = true
		};
		_perfThread.Start();
	}

	/// <summary>完整采样一拍(perf 线程): CPU/磁盘/内存/分核/网卡/GPU + llama API + BeginInvoke OnPerf。
	/// 轻量化模式只每 30s 走一次; 显存熔断不依赖这拍(perf 循环里有独立 3s 探针)。</summary>
	private void HeavySample(bool llamaNow)
	{
		Dictionary<string, string> d = new Dictionary<string, string>();
		try
		{
			if (_cpuCounter != null)
			{
				d["cpu"] = _cpuCounter.NextValue().ToString("F1");
			}
		}
		catch (Exception ex3)
		{
			d["cpu_err"] = ex3.Message;
		}
		try
		{
			if (_diskReadCounter != null)
			{
				d["disk_read"] = _diskReadCounter.NextValue().ToString("F0", CultureInfo.InvariantCulture);
			}
			if (_diskWriteCounter != null)
			{
				d["disk_write"] = _diskWriteCounter.NextValue().ToString("F0", CultureInfo.InvariantCulture);
			}
		}
		catch (Exception ex4b)
		{
			d["disk_err"] = ex4b.Message;
		}
		try
		{
			if (_ramAvailCounter != null && _ramTotalBytes > 0.0)
			{
				double availBytes = _ramAvailCounter.NextValue() * 1048576.0;
				d["ram_used"] = Math.Max(0.0, _ramTotalBytes - availBytes).ToString("F0", CultureInfo.InvariantCulture);
				d["ram_total"] = _ramTotalBytes.ToString("F0", CultureInfo.InvariantCulture);
			}
		}
		catch (Exception ex4c)
		{
			d["ram_err"] = ex4c.Message;
		}
		// llama-server /slots + /props 采集: 存活即轮询(API 在线兜底接管实例)
		try
		{
			if (llamaNow)
			{
				_llamaApi?.PollFast();
				_llamaApi?.PollSlowIfNeeded();
			}
		}
		catch (Exception exLp)
		{
			LogError(exLp, "llama /slots 采集异常");
		}
		try
		{
			if (_coreCounters != null && _coreCounters.Length > 0)
			{
				_corePcts = _coreCounters.Select(c => Math.Max(0.0, Math.Min(100.0, c.NextValue()))).ToArray();
			}
		}
		catch
		{
		}
		try
		{
			var rows = new List<(string, double, double)>();
			double rxSum = 0;
			foreach (var (name, rx, tx) in _netCounters)
			{
				double r = Math.Max(0.0, rx.NextValue()) / 1048576.0;
				double t = Math.Max(0.0, tx.NextValue()) / 1048576.0;
				rows.Add((name, r, t));
				rxSum += r;
			}
			_netSamples = rows;
			_netRxMbs = rxSum;
		}
		catch
		{
		}
		// 每重拍查询(NVML 为进程内直采, 开销极低): 2 倍频会让温度/功率卡在半数拍上显示无数据
		foreach (KeyValuePair<string, string> item in _gpuMonitor.QueryGpu())
		{
			d[item.Key] = item.Value;
		}
		// NVML/回退采样含进程外调用, 留在 perf 线程; UI 线程只做渲染
		var gpusNow = NvmlMonitor.SamplePerDevice();
		if (gpusNow.Count == 0)
		{
			gpusNow = NvmlMonitor.SampleViaNvidiaSmi();
		}
		List<GpuDevInfo> gpusSnapshot = gpusNow;
		Dictionary<string, string> dSnap = d;
		_lastPerfDict = dSnap;
		_lastPerfGpus = gpusSnapshot;
		_perfSampledAtUtc = DateTime.UtcNow;
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			OnPerf(dSnap, gpusSnapshot);
		}, Array.Empty<object>());
	}

	private void StopPerf()
	{
		_perfCts?.Cancel();
		_perfThread?.Join(3000);
		_perfCts = null;
		_perfThread = null;
		_gpuMonitor.StopGpuTempLoop();
		try
		{
			_cpuCounter?.Dispose();
		}
		catch (Exception ex)
		{
			LogError(ex, "释放 CPU 计数器失败");
		}
		_cpuCounter = null;
	}

	/// <summary>
	/// 分块休眠(Linux 侧 core.Monitor._sleep_until_next 的等价实现): 每 ≤5s 醒一次复查——
	/// 窗口恢复/手动关轻量化/打开监测页/要求补拍, 都不必等完当前 30~60s 的长休眠。
	/// 返回 true = 提前醒(调用方应立即重算档位并补拍); 3s 档下等价于一次 Thread.Sleep。
	/// </summary>
	private bool SleepChunked(int sleepMs, bool light)
	{
		int slept = 0;
		while (slept < sleepMs)
		{
			int chunk = Math.Min(sleepMs - slept, 5000);
			Thread.Sleep(chunk);
			slept += chunk;
			if (_perfPulse)
			{
				return true;
			}
			if (_perfCts != null && _perfCts.IsCancellationRequested)
			{
				return true;
			}
			if (light && !_lightOn)
			{
				return true;
			}
			if (!_trayMode && _monitorTabVisible)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>同步熔断探针配置缓存: UI 线程直读控件; perf 线程调用时 BeginInvoke 异步直读。</summary>
	private void SyncBreakerProbeCache()
	{
		if (!((DispatcherObject)this).Dispatcher.CheckAccess())
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(SyncBreakerProbeCache), Array.Empty<object>());
			return;
		}
		_brkEnabledCache = ChkVramBreaker?.IsChecked == true;
		_brkTypeGbCache = ComboVramBreakerType?.SelectedIndex == 1;
		double thr;
		if (!double.TryParse(TxtVramBreakerVal?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out thr))
		{
			thr = double.MaxValue;
		}
		_brkThrBits = BitConverter.DoubleToInt64Bits(thr);
	}

	/// <summary>perf 线程安全读取 DSH 存活(对齐 Linux busy 判定里的 dsh.running)。
	/// _dshProc/_dshTracker 由 UI 线程改写, 这里捕获局部引用 + try/catch, 不碰控件。</summary>
	private bool DshAliveForPerf()
	{
		try
		{
			Process dp = _dshProc;
			if (dp != null && IsAlive(dp))
			{
				return true;
			}
			ProcessTracker tr = _dshTracker;
			return tr != null && tr.Poll() == null;
		}
		catch
		{
			return false;
		}
	}

	// 配置端口缓存: 采集线程读, 只在 UI 线程写(GetLlamaPort 直接读控件会跨线程异常,
	// 被 _portGetter 吞掉后配置端口永远到不了采集器——模型信息/生成速度全灭的根因)
	private volatile int _cachedLlamaPort;

	private int GetLlamaPort()
	{
		// UI 线程调用时刷新缓存; 采集线程只读缓存值
		if (Dispatcher.CheckAccess())
		{
			CacheLlamaPort();
		}
		// 1) 参数面板缓存的配置端口
		if (_cachedLlamaPort > 0)
		{
			return _cachedLlamaPort;
		}
		// 2) 命令行预览(含自定义参数): --port N 与 --port=N
		Match match = Regex.Match(_lastCmd ?? "", "--port[= ](\\d+)");
		if (match.Success)
		{
			return int.Parse(match.Groups[1].Value);
		}
		// 3) 采集端自动发现命中的端口
		if (_llamaApi != null && _llamaApi.ActivePort > 0)
		{
			return _llamaApi.ActivePort;
		}
		// 4) 未知 → 0, 由采集端自动发现兜底
		return 0;
	}

	/// <summary>读参数面板 port 控件值缓存下来。仅 UI 线程调用。</summary>
	private void CacheLlamaPort()
	{
		try
		{
			if (_pw.TryGetValue("port", out (System.Windows.Controls.CheckBox, System.Windows.Controls.Control) portPair)
				&& portPair.Item1.IsChecked == true)
			{
				// 勾选启用时控件值才可信(禁用时 BuildCmd 不发射 --port, 默认值 1234 是幌子)
				string ctlText = portPair.Item2 is System.Windows.Controls.ComboBox pcb ? pcb.Text
					: portPair.Item2 is System.Windows.Controls.TextBox ptb ? ptb.Text : "";
				if (int.TryParse((ctlText ?? "").Trim(), out int cfgPort) && cfgPort > 0 && cfgPort < 65536)
				{
					_cachedLlamaPort = cfgPort;
					return;
				}
			}
			_cachedLlamaPort = 0;
		}
		catch
		{
		}
	}

	// ── 监测标签页(Strata Monitor 复刻): 60 秒历史 + 图表/状态卡/Gauge/请求表 ──
	// 监测页是否可见(Strata #4066: 可见 3s / 不可见 30s 采样)
	private volatile bool _monitorTabVisible = true;

	// 托盘轻量模式: 最小化到托盘时置位(最小化到任务栏同样生效), 恢复窗口清除。
	// perf 线程读(volatile): OnPerf 走轻量路径(只推历史与熔断)
	private volatile bool _trayMode;

	// 轻量化开关(对齐 Linux 侧 core.Monitor.lightweight): 隐藏/最小化窗口自动开,
	// 托盘菜单"轻量化模式(后台低频保活)"可手动开/关; perf 线程读(volatile)。
	// 开且无人看监测页: 有服务存活 30s 一拍(历史/熔断仍生效), 全空闲 60s 纯休眠零采样。
	private volatile bool _lightOn;

	// 手动切换/恢复窗口/打开监测页时置位: perf 线程最长 60s 的休眠被立即打断补拍
	// (Linux 侧 core.Monitor._sleep_until_next ≤5s 复查唤醒的等价实现)
	private volatile bool _perfPulse;

	// 熔断配置缓存(UI 线程写, perf 线程 3s 探针读): 轻量化下探针不碰控件;
	// 最终判定仍由 UI 线程 CheckVramBreaker 读真实控件, 缓存只做探针门控
	private volatile bool _brkEnabledCache;
	private volatile bool _brkTypeGbCache;
	private long _brkThrBits = BitConverter.DoubleToInt64Bits(double.MaxValue);

	// 托盘菜单里的"轻量化模式"复选项(自动置位与手动勾选同步显示)
	private System.Windows.Forms.ToolStripMenuItem? _trayLightItem;

	private void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
	{
		_monitorTabVisible = MainTabs?.SelectedIndex == 0;
		// 切回监测页立即用最近一拍数据重绘, 不等下一个采样周期(不可见时节奏最长达 30s)
		if (_monitorTabVisible && _lastPerfDict != null)
		{
			OnPerf(_lastPerfDict, _lastPerfGpus ?? new List<GpuDevInfo>());
		}
		// 同时打断 perf 线程的长休眠(空闲/轻量档最长 30~60s), 立刻采一拍新数据
		if (_monitorTabVisible)
		{
			_perfPulse = true;
		}
	}

	// 指标历史(Strata: 60 点窗口 + 时间戳, 传感器缺口用 null 分段)
	private sealed class Hist
	{
		public readonly List<double?> Values = new();
		public readonly List<DateTime> Ticks = new();

		public void Push(double? v)
		{
			Values.Add(v);
			Ticks.Add(DateTime.UtcNow);
			if (Values.Count > 60)
			{
				Values.RemoveAt(0);
				Ticks.RemoveAt(0);
			}
		}

		public double?[] Arr()
		{
			return Values.ToArray();
		}

		public DateTime[] Stamps()
		{
			return Ticks.ToArray();
		}

		public double Last()
		{
			for (int i = Values.Count - 1; i >= 0; i--)
			{
				if (Values[i].HasValue)
				{
					return Values[i]!.Value;
				}
			}
			return double.NaN;
		}

		public void Clear()
		{
			Values.Clear();
			Ticks.Clear();
		}
	}

	private readonly Hist _hCpu = new();
	private readonly Hist _hRam = new();
	private readonly Hist _hTemp = new();
	private readonly Hist _hPower = new();
	private readonly Hist _hTps = new();
	private readonly Hist _hPf = new();
	private readonly Hist _hCtx = new();
	private readonly Hist _hMtp = new();
	private readonly Hist _hGpuUtil = new();
	private readonly Hist _hGpuMem = new();
	private readonly Hist _hNet = new();
	private readonly Hist _hNetTx = new();

	// 每核 CPU 与网卡采样(perf 线程写, UI 读)
	private PerformanceCounter[]? _coreCounters;
	private double[] _corePcts = Array.Empty<double>();
	private List<(string name, double rx, double tx)> _netSamples = new();
	private double _netRxMbs;

	// Strata dark tokens: accent/info/warn/danger
	private static readonly System.Windows.Media.Color ToneAccent = System.Windows.Media.Color.FromRgb(0x34, 0xd3, 0x99);
	private static readonly System.Windows.Media.Color ToneAccent2 = System.Windows.Media.Color.FromRgb(0x24, 0x94, 0x6b);
	private static readonly System.Windows.Media.Color ToneInfo = System.Windows.Media.Color.FromRgb(0x3a, 0xa9, 0xff);
	private static readonly System.Windows.Media.Color ToneWarn = System.Windows.Media.Color.FromRgb(0xf5, 0xb5, 0x3d);

	// 副行信息: GPU 数量/名称, CPU 型号拓扑(性能线程初始化时采集)
	private string _cpuName = "处理器";

	// llama-server HTTP 采集: /slots 差分 + /props 模型信息 + 事件流
	private LlamaApiMonitor? _llamaApi;

	// 扩展采集: 磁盘读写/物理内存/能耗积分(GPU 功率 × 采样间隔)
	private PerformanceCounter? _diskReadCounter;
	private PerformanceCounter? _diskWriteCounter;
	private PerformanceCounter? _ramAvailCounter;
	private double _ramTotalBytes;
	private List<(string name, PerformanceCounter rx, PerformanceCounter tx)> _netCounters = new();


	// 追加采样点并重绘 spark(与 SparkChart 同规格)
	private void PushChart(SparkChart c, Hist h, double? v, double max, string unit, System.Windows.Media.Color tone)
	{
		h.Push(v);
		c.SetSeries(h.Arr(), null, max, unit, h.Stamps(), tone, ToneAccent2, "", "");
	}

	// 数据来源徽标: 有效="实测", 失效="无数据"; src 非空时显示来源
	private static void SetBadge(System.Windows.Controls.TextBlock b, bool ok, string src = "实测")
	{
		b.Text = ok ? src : "无数据";
		b.Background = new SolidColorBrush(ok ? System.Windows.Media.Color.FromArgb(0x1f, 0x34, 0xd3, 0x99) : System.Windows.Media.Color.FromArgb(0x22, 0x8b, 0x94, 0x9e));
		b.Foreground = new SolidColorBrush(ok ? System.Windows.Media.Color.FromRgb(0x4a, 0xde, 0x80) : System.Windows.Media.Color.FromRgb(0x8b, 0x94, 0x9e));
	}

	// Llama 状态卡(全字段)
	private void UpdatePhaseCard()
	{
		var api = _llamaApi;
		bool llamaUp = IsAlive(_llamaProc);
		bool online = api != null && api.Online && llamaUp;
		string phase = api?.Phase ?? "idle";
		var slots = api?.SlotsSnapshot() ?? new List<LlamaApiMonitor.SlotInfo>();
		var act = slots.FirstOrDefault(x => x.Processing);

		// ctx 合并(slots API 优先, release/启动块兜底)
		long ctxUsed = api != null && api.CtxUsed >= 0 ? api.CtxUsed : api?.LogCtxUsed ?? -1;
		long ctxTotal = api?.CtxTotal ?? 0;
		if (ctxTotal <= 0)
		{
			long bt = api?.Boot?.NCtxSlot ?? 0;
			if (bt > 0)
			{
				ctxTotal = bt;
			}
		}
		string ctxText = ctxUsed >= 0 && ctxTotal > 0 ? $"{FmtTok(ctxUsed)} / {FmtTok(ctxTotal)}" : "—";
		string ctxPct = ctxUsed >= 0 && ctxTotal > 0 ? $" ({ctxUsed * 100.0 / ctxTotal:F0}%)" : "";

		// 相位文案与配色(decoding=cyan, prompt=green, 离线=danger)
		string phaseText;
		System.Windows.Media.Color pillBg;
		System.Windows.Media.Color pillFg;
		if (!llamaUp)
		{
			phaseText = "llama 离线";
			pillBg = System.Windows.Media.Color.FromArgb(0x2e, 0xff, 0x5a, 0x6e);
			pillFg = System.Windows.Media.Color.FromRgb(0xff, 0x8a, 0x98);
		}
		else if (!online)
		{
			phaseText = "未知（API）";
			pillBg = System.Windows.Media.Color.FromArgb(0x2e, 0xf5, 0xb5, 0x3d);
			pillFg = System.Windows.Media.Color.FromRgb(0xf8, 0xc9, 0x6a);
		}
		else if (phase == "decoding")
		{
			phaseText = "生成中";
			pillBg = System.Windows.Media.Color.FromArgb(0x2e, 0x7c, 0xd7, 0xf7);
			pillFg = System.Windows.Media.Color.FromRgb(0x7c, 0xd7, 0xf7);
		}
		else if (phase == "prompt_processing")
		{
			phaseText = "Prompt 处理中";
			pillBg = System.Windows.Media.Color.FromArgb(0x2e, 0x34, 0xd3, 0x99);
			pillFg = System.Windows.Media.Color.FromRgb(0x4a, 0xde, 0x80);
		}
		else
		{
			phaseText = "空闲";
			pillBg = System.Windows.Media.Color.FromArgb(0x1a, 0x8e, 0x97, 0x9f);
			pillFg = System.Windows.Media.Color.FromRgb(0x8e, 0x97, 0x9f);
		}
		if (_vramBreakerTriggered)
		{
			phaseText = "已熔断";
			pillBg = System.Windows.Media.Color.FromArgb(0x2e, 0xff, 0x5a, 0x6e);
			pillFg = System.Windows.Media.Color.FromRgb(0xff, 0x8a, 0x98);
		}
		MonPhaseText.Text = phaseText;
		MonPhaseText.Foreground = new SolidColorBrush(pillFg);
		MonPhasePill.Background = new SolidColorBrush(pillBg);
		MonPhasePill.BorderBrush = new SolidColorBrush(pillBg);

		// 模型名: /props 别名/文件名 > 启动行 loading model > 未加载
		string modelName = api?.Model?.Name;
		if (string.IsNullOrEmpty(modelName) && api != null && !string.IsNullOrEmpty(api.Boot?.Model))
		{
			modelName = api.Boot.Model[(api.Boot.Model.LastIndexOf('/') + 1)..].Replace('\\', '/');
		}
		MonModelName.Text = string.IsNullOrEmpty(modelName) ? (llamaUp ? "已连接 · 模型未报告" : "未加载模型") : modelName;

		// KV 缓存命中: 活动槽 cached/prompt
		string cacheHitText = "";
		if (act != null && act.NPromptTokens > 0 && act.NPromptCache > 0)
		{
			cacheHitText = $" · 缓存命中 {Math.Min(100.0, act.NPromptCache * 100.0 / act.NPromptTokens):F1}%";
		}

		// 任务已运行(started_at 起算)
		string elapsedText = "";
		if (online && phase != "idle" && api?.TaskStartedAt is DateTime t0)
		{
			double el = (DateTime.Now - t0).TotalSeconds;
			if (el >= 0)
			{
				elapsedText = $"已运行 {FmtEta(el)}";
			}
		}

		// n_remain>0 表示设了上限; -1/0 = 不限
		bool hasLimit = act != null && act.NRemain > 0;
		string remainText = hasLimit ? FmtTok(act.NRemain) : "不限";
		double barPct = -1.0;
		string leftText;
		string rightText;
		if (online && phase == "prompt_processing")
		{
			// 预填充: 进度/速度/耗时 + ETA
			double prog = api?.PromptProgress ?? 0.0;
			long total = api?.PromptTotalTokens ?? 0;
			double spd = api?.LogPromptSpeed ?? 0.0;
			double processed = act != null && act.NPromptProcessed > 0 ? act.NPromptProcessed : total * prog;
			Stat1Label.Text = "已处理 Prompt";
			Stat1Val.Text = processed > 0 ? FmtTok(processed) : "—";
			Stat1Unit.Text = "tokens";
			Stat2Label.Text = "Prompt 速度";
			Stat2Val.Text = spd > 0 ? FT(spd) : "—";
			Stat3Label.Text = "已耗时";
			double elapsed = api?.PromptElapsedS ?? 0.0;
			Stat3Val.Text = elapsed > 0 ? elapsed.ToString("F1") : "—";
			Stat3Unit.Text = "s";
			barPct = prog * 100.0;
			leftText = api?.LogTaskId is long pt ? $"Prompt 处理中 · 任务 #{pt}" : "Prompt 处理中";
			double eta = spd > 0 && total > 0 ? Math.Max(0.0, (1.0 - prog) * total / spd) : -1.0;
			rightText = $"prompt {FmtTok(total)} tokens · 上下文 {ctxText}{ctxPct}" + (eta > 0 ? $" · 预计剩余 {FmtEta(eta)}" : "") + cacheHitText;
			SetMonBar(true, ToneInfo, barPct);
		}
		else if (online && phase == "decoding")
		{
			// 生成: 已解码/实时速度(tg_3s)/任务均速(tg) + 进度与 ETA(仅设了 n_remain 上限时)
			Stat1Label.Text = "已解码";
			Stat1Val.Text = FmtTok(api.LogNDecoded);
			Stat1Unit.Text = "tokens";
			Stat2Label.Text = "实时速度 (3s)";
			Stat2Val.Text = api.Tg3sTps is double t3 ? FT(t3) : "—";
			Stat3Label.Text = "任务均速";
			Stat3Val.Text = api.TgTps is double tt ? FT(tt) : "—";
			Stat3Unit.Text = "t/s";
			if (hasLimit && api.LogNDecoded > 0)
			{
				barPct = Math.Min(100.0, api.LogNDecoded * 100.0 / (api.LogNDecoded + act.NRemain));
			}
			leftText = api?.LogTaskId is long dt ? $"生成中 · 任务 #{dt}" + (api.IsChild == true ? " · 子任务" : "") : "生成中";
			double eta = hasLimit && api.Tg3sTps is double t32 && t32 > 0 ? act.NRemain / t32 : -1.0;
			rightText = $"剩余 {remainText} tokens · 上下文 {ctxText}{ctxPct}" + (eta > 0 ? $" · 预计完成 {FmtEta(eta)}" : "") + cacheHitText;
			SetMonBar(true, ToneInfo, barPct >= 0 ? barPct : null);
		}
		else
		{
			// 空闲/离线/熔断: idle-note + 上次任务
			var lt = api?.LastTask;
			Stat1Label.Text = "上次输出";
			Stat1Val.Text = (lt?.TotalTokens ?? lt?.DecodedTokens) is long tok && tok > 0 ? FmtTok(tok) : "—";
			Stat1Unit.Text = "tokens";
			Stat2Label.Text = "任务均速";
			Stat2Val.Text = (lt?.GenSpeed ?? lt?.AvgTps) is double gs && gs > 0 ? FT(gs) : "—";
			Stat3Label.Text = "上下文占用";
			Stat3Val.Text = ctxText;
			Stat3Unit.Text = ctxPct;
			SetMonBar(false, ToneAccent, 0.0);
			leftText = _vramBreakerTriggered ? "显存熔断已触发，llama 已停止" : (llamaUp ? "空闲 · 等待新任务" : "llama 未启动");
			rightText = "";
			if (lt != null)
			{
				var parts = new List<string>();
				if ((lt.TotalTokens ?? lt.DecodedTokens) is long ltk && ltk > 0)
				{
					parts.Add($"{FmtTok(ltk)} tokens");
				}
				if (lt.Duration > 0)
				{
					parts.Add($"{lt.Duration:F1}s");
				}
				if ((lt.GenSpeed ?? lt.AvgTps) is double la && la > 0)
				{
					parts.Add($"均速 {FT(la)} t/s");
				}
				if (lt.PromptSpeed is double lps && lps > 0)
				{
					parts.Add($"预填充 {FT(lps)} t/s");
				}
				if (lt.Truncated)
				{
					parts.Add("已截断");
				}
				if (parts.Count > 0)
				{
					rightText = $"上次 #{lt.TaskId}: " + string.Join(" · ", parts);
				}
			}
			if (ctxUsed >= 0 && ctxTotal > 0)
			{
				rightText += (rightText.Length > 0 ? " · " : "") + $"上下文 {ctxText}{ctxPct}";
			}
		}
		if (elapsedText.Length > 0)
		{
			rightText += (rightText.Length > 0 ? " · " : "") + elapsedText;
		}
		MonPhaseLabel.Text = leftText;
		MonPhaseLabel.ToolTip = null;
		MonPhaseDetail.Text = rightText;

		UpdateModelInfoCard(api);
		UpdateEventFeed(api);
	}

	private bool _synthGpuLogged;
	// llama 更新/变体安装互斥: 后台检查、按钮检查、变体窗口三个入口共用
	private bool _llamaUpdBusy;
	// 真退出标志: Shutdown 会再次触发 Window_Closing, 没有它托盘分支会重新 Hide 窗口造成僵尸进程
	private bool _exiting;

	// llama.cpp 变体(分支): 名称/仓库/目录, 随方案配置持久化
	private List<Models.LlamaVariant> _llamaVariants = new();
	private string _activeVariant = "";
	private bool _variantGuard;

	// WMI 显卡友好名(一次性缓存): "AMD Radeon RX 7900 XTX"
	private string? _gpuFriendlyName;

	private string GpuFriendlyName()
	{
		if (_gpuFriendlyName != null)
		{
			return _gpuFriendlyName;
		}
		try
		{
			// iGPU/独显混装时 WMI 顺序不保证: 优先取独显特征名(Arc/GeForce/Radeon), 否则退回第一个
			string first = "";
			using var mos = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
			foreach (System.Management.ManagementObject o in mos.Get())
			{
				using (o)
				{
					var nm = o["Name"]?.ToString()?.Trim();
					if (string.IsNullOrWhiteSpace(nm))
					{
						continue;
					}
					if (first.Length == 0)
					{
						first = nm;
					}
					if (nm.Contains("Arc", StringComparison.OrdinalIgnoreCase) || nm.Contains("GeForce", StringComparison.OrdinalIgnoreCase) || nm.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
					{
						_gpuFriendlyName = nm;
						break;
					}
				}
			}
			_gpuFriendlyName ??= first.Length > 0 ? first : null;
		}
		catch
		{
		}
		_gpuFriendlyName ??= "GPU(系统聚合)";
		return _gpuFriendlyName;
	}

	// 按显卡厂商挑 llama.cpp 预编译后端: NVIDIA=CUDA, Intel=SYCL(原生低比特内核), AMD/未知=Vulkan
	private string LlamaBackendPreference()
	{
		string g = GpuFriendlyName();
		if (g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || g.Contains("GeForce", StringComparison.OrdinalIgnoreCase) || g.Contains("RTX", StringComparison.OrdinalIgnoreCase) || g.Contains("GTX", StringComparison.OrdinalIgnoreCase))
		{
			return "cuda";
		}
		if (g.Contains("Intel", StringComparison.OrdinalIgnoreCase) || g.Contains("Arc", StringComparison.OrdinalIgnoreCase) || g.Contains("Iris", StringComparison.OrdinalIgnoreCase))
		{
			return "sycl";
		}
		return "vulkan";
	}

	// 真进度条: pct 有值=决定填充, 无值=不定态滑块
	private void SetMonBar(bool active, System.Windows.Media.Color tone, double? pct)
	{
		MonBarFill.Background = new SolidColorBrush(tone);
		MonBarFill.BeginAnimation(System.Windows.FrameworkElement.WidthProperty, null);
		double w = MonBar.ActualWidth;
		if (!active)
		{
			MonBarFill.Width = 0.0;
			_barAnim = null;
			return;
		}
		if (pct.HasValue && w > 0)
		{
			_barAnim = null;
			MonBarFill.Width = Math.Max(0.0, Math.Min(1.0, pct.Value / 100.0)) * w;
			return;
		}
		if (w <= 0 || _barAnim != null)
		{
			return;
		}
		var anim = new System.Windows.Media.Animation.DoubleAnimation
		{
			From = 0.0,
			To = w,
			Duration = new System.Windows.Duration(TimeSpan.FromSeconds(1.6)),
			AutoReverse = true,
			RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
			EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
		};
		_barAnim = anim;
		MonBarFill.BeginAnimation(System.Windows.FrameworkElement.WidthProperty, anim);
	}

	private System.Windows.Media.Animation.DoubleAnimation? _barAnim;

	// 时长: 42s / 3m12s / 1h02m
	private static string FmtEta(double sec)
	{
		if (sec <= 0)
		{
			return "—";
		}
		if (sec < 60)
		{
			return $"{sec:F0}s";
		}
		if (sec < 3600)
		{
			return $"{(int)(sec / 60)}m{(int)sec % 60:D2}s";
		}
		return $"{(int)(sec / 3600)}h{(int)(sec / 60) % 60:D2}m";
	}

	// Slot 表
	private static string FmtTok(double n)
	{
		if (n >= 1000000.0)
		{
			return (n / 1000000.0).ToString("0.#") + "M";
		}
		if (n >= 1000.0)
		{
			return (n / 1000.0).ToString("0.#") + "K";
		}
		return ((long)n).ToString();
	}

	// 模型信息卡
	private void UpdateModelInfoCard(LlamaApiMonitor? api)
	{
		var m = api?.Model;
		if (m == null || (string.IsNullOrEmpty(m.Path) && string.IsNullOrEmpty(m.Name)))
		{
			return;
		}
		MiName.Text = string.IsNullOrEmpty(m.Name) ? "—" : m.Name;
		MiPath.Text = "路径 " + (string.IsNullOrEmpty(m.Path) ? "—" : m.Path);
		MiFtype.Text = m.Ftype;
		MiParams.Text = m.NParams.HasValue ? FmtTok(m.NParams.Value) : "—";
		MiSize.Text = m.FileSize.HasValue ? m.FileSize.Value >= 1073741824L ? $"{m.FileSize.Value / 1073741824.0:F2} GB" : $"{m.FileSize.Value / 1048576.0:F0} MB" : "—";
		MiEmbd.Text = m.NEmbd?.ToString() ?? "—";
		MiVocab.Text = m.NVocab?.ToString() ?? "—";
		long? nctx = m.NCtx ?? api?.Boot?.NCtxSlot;
		if (nctx == null)
		{
			var sl = api?.SlotsSnapshot();
			var c = sl?.FirstOrDefault(x => x.NCtx > 0);
			nctx = c?.NCtx;
		}
		MiCtx.Text = nctx?.ToString() ?? "—";
		MiCtxTrain.Text = m.NCtxTrain?.ToString() ?? (nctx != null ? "≥ " + nctx : "—");
		MiVocabType.Text = string.IsNullOrEmpty(m.VocabType) ? "—" : m.VocabType;
		var mods = new List<string>();
		if (m.Vision)
		{
			mods.Add("vision");
		}
		if (m.Video)
		{
			mods.Add("video");
		}
		if (m.Audio)
		{
			mods.Add("audio");
		}
		MiModal.Text = mods.Count > 0 ? string.Join(" · ", mods) : "—";
	}

	// 事件流
	private void UpdateEventFeed(LlamaApiMonitor? api)
	{
		if (api == null)
		{
			return;
		}
		var evs = api.EventsSnapshot();
		if (api.TotalEvents == _eventFeedCount)
		{
			return;
		}
		_eventFeedCount = api.TotalEvents;
		MonEventCount.Text = $"{evs.Count} / 200";
		var list = new List<object>(evs.Count);
		for (int i = evs.Count - 1; i >= 0; i--)
		{
			var e = evs[i];
			var color = e.Level == "error" ? Color.FromRgb(0xff, 0x8a, 0x98)
				: e.Level == "warn" ? Color.FromRgb(0xf8, 0xc9, 0x6a)
				: e.Level == "ok" ? Color.FromRgb(0x4a, 0xde, 0x80)
				: Color.FromRgb(0x8e, 0x97, 0x9f);
			list.Add(new
			{
				Line = $"{e.Ts:HH:mm:ss}  {e.Msg}",
				LevelColor = new SolidColorBrush(color),
			});
		}
		MonEventFeed.ItemsSource = list;
	}

	private long _eventFeedCount;

	private static Grid CreateBar(double pct, Color color, double height)
	{
	var g = new Grid { Height = height, Background = new SolidColorBrush(Color.FromRgb(0x1d, 0x22, 0x26)) };
	g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0, Math.Min(100.0, pct)), GridUnitType.Star) });
	g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0, 100 - Math.Min(100.0, pct)), GridUnitType.Star) });
	g.Children.Add(new Border { Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(2) });
	return g;
	}
	// 保存监测页快照(Strata Save Monitor screenshot): 渲染面板为 PNG
	private void Snapshot_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)MonitorRoot.ActualWidth, (int)MonitorRoot.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
			rtb.Render(MonitorRoot);
			var dlg = new Microsoft.Win32.SaveFileDialog
			{
				Filter = "PNG 图片|*.png",
				FileName = $"monitor-{DateTime.Now:yyyyMMdd-HHmmss}.png"
			};
			if (dlg.ShowDialog() != true)
			{
				return;
			}
			var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
			enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
			using FileStream fs = File.Open(dlg.FileName, FileMode.Create);
			enc.Save(fs);
			Log("已保存监测快照: " + dlg.FileName);
		}
		catch (Exception ex)
		{
			LogError(ex, "保存监测快照失败");
		}
	}

	private Dictionary<string, string>? _lastPerfDict;
	private List<GpuDevInfo>? _lastPerfGpus;

	// 最近一拍重采样的 UTC 时刻(Agent /api/monitor 用来自报数据年龄)
	private DateTime _perfSampledAtUtc;

	/// <summary>
	/// 托盘轻量模式性能路径(重拍, 30s 一次): 只做历史序列入库与显存熔断复核, 不碰任何视觉元素。
	/// 熔断响应不靠这拍 —— perf 循环在轻量下有独立 3s 显存探针(NVML 进程内直读), 降频不降级。
	/// 全空闲时重拍退到 60s 且零采样。
	/// </summary>
	private void TrayLightPerf(Dictionary<string, string> d, List<GpuDevInfo> gpusPre)
	{
		var api = _llamaApi;
		if (api != null)
		{
			var (gSpd, pSpd, _) = api.Speeds();
			_hTps.Push(gSpd > 0.0 ? gSpd : null);
			_hPf.Push(pSpd > 0.0 ? pSpd : null);
			long ctxUsed = api.CtxUsed >= 0 ? api.CtxUsed : (api.LogCtxUsed ?? -1);
			_hCtx.Push(ctxUsed >= 0 ? ctxUsed : null);
			_hMtp.Push(api.MtpAcceptance is double acc ? acc * 100.0 : null);
		}
		else
		{
			_hTps.Push(null);
			_hPf.Push(null);
			_hCtx.Push(null);
			_hMtp.Push(null);
		}
		if (gpusPre.Count > 0)
		{
			var temps = gpusPre.Where(x => x.Temp > 0).ToArray();
			double pw = gpusPre.Sum(x => x.Power);
			_hGpuUtil.Push(gpusPre.Average(x => x.Util));
			_hGpuMem.Push(gpusPre.Sum(x => x.MemUsedMb) / 1024.0);
			_hTemp.Push(temps.Length > 0 ? temps.Max(x => x.Temp) : null);
			_hPower.Push(pw > 0 ? pw : null);
		}
		else
		{
			_hGpuUtil.Push(null);
			_hGpuMem.Push(null);
			_hTemp.Push(null);
			_hPower.Push(null);
		}
		if (d.TryGetValue("ram_used", out string vRu) && d.TryGetValue("ram_total", out string vRt)
			&& double.TryParse(vRu, NumberStyles.Float, CultureInfo.InvariantCulture, out var vRu2)
			&& double.TryParse(vRt, NumberStyles.Float, CultureInfo.InvariantCulture, out var vRt2)
			&& vRt2 > 0.0)
		{
			_hRam.Push(vRu2 / 1073741824.0);
		}
		else
		{
			_hRam.Push(null);
		}
		if (d.TryGetValue("cpu", out string vCpuL) && double.TryParse(vCpuL, NumberStyles.Float, CultureInfo.InvariantCulture, out var cpuPctL))
		{
			_hCpu.Push(cpuPctL);
		}
		else
		{
			_hCpu.Push(null);
		}
		CheckVramBreaker(gpusPre);
	}

	/// <summary>
	/// 显存熔断: llama 运行中显存越过阈值立即停止服务(整机 OOM 保护, 无防抖、绝不容许突破)。
	/// 百分比模式=各卡已用/总量; GB 模式=各卡已用合计。
	/// </summary>
	// 驱动器快照缓存: GetDrives/IsReady 对失效映射网络盘、空读卡器会阻塞数秒,
	// 原先每 3s 在 UI 线程执行导致监测页周期性整体冻结。后台 30s 刷新, UI 只读缓存。
	private static List<(string Name, double TotalGb, double FreeGb)> _driveSnapshot = new();

	private static DateTime _driveSnapAt = DateTime.MinValue;

	private static int _driveSnapBusy;

	private static List<(string Name, double TotalGb, double FreeGb)> DrivesCached()
	{
		if ((DateTime.Now - _driveSnapAt).TotalSeconds > 30.0
			&& Interlocked.CompareExchange(ref _driveSnapBusy, 1, 0) == 0)
		{
			Task.Run(delegate
			{
				try
				{
					var list = new List<(string, double, double)>();
					foreach (var drv in System.IO.DriveInfo.GetDrives())
					{
						try
						{
							if (!drv.IsReady || drv.DriveType != System.IO.DriveType.Fixed)
							{
								continue;
							}
							list.Add((drv.Name, drv.TotalSize / 1073741824.0, drv.AvailableFreeSpace / 1073741824.0));
						}
						catch
						{
						}
					}
					_driveSnapshot = list;
				}
				catch
				{
				}
				finally
				{
					_driveSnapAt = DateTime.Now;
					Interlocked.Exchange(ref _driveSnapBusy, 0);
				}
			});
		}
		return _driveSnapshot;
	}

	private void CheckVramBreaker(List<GpuDevInfo> gpus)
	{
		if (_vramBreakerTriggered || ChkVramBreaker?.IsChecked != true || gpus.Count == 0)
		{
			return;
		}
		if (_llamaProc == null || _llamaProc.HasExited)
		{
			return;
		}
		double usedMb = gpus.Sum(x => x.MemUsedMb);
		bool hit = false;
		string desc = "";
		if (double.TryParse(TxtVramBreakerVal.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double thr))
		{
			if (ComboVramBreakerType.SelectedIndex == 1)
			{
				double usedGb = usedMb / 1024.0;
				hit = usedGb >= thr;
				desc = $"{usedGb:F1}GB ≥ 阈值 {thr:F1}GB";
			}
			else
			{
				double totalMb = gpus.Sum(x => x.MemTotalMb);
				if (totalMb > 0)
				{
					double pct = usedMb / totalMb * 100.0;
					hit = pct >= thr;
					desc = $"{pct:F0}% ≥ 阈值 {thr:F0}%";
				}
			}
		}
		if (!hit)
		{
			return;
		}
		_vramBreakerTriggered = true;
		Log($"[熔断] 显存越过阈值({desc}), 自动停止 Llama 服务");
		LblVramBreaker.Text = "已触发";
		LblVramBreaker.Foreground = new SolidColorBrush(Color.FromRgb(0xff, 0x8a, 0x98));
		ShowNotice("⚠ 显存熔断已触发（" + desc + "），Llama 服务已自动停止。", isError: true);
		StopLlama();
	}

	private void OnPerf(Dictionary<string, string> d, List<GpuDevInfo> gpusPre)
	{
		// 托盘轻量模式: 只推历史 + 熔断判定, 全部视觉树重建跳过(恢复窗口时用
		// _lastPerfDict 立即全量刷新, 曲线无缺口)
		if (_trayMode)
		{
			TrayLightPerf(d, gpusPre);
			return;
		}
		// 速度历史: 日志 tg_3s / prompt 行 > /slots 差分。
		// 每拍必推(空闲推 null, api 不在也推): 以前只在速度>0 时推, 这 60 个槽的时间窗
		// 就漂移成"攒满 60 个活跃样本的时长"(可能跨几小时), 时间映射的曲线把长间隔当成
		// 相邻点直连、底部窗口标签乱跳 —— 监测页曲线紊乱的直接来源(Linux 端一直是每拍
		// null 填充, 所以同一份数据在那边是平的)。
		var api = _llamaApi;
		if (api != null)
		{
			var (gSpd, pSpd, _) = api.Speeds();
			_hTps.Push(gSpd > 0.0 ? gSpd : null);
			_hPf.Push(pSpd > 0.0 ? pSpd : null);
		}
		else
		{
			_hTps.Push(null);
			_hPf.Push(null);
		}
		bool decoding = api != null && api.Online && api.Phase == "decoding";
		bool prompting = api != null && api.Online && api.Phase == "prompt_processing";
		double decNow = _hTps.Last();
		double pfNow = _hPf.Last();
		var slotsNow = api?.SlotsSnapshot() ?? new List<LlamaApiMonitor.SlotInfo>();
		var actSlot = slotsNow.FirstOrDefault(x => x.Processing);

		// ctx 合并(slots API 优先, release/启动块兜底)
		long ctxUsed = api != null && api.CtxUsed >= 0 ? api.CtxUsed : api?.LogCtxUsed ?? -1;
		long ctxTotal = api?.CtxTotal ?? 0;
		if (ctxTotal <= 0)
		{
			long bt = api?.Boot?.NCtxSlot ?? 0;
			if (bt > 0)
			{
				ctxTotal = bt;
			}
		}
		_hCtx.Push(ctxUsed >= 0 ? ctxUsed : null);
		// MtpAcceptance 是 0-1 小数(圆环显示时 ×100); 历史序列按百分数入库, 供迷你曲线/趋势图共用
		_hMtp.Push(api?.MtpAcceptance is double accPct ? accPct * 100.0 : null);

		// ── 摘要条 ──
		StripGpuVal.Text = "—";
		StripMemVal.Text = "—";
		StripTempVal.Text = "—";
		StripPowerVal.Text = "—";

		// ── BarCard 生成速度 ──
		GenVal.Text = decoding && api.Tg3sTps is double gt3 ? FT(gt3)
			: api?.LastTask?.GenSpeed is double lg ? FT(lg)
			: !double.IsNaN(decNow) ? FT(decNow) : "—";
		GenSub.Text = decoding ? "decoding" : "";
		if (decoding && actSlot != null && actSlot.NRemain > 0 && GenBar.ActualWidth > 0)
		{
			double gp = Math.Min(100.0, actSlot.NDecoded * 100.0 / (actSlot.NDecoded + actSlot.NRemain));
			GenBarFill.Width = gp / 100.0 * GenBar.ActualWidth;
			GenBarMax.Text = FmtTok(actSlot.NDecoded + actSlot.NRemain);
		}
		else
		{
			GenBarFill.Width = 0;
			GenBarMax.Text = "";
		}
		GenFoot.Text = api != null && (api.TgTps.HasValue || api.Tg3sTps.HasValue)
			? $"tg {api.TgTps:0.##} · tg3 {api.Tg3sTps:0.##} tok/s"
			: api != null && api.Online ? "来源 /slots 差分" : "";
		GenSpark.SetSeries(_hTps.Arr(), null, double.NaN, "t/s", _hTps.Stamps(), ToneInfo, ToneAccent2, "", "");

		// ── BarCard 预填充速度 ──
		PfVal.Text = prompting && api.LogPromptSpeed is double lps ? FT(lps)
			: api?.LastPrefillSpeed is double lpf ? FT(lpf)
			: !double.IsNaN(pfNow) ? FT(pfNow) : "—";
		PfSub.Text = prompting ? "prefilling" : "";
		if (prompting && api.PromptProgress is double pp && PfBar.ActualWidth > 0)
		{
			PfBarFill.Width = Math.Min(100.0, pp * 100.0) / 100.0 * PfBar.ActualWidth;
			PfBarMax.Text = api.PromptTotalTokens is long pt ? FmtTok(pt) : "";
		}
		else
		{
			PfBarFill.Width = 0;
			PfBarMax.Text = "";
		}
		PfFoot.Text = prompting ? $"已耗时 {api.PromptElapsedS:F1}s · 总量 {(api.PromptTotalTokens.HasValue ? FmtTok(api.PromptTotalTokens.Value) : "—")} tok"
			: api?.LastPrefillSpeed is double lpf2 ? $"上次预填充 {FT(lpf2)} tok/s" : "";
		PfSpark.SetSeries(_hPf.Arr(), null, double.NaN, "t/s", _hPf.Stamps(), ToneAccent, ToneAccent2, "", "");

		// ── BarCard 上下文占用 ──
		if (ctxUsed >= 0 && ctxTotal > 0)
		{
			double pct = Math.Min(100.0, ctxUsed * 100.0 / ctxTotal);
			CtxVal.Text = FmtTok(ctxUsed);
			CtxVal.Foreground = new SolidColorBrush(pct >= 90 ? Color.FromRgb(0xff, 0x8a, 0x98) : pct >= 70 ? Color.FromRgb(0xf8, 0xc9, 0x6a) : Color.FromRgb(0xee, 0xf1, 0xf3));
			CtxMax.Text = FmtTok(ctxTotal);
			CtxBadge.Visibility = pct >= 90 ? Visibility.Visible : Visibility.Collapsed;
			if (CtxBar.ActualWidth > 0)
			{
				CtxBarFill.Width = pct / 100.0 * CtxBar.ActualWidth;
			}
			CtxBarFill.Background = new SolidColorBrush(pct >= 90 ? System.Windows.Media.Color.FromRgb(0xff, 0x5a, 0x6e) : pct >= 70 ? System.Windows.Media.Color.FromRgb(0xf5, 0xb5, 0x3d) : ToneAccent);
			CtxSub.Text = $"{pct:F1}% · 剩余 {FmtTok(Math.Max(0, ctxTotal - ctxUsed))} tokens";
		}
		else
		{
			CtxVal.Text = "—";
			CtxMax.Text = "—";
			CtxBadge.Visibility = Visibility.Collapsed;
			CtxBarFill.Width = 0;
			CtxSub.Text = "无请求记录";
		}
		CtxSpark.SetSeries(_hCtx.Arr(), null, ctxTotal > 0 ? ctxTotal : double.NaN, "tok", _hCtx.Stamps(), ToneWarn, ToneAccent2, "", "");

		// ── MTP 接受率 Gauge ──
		if (api?.MtpAcceptance is double acc)
		{
			MtpRing.StrokeDashArray = new System.Windows.Media.DoubleCollection { Math.Max(0.02, 24.74 * acc), 100.0 };
			MtpRing.Opacity = 1.0;
			MtpRing.Stroke = new SolidColorBrush(acc < 0.65 ? Color.FromRgb(0xff, 0x5a, 0x6e) : acc < 0.8 ? Color.FromRgb(0xf5, 0xb5, 0x3d) : Color.FromRgb(0x7c, 0xd7, 0xf7));
			MtpVal.Text = $"{acc * 100.0:F1}%";
			MtpVal.Foreground = new SolidColorBrush(acc < 0.65 ? Color.FromRgb(0xff, 0x8a, 0x98) : acc < 0.8 ? Color.FromRgb(0xf8, 0xc9, 0x6a) : Color.FromRgb(0xee, 0xf1, 0xf3));
			MtpSub.Text = $"{api.MtpAccepted} / {api.MtpGenerated} · mean {api.MtpMeanLen:0.##}" + (api.GraphsReused is long gr ? $" · graphs {gr}" : "");
		}
		else
		{
			MtpRing.Opacity = 0.0;
			MtpVal.Text = "—";
			MtpSub.Text = "启用投机解码后显示";
		}
		MtpSpark.SetSeries(_hMtp.Arr(), null, 100.0, "%", _hMtp.Stamps(), ToneInfo, ToneAccent2, "", "");

		// ── GPU 每卡 + 聚合(NVML → nvidia-smi → 既有聚合键合成单卡) ──
		// gpus 由 perf 线程采样后传入(UI 线程不再做进程外调用)
		var gpus = gpusPre;
		if (gpus.Count == 0 && d.TryGetValue("gpu_util", out string sGu0))
		{
			if (!_synthGpuLogged)
			{
				_synthGpuLogged = true;
				Log("[GPU] NVML 与 nvidia-smi 均不可用, GPU 面板使用系统聚合数据(非 NVIDIA 或驱动未装)");
			}
			// 非 NVIDIA/无驱动: 用既有聚合键合成一张卡, 保底不让 GPU 区空白
			double Num1(string k) => d.TryGetValue(k, out string v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var vv) ? vv : 0;
				gpus.Add(new GpuDevInfo
			{
				Index = 0,
				Name = d.TryGetValue("gpu_name", out string gn) ? gn : GpuFriendlyName(),
				Util = Num1("gpu_util"),
				// vram_* 键是字节(GpuMonitor 口径) → MB
				MemUsedMb = Num1("vram_used") / 1048576.0,
				MemTotalMb = Num1("vram_total") / 1048576.0,
				Temp = Num1("gpu_temp"),
				Power = Num1("power_w"),
				Limit = Num1("power_limit_w"),
				Fan = Num1("fan_pct"),
				Clock = Num1("clock_mhz"),
			});
		}
		if (gpus.Count > 0)
		{
			StripGpuVal.Text = gpus.Average(x => x.Util).ToString("F0");
			StripMemVal.Text = (gpus.Sum(x => x.MemUsedMb) / 1024.0).ToString("F1");
			StripMemUnit.Text = "GB";
			var temps = gpus.Where(x => x.Temp > 0).ToArray();
			StripTempVal.Text = temps.Length > 0 ? temps.Max(x => x.Temp).ToString("F0") : "—";
			double pw = gpus.Sum(x => x.Power);
			StripPowerVal.Text = pw > 0 ? pw.ToString("F0") : "—";
			_hGpuUtil.Push(gpus.Average(x => x.Util));
			_hGpuMem.Push(gpus.Sum(x => x.MemUsedMb) / 1024.0);
			_hTemp.Push(temps.Length > 0 ? temps.Max(x => x.Temp) : null);
			_hPower.Push(pw > 0 ? pw : null);
		}
		else
		{
			_hGpuUtil.Push(null);
			_hGpuMem.Push(null);
			_hTemp.Push(null);
			_hPower.Push(null);
		}
		CheckVramBreaker(gpus); // 熔断判定任何可见性下都必须生效
		if (_monitorTabVisible)
		{
			BuildGpuPanels(gpus);
		}

		// ── 系统资源 ──
		if (d.TryGetValue("cpu", out string vCpu) && double.TryParse(vCpu, out var cpuPct))
		{
			_hCpu.Push(cpuPct);
		}
		else
		{
			_hCpu.Push(null);
		}
		double ramUsedGb = double.NaN, ramTotalGb = double.NaN;
		if (d.TryGetValue("ram_used", out string vRu) && d.TryGetValue("ram_total", out string vRt) && double.TryParse(vRu, NumberStyles.Float, CultureInfo.InvariantCulture, out var vRu2) && double.TryParse(vRt, NumberStyles.Float, CultureInfo.InvariantCulture, out var vRt2) && vRt2 > 0.0)
		{
			ramUsedGb = vRu2 / 1073741824.0;
			ramTotalGb = vRt2 / 1073741824.0;
			_hRam.Push(ramUsedGb);
		}
		else
		{
			_hRam.Push(null);
		}
		double txSum = 0;
		foreach (var (_, _, t) in _netSamples)
		{
			txSum += t;
		}
		_hNet.Push(_netRxMbs > 0 ? _netRxMbs : null);
		_hNetTx.Push(txSum > 0 ? txSum : null);
		if (_monitorTabVisible)
		{
			BuildSysPanels(d, ramUsedGb, ramTotalGb);
		}

		// ── 模型与 Slot ──
		if (_monitorTabVisible)
		{
			BuildSlotCards(slotsNow, llamaUp: IsAlive(_llamaProc));
		}

		// ── 历史趋势 ──
		if (_monitorTabVisible)
		{
			UpdateTrends();
		}
		UpdatePhaseCard();

		// OOM 重试成功后真正进入 API 在线态才补重置额度(3 秒进程存活不算数)
		if (api != null && api.Online && _oomRetryActive)
		{
			_oomRetryActive = false;
			_oomRetryLeft = 3;
			Log("[OOM] 服务已在线, 重试额度重置为 3");
			// 降层值落盘: 否则下次启动回到 OOM 原值, 重演崩溃循环
			try
			{
				_profiles[_currentProfile] = BuildConfig();
				SaveConfigFile();
				Log("[OOM] 降层后的配置已写入当前方案");
			}
			catch (Exception exPer)
			{
				LogError(exPer, "OOM 降层配置持久化失败");
			}
		}

	// ============ 构建器: GPU 每卡面板 ============
		void BuildGpuPanels(List<GpuDevInfo> gpus)
		{
			GpuPanelsHost.Children.Clear();
			if (gpus.Count == 0)
			{
				GpuPanelsHost.Children.Add(new TextBlock { Text = "▣ 数据不可用（需要 NVIDIA 驱动 NVML）", Foreground = new SolidColorBrush(Color.FromRgb(0x8e, 0x97, 0x9f)), FontSize = 12, Margin = new Thickness(4, 2, 0, 2) });
				return;
			}
			foreach (var g in gpus)
			{
				var card = new Border
				{
					Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1a, 0x1d)),
					BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x30, 0x36)),
					BorderThickness = new Thickness(1),
					CornerRadius = new CornerRadius(10),
					Padding = new Thickness(14, 10, 14, 12),
					Margin = new Thickness(0, 0, 0, 8),
				};
				var root = new StackPanel();
				root.Children.Add(new TextBlock
				{
					Text = $"GPU{g.Index} · {(string.IsNullOrEmpty(g.Name) ? "NVIDIA GPU" : g.Name)}",
					Foreground = new SolidColorBrush(Color.FromRgb(0xee, 0xf1, 0xf3)),
					FontSize = 13,
					FontWeight = FontWeights.Bold,
				});
				var body = new Grid { Margin = new Thickness(0, 8, 0, 0) };
				body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
				body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				// 环形利用率
				var ringWrap = new Grid { Width = 104, Height = 104, VerticalAlignment = VerticalAlignment.Center };
				const double utilArc = 30.04; // 270° of r=42 圈(=104 边长 stroke8), WPF dash 单位=×8
				ringWrap.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = new SolidColorBrush(Color.FromRgb(0x1d, 0x22, 0x26)), StrokeThickness = 8, StrokeDashCap = PenLineCap.Round, StrokeDashArray = new DoubleCollection { utilArc, 100.0 }, RenderTransformOrigin = new Point(0.5, 0.5) });
				double frac = Math.Max(0.0, Math.Min(1.0, g.Util / 100.0));
				var utilColor = g.Util >= 90 ? Color.FromRgb(0xff, 0x5a, 0x6e) : g.Util >= 70 ? Color.FromRgb(0xf5, 0xb5, 0x3d) : Color.FromRgb(0x34, 0xd3, 0x99);
				ringWrap.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = new SolidColorBrush(utilColor), StrokeThickness = 8, StrokeDashCap = PenLineCap.Round, StrokeDashArray = new DoubleCollection { Math.Max(0.02, utilArc * frac), 100.0 }, RenderTransformOrigin = new Point(0.5, 0.5) });
				var ringLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
				ringLabel.Children.Add(new TextBlock { Text = $"{g.Util:F0}", FontSize = 20, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(Color.FromRgb(0xee, 0xf1, 0xf3)), HorizontalAlignment = HorizontalAlignment.Center });
				ringLabel.Children.Add(new TextBlock { Text = "利用率", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x8e, 0x97, 0x9f)), HorizontalAlignment = HorizontalAlignment.Center });
				ringWrap.Children.Add(ringLabel);
				Grid.SetColumn(ringWrap, 0);
				body.Children.Add(ringWrap);
				// 右列: 显存条 + kv
				var right = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
				double memPct = g.MemTotalMb > 0 ? g.MemUsedMb / g.MemTotalMb * 100.0 : 0;
				right.Children.Add(new TextBlock
				{
					Text = g.MemTotalMb > 0
						? $"显存 {g.MemUsedMb / 1024.0:F1} / {g.MemTotalMb / 1024.0:F1} GB ({memPct:F0}%)"
						: $"显存 {g.MemUsedMb / 1024.0:F1} GB",
					FontSize = 11,
					FontFamily = new FontFamily("Consolas"),
					Foreground = new SolidColorBrush(memPct >= 90 ? Color.FromRgb(0xff, 0x8a, 0x98) : Color.FromRgb(0x8e, 0x97, 0x9f)),
				});
				var memBar = CreateBar(memPct, memPct >= 90 ? Color.FromRgb(0xff, 0x5a, 0x6e) : memPct >= 70 ? Color.FromRgb(0xf5, 0xb5, 0x3d) : Color.FromRgb(0x34, 0xd3, 0x99), 5);
				memBar.Margin = new Thickness(0, 3, 0, 8);
				right.Children.Add(memBar);
				var grid3 = new UniformGrid { Columns = 3 };
				void Kv(string k, string v, System.Windows.Media.Color? c = null)
				{
					var sp = new StackPanel { Margin = new Thickness(0, 3, 10, 3) };
					sp.Children.Add(new TextBlock { Text = k, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x8e, 0x97, 0x9f)) });
					sp.Children.Add(new TextBlock { Text = v, FontSize = 12, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(c ?? Color.FromRgb(0xc7, 0xcd, 0xd3)) });
					grid3.Children.Add(sp);
				}
				Kv("温度", g.Temp > 0 ? $"{g.Temp:F0}°C" : "—", g.Temp >= 85 ? Color.FromRgb(0xff, 0x8a, 0x98) : null);
				Kv("功耗", g.Power > 0 ? $"{g.Power:F0} / {g.Limit:F0} W" : "—");
				Kv("风扇", $"{g.Fan:F0}%");
				Kv("频率", $"{g.Clock:F0} MHz");
				Kv("PCIe", g.Gen.HasValue ? $"gen{g.Gen} x{g.Width ?? 0}" : "—");
				Kv("P-State", string.IsNullOrEmpty(g.Pstate) ? "—" : g.Pstate);
				Kv("驱动", string.IsNullOrEmpty(g.Driver) ? "—" : g.Driver);
				Kv("显存利用率", $"{memPct:F0}%");
				Kv("卡序", $"{g.Index + 1}/{gpus.Count}");
				right.Children.Add(grid3);
				Grid.SetColumn(right, 1);
				body.Children.Add(right);
				root.Children.Add(body);
				card.Child = root;
				GpuPanelsHost.Children.Add(card);
			}
		}

		// ============ 系统面板(CPU 每核/内存/磁盘/网络) ============
		void BuildSysPanels(Dictionary<string, string> d, double ramUsedGb, double ramTotalGb)
		{
			// CPU
			if (d.TryGetValue("cpu", out string vCpu) && double.TryParse(vCpu, out var cpuPct))
			{
				CpuBigVal.Text = cpuPct.ToString("F0");
				CpuBigVal.Foreground = new SolidColorBrush(cpuPct >= 90 ? Color.FromRgb(0xff, 0x8a, 0x98) : cpuPct >= 70 ? Color.FromRgb(0xf8, 0xc9, 0x6a) : Color.FromRgb(0xee, 0xf1, 0xf3));
			}
			CpuHeadSub.Text = _cpuName;
			CpuCoresHost.Children.Clear();
			if (_corePcts.Length > 0)
			{
				bool dense = _corePcts.Length > 16;
				for (int i = 0; i < _corePcts.Length; i++)
				{
					double v = _corePcts[i];
					var col = v >= 90 ? Color.FromRgb(0xff, 0x5a, 0x6e) : v >= 70 ? Color.FromRgb(0xf5, 0xb5, 0x3d) : Color.FromRgb(0x34, 0xd3, 0x99);
					var coreWrap = new StackPanel { Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Bottom };
					var barGrid = new Grid { Height = 46, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Color.FromRgb(0x1d, 0x22, 0x26)) };
					barGrid.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = Math.Max(1, v / 100.0 * 46), Background = new SolidColorBrush(col) });
					coreWrap.Children.Add(barGrid);
					if (!dense)
					{
						coreWrap.Children.Add(new TextBlock { Text = i.ToString(), FontSize = 8, Foreground = new SolidColorBrush(Color.FromRgb(0x8e, 0x97, 0x9f)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) });
					}
					CpuCoresHost.Children.Add(coreWrap);
				}
			}
			// 内存
			if (!double.IsNaN(ramTotalGb) && ramTotalGb > 0)
			{
				double pct = Math.Min(100.0, ramUsedGb / ramTotalGb * 100.0);
				MemBigVal.Text = pct.ToString("F0");
				MemHeadSub.Text = $"{ramTotalGb:F1} GB 总量";
				MemUsedSub.Text = $"{ramUsedGb:F1} / {ramTotalGb:F0} GB";
				if (MemStackUsed.Parent is Grid pg && pg.ColumnDefinitions.Count == 0)
				{
					pg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(pct, GridUnitType.Star) });
					pg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - pct, GridUnitType.Star) });
				}
				else if (MemStackUsed.Parent is Grid pg2 && pg2.ColumnDefinitions.Count == 2)
				{
					pg2.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
					pg2.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
				}
				MemStackUsed.Background = new SolidColorBrush(pct >= 90 ? Color.FromRgb(0xff, 0x5a, 0x6e) : pct >= 70 ? Color.FromRgb(0xf5, 0xb5, 0x3d) : Color.FromRgb(0x34, 0xd3, 0x99));
				MemLegendUsed.Text = $" 已用 {ramUsedGb:F1} GB";
				MemLegendAvail.Text = $" 可用 {Math.Max(0.0, ramTotalGb - ramUsedGb):F1} GB";
			}
			// 磁盘
			double rd = d.TryGetValue("disk_read", out string vDr2) && double.TryParse(vDr2, NumberStyles.Float, CultureInfo.InvariantCulture, out var vDr3) ? vDr3 / 1048576.0 : 0;
			double wr = d.TryGetValue("disk_write", out string vDw1) && double.TryParse(vDw1, NumberStyles.Float, CultureInfo.InvariantCulture, out var vDw2) ? vDw2 / 1048576.0 : 0;
			DiskHeadSub.Text = $"读 {rd:F1} MB/s · 写 {wr:F1} MB/s";
			DiskRowsHost.Children.Clear();
			foreach (var dr in DrivesCached())
			{
				try
				{
					double total = dr.TotalGb;
					double free = dr.FreeGb;
					double used = total - free;
					double pct = total > 0 ? used / total * 100.0 : 0;
					var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
					row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
					row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
					row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
					DiskRowsHost.Children.Add(row);
					var name = new TextBlock { Text = dr.Name, FontSize = 11, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(Color.FromRgb(0xc7, 0xcd, 0xd3)), VerticalAlignment = VerticalAlignment.Center };
					Grid.SetColumn(name, 0);
					row.Children.Add(name);
					var barWrap = CreateBar(pct, pct >= 90 ? Color.FromRgb(0xff, 0x5a, 0x6e) : pct >= 70 ? Color.FromRgb(0xf5, 0xb5, 0x3d) : Color.FromRgb(0x34, 0xd3, 0x99), 6);
					barWrap.VerticalAlignment = VerticalAlignment.Center;
					barWrap.Margin = new Thickness(0, 0, 10, 0);
					Grid.SetColumn(barWrap, 1);
					row.Children.Add(barWrap);
					var txt = new TextBlock { Text = $"{used:F0}G / {total:F0}G ({pct:F0}%)", FontSize = 11, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(Color.FromRgb(0x8e, 0x97, 0x9f)), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
					Grid.SetColumn(txt, 2);
					row.Children.Add(txt);
				}
				catch
				{
				}
			}
			// 网络
			NetHeadSub.Text = $"{_netSamples.Count} 个网卡";
			NetRowsHost.Children.Clear();
			foreach (var (name, rx, tx) in _netSamples.Take(6))
			{
				var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
				row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
				row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
				NetRowsHost.Children.Add(row);
				var n = new TextBlock { Text = name, FontSize = 11, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(Color.FromRgb(0xc7, 0xcd, 0xd3)), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
				Grid.SetColumn(n, 0);
				row.Children.Add(n);
				var r = new TextBlock { Text = $"↓ {rx:F2} MB/s", FontSize = 11, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(Color.FromRgb(0x7c, 0xd7, 0xf7)), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
				Grid.SetColumn(r, 1);
				row.Children.Add(r);
				var t = new TextBlock { Text = $"↑ {tx:F2} MB/s", FontSize = 11, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
				Grid.SetColumn(t, 2);
				row.Children.Add(t);
			}
		}

		// ============ Slot 卡片(卡片式) ============
		void BuildSlotCards(List<LlamaApiMonitor.SlotInfo> slots, bool llamaUp)
		{
			SlotCardsHost.Children.Clear();
			if (!llamaUp || slots.Count == 0)
			{
				SlotCardsHost.Children.Add(new TextBlock { Text = llamaUp ? "无活动槽位" : "llama 未启动", Foreground = new SolidColorBrush(Color.FromRgb(0x8e, 0x97, 0x9f)), FontSize = 12, Margin = new Thickness(0, 2, 0, 2) });
				MonSlotSummary.Text = "";
				return;
			}
			int proc = 0;
			foreach (var x in slots)
			{
				if (x.Processing)
				{
					proc++;
				}
				var card = new Border
				{
					Background = new SolidColorBrush(Color.FromRgb(0x1d, 0x22, 0x26)),
					BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x30, 0x36)),
					BorderThickness = new Thickness(1),
					CornerRadius = new CornerRadius(8),
					Padding = new Thickness(12, 8, 12, 10),
					Margin = new Thickness(0, 0, 0, 8),
				};
				var root = new StackPanel();
				var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
				var badge = new Border
				{
					Background = new SolidColorBrush(x.Processing ? Color.FromArgb(0x2e, 0x34, 0xd3, 0x99) : Color.FromRgb(0x1a, 0x1a, 0x1a)),
					CornerRadius = new CornerRadius(999),
					Padding = new Thickness(10, 2, 10, 2),
					VerticalAlignment = VerticalAlignment.Center,
				};
					badge.Child = new TextBlock { Text = x.Processing ? "处理中" : "空闲", FontSize = 11, Foreground = new SolidColorBrush(x.Processing ? Color.FromRgb(0x4a, 0xde, 0x80) : Color.FromRgb(0x8e, 0x97, 0x9f)) };
				DockPanel.SetDock(badge, Dock.Right);
				head.Children.Add(badge);
				head.Children.Add(new TextBlock { Text = $"Slot {x.Id}", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0xee, 0xf1, 0xf3)) });
				root.Children.Add(head);
				var grid = new UniformGrid { Columns = 2 };
				void Kv(string k, string v, System.Windows.Media.Color? c = null)
				{
					var sp = new StackPanel { Margin = new Thickness(0, 2, 10, 2) };
					sp.Children.Add(new TextBlock { Text = k, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x8e, 0x97, 0x9f)) });
					sp.Children.Add(new TextBlock { Text = v, FontSize = 12, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(c ?? Color.FromRgb(0xc7, 0xcd, 0xd3)) });
					grid.Children.Add(sp);
				}
				Kv("任务", x.TaskId.HasValue ? "#" + x.TaskId : "—");
				Kv("n_ctx", FmtTok(x.NCtx));
				Kv("Prompt", FmtTok(x.NPromptTokens));
				Kv("已处理 / 缓存", $"{FmtTok(x.NPromptProcessed)} / {FmtTok(x.NPromptCache)}");
				Kv("已解码", FmtTok(x.NDecoded));
				Kv("剩余", FmtTok(x.NRemain));
				double pct = x.CtxPct ?? 0;
				Kv("上下文占用", x.NCtx > 0 ? $"{FmtTok(x.CtxUsed)} / {FmtTok(x.NCtx)} ({pct:F0}%)" : "—", pct >= 90 ? Color.FromRgb(0xff, 0x8a, 0x98) : pct >= 70 ? Color.FromRgb(0xf8, 0xc9, 0x6a) : null);
				Kv("投机解码", x.Speculative ? "启用" : "—");
				root.Children.Add(grid);
				root.Children.Add(new TextBlock
				{
					Text = $"gen {x.GenSpeed:0.#} t/s · prompt {x.PromptSpeed:0.#} t/s",
					FontSize = 11,
					FontFamily = new FontFamily("Consolas"),
					Foreground = new SolidColorBrush(Color.FromRgb(0xc7, 0xcd, 0xd3)),
					Margin = new Thickness(0, 4, 0, 0),
				});
				card.Child = root;
				SlotCardsHost.Children.Add(card);
			}
			MonSlotSummary.Text = $"{slots.Count} 槽 · {proc} 处理中";
		}

		// ============ 历史趋势(实时 60s 窗口) ============
		void UpdateTrends()
		{
			TrendGen.StampsRef = _hTps.Stamps();
			TrendPf.StampsRef = _hPf.Stamps();
			TrendCtx.StampsRef = _hCtx.Stamps();
			TrendMtp.StampsRef = _hMtp.Stamps();
			TrendGpuUtil.StampsRef = _hGpuUtil.Stamps();
			TrendGpuMem.StampsRef = _hGpuMem.Stamps();
			TrendGpuTemp.StampsRef = _hTemp.Stamps();
			TrendGpuPower.StampsRef = _hPower.Stamps();
			TrendCpu.StampsRef = _hCpu.Stamps();
			TrendMem.StampsRef = _hRam.Stamps();
			TrendNet.StampsRef = _hNet.Stamps();
			TrendGen.SetSeries("tok/s", new List<(string, Color, double?[])> { ("生成", Color.FromRgb(0x7c, 0xd7, 0xf7), _hTps.Arr()) }, double.NaN, double.NaN);
			TrendPf.SetSeries("tok/s", new List<(string, Color, double?[])> { ("预填充", Color.FromRgb(0x4a, 0xde, 0x80), _hPf.Arr()) }, double.NaN, double.NaN);
			TrendCtx.SetSeries("tokens", new List<(string, Color, double?[])> { ("上下文", Color.FromRgb(0xf5, 0xb5, 0x3d), _hCtx.Arr()) }, 0, ctxTotal > 0 ? ctxTotal : double.NaN);
			TrendMtp.SetSeries("%", new List<(string, Color, double?[])> { ("接受率", Color.FromRgb(0x7c, 0xd7, 0xf7), _hMtp.Arr()) }, 0, 100);
			TrendGpuUtil.SetSeries("%", new List<(string, Color, double?[])> { ("利用率", Color.FromRgb(0xf0, 0x88, 0x3e), _hGpuUtil.Arr()) }, 0, 100);
			TrendGpuMem.SetSeries("GB", new List<(string, Color, double?[])> { ("显存", Color.FromRgb(0xf0, 0x88, 0x3e), _hGpuMem.Arr()) }, 0, double.NaN);
			TrendGpuTemp.SetSeries("°C", new List<(string, Color, double?[])> { ("温度", Color.FromRgb(0xf5, 0xb5, 0x3d), _hTemp.Arr()) }, 0, 110);
			TrendGpuPower.SetSeries("W", new List<(string, Color, double?[])> { ("功耗", Color.FromRgb(0xd2, 0x99, 0x22), _hPower.Arr()) }, 0, double.NaN);
			TrendCpu.SetSeries("%", new List<(string, Color, double?[])> { ("CPU", Color.FromRgb(0x58, 0xa6, 0xff), _hCpu.Arr()) }, 0, 100);
			TrendMem.SetSeries("GB", new List<(string, Color, double?[])> { ("内存", Color.FromRgb(0x58, 0xa6, 0xff), _hRam.Arr()) }, 0, double.NaN);
			TrendNet.SetSeries("MB/s", new List<(string, Color, double?[])> { ("下行", Color.FromRgb(0x7c, 0xd7, 0xf7), _hNet.Arr()), ("上行", Color.FromRgb(0x4a, 0xde, 0x80), _hNetTx.Arr()) }, 0, double.NaN);
		}

		if (d.TryGetValue("cpu_err", out string vCe))
		{
			Log("[Perf] CPU error: " + vCe);
		}
	}

	private void HealthCheck(object? s, EventArgs e)
	{
		if (!IsAlive(_llamaProc) && _llamaProc != null)
		{
			OnLlamaDone();
		}
		if (_dshProc != null && !IsAlive(_dshProc))
		{
			int id = _dshProc.Id;
			string dd = EffectiveDshDir();
			int? num = ResolveDshPid(id, dd);
			if (num.HasValue && num.Value != id)
			{
				_dshTracker = new ProcessTracker(num.Value);
				DisposeProc(ref _dshProc);
				SaveState();
				Log($"DSH wrapper {id}->{num.Value}");
			}
			else if (!num.HasValue)
			{
				OnDshDone();
			}
		}
		else if (_dshTracker != null && _dshTracker.Poll().HasValue)
		{
			OnDshDone();
		}
		UpdateTray();
	}

	private static bool IsAlive(Process? p)
	{
		try
		{
			return p != null && !p.HasExited;
		}
		catch
		{
			return false;
		}
	}

	private void SaveState()
	{
		StateFile stateFile = new StateFile();
		if (IsAlive(_llamaProc))
		{
			stateFile.LlamaPid = _llamaProc.Id;
		}
		if (IsAlive(_dshProc))
		{
			stateFile.DshPid = _dshProc.Id;
			stateFile.DshDir = InputDshDir.Text.Trim().Trim('"');
		}
		if (_dshTracker != null && !_dshTracker.Poll().HasValue)
		{
			stateFile.DshPid = _dshTracker.Pid;
			stateFile.DshDir = InputDshDir.Text.Trim().Trim('"');
		}
		if (stateFile.LlamaPid.HasValue || stateFile.DshPid.HasValue)
		{
			try
			{
				WriteTextAtomic(_stateFile, JsonSerializer.Serialize(stateFile, new JsonSerializerOptions
				{
					WriteIndented = true
				}));
				return;
			}
			catch (Exception ex)
			{
				LogError(ex, "状态保存失败");
				return;
			}
		}
		ClearStateFile();
	}

	private void LoadState()
	{
		StateFile stateFile = null;
		if (File.Exists(_stateFile))
		{
			try
			{
				stateFile = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_stateFile));
			}
			catch (Exception ex)
			{
				LogError(ex, "读取状态文件失败");
			}
		}
		if (stateFile != null && stateFile.LlamaPid.HasValue)
		{
			try
			{
				Process pidProc = Process.GetProcessById(stateFile.LlamaPid.Value);
				// PID 复用防护: 只有 llama-server 本尊才接管, 否则"停止"会误杀无关进程
				if (pidProc.HasExited || !pidProc.ProcessName.StartsWith("llama-server", StringComparison.OrdinalIgnoreCase))
				{
					Log($"状态文件中的 Llama PID {stateFile.LlamaPid.Value} 已失效(进程退出或 PID 被复用), 跳过接管");
					pidProc.Dispose();
					ClearStateFile();
				}
				else
				{
					_llamaProc = pidProc;
					BtnLlamaStart.IsEnabled = false;
					BtnLlamaStop.IsEnabled = true;
					LblLlamaStatus.Text = "● 接管运行中";
					LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 185, 80));
					Log($"Resumed Llama:{stateFile.LlamaPid.Value}");
				}
			}
			catch (Exception ex2)
			{
				LogError(ex2, "接管 Llama 进程失败");
				// PID 已不存在(GetProcessById 抛异常)是最常见失效场景:
				// 不清状态文件会让每次启动都报一遍接管失败
				ClearStateFile();
			}
		}
		string dd = stateFile?.DshDir ?? EffectiveDshDir();
		if (string.IsNullOrEmpty(dd))
		{
			dd = EffectiveDshDir();
		}
		StateFile stRef = stateFile;
		Task.Run(delegate
		{
			try
			{
				int? pp = ProcessHelper.FindProcessByPort(3080);
				if (pp.HasValue)
				{
					((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
					{
						AttachDsh(pp.Value, "Resumed DSH", saveState: true);
					}, Array.Empty<object>());
				}
				else
				{
					StateFile stateFile2 = stRef;
					if (stateFile2 != null && stateFile2.DshPid.HasValue)
					{
						int dp = stRef.DshPid.Value;
						try
						{
							using (Process.GetProcessById(dp))
							{
								int? rp = ResolveDshPid(dp, dd);
								int tp = rp.GetValueOrDefault(dp);
								((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
								{
									AttachDsh(tp, "Resumed DSH", saveState: false);
									if (rp.HasValue && rp.Value != dp)
									{
										SaveState();
									}
								}, Array.Empty<object>());
								return;
							}
						}
						catch (Exception ex3)
						{
							LogError(ex3, "接管 DSH 进程失败");
						}
					}
					List<int> or = ProcessHelper.FindOrphanedDshProcesses(dd);
					if (or.Count > 0)
					{
						((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
						{
							AttachDsh(or[0], "Orphan DSH", saveState: false);
						}, Array.Empty<object>());
					}
					else
					{
						((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
						{
							if (_llamaProc == null && _dshProc == null && _dshTracker == null)
							{
								ClearStateFile();
							}
						}, Array.Empty<object>());
					}
				}
			}
			catch (Exception ex4)
			{
				LogError(ex4, "DSH 接管后台任务异常");
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					if (_llamaProc == null && _dshProc == null && _dshTracker == null)
					{
						ClearStateFile();
					}
				}, Array.Empty<object>());
			}
		});
	}

	private void AttachDsh(int pid, string logPrefix, bool saveState)
	{
		_dshTracker = new ProcessTracker(pid);
		BtnDshStart.IsEnabled = false;
		BtnDshStop.IsEnabled = true;
		BtnOpenDsh.IsEnabled = true;
		LblDshStatus.Text = "● 接管运行中";
		LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 185, 80));
		Log($"{logPrefix}:{pid}");
		if (saveState)
		{
			SaveState();
		}
	}

	private void ClearStateFor(string svc)
	{
		if (!File.Exists(_stateFile))
		{
			return;
		}
		try
		{
			StateFile stateFile = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_stateFile));
			if (stateFile != null)
			{
				if (svc == "llama")
				{
					stateFile.LlamaPid = null;
				}
				else if (svc == "dsh")
				{
					stateFile.DshPid = null;
				}
				if (stateFile.LlamaPid.HasValue || stateFile.DshPid.HasValue)
				{
					WriteTextAtomic(_stateFile, JsonSerializer.Serialize(stateFile, new JsonSerializerOptions
					{
						WriteIndented = true
					}));
				}
				else
				{
					ClearStateFile();
				}
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "清理状态失败");
		}
	}

	private void ClearStateFile()
	{
		try
		{
			if (File.Exists(_stateFile))
			{
				File.Delete(_stateFile);
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "删除状态文件失败");
		}
	}

	// 原子文本写: tmp + File.Replace, 与 SaveConfigFile 同语义。
	// 状态文件半写入(杀进程/断电)会导致重启后无法接管 llama/DSH, 代价高。
	private static void WriteTextAtomic(string path, string text)
	{
		string tmpPath = path + ".tmp";
		File.WriteAllText(tmpPath, text);
		if (File.Exists(path))
		{
			File.Replace(tmpPath, path, null);
		}
		else
		{
			File.Move(tmpPath, path);
		}
	}

	private void LoadConfigDirFromFile()
	{
		try
		{
			if (File.Exists(_cfgPathFile))
			{
				ConfigPathFile configPathFile = JsonSerializer.Deserialize<ConfigPathFile>(File.ReadAllText(_cfgPathFile));
				if (!string.IsNullOrEmpty(configPathFile?.Dir) && Directory.Exists(configPathFile.Dir))
				{
					_configDir = configPathFile.Dir;
				}
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "读取配置路径文件失败");
		}
	}

	private void SaveConfigDirToFile()
	{
		try
		{
			File.WriteAllText(_cfgPathFile, JsonSerializer.Serialize(new ConfigPathFile
			{
				Dir = _configDir
			}, new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
		catch (Exception ex)
		{
			LogError(ex, "保存配置路径文件失败");
		}
	}

	private void ChangeConfigPath_Click(object s, RoutedEventArgs e)
	{
		string? dir = PickFolder("选择配置文件存放目录", _configDir);
		if (dir != null)
		{
			_configDir = dir;
			SaveConfigDirToFile();
			Log("Config dir:" + _configDir);
		}
	}

	private ProfileConfig BuildConfig()
	{
		ProfileConfig profileConfig = new ProfileConfig
		{
			ModelPath = InputModelPath.Text,
			MtpPath = InputMtpPath.Text,
			MmprojPath = InputMmprojPath.Text,
			MmprojUrl = InputMmprojUrl.Text,
			NoMmprojOffload = (ChkNoMmprojOffload.IsChecked == true),
			LlamaDir = InputLlamaDir.Text,
			LlamaVariants = _llamaVariants,
			ActiveLlamaVariant = _activeVariant,
			DshDir = InputDshDir.Text,
			DshMode = ComboDshMode.SelectedIndex,
			DshAdminMode = (ChkDshAdmin.IsChecked == true),
			MinimizeToTray = (ChkMinimizeToTray.IsChecked == true)
		};
		foreach (KeyValuePair<string, System.Windows.Controls.CheckBox> item3 in _pbw)
		{
			profileConfig.BoolParams[item3.Key] = item3.Value.IsChecked == true;
		}
		foreach (KeyValuePair<string, (System.Windows.Controls.CheckBox, System.Windows.Controls.Control)> item4 in _pw)
		{
			(System.Windows.Controls.CheckBox, System.Windows.Controls.Control) value = item4.Value;
			System.Windows.Controls.CheckBox item = value.Item1;
			System.Windows.Controls.Control item2 = value.Item2;
			string value2 = ((item2 is System.Windows.Controls.ComboBox comboBox) ? comboBox.Text : ((System.Windows.Controls.TextBox)item2).Text);
			profileConfig.ValueParams[item4.Key] = new ValueParamData
			{
				Enabled = (item.IsChecked == true),
				Value = value2
			};
		}
		profileConfig.CmdPreview = (string.IsNullOrEmpty(_lastCmd) ? CmdPreview.Text : _lastCmd);
		profileConfig.CustomArgs = new List<string>(_customArgs);
		profileConfig.VramBreakerEnabled = ChkVramBreaker?.IsChecked == true;
		profileConfig.VramBreakerType = ComboVramBreakerType?.SelectedIndex ?? 0;
		double vramBreakerValue;
		// 全链路 InvariantCulture(与 CheckVramBreaker 的解析一致):
		// 逗号小数 locale 下 CurrentCulture 读写会让熔断阈值"90,5"解析失败而静默失效。
		if (!double.TryParse(TxtVramBreakerVal?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
		{
			NoScrollComboBox comboVramBreakerType = ComboVramBreakerType;
			vramBreakerValue = ((comboVramBreakerType != null && comboVramBreakerType.SelectedIndex == 0) ? 90 : 20);
		}
		else
		{
			vramBreakerValue = result;
		}
		profileConfig.VramBreakerValue = vramBreakerValue;
		return profileConfig;
	}

	private void ApplyConfig(ProfileConfig c)
	{
		// 切换方案时退出命令编辑态，并恢复该方案自己的自定义参数。
		_cmdEditing = false;
		CmdPreview.IsReadOnly = true;
		CmdPreview.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(126, 231, 135));
		CmdPreview.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(13, 17, 23));
		BtnEditCmd.Content = "✏ 编辑命令（仅追加自定义参数）";
		BtnCancelCmd.Visibility = Visibility.Collapsed;
		_customArgs = c.CustomArgs != null ? new List<string>(c.CustomArgs) : new List<string>();
		// llama.cpp 参数改名后，旧 profile 里旧 flag 键的值自动迁移到新 flag
		ParamSchemaService.MigrateProfileFlags(c, _paramDefs);
		InputModelPath.Text = c.ModelPath;
		InputMtpPath.Text = c.MtpPath;
		InputMmprojPath.Text = c.MmprojPath;
		InputMmprojUrl.Text = c.MmprojUrl;
		ChkNoMmprojOffload.IsChecked = c.NoMmprojOffload;
		InputLlamaDir.Text = c.LlamaDir;
		LoadLlamaVariants(c);
		InputDshDir.Text = c.DshDir;
		ComboDshMode.SelectedIndex = c.DshMode;
		ChkDshAdmin.IsChecked = c.DshAdminMode;
		ChkMinimizeToTray.IsChecked = c.MinimizeToTray;
		if (ChkVramBreaker != null)
		{
			ChkVramBreaker.IsChecked = c.VramBreakerEnabled;
		}
		if (ComboVramBreakerType != null)
		{
			ComboVramBreakerType.SelectedIndex = c.VramBreakerType;
		}
		if (TxtVramBreakerVal != null)
		{
			TxtVramBreakerVal.Text = c.VramBreakerValue.ToString(CultureInfo.InvariantCulture);
		}
		foreach (KeyValuePair<string, bool> boolParam in c.BoolParams)
		{
			if (_pbw.TryGetValue(boolParam.Key, out System.Windows.Controls.CheckBox value))
			{
				value.IsChecked = boolParam.Value;
			}
		}
		foreach (KeyValuePair<string, ValueParamData> valueParam in c.ValueParams)
		{
			if (_pw.TryGetValue(valueParam.Key, out (System.Windows.Controls.CheckBox, System.Windows.Controls.Control) value2))
			{
				value2.Item1.IsChecked = valueParam.Value.Enabled;
				if (value2.Item2 is System.Windows.Controls.ComboBox comboBox)
				{
					comboBox.Text = valueParam.Value.Value;
				}
				else
				{
					((System.Windows.Controls.TextBox)value2.Item2).Text = valueParam.Value.Value;
				}
			}
		}
		RefreshCmdNow();
	}

	private void SaveConfigFile()
	{
		string path = Path.Combine(_configDir, _configFilename);
		try
		{
			if (!Directory.Exists(_configDir))
			{
				Directory.CreateDirectory(_configDir);
			}
			// 原子写: 半写入(进程被杀/断电)不再产生截断的配置文件
			string tmpPath = path + ".tmp";
			File.WriteAllText(tmpPath, JsonSerializer.Serialize(new ConfigFile
			{
				Profiles = _profiles,
				CurrentProfile = _currentProfile,
				WindowWidth = base.ActualWidth,
				WindowHeight = base.ActualHeight
			}, new JsonSerializerOptions
			{
				WriteIndented = true,
				Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
			}));
			if (File.Exists(path))
			{
				File.Replace(tmpPath, path, null);
			}
			else
			{
				File.Move(tmpPath, path);
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "保存配置失败");
			ShowNotice("Save failed:" + ex.Message, isError: true);
		}
	}

	private void RestoreWindowSize()
	{
		try
		{
			if (File.Exists(_windowStateFile))
			{
				ConfigFile configFile = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(_windowStateFile));
				if (configFile != null && configFile.WindowWidth > 100.0 && configFile.WindowHeight > 100.0)
				{
					base.Width = Math.Min(configFile.WindowWidth, SystemParameters.PrimaryScreenWidth);
					base.Height = Math.Min(configFile.WindowHeight, SystemParameters.PrimaryScreenHeight);
				}
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "恢复窗口尺寸失败");
		}
	}

	private void SaveWindowSize()
	{
		try
		{
			if (base.WindowState != WindowState.Minimized)
			{
				WriteTextAtomic(_windowStateFile, JsonSerializer.Serialize(new ConfigFile
				{
					WindowWidth = base.ActualWidth,
					WindowHeight = base.ActualHeight
				}, new JsonSerializerOptions
				{
					WriteIndented = true
				}));
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "保存窗口尺寸失败");
		}
	}

	private (Dictionary<string, ProfileConfig>, string)? LoadConfigFile()
	{
		string path = Path.Combine(_configDir, _configFilename);
		if (!File.Exists(path))
		{
			return null;
		}
		try
		{
			string json = File.ReadAllText(path);
			ConfigFile configFile = JsonSerializer.Deserialize<ConfigFile>(json);
			if (configFile == null)
			{
				return null;
			}
			if (configFile.Profiles == null || configFile.Profiles.Count == 0)
			{
				try
				{
					ProfileConfig profileConfig = JsonSerializer.Deserialize<ProfileConfig>(json);
					if (profileConfig != null)
					{
						return (new Dictionary<string, ProfileConfig> { { "Default", profileConfig } }, "Default");
					}
				}
				catch (Exception ex)
				{
					LogError(ex, "旧格式配置解析失败");
				}
			}
			return (configFile.Profiles ?? new Dictionary<string, ProfileConfig>(), configFile.CurrentProfile ?? "Default");
		}
		catch (Exception ex2)
		{
			LogError(ex2, "读取配置文件失败");
			// 损坏文件留底: 否则下一次保存用默认状态覆盖, 多方案数据永久丢失且无感知
			try
			{
				string bak = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".bak";
				File.Copy(path, bak, true);
				Log("[配置] 已把损坏的配置文件备份为: " + bak);
				ShowNotice("配置文件损坏无法读取，已备份为 " + Path.GetFileName(bak) + "，本次使用默认配置。", isError: true);
			}
			catch
			{
			}
			return null;
		}
	}

	private void AutoLoadConfig()
	{
		(Dictionary<string, ProfileConfig>, string)? tuple = LoadConfigFile();
		if (!tuple.HasValue)
		{
			RefreshProfileCombo();
			return;
		}
		(Dictionary<string, ProfileConfig>, string) value = tuple.Value;
		Dictionary<string, ProfileConfig> item = value.Item1;
		string item2 = value.Item2;
		_profiles = item;
		_currentProfile = item2;
		RefreshProfileCombo();
		if (item.TryGetValue(item2, out var value2))
		{
			ApplyConfig(value2);
			Log("Loaded:" + item2);
		}
		RestoreWindowSize();
	}

	private void QuickSave_Click(object s, RoutedEventArgs e)
	{
		_profiles[_currentProfile] = BuildConfig();
		SaveConfigFile();
		Log("Saved:" + _currentProfile);
	}

	private void SaveAs_Click(object s, RoutedEventArgs e)
	{
		_profiles[_currentProfile] = BuildConfig();
		Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
		{
			FileName = _configFilename,
			InitialDirectory = _configDir,
			Filter = "JSON|*.json"
		};
		if (saveFileDialog.ShowDialog() == true)
		{
			try
			{
				File.WriteAllText(saveFileDialog.FileName, JsonSerializer.Serialize(new ConfigFile
				{
					Profiles = _profiles,
					CurrentProfile = _currentProfile,
					WindowWidth = base.ActualWidth,
					WindowHeight = base.ActualHeight
				}, new JsonSerializerOptions
				{
					WriteIndented = true,
					Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
				}));
				Log("Exported:" + saveFileDialog.FileName);
			}
			catch (Exception ex)
			{
				LogError(ex, "导出配置失败");
				ShowNotice("Export failed:" + ex.Message, isError: true);
			}
		}
	}

	private void LoadConfig_Click(object s, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			InitialDirectory = _configDir,
			Filter = "JSON|*.json"
		};
		if (openFileDialog.ShowDialog() != true)
		{
			return;
		}
		try
		{
			string json = File.ReadAllText(openFileDialog.FileName);
			Dictionary<string, ProfileConfig> dictionary;
			string text;
			try
			{
				ConfigFile? configFile = JsonSerializer.Deserialize<ConfigFile>(json);
				dictionary = configFile?.Profiles ?? new Dictionary<string, ProfileConfig>();
				text = configFile?.CurrentProfile ?? dictionary.Keys.FirstOrDefault() ?? "Default";
			}
			catch (Exception ex)
			{
				LogError(ex, "旧格式配置解析失败");
				ProfileConfig value = JsonSerializer.Deserialize<ProfileConfig>(json);
				dictionary = new Dictionary<string, ProfileConfig> { 
				{
					Path.GetFileNameWithoutExtension(openFileDialog.FileName),
					value
				} };
				text = dictionary.Keys.First();
			}
			foreach (KeyValuePair<string, ProfileConfig> item in dictionary)
			{
				_profiles[item.Key] = item.Value;
			}
			_currentProfile = text;
			RefreshProfileCombo();
			if (_profiles.TryGetValue(text, out ProfileConfig value2))
			{
				ApplyConfig(value2);
			}
			Log("Loaded:" + openFileDialog.FileName);
		}
		catch (Exception ex2)
		{
			LogError(ex2, "加载配置失败");
			ShowNotice("Load failed:" + ex2.Message, isError: true);
		}
	}

	private void RefreshProfileCombo()
	{
		ComboProfile.SelectionChanged -= ProfileChanged;
		ComboProfile.Items.Clear();
		foreach (string key in _profiles.Keys)
		{
			ComboProfile.Items.Add(key);
		}
		if (_profiles.ContainsKey(_currentProfile))
		{
			ComboProfile.SelectedItem = _currentProfile;
		}
		else if (ComboProfile.Items.Count > 0)
		{
			_currentProfile = ComboProfile.SelectedItem?.ToString() ?? "";
		}
		ComboProfile.SelectionChanged += ProfileChanged;
	}

	private void ProfileChanged(object s, SelectionChangedEventArgs e)
	{
		string text = ComboProfile.SelectedItem?.ToString();
		if (!string.IsNullOrEmpty(text) && _profiles.ContainsKey(text))
		{
			// 切换前把当前面板状态写回原方案, 避免未点保存的修改静默丢失
			if (_profiles.ContainsKey(_currentProfile))
			{
				try
				{
					_profiles[_currentProfile] = BuildConfig();
				}
				catch (Exception ex)
				{
					LogError(ex, "切换方案前保存当前配置失败, 保留原方案内容");
				}
			}
			_currentProfile = text;
			ApplyConfig(_profiles[text]);
			Log("Switched:" + text);
		}
	}

	private void SaveProfile_Click(object s, RoutedEventArgs e)
	{
		_profiles[_currentProfile] = BuildConfig();
		SaveConfigFile();
		Log("Saved:" + _currentProfile);
	}

	private void NewProfile_Click(object s, RoutedEventArgs e)
	{
		Window dlg = new Window
		{
			Title = "新建方案",
			Width = 350.0,
			SizeToContent = SizeToContent.Height,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Owner = this,
			Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 27, 34)),
			ResizeMode = ResizeMode.NoResize
		};
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(16.0)
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = "方案名称:",
			Foreground = System.Windows.Media.Brushes.White
		});
		System.Windows.Controls.TextBox textBox = new System.Windows.Controls.TextBox
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 16.0)
		};
		stackPanel.Children.Add(textBox);
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Right
		};
		System.Windows.Controls.Button button = new System.Windows.Controls.Button
		{
			Content = "确定",
			Width = 80.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
		};
		System.Windows.Controls.Button button2 = new System.Windows.Controls.Button
		{
			Content = "取消",
			Width = 80.0
		};
		button.Click += delegate
		{
			dlg.DialogResult = true;
			dlg.Close();
		};
		button2.Click += delegate
		{
			dlg.DialogResult = false;
			dlg.Close();
		};
		stackPanel2.Children.Add(button);
		stackPanel2.Children.Add(button2);
		stackPanel.Children.Add(stackPanel2);
		dlg.Content = stackPanel;
		if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(textBox.Text))
		{
			string text = textBox.Text.Trim();
			if (_profiles.ContainsKey(text))
			{
				ShowNotice("已存在同名方案");
				return;
			}
			_profiles[text] = BuildConfig();
			_currentProfile = text;
			RefreshProfileCombo();
			SaveConfigFile();
			Log("新建方案:" + text);
		}
	}

	private void RenameProfile_Click(object s, RoutedEventArgs e)
	{
		string currentProfile = _currentProfile;
		Window dlg = new Window
		{
			Title = "重命名方案",
			Width = 350.0,
			SizeToContent = SizeToContent.Height,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Owner = this,
			Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 27, 34)),
			ResizeMode = ResizeMode.NoResize
		};
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(16.0)
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = "将 \"" + currentProfile + "\" 重命名为:",
			Foreground = System.Windows.Media.Brushes.White
		});
		System.Windows.Controls.TextBox textBox = new System.Windows.Controls.TextBox
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 16.0),
			Text = currentProfile
		};
		stackPanel.Children.Add(textBox);
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Right
		};
		System.Windows.Controls.Button button = new System.Windows.Controls.Button
		{
			Content = "确定",
			Width = 80.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
		};
		System.Windows.Controls.Button button2 = new System.Windows.Controls.Button
		{
			Content = "取消",
			Width = 80.0
		};
		button.Click += delegate
		{
			dlg.DialogResult = true;
			dlg.Close();
		};
		button2.Click += delegate
		{
			dlg.DialogResult = false;
			dlg.Close();
		};
		stackPanel2.Children.Add(button);
		stackPanel2.Children.Add(button2);
		stackPanel.Children.Add(stackPanel2);
		dlg.Content = stackPanel;
		if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(textBox.Text))
		{
			return;
		}
		string text = textBox.Text.Trim();
		if (!(text == currentProfile))
		{
			if (_profiles.ContainsKey(text))
			{
				ShowNotice("已存在同名方案");
				return;
			}
			_profiles[text] = _profiles[currentProfile];
			_profiles.Remove(currentProfile);
			_currentProfile = text;
			RefreshProfileCombo();
			SaveConfigFile();
			Log("重命名方案: " + currentProfile + " → " + text);
		}
	}

	private void DeleteProfile_Click(object s, RoutedEventArgs e)
	{
		if (_profiles.Count <= 1)
		{
			ShowNotice("至少需要保留一个方案");
		}
		else if (System.Windows.MessageBox.Show("确定删除方案 \"" + _currentProfile + "\"？", "确认删除", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
		{
			string currentProfile = _currentProfile;
			_profiles.Remove(_currentProfile);
			_currentProfile = _profiles.Keys.First();
			RefreshProfileCombo();
			if (_profiles.TryGetValue(_currentProfile, out ProfileConfig value))
			{
				ApplyConfig(value);
			}
			SaveConfigFile();
			Log("已删除方案: " + currentProfile + ", 当前: " + _currentProfile);
		}
	}

	// ── 浏览器式选项卡：路径配置 / 参数配置 / 命令预览 ───────────────

	private void SwitchSection_Click(object sender, RoutedEventArgs e)
	{
		ShowSection((sender as System.Windows.Controls.Button)?.Tag as string ?? "path");
	}

	private void ShowSection(string which)
	{
		SecPath.Visibility = which == "path" ? Visibility.Visible : Visibility.Collapsed;
		SecParam.Visibility = which == "param" ? Visibility.Visible : Visibility.Collapsed;
		SecCmd.Visibility = which == "cmd" ? Visibility.Visible : Visibility.Collapsed;
		StyleTabButton(TabBtnPath, which == "path");
		StyleTabButton(TabBtnParam, which == "param");
		StyleTabButton(TabBtnCmd, which == "cmd");
	}

	private static void StyleTabButton(System.Windows.Controls.Button b, bool active)
	{
		b.Background = active ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 27, 34)) : System.Windows.Media.Brushes.Transparent;
		b.Foreground = active ? System.Windows.Media.Brushes.White : new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));
	}

	// ── Node 环境：傻瓜式安装/更新/配置（一键部署，供 DSH 启动/重建使用）───────────────

	private bool _nodeEnvBusy;

	private void SetNodeEnvBusy(bool busy)
	{
		_nodeEnvBusy = busy;
		BtnInstallNodeEnv.IsEnabled = !busy;
		BtnRefreshNodeEnv.IsEnabled = !busy;
	}

	private void NodeLogLine(string line)
	{
		Dispatcher.BeginInvoke((Action)(() =>
		{
			NodeLog.Text += "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine;
			// 与 LogArea 同款上限: pnpm install 输出可达上千行, 不裁剪会逐渐卡顿
			var nl = NodeLog.Text.Split('\n');
			if (nl.Length > 500)
			{
				NodeLog.Text = string.Join("\n", nl[^500..]);
			}
			NodeLog.CaretIndex = NodeLog.Text.Length;
		}));
	}

	private async void RefreshNodeEnv_Click(object sender, RoutedEventArgs e)
	{
		if (_nodeEnvBusy) return;
		SetNodeEnvBusy(true);
		try { await DoRefreshNodeStatus(); }
		catch (Exception ex)
		{
			// async void 漏掉 catch 会逃逸成 Dispatcher 未处理异常（弹崩溃框），这里必须兜住
			AppLog.WriteError("刷新 Node 状态失败: " + ex);
			NodeLogLine("状态刷新失败：" + ex.Message);
		}
		finally { SetNodeEnvBusy(false); }
	}

	private async Task RefreshNodeStatusAsync()
	{
		if (_nodeEnvBusy) return;
		SetNodeEnvBusy(true);
		try { await DoRefreshNodeStatus(); }
		catch (Exception ex) { LblNodeStatus.Text = "检测失败: " + ex.Message; }
		finally { SetNodeEnvBusy(false); }
	}

	private async Task DoRefreshNodeStatus()
	{
		var status = await Task.Run(() => NodeSetup.Detect());
		string latest = await NodeSetup.GetLatestV24Async();
		UpdateNodeLabels(status, latest);
	}

	private void UpdateNodeLabels(NodeSetup.NodeStatus st, string latest)
	{
		if (st.MeetsRequirement)
		{
			string extra = (latest != "" && st.Node24Version != latest) ? "  ·  ⬆ 可更新" : "";
			LblNodeStatus.Text = "✅ Node " + st.Node24Version + extra + " — " + st.Node24Exe;
		}
		else if (st.AnyNodeExe != null)
		{
			LblNodeStatus.Text = "⚠️ 当前 node " + st.AnyNodeVersion + " 不满足要求（DSH 需要 v24+）— " + st.AnyNodeExe;
		}
		else
		{
			LblNodeStatus.Text = "❌ 未找到 Node（DSH 启动/重建必需）";
		}
		if (st.PnpmExe != null)
		{
			LblPnpmStatus.Text = "✅ pnpm " + st.PnpmVersion + " — " + st.PnpmExe;
		}
		else
		{
			LblPnpmStatus.Text = "❌ 未找到 pnpm（插件安装/升级必需，可用下方按钮一并安装）";
		}
		if (latest != "")
		{
			bool upToDate = st.MeetsRequirement && st.Node24Version == latest;
			LblLatestStatus.Text = "最新 v24.x: " + latest + (upToDate ? "  ·  当前已是最新" : "");
		}
		else
		{
			LblLatestStatus.Text = "未能查询最新 v24.x 版本号（网络不可用或镜像不可达）";
		}
	}

	private async void InstallNodeEnv_Click(object sender, RoutedEventArgs e)
	{
		if (_nodeEnvBusy) return;
		SetNodeEnvBusy(true);
		NodeLog.Text = "";
		// 必须在 UI 线程取（EffectiveDshDir 会读控件），后台线程只消费字符串
		string dshDirForLinks = EffectiveDshDir();
		try
		{
			await Task.Run(async () =>
			{
				Action<string> log = s => NodeLogLine(s);
				log("── 开始安装/更新 Node 环境 ──");
				string latest = await NodeSetup.GetLatestV24Async();
				if (latest == "") throw new Exception("无法查询最新 v24.x 版本号：网络不可用或镜像不可达，请检查网络后重试。");

				var st = NodeSetup.Detect();
				// 版本探测失败(node.exe 存在但 node -v 无输出, 常见于安全软件拦截):
				// 视作"已安装未知版本"跳过重装, 避免每次点击都白跑一遍 30MB MSI
				bool versionUnknown = st.Node24Exe != null && string.IsNullOrEmpty(st.Node24Version);
				bool needNode = !st.MeetsRequirement || (st.Node24Version != latest && !versionUnknown);
				bool needPnpm = st.PnpmExe == null;
				if (versionUnknown)
				{
					log("⚠ Node " + st.Node24Exe + " 存在但版本探测失败（node -v 无输出）。跳过重装——若确需更新请先手动确认 node 可运行。");
				}

				if (!needNode && !needPnpm)
				{
					log("当前环境已是最新（node " + latest + " + pnpm），无需操作。");
					return;
				}

				string tempDir = Path.Combine(Path.GetTempPath(), "dsh_node_install");
				Directory.CreateDirectory(tempDir);

				if (needNode)
				{
					string currentDesc = st.Node24Exe != null ? st.Node24Version : (st.AnyNodeExe != null ? st.AnyNodeVersion + "（不满足要求）" : "未安装");
					log("目标版本: " + latest + "  当前: " + currentDesc);
					string? msi = await NodeSetup.DownloadMsiAsync(latest, tempDir, log);
					if (msi == null) throw new Exception("MSI 下载失败：所有源均不可达。也可手动到 https://nodejs.org 下载安装。");
					try
					{
						if (!NodeSetup.InstallMsi(msi, log, out bool needReboot)) throw new Exception("安装器未成功完成（见上方退出码）。");
						if (needReboot)
						{
							// 3010/1641：安装已成功但重启前 node 尚未生效，继续 Detect→InstallPnpm 只会必然失败，这里直接收尾（不是失败）。
							log("Node 已安装完成，但需要重启 Windows 后才能生效；请重启后再次点击【安装/更新 Node 环境】完成 pnpm 安装。");
							return;
						}
					}
					finally
					{
						// 安装结束后（成功/失败/超时）都清掉临时包，避免残留占空间
						try { File.Delete(msi); } catch { /* ignore */ }
					}
				}

				var st2 = NodeSetup.Detect();
				if (st2.PnpmExe == null)
				{
					if (!NodeSetup.InstallPnpm(log)) throw new Exception("pnpm 安装失败。也可在 node 目录手动运行 npm install -g pnpm。");
				}

				var st3 = NodeSetup.Detect();
				if (st3.MeetsRequirement && st3.PnpmExe != null)
				{
					log("✅ 完成: node " + st3.Node24Version + " + pnpm " + st3.PnpmVersion);
					log("PATH 已由安装器自动配置，新开的终端立即生效。");

					// Node 换版本/换机器后，DSH 的 node_modules 里 pnpm workspace 链接可能悬空
					// （绝对路径残留），顺手重连一次，避免用户点"启动 DSH"才发现 Cannot find package。
					if (!string.IsNullOrEmpty(dshDirForLinks))
					{
						try
						{
							log("检查 DSH 的 node_modules 链接…");
							(int Detected, int Relinked, int Removed, int Failed) link =
								DshCompatService.RepairBrokenLinksOnly(dshDirForLinks, _currentProfile);
							if (link.Detected == 0)
							{
								log("DSH node_modules 链接正常。");
							}
							else
							{
								log("失效链接 " + link.Detected + " 条 → 重连 " + link.Relinked + " / 清理 " + link.Removed
									+ (link.Failed > 0 ? " / 失败 " + link.Failed : ""));
							}
						}
						catch (Exception linkEx)
						{
							log("链接自检跳过：" + linkEx.Message);
						}
					}
				}
				else
				{
					throw new Exception("安装完成但校验未通过，请点“刷新状态”查看详情。");
				}
			});

			await DoRefreshNodeStatus();
		}
		catch (Exception ex)
		{
			NodeLogLine("❌ " + ex.Message);
			AppLog.WriteError("InstallNodeEnv_Click: " + ex);
			NodeLogLine("💡 可点【📋 复制诊断】把本次错误与环境信息复制出来。");
			ShowNotice("Node 环境安装失败：" + ex.Message, isError: true);
		}
		finally
		{
			SetNodeEnvBusy(false);
		}
	}

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
		AutoLoadConfig();
		// 目录探测可能涉及 WMI/磁盘扫描：先让窗口画出来，再在 UI 空闲时执行。
		Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (Action)EnsureDirsDetected);
		ClampToWorkArea();
		string text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "app.ico");
		if (File.Exists(text))
		{
			try
			{
				base.Icon = new BitmapImage(new Uri(text));
			}
			catch (Exception ex)
			{
				LogError(ex, "加载窗口图标失败");
			}
		}
		StartBackgroundUpdateCheck();
		// Node 环境状态：后台异步刷新，不阻塞启动。
		_ = RefreshNodeStatusAsync();
	}

	private void ClampToWorkArea()
	{
		try
		{
			// SystemParameters.WorkArea 已是 DIP(WPF 逻辑单位)，直接使用；
			// 旧实现误除以 DPI 缩放系数，导致缩放≠100% 的屏幕上工作区被算错、窗体超出屏幕
			Rect workArea = SystemParameters.WorkArea;
			if (base.Width > workArea.Width)
			{
				base.Width = workArea.Width;
			}
			if (base.Height > workArea.Height)
			{
				base.Height = workArea.Height;
			}
			if (base.Left < workArea.Left)
			{
				base.Left = workArea.Left;
			}
			if (base.Top < workArea.Top)
			{
				base.Top = workArea.Top;
			}
			if (base.Left + base.Width > workArea.Right)
			{
				base.Left = workArea.Right - base.Width;
			}
			if (base.Top + base.Height > workArea.Bottom)
			{
				base.Top = workArea.Bottom - base.Height;
			}
		}
		catch (Exception ex)
		{
			AppLog.WriteError("窗口钳制到工作区失败: " + ex.Message);
		}
	}

	private void Window_Closing(object sender, CancelEventArgs e)
	{
		if (_exiting)
		{
			return; // QuitApp/Shutdown 重入, 放行关闭
		}
		if (ChkMinimizeToTray.IsChecked == true && _trayIcon != null)
		{
			EnterTrayLightMode();
			Hide();
			_trayIcon.ShowBalloonTip(2000, "Llama 启动器", "程序已最小化到系统托盘", ToolTipIcon.Info);
			e.Cancel = true;
			return;
		}
		bool flag = _llamaProc != null && !_llamaProc.HasExited;
		bool flag2 = HasDsh();
		bool flag3 = true;
		if (flag | flag2)
		{
			string text = "检测到以下服务正在运行：\n\n";
			if (flag)
			{
				text += "  • Llama Server\n";
			}
			if (flag2)
			{
				text += "  • DSH Web\n";
			}
			text += "\n请选择操作：\n\n  是(Y) — 停止所有服务并退出\n  否(N) — 不停止服务直接退出\n  取消 — 返回程序";
			MessageBoxResult messageBoxResult = System.Windows.MessageBox.Show(text, "确认退出", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
			if (messageBoxResult == MessageBoxResult.Cancel)
			{
				e.Cancel = true;
				return;
			}
			flag3 = messageBoxResult == MessageBoxResult.Yes;
		}
		_exiting = true; // Shutdown 会再次触发 Closing, 入口守卫放行(此处先置位防重入二次弹窗)
		if (flag3)
		{
			// 记录退出前 pid, Stop 的异步杀树若未及完成, Shutdown 前同步补杀
			// (Shutdown 不等后台线程池, fire-and-forget 会让"停止并退出"落空)
			int? llamaPid = _llamaProc?.Id;
			int? dshPid = _dshTracker?.Pid ?? _dshProc?.Id;
			StopLlama();
			StopDsh();
			try
			{
				if (llamaPid.HasValue) ProcessHelper.KillProcessTree(llamaPid.Value);
			}
			catch { }
			try
			{
				if (dshPid.HasValue) ProcessHelper.KillProcessTree(dshPid.Value);
			}
			catch { }
			ClearStateFile();
		}
		else
		{
			SaveState();
		}
		StopPerf();
		FlushRunLog();
		try { _agentApi?.Stop(); } catch { }
		_trayIcon?.Dispose();
		_trayIcon = null; // 重入的托盘分支会对已 Dispose 的 NotifyIcon 调方法
		System.Windows.Application.Current.Shutdown();
	}

	// ═════════════════ Agent 调试/调用接口(127.0.0.1 + token; 内核见 Services/AgentApi.cs) ═════════════════
	// 端点命名对齐 Linux 侧 main.py ROUTES。所有涉 UI/控件的操作统一 marshal 回 UI 线程,
	// 带超时保护, 永不把异常抛回 HTTP 线程池。stdout/HTTP 输出均为可解析 JSON。

	private AgentApi? _agentApi;

	private void TryStartAgentApi()
	{
		try
		{
			AgentApi api = new AgentApi();
			api.RegisterGet("ping", delegate
			{
				return "{\"ok\":true,\"app\":\"llamadsh-launcher\",\"cli\":\"LlamaDSH_Launcher.exe cli help\"}";
			});
			api.RegisterGet("status", delegate { return AgentStatusJson(); });
			api.RegisterGet("monitor", delegate { return AgentMonitorJson(); });
			api.RegisterGet("logs", AgentLogsTail);
			api.RegisterGet("profiles", delegate { return AgentProfilesJson(); });
			api.RegisterGet("breaker", delegate { return AgentBreakerGet(); });
			api.RegisterGet("llama/raw", AgentLlamaRaw);
			api.RegisterPost("llama/start", delegate { return AgentLlamaStart(); });
			api.RegisterPost("llama/stop", delegate { return AgentLlamaStop(); });
			api.RegisterPost("dsh/start", delegate { return AgentDshStart(); });
			api.RegisterPost("dsh/stop", delegate { return AgentDshStop(); });
			api.RegisterPost("lightweight", AgentLightweight);
			api.RegisterPost("window", AgentWindowOp);
			api.RegisterPost("profiles/use", AgentProfileUse);
			api.RegisterPost("breaker", AgentBreakerSet);
			api.RegisterPost("logs/clear", delegate
			{
				lock (_runLogLock)
				{
					_runLog.Clear();
					_runLogCount = 0;
				}
				return AgentOk("运行日志缓冲已清空(LogArea 界面内容不动)");
			});
			api.RegisterPost("config/save", delegate
			{
				return AgentOp(delegate
				{
					_profiles[_currentProfile] = BuildConfig();
					SaveConfigFile();
					return AgentOk("当前界面配置已写回配置文件");
				});
			});
			api.RegisterPost("quit", AgentQuitOp);
			string? fail;
			if (api.Start(out fail))
			{
				_agentApi = api;
				Log("[Agent] 调试接口就绪: http://127.0.0.1:" + api.Port + "/api/status  (token 见 " + api.ConfigFile + ")");
			}
			else
			{
				Log("[Agent] 接口未启用: " + fail);
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "Agent 接口启动失败");
		}
	}

	private string AgentStatusJson()
	{
		Dictionary<string, object>? d = AgentUi(AgentBuildStatus, 10000);
		return d == null ? AgentErr("UI 线程 10s 内未响应(界面可能卡在弹窗)") : JsonSerializer.Serialize(d, AgentApi.JsonOpts);
	}

	private Dictionary<string, object> AgentBuildStatus()
	{
		bool llamaAlive = IsAlive(_llamaProc);
		int llamaPid = llamaAlive ? _llamaProc!.Id : (_llamaApi != null && _llamaApi.Online ? _llamaApi.ActivePort > 0 ? -_llamaApi.ActivePort : 0 : 0);
		int dshPid = _dshProc != null && IsAlive(_dshProc) ? _dshProc.Id : (_dshTracker != null && _dshTracker.Poll() == null ? _dshTracker.Pid : 0);
		ProfileConfig pc = _profiles.TryGetValue(_currentProfile, out ProfileConfig? pcv) && pcv != null ? pcv : new ProfileConfig();
		return new Dictionary<string, object>
		{
			["ok"] = true,
			["app"] = "LlamaDSH_Launcher",
			["version"] = (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?"),
			["pid"] = Environment.ProcessId,
			["uptime_s"] = Math.Round((DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds, 1),
			["ui"] = new Dictionary<string, object>
			{
				["window_visible"] = IsVisible,
				["minimized"] = WindowState == WindowState.Minimized,
				["tray_mode"] = _trayMode,
				["lightweight"] = _lightOn,
				["monitor_tab_visible"] = _monitorTabVisible,
				["exiting"] = _exiting
			},
			["llama"] = new Dictionary<string, object>
			{
				["running"] = llamaAlive || (_llamaApi != null && _llamaApi.Online),
				["pid"] = llamaPid,
				["api_online"] = _llamaApi != null && _llamaApi.Online,
				["api_port"] = _llamaApi?.ActivePort ?? 0,
				["cfg_port"] = GetLlamaPort(),
				["phase"] = _llamaApi?.Phase ?? "idle",
				["ctx_used"] = _llamaApi?.CtxUsed ?? -1,
				["ctx_total"] = _llamaApi?.CtxTotal ?? 0,
				["gen_tps"] = Math.Round(_llamaApi?.GenTps ?? 0.0, 2),
				["prompt_tps"] = Math.Round(_llamaApi?.PromptTps ?? 0.0, 2),
				["model"] = _llamaApi?.Model?.Name ?? "",
				["last_task"] = _llamaApi?.LastTask,
				["last_cmd"] = _lastCmd
			},
			["dsh"] = new Dictionary<string, object>
			{
				["running"] = HasDsh(),
				["pid"] = dshPid,
				["dir"] = (InputDshDir?.Text ?? "").Trim()
			},
			["vram_breaker"] = new Dictionary<string, object>
			{
				["enabled"] = ChkVramBreaker?.IsChecked == true,
				["type"] = ComboVramBreakerType?.SelectedIndex == 1 ? "gb" : "pct",
				["value"] = double.TryParse(TxtVramBreakerVal?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double thr) ? thr : 0.0,
				["triggered"] = _vramBreakerTriggered
			},
			["profile"] = new Dictionary<string, object>
			{
				["current"] = _currentProfile,
				["model_path"] = pc.ModelPath,
				["mtp_path"] = pc.MtpPath,
				["llama_dir"] = pc.LlamaDir,
				["dsh_dir"] = pc.DshDir,
				["active_variant"] = pc.ActiveLlamaVariant,
				["variants"] = pc.LlamaVariants
			},
			["gpus"] = _lastPerfGpus ?? new List<GpuDevInfo>(),
			["system"] = _lastPerfDict ?? new Dictionary<string, string>(),
			["sampled_at_utc"] = _perfSampledAtUtc == default ? "" : _perfSampledAtUtc.ToString("O"),
			["paths"] = new Dictionary<string, object>
			{
				["config_dir"] = _configDir,
				["log_dir"] = AppLog.LogDir,
				["state_file"] = _stateFile,
				["agent_config"] = _agentApi?.ConfigFile ?? ""
			},
			["agent_port"] = _agentApi?.Port ?? 0
		};
	}

	private string AgentMonitorJson()
	{
		Dictionary<string, object>? d = AgentUi(delegate
		{
			LlamaApiMonitor? api = _llamaApi;
			return new Dictionary<string, object>
			{
				["ok"] = true,
				["sampled_at_utc"] = _perfSampledAtUtc == default ? "" : _perfSampledAtUtc.ToString("O"),
				["age_s"] = _perfSampledAtUtc == default ? -1.0 : Math.Round((DateTime.UtcNow - _perfSampledAtUtc).TotalSeconds, 1),
				["lightweight"] = _lightOn,
				["system"] = _lastPerfDict ?? new Dictionary<string, string>(),
				["gpus"] = _lastPerfGpus ?? new List<GpuDevInfo>(),
				["llama"] = new Dictionary<string, object>
				{
					["online"] = api != null && api.Online,
					["port"] = api?.ActivePort ?? 0,
					["phase"] = api?.Phase ?? "idle",
					["ctx_used"] = api?.CtxUsed ?? -1,
					["ctx_total"] = api?.CtxTotal ?? 0,
					["gen_tps"] = api?.GenTps ?? 0.0,
					["prompt_tps"] = api?.PromptTps ?? 0.0,
					["mtp_acceptance"] = api?.MtpAcceptance,
					["boot"] = api?.Boot,
					["model"] = api?.Model,
					["slots"] = api?.SlotsSnapshot(),
					["total_events"] = api?.TotalEvents ?? 0
				},
				["hist"] = new Dictionary<string, object>
				{
					["tps"] = _hTps.Values,
					["pf"] = _hPf.Values,
					["ctx"] = _hCtx.Values,
					["mtp"] = _hMtp.Values,
					["gpu_util"] = _hGpuUtil.Values,
					["gpu_mem"] = _hGpuMem.Values,
					["temp"] = _hTemp.Values,
					["power"] = _hPower.Values,
					["cpu"] = _hCpu.Values,
					["ram"] = _hRam.Values,
					["net"] = _hNet.Values,
					["net_tx"] = _hNetTx.Values
				},
				["hist_ticks"] = _hTps.Ticks
			};
		}, 10000);
		return d == null ? AgentErr("UI 线程 10s 内未响应") : JsonSerializer.Serialize(d, AgentApi.JsonOpts);
	}

	private string AgentLogsTail(string query)
	{
		int n = 200;
		Match m = Regex.Match(query ?? "", "n=(\\d+)");
		if (m.Success && int.TryParse(m.Groups[1].Value, out int mn) && mn >= 1 && mn <= 2000)
		{
			n = mn;
		}
		string joined;
		lock (_runLogLock)
		{
			joined = _runLog.ToString();
		}
		string[] all = joined.Split('\n');
		List<string> tail = new List<string>();
		for (int i = Math.Max(0, all.Length - n); i < all.Length; i++)
		{
			string ln = all[i].TrimEnd('\r');
			if (ln.Length > 0)
			{
				tail.Add(ln);
			}
		}
		return JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = true, ["count"] = tail.Count, ["lines"] = tail }, AgentApi.JsonOpts);
	}

	private string AgentProfilesJson()
	{
		Dictionary<string, object>? d = AgentUi(delegate
		{
			return new Dictionary<string, object>
			{
				["ok"] = true,
				["current"] = _currentProfile,
				["names"] = new List<string>(_profiles.Keys),
				["configs"] = _profiles
			};
		}, 10000);
		return d == null ? AgentErr("UI 线程 10s 内未响应") : JsonSerializer.Serialize(d, AgentApi.JsonOpts);
	}

	private string AgentBreakerGet()
	{
		Dictionary<string, object>? d = AgentUi(delegate
		{
			return new Dictionary<string, object>
			{
				["ok"] = true,
				["enabled"] = ChkVramBreaker?.IsChecked == true,
				["type"] = ComboVramBreakerType?.SelectedIndex == 1 ? "gb" : "pct",
				["value"] = double.TryParse(TxtVramBreakerVal?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double thr) ? thr : 0.0,
				["triggered"] = _vramBreakerTriggered
			};
		}, 10000);
		return d == null ? AgentErr("UI 线程 10s 内未响应") : JsonSerializer.Serialize(d, AgentApi.JsonOpts);
	}

	private string AgentBreakerSet(string body)
	{
		bool? enabled = null;
		string? type = null;
		double? value = null;
		try
		{
			using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
			if (doc.RootElement.TryGetProperty("enabled", out JsonElement e) && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False))
			{
				enabled = e.GetBoolean();
			}
			if (doc.RootElement.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String)
			{
				type = t.GetString();
			}
			if (doc.RootElement.TryGetProperty("value", out JsonElement v) && v.TryGetDouble(out double dv))
			{
				value = dv;
			}
		}
		catch (Exception ex)
		{
			return AgentErr("JSON 解析失败: " + ex.Message);
		}
		if (enabled == null && type == null && value == null)
		{
			return AgentErr("至少给一个字段: {\"enabled\":true|false,\"type\":\"pct|gb\",\"value\":90.0}");
		}
		return AgentOp(delegate
		{
			if (type != null)
			{
				if (type.ToLowerInvariant() is not ("pct" or "percent" or "gb"))
				{
					return AgentErr("type 仅支持 pct|gb");
				}
				ComboVramBreakerType.SelectedIndex = type.ToLowerInvariant() == "gb" ? 1 : 0;
			}
			if (value.HasValue)
			{
				if (value.Value <= 0.0)
				{
					return AgentErr("阈值需为正数");
				}
				TxtVramBreakerVal.Text = value.Value.ToString(CultureInfo.InvariantCulture);
			}
			if (enabled.HasValue)
			{
				ChkVramBreaker.IsChecked = enabled.Value;
			}
			SyncBreakerProbeCache();
			return JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["ok"] = true,
				["enabled"] = ChkVramBreaker?.IsChecked == true,
				["type"] = ComboVramBreakerType?.SelectedIndex == 1 ? "gb" : "pct",
				["value"] = double.TryParse(TxtVramBreakerVal?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double thr) ? thr : 0.0,
				["note"] = "已即时生效(熔断判定用新值); 要长期保存请再调 config/save"
			}, AgentApi.JsonOpts);
		});
	}

	private string AgentLlamaRaw(string query)
	{
		Match m = Regex.Match(query ?? "", "path=(\\w+)");
		string sub = m.Success ? m.Groups[1].Value.ToLowerInvariant() : "slots";
		if (sub is not ("slots" or "props" or "health"))
		{
			return AgentErr("path 仅支持 slots|props|health");
		}
		int port = _llamaApi?.ActivePort ?? 0;
		if (port <= 0)
		{
			return AgentErr("llama API 当前不在线(无活动端口)");
		}
		try
		{
			using System.Net.Http.HttpClient hc = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
			string raw = hc.GetStringAsync("http://127.0.0.1:" + port + "/" + sub).GetAwaiter().GetResult();
			object data;
			try
			{
				using JsonDocument pd = JsonDocument.Parse(raw);
				data = pd.RootElement.Clone();
			}
			catch
			{
				data = raw;
			}
			return JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = true, ["port"] = port, ["path"] = "/" + sub, ["data"] = data }, AgentApi.JsonOpts);
		}
		catch (Exception ex)
		{
			return AgentErr("llama /" + sub + " 不可达: " + ex.Message);
		}
	}

	private string AgentLlamaStart()
	{
		return AgentOp(delegate
		{
			if (IsAlive(_llamaProc))
			{
				return AgentErr("Llama 已在运行");
			}
			int prePort;
			try { prePort = GetLlamaPort(); } catch { prePort = 1234; }
			if (ProcessHelper.FindProcessByPort(prePort).HasValue)
			{
				return AgentErr("端口 " + prePort + " 已被占用(遗留 llama-server?), 先处理再启动");
			}
			string? preModel = ResolveModelPath(throwOnError: false);
			if (!string.IsNullOrEmpty(preModel) && !preModel.StartsWith("http", StringComparison.OrdinalIgnoreCase)
				&& !File.Exists(preModel) && !Directory.Exists(preModel))
			{
				return AgentErr("模型路径不存在: " + preModel);
			}
			StartLlama();
			return AgentOk("启动指令已下发; 用 cli status/monitor 跟踪 phase 与端口。注意: safetensors 分片会弹转换确认框, agent 场景请用 GGUF 路径");
		}, 60000);
	}

	private string AgentLlamaStop()
	{
		return AgentOp(delegate
		{
			if (!IsAlive(_llamaProc))
			{
				return AgentErr("Llama 未在运行");
			}
			StopLlama();
			return AgentOk("已停止");
		}, 60000);
	}

	private string AgentDshStart()
	{
		return AgentOp(delegate
		{
			if (HasDsh())
			{
				return AgentErr("DSH 已在运行");
			}
			StartDsh();
			return AgentOk("启动指令已下发; 用 cli status 跟踪 pid");
		}, 60000);
	}

	private string AgentDshStop()
	{
		return AgentOp(delegate
		{
			if (!HasDsh())
			{
				return AgentErr("DSH 未在运行");
			}
			StopDsh();
			return AgentOk("已停止");
		}, 60000);
	}

	private string AgentLightweight(string body)
	{
		bool? on = null;
		try
		{
			using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
			if (doc.RootElement.TryGetProperty("on", out JsonElement v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
			{
				on = v.GetBoolean();
			}
		}
		catch (Exception ex)
		{
			return AgentErr("JSON 解析失败: " + ex.Message);
		}
		return AgentOp(delegate
		{
			_lightOn = on ?? !_lightOn; // 不带 on 字段 = toggle
			SyncTrayLightItem();
			_perfPulse = true;
			return JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["ok"] = true,
				["lightweight"] = _lightOn,
				["tray_mode"] = _trayMode
			}, AgentApi.JsonOpts);
		});
	}

	private string AgentWindowOp(string body)
	{
		string? action = AgentBodyStr(body, "action");
		return AgentOp(delegate
		{
			switch (action)
			{
				case "show":
					ShowFromTray();
					break;
				case "hide":
					if (_trayIcon == null)
					{
						return AgentErr("无托盘图标, 不能隐藏到托盘");
					}
					EnterTrayLightMode();
					Hide();
					break;
				case "minimize":
					WindowState = WindowState.Minimized;
					break;
				default:
					return AgentErr("action 仅支持 show|hide|minimize");
			}
			return JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["ok"] = true,
				["window_visible"] = IsVisible,
				["minimized"] = WindowState == WindowState.Minimized,
				["tray_mode"] = _trayMode
			}, AgentApi.JsonOpts);
		});
	}

	private string AgentProfileUse(string body)
	{
		string? name = AgentBodyStr(body, "name");
		if (string.IsNullOrEmpty(name))
		{
			return AgentErr("缺 {\"name\":\"方案名\"}");
		}
		return AgentOp(delegate
		{
			if (!_profiles.ContainsKey(name))
			{
				return AgentErr("方案不存在: " + name + "; 可用: " + string.Join(", ", _profiles.Keys));
			}
			// 走 ComboBox 通道 = ProfileChanged 完整流程(先写回旧方案面板, 再应用新方案)
			ComboProfile.SelectedItem = name;
			return AgentOk("已切换到方案: " + name);
		});
	}

	private string AgentQuitOp(string body)
	{
		bool stop = true;
		try
		{
			using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
			if (doc.RootElement.TryGetProperty("stop_services", out JsonElement v))
			{
				stop = v.ValueKind == JsonValueKind.True;
			}
		}
		catch
		{
		}
		// Background 优先级: 先让本次 HTTP 响应发出去, 再在 UI 线程收尾退出
		Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			try { if (stop && IsAlive(_llamaProc)) { StopLlama(); } } catch { }
			try { if (stop && HasDsh()) { StopDsh(); } } catch { }
			_exiting = true;
			System.Windows.Application.Current?.Shutdown();
		}, System.Windows.Threading.DispatcherPriority.Background);
		return AgentOk("正在退出(stop_services=" + (stop ? "true" : "false") + ")");
	}

	/// <summary>操作 marshal 到 UI 线程执行(带超时/异常兜底): HTTP 线程永远拿到 JSON。</summary>
	private string AgentOp(Func<string> op, int timeoutMs = 30000)
	{
		try
		{
			System.Windows.Threading.DispatcherOperation<string> dop = Dispatcher.InvokeAsync<string>(op);
			if (!dop.Task.Wait(timeoutMs))
			{
				return AgentErr("UI 操作 " + (timeoutMs / 1000) + "s 未返回(可能在等弹窗确认; 操作可能仍在进行)");
			}
			return dop.Result ?? AgentErr("操作返回空");
		}
		catch (AggregateException aex)
		{
			return AgentErr(aex.GetBaseException().Message);
		}
		catch (Exception ex)
		{
			return AgentErr(ex.Message);
		}
	}

	/// <summary>只读快照 marshal 到 UI 线程; 超时返回 default 而不是卡死调用方。</summary>
	private T? AgentUi<T>(Func<T> fn, int timeoutMs = 10000)
	{
		try
		{
			System.Windows.Threading.DispatcherOperation<T> dop = Dispatcher.InvokeAsync<T>(fn);
			if (!dop.Task.Wait(timeoutMs))
			{
				return default;
			}
			return dop.Result;
		}
		catch (Exception ex)
		{
			LogError(ex, "Agent UI 快照失败");
			return default;
		}
	}

	private static string? AgentBodyStr(string body, string prop)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
			if (doc.RootElement.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String)
			{
				return v.GetString();
			}
		}
		catch
		{
		}
		return null;
	}

	private static string AgentOk(string? msg = null)
	{
		Dictionary<string, object> d = new Dictionary<string, object> { ["ok"] = true };
		if (msg != null)
		{
			d["msg"] = msg;
		}
		return JsonSerializer.Serialize(d, AgentApi.JsonOpts);
	}

	private static string AgentErr(string msg)
	{
		return JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = false, ["error"] = msg }, AgentApi.JsonOpts);
	}

}
