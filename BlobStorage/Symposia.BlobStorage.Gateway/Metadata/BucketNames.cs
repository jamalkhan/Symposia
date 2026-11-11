using System.Text.RegularExpressions;

namespace Symposia.BlobStorage.Gateway.Metadata;

/// <summary>
/// S3 bucket naming rules, enforced by both APIs (#203): 3-63 lowercase letters, digits, dots and
/// hyphens, starting and ending with a letter or digit, no "..", not an IP address. The minimum length
/// also keeps <c>v1</c>, the native API's path prefix, from ever being a bucket.
/// </summary>
public static partial class BucketNames
{
    public const string Rule = "Bucket names are 3-63 lowercase letters, digits, dots and hyphens, starting and ending with a letter or digit.";

    public static bool IsValid(string name) =>
        name.Length is >= 3 and <= 63 && Shape().IsMatch(name) && !name.Contains("..") && !IpAddress().IsMatch(name);

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*[a-z0-9]$")]
    private static partial Regex Shape();

    [GeneratedRegex(@"^\d+\.\d+\.\d+\.\d+$")]
    private static partial Regex IpAddress();
}
