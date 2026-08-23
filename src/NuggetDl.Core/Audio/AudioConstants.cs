using NuggetDl.Core.Sources;

namespace NuggetDl.Core.Audio;

/// <summary>
/// Fixed HCA decryption key used for every ACB/AWB/USM audio stream,
/// independent of the asset bundle decrypt key.
/// </summary>
public static class AudioConstants
{
    public static long AudioKey => ClientValues.AudioKey;
}
