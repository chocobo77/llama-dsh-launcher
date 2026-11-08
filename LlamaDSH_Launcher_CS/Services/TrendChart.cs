using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// 多序列时间趋势折线图(2026-11-04 起为独立实现):
/// 头部=标题+图例(序列名:最新值)+单位; 绘图区 OnRender 直接绘制
/// 三条水平参考线、按样本时间成比例的折线与面积渐变; 底部三个时间刻度;
/// 悬停显示十字线与"序列 值 · -Ns"读数。yMin/yMax 传 NaN 表示自动量程。
/// </summary>
public class TrendChart : Grid
{
	private const double LineThickness = 1.5;

	private readonly TextBlock _titleText = new();
	private readonly TextBlock _unitText = new();
	private readonly ItemsControl _legendItems = new();
	private readonly PlotLayer _plot = new();
	private readonly TextBlock _footLeft = new();
	private readonly TextBlock _footMid = new();
	private readonly TextBlock _footRight = new();
	private readonly TextBlock _tip = new();

	private List<(string name, Color color, double?[] data)> _series = new();
	private DateTime[] _stamps = Array.Empty<DateTime>();
	private double? _floor;
	private double? _ceil;
	private string _unit = "";

	/// <summary>序列各点的采样时刻(与数据等长); 缺省则按等间距渲染。</summary>
	public DateTime[] StampsRef { get; set; } = Array.Empty<DateTime>();

	public string Title
	{
		get => _titleText.Text;
		set => _titleText.Text = value ?? "";
	}

	public string Unit
	{
		get => _unit;
		set
		{
			_unit = value ?? "";
			_unitText.Text = _unit;
		}
	}

	public TrendChart()
	{
		RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		var header = new Grid();
		header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		_titleText.FontWeight = FontWeights.SemiBold;
		_titleText.FontSize = 12.5;
		_titleText.Foreground = BrushFrom(0xe6, 0xea, 0xef);
		_titleText.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetColumn(_titleText, 0);
		_legendItems.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
		_legendItems.IsHitTestVisible = false;
		_legendItems.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetColumn(_legendItems, 1);
		_unitText.FontSize = 10.5;
		_unitText.Foreground = BrushFrom(0x8b, 0x94, 0x9e);
		_unitText.Margin = new Thickness(6, 0, 0, 0);
		_unitText.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetColumn(_unitText, 2);
		header.Children.Add(_titleText);
		header.Children.Add(_legendItems);
		header.Children.Add(_unitText);
		header.Margin = new Thickness(2, 0, 2, 3);
		SetRow(header, 0);

		_plot.Focusable = false;
		SetRow(_plot, 1);

		var footer = new Grid { Margin = new Thickness(2, 2, 2, 0) };
		for (int i = 0; i < 3; i++)
		{
			footer.ColumnDefinitions.Add(new ColumnDefinition());
		}
		_footLeft.FontSize = _footMid.FontSize = _footRight.FontSize = 9.5;
		_footLeft.Foreground = _footMid.Foreground = _footRight.Foreground = BrushFrom(0x77, 0x80, 0x8a);
		_footLeft.FontFamily = _footMid.FontFamily = _footRight.FontFamily = new FontFamily("Consolas");
		_footMid.TextAlignment = TextAlignment.Center;
		_footRight.TextAlignment = TextAlignment.Right;
		Grid.SetColumn(_footLeft, 0);
		Grid.SetColumn(_footMid, 1);
		Grid.SetColumn(_footRight, 2);
		footer.Children.Add(_footLeft);
		footer.Children.Add(_footMid);
		footer.Children.Add(_footRight);
		SetRow(footer, 2);

		_tip.IsHitTestVisible = false;
		_tip.Visibility = Visibility.Collapsed;
		_tip.FontSize = 10.5;
		_tip.FontFamily = new FontFamily("Consolas");
		_tip.Foreground = BrushFrom(0xf0, 0xf3, 0xf6);
		_tip.Background = new SolidColorBrush(Color.FromArgb(0xd9, 0x14, 0x1a, 0x21));
		_tip.Padding = new Thickness(5, 2, 5, 2);
		_tip.HorizontalAlignment = HorizontalAlignment.Left;
		_tip.VerticalAlignment = VerticalAlignment.Top;
		_tip.Margin = new Thickness(8, 20, 0, 0);
		SetRow(_tip, 1);
		Panel.SetZIndex(_tip, 9);

		_plot.HoverIndex = -1;
		_plot.PointHovered += (_, idx) => { _plot.HoverIndex = idx; RefreshTip(); };
		_plot.PointLeft += (_, _) => { _plot.HoverIndex = -1; _tip.Visibility = Visibility.Collapsed; _plot.InvalidateVisual(); };

		Children.Add(header);
		Children.Add(_plot);
		Children.Add(footer);
		Children.Add(_tip);
	}

	/// <summary>设置序列(等长数组, null 表示该时刻无样本、断线)。yMin/yMax 传 NaN 走自动量程。</summary>
	public void SetSeries(string unit, List<(string name, Color color, double?[] data)> series, double yMin, double yMax)
	{
		Unit = unit;
		_series = series ?? new List<(string, Color, double?[])>();
		_stamps = StampsRef ?? Array.Empty<DateTime>();
		if (_stamps.Length < 2)
		{
			_stamps = Array.Empty<DateTime>();
		}
		_floor = double.IsNaN(yMin) ? null : yMin;
		_ceil = double.IsNaN(yMax) ? null : yMax;
		_plot.Bind(_series, _stamps, _floor, _ceil);
		RebuildLegend();
		RebuildFooter();
		RefreshTip();
	}

	private void RebuildLegend()
	{
		var items = new List<UIElement>();
		foreach ((string name, Color color, double?[] data) in _series)
		{
			var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0) };
			sp.Children.Add(new System.Windows.Shapes.Ellipse
			{
				Width = 7,
				Height = 7,
				Fill = new SolidColorBrush(color),
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0, 0, 4, 0)
			});
			double? latest = null;
			for (int i = data.Length - 1; i >= 0 && latest == null; i--)
			{
				latest = data[i];
			}
			sp.Children.Add(new TextBlock
			{
				Text = name + " " + (latest.HasValue ? latest.Value.ToString("0.##") : "—"),
				FontSize = 10.5,
				Foreground = BrushFrom(0xc3, 0xca, 0xd2),
				VerticalAlignment = VerticalAlignment.Center
			});
			items.Add(sp);
		}
		_legendItems.Items.Clear();
		foreach (UIElement el in items)
		{
			_legendItems.Items.Add(el);
		}
	}

	private void RebuildFooter()
	{
		if (_stamps.Length >= 2 && (_stamps[^1] - _stamps[0]).TotalSeconds > 1)
		{
			int span = (int)Math.Round((_stamps[^1] - _stamps[0]).TotalSeconds);
			_footLeft.Text = "-" + span + "s";
			_footMid.Text = "-" + (span / 2) + "s";
			_footRight.Text = "now";
		}
		else
		{
			_footLeft.Text = "-60s";
			_footMid.Text = "-30s";
			_footRight.Text = "now";
		}
	}

	private void RefreshTip()
	{
		int idx = _plot.HoverIndex;
		if (idx < 0 || _series.Count == 0)
		{
			_tip.Visibility = Visibility.Collapsed;
			return;
		}
		var parts = new List<string>();
		foreach ((string name, _, double?[] data) in _series)
		{
			string v = idx >= 0 && idx < data.Length && data[idx].HasValue ? data[idx]!.Value.ToString("0.##") : "—";
			parts.Add(name + " " + v + (Unit.Length > 0 ? " " + Unit : ""));
		}
		string when = "now";
		if (_stamps.Length >= 2 && idx < _stamps.Length)
		{
			int ago = (int)Math.Max(0, Math.Round((_stamps[^1] - _stamps[idx]).TotalSeconds));
			when = ago > 0 ? "-" + ago + "s" : "now";
		}
		_tip.Text = string.Join("   ", parts) + "   " + when;
		_tip.Visibility = Visibility.Visible;
	}

	private static SolidColorBrush BrushFrom(byte r, byte g, byte b)
	{
		return new SolidColorBrush(Color.FromRgb(r, g, b));
	}

	/// <summary>实际绘图画布: OnRender 里完成网格/面积/折线/悬停十字线, 并做样本命中检测。</summary>
	private sealed class PlotLayer : FrameworkElement
	{
		private List<(string name, Color color, double?[] data)> _series = new();
		private DateTime[] _stamps = Array.Empty<DateTime>();
		private double? _floor;
		private double? _ceil;
		private float[] _xs = Array.Empty<float>();
		private bool _hasData;

		public int HoverIndex { get; set; } = -1;

		public event EventHandler<int>? PointHovered;
		public event EventHandler? PointLeft;

		public void Bind(List<(string, Color, double?[])> series, DateTime[] stamps, double? floor, double? ceil)
		{
			_series = series;
			_stamps = stamps;
			_floor = floor;
			_ceil = ceil;
			_hasData = false;
			foreach ((_, _, double?[] d) in _series)
			{
				if (d.Length > 0)
				{
					_hasData = true;
					break;
				}
			}
			InvalidateVisual();
		}

		protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
		{
			base.OnMouseLeftButtonDown(e);
			e.Handled = true;
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			if (_xs.Length == 0)
			{
				return;
			}
			double best = double.MaxValue;
			int pick = 0;
			for (int i = 0; i < _xs.Length; i++)
			{
				double dist = Math.Abs(_xs[i] - e.GetPosition(this).X);
				if (dist < best)
				{
					best = dist;
					pick = i;
				}
			}
			PointHovered?.Invoke(this, pick);
		}

		protected override void OnMouseLeave(MouseEventArgs e)
		{
			base.OnMouseLeave(e);
			PointLeft?.Invoke(this, EventArgs.Empty);
		}

		protected override void OnRender(DrawingContext dc)
		{
			double w = ActualWidth;
			double h = ActualHeight;
			if (w < 8 || h < 8 || !_hasData)
			{
				_xs = Array.Empty<float>();
				return;
			}

			int len = 0;
			foreach ((_, _, double?[] d) in _series)
			{
				len = Math.Max(len, d.Length);
			}
			if (len == 0)
			{
				return;
			}

			// 量程: 固定值优先; 自动量程取数据 min/max 并留 6% 呼吸空间
			double y0 = _floor ?? double.MaxValue;
			double y1 = _ceil ?? double.MinValue;
			if (_floor == null || _ceil == null)
			{
				double dMin = double.MaxValue;
				double dMax = double.MinValue;
				foreach ((_, _, double?[] d) in _series)
				{
					foreach (double? v in d)
					{
						if (v.HasValue)
						{
							dMin = Math.Min(dMin, v.Value);
							dMax = Math.Max(dMax, v.Value);
						}
					}
				}
				if (dMin > dMax)
				{
					return;
				}
				if (dMax - dMin < 1e-9)
				{
					dMax = dMin + 1;
				}
				double pad = (dMax - dMin) * 0.06;
				y0 = Math.Min(y0, dMin - pad);
				y1 = Math.Max(y1, dMax + pad);
			}
			if (y1 <= y0)
			{
				y1 = y0 + 1;
			}

			// x 坐标: 有采样时刻则按真实时间成比例, 否则等间距
			_xs = new float[len];
			bool timed = _stamps.Length == len && len > 1 && (_stamps[^1] - _stamps[0]).TotalSeconds > 0.01;
			double span = timed ? Math.Max(1e-6, (_stamps[^1] - _stamps[0]).TotalSeconds) : 1.0;
			for (int i = 0; i < len; i++)
			{
				_xs[i] = timed
					? (float)((_stamps[i] - _stamps[0]).TotalSeconds / span * w)
					: (float)(len == 1 ? w / 2 : i / (double)(len - 1) * w);
			}

			double Y(double v) => h - (v - y0) / (y1 - y0) * h;

			var penGrid = new Pen(new SolidColorBrush(Color.FromArgb(0x26, 0x93, 0xa7, 0xc9)), 1.0);
			penGrid.Freeze();
			for (int g = 1; g <= 3; g++)
			{
				double gy = h * g / 4.0;
				dc.DrawLine(penGrid, new Point(0, gy), new Point(w, gy));
			}

			foreach ((_, Color color, double?[] data) in _series)
			{
				var stroke = new SolidColorBrush(color);
				stroke.Freeze();
				var pen = new Pen(stroke, LineThickness) { LineJoin = PenLineJoin.Round };
				pen.Freeze();

				// null 断点切段; 每段独立画 面积 + 折线
				int i = 0;
				while (i < data.Length)
				{
					if (!data[i].HasValue)
					{
						i++;
						continue;
					}
					int j = i;
					while (j < data.Length && data[j].HasValue)
					{
						j++;
					}
					if (j - i == 1)
					{
						dc.DrawEllipse(stroke, null, new Point(_xs[i], Y(data[i]!.Value)), 1.8, 1.8);
					}
					else
					{
						var line = new StreamGeometry();
						var area = new StreamGeometry();
						using (StreamGeometryContext g = line.Open())
						{
							g.BeginFigure(new Point(_xs[i], Y(data[i]!.Value)), false, false);
							for (int k = i + 1; k < j; k++)
							{
								g.LineTo(new Point(_xs[k], Y(data[k]!.Value)), true, false);
							}
						}
						using (StreamGeometryContext g = area.Open())
						{
							g.BeginFigure(new Point(_xs[i], h), true, true);
							g.LineTo(new Point(_xs[i], Y(data[i]!.Value)), true, false);
							for (int k = i + 1; k < j; k++)
							{
								g.LineTo(new Point(_xs[k], Y(data[k]!.Value)), true, false);
							}
							g.LineTo(new Point(_xs[j - 1], h), true, false);
						}
						var fill = new LinearGradientBrush(Color.FromArgb(0x33, color.R, color.G, color.B),
							Color.FromArgb(0x05, color.R, color.G, color.B), 90);
						fill.Freeze();
						dc.DrawGeometry(fill, null, area);
						dc.DrawGeometry(null, pen, line);
					}
					i = j;
				}
			}

			if (HoverIndex >= 0 && HoverIndex < _xs.Length)
			{
				var cross = new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0x9a, 0xa6, 0xb2)), 1.0)
				{
					DashStyle = DashStyles.Dash
				};
				cross.Freeze();
				dc.DrawLine(cross, new Point(_xs[HoverIndex], 0), new Point(_xs[HoverIndex], h));
			}
		}
	}
}
