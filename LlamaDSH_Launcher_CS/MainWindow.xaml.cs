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

public partial class MainWindow : Window, IComponentConnector
{
	private const string DSH_DIR = "%DSH%";

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

	private readonly DispatcherTimer _healthTimer;

	private readonly ConcurrentQueue<string> _logPending;

	private readonly DispatcherTimer _logTimer;

	private const int LogMaxLines = 2000;

	private const int LogTrimKeep = 1700;

	private NotifyIcon? _trayIcon;

	private List<ParamDefinition> _paramDefs;

	private ParamSchemaLoadResult _paramSchema = new();

	private List<ParamGroupDef> _paramGroups = new();

	/// <summary>最近一次 BuildCmd 依赖/校验未通过的参数清单（启动时弹窗确认）。</summary>
	private List<string> _lastViolations = new();

	private bool _inDepEval;

	private readonly Dictionary<string, (System.Windows.Controls.CheckBox ec, System.Windows.Controls.Control vw)> _pw;

	private readonly Dictionary<string, System.Windows.Controls.CheckBox> _pbw;

	private static readonly Regex _rePromptTps = new Regex("prompt eval time\\s+=\\s*([\\d.]+)\\s*ms\\s*/\\s*(\\d+)\\s*tokens", RegexOptions.Compiled);

	private static readonly Regex _rePromptMs = new Regex("prompt eval time\\s+=\\s*([\\d.]+)\\s*ms", RegexOptions.Compiled);

	private static readonly Regex _reEvalTps = new Regex("(?<!prompt )eval time\\s+=\\s*[\\d.]+\\s*ms\\s*/\\s*\\d+\\s*(?:new tokens|tokens|runs)\\s*\\([^)]*?([\\d.]+)\\s*(?:tokens? per second|tok/s)", RegexOptions.Compiled);

	private static readonly Regex _reEvalRaw = new Regex("(?<!prompt )eval time\\s+=\\s*([\\d.]+)\\s*ms\\s*/\\s*(\\d+)\\s*(?:new tokens|tokens|runs)", RegexOptions.Compiled);

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
				System.Windows.MessageBox.Show(schemaMsg, "参数配置提示", MessageBoxButton.OK, MessageBoxImage.Warning);
			}));
		}
		LoadConfigDirFromFile();
		InitializeComponent();
		BuildParamGroups();
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
		foreach (ParamGroupDef g in _paramGroups.OrderBy((ParamGroupDef x) => x.Order))
		{
			if (!_paramDefs.Any((ParamDefinition p) => p.Group == g.Id && !p.Hidden))
			{
				continue;
			}
			Expander expander = new Expander
			{
				Header = g.Title,
				IsExpanded = true,
				Margin = new Thickness(0.0, 4.0, 0.0, 0.0),
				Foreground = System.Windows.Media.Brushes.White
			};
			Grid grid = new Grid
			{
				Margin = new Thickness(4.0)
			};
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(1.0, GridUnitType.Star)
			});
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(12.0)
			});
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(1.0, GridUnitType.Star)
			});
			int idx = 0;
			string? lastSubgroup = null;
			foreach (ParamDefinition item in _paramDefs.Where((ParamDefinition p) => p.Group == g.Id && !p.Hidden))
			{
				if (!string.IsNullOrEmpty(item.Subgroup) && item.Subgroup != lastSubgroup)
				{
					lastSubgroup = item.Subgroup;
					System.Windows.Controls.TextBlock header = new System.Windows.Controls.TextBlock
					{
						Text = "▸ " + item.Subgroup,
						Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 168, 83)),
						FontWeight = FontWeights.SemiBold,
						Margin = new Thickness(0.0, 8.0, 0.0, 2.0)
					};
					if (idx % 2 != 0)
					{
						idx++; // 子组标题总是从左列开始
					}
					Grid.SetRow(header, idx / 2);
					Grid.SetColumnSpan(header, 3);
					while (grid.RowDefinitions.Count <= idx / 2)
					{
						grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
					}
					grid.Children.Add(header);
					idx += 2;
					continue;
				}
				if (item.Type == ParamType.Bool)
				{
					System.Windows.Controls.CheckBox checkBox = new System.Windows.Controls.CheckBox
					{
						Content = item.Label,
						IsChecked = string.Equals(item.DefaultValue, "true", StringComparison.OrdinalIgnoreCase),
						ToolTip = item.Tooltip,
						Foreground = System.Windows.Media.Brushes.White,
						Margin = new Thickness(0.0, 2.0, 0.0, 2.0)
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
					continue;
				}
				DockPanel dockPanel = new DockPanel
				{
					Margin = new Thickness(0.0, 2.0, 0.0, 2.0)
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
						HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
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
						HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
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
				PlaceParamRow(grid, dockPanel, ref idx);
			}
			expander.Content = grid;
			ParamGroupsPanel.Children.Add(expander);
		}
	}

	private static void PlaceParamRow(Grid g, UIElement el, ref int idx)
	{
		int num = idx / 2;
		int value = idx % 2 * 2;
		while (g.RowDefinitions.Count <= num)
		{
			g.RowDefinitions.Add(new RowDefinition
			{
				Height = GridLength.Auto
			});
		}
		Grid.SetRow(el, num);
		Grid.SetColumn(el, value);
		g.Children.Add(el);
		idx++;
	}

	// ==================== 参数依赖引擎（配置驱动） ====================

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

	private static bool CompareValues(string v, string op, string target)
	{
		bool isNumA = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double a);
		bool isNumB = double.TryParse(target, NumberStyles.Float, CultureInfo.InvariantCulture, out double b);
		int c = (isNumA && isNumB) ? a.CompareTo(b) : string.CompareOrdinal(v, target);
		return op switch
		{
			"==" => c == 0,
			"!=" => c != 0,
			">" => c > 0,
			"<" => c < 0,
			">=" => c >= 0,
			"<=" => c <= 0,
			_ => true
		};
	}

	private bool RuleMet(ParamRule r)
	{
		ParamDefinition? def = _paramDefs.FirstOrDefault((ParamDefinition x) => x.Flag == r.Flag);
		if (def == null)
		{
			return true;
		}
		switch (r.Op)
		{
		case "enabled":
			return IsEffectivelyOn(def);
		case "disabled":
			return !IsEffectivelyOn(def);
		default:
			return CompareValues(EffectiveValue(def), r.Op, r.Value);
		}
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
			foreach (ParamDefinition d in _paramDefs)
			{
				if (d.Hidden || d.Requires == null || d.Requires.Count == 0)
				{
					continue;
				}
				bool ok = d.Requires.All(RuleMet);
				if (_pbw.TryGetValue(d.Flag, out System.Windows.Controls.CheckBox bcb))
				{
					bcb.IsEnabled = ok;
					bcb.ToolTip = ok ? d.Tooltip : d.Tooltip + "\n⚠ 前置未满足: " + string.Join(" 且 ", d.Requires.Select((ParamRule r) => r.Describe()));
				}
				if (_pw.TryGetValue(d.Flag, out (System.Windows.Controls.CheckBox, System.Windows.Controls.Control) pair))
				{
					pair.Item1.IsEnabled = ok;
					pair.Item2.IsEnabled = ok;
					pair.Item2.ToolTip = ok ? d.Tooltip : d.Tooltip + "\n⚠ 前置未满足: " + string.Join(" 且 ", d.Requires.Select((ParamRule r) => r.Describe()));
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
		if (d.Requires is { Count: > 0 })
		{
			List<ParamRule> unmet = d.Requires.Where((ParamRule r) => !RuleMet(r)).ToList();
			if (unmet.Count > 0)
			{
				return "前置不满足（" + string.Join(" 且 ", unmet.Select((ParamRule r) => r.Describe())) + "）";
			}
		}
		if (d.Conflicts is { Count: > 0 })
		{
			string? hit = d.Conflicts.FirstOrDefault((string c) => on.Contains(c));
			if (hit != null)
			{
				return "与 --" + hit + " 互斥";
			}
		}
		if (d.Validate != null)
		{
			string v = (d.Type == ParamType.Bool) ? "true" : EffectiveValue(d);
			ParamValidate val = d.Validate;
			if (d.Type != ParamType.Bool)
			{
				string vn = NormTok(v);
				bool isNum = double.TryParse(vn, NumberStyles.Float, CultureInfo.InvariantCulture, out double n);
				if ((val.Min.HasValue || val.Max.HasValue) && !isNum)
				{
					return "值 '" + v + "' 不是数字";
				}
				if (val.Min.HasValue && isNum && n < val.Min.Value)
				{
					return "值 " + vn + " 小于下限 " + val.Min.Value;
				}
				if (val.Max.HasValue && isNum && n > val.Max.Value)
				{
					return "值 " + vn + " 大于上限 " + val.Max.Value;
				}
				if (val.Leq != null)
				{
					ParamDefinition? other = _paramDefs.FirstOrDefault((ParamDefinition x) => x.Flag == val.Leq);
					if (other != null && on.Contains(other.Flag) && double.TryParse(NormTok(EffectiveValue(other)), NumberStyles.Float, CultureInfo.InvariantCulture, out double cap))
					{
						if (isNum && n > cap)
						{
							return "值 " + vn + " 超过 --" + other.Flag + " (" + cap + ")";
						}
					}
				}
				if (val.Regex != null && !Regex.IsMatch(v, val.Regex))
				{
					return "值 '" + v + "' 不匹配格式 " + val.Regex;
				}
			}
		}
		return null;
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
		using (System.Windows.Forms.FolderBrowserDialog folderBrowserDialog = new System.Windows.Forms.FolderBrowserDialog())
		{
			folderBrowserDialog.Description = "选择模型目录：分片 GGUF 自动取第一片，safetensors 需先转 GGUF";
			if (folderBrowserDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
			{
				InputModelPath.Text = folderBrowserDialog.SelectedPath;
			}
		}
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
		FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog();
		if (folderBrowserDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
		{
			InputLlamaDir.Text = folderBrowserDialog.SelectedPath;
		}
	}

	private void BrowseDsh_Click(object s, RoutedEventArgs e)
	{
		FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog();
		if (folderBrowserDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
		{
			InputDshDir.Text = folderBrowserDialog.SelectedPath;
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
		string dir = InputLlamaDir.Text.Trim().Trim('"');
		return string.IsNullOrEmpty(dir) ? "D:\\llama.cpp" : dir;
	}

	private string EffectiveDshDir()
	{
		string dir = InputDshDir.Text.Trim().Trim('"');
		return string.IsNullOrEmpty(dir) ? "%DSH%" : dir;
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
					System.Windows.MessageBox.Show("Llama 服务正在运行，请先停止再更新。", "更新 llama.cpp", MessageBoxButton.OK, MessageBoxImage.Warning);
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
					System.Windows.MessageBox.Show("未找到 llama-server.exe，请先在“Llama 程序目录”填写实际路径。", "更新 llama.cpp", MessageBoxButton.OK, MessageBoxImage.Warning);
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
					System.Windows.MessageBox.Show("无法获取 llama.cpp 最新版本（网络/GitHub 不可达）。", "更新 llama.cpp", MessageBoxButton.OK, MessageBoxImage.Warning);
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
					System.Windows.MessageBox.Show("llama.cpp 已是最新版本。\n当前: build " + local.Value + "\n上游: " + upstream, "更新 llama.cpp", MessageBoxButton.OK, MessageBoxImage.Information);
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
				System.Windows.MessageBox.Show(m + "\n\n可点“⟳ 重载参数配置”加载新版本参数。", "更新 llama.cpp", MessageBoxButton.OK, MessageBoxImage.Information);
			}
			else
			{
				System.Windows.MessageBox.Show("更新失败:\n" + m, "更新 llama.cpp", MessageBoxButton.OK, MessageBoxImage.Warning);
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
					System.Windows.MessageBox.Show("当前为 npx 一键部署模式，无法 git 更新。请切换到“编译版”模式。", "更新 deepseek-harness", MessageBoxButton.OK, MessageBoxImage.Warning);
				}
				return;
			}
			if (IsDshRunning())
			{
				if (fromButton)
				{
					System.Windows.MessageBox.Show("DSH 正在运行，请先停止再更新。", "更新 deepseek-harness", MessageBoxButton.OK, MessageBoxImage.Warning);
				}
				else
				{
					Log("[更新] 后台检测到 deepseek-harness 有更新，但 DSH 正在运行，跳过（请手动停止后点“检查更新”）");
				}
				return;
			}
			string dir = EffectiveDshDir();
			if (!Directory.Exists(Path.Combine(dir, ".git")))
			{
				if (fromButton)
				{
					System.Windows.MessageBox.Show("DSH 目录不是 git 仓库: " + dir + "\n请先在“DSH 项目目录”填写实际仓库路径。", "更新 deepseek-harness", MessageBoxButton.OK, MessageBoxImage.Warning);
				}
				return;
			}
			SetDshUpdBtn(true, "⏳ 检查中...");
			Services.UpdateService.DshUpdateInfo? info = await Task.Run(() => UpdateService.CheckDshUpdate(dir));
			SetDshUpdBtn(false);
			if (info == null)
			{
				if (fromButton)
				{
					System.Windows.MessageBox.Show("无法检查 DSH 上游（git 或网络不可达）。", "更新 deepseek-harness", MessageBoxButton.OK, MessageBoxImage.Warning);
				}
				return;
			}
			if (info.Ahead > 0)
			{
				if (fromButton)
				{
					System.Windows.MessageBox.Show("本地领先上游 " + info.Ahead + " 个提交（存在本地改动），为避免冲突不会自动更新。\n请自行处理本地提交后重试。", "更新 deepseek-harness", MessageBoxButton.OK, MessageBoxImage.Warning);
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
					System.Windows.MessageBox.Show("deepseek-harness 已是最新。\n当前: " + info.LocalHead.Substring(0, 7) + " (" + info.Branch + ")", "更新 deepseek-harness", MessageBoxButton.OK, MessageBoxImage.Information);
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
			(bool ok, string m) = await Task.Run(() => UpdateService.PullDsh(dir));
			if (!ok)
			{
				SetDshUpdBtn(false);
				Log("[更新] DSH git pull 失败: " + m);
				System.Windows.MessageBox.Show("git pull 失败:\n" + m + "\n\n（可能有本地未提交改动，或上游历史已改写。请手动处理。）", "更新 deepseek-harness", MessageBoxButton.OK, MessageBoxImage.Warning);
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
				System.Windows.MessageBox.Show(BuildCompatSummary(report) + "\n\n未发现可自动修复的问题。", "DSH 兼容性检查", MessageBoxButton.OK, report.ErrorCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
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
			System.Windows.MessageBox.Show("DSH 正在运行，无法写入修复。\n\n请先在启动器中停止 DSH，再执行自动修复。", "DSH 兼容性修复", MessageBoxButton.OK, MessageBoxImage.Warning);
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
		System.Windows.MessageBox.Show("兼容性修复完成。\n\n" + string.Join("\n", res.Messages) + "\n\n" + BuildCompatSummary(res.Report), "DSH 兼容性修复", MessageBoxButton.OK, res.Report.ErrorCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
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

	private List<string> BuildCmd()
	{
		string text = InputLlamaDir.Text.Trim().Trim('"');
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
		// 参数依赖引擎：先收集所有"生效"参数，再逐个检查前置/互斥/校验，不通过则剔除并记录原因
		_lastViolations = new List<string>();
		HashSet<string> on = new HashSet<string>();
		foreach (ParamDefinition d0 in _paramDefs)
		{
			if (!d0.Hidden && d0.Flag != "model" && d0.Flag != "model-draft" && IsEffectivelyOn(d0))
			{
				on.Add(d0.Flag);
			}
		}
		foreach (ParamDefinition paramDef in _paramDefs)
		{
			string flag = paramDef.Flag;
			if (flag == "model" || flag == "model-draft" || paramDef.Hidden)
			{
				continue;
			}
			if (!on.Contains(flag))
			{
				continue;
			}
			string? reject = CheckParam(paramDef, on);
			if (reject != null)
			{
				_lastViolations.Add("--" + flag + "（" + reject + "）");
				continue;
			}
			if (paramDef.Type == ParamType.Bool)
			{
				list.Add("--" + paramDef.Flag);
			}
			else
			{
				string text4 = EffectiveValue(paramDef);
				if (paramDef.Role == "ctx_size")
				{
					text4 = NormTok(text4);
				}
				if (!string.IsNullOrEmpty(text4))
				{
					list.AddRange(new string[2]
					{
						"--" + paramDef.Flag,
						text4
					});
				}
			}
		}
		// "取消勾选=关闭"语义：对配置了 off 语义的参数，未勾选时显式发射关闭值/否定旗标，
		// 覆盖 llama-server 默认值不是"关闭"的选项（如 --no-jinja、--flash-attn off、--spec-type none）
		foreach (ParamDefinition paramDef in _paramDefs)
		{
			if (paramDef.Hidden || paramDef.Flag == "model" || paramDef.Flag == "model-draft" || on.Contains(paramDef.Flag))
			{
				continue;
			}
			if (paramDef.Type == ParamType.Bool)
			{
				if (!string.IsNullOrEmpty(paramDef.OffFlag))
				{
					list.Add("--" + paramDef.OffFlag);
				}
			}
			else if (!string.IsNullOrEmpty(paramDef.OffValue))
			{
				list.AddRange(new string[2] { "--" + paramDef.Flag, paramDef.OffValue });
			}
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
		string text = v.Trim();
		if (string.IsNullOrEmpty(text))
		{
			return text;
		}
		Match match = Regex.Match(text, "^(\\d+(?:\\.\\d+)?)\\s*([KkMm]?)$");
		if (!match.Success)
		{
			return text;
		}
		double num = double.Parse(match.Groups[1].Value);
		string text2 = match.Groups[2].Value.ToLowerInvariant();
		if (text2 == "k")
		{
			num *= 1024.0;
		}
		else if (text2 == "m")
		{
			num *= 1048576.0;
		}
		return ((int)Math.Round(num)).ToString();
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
			return 0.5625;
		case "q4_1":
			return 0.625;
		case "q5_0":
			return 0.6875;
		case "q5_1":
			return 0.75;
		case "q6_0":
			return 0.875;
		case "tq3_0":
			return 0.4375;
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
					}
					else if (key.EndsWith(".context_length", StringComparison.Ordinal))
					{
						r.ContextLen = v;
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

	private (double estBytes, double modelBytes, double kvBytes, double availBytes, string detail)? EstimateVramNeeded()
	{
		try
		{
			string modelPath = ResolveModelPath(throwOnError: false) ?? "";
			if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
			{
				return null;
			}
			double modelBytes = new FileInfo(modelPath).Length;
			if (modelBytes <= 0.0)
			{
				return null;
			}
			Match shm2 = Regex.Match(Path.GetFileName(modelPath), "-(\\d{5})-of-(\\d{5})\\.gguf$", RegexOptions.IgnoreCase);
			if (shm2.Success)
			{
				string sdir = Path.GetDirectoryName(modelPath) ?? "";
				string sbase = Path.GetFileName(modelPath);
				int dashIdx = sbase.LastIndexOf("-" + shm2.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
				if (dashIdx > 0)
				{
					sbase = sbase.Substring(0, dashIdx);
					string[] shards = Directory.GetFiles(sdir, sbase + "-*-of-*.gguf");
					foreach (string sh in shards)
					{
						if (!string.Equals(sh, modelPath, StringComparison.OrdinalIgnoreCase))
						{
							modelBytes += new FileInfo(sh).Length;
						}
					}
				}
			}
			string mmpp = InputMmprojPath.Text.Trim().Trim('"');
			bool mmCpu = (ChkNoMmprojOffload.IsChecked == true);
			if (!mmCpu && !string.IsNullOrEmpty(mmpp) && File.Exists(mmpp))
			{
				modelBytes += new FileInfo(mmpp).Length;
			}
			long ctx = 0;
			if (RoleText("ctx_size", out string ctxStr))
			{
				ctxStr = NormTok(ctxStr);
				if (!long.TryParse(ctxStr, out ctx))
				{
					ctx = 0;
				}
			}
			string ck = (RoleText("cache_type_k", out string ckv) && !string.IsNullOrEmpty(ckv)) ? ckv : "f16";
			string cv = (RoleText("cache_type_v", out string cvv) && !string.IsNullOrEmpty(cvv)) ? cvv : "f16";
			double bytesK = CacheBytesPerElem(ck);
			double bytesV = CacheBytesPerElem(cv);
			GgufArchInfo gi = ReadGgufArch(modelPath);
			double gpuFrac = 1.0;
			if (RoleText("ngl", out string ngl) && double.TryParse(ngl, out double nglV) && nglV >= 0.0 && gi.Ok && gi.Layers > 0)
			{
				gpuFrac = Math.Clamp(nglV / gi.Layers, 0.0, 1.0);
			}
			// MoE 专家在 CPU 时按经验系数折算显存中的模型占用(注意力+共享权重约占 12%)
			bool cpuMoeOn = RoleBool("cpu_moe");
			double moeFactor = 1.0;
			if (cpuMoeOn)
			{
				moeFactor = 0.12;
			}
			else if (RoleText("n_cpu_moe", out string ncm) && double.TryParse(ncm, out double ncmV) && ncmV > 0 && gi.Ok && gi.Layers > 0)
			{
				moeFactor = Math.Min(1.0, 0.12 + 0.88 * (ncmV / (double)gi.Layers));
			}
			double modelFileBytes = modelBytes;
			if (moeFactor < 1.0)
			{
				modelBytes *= moeFactor;
			}
			double kvPerToken;
			if (gi.Ok)
			{
				kvPerToken = (double)gi.KvHeads * gi.HeadDim * (bytesK + bytesV) * gi.Layers;
			}
			else
			{
				kvPerToken = 64.0 * 8.0 * 128.0 * (bytesK + bytesV);
			}
			double kvBytes = kvPerToken * ctx * gpuFrac;
			double bufferBytes = 1.5 * 1073741824.0;
			double estVram = modelBytes * gpuFrac + kvBytes + bufferBytes;
			double total = 0.0;
			double used = 0.0;
			Dictionary<string, string> g = _gpuMonitor.QueryGpu();
			if (g.TryGetValue("vram_total", out string ts) && double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out total) && total > 0.0)
			{
				if (g.TryGetValue("vram_used", out string us))
				{
					double.TryParse(us, NumberStyles.Float, CultureInfo.InvariantCulture, out used);
				}
			}
			double avail = Math.Max(0.0, total - used);
			long safeCtx = 0;
			// 部分卸载时 GPU 上只承载 kvPerToken*gpuFrac 的 KV，分母必须同步缩放，
			// 否则 safeCtx 会被低估 ~1/gpuFrac 倍；gpuFrac=0 时显存不是瓶颈，跳过该提示
			double gpuKvPerToken = kvPerToken * gpuFrac;
			if (gpuFrac > 0.0 && gpuKvPerToken > 0.0 && avail > 0.0)
			{
				safeCtx = (long)((avail - modelBytes * gpuFrac - bufferBytes) / gpuKvPerToken);
				if (safeCtx < 0)
				{
					safeCtx = 0;
				}
				safeCtx = (safeCtx / 1024L) * 1024L;
			}
			string detail = "模型文件: " + (modelFileBytes / 1073741824.0).ToString("F1") + " GiB\n"
				+ "上下文: " + ctx + " token (" + (ctx / 1024L) + "K)\n"
				+ "KV 缓存(按当前缓存类型估算): " + (kvBytes / 1073741824.0).ToString("F2") + " GiB\n"
				+ "预计总占用: " + (estVram / 1073741824.0).ToString("F1") + " GiB\n"
				+ "可用显存: " + (avail / 1073741824.0).ToString("F1") + " GiB";
			if (moeFactor < 1.0)
			{
				detail += "\nMoE 专家在 CPU: 显存按系数 " + moeFactor.ToString("F2") + " 折算";
			}
			if (safeCtx > 0)
			{
				detail += "\n建议: 当前显存下安全上下文约 " + safeCtx + " token (" + (safeCtx / 1024L) + "K)";
			}
			return (estVram, modelBytes, kvBytes, avail, detail);
		}
		catch
		{
			return null;
		}
	}

	private bool ConfirmVramFit()
	{
		(double, double, double, double, string)? est = EstimateVramNeeded();
		if (!est.HasValue)
		{
			return true;
		}
		(double estBytes, double modelBytes, double kvBytes, double availBytes, string detail) = est.Value;
		if (availBytes <= 0.0)
		{
			return true;
		}
		if (estBytes <= availBytes)
		{
			return true;
		}
		string msg = "⚠ 预计显存不足，llama-server 很可能因显存耗尽而崩溃（bad allocation / GGML_ASSERT）。\n\n"
			+ detail
			+ "\n\n仍要尝试启动吗？\n（建议：减小“上下文大小”，或将 KV 缓存类型改为 q4_0/q4_1）";
		System.Windows.MessageBoxResult r = System.Windows.MessageBox.Show(msg, "显存可能不足", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
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
		EvaluateDepUI();
		try
		{
			List<string> list = BuildCmd();
			_lastCmd = string.Join(" ", list.Select(QA));
			CmdPreview.Text = FmtCmd(list);
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
			LogError(ex, "命令构建失败");
		}
	}

	private void RefreshCmd_Click(object s, RoutedEventArgs e)
	{
		RefreshCmd();
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
			ParamGroupsPanel.Children.Clear();
			_pw.Clear();
			_pbw.Clear();
			BuildParamGroups();
			ApplyConfig(snap);
			Log("[参数] 配置已重载，来源: " + r.Source + "，参数 " + r.Defs.Count + " 项 / 组 " + r.Groups.Count + " 个");
			if (r.Errors.Count > 0 || r.Warnings.Count > 0)
			{
				System.Windows.MessageBox.Show(string.Join("\n", r.Errors.Concat(r.Warnings)), "参数配置提示", MessageBoxButton.OK, MessageBoxImage.Warning);
			}
		}
		catch (Exception ex)
		{
			LogError(ex, "重载参数配置失败");
			System.Windows.MessageBox.Show("重载失败: " + ex.Message);
		}
	}

	private void CopyCmd_Click(object s, RoutedEventArgs e)
	{
		string text = CmdPreview.Text;
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
			System.Windows.MessageBox.Show("Already running");
			return;
		}
		_vramBreakerTriggered = false;
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
			System.Windows.MessageBox.Show("Build failed:" + ex.Message);
			return;
		}
		if (list.Count == 0)
		{
			System.Windows.MessageBox.Show("Empty");
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
		if (!ConfirmVramFit())
		{
			Log("--- 启动已取消：预计显存不足 ---");
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
			BtnLlamaStart.IsEnabled = true;
			BtnLlamaStop.IsEnabled = false;
			if (exitCode != 0)
			{
				LblLlamaStatus.Text = "● 启动失败/崩溃";
				LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 54, 51));
				Log("--- Llama 异常退出 (退出码 " + exitCode + ") --- 请查看上方日志，通常是显存不足或参数错误导致 ---");
			}
			else
			{
				LblLlamaStatus.Text = "● 已停止";
				LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));
				Log("--- Llama 已退出 ---");
			}
			ClearStateFor("llama");
		}
	}

	private void StopLlama()
	{
		if (_llamaProc != null)
		{
			Log("--- 正在停止 Llama ---");
			ProcessHelper.KillProcessTree(_llamaProc.Id);
			DisposeProc(ref _llamaProc);
			BtnLlamaStart.IsEnabled = true;
			BtnLlamaStop.IsEnabled = false;
			LblLlamaStatus.Text = "● 已停止";
			LblLlamaStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));
			Log("--- 已停止 ---");
			ClearStateFor("llama");
		}
	}

	private void StartDsh_Click(object s, RoutedEventArgs e)
	{
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
			System.Windows.MessageBox.Show("仅本地目录模式支持重建。", "重建 DSH");
			return;
		}
		string dshDir = InputDshDir.Text.Trim().Trim('"');
		if (string.IsNullOrEmpty(dshDir))
		{
			dshDir = "%DSH%";
		}
		if (!Directory.Exists(Path.Combine(dshDir, "apps", "cli")))
		{
			System.Windows.MessageBox.Show("DSH 目录无效: " + dshDir, "重建 DSH");
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
			System.Windows.MessageBox.Show("无法读取仓库 git HEAD，请确认是 deepseek-harness 的 git checkout。", "重建 DSH", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}
		string msg = "将执行全量重建:\n  1/3 pnpm clean\n  2/3 pnpm build:lib\n  3/3 build:web (vite)\n"
				+ (HasDsh() ? "\nDSH 正在运行，会先停止 DSH。" : "")
				+ "\n重建完成后自动做兼容性检查并启动 DSH。\n继续？";
		if (System.Windows.MessageBox.Show(msg, "重建 DSH", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
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
			while (HasDsh() && DateTime.Now < stopDeadline)
			{
				System.Threading.Thread.Sleep(500);
			}
			if (HasDsh())
			{
				BtnRebuildDsh.IsEnabled = true;
				LblDshStatus.Text = "已停止";
				System.Windows.MessageBox.Show("DSH 进程未完全退出，请手动结束后再点重建。", "重建 DSH", MessageBoxButton.OK, MessageBoxImage.Warning);
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
				string dshDir = InputDshDir.Text.Trim().Trim('"');
				if (string.IsNullOrEmpty(dshDir))
				{
					dshDir = "%DSH%";
				}
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
			if (!File.Exists(DshMaintenance.Node24Path))
			{
				System.Windows.MessageBox.Show("未找到 node24: " + DshMaintenance.Node24Path + "\n无法自动重建，将直接启动。");
				BtnRebuildDsh.IsEnabled = true;
				StartDsh();
				return;
			}
			string logPath = Path.Combine(Path.GetTempPath(), "LlamaDSH_dsh_rebuild.log");
			string cmdPath = Path.Combine(Path.GetTempPath(), "LlamaDSH_dsh_rebuild.cmd");
			string nodeDir = Path.GetDirectoryName(DshMaintenance.Node24Path) ?? "C:\\Program Files\\nodejs";
			string q = "\"";
			string redirect = " >> " + q + logPath + q + " 2>&1";
			string cmdText = "@echo off" + Environment.NewLine
				+ "set \"PATH=" + nodeDir + ";%PATH%\"" + Environment.NewLine
				+ "cd /d " + q + dshDir + q + Environment.NewLine
				+ "echo [REBUILD] 1/3 pnpm clean" + redirect + Environment.NewLine
				+ "call pnpm clean" + redirect + Environment.NewLine
				+ "if errorlevel 1 goto :fail" + Environment.NewLine
				+ "echo [REBUILD] 2/3 pnpm build:lib" + redirect + Environment.NewLine
				+ "call pnpm build:lib" + redirect + Environment.NewLine
				+ "if errorlevel 1 goto :fail" + Environment.NewLine
				+ "echo [REBUILD] 3/3 build:web (node24 vite)" + redirect + Environment.NewLine
				+ "cd /d " + q + dshDir + "\\apps\\web" + q + Environment.NewLine
				+ q + DshMaintenance.Node24Path + q + " node_modules\\vite\\bin\\vite.js build" + redirect + Environment.NewLine
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
						System.Windows.MessageBox.Show("DSH 重建失败，详见启动器日志。\n\n常见原因: node24/pnpm 不可用、依赖安装网络问题、TS 编译错误。", "DSH 重建失败", MessageBoxButton.OK, MessageBoxImage.Warning);
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
		StopDsh();
	}

	private void StartDsh()
	{
		if (HasDsh())
		{
			System.Windows.MessageBox.Show("Already running");
			return;
		}
		try
		{
			string text2;
			string text3;
			string dshDir = "";
			if (ComboDshMode.SelectedIndex == 0)
			{
				dshDir = InputDshDir.Text.Trim().Trim('"');
				if (string.IsNullOrEmpty(dshDir))
				{
					dshDir = "%DSH%";
				}
				text3 = ((File.Exists(Path.Combine(dshDir, "apps", "cli", "lib", "bin.js")) && _dshRetriesLeft > 0) ? "compiled-js" : "compiled-tsx");
			}
			else
			{
				text3 = "npx";
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
			if (ChkDshAdmin.IsChecked == true)
			{
				string innerCmd = ((ComboDshMode.SelectedIndex == 0) ? (((text3 == "compiled-js") ? "node apps/cli/lib/bin.js web" : "pnpm dsh web")) : "npx --yes @deepseek-ai/dsh web");
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
					text2 = "cd /d \"" + dshDir + "\" && node apps/cli/lib/bin.js web";
				}
				else
				{
					if (File.Exists(Path.Combine(dshDir, "apps", "cli", "lib", "bin.js")))
					{
						Log("--- 编译版连续启动失败，本次回退到 pnpm dsh web ---");
					}
					text2 = "cd /d \"" + dshDir + "\" && pnpm dsh web";
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
			string text = ((modeLabel == "compiled-js") ? "node apps/cli/lib/bin.js web" : "pnpm dsh web");
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
			string text3 = "cmd.exe /c cd /d \"" + dshDir + "\" & echo ===CHILD-ENV=== >> \"" + text2 + "\" 2>&1 & set >> \"" + text2 + "\" 2>&1 & where node >> \"" + text2 + "\" 2>&1 & node -v >> \"" + text2 + "\" 2>&1 & echo ===BOOT=== >> \"" + text2 + "\" 2>&1 & " + text + " >> \"" + text2 + "\" 2>&1";
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
			string text3 = (string.IsNullOrEmpty(dshDir) ? "" : ("cd /d \"" + dshDir + "\" & "));
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
		string text = ((ComboDshMode.SelectedIndex == 0) ? InputDshDir.Text.Trim().Trim('"') : "");
		if (string.IsNullOrEmpty(text))
		{
			text = "%DSH%";
		}
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
			string dd = ((ComboDshMode.SelectedIndex == 0) ? InputDshDir.Text.Trim().Trim('"') : "%DSH%");
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
		StopDshVerifyLoop();
		StopDshLogTailer();
		// 提权模式下 _dshProc 恒为 null，被托管的 pid 在 _dshTracker 里；先记下来供 UIPI 回退使用
		int? adminKillPid = (_dshTracker?.Pid ?? _dshProc?.Id); // 接管来的提权进程同样需要 UIPI 回退
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
		BtnDshStart.IsEnabled = true;
		BtnDshStop.IsEnabled = false;
		BtnOpenDsh.IsEnabled = false;
		LblDshStatus.Text = "● 已停止";
		LblDshStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));
		Log("--- 已停止 DSH ---");
		ClearStateFor("dsh");
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
			LogArea.AppendText(stringBuilder.ToString());
			TruncateLogIfNeeded();
			if (ChkAutoScroll.IsChecked == true)
			{
				LogArea.ScrollToEnd();
			}
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
			LogArea.AppendText(text + Environment.NewLine);
			AppendRunLog($"[{DateTime.Now:HH:mm:ss}] {text}");
			TruncateLogIfNeeded();
			if (ChkAutoScroll.IsChecked == true)
			{
				LogArea.ScrollToEnd();
			}
		}
	}

	private void ClearLog_Click(object s, RoutedEventArgs e)
	{
		LogArea.Clear();
	}

	private void ParseTimings(string text)
	{
		if (text.IndexOf("eval time", StringComparison.Ordinal) < 0)
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
			Match match3 = _reEvalTps.Match(input);
			if (match3.Success)
			{
				AddS(_tpsSamples, double.Parse(match3.Groups[1].Value), 20);
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Func<string>)(() => PerfTps.Text = FT(_tpsSamples.ToArray().Average()) + " tok/s"), Array.Empty<object>());
				continue;
			}
			Match match4 = _reEvalRaw.Match(input);
			if (!match4.Success)
			{
				continue;
			}
			double num3 = double.Parse(match4.Groups[1].Value);
			int num4 = int.Parse(match4.Groups[2].Value);
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
					if (num % 2 == 0)
					{
						foreach (KeyValuePair<string, string> item in _gpuMonitor.QueryGpu())
						{
							d[item.Key] = item.Value;
						}
					}
					if (IsAlive(_llamaProc) && _tpsSamples.IsEmpty && _ttftSamples.IsEmpty)
					{
						(double, double)? tuple = FetchLlamaMetrics();
						if (tuple.HasValue)
						{
							d["httptps"] = tuple.Value.Item1.ToString("F2", CultureInfo.InvariantCulture);
							d["httppf"] = tuple.Value.Item2.ToString("F2", CultureInfo.InvariantCulture);
						}
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
				Thread.Sleep(2000);
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
			}
			double num5 = ((num4 > 0.0) ? (num3 / num4) : 0.0);
			double num6 = ((num2 > 0.0) ? (num / num2) : 0.0);
			if (num5 > 0.0 || num6 > 0.0)
			{
				return (num5, num6);
			}
		}
		catch
		{
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

	private void OnPerf(Dictionary<string, string> d)
	{
		if (d.TryGetValue("cpu", out string value))
		{
			PerfCpu.Text = FP(value);
		}
		if (d.TryGetValue("mem", out string value2))
		{
			PerfMem.Text = FP(value2);
		}
		if (d.TryGetValue("gpu_util", out string value3))
		{
			if (double.TryParse(value3, out var result))
			{
				PerfGpu.Text = $"{Math.Min(100.0, Math.Max(0.0, result)):F0}%";
			}
			else
			{
				PerfGpu.Text = "N/A";
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
			}
			else
			{
				PerfVram.Text = "N/A";
				PerfVram.ToolTip = null;
			}
		}
		else if (d.TryGetValue("vram_total", out value7) && double.TryParse(value7, out result4))
		{
			double value8 = result4 / 1073741824.0;
			PerfVram.Text = $"{value8:F0}G";
			PerfVram.ToolTip = $"显存总量 {value8:F1} GB";
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
			}
			else
			{
				PerfGpuTemp.Text = "N/A";
				PerfGpuTemp.ToolTip = null;
			}
		}
		if (d.TryGetValue("httptps", out string value12) && double.TryParse(value12, NumberStyles.Float, CultureInfo.InvariantCulture, out var result6) && result6 > 0.0)
		{
			PerfTps.Text = FT(result6) + " tok/s";
		}
		if (d.TryGetValue("httppf", out string value13) && double.TryParse(value13, NumberStyles.Float, CultureInfo.InvariantCulture, out var result7) && result7 > 0.0)
		{
			PerfPrefill.Text = FT(result7) + " tok/s";
		}
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
				LblVramBreaker.Foreground = System.Windows.Media.Brushes.OrangeRed;
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
		static string FP(string v)
		{
			if (double.TryParse(v, out var result9))
			{
				return $"{result9:F1}%";
			}
			return "N/A";
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
			string dd = ((ComboDshMode.SelectedIndex == 0) ? InputDshDir.Text.Trim().Trim('"') : "%DSH%");
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
		string dd = stateFile?.DshDir ?? InputDshDir.Text.Trim().Trim('"');
		if (string.IsNullOrEmpty(dd))
		{
			dd = "%DSH%";
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
		FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog
		{
			SelectedPath = _configDir
		};
		if (folderBrowserDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
		{
			_configDir = folderBrowserDialog.SelectedPath;
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
			System.Windows.MessageBox.Show("Save failed:" + ex.Message);
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
				System.Windows.MessageBox.Show("Export failed:" + ex.Message);
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
			System.Windows.MessageBox.Show("Load failed:" + ex2.Message);
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
				System.Windows.MessageBox.Show("已存在同名方案", "提示");
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
				System.Windows.MessageBox.Show("已存在同名方案", "提示");
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
			System.Windows.MessageBox.Show("至少需要保留一个方案", "提示");
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

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
		AutoLoadConfig();
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
