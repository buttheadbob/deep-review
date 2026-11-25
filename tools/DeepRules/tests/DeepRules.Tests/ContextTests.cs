using System.Linq;
using Xunit;

namespace DeepRules.Tests
{
    /// <summary>
    /// Which referenced assembly names make a game or plugin compilation. The same snippet gets the reload check (DR034) in
    /// game and plugin code and the per-instance check (DR010) elsewhere, so the rule that fires shows the context.
    /// </summary>
    public class ContextTests
    {
        private const string Snippet = """
using System.Collections.Generic;
public class Tracker
{
    private static List<int>? s_items;
    public void Use(List<int> items) { s_items = items; }
}
""";

        [Theory]
        [InlineData("Sandbox.Game")]
        [InlineData("Sandbox.Common")]
        [InlineData("VRage")]
        [InlineData("VRage.Game")]
        [InlineData("SpaceEngineers.Game")]
        [InlineData("HavokWrapper")]
        [InlineData("0Harmony")]
        [InlineData("Lib.Harmony")]
        [InlineData("Torch")]
        [InlineData("Torch.API")]
        [InlineData("Magnetar.Protocol")]
        [InlineData("Quasar.Plugin.Abstractions")]
        public void GameOrPluginReferenceGivesTheReloadCheck(string assembly)
        {
            var d = Harness.AnalyzeReferencing(Snippet, assembly);
            Assert.Contains(d, x => x.Id == "DR034");
            Assert.DoesNotContain(d, x => x.Id == "DR010");
        }

        [Theory]
        [InlineData("TorchSharp")]
        [InlineData("MagnetarSoft")]
        [InlineData("Pulsar.Client")]
        [InlineData("Steamworks.NET")]
        [InlineData("Newtonsoft.Json")]
        public void OtherReferencesGiveTheInstanceCheck(string assembly)
        {
            var d = Harness.AnalyzeReferencing(Snippet, assembly);
            Assert.Contains(d, x => x.Id == "DR010");
            Assert.DoesNotContain(d, x => x.Id == "DR034");
        }

        [Fact]
        public void NoReferenceGivesTheInstanceCheck()
        {
            var d = Harness.AnalyzeReferencing(Snippet);
            Assert.Equal(new[] { "DR010" }, d.Select(x => x.Id).Distinct().ToArray());
        }
    }
}
