using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using LlamaDSH_Launcher.Services;


namespace LauncherSmokeTests;

/// <summary>
/// 极简断言框架：不依赖 xUnit/NUnit，避免离线环境还原失败。
/// 输出每个用例的结果，退出码 = 失败数（0 表示全绿）。
/// </summary>
internal static class Program
{
    private static int _failed;
    private static int _passed;

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("  PASS  " + name);
        }
        else
        {
            _failed++;
            Console.WriteLine("  FAIL  " + name + (detail == null ? "" : "   [" + detail + "]"));
        }
    }

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        TestBasicRun();
        TestExitCode();
        TestStderrSeparation();
        TestSpacesInArguments();
        TestMissingExecutable();
        TestTimeout();
        TestLargeOutput();
        TestLogRotation();
        TestAuthenticode();
        TestDiagnostics();

        Console.WriteLine();
        Console.WriteLine($"总计: {_passed + _failed}  通过: {_passed}  失败: {_failed}");
        return _failed;
    }

    private static void TestBasicRun()
    {
        Console.WriteLine("[ProcessRunner] 基本执行");
        ProcessResult r = ProcessRunner.Run("cmd.exe", new[] { "/c", "echo hello" }, 10000);
        Check("Started=true", r.Started);
        Check("ExitCode=0", r.ExitCode == 0, "ExitCode=" + r.ExitCode);
        Check("stdout 含 hello", r.StdOut.Contains("hello"), r.StdOut.Trim());
        Check("未超时", !r.TimedOut);
        Check("Success=true", r.Success);
    }

    private static void TestExitCode()
    {
        Console.WriteLine("[ProcessRunner] 非零退出码");
        ProcessResult r = ProcessRunner.Run("cmd.exe", new[] { "/c", "exit", "3" }, 10000);
        Check("ExitCode=3", r.ExitCode == 3, "ExitCode=" + r.ExitCode);
        Check("Success=false", !r.Success);
        Check("Started=true", r.Started);
    }

    private static void TestStderrSeparation()
    {
        Console.WriteLine("[ProcessRunner] stdout / stderr 分离");
        ProcessResult r = ProcessRunner.Run("cmd.exe", new[] { "/c", "echo out& echo err 1>&2" }, 10000);
        Check("stdout 有 out", r.StdOut.Contains("out"), r.StdOut.Trim());
        Check("stderr 有 err", r.StdErr.Contains("err"), r.StdErr.Trim());
        Check("Combined 两者都有", r.Combined.Contains("out") && r.Combined.Contains("err"));
    }

    private static void TestSpacesInArguments()
    {
        Console.WriteLine("[ProcessRunner] 含空格参数不被拆开");
        ProcessResult r = ProcessRunner.Run("cmd.exe", new[] { "/c", "echo", "a b" }, 10000);
        Check("stdout 含 \"a b\"", r.StdOut.Contains("a b"), r.StdOut.Trim());
    }

    private static void TestMissingExecutable()
    {
        Console.WriteLine("[ProcessRunner] 可执行文件不存在");
        ProcessResult r = ProcessRunner.Run("definitely-not-a-real-exe-xyz.exe", null, 5000);
        Check("Started=false", !r.Started);
        Check("不抛异常且 StdErr 有信息", !string.IsNullOrWhiteSpace(r.StdErr));
        Check("ExitCode=-1", r.ExitCode == -1, "ExitCode=" + r.ExitCode);
    }

    private static void TestTimeout()
    {
        Console.WriteLine("[ProcessRunner] 超时强杀");
        Stopwatch sw = Stopwatch.StartNew();
        ProcessResult r = ProcessRunner.Run("cmd.exe", new[] { "/c", "ping", "-n", "20", "127.0.0.1" }, 800);
        sw.Stop();
        Check("TimedOut=true", r.TimedOut);
        Check("ExitCode=-1", r.ExitCode == -1, "ExitCode=" + r.ExitCode);
        Check("在 15 秒内返回（未挂死）", sw.ElapsedMilliseconds < 15000, sw.ElapsedMilliseconds + "ms");
    }

    private static void TestLargeOutput()
    {
        Console.WriteLine("[ProcessRunner] 大输出不被截断（R5 回归）");
        // 3000 行、每行约 30 字节 ≈ 90KB，超过旧实现 3 秒宽限容易丢失的量级
        ProcessResult r = ProcessRunner.Run(
            "cmd.exe",
            new[] { "/c", "for /L %i in (1,1,3000) do @echo LINE-%i-XXXXXXXXXXXXXXXXXXXX" },
            60000);
        int lines = r.StdOut.Split('\n').Count(s => s.Contains("LINE-"));
        Check("抓到 3000 行", lines == 3000, "实际 " + lines + " 行");
        Check("最后一行也在", r.StdOut.Contains("LINE-3000-"), "尾部=" + Tail(r.StdOut, 60));
    }

    private static void TestLogRotation()
    {
        Console.WriteLine("[AppLog] 写入与轮转");
        string dir = LlamaDSH_Launcher.AppLog.LogDir;
        string file = Path.Combine(dir, "info.log");
        long before = File.Exists(file) ? new FileInfo(file).Length : -1;
        LlamaDSH_Launcher.AppLog.WriteInfo("smoke-test-marker");
        long after = File.Exists(file) ? new FileInfo(file).Length : -1;
        Check("日志可写且长度增加", after > before, $"{before} -> {after}");

        // 轮转：把文件撑到 2MB 以上再写一条，应生成 .1
        string big = Path.Combine(dir, "run.log");
        string rolled = big + ".1";
        try
        {
            if (File.Exists(rolled)) File.Delete(rolled);
            File.WriteAllText(big, new string('x', 2 * 1024 * 1024 + 1024));
            LlamaDSH_Launcher.AppLog.WriteRun("trigger-roll");
            Check("超过 2MB 触发轮转为 .1", File.Exists(rolled));
            Check("轮转后原文件变小", new FileInfo(big).Length < 2 * 1024 * 1024);
        }
        finally
        {
            try { if (File.Exists(rolled)) File.Delete(rolled); } catch { }
        }
    }

    private static void TestDiagnostics()
    {
        Console.WriteLine("[AppLog] 诊断文本");
        string text = LlamaDSH_Launcher.AppLog.BuildDiagnostics("这是一条测试错误：node 未安装", 20);
        Check("含标题", text.Contains("诊断信息"));
        Check("含上下文", text.Contains("这是一条测试错误"));
        Check("含环境信息", text.Contains("系统:") && text.Contains("运行时:") && text.Contains("程序版本:"));
        Check("含日志目录", text.Contains("日志目录:"));
        Check("长度合理", text.Length > 100, "len=" + text.Length);
    }

    private static void TestAuthenticode()
    {
        Console.WriteLine("[AuthenticodeVerifier] 真实验签");

        // 1) 带【嵌入】签名的程序：应判定有效，且主体含 Microsoft
        //    注意：System32 下很多文件是 catalog 签名（如 notepad.exe），本验签器只认嵌入签名，
        //    用它们做用例会得到“没有数字签名”，属预期行为，不是缺陷。
        string signed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet",
            "dotnet.exe");
        if (File.Exists(signed))
        {
            AuthenticodeVerifier.Result r = AuthenticodeVerifier.Verify(signed);
            Check("dotnet.exe 验签通过", r.IsValid, "HRESULT=0x" + r.HResult.ToString("X8") + " " + r.Message);
            Check("主体含 Microsoft", r.Subject.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0, r.Subject);
        }
        else
        {
            Console.WriteLine("  SKIP  dotnet.exe 不存在（跳过嵌入签名用例）");
        }

        // 1b) 与安装器白名单对齐：真实 OpenJS 签名产物必须能通过
        string nodeExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        if (File.Exists(nodeExe))
        {
            AuthenticodeVerifier.Result rn = AuthenticodeVerifier.Verify(nodeExe);
            Check("node.exe 验签通过", rn.IsValid, "HRESULT=0x" + rn.HResult.ToString("X8") + " " + rn.Message);
            Check("主体含 OpenJS Foundation（安装器白名单接受）",
                rn.Subject.IndexOf("OpenJS Foundation", StringComparison.OrdinalIgnoreCase) >= 0, rn.Subject);
        }
        else
        {
            Console.WriteLine("  SKIP  node.exe 不存在");
        }

        // 2) 未签名的伪造 exe：应拒绝，且失败原因为“没有数字签名”
        string fake = Path.Combine(Path.GetTempPath(), "smoke-unsigned-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllText(fake, "MZ not really a signed executable");
            AuthenticodeVerifier.Result r = AuthenticodeVerifier.Verify(fake);
            Check("未签名文件被拒绝", !r.IsValid);
            // 非 PE 文件可能返回 NOSIGNATURE(0x800B0100) 或 SUBJECT_FORM_UNKNOWN(0x800B0003)，两者都算“没有可用签名”
            bool noSig = r.HResult == unchecked((int)0x800B0100) || r.HResult == unchecked((int)0x800B0003);
            Check("原因指向未签名", noSig, "HRESULT=0x" + r.HResult.ToString("X8") + " " + r.Message);
        }
        finally
        {
            try { if (File.Exists(fake)) File.Delete(fake); } catch { }
        }

        // 3) 篡改已签名的文件：必须失败（只改中间一个字节）
        if (File.Exists(signed))
        {
            string tampered = Path.Combine(Path.GetTempPath(), "smoke-tampered-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                File.Copy(signed, tampered, true);
                byte[] bytes = File.ReadAllBytes(tampered);
                int offset = Math.Min(0x400, bytes.Length - 1);
                bytes[offset] ^= 0xFF;
                File.WriteAllBytes(tampered, bytes);
                AuthenticodeVerifier.Result r = AuthenticodeVerifier.Verify(tampered);
                Check("篡改后的文件被拒绝", !r.IsValid, "HRESULT=0x" + r.HResult.ToString("X8") + " " + r.Message);
            }
            finally
            {
                try { if (File.Exists(tampered)) File.Delete(tampered); } catch { }
            }
        }

        // 4) 不存在的文件：不得抛异常
        AuthenticodeVerifier.Result missing = AuthenticodeVerifier.Verify(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".msi"));
        Check("不存在的文件不抛异常且判失败", !missing.IsValid);
    }

    private static string Tail(string s, int n)
    {
        s = s.TrimEnd();
        return s.Length <= n ? s : s.Substring(s.Length - n);
    }
}
