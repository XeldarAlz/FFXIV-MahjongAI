namespace DomanMahjongAI.Engine;

/// <summary>
/// Publicly-visible state of one seat at the mahjong table — our own or an opponent.
/// Every field here is derivable from legal game observations (discard pool, open melds,
/// riichi declaration, ippatsu flag). Closed hands of opponents are NOT represented here
/// — they belong in the opponent-model belief state, not the observable snapshot.
/// </summary>
public sealed record SeatView(
    IReadOnlyList<Tile> Discards,
    IReadOnlyList<bool> DiscardIsTedashi,  // parallel to Discards
    IReadOnlyList<Meld> Melds,
    bool Riichi,
    int RiichiDiscardIndex,                // -1 if not riichi'd
    bool Ippatsu,
    bool IsTenpaiCalled,
    int DiscardCount = 0);                 // authoritative count even when Discards list is empty
                                           // (UI-tree walk pinned the per-seat count byte; the
                                           // tile pool itself lives in the game-state module
                                           // which is behind a stale static offset, so we track
                                           // count-only for now). Prefer this over Discards.Count
                                           // in policy code — the latter is 0 when the pool
                                           // couldn't be resolved.

/// <summary>
/// An immutable description of the current table state from our perspective.
/// Produced by the AddonEmjReader on every meaningful UI change. Consumed by
/// the opponent model, the policy pipeline, and the debug overlay.
///
/// SchemaVersion is bumped whenever the shape of this record or any nested record
/// changes; StateAggregator rejects snapshots that don't match the expected version.
/// </summary>
public sealed record StateSnapshot(
    // Self
    IReadOnlyList<Tile> Hand,
    IReadOnlyList<Meld> OurMelds,
    int OurSeat,                           // 0=E, 1=S, 2=W, 3=N
    bool OurRiichi,
    bool OurIppatsu,
    bool OurDoubleRiichi,

    // Table
    int RoundWind,                         // 0=E, 1=S (hanchan)
    int Honba,
    int RiichiSticks,
    IReadOnlyList<int> Scores,             // length 4
    IReadOnlyList<Tile> DoraIndicators,
    IReadOnlyList<Tile> UraDoraIndicators,
    int WallRemaining,
    int TurnIndex,
    int DealerSeat,

    IReadOnlyList<SeatView> Seats,         // length 4, indexed by seat (self included)

    LegalActions Legal,
    int SchemaVersion)
{
    public const int CurrentSchemaVersion = 1;

    public SeatView Us => Seats[OurSeat];

    public static StateSnapshot Empty { get; } = new(
        Hand: [],
        OurMelds: [],
        OurSeat: 0,
        OurRiichi: false,
        OurIppatsu: false,
        OurDoubleRiichi: false,
        RoundWind: 0,
        Honba: 0,
        RiichiSticks: 0,
        Scores: [25000, 25000, 25000, 25000],
        DoraIndicators: [],
        UraDoraIndicators: [],
        WallRemaining: 70,
        TurnIndex: 0,
        DealerSeat: 0,
        Seats:
        [
            EmptySeat(), EmptySeat(), EmptySeat(), EmptySeat(),
        ],
        Legal: LegalActions.None,
        SchemaVersion: CurrentSchemaVersion);

    private static SeatView EmptySeat() => new(
        Discards: [],
        DiscardIsTedashi: [],
        Melds: [],
        Riichi: false,
        RiichiDiscardIndex: -1,
        Ippatsu: false,
        IsTenpaiCalled: false);
}
