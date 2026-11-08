using NetGuard.Core.Config;
using NetGuard.Core.Editing;
using NetGuard.Core.Paths;
using NetGuard.Core.Policy;

namespace NetGuard.Core.Tests;

public class ConfigEditorTests
{
    [Theory]
    [InlineData("CN=Spotify AB, O=Spotify AB, L=Stockholm, C=SE", "Spotify AB")]
    [InlineData("CN=\"Blizzard Entertainment, Inc.\", O=\"Blizzard Entertainment, Inc.\", L=Irvine, S=California, C=US", "Blizzard Entertainment, Inc.")]
    [InlineData("CN=Some Dev", "Some Dev")]
    [InlineData(null, null)]
    public void Reads_the_signer_from_a_certificate_subject(string? subject, string? expected) =>
        Assert.Equal(expected, ConfigEditor.SignerName(subject));

    [Fact]
    public void Signed_files_are_pinned_to_their_publisher_unsigned_to_their_hash()
    {
        var signed = ConfigEditor.Suggest(@"C:\Games\Foo\foo.exe", "Foo Game", new SignatureInfo(true, "CN=Foo Ltd, O=Foo Ltd", null), _ => "AA");
        Assert.Equal(PinMode.Signature, signed.Pin);
        Assert.Equal("Foo Ltd", signed.Signer);
        Assert.Equal("Foo Game", signed.Name);

        var unsigned = ConfigEditor.Suggest(@"C:\Tools\bar.exe", null, new SignatureInfo(false, null, "unsigned"), _ => "ABCDEF");
        Assert.Equal(PinMode.Sha256, unsigned.Pin);
        Assert.Equal("ABCDEF", unsigned.Sha256);
        Assert.Equal("bar", unsigned.Name);
    }

    [Fact]
    public void Add_and_remove_programs_and_profiles()
    {
        var cfg = DefaultConfig.Create(new DetectedPaths());
        ConfigEditor.AddProfile(cfg, "Music");
        ConfigEditor.AddProgram(cfg, "Music", new ProgramEntry { Name = "Spotify", Paths = { @"C:\s.exe" } });
        ConfigEditor.AddProgram(cfg, "Music", new ProgramEntry { Name = "Spotify", Paths = { @"C:\s2.exe" } });
        Assert.Equal(new[] { "Spotify", "Spotify (2)" }, cfg.FindProfile("Music")!.Programs.Select(p => p.Name));
        Assert.Throws<InvalidOperationException>(() =>
            ConfigEditor.AddProgram(cfg, "Music", new ProgramEntry { Name = "x", Paths = { @"c:\S.EXE" } }));

        ConfigEditor.RemoveProgram(cfg, "Music", "Spotify");
        Assert.Single(cfg.FindProfile("Music")!.Programs);

        Assert.Throws<InvalidOperationException>(() => ConfigEditor.AddProfile(cfg, "music"));
        Assert.Throws<InvalidOperationException>(() => ConfigEditor.RemoveProfile(cfg, "Music", activeProfile: "Music"));
        ConfigEditor.RemoveProfile(cfg, "Music", activeProfile: "WoW");
        Assert.Null(cfg.FindProfile("Music"));
    }

    [Fact]
    public void Maintenance_takes_no_programs()
    {
        var cfg = DefaultConfig.Create(new DetectedPaths());
        Assert.Throws<InvalidOperationException>(() =>
            ConfigEditor.AddProgram(cfg, "Maintenance", new ProgramEntry { Name = "x", Paths = { @"C:\x.exe" } }));
    }

    [Fact]
    public void Always_blocked_list_is_editable_and_its_direction_is_reversed()
    {
        var cfg = DefaultConfig.Create(new DetectedPaths());
        ConfigEditor.AddProgram(cfg, ConfigEditor.AlwaysBlockedList, new ProgramEntry { Name = "Discord", Paths = { @"C:\d.exe" } });
        Assert.Contains(cfg.AlwaysBlocked, e => e.Name == "Discord");
        Assert.False(ConfigEditor.IsLoosening("program-add", ConfigEditor.AlwaysBlockedList));
        Assert.True(ConfigEditor.IsLoosening("program-remove", ConfigEditor.AlwaysBlockedList));
        Assert.True(ConfigEditor.IsLoosening("program-add", "WoW"));
        Assert.False(ConfigEditor.IsLoosening("program-remove", "WoW"));
    }

    [Fact]
    public void Loosening_the_config_only_without_a_lock_or_in_maintenance()
    {
        var cfg = DefaultConfig.Create(new DetectedPaths());
        Assert.True(ProfilePolicy.CanLoosenConfig(cfg, null).Allowed);
        Assert.True(ProfilePolicy.CanLoosenConfig(cfg, "Maintenance").Allowed);
        Assert.False(ProfilePolicy.CanLoosenConfig(cfg, "WoW").Allowed);
        Assert.False(ProfilePolicy.CanLoosenConfig(cfg, "Offline").Allowed);
    }
}
