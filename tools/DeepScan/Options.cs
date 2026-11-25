using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeepScan;

/// <summary>Command line options. Every option that takes values reads until the next "--option".</summary>
internal sealed class Options
{
    public List<string> Sources { get; } = [];
    public string? SourceRoot { get; set; }
    public string? Manifest { get; set; }
    public string? Repo { get; set; }
    public string Runtime { get; set; } = "net10";
    public string? Profile { get; set; }
    public List<string> Probe { get; } = [];
    public string? GameDir { get; set; }
    public List<string> Refs { get; } = [];
    public List<string> RefFiles { get; } = [];
    public List<string> RootNames { get; } = [];
    public string? LangVersion { get; set; }
    public List<string>? Defines { get; set; }
    public string? Nullable { get; set; }
    public bool? Unsafe { get; set; }
    public bool Debug { get; set; }
    public int? WarningLevel { get; set; }
    public List<string> Analyzers { get; } = [];
    public List<string> Generators { get; } = [];
    public List<string> GlobalConfigs { get; } = [];
    public List<string> AdditionalFiles { get; } = [];
    public List<string> AnalyzerOptions { get; } = [];
    public List<string> GlobalUsings { get; } = [];
    public string? Emit { get; set; }
    public string? Out { get; set; }
    public string AssemblyName { get; set; } = "Plugin";
    public bool IncludeInfo { get; set; }
    public bool NoAnalyzers { get; set; }
    public bool NoGraph { get; set; }
    public int TimeoutSeconds { get; set; } = 1800;
    public List<string> Exclude { get; } = ["bin", "obj", ".git", ".vs", ".idea"];

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{a}'.");
            string name = a[2..].ToLowerInvariant();
            var values = new List<string>();
            while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                values.Add(args[++i]);
            string One() => values.Count == 1 ? values[0] : throw new ArgumentException($"--{name} takes one value.");
            switch (name)
            {
                case "sources": o.Sources.AddRange(values); break;
                case "source-root": o.SourceRoot = One(); break;
                case "manifest": o.Manifest = One(); break;
                case "repo": o.Repo = One(); break;
                case "runtime": o.Runtime = One().ToLowerInvariant(); break;
                case "profile": o.Profile = One().ToLowerInvariant(); break;
                case "probe": o.Probe.AddRange(values); break;
                case "game-dir": o.GameDir = One(); break;
                case "refs": o.Refs.AddRange(values); break;
                case "ref-files": o.RefFiles.AddRange(values); break;
                case "root-names": o.RootNames.AddRange(values.SelectMany(Split)); break;
                case "langversion": o.LangVersion = One(); break;
                case "define": o.Defines = values.SelectMany(Split).ToList(); break;
                case "nullable": o.Nullable = One().ToLowerInvariant(); break;
                case "unsafe": o.Unsafe = values.Count == 0 || bool.Parse(One()); break;
                case "debug": o.Debug = true; break;
                case "warnlevel": o.WarningLevel = int.Parse(One(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "analyzers": o.Analyzers.AddRange(values); break;
                case "generators": o.Generators.AddRange(values); break;
                case "globalconfig": o.GlobalConfigs.AddRange(values); break;
                case "additional-files": o.AdditionalFiles.AddRange(values); break;
                case "analyzer-option": o.AnalyzerOptions.AddRange(values); break;
                case "global-usings": o.GlobalUsings.AddRange(values.SelectMany(Split)); break;
                case "emit": o.Emit = One(); break;
                case "out": o.Out = One(); break;
                case "assembly-name": o.AssemblyName = One(); break;
                case "include-info": o.IncludeInfo = true; break;
                case "no-analyzers": o.NoAnalyzers = true; break;
                case "no-graph": o.NoGraph = true; break;
                case "timeout": o.TimeoutSeconds = int.Parse(One(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "exclude": o.Exclude.AddRange(values); break;
                default: throw new ArgumentException($"Unknown option --{name}.");
            }
        }

        if (o.Runtime is not ("net10" or "net48"))
            throw new ArgumentException("--runtime must be net10 or net48.");
        if (o.Out is null)
            throw new ArgumentException("--out is required.");
        if (o.Sources.Count == 0 && o.Manifest is null)
            throw new ArgumentException("Give --sources or --manifest with --repo.");
        if (o.Manifest is not null && o.Repo is null)
            o.Repo = Path.GetDirectoryName(Path.GetFullPath(o.Manifest));
        o.Profile ??= o.Manifest is not null ? "magnetar" : "plain";
        if (o.Profile is not ("magnetar" or "pulsar" or "plain"))
            throw new ArgumentException("--profile must be magnetar, pulsar or plain.");
        return o;
    }

    private static IEnumerable<string> Split(string v) =>
        v.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Usage => """
        DeepScan: loader-faithful Roslyn compile plus analyzer host.

          --sources <dirs/files...>   C# sources (dirs are searched for *.cs, skipping bin/obj/.git)
          --manifest <hub xml>        read SourceDirectories (prefix match, like the loader) ...
          --repo <dir>                ... from this tree
          --source-root <dir>         paths in the output are relative to it (default: --repo or common root)
          --runtime net10|net48
          --profile magnetar|pulsar|plain   loader reference closure (magnetar/pulsar) or explicit refs (plain)
          --probe <dirs...>           loader probe directories in order (runtime dir, game dir, Libraries dir)
          --game-dir <dir>            DedicatedServer64 / Bin64, for the loader's SpaceEngineers*/VRage*/Sandbox* globs
          --refs <dirs...>            plain: every managed DLL in these dirs; loader: extra probe dirs
          --ref-files <files...>      custom references (NuGet compile assets, assets); override by assembly name
          --root-names <names>        extra closure roots
          --langversion <v>           default: CSharp14 (loader), latest (plain)
          --define <a;b>              default: the loader's symbols for the profile and runtime
          --nullable disable|enable|warnings|annotations   default disable (loader)
          --unsafe [true|false]       default true (loader)
          --debug                     Debug optimization and DEBUG symbol (loader default is Release)
          --warnlevel <n>             default 4 (CSharpCompilationOptions default, as the loader)
          --analyzers <dirs...>       every *.dll in these dirs is loaded as an analyzer reference
          --generators <dirs/files>   source generators (e.g. the SDK's Razor generator)
          --globalconfig <files...>   is_global configs, later files win: severities and analyzer options
          --additional-files <files>  AdditionalTexts (BannedSymbols.txt, .razor files)
          --analyzer-option k=v       extra global analyzer options
          --global-usings <ns;ns>     implicit usings to add
          --emit <dir>                write <assembly>.dll and .pdb
          --out <json>                result file (required)
          --include-info              keep Info-severity analyzer diagnostics
          --no-analyzers / --no-graph
          --timeout <seconds>         analyzer time limit (default 1800)

        Exit codes: 0 ran, 2 ran with gaps (load failures, missing references, timeouts), 1 could not run.
        """;
}
