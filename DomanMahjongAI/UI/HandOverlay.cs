using Dalamud.Bindings.ImGui;
using DomanMahjongAI.Actions;
using DomanMahjongAI.Engine;
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

        var ptr = Plugin.GameGui.GetAddonByName("Emj");
        if (ptr.Address == nint.Zero) return;
        var unit = (AtkUnitBase*)ptr.Address;
        if (!unit->IsVisible) return;

        var snap = plugin.AddonReader.TryBuildSnapshot();
        if (snap is null || snap.Hand.Count != 14) return;

        ActionChoice choice;
        try { choice = plugin.Policy.Choose(snap); }
        catch { return; }

        if (choice.DiscardTile is null) return;
        int slot = InputDispatcher.FindSlotOfTile(choice.DiscardTile.Value, snap.Hand);
        if (slot is < 0 or > 13) return;

        var rects = TryFindHandTileRects(unit);
        if (rects is null || slot >= rects.Count) return;

        DrawHighlight(rects[slot], isDrawnTile: slot == 13);
    }

    private static unsafe List<(Vector2 Pos, Vector2 Size)>? TryFindHandTileRects(AtkUnitBase* unit)
    {
        var uld = unit->UldManager;
        if (uld.NodeList == null || uld.NodeListCount <= 0) return null;

        // Collect tile-shaped visible nodes with their absolute screen rects.
        var tiles = new List<(float Y, Vector2 Pos, Vector2 Size)>(32);
        float addonScale = unit->Scale <= 0 ? 1f : unit->Scale;
        float addonX = unit->X;
        float addonY = unit->Y;

        for (int i = 0; i < uld.NodeListCount; i++)
        {
            var n = uld.NodeList[i];
            if (n == null) continue;
            if (!n->IsVisible()) continue;

            float w = n->Width;
            float h = n->Height;
            if (w < MinTileWidth || w > MaxTileWidth) continue;
            if (h < MinTileHeight || h > MaxTileHeight) continue;
            // Tiles are upright — skip nodes that are clearly wider than tall.
            if (w > h) continue;

            // Compose absolute (unit-local) position by walking the parent chain.
            AbsolutePosition(n, out float nx, out float ny, out float sx, out float sy);

            float screenX = addonX + nx * addonScale;
            float screenY = addonY + ny * addonScale;
            float screenW = w * sx * addonScale;
            float screenH = h * sy * addonScale;

            tiles.Add((ny, new Vector2(screenX, screenY), new Vector2(screenW, screenH)));
        }

        if (tiles.Count < 14) return null;

        // Group by Y and find the tightest row of 14. Sort ascending, slide a
        // 14-wide window; pick the window with the smallest Y span.
        tiles.Sort((a, b) => a.Y.CompareTo(b.Y));

        int bestStart = -1;
        float bestSpan = float.MaxValue;
        for (int i = 0; i + 14 <= tiles.Count; i++)
        {
            float span = tiles[i + 13].Y - tiles[i].Y;
            if (span < bestSpan)
            {
                bestSpan = span;
                bestStart = i;
            }
        }

        if (bestStart < 0 || bestSpan > MaxRowYSpread) return null;

        // Take those 14, sort left-to-right — that's hand slot 0..13.
        var selected = new List<(Vector2 Pos, Vector2 Size)>(14);
        for (int i = bestStart; i < bestStart + 14; i++)
            selected.Add((tiles[i].Pos, tiles[i].Size));
        selected.Sort((a, b) => a.Pos.X.CompareTo(b.Pos.X));
        return selected;
    }

    /// <summary>
    /// Walk <paramref name="node"/>'s parent chain to compute its position and
    /// accumulated scale relative to the addon root. Coordinates at each level
    /// are parent-relative, so child offset is added *after* the parent's scale
    /// has applied to it.
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
