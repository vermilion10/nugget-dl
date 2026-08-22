namespace NuggetDl.Core.BinaryTable;

/// <summary>A binary table could not be parsed (malformed, truncated, or unsupported).</summary>
public sealed class BinaryTableException : Exception
{
    public BinaryTableException(string message) : base(message)
    {
    }

    public BinaryTableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
