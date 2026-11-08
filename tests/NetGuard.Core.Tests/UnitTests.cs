using NetGuard.Core.Config;
using NetGuard.Core.Hosts;
using NetGuard.Core.Paths;

namespace NetGuard.Core.Tests;

public class PathGlobTests
{
    [Fact]
    public void Expands_star_segments_and_double_star()
    {
        using var env = new TestEnv();
        env.Touch("", "A", "Agent.1", "Agent.exe");
        env.Touch("", "A", "Agent.2", "Agent.exe");
        env.Touch("", "A", "deep", "x", "y", "Tool.exe");
        env.Touch("", "A", "readme.txt");

        Assert.Equal(2, PathGlob.Expand(env.P("A", "Agent.*", "Agent.exe"), s => s).Count);
        Assert.Equal(3, PathGlob.Expand(env.P("A", "**", "*.exe"), s => s).Count);
        Assert.Single(PathGlob.Expand(env.P("A", "readme.txt"), s => s));
        Assert.Empty(PathGlob.Expand(env.P("A", "missing", "*.exe"), s => s));
        Assert.Empty(PathGlob.Expand("relative/path.exe", s => s));
    }
}

public class HostsFileTests
{
    private const string Original = "# comment\r\n127.0.0.1 localhost\r\n";

    [Fact]
    public void Adds_and_removes_block_without_touching_other_lines()
    {
        var withBlock = HostsFile.Rewrite(Original, new[] { "b.com", "a.com" });
        Assert.StartsWith(Original, withBlock);
        Assert.Equal(new[] { "a.com", "b.com" }, HostsFile.ReadManagedHosts(withBlock));
        Assert.True(HostsFile.IsUpToDate(withBlock, new[] { "a.com", "b.com" }));
        Assert.False(HostsFile.IsUpToDate(withBlock, new[] { "a.com" }));
        Assert.False(HostsFile.IsUpToDate(Original, new[] { "a.com" }));

        Assert.Equal(Original, HostsFile.Rewrite(withBlock, Array.Empty<string>()));
        Assert.Equal(withBlock, HostsFile.Rewrite(withBlock, new[] { "a.com", "b.com" }));
    }

    [Fact]
    public void Detects_edited_block()
    {
        var withBlock = HostsFile.Rewrite(Original, new[] { "a.com" });
        var edited = withBlock.Replace("0.0.0.0 a.com", "# 0.0.0.0 a.com");
        Assert.False(HostsFile.IsUpToDate(edited, new[] { "a.com" }));
    }
}

public class ConfigTests
{
    [Fact]
    public void Default_config_round_trips_and_validates()
    {
        using var env = new TestEnv();
        var cfg = DefaultConfig.Create(new DetectedPaths());
        var path = env.P("config.json");
        cfg.Save(path);
        var loaded = NetGuardConfig.Load(path);

        Assert.Equal(new[] { "Offline", "WoW", "Maintenance" }, loaded.Profiles.Select(p => p.Name));
        Assert.Equal(ProfileKind.AllowAll, loaded.FindProfile("maintenance")!.Kind);
        Assert.Equal(400, loaded.Timings.ConfirmPassageLength);
        Assert.Equal(3, loaded.Timings.TestModeMinutes);
        Assert.Contains(loaded.BaseServiceRules, r => r.Service == "Dnscache" && r.RemotePorts == "53");
        Assert.Contains("\"kind\": \"AllowAll\"", File.ReadAllText(path));
    }

    [Fact]
    public void No_profile_ever_allows_steam()
    {
        var cfg = DefaultConfig.Create(new DetectedPaths());
        Assert.DoesNotContain(cfg.Profiles.SelectMany(p => p.Programs).SelectMany(p => p.Paths),
            p => p.Contains("steam", StringComparison.OrdinalIgnoreCase));
    }
}
