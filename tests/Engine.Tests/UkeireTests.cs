using Xunit;

namespace DomanMahjongAI.Engine.Tests;

public class UkeireTests
{
    [Fact]
    public void Tenpai_hand_has_entry_with_shanten_zero_and_accepted_tiles()
    {
        // 14-tile hand where discarding any single tile leaves 13 that can be tenpai.
        // 123m 456p 789s 11z 22z (13 tiles = tenpai) + an extra 3z for 14.
        var hand = Hand.FromNotation("123m456p789s11z22z3z");
        Assert.Equal(14, hand.ClosedTileCount);

        var ukeire = UkeireEnumerator.Enumerate(hand);

        // Discarding 3z must leave shanten = 0 with a ukeire (1z or 2z).
        var dropZ3 = ukeire.Single(e => e.Discard.Id == 29);  // 3z = 27+2 = 29
        Assert.Equal(0, dropZ3.ShantenAfter);
        Assert.Contains(dropZ3.AcceptedKinds, t => t.Id == 27); // 1z
        Assert.Contains(dropZ3.AcceptedKinds, t => t.Id == 28); // 2z
    }

    [Fact]
    public void Weighted_ukeire_honors_wall_visibility()
    {
        var hand = Hand.FromNotation("123m456p789s11z22z3z");
        var wall = new Wall();
        // Pretend three 1z have already been seen (opponents' discards, etc.)
        for (int i = 0; i < 3; i++) wall.Observe(Tile.FromId(27));

        var ukeire = UkeireEnumerator.Enumerate(hand, wall);
        var dropZ3 = ukeire.Single(e => e.Discard.Id == 29);

        // Accepted kinds still include 1z and 2z (1z is hand-held, not yet seen in wall tracker).
        // Weighted count for 1z should contribute only 1 (4-3). 2z still contributes 4.
        Assert.Contains(dropZ3.AcceptedKinds, t => t.Id == 27);
        // Weighted total must be strictly less than the unwall version.
        var unwalled = UkeireEnumerator.Enumerate(hand).Single(e => e.Discard.Id == 29);
        Assert.True(dropZ3.WeightedCount < unwalled.WeightedCount);
    }

    [Fact]
    public void Requires_fourteen_tile_hand()
    {
        var hand = Hand.FromNotation("123m456p789s11z");  // 11 tiles
        Assert.Throws<ArgumentException>(() => UkeireEnumerator.Enumerate(hand));
    }
}
