using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static DeepRules.Tests.Harness;

namespace DeepRules.Tests
{
    public class DR026Tests
    {
        [Fact]
        public void EpsilonCompare() => Fires("DR026", """
using System;
using System.Collections.Generic;
class ByVolume : IComparer<double>
{
    public int Compare(double a, double b) => Math.Abs(a - b) < 0.001 ? 0 : a < b ? -1 : 1;
}
""", 1, "Math.Abs");

        [Fact]
        public void SubtractionInSortLambdaAndCastInCompareTo() => Fires("DR026", """
using System;
using System.Collections.Generic;
class Item : IComparable<Item>
{
    public int Id;
    public double Weight;
    public int CompareTo(Item? other) => (int)(Weight - (other?.Weight ?? 0));
    public static void Order(List<Item> items) => items.Sort((a, b) => a.Id - b.Id);
}
""", 2);

        [Fact]
        public void CompareToIsSilent() => Silent("DR026", """
using System.Collections.Generic;
class ByLength : IComparer<string>
{
    public int Compare(string? x, string? y) => (x?.Length ?? 0).CompareTo(y?.Length ?? 0);
}
""");

        [Fact]
        public void NonComparerSubtractionIsSilent() => Silent("DR026", """
using System.Collections.Generic;
class Maths
{
    public int Diff(int a, int b) => a - b;
    public static void Order(List<int> items) => items.Sort((a, b) => a.CompareTo(b));
}
""");
    }

    public class DR027Tests
    {
        [Fact]
        public void NullSlotBreaksForeach() => Fires("DR027", """
using System.Collections.Generic;
class Slot { public long Free; }
class Inventory
{
    public long Total(List<Slot?> slots)
    {
        long total = 0;
        foreach (var slot in slots)
        {
            if (slot == null) break;
            total += slot.Free;
        }
        return total;
    }
}
""", 1);

        [Fact]
        public void IndexedElementTestBreaksFor() => Fires("DR027", """
class Inventory
{
    public int Count(object?[] items)
    {
        int n = 0;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] == null) break;
            n++;
        }
        return n;
    }
}
""", 1);

        [Fact]
        public void LimitTestIsSilent() => Silent("DR027", """
using System.Collections.Generic;
class Inventory
{
    public long Take(List<long> amounts, long limit)
    {
        long total = 0;
        foreach (var a in amounts)
        {
            if (total >= limit) break;
            total += a;
        }
        return total;
    }
}
""");

        [Fact]
        public void ContinueAndSwitchBreakAreSilent() => Silent("DR027", """
using System.Collections.Generic;
class Inventory
{
    public int Count(List<string?> names)
    {
        int n = 0;
        foreach (var name in names)
        {
            if (name == null) continue;
            switch (name.Length) { case 0: break; default: n++; break; }
        }
        return n;
    }
}
""");
    }

    public class DR028Tests
    {
        [Fact]
        public void GpsRemovedBeforeAdd() => Fires("DR028", """
using Sandbox.ModAPI;
class Markers
{
    public void Replace(long identity, IMyGps old, IMyGps next)
    {
        MyAPIGateway.Session.GPS.RemoveGps(identity, old);
        MyAPIGateway.Session.GPS.AddGps(identity, next);
    }
}
""", 1, "RemoveGps");

        [Fact]
        public void CustomStoreAndFileDelete() => Fires("DR028", """
using System.IO;
class Store { public void Remove(string k) { } public void Add(string k, string v) { } }
class Saver
{
    private readonly Store _store = new Store();
    public void Update(string key, string value) { _store.Remove(key); _store.Add(key, value); }
    public void Swap(string path, string tmp) { File.Delete(path); File.Move(tmp, path); }
}
""", 2);

        [Fact]
        public void AddFirstOrTryFinallyIsSilent() => Silent("DR028", """
using Sandbox.ModAPI;
class Markers
{
    public void Replace(long identity, IMyGps old, IMyGps next)
    {
        MyAPIGateway.Session.GPS.AddGps(identity, next);
        MyAPIGateway.Session.GPS.RemoveGps(identity, old);
    }
    public void Guarded(long identity, IMyGps old, IMyGps next)
    {
        MyAPIGateway.Session.GPS.RemoveGps(identity, old);
        try { MyAPIGateway.Session.GPS.AddGps(identity, next); }
        catch { MyAPIGateway.Session.GPS.AddGps(identity, old); throw; }
    }
}
""");

        [Fact]
        public void BclListReplaceIsSilent() => Silent("DR028", """
using System.Collections.Generic;
class Names
{
    private readonly List<string> _names = new List<string>();
    private readonly Dictionary<int, string> _byId = new Dictionary<int, string>();
    public void Rename(string old, string next) { _names.Remove(old); _names.Add(next); }
    public void Set(int id, string v) { _byId.Remove(id); _byId.Add(id, v); }
}
""");
    }

    public class DR029Tests
    {
        [Fact]
        public void DebugOnlyBehaviour() => Fires("DR029", """
class Settings
{
    public int Interval = 60;
    public void Load()
    {
#if DEBUG
        Interval = 1;
#endif
    }
}
""", 1, "Interval = 1");

        [Fact]
        public void ElseBranchAndNotLocalBuild()
        {
            var d = Fires("DR029", """
class Settings
{
    public string Url = "";
    public void Load()
    {
#if DEBUG
        Url = "http://localhost";
#else
        Url = "https://example.invalid";
#endif
#if !LOCAL_BUILD
        Url += "/v1";
#endif
    }
}
""", 3);
            Assert.Contains(d, x => x.GetMessage().Contains("#else branch"));
            Assert.Contains(d, x => x.GetMessage().Contains("#if !LOCAL_BUILD"));
        }

        [Fact]
        public void LoggingOnlyRegionIsSilent() => Silent("DR029", """
using Game.Stubs;
class Settings
{
    public void Load()
    {
#if DEBUG
        // trace the load
        Log.Info("loading settings");
#endif
    }
}
""");

        [Fact]
        public void OtherSymbolsAreSilent() => Silent("DR029", """
class Settings
{
    public int Interval = 60;
    public void Load()
    {
#if NETFRAMEWORK
        Interval = 30;
#endif
    }
}
""");
    }

    public class DR030Tests
    {
        [Fact]
        public void AnnotationsWithoutDirectiveGiveOneDiagnosticPerFile()
        {
            var d = Fires("DR030", """
class Names
{
    private string? _last;
    public string? Last => _last;
    public string Get(object? o) => o!.ToString() ?? "";
}
""", 1, nullable: NullableContextOptions.Disable);
            Assert.Contains("3 nullable reference annotations", d[0].GetMessage());
            Assert.Contains("1 null-forgiving operator", d[0].GetMessage());
            Assert.Contains("NullableContextOptions.Disable", d[0].GetMessage());
            Assert.Contains("Pulsar and Magnetar", d[0].GetMessage());
        }

        private const string Annotated = """
class Names
{
    private string? _last;
    public string? Last => _last;
}
""";

        /// <summary>The compilation's own setting decides: the same file fires with nullable off and is silent with it on.</summary>
        [Fact]
        public void SameFileFiresOnlyWhereTheCompileHasNullableOff()
        {
            Fires("DR030", Annotated, 1, nullable: NullableContextOptions.Disable);
            Fires("DR030", Annotated, 1, nullable: NullableContextOptions.Warnings);
            Silent("DR030", Annotated, nullable: NullableContextOptions.Enable);
            Silent("DR030", Annotated, nullable: NullableContextOptions.Annotations);
        }

        [Fact]
        public void NoGameOrPluginCondition()
        {
            var d = Fires("DR030", Annotated, 1, host: Host.None, nullable: NullableContextOptions.Disable);
            Assert.DoesNotContain("Pulsar", d[0].GetMessage());
            Silent("DR030", Annotated, host: Host.None, nullable: NullableContextOptions.Enable);
        }

        [Fact]
        public void DisableAfterEnableStillCounts() => Fires("DR030", """
#nullable enable
class A { private string? _a; public string? Get() => _a; }
#nullable disable
class B { private string? _b; public string? Get() => _b; }
""", 1, "class B", nullable: NullableContextOptions.Disable);

        [Fact]
        public void NullableEnableIsSilent() => Silent("DR030", """
#nullable enable
class Names
{
    private string? _last;
    public string Get(object? o) => o!.ToString() ?? _last ?? "";
}
""", nullable: NullableContextOptions.Disable);

        [Fact]
        public void ValueTypeNullablesAreSilent() => Silent("DR030", """
class Numbers
{
    private int? _count;
    public long? Total(int? add) => _count + add;
}
""", nullable: NullableContextOptions.Disable);

        /// <summary>
        /// What the hub's compile (no Nullable setting) reports: CS8632 for '?' on reference types, and nothing for '!'.
        /// DR030's message relies on this.
        /// </summary>
        [Fact]
        public void CompilerReportsCs8632OnlyForQuestionMarks()
        {
            var tree = CSharpSyntaxTree.ParseText("class C { string? a; object M(object o) => o!; }");
            var comp = CSharpCompilation.Create("X", new[] { tree },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));
            var ids = comp.GetDiagnostics().Where(x => x.Severity != DiagnosticSeverity.Hidden).Select(x => x.Id).ToList();
            Assert.Equal(1, ids.Count(x => x == "CS8632"));
            Assert.DoesNotContain(ids, x => x != "CS8632" && x != "CS0169");
        }
    }

    public class DR031Tests
    {
        [Fact]
        public void DereferencedAndStoredThenUsed() => Fires("DR031", """
using System.Reflection;
using HarmonyLib;
class Target { private int m_value; private int count; }
class Hack
{
    public object? Read(Target t) => typeof(Target).GetField("m_value", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(t);
    public void Write(Target t) { var f = AccessTools.Field(typeof(Target), "count"); f.SetValue(t, 1); }
}
""", 2);

        [Fact]
        public void BindingFlagsThatCannotMatch()
        {
            var d = Fires("DR031", """
using System.Reflection;
class Target { private int _count; }
class Hack
{
    public FieldInfo? A() => typeof(Target).GetField("_count");
    public FieldInfo? B() => typeof(Target).GetField("_count", BindingFlags.NonPublic);
}
""", 2);
            Assert.Contains(d, x => x.GetMessage().Contains("default Public binding flags"));
            Assert.Contains(d, x => x.GetMessage().Contains("neither Instance nor Static"));
        }

        [Fact]
        public void NullCheckedIsSilent() => Silent("DR031", """
using System;
using System.Reflection;
class Target { private int m_value; }
class Hack
{
    public object? Read(Target t)
    {
        var f = typeof(Target).GetField("m_value", BindingFlags.Instance | BindingFlags.NonPublic);
        if (f == null) throw new MissingFieldException("Target", "m_value");
        return f.GetValue(t);
    }
    public object? Maybe(Target t) => typeof(Target).GetField("m_value", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(t);
}
""");

        [Fact]
        public void NameofIsSilent() => Silent("DR031", """
using System.Reflection;
class Target { public int Value; }
class Hack
{
    public object? Read(Target t) => typeof(Target).GetField(nameof(Target.Value)).GetValue(t);
}
""");
    }

    public class DR031ContextTests
    {
        [Fact]
        public void TypeLookupOutsideGameCodeNamesNoGameUpdate()
        {
            var d = Fires("DR031", """
using System.Reflection;
class Target { private int m_value; }
class Hack
{
    public object? Read(Target t) => typeof(Target).GetField("m_value", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(t);
}
""", 1, host: Host.None);
            Assert.Contains("a change to the reflected type", d[0].GetMessage());
        }

        [Fact]
        public void AccessToolsLookalikeWithoutHarmonyIsSilent() => Silent("DR031", """
using System.Reflection;
namespace HarmonyLib { public static class AccessTools { public static FieldInfo? Field(System.Type t, string n) => null; } }
class Target { private int count; }
class Hack { public void Write(Target t) { var f = HarmonyLib.AccessTools.Field(typeof(Target), "count"); f!.SetValue(t, 1); } }
""", host: Host.None);
    }

    public class DR032Tests
    {
        [Fact]
        public void PlainAwaitIsSilent() => Silent("DR032", """
using System.Threading.Tasks;
using Sandbox.Game.Entities;
class Scanner
{
    public async Task Check(long id)
    {
        await Task.Delay(100);
        var e = MyEntities.GetEntityById(id);
    }
}
""");

        /// <summary>A MyEntities type in a compilation without the game is not the game's.</summary>
        [Fact]
        public void LookalikeWithoutTheGameIsSilent() => Silent("DR032", """
using System.Threading.Tasks;
namespace Sandbox.Game.Entities { public static class MyEntities { public static object? GetEntityById(long id) => null; } }
class Scanner
{
    public Task Scan(long id) => Task.Run(() => { var e = Sandbox.Game.Entities.MyEntities.GetEntityById(id); });
}
""", host: Host.None);

        [Fact]
        public void LookupInTaskRun() => Fires("DR032", """
using System.Threading.Tasks;
using Sandbox.Game.Entities;
class Scanner
{
    public Task Scan(long id) => Task.Run(() => { var e = MyEntities.GetEntityById(id); });
}
""", 1, "GetEntityById");

        [Fact]
        public void LookupAfterAwaitAndInTimer() => Fires("DR032", """
using System.Threading;
using System.Threading.Tasks;
using Sandbox.ModAPI;
using Sandbox.Game.Entities;
class Scanner
{
    private Timer? _timer;
    public async Task Check(long id)
    {
        await Task.Delay(100).ConfigureAwait(false);
        var e = MyAPIGateway.Entities.GetEntityById(id);
    }
    public void Start(long id) { _timer = new Timer(_ => MyEntities.TryGetEntityById(id, out var e), null, 0, 1000); }
}
""", 2);

        [Fact]
        public void GameThreadIsSilent() => Silent("DR032", """
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.ModAPI;
class Scanner
{
    public IMyEntity? Find(long id) => MyEntities.GetEntityById(id);
    public IMyEntity? Find2(long id) => MyAPIGateway.Entities.GetEntityById(id);
}
""");

        [Fact]
        public void MarshalledOrBeforeAwaitIsSilent() => Silent("DR032", """
using System.Threading.Tasks;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
class Scanner
{
    public Task Scan(long id) => Task.Run(() => MyAPIGateway.Utilities.InvokeOnGameThread(() => MyEntities.GetEntityById(id)));
    public async Task Check(long id)
    {
        var e = MyEntities.GetEntityById(id);
        await Task.Delay(100);
    }
}
""");
    }
}
