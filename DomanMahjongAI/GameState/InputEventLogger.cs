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

    /// <summary>Label of the next FireCallback to record verbatim into the dedicated
    /// capture log, or null if not armed. Cleared after one capture or on timeout.
    /// Used to RE the click payload for actions whose opcodes we don't yet know
    /// (riichi, tsumo, ron, ankan, shouminkan).</summary>
    public string? PendingCaptureLabel { get; private set; }
    private DateTime captureArmedAt;

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
        PendingCaptureLabel = label;
        captureArmedAt = DateTime.UtcNow;
    }

    /// <summary>Cancel a pending capture without recording anything.</summary>
    public void DisarmCapture()
    {
        PendingCaptureLabel = null;
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

        // One-shot capture: dump the full FireCallback payload + addon AtkValues
        // context to a dedicated file under a human-supplied label, then disarm.
        // Used for opcode RE — the user runs `/mjauto capture riichi`, then clicks
        // the riichi button in-game, and we get a single labeled entry to inspect.
        if (PendingCaptureLabel is { } label
            && addon != null && addon->NameString == AddonName)
        {
            try
            {
                if ((DateTime.UtcNow - captureArmedAt).TotalSeconds > CaptureTimeoutSeconds)
                {
                    PendingCaptureLabel = null;
                }
                else
                {
                    WriteCaptureEntry(label, addon, valueCount, values, close, result);
                    PendingCaptureLabel = null;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"FireCallback capture error: {ex.Message}");
                PendingCaptureLabel = null;
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
    /// Append a single annotated capture entry. Records every FireCallback arg
    /// (so the click payload can be replayed) plus the addon's full AtkValues
    /// snapshot (so we know which prompt/state was active when the click fired).
    /// File is grep-friendly: each entry starts with a `# label=...` header.
    /// </summary>
    private unsafe void WriteCaptureEntry(
        string label, AtkUnitBase* addon, uint valueCount, AtkValue* values,
        byte close, bool result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $"# {DateTime.UtcNow:o}  label={label}  result={result}  close={(close != 0)}  " +
            $"valueCount={valueCount}");

        var snap = reader.TryBuildSnapshot();
        if (snap is not null && snap.Hand.Count > 0)
            sb.AppendLine($"hand={Engine.Tiles.Render(snap.Hand)}");

        sb.AppendLine($"fire_args (count={valueCount}):");
        if (values != null)
        {
            uint cap = valueCount > 32 ? 32 : valueCount;
            for (uint i = 0; i < cap; i++)
                sb.AppendLine($"  [{i,3}] {FormatValue(values[i])}");
            if (valueCount > cap) sb.AppendLine($"  ... +{valueCount - cap} more");
        }

        var atk = addon->AtkValues;
        int atkCount = addon->AtkValuesCount;
        sb.AppendLine($"addon_atkvalues (count={atkCount}):");
        if (atk != null)
        {
            int cap = Math.Min(atkCount, 64);
            for (int i = 0; i < cap; i++)
                sb.AppendLine($"  [{i,3}] {FormatValue(atk[i])}");
            if (atkCount > cap) sb.AppendLine($"  ... +{atkCount - cap} more");
        }

        sb.AppendLine();
        File.AppendAllText(capturePath, sb.ToString());
        Plugin.Log.Info(
            $"[capture] recorded label={label} (result={result}) → {capturePath}");
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
