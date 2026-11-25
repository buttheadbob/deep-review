using Xunit;
using static DeepRules.Tests.Harness;

namespace DeepRules.Tests
{
    /// <summary>DR036: periodic work DR023 cannot prove runs every time.</summary>
    public class DR036Tests
    {
        /// <summary>
        /// Sens Essentials' auto command and sim speed timers: a System.Timers.Timer handler with a guard queues a lambda,
        /// the lambda calls an abstract Tick, and the override builds a LINQ list after its own guards. DR023 saw none of it.
        /// </summary>
        [Fact]
        public void TimerHandlerThroughLambdaAndOverrideFires()
        {
            var d = Fires("DR036", """
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
public static class GameQueue { public static void Invoke(Action action) => action(); }
public abstract class GameThreadTimer
{
    private System.Timers.Timer? _timer;
    private int _queued;
    public void Start() { _timer = new System.Timers.Timer(1000); _timer.Elapsed += TimerElapsed; _timer.Start(); }
    protected abstract void Tick();
    private void TimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        GameQueue.Invoke(() => { try { Tick(); } finally { Interlocked.Exchange(ref _queued, 0); } });
    }
}
public sealed class SimSpeedEvents : GameThreadTimer
{
    private readonly List<int> _commands = new List<int>();
    public object? Config;
    protected override void Tick()
    {
        if (Config == null) return;
        var commands = _commands.Where(c => c > 0).ToList();
    }
}
""", 1, onLine: "ToList", host: Host.None);
            var message = d[0].GetMessage();
            Assert.Contains("runs on every timer tick", message);
            Assert.Contains("GameThreadTimer.TimerElapsed (System.Timers.Timer.Elapsed handler)", message);
            Assert.Contains("SimSpeedEvents.Tick", message);
            Assert.Contains("LINQ", message);
        }

        [Fact]
        public void ThreadingTimerLambdaCallbackFires()
        {
            var d = Fires("DR036", """
using System.Collections.Generic;
using System.Threading;
public sealed class Poller
{
    private Timer? _timer;
    public void Start() => _timer = new Timer(_ => { var seen = new List<int>(); }, null, 0, 1000);
}
""", 1, onLine: "new List", host: Host.None);
            Assert.Contains("a lambda in Poller.Start (System.Threading.Timer callback)", d[0].GetMessage());
        }

        /// <summary>Behind a branch in a per-frame override: DR023 stays silent (it may be throttled), DR036 asks a reviewer.</summary>
        [Fact]
        public void GuardedWorkInAPerFrameOverrideIsReviewOnly()
        {
            const string source = """
using System.Collections.Generic;
using System.Linq;
using VRage.Game.Components;
public class Session : MySessionComponentBase
{
    private readonly List<int> _items = new List<int>();
    private bool _dirty;
    public override void UpdateBeforeSimulation()
    {
        if (_dirty) { var changed = _items.ToList(); _dirty = false; }
    }
}
""";
            Silent("DR023", source, host: Host.Game);
            var d = Fires("DR036", source, 1, onLine: "ToList", host: Host.Game);
            Assert.Contains("runs every frame", d[0].GetMessage());
        }

        /// <summary>Work DR023 already reports is not reported again.</summary>
        [Fact]
        public void WorkDr023ReportsIsNotRepeated()
        {
            const string source = """
using System.Collections.Generic;
using System.Linq;
using VRage.Game.Components;
public class Session : MySessionComponentBase
{
    private readonly List<int> _items = new List<int>();
    public override void UpdateAfterSimulation() { var all = _items.ToList(); }
}
""";
            Fires("DR023", source, 1, host: Host.Game);
            Silent("DR036", source, host: Host.Game);
        }

        /// <summary>Code no per-frame override and no timer callback reaches is never reported, whatever its name.</summary>
        [Fact]
        public void UnreachedCodeIsSilent() => Silent("DR036", """
using System.Collections.Generic;
using System.Linq;
public class Session
{
    private readonly List<int> _items = new List<int>();
    private System.Timers.Timer? _timer;
    public void Start() { _timer = new System.Timers.Timer(1000); _timer.Elapsed += (s, e) => { }; }
    public void UpdateAfterSimulation() { var big = _items.Where(x => x > 1).ToList(); }
    public void OnTick() { var a = new List<int>(_items); }
}
""", host: Host.None);
    }
}
