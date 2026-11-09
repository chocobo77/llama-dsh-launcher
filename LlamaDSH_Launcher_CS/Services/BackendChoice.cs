using System;

namespace LlamaDSH_Launcher
{
	/// <summary>
	/// llama.cpp 预编译后端的选择口径，与 Linux 侧 toolchain.resolve_backend / pick_assets 对齐。
	///
	/// 方案字段 LlamaBackend 两端同名同义："" = 按显卡自动；可显式 vulkan / rocm / cuda / sycl。
	/// 它只决定「下载或检查更新时取哪个预编译包」，不改命令行参数，也不改正在跑的进程。
	/// 之所以要能显式指定：AMD RDNA3 上 ROCm 构建通常比 Vulkan 快，但按厂商自动只会给 Vulkan；
	/// NVIDIA 机器想改用 Vulkan 逃避 cudart 缺库也同理。
	/// </summary>
	public static class BackendChoice
	{
		/// <summary>可显式指定的后端（顺序即界面上的顺序）。</summary>
		public static readonly string[] Known = { "vulkan", "rocm", "cuda", "sycl" };

		/// <summary>规范化选择：去空白、转小写；不认识的值按「自动」处理（返回 ""）。</summary>
		public static string Normalize(string choice)
		{
			if (string.IsNullOrWhiteSpace(choice))
			{
				return "";
			}
			string c = choice.Trim().ToLowerInvariant();
			for (int i = 0; i < Known.Length; i++)
			{
				if (c == Known[i])
				{
					return Known[i];
				}
			}
			return "";
		}

		/// <summary>生效后端 = 显式选择优先，否则用自动探测结果；两者都拿不到时落到 vulkan。</summary>
		public static string Resolve(string choice, string autoPreference)
		{
			string c = Normalize(choice);
			if (c.Length > 0)
			{
				return c;
			}
			if (string.IsNullOrWhiteSpace(autoPreference))
			{
				return "vulkan";
			}
			return autoPreference.Trim().ToLowerInvariant();
		}

		/// <summary>
		/// GitHub Release 资产名的挑选顺序（逐个按 token 匹配，命中即止）。
		/// ROCm 资产名带 ROCm 版本（如 llama-b12212-bin-win-rocm-10.0-x64.zip），所以 token 用 "rocm-"；
		/// CPU 资产在近一年改名为 …-bin-win-cpu-x64，老名 avx2-x64 留在链尾兜底旧仓库。
		/// 与 Linux 侧 toolchain.pick_assets 的链同口径（那边还多一层 sycl-fp16/fp32）。
		/// </summary>
		public static string[] FallbackTokens(string backend)
		{
			switch (Normalize(backend))
			{
				case "rocm":
					return new string[] { "rocm-", "vulkan-x64", "cpu-x64", "avx2-x64" };
				case "cuda":
					return new string[] { "cuda-", "vulkan-x64", "cpu-x64", "avx2-x64" };
				case "sycl":
					return new string[] { "sycl-fp16-x64", "sycl-x64", "vulkan-x64", "cpu-x64", "avx2-x64" };
				default:
					return new string[] { "vulkan-x64", "rocm-", "cpu-x64", "avx2-x64" };
			}
		}

		/// <summary>界面上那行「生效: xxx (手动/自动检测)」说明。</summary>
		public static string Hint(string choice, string autoPreference)
		{
			string c = Normalize(choice);
			if (c.Length > 0)
			{
				return "生效: " + c + "（手动）";
			}
			return "生效: " + Resolve("", autoPreference) + "（自动检测）";
		}
	}
}
