using Vndb.Core;
using Xunit;

namespace Vndb.Tests;

public sealed class VndbIdTests
{
    [Theory]
    [InlineData("v17", 'v', "v17")]
    [InlineData(" V17 ", 'v', "v17")]
    [InlineData("https://vndb.org/v17", 'v', "v17")]
    [InlineData("https://vndb.org/p98/", 'p', "p98")]
    [InlineData("https://vndb.org/v17#main", 'v', "v17")]
    public void AcceptsCanonicalIdsAndLinks(string input, char prefix, string expected) =>
        Assert.Equal(expected, VndbId.Normalize(input, prefix));

    [Theory]
    [InlineData("17")]
    [InlineData("p17")]
    [InlineData("v0")]
    [InlineData("v01")]
    [InlineData("v-1")]
    [InlineData("v１２")]
    [InlineData("v17/extra")]
    [InlineData("https://evil.example/v17")]
    [InlineData("https://vndb.org.evil.example/v17")]
    [InlineData("http://vndb.org/v17")]
    [InlineData("https://vndb.org:444/v17")]
    [InlineData("https://user@vndb.org/v17")]
    [InlineData(" ")]
    public void RejectsInvalidOrWrongEntityIds(string input) =>
        Assert.Throws<ArgumentException>(() => VndbId.Normalize(input, 'v'));
}
