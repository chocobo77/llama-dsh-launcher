using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LlamaDSH_Launcher
{
    /// <summary>
    /// 应用日志：错误 / 运行 / 崩溃 / 信息四类，落盘到用户目录下的日志文件夹。
    /// 所有写入都先过 Redact(脱敏)再落盘；按大小滚动保留 3 份历史。
    /// </summary>
    public static class AppLog
    {
        /// <summary>单个日志文件大小上限：超过后滚动为 .1/.2/.3（保留 3 份历史）。</summary>
        private const long MaxBytes = 4 * 1024 * 1024;

        /// <summary>滚动保留的历史份数（.1 最新）。</summary>
        private const int KeepBackups = 3;

        /// <summary>保护"轮转 + 追加"这一整段的进程内互斥。</summary>
        private static readonly object _logLock = new object();

        /// <summary>
        /// 本次进程启动的标识。日志游标(seq)每次启动都从 1 重来，客户端手上的 after 是上一轮的
        /// 大数时会永久返回空且不报错，必须让客户端能靠 boot 变化发现重启并重置游标。
        /// </summary>
        public static string BootId { get; } = DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Environment.ProcessId;

        public static string LogDir { get; private set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh_launcher_logs");

        /// <summary>仅测试用: 把日志根切到临时目录, 防止冒烟测试覆盖真实用户的 run.log。</summary>
        public static void SetLogDirForTests(string dir)
        {
            lock (_logLock)
            {
                LogDir = dir;
            }
        }

        static AppLog()
        {
            try { if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir); } catch { }
        }

        private static readonly Regex TokenQuery = new Regex("([?&]token=)[^&\\s\"'<>|]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TokenJson = new Regex("(\"token\"\\s*:\\s*\")[^\"]*(\")", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex BearerValue = new Regex("(Bearer\\s+)[A-Za-z0-9+_\\-/=.]{12,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 抹掉日志里的访问凭据。DSH 的启动横幅会把 `dsh web: http://127.0.0.1:3080/?token=…`
        /// 原样打出来，而这行既进 run.log、也进界面日志和 agent 的 logs 响应，还会被
        /// 「复制诊断信息」整段粘给外部。只改日志文本，不影响打开浏览器用的真 URL。
        /// </summary>
        public static string Redact(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? "";
            }
            string outText = TokenQuery.Replace(text, "$1‹已隐藏›");
            outText = TokenJson.Replace(outText, "$1‹已隐藏›$2");
            return BearerValue.Replace(outText, "$1‹已隐藏›");
        }

        /// <summary>
        /// 生成一段可直接粘贴给维护者的诊断文本：环境信息 + 当前错误 + 最近日志尾部。
        /// 用于"复制诊断信息"按钮，避免用户只能描述"它报错了"。
        /// 尾部按字节从文件末尾倒读（以前是 ReadAllLines，2 MB 的 error.log 会在 UI 线程上
        /// 整份读进来），并把 run.log 尾部一并带上——排查启动问题时最有用的是那份。
        /// </summary>
        public static string BuildDiagnostics(string? note = null, int tailLines = 40)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== LlamaDSH Launcher 诊断信息 ===");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            string version = "-";
            try { version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "-"; } catch { }
            sb.AppendLine("程序版本: " + version + "  本次启动: " + BootId);
            sb.AppendLine("系统: " + Environment.OSVersion.VersionString + " (" + (Environment.Is64BitOperatingSystem ? "x64" : "x86") + ")");
            sb.AppendLine("运行时: " + Environment.Version);
            sb.AppendLine("日志目录: " + LogDir);
            if (!string.IsNullOrWhiteSpace(note))
            {
                sb.AppendLine();
                sb.AppendLine("--- 当前错误 / 上下文 ---");
                sb.AppendLine(Redact(note!.Trim()));
            }
            AppendTail(sb, "run.log", tailLines);
            AppendTail(sb, "error.log", tailLines);
            AppendTail(sb, "crash.log", tailLines);
            return sb.ToString();
        }

        private static void AppendTail(StringBuilder sb, string fileName, int tailLines)
        {
            try
            {
                string text = Tail(fileName, 128 * 1024);
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }
                string[] all = text.Split('\n');
                int start = Math.Max(0, all.Length - Math.Max(1, tailLines));
                sb.AppendLine();
                sb.AppendLine($"--- {fileName} 末 {all.Length - start} 行（从末尾 {Math.Min(128 * 1024, new FileInfo(Path.Combine(LogDir, fileName)).Length)} 字节内）---");
                for (int i = start; i < all.Length; i++)
                {
                    sb.AppendLine(Redact(all[i].TrimEnd('\r')));
                }
            }
            catch { }
        }

        /// <summary>
        /// 只读文件末尾至多 maxBytes 字节（首行可能被切断，丢掉它）。用于诊断组装与
        /// 尾部查看，避免为了最后几十行把整个日志读进内存。
        /// </summary>
        public static string Tail(string fileName, long maxBytes = 131072)
        {
            try
            {
                string path = Path.Combine(LogDir, fileName);
                if (!File.Exists(path))
                {
                    return "";
                }
                using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long len = fs.Length;
                if (len == 0)
                {
                    return "";
                }
                int take = (int)Math.Min(Math.Max(0, maxBytes), len);
                fs.Seek(len - take, SeekOrigin.Begin);
                byte[] buf = new byte[take];
                int got = 0;
                while (got < take)
                {
                    int n = fs.Read(buf, got, take - got);
                    if (n <= 0)
                    {
                        break;
                    }
                    got += n;
                }
                string text = Encoding.UTF8.GetString(buf, 0, got);
                if (len > take)
                {
                    int nl = text.IndexOf('\n');
                    if (nl >= 0)
                    {
                        text = text.Substring(nl + 1);
                    }
                }
                return text;
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 一次性把历史日志里已落盘的凭据抹掉（含滚动出的 .1/.2/.3）。
        /// 只加"今后不写"是不够的：旧 run.log 里的 DSH token 会被「复制诊断信息」的尾部
        /// 原样带出去。逐份整读(单份上限 4 MB)后同目录临时文件替换，失败就放弃这一次。
        /// </summary>
        public static void ScrubExisting()
        {
            try
            {
                string[] names = { "run.log", "info.log", "error.log", "crash.log" };
                foreach (string name in names)
                {
                    for (int i = 0; i <= KeepBackups; i++)
                    {
                        string path = Path.Combine(LogDir, (i == 0) ? name : (name + "." + i));
                        try
                        {
                            if (!File.Exists(path))
                            {
                                continue;
                            }
                            string text = File.ReadAllText(path);
                            string clean = Redact(text);
                            if (clean == text)
                            {
                                continue;
                            }
                            string tmp = path + ".scrub.tmp";
                            File.WriteAllText(tmp, clean, new UTF8Encoding(false));
                            File.Replace(tmp, path, null);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public static void WriteError(string message) => Append("error.log", message);

        public static void WriteInfo(string message) => Append("info.log", message);

        /// <summary>单行写 run.log（正常路径走 WriteRunLines；这里给启动早期等零散场景）。</summary>
        public static void WriteRun(string content) => Append("run.log", content);

        /// <summary>
        /// 逐行写 run.log：每行一个 `[yyyy-MM-dd HH:mm:ss]` 前缀。以前整段一次写，
        /// 只有首行带日期、且那是"落盘时刻"而不是"发生时刻"，run.log 里出现
        /// `[2026-11-09 02:35:48] [02:34:34] …` 这种错位双戳，多实例交错时无法对表。
        /// level 为 ERROR/WARN 时带级别标签，INFO 不带（保持肉眼可读，过滤用 agent 的 level=）。
        /// </summary>
        public static void WriteRunLines(IEnumerable<(string level, string text)> lines)
        {
            try
            {
                string path = Path.Combine(LogDir, "run.log");
                lock (_logLock)
                {
                    RotateIfNeeded(path);
                    using StreamWriter w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                    foreach ((string level, string text) in lines)
                    {
                        string tag = (level == "ERROR" || level == "WARN") ? ("[" + level + "] ") : "";
                        w.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {tag}{Redact(text)}");
                    }
                }
            }
            catch { }
        }

        public static void WriteCrash(string message) => Append("crash.log", message);

        private static void Append(string filename, string content)
        {
            try
            {
                var path = Path.Combine(LogDir, filename);
                lock (_logLock)
                {
                    RotateIfNeeded(path);
                    File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {Redact(content)}\n");
                }
            }
            catch { }
        }

        /// <summary>
        /// 超过 MaxBytes 时滚动为 &lt;file&gt;.1（旧的 .1→.2、.2→.3，最老的 .3 丢弃）。
        /// 轮转失败（文件被其它进程占用等）只忽略，绝不影响本次日志写入。
        /// </summary>
        private static void RotateIfNeeded(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }
                if (new FileInfo(path).Length <= MaxBytes)
                {
                    return;
                }
                string oldest = path + "." + KeepBackups;
                if (File.Exists(oldest))
                {
                    File.Delete(oldest);
                }
                for (int i = KeepBackups - 1; i >= 1; i--)
                {
                    string from = path + "." + i;
                    if (File.Exists(from))
                    {
                        File.Move(from, path + "." + (i + 1));
                    }
                }
                File.Move(path, path + ".1");
            }
            catch { }
        }
    }
}
