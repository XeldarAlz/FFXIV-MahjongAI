using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using DomanMahjongAI.Engine;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DomanMahjongAI.GameState;

/// <summary>
/// Records every <c>PostReceiveEvent</c> the Emj addon sees into a rolling log file.
/// Used to reverse-engineer the click-dispatch API: once a human plays a move
/// (discard tile, pon, pass, riichi, etc.), the log captures the addon's callback
/// arguments so we can replay them programmatically in M6.
///
/// Output: <c>%AppData%\XIVLauncher\pluginConfigs\DomanMahjongAI\emj-events.log</c>.
/// Each line: <c>UTC  event=X  param=Y  args=[...]  hand=...</c>.
///
/// Enable/disable via <see cref="Enabled"/>. Off by default so we don't spam the log
/// during normal play; flip it on when doing RE sessions.
/// </summary>
public sealed class InputEventLogger : IDisposable
{
    public const string AddonName = AddonEmjReader.AddonName;

    // AtkUnitBase::FireCallback — signature from FFXIVClientStructs:
    //   bool FireCallback(uint valueCount, AtkValue* values, bool close)
    // Sig covers the callsite; Dalamud's HookFromSignature follows the E8 to the real function.
    private const string FireCallbackSig = "E8 ?? ?? ?? ?? 0F B6 E8 8B 44 24 20";
    private unsafe delegate bool FireCallbackDelegate(AtkUnitBase* addon, uint valueCount, AtkValue* values, byte close);

    private const double CaptureTimeoutSeconds = 60.0;

    private readonly AddonEmjReader reader;
    private readonly MeldTracker meldTracker;
    private readonly string logPath;
    private readonly string capturePath;
    private StreamWriter? writer;
    private bool disposed;
    private unsafe Hook<FireCallbackDelegate>? fireCallbackHook;

    /// <summary>Backing field for <see cref="PendingCaptureLabel"/>. Use the public
    /// property — its getter expires stale labels lazily so the user-facing status
    /// matches the actual capture behavior.</summary>
    private string? pendingCaptureLabel;
    private DateTime captureArmedAt;

    /// <summary>Label of the next FireCallback to record verbatim into the dedicated
    /// capture log, or null if not armed / expired. Cleared after one capture, on
    /// timeout (lazy — the getter clears stale labels on access), or via
    /// <see cref="DisarmCapture"/>. Used to RE the click payload for actions whose
    /// opcodes we don't yet know (riichi, tsumo, ron, ankan, shouminkan).</summary>
    public string? PendingCaptureLabel
    {
        get
        {
            if (pendingCaptureLabel is not null
                && (DateTime.UtcNow - captureArmedAt).TotalSeconds > CaptureTimeoutSeconds)
            {
                pendingCaptureLabel = null;
            }
            return pendingCaptureLabel;
        }
    }

    public bool Enabled { get; set; }

    public string CaptureLogPath => capturePath;

    public unsafe InputEventLogger(AddonEmjReader reader, MeldTracker meldTracker)
    {
        this.reader = reader;
        this.meldTracker = meldTracker;
        var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(dir);
        logPath = Path.Combine(dir, "emj-events.log");
        capturePath = Path.Combine(dir, "emj-captures.log");

        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, AddonName, OnReceiveEvent);

        // Install a global FireCallback hook; we filter by addon name inside the detour.
        try
        {
            fireCallbackHook = Plugin.GameInterop.HookFromSignature<FireCallbackDelegate>(
                FireCallbackSig, FireCallbackDetour);
            fireCallbackHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"InputEventLogger: failed to hook FireCallback: {ex}");
            fireCallbackHook = null;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Plugin.AddonLifecycle.UnregisterListener(OnReceiveEvent);
        fireCallbackHook?.Disable();
        fireCallbackHook?.Dispose();
        fireCallbackHook = null;
        writer?.Flush();
        writer?.Dispose();
    }

    public string LogPath => logPath;

    /// <summary>
    /// Arm a one-shot capture: the next FireCallback fired against the Emj addon will
    /// be appended verbatim to <c>emj-captures.log</c> under <paramref name="label"/>.
    /// Auto-clears after one capture or after <see cref="CaptureTimeoutSeconds"/>
    /// seconds with no click — so a stray UI interaction days later won't be tagged.
    /// Re-arming overwrites any pending label.
    /// </summary>
    public void ArmCapture(string label)
    {
        pendingCaptureLabel = label;
        captureArmedAt = DateTime.UtcNow;
    }

    /// <summary>Cancel a pending capture without recording anything.</summary>
    public void DisarmCapture()
    {
        pendingCaptureLabel = null;
    }

    public void OpenLog()
    {
        writer ??= new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
    }

    public void CloseLog()
    {
        writer?.Flush();
        writer?.Dispose();
        writer = null;
    }

    private unsafe bool FireCallbackDetour(AtkUnitBase* addon, uint valueCount, AtkValue* values, byte close)
    {
        // Determine meld-accept intent BEFORE the game processes the click so the Legal
        // snapshot still reflects the offered candidates. opcode 11 + option 0 = accept
        // leftmost button on a call prompt (pon / chi / min-kan). For multi-variant chi
        // we'd want the specific variant picked but the game only ever takes option 0
        // from us today (matches what DispatchCall() sends).
        MeldCandidate? acceptedMeld = null;
        if (addon != null && addon->NameString == AddonName
            && valueCount == 2
            && values[0].Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int
            && values[0].Int == 11
            && values[1].Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int
            && values[1].Int == 0)
        {
            var preSnap = reader.TryBuildSnapshot();
            if (preSnap is not null)
            {
                if (preSnap.Legal.PonCandidates.Count > 0)
                    acceptedMeld = preSnap.Legal.PonCandidates[0];
                else if (preSnap.Legal.ChiCandidates.Count > 0)
                    acceptedMeld = preSnap.Legal.ChiCandidates[0];
                else if (preSnap.Legal.KanCandidates.Count > 0)
                    acceptedMeld = preSnap.Legal.KanCandidates[0];
            }
        }

        // Capture snapshot — must run BEFORE the original FireCallback. The original may
        // mutate addon state (close a modal, refresh AtkValues), so reading post-call
        // would record post-click state instead of the at-click context we want for RE.
        // Both the addon AtkValues and the fire_args are formatted into managed strings
        // here so the captured payload stays valid even if the caller's buffers move.
        string? captureLabel = null;
        string? captureHand = null;
        string[]? captureFireArgs = null;
        string[]? captureAtkValues = null;
        int captureAtkCount = 0;
        if (PendingCaptureLabel is { } pending
            && addon != null && addon->NameString == AddonName)
        {
            captureLabel = pending;
            captureFireArgs = SnapshotValues(values, (int)valueCount, max: 32);
            captureAtkCount = addon->AtkValuesCount;
            captureAtkValues = SnapshotValues(addon->AtkValues, captureAtkCount, max: 64);
            var preSnap = reader.TryBuildSnapshot();
            if (preSnap is not null && preSnap.Hand.Count > 0)
                captureHand = Engine.Tiles.Render(preSnap.Hand);
        }

        // Always call the original FIRST so game logic is unaffected regardless of logger state.
        bool result = fireCallbackHook!.Original(addon, valueCount, values, close);

        // Record the meld only if the game accepted the click. Covers both plugin
        // auto-accepts and manual in-game clicks — the tracker needs both.
        if (acceptedMeld is { } meld && result)
        {
            try { meldTracker.Record(Engine.Meld.FromAcceptedCandidate(meld)); }
            catch (Exception ex) { Plugin.Log.Error($"MeldTracker record error: {ex.Message}"); }
        }

        try
        {
            if (Enabled && addon != null && addon->NameString == AddonName)
            {
                OpenLog();
                var sb = new StringBuilder();
                sb.Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                sb.Append($"  evt=FireCallback  count={valueCount}  close={(close != 0)}  result={result}");

                var snap = reader.TryBuildSnapshot();
                if (snap is not null && snap.Hand.Count > 0)
                {
                    sb.Append("  hand=");
                    sb.Append(Engine.Tiles.Render(snap.Hand));
                }

                if (values != null && valueCount > 0)
                {
                    sb.Append("  values=[");
                    uint cap = valueCount > 16 ? 16 : valueCount;
                    for (uint i = 0; i < cap; i++)
                    {
                        var v = values[i];
                        sb.Append($"{i}:{v.Type}=");
                        switch (v.Type)
                        {
                            case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int:
                                sb.Append(v.Int); break;
                            case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt:
                                sb.Append(v.UInt); break;
                            case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool:
                                sb.Append(v.Byte != 0); break;
                            default:
                                sb.Append($"raw=0x{v.UInt:X}"); break;
                        }
                        if (i < cap - 1) sb.Append(',');
                    }
                    if (valueCount > cap) sb.Append($"...+{valueCount - cap}");
                    sb.Append(']');
                }

                writer?.WriteLine(sb.ToString());
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"FireCallback log error: {ex.Message}");
        }

        // One-shot capture: write out the pre-call snapshot (taken above) plus the
        // now-known result, then disarm. Used for opcode RE — the user runs
        // `/mjauto capture riichi`, clicks the riichi button, and gets a labeled entry.
        if (captureLabel is not null)
        {
            try
            {
                WriteCaptureEntry(
                    captureLabel, captureHand, captureFireArgs!, valueCount,
                    captureAtkValues!, captureAtkCount, close, result);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"FireCallback capture error: {ex.Message}");
            }
            finally
            {
                pendingCaptureLabel = null;
            }
        }

        return result;
    }

    private unsafe void OnReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (!Enabled) return;
        OpenLog();

        var addr = args.Addon.Address;
        if (addr == 0) return;

        var sb = new StringBuilder();
        sb.Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        sb.Append("  evt=PostReceiveEvent");

        if (args is AddonReceiveEventArgs rea)
        {
            sb.Append($"  type={rea.AtkEventType}  param={rea.EventParam}");
        }

        // Snapshot hand so we can correlate a click with the hand shape at click time.
        var snap = reader.TryBuildSnapshot();
        if (snap is not null && snap.Hand.Count > 0)
        {
            sb.Append("  hand=");
            sb.Append(Engine.Tiles.Render(snap.Hand));
        }

        // Dump the first few AtkValues — some addons push context through here.
        var unit = (AtkUnitBase*)addr;
        int valueCount = Math.Min((int)unit->AtkValuesCount, 8);
        if (valueCount > 0)
        {
            sb.Append("  atk=[");
            for (int i = 0; i < valueCount; i++)
            {
                var v = unit->AtkValues[i];
                sb.Append($"{i}:{v.Type}=");
                switch (v.Type)
                {
                    case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int:
                        sb.Append(v.Int); break;
                    case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt:
                        sb.Append(v.UInt); break;
                    case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool:
                        sb.Append(v.Byte != 0); break;
                    default:
                        sb.Append("?"); break;
                }
                if (i < valueCount - 1) sb.Append(',');
            }
            sb.Append(']');
        }

        writer?.WriteLine(sb.ToString());
    }

    /// <summary>
    /// Append a single annotated capture entry from a pre-call snapshot. The lines for
    /// fire_args and addon AtkValues are formatted by <see cref="SnapshotValues"/>
    /// before the original FireCallback runs, so they reflect the at-click state even
    /// if the original mutates the addon. File is grep-friendly: each entry starts
    /// with a <c># label=...</c> header.
    /// </summary>
    private void WriteCaptureEntry(
        string label, string? hand, string[] fireArgs, uint fireArgCount,
        string[] atkValues, int atkCount, byte close, bool result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $"# {DateTime.UtcNow:o}  label={label}  result={result}  close={(close != 0)}  " +
            $"valueCount={fireArgCount}");

        if (hand is not null)
            sb.AppendLine($"hand={hand}");

        sb.AppendLine($"fire_args (count={fireArgCount}):");
        for (int i = 0; i < fireArgs.Length; i++)
            sb.AppendLine($"  [{i,3}] {fireArgs[i]}");
        if (fireArgCount > fireArgs.Length)
            sb.AppendLine($"  ... +{fireArgCount - fireArgs.Length} more");

        sb.AppendLine($"addon_atkvalues (count={atkCount}):");
        for (int i = 0; i < atkValues.Length; i++)
            sb.AppendLine($"  [{i,3}] {atkValues[i]}");
        if (atkCount > atkValues.Length)
            sb.AppendLine($"  ... +{atkCount - atkValues.Length} more");

        sb.AppendLine();
        File.AppendAllText(capturePath, sb.ToString());
        Plugin.Log.Info(
            $"[capture] recorded label={label} (result={result}) → {capturePath}");
    }

    /// <summary>
    /// Format up to <paramref name="max"/> AtkValues into managed strings while we
    /// still have valid pointers. Strings are decoded eagerly so the captured payload
    /// stays correct after the original FireCallback returns and the source memory may
    /// have been reused. Returns an empty array if <paramref name="values"/> is null.
    /// </summary>
    private static unsafe string[] SnapshotValues(AtkValue* values, int count, int max)
    {
        if (values == null || count <= 0) return Array.Empty<string>();
        int n = Math.Min(count, max);
        var result = new string[n];
        for (int i = 0; i < n; i++)
            result[i] = FormatValue(values[i]);
        return result;
    }

    private static unsafe string FormatValue(AtkValue v)
    {
        switch (v.Type)
        {
            case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int:
                return $"{v.Type,-14} Int={v.Int}";
            case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt:
                return $"{v.Type,-14} UInt={v.UInt} (0x{v.UInt:X})";
            case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool:
                return $"{v.Type,-14} Bool={v.Byte != 0}";
            case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String:
            case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String8:
            case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString:
                var s = v.String.Value != null
                    ? System.Text.Encoding.UTF8.GetString(v.String)
                    : "(null)";
                return $"{v.Type,-14} String=\"{s}\"";
            default:
                return $"{v.Type,-14} raw=0x{v.UInt:X}";
        }
    }
}
