using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

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
		Emit("  [S1b] Known install locations...");
		string? knownLlama = ProbeKnownLlamaServer();
		if (knownLlama != null)
		{
			Emit("  [S1b] Hit: " + knownLlama);
			return knownLlama;
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
		Emit("  [S0] Known locations...");
		string? knownDsh = ProbeKnownDshRoots();
		if (knownDsh != null)
		{
			Emit("  [S0] Hit: " + knownDsh);
			return knownDsh;
		}
		Emit("  [S1] Common dirs...");
		List<(string, int)> list = new List<(string, int)>();
		string[] drives = GetDrives();
		foreach (string path in drives)
		{
			string[] array = new string[10] { "Program Files", "Program Files (x86)", "Projects", "dev", "code", "src", "repos", "work", "git", "tools" };
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
		drives = new string[9] { "Projects", "dev", "code", "repos", "src", "git", "github", "Documents", "source" };
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
					if (MatchKeyword(Path.GetFileName(text4)) && IsDsh(text4))
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
		static bool MatchKeyword(string name)
		{
			string n = name.ToLowerInvariant();
			return n.Contains("deepseek") || n.Contains("dsh") || n.Contains("harness");
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

	/// <summary>
	/// 已知安装位置的定向探测（不依赖本机布局）：注册表 App Paths → 包管理器(scoop/choco) →
	/// LM Studio 内置后端 → 常见源码/构建/下载目录。命中即返回，避免只能靠全盘 BFS 撞运气。
	/// </summary>
	public static string? ProbeKnownLlamaServer()
	{
		try
		{
			foreach (RegistryKey root in new RegistryKey[2] { Registry.LocalMachine, Registry.CurrentUser })
			{
				foreach (string sub in new string[2]
				{
					"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\llama-server.exe",
					"SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\App Paths\\llama-server.exe"
				})
				{
					try
					{
						using RegistryKey? key = root.OpenSubKey(sub);
						if (key?.GetValue(null) is string exe && exe.Length > 0)
						{
							string? dir = Path.GetDirectoryName(exe);
							if (dir != null && File.Exists(Path.Combine(dir, "llama-server.exe")))
							{
								return dir;
							}
						}
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
		foreach (string dir2 in new string[6]
		{
			Path.Combine(folderPath, "scoop", "apps", "llama.cpp", "current"),
			Path.Combine(folderPath, "scoop", "apps", "llamacpp", "current"),
			Path.Combine(local, "scoop", "apps", "llama.cpp", "current"),
			Path.Combine(common, "chocolatey", "lib", "llama.cpp", "tools"),
			Path.Combine(common, "chocolatey", "lib", "llamacpp", "tools"),
			Path.Combine(common, "chocolatey", "bin")
		})
		{
			if (File.Exists(Path.Combine(dir2, "llama-server.exe")))
			{
				return dir2;
			}
		}
		List<string> bases = new List<string>();
		bases.Add(folderPath);
		bases.Add(Path.Combine(folderPath, "source", "repos"));
		bases.Add(Path.Combine(folderPath, "Documents"));
		bases.Add(Path.Combine(folderPath, "Desktop"));
		bases.Add(Path.Combine(folderPath, "Downloads"));
		bases.Add(Path.Combine(local, "Programs"));
		bases.Add(local);
		foreach (string drive in GetDrives())
		{
			bases.Add(drive);
			bases.Add(Path.Combine(drive, "code"));
			bases.Add(Path.Combine(drive, "dev"));
			bases.Add(Path.Combine(drive, "Apps"));
			bases.Add(Path.Combine(drive, "tools"));
			bases.Add(Path.Combine(drive, "Program Files"));
		}
		string[] names = new string[4] { "llama.cpp", "llamacpp", "llama-cpp", "llama.cpp-master" };
		string[] subs = new string[9] { "", "bin", "build", "build\\bin", "build\\bin\\Release", "build\\bin\\Debug", "Release", "windows", "llama.cpp" };
		foreach (string text7 in bases)
		{
			if (File.Exists(Path.Combine(text7, "llama-server.exe")))
			{
				return text7;
			}
			foreach (string text8 in names)
			{
				string text9 = Path.Combine(text7, text8);
				foreach (string text10 in subs)
				{
					string text11 = ((text10.Length == 0) ? text9 : Path.Combine(text9, text10));
					if (File.Exists(Path.Combine(text11, "llama-server.exe")))
					{
						return text11;
					}
				}
			}
		}

		// 兜底：LM Studio / 其它编辑器内置的 llama.cpp 后端（版本号由编辑器管理，优先级低于专用安装目录）
		foreach (string backends in new string[4]
		{
			Path.Combine(folderPath, ".lmstudio", "extensions", "backends"),
			Path.Combine(local, ".lmstudio", "extensions", "backends"),
			Path.Combine(roaming, "LM Studio", "extensions", "backends"),
			Path.Combine(local, "LM Studio", "extensions", "backends")
		})
		{
			try
			{
				if (!Directory.Exists(backends))
				{
					continue;
				}
				foreach (string sub2 in Directory.GetDirectories(backends))
				{
					if (File.Exists(Path.Combine(sub2, "llama-server.exe")))
					{
						return sub2;
					}
				}
			}
			catch
			{
			}
		}
		return null;
	}

	/// <summary>DSH 检出目录的已知候选（不再写死本机路径）：家目录/常见代码目录/各盘根 + 关键词子目录。</summary>
	public static string? ProbeKnownDshRoots()
	{
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		List<string> list = new List<string>();
		list.Add(Path.Combine(folderPath, "deepseek-harness"));
		list.Add(Path.Combine(folderPath, "dsh"));
		list.Add(Path.Combine(folderPath, "code", "deepseek-harness"));
		list.Add(Path.Combine(folderPath, "source", "repos", "deepseek-harness"));
		list.Add(Path.Combine(folderPath, "Documents", "deepseek-harness"));
		list.Add(Path.Combine(local, "Programs", "deepseek-harness"));
		foreach (string drive in GetDrives())
		{
			list.Add(Path.Combine(drive, "deepseek-harness"));
			list.Add(Path.Combine(drive, "dsh"));
			list.Add(Path.Combine(drive, "code", "deepseek-harness"));
			list.Add(Path.Combine(drive, "dev", "deepseek-harness"));
			list.Add(Path.Combine(drive, "src", "deepseek-harness"));
			list.Add(Path.Combine(drive, "Program files", "deepseek-harness"));
			list.Add(Path.Combine(drive, "Program Files", "deepseek-harness"));
			try
			{
				foreach (string text12 in Directory.GetDirectories(drive))
				{
					string text13 = Path.GetFileName(text12).ToLowerInvariant();
					if (text13.Contains("deepseek") || text13.Contains("dsh") || text13.Contains("harness"))
					{
						list.Add(text12);
					}
				}
			}
			catch
			{
			}
		}
		foreach (string text14 in list)
		{
			if (IsDshProjectDir(text14))
			{
				return text14;
			}
		}
		return null;
	}

	/// <summary>DSH 检出目录判据（供定向探测复用；与 FindDshProject 内 IsDsh 同义）。</summary>
	private static bool IsDshProjectDir(string p)
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
			if (File.Exists(Path.Combine(p, "apps", "cli", "package.json")) && Directory.Exists(Path.Combine(p, "packages")))
			{
				return true;
			}
			string text15 = Path.Combine(p, "package.json");
			if (File.Exists(text15))
			{
				string text16 = (JsonDocument.Parse(File.ReadAllText(text15)).RootElement.GetProperty("name").GetString() ?? "").ToLowerInvariant();
				if (text16.Contains("deepseek") || text16.Contains("dsh") || text16.Contains("harness"))
				{
					return true;
				}
			}
		}
		catch
		{
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
