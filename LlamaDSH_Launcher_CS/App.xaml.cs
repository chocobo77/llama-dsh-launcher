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
        // 安装器血统检测与血统切断：Inno Setup"安装完成后运行"拉起本程序时，会遗留
        // __COMPAT_LAYER=ElevateCreateProcess 等不可见的安装器上下文。实测(调用代码隔离
        // 探针 V1)：同样的 WMI 调用代码在用户自己的进程中一切正常，唯独从启动器进程
        // 发起必失败——即问题在进程血统而非代码或环境。因此只要检测到安装器血统标记，
        // 就经 explorer.exe 以用户 shell 的干净血统重启自己(无论是否提权)。
        try
        {
            string? compatLayer = Environment.GetEnvironmentVariable("__COMPAT_LAYER");
            bool installerSpawn = !string.IsNullOrEmpty(compatLayer);
            using (System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                bool elevated = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                if (installerSpawn || elevated)
                {
                    string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        AppLog.WriteInfo($"installer/elevated lineage detected (compat={compatLayer ?? "-"}, elevated={elevated}, pid={Environment.ProcessId}), relaunching clean via explorer.exe: {exePath}");
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

        // 单实例检查（M6）：经 explorer 重启自身时，旧实例可能尚未释放 mutex，
        // 新实例只尝试一次会误判“已在运行”后退出（表现为双击没反应）。
        // 因此最多重试 3 次、每次间隔 300ms，全部失败才提示重复启动。
        const string mutexName = "LlamaDSH_Launcher_SingleInstance_Mutex";
        Mutex? mutex = null;
        bool createdNew = false;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            mutex = new Mutex(true, mutexName, out createdNew);
            if (createdNew)
            {
                if (attempt > 1)
                {
                    AppLog.WriteInfo($"单实例 mutex 第 {attempt} 次尝试获取成功（旧实例正在退出）");
                }
                break;
            }
            // 未拿到所有权：先释放本句柄，避免自己持有内核对象导致后续重试始终失败
            mutex.Dispose();
            mutex = null;
            if (attempt < 3)
            {
                Thread.Sleep(300);
            }
        }
        if (!createdNew)
        {
            mutex?.Dispose();
            MessageBoxW(IntPtr.Zero,
                "LlamaDSH Launcher 已经在运行中。\n请检查系统托盘图标。",
                "重复启动", 0x40);
            Shutdown();
            return;
        }
        _mutex = mutex;

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
