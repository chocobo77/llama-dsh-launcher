using System.Collections.Generic;

namespace LlamaDSH_Launcher.Models;

public class ConfigFile
{
	/// <summary>窗口尺寸缺失时的兜底值，与 MainWindow.xaml 的默认窗口大小保持一致。</summary>
	public const double DefaultWindowWidth = 1400;

	public const double DefaultWindowHeight = 950;

	/// <summary>当前方案缺失时的兜底名，与 MainWindow 的初始方案名保持一致。</summary>
	public const string DefaultProfileName = "默认方案";

	private string _currentProfile = DefaultProfileName;

	private double _windowWidth = DefaultWindowWidth;

	private double _windowHeight = DefaultWindowHeight;

	public Dictionary<string, ProfileConfig> Profiles { get; set; } = new Dictionary<string, ProfileConfig>();

	/// <summary>
	/// 当前方案名；空串/空白回落默认方案名（兼容旧配置）。
	/// 若旧配置的方案表里没有默认名，则依次回退到 "Default" / 第一个方案，避免整套配置读不到。
	/// </summary>
	public string CurrentProfile
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(_currentProfile))
			{
				return _currentProfile;
			}
			if (Profiles != null)
			{
				if (Profiles.ContainsKey(DefaultProfileName))
				{
					return DefaultProfileName;
				}
				if (Profiles.ContainsKey("Default"))
				{
					return "Default";
				}
				string? first = Profiles.Keys.FirstOrDefault();
				if (!string.IsNullOrEmpty(first))
				{
					return first;
				}
			}
			return DefaultProfileName;
		}
		set => _currentProfile = value;
	}

	/// <summary>窗口宽；0/负数（旧配置或首次启动）回落默认宽度。</summary>
	public double WindowWidth
	{
		get => _windowWidth > 100.0 ? _windowWidth : DefaultWindowWidth;
		set => _windowWidth = value;
	}

	/// <summary>窗口高；0/负数（旧配置或首次启动）回落默认高度。</summary>
	public double WindowHeight
	{
		get => _windowHeight > 100.0 ? _windowHeight : DefaultWindowHeight;
		set => _windowHeight = value;
	}
}
