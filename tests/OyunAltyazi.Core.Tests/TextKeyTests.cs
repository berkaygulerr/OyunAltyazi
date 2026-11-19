using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class TextKeyTests
{
    [Theory]
    [InlineData("Frag out!", "frag out")]
    [InlineData("  RELOADING...  ", "reloading")]
    [InlineData("Let's go", "let s go")]
    [InlineData("<i>Cover</i> me", "cover me")]
    [InlineData("İstanbul café", "istanbul cafe")]
    public void Canon_matches_rules(string input, string expected) => Assert.Equal(expected, TextKey.Canon(input));

    [Theory]
    [InlineData("Frag out!", "3b96e8a2a8dac727")]
    [InlineData("Reloading", "11747904a6b688d5")]
    [InlineData("Cover me.", "5ad7d73f3757fa41")]
    [InlineData("Let's go", "27b5ebc897524c85")]
    [InlineData("Tear gas out!", "03e5024798f115b1")]
    public void PackKey_is_compatible(string input, string key) => Assert.Equal(key, TextKey.PackKey(input));
}
