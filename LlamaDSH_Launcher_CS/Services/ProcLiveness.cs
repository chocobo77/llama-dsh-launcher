using System;
using System.Diagnostics;

namespace LlamaDSH_Launcher
{
    /// <summary>
    /// 子进程存活判定的唯一入口。
    ///
    /// Process 对象与它代表的进程是两回事：对象可能已经与进程脱钩——
    /// PumpProcessStreams 在管道 EOF 后会 Dispose(Process)（为了保住尾部日志，
    /// 释放权归读线程），GetProcessById 拿到的对象在目标退出后也读不到状态。
    /// 这时读 HasExited 会抛 InvalidOperationException("No process is associated with
    /// this object")。以前多处轮询直接用裸 HasExited，一旦对象脱钩就把
    /// HealthCheck 定时器、显存熔断、退出回调全部打成未处理异常反复刷屏，
    /// 而且最先抛的那处正好在"清理字段"之前，字段永远指着一个已释放对象。
    ///
    /// 这里把"判定不了"一律按已退出处理：调用方据此清引用，状态不会长期中毒。
    /// </summary>
    public static class ProcLiveness
    {
        /// <summary>进程对象是否还代表一个活进程；对象脱钩或已释放都算已退出。</summary>
        public static bool Alive(Process? p)
        {
            if (p == null)
            {
                return false;
            }
            try
            {
                return !p.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // 查询进程状态时被系统拒绝(访问受限/进程正在退出), 同样只能按已退出处理
                return false;
            }
        }

        /// <summary>
        /// 按 PID 判定存活，并可用启动时刻排除 PID 复用。
        /// PID 不存在(ArgumentException)按"不存活"返回，不当成错误。
        /// </summary>
        public static bool PidAlive(int pid, DateTime? startTime = null)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                if (p.HasExited)
                {
                    return false;
                }
                if (startTime.HasValue && p.StartTime != startTime.Value)
                {
                    return false;
                }
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 安全 Dispose：Process 对象可能已被读线程释放过，重复 Dispose 不该冒异常。
        /// </summary>
        public static void SafeDispose(Process? p)
        {
            if (p == null)
            {
                return;
            }
            try
            {
                p.Dispose();
            }
            catch
            {
                // Dispose 失败没有可操作动作：对象引用即将丢弃，句柄由 GC 兜底
            }
        }
    }
}
