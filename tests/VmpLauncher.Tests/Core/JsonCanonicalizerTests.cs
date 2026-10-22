using VmpLauncher.Core.Hashing;
using Xunit;

namespace VmpLauncher.Tests.Core;

public class JsonCanonicalizerTests
{
    [Fact]
    public void Sorts_keys_and_strips_whitespace()
    {
        var a = "{\n  \"b\": 1,\n  \"a\": [1, 2, {\"z\": true, \"y\": null}]\n}";
        var b = "{\"a\":[1,2,{\"y\":null,\"z\":true}],\"b\":1}";
        Assert.Equal(b, JsonCanonicalizer.Canonicalize(a));
        Assert.Equal(JsonCanonicalizer.Sha256(a), JsonCanonicalizer.Sha256(b));
    }

    [Fact]
    public void Different_content_hashes_differently()
        => Assert.NotEqual(JsonCanonicalizer.Sha256("{\"v\":\"1.0\"}"), JsonCanonicalizer.Sha256("{\"v\":\"1.1\"}"));

    [Fact]
    public void Escapes_control_characters_minimally()
        => Assert.Equal("{\"s\":\"a\\nb\\u0001\\\"\"}", JsonCanonicalizer.Canonicalize("{\"s\":\"a\\nb\\u0001\\\"\"}"));

    [Fact]
    public void Integers_stay_integers()
        => Assert.Equal("{\"n\":12345678901234}", JsonCanonicalizer.Canonicalize("{\"n\": 12345678901234}"));
}
