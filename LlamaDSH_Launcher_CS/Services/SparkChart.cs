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
		RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
		RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
		_readout = new TextBlock
		{
			FontSize = 11,
			Foreground = new SolidColorBrush(Color.FromRgb(0xee, 0xf1, 0xf3)),
			Visibility = Visibility.Collapsed,
			VerticalAlignment = VerticalAlignment.Bottom,
			Margin = new Thickness(0, 0, 0, 34)
		};
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
		SetRow(host, 1);
		SetRow(_readout, 0);
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
		SetHover((int)Math.Round(Math.Max(0, Math.Min(1, pos.X / w)) * (_primary.Length - 1)));
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
		// 十字线 x 位置按控制像素宽换算
		if (_crosshair != null && idx >= 0 && _primary.Length > 1)
		{
			double w = RenderSize.Width;
			double x = idx / (double)(_primary.Length - 1) * w;
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
							Point p = Pt(start, data[start]!.Value, top, w);
							lctx.BeginFigure(new Point(p.X - 1, p.Y), false, false);
							lctx.LineTo(new Point(p.X + 1, p.Y), true, false);
							anySeg = true;
						}
						start = -1;
						continue;
					}
					Point p0 = Pt(start, data[start]!.Value, top, w);
					lctx.BeginFigure(p0, false, false);
					actx?.BeginFigure(p0, false, false);
					for (int j = start + 1; j <= end; j++)
					{
						Point pj = Pt(j, data[j]!.Value, top, w);
						lctx.LineTo(pj, true, false);
						actx?.LineTo(pj, true, false);
					}
					if (actx != null)
					{
						actx.LineTo(new Point(Pt(end, 0, top, w).X, 32), true, false);
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
	private Point Pt(int idx, double v, double top, double w)
	{
		double x = _primary.Length > 1 ? idx / (double)(_primary.Length - 1) * w : 0;
		double y = 30 - v / top * 26;
		return new Point(x, y);
	}
}
