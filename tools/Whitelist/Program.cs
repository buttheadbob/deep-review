// DeepWhitelist: the Space Engineers script whitelist, built and checked by the game's own code.
//
// The game (MySandboxGame.InitModAPI) does, in this order:
//   1. MySpaceGameDefaultIlCompiler.InitIlCompiler -> MyVRageScriptingInternal.Initialize: MyScriptCompiler.Static
//      .AddReferencedAssemblies(netstandard, the game DLLs it names, System.Runtime, System.Collections),
//      .AddImplicitInGameNamespacesFromTypes(the types it names) and .AddConditionalCompilationSymbols(...).
//   2. MySandboxGame.GameCustomIlChecker (SpaceEngineersGame picks MySpaceGameDefaultIlChecker unless
//      MyFakes.USE_CUSTOM_IL_CHECKER_WHITELIST): InitIlChecker opens MyScriptCompiler.Static.Whitelist.OpenBatch() and
//      calls AllowDefaultNamespaces, AllowSandboxNamespaces and AllowSpaceEngineersNamespaces.
//   3. MySandboxGame.InitIlChecker: the in-game blacklist (OpenIngameBlacklistBatch().AddMembers(...)).
// This tool repeats those steps against the DedicatedServer64 assemblies. The reference names, implicit namespace types,
// symbols, registration steps and blacklist calls are read from the game's IL (ldstr/ldtoken/call operands); the
// registration methods themselves are the game's own, called by reflection. Nothing is written by hand.
//
// Scripts are then compiled the way the game compiles them: mods per Data/Scripts/<folder> (MyScriptManager.LoadScripts)
// after MyScriptManager.UpdateCompatibility (its usings prefix and replacement table, read from the game), PB scripts as
// written; both with the game's MyScriptCompiler.CreateCompilation (C# 6, the game's references and symbols), the game's
// WhitelistDiagnosticAnalyzer for the target (ModApi or Ingame) and the game's MyBlacklistSyntaxVisitor.
//
// usage: DeepWhitelist --ds <DedicatedServer64> --dump <dir>
//        DeepWhitelist --ds <DedicatedServer64> --jobs <jobs.txt> --out <result.json> [--dump <dir>]
// jobs.txt: lines "job<TAB>id<TAB>mod|ingame" followed by "file<TAB>repo-relative path<TAB>absolute path" lines.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DeepWhitelist;

internal static class Program
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static string _ds;

    private static int Main(string[] args)
    {
        var a = Args(args);
        if (!a.TryGetValue("ds", out _ds) || !Directory.Exists(_ds) || !(a.ContainsKey("dump") || a.ContainsKey("jobs") && a.ContainsKey("out")))
        {
            Console.Error.WriteLine("usage: DeepWhitelist --ds <DedicatedServer64> (--dump <dir> | --jobs <jobs.txt> --out <json> [--dump <dir>])");
            return 1;
        }
        _ds = Path.GetFullPath(_ds);
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        try
        {
            return Run(a);
        }
        catch (Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            Console.Error.WriteLine("DeepWhitelist failed: " + e);
            return 1;
        }
    }

    // The game's assemblies, and the versions of Roslyn and System.* it ships, come from DedicatedServer64.
    private static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string path = Path.Combine(_ds, new AssemblyName(e.Name).Name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }

    private static Dictionary<string, string> Args(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
            if (args[i].StartsWith("--", StringComparison.Ordinal))
                d[args[i].Substring(2)] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "";
        return d;
    }

    private static Type Need(Assembly asm, string name) => asm.GetType(name, true);

    private static MethodInfo NeedMethod(Type t, string name, BindingFlags flags) =>
        t.GetMethod(name, flags) ?? throw new InvalidOperationException(t.FullName + "." + name + " not found");

    private static int Run(Dictionary<string, string> a)
    {
        var info = new Dictionary<string, object> { ["ds"] = _ds };
        var scripting = Assembly.LoadFrom(Path.Combine(_ds, "VRage.Scripting.dll"));
        var sandboxGame = Assembly.LoadFrom(Path.Combine(_ds, "Sandbox.Game.dll"));
        var seGame = Assembly.LoadFrom(Path.Combine(_ds, "SpaceEngineers.Game.dll"));
        using (var sha = SHA256.Create())
            info["sandboxGameSha256"] = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(sandboxGame.Location))).Replace("-", "").ToLowerInvariant();
        var compilerType = Need(scripting, "VRage.Scripting.MyScriptCompiler");
        object compiler = compilerType.GetField("Static", AnyStatic).GetValue(null);
        object whitelist = compilerType.GetProperty("Whitelist").GetValue(compiler);

        // 1. MySpaceGameDefaultIlCompiler.InitIlCompiler, replayed from its IL.
        var initCompiler = NeedMethod(Need(seGame, "SpaceEngineers.Game.MySpaceGameDefaultIlCompiler"), "InitIlCompiler", AnyInstance);
        var ops = Il.Read(initCompiler).ToList();
        if (!ops.Any(o => o.Operand is MethodBase m && m.Name == "Initialize" && m.DeclaringType?.Name == "IVRageScripting"))
            throw new InvalidOperationException("InitIlCompiler no longer calls IVRageScripting.Initialize; this tool does not replay it");
        var strings = ops.Select(o => o.Operand).OfType<string>().ToList();
        var references = new List<string>();
        var referenceNotes = new List<string>();
        foreach (string text in strings.Where(s => s.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            references.Add(Path.Combine(_ds, text));
        if (!strings.Contains("netstandard"))
            throw new InvalidOperationException("InitIlCompiler no longer loads netstandard; this tool does not replay it");
        references.Insert(0, Assembly.Load("netstandard").Location);
        // The game adds System.Runtime and System.Collections when they are loaded in its AppDomain. Here they are loaded
        // the way the game's own DLLs reference them (the versions the binding redirects of SpaceEngineersDedicated.exe.config give).
        foreach (string facade in new[] { "System.Runtime", "System.Collections" }.Where(strings.Contains))
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == facade);
            if (loaded == null)
            {
                var wanted = references.Where(File.Exists).Select(r => { try { return Assembly.LoadFrom(r); } catch (Exception) { return null; } })
                    .Where(x => x != null).SelectMany(x => x.GetReferencedAssemblies()).FirstOrDefault(n => n.Name == facade);
                if (wanted != null) loaded = Assembly.Load(wanted);
            }
            if (loaded != null) references.Add(loaded.Location);
            referenceNotes.Add(facade + ": " + (loaded?.Location ?? "not referenced by the game's DLLs, so not added"));
        }
        if (references.Count < 10 || references.Any(r => !File.Exists(r)))
            throw new InvalidOperationException("InitIlCompiler's reference list could not be resolved: " + string.Join(", ", references.Where(r => !File.Exists(r))));
        var implicitTypes = ops.Where(o => o.Code == OpCodes.Ldtoken).Select(o => o.Operand).OfType<Type>().ToArray();
        var symbols = new List<string>();
        // GetPrefixedBranchName(): "BRANCH_" + the Steam beta branch, or "BRANCH_STABLE" on the default branch (no branch name).
        var branch = NeedMethod(initCompiler.DeclaringType, "GetPrefixedBranchName", AnyStatic);
        var branchStrings = Il.Read(branch).Select(o => o.Operand).OfType<string>().ToList();
        if (!branchStrings.Contains("BRANCH_") || !branchStrings.Contains("STABLE"))
            throw new InvalidOperationException("GetPrefixedBranchName changed; this tool does not replay it");
        symbols.Add("BRANCH_STABLE");
        foreach (string s in new[] { "STABLE", "VERSION_", "BUILD_" })
            if (!strings.Contains(s)) throw new InvalidOperationException("InitIlCompiler no longer sets the " + s + " symbol; this tool does not replay it");
        symbols.Add("STABLE");
        // MyFinalBuildConstants.APP_VERSION = (MyVersion)MyPerGameSettings.BasicGameInfo.GameVersion, which
        // SpaceEngineersGame.SetupBasicGameInfo sets to a constant; "VERSION_" + Minor and "BUILD_" + Build of (Version)APP_VERSION.
        int? gameVersion = null;
        object lastInt = null;
        foreach (var op in Il.Read(NeedMethod(Need(seGame, "SpaceEngineers.Game.SpaceEngineersGame"), "SetupBasicGameInfo", AnyStatic)))
        {
            if (op.Operand is int n) lastInt = n;
            else if (op.Operand is FieldInfo f && f.Name == "GameVersion" && lastInt is int v) { gameVersion = v; break; }
        }
        if (gameVersion == null) throw new InvalidOperationException("SpaceEngineersGame.SetupBasicGameInfo no longer sets GameVersion to a constant");
        var constants = Assembly.LoadFrom(Path.Combine(_ds, "VRage.Game.dll")).GetType("VRage.Game.MyFinalBuildConstants", true);
        var myVersion = constants.GetField("APP_VERSION", AnyStatic).FieldType;
        object appVersion = myVersion.GetMethods(AnyStatic).First(m => m.Name == "op_Implicit" && m.ReturnType == myVersion && m.GetParameters()[0].ParameterType == typeof(int))
            .Invoke(null, new object[] { gameVersion.Value });
        var version = (Version)myVersion.GetMethods(AnyStatic).First(m => m.Name == "op_Implicit" && m.ReturnType == typeof(Version)).Invoke(null, new[] { appVersion });
        symbols.Add("VERSION_" + version.Minor);
        symbols.Add("BUILD_" + version.Build);
        // MyVRageScriptingInternal.Initialize: references, implicit in-game namespaces, symbols (empty strings are skipped by the game).
        compilerType.GetMethod("AddReferencedAssemblies").Invoke(compiler, new object[] { references.ToArray() });
        compilerType.GetMethod("AddImplicitInGameNamespacesFromTypes").Invoke(compiler, new object[] { implicitTypes });
        compilerType.GetMethod("AddConditionalCompilationSymbols").Invoke(compiler, new object[] { symbols.ToArray() });
        info["gameVersion"] = version.ToString();
        info["references"] = references;
        info["referenceNotes"] = referenceNotes;
        info["implicitNamespaceTypes"] = implicitTypes.Select(t => t.FullName).ToList();
        info["symbols"] = symbols;

        // 2. The checker SpaceEngineersGame picks: the custom (empty) one when MyFakes.USE_CUSTOM_IL_CHECKER_WHITELIST is set
        // in MyFakes' static constructor, else the default one.
        var fakes = Need(sandboxGame, "Sandbox.Engine.Utils.MyFakes");
        var fakeField = fakes.GetField("USE_CUSTOM_IL_CHECKER_WHITELIST", AnyStatic) ?? throw new InvalidOperationException("MyFakes.USE_CUSTOM_IL_CHECKER_WHITELIST not found");
        bool? customChecker = null;
        Il.Op previous = null;
        foreach (var op in Il.Read(fakes.TypeInitializer))
        {
            if (op.Code == OpCodes.Stsfld && Il.Same(op.Operand, fakeField))
                customChecker = previous?.Code == OpCodes.Ldc_I4_1 ? true : previous?.Code == OpCodes.Ldc_I4_0 ? false : (bool?)null;
            previous = op;
        }
        if (customChecker == null)
            throw new InvalidOperationException("MyFakes' static constructor does not set USE_CUSTOM_IL_CHECKER_WHITELIST to a constant");
        info["checker"] = customChecker.Value ? "MySpaceGameCustomIlChecker (empty)" : "MySpaceGameDefaultIlChecker";
        var steps = new List<string>();
        using (var batch = (IDisposable)whitelist.GetType().GetMethod("OpenBatch").Invoke(whitelist, null))
        {
            if (!customChecker.Value)
            {
                var checker = Need(seGame, "SpaceEngineers.Game.MySpaceGameDefaultIlChecker");
                foreach (var op in Il.Read(NeedMethod(checker, "InitIlChecker", AnyInstance)))
                {
                    if (!(op.Operand is MethodInfo m) || m.DeclaringType != checker) continue;
                    var p = m.GetParameters();
                    if (!m.IsStatic || p.Length != 1 || p[0].ParameterType.Name != "IMyWhitelistBatch")
                        throw new InvalidOperationException("InitIlChecker calls " + m.Name + ", which this tool does not replay");
                    m.Invoke(null, new object[] { batch });
                    steps.Add(m.Name);
                }
                if (steps.Count == 0) throw new InvalidOperationException("no registration steps found in MySpaceGameDefaultIlChecker.InitIlChecker");
            }
        }
        info["steps"] = steps;

        // 3. The in-game blacklist, as MySandboxGame.InitIlChecker: each AddMembers(typeof(T), "a", "b") call read from its IL.
        var initChecker = NeedMethod(Need(sandboxGame, "Sandbox.MySandboxGame"), "InitIlChecker", AnyStatic);
        var blacklistCalls = new List<string>();
        using (var batch = (IDisposable)whitelist.GetType().GetMethod("OpenIngameBlacklistBatch").Invoke(whitelist, null))
        {
            Type type = null;
            var names = new List<string>();
            foreach (var op in Il.Read(initChecker))
            {
                if (op.Code == OpCodes.Ldtoken && op.Operand is Type t) { type = t; names.Clear(); }
                else if (op.Operand is string s) names.Add(s);
                else if (op.Operand is MethodBase mb && mb.DeclaringType?.Name == "IMyScriptBlacklistBatch")
                {
                    if (mb.Name != "AddMembers" || type == null)
                        throw new InvalidOperationException("InitIlChecker calls " + mb.Name + ", which this tool does not replay");
                    batch.GetType().GetMethod("AddMembers").Invoke(batch, new object[] { type, names.ToArray() });
                    blacklistCalls.Add(type.FullName + ": " + string.Join(", ", names));
                    type = null;
                    names.Clear();
                }
            }
        }
        if (blacklistCalls.Count == 0)
            throw new InvalidOperationException("no blacklist calls found in MySandboxGame.InitIlChecker");
        info["blacklistCalls"] = blacklistCalls;

        var entries = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (object kv in (IEnumerable)whitelist.GetType().GetMethod("GetWhitelist").Invoke(whitelist, null))
            entries[(string)kv.GetType().GetProperty("Key").GetValue(kv)] = Convert.ToInt32(kv.GetType().GetProperty("Value").GetValue(kv));
        var blacklist = ((IEnumerable)whitelist.GetType().GetMethod("GetBlacklistedIngameEntries").Invoke(whitelist, null)).Cast<string>().OrderBy(x => x, StringComparer.Ordinal).ToList();
        info["whitelistEntries"] = entries.Count;
        info["modEntries"] = entries.Count(kv => (kv.Value & 1) != 0);
        info["pbEntries"] = entries.Count(kv => (kv.Value & 2) != 0);
        info["blacklistEntries"] = blacklist.Count;

        if (a.TryGetValue("dump", out string dumpDir) && dumpDir.Length > 0)
        {
            Directory.CreateDirectory(dumpDir);
            string source = "built by the game's own registration code (MySpaceGameDefaultIlChecker, MySandboxGame.InitIlChecker) from " + _ds;
            Json.Write(Path.Combine(dumpDir, "whitelist-mod.json"), new Dictionary<string, object>
            {
                ["target"] = "ModApi", ["sandboxGameSha256"] = info["sandboxGameSha256"], ["gameVersion"] = info["gameVersion"], ["source"] = source,
                ["entries"] = entries.Where(kv => (kv.Value & 1) != 0).Select(kv => kv.Key).ToList(),
            });
            Json.Write(Path.Combine(dumpDir, "whitelist-pb.json"), new Dictionary<string, object>
            {
                ["target"] = "Ingame", ["sandboxGameSha256"] = info["sandboxGameSha256"], ["gameVersion"] = info["gameVersion"], ["source"] = source,
                ["entries"] = entries.Where(kv => (kv.Value & 2) != 0).Select(kv => kv.Key).ToList(),
                ["blacklist"] = blacklist,
            });
            Json.Write(Path.Combine(dumpDir, "whitelist-info.json"), info);
        }

        if (!a.TryGetValue("jobs", out string jobsFile))
        {
            Console.WriteLine($"DeepWhitelist: {entries.Count} whitelist entries, {blacklist.Count} blacklist entries");
            return 0;
        }
        var compat = Compatibility(sandboxGame);
        info["modCompatibility"] = new Dictionary<string, object> { ["prefixLines"] = compat.Key.Split('\n').Length - 1, ["replacements"] = compat.Value.Count };
        var jobs = new List<Dictionary<string, object>>();
        var create = NeedMethod(compilerType, "CreateCompilation", AnyInstance);
        // VRage.Scripting.Script lives in VRage.dll: take it from CreateCompilation's IEnumerable<Script> parameter.
        var scriptType = create.GetParameters()[1].ParameterType.GetGenericArguments()[0];
        var visitorType = Need(scripting, "VRage.Scripting.MyBlacklistSyntaxVisitor");
        foreach (var job in ReadJobs(jobsFile))
        {
            bool ingame = job.Target == "ingame";
            var scripts = Array.CreateInstance(scriptType, job.Files.Count);
            for (int i = 0; i < job.Files.Count; i++)
            {
                string text = File.ReadAllText(job.Files[i].Value, Encoding.UTF8);
                if (!ingame)
                {
                    text = text.Insert(0, compat.Key);
                    foreach (var kv in compat.Value) text = text.Replace(kv.Key, kv.Value);
                }
                scripts.SetValue(Activator.CreateInstance(scriptType, job.Files[i].Key, text), i);
            }
            var compilation = (Compilation)create.Invoke(compiler, new object[] { "DeepWhitelist_" + job.Id.Replace(':', '_'), scripts, false });
            string field = ingame ? "m_inGameWhitelistDiagnosticAnalyzer" : "m_modApiWhitelistDiagnosticAnalyzer";
            var analyzer = (DiagnosticAnalyzer)compilerType.GetField(field, AnyInstance).GetValue(compiler);
            var all = compilation.WithAnalyzers(ImmutableArray.Create(analyzer)).GetAllDiagnosticsAsync().GetAwaiter().GetResult();
            var diags = new List<Dictionary<string, object>>();
            foreach (var d in all)
            {
                // The game rejects a script on any error; warnings only reach its log. AD0001 (the analyzer threw) is kept
                // so the caller can tell the check did not complete.
                if (d.Severity != DiagnosticSeverity.Error && d.Id != "AD0001") continue;
                var span = d.Location.GetMappedLineSpan();
                diags.Add(new Dictionary<string, object>
                {
                    ["id"] = d.Id, ["severity"] = d.Severity.ToString().ToLowerInvariant(), ["message"] = d.GetMessage(),
                    ["file"] = d.Location.IsInSource ? span.Path.Replace('\\', '/') : null,
                    ["line"] = d.Location.IsInSource ? span.StartLinePosition.Line + 1 : 0,
                    ["column"] = d.Location.IsInSource ? span.StartLinePosition.Character + 1 : 0,
                    ["endLine"] = d.Location.IsInSource ? span.EndLinePosition.Line + 1 : 0,
                    ["kind"] = d.Descriptor.Category == "Whitelist" ? "whitelist" : d.Id == "AD0001" ? "analyzer-failure" : "compile",
                });
            }
            // MyBlacklistSyntaxVisitor (blacklisted attributes): the game runs it on every compile and fails on any result.
            object visitor = Activator.CreateInstance(visitorType);
            var results = (IEnumerable)visitorType.GetProperty("Results").GetValue(visitor);
            int seen = 0;
            foreach (var tree in compilation.SyntaxTrees)
            {
                visitorType.GetMethod("SetSemanticModel").Invoke(visitor, new object[] { compilation.GetSemanticModel(tree) });
                try
                {
                    visitorType.GetMethod("Visit").Invoke(visitor, new object[] { tree.GetRoot() });
                }
                catch (TargetInvocationException e)
                {
                    diags.Add(new Dictionary<string, object>
                    {
                        ["id"] = "BlacklistVisitorFailed", ["severity"] = "error", ["kind"] = "analyzer-failure",
                        ["message"] = "the game's MyBlacklistSyntaxVisitor threw " + e.InnerException?.GetType().Name + ": " + e.InnerException?.Message,
                        ["file"] = tree.FilePath.Replace('\\', '/'), ["line"] = 0, ["column"] = 0, ["endLine"] = 0,
                    });
                }
                var now = results.Cast<object>().ToList();
                foreach (var item in now.Skip(seen))
                    diags.Add(new Dictionary<string, object>
                    {
                        ["id"] = "BlacklistedAttribute", ["severity"] = "error", ["kind"] = "whitelist",
                        ["message"] = ((string)item.GetType().GetField("Message").GetValue(item)).Trim(),
                        ["file"] = tree.FilePath.Replace('\\', '/'), ["line"] = 0, ["column"] = 0, ["endLine"] = 0,
                    });
                seen = now.Count;
            }
            jobs.Add(new Dictionary<string, object>
            {
                ["id"] = job.Id, ["target"] = ingame ? "Ingame" : "ModApi", ["files"] = job.Files.Count, ["diagnostics"] = diags,
            });
            Console.WriteLine($"DeepWhitelist {job.Id} ({(ingame ? "Ingame" : "ModApi")}): {job.Files.Count} files, " +
                              $"{diags.Count(x => (string)x["kind"] == "whitelist")} whitelist, {diags.Count(x => (string)x["kind"] == "compile")} compile errors");
        }
        info["jobs"] = jobs;
        Json.Write(a["out"], info);
        return 0;
    }

    /// <summary>MyScriptManager.UpdateCompatibility: COMPATIBILITY_USINGS inserted at the start, then each entry of
    /// m_compatibilityChanges replaced in order. Both read from the game; the method's IL is checked to still do that.</summary>
    private static KeyValuePair<string, List<KeyValuePair<string, string>>> Compatibility(Assembly sandboxGame)
    {
        var manager = Need(sandboxGame, "Sandbox.Game.World.MyScriptManager");
        var usingsField = manager.GetField("COMPATIBILITY_USINGS", AnyStatic) ?? throw new InvalidOperationException("MyScriptManager.COMPATIBILITY_USINGS not found");
        string usings = (string)usingsField.GetRawConstantValue();
        var changesField = manager.GetField("m_compatibilityChanges", AnyStatic) ?? throw new InvalidOperationException("MyScriptManager.m_compatibilityChanges not found");
        var ops = Il.Read(NeedMethod(manager, "UpdateCompatibility", AnyStatic)).ToList();
        bool insertsUsings = ops.Any(o => usings.Equals(o.Operand));
        bool usesChanges = ops.Any(o => Il.Same(o.Operand, changesField));
        bool replaces = ops.Any(o => o.Operand is MethodBase m && m.DeclaringType == typeof(string) && m.Name == "Replace");
        if (!insertsUsings || !usesChanges || !replaces)
            throw new InvalidOperationException("MyScriptManager.UpdateCompatibility changed; this tool does not replay it");
        var changes = ((IEnumerable<KeyValuePair<string, string>>)changesField.GetValue(null)).ToList();
        return new KeyValuePair<string, List<KeyValuePair<string, string>>>(usings, changes);
    }

    private sealed class Job
    {
        public string Id;
        public string Target;
        public readonly List<KeyValuePair<string, string>> Files = new List<KeyValuePair<string, string>>();
    }

    private static List<Job> ReadJobs(string path)
    {
        var jobs = new List<Job>();
        foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
        {
            var p = line.Split('\t');
            if (p[0] == "job" && p.Length == 3) jobs.Add(new Job { Id = p[1], Target = p[2] });
            else if (p[0] == "file" && p.Length == 3 && jobs.Count > 0) jobs[jobs.Count - 1].Files.Add(new KeyValuePair<string, string>(p[1], p[2]));
            else if (line.Trim().Length > 0) throw new InvalidOperationException("bad jobs line: " + line);
        }
        return jobs;
    }
}

/// <summary>Reads a method body: each opcode with its resolved operand (string, type, method or field).</summary>
internal static class Il
{
    internal sealed class Op
    {
        public OpCode Code;
        public object Operand;
    }

    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => o.Value);

    /// <summary>The same field, whether reached through IL tokens or Type.GetField.</summary>
    public static bool Same(object operand, FieldInfo field) =>
        operand is FieldInfo f && f.Module == field.Module && f.MetadataToken == field.MetadataToken;

    public static IEnumerable<Op> Read(MethodBase method)
    {
        byte[] il = method?.GetMethodBody()?.GetILAsByteArray() ?? throw new InvalidOperationException((method?.Name ?? "method") + " has no body");
        var module = method.Module;
        Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
        Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        int i = 0;
        while (i < il.Length)
        {
            short value = il[i] == 0xFE ? (short)(0xFE00 | il[i + 1]) : il[i];
            i += il[i] == 0xFE ? 2 : 1;
            if (!Codes.TryGetValue(value, out OpCode op)) throw new InvalidOperationException($"unknown opcode {value:X} in {method.Name}");
            int token = 0;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: i += 1; break;
                case OperandType.InlineVar: i += 2; break;
                case OperandType.InlineI8: case OperandType.InlineR: i += 8; break;
                case OperandType.InlineSwitch: i += 4 + BitConverter.ToInt32(il, i) * 4; break;
                default:
                    token = BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
            }
            object operand = null;
            try
            {
                if (op.OperandType == OperandType.InlineI) operand = token;
                else if (op == OpCodes.Ldstr) operand = module.ResolveString(token);
                else if (op.OperandType == OperandType.InlineTok || op.OperandType == OperandType.InlineType)
                    operand = module.ResolveMember(token, typeArgs, methodArgs) as Type;
                else if (op.OperandType == OperandType.InlineMethod) operand = module.ResolveMethod(token, typeArgs, methodArgs);
                else if (op.OperandType == OperandType.InlineField) operand = module.ResolveField(token, typeArgs, methodArgs);
            }
            catch (ArgumentException)
            {
            }
            yield return new Op { Code = op, Operand = operand };
        }
    }
}

/// <summary>Minimal JSON writer (no package needed): strings, numbers, booleans, lists and string-keyed dictionaries.</summary>
internal static class Json
{
    public static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        var sb = new StringBuilder();
        Emit(sb, value);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void Emit(StringBuilder sb, object v)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case string s: sb.Append('"'); foreach (char c in s) sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => c < ' ' ? $"\\u{(int)c:x4}" : c.ToString() }); sb.Append('"'); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case int or long or double: sb.Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)); break;
            case IDictionary d:
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in d) { if (!first) sb.Append(','); first = false; Emit(sb, kv.Key.ToString()); sb.Append(':'); Emit(sb, kv.Value); }
                sb.Append('}');
                break;
            case IEnumerable e:
                sb.Append('[');
                bool f = true;
                foreach (object x in e) { if (!f) sb.Append(','); f = false; Emit(sb, x); }
                sb.Append(']');
                break;
            default: Emit(sb, v.ToString()); break;
        }
    }
}
