using Dalamud.Bindings.ImGui;
using DomanMahjongAI.Engine;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace DomanMahjongAI.UI;

/// <summary>
/// Shared styling primitives for MainWindow + DebugOverlay.
/// Palette, surface cards, suit-colored tile rendering, pills, section headers.
/// All helpers push/pop balanced state — safe for nesting within a single window.
/// </summary>
internal static class Theme
{
    // ---- Text / chrome ---------------------------------------------------
    public static readonly Vector4 Header = new(0.97f, 0.97f, 1.00f, 1f);
    public static readonly Vector4 Body   = new(0.86f, 0.88f, 0.92f, 1f);
    public static readonly Vector4 Muted  = new(0.62f, 0.62f, 0.66f, 1f);
    public static readonly Vector4 Faint  = new(0.45f, 0.45f, 0.48f, 1f);

    // ---- Semantic --------------------------------------------------------
    public static readonly Vector4 Accent = new(0.28f, 0.82f, 0.62f, 1f);
    public static readonly Vector4 Warn   = new(0.98f, 0.80f, 0.30f, 1f);
    public static readonly Vector4 Danger = new(1.00f, 0.45f, 0.35f, 1f);
    public static readonly Vector4 Info   = new(0.48f, 0.72f, 0.98f, 1f);

    // ---- Surfaces --------------------------------------------------------
    public static readonly Vector4 Surface    = new(0.10f, 0.11f, 0.13f, 0.90f);
    public static readonly Vector4 SurfaceAlt = new(0.13f, 0.14f, 0.16f, 0.90f);
    public static readonly Vector4 Divider    = new(1f, 1f, 1f, 0.06f);
    public static readonly Vector4 Border     = new(1f, 1f, 1f, 0.08f);

    // ---- Tile face palette (mahjong-inspired ivory) ----------------------
    public static readonly Vector4 TileFace   = new(0.95f, 0.92f, 0.84f, 1f);
    public static readonly Vector4 TileBorder = new(0.17f, 0.16f, 0.14f, 1f);
    public static readonly Vector4 TileShadow = new(0.00f, 0.00f, 0.00f, 0.35f);

    public static readonly Vector4 ManInk   = new(0.58f, 0.13f, 0.13f, 1f);
    public static readonly Vector4 PinInk   = new(0.13f, 0.32f, 0.62f, 1f);
    public static readonly Vector4 SouInk   = new(0.15f, 0.50f, 0.23f, 1f);
    public static readonly Vector4 HonorInk = new(0.28f, 0.24f, 0.13f, 1f);

    public static Vector4 SuitInk(TileSuit s) => s switch
    {
        TileSuit.Man   => ManInk,
        TileSuit.Pin   => PinInk,
        TileSuit.Sou   => SouInk,
        _              => HonorInk,
    };

    // ---- Color packing ---------------------------------------------------
    public static uint Pack(Vector4 c)
    {
        uint r  = (uint)(Math.Clamp(c.X, 0f, 1f) * 255f);
        uint g  = (uint)(Math.Clamp(c.Y, 0f, 1f) * 255f);
        uint b  = (uint)(Math.Clamp(c.Z, 0f, 1f) * 255f);
        uint ai = (uint)(Math.Clamp(c.W, 0f, 1f) * 255f);
        return (ai << 24) | (b << 16) | (g << 8) | r;
    }

    public static uint Pack(Vector4 c, float alphaMul)
        => Pack(new Vector4(c.X, c.Y, c.Z, c.W * alphaMul));

    public static Vector4 Fade(Vector4 c, float alpha)
        => new(c.X, c.Y, c.Z, alpha);

    /// <summary>Sine-based attention pulse in [lo, hi]. Period in seconds.</summary>
    public static float Pulse(float period = 1.4f, float lo = 0.55f, float hi = 1.0f)
    {
        float t = (float)((DateTime.UtcNow.TimeOfDay.TotalSeconds % period) / period);
        float s = 0.5f + 0.5f * MathF.Sin(t * MathF.PI * 2f);
        return lo + (hi - lo) * s;
    }

    // ---- Window-scope style (RAII) ---------------------------------------
    /// <summary>
    /// Push a consistent base style for both windows. Always pair with
    /// <c>using var _s = Theme.PushWindowStyle();</c> at the top of Draw().
    /// </summary>
    public static StyleScope PushWindowStyle()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,      new Vector2(14, 12));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,        new Vector2(8, 6));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,      6f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,       new Vector2(10, 6));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize,    0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding,      8f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize,    1f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,      6f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding,  6f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding,       4f);
        ImGui.PushStyleColor(ImGuiCol.Border,    Border);
        ImGui.PushStyleColor(ImGuiCol.Separator, Divider);
        return default;
    }

    public struct StyleScope : IDisposable
    {
        public void Dispose()
        {
            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar(10);
        }
    }

    // ---- Card (surface panel via drawlist channel-split) -----------------
    /// <summary>
    /// Open a surface card that auto-fits its content. Uses drawlist
    /// channel-split so the background paints behind content drawn inside
    /// the <c>using</c> block, without needing a fixed height.
    /// </summary>
    public static Card BeginCard(string id, bool alt = false)
        => new(new Vector2(12, 10), alt ? SurfaceAlt : Surface, Border);

    public struct Card : IDisposable
    {
        private readonly Vector2 start;
        private readonly float width;
        private readonly Vector2 pad;
        private readonly Vector4 bg;
        private readonly Vector4 border;
        private readonly ImDrawListPtr dl;

        public Card(Vector2 padding, Vector4 bg, Vector4 border)
        {
            this.pad = padding;
            this.bg = bg;
            this.border = border;
            dl = ImGui.GetWindowDrawList();
            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);
            start = ImGui.GetCursorScreenPos();
            width = ImGui.GetContentRegionAvail().X;
            ImGui.Dummy(new Vector2(0, pad.Y));
            ImGui.Indent(pad.X);
            ImGui.PushTextWrapPos(start.X + width - pad.X);
        }

        public void Dispose()
        {
            ImGui.PopTextWrapPos();
            ImGui.Unindent(pad.X);
            ImGui.Dummy(new Vector2(0, pad.Y));
            float endY = ImGui.GetCursorScreenPos().Y;
            var min = start;
            var max = new Vector2(start.X + width, endY);
            dl.ChannelsSetCurrent(0);
            dl.AddRectFilled(min, max, Pack(bg), 8f);
            dl.AddRect(min, max, Pack(border), 8f, ImDrawFlags.None, 1f);
            dl.ChannelsMerge();
        }
    }

    // ---- Section header (accent text + thin rule) ------------------------
    public static void SectionHeader(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Header);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        dl.AddLine(p + new Vector2(0, 2), new Vector2(p.X + w, p.Y + 2), Pack(Divider), 1f);
        ImGui.Dummy(new Vector2(0, 6));
    }

    public static void Subtle(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Faint);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    public static void Caption(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    // ---- Rounded pill with centered label --------------------------------
    public static void Pill(string label, Vector4 tint, bool filled)
    {
        const float padX = 14f;
        const float height = 24f;
        var ts = ImGui.CalcTextSize(label);
        var size = new Vector2(ts.X + padX * 2, height);
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        float r = height * 0.5f;
        Vector4 fill = filled ? tint : Fade(tint, 0.14f);
        dl.AddRectFilled(min, max, Pack(fill), r);
        dl.AddRect(min, max, Pack(tint, filled ? 0.85f : 1.0f), r, ImDrawFlags.None, 1.5f);
        var tp = min + new Vector2((size.X - ts.X) * 0.5f, (size.Y - ts.Y) * 0.5f);
        Vector4 textColor = filled ? new Vector4(1f, 1f, 1f, 1f) : tint;
        dl.AddText(tp, Pack(textColor), label);
        ImGui.Dummy(size);
    }

    /// <summary>Right-align the next item on the current line by shifting the cursor.</summary>
    public static void RightAlign(float itemWidth)
    {
        ImGui.SameLine();
        float remaining = ImGui.GetContentRegionAvail().X;
        if (remaining > itemWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + remaining - itemWidth);
    }

    // ---- Tile rendering --------------------------------------------------
    public const float TileW = 26f;
    public const float TileH = 36f;
    public const float TileGap = 3f;
    public const float TileSuitGap = 8f;
    public const float BigTileW = 44f;
    public const float BigTileH = 60f;
    public const float SmallTileW = 22f;
    public const float SmallTileH = 30f;

    private static string TileNumberGlyph(Tile t) => t.Suit switch
    {
        TileSuit.Man or TileSuit.Pin or TileSuit.Sou => t.Number.ToString(),
        TileSuit.Honor => t.HonorNumber switch
        {
            1 => "E", 2 => "S", 3 => "W", 4 => "N",
            5 => "白", // 白  haku
            6 => "發", // 發  hatsu
            7 => "中", // 中  chun
            _ => "?",
        },
        _ => "?",
    };

    private static string TileSuitGlyph(Tile t) => t.Suit switch
    {
        TileSuit.Man => "m",
        TileSuit.Pin => "p",
        TileSuit.Sou => "s",
        _            => "",
    };

    /// <summary>
    /// Draw a single tile at the current cursor position and reserve its footprint.
    /// <paramref name="emphasize"/> &gt; 0 paints an accent outline around the tile
    /// (used for the best-move highlight and pulsed picks).
    /// </summary>
    public static void DrawTile(Tile tile, Vector2 size, float emphasize = 0f)
    {
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        float round = 4f;

        dl.AddRectFilled(min + new Vector2(1, 2), max + new Vector2(1, 2), Pack(TileShadow), round);
        dl.AddRectFilled(min, max, Pack(TileFace), round);
        dl.AddRect(min, max, Pack(TileBorder), round, ImDrawFlags.None, 1.5f);

        if (emphasize > 0f)
        {
            dl.AddRect(
                min - new Vector2(2, 2),
                max + new Vector2(2, 2),
                Pack(Accent, emphasize),
                round + 2f, ImDrawFlags.None, 2f);
        }

        var ink = SuitInk(tile.Suit);
        string glyph = TileNumberGlyph(tile);
        string suit  = TileSuitGlyph(tile);
        var glyphSize = ImGui.CalcTextSize(glyph);

        if (!string.IsNullOrEmpty(suit))
        {
            var suitSize = ImGui.CalcTextSize(suit);
            var glyphPos = min + new Vector2((size.X - glyphSize.X) * 0.5f, (size.Y - glyphSize.Y) * 0.5f - 5);
            var suitPos  = min + new Vector2((size.X - suitSize.X) * 0.5f, size.Y - suitSize.Y - 3);
            dl.AddText(glyphPos, Pack(ink), glyph);
            dl.AddText(suitPos,  Pack(ink, 0.75f), suit);
        }
        else
        {
            var glyphPos = min + new Vector2((size.X - glyphSize.X) * 0.5f, (size.Y - glyphSize.Y) * 0.5f);
            dl.AddText(glyphPos, Pack(ink), glyph);
        }

        ImGui.Dummy(size);
    }

    /// <summary>
    /// Draw a row of tiles with a small gap between tiles of the same suit
    /// and a larger gap between suit groups.
    /// </summary>
    public static void DrawHand(IReadOnlyList<Tile> hand, int highlightSlot = -1)
    {
        if (hand.Count == 0) { ImGui.TextDisabled("—"); return; }
        float emphasize = Pulse(1.4f, 0.5f, 1.0f);
        var lastSuit = hand[0].Suit;
        for (int i = 0; i < hand.Count; i++)
        {
            if (i > 0)
            {
                bool suitChange = hand[i].Suit != lastSuit;
                ImGui.SameLine(0, suitChange ? TileSuitGap : TileGap);
                if (suitChange) lastSuit = hand[i].Suit;
            }
            float em = (i == highlightSlot) ? emphasize : 0f;
            DrawTile(hand[i], new Vector2(TileW, TileH), em);
        }
    }
}
