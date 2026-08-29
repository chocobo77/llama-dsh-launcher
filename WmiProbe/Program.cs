using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Management;
using System.Threading;
using System.Collections;

class Probe
{
    const string DD = "%DSH%";
    static StreamWriter logw = null!;

    static void Log(string m)
    {
        var l = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + m;
        Console.WriteLine(l);
        logw.WriteLine(l);
        logw.Flush();
    }

    static bool PortBound()
    {
        for (int i = 0; i < 100; i++)
        {
            Thread.Sleep(100);
            try
            {
                var props = IPGlobalProperties.GetIPGlobalProperties();
                foreach (var ep in props.GetActiveTcpListeners())
                    if (ep.Port == 3080) return true;
            }
            catch { }
        }
        return false;
    }

    static void KillTree(int pid)
    {
        if (pid <= 0) return;
        try { Process.Start(new ProcessStartInfo("taskkill", "/PID " + pid + " /T /F") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit(); } catch { }
        Thread.Sleep(800);
        for (int i = 0; i < 20; i++)
        {
            if (!PortBoundQuick()) return;
            try
            {
                var props = IPGlobalProperties.GetIPGlobalProperties();
                foreach (var ep in props.GetActiveTcpListeners())
                    if (ep.Port == 3080)
                    {
                        int owner = PidByPort();
                        if (owner > 0) { try { Process.Start(new ProcessStartInfo("taskkill", "/PID " + owner + " /T /F") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit(); } catch { } }
                    }
            }
            catch { }
            Thread.Sleep(500);
        }
    }

    static bool PortBoundQuick()
    {
        try
        {
            foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                if (ep.Port == 3080) return true;
        }
        catch { }
        return false;
    }

    static int PidByPort()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell", "-NoProfile -Command \"(Get-NetTCPConnection -LocalPort 3080 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            int v; int.TryParse(o, out v); return v;
        }
        catch { return 0; }
    }

    static string[] BuildEnv()
    {
        var list = new System.Collections.Generic.List<string>();
        var vars = Environment.GetEnvironmentVariables();
        foreach (DictionaryEntry e in vars)
        {
            if (e.Key == null || e.Value == null) continue;
            if (string.Equals((string)e.Key, "__COMPAT_LAYER", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(e.Key + "=" + e.Value);
        }
        return list.ToArray();
    }

    // 启动器逐字节的 WMI 调用实现
    static int WmiCreate(string cmdLine, string cwd, string[]? env)
    {
        using (var mc = new ManagementClass("Win32_Process"))
        {
            var inParams = mc.GetMethodParameters("Create");
            inParams["CommandLine"] = cmdLine;
            if (cwd != null) inParams["CurrentDirectory"] = cwd;
            if (env != null)
            {
                using (var suClass = new ManagementClass("Win32_ProcessStartup"))
                {
                    var su = suClass.CreateInstance();
                    su["EnvironmentVariables"] = env;
                    inParams["ProcessStartupInformation"] = su;
                }
            }
            var outParams = mc.InvokeMethod("Create", inParams, null);
            int rc = Convert.ToInt32(outParams["ReturnValue"]);
            if (rc != 0) return -rc;
            return Convert.ToInt32(outParams["ProcessId"]);
        }
    }

    static void Main(string[] args)
    {
        var diagDir = "D:\\code\\DSH\\dsh_diag";
        Directory.CreateDirectory(diagDir);
        string logPath = Path.Combine(diagDir, "wmi_probe_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
        logw = new StreamWriter(logPath, false) { AutoFlush = true };
        Log("=== launcher-exact WMI calling-code isolation probe ===");

        var c = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        bool occupied = false;
        foreach (var ep in c) if (ep.Port == 3080) occupied = true;
        if (occupied) { Log("ABORT: 3080 occupied. Stop DSH first."); Console.WriteLine("ABORT: stop DSH first, then rerun."); logw.Dispose(); return; }
        Log("precondition OK: 3080 free");

        string bootLog = Path.Combine(Path.GetTempPath(), "LlamaDSH_dsh_boot.log");
        string probeMjs = Path.Combine(DD, "apps", "cli", "lib", "__resolve_test.mjs");
        System.IO.File.WriteAllText(probeMjs, "import('@deepseek-ai/dsh-app-boot').then(() => { console.log('RESOLVE-OK'); process.exit(0); }).catch((e) => { console.error('RESOLVE-FAIL: ' + e.message); process.exit(1); });");
        string[] env = BuildEnv();
        Log("explicit env entries=" + env.Length);

        // ---- V1: launcher-exact (prelude + bin.js web + explicit env + cwd) ----
        RunVariant("V1_launcher_exact", vlog =>
        {
            string cmd = "cmd.exe /c cd /d \"" + DD + "\" & echo ===CHILD-ENV=== >> \"" + vlog + "\" 2>&1 & set >> \"" + vlog + "\" 2>&1 & where node >> \"" + vlog + "\" 2>&1 & node -v >> \"" + vlog + "\" 2>&1 & echo ===BOOT=== >> \"" + vlog + "\" 2>&1 & node apps/cli/lib/bin.js web >> \"" + vlog + "\" 2>&1";
            return WmiCreate(cmd, DD, env);
        }, true);

        // ---- V2: same but WITHOUT explicit env ----
        RunVariant("V2_no_explicit_env", vlog =>
        {
            string cmd = "cmd.exe /c cd /d \"" + DD + "\" & echo ===BOOT=== >> \"" + vlog + "\" 2>&1 & node apps/cli/lib/bin.js web >> \"" + vlog + "\" 2>&1";
            return WmiCreate(cmd, DD, null);
        }, true);

        // ---- V3: same but WITHOUT CurrentDirectory ----
        RunVariant("V3_no_cwd", vlog =>
        {
            string cmd = "cmd.exe /c cd /d \"" + DD + "\" & echo ===BOOT=== >> \"" + vlog + "\" 2>&1 & node apps/cli/lib/bin.js web >> \"" + vlog + "\" 2>&1";
            return WmiCreate(cmd, null, env);
        }, true);

        // ---- V4: short cmd form + explicit env + cwd ----
        RunVariant("V4_short_cmd", vlog =>
        {
            string cmd = "cmd.exe /c \"cd /d \"" + DD + "\" && node apps/cli/lib/bin.js web >> \"" + vlog + "\" 2>&1\"";
            return WmiCreate(cmd, DD, env);
        }, true);

        // ---- V5: minimal (short cmd, no env, no cwd) ----
        RunVariant("V5_minimal", vlog =>
        {
            string cmd = "cmd.exe /c \"cd /d \"" + DD + "\" && node apps/cli/lib/bin.js web >> \"" + vlog + "\" 2>&1\"";
            return WmiCreate(cmd, null, null);
        }, true);

        // ---- V6: control probe (import only, via launcher-exact style) ----
        RunVariant("V6_probe_control", vlog =>
        {
            string cmd = "cmd.exe /c cd /d \"" + DD + "\" & node apps\\cli\\lib\\__resolve_test.mjs >> \"" + vlog + "\" 2>&1";
            return WmiCreate(cmd, DD, env);
        }, false);

        Log("=== done. log: " + logPath + " ===");
        Console.WriteLine();
        Console.WriteLine("Done. Send this file to the assistant: " + logPath);
        logw.Dispose();
    }

    static void RunVariant(string name, Func<string, int> spawn, bool expectPort)
    {
        Log("---- [" + name + "] ----");
        string vlog = Path.Combine(Path.GetTempPath(), "wmi_probe_" + name + ".out");
        try { File.Delete(vlog); } catch { }
        int pid = 0;
        try { pid = spawn(vlog); } catch (Exception ex) { Log("  spawn exception: " + ex.Message); }
        Log("  childPid=" + pid);
        bool bound = expectPort ? PortBound() : false;
        if (!expectPort)
        {
            Thread.Sleep(2500);
        }
        if (expectPort)
        {
            Log("  RESULT: " + (bound ? "SUCCESS - 3080 BOUND" : "FAILED - not bound in 10s"));
        }
        else
        {
            string o = File.Exists(vlog) ? File.ReadAllText(vlog) : "(no output)";
            Log("  RESULT: " + (o.Contains("RESOLVE-OK") ? "SUCCESS - RESOLVE-OK" : (o.Contains("RESOLVE-FAIL") ? "FAILED - " + o.Trim() : "UNKNOWN - " + o.Trim())));
        }
        if (File.Exists(vlog))
        {
            var lines = File.ReadAllLines(vlog);
            int take = Math.Min(lines.Length, 12);
            for (int i = 0; i < take; i++) Log("  | " + lines[i]);
            if (lines.Length > take) Log("  | ...(" + (lines.Length - take) + " more lines)");
        }
        else Log("  | (no output file)");
        KillTree(pid);
        Log("  cleaned. 3080 free now: " + !PortBoundQuick());
        Thread.Sleep(1000);
    }
}
