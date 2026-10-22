using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// 运行环境与安装目录发现：
/// - 不同机器上 node24 / pnpm / git 的安装位置不同（Program Files、nvm、scoop、winget、PATH…）；
/// - deepseek-harness / llama.cpp 也可能装在任何盘符/任意目录。
/// 这里统一做“可用优先、路径无关”的解析，并把结果缓存，避免每次操作都全盘扫描。
/// </summary>
public static class Toolchain
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, string> NodeVersions = new(StringComparer.OrdinalIgnoreCase);
    private static string? _node24;
    private static bool _node24Searched;
    private static string? _pnpm;
    private static bool _pnpmSearched;
    private static string? _git;
    private static bool _gitSearched;
    private static (string? Dir, DateTime At) _dshCache = (null, DateTime.MinValue);
    private static (string? Dir, DateTime At) _llamaCache = (null, DateTime.MinValue);

    public static string? Node24Path
    {
        get { return ResolveNode24Exe(); }
    }

    public static string? PnpmPath
    {
        get { return ResolvePnpmExe(); }
    }

    public static string? GitPath
    {
        get { return ResolveGitExe(); }
    }

    /// <summary>node24 所在目录（用于给子进程补 PATH）。</summary>
    public static string? NodeDir
    {
        get
        {
            string? exe = ResolveNode24Exe();
            return exe == null ? null : Path.GetDirectoryName(exe);
        }
    }

    /// <summary>pnpm 所在目录（用于给子进程补 PATH）。</summary>
    public static string? PnpmDir
    {
        get
        {
            string? exe = ResolvePnpmExe();
            return exe == null ? null : Path.GetDirectoryName(exe);
        }
    }

    /// <summary>git 所在目录（用于给子进程补 PATH）。</summary>
    public static string? GitDir
    {
        get
        {
            string? exe = ResolveGitExe();
            return exe == null ? null : Path.GetDirectoryName(exe);
        }
    }

    /// <summary>node24/pnpm/git 目录组成的 PATH 前缀（无则空串）。</summary>
    public static string PathPrefix
    {
        get
        {
            List<string> parts = new();
            foreach (string? dir in new[] { NodeDir, PnpmDir, GitDir })
            {
                if (!string.IsNullOrEmpty(dir) && !parts.Any((string x) => string.Equals(x, dir, StringComparison.OrdinalIgnoreCase)))
                {
                    parts.Add(dir);
                }
            }
            return string.Join(";", parts);
        }
    }

    // ==================== 目录判定 ====================

    public static string NormalizeDir(string? dir)
    {
        return (dir ?? "").Trim().Trim('"').Trim();
    }

    public static bool IsDshDir(string? dir)
    {
        string p = NormalizeDir(dir);
        if (p.Length == 0 || !Directory.Exists(p))
        {
            return false;
        }
        try
        {
            if (File.Exists(Path.Combine(p, "pnpm-workspace.yaml")) &&
                (Directory.Exists(Path.Combine(p, "apps", "cli")) || Directory.Exists(Path.Combine(p, "packages"))))
            {
                return true;
            }
            if (File.Exists(Path.Combine(p, "apps", "cli", "package.json")) &&
                Directory.Exists(Path.Combine(p, "packages")))
            {
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    public static bool IsLlamaDir(string? dir)
    {
        return UpdateService.FindLlamaServerExe(NormalizeDir(dir)) != null;
    }

    // ==================== DSH 目录 ====================

    /// <summary>
    /// 解析 DSH 仓库目录。allowFullSearch=false 时只做快速探测（配置/状态/运行进程/profile 链接/常见位置），
    /// 不会全盘扫描；需要全盘检索时由“自动检索”按钮显式调用 FileSearcher。
    /// </summary>
    public static string? ResolveDshDir(string? preferred, bool allowFullSearch = false)
    {
        string p = NormalizeDir(preferred);
        if (IsDshDir(p))
        {
            return p;
        }
        lock (Sync)
        {
            if (_dshCache.Dir != null && IsDshDir(_dshCache.Dir) && DateTime.UtcNow - _dshCache.At < TimeSpan.FromSeconds(30))
            {
                return _dshCache.Dir;
            }
        }

        string? found = ProbeDshFromState();
        found ??= ProbeDshFromRunningProcess();
        found ??= ProbeDshFromProfileLinks();
        found ??= FileSearcher.ProbeKnownDshRoots();
        found ??= ProbeDshFromNpmGlobal();
        if (found == null && allowFullSearch)
        {
            try
            {
                found = FileSearcher.FindDshProject(null, 45);
            }
            catch
            {
            }
        }
        if (IsDshDir(found))
        {
            lock (Sync)
            {
                _dshCache = (found, DateTime.UtcNow);
            }
            return found;
        }
        return null;
    }

    private static string? ProbeDshFromState()
    {
        try
        {
            string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh_launcher_state.json");
            if (!File.Exists(state))
            {
                return null;
            }
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(state));
            if (doc.RootElement.TryGetProperty("DshDir", out System.Text.Json.JsonElement el))
            {
                string? dir = el.GetString();
                if (IsDshDir(dir))
                {
                    return NormalizeDir(dir);
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static string? ProbeDshFromRunningProcess()
    {
        try
        {
            foreach (Process p in Process.GetProcessesByName("node"))
            {
                try
                {
                    string cmd = ProcessHelper.GetCommandLine(p.Id);
                    if (string.IsNullOrEmpty(cmd))
                    {
                        continue;
                    }
                    string? root = ExtractDshRoot(cmd);
                    if (IsDshDir(root))
                    {
                        return root;
                    }
                }
                catch
                {
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>从 node 命令行里提取 DSH 仓库根（...\apps\cli\lib\bin.js / ...\apps\cli\src\bin.ts）。</summary>
    internal static string? ExtractDshRoot(string commandLine)
    {
        try
        {
            Match m = Regex.Match(commandLine, @"([A-Za-z]:\\[^""']*?)\\apps\\cli\\(?:lib|src)\\bin\.(?:js|ts)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string root = m.Groups[1].Value;
                if (IsDshDir(root))
                {
                    return root;
                }
            }
            // 命令行里没有 bin.js 时，按 deepseek-harness 关键片段逐级向上找
            Match m2 = Regex.Match(commandLine, @"([A-Za-z]:\\[^""']*deepseek-harness[^""']*)", RegexOptions.IgnoreCase);
            if (m2.Success)
            {
                string cur = m2.Groups[1].Value;
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(cur); i++)
                {
                    if (IsDshDir(cur))
                    {
                        return cur;
                    }
                    cur = Path.GetDirectoryName(cur) ?? "";
                }
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>从 profile 的 @deepseek-ai junction 反推 DSH 安装目录（不依赖固定盘符）。</summary>
    private static string? ProbeDshFromProfileLinks()
    {
        try
        {
            string profiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "profiles");
            if (!Directory.Exists(profiles))
            {
                return null;
            }
            List<string> bases = new();
            // 共享运行时 farm：.dsh/profiles/node_modules（dsh → apps/cli 的 junction）
            bases.Add(Path.Combine(profiles, "node_modules"));
            foreach (string profile in Directory.GetDirectories(profiles))
            {
                if (!string.Equals(Path.GetFileName(profile), "node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    bases.Add(Path.Combine(profile, "node_modules"));
                }
            }
            foreach (string baseDir in bases)
            {
                // dsh 是 apps/cli 的 junction，向上两级就是仓库根；dsh-app-boot 等多在 apps/cli/node_modules 下。
                foreach (string probe in new[] { "dsh", "dsh-app-boot", "dsh-cli", "dsh-agent" })
                {
                    string link = Path.Combine(baseDir, "@deepseek-ai", probe);
                    string? target = ResolveLinkTarget(link);
                    if (target == null)
                    {
                        continue;
                    }
                    string cur = target;
                    for (int i = 0; i < 14 && !string.IsNullOrEmpty(cur); i++)
                    {
                        if (IsDshDir(cur))
                        {
                            return cur;
                        }
                        cur = Path.GetDirectoryName(cur) ?? "";
                    }
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static string? ResolveLinkTarget(string path)
    {
        try
        {
            DirectoryInfo di = new DirectoryInfo(path);
            if (!di.Exists)
            {
                return null;
            }
            FileSystemInfo? target = di.ResolveLinkTarget(true);
            if (target != null && target.Exists)
            {
                return target.FullName;
            }
            string? raw = di.LinkTarget;
            if (!string.IsNullOrEmpty(raw))
            {
                return Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(di.Parent?.FullName ?? "", raw));
            }
        }
        catch
        {
        }
        return null;
    }

    private static string? ProbeDshFromNpmGlobal()
    {
        try
        {
            foreach (string baseDir in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm", "global", "5", "node_modules"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm", "global", "4", "node_modules")
            })
            {
                string d = Path.Combine(baseDir, "deepseek-harness");
                if (IsDshDir(d))
                {
                    return d;
                }
            }
        }
        catch
        {
        }
        return null;
    }

    // ==================== llama.cpp 目录 ====================

    public static string? ResolveLlamaDir(string? preferred, bool allowFullSearch = false)
    {
        string p = NormalizeDir(preferred);
        if (IsLlamaDir(p))
        {
            return p;
        }
        lock (Sync)
        {
            if (_llamaCache.Dir != null && IsLlamaDir(_llamaCache.Dir) && DateTime.UtcNow - _llamaCache.At < TimeSpan.FromSeconds(30))
            {
                return _llamaCache.Dir;
            }
        }
        string? found = FileSearcher.ProbeKnownLlamaServer();
        if (found == null && allowFullSearch)
        {
            try
            {
                found = FileSearcher.FindLlamaServer(null, 60);
            }
            catch
            {
            }
        }
        if (IsLlamaDir(found))
        {
            lock (Sync)
            {
                _llamaCache = (found, DateTime.UtcNow);
            }
            return found;
        }
        return null;
    }

    // ==================== node / pnpm / git ====================

    public static string? ResolveNode24Exe(bool force = false)
    {
        string? exe = ResolveNodeExe(require24: true, force: force);
        return exe;
    }

    /// <summary>解析 node.exe；require24=true 时只接受 v24+（DSH 运行时要求）。</summary>
    public static string? ResolveNodeExe(bool require24, bool force = false)
    {
        lock (Sync)
        {
            if (!force && require24 && _node24Searched)
            {
                return _node24;
            }
        }
        string? best = null;
        int bestMajor = -1;
        foreach (string candidate in NodeCandidates())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }
            int major = GetNodeMajor(candidate);
            if (major < 0)
            {
                continue;
            }
            if (major > bestMajor)
            {
                bestMajor = major;
                best = candidate;
            }
            if (major >= 24)
            {
                break;
            }
        }
        if (best == null)
        {
            best = FirstExisting(NodeCandidates());
            bestMajor = best == null ? -1 : GetNodeMajor(best);
        }
        lock (Sync)
        {
            if (require24)
            {
                _node24Searched = true;
                _node24 = (bestMajor >= 24) ? best : null;
                return _node24;
            }
            return (bestMajor >= 0) ? best : null;
        }
    }

    private static IEnumerable<string> NodeCandidates()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        List<string> list = new();
        AddEnv(list, "NODE24_PATH");
        AddEnv(list, "NODE_EXE");
        AddEnv(list, "NODE_HOME", "node.exe");
        list.Add(Path.Combine(pf, "nodejs", "node.exe"));
        list.Add(Path.Combine(pf86, "nodejs", "node.exe"));
        list.Add(Path.Combine(local, "Programs", "nodejs", "node.exe"));
        list.Add(Path.Combine(local, "nodejs", "node.exe"));
        list.Add(Path.Combine(roaming, "nvm", "node.exe"));
        list.Add(Path.Combine(home, "scoop", "apps", "nodejs", "current", "node.exe"));
        list.Add(Path.Combine(local, "scoop", "apps", "nodejs", "current", "node.exe"));
        list.Add(Path.Combine(pf, "nodejs", "node.exe"));
        list.Add(Path.Combine(home, "AppData", "Roaming", "npm", "node.exe"));
        list.Add("C:\\nvm4w\\nodejs\\node.exe");
        list.Add("C:\\nodejs\\node.exe");

        string? nvmHome = Environment.GetEnvironmentVariable("NVM_HOME");
        if (!string.IsNullOrEmpty(nvmHome))
        {
            AddVersionDirs(list, nvmHome, "v24", "node.exe");
        }
        string? nvmSymlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");
        if (!string.IsNullOrEmpty(nvmSymlink))
        {
            list.Add(Path.Combine(nvmSymlink, "node.exe"));
        }
        AddVersionDirs(list, Path.Combine(roaming, "nvm"), "v24", "node.exe");
        AddVersionDirs(list, Path.Combine(local, "fnm_multishells"), "v24", "node.exe");
        // winget 安装的 Node（OpenJS.NodeJS / OpenJS.NodeJS.LTS）不在 Program Files 固定路径
        AddPackageDirs(list, Path.Combine(local, "Microsoft", "WinGet", "Packages"), "OpenJS.NodeJS*", "node.exe");
        AddVersionDirs(list, Path.Combine(home, ".volta", "tools", "image", "node"), "v24", "node.exe");
        AddVersionDirs(list, Path.Combine(local, "Volta", "tools", "image", "node"), "v24", "node.exe");

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (string part in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string dir = part.Trim().Trim('"');
                if (dir.Length > 0)
                {
                    list.Add(Path.Combine(dir, "node.exe"));
                }
            }
        }
        string? where = WhereExe("node.exe");
        if (where != null)
        {
            list.Add(where);
        }
        foreach (string item in list)
        {
            if (seen.Add(item))
            {
                yield return item;
            }
        }
    }

    private static void AddEnv(List<string> list, string name, string? leaf = null)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v))
        {
            return;
        }
        foreach (string part in v.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part.Trim().Trim('"');
            if (p.Length == 0)
            {
                continue;
            }
            list.Add(leaf == null ? p : Path.Combine(p, leaf));
        }
    }

    private static void AddPackageDirs(List<string> list, string root, string pattern, string leaf)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }
            foreach (string dir in Directory.GetDirectories(root, pattern))
            {
                list.Add(Path.Combine(dir, leaf));
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    list.Add(Path.Combine(sub, leaf));
                }
            }
        }
        catch
        {
        }
    }

    private static void AddVersionDirs(List<string> list, string root, string versionPrefix, string leaf)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }
            foreach (string dir in Directory.GetDirectories(root, versionPrefix + "*"))
            {
                list.Add(Path.Combine(dir, leaf));
            }
        }
        catch
        {
        }
    }

    private static int GetNodeMajor(string exe)
    {
        lock (Sync)
        {
            if (NodeVersions.TryGetValue(exe, out string? cached))
            {
                return int.TryParse(cached.Split('.')[0], out int m) ? m : -1;
            }
        }
        string version = RunCapture(exe, new[] { "-p", "process.versions.node" }, 8, out _).Trim();
        lock (Sync)
        {
            NodeVersions[exe] = version;
        }
        string[] parts = version.Split('.');
        return parts.Length > 0 && int.TryParse(parts[0], out int major) ? major : -1;
    }

    public static string? ResolvePnpmExe(bool force = false)
    {
        lock (Sync)
        {
            if (!force && _pnpmSearched)
            {
                return _pnpm;
            }
        }
        string? found = null;
        foreach (string candidate in PnpmCandidates())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }
            string ext = Path.GetExtension(candidate);
            if (!ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            found = candidate;
            break;
        }
        lock (Sync)
        {
            _pnpmSearched = true;
            _pnpm = found;
            return _pnpm;
        }
    }

    private static IEnumerable<string> PnpmCandidates()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> list = new();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        AddEnv(list, "PNPM_HOME", "pnpm.exe");
        AddEnv(list, "PNPM_HOME", "pnpm.cmd");
        list.Add(Path.Combine(local, "pnpm", "pnpm.exe"));
        list.Add(Path.Combine(local, "pnpm", "pnpm.cmd"));
        list.Add(Path.Combine(roaming, "npm", "pnpm.cmd"));
        list.Add(Path.Combine(roaming, "npm", "pnpm.exe"));
        list.Add(Path.Combine(pf, "nodejs", "pnpm.cmd"));
        string? nodeDir = NodeDir;
        if (!string.IsNullOrEmpty(nodeDir))
        {
            list.Add(Path.Combine(nodeDir, "pnpm.cmd"));
            list.Add(Path.Combine(nodeDir, "pnpm.exe"));
        }
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (string part in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string dir = part.Trim().Trim('"');
                if (dir.Length == 0)
                {
                    continue;
                }
                list.Add(Path.Combine(dir, "pnpm.cmd"));
                list.Add(Path.Combine(dir, "pnpm.exe"));
            }
        }
        string? where = WhereExe("pnpm.cmd") ?? WhereExe("pnpm.exe");
        if (where != null)
        {
            list.Add(where);
        }
        foreach (string item in list)
        {
            if (seen.Add(item))
            {
                yield return item;
            }
        }
    }

    public static string? ResolveGitExe(bool force = false)
    {
        lock (Sync)
        {
            if (!force && _gitSearched)
            {
                return _git;
            }
        }
        string? found = null;
        foreach (string candidate in GitCandidates())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }
            string output = RunCapture(candidate, new[] { "--version" }, 8, out _);
            if (output.IndexOf("git version", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                found = candidate;
                break;
            }
        }
        lock (Sync)
        {
            _gitSearched = true;
            _git = found;
            return _git;
        }
    }

    private static IEnumerable<string> GitCandidates()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> list = new();
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (string part in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string dir = part.Trim().Trim('"');
                if (dir.Length > 0)
                {
                    list.Add(Path.Combine(dir, "git.exe"));
                }
            }
        }
        list.Add(Path.Combine(pf, "Git", "cmd", "git.exe"));
        list.Add(Path.Combine(pf86, "Git", "cmd", "git.exe"));
        list.Add(Path.Combine(local, "Programs", "Git", "cmd", "git.exe"));
        list.Add(Path.Combine(home, "scoop", "apps", "git", "current", "cmd", "git.exe"));
        list.Add(Path.Combine(local, "scoop", "apps", "git", "current", "cmd", "git.exe"));
        string? where = WhereExe("git.exe");
        if (where != null)
        {
            list.Add(where);
        }
        foreach (string item in list)
        {
            if (seen.Add(item))
            {
                yield return item;
            }
        }
    }

    private static string? WhereExe(string exe)
    {
        try
        {
            string output = RunCapture("where.exe", new[] { exe }, 8, out int code);
            if (code == 0)
            {
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string p = line.Trim();
                    if (File.Exists(p))
                    {
                        return p;
                    }
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static string? FirstExisting(IEnumerable<string> paths)
    {
        foreach (string p in paths)
        {
            try
            {
                if (File.Exists(p))
                {
                    return p;
                }
            }
            catch
            {
            }
        }
        return null;
    }

    // ==================== 进程辅助 ====================

    /// <summary>给子进程补上 node24 / pnpm / git 的目录，解决“本机能在终端跑、启动器里找不到命令”。</summary>
    public static void AugmentPath(ProcessStartInfo psi)
    {
        try
        {
            List<string> parts = new();
            foreach (string? dir in new[] { NodeDir, PnpmDir, GitDir })
            {
                if (!string.IsNullOrEmpty(dir) && !parts.Any((string x) => string.Equals(x, dir, StringComparison.OrdinalIgnoreCase)))
                {
                    parts.Add(dir);
                }
            }
            if (parts.Count == 0)
            {
                return;
            }
            string oldPath = "";
            if (psi.Environment.TryGetValue("PATH", out string? p))
            {
                oldPath = p ?? "";
            }
            if (oldPath.Length == 0)
            {
                oldPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            }
            psi.Environment["PATH"] = string.Join(";", parts) + ";" + oldPath;
        }
        catch
        {
        }
    }

    /// <summary>同步执行并捕获输出（用于探测；业务命令仍走各服务的 RunCapture）。</summary>
    public static string RunCapture(string fileName, IEnumerable<string> args, int timeoutSec, out int exitCode)
    {
        exitCode = -1;
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }
            // 探测用 RunCapture 不调用 AugmentPath，避免“解析 pnpm → 探测 node → where → 再解析 pnpm”的递归。
            using Process? p = Process.Start(psi);
            if (p == null)
            {
                return "";
            }
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(Math.Max(1, timeoutSec) * 1000))
            {
                try { p.Kill(true); } catch { }
                return "";
            }
            string output = outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult();
            exitCode = p.ExitCode;
            return output;
        }
        catch
        {
            return "";
        }
    }
}
