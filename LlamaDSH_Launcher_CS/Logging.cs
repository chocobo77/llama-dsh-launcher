using System;
using System.IO;

namespace LlamaDSH_Launcher
{
    /// <summary>
    /// 应用日志：错误 / 运行 / 崩溃三类，落盘到用户目录下的日志文件夹。
    /// </summary>
    public static class AppLog
    {
        public static string LogDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh_launcher_logs");

        static AppLog()
        {
            try { if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir); } catch { }
        }

        public static void WriteError(string message) => Append("error.log", message);

        public static void WriteRun(string content) => Append("run.log", content);

        public static void WriteCrash(string message) => Append("crash.log", message);

        private static void Append(string filename, string content)
        {
            try
            {
                var path = Path.Combine(LogDir, filename);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {content}\n");
            }
            catch { }
        }
    }
}
