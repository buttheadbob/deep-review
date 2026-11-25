// Runtime matrix harness template.
//
// How to use it for a review:
//  1. Copy this folder (run_matrix.py --new <name> does it) and keep Matrix.cs unchanged.
//  2. Paste the plugin code under test into PluginCode.cs (or more files next to it), read-only from the repo at the
//     reviewed commit (git show <sha>:<path>). Keep the original line order and logic; change nothing that matters to
//     the behaviour being shown.
//  3. Stub every game, Torch, Magnetar or PluginSdk type it touches with the smallest stand-in that keeps the behaviour
//     (Log -> Console, MySandboxGame.Static.Invoke -> GameThread.Invoke, MyAPIGateway.Session -> a fake, and so on).
//     Never reference game DLLs: the harness must build and run anywhere, and only code written for it runs.
//  4. Write checks below with Matrix.Check / Repeat / Count. A check that should fail on one runtime and pass on the
//     other is the evidence: run_matrix.py builds net48 and net10.0, runs both, and prints which checks differ.
//  5. Add the fixed version next to the original and check it passes on both.
//
// Both runtimes compile the same code with LangVersion latest, so stay in the API subset net48 has (no
// System.Threading.Lock, no CancellationTokenSource.CancelAsync, no ArgumentNullException.ThrowIfNull).
using System;

namespace Harness;

public static class Program
{
    public static int Main()
    {
        Matrix.Note("runtime " + Matrix.Runtime);
        using var game = new GameThread();

        Matrix.Check("example: the game thread has no SynchronizationContext", () =>
        {
            bool none = false;
            game.Run(() => none = System.Threading.SynchronizationContext.Current is null);
            Matrix.Expect(none, "SynchronizationContext.Current is null on the game thread");
        });

        return Matrix.Finish();
    }
}
