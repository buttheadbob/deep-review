using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace DeepRules.Tests
{
    public class RulesTests
    {
        private static List<DiagnosticDescriptor> All() => typeof(ConcurrencyAnalyzer).Assembly.GetTypes()
            .Where(t => typeof(DiagnosticAnalyzer).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => ((DiagnosticAnalyzer)System.Activator.CreateInstance(t)!).SupportedDiagnostics)
            .ToList();

        [Fact]
        public void ThirtyNineRulesEachSupportedOnce()
        {
            var ids = All().Select(d => d.Id).OrderBy(x => x).ToList();
            Assert.Equal(Enumerable.Range(1, 39).Select(i => "DR" + i.ToString("000")).ToList(), ids);
        }

        /// <summary>The rules whose hits need judgement carry the ReviewOnly tag; every other rule reports only defects.</summary>
        [Fact]
        public void ReviewOnlyTagsMatchTheReadme()
        {
            var expected = new[]
            {
                "DR002", "DR003", "DR005", "DR006", "DR008", "DR011", "DR016", "DR017", "DR018", "DR019", "DR020", "DR021",
                "DR022", "DR024", "DR025", "DR027", "DR028", "DR029", "DR031", "DR033", "DR034", "DR035", "DR036"
            };
            var tagged = All().Where(d => d.CustomTags.Contains("ReviewOnly")).Select(d => d.Id).OrderBy(x => x).ToArray();
            Assert.Equal(expected, tagged);
        }

        [Fact]
        public void DescriptorsFollowTheContract()
        {
            foreach (var d in All())
            {
                Assert.Equal(DiagnosticSeverity.Warning, d.DefaultSeverity);
                Assert.Equal("DeepReview", d.Category);
                Assert.True(d.IsEnabledByDefault);
                Assert.Equal("{0}", d.MessageFormat.ToString());
                Assert.Matches(@"^checks/core/(concurrency|lifecycle|errors|persistence|inputs|performance|correctness|security)\.md#[a-z0-9-]+$", d.HelpLinkUri);
            }
        }
    }
}
