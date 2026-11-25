using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;
using static DeepRules.Tests.Harness;

namespace DeepRules.Tests
{
    /// <summary>
    /// DR039: SHA-1 may only compute the WebSocket handshake answer, Base64(SHA1(Sec-WebSocket-Key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))
    /// of RFC 6455 section 4.2.2 (the user, 2026-11-21: "SHA handshakes are an exception").
    /// </summary>
    public class DR039Tests
    {
        private const string Preamble = """
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;

""";

        private static string Wrap(string body) => Preamble + body;

        // ------------------------------------------------------------------ accepted

        /// <summary>HttpListener server: the key from the request's header, a local hash object, the answer set as the Sec-WebSocket-Accept header.</summary>
        [Fact]
        public void HttpListenerHandshakeIsSilent() => Silent("DR039", Wrap("""
public static class WebSocketServer
{
    private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public static void Accept(HttpListenerContext context)
    {
        string key = context.Request.Headers["Sec-WebSocket-Key"];
        using var sha = SHA1.Create();
        var accept = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(key + Guid)));
        context.Response.StatusCode = 101;
        context.Response.Headers.Add("Sec-WebSocket-Accept", accept);
    }
}
"""), host: Host.None);

        /// <summary>The classic TcpClient server: the key read from the raw request with a Regex, the 101 answer written to the NetworkStream.</summary>
        [Fact]
        public void TcpClientHandshakeIsSilent() => Silent("DR039", Wrap("""
public static class WebSocketServer
{
    public static void Accept(TcpClient client, string data)
    {
        NetworkStream stream = client.GetStream();
        string swk = Regex.Match(data, "Sec-WebSocket-Key: (.*)").Groups[1].Value.Trim();
        string swka = swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        byte[] swkaSha1 = SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(swka));
        string swkaSha1Base64 = Convert.ToBase64String(swkaSha1);
        byte[] response = Encoding.UTF8.GetBytes("HTTP/1.1 101 Switching Protocols\r\n" +
            "Connection: Upgrade\r\n" +
            "Upgrade: websocket\r\n" +
            "Sec-WebSocket-Accept: " + swkaSha1Base64 + "\r\n\r\n");
        stream.Write(response, 0, response.Length);
    }
}
"""), host: Host.None);

        /// <summary>A private helper with the key as a parameter named like the header: every caller must send the answer as the Accept value.</summary>
        [Fact]
        public void PrivateHelperWithItsCallersIsSilent() => Silent("DR039", Wrap("""
public sealed class WebSocketServer
{
    private static readonly string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public void Upgrade(HttpListenerResponse response, string requestKey, NetworkStream stream)
    {
        response.Headers["Sec-WebSocket-Accept"] = AcceptKey(requestKey);
        var line = $"HTTP/1.1 101 Switching Protocols\r\nSec-WebSocket-Accept: {AcceptKey(requestKey)}\r\n\r\n";
        stream.Write(Encoding.ASCII.GetBytes(line), 0, line.Length);
    }

    private static string AcceptKey(string secWebSocketKey)
    {
        using (SHA1 sha1 = SHA1.Create())
        {
            return Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(secWebSocketKey + HandshakeGuid)));
        }
    }
}
"""), host: Host.None);

        /// <summary>SHA1.HashData (.NET 8 and later), the key from TryGetValue, string.Concat, the interpolated form and a byte-array Concat.</summary>
        [Fact]
        public void HashDataFormsAreSilent() => Silent("DR039", Wrap("""
public static class WebSocketServer
{
    public static void A(HttpListenerRequest request, HttpListenerResponse response)
    {
        string key = request.Headers.Get("Sec-WebSocket-Key")!;
        response.AddHeader("Sec-WebSocket-Accept", Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))));
    }

    public static void B(Dictionary<string, string> headers, HttpListenerResponse response)
    {
        if (!headers.TryGetValue("sec-websocket-key", out var key)) return;
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(string.Concat(key, "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        response.Headers.Set("Sec-WebSocket-Accept", Convert.ToBase64String(hash));
    }

    public static void C(HttpListenerRequest request, HttpListenerResponse response)
    {
        string key = request.Headers["Sec-WebSocket-Key"]!.Trim();
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes($"{key}258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
    }

    public static void D(HttpListenerRequest request, HttpListenerResponse response)
    {
        string key = request.Headers["Sec-WebSocket-Key"]!;
        var joined = Encoding.UTF8.GetBytes(key).Concat(Encoding.UTF8.GetBytes("258EAFA5-E914-47DA-95CA-C5AB0DC85B11")).ToArray();
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.HashData(joined));
    }
}
"""), net48: false, host: Host.None);

        // ------------------------------------------------------------------ rejected: what is hashed

        [Fact]
        public void Sha1OfAnythingElseFires() => Fires("DR039", Wrap("""
public static class Hashes
{
    public static string Password(string password)
    {
        using var sha = SHA1.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password)));
    }

    public static string Checksum(byte[] data) => Convert.ToBase64String(SHA1.HashData(data));
}
"""), 2, host: Host.None, net48: false);

        /// <summary>The GUID compared exactly: one digit, the case, or text that is not constant.</summary>
        [Fact]
        public void WrongGuidFires()
        {
            var found = Fires("DR039", Wrap("""
public static class WebSocketServer
{
    public static void Accept(HttpListenerRequest request, HttpListenerResponse response, string guid)
    {
        string key = request.Headers["Sec-WebSocket-Key"]!;
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B12")));
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258eafa5-e914-47da-95ca-c5ab0dc85b11")));
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + guid)));
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
    }
}
"""), 4, host: Host.None, net48: false);
            Assert.Contains("not the RFC 6455 GUID", found[0].GetMessage());
        }

        /// <summary>A GUID field set again in the static constructor is not constant text.</summary>
        [Fact]
        public void GuidFieldAssignedAgainFires() => Fires("DR039", Wrap("""
public static class WebSocketServer
{
    private static readonly string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    static WebSocketServer() { Guid = Environment.GetEnvironmentVariable("GUID") ?? Guid; }

    public static void Accept(HttpListenerRequest request, HttpListenerResponse response)
    {
        string key = request.Headers["Sec-WebSocket-Key"]!;
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + Guid)));
    }
}
"""), 1, host: Host.None, net48: false);

        /// <summary>The first part must be the value of a header named Sec-WebSocket-Key: not input read from elsewhere, not another header, not an unrelated parameter.</summary>
        [Fact]
        public void KeyNotFromTheHeaderFires() => Fires("DR039", Wrap("""
public static class WebSocketServer
{
    public static void Accept(HttpListenerRequest request, HttpListenerResponse response, string userName)
    {
        string a = Console.ReadLine()!;
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(a + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        string b = request.Headers["X-Api-Token"]!;
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(b + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(userName + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
    }
}
"""), 3, host: Host.None, net48: false);

        /// <summary>The key's variable changed after the header was read is no longer the header value.</summary>
        [Fact]
        public void KeyVariableChangedFires() => Fires("DR039", Wrap("""
public static class WebSocketServer
{
    public static void Accept(HttpListenerRequest request, HttpListenerResponse response)
    {
        string key = request.Headers["Sec-WebSocket-Key"]!;
        key = key + Environment.UserName;
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
    }
}
"""), 1, host: Host.None, net48: false);

        /// <summary>The key and GUID in a form that is not shown (a Buffer copy, extra text, a format string, Unicode bytes) is not followed.</summary>
        [Fact]
        public void OtherInputFormsFire() => Fires("DR039", Wrap("""
public static class WebSocketServer
{
    public static void Accept(HttpListenerRequest request, HttpListenerResponse response)
    {
        string key = request.Headers["Sec-WebSocket-Key"]!;
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11" + Environment.MachineName)));
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(string.Format("{0}258EAFA5-E914-47DA-95CA-C5AB0DC85B11", key))));
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.HashData(Encoding.Unicode.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var a = Encoding.UTF8.GetBytes(key);
        var b = Encoding.UTF8.GetBytes("258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
        var all = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, all, 0, a.Length);
        Buffer.BlockCopy(b, 0, all, a.Length, b.Length);
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.HashData(all));
    }
}
"""), 4, host: Host.None, net48: false);

        // ------------------------------------------------------------------ rejected: where the result goes

        [Fact]
        public void ResultWrittenToAFileOrLoggedFires() => Fires("DR039", Wrap("""
public static class WebSocketServer
{
    public static void Accept(HttpListenerRequest request, string path)
    {
        string key = request.Headers["Sec-WebSocket-Key"]!;
        File.WriteAllText(path, Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))));
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        Console.WriteLine("accepted " + accept);
        var hex = BitConverter.ToString(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
    }
}
"""), 3, host: Host.None, net48: false);

        /// <summary>A hash that is compared, stored as a password hash or handed on as a token is not the Accept value.</summary>
        [Fact]
        public void ResultComparedOrUsedAsATokenFires() => Fires("DR039", Wrap("""
public static class WebSocketServer
{
    public static bool Matches(HttpListenerRequest request, string stored)
    {
        string key = request.Headers["Sec-WebSocket-Key"]!;
        return Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))) == stored;
    }

    public static string Token(HttpListenerRequest request)
    {
        string key = request.Headers["Sec-WebSocket-Key"]!;
        return Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
    }

    public static void Header(HttpListenerRequest request, HttpListenerResponse response)
    {
        string key = request.Headers["Sec-WebSocket-Key"]!;
        response.Headers["X-Session"] = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
    }
}
"""), 3, host: Host.None, net48: false);

        /// <summary>A private helper is only accepted when every caller uses its result as the Accept value.</summary>
        [Fact]
        public void HelperWithABadCallerFires() => Fires("DR039", Wrap("""
public sealed class WebSocketServer
{
    public void Upgrade(HttpListenerResponse response, string secWebSocketKey)
    {
        response.Headers["Sec-WebSocket-Accept"] = AcceptKey(secWebSocketKey);
        File.WriteAllText("accept.txt", AcceptKey(secWebSocketKey));
    }

    private static string AcceptKey(string secWebSocketKey) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(secWebSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
}
""" ), 1, host: Host.None, net48: false);

        /// <summary>A public helper cannot have its callers checked; a Base64 text that is used as a delegate cannot be followed.</summary>
        [Fact]
        public void PublicHelperFires() => Fires("DR039", Wrap("""
public sealed class WebSocketServer
{
    public static string AcceptKey(string secWebSocketKey) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(secWebSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
}
"""), 1, host: Host.None, net48: false);

        /// <summary>The 101 line is only accepted when it is written to a network stream or socket, not a file.</summary>
        [Fact]
        public void ResponseWrittenToAFileFires() => Fires("DR039", Wrap("""
public static class WebSocketServer
{
    public static void Accept(string data, string path)
    {
        string swk = Regex.Match(data, "Sec-WebSocket-Key: (.*)").Groups[1].Value.Trim();
        var accept = Convert.ToBase64String(SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = Encoding.UTF8.GetBytes("HTTP/1.1 101 Switching Protocols\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
        using var file = new FileStream(path, FileMode.Create);
        file.Write(response, 0, response.Length);
    }
}
"""), 1, host: Host.None);

        /// <summary>The text before the value must end the Sec-WebSocket-Accept line, so the value is the header's value.</summary>
        [Fact]
        public void ResponseWithoutTheAcceptLineFires() => Fires("DR039", Wrap("""
public static class WebSocketServer
{
    public static void Accept(string data, NetworkStream stream)
    {
        string swk = Regex.Match(data, "Sec-WebSocket-Key: (.*)").Groups[1].Value.Trim();
        var accept = Convert.ToBase64String(SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var a = Encoding.UTF8.GetBytes("HTTP/1.1 101 Switching Protocols\r\nX-Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
        stream.Write(a, 0, a.Length);
        var b = Encoding.UTF8.GetBytes("HTTP/1.1 101 Switching Protocols\r\nX-Note: " + accept + "\r\n\r\n");
        stream.Write(b, 0, b.Length);
    }
}
"""), 1, host: Host.None);

        // ------------------------------------------------------------------ rejected: other SHA-1 types and uses

        /// <summary>What the old T: ban reached: derived types and any mention of SHA1.</summary>
        [Fact]
        public void DerivedTypesAndMentionsFire() => Fires("DR039", Wrap("""
public static class Hashes
{
    public static byte[] Managed(byte[] data)
    {
        using var sha = new SHA1Managed();
        return sha.ComputeHash(data);
    }

    public static byte[] Provider(byte[] data)
    {
        using var sha = new SHA1CryptoServiceProvider();
        return sha.ComputeHash(data);
    }

    public static byte[] Implicit(byte[] data)
    {
        SHA1Managed sha = new();
        return sha.ComputeHash(data);
    }

    public static System.Type Kind() => typeof(SHA1);
    public static Func<SHA1> Factory = SHA1.Create;
    public static SHA1? Held;
}
"""), 8, host: Host.None);

        /// <summary>A hash object that is held in a field, passed on or used for more than ComputeHash is not followed.</summary>
        [Fact]
        public void HashObjectHandedOnFires() => Fires("DR039", Wrap("""
public static class WebSocketServer
{
    private static readonly HashAlgorithm Shared = SHA1.Create();

    public static void Accept(HttpListenerRequest request, HttpListenerResponse response)
    {
        string key = request.Headers["Sec-WebSocket-Key"]!;
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(Shared.ComputeHash(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        using var sha = SHA1.Create();
        Use(sha);
        using var other = SHA1.Create();
        var bits = other.HashSize;
    }

    private static void Use(HashAlgorithm algorithm) { }
}
"""), 3, host: Host.None);

        /// <summary>Other SHA1 overloads and the stream form: not a site the handshake needs.</summary>
        [Fact]
        public void OtherHashDataOverloadsFire() => Fires("DR039", Wrap("""
public static class Hashes
{
    public static byte[] OfStream(Stream data) => SHA1.HashData(data);
    public static int Into(byte[] data, byte[] destination) => SHA1.HashData(data, destination);
}
"""), 2, host: Host.None, net48: false);

        /// <summary>HMAC-SHA1 and the other hashes are not covered by this rule: they stay as before (CA5350 for HMACSHA1, the bans for MD5).</summary>
        [Fact]
        public void HmacAndOtherHashesAreNotThisRule() => Silent("DR039", Wrap("""
public static class Hashes
{
    public static byte[] Hmac(byte[] key, byte[] data) { using var h = new HMACSHA1(key); return h.ComputeHash(data); }
    public static byte[] Sha256(byte[] data) => SHA256.HashData(data);
    public static byte[] Md5(byte[] data) { using var h = MD5.Create(); return h.ComputeHash(data); }
}
"""), net48: false, host: Host.None);

        /// <summary>The message names the one allowed use and what in the code broke it.</summary>
        [Fact]
        public void MessageStatesTheDefectAndTheFix()
        {
            var d = Fires("DR039", Wrap("""
public static class Hashes
{
    public static string Password(string password) => Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
}
"""), 1, host: Host.None, net48: false);
            var message = d[0].GetMessage();
            Assert.Contains("not the WebSocket handshake answer", message);
            Assert.Contains("RFC 6455", message);
            Assert.Contains("258EAFA5-E914-47DA-95CA-C5AB0DC85B11", message);
            Assert.Contains("SHA256", message);
        }

        // ------------------------------------------------------------------ the suppressor

        /// <summary>
        /// Stands in for NetAnalyzers (CA5350), Sonar (S4790) and SecurityCodeScan (SCS0006): each reports on the whole SHA1.Create() or
        /// SHA1.HashData(...) call, on new SHA1Managed(...) and on new HMACSHA1(...), the places the real analyzers were seen to report
        /// (a gate run of DeepScan with the real packs over a sample of these shapes, 2026-11-21).
        /// </summary>
        private sealed class WeakHashAnalyzer : DiagnosticAnalyzer
        {
            public static readonly DiagnosticDescriptor CA5350 = Descriptor("CA5350");
            public static readonly DiagnosticDescriptor S4790 = Descriptor("S4790");
            public static readonly DiagnosticDescriptor SCS0006 = Descriptor("SCS0006");
            public static readonly DiagnosticDescriptor CA5351 = Descriptor("CA5351");

            private static DiagnosticDescriptor Descriptor(string id) => new(id, "weak hash", "weak hash", "Security", DiagnosticSeverity.Warning, true);

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(CA5350, S4790, SCS0006, CA5351);

            public override void Initialize(AnalysisContext context)
            {
                context.RegisterSyntaxNodeAction(c =>
                {
                    var text = c.Node.ToString();
                    var isSha1Call = c.Node is InvocationExpressionSyntax && (text.StartsWith("SHA1.Create(", System.StringComparison.Ordinal) || text.StartsWith("SHA1.HashData(", System.StringComparison.Ordinal));
                    var isCreation = c.Node is ObjectCreationExpressionSyntax && (text.StartsWith("new SHA1Managed(", System.StringComparison.Ordinal) || text.StartsWith("new HMACSHA1(", System.StringComparison.Ordinal));
                    if (!isSha1Call && !isCreation) return;
                    foreach (var d in new[] { CA5350, S4790, SCS0006, CA5351 })
                        c.ReportDiagnostic(Diagnostic.Create(d, c.Node.GetLocation()));
                }, SyntaxKind.InvocationExpression, SyntaxKind.ObjectCreationExpression);
            }
        }

        private static ImmutableArray<Diagnostic> WithSuppressor(string source)
        {
            var trees = new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: "Test.cs") };
            var refs = System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa
                ? tpa.Split(System.IO.Path.PathSeparator).Where(p => p.Contains("Microsoft.NETCore.App", System.StringComparison.OrdinalIgnoreCase))
                    .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray()
                : System.Array.Empty<MetadataReference>();
            var compilation = CSharpCompilation.Create("Snippet", trees, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, reportSuppressedDiagnostics: true));
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new WeakHashAnalyzer(), new HandshakeSuppressor());
            var options = new CompilationWithAnalyzersOptions(new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty), null, false, false, reportSuppressedDiagnostics: true);
            return compilation.WithAnalyzers(analyzers, options).GetAllDiagnosticsAsync().GetAwaiter().GetResult()
                .Where(d => d.Id is "CA5350" or "S4790" or "SCS0006" or "CA5351").ToImmutableArray();
        }

        [Fact]
        public void SuppressorDropsTheThreeRulesOnTheHandshakeCallOnly()
        {
            var all = WithSuppressor(Wrap("""
public static class WebSocketServer
{
    public static void Accept(HttpListenerRequest request, HttpListenerResponse response, string password)
    {
        string key = request.Headers["Sec-WebSocket-Key"]!;
        response.Headers["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        using var sha = SHA1.Create();
        var other = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password)));
        using var managed = new SHA1Managed();
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(key));
    }
}
"""));
            string Line(Diagnostic d) => d.Location.SourceTree!.GetText().Lines[d.Location.GetLineSpan().StartLinePosition.Line].ToString();

            var handshake = all.Where(d => Line(d).Contains("Sec-WebSocket-Accept")).ToList();
            Assert.Equal(new[] { "CA5350", "CA5351", "S4790", "SCS0006" }, handshake.Select(d => d.Id).OrderBy(i => i, System.StringComparer.Ordinal).ToArray());
            Assert.All(handshake.Where(d => d.Id != "CA5351"), d => Assert.True(d.IsSuppressed, d.Id + " was not dropped on the handshake call"));
            Assert.False(handshake.Single(d => d.Id == "CA5351").IsSuppressed);

            // everything else stays reported
            foreach (var d in all.Where(d => !Line(d).Contains("Sec-WebSocket-Accept")))
                Assert.False(d.IsSuppressed, d.Id + " on '" + Line(d).Trim() + "' must stay reported");
            Assert.Equal(4 * 3, all.Count(d => !Line(d).Contains("Sec-WebSocket-Accept")));
        }

        /// <summary>The same rule DR039 uses decides: a handshake-looking call with the wrong GUID or the result written elsewhere keeps all its results.</summary>
        [Fact]
        public void SuppressorKeepsTheRulesOnALookalike()
        {
            var all = WithSuppressor(Wrap("""
public static class WebSocketServer
{
    public static void Accept(HttpListenerRequest request, string path)
    {
        string key = request.Headers["Sec-WebSocket-Key"]!;
        File.WriteAllText(path, Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))));
        var wrong = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B12")));
    }
}
"""));
            Assert.Equal(8, all.Length);
            Assert.All(all, d => Assert.False(d.IsSuppressed, d.Id + " must stay reported"));
        }

        [Fact]
        public void SuppressorOnlyDeclaresTheThreeRules()
        {
            var ids = new HandshakeSuppressor().SupportedSuppressions.Select(s => s.SuppressedDiagnosticId).OrderBy(i => i, System.StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "CA5350", "S4790", "SCS0006" }, ids);
        }
    }
}
