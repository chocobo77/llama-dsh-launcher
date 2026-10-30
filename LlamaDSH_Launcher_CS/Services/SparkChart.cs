using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// Strata ReferenceSpark 的 WPF 复刻: 100×32 几何(y=30−v/max×26), 12% 面积填充 + 1.6px 线,
/// 断点分段(传感器缺口), 空态虚线基线, 悬停十字线 + 读数条("解码 X t/s · 预填充 Y t/s · Ns前")。
/// </summary>
public class SparkChart : Grid
{
	private Path? _area;
	private Path? _line;
	private Path? _area2;
	private Path? _line2;
	private Line? _emptyLine;
	private Line? _crosshair;
	private TextBlock? _readout;

	private double?[] _primary = Array.Empty<double?>();
	private double?[] _secondary = Array.Empty<double?>();
	private DateTime[] _stamps = Array.Empty<DateTime>();
	private double _max = double.NaN;
	private string _unit = "";
	private Color _tone = Color.FromRgb(0x34, 0xd3, 0x99);
	private Color _tone2 = Color.FromRgb(0x2e, 0xa5, 0x6c);
	private string _label = "";
	private string _label2 = "";
	private int _hover = -1;

	public SparkChart()
	{
		RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
		// 读数条为悬浮覆盖: 绝不参与布局(否则悬停→增高→鼠标脱离→收起→死循环), 底色保证可读
		_readout = new TextBlock
		{
			FontSize = 11,
			Foreground = new SolidColorBrush(Color.FromRgb(0xee, 0xf1, 0xf3)),
			Background = new SolidColorBrush(Color.FromArgb(0xe0, 0x16, 0x1b, 0x22)),
			Padding = new Thickness(4, 1, 4, 1),
			Visibility = Visibility.Collapsed,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(2, -20, 2, 0),
			IsHitTestVisible = false
		};
		System.Windows.Controls.Panel.SetZIndex(_readout, 10);
		var host = new Grid { ClipToBounds = false, Background = Brushes.Transparent };
		_emptyLine = new Line { X1 = 0, Y1 = 30, X2 = 100, Y2 = 30, Stroke = new SolidColorBrush(Color.FromRgb(0x2a, 0x30, 0x36)), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }, Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
		_area = new Path { Fill = new SolidColorBrush(Color.FromArgb(31, 0x34, 0xd3, 0x99)), StrokeThickness = 0 };
		_line = new Path { Stroke = new SolidColorBrush(_tone), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
		_area2 = new Path { Fill = Brushes.Transparent, StrokeThickness = 0, Visibility = Visibility.Collapsed };
		_line2 = new Path { Stroke = new SolidColorBrush(_tone2), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Visibility = Visibility.Collapsed };
		_crosshair = new Line { Y1 = 2, Y2 = 32, Stroke = new SolidColorBrush(_tone), StrokeThickness = 1, Opacity = 0.45, Visibility = Visibility.Collapsed };
		host.Children.Add(_emptyLine);
		host.Children.Add(_area);
		host.Children.Add(_area2);
		host.Children.Add(_line);
		host.Children.Add(_line2);
		host.Children.Add(_crosshair);
		host.MouseMove += OnMouseMove;
		host.MouseLeave += (_, _) => SetHover(-1);
		Children.Add(_readout);
		Children.Add(host);
		SizeChanged += (_, _) => Rebuild();
	}

	/// <summary>设置序列。primary/secondary 允许含 null(传感器缺口→分段); max=NaN 自动取序列峰值; stamps 与 primary 等长。</summary>
	public void SetSeries(double?[] primary, double?[]? secondary, double max, string unit, DateTime[] stamps, Color tone, Color tone2, string label, string label2)
	{
		_primary = primary ?? Array.Empty<double?>();
		_secondary = secondary ?? Array.Empty<double?>();
		_stamps = stamps ?? Array.Empty<DateTime>();
		_max = max;
		_unit = unit ?? "";
		_tone = tone;
		_tone2 = tone2;
		_label = label ?? "";
		_label2 = label2 ?? "";
		if (_line != null)
		{
			_line.Stroke = new SolidColorBrush(_tone);
		}
		if (_area != null)
		{
			_area.Fill = new SolidColorBrush(Color.FromArgb(31, _tone.R, _tone.G, _tone.B));
		}
		if (_crosshair != null)
		{
			_crosshair.Stroke = new SolidColorBrush(_tone);
		}
		if (_line2 != null)
		{
			_line2.Stroke = new SolidColorBrush(_tone2);
			_line2.Visibility = _secondary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		}
		if (_area2 != null)
		{
			_area2.Visibility = Visibility.Collapsed; // 副曲线只画线(Strata secondary 无面积)
		}
		SetHover(-1);
		Rebuild();
	}

	private void OnMouseMove(object sender, MouseEventArgs e)
	{
		if (_primary.Length < 2)
		{
			return;
		}
		var pos = e.GetPosition(this);
		var host = (UIElement)sender;
		double w = host.RenderSize.Width;
		if (w <= 0)
		{
			return;
		}
		// 时间映射下按"最近样本点"反查索引(点数 ≤60, 线性扫描即可)
		double frac = Math.Max(0, Math.Min(1, pos.X / w));
		int best = 0;
		double bestD = double.MaxValue;
		for (int i = 0; i < _primary.Length; i++)
		{
			double d = Math.Abs(XAt(i, _primary.Length) / w - frac);
			if (d < bestD)
			{
				bestD = d;
				best = i;
			}
		}
		SetHover(best);
	}

	private void SetHover(int idx)
	{
		if (_hover == idx)
		{
			return;
		}
		_hover = idx;
		if (_crosshair != null)
		{
			_crosshair.Visibility = idx >= 0 && _primary.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
		}
		if (_readout != null)
		{
			bool show = idx >= 0;
			_readout.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
			if (show)
			{
				int ago = idx < _stamps.Length && _stamps.Length > 0 ? (int)Math.Max(0, (_stamps[^1] - _stamps[idx]).TotalSeconds) : 0;
				string v1 = ReadVal(_primary, idx);
				string text = (_secondary.Length > 0 ? "解码 " : "") + v1;
				if (_secondary.Length > 0)
				{
					text += $" · {_label2} {ReadVal(_secondary, idx)}";
				}
				else if (!string.IsNullOrEmpty(_label))
				{
					text = _label + " " + v1;
				}
				_readout.Text = text + (ago > 0 ? $" · {ago}s前" : " · 现在");
			}
		}
		// 十字线 x 与索引反查同用 XAt, 时间映射下保持一致
		if (_crosshair != null && idx >= 0 && _primary.Length > 1)
		{
			double x = XAt(idx, _primary.Length);
			_crosshair.X1 = x;
			_crosshair.X2 = x;
		}
	}

	private string ReadVal(double?[] data, int idx)
	{
		if (idx >= data.Length || !data[idx].HasValue)
		{
			return "无数据";
		}
		return $"{Math.Round(data[idx]!.Value * 10) / 10}{(_unit.Length > 0 ? " " + _unit : "")}";
	}

	private void Rebuild()
	{
		if (_area == null || _line == null || _emptyLine == null)
		{
			return;
		}
		double w = RenderSize.Width;
		if (w <= 0 || _primary.Length == 0)
		{
			return;
		}
		bool has1 = HasData(_primary);
		bool has2 = HasData(_secondary);
		_emptyLine.Visibility = !has1 && !has2 ? Visibility.Visible : Visibility.Collapsed;
		double top = double.NaN;
		if (!double.IsNaN(_max) && _max > 0)
		{
			top = _max;
		}
		else
		{
			foreach (double? v in _primary)
			{
				if (v.HasValue && (!double.IsNaN(top) ? v.Value > top : true))
				{
					top = v.Value;
				}
			}
			foreach (double? v in _secondary)
			{
				if (v.HasValue && (!double.IsNaN(top) ? v.Value > top : true))
				{
					top = v.Value;
				}
			}
			if (double.IsNaN(top) || top <= 0)
			{
				top = 1e-9;
			}
		}
		DrawSeries(_primary, _area, _line, top, closeArea: true);
		if (has2)
		{
			DrawSeries(_secondary, null, _line2, top, closeArea: false);
		}
		if (_hover >= 0)
		{
			int h = _hover;
			_hover = -1;
			SetHover(h);
		}
	}

	private static bool HasData(double?[] a)
	{
		foreach (double? v in a)
		{
			if (v.HasValue)
			{
				return true;
			}
		}
		return false;
	}

	private void DrawSeries(double?[] data, Path? area, Path? line, double top, bool closeArea)
	{
		if (line == null)
		{
			return;
		}
		double w = RenderSize.Width;
		if (w <= 0)
		{
			return;
		}
		var lineGeo = new StreamGeometry { FillRule = FillRule.Nonzero };
		StreamGeometry? areaGeo = closeArea ? new StreamGeometry { FillRule = FillRule.Nonzero } : null;
		bool anySeg = false;
		using (StreamGeometryContext lctx = lineGeo.Open())
		{
			StreamGeometryContext? actx = areaGeo?.Open();
			using (actx)
			{
				int start = -1;
				for (int i = 0; i <= data.Length; i++)
				{
					bool valid = i < data.Length && data[i].HasValue;
					if (valid && start < 0)
					{
						start = i;
					}
					bool segEnd = start >= 0 && (!valid || i == data.Length - 1);
					if (!segEnd)
					{
						continue;
					}
					int end = valid ? i : i - 1;
					if (end - start < 1)
					{
						if (end == start && !closeArea)
						{
							// 单点副曲线: 画短横线段
							Point p = Pt(start, data[start]!.Value, top, data.Length);
							lctx.BeginFigure(new Point(p.X - 1, p.Y), false, false);
							lctx.LineTo(new Point(p.X + 1, p.Y), true, false);
							anySeg = true;
						}
						start = -1;
						continue;
					}
					Point p0 = Pt(start, data[start]!.Value, top, data.Length);
					lctx.BeginFigure(p0, false, false);
					actx?.BeginFigure(p0, false, false);
					for (int j = start + 1; j <= end; j++)
					{
						Point pj = Pt(j, data[j]!.Value, top, data.Length);
						lctx.LineTo(pj, true, false);
						actx?.LineTo(pj, true, false);
					}
					if (actx != null)
					{
						actx.LineTo(new Point(Pt(end, 0, top, data.Length).X, 32), true, false);
						actx.LineTo(new Point(p0.X, 32), true, false);
					}
					anySeg = true;
					start = -1;
				}
			}
		}
		lineGeo.Freeze();
		areaGeo?.Freeze();
		line.Data = lineGeo;
		line.Visibility = anySeg ? Visibility.Visible : Visibility.Collapsed;
		if (area != null)
		{
			area.Data = anySeg && areaGeo != null ? areaGeo : null;
		}
	}

	// 100×32 逻辑坐标映射到控制像素
	private Point Pt(int idx, double v, double top, int count)
	{
		double x = XAt(idx, count);
		double y = 30 - v / top * 26;
		return new Point(x, y);
	}

	/// <summary>
	/// x 坐标: 时间戳与序列等长且跨度有效时按真实时间映射(采样间隔不均匀/混合节奏不失真),
	/// 否则按索引均匀分布。
	/// </summary>
	private double XAt(int idx, int count)
	{
		double w = RenderSize.Width;
		if (count <= 1)
		{
			return 0;
		}
		if (_stamps.Length == count && _stamps.Length > 1)
		{
			double span = (_stamps[^1] - _stamps[0]).TotalSeconds;
			if (span > 0.01)
			{
				return (_stamps[idx] - _stamps[0]).TotalSeconds / span * w;
			}
		}
		return idx / (double)(count - 1) * w;
	}
}
