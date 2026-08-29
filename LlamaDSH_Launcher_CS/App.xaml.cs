using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;


namespace LlamaDSH_Launcher;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [LibraryImport("shcore.dll")]
    private static partial int SetProcessDpiAwareness(int awareness);

    protected override void OnStartup(StartupEventArgs e)
    {
        // 自我降权：从安装器拉起时继承提权(管理员)令牌，而提权上下文派生的 DSH 子进程
        // 会因环境差异导致 pnpm 符号链接解析失败(ERR_MODULE_NOT_FOUND)，且 WMI 分离模式
        // 同样受影响(提权调用者由提权 WmiPrvSE 实例服务)。检测到提权则经 explorer.exe
        // 以普通令牌重启自己——与诊断结论一致：非提权上下文下一切正常。
        try
        {
            using (System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                {
                    string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = "\"" + exePath + "\"",
                            UseShellExecute = true
                        });
                        Shutdown();
                        return;
                    }
                }
            }
        }
        catch { }

        // 禁用 WPF 硬件加速：AMD 驱动在长期运行 WPF 应用时可能泄漏显存/系统内存，
        // 导致"无法创建新的堆栈防护页面"。启动器界面简单，软件渲染完全够用。
        try { System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly; } catch { }

        // DPI 适配
        try { SetProcessDpiAwareness(2); } catch { }

        // 单实例检查
        const string mutexName = "LlamaDSH_Launcher_SingleInstance_Mutex";
        _mutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBoxW(IntPtr.Zero,
                "LlamaDSH Launcher 已经在运行中。\n请检查系统托盘图标。",
                "重复启动", 0x40);
            Shutdown();
            return;
        }

        // 全局未捕获异常兜底（B4）：写入 crash.log，避免后台线程静默崩溃无痕
        DispatcherUnhandledException += (_, ev) =>
        {
            AppLog.WriteCrash($"Dispatcher 未处理异常: {ev.Exception}");
            try
            {
                MessageBoxW(IntPtr.Zero,
                    $"程序发生未处理异常：\n{ev.Exception.Message}\n\n崩溃日志已写入：\n{Path.Combine(AppLog.LogDir, "crash.log")}",
                    "LlamaDSH Launcher - 异常", 0x10);
            }
            catch { }
            ev.Handled = true; // 已记录，不再让 WPF 弹出原始崩溃框
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            var ex = ev.ExceptionObject as Exception;
            AppLog.WriteCrash($"AppDomain 未处理异常: {ex?.ToString() ?? ev.ExceptionObject?.ToString()}");
        };
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            AppLog.WriteCrash($"Task 未观察异常: {ev.Exception}");
            ev.SetObserved();
        };

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
