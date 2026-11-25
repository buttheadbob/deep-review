using System.Linq;
using Xunit;
using static DeepRules.Tests.Harness;

namespace DeepRules.Tests
{
    /// <summary>
    /// Minimal, self-contained reproductions of the real defects DeepRules was built for. Game and SDK types are stubbed.
    /// </summary>
    public class RegressionTests
    {
        private const string SensStubs = """
namespace SensEssentials
{
    internal static class Log
    {
        public static void Info(string m) { }
        public static void Warn(string m) { }
        public static void Error(System.Exception e, string m) { }
    }

    internal static class EssentialsCore
    {
        public static bool IsServerRunning = true;
        public static void RunCommand(string command, string source) { }
    }

    internal static class CommandSequence
    {
        internal static async System.Threading.Tasks.Task Run(System.Collections.Generic.IReadOnlyList<(string Command, System.TimeSpan Delay)> steps, string source, System.Threading.CancellationToken token)
        {
            foreach (var (command, delay) in steps)
            {
                EssentialsCore.RunCommand(command, source);
                await System.Threading.Tasks.Task.Delay(delay, token).ConfigureAwait(false);
            }
        }
    }

    public class CommandStep
    {
        internal System.TimeSpan DelaySpan;
        public string Command { get; set; } = "";

        /// <summary>Runs the command now. Only call this on the game thread.</summary>
        public void RunStep(string owner)
        {
            if (!EssentialsCore.IsServerRunning || string.IsNullOrEmpty(Command)) return;
            EssentialsCore.RunCommand(Command, "Auto command '" + owner + "'");
        }
    }
}
""";

        /// <summary>The shape of SensEssentials Shared/Config/AutoCommand.cs at 0b51e8a.</summary>
        private const string SensOriginal = SensStubs + """
namespace SensEssentials
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    public class AutoCommand
    {
        private TimeSpan _interval = TimeSpan.FromMinutes(1);
        private DateTime _nextRun = DateTime.MinValue;
        private int _currentStep;
        private string _name = "";
        private readonly object _sync = new object();
        private readonly List<CancellationTokenSource> _runs = new List<CancellationTokenSource>();
        private const int MaxRuns = 8;

        public List<CommandStep> Steps { get; } = new List<CommandStep>();

        public void Update()
        {
            lock (_sync)
            {
                if (DateTime.UtcNow < _nextRun)
                    return;
                var steps = Steps.ToList();
                if (steps.Count <= 0)
                    return;
                if (_currentStep >= steps.Count)
                    _currentStep = 0;
                var step = steps[_currentStep];
                step.RunStep(_name);
                _currentStep++;
                _nextRun = DateTime.UtcNow + step.DelaySpan;
                if (_currentStep < steps.Count) return;
                _currentStep = 0;
                _nextRun = DateTime.UtcNow + _interval;
            }
        }

        internal async void RunNow()
        {
            var cts = new CancellationTokenSource();
            lock (_sync)
            {
                if (_runs.Count >= MaxRuns)
                {
                    Log.Warn("already has " + MaxRuns + " runs going");
                    cts.Dispose();
                    return;
                }
                _runs.Add(cts);
            }

            try
            {
                var steps = Steps.Select(step => (step.Command, step.DelaySpan)).ToList();
                await CommandSequence.Run(steps, "Auto command '" + _name + "'", cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Log.Error(e, "Auto command '" + _name + "' failed");
            }
            finally
            {
                lock (_sync)
                    _runs.Remove(cts);
                cts.Dispose();
            }
        }

        internal void Cancel()
        {
            Log.Info("Cancelling autocommand " + _name);
            lock (_sync)
            {
                foreach (var run in _runs)
                    run.Cancel();

                _currentStep = 0;
                _nextRun = DateTime.UtcNow + _interval;
            }
        }
    }
}
""";

        private const string SensFixedCore = SensStubs + """
namespace SensEssentials
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    public class AutoCommand
    {
        private TimeSpan _interval = TimeSpan.FromMinutes(1);
        private DateTime _nextRun = DateTime.MinValue;
        private int _currentStep;
        private string _name = "";
        private readonly object _sync = new object();
        private readonly List<CancellationTokenSource> _runs = new List<CancellationTokenSource>();
        private int _scheduleVersion;
        private const int MaxRuns = 8;

        public List<CommandStep> Steps { get; } = new List<CommandStep>();

        public void Update()
        {
            CommandStep step;
            bool last;
            int version;
            lock (_sync)
            {
                if (DateTime.UtcNow < _nextRun)
                    return;
                var steps = Steps.ToList();
                if (steps.Count <= 0)
                    return;
                if (_currentStep >= steps.Count)
                    _currentStep = 0;
                step = steps[_currentStep];
                _currentStep++;
                last = _currentStep >= steps.Count;
                if (last)
                    _currentStep = 0;
                _nextRun = NextRunAfter(step, last);
                version = _scheduleVersion;
            }

            step.RunStep(_name);

            lock (_sync)
            {
                if (_scheduleVersion == version)
                    _nextRun = NextRunAfter(step, last);
            }
        }

        private DateTime NextRunAfter(CommandStep step, bool last)
        {
            if (!last)
                return DateTime.UtcNow + step.DelaySpan;
            return DateTime.UtcNow + _interval;
        }

        internal async void RunNow()
        {
            var cts = new CancellationTokenSource();
            lock (_sync)
            {
                if (_runs.Count >= MaxRuns)
                {
                    Log.Warn("already has " + MaxRuns + " runs going");
                    cts.Dispose();
                    return;
                }
                _runs.Add(cts);
            }

            try
            {
                var steps = Steps.Select(step => (step.Command, step.DelaySpan)).ToList();
                await CommandSequence.Run(steps, "Auto command '" + _name + "'", cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Log.Error(e, "Auto command '" + _name + "' failed");
            }
            finally
            {
                // Not disposed: Cancel may still be cancelling it from a copy of the list.
                lock (_sync)
                    _runs.Remove(cts);
            }
        }

        internal void Cancel()
        {
            Log.Info("Cancelling autocommand " + _name);
            CancellationTokenSource[] runs;
            lock (_sync)
            {
                runs = _runs.ToArray();
                _scheduleVersion++;
                _currentStep = 0;
                _nextRun = DateTime.UtcNow + _interval;
            }

            foreach (var run in runs)
                run.Cancel();
        }
/*RUNIFIDLE*/
    }
}
""";

        private static readonly string SensFixed = SensFixedCore.Replace("/*RUNIFIDLE*/", "");

        private static readonly string SensFixedWithRunIfIdle = SensFixedCore.Replace("/*RUNIFIDLE*/", """
        internal void RunIfIdle()
        {
            lock (_sync)
            {
                if (_runs.Count == 0)
                    RunNow();
            }
        }
""");

        [Fact]
        public void SensEssentials_0b51e8a_FiresAllFive()
        {
            Fires("DR001", SensOriginal, 1, "run.Cancel()");
            var dr002 = Fires("DR002", SensOriginal, 1, "foreach (var run in _runs)");
            Assert.Contains("RunNow (async, finally) via Remove", dr002[0].GetMessage());
            Fires("DR003", SensOriginal, 1, "step.RunStep(_name)");
            Fires("DR004", SensOriginal, 1, "internal async void RunNow()");
            var dr006 = Fires("DR006", SensOriginal, 1);
            // the finally's Dispose, not the early-return Dispose before the CTS was stored
            var finallyDispose = SensOriginal.Split('\n').Select((text, i) => (text, line: i + 1))
                .Where(x => x.text.Trim() == "cts.Dispose();").Select(x => x.line).Last();
            Assert.Equal(finallyDispose, LineOf(dr006[0]));
        }

        [Fact]
        public void SensEssentials_Fixed_NoneOfTheFour()
        {
            Silent("DR001", SensFixed);
            Silent("DR035", SensFixed);
            Silent("DR002", SensFixed);
            Silent("DR003", SensFixed);
            Silent("DR006", SensFixed);
        }

        /// <summary>
        /// The fixed file still calls RunNow() under _sync from RunIfIdle. RunNow is async void, so its synchronous part
        /// (up to CommandSequence.Run's first await) runs inside the lock: DR003 reports that call and nothing else.
        /// </summary>
        [Fact]
        public void SensEssentials_Fixed_RunIfIdleIsTheOnlyLockLead()
        {
            var d = Fires("DR003", SensFixedWithRunIfIdle, 1, "RunNow();");
            Assert.Contains("CommandSequence.Run", d[0].GetMessage());
            Silent("DR001", SensFixedWithRunIfIdle);
            Silent("DR002", SensFixedWithRunIfIdle);
            Silent("DR006", SensFixedWithRunIfIdle);
        }

        /// <summary>The shape of Advanced PB Limiter Shared/Utils/InstantPunishmentFile.cs at 7424144.</summary>
        private const string ApblFile = """
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace Advanced_PB_Limiter.Utils
{
    public static class InstantPunishmentFile
    {
        public static readonly string[] Settings = { "InstantKillPunishment", "MemoryPunishment" };

        public static List<string> ReplaceDamage(string path)
        {
            List<string> changed = new List<string>();
            if (!File.Exists(path)) return changed;

            byte[] original = File.ReadAllBytes(path);
            XmlDocument document = new XmlDocument { PreserveWhitespace = true };
            using (MemoryStream read = new MemoryStream(original))
                document.Load(read);
            XmlElement? root = document.DocumentElement;
            if (root is null) return changed;

            foreach (string name in Settings)
            {
                XmlElement? element = root[name];
                if (element is null || element.InnerText.Trim() != "Damage") continue;
                element.InnerText = "TurnOff";
                changed.Add(name);
            }

            if (changed.Count > 0)
            {
                bool bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
                XmlWriterSettings settings = new XmlWriterSettings { Encoding = new UTF8Encoding(bom), Indent = false, NewLineHandling = NewLineHandling.None };
                using MemoryStream written = new MemoryStream();
                using (XmlWriter writer = XmlWriter.Create(written, settings))
                    document.Save(writer);
                File.WriteAllBytes(path, written.ToArray());
            }
            return changed;
        }
    }
}
""";

        [Fact]
        public void Apbl_7424144_InPlaceWrite() => Fires("DR016", ApblFile, 1, "File.WriteAllBytes(path, written.ToArray());");

        /// <summary>The shape of APBL's ServerPlugin/InstantPunishmentJsonConverter.cs at 7424144 and the config that uses it.</summary>
        private const string ApblConverter = """
#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Advanced_PB_Limiter.Utils
{
    public static class Enums
    {
        public enum InstantPunishment
        {
            TurnOff,
            Destroy
        }
    }
}

namespace Advanced_PB_Limiter.MagnetarHost
{
    using Advanced_PB_Limiter.Utils;

    internal sealed class InstantPunishmentJsonConverter : JsonConverter<Enums.InstantPunishment>
    {
        internal static bool ReadDamage { get; set; }

        public override Enums.InstantPunishment Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number) && Enum.IsDefined(typeof(Enums.InstantPunishment), number))
                return (Enums.InstantPunishment)number;

            string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (text is not null && Enum.TryParse(text, ignoreCase: false, out Enums.InstantPunishment value) && Enum.IsDefined(typeof(Enums.InstantPunishment), value))
                return value;

            if (string.Equals(text, "Damage", StringComparison.Ordinal))
            {
                ReadDamage = true;
                return Enums.InstantPunishment.TurnOff;
            }

            throw new JsonException($"'{text}' is not an instant kill punishment. Use TurnOff or Destroy.");
        }

        public override void Write(Utf8JsonWriter writer, Enums.InstantPunishment value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }

    public class LimiterConfig
    {
        [JsonConverter(typeof(InstantPunishmentJsonConverter))]
        public Enums.InstantPunishment InstantKillPunishment { get; set; }
    }
}
""";

        [Fact]
        public void Apbl_7424144_ConverterAcceptsNumbers()
        {
            var d = Fires("DR017", ApblConverter, 2, "return (Enums.InstantPunishment)number;", net48: false);
            Assert.Contains(d, x => x.GetMessage().Contains("accepts a number for enum InstantPunishment"));
            Assert.Contains(d, x => LineText(x).Contains("InstantKillPunishment"));
        }
    }
}
