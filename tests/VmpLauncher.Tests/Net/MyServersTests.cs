using VmpLauncher.App.Services;
using VmpLauncher.Core.Packs;
using VmpLauncher.Net.Directory;
using Xunit;

namespace VmpLauncher.Tests.Net;

/// <summary>Servers the player adds on the Play page, and how they and the host's private presets join the published list.</summary>
public class MyServersTests
{
    private static readonly KnownRoute[] Published =
    {
        new("Train - Playit", "signaling-karma.tun.ply.gg", 18113),
        new("Train - ZeroTier", "10.37.239.191", 8800),
    };

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20", 8800)]
    [InlineData("192.168.1.20:9000", "192.168.1.20", 9000)]
    [InlineData("  trainpc.local  ", "trainpc.local", 8800)]
    [InlineData("trainpc", "trainpc", 8800)]
    [InlineData("[fe80::1]:8801", "fe80::1", 8801)]
    [InlineData("fe80::1", "fe80::1", 8800)]
    [InlineData("play.example.com:65535", "play.example.com", 65535)]
    public void Addresses_are_split_into_host_and_port(string typed, string host, int port)
    {
        var check = MyServers.Check("Home", typed, Published);

        Assert.Null(check.Error);
        Assert.Equal(new MyServer("Home", host, port), check.Server);
    }

    [Theory]
    [InlineData("http://192.168.1.2")]
    [InlineData("192.168.1.2/path")]
    [InlineData("192.168.1.2 8800")]
    [InlineData("192.168.1.2:0")]
    [InlineData("192.168.1.2:70000")]
    [InlineData("192.168.1.2:abc")]
    [InlineData("192.168.1.2:-5")]
    [InlineData("[fe80::1")]
    [InlineData("a:b:c")]
    [InlineData("-bad-.example.com")]
    [InlineData("exa_mple.com")]
    [InlineData(":8800")]
    public void Addresses_that_are_not_addresses_are_refused(string typed)
    {
        var check = MyServers.Check("Home", typed, Published);

        Assert.Null(check.Server);
        Assert.NotNull(check.Error);
    }

    [Fact]
    public void A_field_that_is_still_empty_is_not_an_error_but_cannot_be_saved()
    {
        Assert.Equal(new MyServerCheck(null, null), MyServers.Check("", "", Published));
        Assert.Equal(new MyServerCheck(null, null), MyServers.Check("Home", "", Published));
        Assert.Equal(new MyServerCheck(null, null), MyServers.Check("", "192.168.1.2", Published));
    }

    [Fact]
    public void A_name_or_address_already_in_the_list_is_refused()
    {
        Assert.Contains("name", MyServers.Check("train - playit", "192.168.1.2", Published).Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Train - ZeroTier", MyServers.Check("Other", "10.37.239.191", Published).Error, StringComparison.Ordinal);
        Assert.Contains("Train - Playit", MyServers.Check("Other", "SIGNALING-KARMA.tun.ply.gg:18113", Published).Error, StringComparison.Ordinal);
        // The same host on another port is a different server.
        Assert.Null(MyServers.Check("Other", "10.37.239.191:8801", Published).Error);
    }

    [Fact]
    public void Names_are_trimmed_limited_and_free_of_quotes()
    {
        Assert.Equal("Home", MyServers.Check("  Home  ", "192.168.1.2", Published).Server?.Name);
        Assert.NotNull(MyServers.Check(new string('x', MyServers.MaxNameLength + 1), "192.168.1.2", Published).Error);
        Assert.NotNull(MyServers.Check("Say \"hi\"", "192.168.1.2", Published).Error);
        Assert.Null(MyServers.Check(new string('x', MyServers.MaxNameLength), "192.168.1.2", Published).Error);
    }

    [Theory]
    [InlineData("192.168.1.20", "lan")]
    [InlineData("172.16.0.9", "lan")]
    [InlineData("172.31.255.1", "lan")]
    [InlineData("172.32.0.1", "direct")]
    [InlineData("169.254.3.4", "lan")]
    [InlineData("10.37.239.191", "direct")]
    [InlineData("8.8.8.8", "direct")]
    [InlineData("trainpc.local", "lan")]
    [InlineData("trainpc", "lan")]
    [InlineData("example.com", "direct")]
    [InlineData("signaling-karma.tun.ply.gg", "playit")]
    public void Routes_are_labelled_by_where_they_live(string host, string kind) => Assert.Equal(kind, MyServers.Kind(host));

    private static RouteInfo Pack(string name, string host, int port) => new(name.ToLowerInvariant().Replace(' ', '-'), name, name, host, port, MyServers.Kind(host), null, null, null);

    [Fact]
    public void The_hosts_private_presets_and_own_servers_follow_the_published_routes()
    {
        var published = new[] { Pack("Train - Playit", "signaling-karma.tun.ply.gg", 18113), Pack("Train - ZeroTier", "10.37.239.191", 8800) };
        var presets = new[]
        {
            new ServerPreset("Train - Playit", "signaling-karma.tun.ply.gg", 18113),
            new ServerPreset("Train - LAN", "192.168.1.20", 8800, Exportable: false),
        };

        var merged = RouteMerger.Merge(published, presets, new[] { new MyServer("Laptop", "192.168.1.30", 8800) });

        Assert.Equal(new[] { "Train - Playit", "Train - ZeroTier", "Train - LAN", "Laptop" }, merged.Select(r => r.Name));
        var lan = merged[2];
        Assert.Equal((RouteOrigin.Host, "lan", "host-train-lan"), (lan.Origin, lan.Kind, lan.Id));
        Assert.Equal(RouteMerger.HostOnlyNote, lan.Notes);
        Assert.Equal((RouteOrigin.Mine, "mine-laptop"), (merged[3].Origin, merged[3].Id));
        Assert.All(merged.Take(2), r => Assert.Equal(RouteOrigin.Pack, r.Origin));
    }

    [Fact]
    public void Exportable_presets_are_not_added_twice_and_duplicates_are_dropped()
    {
        var published = new[] { Pack("Train - Playit", "signaling-karma.tun.ply.gg", 18113) };
        var presets = new[] { new ServerPreset("Train - Playit", "signaling-karma.tun.ply.gg", 18113) };
        var mine = new[]
        {
            new MyServer("train - playit", "192.168.1.5", 8800),       // same name as a published route
            new MyServer("Via tunnel", "SIGNALING-KARMA.TUN.PLY.GG", 18113), // same address as a published route
            new MyServer("Mine", "192.168.1.6", 8800),
            new MyServer("Mine", "192.168.1.7", 8800),                 // same name twice (hand-edited settings)
        };

        var merged = RouteMerger.Merge(published, presets, mine);

        Assert.Equal(new[] { "Train - Playit", "Mine" }, merged.Select(r => r.Name));
        Assert.Equal("192.168.1.6", merged[1].Host);
    }

    [Fact]
    public void Settings_keep_my_servers_and_older_settings_files_load_without_them()
    {
        var saved = new AppSettings { MyServers = { new MyServer("Home", "192.168.1.20", 8800), new MyServer("Far", "example.com", 9000) } };

        var json = System.Text.Json.JsonSerializer.Serialize(saved, VmpLauncher.Core.Serialization.LauncherJson.Pretty);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, VmpLauncher.Core.Serialization.LauncherJson.Pretty)!;
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "playerName": "Vlad" }""", VmpLauncher.Core.Serialization.LauncherJson.Pretty)!;

        Assert.Equal(saved.MyServers, loaded.MyServers);
        Assert.Empty(old.MyServers);
    }

    [Fact]
    public void Ids_stay_unique_when_names_slug_alike()
    {
        var merged = RouteMerger.Merge(Array.Empty<RouteInfo>(), Array.Empty<ServerPreset>(),
            new[] { new MyServer("Home LAN", "192.168.1.5", 8800), new MyServer("home-lan", "192.168.1.6", 8800), new MyServer("!!!", "192.168.1.7", 8800) });

        Assert.Equal(new[] { "mine-home-lan", "mine-home-lan-2", "mine-server" }, merged.Select(r => r.Id));
    }
}
