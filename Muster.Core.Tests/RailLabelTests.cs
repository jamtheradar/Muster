using Muster.Core.Config;

namespace Muster.Core.Tests;

/// <summary>
/// The rail tile is how you tell one tenant from another at a glance, so what it shows is worth
/// pinning down. Initials are only the fallback.
/// </summary>
public sealed class RailLabelTests
{
    [Theory]
    [InlineData("DataByte", "DA")]
    [InlineData("Fabrikam", "FA")]
    [InlineData("Contoso Group", "CG")]
    [InlineData("X", "X")]
    [InlineData("", "?")]
    public void Without_an_abbreviation_the_tile_falls_back_to_initials(string name, string expected)
    {
        var workspace = new WorkspaceConfig { Id = "w", Name = name };

        Assert.Equal(expected, workspace.RailLabel);
    }

    [Fact]
    public void An_abbreviation_wins_over_the_name()
    {
        // Two tenants starting with the same letters is exactly why this field exists.
        var workspace = new WorkspaceConfig { Id = "w", Name = "DataByte", Abbreviation = "DB" };

        Assert.Equal("DB", workspace.RailLabel);
    }

    [Fact]
    public void An_abbreviation_is_taken_verbatim_apart_from_surrounding_space()
    {
        // Not upper-cased: "iX" and "3M" are the names people actually want on the tile.
        var workspace = new WorkspaceConfig { Id = "w", Name = "Ignored", Abbreviation = " iX " };

        Assert.Equal("iX", workspace.RailLabel);
    }

    [Fact]
    public void Whitespace_is_treated_as_no_abbreviation()
    {
        var workspace = new WorkspaceConfig { Id = "w", Name = "DataByte", Abbreviation = "   " };

        Assert.Equal("DA", workspace.RailLabel);
    }

    [Fact]
    public void Abbreviation_differences_break_config_equality()
    {
        // Hot-reload will diff two loads to decide what changed; a field that compares equal
        // regardless of its value would be a silent hole.
        var workspace = new WorkspaceConfig { Id = "w", Name = "DataByte" };

        Assert.NotEqual(workspace, workspace with { Abbreviation = "DB" });
        Assert.Equal(workspace, workspace with { Abbreviation = null });
    }
}
