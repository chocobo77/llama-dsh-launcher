using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// llama-lens TrendChart 的 WPF 复刻: 标题+单位行, 多序列折线(1.5px, 面积渐隐),
/// 3 条横向网格线, 底部 -60s/-30s/now, 右上图例=序列名:当前值; 悬停读值。
/// </summary>
public class TrendChart : Grid
{
	private readonly TextBlock _title = new();
	private string _titleText = "";
	private readonly TextBlock _unit = new();
	private readonly StackPanel _legend = new();
	private readonly Grid _plot = new();
	private readonly List<Shape?> _grid = new();
	private readonly List<Path> _lines = new();
	private readonly List<Path> _areas = new();
	private Line? _crosshair;
	private TextBlock _hoverTip = new();
	private double? _yMin;
	private double? _yMax;
	private string _unitText = "";
	private List<(string name, Color color, double?[] data)> _series = new();
	private DateTime[] _stampsForFoot = Array.Empty<DateTime>();
	private int _hover = -1;

	public TrendChart()
	{
		Init();
	}

	public TrendChart(string title, string unit, double height = 170)
	{
		Init();
		Title = title;
		Unit = unit;
		Height = height;
	}

	public DateTime[] StampsRef { get; set; } = Array.Empty<DateTime>();

	public string Title
	{
		get => _titleText;
		set { _titleText = value; _title.Text = value; }
	}

	public string Unit
	{
		get => _unitText;
		set => _unitText = value;
	}

	private void Init()
	{
		RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
		RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

		var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
		_title.Text = _titleText;
		_title.FontSize = 12;
		_title.FontWeight = FontWeights.Bold;
		_title.Foreground = new SolidColorBrush(Color.FromRgb(0xee, 0xf1, 0xf3));
		_unit.Text = Unit;
		_unit.FontSize = 10;
		_unit.Foreground = new SolidColorBrush(Color.FromRgb(0x8e, 0x97, 0x9f));
		_legend.Orientation = Orientation.Horizontal;
		DockPanel.SetDock(_legend, Dock.Right);
		DockPanel.SetDock(_unit, Dock.Right);
		head.Children.Add(_legend);
		head.Children.Add(_unit);
		head.Children.Add(_title);
		SetRow(head, 0);

		_plot.Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));
		_plot.ClipToBounds = true;
		for (int i = 0; i < 3; i++)
		{
			var ln = new Line { Stroke = new SolidColorBrush(Color.FromArgb(0x1f, 0x8f, 0xa3, 0xc8)), StrokeThickness = 1, X1 = 0, X2 = 100 };
			_grid.Add(ln);
			_plot.Children.Add(ln);
		}
		_crosshair = new Line { Y1 = 0, Y2 = 100, Stroke = new SolidColorBrush(Color.FromRgb(0x8e, 0x97, 0x9f)), StrokeThickness = 1, Opacity = 0.35, Visibility = Visibility.Collapsed, Stretch = Stretch.None };
		_plot.Children.Add(_crosshair);
		_hoverTip = new TextBlock
		{
			FontSize = 10,
			FontFamily = new FontFamily("Consolas"),
			Background = new SolidColorBrush(Color.FromArgb(0xe0, 0x16, 0x1b, 0x22)),
			Padding = new Thickness(4, 1, 4, 1),
			Foreground = new SolidColorBrush(Color.FromRgb(0xee, 0xf1, 0xf3)),
			Visibility = Visibility.Collapsed,
			VerticalAlignment = VerticalAlignment.Top,
			HorizontalAlignment = HorizontalAlignment.Left,
			Margin = new Thickness(6, 2, 0, 0),
			IsHitTestVisible = false
		};
		System.Windows.Controls.Panel.SetZIndex(_hoverTip, 10);
		_plot.Children.Add(_hoverTip);
		_plot.MouseMove += OnMove;
		_plot.MouseLeave += (_, _) => { _hover = -1; ShowHover(); };
		SetRow(_plot, 1);

		var foot = new Grid { Margin = new Thickness(0, 3, 0, 0) };
		_footLabels = new TextBlock[3];
		for (int i = 0; i < 3; i++)
		{
			var tb = new TextBlock { FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x8e, 0x97, 0x9f)), FontFamily = new FontFamily("Consolas") };
			_footLabels[i] = tb;
			Grid.SetColumn(tb, i);
			if (i == 1)
			{
				tb.HorizontalAlignment = HorizontalAlignment.Center;
			}
			if (i == 2)
			{
				tb.HorizontalAlignment = HorizontalAlignment.Right;
			}
			foot.ColumnDefinitions.Add(new ColumnDefinition());
			foot.Children.Add(tb);
		}
		SetRow(foot, 2);

		Children.Add(head);
		Children.Add(_plot);
		Children.Add(foot);
	}

	private void OnMove(object sender, MouseEventArgs e)
	{
		if (_series.Count == 0)
		{
			return;
		}
		var pos = e.GetPosition(_plot);
		double w = _plot.ActualWidth;
		if (w <= 0)
		{
			return;
		}
		int maxLen = MaxLen();
		// 时间映射下按最近样本点反查索引
		double frac = Math.Max(0, Math.Min(1, pos.X / w));
		int idx = 0;
		double bestD = double.MaxValue;
		for (int i = 0; i < maxLen; i++)
		{
			double xi = _stampsForFoot.Length == maxLen && maxLen > 1
				&& (_stampsForFoot[^1] - _stampsForFoot[0]).TotalSeconds > 0.01
				? (_stampsForFoot[i] - _stampsForFoot[0]).TotalSeconds
				/ (_stampsForFoot[^1] - _stampsForFoot[0]).TotalSeconds * w
				: maxLen > 1 ? i / (double)(maxLen - 1) * w : 0;
			double d = Math.Abs(xi - frac * w);
			if (d < bestD)
			{
				bestD = d;
				idx = i;
			}
		}
		_hover = idx;
		if (_crosshair != null)
		{
			_crosshair.Visibility = Visibility.Visible;
			_crosshair.X1 = pos.X;
			_crosshair.X2 = pos.X;
			_crosshair.Y1 = 0;
			_crosshair.Y2 = _plot.ActualHeight;
		}
		ShowHover();
	}

	private void ShowHover()
	{
		if (_crosshair != null && _hover < 0)
		{
			_crosshair.Visibility = Visibility.Collapsed;
		}
		if (_hover < 0 || _series.Count == 0)
		{
			_hoverTip.Visibility = Visibility.Collapsed;
			return;
		}
		var parts = new List<string>();
		foreach (var (name, _, data) in _series)
		{
			double? v = idx(data, _hover);
			parts.Add($"{name} {(v.HasValue ? v.Value.ToString("0.#") : "—")}");
		}
		// 读数时间标签按真实时间跨度(混合采样下 60-idx 恒等式不成立)
		string agoTip;
		if (_stampsForFoot.Length > 1 && _hover < _stampsForFoot.Length)
		{
			int ago = (int)Math.Max(0, (_stampsForFoot[^1] - _stampsForFoot[_hover]).TotalSeconds);
			agoTip = ago > 0 ? $"  · -{ago}s" : "  · now";
		}
		else
		{
			agoTip = _hover >= 55 ? "  · now" : $"  · -{60 - _hover}s";
		}
		_hoverTip.Text = string.Join("  ", parts) + agoTip;
		_hoverTip.Visibility = Visibility.Visible;
	}

	private static double? idx(double?[] a, int i)
	{
		return i >= 0 && i < a.Length ? a[i] : null;
	}

	/// <summary>设置序列(各序列等长 ≤60 点)。yMin/yMax 固定量程, NaN 自动。</summary>
	public void SetSeries(string unit, List<(string name, Color color, double?[] data)> series, double yMin, double yMax)
	{
		_unitText = unit;
		_series = series ?? new();
		_stampsForFoot = _series.Count > 0 ? StampsRef ?? Array.Empty<DateTime>() : Array.Empty<DateTime>();
		_yMin = double.IsNaN(yMin) ? null : yMin;
		_yMax = double.IsNaN(yMax) ? null : yMax;
		_unit.Text = unit;
		Rebuild();
		UpdateLegend();
	}

	private void UpdateLegend()
	{
		_legend.Children.Clear();
		int i = _hover;
		foreach (var (name, color, data) in _series)
		{
			double? v = i >= 0 ? idx(data, i) : LastVal(data);
			var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
			sp.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
			sp.Children.Add(new TextBlock
			{
				Text = $" {name} {(v.HasValue ? v.Value.ToString("0.#") : "—")}{(_unitText.Length > 0 ? " " + _unitText : "")}",
				FontSize = 10,
				FontFamily = new FontFamily("Consolas"),
				Foreground = new SolidColorBrush(Color.FromRgb(0xc7, 0xcd, 0xd3)),
				VerticalAlignment = VerticalAlignment.Center
			});
			_legend.Children.Add(sp);
		}
	}

	private static double? LastVal(double?[] a)
	{
		for (int i = a.Length - 1; i >= 0; i--)
		{
			if (a[i].HasValue)
			{
				return a[i];
			}
		}
		return null;
	}

	private Line? _baseline;
	private TextBlock[]? _footLabels;

	private int MaxLen()
	{
		int n = 0;
		foreach (var (_, _, d) in _series)
		{
			if (d.Length > n)
			{
				n = d.Length;
			}
		}
		return Math.Max(n, 1);
	}

	private void Rebuild()
	{
		// 窗口标签按实际时间跨度(采样间隔自适应)
		if (_footLabels != null && _series.Count > 0)
		{
			var stamps = _series[0].data.Length >= 0 ? _stampsForFoot : null;
			double windowSec = 60;
			if (stamps != null && stamps.Length >= 2)
			{
				windowSec = Math.Max(1.0, (stamps[^1] - stamps[0]).TotalSeconds);
			}
			_footLabels[0].Text = $"-{windowSec:F0}s";
			_footLabels[1].Text = $"-{windowSec / 2:F0}s";
			_footLabels[2].Text = "now";
		}
		double w = _plot.ActualWidth;
		double h = _plot.ActualHeight;
		if (w <= 0 || h <= 0)
		{
			return;
		}
		if (_baseline != null)
		{
			_plot.Children.Remove(_baseline);
			_baseline = null;
		}
		for (int i = 0; i < _grid.Count; i++)
		{
			if (_grid[i] is Line ln)
			{
				double frac = (i + 1) / 4.0;
				ln.X1 = 0;
				ln.X2 = w;
				ln.Y1 = ln.Y2 = h * frac;
			}
		}
		double top = _yMax ?? 0, bot = _yMin ?? 0;
		if (!_yMax.HasValue || !_yMin.HasValue)
		{
			foreach (var (_, _, data) in _series)
			{
				foreach (var v in data)
				{
					if (!v.HasValue)
					{
						continue;
					}
					if (!_yMax.HasValue && v.Value > top)
					{
						top = v.Value;
					}
					if (!_yMin.HasValue && (top == 0 || v.Value < bot))
					{
						bot = Math.Min(0, v.Value);
					}
				}
			}
		}
		double span = top - bot;
		if (span <= 1e-9)
		{
			top = bot + 1;
			span = 1;
		}
		// 清旧线
		foreach (var p in _lines)
		{
			_plot.Children.Remove(p);
		}
		foreach (var p in _areas)
		{
			_plot.Children.Remove(p);
		}
		_lines.Clear();
		_areas.Clear();
		int maxLen = MaxLen();
		bool timeX = _stampsForFoot.Length == maxLen && maxLen > 1
			&& (_stampsForFoot[^1] - _stampsForFoot[0]).TotalSeconds > 0.01;
		double stampSpan = timeX ? (_stampsForFoot[^1] - _stampsForFoot[0]).TotalSeconds : 0;
		foreach (var (name, color, data) in _series)
		{
			var line = new Path
			{
				Stroke = new SolidColorBrush(color),
				StrokeThickness = 1.5,
				StrokeLineJoin = PenLineJoin.Round,
				StrokeStartLineCap = PenLineCap.Round,
				StrokeEndLineCap = PenLineCap.Round
			};
			var area = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x14, color.R, color.G, color.B)) };
			var geo = new StreamGeometry();
			var ageo = new StreamGeometry();
			bool any = false;
			using (var ctx = geo.Open())
			using (var actx = ageo.Open())
			{
				int start = -1;
				for (int i = 0; i <= data.Length; i++)
				{
					bool valid = i < data.Length && data[i].HasValue;
					if (valid && start < 0)
					{
						start = i;
					}
					if (!(start >= 0 && (!valid || i == data.Length - 1)))
					{
						continue;
					}
					int end = valid ? i : i - 1;
					if (end > start)
					{
						// 时间戳有效时按真实时间映射 x(混合采样节奏不失真)
						double XAt(int k) => timeX
							? (_stampsForFoot[k] - _stampsForFoot[0]).TotalSeconds / stampSpan * w
							: maxLen > 1 ? k / (double)(maxLen - 1) * w : 0;
						Point P(int k) => new Point(XAt(k), h - ((data[k]!.Value - bot) / span) * (h - 6) - 3);
						ctx.BeginFigure(P(start), false, false);
						actx?.BeginFigure(P(start), false, false);
						for (int j = start + 1; j <= end; j++)
						{
							var pj = P(j);
							ctx.LineTo(pj, true, false);
							actx?.LineTo(pj, true, false);
						}
						actx?.LineTo(new Point(P(end).X, h), true, false);
						actx?.LineTo(new Point(P(start).X, h), true, false);
						any = true;
					}
					start = -1;
				}
			}
			if (!any)
			{
				continue;
			}
			geo.Freeze();
			ageo.Freeze();
			line.Data = geo;
			area.Data = ageo;
			_plot.Children.Add(area);
			_plot.Children.Add(line);
			_areas.Add(area);
			_lines.Add(line);
		}
		if (_lines.Count == 0)
		{
			// 空态虚线基线(受管, 防 Rebuild 累积)
			_baseline = new Line { X1 = 0, Y1 = h - 3, X2 = w, Y2 = h - 3, Stroke = new SolidColorBrush(Color.FromRgb(0x2a, 0x30, 0x36)), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } };
			_plot.Children.Add(_baseline);
		}
		ShowHover();
	}
}
