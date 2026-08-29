using System;
using System.Diagnostics;

namespace LlamaDSH_Launcher.Services;

public class ProcessTracker : IDisposable
{
	private readonly DateTime? _createTime;

	public int Pid { get; }

	public ProcessTracker(int pid)
	{
		Pid = pid;
		try
		{
			using Process process = Process.GetProcessById(pid);
			_createTime = process.StartTime;
		}
		catch
		{
			_createTime = null;
		}
	}

	public int? Poll()
	{
		try
		{
			using Process process = Process.GetProcessById(Pid);
			if (_createTime.HasValue && process.StartTime != _createTime.Value)
			{
				return -1;
			}
			return null;
		}
		catch
		{
			return -1;
		}
	}

	public void Dispose()
	{
	}
}
