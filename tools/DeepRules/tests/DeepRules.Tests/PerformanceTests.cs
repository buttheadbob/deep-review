using Xunit;
using static DeepRules.Tests.Harness;

namespace DeepRules.Tests
{
    public class DR023Tests
    {
        [Fact]
        public void LinqTwiceGivesOneDiagnosticWithBothLines()
        {
            var d = Fires("DR023", """
using System.Collections.Generic;
using System.Linq;
using VRage.Game.Components;
public class Session : MySessionComponentBase
{
    private readonly List<int> _items = new List<int>();
    public override void UpdateAfterSimulation()
    {
        var count = _items.Count(x => x > 0);
        var first = _items.FirstOrDefault();
    }
}
""", 1, host: Host.Game);
            Assert.Contains("lines 9, 10", d[0].GetMessage());
            Assert.Contains("MySessionComponentBase.UpdateAfterSimulation", d[0].GetMessage());
        }

        [Fact]
        public void DrawWithStringsFilesAllocationsAndClosures()
        {
            var d = Fires("DR023", """
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VRage.Game.Components;
public class Screen : MySessionComponentBase
{
    private string _path = "doom.wad";
    private double _fps;
    private List<int> _ids = new List<int>();
    public override void Draw()
    {
        var label = $"fps {_fps}";
        if (File.Exists(_path)) { }
        var buffer = new List<int>();
        int min = 3;
        var big = _ids.Where(x => x > min);
    }
}
""", host: Host.Game);
            Assert.Contains(d, x => x.GetMessage().Contains("string building"));
            Assert.Contains(d, x => x.GetMessage().Contains("file system calls"));
            Assert.Contains(d, x => x.GetMessage().Contains("allocations (new List`1)") || x.GetMessage().Contains("allocations (new List"));
            Assert.Contains(d, x => x.GetMessage().Contains("closures capturing locals (min)"));
        }

        [Fact]
        public void TorchAndGamePluginUpdate()
        {
            Fires("DR023", """
using System.Collections.Generic;
using Torch;
public class Plugin : TorchPluginBase
{
    public override void Update() { var pending = new List<int>(); }
}
""", 1, "new List", host: Host.Torch);
            Fires("DR023", """
using System.Collections.Generic;
using VRage.Plugins;
public class Plugin : IPlugin
{
    private readonly List<string> _names = new List<string>();
    public void Init(object game) { }
    public void Update() { var line = string.Join(",", _names); }
    public void Dispose() { }
}
""", 1, "string.Join", host: Host.Game);
        }

        [Fact]
        public void HarmonyPatchOnPerFrameGameMethod() => Fires("DR023", """
using System.Collections.Generic;
using HarmonyLib;
using Sandbox.Game.Entities;
[HarmonyPatch(typeof(MyCubeGrid), "UpdateBeforeSimulation")]
public static class GridPatch
{
    public static void Postfix(MyCubeGrid __instance) { var seen = new HashSet<long>(); }
}
""", 1, "new HashSet");

        [Fact]
        public void HarmonyPatchOnOtherMethodsIsSilent() => Silent("DR023", """
using System.Collections.Generic;
using HarmonyLib;
using Sandbox.Game.Entities;
public class Grid { public void UpdateBeforeSimulation() { } }
[HarmonyPatch(typeof(MyCubeGrid), "UpdateBeforeSimulation10")]
public static class Every10Patch
{
    public static void Postfix() { var seen = new HashSet<long>(); }
}
[HarmonyPatch(typeof(Grid), "UpdateBeforeSimulation")]
public static class OwnTypePatch
{
    public static void Postfix() { var seen = new HashSet<long>(); }
}
""");

        /// <summary>A method's name never makes it per-frame, in any compilation.</summary>
        [Fact]
        public void NamesAloneAreSilent()
        {
            const string source = """
using System.Collections.Generic;
using System.Linq;
public class Session
{
    private readonly List<int> _items = new List<int>();
    public void UpdateAfterSimulation() { var big = _items.Where(x => x > 1).ToList(); }
    public void UpdateMessageAsync() { var s = $"{_items.Count}"; }
    public void Draw() { var copy = new List<int>(_items); }
    public void OnTick() { var a = _items.ToArray(); }
}
""";
            Silent("DR023", source);
            Silent("DR023", source, host: Host.None);
        }

        [Fact]
        public void ConditionalThrottledAndLoopWorkIsSilent() => Silent("DR023", """
using System.Collections.Generic;
using System.Linq;
using VRage.Game.Components;
public class Session : MySessionComponentBase
{
    private readonly List<int> _items = new List<int>();
    private int _tick;
    private bool _dirty;
    public override void UpdateBeforeSimulation()
    {
        if (_dirty) { var changed = _items.ToList(); _dirty = false; }
        foreach (var i in _items) { var s = i.ToString() + "x"; }
        var name = _dirty ? string.Join(",", _items) : "";
    }
    public override void UpdateAfterSimulation()
    {
        if (++_tick % 60 != 0) return;
        var snapshot = _items.ToArray();
    }
    public override void UpdatingStopped() { var all = _items.ToList(); }
}
""", host: Host.Game);

        /// <summary>Two direct calls below a real override on the unconditional path: per frame, and the message names the chain.</summary>
        [Fact]
        public void MethodTwoCallsBelowAnOverrideFires()
        {
            var d = Fires("DR023", """
using System.Collections.Generic;
using System.Linq;
using VRage.Plugins;
public class Plugin : IPlugin
{
    private readonly Session _session = new Session();
    public void Init(object game) { }
    public void Update() { _session.Update(); }
    public void Dispose() { }
}
public class Session
{
    private readonly Commands _commands = new Commands();
    public void Update() { _commands.Tick(); }
}
public class Commands
{
    private readonly List<int> _due = new List<int>();
    public void Tick() { var now = _due.Where(x => x > 0).ToList(); }
}
""", 1, "_due.Where", host: Host.Game);
            Assert.Contains("Plugin.Update (overrides IPlugin.Update) \u2192 Session.Update \u2192 Commands.Tick", d[0].GetMessage());
        }

        /// <summary>The same name, not reached (only through a delegate, behind a condition, or not called at all), stays silent.</summary>
        [Fact]
        public void UnreachedOrConditionallyCalledMethodsAreSilent() => Silent("DR023", """
using System;
using System.Collections.Generic;
using System.Linq;
using VRage.Plugins;
public class Plugin : IPlugin
{
    private readonly Commands _commands = new Commands();
    private readonly Other _other = new Other();
    private int _tick;
    public void Init(object game) { }
    public void Update()
    {
        Run(_commands.Tick);
        if (++_tick % 60 == 0) _other.Tick();
    }
    public void Dispose() { }
    private static void Run(Action a) => a();
}
public class Commands
{
    private readonly List<int> _due = new List<int>();
    public void Tick() { var now = _due.Where(x => x > 0).ToList(); }
}
public class Other
{
    private readonly List<int> _due = new List<int>();
    public void Tick() { var now = _due.Where(x => x > 0).ToList(); }
}
public class Unused
{
    private readonly List<int> _due = new List<int>();
    public void Tick() { var now = _due.Where(x => x > 0).ToList(); }
    public void Update() { var copy = new List<int>(_due); }
}
""", host: Host.Game);

        [Fact]
        public void LogInAMethodReachedEveryFrame()
        {
            var d = Fires("DR024", """
using Game.Stubs;
using VRage.Game.Components;
class Punisher : MyGameLogicComponent
{
    private readonly Checker _checker = new Checker();
    public override void UpdateAfterSimulation() { _checker.Check(); }
}
class Checker
{
    private bool _broken;
    public void Check() { if (_broken) Log.Error("script still over the limit"); }
}
""", 1, "Log.Error", host: Host.Game);
            Assert.Contains("reached through Punisher.UpdateAfterSimulation", d[0].GetMessage());
        }

        [Fact]
        public void CleanUpdateIsSilent() => Silent("DR023", """
using VRage.Game.Components;
public class Session : MySessionComponentBase
{
    private int _ticks;
    private readonly int[] _buffer = new int[16];
    public override void UpdateBeforeSimulation() { _ticks++; _buffer[_ticks % 16] = _ticks; }
}
""", host: Host.Game);
    }

    public class DR024Tests
    {
        [Fact]
        public void LogInUnboundedLoop() => Fires("DR024", """
using System.Collections.Generic;
using Game.Stubs;
class Checker
{
    private readonly Queue<int> _values = new Queue<int>();
    private bool _running = true;
    public void Run() { while (_running) { var v = _values.Dequeue(); if (v < 0) Log.Warn("negative value"); } }
}
""", 1, "Log.Warn");

        [Fact]
        public void LogInFiniteForeachIsSilent() => Silent("DR024", """
using System.Collections.Generic;
class Log { public static void Warn(string m) { } }
class Checker
{
    public void Check(List<int> values) { foreach (var v in values) if (v < 0) Log.Warn("negative value"); }
    public void Count(int[] values) { for (var i = 0; i < values.Length; i++) if (values[i] < 0) Log.Warn("negative"); }
}
""", host: Host.None);

        [Fact]
        public void LogInPerFrameOverrideBehindPlainFlag() => Fires("DR024", """
using Game.Stubs;
using VRage.Game.Components;
class Punisher : MyGameLogicComponent
{
    private bool _broken;
    public override void UpdateAfterSimulation() { if (_broken) Log.Error("script still over the limit"); }
}
""", 1, "Log.Error", host: Host.Game);

        [Fact]
        public void LogInMethodNamedLikeATickIsSilent() => Silent("DR024", """
using Game.Stubs;
class Punisher
{
    private bool _broken;
    public void UpdateAfterSimulation() { if (_broken) Log.Error("script still over the limit"); }
}
""");

        [Fact]
        public void TimeGuardIsSilent() => Silent("DR024", """
using System;
using System.Collections.Generic;
using Game.Stubs;
class Checker
{
    private DateTime _nextLog;
    public void Check(Queue<int> values)
    {
        while (values.Count > 0)
        {
            var v = values.Dequeue();
            if (v >= 0) continue;
            if (DateTime.UtcNow >= _nextLog) { _nextLog = DateTime.UtcNow.AddMinutes(1); Log.Warn("negative value"); }
        }
    }
}
""");

        [Fact]
        public void DedupeSetAndEarlyReturnAreSilent() => Silent("DR024", """
using System.Collections.Generic;
using Game.Stubs;
using VRage.Game.Components;
class Punisher : MyGameLogicComponent
{
    private readonly HashSet<long> _warned = new HashSet<long>();
    private long _id;
    private string? _problem;
    private string? _lastProblem;
    public override void UpdateAfterSimulation()
    {
        if (_warned.Add(_id)) Log.Warn("over the limit");
        if (_problem == _lastProblem) return;
        _lastProblem = _problem;
        Log.Info(_problem ?? "");
    }
}
""", host: Host.Game);
    }

    public class DR025Tests
    {
        [Fact]
        public void ListOnlyAdded() => Fires("DR025", """
using System.Collections.Generic;
class Seen
{
    private readonly List<long> _seen = new List<long>();
    public void Saw(long id) => _seen.Add(id);
    public bool Has(long id) => _seen.Contains(id);
}
""", 1, "_seen");

        [Fact]
        public void DictionaryIndexerOnly() => Fires("DR025", """
using System.Collections.Generic;
class Names
{
    private readonly Dictionary<long, string> _names = new Dictionary<long, string>();
    public void Set(long id, string n) { _names[id] = n; }
    public string Get(long id) => _names.TryGetValue(id, out var n) ? n : "";
}
""", 1, "_names");

        [Fact]
        public void RemovedOrCappedIsSilent() => Silent("DR025", """
using System.Collections.Generic;
class Seen
{
    private readonly List<long> _seen = new List<long>();
    private readonly Queue<string> _recent = new Queue<string>();
    public void Saw(long id) => _seen.Add(id);
    public void Gone(long id) => _seen.Remove(id);
    public void Note(string s) { _recent.Enqueue(s); if (_recent.Count > 100) _recent.Dequeue(); }
}
""");

        [Fact]
        public void PublicOrEscapingIsSilent() => Silent("DR025", """
using System.Collections.Generic;
class Registry
{
    public List<int> Items = new List<int>();
    private readonly List<int> _pending = new List<int>();
    public void Add(int v) { Items.Add(v); _pending.Add(v); }
    public void Flush(Sink s) => s.Drain(_pending);
}
class Sink { public void Drain(List<int> l) => l.Clear(); }
""");
    }
}
