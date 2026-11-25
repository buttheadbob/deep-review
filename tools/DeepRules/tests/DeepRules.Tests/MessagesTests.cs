using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace DeepRules.Tests
{
    /// <summary>Prints every DeepRules diagnostic for a realistic snippet, so the messages can be read in the test log.</summary>
    public class MessagesTests
    {
        private readonly ITestOutputHelper _output;

        public MessagesTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void EveryMessageOnTheSensEssentialsShapeStatesTheDefect()
        {
            var source = typeof(RegressionTests).GetField("SensOriginal", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null) as string;
            Assert.NotNull(source);
            var diags = Harness.Analyze(source!);
            foreach (var d in diags)
                _output.WriteLine(d.Id + " line " + Harness.LineOf(d) + ": " + d.GetMessage());
            Assert.NotEmpty(diags);
            Assert.All(diags, d => Assert.DoesNotMatch("^lead:", d.GetMessage()));
            Assert.All(diags, d => Assert.Matches("^DR0[0-3][0-9]$", d.Id));
        }

        [Fact]
        public void HarnessRejectsSnippetsThatDoNotCompile()
        {
            var ex = Assert.Throws<System.InvalidOperationException>(() => Harness.Analyze("class C { void M() { undefined(); } }"));
            Assert.Contains("does not compile", ex.Message);
        }

        [Fact]
        public void FiresFailsWhenTheRuleIsAbsent()
        {
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Harness.Fires("DR001", "class C { }"));
            Assert.Empty(Harness.Analyze("class C { }").Where(d => d.Id == "DR001"));
        }
    }
}
