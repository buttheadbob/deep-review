// Stand-ins for the game and plugin types the pasted code touches. Nothing here references a game DLL.
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Sandbox
{
    /// <summary>MySandboxGame.Static.Invoke queues an action for the game thread (the harness GameThread stand-in).</summary>
    public sealed class MySandboxGame
    {
        public static MySandboxGame Static { get; } = new();

        public static Harness.GameThread? Thread { get; set; }

        public void Invoke(Action action, string invokerName) =>
            (Thread ?? throw new InvalidOperationException("No game thread stand-in")).Invoke(action);
    }
}

namespace SensEssentials
{
    public enum Trigger
    {
        Disabled,
        Timed,
        Scheduled,
    }

    public sealed class AutoCommandStep
    {
        public string Command { get; set; } = "";

        internal TimeSpan DelaySpan;
    }

    public static class Log
    {
        public static void Info(string message) => Console.WriteLine("LOG info " + message);

        public static void Warn(string message) => Console.WriteLine("LOG warn " + message);

        public static void Error(Exception e, string message) => Console.WriteLine($"LOG error {message}: {e.GetType().Name}: {e.Message}");
    }

    public static class EssentialsCore
    {
        public static readonly ConcurrentQueue<string> Ran = new();

        public static HostStub Host { get; } = new();

        public static void RunCommand(string command, string source) => Ran.Enqueue(command);

        public sealed class HostStub
        {
            public bool IsServerRunning => true;
        }
    }
}
