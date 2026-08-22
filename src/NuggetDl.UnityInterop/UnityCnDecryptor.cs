using System.Security.Cryptography;

namespace NuggetDl.UnityInterop;

/// <summary>
/// Decrypts UnityCN-encrypted asset bundles.
/// </summary>
public sealed class UnityCnDecryptor
{
    private static readonly byte[] SignatureBytes = "#$unity3dchina!@"u8.ToArray(); // 16 bytes

    private readonly byte[] _indexTable; // 16 nibbles, values 0-15
    private readonly byte[] _substitute; // 16 nibbles, values 0-15

    private UnityCnDecryptor(byte[] indexTable, byte[] substitute)
    {
        _indexTable = indexTable;
        _substitute = substitute;
    }

    /// <summary>
    /// Reads the decryptor header from <paramref name="reader"/> (must be
    /// positioned right after the bundle's dataflags field) and validates it
    /// against <paramref name="decryptKeyBytes"/> (exactly 16 bytes).
    /// Throws if the key is wrong (signature mismatch) - this is the only
    /// validation that the decrypt key is correct; there's no separate check.
    /// </summary>
    public static UnityCnDecryptor Read(AssetsTools.NET.AssetsFileReader reader, byte[] decryptKeyBytes)
    {
        if (decryptKeyBytes.Length != 16)
        {
            throw new ArgumentException("UnityCN decrypt key must be exactly 16 bytes", nameof(decryptKeyBytes));
        }

        _ = reader.ReadUInt32(); // unknown_1, unused

        var (data, key) = ReadVector(reader);
        var (dataSig, keySig) = ReadVector(reader);

        var signature = DecryptKey(keySig, dataSig, decryptKeyBytes);
        if (!signature.AsSpan().SequenceEqual(SignatureBytes))
        {
            throw new InvalidOperationException(
                "UnityCN signature check failed - the decrypt key is very likely wrong for this build.");
        }

        var expanded = DecryptKey(key, data, decryptKeyBytes);

        // Flatten each byte into (high nibble, low nibble) - 32 nibbles total.
        var nibbles = new byte[32];
        for (var i = 0; i < 16; i++)
        {
            nibbles[2 * i] = (byte)(expanded[i] >> 4);
            nibbles[2 * i + 1] = (byte)(expanded[i] & 0xF);
        }

        var indexTable = nibbles[..16];

        // Mirrors Python's `bytes(data[0x10 + i*4+j] for j in range(4) for i in range(4))`
        // exactly - outer loop j, inner loop i, so don't "simplify" the loop order.
        var substitute = new byte[16];
        var k = 0;
        for (var j = 0; j < 4; j++)
        {
            for (var i = 0; i < 4; i++)
            {
                substitute[k++] = nibbles[0x10 + (i * 4) + j];
            }
        }

        return new UnityCnDecryptor(indexTable, substitute);
    }

    /// <summary>
    /// Decrypts one block's compressed bytes in place, given the running
    /// block index (0 for the blocksInfo block if encrypted, then 0, 1, 2...
    /// for each data block in order).
    /// </summary>
    public void DecryptBlock(byte[] data, int blockIndex)
    {
        var offset = 0;
        var index = blockIndex;
        var span = data.AsSpan();

        while (offset < data.Length)
        {
            offset += Decrypt(span[offset..], index, data.Length - offset);
            index += 1;
        }
    }

    private static (byte[] Data, byte[] Key) ReadVector(AssetsTools.NET.AssetsFileReader reader)
    {
        var data = reader.ReadBytes(0x10);
        var key = reader.ReadBytes(0x10);
        reader.Position += 1;
        return (data, key);
    }

    private static byte[] DecryptKey(byte[] key, byte[] data, byte[] keyBytes)
    {
        var keystream = AesEcbEncryptBlock(keyBytes, key);
        var result = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            result[i] = (byte)(data[i] ^ keystream[i]);
        }

        return result;
    }

    private static byte[] AesEcbEncryptBlock(byte[] keyBytes, byte[] plaintextBlock)
    {
        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        var result = new byte[16];
        encryptor.TransformBlock(plaintextBlock, 0, 16, result, 0);
        return result;
    }

    // index parameter here is the WITHIN-CALL running counter, distinct from
    // (and reset from) the OUTER per-token index in DecryptBlock's loop -
    // this asymmetry is deliberate and matches Python exactly: DecryptBlock
    // only ever increments its own index by 1 per token, regardless of how
    // many DecryptByte calls happened inside a single Decrypt() call.
    private int Decrypt(Span<byte> data, int index, int remaining)
    {
        var offset = 0;

        var curByte = DecryptByte(data, ref offset, ref index);
        var byteHigh = curByte >> 4;
        var byteLow = curByte & 0xF;

        if (byteHigh == 0xF)
        {
            int b;
            do
            {
                b = DecryptByte(data, ref offset, ref index);
                byteHigh += b;
            }
            while (b == 0xFF);
        }

        offset += byteHigh;

        if (offset < remaining)
        {
            DecryptByte(data, ref offset, ref index);
            DecryptByte(data, ref offset, ref index);

            if (byteLow == 0xF)
            {
                int b;
                do
                {
                    b = DecryptByte(data, ref offset, ref index);
                }
                while (b == 0xFF);
            }
        }

        return offset;
    }

    private int DecryptByte(Span<byte> data, ref int offset, ref int index)
    {
        var b = _substitute[((index >> 2) & 3) + 4]
            + _substitute[index & 3]
            + _substitute[((index >> 4) & 3) + 8]
            + _substitute[((index % 256) >> 6) + 12];

        var low = data[offset] & 0xF;
        var high = data[offset] >> 4;

        var term1 = (_indexTable[low] - b) & 0xF;
        var term2 = 0x10 * (_indexTable[high] - b);
        var combined = term1 | term2;

        // C#'s % can return negative for a negative dividend; Python's can't.
        // Floor-mod explicitly to match Python's `% 256` semantics exactly.
        var result = ((combined % 256) + 256) % 256;

        data[offset] = (byte)result;
        offset += 1;
        index += 1;
        return result;
    }
}
