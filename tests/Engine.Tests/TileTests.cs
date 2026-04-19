using Xunit;

namespace DomanMahjongAI.Engine.Tests;

public class TileTests
{
    [Theory]
    [InlineData(0, TileSuit.Man, 1)]
    [InlineData(8, TileSuit.Man, 9)]
    [InlineData(9, TileSuit.Pin, 1)]
    [InlineData(17, TileSuit.Pin, 9)]
    [InlineData(18, TileSuit.Sou, 1)]
    [InlineData(26, TileSuit.Sou, 9)]
    public void Suited_tile_exposes_suit_and_number(int id, TileSuit suit, int num)
    {
        var t = Tile.FromId(id);
        Assert.Equal(suit, t.Suit);
        Assert.Equal(num, t.Number);
        Assert.False(t.IsHonor);
    }

    [Theory]
    [InlineData(27, 1)] // East
    [InlineData(30, 4)] // North
    [InlineData(31, 5)] // haku
    [InlineData(33, 7)] // chun
    public void Honor_tile_maps_to_z_digit(int id, int zDigit)
    {
        var t = Tile.FromId(id);
        Assert.Equal(TileSuit.Honor, t.Suit);
        Assert.Equal(zDigit, t.HonorNumber);
        Assert.True(t.IsHonor);
    }

    [Fact]
    public void Terminals_and_honors_flagged()
    {
        Assert.True(Tile.FromId(0).IsTerminal);   // 1m
        Assert.True(Tile.FromId(8).IsTerminal);   // 9m
        Assert.False(Tile.FromId(4).IsTerminal);  // 5m
        Assert.True(Tile.FromId(27).IsTerminalOrHonor);
        Assert.True(Tile.FromId(0).IsTerminalOrHonor);
        Assert.True(Tile.FromId(4).IsSimple);
    }

    [Fact]
    public void Dragons_flagged()
    {
        Assert.True(Tile.FromId(31).IsDragon);
        Assert.True(Tile.FromId(33).IsDragon);
        Assert.False(Tile.FromId(30).IsDragon);
        Assert.True(Tile.FromId(30).IsWind);
    }

    [Theory]
    [InlineData("1m", 0)]
    [InlineData("9m", 8)]
    [InlineData("1p", 9)]
    [InlineData("5s", 22)]
    [InlineData("1z", 27)]
    [InlineData("5z", 31)]
    [InlineData("7z", 33)]
    public void Parse_single_tile(string text, int expectedId)
    {
        var tiles = Tiles.Parse(text);
        Assert.Single(tiles);
        Assert.Equal(expectedId, tiles[0].Id);
    }

    [Fact]
    public void Parse_multi_suit_hand()
    {
        var tiles = Tiles.Parse("123m456p789s");
        Assert.Equal(9, tiles.Length);
        // 1m=0, 2m=1, 3m=2; 4p=12, 5p=13, 6p=14; 7s=24, 8s=25, 9s=26
        Assert.Equal([0, 1, 2, 12, 13, 14, 24, 25, 26],
                     tiles.Select(t => (int)t.Id));
    }

    [Fact]
    public void Parse_honors()
    {
        var tiles = Tiles.Parse("1234567z");
        Assert.Equal(7, tiles.Length);
        for (int i = 0; i < 7; i++)
            Assert.Equal(27 + i, tiles[i].Id);
    }

    [Fact]
    public void Parse_rejects_invalid_honor_digit()
    {
        Assert.Throws<FormatException>(() => Tiles.Parse("8z"));
        Assert.Throws<FormatException>(() => Tiles.Parse("0z"));
    }

    [Fact]
    public void Parse_rejects_dangling_digits()
    {
        Assert.Throws<FormatException>(() => Tiles.Parse("123"));
    }

    [Fact]
    public void Parse_rejects_unknown_suit()
    {
        Assert.Throws<FormatException>(() => Tiles.Parse("1q"));
    }

    [Fact]
    public void Parse_then_render_roundtrips_for_canonical_forms()
    {
        // Canonical form = per-suit ascending digits then suit letter.
        string[] cases = ["123m456p789s1234567z", "1122m3344p5566s77z", "119m19p19s1234567z"];
        foreach (var c in cases)
        {
            var tiles = Tiles.Parse(c);
            var rendered = Tiles.Render(tiles);
            Assert.Equal(c, rendered);
        }
    }

    [Fact]
    public void Render_canonicalizes_ordering()
    {
        // Digits may appear out of order; render sorts within each suit.
        var tiles = Tiles.Parse("19m19p19s1234567z1m");
        Assert.Equal("119m19p19s1234567z", Tiles.Render(tiles));
    }

    [Fact]
    public void ShortName_readable()
    {
        Assert.Equal("1m", Tile.FromId(0).ShortName);
        Assert.Equal("9s", Tile.FromId(26).ShortName);
        Assert.Equal("5z", Tile.FromId(31).ShortName);
    }
}
