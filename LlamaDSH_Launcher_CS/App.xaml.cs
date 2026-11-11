using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;


namespace LlamaDSH_Launcher;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;

    // 血统自重启的判定已抽到 Services\LineageGuard.cs（白名单 + 一次封顶），
    // 并由 LlamaDSH_Launcher_CS\selftest.ps1 直接测到；这里只负责执行重启动作。

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [LibraryImport("shcore.dll")]
    private static partial int SetProcessDpiAwareness(int awareness);

    protected override void OnStartup(StartupEventArgs e)
    {
        // 内置 Agent CLI(cli 子命令): 在任何启动逻辑之前处理——它只是 HTTP 客户端,
        // 不触发血统重启/单实例 mutex, 打完请求把退出码原样返回。
        if (e.Args.Length > 0 && string.Equals(e.Args[0], "cli", StringComparison.OrdinalIgnoreCase))
        {
            Environment.Exit(LlamaDSH_Launcher.Services.AgentCli.Run(e.Args));
        }

        // 安装器血统检测与血统切断：Inno Setup"安装完成后运行"拉起本程序时，会遗留
        // __COMPAT_LAYER=ElevateCreateProcess 等不可见的安装器上下文。实测(调用代码隔离
        // 探针 V1)：同样的 WMI 调用代码在用户自己的进程中一切正常，唯独从启动器进程
        // 发起必失败——即问题在进程血统而非代码或环境。因此检测到安装器血统标记时，
        // 就经 explorer.exe 以用户 shell 的干净血统重启自己(无论是否提权)。
        //
        // 判定与封顶的全部理由、约束和白名单都在 Services\LineageGuard.cs（由 selftest.ps1
        // 直接跑），这里只执行动作。要点：
        //   ① 判定走白名单，不能"非空即重启"（用户级安装的 RunAsInvoker 不需要切血统）。
        //   ② 自重启一次封顶（凭证走磁盘标记文件；explorer 拉起不带参数，命令行传不了凭证）。
        //   ③ 封顶 fail-closed：凭证写不出来就**不重启**，宁可留脏血统也不开无界重启。
        //   ④ [Run] 的 runasoriginaluser 只换令牌、不改环境块，装完自动启动那份仍带
        //      compat=ElevateCreateProcess（2026-11-11 09:38 实测），别指望它挡血统。
        try
        {
            string? compatLayer = Environment.GetEnvironmentVariable("__COMPAT_LAYER");
            using (System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                bool elevated = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                string markerPath = Services.LineageGuard.MarkerPath;
                Services.LineageGuard.Decision decision = Services.LineageGuard.Decide(elevated, compatLayer, markerPath);
                if (decision == Services.LineageGuard.Decision.CappedAlreadyReset)
                {
                    // 判定仍成立但窗口内已重置过：不再自启，按当前血统直接启动（封顶的意义就在这里，
                    // 否则在 explorer 不滤提权令牌的机器上这条链每次判定都成立 → 无限反复启动）。
                    AppLog.WriteInfo($"lineage flags still present after reset (compat={compatLayer ?? "-"}, elevated={elevated}, pid={Environment.ProcessId}), {Services.LineageGuard.MarkerTtlSeconds}s 内已重置过，不再自启，按当前血统直接启动");
                    // 凭证用完即弃：留着它只靠 TTL 过期，会把窗口内下一次"确实带着脏血统"的启动
                    // 也误判成已重置过(= 当初 WMI 必失败的症状回来)。
                    Services.LineageGuard.ClearRelaunchMarker(markerPath);
                }
                else if (decision == Services.LineageGuard.Decision.Relaunch)
                {
                    string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        if (!Services.LineageGuard.TryMarkRelaunchAttempted(markerPath, out string markFailure))
                        {
                            // fail-closed：没有可信凭证就绝不重启。这条链在"判定持续成立"的机器上
                            // 每次启动都会成立，重启一次 = 无限反复启动（2026-11-10 实测过的事故）。
                            AppLog.WriteError("血统封顶凭证写不出来(" + markFailure + ")，marker=" + markerPath
                                + "；放弃切血统重启，按当前血统直接启动(compat=" + (compatLayer ?? "-") + ", elevated=" + elevated + ")");
                        }
                        else
                        {
                            AppLog.WriteInfo($"installer/elevated lineage detected (compat={compatLayer ?? "-"}, elevated={elevated}, pid={Environment.ProcessId}), relaunching clean via explorer.exe: {exePath}");
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                // 必须不带额外参数：实测加了参数 explorer 就不会拉起程序。
                                // 一次性凭证走 LineageGuard 的标记文件，不走参数。
                                Arguments = "\"" + exePath + "\"",
                                UseShellExecute = true
                            });
                            Shutdown();
                            return;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 以前这里是空的 catch：血统判定本身出错时既看不见原因，也看不出"这次到底切没切血统"。
            AppLog.WriteError("血统判定异常(按当前血统直接启动): " + ex.GetType().Name + ": " + ex.Message);
        }

        // 禁用 WPF 硬件加速：AMD 驱动在长期运行 WPF 应用时可能泄漏显存/系统内存，
        // 导致"无法创建新的堆栈防护页面"。启动器界面简单，软件渲染完全够用。
        try { System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly; } catch { }

        // DPI 适配
        try { SetProcessDpiAwareness(2); } catch { }

        // 把改动前已经落盘的凭据抹掉: 旧 run.log 里存着 DSH 启动横幅的 ?token=,
        // 而「复制诊断信息」会把尾部原样带出去。后台跑, 不挡启动。
        System.Threading.Tasks.Task.Run(() => { try { AppLog.ScrubExisting(); } catch { } });

        // 单实例检查（M6）：经 explorer 重启自身时，旧实例可能尚未释放 mutex，
        // 新实例只尝试一次会误判“已在运行”后退出（表现为双击没反应）。
        // 因此最多重试 3 次、每次间隔 300ms，全部失败才提示重复启动。
        const string mutexName = "LlamaDSH_Launcher_SingleInstance_Mutex";
        Mutex? mutex = null;
        bool createdNew = false;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                mutex = new Mutex(true, mutexName, out createdNew);
            }
            catch (AbandonedMutexException)
            {
                // 上一实例被强杀/崩溃而未 ReleaseMutex：内核把遗弃 mutex 的所有权直接交给
                // 本次调用方并抛这里。旧进程已不存在，接管是安全的；不接住会让启动器
                // 在崩溃后一次都打不开（窗口不出，异常发生在下面的全局兜底注册之前）。
                createdNew = true;
                mutex = new Mutex(false, mutexName, out _); // 重新拿一个句柄，所有权仍在本线程
                AppLog.WriteInfo($"单实例 mutex 从已退出的旧实例接管（第 {attempt} 次尝试）");
                break;
            }
            catch (Exception ex)
            {
                // mutex 本身建不起来（安全描述符/权限异常）时不该阻止启动：
                // 放弃单实例保护并留痕，比双击没反应且无痕要好。
                AppLog.WriteInfo($"单实例 mutex 创建失败，跳过单实例检查: {ex.Message}");
                mutex = null;
                createdNew = true;
                break;
            }
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
