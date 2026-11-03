namespace Symposia.Network.Node.Tests;

/// <summary>Endpoint canonicalization and dial policy (#129 review findings).</summary>
public sealed class NodeEndpointTests
{
    [Theory]
    [InlineData("http://h:5180", "http://h:5180")]
    [InlineData("HTTP://H:5180/", "http://h:5180")]
    [InlineData("http://h", "http://h:80")]
    [InlineData("https://h", "https://h:443")]
    [InlineData(" http://10.0.0.1:5180 ", "http://10.0.0.1:5180")]
    public void Normalize_Canonicalizes(string input, string expected) =>
        Assert.Equal(expected, NodeEndpoint.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("h:5180")]
    [InlineData("ftp://h:21")]
    [InlineData("http://h:5180/path")]
    [InlineData("http://user:pw@h:5180")]
    [InlineData("http://h:5180/?q=1")]
    public void Normalize_RejectsNonBaseHttpUrls(string input) =>
        Assert.Null(NodeEndpoint.Normalize(input));

    [Theory]
    [InlineData("http://127.0.0.1:5180", true)]
    [InlineData("http://10.0.0.5:5180", true)]
    [InlineData("http://node.example:5180", true)]
    [InlineData("http://169.254.169.254", false)]
    [InlineData("http://[fe80::1]:5180", false)]
    [InlineData("http://[::ffff:169.254.169.254]:80", false)]
    public void IsDialable(string endpoint, bool expected) =>
        Assert.Equal(expected, NodeEndpoint.IsDialable(endpoint));
}
