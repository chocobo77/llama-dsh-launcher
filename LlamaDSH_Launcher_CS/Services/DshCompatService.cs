using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace LlamaDSH_Launcher.Services;

public enum DshCompatLevel
{
        Info,
        Warning,
        Error
}

/// <summary>一条兼容性检查结论（供 UI 汇总展示）。</summary>
public sealed class DshCompatItem
{
        public string Category = "";
        public DshCompatLevel Level = DshCompatLevel.Info;
        public string Title = "";
        public string Detail = "";
        /// <summary>是否可自动修复。</summary>
        public bool Repairable;
        /// <summary>修复动作说明。</summary>
        public string Repair = "";

        public override string ToString()
        {
                string tag = Level switch
                {
                        DshCompatLevel.Error => "[错误]",
                        DshCompatLevel.Warning => "[警告]",
                        _ => "[信息]"
                };
                return tag + " " + Category + "：" + Title + (string.IsNullOrEmpty(Detail) ? "" : " —— " + Detail);
        }
}

/// <summary>单个插件/bundle 的兼容性画像。</summary>
public sealed class DshPluginInfo
{
        public string Name = "";
        public string Version = "";
        public string Dir = "";
        public bool Installed;
        public bool IsBundle;
        public bool InBundles;
        /// <summary>该包 bundle 声明的 patch 所插入的 loader 行 id。</summary>
        public List<string> OwnedIds = new();
        /// <summary>被用户 patch 层最后一条 disabled:true 关掉。</summary>
        public bool DisabledByPatch;
        /// <summary>出现在 .dsh-market/state.json 的 disabled 列表。</summary>
        public bool DisabledByMarket;
        /// <summary>面向 UI 的状态标签（已启用/已停用/未激活/…）。</summary>
        public string StateLabel = "";
        /// <summary>peer 快照判定结果：true=兼容 / false=不兼容 / null=未判定。</summary>
        public bool? Compatible;
        public bool Exempted;
        /// <summary>该插件在 profile package.json 中的依赖声明（file:/link: 表示本地/源码安装）。</summary>
        public string Spec = "";
        /// <summary>不兼容的 peer（键=包名，值=声明范围）。</summary>
        public Dictionary<string, string> PeerIssues = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>从插件目录无法解析的依赖/peer。</summary>
        public List<string> MissingSpecs = new();
        public List<string> Errors = new();

        public bool Ok => Installed && (Compatible != false) && MissingSpecs.Count == 0 && Errors.Count == 0;
}

/// <summary>cordis.patch.yml 中一条失效/需迁移的补丁条目。</summary>
public sealed class DshPatchIssue
{
        public string Id = "";
        /// <summary>NotFound / Alias / NameMismatch / OrphanDisable</summary>
        public string Kind = "NotFound";
        public string Detail = "";
        public string AliasTarget = "";
        public bool Repairable;
}

/// <summary>node_modules 下一条失效链接（junction/symlink）。</summary>
public sealed class DshLinkIssue
{
        public string LinkPath = "";
        public string PackageName = "";
        public string OldTarget = "";
        public string NewTarget = "";
        /// <summary>relink=重连到当前安装；remove=删除失效链接。</summary>
        public string Action = "remove";
        /// <summary>重连目标来源说明。</summary>
        public string Source = "";
}

public sealed class DshCompatReport
{
        public string DshDir = "";
        public string Profile = "web";
        public string ProfileDir = "";
        public string ProfileManifestPath = "";
        public string PatchFilePath = "";
        public string DshVersion = "";
        public string Head = "";
        public bool DshDirExists;
        public bool GitRepo;
        public bool BinBuilt;
        public bool Node24Ok;
        public bool PnpmOk;
        public bool DumpConfigRan;
        public bool DumpConfigOk;
        public int DumpConfigExitCode = -1;
        public List<string> DumpConfigErrors = new();
        public List<string> DumpBundleWarnings = new();
        public List<DshPluginInfo> Plugins = new();
        public List<DshPatchIssue> PatchIssues = new();
        public List<DshLinkIssue> LinkIssues = new();
        public List<DshCompatItem> Items = new();
        /// <summary>需要写入 dsh.profile.bundles 的 bundle。</summary>
        public List<string> BundlesToAdd = new();
        /// <summary>需要从 dsh.profile.bundles 移除的失效项。</summary>
        public List<string> BundlesToRemove = new();
        public bool ManifestRepairNeeded;
        /// <summary>.dsh-market/state.json 路径与其中持久化的停用列表。</summary>
        public string MarketStatePath = "";
        public List<string> MarketDisabled = new();
        /// <summary>本次修复要重新启用的插件（删除其禁用行并清理市场停用记录）。</summary>
        public List<string> ReenabledPlugins = new();
        /// <summary>peer 与 DSH 版本不兼容、且来自 npm registry（可 pnpm add @latest 升级）的插件名。</summary>
        public List<string> PluginsToUpgrade = new();
        /// <summary>入口文件 inject 里声明了、但当前 DSH 永不解析的服务名（自动删除/改名）。</summary>
        public List<DshInjectFix> InjectFixes = new();
        /// <summary>可通过共享 node_modules junction 补齐的缺失 peer/依赖（键=包名，值=DSH 内实体目录）。</summary>
        public Dictionary<string, string> PeerJunctionFixes = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>属于“被市场/载体误停用”的补丁行 id，修复时整块删除以恢复插件。</summary>
        public List<string> OrphanDisableIds = new();
        public bool MarketStateRepairNeeded;
        public bool ProbeRan;
        public List<string> ProbeErrors = new();

        public int ErrorCount => Items.Count((DshCompatItem i) => i.Level == DshCompatLevel.Error);
        public int WarningCount => Items.Count((DshCompatItem i) => i.Level == DshCompatLevel.Warning);
        public int RepairableCount => Items.Count((DshCompatItem i) => i.Repairable);
        public bool HasRepairable => RepairableCount > 0;

        public void Add(DshCompatLevel level, string category, string title, string detail = "", bool repairable = false, string repair = "")
        {
                Items.Add(new DshCompatItem
                {
                        Level = level,
                        Category = category,
                        Title = title,
                        Detail = detail ?? "",
                        Repairable = repairable,
                        Repair = repair ?? ""
                });
        }

        public DshCompatItem? Find(string category, string title)
        {
                return Items.FirstOrDefault((DshCompatItem i) => i.Category == category && i.Title == title);
        }
}

public sealed class DshCompatRepairResult
{
        public List<string> Messages = new();
        public DshCompatReport Report = new();
        public bool ManifestChanged;
        public int RelinkedLinks;
        public int RemovedLinks;
        public int CommentedPatchBlocks;
        public int MigratedPatchAliases;
        /// <summary>为重新启用插件而删除的纯禁用补丁块数量。</summary>
        public int RemovedDisableBlocks;
        public bool MarketStateChanged;
}


/// <summary>
/// 内置 semver 版本/范围判定（DSH 探针不可用时的离线回退）。
/// 覆盖 npm 常见写法：比较符、^、~、x 通配、|| 并集、连字符区间、
/// 星号与 workspace: 协议；pre-release 按标准 semver 优先级比较。
/// 只用于“近似/兜底”报告，权威判定始终优先使用 DSH 自带的
/// evaluatePluginCompatibility。
/// </summary>
internal static class SemverUtil
{
        private sealed class Ver : IComparable<Ver>
        {
                public int Major;
                public int Minor;
                public int Patch;
                public List<string> Pre = new();
                public bool HasMinor;
                public bool HasPatch;

                public static Ver? Parse(string? text)
                {
                        if (string.IsNullOrWhiteSpace(text))
                        {
                                return null;
                        }
                        Match m = Regex.Match(text.Trim(), @"^[v=\s]*(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z.\-]+))?(?:\+[0-9A-Za-z.\-]+)?\s*$");
                        if (!m.Success)
                        {
                                return null;
                        }
                        Ver v = new Ver
                        {
                                Major = int.Parse(m.Groups[1].Value),
                                HasMinor = m.Groups[2].Success,
                                HasPatch = m.Groups[3].Success
                        };
                        v.Minor = v.HasMinor ? int.Parse(m.Groups[2].Value) : 0;
                        v.Patch = v.HasPatch ? int.Parse(m.Groups[3].Value) : 0;
                        if (m.Groups[4].Success)
                        {
                                v.Pre = m.Groups[4].Value.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
                        }
                        return v;
                }

                public int CompareTo(Ver? other)
                {
                        if (other == null)
                        {
                                return 1;
                        }
                        int c = Major.CompareTo(other.Major);
                        if (c != 0) return c;
                        c = Minor.CompareTo(other.Minor);
                        if (c != 0) return c;
                        c = Patch.CompareTo(other.Patch);
                        if (c != 0) return c;
                        if (Pre.Count == 0 && other.Pre.Count == 0) return 0;
                        if (Pre.Count == 0) return 1;
                        if (other.Pre.Count == 0) return -1;
                        for (int i = 0; i < Math.Max(Pre.Count, other.Pre.Count); i++)
                        {
                                if (i >= Pre.Count) return -1;
                                if (i >= other.Pre.Count) return 1;
                                string a = Pre[i];
                                string b = other.Pre[i];
                                bool an = long.TryParse(a, out long ai);
                                bool bn = long.TryParse(b, out long bi);
                                if (an && bn)
                                {
                                        c = ai.CompareTo(bi);
                                }
                                else if (an)
                                {
                                        c = -1;
                                }
                                else if (bn)
                                {
                                        c = 1;
                                }
                                else
                                {
                                        c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                                }
                                if (c != 0) return c;
                        }
                        return 0;
                }
        }

        public static bool Satisfies(string? version, string? range)
        {
                Ver? v = Ver.Parse(version);
                if (v == null)
                {
                        return false;
                }
                string r = (range ?? "").Trim();
                if (r.Length == 0 || r == "*" || r == "x" || r == "latest")
                {
                        return true;
                }
                if (r.StartsWith("workspace:", StringComparison.OrdinalIgnoreCase))
                {
                        string inner = r.Substring("workspace:".Length).Trim();
                        if (inner.Length == 0 || inner == "*" || inner == "^" || inner == "~")
                        {
                                return true;
                        }
                        r = inner;
                }
                foreach (string alternative in r.Split("||", StringSplitOptions.RemoveEmptyEntries))
                {
                        if (SatisfiesClause(v, alternative.Trim()))
                        {
                                return true;
                        }
                }
                return false;
        }

        private static bool SatisfiesClause(Ver v, string clause)
        {
                if (clause.Length == 0)
                {
                        return true;
                }
                Match hyphen = Regex.Match(clause, @"^(\S+)\s+-\s+(\S+)$");
                if (hyphen.Success)
                {
                        Ver? lo = Ver.Parse(hyphen.Groups[1].Value);
                        Ver? hi = Ver.Parse(hyphen.Groups[2].Value);
                        if (lo == null || hi == null)
                        {
                                return false;
                        }
                        return v.CompareTo(lo) >= 0 && v.CompareTo(UpperOfPartial(hyphen.Groups[2].Value, hi)) < 0;
                }
                foreach (string token in clause.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                        if (!SatisfiesToken(v, token))
                        {
                                return false;
                        }
                }
                return true;
        }

        private static bool SatisfiesToken(Ver v, string token)
        {
                string t = token.Trim();
                if (t.Length == 0 || t == "*" || t == "x" || t == "X")
                {
                        return true;
                }
                if (t.StartsWith("^", StringComparison.Ordinal))
                {
                        Ver? lo = Ver.Parse(t.Substring(1));
                        if (lo == null) return false;
                        Ver hi = CaretUpper(lo);
                        return v.CompareTo(lo) >= 0 && v.CompareTo(hi) < 0;
                }
                if (t.StartsWith("~", StringComparison.Ordinal))
                {
                        Ver? lo = Ver.Parse(t.Substring(1));
                        if (lo == null) return false;
                        Ver hi = TildeUpper(lo);
                        return v.CompareTo(lo) >= 0 && v.CompareTo(hi) < 0;
                }
                Match cmp = Regex.Match(t, @"^(>=|<=|>|<|=)?\s*(.+)$");
                if (!cmp.Success)
                {
                        return false;
                }
                string op = cmp.Groups[1].Value;
                string body = cmp.Groups[2].Value;
                if (body.Contains('*') || body.Equals("x", StringComparison.OrdinalIgnoreCase) || body.Equals("X", StringComparison.Ordinal))
                {
                        return SatisfiesXRange(v, body);
                }
                Ver? ver = Ver.Parse(body);
                if (ver == null)
                {
                        return false;
                }
                if (body.Split('.').Length < 3 && op.Length == 0)
                {
                        return v.CompareTo(ver) >= 0 && v.CompareTo(PartialUpper(body, ver)) < 0;
                }
                int c = v.CompareTo(ver);
                return op switch
                {
                        ">=" => c >= 0,
                        "<=" => c <= 0,
                        ">" => c > 0,
                        "<" => c < 0,
                        "=" => c == 0,
                        _ => c == 0
                };
        }

        private static bool SatisfiesXRange(Ver v, string body)
        {
                string[] parts = body.Split('.');
                Ver? lower = Ver.Parse(Regex.Replace(body, @"[xX*]", "0"));
                if (lower == null)
                {
                        return true;
                }
                Ver upper;
                int defined = parts.Count((string s) => !s.Equals("x", StringComparison.OrdinalIgnoreCase) && s != "*");
                if (defined <= 1)
                {
                        upper = new Ver { Major = lower.Major + 1, HasMinor = true, HasPatch = true };
                }
                else if (defined == 2)
                {
                        upper = new Ver { Major = lower.Major, Minor = lower.Minor + 1, HasMinor = true, HasPatch = true };
                }
                else
                {
                        upper = new Ver { Major = lower.Major, Minor = lower.Minor, Patch = lower.Patch + 1, HasMinor = true, HasPatch = true };
                }
                return v.CompareTo(lower) >= 0 && v.CompareTo(upper) < 0;
        }

        private static Ver CaretUpper(Ver lo)
        {
                if (lo.Major > 0 || !lo.HasMinor)
                {
                        return new Ver { Major = lo.Major + 1, HasMinor = true, HasPatch = true };
                }
                if (lo.Minor > 0 || !lo.HasPatch)
                {
                        return new Ver { Major = lo.Major, Minor = lo.Minor + 1, HasMinor = true, HasPatch = true };
                }
                return new Ver { Major = lo.Major, Minor = lo.Minor, Patch = lo.Patch + 1, HasMinor = true, HasPatch = true };
        }

        private static Ver TildeUpper(Ver lo)
        {
                if (!lo.HasMinor)
                {
                        return new Ver { Major = lo.Major + 1, HasMinor = true, HasPatch = true };
                }
                return new Ver { Major = lo.Major, Minor = lo.Minor + 1, HasMinor = true, HasPatch = true };
        }

        private static Ver PartialUpper(string body, Ver lo)
        {
                int n = body.Split('.').Length;
                if (n <= 1)
                {
                        return new Ver { Major = lo.Major + 1, HasMinor = true, HasPatch = true };
                }
                return new Ver { Major = lo.Major, Minor = lo.Minor + 1, HasMinor = true, HasPatch = true };
        }

        private static Ver UpperOfPartial(string body, Ver hi)
        {
                int n = body.Split('.').Length;
                if (n <= 1)
                {
                        return new Ver { Major = hi.Major + 1, HasMinor = true, HasPatch = true };
                }
                if (n == 2)
                {
                        return new Ver { Major = hi.Major, Minor = hi.Minor + 1, HasMinor = true, HasPatch = true };
                }
                return new Ver { Major = hi.Major, Minor = hi.Minor, Patch = hi.Patch + 1, HasMinor = true, HasPatch = true };
        }
}


/// <summary>
/// DSH 升级后 / 启动前的“全方面兼容性检查与修复”。
/// 检查面：
///  1) 环境与版本（目录/git/node24/pnpm/编译产物/DSH 版本/HEAD）；
///  2) bundle 激活一致性（依赖声明了 dsh.bundle 却没进 dsh.profile.bundles）；
///  3) 插件 peer 兼容性（优先用 DSH 自带 evaluatePluginCompatibility）；
///  4) cordis.patch.yml 补丁目标（--dump-config 的 entry not found 精确定位）；
///  5) profiles/**/node_modules 失效 junction/symlink；
///  6) 从插件目录解析 peer/依赖的能力（可选 peer 除外）。
/// 修复面：补回/清理 bundle、迁移已知改名补丁、注释失效补丁、重连/清理失效链接、
/// pnpm 升级 peer 不兼容的 registry 插件、把 DSH 安装内实体以 junction 补进共享
/// node_modules 以补齐缺失 peer，全部先备份再写入，最后重新审计确认。
/// </summary>
public static class DshCompatService
{
        public const string DefaultProfile = "web";

        /// <summary>已知补丁 id 改名：旧 id → 新 id + 配置键映射。</summary>
        private static readonly Dictionary<string, (string NewId, Dictionary<string, string> Keys)> PatchAliases =
                new Dictionary<string, (string, Dictionary<string, string>)>(StringComparer.OrdinalIgnoreCase)
                {
                        ["code-runtime"] = ("ptc-runtime", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                                ["computeMs"] = "timeoutMs",
                                ["maxWallMs"] = "maxTimeoutMs"
                        }),
                        ["code-runtime-worker-thread"] = ("ptc-runtime", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                                ["computeMs"] = "timeoutMs",
                                ["maxWallMs"] = "maxTimeoutMs"
                        })
                };

        /// <summary>DSH 安装自带的 bundle，不属于 profile 依赖，也不应被清理。</summary>
        private static readonly HashSet<string> InstallationBundles = new(StringComparer.OrdinalIgnoreCase)
        {
                "@deepseek-ai/dsh-base",
                "@deepseek-ai/dsh-web-app",
                "@deepseek-ai/dsh-headless",
                "@deepseek-ai/dsh-acp-app",
                "@deepseek-ai/dsh-sdk-app",
                "@deepseek-ai/dsh-sdk-minimal",
                "@deepseek-ai/dsh-experimental-voice-input-bundle",
                "@deepseek-ai/dsh-experimental-agent-team-profile"
        };

        // ============================== 插件管理（锁定停用 / 启停 / 升级 / 豁免 / 卸载） ==============================

        /// <summary>锁定停用持久化路径：profile → 保持停用的插件名列表。
        /// DSH 网页端插件管理 / 市场插件会把 bundle 写回 dsh.profile.bundles，
        /// 启动器审计发现锁定列表中的包被写回时自动移除并提示。</summary>
        public static string PinnedPluginsPath => Path.Combine(ResolveDshHome(), "pinned_plugins.json");

        public static List<string> ReadPinnedInactive(string profile)
        {
            try
            {
                if (!File.Exists(PinnedPluginsPath))
                {
                    return new List<string>();
                }
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(PinnedPluginsPath, Encoding.UTF8));
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(profile, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
                {
                    return new List<string>();
                }
                List<string> result = new();
                foreach (JsonElement e in arr.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is string s && s.Length > 0)
                    {
                        result.Add(s);
                    }
                }
                return result;
            }
            catch
            {
                return new List<string>();
            }
        }

        public static void PinInactive(string profile, string name)
        {
            Dictionary<string, List<string>> all = new(StringComparer.Ordinal);
            try
            {
                if (File.Exists(PinnedPluginsPath))
                {
                    all = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(PinnedPluginsPath, Encoding.UTF8)) ?? new();
                }
            }
            catch
            {
            }
            List<string> list = all.TryGetValue(profile, out List<string>? v) ? v : new List<string>();
            if (!list.Contains(name, StringComparer.Ordinal))
            {
                list.Add(name);
            }
            all[profile] = list;
            File.WriteAllText(PinnedPluginsPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
        }

        public static void UnpinInactive(string profile, string name)
        {
            try
            {
                if (!File.Exists(PinnedPluginsPath))
                {
                    return;
                }
                Dictionary<string, List<string>>? all = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(PinnedPluginsPath, Encoding.UTF8));
                if (all != null && all.TryGetValue(profile, out List<string>? list))
                {
                    list.RemoveAll((string x) => x == name);
                    all[profile] = list;
                    File.WriteAllText(PinnedPluginsPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
                }
            }
            catch
            {
            }
        }

        /// <summary>启用/停用 bundle：增删 dsh.profile.bundles（先备份）。停用写回锁定列表。</summary>
        public static string SetBundleActive(string profile, string name, bool active, Action<string>? log = null)
        {
            string manifest = Path.Combine(ResolveProfileDir(profile), "package.json");
            if (!File.Exists(manifest))
            {
                return "profile package.json 不存在";
            }
            try
            {
                JsonNode? root = JsonNode.Parse(File.ReadAllText(manifest, Encoding.UTF8));
                JsonArray? bundles = root?["dsh"]?["profile"]?["bundles"] as JsonArray;
                if (bundles == null)
                {
                    return "package.json 中没有 dsh.profile.bundles";
                }
                List<string> names = bundles.Where(n => n?.GetValue<string>() is string s).Select(n => n!.GetValue<string>()).ToList();
                bool changed = false;
                if (active && !names.Contains(name, StringComparer.Ordinal))
                {
                    names.Add(name);
                    changed = true;
                }
                else if (!active)
                {
                    changed = names.RemoveAll((string x) => x == name) > 0;
                }
                if (!changed)
                {
                    return "无需变更";
                }
                string backup = manifest + ".bak-mgr-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(manifest, backup, true);
                JsonArray rebuilt = new();
                foreach (string n in names)
                {
                    rebuilt.Add(n);
                }
                root!["dsh"]!["profile"]!["bundles"] = rebuilt;
                JsonSerializerOptions opt = new() { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
                File.WriteAllText(manifest, root.ToJsonString(opt) + Environment.NewLine, new UTF8Encoding(false));
                if (active)
                {
                    UnpinInactive(profile, name);
                    log?.Invoke("[插件管理] 已激活 bundle: " + name);
                    return "已激活（DSH 重启后生效）";
                }
                PinInactive(profile, name);
                log?.Invoke("[插件管理] 已停用并锁定 bundle: " + name);
                return "已停用并锁定（DSH 重启后生效；被外部写回时启动器会自动移除）";
            }
            catch (Exception ex)
            {
                return "操作失败: " + ex.Message;
            }
        }

        /// <summary>卸载插件：从 bundles 移除 + pnpm remove（需已停止 DSH）。</summary>
        public static string UninstallPlugin(string profile, string name, Action<string>? log = null)
        {
            string msg = SetBundleActive(profile, name, false, log);
            string dir = ResolveProfileDir(profile);
            (int ExitCode, string StdOut, string StdErr)? r = RunCapture("cmd.exe",
                new[] { "/c", "pnpm", "remove", name }, dir, 600);
            if (r == null)
            {
                return msg + "；pnpm remove 启动失败（插件文件仍在）";
            }
            if (r.Value.ExitCode != 0)
            {
                return msg + "；pnpm remove 失败: " + Tail(r.Value.StdErr.Length > 0 ? r.Value.StdErr : r.Value.StdOut, 400);
            }
            log?.Invoke("[插件管理] 已卸载插件包: " + name);
            return msg + "；插件包已卸载";
        }

        /// <summary>升级插件到最新版（需已停止 DSH）。</summary>
        public static string UpgradePlugin(string profile, string name, Action<string>? log = null)
        {
            string dir = ResolveProfileDir(profile);
            log?.Invoke("[插件管理] pnpm add " + name + "@latest ...");
            (int ExitCode, string StdOut, string StdErr)? r = RunCapture("cmd.exe",
                new[] { "/c", "pnpm", "add", name + "@latest" }, dir, 900);
            if (r == null)
            {
                return "pnpm 启动失败";
            }
            if (r.Value.ExitCode != 0)
            {
                return "升级失败: " + Tail(r.Value.StdErr.Length > 0 ? r.Value.StdErr : r.Value.StdOut, 500);
            }
            return "已升级到最新版";
        }

        /// <summary>写入版本豁免（compatibility.json）：name@version → dsh 版本。</summary>
        public static string ExemptPlugin(string profile, string name, string version, string dshVersion)
        {
            string path = Path.Combine(ResolveProfileDir(profile), "compatibility.json");
            Dictionary<string, List<string>> map = new(StringComparer.Ordinal);
            try
            {
                if (File.Exists(path))
                {
                    map = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path, Encoding.UTF8)) ?? new();
                }
            }
            catch
            {
            }
            string key = name + "@" + version;
            List<string> versions = map.TryGetValue(key, out List<string>? v) ? v : new List<string>();
            if (!versions.Contains(dshVersion, StringComparer.Ordinal))
            {
                versions.Add(dshVersion);
            }
            map[key] = versions;
            File.WriteAllText(path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
            return "已豁免 " + key + " @ dsh " + dshVersion;
        }

        // ============================== 基础路径/工具 ==============================

        public static string ResolveDshHome()
        {
                string? env = Environment.GetEnvironmentVariable("DSH_HOME");
                if (!string.IsNullOrWhiteSpace(env))
                {
                        return env.Trim().Trim('"');
                }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }

        public static string ResolveProfileDir(string profile)
        {
                string name = string.IsNullOrWhiteSpace(profile) ? DefaultProfile : profile.Trim();
                return Path.Combine(ResolveDshHome(), "profiles", name);
        }

        public static string? GetDshVersion(string dshDir)
        {
                foreach (string candidate in new[]
                {
                        Path.Combine(dshDir, "apps", "cli", "package.json"),
                        Path.Combine(dshDir, "package.json")
                })
                {
                        try
                        {
                                if (!File.Exists(candidate))
                                {
                                        continue;
                                }
                                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(candidate, Encoding.UTF8));
                                if (doc.RootElement.TryGetProperty("version", out JsonElement v) && v.ValueKind == JsonValueKind.String)
                                {
                                        string? s = v.GetString();
                                        if (!string.IsNullOrWhiteSpace(s))
                                        {
                                                return s;
                                        }
                                }
                        }
                        catch
                        {
                        }
                }
                return null;
        }

        public static string? ResolveNodeExe()
        {
                try
                {
                        if (File.Exists(DshMaintenance.Node24Path))
                        {
                                return DshMaintenance.Node24Path;
                        }
                }
                catch
                {
                }
                string? path = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(path))
                {
                        foreach (string dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                                try
                                {
                                        string exe = Path.Combine(dir.Trim(), "node.exe");
                                        if (File.Exists(exe))
                                        {
                                                return exe;
                                        }
                                }
                                catch
                                {
                                }
                        }
                }
                return null;
        }

        private static (int ExitCode, string StdOut, string StdErr)? RunCapture(string fileName, IEnumerable<string> args, string? workDir, int timeoutSec)
        {
                try
                {
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                                FileName = fileName,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                StandardOutputEncoding = Encoding.UTF8,
                                StandardErrorEncoding = Encoding.UTF8
                        };
                        if (!string.IsNullOrEmpty(workDir))
                        {
                                psi.WorkingDirectory = workDir;
                        }
                        foreach (string a in args)
                        {
                                psi.ArgumentList.Add(a);
                        }
                        using Process? p = Process.Start(psi);
                        if (p == null)
                        {
                                return null;
                        }
                        StringBuilder stdout = new StringBuilder();
                        StringBuilder stderr = new StringBuilder();
                        p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                        p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        if (!p.WaitForExit(Math.Max(5, timeoutSec) * 1000))
                        {
                                try { p.Kill(true); } catch { }
                                return (-1, stdout.ToString(), "timeout");
                        }
                        p.WaitForExit();
                        return (p.ExitCode, stdout.ToString(), stderr.ToString());
                }
                catch (Exception ex)
                {
                        return (-1, "", ex.Message);
                }
        }

        private static bool CommandAvailable(string command, string workDir)
        {
                try
                {
                        (int ExitCode, string StdOut, string StdErr)? r = RunCapture("cmd.exe", new[] { "/c", command }, workDir, 20);
                        return r.HasValue && r.Value.ExitCode == 0;
                }
                catch
                {
                        return false;
                }
        }

        // ============================== 审计入口 ==============================

        public static DshCompatReport Audit(string dshDir, string profile, Action<string>? log = null, bool includeDumpConfig = true)
        {
                DshCompatReport r = new DshCompatReport
                {
                        DshDir = (dshDir ?? "").Trim().Trim('"'),
                        Profile = string.IsNullOrWhiteSpace(profile) ? DefaultProfile : profile.Trim()
                };
                if (r.DshDir.Length == 0)
                {
                        r.DshDir = "%DSH%";
                }
                r.ProfileDir = ResolveProfileDir(r.Profile);
                r.ProfileManifestPath = Path.Combine(r.ProfileDir, "package.json");
                r.PatchFilePath = Path.Combine(r.ProfileDir, "cordis.patch.yml");

                try
                {
                        r.DshDirExists = Directory.Exists(r.DshDir);
                        if (!r.DshDirExists)
                        {
                                r.Add(DshCompatLevel.Error, "环境", "DSH 目录不存在", r.DshDir);
                                return r;
                        }
                        log?.Invoke("[兼容] DSH 目录: " + r.DshDir);
                        r.GitRepo = Directory.Exists(Path.Combine(r.DshDir, ".git"));
                        r.DshVersion = GetDshVersion(r.DshDir) ?? "(未知)";
                        r.Head = DshMaintenance.GetGitHead(r.DshDir) ?? "";
                        r.BinBuilt = File.Exists(Path.Combine(r.DshDir, "apps", "cli", "lib", "bin.js"));
                        r.Node24Ok = ResolveNodeExe() != null;
                        r.PnpmOk = CommandAvailable("pnpm --version", r.DshDir);
                        log?.Invoke("[兼容] 版本 " + r.DshVersion + " · HEAD " + Short(r.Head) + " · node24=" + (r.Node24Ok ? "OK" : "缺失") + " · pnpm=" + (r.PnpmOk ? "OK" : "缺失") + " · 编译产物=" + (r.BinBuilt ? "OK" : "缺失"));

                        if (!r.GitRepo)
                        {
                                r.Add(DshCompatLevel.Info, "环境", "DSH 目录不是 git 仓库", "跳过 git 版本对比；仅做静态检查（npx 模式不受影响）");
                        }
                        if (!r.Node24Ok)
                        {
                                r.Add(DshCompatLevel.Error, "环境", "未找到 node24", DshMaintenance.Node24Path + "（DSH/pnpm11 必须使用 node24）");
                        }
                        if (!r.PnpmOk)
                        {
                                r.Add(DshCompatLevel.Warning, "环境", "pnpm 不可用", "无法安装/修复插件依赖；请确认 pnpm 在 PATH 中", false, "");
                        }
                        if (!r.BinBuilt)
                        {
                                r.Add(DshCompatLevel.Warning, "环境", "未找到编译产物 apps/cli/lib/bin.js", "重启启动器会自动触发全量重建", false, "");
                        }
                        if (!Directory.Exists(r.ProfileDir))
                        {
                                r.Add(DshCompatLevel.Error, "配置", "profile 目录不存在", r.ProfileDir);
                                return r;
                        }

                        // 1) manifest / bundle 激活
                        AuditManifestAndBundles(r, log);

                        // 2) dump-config：补丁目标 + 合成树
                        DumpData dump = new DumpData();
                        if (includeDumpConfig && r.BinBuilt && r.Node24Ok)
                        {
                                dump = RunDumpConfig(r, log);
                        }
                        else if (includeDumpConfig && !r.BinBuilt)
                        {
                                r.Add(DshCompatLevel.Info, "补丁", "跳过 --dump-config", "编译产物缺失，无法离线合成配置树");
                        }

                        // 3) cordis.patch.yml 静态检查（结合 dead id）
                        AuditPatchFile(r, dump, log);

                        // 3b) 插件行 inject 永不解析（fiber 永久 pending）+ bundle 补丁裸 id 静默失效
                        AuditInject(r, dump, log);
                        AuditBundlePatchRows(r, dump, log);

                        // 4) 插件 peer 兼容 + 解析
                        AuditPlugins(r, log);

                        // 5) node_modules 失效链接
                        AuditLinks(r, log);

                        log?.Invoke("[兼容] 检查完成：错误 " + r.ErrorCount + " · 警告 " + r.WarningCount + " · 可修复 " + r.RepairableCount);
                }
                catch (Exception ex)
                {
                        r.Add(DshCompatLevel.Error, "检查器", "兼容性检查异常终止", ex.Message);
                }
                return r;
        }

        private static string Short(string? head)
        {
                if (string.IsNullOrEmpty(head))
                {
                        return "(无)";
                }
                return head.Length >= 7 ? head.Substring(0, 7) : head;
        }

        // ============================== dump-config ==============================

        private sealed class DumpData
        {
                public bool Ran;
                public int ExitCode = -1;
                public string StdOut = "";
                public string StdErr = "";
                public HashSet<string> EntryIds = new(StringComparer.Ordinal);
                /// <summary>合成树里的每个 loader 行：(行 id, 模块名 name)。字面 disabled: true 的行不入内。</summary>
                public List<(string RowId, string ModuleName)> Rows = new();
                public HashSet<string> DeadPatchIds = new(StringComparer.Ordinal);
                public HashSet<string> NameMismatchIds = new(StringComparer.Ordinal);
                public List<string> BundleWarnings = new();
        }

        private static DumpData RunDumpConfig(DshCompatReport r, Action<string>? log)
        {
                DumpData d = new DumpData();
                string bin = Path.Combine(r.DshDir, "apps", "cli", "lib", "bin.js");
                string? node = ResolveNodeExe();
                if (!File.Exists(bin) || node == null)
                {
                        return d;
                }
                log?.Invoke("[兼容] 运行 --dump-config 合成配置树 ...");
                (int ExitCode, string StdOut, string StdErr)? res = RunCapture(node,
                        new[] { bin, "--profile", r.Profile, "--dump-config" }, r.DshDir, 180);
                if (!res.HasValue)
                {
                        r.Add(DshCompatLevel.Warning, "补丁", "--dump-config 无法启动", "node 进程启动失败；跳过补丁目标检查");
                        return d;
                }
                d.Ran = true;
                d.ExitCode = res.Value.ExitCode;
                d.StdOut = res.Value.StdOut ?? "";
                d.StdErr = res.Value.StdErr ?? "";
                r.DumpConfigRan = true;
                r.DumpConfigExitCode = d.ExitCode;
                r.DumpConfigOk = d.ExitCode == 0;
                if (d.ExitCode != 0)
                {
                        string tail = Tail(d.StdErr, 1200);
                        r.DumpConfigErrors.Add(tail);
                        r.Add(DshCompatLevel.Error, "补丁", "--dump-config 退出码 " + d.ExitCode, tail, false, "");
                }
                ParseDumpOutput(d);
                return d;
        }

        private static void ParseDumpOutput(DumpData d)
        {
                d.Rows.Clear();
                d.Rows.AddRange(ParseDumpRows(d.StdOut));
                foreach (string raw in (d.StdOut ?? "").Replace("\r\n", "\n").Split('\n'))
                {
                        Match m = Regex.Match(raw, @"^\s*-\s+id:\s*(.+?)\s*$");
                        if (!m.Success)
                        {
                                continue;
                        }
                        string id = m.Groups[1].Value.Trim().Trim('\'', '"');
                        if (id.Length > 0)
                        {
                                d.EntryIds.Add(id);
                        }
                }
                foreach (string raw in (d.StdErr ?? "").Replace("\r\n", "\n").Split('\n'))
                {
                        string line = raw.Trim();
                        if (line.Length == 0)
                        {
                                continue;
                        }
                        Match dead = Regex.Match(line, "patch(?:\\s+insert)?:\\s*entry\\s+\"(?<id>[^\"]+)\"\\s+not found");
                        if (dead.Success)
                        {
                                d.DeadPatchIds.Add(dead.Groups["id"].Value);
                                continue;
                        }
                        Match insertGroup = Regex.Match(line, "patch insert:\\s*entry\\s+\"(?<id>[^\"]+)\"\\s+is not a group");
                        if (insertGroup.Success)
                        {
                                d.DeadPatchIds.Add(insertGroup.Groups["id"].Value);
                                continue;
                        }
                        Match mismatch = Regex.Match(line, "patch:\\s*name mismatch for\\s+\"(?<id>[^\"]+)\"");
                        if (mismatch.Success)
                        {
                                d.NameMismatchIds.Add(mismatch.Groups["id"].Value);
                                continue;
                        }
                        Match skip = Regex.Match(line, "skipping profile bundle\\s+\"(?<name>[^\"]+)\":\\s*(?<why>.+)$");
                        if (skip.Success)
                        {
                                d.BundleWarnings.Add(skip.Groups["name"].Value + " —— " + skip.Groups["why"].Value.Trim());
                        }
                }
        }

        private static string Tail(string text, int max)
        {
                string t = (text ?? "").Trim();
                if (t.Length <= max)
                {
                        return t;
                }
                return "…" + t.Substring(t.Length - max);
        }


        // ============================== manifest / bundle ==============================

        private static JsonNode? TryReadManifest(string path)
        {
                try
                {
                        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) : null;
                }
                catch
                {
                        return null;
                }
        }

        private static bool IsBundleManifest(JsonNode? manifest)
        {
                if (manifest == null)
                {
                        return false;
                }
                JsonNode? patch = manifest["dsh"]?["bundle"]?["patch"];
                return patch != null && patch.ToString().Length > 0;
        }

        /// <summary>读取 bundle 声明的 patch 文件（含约定 cordis.patch.yml）中 insert 块插入的 loader 行 id。</summary>
        public static List<string> BundleInsertedIds(string packageDir)
        {
                List<string> ids = new List<string>();
                if (string.IsNullOrEmpty(packageDir) || !Directory.Exists(packageDir))
                {
                        return ids;
                }
                List<string> files = new List<string>();
                JsonNode? manifest = TryReadManifest(Path.Combine(packageDir, "package.json"));
                string? declared = manifest?["dsh"]?["bundle"]?["patch"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(declared))
                {
                        files.Add(Path.Combine(packageDir, declared!));
                }
                string conventional = Path.Combine(packageDir, "cordis.patch.yml");
                if (File.Exists(conventional) && !files.Contains(conventional, StringComparer.OrdinalIgnoreCase))
                {
                        files.Add(conventional);
                }
                foreach (string file in files)
                {
                        if (!File.Exists(file))
                        {
                                continue;
                        }
                        try
                        {
                                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                                bool inInsert = false;
                                foreach (string raw in lines)
                                {
                                        if (Regex.IsMatch(raw, @"^-\s*insert\s*:"))
                                        {
                                                inInsert = true;
                                                continue;
                                        }
                                        if (Regex.IsMatch(raw, @"^-\s") ) // 下一个顶层补丁块
                                        {
                                                inInsert = false;
                                        }
                                        if (!inInsert)
                                        {
                                                continue;
                                        }
                                        Match m = Regex.Match(raw, @"^\s*-\s*id:\s*(?<id>.+?)\s*$");
                                        if (m.Success)
                                        {
                                                string id = m.Groups["id"].Value.Trim().Trim('\'', '"');
                                                if (id.Length > 0 && !ids.Contains(id, StringComparer.Ordinal))
                                                {
                                                        ids.Add(id);
                                                }
                                        }
                                }
                        }
                        catch
                        {
                        }
                }
                return ids;
        }

        /// <summary>读取 .dsh-market/state.json 的持久化停用列表（不存在/损坏时为空）。</summary>
        public static List<string> ReadMarketDisabled(string statePath)
        {
                List<string> disabled = new List<string>();
                try
                {
                        if (!File.Exists(statePath))
                        {
                                return disabled;
                        }
                        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(statePath, Encoding.UTF8));
                        if (doc.RootElement.TryGetProperty("disabled", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                        {
                                foreach (JsonElement e in arr.EnumerateArray())
                                {
                                        if (e.ValueKind == JsonValueKind.String && e.GetString() is string s && !string.IsNullOrWhiteSpace(s))
                                        {
                                                disabled.Add(s);
                                        }
                                }
                        }
                }
                catch
                {
                }
                return disabled;
        }

        /// <summary>按 profile → DSH 安装的顺序解析已安装包目录。</summary>
        public static string? ResolveInstalledPackageDir(string name, string profileDir, string dshDir)
        {
                if (string.IsNullOrWhiteSpace(name))
                {
                        return null;
                }
                string rel = name.Replace('/', Path.DirectorySeparatorChar);
                string[] bases =
                {
                        Path.Combine(profileDir, "node_modules"),
                        Path.Combine(dshDir, "apps", "cli", "node_modules"),
                        Path.Combine(dshDir, "apps", "web", "node_modules"),
                        Path.Combine(dshDir, "node_modules")
                };
                foreach (string b in bases)
                {
                        try
                        {
                                string dir = Path.Combine(b, rel);
                                if (File.Exists(Path.Combine(dir, "package.json")))
                                {
                                        return dir;
                                }
                        }
                        catch
                        {
                        }
                }
                return null;
        }

        private static void AuditManifestAndBundles(DshCompatReport r, Action<string>? log)
        {
                if (!File.Exists(r.ProfileManifestPath))
                {
                        r.Add(DshCompatLevel.Error, "配置", "profile package.json 不存在", r.ProfileManifestPath);
                        return;
                }
                JsonNode? root;
                try
                {
                        root = JsonNode.Parse(File.ReadAllText(r.ProfileManifestPath, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                        r.Add(DshCompatLevel.Error, "配置", "package.json 解析失败", ex.Message);
                        return;
                }
                JsonObject? deps = root?["dependencies"] as JsonObject;
                JsonArray? bundlesNode = root?["dsh"]?["profile"]?["bundles"] as JsonArray;
                List<string> bundleNames = new List<string>();
                if (bundlesNode != null)
                {
                        foreach (JsonNode? n in bundlesNode)
                        {
                                string? s = n?.GetValue<string>();
                                if (!string.IsNullOrWhiteSpace(s))
                                {
                                        bundleNames.Add(s!);
                                }
                        }
                }
                HashSet<string> bundleSet = new HashSet<string>(bundleNames, StringComparer.Ordinal);
                r.MarketStatePath = Path.Combine(r.ProfileDir, ".dsh-market", "state.json");
                r.MarketDisabled = ReadMarketDisabled(r.MarketStatePath);

                if (deps != null)
                {
                        foreach (KeyValuePair<string, JsonNode?> kv in deps)
                        {
                                string name = kv.Key;
                                string? dir = ResolveInstalledPackageDir(name, r.ProfileDir, r.DshDir);
                                DshPluginInfo info = new DshPluginInfo
                                {
                                        Name = name,
                                        Dir = dir ?? "",
                                        Installed = dir != null,
                                        InBundles = bundleSet.Contains(name)
                                };
                                info.DisabledByMarket = r.MarketDisabled.Contains(name, StringComparer.Ordinal);
                                if (dir != null)
                                {
                                        JsonNode? m = TryReadManifest(Path.Combine(dir, "package.json"));
                                        info.Version = m?["version"]?.GetValue<string>() ?? "";
                                        info.IsBundle = IsBundleManifest(m);
                                        info.OwnedIds = BundleInsertedIds(dir);
                                }
                                else
                                {
                                        r.Add(DshCompatLevel.Warning, "插件", "依赖未安装: " + name, "node_modules 中找不到，pnpm 安装可能未完成");
                                }
                                if (info.IsBundle && !info.InBundles)
                                {
                                        r.BundlesToAdd.Add(name);
                                        r.Add(DshCompatLevel.Warning, "插件", "bundle 未激活: " + name,
                                                "该包声明了 dsh.bundle 但不在 dsh.profile.bundles 中，它的补丁目标会全部报 entry not found",
                                                true, "补进 dsh.profile.bundles");
                                }
                                r.Plugins.Add(info);
                        }
                }
                foreach (string b in bundleNames)
                {
                        string? dir = ResolveInstalledPackageDir(b, r.ProfileDir, r.DshDir);
                        if (dir == null && !InstallationBundles.Contains(b))
                        {
                                r.BundlesToRemove.Add(b);
                                r.Add(DshCompatLevel.Warning, "插件", "bundle 已失效: " + b,
                                        "既不在 profile 依赖中，也无法从 DSH 安装解析", true, "从 dsh.profile.bundles 移除");
                        }
                        else if (!r.Plugins.Any((DshPluginInfo x) => string.Equals(x.Name, b, StringComparison.Ordinal)))
                        {
                                r.Plugins.Add(new DshPluginInfo
                                {
                                        Name = b,
                                        Dir = dir ?? "",
                                        Installed = dir != null,
                                        InBundles = true,
                                        IsBundle = true,
                                        Version = dir == null ? "" : (TryReadManifest(Path.Combine(dir, "package.json"))?["version"]?.GetValue<string>() ?? "")
                                });
                        }
                }
                // 锁定停用检测：被用户在启动器中"停用并锁定"的插件被外部（DSH 网页端/市场插件）
                // 写回 dsh.profile.bundles 时，报错并纳入修复（复用 BundlesToRemove 移除链路）
                List<string> pinned = ReadPinnedInactive(r.Profile);
                foreach (string p in pinned)
                {
                    if (bundleNames.Contains(p, StringComparer.Ordinal))
                    {
                        if (!r.BundlesToRemove.Contains(p, StringComparer.Ordinal))
                        {
                            r.BundlesToRemove.Add(p);
                        }
                        r.Add(DshCompatLevel.Error, "插件", "插件被外部重新激活: " + p,
                                "该插件已在启动器中停用并锁定（peer 版本错位/导入失败），但被外部写回激活表；修复将再次移除并保持锁定", true, "从 dsh.profile.bundles 移除并保持锁定");
                    }
                    else
                    {
                        DshPluginInfo? pi = r.Plugins.FirstOrDefault((DshPluginInfo x) => x.Name == p);
                        if (pi != null)
                        {
                            pi.StateLabel = "已锁定停用";
                        }
                    }
                }
                r.ManifestRepairNeeded = r.BundlesToAdd.Count > 0 || r.BundlesToRemove.Count > 0;
                if (r.ManifestRepairNeeded)
                {
                        r.Add(DshCompatLevel.Info, "插件", "bundle 激活表需要修复",
                                "待补: " + (r.BundlesToAdd.Count == 0 ? "无" : string.Join(", ", r.BundlesToAdd))
                                + "；待移除: " + (r.BundlesToRemove.Count == 0 ? "无" : string.Join(", ", r.BundlesToRemove)),
                                true, "更新 dsh.profile.bundles（修改前自动备份 package.json）");
                }
                foreach (DshPluginInfo info in r.Plugins)
                {
                        if (info.Installed && info.DisabledByMarket)
                        {
                                r.MarketStateRepairNeeded = true;
                                r.Add(DshCompatLevel.Warning, "插件", "市场记录为已停用: " + info.Name,
                                        "出现在 .dsh-market/state.json 的 disabled 列表；修复可重新启用（删除对应禁用行并清理该记录）",
                                        true, "从 state.json 移除并删除其禁用补丁行");
                        }
                }
                if (r.MarketStateRepairNeeded)
                {
                        r.Add(DshCompatLevel.Info, "插件", "市场停用记录需要修复",
                                string.Join(", ", r.MarketDisabled), true, "更新 .dsh-market/state.json（修改前备份）");
                }
                log?.Invoke("[兼容] bundle: 已激活 " + bundleNames.Count + " 个，依赖 " + (deps?.Count ?? 0) + " 个；市场停用记录 " + r.MarketDisabled.Count + " 条");
        }

        // ============================== cordis.patch.yml ==============================

        private sealed class PatchBlock
        {
                public int Start;
                public int End;
                public string Id = "";
                public bool HasInsert;
                public bool HasConfig;
                public bool HasName;
                public List<string> DisabledValues = new();
                public List<(int Line, string Key, string NewKey)> KeyMigrations = new();
        }

        private static List<PatchBlock> ParsePatchBlocks(string[] lines)
        {
                List<PatchBlock> blocks = new List<PatchBlock>();
                int i = 0;
                while (i < lines.Length)
                {
                        if (!Regex.IsMatch(lines[i], @"^-\s"))
                        {
                                i++;
                                continue;
                        }
                        int start = i;
                        int j = i + 1;
                        while (j < lines.Length && !Regex.IsMatch(lines[j], @"^-\s"))
                        {
                                j++;
                        }
                        int end = j - 1;
                        while (end > start && string.IsNullOrWhiteSpace(lines[end]))
                        {
                                end--;
                        }
                        PatchBlock b = new PatchBlock { Start = start, End = end };
                        for (int k = start; k <= end; k++)
                        {
                                Match idm = Regex.Match(lines[k], @"^\s*-\s*id:\s*(?<id>.+?)\s*$");
                                if (idm.Success && b.Id.Length == 0)
                                {
                                        b.Id = idm.Groups["id"].Value.Trim().Trim('\'', '"');
                                }
                                if (Regex.IsMatch(lines[k], @"^\s*(?:-\s*)?insert\s*:"))
                                {
                                        b.HasInsert = true;
                                }
                                if (Regex.IsMatch(lines[k], @"^\s*config:\s*"))
                                {
                                        b.HasConfig = true;
                                }
                                if (Regex.IsMatch(lines[k], @"^\s*name:\s*"))
                                {
                                        b.HasName = true;
                                }
                                Match dm = Regex.Match(lines[k], @"^\s*disabled:\s*(?<v>true|false)\s*(?:#.*)?$", RegexOptions.IgnoreCase);
                                if (dm.Success)
                                {
                                        b.DisabledValues.Add(dm.Groups["v"].Value.ToLowerInvariant());
                                }
                        }
                        blocks.Add(b);
                        i = j;
                }
                return blocks;
        }

        private static void AuditPatchFile(DshCompatReport r, DumpData dump, Action<string>? log)
        {
                if (!File.Exists(r.PatchFilePath))
                {
                        r.Add(DshCompatLevel.Info, "补丁", "cordis.patch.yml 不存在", "首次启动 DSH 时会自动生成空模板");
                        return;
                }
                string[] lines;
                try
                {
                        lines = File.ReadAllLines(r.PatchFilePath, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                        r.Add(DshCompatLevel.Error, "补丁", "cordis.patch.yml 读取失败", ex.Message);
                        return;
                }
                List<PatchBlock> blocks = ParsePatchBlocks(lines);

                // 同一 id 的 disabled 取值（后出现者生效）
                Dictionary<string, string> lastDisabled = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (PatchBlock b in blocks)
                {
                        if (b.Id.Length > 0 && b.DisabledValues.Count > 0)
                        {
                                lastDisabled[b.Id] = b.DisabledValues[b.DisabledValues.Count - 1];
                        }
                }
                foreach (DshPluginInfo info in r.Plugins)
                {
                        info.DisabledByPatch = info.OwnedIds.Any((string id) =>
                                lastDisabled.TryGetValue(id, out string? dv) && dv == "true");
                }
                // 被市场持久化停用 / bundle 未激活 → 视为“误停用”，修复时删除禁用行以重新启用
                foreach (DshPluginInfo info in r.Plugins)
                {
                        if (!info.Installed)
                        {
                                continue;
                        }
                        bool marketOff = info.DisabledByMarket;
                        bool bundleOff = r.BundlesToAdd.Contains(info.Name, StringComparer.Ordinal);
                        if (!marketOff && !bundleOff)
                        {
                                continue;
                        }
                        if (marketOff && !r.ReenabledPlugins.Contains(info.Name, StringComparer.Ordinal))
                        {
                                r.ReenabledPlugins.Add(info.Name);
                        }
                        foreach (string id in info.OwnedIds)
                        {
                                if (!lastDisabled.TryGetValue(id, out string? dv) || dv != "true")
                                {
                                        continue;
                                }
                                if (!r.OrphanDisableIds.Contains(id, StringComparer.Ordinal))
                                {
                                        r.OrphanDisableIds.Add(id);
                                }
                                if (!r.PatchIssues.Any((DshPatchIssue x) => x.Id == id && x.Kind == "OrphanDisable"))
                                {
                                        r.PatchIssues.Add(new DshPatchIssue
                                        {
                                                Id = id,
                                                Kind = "OrphanDisable",
                                                Repairable = true,
                                                Detail = "被市场/载体操作误停用（bundle 未激活或市场记录为停用）；修复时删除该 disabled:true 行以重新启用"
                                        });
                                }
                        }
                }
                if (r.ReenabledPlugins.Count > 0)
                {
                        r.Add(DshCompatLevel.Warning, "插件", "检测到被市场停用的插件 " + r.ReenabledPlugins.Count + " 个",
                                string.Join(", ", r.ReenabledPlugins), true, "删除对应 disabled:true 补丁行，并清理 .dsh-market/state.json");
                }

                if (dump.Ran)
                {
                        foreach (string id in dump.DeadPatchIds.OrderBy((string x) => x, StringComparer.Ordinal))
                        {
                                if (r.OrphanDisableIds.Contains(id, StringComparer.Ordinal))
                                {
                                        continue; // 已在“误停用”里处理，修复动作是删除而不是注释
                                }
                                bool alias = PatchAliases.TryGetValue(id, out (string NewId, Dictionary<string, string> Keys) map);
                                bool targetOk = alias && dump.EntryIds.Contains(map.NewId);
                                PatchBlock? block = blocks.FirstOrDefault((PatchBlock b) => string.Equals(b.Id, id, StringComparison.Ordinal));
                                DshPatchIssue issue = new DshPatchIssue
                                {
                                        Id = id,
                                        Kind = targetOk ? "Alias" : "NotFound",
                                        AliasTarget = targetOk ? map.NewId : "",
                                        Repairable = targetOk || (block != null && !block.HasInsert),
                                        Detail = targetOk
                                                ? "已知改名: " + id + " → " + map.NewId + "（配置键 " + string.Join(",", map.Keys.Select((KeyValuePair<string, string> k) => k.Key + "→" + k.Value)) + "）"
                                                : (block == null
                                                        ? "补丁目标在合成树中不存在（可能来自 bundle 内层补丁）"
                                                        : (block.HasInsert ? "insert 目标不存在（保留不动，需人工确认）" : "补丁目标在合成树中不存在"))
                                };
                                r.PatchIssues.Add(issue);
                        }
                        foreach (string id in dump.NameMismatchIds.OrderBy((string x) => x, StringComparer.Ordinal))
                        {
                                r.PatchIssues.Add(new DshPatchIssue
                                {
                                        Id = id,
                                        Kind = "NameMismatch",
                                        Repairable = false,
                                        Detail = "补丁的 name 与合成树中的行不一致，DSH 会跳过该补丁（需人工确认新 name）"
                                });
                        }
                }

                // 同一 id 同时出现 disabled:true / false（冲突，人工确认）
                foreach (IGrouping<string, PatchBlock> g in blocks.Where((PatchBlock b) => b.Id.Length > 0).GroupBy((PatchBlock b) => b.Id, StringComparer.Ordinal))
                {
                        List<string> vals = g.SelectMany((PatchBlock b) => b.DisabledValues).Distinct().ToList();
                        if (vals.Count > 1)
                        {
                                r.Add(DshCompatLevel.Warning, "补丁", "补丁 disabled 冲突: " + g.Key,
                                        "同一 id 同时存在 disabled 的 true/false（后出现者生效），非自动修复范围，请人工确认插件启停意图", false, "");
                        }
                }

                if (dump.Ran && r.PatchIssues.Count > 0)
                {
                        List<DshPatchIssue> fixable = r.PatchIssues.Where((DshPatchIssue x) => x.Repairable).ToList();
                        string ids = string.Join(", ", r.PatchIssues.Take(12).Select((DshPatchIssue x) => x.Id));
                        if (r.PatchIssues.Count > 12)
                        {
                                ids += " 等 " + r.PatchIssues.Count + " 项";
                        }
                        r.Add(DshCompatLevel.Warning, "补丁", "存在 " + r.PatchIssues.Count + " 条失效补丁目标",
                                ids, fixable.Count > 0, "已知改名自动迁移；其余失效非 insert 条目整体注释（先备份 cordis.patch.yml）");
                }
                foreach (string bw in dump.BundleWarnings)
                {
                        r.Add(DshCompatLevel.Error, "插件", "bundle 加载失败", bw, false, "");
                }
                log?.Invoke("[兼容] 补丁: 条目 " + blocks.Count + " 个，失效目标 " + dump.DeadPatchIds.Count + " 个");
        }

        // ============================== 插件 peer / 解析 ==============================

        private static void AuditPlugins(DshCompatReport r, Action<string>? log)
        {
                Dictionary<string, List<string>> exemptions = ReadExemptions(r.ProfileDir);

                foreach (DshPluginInfo info in r.Plugins)
                {
                        if (!info.Installed || info.Dir.Length == 0)
                        {
                                continue;
                        }
                        JsonNode? m = TryReadManifest(Path.Combine(info.Dir, "package.json"));
                        if (m == null)
                        {
                                info.Errors.Add("package.json 不可读");
                                continue;
                        }
                        info.IsBundle = IsBundleManifest(m);
                        JsonNode? peers = m["peerDependencies"];
                        if (peers is JsonObject po)
                        {
                                foreach (KeyValuePair<string, JsonNode?> kv in po)
                                {
                                        string pname = kv.Key;
                                        if (!(pname == "@deepseek-ai/dsh" || pname.StartsWith("@deepseek-ai/dsh-", StringComparison.Ordinal)))
                                        {
                                                continue;
                                        }
                                        string range = kv.Value?.ToString() ?? "";
                                        if (!SemverUtil.Satisfies(r.DshVersion, range))
                                        {
                                                info.PeerIssues[pname] = range;
                                        }
                                }
                        }
                        info.Compatible = info.PeerIssues.Count == 0;
                        string key = info.Name + "@" + info.Version;
                        info.Exempted = exemptions.ContainsKey(key) && exemptions[key].Contains(r.DshVersion, StringComparer.Ordinal);
                        if (info.Exempted)
                        {
                                info.Compatible = true;
                        }
                }

                MergeNodeProbe(r, log);

                // 汇总不兼容插件：registry 依赖 → 可自动升级；本地/源码安装 → 只提示
                foreach (DshPluginInfo info in r.Plugins.Where((DshPluginInfo x) => x.Installed && x.Compatible == false))
                {
                        if (info.Exempted)
                        {
                                continue;
                        }
                        string peers = string.Join("; ", info.PeerIssues.Select((KeyValuePair<string, string> kv) => kv.Key + " " + kv.Value));
                        bool fromRegistry = info.Spec.Length > 0
                                && !info.Spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                                && !info.Spec.StartsWith("link:", StringComparison.OrdinalIgnoreCase)
                                && !info.Spec.StartsWith("workspace:", StringComparison.OrdinalIgnoreCase)
                                && !Path.IsPathRooted(info.Spec);
                        if (fromRegistry && !r.PluginsToUpgrade.Contains(info.Name, StringComparer.Ordinal))
                        {
                                r.PluginsToUpgrade.Add(info.Name);
                        }
                        string suggestion = fromRegistry
                                ? "升级该插件到兼容版本（启动器可自动执行 pnpm add " + info.Name + "@latest）；或显式接受风险: dsh plugin --profile " + r.Profile + " allow-version " + info.Name + "@" + info.Version + " --dsh-version " + r.DshVersion + " --accept-risk"
                                : "本地/源码安装（" + (info.Spec.Length > 0 ? info.Spec : "未知来源") + "），请在源码项目放宽 peerDependencies 并重新构建安装；或显式 allow-version 接受风险";
                        r.Add(DshCompatLevel.Error, "插件", "插件与 DSH 不兼容: " + info.Name,
                                "已安装 " + info.Name + "@" + info.Version + "，peer " + peers + "；" + suggestion, fromRegistry,
                                fromRegistry ? "pnpm add " + info.Name + "@latest（先停止 DSH）" : "");
                }

                // 规划：缺失 peer 中能从 DSH 安装内找到实体的 → 修复时在共享 node_modules 建 junction
                foreach (DshPluginInfo info in r.Plugins.Where((DshPluginInfo x) => x.Installed && x.MissingSpecs.Count > 0))
                {
                        foreach (string spec in info.MissingSpecs)
                        {
                                if (spec.Length == 0 || !spec.StartsWith('@') || r.PeerJunctionFixes.ContainsKey(spec))
                                {
                                        continue;
                                }
                                string? target = FindPackageDir(spec, r.DshDir, r.ProfileDir);
                                if (target != null && !target.StartsWith(r.ProfileDir, StringComparison.OrdinalIgnoreCase))
                                {
                                        r.PeerJunctionFixes[spec] = target;
                                }
                        }
                }

                // 关键依赖解析
                foreach (DshPluginInfo info in r.Plugins.Where((DshPluginInfo x) => x.MissingSpecs.Count > 0))
                {
                        bool junctionFixable = info.MissingSpecs.All((string s) => r.PeerJunctionFixes.ContainsKey(s));
                        r.Add(DshCompatLevel.Warning, "解析", "插件依赖无法解析: " + info.Name,
                                string.Join(", ", info.MissingSpecs.Distinct()), junctionFixable,
                                junctionFixable ? "在共享 node_modules 建立 junction 指向 DSH 安装内实体" : "请用 pnpm 重装该插件依赖，或由启动器重连失效链接");
                }

                if (!r.ProbeRan && r.BinBuilt && ResolveNodeExe() != null)
                {
                        r.Add(DshCompatLevel.Info, "解析", "DSH 兼容性探针未产出结果", string.Join("；", r.ProbeErrors.Take(2)));
                }
                FilterBundleActivation(r);
                List<string> pinnedInactive = ReadPinnedInactive(r.Profile);
                // 插件状态画像（面向“智能检测/修复插件”）
                foreach (DshPluginInfo info in r.Plugins)
                {
                        bool pinned = pinnedInactive.Contains(info.Name, StringComparer.Ordinal);
                        if (!info.Installed)
                        {
                                info.StateLabel = pinned ? "已锁定停用" : "未安装";
                        }
                        else if (pinned)
                        {
                                info.StateLabel = "已锁定停用";
                        }
                        else if (info.Compatible == false)
                        {
                                info.StateLabel = "不兼容";
                        }
                        else if (info.MissingSpecs.Count > 0)
                        {
                                info.StateLabel = "缺依赖";
                        }
                        else if (info.IsBundle && !info.InBundles)
                        {
                                info.StateLabel = r.BundlesToAdd.Contains(info.Name, StringComparer.Ordinal)
                                        ? "bundle 未激活"
                                        : "bundle 保持不激活";
                        }
                        else if (info.DisabledByMarket || info.DisabledByPatch)
                        {
                                info.StateLabel = "已停用";
                        }
                        else
                        {
                                info.StateLabel = "已启用";
                        }
                }
                List<DshPluginInfo> abnormal = r.Plugins.Where((DshPluginInfo x) => x.Installed && x.StateLabel != "已启用" && x.StateLabel != "bundle 保持不激活" && x.StateLabel != "已锁定停用").ToList();
                if (abnormal.Count > 0)
                {
                        r.Add(DshCompatLevel.Warning, "插件", "插件状态异常 " + abnormal.Count + " 个",
                                string.Join("；", abnormal.Select((DshPluginInfo x) => x.Name + "=" + x.StateLabel)),
                                abnormal.Any((DshPluginInfo x) => x.StateLabel == "bundle 未激活" || x.StateLabel == "已停用"),
                                "恢复未激活 bundle 并删除误停用行，使插件回到已启用");
                }
                log?.Invoke("[兼容] 插件: 共 " + r.Plugins.Count + " 个，不兼容 " + r.Plugins.Count((DshPluginInfo x) => x.Compatible == false) + " 个，解析缺失 " + r.Plugins.Count((DshPluginInfo x) => x.MissingSpecs.Count > 0) + " 个，状态异常 " + abnormal.Count + " 个");
        }

        /// <summary>
        /// bundle 自动激活守卫：peer 与当前 DSH 版本不匹配（含已豁免）/ 缺依赖 / 有错误的包，
        /// 不自动补进 dsh.profile.bundles —— 版本检查能豁免，但 client 模块表是构建期对齐的，
        /// 强行激活会在 web boot 报 "missed the module table" 加载失败（如 better-locale）。
        /// 同时回写 bundle 未激活警告的措辞与 ManifestRepairNeeded。
        /// </summary>
        private static void FilterBundleActivation(DshCompatReport r)
        {
                if (r.BundlesToAdd.Count == 0)
                {
                        return;
                }
                foreach (string name in r.BundlesToAdd.ToList())
                {
                        DshPluginInfo? info = r.Plugins.FirstOrDefault((DshPluginInfo x) => string.Equals(x.Name, name, StringComparison.Ordinal));
                        if (info == null)
                        {
                                continue;
                        }
                        bool peerMismatch = info.Compatible == false || (info.Exempted && info.PeerIssues.Count > 0);
                        // 用户锁定停用的包永远不自动激活
                        if (!peerMismatch && info.MissingSpecs.Count == 0 && info.Errors.Count == 0 && !ReadPinnedInactive(r.Profile).Contains(name, StringComparer.Ordinal))
                        {
                                continue;
                        }
                        r.BundlesToAdd.Remove(name);
                        string why = peerMismatch
                                ? "peer 与当前 DSH 版本不匹配" + (info.Exempted ? "（版本检查已豁免）" : "")
                                : (info.MissingSpecs.Count > 0 ? "依赖无法解析: " + string.Join(", ", info.MissingSpecs) : "插件自身状态异常");
                        DshCompatItem? warn = r.Find("插件", "bundle 未激活: " + name);
                        if (warn != null)
                        {
                                warn.Level = DshCompatLevel.Info;
                                warn.Title = "bundle 保持不激活: " + name;
                                warn.Detail = why + "；激活会导致 client 模块加载失败（module table 漂移），保持不激活。待插件发布兼容版本后再激活";
                                warn.Repairable = false;
                                warn.Repair = "";
                        }
                        else
                        {
                                r.Add(DshCompatLevel.Info, "插件", "bundle 保持不激活: " + name,
                                        why + "；激活会导致 client 模块加载失败，保持不激活", false, "");
                        }
                }
                r.ManifestRepairNeeded = r.BundlesToAdd.Count > 0 || r.BundlesToRemove.Count > 0;
                DshCompatItem? infoItem = r.Find("插件", "bundle 激活表需要修复");
                if (infoItem != null)
                {
                        if (!r.ManifestRepairNeeded)
                        {
                                r.Items.Remove(infoItem);
                        }
                        else
                        {
                                infoItem.Detail = "待补: " + (r.BundlesToAdd.Count == 0 ? "无" : string.Join(", ", r.BundlesToAdd))
                                        + "；待移除: " + (r.BundlesToRemove.Count == 0 ? "无" : string.Join(", ", r.BundlesToRemove));
                        }
                }
        }

        private static Dictionary<string, List<string>> ReadExemptions(string profileDir)
        {
                Dictionary<string, List<string>> result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                try
                {
                        string path = Path.Combine(profileDir, "compatibility.json");
                        if (!File.Exists(path))
                        {
                                return result;
                        }
                        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                        if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        {
                                return result;
                        }
                        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                        {
                                if (p.Value.ValueKind != JsonValueKind.Array)
                                {
                                        continue;
                                }
                                List<string> versions = new List<string>();
                                foreach (JsonElement e in p.Value.EnumerateArray())
                                {
                                        if (e.ValueKind == JsonValueKind.String && e.GetString() is string s)
                                        {
                                                versions.Add(s);
                                        }
                                }
                                result[p.Name] = versions;
                        }
                }
                catch
                {
                }
                return result;
        }


        // ============================== node_modules 失效链接 ==============================

        private static void AuditLinks(DshCompatReport r, Action<string>? log)
        {
                List<string> roots = new List<string>();
                string profileModules = Path.Combine(r.ProfileDir, "node_modules");
                if (Directory.Exists(profileModules))
                {
                        roots.Add(profileModules);
                }
                string? profilesDir = Path.GetDirectoryName(r.ProfileDir);
                string shared = profilesDir == null ? "" : Path.Combine(profilesDir, "node_modules");
                if (shared.Length > 0 && Directory.Exists(shared))
                {
                        roots.Add(shared);
                }
                if (roots.Count == 0)
                {
                        return;
                }

                foreach (string root in roots)
                {
                        foreach (FileSystemInfo entry in EnumerateReparsePoints(root, 3))
                        {
                                bool isDir = (entry.Attributes & FileAttributes.Directory) != 0;
                                if (!IsBrokenLink(entry.FullName, isDir))
                                {
                                        continue;
                                }
                                string pkg = PackageNameFromLink(entry.FullName);
                                string? replacement = FindPackageDir(pkg, r.DshDir, r.ProfileDir);
                                string action = replacement != null ? "relink" : "remove";
                                // profile 本地已有同名包时，共享层的失效回退链接可直接清理
                                try
                                {
                                        string localPkg = Path.Combine(profileModules, pkg.Replace('/', Path.DirectorySeparatorChar));
                                        if (replacement != null && File.Exists(Path.Combine(localPkg, "package.json")))
                                        {
                                                action = "remove";
                                                replacement = null;
                                        }
                                }
                                catch
                                {
                                }
                                r.LinkIssues.Add(new DshLinkIssue
                                {
                                        LinkPath = entry.FullName,
                                        PackageName = pkg,
                                        Action = action,
                                        NewTarget = replacement ?? "",
                                        Source = replacement == null ? "(无重连目标)" : "当前 DSH 安装"
                                });
                        }
                }
                if (r.LinkIssues.Count > 0)
                {
                        int relink = r.LinkIssues.Count((DshLinkIssue x) => x.Action == "relink");
                        int remove = r.LinkIssues.Count - relink;
                        r.Add(DshCompatLevel.Warning, "链接", "node_modules 存在 " + r.LinkIssues.Count + " 条失效链接",
                                "可重连 " + relink + " 条 / 可清理 " + remove + " 条（旧 DSH 安装路径残留，会导致插件解析失败）",
                                true, "按包名重连到当前 DSH 安装，找不到替代则删除失效 junction");
                }
                log?.Invoke("[兼容] 链接: 根 " + roots.Count + " 个，失效 " + r.LinkIssues.Count + " 条");
        }

        /// <summary>枚举目录下所有 reparse point，但不递归进入链接本身，避免环路与巨量目标。</summary>
        private static IEnumerable<FileSystemInfo> EnumerateReparsePoints(string root, int maxDepth)
        {
                if (!Directory.Exists(root))
                {
                        yield break;
                }
                Stack<(string Dir, int Depth)> stack = new Stack<(string, int)>();
                stack.Push((root, 0));
                while (stack.Count > 0)
                {
                        (string dir, int depth) = stack.Pop();
                        FileSystemInfo[] entries;
                        try
                        {
                                entries = new DirectoryInfo(dir).GetFileSystemInfos("*", SearchOption.TopDirectoryOnly);
                        }
                        catch
                        {
                                continue;
                        }
                        foreach (FileSystemInfo e in entries)
                        {
                                FileAttributes at;
                                try
                                {
                                        at = e.Attributes;
                                }
                                catch
                                {
                                        continue;
                                }
                                if ((at & FileAttributes.ReparsePoint) != 0)
                                {
                                        yield return e;
                                        continue;
                                }
                                if ((at & FileAttributes.Directory) == 0)
                                {
                                        continue;
                                }
                                string name = e.Name;
                                if (name == ".pnpm" || name == ".bin" || name.StartsWith(".", StringComparison.Ordinal))
                                {
                                        continue;
                                }
                                if (depth + 1 <= maxDepth)
                                {
                                        stack.Push((e.FullName, depth + 1));
                                }
                        }
                }
        }

        private static bool IsBrokenLink(string path, bool isDir)
        {
                if (!isDir)
                {
                        try
                        {
                                return !File.Exists(path);
                        }
                        catch
                        {
                                return true;
                        }
                }
                try
                {
                        using IEnumerator<string> e = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                        e.MoveNext();
                        return false;
                }
                catch (DirectoryNotFoundException)
                {
                        return true;
                }
                catch (IOException)
                {
                        return true;
                }
                catch (UnauthorizedAccessException)
                {
                        return false;
                }
                catch
                {
                        return false;
                }
        }

        private static string PackageNameFromLink(string path)
        {
                int idx = path.LastIndexOf("node_modules", StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                        return Path.GetFileName(path);
                }
                string rel = path.Substring(idx + "node_modules".Length).Trim('\\', '/');
                return rel.Replace('\\', '/');
        }

        /// <summary>在当前 DSH 安装 / pnpm store / profile 中查找同名包目录（作为失效链接的重连目标）。</summary>
        public static string? FindPackageDir(string packageName, string dshDir, string profileDir)
        {
                if (string.IsNullOrWhiteSpace(packageName))
                {
                        return null;
                }
                string rel = packageName.Replace('/', Path.DirectorySeparatorChar);
                string[] bases =
                {
                        Path.Combine(dshDir, "apps", "cli", "node_modules"),
                        Path.Combine(dshDir, "node_modules"),
                        Path.Combine(dshDir, "apps", "web", "node_modules"),
                        Path.Combine(dshDir, "apps", "desktop", "node_modules"),
                        Path.Combine(dshDir, "apps", "desktop-host", "node_modules"),
                        Path.Combine(profileDir, "node_modules")
                };
                foreach (string b in bases)
                {
                        try
                        {
                                string dir = Path.Combine(b, rel);
                                if (File.Exists(Path.Combine(dir, "package.json")))
                                {
                                        return dir;
                                }
                        }
                        catch
                        {
                        }
                }
                string mangled = packageName.Replace('/', '+');
                foreach (string store in new[]
                {
                        Path.Combine(dshDir, "node_modules", ".pnpm"),
                        Path.Combine(dshDir, "apps", "cli", "node_modules", ".pnpm"),
                        Path.Combine(dshDir, "apps", "web", "node_modules", ".pnpm")
                })
                {
                        if (!Directory.Exists(store))
                        {
                                continue;
                        }
                        try
                        {
                                foreach (string d in Directory.GetDirectories(store, mangled + "@*"))
                                {
                                        string inner = Path.Combine(d, "node_modules", rel);
                                        if (File.Exists(Path.Combine(inner, "package.json")))
                                        {
                                                return inner;
                                        }
                                }
                        }
                        catch
                        {
                        }
                }
                return null;
        }

        // ============================== Node 探针（权威判定 + 解析） ==============================

        private static void MergeNodeProbe(DshCompatReport r, Action<string>? log)
        {
                string? node = ResolveNodeExe();
                if (node == null || !r.BinBuilt)
                {
                        return;
                }
                string script = Path.Combine(Path.GetTempPath(), "LlamaDSH_dsh_compat_probe.mjs");
                try
                {
                        File.WriteAllText(script, NodeProbeScript, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                        r.ProbeErrors.Add("探针写入失败: " + ex.Message);
                        return;
                }
                (int ExitCode, string StdOut, string StdErr)? res = RunCapture(node, new[] { script, r.DshDir, r.ProfileDir }, r.DshDir, 180);
                if (!res.HasValue || res.Value.ExitCode != 0)
                {
                        r.ProbeErrors.Add(Tail(res?.StdErr ?? "node 启动失败", 800));
                        return;
                }
                string json = ExtractJson(res.Value.StdOut);
                if (json.Length == 0)
                {
                        r.ProbeErrors.Add(Tail(res.Value.StdErr, 800));
                        return;
                }
                try
                {
                        using JsonDocument doc = JsonDocument.Parse(json);
                        JsonElement root = doc.RootElement;
                        if (root.TryGetProperty("runtimeVersion", out JsonElement rv) && rv.ValueKind == JsonValueKind.String)
                        {
                                string? s = rv.GetString();
                                if (!string.IsNullOrWhiteSpace(s))
                                {
                                        r.DshVersion = s!;
                                }
                        }
                        if (root.TryGetProperty("errors", out JsonElement errs) && errs.ValueKind == JsonValueKind.Array)
                        {
                                foreach (JsonElement e in errs.EnumerateArray())
                                {
                                        if (e.ValueKind == JsonValueKind.String)
                                        {
                                                r.ProbeErrors.Add(e.GetString() ?? "");
                                        }
                                }
                        }
                        if (!root.TryGetProperty("plugins", out JsonElement plugins) || plugins.ValueKind != JsonValueKind.Array)
                        {
                                return;
                        }
                        foreach (JsonElement p in plugins.EnumerateArray())
                        {
                                string name = p.TryGetProperty("name", out JsonElement ne) ? ne.GetString() ?? "" : "";
                                if (name.Length == 0)
                                {
                                        continue;
                                }
                                DshPluginInfo? info = r.Plugins.FirstOrDefault((DshPluginInfo x) => string.Equals(x.Name, name, StringComparison.Ordinal));
                                if (info == null)
                                {
                                        info = new DshPluginInfo { Name = name };
                                        r.Plugins.Add(info);
                                }
                                info.Installed = p.TryGetProperty("installed", out JsonElement ie) && ie.ValueKind == JsonValueKind.True;
                                if (p.TryGetProperty("dir", out JsonElement de) && de.ValueKind == JsonValueKind.String)
                                {
                                        info.Dir = de.GetString() ?? info.Dir;
                                }
                                if (p.TryGetProperty("version", out JsonElement ve) && ve.ValueKind == JsonValueKind.String)
                                {
                                        info.Version = ve.GetString() ?? info.Version;
                                }
                                info.IsBundle = p.TryGetProperty("isBundle", out JsonElement be) && be.ValueKind == JsonValueKind.True;
                                info.InBundles = p.TryGetProperty("inBundles", out JsonElement ib) && ib.ValueKind == JsonValueKind.True;
                                if (p.TryGetProperty("compatible", out JsonElement ce))
                                {
                                        if (ce.ValueKind == JsonValueKind.True)
                                        {
                                                info.Compatible = true;
                                        }
                                        else if (ce.ValueKind == JsonValueKind.False)
                                        {
                                                info.Compatible = false;
                                        }
                                }
                                info.Exempted = p.TryGetProperty("exempted", out JsonElement ex) && ex.ValueKind == JsonValueKind.True;
                                if (p.TryGetProperty("spec", out JsonElement sp) && sp.ValueKind == JsonValueKind.String)
                                {
                                        info.Spec = sp.GetString() ?? "";
                                }
                                info.PeerIssues.Clear();
                                if (p.TryGetProperty("peers", out JsonElement pes) && pes.ValueKind == JsonValueKind.Object)
                                {
                                        foreach (JsonProperty kv in pes.EnumerateObject())
                                        {
                                                info.PeerIssues[kv.Name] = kv.Value.ToString();
                                        }
                                }
                                info.MissingSpecs.Clear();
                                if (p.TryGetProperty("missing", out JsonElement miss) && miss.ValueKind == JsonValueKind.Array)
                                {
                                        foreach (JsonElement e in miss.EnumerateArray())
                                        {
                                                if (e.ValueKind == JsonValueKind.String && e.GetString() is string spec)
                                                {
                                                        info.MissingSpecs.Add(spec);
                                                }
                                        }
                                }
                                if (p.TryGetProperty("errors", out JsonElement pe) && pe.ValueKind == JsonValueKind.Array)
                                {
                                        foreach (JsonElement e in pe.EnumerateArray())
                                        {
                                                if (e.ValueKind == JsonValueKind.String)
                                                {
                                                        info.Errors.Add(e.GetString() ?? "");
                                                }
                                        }
                                }
                        }
                        r.ProbeRan = true;
                        log?.Invoke("[兼容] DSH 探针: 权威判定完成（runtime " + r.DshVersion + "）");
                }
                catch (Exception ex)
                {
                        r.ProbeErrors.Add("探针 JSON 解析失败: " + ex.Message);
                }
        }

        private static string ExtractJson(string stdout)
        {
                string s = stdout ?? "";
                int start = s.IndexOf('{');
                int end = s.LastIndexOf('}');
                if (start < 0 || end <= start)
                {
                        return "";
                }
                return s.Substring(start, end - start + 1);
        }

        private const string NodeProbeScript = """
import { readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';

const dshDir = process.argv[2];
const profileDir = process.argv[3];
const out = { ok: false, runtimeVersion: null, plugins: [], errors: [] };
const BASE = ['@deepseek-ai/cordis', '@deepseek-ai/schemastery'];

function readJson(p) { try { return JSON.parse(readFileSync(p, 'utf8')); } catch (e) { return null; } }
function resolveDir(name) {
  const cands = [
    join(profileDir, 'node_modules', name),
    join(dshDir, 'apps', 'cli', 'node_modules', name),
    join(dshDir, 'apps', 'web', 'node_modules', name),
    join(dshDir, 'node_modules', name)
  ];
  for (const c of cands) { if (existsSync(join(c, 'package.json'))) { return c; } }
  return null;
}

let appBoot = null;
try {
  appBoot = await import(pathToFileURL(join(dshDir, 'apps', 'cli', 'node_modules', '@deepseek-ai', 'dsh-app-boot', 'lib', 'index.js')).href);
} catch (e) {
  out.errors.push('app-boot 导入失败: ' + String((e && e.message) || e));
}
if (appBoot) {
  try { out.runtimeVersion = appBoot.getDshRuntimeVersion(); } catch (e) { out.errors.push('runtime: ' + String((e && e.message) || e)); }
}
let exemptions = {};
if (appBoot) {
  try { exemptions = (appBoot.readProfileCompatibility(profileDir) || {}).exemptions || {}; } catch (e) { out.errors.push('compatibility.json: ' + String((e && e.message) || e)); }
}

const manifest = readJson(join(profileDir, 'package.json')) || {};
const bundles = new Set(((manifest.dsh || {}).profile || {}).bundles || []);
const deps = new Set(Object.keys(manifest.dependencies || {}));
const names = Array.from(new Set([...bundles, ...deps]));

for (const name of names) {
  const dir = resolveDir(name);
  const rec = { name, dir: dir || null, installed: !!dir, version: null, isBundle: false, inBundles: bundles.has(name), compatible: null, exempted: false, peers: {}, missing: [], errors: [], spec: (manifest.dependencies || {})[name] || null };
  if (!dir) { out.plugins.push(rec); continue; }
  const pj = readJson(join(dir, 'package.json'));
  if (!pj) { rec.errors.push('package.json 不可读'); out.plugins.push(rec); continue; }
  rec.version = pj.version || null;
  rec.isBundle = !!(pj.dsh && pj.dsh.bundle && pj.dsh.bundle.patch);
  if (appBoot) {
    try {
      const issue = appBoot.evaluatePluginCompatibility(pj, exemptions, out.runtimeVersion);
      if (issue === undefined) { rec.compatible = true; }
      else { rec.compatible = !!issue.exempted; rec.exempted = !!issue.exempted; rec.peers = issue.peers || {}; }
    } catch (e) { rec.errors.push('compat: ' + String((e && e.message) || e)); }
  }
  const optional = new Set(Object.entries(pj.peerDependenciesMeta || {}).filter(([, v]) => v && v.optional).map(([k]) => k));
  const specs = new Set([...Object.keys(pj.peerDependencies || {}), ...Object.keys(pj.dependencies || {}), ...BASE].filter((s) => !optional.has(s)));
  const anchor = pathToFileURL(join(dir, 'package.json')).href;
  let req = null;
  try { req = createRequire(anchor); } catch (e) { req = null; }
  for (const spec of specs) {
    let ok = false;
    if (req) {
      try { req.resolve(spec); ok = true; }
      catch (e) { if (e && e.code && e.code !== 'MODULE_NOT_FOUND') { ok = true; } }
    }
    if (!ok) { try { import.meta.resolve(spec, anchor); ok = true; } catch (e) { } }
    if (!ok) { rec.missing.push(spec); }
  }
  out.plugins.push(rec);
}
out.ok = true;
process.stdout.write(JSON.stringify(out));
""";


        // ============================== 修复 ==============================

        /// <summary>
        /// 执行自动修复：bundle 激活表 → 失效补丁 → 失效链接，全部先备份再写入，
        /// 完成后重新审计并返回结果。
        /// </summary>
        public static DshCompatRepairResult Repair(string dshDir, string profile, DshCompatReport report, Action<string>? log = null)
        {
                DshCompatRepairResult res = new DshCompatRepairResult();
                string dir = string.IsNullOrWhiteSpace(dshDir) ? report.DshDir : dshDir.Trim().Trim('"');
                string prof = string.IsNullOrWhiteSpace(profile) ? report.Profile : profile.Trim();
                try
                {
                        // 1) bundle 激活表
                        if (report.ManifestRepairNeeded)
                        {
                                string msg = RepairManifest(report, log);
                                res.ManifestChanged = true;
                                res.Messages.Add(msg);
                                log?.Invoke("[兼容修复] " + msg);
                        }

                        // 2) 重新审计，拿到修复 manifest 后的失效补丁/链接
                        log?.Invoke("[兼容修复] 重新审计以刷新补丁目标 ...");
                        DshCompatReport fresh = Audit(dir, prof, log, includeDumpConfig: true);

                        // 3) cordis.patch.yml：改名迁移 + 注释失效 + 删除误停用行（重新启用插件）
                        HashSet<string> removeIds = new HashSet<string>(fresh.OrphanDisableIds, StringComparer.Ordinal);
                        if (fresh.PatchIssues.Any((DshPatchIssue x) => x.Repairable) || removeIds.Count > 0)
                        {
                                (int commented, int migrated, int removedBlocks, bool changed) = RepairPatchFile(fresh.PatchFilePath, fresh.PatchIssues, removeIds, log);
                                res.CommentedPatchBlocks = commented;
                                res.MigratedPatchAliases = migrated;
                                res.RemovedDisableBlocks = removedBlocks;
                                if (changed)
                                {
                                        string msg = "补丁修复: 迁移 " + migrated + " 条改名，注释 " + commented + " 条失效条目，重新启用删除 " + removedBlocks + " 条禁用行（已备份 cordis.patch.yml）";
                                        res.Messages.Add(msg);
                                        log?.Invoke("[兼容修复] " + msg);
                                }
                        }

                        // 3b) 清理 .dsh-market/state.json 中对应的停用记录
                        if (fresh.MarketStateRepairNeeded && fresh.ReenabledPlugins.Count > 0)
                        {
                                string mmsg = RepairMarketState(fresh, log);
                                res.MarketStateChanged = true;
                                res.Messages.Add(mmsg);
                                log?.Invoke("[兼容修复] " + mmsg);
                        }

                        // 3c) 入口文件里永不解析的 inject 服务名
                        if (fresh.InjectFixes.Count > 0)
                        {
                                string imsg = RepairInjectFixes(fresh, log);
                                res.Messages.Add(imsg);
                                log?.Invoke("[兼容修复] " + imsg);
                        }

                        // 4) node_modules 失效链接
                        (int relinked, int removed, int failed) = RepairLinks(fresh, log);
                        res.RelinkedLinks = relinked;
                        res.RemovedLinks = removed;
                        if (relinked > 0 || removed > 0 || failed > 0)
                        {
                                string msg = "链接修复: 重连 " + relinked + " 条，清理 " + removed + " 条" + (failed > 0 ? "，失败 " + failed + " 条" : "");
                                res.Messages.Add(msg);
                                log?.Invoke("[兼容修复] " + msg);
                        }

                        // 4b) peer 与 DSH 版本不兼容 → pnpm 升级到最新（registry 依赖）
                        if (fresh.PluginsToUpgrade.Count > 0)
                        {
                                string msg = RepairUpgradePlugins(fresh, log);
                                res.Messages.Add(msg);
                                log?.Invoke("[兼容修复] " + msg);
                        }

                        // 4c) 缺失 peer → 在共享 node_modules 建 junction（升级后重算一次，覆盖新装插件的缺失项）
                        DshCompatReport afterUpgrade = fresh.PluginsToUpgrade.Count > 0
                                ? Audit(dir, prof, log, includeDumpConfig: false)
                                : fresh;
                        if (afterUpgrade.PeerJunctionFixes.Count > 0)
                        {
                                (int linked, int jfailed) = RepairPeerJunctions(afterUpgrade, log);
                                if (linked > 0 || jfailed > 0)
                                {
                                        string msg = "peer 补齐: junction " + linked + " 条" + (jfailed > 0 ? "，失败 " + jfailed + " 条" : "");
                                        res.Messages.Add(msg);
                                        log?.Invoke("[兼容修复] " + msg);
                                }
                        }

                        // 5) 复检
                        log?.Invoke("[兼容修复] 修复完成，重新检查 ...");
                        res.Report = Audit(dir, prof, log, includeDumpConfig: true);
                        res.Messages.Add("复检：错误 " + res.Report.ErrorCount + " · 警告 " + res.Report.WarningCount + " · 可修复 " + res.Report.RepairableCount);
                }
                catch (Exception ex)
                {
                        res.Messages.Add("修复异常: " + ex.Message);
                        try
                        {
                                res.Report = Audit(dir, prof, log, includeDumpConfig: false);
                        }
                        catch
                        {
                        }
                }
                return res;
        }

        private static string RepairManifest(DshCompatReport report, Action<string>? log)
        {
                string path = report.ProfileManifestPath;
                if (!File.Exists(path))
                {
                        return "package.json 不存在，跳过 bundle 修复";
                }
                string backup = path + ".bak-compat-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(path, backup, true);
                JsonNode? root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (root == null)
                {
                        return "package.json 解析为空，跳过";
                }
                JsonObject dsh = root["dsh"] as JsonObject ?? new JsonObject();
                root["dsh"] = dsh;
                JsonObject profileObj = dsh["profile"] as JsonObject ?? new JsonObject();
                dsh["profile"] = profileObj;
                JsonArray bundles = profileObj["bundles"] as JsonArray ?? new JsonArray();
                profileObj["bundles"] = bundles;

                List<string> names = new List<string>();
                foreach (JsonNode? n in bundles)
                {
                        string? s = n?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                                names.Add(s!);
                        }
                }
                int removed = names.RemoveAll((string x) => report.BundlesToRemove.Contains(x, StringComparer.Ordinal));
                foreach (string add in report.BundlesToAdd)
                {
                        if (!names.Contains(add, StringComparer.Ordinal))
                        {
                                names.Add(add);
                        }
                }
                JsonArray rebuilt = new JsonArray();
                foreach (string n in names)
                {
                        rebuilt.Add(n);
                }
                profileObj["bundles"] = rebuilt;

                JsonSerializerOptions opt = new JsonSerializerOptions
                {
                        WriteIndented = true,
                        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
                };
                File.WriteAllText(path, root.ToJsonString(opt) + Environment.NewLine, new UTF8Encoding(false));
                return "bundle 激活表已更新（补 " + report.BundlesToAdd.Count + "，移除 " + removed + "），备份: " + Path.GetFileName(backup);
        }

        /// <summary>清理 .dsh-market/state.json 中「被重新启用」插件的停用记录（先备份）。</summary>
        private static string RepairMarketState(DshCompatReport report, Action<string>? log)
        {
                string path = report.MarketStatePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                        return "market state.json 不存在，跳过停用记录清理";
                }
                try
                {
                        JsonNode? root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
                        if (root == null || root["disabled"] is not JsonArray arr)
                        {
                                return "market state.json 无可清理的 disabled 列表";
                        }
                        HashSet<string> keepOff = new HashSet<string>(report.ReenabledPlugins, StringComparer.Ordinal);
                        JsonArray next = new JsonArray();
                        List<string> removedNames = new List<string>();
                        foreach (JsonNode? node in arr)
                        {
                                string? s = node?.GetValue<string>();
                                if (string.IsNullOrWhiteSpace(s))
                                {
                                        continue;
                                }
                                if (keepOff.Contains(s))
                                {
                                        removedNames.Add(s);
                                }
                                else
                                {
                                        next.Add(s);
                                }
                        }
                        if (removedNames.Count == 0)
                        {
                                return "market state.json 无需清理";
                        }
                        string backup = path + ".bak-compat-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        File.Copy(path, backup, true);
                        root["disabled"] = next;
                        JsonSerializerOptions opt = new JsonSerializerOptions
                        {
                                WriteIndented = false,
                                TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
                        };
                        File.WriteAllText(path, root.ToJsonString(opt) + Environment.NewLine, new UTF8Encoding(false));
                        return "market 停用记录已清理: " + string.Join(", ", removedNames) + "（已备份 state.json）";
                }
                catch (Exception ex)
                {
                        return "market state.json 清理失败: " + ex.Message;
                }
        }

        /// <summary>注释失效补丁、迁移已知改名、删除误停用行。只处理非 insert 的顶层补丁块；
        /// 写入前备份整个文件。返回 (注释块数, 迁移块数, 删除的禁用块数, 是否有改动)。</summary>
        private static (int Commented, int Migrated, int Removed, bool Changed) RepairPatchFile(string path, List<DshPatchIssue> issues, HashSet<string> removeIds, Action<string>? log)
        {
                if (!File.Exists(path))
                {
                        return (0, 0, 0, false);
                }
                Dictionary<string, string> renames = new Dictionary<string, string>(StringComparer.Ordinal);
                HashSet<string> commentIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (DshPatchIssue issue in issues)
                {
                        if (!issue.Repairable)
                        {
                                continue;
                        }
                        if (issue.Kind == "Alias" && issue.AliasTarget.Length > 0)
                        {
                                renames[issue.Id] = issue.AliasTarget;
                        }
                        else if (issue.Kind == "NotFound")
                        {
                                commentIds.Add(issue.Id);
                        }
                }
                if (renames.Count == 0 && commentIds.Count == 0 && (removeIds == null || removeIds.Count == 0))
                {
                        return (0, 0, 0, false);
                }
                string raw = File.ReadAllText(path, Encoding.UTF8);
                string nl = raw.Contains("\r\n") ? "\r\n" : "\n";
                string[] lines = raw.Replace("\r\n", "\n").Split('\n');
                List<PatchBlock> blocks = ParsePatchBlocks(lines);
                HashSet<int> deleteLines = new HashSet<int>();
                int commented = 0;
                int migrated = 0;
                int removed = 0;
                foreach (PatchBlock b in blocks)
                {
                        if (b.Id.Length == 0 || b.HasInsert)
                        {
                                continue;
                        }
                        // 被市场/载体误停用的纯禁用行：整块删除 = 重新启用插件
                        if (removeIds != null && removeIds.Contains(b.Id) && !b.HasConfig && !b.HasName && b.DisabledValues.Count > 0)
                        {
                                for (int k = b.Start; k <= b.End; k++)
                                {
                                        deleteLines.Add(k);
                                }
                                int after = b.End + 1;
                                if (after < lines.Length && string.IsNullOrWhiteSpace(lines[after]))
                                {
                                        deleteLines.Add(after);
                                }
                                removed++;
                                log?.Invoke("[兼容修复] 重新启用插件，移除禁用行: " + b.Id);
                                continue;
                        }
                        if (renames.TryGetValue(b.Id, out string? newId))
                        {
                                PatchAliases.TryGetValue(b.Id, out (string NewId, Dictionary<string, string> Keys) map);
                                for (int k = b.Start; k <= b.End; k++)
                                {
                                        if (Regex.IsMatch(lines[k], @"^\s*-\s*id:\s*"))
                                        {
                                                lines[k] = Regex.Replace(lines[k], @"^(\s*-\s*id:\s*).*$", "${1}" + newId);
                                        }
                                        if (map.Keys.Count > 0)
                                        {
                                                Match km = Regex.Match(lines[k], @"^(?<ind>\s*)(?<key>[A-Za-z0-9_.\-]+)(?<rest>\s*:.*)$");
                                                if (km.Success && map.Keys.TryGetValue(km.Groups["key"].Value, out string? nk))
                                                {
                                                        lines[k] = km.Groups["ind"].Value + nk + km.Groups["rest"].Value;
                                                }
                                        }
                                }
                                migrated++;
                                log?.Invoke("[兼容修复] 补丁改名 " + b.Id + " → " + newId);
                        }
                        else if (commentIds.Contains(b.Id))
                        {
                                for (int k = b.Start; k <= b.End; k++)
                                {
                                        if (string.IsNullOrWhiteSpace(lines[k]) || lines[k].TrimStart().StartsWith("#", StringComparison.Ordinal))
                                        {
                                                continue;
                                        }
                                        lines[k] = "# " + lines[k];
                                }
                                commented++;
                                log?.Invoke("[兼容修复] 注释失效补丁: " + b.Id);
                        }
                }
                if (commented == 0 && migrated == 0 && removed == 0)
                {
                        return (0, 0, 0, false);
                }
                string backup = path + ".bak-compat-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(path, backup, true);
                if (deleteLines.Count > 0)
                {
                        List<string> kept = new List<string>();
                        for (int k = 0; k < lines.Length; k++)
                        {
                                if (!deleteLines.Contains(k))
                                {
                                        kept.Add(lines[k]);
                                }
                        }
                        lines = kept.ToArray();
                }
                File.WriteAllText(path, string.Join(nl, lines), new UTF8Encoding(false));
                return (commented, migrated, removed, true);
        }

        private static (int Relinked, int Removed, int Failed) RepairLinks(DshCompatReport report, Action<string>? log)
        {
                int relinked = 0;
                int removed = 0;
                int failed = 0;
                foreach (DshLinkIssue issue in report.LinkIssues)
                {
                        try
                        {
                                FileSystemInfo? entry = GetExistingReparsePoint(issue.LinkPath);
                                if (entry == null)
                                {
                                        continue;
                                }
                                bool isDir = (entry.Attributes & FileAttributes.Directory) != 0;
                                if (!IsBrokenLink(entry.FullName, isDir))
                                {
                                        continue;
                                }
                                bool wantRelink = issue.Action == "relink"
                                        && issue.NewTarget.Length > 0
                                        && Directory.Exists(issue.NewTarget)
                                        && File.Exists(Path.Combine(issue.NewTarget, "package.json"));
                                RemoveLink(entry.FullName);
                                if (wantRelink && CreateJunction(entry.FullName, issue.NewTarget))
                                {
                                        relinked++;
                                        log?.Invoke("[兼容修复] 重连 " + issue.PackageName + " → " + issue.NewTarget);
                                }
                                else
                                {
                                        removed++;
                                        log?.Invoke("[兼容修复] 清理失效链接 " + issue.PackageName + (issue.NewTarget.Length > 0 ? "（无可用替代）" : ""));
                                }
                        }
                        catch (Exception ex)
                        {
                                failed++;
                                log?.Invoke("[兼容修复] 链接处理失败 " + issue.PackageName + ": " + ex.Message);
                        }
                }
                return (relinked, removed, failed);
        }

        /// <summary>peer 与 DSH 不兼容的 registry 插件 → pnpm add @latest 一次性升级（需已停止 DSH）。</summary>
        private static string RepairUpgradePlugins(DshCompatReport report, Action<string>? log)
        {
                if (!report.PnpmOk)
                {
                        return "pnpm 不可用，跳过插件升级（请确认 pnpm 在 PATH 中）";
                }
                List<string> names = report.PluginsToUpgrade;
                string args = string.Join(" ", names.Select((string n) => n + "@latest"));
                log?.Invoke("[兼容修复] pnpm add " + args + "（可能需要数分钟）...");
                (int ExitCode, string StdOut, string StdErr)? r = RunCapture("cmd.exe",
                        new List<string> { "/c", "pnpm", "add" }.Concat(names.Select((string n) => n + "@latest")).ToArray(),
                        report.ProfileDir, 900);
                if (r == null)
                {
                        return "pnpm 启动失败，插件升级未执行";
                }
                string tail = Tail(r.Value.StdErr.Length > 0 ? r.Value.StdErr : r.Value.StdOut, 600);
                if (r.Value.ExitCode != 0)
                {
                        return "插件升级失败（exit " + r.Value.ExitCode + "）：" + tail;
                }
                return "插件已升级到最新: " + string.Join(", ", names);
        }

        /// <summary>把 DSH 安装内存在的缺失 peer 以 junction 补进共享 node_modules，使插件目录可解析。</summary>
        private static (int Linked, int Failed) RepairPeerJunctions(DshCompatReport report, Action<string>? log)
        {
                string? profilesDir = Path.GetDirectoryName(report.ProfileDir);
                if (string.IsNullOrEmpty(profilesDir))
                {
                        return (0, 0);
                }
                string shared = Path.Combine(profilesDir, "node_modules");
                int linked = 0;
                int failed = 0;
                foreach (KeyValuePair<string, string> kv in report.PeerJunctionFixes)
                {
                        try
                        {
                                string linkPath = Path.Combine(shared, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                                if (Directory.Exists(linkPath) && File.Exists(Path.Combine(linkPath, "package.json")))
                                {
                                        continue; // 已可解析，无需处理
                                }
                                Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
                                FileSystemInfo? existing = GetExistingReparsePoint(linkPath);
                                if (existing != null)
                                {
                                        RemoveLink(existing.FullName);
                                }
                                else if (Directory.Exists(linkPath) && Directory.GetFileSystemEntries(linkPath).Length > 0)
                                {
                                        failed++;
                                        log?.Invoke("[兼容修复] peer 补齐跳过（目录非空且非链接）: " + kv.Key);
                                        continue;
                                }
                                if (CreateJunction(linkPath, kv.Value))
                                {
                                        linked++;
                                        log?.Invoke("[兼容修复] 补齐 peer 链接 " + kv.Key + " → " + kv.Value);
                                }
                                else
                                {
                                        failed++;
                                }
                        }
                        catch (Exception ex)
                        {
                                failed++;
                                log?.Invoke("[兼容修复] peer 补齐失败 " + kv.Key + ": " + ex.Message);
                        }
                }
                return (linked, failed);
        }

        private static FileSystemInfo? GetExistingReparsePoint(string path)
        {
                try
                {
                        FileSystemInfo fi = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                        if (!fi.Exists && fi.Attributes == (FileAttributes)(-1))
                        {
                                return null;
                        }
                        return fi;
                }
                catch
                {
                        return null;
                }
        }

        private static void RemoveLink(string path)
        {
                try
                {
                        Directory.Delete(path, false);
                        return;
                }
                catch
                {
                }
                try
                {
                        File.Delete(path);
                        return;
                }
                catch
                {
                }
                try
                {
                        RunCapture("cmd.exe", new[] { "/c", "rmdir", "\"" + path + "\"" }, Path.GetDirectoryName(path), 20);
                }
                catch
                {
                }
        }

        private static bool CreateJunction(string linkPath, string target)
        {
                try
                {
                        string? parent = Path.GetDirectoryName(linkPath);
                        if (!string.IsNullOrEmpty(parent))
                        {
                                Directory.CreateDirectory(parent);
                        }
                        if (Directory.Exists(linkPath) || File.Exists(linkPath))
                        {
                                RemoveLink(linkPath);
                        }
                        (int ExitCode, string StdOut, string StdErr)? r = RunCapture("cmd.exe",
                                new[] { "/c", "mklink", "/J", linkPath, target }, parent, 30);
                        if (r.HasValue && r.Value.ExitCode != 0)
                        {
                                return false;
                        }
                        return File.Exists(Path.Combine(linkPath, "package.json")) || Directory.Exists(linkPath);
                }
                catch
                {
                        return false;
                }
        }

        // ============================== inject 服务 / bundle 补丁裸 id ==============================

        /// <summary>bundle 补丁文件里的行 id：insert 块插入的 id，与只覆盖已有行的顶层裸 id。</summary>
        public sealed class DshBundlePatchIds
        {
                public List<string> InsertIds = new();
                public List<string> BareIds = new();
        }

        /// <summary>
        /// 从 --dump-config 输出解析每个 loader 行：(行 id, 紧随其后的模块名 name)。
        /// name 带缩进或不带都接受；行体内出现字面 disabled: true 的行视为未挂载，跳过。
        /// </summary>
        public static List<(string RowId, string ModuleName)> ParseDumpRows(string? stdout)
        {
                List<(string RowId, string ModuleName)> rows = new List<(string, string)>();
                string[] lines = (stdout ?? "").Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                        Match m = Regex.Match(lines[i], @"^(\s*)-\s+id:\s*(?<id>.+?)\s*$");
                        if (!m.Success)
                        {
                                continue;
                        }
                        string id = m.Groups["id"].Value.Trim().Trim('\'', '"');
                        if (id.Length == 0)
                        {
                                continue;
                        }
                        int baseIndent = m.Groups[1].Value.Length;
                        string name = "";
                        bool disabled = false;
                        for (int j = i + 1; j < lines.Length && j <= i + 24; j++)
                        {
                                string line = lines[j];
                                if (line.Trim().Length == 0)
                                {
                                        break;
                                }
                                string trimmed = line.TrimStart();
                                int indent = line.Length - trimmed.Length;
                                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                                {
                                        break;
                                }
                                if (indent <= baseIndent && trimmed.StartsWith("- ", StringComparison.Ordinal))
                                {
                                        break;
                                }
                                if (indent <= baseIndent + 2)
                                {
                                        if (Regex.IsMatch(trimmed, @"^disabled:\s*true\s*$"))
                                        {
                                                disabled = true;
                                                break;
                                        }
                                        Match nm = Regex.Match(trimmed, @"^name:\s*(?<v>.+?)\s*$");
                                        if (nm.Success && name.Length == 0)
                                        {
                                                name = nm.Groups["v"].Value.Trim().Trim('\'', '"');
                                        }
                                }
                        }
                        if (disabled || name.Length == 0)
                        {
                                continue;
                        }
                        rows.Add((id, name));
                }
                return rows;
        }

        /// <summary>
        /// 从插件入口 js 文本提取 inject 声明的服务名。支持单引号/双引号、跨行缩进
        /// 与 inject: [...] 形式；只认 inject 数组本身，不递归其它数组。
        /// </summary>
        public static List<string> ExtractInjectNames(string? jsText)
        {
                List<string> names = new List<string>();
                string text = jsText ?? "";
                foreach ((int start, int length) in FindInjectArraySpans(text))
                {
                        if (start < 0 || length <= 0 || start + length > text.Length)
                        {
                                continue;
                        }
                        string body = text.Substring(start, length);
                        foreach (Match m in Regex.Matches(body, "['\"](?<n>[A-Za-z_$][A-Za-z0-9_$.-]*)['\"]"))
                        {
                                string n = m.Groups["n"].Value;
                                if (n.Length > 0 && !names.Contains(n, StringComparer.Ordinal))
                                {
                                        names.Add(n);
                                }
                        }
                }
                return names;
        }

        /// <summary>定位文本里每个 inject 数组的括号内容区（左闭右开偏移）。</summary>
        private static List<(int Start, int Length)> FindInjectArraySpans(string text)
        {
                List<(int Start, int Length)> spans = new List<(int, int)>();
                if (text.Length == 0)
                {
                        return spans;
                }
                foreach (Match m in Regex.Matches(text, @"(?<![\w$.])inject\s*[:=]\s*\["))
                {
                        int open = m.Index + m.Length - 1;
                        int depth = 0;
                        for (int k = open; k < text.Length && k - open < 20000; k++)
                        {
                                if (text[k] == '[')
                                {
                                        depth++;
                                }
                                else if (text[k] == ']')
                                {
                                        depth--;
                                        if (depth == 0)
                                        {
                                                spans.Add((open + 1, k - open - 1));
                                                break;
                                        }
                                }
                        }
                }
                return spans;
        }

        /// <summary>
        /// 从入口文本的 inject 数组里删除（或改名）一个服务名，返回新文本。
        /// 只改 inject 数组内的字符串字面量，绝不碰 ctx.logger 这类运行时引用。
        /// </summary>
        public static string RemoveFromInjectService(string text, string service, string replacement, out int count)
        {
                count = 0;
                if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(service))
                {
                        return text;
                }
                List<(int Start, int Length)> spans = FindInjectArraySpans(text);
                for (int i = spans.Count - 1; i >= 0; i--)
                {
                        int start = spans[i].Start;
                        int length = spans[i].Length;
                        if (start < 0 || length < 0 || start + length > text.Length)
                        {
                                continue;
                        }
                        string body = text.Substring(start, length);
                        string dq = "\"" + service + "\"";
                        string sq = "'" + service + "'";
                        int at = body.IndexOf(dq, StringComparison.Ordinal);
                        int len = dq.Length;
                        if (at < 0)
                        {
                                at = body.IndexOf(sq, StringComparison.Ordinal);
                                len = sq.Length;
                        }
                        if (at < 0)
                        {
                                continue;
                        }
                        string quote = body[at] == '\'' ? "'" : "\"";
                        string newBody;
                        if (replacement.Length > 0)
                        {
                                newBody = body.Substring(0, at) + quote + replacement + quote + body.Substring(at + len);
                        }
                        else
                        {
                                newBody = CleanupInjectArray(body.Remove(at, len));
                        }
                        text = text.Substring(0, start) + newBody + text.Substring(start + length);
                        count++;
                }
                return text;
        }

        /// <summary>删掉一个数组元素后收拾多余逗号（保留尾逗号之外的空格与换行）。</summary>
        private static string CleanupInjectArray(string body)
        {
                string prev;
                do
                {
                        prev = body;
                        body = Regex.Replace(body, @",\s*,", ",");
                        body = Regex.Replace(body, @"^\s*,", "");
                        body = Regex.Replace(body, @",\s*$", "");
                }
                while (!string.Equals(body, prev, StringComparison.Ordinal));
                return body;
        }

        /// <summary>
        /// 把 loader 行的模块名（包名，或 包名/子路径）解析成真正会执行的入口 js 文件：
        /// 按 package.json exports（含条件对象）→ module → main → 约定 index.js 逐级回退。
        /// </summary>
        public static string? ResolveModuleEntryFile(string moduleName, string dshDir, string profileDir)
        {
                string name = (moduleName ?? "").Trim();
                if (name.Length == 0)
                {
                        return null;
                }
                string pkgName = name;
                string subPath = "";
                int slash = name.IndexOf('/');
                if (name.StartsWith("@", StringComparison.Ordinal))
                {
                        int second = slash >= 0 ? name.IndexOf('/', slash + 1) : -1;
                        if (second > 0)
                        {
                                pkgName = name.Substring(0, second);
                                subPath = name.Substring(second + 1);
                        }
                }
                else if (slash > 0)
                {
                        pkgName = name.Substring(0, slash);
                        subPath = name.Substring(slash + 1);
                }
                string? dir = ResolveInstalledPackageDir(pkgName, profileDir, dshDir)
                        ?? FindPackageDir(pkgName, dshDir, profileDir);
                if (dir == null || !Directory.Exists(dir))
                {
                        return null;
                }
                try
                {
                        JsonNode? manifest = TryReadManifest(Path.Combine(dir, "package.json"));
                        if (manifest != null)
                        {
                                string key = subPath.Length == 0 ? "." : "./" + subPath;
                                string? rel = null;
                                if (manifest["exports"] is JsonObject exports
                                        && exports.TryGetPropertyValue(key, out JsonNode? entry))
                                {
                                        rel = ExportTargetPath(entry);
                                }
                                if (rel == null && subPath.Length == 0)
                                {
                                        rel = ExportTargetPath(manifest["exports"])
                                                ?? ExportTargetPath(manifest["module"])
                                                ?? ExportTargetPath(manifest["main"]);
                                }
                                if (rel != null)
                                {
                                        string? file = ResolveRelativeFile(dir, rel);
                                        if (file != null)
                                        {
                                                return file;
                                        }
                                }
                        }
                }
                catch
                {
                }
                return ResolveRelativeFile(dir, subPath.Length == 0 ? "index.js" : subPath);
        }

        /// <summary>从 exports 目标值（字符串，或 default/import/require 条件对象）取默认入口路径。</summary>
        private static string? ExportTargetPath(JsonNode? node)
        {
                for (int depth = 0; node != null && depth < 5; depth++)
                {
                        if (node is JsonValue value)
                        {
                                try
                                {
                                        string s = value.GetValue<string>();
                                        return string.IsNullOrWhiteSpace(s) ? null : s;
                                }
                                catch
                                {
                                        return null;
                                }
                        }
                        if (node is not JsonObject obj)
                        {
                                return null;
                        }
                        JsonNode? next = null;
                        foreach (string key in new[] { "default", "import", "module", "require", "node" })
                        {
                                if (obj.TryGetPropertyValue(key, out JsonNode? candidate) && candidate != null)
                                {
                                        next = candidate;
                                        break;
                                }
                        }
                        node = next;
                }
                return null;
        }

        /// <summary>把包内相对路径解析成存在的文件（补 .js/.mjs/.cjs 扩展名与 index 回退）。</summary>
        private static string? ResolveRelativeFile(string dir, string rel)
        {
                if (string.IsNullOrWhiteSpace(rel))
                {
                        return null;
                }
                try
                {
                        string path = Path.GetFullPath(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)));
                        if (File.Exists(path))
                        {
                                return path;
                        }
                        foreach (string ext in new[] { ".js", ".mjs", ".cjs" })
                        {
                                if (File.Exists(path + ext))
                                {
                                        return path + ext;
                                }
                        }
                        foreach (string index in new[] { "index.js", "index.mjs", "index.cjs" })
                        {
                                string candidate = Path.Combine(path, index);
                                if (File.Exists(candidate))
                                {
                                        return candidate;
                                }
                        }
                }
                catch
                {
                }
                return null;
        }

        /// <summary>
        /// 逐行检查合成树里的 loader 行：入口文件 inject 里若声明了当前 DSH 永不解析的服务名，
        /// 该行会永久 pending（apply 从不执行）。logger 任何 cordis 4 版本都错；
        /// webRuntime 自 0.2.1-alpha.1 被 webStartup 取代；invariants 自 0.2.0-rc.2 整包删除。
        /// </summary>
        private static void AuditInject(DshCompatReport r, DumpData dump, Action<string>? log)
        {
                if (!dump.Ran || dump.Rows.Count == 0)
                {
                        log?.Invoke("[兼容] inject 检查: 跳过（无 --dump-config 行）");
                        return;
                }
                int resolved = 0;
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                foreach ((string rowId, string moduleName) in dump.Rows)
                {
                        string? entry = ResolveModuleEntryFile(moduleName, r.DshDir, r.ProfileDir);
                        if (entry == null)
                        {
                                continue;
                        }
                        resolved++;
                        string text;
                        try
                        {
                                FileInfo fi = new FileInfo(entry);
                                if (!fi.Exists || fi.Length > 8000000)
                                {
                                        continue;
                                }
                                text = File.ReadAllText(entry, Encoding.UTF8);
                        }
                        catch
                        {
                                continue;
                        }
                        foreach (string service in ExtractInjectNames(text))
                        {
                                string replacement = "";
                                string reason;
                                if (string.Equals(service, "logger", StringComparison.Ordinal))
                                {
                                        reason = "cordis 4 里 ctx.logger 是上下文普通属性、不是可注入服务（ctx.get('logger') 为 undefined）";
                                }
                                else if (string.Equals(service, "webRuntime", StringComparison.Ordinal)
                                        && SemverUtil.Satisfies(r.DshVersion, ">=0.2.1-alpha.1"))
                                {
                                        reason = "DSH " + r.DshVersion + " 起 Web profile 不再提供 webRuntime，改由 web-startup 行提供 webStartup";
                                        replacement = "webStartup";
                                }
                                else if (string.Equals(service, "invariants", StringComparison.Ordinal)
                                        && SemverUtil.Satisfies(r.DshVersion, ">=0.2.0-rc.2"))
                                {
                                        reason = "@deepseek-ai/dsh-invariants 与 invariants 服务自 DSH 0.2.0-rc.2 起已整包删除";
                                }
                                else
                                {
                                        continue;
                                }
                                string key = entry.ToLowerInvariant() + "|" + service;
                                if (!seen.Add(key))
                                {
                                        continue;
                                }
                                r.InjectFixes.Add(new DshInjectFix
                                {
                                        File = entry,
                                        Service = service,
                                        Replacement = replacement,
                                        RowId = rowId,
                                        Module = moduleName
                                });
                                string fixHint = replacement.Length > 0
                                        ? "把入口 inject 里的 " + service + " 改名为 " + replacement + "（改前自动备份 .bak-compat-*）"
                                        : "从入口 inject 里删除 " + service + "（改前自动备份 .bak-compat-*）";
                                r.Add(DshCompatLevel.Error, "插件", "插件行 inject 永不解析: " + rowId + " → " + service,
                                        "行 id " + rowId + "（模块 " + moduleName + "）的入口 " + entry
                                        + " 把 " + service + " 列在 inject 中：" + reason
                                        + "，该行会一直 pending、插件永不生效（--dump-config 不报错）", true, fixHint);
                        }
                }
                log?.Invoke("[兼容] inject 检查: dump 行 " + dump.Rows.Count + " 个，定位入口 " + resolved
                        + " 个，问题 " + r.InjectFixes.Count + " 项");
        }

        /// <summary>安全读取 JSON 字符串标量；数组/对象/数字返回 null 而不抛异常。</summary>
        private static string? NodeString(JsonNode? node)
        {
                if (node is not JsonValue value)
                {
                        return null;
                }
                try
                {
                        string s = value.GetValue<string>();
                        return string.IsNullOrWhiteSpace(s) ? null : s;
                }
                catch
                {
                        return null;
                }
        }

        /// <summary>
        /// 读取 bundle 声明的补丁文件（含约定 cordis.patch.yml）里的 id：
        /// insert 块内插入的 id，与顶层裸 - id:（只覆盖已有行、不插入新行）。
        /// </summary>
        public static DshBundlePatchIds BundlePatchIds(string packageDir)
        {
                DshBundlePatchIds res = new DshBundlePatchIds();
                if (string.IsNullOrEmpty(packageDir) || !Directory.Exists(packageDir))
                {
                        return res;
                }
                List<string> files = new List<string>();
                JsonNode? manifest = TryReadManifest(Path.Combine(packageDir, "package.json"));
                JsonNode? patchNode = null;
                try
                {
                        patchNode = manifest?["dsh"]?["bundle"]?["patch"];
                }
                catch
                {
                        patchNode = null;
                }
                if (patchNode is JsonArray patchArray)
                {
                        foreach (JsonNode? node in patchArray)
                        {
                                string? rel = NodeString(node);
                                if (!string.IsNullOrWhiteSpace(rel))
                                {
                                        files.Add(Path.Combine(packageDir, rel!));
                                }
                        }
                }
                else
                {
                        string? declared = NodeString(patchNode);
                        if (!string.IsNullOrWhiteSpace(declared))
                        {
                                files.Add(Path.Combine(packageDir, declared!));
                        }
                }
                string conventional = Path.Combine(packageDir, "cordis.patch.yml");
                if (File.Exists(conventional) && !files.Contains(conventional, StringComparer.OrdinalIgnoreCase))
                {
                        files.Add(conventional);
                }
                foreach (string file in files)
                {
                        if (!File.Exists(file))
                        {
                                continue;
                        }
                        try
                        {
                                bool inInsert = false;
                                foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                                {
                                        if (Regex.IsMatch(raw, @"^-\s*insert\s*:"))
                                        {
                                                inInsert = true;
                                                continue;
                                        }
                                        if (Regex.IsMatch(raw, @"^-\s"))
                                        {
                                                inInsert = false;
                                        }
                                        Match m = Regex.Match(raw, @"^\s*-\s*id:\s*(?<id>.+?)\s*$");
                                        if (!m.Success)
                                        {
                                                continue;
                                        }
                                        string id = m.Groups["id"].Value.Trim().Trim('\'', '"');
                                        if (id.Length == 0)
                                        {
                                                continue;
                                        }
                                        if (inInsert)
                                        {
                                                if (!res.InsertIds.Contains(id, StringComparer.Ordinal))
                                                {
                                                        res.InsertIds.Add(id);
                                                }
                                        }
                                        else if (Regex.IsMatch(raw, @"^-\s*id:"))
                                        {
                                                if (!res.BareIds.Contains(id, StringComparer.Ordinal))
                                                {
                                                        res.BareIds.Add(id);
                                                }
                                        }
                                }
                        }
                        catch
                        {
                        }
                }
                return res;
        }

        /// <summary>
        /// 检出静默失效的 bundle 补丁：顶层裸 - id: X 只覆盖已有行，绝不会插入新行，
        /// 且 id 不存在时 DSH 连 stderr 都不报（dsh-rea 的真实案例）。判据：
        /// 该文件里的裸 id X 既不在合成树中，也不在该文件 insert 块的 id 集合里。
        /// </summary>
        private static void AuditBundlePatchRows(DshCompatReport r, DumpData dump, Action<string>? log)
        {
                if (!dump.Ran)
                {
                        return;
                }
                int suspicious = 0;
                foreach (DshPluginInfo info in r.Plugins.Where((DshPluginInfo x) => x.Installed && x.IsBundle && x.Dir.Length > 0))
                {
                        DshBundlePatchIds ids = BundlePatchIds(info.Dir);
                        foreach (string id in ids.BareIds)
                        {
                                if (dump.EntryIds.Contains(id))
                                {
                                        continue;
                                }
                                if (ids.InsertIds.Contains(id, StringComparer.Ordinal))
                                {
                                        continue;
                                }
                                suspicious++;
                                r.Add(DshCompatLevel.Warning, "补丁", "补丁裸 id 从未插入新行: " + id,
                                        "bundle " + info.Name + " 的补丁文件用顶层 - id: " + id
                                        + " 定义了一行，但该 id 既不在合成树（--dump-config）里，也不在该文件的 insert 块里；"
                                        + "裸 - id: 只覆盖已有行，新增行必须写成 - insert: 再在内层列 - id:（DSH 对此静默通过）",
                                        false, "把该条目包成 - insert: 块（内层 - id:/name: 缩进）");
                        }
                }
                log?.Invoke("[兼容] bundle 补丁裸 id 检查: 可疑 " + suspicious + " 条");
        }

        /// <summary>按 inject 审计结论改写入口文件：删除永不解析的服务名（webRuntime 改名为 webStartup）。</summary>
        private static string RepairInjectFixes(DshCompatReport report, Action<string>? log)
        {
                int files = 0;
                int changed = 0;
                int failed = 0;
                foreach (IGrouping<string, DshInjectFix> group in report.InjectFixes.GroupBy((DshInjectFix f) => f.File, StringComparer.OrdinalIgnoreCase))
                {
                        string path = group.Key;
                        try
                        {
                                FileInfo fi = new FileInfo(path);
                                if (!fi.Exists || fi.Length > 8000000)
                                {
                                        failed++;
                                        continue;
                                }
                                string text = File.ReadAllText(path, Encoding.UTF8);
                                string original = text;
                                foreach (DshInjectFix fix in group)
                                {
                                        text = RemoveFromInjectService(text, fix.Service, fix.Replacement, out int n);
                                        changed += n;
                                }
                                if (string.Equals(text, original, StringComparison.Ordinal))
                                {
                                        continue;
                                }
                                string backup = path + ".bak-compat-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                                File.Copy(path, backup, true);
                                File.WriteAllText(path, text, new UTF8Encoding(false));
                                files++;
                                log?.Invoke("[兼容修复] inject 已改写: " + path + "（备份 " + Path.GetFileName(backup) + "）");
                        }
                        catch (Exception ex)
                        {
                                failed++;
                                log?.Invoke("[兼容修复] inject 改写失败: " + path + " —— " + ex.Message);
                        }
                }
                return "inject 修复: 改写 " + files + " 个入口文件（处理 " + changed + " 处）"
                        + (failed > 0 ? "，失败 " + failed : "") + "；改前已备份 .bak-compat-*";
        }

}

/// <summary>一条可自动修复的 inject 服务名：入口文件、服务名与改法。</summary>
public sealed class DshInjectFix
{
        public string File = "";
        public string Service = "";
        /// <summary>非空表示"改名"，为空表示"删除"。</summary>
        public string Replacement = "";
        public string RowId = "";
        public string Module = "";
}

