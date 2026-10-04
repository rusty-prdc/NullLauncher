using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NullLauncher.Minecraft;

public sealed class Library
{
    public string Name { get; set; } = "";
    public string? Url { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public List<Rule> Rules { get; set; } = new();
    public List<string> Natives { get; set; } = new();
    public Dictionary<string, string> Extract { get; set; } = new();
    public JsonObject? Raw { get; set; }
    public bool IsNative { get; set; }
    public string? Classifier { get; set; }

    /// <summary>Относительный путь файла библиотеки: com/mojang/brigadier/1.0.18/brigadier-1.0.18.jar</summary>
    public string RelativePath
    {
        get
        {
            var parts = Name.Split(':');
            if (parts.Length < 3) return "";
            var (g, a, v) = (parts[0], parts[1], parts[2]);
            var classifier = parts.Length > 3 ? parts[3] : null;
            var file = $"{a}-{v}{(classifier is not null ? "-" + classifier : "")}.jar";
            return $"{g.Replace('.', '/')}/{a}/{v}/{file}";
        }
    }
}

public sealed class Rule
{
    public string Action { get; set; } = "allow";
    public string? OsName { get; set; }
    public string? OsArch { get; set; }
    public string? OsVersion { get; set; }
    /// <summary>Условия вида features: {is_demo_user:true}, {has_custom_resolution:true} и т.п.</summary>
    public Dictionary<string, bool> Features { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Disallowed { get; set; }
}

public sealed class ArgumentSpec
{
    /// <summary>
    /// Готовые элементы аргумента. В формате Mojang каждый элемент массива — один аргумент,
    /// пробелы внутри него значимы (например Fabric: «-DFabricMcEmu= net.minecraft.client.main.Main »),
    /// поэтому строку нельзя резать по пробелам.
    /// </summary>
    public List<string> Parts { get; set; } = new();
    public List<Rule> Rules { get; set; } = new();
    public string Value => string.Join(' ', Parts);
}

/// <summary>
/// Разобранный version JSON Minecraft (клиент). Понимает и старый формат "minecraftArguments",
/// и новый "arguments" с правилами.
/// </summary>
public sealed class VersionNode
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "release";
    public DateTime ReleaseTime { get; set; }
    public string MainClass { get; set; } = "";
    public string? MinecraftArguments { get; set; }
    public List<ArgumentSpec> GameArgs { get; set; } = new();
    public List<ArgumentSpec> JvmArgs { get; set; } = new();
    public List<Library> Libraries { get; set; } = new();
    public string? AssetIndexId { get; set; }
    public string? Assets { get; set; }
    public string? AssetIndexUrl { get; set; }
    public string? AssetIndexSha1 { get; set; }
    public long AssetIndexSize { get; set; }
    public int AssetCount { get; set; }
    public string? ClientJarUrl { get; set; }
    public string? ClientJarSha1 { get; set; }
    public long ClientJarSize { get; set; }
    /// <summary>Требуемая Java из version json; 0 — в этом профиле не указана (наследуется или определяется по версии).</summary>
    public int JavaMajor { get; set; }
    public string? LoggingUrl { get; set; }
    public string? LoggingSha1 { get; set; }
    /* аргумент вида -Dlog4j.configurationFile=${path} — подставляется при запуске */
    public string? LoggingArgument { get; set; }
    public string? InheritsFrom { get; set; }
    public JsonObject Raw { get; set; } = new();
    public string FilePath { get; set; } = "";
    public List<string> Downloads { get; set; } = new();

    public static VersionNode Load(string path)
    {
        var node = new VersionNode { FilePath = path };
        var doc = JsonDocument.Parse(File.ReadAllText(path));
        node.Raw = doc.RootElement.Clone().Deserialize<JsonObject>(Core.IpcRouter.JsonOptions) ?? new JsonObject();
        ParseInto(doc.RootElement, node);
        return node;
    }

    private static void ParseInto(JsonElement root, VersionNode v)
    {
        v.Id = Get(root, "id") ?? Path.GetFileNameWithoutExtension(v.FilePath);
        v.Type = Get(root, "type") ?? "release";
        if (root.TryGetProperty("releaseTime", out var rt) && DateTime.TryParse(rt.GetString(), out var d))
            v.ReleaseTime = d.ToUniversalTime();
        v.MainClass = Get(root, "mainClass") ?? "";
        v.MinecraftArguments = Get(root, "minecraftArguments");
        v.InheritsFrom = Get(root, "inheritsFrom");
        if (root.TryGetProperty("assetIndex", out var ai))
        {
            v.AssetIndexId = Get(ai, "id");
            v.AssetIndexUrl = Get(ai, "url");
            v.AssetIndexSha1 = Get(ai, "sha1");
            if (ai.TryGetProperty("totalSize", out var ts)) v.AssetIndexSize = ts.GetInt64();
        }
        v.Assets = Get(root, "assets");
        if (root.TryGetProperty("downloads", out var dl) && dl.TryGetProperty("client", out var client))
        {
            v.ClientJarUrl = Get(client, "url");
            v.ClientJarSha1 = Get(client, "sha1");
            if (client.TryGetProperty("size", out var sz)) v.ClientJarSize = sz.GetInt64();
        }
        if (root.TryGetProperty("javaVersion", out var jv) &&
            jv.TryGetProperty("majorVersion", out var mv))
            v.JavaMajor = mv.GetInt32();
        if (root.TryGetProperty("logging", out var lg) &&
            lg.TryGetProperty("client", out var lc))
        {
            // argument — это шаблон JVM-аргумента, сам URL лог-конфига лежит в file.url
            if (lc.TryGetProperty("argument", out var larg))
                v.LoggingArgument = larg.GetString();
            if (lc.TryGetProperty("file", out var lf))
            {
                v.LoggingUrl = Get(lf, "url");
                v.LoggingSha1 = Get(lf, "sha1");
            }
        }

        // аргументы
        if (root.TryGetProperty("arguments", out var args))
        {
            if (args.TryGetProperty("game", out var g)) v.GameArgs = ParseArgs(g);
            if (args.TryGetProperty("jvm", out var j)) v.JvmArgs = ParseArgs(j);
        }

        if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in libs.EnumerateArray())
            {
                var lib = new Library
                {
                    Name = Get(l, "name") ?? "",
                    Raw = l.Clone().Deserialize<JsonObject>(Core.IpcRouter.JsonOptions),
                };
                if (l.TryGetProperty("rules", out var rules)) lib.Rules = ParseRules(rules);
                if (l.TryGetProperty("downloads", out var ldl))
                {
                    if (ldl.TryGetProperty("artifact", out var art))
                    {
                        lib.Url = Get(art, "url");
                        lib.Sha1 = Get(art, "sha1");
                        if (art.TryGetProperty("size", out var s2)) lib.Size = s2.GetInt64();
                    }
                    if (ldl.TryGetProperty("classifiers", out var cls))
                    {
                        foreach (var c in cls.EnumerateObject())
                        {
                            if (c.Name == "natives-windows" || c.Name == "natives-windows-64" || c.Name == "natives-windows-32")
                            {
                                lib.Natives.Add(c.Name);
                                var nat = c.Value;
                                if (nat.TryGetProperty("url", out var u)) lib.Url ??= u.GetString();
                                if (nat.TryGetProperty("sha1", out var sh)) lib.Sha1 ??= sh.GetString();
                                if (nat.TryGetProperty("size", out var sz)) lib.Size = sz.GetInt64();
                                lib.IsNative = true;
                                if (c.Name == "natives-windows-64") lib.Classifier = "natives-windows-64";
                                if (c.Name == "natives-windows-32") lib.Classifier = "natives-windows-32";
                                if (c.Name == "natives-windows") lib.Classifier = "natives-windows";
                            }
                        }
                    }
                }
                if (l.TryGetProperty("natives", out var natObj))
                {
                    foreach (var c in natObj.EnumerateObject())
                    {
                        if (c.Value.GetString()?.Contains("windows") == true)
                        {
                            lib.IsNative = true;
                            lib.Natives.Add(c.Name);
                            lib.Classifier = c.Value.GetString();
                        }
                    }
                    if (l.TryGetProperty("extract", out var ex))
                        foreach (var e2 in ex.EnumerateObject())
                            lib.Extract[e2.Name] = e2.Value.GetString() ?? "";
                }
                if (string.IsNullOrEmpty(lib.Name)) continue;
                v.Libraries.Add(lib);
            }
        }
    }

    private static string? Get(JsonElement e, string key)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static List<ArgumentSpec> ParseArgs(JsonElement arr)
    {
        var list = new List<ArgumentSpec>();
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(new ArgumentSpec { Parts = { item.GetString() ?? "" } });
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var spec = new ArgumentSpec();
                if (item.TryGetProperty("value", out var val))
                {
                    if (val.ValueKind == JsonValueKind.String)
                    {
                        spec.Parts.Add(val.GetString() ?? "");
                    }
                    else if (val.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var x in val.EnumerateArray())
                            if (x.ValueKind == JsonValueKind.String) spec.Parts.Add(x.GetString() ?? "");
                    }
                }
                if (item.TryGetProperty("rules", out var rules)) spec.Rules = ParseRules(rules);
                list.Add(spec);
            }
        }
        return list;
    }

    private static List<Rule> ParseRules(JsonElement rules)
    {
        var list = new List<Rule>();
        if (rules.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in rules.EnumerateArray())
        {
            var rule = new Rule { Action = Get(r, "action") ?? "allow" };
            if (r.TryGetProperty("os", out var os))
            {
                rule.OsName = Get(os, "name");
                rule.OsArch = Get(os, "arch");
                rule.OsVersion = Get(os, "version");
            }
            if (r.TryGetProperty("features", out var feat) && feat.ValueKind == JsonValueKind.Object)
            {
                foreach (var f in feat.EnumerateObject())
                {
                    if (f.Value.ValueKind == JsonValueKind.True || f.Value.ValueKind == JsonValueKind.False)
                        rule.Features[f.Name] = f.Value.GetBoolean();
                }
            }
            list.Add(rule);
        }
        return list;
    }

    /// <summary>
    /// Проверка правил Mojang. features — реальные значения возможностей лаунчера
    /// (is_demo_user, has_custom_resolution, is_quick_play_multiplayer…). Раньше features
    /// игнорировались, поэтому всегда подставлялся аргумент --demo (лимит 100 минут в игре).
    /// </summary>
    public bool RulesAllow(IEnumerable<Rule> rules, IReadOnlyDictionary<string, bool>? features = null)
    {
        var list = rules.ToList();
        if (list.Count == 0) return true;
        var allowed = false;
        foreach (var r in list)
        {
            var matches = MatchesOs(r) && MatchesFeatures(r, features);
            if (r.Action == "disallow")
            {
                if (matches) return false;
                continue;
            }
            if (matches) allowed = true;
        }
        return allowed;
    }

    private static bool MatchesFeatures(Rule r, IReadOnlyDictionary<string, bool>? features)
    {
        if (r.Features.Count == 0) return true;
        foreach (var (key, expected) in r.Features)
        {
            var actual = features is not null && features.TryGetValue(key, out var v) && v;
            if (actual != expected) return false;
        }
        return true;
    }

    private static bool MatchesOs(Rule r)
    {
        if (r.OsName is not null && !string.Equals(r.OsName, "windows", StringComparison.OrdinalIgnoreCase))
            return false;

        if (r.OsArch is not null)
        {
            // Mojang помечает 32-битные аргументы как os.arch=x86: на 64-битной системе они не применяются.
            var want = r.OsArch.Equals("amd64", StringComparison.OrdinalIgnoreCase) ? "x86_64" : r.OsArch;
            if (!string.Equals(want, ActualArch, StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (r.OsVersion is not null &&
            !System.Text.RegularExpressions.Regex.IsMatch(Environment.OSVersion.Version.ToString(), r.OsVersion))
            return false;
        return true;
    }

    private static string ActualArch =>
        !Environment.Is64BitOperatingSystem
            ? "x86"
            : System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
              System.Runtime.InteropServices.Architecture.Arm64
                ? "arm64"
                : "x86_64";

    /// <summary>Библиотеки, подходящие под ОС (включая нативные).</summary>
    public List<Library> ApplicableLibraries()
        => Libraries.Where(l => RulesAllow(l.Rules)).ToList();

    public IEnumerable<string> GameArgsFor(Dictionary<string, string> map, IReadOnlyDictionary<string, bool>? features = null)
        => EmitArgs(GameArgs, map, features);

    public IEnumerable<string> JvmArgsFor(Dictionary<string, string> map, IReadOnlyDictionary<string, bool>? features = null)
        => EmitArgs(JvmArgs, map, features);

    private IEnumerable<string> EmitArgs(List<ArgumentSpec> specs, Dictionary<string, string> map,
        IReadOnlyDictionary<string, bool>? features)
    {
        foreach (var a in specs)
        {
            if (!RulesAllow(a.Rules, features)) continue;

            // 1. шаблон → токены. Пробелы внутри значения системного свойства (-D…) значимы
            //    (Fabric: «-DFabricMcEmu= net.minecraft.client.main.Main »), в остальных случаях
            //    строка может содержать несколько аргументов (Mojang: «--width N --height M»).
            var tokens = new List<string>();
            foreach (var part in a.Parts)
            {
                if (part.StartsWith("-D", StringComparison.Ordinal)) tokens.Add(part);
                else tokens.AddRange(part.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            }
            if (tokens.Count == 0) continue;

            // 2. подстановка ${...}; неизвестный placeholder = аргумент не передаём
            var values = new List<string>();
            var unresolved = false;
            foreach (var t in tokens)
            {
                var v = Substitute(t, map);
                if (v.Contains("${", StringComparison.Ordinal)) { unresolved = true; break; }
                values.Add(v);
            }
            if (unresolved) continue;
            if (values.All(v => v.Length == 0)) continue;

            // пустое значение (например clientId/xuid у офлайн-аккаунта) сохраняем:
            // иначе флаг остаётся без значения и «съедает» следующий аргумент
            foreach (var v in values) yield return v;
        }
    }

    /// <summary>Подставляет значения ${placeholder}.</summary>
    private static string Substitute(string token, Dictionary<string, string> map)
    {
        var v = token;
        foreach (var (k, val) in map) v = v.Replace("${" + k + "}", val);
        return v;
    }

    /// <summary>
    /// Собирает цепочку наследования (базовый Minecraft → загрузчик) в один профиль.
    /// Профили Fabric/Quilt/Forge/NeoForge содержат только свои аргументы и наследуют остальные
    /// у версии Minecraft, поэтому без слияния в игру не попадали --username/--accessToken и -cp.
    /// </summary>
    public static VersionNode MergeChain(IReadOnlyList<VersionNode> baseToChild)
    {
        if (baseToChild.Count == 0) throw new ArgumentException("Пустая цепочка версий", nameof(baseToChild));
        if (baseToChild.Count == 1) return baseToChild[0];

        var top = baseToChild[^1];
        var merged = new VersionNode
        {
            Id = top.Id,
            Type = top.Type != "release" ? top.Type : baseToChild[0].Type,
            ReleaseTime = top.ReleaseTime != default ? top.ReleaseTime : baseToChild[0].ReleaseTime,
            FilePath = top.FilePath,
            Raw = top.Raw,
            InheritsFrom = null,
            MainClass = FirstNonEmpty(baseToChild, n => n.MainClass) ?? "",
            MinecraftArguments = FirstNonEmpty(baseToChild, n => n.MinecraftArguments),
            AssetIndexId = FirstNonEmpty(baseToChild, n => n.AssetIndexId),
            AssetIndexUrl = FirstNonEmpty(baseToChild, n => n.AssetIndexUrl),
            AssetIndexSha1 = FirstNonEmpty(baseToChild, n => n.AssetIndexSha1),
            Assets = FirstNonEmpty(baseToChild, n => n.Assets),
            ClientJarUrl = FirstNonEmpty(baseToChild, n => n.ClientJarUrl),
            ClientJarSha1 = FirstNonEmpty(baseToChild, n => n.ClientJarSha1),
            LoggingUrl = FirstNonEmpty(baseToChild, n => n.LoggingUrl),
            LoggingSha1 = FirstNonEmpty(baseToChild, n => n.LoggingSha1),
            LoggingArgument = FirstNonEmpty(baseToChild, n => n.LoggingArgument),
            JavaMajor = baseToChild.Select(n => n.JavaMajor).LastOrDefault(j => j > 0),
        };
        merged.AssetIndexSize = baseToChild.Select(n => n.AssetIndexSize).LastOrDefault(s => s > 0);
        merged.ClientJarSize = baseToChild.Select(n => n.ClientJarSize).LastOrDefault(s => s > 0);

        foreach (var n in baseToChild)
        {
            merged.GameArgs.AddRange(n.GameArgs);
            merged.JvmArgs.AddRange(n.JvmArgs);
            merged.Libraries.AddRange(n.Libraries);
        }
        return merged;
    }

    private static string? FirstNonEmpty(IReadOnlyList<VersionNode> nodes, Func<VersionNode, string?> pick)
    {
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            var v = pick(nodes[i]);
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }
}
