using System.Text.Json;
using System.Text.Json.Serialization;

namespace BingLan.Core.Desktop;

public enum CleanDesktopPhase
{
    Prepared,
    Applied,
    Restoring
}

/// <summary>
/// What clean desktop changed on the Windows desktop view, written before the change so
/// a later start (or an uninstaller) can put the icons back after a crash. Windows keeps
/// the hidden state across Explorer restarts and sign-ins, so the checkpoint is the only
/// record of what the user had. It never leaves this machine and is never exported.
/// </summary>
public sealed class CleanDesktopCheckpoint
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public CleanDesktopPhase Phase { get; set; } = CleanDesktopPhase.Prepared;
    public bool OriginalIconsHidden { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public static CleanDesktopCheckpoint? Parse(string json)
    {
        try
        {
            // Every field must be present: a partial record cannot say what the user
            // had, and restoring from it would be a guess.
            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty(nameof(SchemaVersion), out _)
                    || !root.TryGetProperty(nameof(Phase), out _)
                    || !root.TryGetProperty(nameof(OriginalIconsHidden), out _)
                    || !root.TryGetProperty(nameof(CreatedAtUtc), out _))
                {
                    return null;
                }
            }

            var checkpoint = JsonSerializer.Deserialize<CleanDesktopCheckpoint>(json, JsonOptions);
            return checkpoint is { SchemaVersion: >= 1 and <= CurrentSchemaVersion }
                && Enum.IsDefined(checkpoint.Phase)
                    ? checkpoint
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Rules for the one desktop view flag clean desktop owns. Writes go through the
/// FWF_NOICONS mask only, so every other folder flag stays as Windows or the user set it.
/// </summary>
public static class CleanDesktopRules
{
    public const uint NoIconsFlag = 0x00001000;

    public static bool AreIconsHidden(uint folderFlags) => (folderFlags & NoIconsFlag) != 0;

    public static uint Apply(uint folderFlags, bool hidden) => hidden
        ? folderFlags | NoIconsFlag
        : folderFlags & ~NoIconsFlag;

    /// <summary>
    /// Whether hiding needs a write and a checkpoint. Icons the user already hid are
    /// left alone, so there is nothing of ours to restore later.
    /// </summary>
    public static bool NeedsHide(uint currentFlags) => !AreIconsHidden(currentFlags);

    /// <summary>Whether restoring needs a write to reach the recorded original state.</summary>
    public static bool NeedsRestore(CleanDesktopCheckpoint checkpoint, uint currentFlags)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return AreIconsHidden(currentFlags) != checkpoint.OriginalIconsHidden;
    }

    /// <summary>
    /// Icons showing again while clean desktop holds them hidden means someone turned
    /// them back on (the desktop menu or another tool); the mode then steps aside.
    /// </summary>
    public static bool IsTakenOver(bool holdingHidden, uint currentFlags) =>
        holdingHidden && !AreIconsHidden(currentFlags);

    /// <summary>
    /// Whether an explicit turn-off has to show the icons even though there is no
    /// usable checkpoint: while the toggle was on, the mode held the hidden state, so
    /// a lost record must not end in a reported success with the desktop still hidden.
    /// </summary>
    public static bool TurnOffMustShowIcons(bool hasCheckpoint, uint currentFlags) =>
        !hasCheckpoint && AreIconsHidden(currentFlags);
}
