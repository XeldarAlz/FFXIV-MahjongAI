namespace DomanMahjongAI.Engine;

public enum TileSuit : byte
{
    Man = 0,
    Pin = 1,
    Sou = 2,
    Honor = 3,
}

/// <summary>
/// 34-space tile. Id layout:
///   0..8   = 1m..9m
///   9..17  = 1p..9p
///   18..26 = 1s..9s
///   27..30 = E,S,W,N winds
///   31..33 = haku (white), hatsu (green), chun (red) dragons
/// </summary>
public readonly record struct Tile(byte Id) : IComparable<Tile>
{
    public const int Count34 = 34;
    public const int CopiesPerKind = 4;

    public static Tile FromId(int id) => new((byte)id);

    public TileSuit Suit => Id < 9 ? TileSuit.Man
                          : Id < 18 ? TileSuit.Pin
                          : Id < 27 ? TileSuit.Sou
                          : TileSuit.Honor;

    public int Number => Suit == TileSuit.Honor ? 0 : (Id % 9) + 1;
    public int HonorNumber => Suit == TileSuit.Honor ? Id - 27 + 1 : 0;

    public bool IsHonor => Id >= 27;
    public bool IsWind => Id >= 27 && Id <= 30;
    public bool IsDragon => Id >= 31;
    public bool IsTerminal => !IsHonor && (Number == 1 || Number == 9);
    public bool IsTerminalOrHonor => IsHonor || IsTerminal;
    public bool IsSimple => !IsTerminalOrHonor;

    public int CompareTo(Tile other) => Id.CompareTo(other.Id);

    public string ShortName => Suit switch
    {
        TileSuit.Man => $"{Number}m",
        TileSuit.Pin => $"{Number}p",
        TileSuit.Sou => $"{Number}s",
        TileSuit.Honor => $"{HonorNumber}z",
        _ => "?",
    };

    public override string ToString() => ShortName;

    public static IEnumerable<Tile> All34()
    {
        for (int i = 0; i < Count34; i++) yield return new Tile((byte)i);
    }
}

public static class Tiles
{
    /// <summary>
    /// Parse tile notation like "123m456p789s1234567z" into a sorted 34-space tile array.
    /// Digits accumulate until a suit letter (m/p/s/z), which flushes them.
    /// For z: 1=E, 2=S, 3=W, 4=N, 5=haku, 6=hatsu, 7=chun.
    /// </summary>
    public static Tile[] Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var result = new List<Tile>();
        var buffer = new List<int>(8);

        foreach (char raw in text)
        {
            if (char.IsWhiteSpace(raw)) continue;
            char c = char.ToLowerInvariant(raw);

            if (c >= '0' && c <= '9')
            {
                buffer.Add(c - '0');
                continue;
            }

            int suitBase = c switch
            {
                'm' => 0,
                'p' => 9,
                's' => 18,
                'z' => 27,
                _ => throw new FormatException($"Unknown suit char '{raw}' in \"{text}\""),
            };

            foreach (int n in buffer)
            {
                if (suitBase == 27)
                {
                    if (n < 1 || n > 7)
                        throw new FormatException($"Honor digit must be 1..7 (got {n}) in \"{text}\"");
                    result.Add(new Tile((byte)(27 + n - 1)));
                }
                else
                {
                    if (n < 1 || n > 9)
                        throw new FormatException($"Suited digit must be 1..9 (got {n}) in \"{text}\"");
                    result.Add(new Tile((byte)(suitBase + n - 1)));
                }
            }
            buffer.Clear();
        }

        if (buffer.Count > 0)
            throw new FormatException($"Dangling digits without suit letter in \"{text}\"");

        return result.ToArray();
    }

    /// <summary>Render an unordered tile set as "123m45p6s7z" — sorted per suit.</summary>
    public static string Render(IEnumerable<Tile> tiles)
    {
        var counts = new int[Tile.Count34];
        foreach (var t in tiles) counts[t.Id]++;
        return RenderCounts(counts);
    }

    public static string RenderCounts(ReadOnlySpan<int> counts)
    {
        if (counts.Length != Tile.Count34)
            throw new ArgumentException($"counts must be length {Tile.Count34}");

        var sb = new System.Text.StringBuilder();
        for (int suit = 0; suit < 4; suit++)
        {
            int lo = suit * 9;
            int hi = suit == 3 ? 34 : lo + 9;
            int len = hi - lo;
            bool any = false;
            for (int i = 0; i < len; i++)
                if (counts[lo + i] > 0) { any = true; break; }
            if (!any) continue;

            for (int i = 0; i < len; i++)
                for (int k = 0; k < counts[lo + i]; k++)
                    sb.Append(i + 1);

            sb.Append(suit switch { 0 => 'm', 1 => 'p', 2 => 's', _ => 'z' });
        }
        return sb.ToString();
    }

    /// <summary>Convert an unordered tile set into 34-space counts.</summary>
    public static int[] ToCounts(IEnumerable<Tile> tiles)
    {
        var counts = new int[Tile.Count34];
        foreach (var t in tiles) counts[t.Id]++;
        return counts;
    }
}
