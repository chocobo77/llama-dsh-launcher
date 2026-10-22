using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LlamaDSH_Launcher.Services
{
    /// <summary>
    /// Node.js 傻瓜式安装/更新/配置服务。
    /// 参考配置（当前环境）：Node v24.x MSI 静默装到 C:\Program Files\nodejs，安装器自动写入机器 PATH；
    /// pnpm 由 npm 全局安装到 %APPDATA%\npm。DSH 运行时要求 node v24+ 与 pnpm（插件安装/升级）。
    /// </summary>
    public static class NodeSetup
    {
        // 下载源：优先国内镜像，失败回退官方。
        private static readonly string[] MirrorBases =
        {
            "https://registry.npmmirror.com/-/binary/node",
            "https://nodejs.org/dist"
        };

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

        public sealed class NodeStatus
        {
            /// <summary>满足 v24+ 的 node.exe（无则 null）。</summary>
            public string? Node24Exe;
            public string Node24Version = "";
            /// <summary>任意已装 node.exe（当没有 v24+ 时用于提示现状）。</summary>
            public string? AnyNodeExe;
            public string AnyNodeVersion = "";
            public string? PnpmExe;
            public string PnpmVersion = "";
            public bool MeetsRequirement => Node24Exe != null;
        }

        /// <summary>探测当前 node/pnpm 状态（复用 Toolchain 解析逻辑，强制刷新缓存）。</summary>
        public static NodeStatus Detect(bool force = true)
        {
            var s = new NodeStatus();
            try
            {
                s.Node24Exe = Toolchain.ResolveNode24Exe(force);
                if (s.Node24Exe != null)
                {
                    s.Node24Version = GetToolVersion(s.Node24Exe, "-v");
                }
                else
                {
                    s.AnyNodeExe = Toolchain.ResolveNodeExe(false, force);
                    if (s.AnyNodeExe != null)
                    {
                        s.AnyNodeVersion = GetToolVersion(s.AnyNodeExe, "-v");
                    }
                }
            }
            catch (Exception ex)
            {
                // 探测失败按未找到处理，但要留痕，避免“为什么没检测到”无从查起
                AppLog.WriteError("NodeSetup.Detect 探测 node 失败: " + ex.Message);
            }
            try
            {
                s.PnpmExe = Toolchain.ResolvePnpmExe(force);
                if (s.PnpmExe != null)
                {
                    s.PnpmVersion = GetToolVersion(s.PnpmExe, "-v");
                }
            }
            catch (Exception ex)
            {
                // 同上：按未找到处理，同时记录异常
                AppLog.WriteError("NodeSetup.Detect 探测 pnpm 失败: " + ex.Message);
            }
            return s;
        }

        /// <summary>查询最新 v24.x 版本号（如 "v24.19.0"）；失败返回空串。</summary>
        public static async Task<string> GetLatestV24Async()
        {
            foreach (string mirrorBase in MirrorBases)
            {
                try
                {
                    using var resp = await Http.GetAsync(mirrorBase + "/index.json");
                    if (!resp.IsSuccessStatusCode)
                    {
                        continue;
                    }
                    string json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    string best = "";
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        string v = e.TryGetProperty("version", out var vp) ? vp.GetString() ?? "" : "";
                        if (!v.StartsWith("v24."))
                        {
                            continue;
                        }
                        if (SemverGreater(v, best))
                        {
                            best = v;
                        }
                    }
                    if (best != "")
                    {
                        return best;
                    }
                }
                catch
                {
                    // 换下一个镜像
                }
            }
            return "";
        }

        /// <summary>下载指定版本 node MSI 到 tempDir；成功返回本地路径，失败返回 null。</summary>
        public static async Task<string?> DownloadMsiAsync(string version, string tempDir, Action<string>? log = null)
        {
            string fileName = $"node-{version}-x64.msi";
            foreach (string mirrorBase in MirrorBases)
            {
                string url = $"{mirrorBase}/{version}/{fileName}";
                string dest = Path.Combine(tempDir, fileName);
                try
                {
                    log?.Invoke($"下载: {url}");
                    // HttpClient.Timeout 只覆盖到“拿到响应头”，流式读取不受它约束，这里显式给整段下载限时 10 分钟。
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                    using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (!resp.IsSuccessStatusCode)
                    {
                        log?.Invoke($"  失败 (HTTP {(int)resp.StatusCode})，尝试下一源...");
                        continue;
                    }
                    long total = resp.Content.Headers.ContentLength ?? -1;
                    using (var fs = File.Create(dest))
                    {
                        using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                        byte[] buf = new byte[64 * 1024];
                        long got = 0;
                        long nextMb = 10; // 没有 Content-Length 时按体积报进度，每 10MB 一条
                        int lastPct = -1;
                        int read;
                        while ((read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                        {
                            // 双保险：个别流实现不响应 token 时，也能在超时后跳出循环。
                            if (cts.IsCancellationRequested)
                            {
                                throw new OperationCanceledException();
                            }
                            await fs.WriteAsync(buf, 0, read, cts.Token);
                            got += read;
                            if (total > 0)
                            {
                                int pct = (int)(got * 100 / total);
                                if (pct >= lastPct + 5 || pct == 100)
                                {
                                    lastPct = pct;
                                    log?.Invoke($"  下载进度: {pct}% ({got / 1024.0 / 1024.0:F1} MB)");
                                }
                            }
                            else if (got >= nextMb * 1024 * 1024)
                            {
                                log?.Invoke($"  已下载 {got / 1024.0 / 1024.0:F1} MB");
                                nextMb += 10;
                            }
                        }
                    }
                    if (!await VerifyMsiAsync(dest, mirrorBase, version, fileName, log))
                    {
                        log?.Invoke("  文件校验未通过，尝试下一源...");
                        try { File.Delete(dest); } catch { /* ignore */ }
                        continue;
                    }
                    return dest;
                }
                catch (OperationCanceledException)
                {
                    AppLog.WriteError($"NodeSetup.DownloadMsiAsync：下载超时（超过 10 分钟）{url}");
                    log?.Invoke("  下载超时（超过 10 分钟），已放弃该源；请检查网络后重试。");
                    try { if (File.Exists(dest)) File.Delete(dest); } catch { /* ignore */ }
                }
                catch (Exception ex)
                {
                    AppLog.WriteError($"NodeSetup.DownloadMsiAsync：下载失败 {url}：{ex.Message}");
                    log?.Invoke($"  失败: {ex.Message}，尝试下一源...");
                    try { if (File.Exists(dest)) File.Delete(dest); } catch { /* ignore */ }
                }
            }
            return null;
        }

        /// <summary>
        /// 校验下载的 MSI：优先与同目录 SHASUMS256.txt 比对 SHA-256；
        /// 取不到校验和或计算哈希失败时回退为“数字签名校验（签名主体须为 Node.js Foundation）”，
        /// 签名校验不通过即判定该源不可用（该包随后会以 runas 提权执行，不能只看体积就放行）。
        /// </summary>
        private static async Task<bool> VerifyMsiAsync(string path, string mirrorBase, string version, string fileName, Action<string>? log)
        {
            const long MinBytes = 5L * 1024 * 1024;
            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                AppLog.WriteError($"NodeSetup.VerifyMsiAsync：读取下载文件大小失败 {path}：{ex.Message}");
                log?.Invoke("  无法读取已下载文件，判定为失败。");
                return false;
            }
            if (size < MinBytes)
            {
                log?.Invoke("  下载文件过小，疑似损坏，尝试下一源...");
                return false;
            }
            string? expected = await TryGetExpectedSha256Async(mirrorBase, version, fileName, log);
            if (string.IsNullOrEmpty(expected))
            {
                AppLog.WriteInfo($"NodeSetup.VerifyMsiAsync：未能取得 {fileName} 的 SHA-256，回退为数字签名校验（{size / 1024.0 / 1024.0:F1} MB）。");
                log?.Invoke($"  未取得官方 SHA-256，改为校验安装包数字签名（文件大小 {size / 1024.0 / 1024.0:F1} MB）...");
                if (!VerifyMsiSignature(path, log))
                {
                    log?.Invoke("  安装包数字签名校验未通过，已丢弃该文件，尝试下一源...");
                    return false;
                }
                log?.Invoke("  安装包数字签名校验通过（签名主体为 Node.js 官方）。");
                return true;
            }
            string actual;
            try
            {
                using var fs = File.OpenRead(path);
                actual = Convert.ToHexString(SHA256.HashData(fs));
            }
            catch (Exception ex)
            {
                AppLog.WriteError($"NodeSetup.VerifyMsiAsync：计算 SHA-256 失败 {path}：{ex.Message}");
                log?.Invoke("  计算 SHA-256 失败，改为校验安装包数字签名...");
                if (!VerifyMsiSignature(path, log))
                {
                    log?.Invoke("  安装包数字签名校验未通过，已丢弃该文件，尝试下一源...");
                    return false;
                }
                log?.Invoke("  安装包数字签名校验通过（签名主体为 Node.js 官方）。");
                return true;
            }
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.WriteInfo($"NodeSetup.VerifyMsiAsync：{fileName} SHA-256 校验通过。");
                log?.Invoke("  SHA-256 校验通过。");
                return true;
            }
            AppLog.WriteError($"NodeSetup.VerifyMsiAsync：{fileName} SHA-256 不匹配（期望 {expected}，实际 {actual}）。");
            log?.Invoke("  SHA-256 校验不通过，已丢弃该文件。");
            return false;
        }

        /// <summary>
        /// 校验 MSI 的 Authenticode 签名：先用 WinVerifyTrust 做真正的验签（摘要 + 证书链），
        /// 再校验签名主体是否为 Node.js 官方。任何一步不通过都返回 false，由调用方换源。
        /// 注意这里只认【嵌入签名】，不认 catalog 签名——Node 的官方 MSI 是嵌入签名，
        /// 而 Windows 系统目录里很多文件是 catalog 签名，用本方法验会被判“没有数字签名”，这是预期行为。
        /// </summary>
        private static bool VerifyMsiSignature(string path, Action<string>? log)
        {
            try
            {
                AuthenticodeVerifier.Result sig = AuthenticodeVerifier.Verify(path);
                if (!sig.IsValid)
                {
                    AppLog.WriteError($"NodeSetup.VerifyMsiSignature：验签未通过，已拒绝安装（安全策略）{path}：{sig.Message} HRESULT=0x{sig.HResult:X8}");
                    log?.Invoke("  安装包验签未通过（" + sig.Message + "），已拒绝安装。");
                    return false;
                }
                // 官方签名主体随版本变化：较早的安装包是 "CN=Node.js Foundation"，
                // v18 之后改为 "CN=OpenJS Foundation"。只认其中一个会把正常安装包全部拒掉。
                bool official = false;
                foreach (string expected in new[] { "OpenJS Foundation", "Node.js Foundation" })
                {
                    if (sig.Subject.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        official = true;
                        break;
                    }
                }
                if (!official)
                {
                    AppLog.WriteError($"NodeSetup.VerifyMsiSignature：签名主体不是 Node.js 官方（期望含 OpenJS Foundation / Node.js Foundation），已拒绝安装（安全策略）{path}：Subject={sig.Subject}");
                    log?.Invoke("  安装包签名主体不是 Node.js 官方，已拒绝安装（安全策略）。");
                    return false;
                }
                AppLog.WriteInfo($"NodeSetup.VerifyMsiSignature：验签通过 {path}：Subject={sig.Subject}");
                return true;
            }
            catch (Exception ex)
            {
                // 任何异常都不放行，避免回退成"只看体积"的弱校验
                AppLog.WriteError($"NodeSetup.VerifyMsiSignature：验签异常，已拒绝安装（安全策略）{path}：{ex.Message}");
                log?.Invoke("  安装包验签失败，已拒绝安装（安全策略）。");
                return false;
            }
        }

        /// <summary>从镜像同目录的 SHASUMS256.txt 取指定文件的 SHA-256；取不到或格式不符返回 null。</summary>
        private static async Task<string?> TryGetExpectedSha256Async(string mirrorBase, string version, string fileName, Action<string>? log)
        {
            string url = $"{mirrorBase}/{version}/SHASUMS256.txt";
            try
            {
                using var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    AppLog.WriteInfo($"NodeSetup.TryGetExpectedSha256Async：获取校验和失败（HTTP {(int)resp.StatusCode}）{url}");
                    return null;
                }
                string text = await resp.Content.ReadAsStringAsync();
                foreach (string line in text.Split('\n'))
                {
                    // 官方格式：<64 位 hex>  <文件名>（文件名前可能带 *）
                    string[] parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2)
                    {
                        continue;
                    }
                    if (!string.Equals(parts[1].TrimStart('*'), fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string hash = parts[0].Trim();
                    if (hash.Length == 64 && IsHexString(hash))
                    {
                        return hash;
                    }
                }
                AppLog.WriteInfo($"NodeSetup.TryGetExpectedSha256Async：{url} 中找不到 {fileName} 的校验和（格式不符），回退体积校验。");
                return null;
            }
            catch (Exception ex)
            {
                AppLog.WriteInfo($"NodeSetup.TryGetExpectedSha256Async：获取校验和异常（{ex.Message}），回退体积校验。");
                return null;
            }
        }

        private static bool IsHexString(string s)
        {
            foreach (char c in s)
            {
                if (!Uri.IsHexDigit(c))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>运行 MSI 安装（UAC 提权，/passive 显示进度条但无需交互）。成功返回 true；needReboot 表示安装成功但需重启 Windows 才生效（退出码 3010/1641）。</summary>
        public static bool InstallMsi(string msiPath, Action<string>? log, out bool needReboot)
        {
            needReboot = false;
            try
            {
                log?.Invoke("正在运行 Node 安装器（如弹出 UAC 授权窗口请点“是”，随后等待进度条走完）...");
                var psi = new ProcessStartInfo("msiexec.exe", $"/i \"{msiPath}\" /passive /norestart")
                {
                    UseShellExecute = true, // runas 需要 shell 执行
                    Verb = "runas"          // 触发 UAC 提权（已提权时无提示）
                };
                using var p = Process.Start(psi);
                if (p == null)
                {
                    AppLog.WriteError("NodeSetup.InstallMsi：Process.Start 返回 null，无法启动 msiexec。");
                    log?.Invoke("无法启动安装器（msiexec 进程创建失败）。");
                    return false;
                }
                // 提权安装可能被 UAC 弹窗或其它安装程序（Windows Installer 单实例）阻塞，给 15 分钟上限，避免永久挂起。
                if (!p.WaitForExit(15 * 60 * 1000))
                {
                    try { p.Kill(true); } catch { /* ignore */ }
                    AppLog.WriteError("NodeSetup.InstallMsi：msiexec 安装超时（15 分钟），已强制结束进程。");
                    log?.Invoke("安装超时（15 分钟）：可能被其它安装程序占用或被 UAC 弹窗阻塞，请关闭其它安装程序、完成 UAC 授权后重试。");
                    return false;
                }
                int code = p.ExitCode;
                log?.Invoke($"安装器退出码: {code}");
                if (code == 0)
                {
                    return true;
                }
                // 3010/1641 = 安装成功但需要重启，对用户是“成功”，不要把裸数字抛给用户。
                if (code == 3010 || code == 1641)
                {
                    needReboot = true;
                    AppLog.WriteInfo($"NodeSetup.InstallMsi：安装成功，需重启后生效（msiexec 退出码 {code}）。");
                    log?.Invoke("安装成功，需重启后生效。");
                    return true;
                }
                AppLog.WriteError($"NodeSetup.InstallMsi：msiexec 退出码 {code}，安装未成功。");
                log?.Invoke($"安装未成功（退出码 {code}），请查看安装日志或到 nodejs.org 手动安装。");
                return false;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                AppLog.WriteError("NodeSetup.InstallMsi：UAC 授权被取消或无法提权: " + ex.Message);
                log?.Invoke("用户取消了 UAC 授权，安装中止。");
                return false;
            }
            catch (Exception ex)
            {
                AppLog.WriteError("NodeSetup.InstallMsi：启动安装器失败: " + ex.Message);
                log?.Invoke($"启动安装器失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>用 npm 全局安装 pnpm（落到 %APPDATA%\npm，与当前环境一致）。成功返回 true。</summary>
        public static bool InstallPnpm(Action<string>? log = null)
        {
            // 刚装完 Node 时 Toolchain 的缓存可能还是旧值/空，这里强制重新探测一次（NodeDir 由 ResolveNode24Exe 现算，随之刷新）。
            string? nodeExe = Toolchain.ResolveNode24Exe(force: true) ?? Toolchain.ResolveNodeExe(require24: false, force: true);
            string? nodeDir = string.IsNullOrEmpty(nodeExe) ? Toolchain.NodeDir : Path.GetDirectoryName(nodeExe);
            if (string.IsNullOrEmpty(nodeDir))
            {
                AppLog.WriteError("NodeSetup.InstallPnpm：未解析到 node.exe 路径（node 可能未安装或安装未生效），无法执行 npm。");
                log?.Invoke("未找到 node.exe，无法安装 pnpm；请先完成 Node 安装。");
                return false;
            }
            string npmCmd = Path.Combine(nodeDir, "npm.cmd");
            if (!File.Exists(npmCmd))
            {
                AppLog.WriteError($"NodeSetup.InstallPnpm：npm.cmd 不存在（node 目录: {nodeDir}），无法安装 pnpm。");
                log?.Invoke($"未找到 npm.cmd（目录: {nodeDir}），请确认 Node 安装完整。");
                return false;
            }
            try
            {
                log?.Invoke("正在用 npm 全局安装 pnpm（可能需要 1-2 分钟）...");
                // 走 cmd /c 调用 npm（npm 是 .cmd，不能直接由 CreateProcess 启动）；PATH 里补上 node 目录，
                // 因为启动器进程的 PATH 可能还是“安装 Node 之前”的旧值，直接 cmd 里敲 npm 会找不到。
                ProcessResult r = ProcessRunner.Run(
                    "cmd.exe",
                    new[] { "/c", "npm", "install", "-g", "pnpm" },
                    10 * 60 * 1000,
                    nodeDir,
                    psi => PrependNodePath(psi, nodeDir));
                if (r.TimedOut)
                {
                    AppLog.WriteError("NodeSetup.InstallPnpm：npm install -g pnpm 超时（10 分钟），已强制结束进程。");
                    log?.Invoke("pnpm 安装超时（10 分钟）。");
                    return false;
                }
                if (!r.Success)
                {
                    AppLog.WriteError($"NodeSetup.InstallPnpm：npm install -g pnpm 失败（启动={r.Started}，退出码 {r.ExitCode}）：{SnipTail(r.StdErr + Environment.NewLine + r.StdOut)}");
                    log?.Invoke(r.Started ? $"npm 退出码: {r.ExitCode}" : "启动 npm 失败（见日志）。");
                    LogTail(log, r.StdErr + Environment.NewLine + r.StdOut);
                    return false;
                }
                LogTail(log, r.StdOut);
                // 验证：不再靠“%APPDATA%\npm\pnpm.cmd 是否存在”猜，实际跑 pnpm --version 才算装好。
                string? pnpmExe = Toolchain.ResolvePnpmExe(force: true);
                if (string.IsNullOrEmpty(pnpmExe))
                {
                    string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "pnpm.cmd");
                    pnpmExe = File.Exists(fallback) ? fallback : "pnpm";
                }
                ProcessResult ver;
                if (Path.IsPathFullyQualified(pnpmExe))
                {
                    // 绝对路径不能当独立 argv 交给 cmd：ProcessRunner 的 ArgumentList 会把路径内的引号转义成 \"，
                    // 而 cmd.exe 不认反斜杠转义（实测报“不是内部或外部命令”）；直接拼命令行又会被引号剥离规则
                    // 和 & ^ 等元字符误解析。这里用经典安全写法 /d /s /c ""<绝对路径>" --version"：
                    // 整条命令作为一个字符串一次性交给 cmd，路径始终处于双引号内。
                    if (pnpmExe.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0)
                    {
                        AppLog.WriteError($"NodeSetup.InstallPnpm：pnpm 路径含非法字符（双引号/换行），拒绝执行：{pnpmExe}");
                        log?.Invoke("pnpm 路径异常（含双引号或换行），已拒绝执行；请手动运行 pnpm --version 检查。");
                        return false;
                    }
                    ver = ProcessRunner.Run(
                        "cmd.exe",
                        null,
                        15000,
                        nodeDir,
                        psi2 =>
                        {
                            psi2.Arguments = "/d /s /c \"\"" + pnpmExe + "\" --version\"";
                            PrependNodePath(psi2, nodeDir);
                        });
                }
                else
                {
                    // 裸命令名（如 pnpm）：沿用原来的多参数写法，由 cmd 自己按 PATH 解析。
                    ver = ProcessRunner.Run(
                        "cmd.exe",
                        new[] { "/c", pnpmExe, "--version" },
                        15000,
                        nodeDir,
                        psi2 => PrependNodePath(psi2, nodeDir));
                }
                if (!ver.Success)
                {
                    AppLog.WriteError($"NodeSetup.InstallPnpm：pnpm --version 校验失败（启动={ver.Started}，超时={ver.TimedOut}，退出码 {ver.ExitCode}）：{SnipTail(ver.Combined)}");
                    log?.Invoke("安装完成但 pnpm 无法运行，请手动运行 npm install -g pnpm 后重试。");
                    LogTail(log, ver.Combined);
                    return false;
                }
                string pnpmVersion = FirstVersionToken(ver.StdOut);
                if (pnpmVersion == "")
                {
                    pnpmVersion = FirstVersionToken(ver.StdErr);
                }
                if (pnpmVersion == "")
                {
                    AppLog.WriteError("NodeSetup.InstallPnpm：pnpm --version 未返回版本号，输出: " + SnipTail(ver.Combined));
                    log?.Invoke("pnpm 已安装但未返回版本号，请手动执行 pnpm --version 检查。");
                    return false;
                }
                log?.Invoke("pnpm 版本: " + pnpmVersion);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.WriteError("NodeSetup.InstallPnpm：执行异常: " + ex.Message);
                log?.Invoke($"pnpm 安装失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>把 node 目录补进子进程 PATH 前缀（启动器进程的 PATH 可能是安装 Node 之前的旧值）。</summary>
        private static void PrependNodePath(ProcessStartInfo psi, string nodeDir)
        {
            string oldPath = psi.EnvironmentVariables["PATH"] ?? "";
            psi.EnvironmentVariables["PATH"] = oldPath.Length == 0 ? nodeDir : nodeDir + ";" + oldPath;
        }

        private static void LogTail(Action<string>? log, string text)
        {
            if (log == null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            string[] lines = text.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            int start = Math.Max(0, lines.Length - 5);
            for (int i = start; i < lines.Length; i++)
            {
                log("  " + lines[i].Trim());
            }
        }

        /// <summary>运行 exe/cmd 取版本输出（node -v / pnpm -v）；失败返回空串。</summary>
        private static string GetToolVersion(string exe, string arg)
        {
            try
            {
                // 关键：旧实现先 ReadToEnd 再 WaitForExit，读不完就永远等不到超时；改用 ProcessRunner（先起双流异步读，再带超时等待）。
                string file = exe;
                string[] args = new[] { arg };
                if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                {
                    // .cmd/.bat 不能直接由 CreateProcess 启动，交给 cmd.exe 执行。
                    file = "cmd.exe";
                    args = new[] { "/c", exe, arg };
                }
                ProcessResult r = ProcessRunner.Run(file, args, 8000);
                if (!r.Started)
                {
                    AppLog.WriteError($"NodeSetup.GetToolVersion：启动失败 {exe} {arg}：{r.StdErr}");
                    return "";
                }
                if (r.TimedOut)
                {
                    AppLog.WriteError($"NodeSetup.GetToolVersion：执行超时（8000ms）{exe} {arg}");
                    return "";
                }
                string outp = r.StdOut.Trim();
                if (outp.Length == 0)
                {
                    outp = r.StdErr.Trim();
                }
                string token = FirstVersionToken(outp);
                return token.Length > 0 ? token : outp;
            }
            catch (Exception ex)
            {
                AppLog.WriteError($"NodeSetup.GetToolVersion：{exe} {arg} 失败: {ex.Message}");
                return "";
            }
        }

        /// <summary>从命令输出里取第一行形如 v24.19.0 / 10.12.1 的版本号；取不到返回空串。</summary>
        private static string FirstVersionToken(string text)
        {
            foreach (string line in text.Split(new[] { '\n' }))
            {
                string t = line.Trim();
                if (t.Length > 0 && (t[0] == 'v' || char.IsDigit(t[0])))
                {
                    return t;
                }
            }
            return "";
        }

        /// <summary>截取输出末尾（最多 500 字符）用于写错误日志，避免整段输出撑爆日志。</summary>
        private static string SnipTail(string text)
        {
            string s = text.Trim();
            return s.Length <= 500 ? s : s.Substring(s.Length - 500);
        }

        /// <summary>比较 "v24.19.0" 形式的版本号，a > b 返回 true。</summary>
        private static bool SemverGreater(string a, string b)
        {
            if (b == "")
            {
                return a != "";
            }
            int[] pa = ParseVer(a), pb = ParseVer(b);
            for (int i = 0; i < 3; i++)
            {
                if (pa[i] != pb[i])
                {
                    return pa[i] > pb[i];
                }
            }
            return false;
        }

        private static int[] ParseVer(string v)
        {
            string s = v.StartsWith("v") ? v.Substring(1) : v;
            string[] parts = s.Split('.');
            int[] r = new int[3];
            for (int i = 0; i < 3 && i < parts.Length; i++)
            {
                r[i] = int.TryParse(parts[i], out int n) ? n : 0;
            }
            return r;
        }
    }
}
