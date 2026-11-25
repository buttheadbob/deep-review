using Xunit;
using static DeepRules.Tests.Harness;

namespace DeepRules.Tests
{
    /// <summary>DR010 outside game and plugin code: per-instance values stored in statics.</summary>
    public class DR010Tests
    {
        /// <summary>SimpleDiscordDotNet's HandlerServices: Configure(provider, bot) from each bot overwrites the one static slot.</summary>
        private const string HandlerServicesShape = """
using System;
public interface IDiscordBot { }
public static class HandlerServices
{
    private static volatile IServiceProvider? s_provider;
    private static volatile IDiscordBot? s_bot;
    internal static void Configure(IServiceProvider? provider, IDiscordBot bot)
    {
        s_provider = provider ?? s_provider;
        s_bot = bot;
    }
    public static T? Get<T>() where T : class => s_provider?.GetService(typeof(T)) as T ?? s_bot as T;
}
public sealed class DiscordBot : IDiscordBot
{
    public DiscordBot(IServiceProvider? services) { HandlerServices.Configure(services, this); }
}
""";

        private const string RegistryShape = """
using System;
using System.Collections.Generic;
public static class Registry
{
    private static readonly Dictionary<string, Type> s_types = new Dictionary<string, Type>();
    static Registry() { s_types["text"] = typeof(string); }
    public static void Register(string name, Type type) => s_types[name] = type;
    public static Type? Find(string name) => s_types.TryGetValue(name, out var t) ? t : null;
}
""";

        [Fact]
        public void HandlerServicesFiresOutsideGames()
        {
            var d = Fires("DR010", HandlerServicesShape, 2, host: Host.None);
            Assert.Contains(d, x => x.GetMessage().Contains("'HandlerServices.s_bot' is set from Configure with its argument 'bot'"));
            Assert.All(d, x => Assert.Contains("shared by every instance", x.GetMessage()));
        }

        [Fact]
        public void InstanceMemberWritingAStaticFires() => Fires("DR010", """
public class Client
{
    public static Client? Current { get; set; }
    private static string? s_name;
    public Client(string name) { Current = this; s_name = name; }
}
""", 2, host: Host.None);

        [Fact]
        public void RegistryFilledOnceIsSilentOutsideGames() => Silent("DR010", RegistryShape, host: Host.None);

        [Fact]
        public void StaticInitialisationAndResetsAreSilent() => Silent("DR010", """
using System;
public class Cache : IDisposable
{
    [ThreadStatic] private static object? t_scratch;
    private static object? s_shared;
    private static string? s_path;
    private static readonly object s_sync = new object();
    private static object? s_backing;
    public static object? Backing { get => s_backing; set => s_backing = value; }
    static Cache() { s_shared = new object(); }
    public static object Shared => s_shared ??= new object();
    public static void SetPath(string path) => s_path = path;
    public void Use() { t_scratch = new object(); }
    public void Dispose() { s_shared = null; }
}
""", host: Host.None);

        /// <summary>In game and plugin code the reload check (DR034) applies instead, to both shapes.</summary>
        [Fact]
        public void BothShapesUseTheReloadCheckInGameCode()
        {
            Silent("DR010", HandlerServicesShape);
            Fires("DR034", HandlerServicesShape, 2);
            Silent("DR010", RegistryShape);
            Fires("DR034", RegistryShape, 1);
        }
    }

    /// <summary>DR034 in game and plugin code: static state not reset on unload.</summary>
    public class DR034Tests
    {
        [Fact]
        public void StaticCollectionAndInstance() => Fires("DR034", """
using System.Collections.Generic;
using VRage.ModAPI;
public class Plugin
{
    public static Plugin? Instance;
    private static readonly List<IMyEntity> Tracked = new List<IMyEntity>();
    public void Init() { Instance = this; }
    public void Track(IMyEntity e) => Tracked.Add(e);
}
""", 2);

        [Fact]
        public void StaticEventAndAutoProperty() => Fires("DR034", """
using System;
public static class Hub
{
    public static event Action? Changed;
    public static string? LastPath { get; set; }
    public static void Raise() => Changed?.Invoke();
}
""", 2);

        [Fact]
        public void ResetOnUnloadIsSilent() => Silent("DR034", """
using System;
using System.Collections.Generic;
using VRage.ModAPI;
public class Plugin : IDisposable
{
    public static Plugin? Instance;
    private static readonly List<IMyEntity> Tracked = new List<IMyEntity>();
    public static event Action? Changed;
    public void Init() { Instance = this; Changed?.Invoke(); }
    public void Track(IMyEntity e) => Tracked.Add(e);
    public void Dispose() { Instance = null; Changed = null; ResetTracking(); }
    private static void ResetTracking() => Tracked.Clear();
}
""");

        [Fact]
        public void ReadonlyNonCollectionAndConstAreSilent() => Silent("DR034", """
public static class Names
{
    public const string Prefix = "x";
    public static readonly string Folder = "Data";
    private static readonly object s_sync = new object();
    private static int s_count;
}
""");

        [Fact]
        public void NotOutsideGameCode() => Silent("DR034", """
using System.Collections.Generic;
public static class Hub
{
    public static string? LastPath { get; set; }
    private static readonly List<int> Items = new List<int>();
}
""", host: Host.None);
    }

    public class DR011Tests
    {
        [Fact]
        public void StaticGameEventWithoutRemove() => Fires("DR011", """
using Sandbox.Game.Entities;
using VRage.ModAPI;
public class Tracker
{
    public void Init() { MyEntities.OnEntityAdd += OnAdd; }
    private void OnAdd(IMyEntity e) { }
}
""", 1, "OnEntityAdd");

        [Fact]
        public void LambdaAndFieldPublisher() => Fires("DR011", """
using Game.Stubs;
using Sandbox.ModAPI;
public class Watcher
{
    private MyCubeGrid _grid;
    public Watcher(MyCubeGrid grid) { _grid = grid; }
    public void Init()
    {
        MyAPIGateway.Session.OnSessionReady += () => { };
        _grid.OnBlockAdded += OnBlock;
    }
    private void OnBlock(MyCubeGrid g) { }
}
""", 2);

        [Fact]
        public void MatchingRemoveIsSilent() => Silent("DR011", """
using Sandbox.Game.Entities;
using VRage.ModAPI;
public class Tracker
{
    public void Init() { MyEntities.OnEntityAdd += OnAdd; }
    public void Unload() { MyEntities.OnEntityAdd -= OnAdd; }
    private void OnAdd(IMyEntity e) { }
}
""");

        [Fact]
        public void LocalsParametersAndOwnedPublishersAreSilent() => Silent("DR011", """
using System.Collections.ObjectModel;
using Game.Stubs;
public class Owner
{
    public ObservableCollection<int> Items { get; } = new ObservableCollection<int>();
    public Owner() { Items.CollectionChanged += (s, e) => { }; }
    public void Hook(MyCubeGrid grid) { grid.OnBlockAdded += g => { }; }
}
""");
    }

    public class DR012Tests
    {
        [Fact]
        public void AssemblyResolveWithoutRemove() => Fires("DR012", """
using System;
using System.Reflection;
public class Loader
{
    public void Init() { AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    private Assembly? Resolve(object? sender, ResolveEventArgs args) => null;
}
""", 1, "AssemblyResolve");

        [Fact]
        public void UnobservedLambdaAndStaticConstructorHook() => Fires("DR012", """
using System.Threading.Tasks;
using Sandbox.Game.Entities;
using VRage.ModAPI;
public class Hooks
{
    static Hooks() { MyEntities.OnEntityAdd += Added; }
    private static void Added(IMyEntity e) { }
    public void Init() { TaskScheduler.UnobservedTaskException += (s, e) => e.SetObserved(); }
}
""", 2);

        [Fact]
        public void RemovedInDisposeIsSilent() => Silent("DR012", """
using System;
using System.Reflection;
public class Loader : IDisposable
{
    public void Init() { AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    public void Dispose() { AppDomain.CurrentDomain.AssemblyResolve -= Resolve; }
    private Assembly? Resolve(object? sender, ResolveEventArgs args) => null;
}
""");

        [Fact]
        public void CancelKeyPressRemovedIsSilent() => Silent("DR012", """
using System;
public class Console2
{
    private readonly ConsoleCancelEventHandler _handler;
    public Console2() { _handler = (s, e) => e.Cancel = true; Console.CancelKeyPress += _handler; }
    public void Stop() { Console.CancelKeyPress -= _handler; }
}
""");

        [Fact]
        public void ExecutableOwnsItsProcessAndIsSilent()
        {
            const string source = """
using System;
public static class Program
{
    public static void Main() { AppDomain.CurrentDomain.UnhandledException += (s, e) => Console.Error.WriteLine(e.ExceptionObject); }
}
""";
            Silent("DR012", source, output: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication);
            Silent("DR011", source, output: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication);
            Fires("DR012", source, 1);
        }

        [Fact]
        public void RemovedByAnotherTypeIsSilent() => Silent("DR012", """
using System;
using System.Reflection;
public class Loader
{
    public static Assembly? Resolve(object? sender, ResolveEventArgs args) => null;
    public void Init() { AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
}
public class Unloader { public void Dispose() { AppDomain.CurrentDomain.AssemblyResolve -= Loader.Resolve; } }
""");
    }

    public class DR013Tests
    {
        [Fact]
        public void UnpatchAllWithId() => Fires("DR013", """
using HarmonyLib;
public class Plugin
{
    private readonly Harmony _harmony = new Harmony("author.plugin");
    public void Init() { }
    public void Dispose() { _harmony.UnpatchAll("author.plugin"); }
}
""", 1, "UnpatchAll");

        [Fact]
        public void BareIdAndPatchAllWithoutUnpatch() => Fires("DR013", """
using HarmonyLib;
public class Plugin
{
    private readonly Harmony _harmony = new Harmony("MyPlugin");
    public void Init() { _harmony.PatchAll(); }
}
""", 2);

        [Fact]
        public void NamespacedIdWithUnpatchSelfIsSilent() => Silent("DR013", """
using HarmonyLib;
public class Plugin
{
    private const string Id = "author.plugin";
    private readonly Harmony _harmony = new Harmony(Id);
    public void Init() { _harmony.PatchAll(); }
    public void Dispose() { _harmony.UnpatchSelf(); }
}
""");

        [Fact]
        public void UnpatchPerOriginalIsSilent() => Silent("DR013", """
using System.Reflection;
using HarmonyLib;
public class Plugin
{
    private readonly Harmony _harmony = new Harmony("author.plugin");
    private MethodBase? _original;
    public void Init() { _harmony.PatchAll(Assembly.GetExecutingAssembly()); }
    public void UnloadData() { if (_original != null) _harmony.Unpatch(_original, HarmonyPatchType.All, _harmony.Id); }
}
""");

        [Fact]
        public void UnpatchInAnotherTypeIsSilent() => Silent("DR013", """
using HarmonyLib;
public static class Patches
{
    public static readonly Harmony Harmony = new Harmony("author.plugin");
    public static void Apply() => Harmony.PatchAll();
}
public class Plugin { public void Dispose() { Patches.Harmony.UnpatchSelf(); } }
""");

        /// <summary>A type named like Harmony's in a compilation without Harmony is not Harmony.</summary>
        [Fact]
        public void LookalikeWithoutHarmonyIsSilent()
        {
            const string source = """
namespace HarmonyLib
{
    public class Harmony
    {
        public Harmony(string id) { }
        public void PatchAll() { }
        public void UnpatchAll(string? id = null) { }
    }
}
public class Plugin
{
    private readonly HarmonyLib.Harmony _h = new HarmonyLib.Harmony("MyPlugin");
    public void Init() { _h.PatchAll(); }
    public void Dispose() { _h.UnpatchAll("MyPlugin"); }
}
""";
            Silent("DR013", source, host: Host.None);
            Silent("DR013", source, host: Host.Game);
        }
    }
}
