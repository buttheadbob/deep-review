using Xunit;
using static DeepRules.Tests.Harness;

namespace DeepRules.Tests
{
    public class DR016Tests
    {
        [Fact]
        public void WriteAllTextAndStreamWriter() => Fires("DR016", """
using System.IO;
class Store
{
    public void Save(string path, string json) { File.WriteAllText(path, json); }
    public void Export(string path) { using (var w = new StreamWriter(path)) w.Write("x"); }
}
""", 2);

        [Fact]
        public void FileStreamCreate() => Fires("DR016", """
using System.IO;
class Store
{
    public void Save(string path, byte[] data)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write)) fs.Write(data, 0, data.Length);
    }
}
""", 1, "FileMode.Create");

        [Fact]
        public void TempThenReplaceIsSilent() => Silent("DR016", """
using System.IO;
class Store
{
    public void Save(string path, string json)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Replace(tmp, path, null);
    }
}
""");

        [Fact]
        public void AppendAndCallerMoveAreSilent() => Silent("DR016", """
using System.IO;
class Store
{
    public void Log(string path, string line) { File.AppendAllText(path, line); }
    public void Save(string target, string json)
    {
        var staging = target + ".part";
        Write(staging, json);
        File.Move(staging, target);
    }
    private void Write(string path, string json) { File.WriteAllText(path, json); }
}
""");
    }

    public class DR017Tests
    {
        [Fact]
        public void ImplicitEnumInConfig() => Fires("DR017", """
public enum Punishment { TurnOff, Damage, Destroy }
public class LimiterConfig
{
    public Punishment InstantKillPunishment { get; set; }
    public Punishment? MemoryPunishment { get; set; }
}
""", 2, "Punishment");

        [Fact]
        public void IntCastFromParseWithoutIsDefined() => Fires("DR017", """
public enum Mode { Off, On }
public class Commands
{
    public Mode Read(string text) { var n = int.Parse(text); return (Mode)n; }
}
""", 1, "(Mode)n");

        [Fact]
        public void ExplicitValuesAndIgnoredAreSilent() => Silent("DR017", """
using System.Xml.Serialization;
public enum Mode { Off = 0, On = 1 }
public enum Shade { Light, Dark }
public class PluginSettings
{
    public Mode Mode { get; set; }
    [XmlIgnore] public Shade Shade { get; set; }
}
""");

        [Fact]
        public void NotSerializedAndCheckedCastAreSilent() => Silent("DR017", """
using System;
public enum Mode { Off, On }
public class Worker
{
    public Mode Current { get; set; }
    public Mode Read(string text)
    {
        var n = int.Parse(text);
        if (!Enum.IsDefined(typeof(Mode), n)) throw new ArgumentException(text);
        return (Mode)n;
    }
}
""");
    }

    public class DR018Tests
    {
        [Fact]
        public void InterpolationIntoFileAndToString() => Fires("DR018", """
using System.IO;
class History
{
    public void Save(string dir, double load) { File.WriteAllText(Path.Combine(dir, "load.txt"), $"{load}"); }
    public string Label(double v) => v.ToString("F2");
}
""", 2);

        [Fact]
        public void ParseWithoutProviderAndConcatIntoFileName() => Fires("DR018", """
using System;
class History
{
    public double Read(string s) => double.Parse(s);
    public string FileFor(DateTime day) { string fileName = "history-" + day + ".csv"; return fileName; }
}
""", 2);

        [Fact]
        public void InvariantCultureIsSilent() => Silent("DR018", """
using System;
using System.Globalization;
using System.IO;
class History
{
    public void Save(string dir, double load) { File.WriteAllText(Path.Combine(dir, "load.txt"), FormattableString.Invariant($"{load}")); }
    public string Label(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    public double Read(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    public string Stamp(DateTime t) => t.ToString("o");
    public string Span(TimeSpan t) => t.ToString();
}
""");

        [Fact]
        public void LoggingOnlyIsSilent() => Silent("DR018", """
using Game.Stubs;
class Monitor
{
    public void Report(double ms) { Log.Info($"tick took {ms} ms"); Log.Info("tick " + ms.ToString("F1")); }
}
""");
    }

    public class DR019Tests
    {
        [Fact]
        public void TimeSpanParse() => Fires("DR019", """
using System;
class Cfg { public TimeSpan Read(string s) => TimeSpan.Parse(s); public bool Try(string s, out TimeSpan t) => TimeSpan.TryParse(s, out t); }
""", 2);

        [Fact]
        public void ConfigIntervalUsedUnchecked() => Fires("DR019", """
using System;
using System.Threading.Tasks;
public class PluginConfig { public TimeSpan Interval { get; set; } public int DelayMs { get; set; } }
public class Runner
{
    private readonly PluginConfig _config = new PluginConfig();
    private DateTime _last;
    public async Task Loop() { await Task.Delay(_config.DelayMs); }
    public bool Due() => DateTime.UtcNow - _last >= _config.Interval;
}
""", 2);

        [Fact]
        public void ParseExactIsSilent() => Silent("DR019", """
using System;
using System.Globalization;
class Cfg { public TimeSpan Read(string s) => TimeSpan.ParseExact(s, "c", CultureInfo.InvariantCulture); }
""");

        [Fact]
        public void GuardedAndConstantDelaysAreSilent() => Silent("DR019", """
using System;
using System.Threading.Tasks;
public class PluginConfig { public TimeSpan Interval { get; set; } }
public class Runner
{
    private readonly PluginConfig _config = new PluginConfig();
    public async Task Loop()
    {
        if (_config.Interval <= TimeSpan.Zero) return;
        await Task.Delay(_config.Interval);
        await Task.Delay(TimeSpan.FromSeconds(5));
    }
}
""");
    }

    public class DR020Tests
    {
        [Fact]
        public void NowStoredAndSubtracted() => Fires("DR020", """
using System;
class Timeouts
{
    private DateTime _start;
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(5);
    public void Begin() { _start = DateTime.Now; }
    public bool Expired() => DateTime.Now - _start > _timeout;
}
""", 2);

        [Fact]
        public void NowInLocalThenCompared() => Fires("DR020", """
using System;
class Timeouts
{
    public bool Before(DateTime deadline) { var now = DateTime.Now; return now < deadline; }
    public DateTime Later() => DateTime.Now.AddMinutes(5);
}
""", 2);

        [Fact]
        public void UtcNowIsSilent() => Silent("DR020", """
using System;
class Timeouts
{
    private DateTime _start;
    public void Begin() { _start = DateTime.UtcNow; }
    public bool Expired() => DateTime.UtcNow - _start > TimeSpan.FromMinutes(5);
}
""");

        [Fact]
        public void DisplayAndTimeOfDayAreSilent() => Silent("DR020", """
using System;
class Clock
{
    public int Hour() => DateTime.Now.Hour;
    public string Show() => "now: " + DateTime.Now;
}
""");
    }
}
