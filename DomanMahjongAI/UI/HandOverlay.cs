using Dalamud.Bindings.ImGui;
using DomanMahjongAI.Actions;
using DomanMahjongAI.Engine;
using DomanMahjongAI.GameState;
using DomanMahjongAI.Policy;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace DomanMahjongAI.UI;

/// <summary>
/// Draws a colored box + arrow on the recommended discard tile directly on top of
/// the <c>Emj</c> game addon. Uses <see cref="ImGui.GetForegroundDrawList()"/> so
/// it renders above every other ImGui surface without needing a dedicated window
/// or input passthrough handling.
///
/// The hand-tile nodes aren't reverse-engineered yet, so we locate them by
/// geometry: walk the addon's node list, keep the visible nodes whose rectangles
/// are tile-shaped, cluster them by Y, and take the 14 that form the closest
/// horizontal row. If that cluster can't be found (fewer than 14 matches, or the
/// Y-spread is too wide) we skip drawing this frame — the MainWindow text is the
/// fallback cue.
/// </summary>
public sealed class HandOverlay : IDisposable
{
    // Tile geometry bounds (unit-local pixels, before the addon's Scale).
    // Doman tiles are roughly square-ish, closer to 50×70. Generous bounds so
    // users with tweaked UI scales still get a match.
    private const float MinTileWidth = 28f;
    private const float MaxTileWidth = 120f;
    private const float MinTileHeight = 45f;
    private const float MaxTileHeight = 160f;

    // Max Y-spread (in addon-local pixels) across the 14 tiles we accept as a row.
    // Doman's hand-tile row has all tiles at the same Y within a pixel or two.
    private const float MaxRowYSpread = 12f;

    private readonly Plugin plugin;
    private bool disposed;

    public HandOverlay(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.PluginInterface.UiBuilder.Draw += Draw;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Plugin.PluginInterface.UiBuilder.Draw -= Draw;
    }

    private unsafe void Draw()
    {
        var cfg = plugin.Configuration;
        if (!cfg.TosAccepted || !cfg.ShowInGameHighlight) return;
        // Only draw when the plugin is actively advising — i.e. Suggestions mode.
        // Auto-play mode doesn't need a visual cue since the plugin clicks for you.
        if (!cfg.AutomationArmed || !cfg.SuggestionOnly) return;

        var ptr = Plugin.GameGui.GetAddonByName(AddonEmjReader.AddonName);
        if (ptr.Address == nint.Zero) return;
        var unit = (AtkUnitBase*)ptr.Address;
        if (!unit->IsVisible) return;

        var snap = plugin.AddonReader.TryBuildSnapshot();
        // Need at least a few tiles to talk about a hand. We no longer require
        // exactly 14 — after calls (chi/pon/kan) the hand row is shorter because
        // some tiles live in the meld area. As long as the policy gives us a
        // discard target and the tile is actually in the hand row, highlight it.
        if (snap is null || snap.Hand.Count < 2) return;

        ActionChoice choice;
        try { choice = plugin.Policy.Choose(snap); }
        catch { return; }

        if (choice.DiscardTile is null) return;
        int slot = InputDispatcher.FindSlotOfTile(choice.DiscardTile.Value, snap.Hand);
        if (slot < 0 || slot >= snap.Hand.Count) return;

        var rects = TryFindHandTileRects(unit, snap.Hand.Count);
        if (rects is null || slot >= rects.Count) return;

        // Keep the viewport offset defensively — in single-viewport mode Pos is
        // (0, 0) so this is a no-op, in multi-viewport mode it's the desktop
        // offset of the game window.
        var viewportOffset = ImGui.GetMainViewport().Pos;
        var rect = rects[slot];
        rect.Pos += viewportOffset;
        // Slot N-1 is conventionally the last-drawn tile (often slightly offset
        // from the main row). Tag it amber so tsumogiri reads distinctly.
        bool isDrawnTile = slot == snap.Hand.Count - 1;
        DrawHighlight(rect, isDrawnTile);
    }

    private static unsafe List<(Vector2 Pos, Vector2 Size)>? TryFindHandTileRects(AtkUnitBase* unit, int expected)
    {
        var uld = unit->UldManager;
        if (uld.NodeList == null || uld.NodeListCount <= 0) return null;

        // Walk tile-shaped visible nodes and stamp each with its absolute rect.
        // The parent-chain walk already incorporates the root node's position
        // and all scale factors — so we do NOT add unit->X/Y or multiply by
        // unit->Scale again here. Doing so caused a double-count when the addon
        // was dragged away from the top-left of the game window.
        var tiles = new List<(float Y, Vector2 Pos, Vector2 Size)>(32);

        for (int i = 0; i < uld.NodeListCount; i++)
        {
            var n = uld.NodeList[i];
            if (n == null) continue;
            if (!n->IsVisible()) continue;

            float w = n->Width;
            float h = n->Height;
            if (w < MinTileWidth || w > MaxTileWidth) continue;
            if (h < MinTileHeight || h > MaxTileHeight) continue;
            if (w > h) continue; // tiles are taller than wide

            AbsolutePosition(n, out float nx, out float ny, out float sx, out float sy);

            tiles.Add((ny,
                new Vector2(nx, ny),
                new Vector2(w * sx, h * sy)));
        }

        if (tiles.Count < expected) return null;

        // Cluster by Y: find the tightest horizontal row of `expected` tiles.
        tiles.Sort((a, b) => a.Y.CompareTo(b.Y));

        int bestStart = -1;
        float bestSpan = float.MaxValue;
        for (int i = 0; i + expected <= tiles.Count; i++)
        {
            float span = tiles[i + expected - 1].Y - tiles[i].Y;
            if (span < bestSpan)
            {
                bestSpan = span;
                bestStart = i;
            }
        }

        if (bestStart < 0 || bestSpan > MaxRowYSpread) return null;

        var selected = new List<(Vector2 Pos, Vector2 Size)>(expected);
        for (int i = bestStart; i < bestStart + expected; i++)
            selected.Add((tiles[i].Pos, tiles[i].Size));
        selected.Sort((a, b) => a.Pos.X.CompareTo(b.Pos.X));
        return selected;
    }

    /// <summary>
    /// Walk the parent chain and return the node's absolute position + accumulated
    /// scale, expressed in the same coordinate space that <see cref="ImGui.GetForegroundDrawList()"/>
    /// expects (game-window-local, before the multi-viewport desktop offset).
    /// The recurrence mirrors the standard Dalamud overlay pattern: at each step,
    /// scale the child's running offset by the parent's scale, then add the parent's
    /// own translation. After the walk the result already includes the root node's
    /// position — <em>do not</em> add <c>unit-&gt;X</c> again on top.
    /// </summary>
    private static unsafe void AbsolutePosition(AtkResNode* node, out float x, out float y, out float scaleX, out float scaleY)
    {
        x = 0; y = 0; scaleX = 1f; scaleY = 1f;
        var cur = node;
        while (cur != null)
        {
            x = cur->X + x * cur->ScaleX;
            y = cur->Y + y * cur->ScaleY;
            scaleX *= cur->ScaleX;
            scaleY *= cur->ScaleY;
            cur = cur->ParentNode;
        }
    }

    private void DrawHighlight((Vector2 Pos, Vector2 Size) rect, bool isDrawnTile)
    {
        // Pulse the highlight so it's noticeable against a busy board. Period ~1.4s.
        float t = (float)((DateTime.UtcNow.TimeOfDay.TotalSeconds % 1.4) / 1.4);
        float pulse = 0.6f + 0.4f * MathF.Sin(t * MathF.PI * 2f);

        // Green for a normal hand discard, amber for the just-drawn tile (slot 13)
        // so the user immediately sees "tsumogiri" vs. "discard from hand."
        var baseColor = isDrawnTile
            ? new Vector4(1.00f, 0.75f, 0.15f, 1f)   // amber
            : new Vector4(0.15f, 0.95f, 0.45f, 1f);  // green

        uint edge = PackColor(baseColor, pulse);
        uint fill = PackColor(baseColor, pulse * 0.22f);

        var dl = ImGui.GetForegroundDrawList();
        var min = rect.Pos;
        var max = rect.Pos + rect.Size;

        // Slight outset so the border hugs the tile edge rather than cutting into it.
        min -= new Vector2(2, 2);
        max += new Vector2(2, 2);

        dl.AddRectFilled(min, max, fill, 6f);
        dl.AddRect(min, max, edge, 6f, ImDrawFlags.None, 3f);

        // Downward-pointing arrow above the tile.
        float cx = (min.X + max.X) * 0.5f;
        float tipY = min.Y - 4f;
        float baseY = tipY - 14f;
        dl.AddTriangleFilled(
            new Vector2(cx - 10f, baseY),
            new Vector2(cx + 10f, baseY),
            new Vector2(cx, tipY),
            edge);
    }

    private static uint PackColor(Vector4 rgba, float alphaMul)
    {
        float a = Math.Clamp(rgba.W * alphaMul, 0f, 1f);
        uint r = (uint)(Math.Clamp(rgba.X, 0f, 1f) * 255f);
        uint g = (uint)(Math.Clamp(rgba.Y, 0f, 1f) * 255f);
        uint b = (uint)(Math.Clamp(rgba.Z, 0f, 1f) * 255f);
        uint ai = (uint)(a * 255f);
        // ImGui packs as ABGR in its uint32 color format.
        return (ai << 24) | (b << 16) | (g << 8) | r;
    }
}
