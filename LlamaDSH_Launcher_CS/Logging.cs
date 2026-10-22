using System;
using System.IO;
using System.Text;

namespace LlamaDSH_Launcher
{
    /// <summary>
    /// 应用日志：错误 / 运行 / 崩溃三类，落盘到用户目录下的日志文件夹。
    /// </summary>
    public static class AppLog
    {
        /// <summary>单个日志文件大小上限：超过后把旧文件滚动为 .1（只保留一份历史）。</summary>
        private const long MaxBytes = 2 * 1024 * 1024;

        /// <summary>保护"轮转 + 追加"这一整段的进程内互斥。</summary>
        private static readonly object _logLock = new object();

        public static string LogDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh_launcher_logs");

        static AppLog()
        {
            try { if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir); } catch { }
        }

        /// <summary>
        /// 生成一段可直接粘贴给维护者的诊断文本：环境信息 + 当前错误 + 最近日志尾部。
        /// 用于"复制诊断信息"按钮，避免用户只能描述"它报错了"。
        /// </summary>
        public static string BuildDiagnostics(string? note = null, int tailLines = 40)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== LlamaDSH Launcher 诊断信息 ===");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            string version = "-";
            try { version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "-"; } catch { }
            sb.AppendLine("程序版本: " + version);
            sb.AppendLine("系统: " + Environment.OSVersion.VersionString + " (" + (Environment.Is64BitOperatingSystem ? "x64" : "x86") + ")");
            sb.AppendLine("运行时: " + Environment.Version);
            sb.AppendLine("日志目录: " + LogDir);
            if (!string.IsNullOrWhiteSpace(note))
            {
                sb.AppendLine();
                sb.AppendLine("--- 当前错误 / 上下文 ---");
                sb.AppendLine(note!.Trim());
            }
            AppendTail(sb, "error.log", tailLines);
            AppendTail(sb, "crash.log", tailLines);
            return sb.ToString();
        }

        private static void AppendTail(StringBuilder sb, string fileName, int tailLines)
        {
            try
            {
                string path = Path.Combine(LogDir, fileName);
                if (!File.Exists(path))
                {
                    return;
                }
                string[] all = File.ReadAllLines(path);
                int start = Math.Max(0, all.Length - Math.Max(1, tailLines));
                sb.AppendLine();
                sb.AppendLine($"--- {fileName} 末 {all.Length - start} 行（共 {all.Length} 行）---");
                for (int i = start; i < all.Length; i++)
                {
                    sb.AppendLine(all[i]);
                }
            }
            catch
            {
            }
        }

        public static void WriteError(string message) => Append("error.log", message);

        public static void WriteInfo(string message) => Append("info.log", message);

        public static void WriteRun(string content) => Append("run.log", content);

        public static void WriteCrash(string message) => Append("crash.log", message);

        private static void Append(string filename, string content)
        {
            try
            {
                var path = Path.Combine(LogDir, filename);
                lock (_logLock)
                {
                    RotateIfNeeded(path);
                    File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {content}\n");
                }
            }
            catch { }
        }

        /// <summary>
        /// 超过 MaxBytes 时把当前文件滚动成 &lt;file&gt;.1（覆盖旧 .1）。
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
                var backup = path + ".1";
                if (File.Exists(backup))
                {
                    File.Delete(backup);
                }
                File.Move(path, backup);
            }
            catch { }
        }
    }
}
