namespace NuggetDl.Core.Sources;

/// <summary>
/// Base class for all errors raised by the sources layer.
/// Callers can catch this to handle any source failure, or a specific
/// subclass to distinguish "this bundle/blob doesn't exist" from
/// "it exists but couldn't be fetched/parsed".
/// </summary>
public abstract class SourceException : Exception
{
    protected SourceException(string message) : base(message)
    {
    }

    protected SourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A bundle or blob could not be resolved in any configured source.
/// </summary>
public sealed class BlobNotFoundException : SourceException
{
    public BlobNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>
/// A blob was located but could not be downloaded (e.g. non-200 response).
/// </summary>
public sealed class BlobDownloadException : SourceException
{
    public BlobDownloadException(string message) : base(message)
    {
    }

    public BlobDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A source's index/config bundle was missing or could not be parsed.
/// </summary>
public sealed class SourceIndexException : SourceException
{
    public SourceIndexException(string message) : base(message)
    {
    }
}

/// <summary>
/// An unknown primary/patch source type was requested.
/// </summary>
public sealed class UnknownSourceException : SourceException
{
    public UnknownSourceException(string message) : base(message)
    {
    }
}

/// <summary>
/// A patch source was requested with no version given, and none could be
/// inferred from sources already added to the set.
/// </summary>
public sealed class PatchVersionRequiredException : SourceException
{
    public PatchVersionRequiredException()
        : base("Patch version required, and could not be inferred from earlier sources")
    {
    }
}

/// <summary>
/// No version was given explicitly and none could be inferred from the
/// primary source.
/// </summary>
public sealed class VersionNotDeterminableException : SourceException
{
    public VersionNotDeterminableException(string primary)
        : base($"Could not determine the version from {primary}. Pass a version explicitly.")
    {
    }
}

/// <summary>
/// No asset-bundle decryption key override was given and none could be
/// derived from the resolved game version.
/// </summary>
public sealed class DecryptionKeyNotDeterminableException : SourceException
{
    public DecryptionKeyNotDeterminableException()
        : base("No decryption key was able to be determined. Specify one explicitly.")
    {
    }
}
