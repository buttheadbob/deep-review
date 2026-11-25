using Xunit;
using static DeepRules.Tests.Harness;

namespace DeepRules.Tests
{
    /// <summary>DR037: requests only to allowed hosts the code shows, through one shared HttpClient.</summary>
    public class DR037Tests
    {
        /// <summary>The pattern plugins should use: one shared client, and a URI whose host is constant text.</summary>
        [Fact]
        public void SharedClientAndFixedDiscordHostAreSilent() => Silent("DR037", """
using System;
using System.Net.Http;
using System.Threading.Tasks;
public static class Webhook
{
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Uri Api = new Uri("https://discord.com/api/");
    public static async Task Send(ulong id, string token, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://discord.com/api/webhooks/{id}/{token}") { Content = content };
        using var response = await Http.SendAsync(request);
        var url = "https://ptb.discord.com/api/webhooks/" + id + "/" + token;
        var text = await Http.GetStringAsync(url);
        var other = await Http.GetStringAsync(new Uri(Api, "gateway"));
    }
}
""", host: Host.None);

        [Fact]
        public void ClientMadePerCallFires() => Fires("DR037", """
using System.Net.Http;
public class Sender
{
    private readonly HttpClient _mine = new HttpClient();
    public void Send() { using var client = new HttpClient(); }
}
""", 2, onLine: "new HttpClient()", host: Host.None);

        [Fact]
        public void HostOutsideTheAllowlistFires()
        {
            var d = Fires("DR037", """
using System.Net.Http;
using System.Threading.Tasks;
public static class Leak
{
    private static readonly HttpClient Http = new HttpClient();
    public static Task<string> Send(string ip) => Http.GetStringAsync("http://example.org/collect?ip=" + ip);
}
""", 1, onLine: "example.org", host: Host.None);
            Assert.Contains("'example.org'", d[0].GetMessage());
        }

        /// <summary>A URI the caller hands in could go anywhere: the code must show the host.</summary>
        [Fact]
        public void HostFromAParameterFires() => Fires("DR037", """
using System.Net.Http;
using System.Threading.Tasks;
public static class Webhook
{
    private static readonly HttpClient Http = new HttpClient();
    public static async Task Send(string url, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        await Http.SendAsync(request);
    }
}
""", 1, onLine: "HttpRequestMessage(HttpMethod.Post, url)", host: Host.None);

        /// <summary>"https://discord.com" + x could become discord.com.example.org, so the host must end in the constant text.</summary>
        [Fact]
        public void HostNotClosedByTheConstantTextFires() => Fires("DR037", """
using System.Net.Http;
using System.Threading.Tasks;
public static class Webhook
{
    private static readonly HttpClient Http = new HttpClient();
    public static Task<string> Send(string rest) => Http.GetStringAsync("https://discord.com" + rest);
}
""", 1, host: Host.None);

        /// <summary>"https://discord.com:" + x could become discord.com:1@example.org (user info), so a port hole must be an integer.</summary>
        [Fact]
        public void PortThatCanCarryUserInfoFires() => Fires("DR037", """
using System.Net.Http;
using System.Threading.Tasks;
public static class Webhook
{
    private static readonly HttpClient Http = new HttpClient();
    public static Task<string> Send(string port) => Http.GetStringAsync("https://discord.com:" + port + "/api");
}
""", 1, host: Host.None);

        [Fact]
        public void ParseHostReadsOnlyAbsoluteHttpUris()
        {
            Assert.Equal("discord.com", NetworkAnalyzer.ParseHost("https://discord.com/api/webhooks/", complete: false));
            Assert.Equal("discord.com", NetworkAnalyzer.ParseHost("https://DISCORD.com", complete: true));
            Assert.Null(NetworkAnalyzer.ParseHost("https://discord.com", complete: false));
            Assert.Null(NetworkAnalyzer.ParseHost("ftp://discord.com/", complete: true));
            Assert.Null(NetworkAnalyzer.ParseHost("https://user@evil.org/", complete: true));
        }
    }
}
