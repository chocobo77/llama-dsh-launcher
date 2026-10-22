using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace LlamaDSH_Launcher.Services
{
    /// <summary>子进程执行结果（统一由 ProcessRunner 产出）。</summary>
    public readonly struct ProcessResult
    {
        public ProcessResult(int exitCode, string? stdOut, string? stdErr, bool timedOut, bool started)
        {
            ExitCode = exitCode;
            StdOut = stdOut ?? string.Empty;
            StdErr = stdErr ?? string.Empty;
            TimedOut = timedOut;
            Started = started;
        }

        /// <summary>退出码；超时或启动失败时为 -1。</summary>
        public int ExitCode { get; }

        /// <summary>标准输出全文。</summary>
        public string StdOut { get; }

        /// <summary>标准错误全文；启动失败时为异常信息。</summary>
        public string StdErr { get; }

        /// <summary>是否因超时被强制结束。</summary>
        public bool TimedOut { get; }

        /// <summary>进程是否成功启动。</summary>
        public bool Started { get; }

        /// <summary>启动成功、未超时且退出码为 0。</summary>
        public bool Success => Started && !TimedOut && ExitCode == 0;

        /// <summary>stdout 与 stderr 合并（stdout 在前），用于直接展示给用户。</summary>
        public string Combined => string.IsNullOrEmpty(StdErr) ? StdOut : StdOut + StdErr;
    }

    /// <summary>
    /// 统一的子进程执行器。
    /// 固定的正确顺序：Start → 同时 ReadToEndAsync(stdout/stderr) → WaitForExit(超时) → 超时 Kill(true) → 收拢读取任务。
    /// 这样可以避免两个历史问题：1) 先 ReadToEnd 再 WaitForExit 会让超时彻底失效；
    /// 2) 串行读 stdout/stderr 可能因管道缓冲区写满而互相锁死。
    /// 本方法是同步阻塞的，请在后台线程或 Task.Run 内调用，不要直接在 UI 线程上等待长任务。
    /// </summary>
    public static class ProcessRunner
    {
        /// <summary>进程退出后等待读取任务收尾的宽限时间。</summary>
        private const int ReadGraceMs = 3000;

        /// <summary>
        /// 执行进程并收集全部输出。
        /// </summary>
        /// <param name="fileName">可执行文件绝对路径或 PATH 中的名字。</param>
        /// <param name="arguments">参数列表（逐个添加，不做字符串拼接，天然免疫注入）。</param>
        /// <param name="timeoutMs">超时毫秒；&lt;= 0 表示无限等待（仅在明确知道对方会正常退出时使用）。</param>
        /// <param name="workingDirectory">工作目录，可为 null。</param>
        /// <param name="configure">可选：额外配置 StartInfo，例如环境变量、Verb。</param>
        public static ProcessResult Run(
            string fileName,
            IReadOnlyList<string>? arguments = null,
            int timeoutMs = 0,
            string? workingDirectory = null,
            Action<ProcessStartInfo>? configure = null)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (!string.IsNullOrEmpty(workingDirectory))
            {
                psi.WorkingDirectory = workingDirectory;
            }

            if (arguments != null)
            {
                foreach (string arg in arguments)
                {
                    psi.ArgumentList.Add(arg);
                }
            }

            configure?.Invoke(psi);

            using Process p = new Process { StartInfo = psi };
            try
            {
                if (!p.Start())
                {
                    return new ProcessResult(-1, string.Empty, "无法启动进程：" + fileName, false, false);
                }
            }
            catch (Exception ex)
            {
                return new ProcessResult(-1, string.Empty, ex.Message, false, false);
            }

            // 关键：两个流必须同时开始读，且必须在等待退出之前开始。
            Task<string> outTask = p.StandardOutput.ReadToEndAsync();
            Task<string> errTask = p.StandardError.ReadToEndAsync();

            bool exited;
            if (timeoutMs > 0)
            {
                exited = p.WaitForExit(timeoutMs);
            }
            else
            {
                p.WaitForExit();
                exited = true;
            }

            if (!exited)
            {
                try { p.Kill(true); } catch { }
                try { p.WaitForExit(ReadGraceMs); } catch { }
                // Kill 之后流会随进程终结而关闭；这里仍给一个宽限，避免 Kill 失败时永久阻塞。
                return new ProcessResult(-1, Drain(outTask, ReadGraceMs), Drain(errTask, ReadGraceMs), true, true);
            }

            // 无参 WaitForExit 确保异步读取完成收尾（成本极低，避免尾部输出丢失）。
            try { p.WaitForExit(); } catch { }

            int exitCode;
            try { exitCode = p.ExitCode; }
            catch { exitCode = -1; }

            // 正常退出后进程已不可能再产生输出，可以无限等待读取任务收尾——否则大输出会被静默截断。
            return new ProcessResult(exitCode, Drain(outTask, -1), Drain(errTask, -1), false, true);
        }

        /// <summary>
        /// 等待读取任务收尾并取出文本。timeoutMs &lt;= 0 表示无限等待（用于进程已正常退出的场景）；
        /// 超时或异常返回空串，不抛出。
        /// </summary>
        private static string Drain(Task<string> task, int timeoutMs)
        {
            try
            {
                if (timeoutMs > 0 && !task.Wait(timeoutMs))
                {
                    return string.Empty;
                }
                if (timeoutMs <= 0)
                {
                    task.Wait();
                }
                return task.Result ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
