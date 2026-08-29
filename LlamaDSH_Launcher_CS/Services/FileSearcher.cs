using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaDSH_Launcher.Services;

public static class FileSearcher
{
	private static readonly HashSet<string> _blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"$recycle.bin", "system volume information", "windows", "recovery", "perflogs", "boot", "programdata", ".git", ".svn", "node_modules",
		"__pycache__", ".cache", "temp", "tmp", "cache"
	};

	public static string? FindLlamaServer(Action<string>? cb = null, int timeoutSec = 60)
	{
		string target = "llama-server.exe";
		long num = Environment.TickCount64 + (long)timeoutSec * 1000L;
		Emit("Searching llama-server.exe ...");
		Emit("  [S0] WinGet...");
		string[] directories;
		try
		{
			directories = Directory.GetDirectories("C:\\Users");
			foreach (string text in directories)
			{
				if (Environment.TickCount64 >= num)
				{
					break;
				}
				bool flag;
				switch (Path.GetFileName(text).ToLowerInvariant())
				{
				case "public":
				case "default":
				case "all users":
					flag = true;
					break;
				default:
					flag = false;
					break;
				}
				if (flag)
				{
					continue;
				}
				string path = Path.Combine(text, "AppData", "Local", "Microsoft", "WinGet", "Packages");
				if (!Directory.Exists(path))
				{
					continue;
				}
				try
				{
					string[] directories2 = Directory.GetDirectories(path);
					foreach (string text2 in directories2)
					{
						if (Path.GetFileName(text2).ToLowerInvariant().Contains("ggml.llamacpp"))
						{
							string text3 = FindFile(text2, target.ToLowerInvariant(), num);
							if (text3 != null)
							{
								string directoryName = Path.GetDirectoryName(text3);
								Emit("  [S0] Hit: " + directoryName);
								return directoryName;
							}
						}
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		Emit("  [S1] PATH...");
		directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
		foreach (string text4 in directories)
		{
			if (Environment.TickCount64 >= num)
			{
				break;
			}
			if (File.Exists(Path.Combine(text4.Trim('"'), target)))
			{
				Emit("  [S1] Hit: " + text4.Trim('"'));
				return text4.Trim('"');
			}
		}
		Emit("  [S2] Drive roots...");
		string text5 = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").ToUpperInvariant();
		List<(string, int)> list = new List<(string, int)>();
		directories = new string[6] { "Program Files", "Program Files (x86)", "Users", "tools", "dev", "opt" };
		foreach (string path2 in directories)
		{
			string text6 = Path.Combine(text5 + "\\", path2);
			if (Directory.Exists(text6))
			{
				list.Add((text6, 3));
			}
		}
		directories = GetDrives();
		foreach (string text7 in directories)
		{
			if (text7.ToUpperInvariant().StartsWith(text5))
			{
				continue;
			}
			try
			{
				string[] directories2 = Directory.GetDirectories(text7);
				foreach (string item in directories2)
				{
					if (Environment.TickCount64 < num)
					{
						list.Add((item, 2));
						continue;
					}
					break;
				}
			}
			catch
			{
			}
		}
		foreach (var item3 in list)
		{
			string item2 = item3.Item1;
			if (Environment.TickCount64 >= num)
			{
				break;
			}
			if (File.Exists(Path.Combine(item2, target)))
			{
				Emit("  [S2] Hit: " + item2);
				return item2;
			}
			try
			{
				directories = Directory.GetDirectories(item2);
				foreach (string text8 in directories)
				{
					if (!Prune(Path.GetFileName(text8), 1, 5) && File.Exists(Path.Combine(text8, target)))
					{
						Emit("  [S2] Hit: " + text8);
						return text8;
					}
				}
			}
			catch
			{
			}
		}
		long num2 = Math.Max(5L, (num - Environment.TickCount64) / 1000);
		Emit($"  [S3] BFS ({num2}s)...");
		List<(string, int)> roots = (from d in GetDrives()
			select (d: d, 5)).ToList();
		string text9 = Search(target.ToLowerInvariant(), new string[4] { "llama", "llamacpp", "llama.cpp", "ggml" }, roots, (string p) => File.Exists(Path.Combine(p, target)), Emit, 5, (int)num2);
		if (text9 != null)
		{
			Emit("  [S3] Hit: " + text9);
		}
		else
		{
			Emit("  Not found");
		}
		return text9;
		void Emit(string msg)
		{
			cb?.Invoke(msg);
		}
	}

	public static string? FindDshProject(Action<string>? cb = null, int timeoutSec = 45)
	{
		string value = "deepseek-harness";
		Emit("Searching DSH project...");
		string text = "%DSH%";
		if (Directory.Exists(text))
		{
			Emit("  [S0] Check: " + text);
			if (IsDsh(text))
			{
				Emit("  [S0] Hit: " + text);
				return text;
			}
		}
		Emit("  [S1] Common dirs...");
		List<(string, int)> list = new List<(string, int)>();
		string[] drives = GetDrives();
		foreach (string path in drives)
		{
			string[] array = new string[5] { "Program Files", "Program Files (x86)", "Projects", "dev", "code" };
			foreach (string path2 in array)
			{
				string text2 = Path.Combine(path, path2);
				if (Directory.Exists(text2))
				{
					list.Add((text2, 3));
				}
			}
		}
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		drives = new string[5] { "Projects", "dev", "code", "repos", "src" };
		foreach (string path3 in drives)
		{
			string text3 = Path.Combine(folderPath, path3);
			if (Directory.Exists(text3))
			{
				list.Add((text3, 2));
			}
		}
		foreach (var item2 in list)
		{
			string item = item2.Item1;
			if (IsDsh(item))
			{
				Emit("  [S1] Hit: " + item);
				return item;
			}
			try
			{
				drives = Directory.GetDirectories(item);
				foreach (string text4 in drives)
				{
					if (Path.GetFileName(text4).ToLowerInvariant().Contains(value) && IsDsh(text4))
					{
						Emit("  [S1] Hit: " + text4);
						return text4;
					}
				}
			}
			catch
			{
			}
		}
		Emit("  [S2] BFS...");
		List<(string, int)> roots = (from d in GetDrives()
			select (d: d, 5)).ToList();
		string text5 = Search("pnpm-workspace.yaml", new string[3] { "deepseek", "dsh", "harness" }, roots, (string p) => IsDsh(Path.GetDirectoryName(p)), Emit, 4, timeoutSec);
		if (text5 != null)
		{
			Emit("  [S2] Hit: " + text5);
		}
		else
		{
			Emit("  Not found");
		}
		return text5;
		void Emit(string msg)
		{
			cb?.Invoke(msg);
		}
		static bool IsDsh(string p)
		{
			try
			{
				if (!Directory.Exists(p))
				{
					return false;
				}
				if (File.Exists(Path.Combine(p, "pnpm-workspace.yaml")))
				{
					return true;
				}
				string path4 = Path.Combine(p, "package.json");
				if (File.Exists(path4))
				{
					try
					{
						string text6 = (JsonDocument.Parse(File.ReadAllText(path4)).RootElement.GetProperty("name").GetString() ?? "").ToLowerInvariant();
						if (text6.Contains("deepseek") || text6.Contains("dsh") || text6.Contains("harness"))
						{
							return true;
						}
					}
					catch
					{
					}
				}
			}
			catch
			{
			}
			return false;
		}
	}

	private static string? Search(string targetFn, string[] keywords, List<(string root, int pr)> roots, Func<string, bool> isTarget, Action<string>? cb = null, int maxDepth = 5, int timeout = 30)
	{
		long deadline = Environment.TickCount64 + (long)timeout * 1000L;
		int stop = 0;
		ConcurrentBag<string> results = new ConcurrentBag<string>();
		foreach (var root in roots)
		{
			string item = root.root;
			if (Interlocked.CompareExchange(ref stop, 0, 0) != 0)
			{
				break;
			}
			if (isTarget(item))
			{
				return item;
			}
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var root2 in roots)
		{
			string item2 = root2.root;
			try
			{
				hashSet.Add(Path.GetPathRoot(item2) ?? "C:\\");
			}
			catch
			{
			}
		}
		string[] drives = GetDrives();
		foreach (string item3 in drives)
		{
			hashSet.Add(item3);
		}
		Parallel.ForEach(hashSet, new ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Min(hashSet.Count, Math.Min(Environment.ProcessorCount, 6))
		}, delegate(string root)
		{
			Bfs(root, targetFn, keywords, isTarget, maxDepth, deadline, ref stop, results);
		});
		return results.FirstOrDefault();
	}

	private static void Bfs(string root, string targetFn, string[] keywords, Func<string, bool> isTarget, int maxDepth, long deadline, ref int stop, ConcurrentBag<string> results)
	{
		SortedSet<(double, int, string)> sortedSet = new SortedSet<(double, int, string)>(Comparer<(double, int, string)>.Create(delegate((double, int, string) a, (double, int, string) b)
		{
			int num = a.Item1.CompareTo(b.Item1);
			if (num != 0)
			{
				return num;
			}
			num = a.Item2.CompareTo(b.Item2);
			return (num != 0) ? num : string.Compare(a.Item3, b.Item3, StringComparison.Ordinal);
		}));
		sortedSet.Add((H(Path.GetFileName(root), keywords), 0, root));
		while (sortedSet.Count > 0 && Interlocked.CompareExchange(ref stop, 0, 0) == 0 && Environment.TickCount64 < deadline)
		{
			(double, int, string) min = sortedSet.Min;
			sortedSet.Remove(min);
			(double, int, string) tuple = min;
			int item = tuple.Item2;
			string item2 = tuple.Item3;
			try
			{
				foreach (string item3 in Directory.EnumerateFileSystemEntries(item2))
				{
					if (Interlocked.CompareExchange(ref stop, 0, 0) != 0 || Environment.TickCount64 >= deadline)
					{
						return;
					}
					try
					{
						if (File.Exists(item3))
						{
							if (Path.GetFileName(item3).Equals(targetFn, StringComparison.OrdinalIgnoreCase))
							{
								string directoryName = Path.GetDirectoryName(item3);
								if (isTarget(directoryName))
								{
									results.Add(directoryName);
									Interlocked.Exchange(ref stop, 1);
									return;
								}
							}
						}
						else if (Directory.Exists(item3))
						{
							string fileName = Path.GetFileName(item3);
							if (!Prune(fileName, item + 1, maxDepth))
							{
								sortedSet.Add(((double)(item + 1) * 1.5 + H(fileName, keywords), item + 1, item3));
							}
						}
					}
					catch
					{
					}
				}
			}
			catch
			{
			}
		}
	}

	private static double H(string name, string[] kws)
	{
		string text = name.ToLowerInvariant();
		foreach (string text2 in kws)
		{
			if (!string.IsNullOrEmpty(text2))
			{
				if (text.Contains(text2))
				{
					return 0.0;
				}
				if (text2.Length >= 3 && text.StartsWith(text2.Substring(0, 3)))
				{
					return 2.0;
				}
			}
		}
		return 15.0;
	}

	private static bool Prune(string name, int depth, int max)
	{
		if (depth > max)
		{
			return true;
		}
		string text = name.ToLowerInvariant();
		if (_blacklist.Contains(text))
		{
			return true;
		}
		if (text.StartsWith('.') && depth > 1)
		{
			return true;
		}
		return false;
	}

	private static string[] GetDrives()
	{
		return (from d in DriveInfo.GetDrives()
			where d.IsReady
			select d.Name).ToArray();
	}

	private static string? FindFile(string dir, string target, long deadline)
	{
		try
		{
			foreach (string item in Directory.EnumerateFileSystemEntries(dir))
			{
				if (Environment.TickCount64 >= deadline)
				{
					return null;
				}
				try
				{
					if (File.Exists(item) && Path.GetFileName(item).Equals(target, StringComparison.OrdinalIgnoreCase))
					{
						return item;
					}
					if (Directory.Exists(item))
					{
						string text = FindFile(item, target, deadline);
						if (text != null)
						{
							return text;
						}
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		return null;
	}
}
