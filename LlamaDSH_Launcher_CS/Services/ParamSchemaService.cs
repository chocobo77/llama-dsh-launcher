using System.IO;
using System.Text;
using LlamaDSH_Launcher.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LlamaDSH_Launcher.Services;

public class SchemaMeta
{
	public int Schema { get; set; }

	public string Adapt { get; set; } = "";

	public string? Notes { get; set; }
}

public class ParamGroupDef
{
	public string Id { get; set; } = "";

	public string Title { get; set; } = "";

	public int Order { get; set; }
}

/// <summary>基础层（内置/exe旁）文档结构。</summary>
public class ParamSchemaDoc
{
	public SchemaMeta Meta { get; set; } = new();

	public List<ParamGroupDef> Groups { get; set; } = new();

	/// <summary>要删除的内置参数 flag 列表（仅 override 层有意义）。</summary>
	public List<string>? Remove { get; set; }

	public List<ParamDefinition> Params { get; set; } = new();
}

/// <summary>override 层的参数补丁：所有字段可空，非空才覆盖。</summary>
public class ParamOverride
{
	public string Flag { get; set; } = "";

	public string? Label { get; set; }

	public string? Role { get; set; }

	public ParamType? Type { get; set; }

	public string? Default { get; set; }

	public string? Group { get; set; }

	public bool? Enabled { get; set; }

	public bool? Advanced { get; set; }

	public List<string>? Options { get; set; }

	public string? Tooltip { get; set; }

	public List<ParamRule>? Requires { get; set; }

	public List<string>? Conflicts { get; set; }

	public List<string>? Implies { get; set; }

	public List<string>? Suggests { get; set; }

	public ParamValidate? Validate { get; set; }

	public List<string>? Aliases { get; set; }

	public bool? Hidden { get; set; }

	public bool? Deprecated { get; set; }
}

/// <summary>override 层文档结构。</summary>
public class ParamOverrideDoc
{
	public List<ParamGroupDef>? Groups { get; set; }

	public List<string>? Remove { get; set; }

	public List<ParamOverride>? Params { get; set; }
}

public class ParamSchemaLoadResult
{
	public List<ParamDefinition> Defs { get; set; } = new();

	public List<ParamGroupDef> Groups { get; set; } = new();

	public List<string> Errors { get; } = new();

	public List<string> Warnings { get; } = new();

	/// <summary>实际生效的基础层来源："exe旁" / "内置"。</summary>
	public string Source { get; set; } = "";

	/// <summary>exe 旁配置损坏已回退内置默认。</summary>
	public bool Fallback { get; set; }
}

/// <summary>
/// 参数模式模块：三层层叠加载（内置资源 → exe旁 llama_params.yml → 用户 override），
/// 负责解析、合并、校验，以及旧 profile 的 flag 别名迁移。
/// </summary>
public static class ParamSchemaService
{
	public const string ExeFileName = "llama_params.yml";

	public static string ExePath
	{
		get
		{
			// 单文件发布时 AppContext.BaseDirectory 指向 bundle 自解压临时目录，
			// 用 Environment.ProcessPath 定位 exe 真实所在目录，让用户能直接编辑旁边的 llama_params.yml
			string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
			return Path.Combine(string.IsNullOrEmpty(exeDir) ? AppContext.BaseDirectory : exeDir, ExeFileName);
		}
	}

	public static string OverrideDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llamadsh");

	public static string OverridePath => Path.Combine(OverrideDir, "params_override.yml");

	private const string EmbeddedResourceName = "LlamaDSH_Launcher.Resources.llama_params.yml";

	public static ParamSchemaLoadResult Load()
	{
		ParamSchemaLoadResult result = new();
		string embedded = ReadEmbedded();
		ParamSchemaDoc? embDoc = ParseDoc(embedded, out string embErr);
		if (embDoc == null)
		{
			result.Errors.Add("内置默认配置解析失败(严重): " + embErr);
			return EmptyResult(result);
		}

		// 第 1 层：exe 旁配置（缺失则从内置资源提取；内置 schema 更高则自动升级并备份）
		ParamSchemaDoc? doc = null;
		try
		{
			if (!File.Exists(ExePath))
			{
				File.WriteAllText(ExePath, embedded, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				doc = embDoc;
			}
			else
			{
				string exeYml = File.ReadAllText(ExePath);
				doc = ParseDoc(exeYml, out string exeErr);
				if (doc == null)
				{
					result.Fallback = true;
					result.Errors.Add("exe旁 llama_params.yml 解析失败: " + exeErr + "\n→ 本次已回退内置默认配置，请修复或删除该文件。");
				}
				else if (embDoc.Meta.Schema > doc.Meta.Schema)
				{
					try
					{
						File.Copy(ExePath, ExePath + ".bak", overwrite: true);
						File.WriteAllText(ExePath, embedded, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
						result.Warnings.Add("参数配置已随启动器升级: v" + doc.Meta.Schema + " → v" + embDoc.Meta.Schema + "（旧文件备份为 llama_params.yml.bak，可从中找回自定义改动）");
					}
					catch (Exception upEx)
					{
						result.Warnings.Add("参数配置自动升级失败(" + upEx.Message + ")，继续使用本地 v" + doc.Meta.Schema);
					}
					doc = embDoc;
				}
			}
		}
		catch (Exception ex)
		{
			result.Warnings.Add("读取 exe 旁配置失败(" + ex.Message + ")，使用内置默认。");
		}

		if (doc == null)
		{
			doc = embDoc;
			result.Source = "内置";
		}
		else
		{
			result.Source = "exe旁";
		}

		// 第 2 层：用户 override（只写差异；解析失败仅告警不阻断）
		try
		{
			if (File.Exists(OverridePath))
			{
				ParamOverrideDoc? ov = ParseOverride(File.ReadAllText(OverridePath), out string ovErr);
				if (ov == null)
				{
					result.Errors.Add("override 配置解析失败，已忽略该层: " + OverridePath + "\n" + ovErr);
				}
				else
				{
					ApplyOverride(doc, ov, result);
				}
			}
		}
		catch (Exception ex)
		{
			result.Errors.Add("读取 override 失败，已忽略该层: " + ex.Message);
		}

		Finalize(doc, result);
		try
		{
			AppLog.WriteInfo($"[参数模块] source={result.Source} defs={result.Defs.Count} groups={result.Groups.Count} fallback={result.Fallback} exePath={ExePath} errors=[{string.Join("; ", result.Errors)}] warnings=[{string.Join("; ", result.Warnings)}]");
		}
		catch { }
		return result;
	}

	private static ParamSchemaLoadResult EmptyResult(ParamSchemaLoadResult result)
	{
		result.Source = "内置(空)";
		result.Groups.Add(new ParamGroupDef { Id = "basics", Title = "基础设置", Order = 0 });
		return result;
	}

	private static string ReadEmbedded()
	{
		using Stream? s = typeof(ParamSchemaService).Assembly.GetManifestResourceStream(EmbeddedResourceName);
		if (s == null)
		{
			throw new InvalidOperationException("内嵌资源缺失: " + EmbeddedResourceName);
		}
		using StreamReader r = new(s, Encoding.UTF8);
		return r.ReadToEnd();
	}

	private static IDeserializer BuildDeserializer()
	{
		return new DeserializerBuilder()
			.WithNamingConvention(CamelCaseNamingConvention.Instance)
			.WithEnumNamingConvention(NullNamingConvention.Instance)
			.IgnoreUnmatchedProperties()
			.Build();
	}

	private static ParamSchemaDoc? ParseDoc(string yml, out string error)
	{
		error = "";
		try
		{
			ParamSchemaDoc? doc = BuildDeserializer().Deserialize<ParamSchemaDoc>(yml);
			if (doc == null || doc.Params.Count == 0)
			{
				error = "未解析到任何参数(params 为空)";
				return null;
			}
			return doc;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return null;
		}
	}

	private static ParamOverrideDoc? ParseOverride(string yml, out string error)
	{
		error = "";
		try
		{
			return BuildDeserializer().Deserialize<ParamOverrideDoc>(yml) ?? new ParamOverrideDoc();
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return null;
		}
	}

	private static void ApplyOverride(ParamSchemaDoc doc, ParamOverrideDoc ov, ParamSchemaLoadResult result)
	{
		// 组合并
		if (ov.Groups != null)
		{
			foreach (ParamGroupDef g in ov.Groups)
			{
				ParamGroupDef? base_ = doc.Groups.FirstOrDefault(x => x.Id == g.Id);
				if (base_ == null)
				{
					doc.Groups.Add(new ParamGroupDef { Id = g.Id, Title = string.IsNullOrEmpty(g.Title) ? g.Id : g.Title, Order = g.Order });
				}
				else
				{
					if (!string.IsNullOrEmpty(g.Title))
					{
						base_.Title = g.Title;
					}
					if (g.Order != 0)
					{
						base_.Order = g.Order;
					}
				}
			}
		}

		// 删除参数
		if (ov.Remove != null)
		{
			foreach (string flag in ov.Remove)
			{
				ParamDefinition? victim = doc.Params.FirstOrDefault(p => p.Flag == flag || (p.Aliases != null && p.Aliases.Contains(flag)));
				if (victim != null)
				{
					doc.Params.Remove(victim);
				}
				else
				{
					result.Warnings.Add("override remove: 未找到参数 --" + flag);
				}
			}
		}

		// 参数补丁
		if (ov.Params == null)
		{
			return;
		}
		foreach (ParamOverride op in ov.Params)
		{
			if (string.IsNullOrEmpty(op.Flag))
			{
				result.Warnings.Add("override 参数缺少 flag，已跳过");
				continue;
			}
			ParamDefinition? target = doc.Params.FirstOrDefault(p => p.Flag == op.Flag || (p.Aliases != null && p.Aliases.Contains(op.Flag)));
			if (target == null)
			{
				// 新增参数
				target = new ParamDefinition
				{
					Flag = op.Flag,
					Label = op.Label ?? op.Flag,
					Type = op.Type ?? ParamType.String,
					DefaultValue = op.Default ?? "",
					Group = op.Group ?? "custom",
					Enabled = op.Enabled ?? false,
					IsAdvanced = op.Advanced ?? false,
					Options = op.Options,
					Tooltip = op.Tooltip ?? "",
					Role = op.Role,
					Requires = op.Requires,
					Conflicts = op.Conflicts,
					Implies = op.Implies,
					Suggests = op.Suggests,
					Validate = op.Validate,
					Aliases = op.Aliases,
					Hidden = op.Hidden ?? false,
					Deprecated = op.Deprecated ?? false
				};
				doc.Params.Add(target);
				continue;
			}
			if (op.Label != null) target.Label = op.Label;
			if (op.Role != null) target.Role = op.Role;
			if (op.Type != null) target.Type = op.Type.Value;
			if (op.Default != null) target.DefaultValue = op.Default;
			if (op.Group != null) target.Group = op.Group;
			if (op.Enabled != null) target.Enabled = op.Enabled.Value;
			if (op.Advanced != null) target.IsAdvanced = op.Advanced.Value;
			if (op.Options != null) target.Options = op.Options;
			if (op.Tooltip != null) target.Tooltip = op.Tooltip;
			if (op.Requires != null) target.Requires = op.Requires;
			if (op.Conflicts != null) target.Conflicts = op.Conflicts;
			if (op.Implies != null) target.Implies = op.Implies;
			if (op.Suggests != null) target.Suggests = op.Suggests;
			if (op.Validate != null) target.Validate = op.Validate;
			if (op.Aliases != null) target.Aliases = op.Aliases;
			if (op.Hidden != null) target.Hidden = op.Hidden.Value;
			if (op.Deprecated != null) target.Deprecated = op.Deprecated.Value;
		}
	}

	private static void Finalize(ParamSchemaDoc doc, ParamSchemaLoadResult result)
	{
		// 默认值 token 展开
		int cpu = Environment.ProcessorCount;
		foreach (ParamDefinition p in doc.Params)
		{
			if (p.DefaultValue is string s && s.Contains("$cpu"))
			{
				p.DefaultValue = s.Replace("$cpu", cpu.ToString());
			}
		}

		Dictionary<string, ParamDefinition> byFlag = new();
		foreach (ParamDefinition p in doc.Params)
		{
			if (byFlag.ContainsKey(p.Flag))
			{
				result.Errors.Add("参数 flag 重复: --" + p.Flag + "（保留第一个）");
				continue;
			}
			byFlag[p.Flag] = p;
		}

		// 组校验 + 补齐
		Dictionary<string, ParamGroupDef> groups = new();
		foreach (ParamGroupDef g in doc.Groups)
		{
			groups[g.Id] = g;
		}
		foreach (ParamDefinition p in doc.Params)
		{
			if (string.IsNullOrEmpty(p.Group))
			{
				p.Group = "custom";
			}
			if (!groups.ContainsKey(p.Group))
			{
				ParamGroupDef ng = new() { Id = p.Group, Title = p.Group, Order = 900 };
				groups[p.Group] = ng;
				doc.Groups.Add(ng);
				result.Warnings.Add("参数 --" + p.Flag + " 引用了未定义的组 '" + p.Group + "'，已自动创建");
			}
		}

		// 依赖引用校验
		foreach (ParamDefinition p in doc.Params)
		{
			if (p.Requires != null)
			{
				foreach (ParamRule r in p.Requires.Where(r => !byFlag.ContainsKey(r.Flag)).ToList())
				{
					result.Errors.Add("--" + p.Flag + " 的 requires 引用了不存在的参数 --" + r.Flag + "，该规则已忽略");
					p.Requires.Remove(r);
				}
			}
			if (p.Conflicts != null)
			{
				foreach (string c in p.Conflicts.Where(c => !byFlag.ContainsKey(c)).ToList())
				{
					result.Errors.Add("--" + p.Flag + " 的 conflicts 引用了不存在的参数 --" + c + "，该规则已忽略");
					p.Conflicts.Remove(c);
				}
			}
			if (p.Implies != null)
			{
				foreach (string c in p.Implies.Where(c => !byFlag.ContainsKey(c)).ToList())
				{
					result.Errors.Add("--" + p.Flag + " 的 implies 引用了不存在的参数 --" + c + "，该规则已忽略");
					p.Implies.Remove(c);
				}
			}
			if (p.Validate?.Leq != null && !byFlag.ContainsKey(p.Validate.Leq))
			{
				result.Errors.Add("--" + p.Flag + " 的 validate.leq 引用了不存在的参数 --" + p.Validate.Leq + "，该规则已忽略");
				p.Validate.Leq = null;
			}
		}

		// 排序：组 order 升序 → 组内保持 yaml 原顺序
		doc.Groups.Sort((a, b) => a.Order.CompareTo(b.Order));
		result.Groups = doc.Groups;
		result.Defs = doc.Params;
	}

	/// <summary>
	/// 旧 profile 的 ValueParams/BoolParams 按 flag 存键；llama.cpp 改名参数后，
	/// 通过 defs 的 aliases 把旧键的值迁移到新 flag（仅在目标键尚不存在时）。
	/// </summary>
	public static void MigrateProfileFlags(ProfileConfig c, List<ParamDefinition> defs)
	{
		Dictionary<string, ParamDefinition> byFlag = defs.GroupBy(d => d.Flag).ToDictionary(g => g.Key, g => g.First());
		foreach (string key in c.ValueParams.Keys.ToList())
		{
			if (byFlag.ContainsKey(key))
			{
				continue;
			}
			ParamDefinition? target = defs.FirstOrDefault(d => d.Aliases != null && d.Aliases.Contains(key));
			if (target != null && !c.ValueParams.ContainsKey(target.Flag))
			{
				c.ValueParams[target.Flag] = c.ValueParams[key];
				c.ValueParams.Remove(key);
			}
		}
		foreach (string key in c.BoolParams.Keys.ToList())
		{
			if (byFlag.ContainsKey(key))
			{
				continue;
			}
			ParamDefinition? target = defs.FirstOrDefault(d => d.Aliases != null && d.Aliases.Contains(key));
			if (target != null && !c.BoolParams.ContainsKey(target.Flag))
			{
				c.BoolParams[target.Flag] = c.BoolParams[key];
				c.BoolParams.Remove(key);
			}
		}
	}
}
