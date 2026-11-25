using Xunit;
using static DeepRules.Tests.Harness;

namespace DeepRules.Tests
{
    /// <summary>DR038: Process.Start may only open a fixed local web page.</summary>
    public class DR038Tests
    {
        /// <summary>Every accepted form: a constant, an interpolation or concatenation that starts with the loopback host, a local set once, a static readonly field in the same file.</summary>
        [Fact]
        public void FixedLoopbackPagesAreSilent() => Silent("DR038", """
using System.Diagnostics;
public static class Links
{
    private static readonly string Page = "http://localhost/";
    public static void Open(int port, string path)
    {
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/") { UseShellExecute = true });
        Process.Start("http://localhost:8080/stats");
        Process.Start("https://127.0.0.1");
        Process.Start("http://127.0.0.1:" + port + "/graphs?range=" + path);
        Process.Start($"http://localhost/{path}");
        Process.Start($"http://localhost:{port}");
        var ipv6 = "https://[::1]:5000/";
        Process.Start(ipv6);
        Process.Start(new ProcessStartInfo { FileName = Page, UseShellExecute = true, Verb = "open" });
        var psi = new ProcessStartInfo($"http://localhost:{port}/");
        psi.UseShellExecute = true;
        psi.Verb = "Open";
        using (Process.Start(psi)) { }
    }
}
""", host: Host.None);

        [Fact]
        public void AnotherHostFires()
        {
            var d = Fires("DR038", """
using System.Diagnostics;
public static class Links
{
    public static void Open() => Process.Start(new ProcessStartInfo("https://example.org/help") { UseShellExecute = true });
}
""", 1, onLine: "example.org", host: Host.None);
            Assert.Contains("'example.org'", d[0].GetMessage());
        }

        /// <summary>A host the constant text does not close can become anything: 127.0.0.1.example.org, or user info before '@'.</summary>
        [Fact]
        public void HostSplitAcrossAHoleFires() => Fires("DR038", """
using System.Diagnostics;
public static class Links
{
    public static void Open(string rest, string port, int number)
    {
        Process.Start($"http://127.0.0.1{rest}");
        Process.Start("http://localhost:" + port + "/");
        Process.Start($"http://localhost:{number}{rest}");
        Process.Start($"http://localhost:{number:'@example.org/'0}/");
        Process.Start("http://localhost:80@example.org/");
    }
}
""", 5, host: Host.None);

        [Fact]
        public void UrlFromAParameterFires() => Fires("DR038", """
using System.Diagnostics;
public static class Links
{
    public static void Open(string url, ProcessStartInfo info)
    {
        Process.Start(url);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        Process.Start(info);
    }
}
""", 3, host: Host.None);

        [Fact]
        public void ArgumentsSetFires() => Fires("DR038", """
using System.Diagnostics;
public static class Links
{
    public static void Open()
    {
        Process.Start(new ProcessStartInfo("http://localhost/", "--flag"));
        Process.Start(new ProcessStartInfo("http://localhost/") { Arguments = "--flag" });
        var psi = new ProcessStartInfo("http://localhost/");
        psi.Arguments = "--flag";
        Process.Start(psi);
    }
}
""", 3, host: Host.None);

        [Fact]
        public void ExecutablePathFires() => Fires("DR038", """
using System.Diagnostics;
public static class Tools
{
    public static void Run()
    {
        Process.Start(@"C:\Windows\System32\cmd.exe");
        Process.Start(new ProcessStartInfo { FileName = "notepad.exe" });
    }
}
""", 2, host: Host.None);

        [Fact]
        public void OtherVerbFires() => Fires("DR038", """
using System.Diagnostics;
public static class Links
{
    public static void Open() => Process.Start(new ProcessStartInfo("http://localhost/") { UseShellExecute = true, Verb = "runas" });
}
""", 1, onLine: "runas", host: Host.None);

        /// <summary>A static readonly field set again in the static constructor no longer shows its value at the declaration.</summary>
        [Fact]
        public void FieldAssignedAgainFires() => Fires("DR038", """
using System;
using System.Diagnostics;
public static class Links
{
    private static readonly string Page = "http://localhost/";
    static Links() { Page = Environment.GetEnvironmentVariable("PAGE") ?? Page; }
    public static void Open() => Process.Start(Page);
}
""", 1, host: Host.None);

        /// <summary>A local the code hands on or changes through ArgumentList cannot be followed.</summary>
        [Fact]
        public void StartInfoHandedOnFires() => Fires("DR038", """
using System.Diagnostics;
public static class Links
{
    public static void Open()
    {
        var psi = new ProcessStartInfo("http://localhost/");
        Change(psi);
        Process.Start(psi);
    }
    private static void Change(ProcessStartInfo info) { }
}
""", 1, onLine: "Change(psi)", host: Host.None);

        [Fact]
        public void MethodGroupFires() => Fires("DR038", """
using System;
using System.Diagnostics;
public static class Links
{
    public static readonly Func<string, Process?> Opener = Process.Start;
}
""", 1, host: Host.None);

        /// <summary>The overloads BannedSymbols.txt still bans (RS0030) are not reported twice; the SecureString overload it does not list is DR038.</summary>
        [Fact]
        public void BannedOverloadsAreLeftToRs0030() => Fires("DR038", """
using System.Diagnostics;
using System.Security;
public static class Tools
{
    public static void Run(SecureString password)
    {
        Process.Start("http://localhost/", "x");
        new Process().Start();
        Process.Start("tool.exe", "x", password, "domain");
    }
}
""", 1, onLine: "password", host: Host.None);

        [Fact]
        public void ParseAuthorityHandlesPortsUserInfoAndIpv6()
        {
            Assert.Equal("[::1]", NetworkAnalyzer.ParseHost("http://[::1]:8080/", complete: false));
            Assert.Equal("localhost", NetworkAnalyzer.ParseHost("http://localhost:8080", complete: true));
            Assert.Null(NetworkAnalyzer.ParseHost("http://localhost:8080", complete: false));
            Assert.Equal("localhost", NetworkAnalyzer.ParseAuthority("http://localhost:", complete: false, out var open));
            Assert.True(open);
            Assert.Null(NetworkAnalyzer.ParseHost("http://localhost:80@example.org/", complete: true));
            Assert.Null(NetworkAnalyzer.ParseHost("http://[::1]example.org/", complete: true));
            Assert.Null(NetworkAnalyzer.ParseHost("http://localhost\\@example.org/", complete: true));
        }
    }
}
