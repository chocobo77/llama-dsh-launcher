using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LlamaDSH_Launcher.Services;

/// <summary>
/// 本地内核补丁守卫。
///
/// 背景：运行中的 DSH 加载的是 checkout 的 lib 产物，而该 checkout 里有两处只存在于本机的
/// 内核修复（text-tool-call 文本形态工具调用兜底、tool-fs-search 路径预检）。更新路径会
/// 抹掉它们——pull 前 stash、pop 冲突时只留一行日志，随后照样重建，运行实例就静默失去
/// 防线（2026-11-16/17 事故即此类，见 checkout 的 LOCAL-CHANGES.txt）。
///
/// 所以更新流程必须：拉取后重放补丁并断言（失败即中止更新），重建后断言产物（失败即不
/// 启动 DSH）。断言与重放都复用 checkout / 规范副本里既有的 node 脚本，本类只做定位、
/// 执行与结果解释。
/// </summary>
public static class LocalPatchGuard
{
	/// <summary>规范副本目录（默认依次探测 checkout 内 / 同级 / exe 祖先 / 文档登记的目录）。</summary>
	public const string PatchesDirEnv = "DSH_LOCAL_PATCHES_DIR";

	/// <summary>非空 = 无本地补丁的部署：跳过全部补丁阶段（日志记明）。</summary>
	public const string SkipEnv = "DSH_SKIP_LOCAL_PATCHES";

	private const string RestoreScript = "restore-local-patches.mjs";
	private const string VerifyScriptRelative = "scripts/verify-local-changes.mjs";

	/// <summary>UPDATE-RULE-REQUEST.md 登记的规范副本位置。</summary>
	private const string DocumentedPatchesDir = @"D:\code\DSH\harness-patches";

	/// <summary>一个阶段的结论：Ok + 给用户看的一句话 + 脚本自己的判定行。</summary>
	public sealed class PatchResult
	{
		public bool Ok;
		public string Detail = "";
		public string Verdict = "";

		public override string ToString()
		{
			return Ok ? Detail : (Detail + (Verdict.Length == 0 ? "" : "\n" + Verdict));
		}
	}

	public static bool Skipped => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SkipEnv));

	public static bool HasVerifier(string dshDir)
	{
		return !string.IsNullOrEmpty(dshDir) && File.Exists(Path.Combine(dshDir, VerifyScriptRelative));
	}

	/// <summary>按顺序给出候选规范副本目录；显式环境变量不在此列（见 ResolvePatchesDir）。</summary>
	public static List<string> Candidates(string dshDir)
	{
		List<string> list = new List<string>();
		if (!string.IsNullOrEmpty(dshDir))
		{
			list.Add(Path.Combine(dshDir, "harness-patches"));
			string? parent = Directory.GetParent(dshDir)?.FullName;
			if (!string.IsNullOrEmpty(parent))
			{
				list.Add(Path.Combine(parent, "harness-patches"));
			}
		}
		// 开发布局：<DSH>\apps\desktop-tauri\... 或启动器 checkout 的 bin 目录向上六层。
		try
		{
			string? exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
			for (int i = 0; i < 6 && !string.IsNullOrEmpty(exeDir); i++)
			{
				list.Add(Path.Combine(exeDir, "harness-patches"));
				exeDir = Path.GetDirectoryName(exeDir);
			}
		}
		catch
		{
		}
		list.Add(DocumentedPatchesDir);
		return list;
	}

	/// <summary>
	/// 规范副本目录。显式设置了 <see cref="PatchesDirEnv"/> 就只认它——指错了要响亮地失败，
	/// 而不是偷偷回退到别的副本继续重放。
	/// </summary>
	public static string? ResolvePatchesDir(string dshDir)
	{
		string explicitDir = (Environment.GetEnvironmentVariable(PatchesDirEnv) ?? "").Trim();
		if (explicitDir.Length > 0)
		{
			return File.Exists(Path.Combine(explicitDir, RestoreScript)) ? explicitDir : null;
		}
		return Candidates(dshDir).FirstOrDefault(delegate (string dir)
		{
			return File.Exists(Path.Combine(dir, RestoreScript));
		});
	}

	/// <summary>阶段 0 / 阶段 4：跑 checkout 自己的断言脚本。buildStage=true 查产物接线。</summary>
	public static PatchResult Verify(string dshDir, bool buildStage)
	{
		PatchResult result = new PatchResult();
		if (string.IsNullOrEmpty(dshDir))
		{
			result.Detail = "DSH 目录为空";
			return result;
		}
		string script = Path.Combine(dshDir, VerifyScriptRelative);
		if (!File.Exists(script))
		{
			result.Detail = "缺少校验脚本 " + script;
			return result;
		}
		string? node = Toolchain.ResolveNode24Exe() ?? Toolchain.ResolveNodeExe(require24: false);
		if (node == null)
		{
			result.Detail = "未找到 node.exe：无法校验本地补丁";
			return result;
		}
		List<string> args = new List<string> { script };
		if (buildStage)
		{
			args.Add("--stage");
			args.Add("build");
		}
		ProcessResult run = ProcessRunner.Run(node, args, 180000, dshDir, delegate (System.Diagnostics.ProcessStartInfo psi)
		{
			psi.StandardOutputEncoding = Encoding.UTF8;
			psi.StandardErrorEncoding = Encoding.UTF8;
		});
		result.Ok = run.Success;
		result.Verdict = VerdictLines(run.Combined);
		result.Detail = run.TimedOut
			? "校验脚本超时（180s）"
			: (run.Started ? "校验脚本退出码 " + run.ExitCode : run.StdErr);
		return result;
	}

	/// <summary>阶段 2：把规范副本重放回 checkout（脚本本身幂等）。</summary>
	public static PatchResult Restore(string dshDir, string patchesDir)
	{
		PatchResult result = new PatchResult();
		string script = Path.Combine(patchesDir, RestoreScript);
		string? node = Toolchain.ResolveNode24Exe() ?? Toolchain.ResolveNodeExe(require24: false);
		if (node == null)
		{
			result.Detail = "未找到 node.exe：无法重放本地补丁";
			return result;
		}
		ProcessResult run = ProcessRunner.Run(node, new List<string> { script, dshDir }, 300000, dshDir, delegate (System.Diagnostics.ProcessStartInfo psi)
		{
			psi.StandardOutputEncoding = Encoding.UTF8;
			psi.StandardErrorEncoding = Encoding.UTF8;
		});
		result.Ok = run.Success;
		result.Verdict = VerdictLines(run.Combined);
		result.Detail = run.Started ? "重放脚本退出码 " + run.ExitCode : run.StdErr;
		return result;
	}

	/// <summary>
	/// 拉取之后调用：断言源码仍带补丁，缺了就重放，再断言一次。任一环节不成立即返回
	/// Ok=false——调用方必须据此中止更新（不重建、不启动 DSH）。
	/// </summary>
	public static PatchResult EnsureAfterUpdate(string dshDir)
	{
		PatchResult result = new PatchResult();
		if (Skipped)
		{
			result.Ok = true;
			result.Detail = "已设置 " + SkipEnv + "：本次更新跳过本地补丁阶段";
			return result;
		}
		bool hasVerifier = HasVerifier(dshDir);
		string? patchesDir = ResolvePatchesDir(dshDir);
		if (!hasVerifier && patchesDir == null)
		{
			result.Ok = true;
			result.Detail = "该 checkout 既没有补丁断言，也没有规范副本（" + PatchesDirEnv + " 未设置）：跳过本地补丁阶段";
			return result;
		}
		if (hasVerifier)
		{
			PatchResult first = Verify(dshDir, buildStage: false);
			if (first.Ok)
			{
				result.Ok = true;
				result.Detail = "本地内核补丁已就位（source 断言通过）";
				return result;
			}
			result.Verdict = first.Verdict;
		}
		if (patchesDir == null)
		{
			result.Detail = "本地补丁缺失，且找不到 " + RestoreScript + "（查过："
				+ string.Join("、", Candidates(dshDir)) + "）。可设 " + PatchesDirEnv + " 指向规范副本目录。";
			return result;
		}
		PatchResult restore = Restore(dshDir, patchesDir);
		if (!restore.Ok)
		{
			result.Detail = "本地补丁重放失败（官方代码结构变化导致锚点失配时需人工适配）：\n"
				+ restore.Detail + "\n规范副本: " + patchesDir;
			result.Verdict = restore.Verdict;
			return result;
		}
		if (!hasVerifier)
		{
			result.Ok = true;
			result.Detail = "补丁已重放（该 checkout 没有断言脚本，无法复核）";
			return result;
		}
		PatchResult again = Verify(dshDir, buildStage: false);
		result.Ok = again.Ok;
		result.Verdict = again.Verdict;
		result.Detail = again.Ok
			? "补丁原本缺失，已从规范副本重放并通过 source 断言：" + patchesDir + "（若你手改过 checkout 里的补丁，现在以规范副本为准——确认规范副本是最新版本）"
			: "补丁重放后 source 断言仍未通过（请确认规范副本是最新版本）";
		return result;
	}

	/// <summary>只留脚本自己的判定行，别把整屏进度写进日志/弹窗。</summary>
	private static string VerdictLines(string output)
	{
		if (string.IsNullOrEmpty(output))
		{
			return "";
		}
		List<string> kept = new List<string>();
		foreach (string raw in output.Replace("\r\n", "\n").Split('\n'))
		{
			string line = raw.Trim();
			if (line.Length == 0)
			{
				continue;
			}
			if (line.StartsWith("FAIL", StringComparison.Ordinal)
				|| line.StartsWith("MISSING", StringComparison.Ordinal)
				|| line.StartsWith("ADVISORY", StringComparison.Ordinal)
				|| line.Contains("missing —", StringComparison.Ordinal)
				|| line.Contains("未应用", StringComparison.Ordinal))
			{
				kept.Add(line);
			}
		}
		if (kept.Count == 0)
		{
			string[] all = output.Replace("\r\n", "\n").Split('\n');
			for (int i = all.Length - 1; i >= 0; i--)
			{
				if (all[i].Trim().Length > 0)
				{
					kept.Add(all[i].Trim());
					break;
				}
			}
		}
		if (kept.Count > 8)
		{
			kept = kept.GetRange(0, 8);
		}
		return string.Join("\n", kept);
	}
}
