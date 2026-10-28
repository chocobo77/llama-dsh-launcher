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

	private PerformanceCounter? _memCounter;

	private readonly ConcurrentQueue<double> _tpsSamples;

	private string _lastLogLine;

	private int _dshHiddenCount;

	private int _dshRetriesLeft;

	private string? _dshBootLogPath;

	private CancellationTokenSource? _dshTailCts;

	// 最近一次 AutoFill 提示集合（用于去重，避免击键刷屏）
	private string? _lastAutoFillHints;

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

	private readonly ConcurrentQueue<double> _ttftSamples;

	private readonly ConcurrentQueue<double> _pfSamples;

	private string _perfLineBuf;

	private static readonly HttpClient _http = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(2.0)
	};

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

	/// <summary>子组内容面板缓存（key = 组id + 子组名），导航切换时挂到 ParamContent。</summary>
	private readonly Dictionary<string, FrameworkElement> _subPanels = new();

	/// <summary>左侧导航条目（用于选中态与过滤徽章更新）。</summary>
	private readonly List<(Border Item, TextBlock Title, TextBlock Badge, string GrpId, string GrpTitle, string Sub, int Count)> _navItems = new();

	private (string GrpId, string Sub)? _currentSub;

	private static readonly Regex _rePromptTps = new Regex("prompt eval time\\s+=\\s*([\\d.]+)\\s*ms\\s*/\\s*(\\d+)\\s*tokens", RegexOptions.Compiled);

	private static readonly Regex _rePromptMs = new Regex("prompt eval time\\s+=\\s*([\\d.]+)\\s*ms", RegexOptions.Compiled);

	private static readonly Regex _reEvalTps = new Regex("(?<!prompt )eval time\\s+=\\s*[\\d.]+\\s*ms\\s*/\\s*\\d+\\s*(?:new tokens|tokens|runs)\\s*\\([^)]*?([\\d.]+)\\s*(?:tokens? per second|tok/s)", RegexOptions.Compiled);

	private static readonly Regex _reEvalRaw = new Regex("(?<!prompt )eval time\\s+=\\s*([\\d.]+)\\s*ms\\s*/\\s*(\\d+)\\s*(?:new tokens|tokens|runs)", RegexOptions.Compiled);
	// Strata 请求表/对话缓存: 前缀缓存复用 + 请求总耗时
	private static readonly Regex _rePromptCached = new Regex("prompt processed \\(cached\\):\\s*(\\d+)\\s*tokens", RegexOptions.Compiled);
	private static readonly Regex _reTotalTime = new Regex("total time\\s+=\\s*([\\d.]+)\\s*ms", RegexOptions.Compiled);

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
		_tpsSamples = new ConcurrentQueue<double>();
		_lastLogLine = "";
		_ttftSamples = new ConcurrentQueue<double>();
		_pfSamples = new ConcurrentQueue<double>();
		_perfLineBuf = "";
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
		RefreshCmd();
		StartPerfMonitor();
		LoadState();
		_healthTimer.Tick += HealthCheck;
		_healthTimer.Start();
		_logTimer.Tick += delegate
		{
			FlushLogs();
		};
		_logTimer.Start();
		base.IsVisibleChanged += new DependencyPropertyChangedEventHandler(OnVisibilityChanged);
	}

	private void BuildParamGroups()
	{
		_paramRows.Clear();
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
			// 左侧导航: 组标题
			ParamNavPanel.Children.Add(new System.Windows.Controls.TextBlock
			{
				Text = g.Title,
				Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158)),
				FontSize = 11.0,
				FontWeight = FontWeights.Bold,
				Margin = new Thickness(4, 10, 0, 3)
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
		DockPanel.SetDock(badge, Dock.Right);
		DockPanel dp = new DockPanel();
		dp.Children.Add(badge);
		dp.Children.Add(title);
		Border item = new Border
		{
			Child = dp,
			CornerRadius = new CornerRadius(4),
			Padding = new Thickness(9, 5, 8, 5),
			Margin = new Thickness(0, 1, 0, 1),
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
		ParamBreadcrumb.Text = (grpTitle ?? grpId) + "  ▸  " + sub + "   ·   " + count + " 个参数";
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
		contextMenuStrip.Items.Add("退出程序", null, delegate
		{
			QuitApp();
		});
		_trayIcon.ContextMenuStrip = contextMenuStrip;
	}

	private void ShowFromTray()
	{
		Show();
		base.WindowState = WindowState.Normal;
		Activate();
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
		_trayIcon?.Dispose();
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
			SetLlamaUpdBtn(true, "⏳ 检查中...");
			int? local = await Task.Run(() => UpdateService.GetLocalLlamaBuild(dir));
			Services.UpdateService.LlamaReleaseInfo? latest = await Task.Run(() => UpdateService.FetchLatestLlamaRelease());
			SetLlamaUpdBtn(false);
			if (latest == null)
			{
				if (fromButton)
				{
					ShowNotice("无法获取 llama.cpp 最新版本（网络/GitHub 不可达）。", isError: true);
				}
				return;
			}
			if (local.HasValue && latest.Build <= local.Value)
			{
				string upstream = string.IsNullOrEmpty(latest.StableVersion)
					? latest.Tag + "（滚动构建）"
					: latest.Tag + "（" + latest.StableVersion + " 稳定版）";
				Log("[更新] llama.cpp 已是最新: build " + local.Value + "（上游 " + upstream + "）");
				if (fromButton)
				{
					ShowNotice("llama.cpp 已是最新版本。\n当前: build " + local.Value + "\n上游: " + upstream);
				}
				return;
			}
			string current = local.HasValue ? "build " + local.Value : "未知";
			string latestDesc = string.IsNullOrEmpty(latest.StableVersion)
				? latest.Tag + "（滚动构建, build " + latest.Build + "）"
				: latest.Tag + "（" + latest.StableVersion + " 稳定版, build " + latest.Build + "）";
			string msg = "发现 llama.cpp 新版本:\n\n"
				+ "  当前: " + current + "\n"
				+ "  上游: " + latestDesc + "\n"
				+ "  预编译包: " + latest.AssetName + "\n\n"
				+ "将下载并覆盖更新到:\n  " + dir + "\n\n"
				+ "（仅更新二进制，不触碰模型；若上游参数有变，更新后点“⟳ 重载参数配置”即可）";
			if (System.Windows.MessageBox.Show(msg, "更新 llama.cpp", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
			{
				Log("[更新] 用户取消 llama.cpp 更新");
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
			AutoFill(InputModelPath.Text);
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
			if (value.Item2 is System.Windows.Controls.TextBox textBox && textBox.Text != text)
			{
				textBox.Text = text;
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
				Array.Sort(firstShards, StringComparer.OrdinalIgnoreCase);
				return firstShards[0];
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
		foreach (string shard in GgufShardPaths(modelPath))
		{
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
					r.Ok = true;
				}
			}
			catch
			{
				// 单分片解析失败继续其余分片; 全部失败时 Ok=false 走近似回退
			}
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
			string kvNote = "";
			if (TryGetParamValue("flash-attn", out string faStr) && faStr.Trim() == "off" && cv != "f16")
			{
				cv = "f16";
				kvNote = "（FA 关闭，V 量化回退 f16）";
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

	private static string FmtCmd(List<string> cl)
	{
		if (cl.Count == 0)
		{
			return "";
		}
		List<string> list = new List<string> { QA(cl[0]) };
		int num = 1;
		while (num < cl.Count)
		{
			string text = cl[num];
			string text2 = QA(text);
			if (text.StartsWith("--") && num + 1 < cl.Count && !cl[num + 1].StartsWith("--"))
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
		if (a.Contains(' ') || a.Contains('"'))
		{
			return "\"" + a.Replace("\"", "\\\"") + "\"";
		}
		return a;
	}

	private void RefreshCmd()
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
		}
		catch (Exception ex)
		{
			_lastCmd = "";
			CmdPreview.Text = "Error:" + ex.Message;
			CmdCustomPreview.Text = "";
			CmdCustomPreview.Visibility = Visibility.Collapsed;
			LogError(ex, "命令构建失败");
		}
	}

	private void RefreshCmd_Click(object s, RoutedEventArgs e)
	{
		RefreshCmd();
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
		RefreshCmd();
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
		RefreshCmd();
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

	/// <summary>按 Windows 命令行习惯拆分：空格分隔，支持单/双引号；反斜杠只在引号/反斜杠前作为转义。</summary>
	private static List<string> SplitCommandLine(string text)
	{
		List<string> result = new List<string>();
		StringBuilder current = new StringBuilder();
		bool inQuote = false;
		char quoteChar = '\0';
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (c == '\\' && i + 1 < text.Length && (text[i + 1] == '"' || text[i + 1] == '\\'))
			{
				current.Append(text[i + 1]);
				i++;
				continue;
			}
			if (inQuote)
			{
				if (c == quoteChar)
				{
					inQuote = false;
				}
				else
				{
					current.Append(c);
				}
				continue;
			}
			if (c == '"' || c == '\'')
			{
				inQuote = true;
				quoteChar = c;
				continue;
			}
			if (char.IsWhiteSpace(c))
			{
				if (current.Length > 0)
				{
					result.Add(current.ToString());
					current.Clear();
				}
				continue;
			}
			current.Append(c);
		}
		if (current.Length > 0)
		{
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
			(string exe, string pre)? detected = DetectPython();
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
        string logPath = Path.Combine(dir, "llamadsh_convert.log");
        string cmdPath = Path.Combine(Path.GetTempPath(), "llamadsh_convert.cmd");
        try
        {
            File.WriteAllText(logPath, "");
        string pyCmd = Q + pyExe + Q + " " + ((pyPre.Length > 0) ? (pyPre + " ") : "") + Q + script + Q + " " + Q + dir + Q + " --outfile " + Q + outFile + Q + " --outtype " + outtype;
        string cmdText = "@echo off" + Environment.NewLine + "cd /d " + Q + dir + Q + Environment.NewLine + pyCmd + " >> " + Q + logPath + Q + " 2>&1" + Environment.NewLine + "echo EXITCODE:%errorlevel% >> " + Q + logPath + Q;
        File.WriteAllText(cmdPath, cmdText);
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
                        using (StreamReader sr = new StreamReader(fs))
                        {
                            string text = sr.ReadToEnd();
                            int num = text.LastIndexOf((char)10);
                            if (num >= 0)
                            {
                                string done = text.Substring(0, num + 1);
                                _convertLogPos += Encoding.UTF8.GetByteCount(done);
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
		_vramBreakerTriggered = false;
		_oomSeenInRun = false;
		if (!_oomRetryActive)
		{
			_oomRetryLeft = 3; // 用户手动启动时重置重试额度
		}
		double result;
		while (_tpsSamples.TryDequeue(out result))
		{
		}
		while (_ttftSamples.TryDequeue(out result))
		{
		}
		while (_pfSamples.TryDequeue(out result))
		{
		}
		_perfLineBuf = "";
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			PerfPrefill.Text = "—";
			PerfTps.Text = "—";
			PerfTtft.Text = "—";
			ResetMonitorCards();
			MonCacheLast.Text = "—";
			MonCacheTotal.Text = "0 tok / 0 次请求";
			RefreshReqList();
		}, Array.Empty<object>());
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
				// OOM 降层重试成功进入运行态: 重置额度与标志
				_oomRetryActive = false;
				_oomRetryLeft = 3;
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
			}
			else
			{
				LblLlamaStatus.Text = "● 已停止";
				LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));
				Log("--- Llama 已退出 ---");
			}
			ClearStateFor("llama");
			_oomRetryLeft = 3;
			_oomRetryActive = false;
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
				// auto/全层: 以上一轮 offloaded 总层数为基准
				baseNgl = _totalLayers > 0 ? _totalLayers : 0;
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
			ProcessHelper.KillProcessTree(pid.Value);
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
	private void RebuildDsh_Click(object s, RoutedEventArgs e)
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
			DateTime stopDeadline = DateTime.Now.AddSeconds(20.0);
			while ((HasDsh() || ProcessHelper.FindProcessByPort(3080).HasValue) && DateTime.Now < stopDeadline)
			{
				System.Threading.Thread.Sleep(500);
			}
			if (HasDsh() || ProcessHelper.FindProcessByPort(3080).HasValue)
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
			string text = Path.Combine(Path.GetTempPath(), "LlamaDSH_GameAccessProbe.ps1");
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
			ProcessHelper.KillProcessTree(_dshProc.Id);
		}
		if (_dshTracker != null)
		{
			try
			{
				ProcessHelper.KillProcessTree(_dshTracker.Pid);
			}
			catch (Exception ex)
			{
				LogError(ex, "停止 DSH 进程失败");
			}
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
		string lower = content.ToLowerInvariant();
		if (lower.Contains("dsh web") || lower.Contains("http") || lower.Contains("127.0.0.1") ||
			lower.Contains("localhost") || lower.Contains("listen") || lower.Contains("port") ||
			lower.Contains("eaddrinuse") || lower.Contains("error") || lower.Contains("fail") ||
			lower.Contains("exception") || lower.Contains("warn") || lower.Contains("started") ||
			lower.Contains("ready") || lower.Contains("loaded") || lower.Contains("plugin") ||
			lower.Contains("exit") || lower.Contains("browser") || lower.Contains("open") ||
			lower.Contains("server") || lower.Contains("version") || lower.Contains("status") ||
			lower.Contains("web"))
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
		if (text.IndexOf("eval time", StringComparison.Ordinal) < 0
			&& text.IndexOf("total time", StringComparison.Ordinal) < 0
			&& text.IndexOf("prompt processed", StringComparison.Ordinal) < 0)
		{
			return;
		}
		if (_perfLineBuf.Length > 0)
		{
			text = _perfLineBuf + text;
			_perfLineBuf = "";
		}
		if ((text.Contains("prompt eval time") || text.Contains("eval time")) && text.Contains("(") && !text.Contains(")"))
		{
			_perfLineBuf = text;
			return;
		}
		string[] array = text.Split('\n');
		foreach (string input in array)
		{
			Match match = _rePromptTps.Match(input);
			if (match.Success)
			{
				double num = double.Parse(match.Groups[1].Value);
				int num2 = int.Parse(match.Groups[2].Value);
				AddS(_ttftSamples, num, 20);
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Func<string>)(() => PerfTtft.Text = $"{_ttftSamples.ToArray().Average() / 1000.0:F2} s"), Array.Empty<object>());
				if (num > 0.0 && num2 > 0)
				{
					AddS(_pfSamples, (double)num2 / (num / 1000.0), 20);
					((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Func<string>)(() => PerfPrefill.Text = FT(_pfSamples.ToArray().Average()) + " tok/s"), Array.Empty<object>());
				}
				// 新请求的 prompt 行出现而上次请求尚未收尾(无 total time 行)时先落账
				if (_curOutToks > 0)
				{
					FinalizeRequest();
				}
				_curPromptToks = num2;
			}
			else
			{
				Match match2 = _rePromptMs.Match(input);
				if (match2.Success)
				{
					AddS(_ttftSamples, double.Parse(match2.Groups[1].Value), 20);
					((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Func<string>)(() => PerfTtft.Text = $"{_ttftSamples.ToArray().Average() / 1000.0:F2} s"), Array.Empty<object>());
				}
			}
			Match matchC = _rePromptCached.Match(input);
			if (matchC.Success)
			{
				_curReused = int.Parse(matchC.Groups[1].Value);
			}
			Match match3 = _reEvalTps.Match(input);
			if (match3.Success)
			{
				_curTps = double.Parse(match3.Groups[1].Value);
				AddS(_tpsSamples, _curTps, 20);
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Func<string>)(() => PerfTps.Text = FT(_tpsSamples.ToArray().Average()) + " tok/s"), Array.Empty<object>());
				// 同一行还带 ms/输出 token 数(供请求表), 不能因 continue 漏采
				Match m3raw = _reEvalRaw.Match(input);
				if (m3raw.Success)
				{
					_curEvalMs = double.Parse(m3raw.Groups[1].Value);
					_curOutToks = int.Parse(m3raw.Groups[2].Value);
				}
				continue;
			}
			Match match4 = _reEvalRaw.Match(input);
			if (!match4.Success)
			{
				Match matchT = _reTotalTime.Match(input);
				if (matchT.Success)
				{
					_curEvalMs = double.Parse(matchT.Groups[1].Value);
					FinalizeRequest();
				}
				continue;
			}
			double num3 = double.Parse(match4.Groups[1].Value);
			int num4 = int.Parse(match4.Groups[2].Value);
			_curOutToks = num4;
			if (num3 > 0.0)
			{
				AddS(_tpsSamples, (double)num4 / (num3 / 1000.0), 20);
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Func<string>)(() => PerfTps.Text = FT(_tpsSamples.ToArray().Average()) + " tok/s"), Array.Empty<object>());
			}
		}
	}

	private static void AddS(ConcurrentQueue<double> q, double v, int m)
	{
		q.Enqueue(v);
		while (q.Count > m)
		{
			q.TryDequeue(out var _);
		}
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
				_memCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use");
				_memCounter.NextValue();
			}
			catch (Exception ex2)
			{
				LogError(ex2, "内存性能计数器初始化失败");
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
			int num = 0;
			while (!_perfCts.Token.IsCancellationRequested)
			{
				try
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
						if (_memCounter != null)
						{
							d["mem"] = _memCounter.NextValue().ToString("F1");
						}
					}
					catch (Exception ex4)
					{
						d["mem_err"] = ex4.Message;
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
					// llama-server /metrics 扩展指标: KV 缓存占用 / 处理中 / 排队中（沿用守卫: 存活且日志样本为空）
					if (IsAlive(_llamaProc) && _tpsSamples.IsEmpty && _ttftSamples.IsEmpty)
					{
						if (FetchLlamaMetrics() is (double mTps, double mPf))
						{
							d["httptps"] = mTps.ToString("F2", CultureInfo.InvariantCulture);
							d["httppf"] = mPf.ToString("F2", CultureInfo.InvariantCulture);
						}
						d["req_proc"] = _llamaReqProc.ToString(CultureInfo.InvariantCulture);
						d["req_wait"] = _llamaReqWait.ToString(CultureInfo.InvariantCulture);
						if (_llamaCtxUsed >= 0.0)
						{
							d["ctx_used"] = _llamaCtxUsed.ToString("F0", CultureInfo.InvariantCulture);
						}
					}
					if (num % 2 == 0)
					{
						foreach (KeyValuePair<string, string> item in _gpuMonitor.QueryGpu())
						{
							d[item.Key] = item.Value;
						}
					}
					// 能耗累计(Strata 读数条): GPU 功率 × 采样间隔积分, llama 启动时清零（须在 GPU 查询合并之后, power_w 才在 d 中）
					if (d.TryGetValue("power_w", out string pwStr) && double.TryParse(pwStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var pwVal) && pwVal > 0.0)
					{
						long nowTick = Environment.TickCount64;
						if (_lastPowerTick >= 0)
						{
							_energyWh += pwVal * ((nowTick - _lastPowerTick) / 1000.0) / 3600.0;
						}
						_lastPowerTick = nowTick;
					}
					else
					{
						_lastPowerTick = -1;
					}
					((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
					{
						OnPerf(d);
					}, Array.Empty<object>());
					num++;
				}
				catch (Exception ex5)
				{
					LogError(ex5, "性能监控异常");
				}
				// 采样节奏(Strata 口径): llama 运行中 1s, 空闲 5s; GPU 查询缓存 TTL 与之同步
				bool llamaRunning = IsAlive(_llamaProc);
				_gpuMonitor.CacheTtlSeconds = llamaRunning ? 1.0 : 10.0;
				Thread.Sleep(llamaRunning ? 1000 : 5000);
			}
		})
		{
			IsBackground = true
		};
		_perfThread.Start();
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
		try
		{
			_memCounter?.Dispose();
		}
		catch (Exception ex2)
		{
			LogError(ex2, "释放内存计数器失败");
		}
		_memCounter = null;
	}

	private int GetLlamaPort()
	{
		Match match = Regex.Match(_lastCmd, "--port\\s+(\\d+)");
		if (!match.Success)
		{
			return 1234;
		}
		return int.Parse(match.Groups[1].Value);
	}

	private (double tps, double prefill)? FetchLlamaMetrics()
	{
		try
		{
			string result = _http.GetStringAsync($"http://127.0.0.1:{GetLlamaPort()}/metrics").Result;
			double num = 0.0;
			double num2 = 0.0;
			double num3 = 0.0;
			double num4 = 0.0;
			double cacheTok = -1.0;
			double reqProc = -1.0;
			double reqWait = -1.0;
			string[] array = result.Split('\n');
			for (int i = 0; i < array.Length; i++)
			{
				string s = array[i].Trim();
				double v2;
				double v3;
				double v4;
				if (GetVal(s, "llamacpp:prompt_tokens_total ", out var v))
				{
					num = v;
				}
				else if (GetVal(s, "llamacpp:prompt_seconds_total ", out v2))
				{
					num2 = v2;
				}
				else if (GetVal(s, "llamacpp:tokens_predicted_total ", out v3))
				{
					num3 = v3;
				}
				else if (GetVal(s, "llamacpp:tokens_predicted_seconds_total ", out v4))
				{
					num4 = v4;
				}
				else if (GetVal(s, "llamacpp:cache_tokens ", out v2))
				{
					cacheTok = v2;
				}
				else if (GetVal(s, "llamacpp:requests_processing ", out v3))
				{
					reqProc = v3;
				}
				else if (GetVal(s, "llamacpp:requests_waiting ", out v4))
				{
					reqWait = v4;
				}
			}
			_llamaCtxUsed = cacheTok;
			_llamaReqProc = reqProc > 0.0 ? (int)reqProc : 0;
			_llamaReqWait = reqWait > 0.0 ? (int)reqWait : 0;
			double num5 = ((num4 > 0.0) ? (num3 / num4) : 0.0);
			double num6 = ((num2 > 0.0) ? (num / num2) : 0.0);
			if (num5 > 0.0 || num6 > 0.0)
			{
				return (num5, num6);
			}
		}
		catch
		{
			_llamaCtxUsed = -1.0;
			_llamaReqProc = 0;
			_llamaReqWait = 0;
		}
		return null;
		static bool GetVal(string text, string prefix, out double reference)
		{
			reference = 0.0;
			if (!text.StartsWith(prefix, StringComparison.Ordinal))
			{
				return false;
			}
			return double.TryParse(text.AsSpan(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out reference);
		}
	}

	// 监测标签页(Strata 复刻): 指标历史环形缓冲(60 点) + 阶段判定时间戳
	private readonly ConcurrentQueue<double> _histCpu = new();
	private readonly ConcurrentQueue<double> _histGpu = new();
	private readonly ConcurrentQueue<double> _histVram = new();
	private readonly ConcurrentQueue<double> _histTemp = new();
	private readonly ConcurrentQueue<double> _histTps = new();
	private readonly ConcurrentQueue<double> _histPf = new();
	private readonly ConcurrentQueue<double> _histPower = new();
	private readonly ConcurrentQueue<double> _histPcie = new();
	private readonly ConcurrentQueue<double> _histDisk = new();
	private DateTime _lastDecodeUtc = DateTime.MinValue;
	private DateTime _lastPrefillUtc = DateTime.MinValue;

	// 扩展采集: 磁盘读写/物理内存/能耗积分(GPU 功率 × 采样间隔)
	private PerformanceCounter? _diskReadCounter;
	private PerformanceCounter? _diskWriteCounter;
	private PerformanceCounter? _ramAvailCounter;
	private double _ramTotalBytes;
	private double _energyWh;
	private double _lastPowerTick = -1.0;

	// llama-server /metrics 扩展: KV 缓存占用(tok)/处理中/排队中; -1 = 未报告
	private double _llamaCtxUsed = -1.0;
	private int _llamaReqProc;
	private int _llamaReqWait;
	private long _ctxTokens;
	private int _queueCount;

	// 最近请求表 + 对话缓存统计(来自 llama 日志的计时行)
	private sealed class ReqRec
	{
		public string Time = "";
		public string Status = "✓ 完成";
		public string Prompt = "—";
		public string Reused = "—";
		public string Output = "—";
		public string Tps = "—";
		public string Hit = "—";
		public string Dur = "—";
	}
	private readonly List<ReqRec> _reqRecords = new();
	private readonly object _reqLock = new();
	private int _curPromptToks;
	private int _curReused;
	private int _curOutToks;
	private double _curTps;
	private double _curEvalMs;
	private long _reusedTotal;
	private long _reqTotalCount;

	// 追加一个采样点并把整条历史重绘到迷你曲线; maxV<=0 时按历史最大值自动缩放
	private static void PushSpark(System.Windows.Shapes.Polyline pl, ConcurrentQueue<double> q, double v, double maxV)
	{
		AddS(q, v, 60);
		double m = maxV;
		if (m <= 0.0)
		{
			foreach (double x in q.ToArray())
			{
				m = Math.Max(m, x);
			}
			if (m <= 0.0)
			{
				m = 1.0;
			}
		}
		var pts = new System.Windows.Media.PointCollection();
		double[] arr = q.ToArray();
		for (int i = 0; i < arr.Length; i++)
		{
			double y = 23.0 - Math.Max(0.0, Math.Min(1.0, arr[i] / m)) * 21.0;
			pts.Add(new System.Windows.Point(arr.Length <= 1 ? 0.0 : i * 100.0 / (arr.Length - 1), y));
		}
		pl.Points = pts;
	}

	// 数据来源徽标: 有效="实测"(绿), 失效="无数据"(灰); src 非空时显示来源(温度传感器)
	private static void SetBadge(System.Windows.Controls.TextBlock b, bool ok, string src = "实测")
	{
		b.Text = ok ? src : "无数据";
		b.Background = new SolidColorBrush(ok ? System.Windows.Media.Color.FromArgb(0x33, 0x23, 0x86, 0x36) : System.Windows.Media.Color.FromArgb(0x22, 0x8b, 0x94, 0x9e));
		b.Foreground = new SolidColorBrush(ok ? System.Windows.Media.Color.FromRgb(0x7e, 0xe7, 0x87) : System.Windows.Media.Color.FromRgb(0x8b, 0x94, 0x9e));
	}

	// 模型状态卡(Strata): 空闲/预填充/生成中/排队/异常 五徽标, 活动徽标高亮; 进度条在活动阶段转为不确定态
	private void UpdatePhaseCard()
	{
		bool llamaUp = IsAlive(_llamaProc);
		string active;   // idle | reading | gen | queue | error
		string detail;
		if (_vramBreakerTriggered)
		{
			active = "error";
			detail = "显存熔断已触发，llama 服务已自动停止";
		}
		else if (!llamaUp)
		{
			active = "idle";
			detail = "llama 未启动";
		}
		else if (_queueCount > 0)
		{
			active = "queue";
			detail = $"{_queueCount} 个请求在队列中等待处理";
		}
		else if ((DateTime.UtcNow - _lastDecodeUtc).TotalSeconds < 15.0 || _llamaReqProc > 0)
		{
			active = "gen";
			double[] arr = _histTps.ToArray();
			double cur = arr.Length > 0 ? arr[arr.Length - 1] : 0.0;
			detail = cur > 0.0 ? $"正在生成 · {FT(cur)} tok/s" : "正在生成";
		}
		else if ((DateTime.UtcNow - _lastPrefillUtc).TotalSeconds < 15.0)
		{
			active = "reading";
			detail = "正在预填充提示词";
		}
		else
		{
			active = "idle";
			detail = "llama 运行中，等待请求";
		}
		bool activeBusy = active == "gen" || active == "reading";
		StylePhase(MonPhIdle, MonPhIdleT, active == "idle", System.Windows.Media.Color.FromArgb(0x33, 0x8b, 0x94, 0x9e), System.Windows.Media.Color.FromRgb(0x8b, 0x94, 0x9e));
		StylePhase(MonPhReading, MonPhReadingT, active == "reading", System.Windows.Media.Color.FromArgb(0x55, 0xbb, 0x80, 0x09), System.Windows.Media.Color.FromRgb(0xe3, 0xb3, 0x41));
		StylePhase(MonPhGen, MonPhGenT, active == "gen", System.Windows.Media.Color.FromArgb(0x55, 0x23, 0x86, 0x36), System.Windows.Media.Color.FromRgb(0x7e, 0xe7, 0x87));
		StylePhase(MonPhQueue, MonPhQueueT, active == "queue", System.Windows.Media.Color.FromArgb(0x55, 0x1f, 0x6f, 0xeb), System.Windows.Media.Color.FromRgb(0x79, 0xc0, 0xff));
		StylePhase(MonPhError, MonPhErrorT, active == "error", System.Windows.Media.Color.FromArgb(0x55, 0xda, 0x36, 0x33), System.Windows.Media.Color.FromRgb(0xff, 0xa1, 0x9b));
		MonPhaseDetail.Text = detail;
		MonProgress.IsIndeterminate = activeBusy;
		if (!activeBusy)
		{
			MonProgress.Value = 0.0;
		}
		MonProgressLabel.Text = active == "gen" ? "生成中…" : (active == "reading" ? "预填充中…" : "");
	}

	// 阶段徽标统一样式: 活动=彩色底亮字, 非活动=暗底灰字
	private static void StylePhase(System.Windows.Controls.Border b, System.Windows.Controls.TextBlock t, bool active, System.Windows.Media.Color bg, System.Windows.Media.Color fg)
	{
		b.Background = new SolidColorBrush(active ? bg : System.Windows.Media.Color.FromArgb(0x18, 0x8b, 0x94, 0x9e));
		t.Foreground = new SolidColorBrush(active ? fg : System.Windows.Media.Color.FromRgb(0x6e, 0x76, 0x81));
		t.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
	}

	// 上下文填充 Gauge 环: 顶部起点顺时针弧, pct 0-100
	private void UpdateGauge(double pct)
	{
		double frac = Math.Max(0.0, Math.Min(1.0, pct / 100.0));
		double r = 34.0, cx = 40.0, cy = 40.0;
		double sweep = Math.Max(0.5, frac * 359.0);
		double a0 = -90.0, a1 = a0 + sweep;
		var p0 = new System.Windows.Point(cx + r * Math.Cos(a0 * Math.PI / 180.0), cy + r * Math.Sin(a0 * Math.PI / 180.0));
		var p1 = new System.Windows.Point(cx + r * Math.Cos(a1 * Math.PI / 180.0), cy + r * Math.Sin(a1 * Math.PI / 180.0));
		var geo = new System.Windows.Media.StreamGeometry();
		using (System.Windows.Media.StreamGeometryContext ctx = geo.Open())
		{
			ctx.BeginFigure(p0, false, false);
			ctx.ArcTo(p1, new System.Windows.Size(r, r), 0.0, sweep > 180.0, SweepDirection.Clockwise, true, false);
		}
		MonGaugeArc.Data = geo;
		MonGaugeArc.Stroke = new SolidColorBrush(frac >= 0.9 ? System.Windows.Media.Color.FromRgb(0xda, 0x36, 0x33) : System.Windows.Media.Color.FromRgb(0x1f, 0x6f, 0xeb));
		MonGaugePct.Text = $"{frac * 100.0:F0}%";
	}

	// llama 启动时清空监测卡历史/曲线/能耗/请求表(Strata: 自启动起算)
	private void ResetMonitorCards()
	{
		foreach (var q in new[] { _histCpu, _histGpu, _histVram, _histTemp, _histTps, _histPf, _histPower, _histPcie, _histDisk })
		{
			q.Clear();
		}
		foreach (var pl in new[] { SparkCpu, SparkGpu, SparkVram, SparkTemp, SparkTps, SparkPf, SparkPower, SparkPcie, SparkDisk })
		{
			pl.Points = new System.Windows.Media.PointCollection();
		}
		_lastDecodeUtc = DateTime.MinValue;
		_lastPrefillUtc = DateTime.MinValue;
		_energyWh = 0.0;
		_lastPowerTick = -1.0;
		_llamaCtxUsed = -1.0;
		_llamaReqProc = 0;
		_llamaReqWait = 0;
		_queueCount = 0;
		_ctxTokens = 0;
		lock (_reqLock)
		{
			_reqRecords.Clear();
		}
		_curPromptToks = 0;
		_curReused = 0;
		_curOutToks = 0;
		_curTps = 0.0;
		_curEvalMs = 0.0;
		_reusedTotal = 0;
		_reqTotalCount = 0;
	}

	// 一次请求计时链路完成(prompt → eval → total time), 落一条请求记录并刷新表/汇总/缓存区
	private void FinalizeRequest()
	{
		ReqRec rec = new ReqRec
		{
			Time = DateTime.Now.ToString("HH:mm:ss"),
			Prompt = _curPromptToks > 0 ? _curPromptToks + " tok" : "—",
			Reused = _curReused > 0 ? _curReused + " tok" : "—",
			Output = _curOutToks > 0 ? _curOutToks + " tok" : "—",
			Tps = _curTps > 0.0 ? FT(_curTps) : "—",
			Dur = _curEvalMs > 0.0 ? $"{_curEvalMs / 1000.0:F1} s" : "—",
		};
		// VRAM 命中率(Strata 口径): 复用 / prompt tokens
		if (_curReused > 0 && _curPromptToks > 0)
		{
			rec.Hit = $"{Math.Min(100.0, _curReused * 100.0 / _curPromptToks):F0}%";
		}
		if (_curReused > 0)
		{
			_reusedTotal += _curReused;
		}
		_reqTotalCount++;
		int lastReused = _curReused;
		lock (_reqLock)
		{
			_reqRecords.Add(rec);
			if (_reqRecords.Count > 200)
			{
				_reqRecords.RemoveAt(0);
			}
		}
		_curPromptToks = 0;
		_curReused = 0;
		_curOutToks = 0;
		_curTps = 0.0;
		_curEvalMs = 0.0;
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			RefreshReqList();
			MonCacheLast.Text = lastReused > 0 ? $"复用 {lastReused} tok" : "未报告复用";
			MonCacheTotal.Text = $"{_reusedTotal} tok / {_reqTotalCount} 次请求";
		}, Array.Empty<object>());
	}

	// 请求表刷新: 默认最新 12 条(新→旧), 勾选显示全部
	private void RefreshReqList()
	{
		List<ReqRec> view;
		lock (_reqLock)
		{
			int n = (ChkShowAllReq?.IsChecked == true) ? _reqRecords.Count : Math.Min(12, _reqRecords.Count);
			view = _reqRecords.Skip(Math.Max(0, _reqRecords.Count - n)).Reverse().ToList();
		}
		MonReqList.ItemsSource = view;
		MonReqSummary.Text = $"{_reqTotalCount} 次请求 · 复用 {_reusedTotal} tok";
	}

	private void ShowAllReq_Changed(object sender, RoutedEventArgs e)
	{
		RefreshReqList();
	}

	private void OnPerf(Dictionary<string, string> d)
	{
		if (d.TryGetValue("cpu", out string value))
		{
			bool okCpu = double.TryParse(value, out var vCpu);
			PerfCpu.Text = okCpu ? $"{vCpu:F1}%" : "N/A";
			SetBadge(BdgCpu, okCpu);
			if (okCpu)
			{
				PushSpark(SparkCpu, _histCpu, vCpu, 100.0);
			}
		}
		if (d.TryGetValue("gpu_util", out string value3))
		{
			bool okGpu = double.TryParse(value3, out var vGpu);
			PerfGpu.Text = okGpu ? $"{Math.Min(100.0, Math.Max(0.0, vGpu)):F0}%" : "N/A";
			SetBadge(BdgGpu, okGpu);
			if (okGpu)
			{
				PushSpark(SparkGpu, _histGpu, Math.Min(100.0, Math.Max(0.0, vGpu)), 100.0);
			}
		}
		string value7;
		double result4;
		if (d.TryGetValue("vram_used", out string value4) && d.TryGetValue("vram_total", out string value5))
		{
			if (double.TryParse(value4, out var result2) && double.TryParse(value5, out var result3))
			{
				double num = result2 / 1073741824.0;
				double num2 = result3 / 1073741824.0;
				double value6 = ((num2 > 0.0) ? (num / num2 * 100.0) : 0.0);
				PerfVram.Text = $"{num:F1}/{num2:F0}G";
				PerfVram.ToolTip = $"显存已用 {num:F1} / {num2:F1} GB（{value6:F1}%）";
				MonVramSub.Text = $"已用 {value6:F1}%";
				SetBadge(BdgVram, num2 > 0.0);
				PushSpark(SparkVram, _histVram, value6, 100.0);
			}
			else
			{
				PerfVram.Text = "N/A";
				PerfVram.ToolTip = null;
				MonVramSub.Text = "—";
				SetBadge(BdgVram, false);
			}
		}
		else if (d.TryGetValue("vram_total", out value7) && double.TryParse(value7, out result4))
		{
			double value8 = result4 / 1073741824.0;
			PerfVram.Text = $"{value8:F0}G";
			PerfVram.ToolTip = $"显存总量 {value8:F1} GB";
			MonVramSub.Text = "总量";
			SetBadge(BdgVram, true);
		}
		if (d.TryGetValue("gpu_temp", out string value9))
		{
			if (double.TryParse(value9, out var result5) && result5 > 0.0 && result5 < 150.0)
			{
				d.TryGetValue("gpu_temp_src", out string value10);
				Dictionary<string, string> dictionary = new Dictionary<string, string>
				{
					["nvidia"] = "NVIDIA",
					["amd"] = "AMD",
					["intel"] = "Intel",
					["ohm"] = "Sensor",
					["acpi"] = "ACPI"
				};
				string text = ((!string.IsNullOrEmpty(value10) && dictionary.TryGetValue(value10, out var value11)) ? value11 : "");
				PerfGpuTemp.Text = $"{result5:F0}°C";
				PerfGpuTemp.ToolTip = (string.IsNullOrEmpty(text) ? null : ("温度来源：" + text));
				SetBadge(BdgTemp, true, string.IsNullOrEmpty(text) ? "实测" : text);
				PushSpark(SparkTemp, _histTemp, result5, 100.0);
				MonGpuTempBar.Value = Math.Min(100.0, result5);
				MonGpuTempBar.Foreground = new SolidColorBrush(result5 > 92.0 ? System.Windows.Media.Color.FromRgb(0xda, 0x36, 0x33) : System.Windows.Media.Color.FromRgb(0x23, 0x86, 0x36));
				MonGpuTempVal.Text = $"{result5:F0}°C";
			}
			else
			{
				PerfGpuTemp.Text = "N/A";
				PerfGpuTemp.ToolTip = null;
				SetBadge(BdgTemp, false);
			}
		}
		if (d.TryGetValue("httptps", out string value12) && double.TryParse(value12, NumberStyles.Float, CultureInfo.InvariantCulture, out var result6) && result6 > 0.0)
		{
			PerfTps.Text = FT(result6);
			SetBadge(BdgTps, true);
			PushSpark(SparkTps, _histTps, result6, 0.0);
			_lastDecodeUtc = DateTime.UtcNow;
		}
		if (d.TryGetValue("httppf", out string value13) && double.TryParse(value13, NumberStyles.Float, CultureInfo.InvariantCulture, out var result7) && result7 > 0.0)
		{
			PerfPrefill.Text = FT(result7);
			PushSpark(SparkPf, _histPf, result7, 0.0);
			_lastPrefillUtc = DateTime.UtcNow;
		}
		// Strata 扩展卡: 功率(上限副行) / PCIe(代数×宽度, Rx 吞吐) / 磁盘读(写副行)
		if (d.TryGetValue("power_w", out string vPw) && double.TryParse(vPw, NumberStyles.Float, CultureInfo.InvariantCulture, out var vP) && vP > 0.0)
		{
			MonPower.Text = vP >= 100.0 ? vP.ToString("F0") : vP.ToString("F1");
			MonPowerSub.Text = (d.TryGetValue("power_limit_w", out string vLim) && double.TryParse(vLim, NumberStyles.Float, CultureInfo.InvariantCulture, out var vL) && vL > 0.0)
				? $"功耗上限 {vL:F0} W" : "整机 GPU 功率";
			SetBadge(BdgPower, true);
			PushSpark(SparkPower, _histPower, vP, 0.0);
		}
		if (d.TryGetValue("pcie_gen", out string vGen))
		{
			MonPcie.Text = $"Gen{vGen} × {(d.TryGetValue("pcie_width", out string vWid) ? vWid : "?")}";
			string pcieSub = "PCIe 吞吐";
			if (d.TryGetValue("pcie_rx_kbs", out string vRx) && double.TryParse(vRx, NumberStyles.Float, CultureInfo.InvariantCulture, out var vR) && vR > 0.0)
			{
				double mbs = vR / 1024.0;
				pcieSub = $"Rx {(mbs >= 1024.0 ? $"{mbs / 1024.0:F1} GB/s" : $"{mbs:F0} MB/s")}";
				PushSpark(SparkPcie, _histPcie, mbs, 0.0);
			}
			MonPcieSub.Text = pcieSub;
			SetBadge(BdgPcie, true);
		}
		if (d.TryGetValue("disk_read", out string vDr) && double.TryParse(vDr, NumberStyles.Float, CultureInfo.InvariantCulture, out var vD) && vD > 0.0)
		{
			MonDisk.Text = vD >= 1073741824.0 ? $"{vD / 1073741824.0:F2} GB/s" : $"{vD / 1048576.0:F0} MB/s";
			MonDiskSub.Text = (d.TryGetValue("disk_write", out string vDw) && double.TryParse(vDw, NumberStyles.Float, CultureInfo.InvariantCulture, out var vDw2) && vDw2 > 0.0)
				? $"写 {(vDw2 >= 1073741824.0 ? $"{vDw2 / 1073741824.0:F2} GB/s" : $"{vDw2 / 1048576.0:F0} MB/s")}" : "磁盘读吞吐";
			SetBadge(BdgDisk, true);
			PushSpark(SparkDisk, _histDisk, vD / 1048576.0, 0.0);
		}
		// 读数条: 能耗累计 / 风扇 / 频率 / CPU 温度
		MonEnergy.Text = _energyWh >= 1000.0 ? $"{_energyWh / 1000.0:F2} kWh" : $"{_energyWh:F1} Wh";
		if (d.TryGetValue("fan_pct", out string vFn) && double.TryParse(vFn, NumberStyles.Float, CultureInfo.InvariantCulture, out var vF) && vF >= 0.0)
		{
			MonFan.Text = $"{vF:F0}%";
		}
		if (d.TryGetValue("clock_mhz", out string vCm) && double.TryParse(vCm, NumberStyles.Float, CultureInfo.InvariantCulture, out var vC) && vC > 0.0)
		{
			MonClock.Text = $"{vC:F0} MHz";
		}
		if (d.TryGetValue("cpu_temp", out string vCt) && double.TryParse(vCt, NumberStyles.Float, CultureInfo.InvariantCulture, out var vCt2) && vCt2 > 0.0)
		{
			MonCpuTemp.Text = $"{vCt2:F0}°C";
			MonCpuTemp.ToolTip = (d.TryGetValue("cpu_temp_src", out string vSrc) && vSrc == "acpi") ? "ACPI 热区(非核心精确值)" : null;
		}
		// 上下文填充: Gauge(KV 占用/配置 ctx) + 系统内存条
		if (_ctxTokens == 0 && IsAlive(_llamaProc) && RoleText("ctx_size", out string ctxStr2))
		{
			long.TryParse(NormTok(ctxStr2), out _ctxTokens);
		}
		if (d.TryGetValue("ctx_used", out string vCu) && double.TryParse(vCu, NumberStyles.Float, CultureInfo.InvariantCulture, out var vCu2) && vCu2 >= 0.0 && _ctxTokens > 0)
		{
			UpdateGauge(Math.Min(100.0, vCu2 / _ctxTokens * 100.0));
			MonGaugeLabel.Text = $"上下文 {vCu2:F0}/{_ctxTokens} tok";
			MonCtxLabel.Text = $"KV {vCu2:F0} / ctx {_ctxTokens} tok";
		}
		else if (!IsAlive(_llamaProc))
		{
			UpdateGauge(0.0);
			MonGaugeLabel.Text = "上下文";
			MonCtxLabel.Text = "KV 占用 / 配置上下文";
		}
		if (d.TryGetValue("ram_used", out string vRu) && d.TryGetValue("ram_total", out string vRt) && double.TryParse(vRu, NumberStyles.Float, CultureInfo.InvariantCulture, out var vRu2) && double.TryParse(vRt, NumberStyles.Float, CultureInfo.InvariantCulture, out var vRt2) && vRt2 > 0.0)
		{
			MonRamLabel.Text = $"{vRu2 / 1073741824.0:F1} / {vRt2 / 1073741824.0:F0} GB";
			MonRamBar.Value = Math.Min(100.0, vRu2 / vRt2 * 100.0);
		}
		if (d.TryGetValue("req_wait", out string vRw) && int.TryParse(vRw, out var vRw2))
		{
			_queueCount = vRw2;
		}
		UpdatePhaseCard();
		if (d.TryGetValue("cpu_err", out string value14))
		{
			Log("[Perf] CPU error: " + value14);
		}
		if (d.TryGetValue("mem_err", out string value15))
		{
			Log("[Perf] Memory error: " + value15);
		}
		try
		{
			System.Windows.Controls.CheckBox chkVramBreaker = ChkVramBreaker;
			if (chkVramBreaker == null || chkVramBreaker.IsChecked != true || _vramBreakerTriggered || !IsAlive(_llamaProc) || !d.TryGetValue("vram_used", out string value16) || !d.TryGetValue("vram_total", out string value17) || !double.TryParse(value16, out var vUsed) || !double.TryParse(value17, out var vTotal) || !(vTotal > 0.0))
			{
				return;
			}
			double pct = vUsed / vTotal * 100.0;
			NoScrollComboBox comboVramBreakerType = ComboVramBreakerType;
			bool flag = comboVramBreakerType != null && comboVramBreakerType.SelectedIndex == 0;
			double num3 = (double.TryParse(TxtVramBreakerVal?.Text, out var result8) ? result8 : ((double)(flag ? 90 : 20)));
			bool hit = (flag ? (pct >= num3) : (vUsed / 1073741824.0 >= num3));
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				LblVramBreaker.Text = (hit ? $"⚠ {pct:F1}% ({vUsed / 1073741824.0:F1}/{vTotal / 1073741824.0:F1} GB) 已熔断" : $"{pct:F1}% 正常");
				LblVramBreaker.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(hit ? 218 : 139), (byte)(hit ? 54 : 148), (byte)(hit ? 51 : 158)));
			}, Array.Empty<object>());
			if (hit)
			{
				_vramBreakerTriggered = true;
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					Log("[熔断] 显存使用超限，自动停止 Llama 服务");
					StopLlama();
					LblVramBreaker.Text += " (已停止)";
				}, Array.Empty<object>());
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "显存熔断检查异常");
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
				File.WriteAllText(_stateFile, JsonSerializer.Serialize(stateFile, new JsonSerializerOptions
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
				_llamaProc = Process.GetProcessById(stateFile.LlamaPid.Value);
				BtnLlamaStart.IsEnabled = false;
				BtnLlamaStop.IsEnabled = true;
				LblLlamaStatus.Text = "● 接管运行中";
				LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 185, 80));
				Log($"Resumed Llama:{stateFile.LlamaPid.Value}");
			}
			catch (Exception ex2)
			{
				LogError(ex2, "接管 Llama 进程失败");
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
					File.WriteAllText(_stateFile, JsonSerializer.Serialize(stateFile, new JsonSerializerOptions
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
		if (!double.TryParse(TxtVramBreakerVal?.Text, out var result))
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
			TxtVramBreakerVal.Text = c.VramBreakerValue.ToString();
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
		RefreshCmd();
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
			File.WriteAllText(path, JsonSerializer.Serialize(new ConfigFile
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
				File.WriteAllText(_windowStateFile, JsonSerializer.Serialize(new ConfigFile
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
				bool needNode = !st.MeetsRequirement || st.Node24Version != latest;
				bool needPnpm = st.PnpmExe == null;

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
		if (ChkMinimizeToTray.IsChecked == true && _trayIcon != null)
		{
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
		FlushRunLog();
		_trayIcon?.Dispose();
		System.Windows.Application.Current.Shutdown();
	}

}
