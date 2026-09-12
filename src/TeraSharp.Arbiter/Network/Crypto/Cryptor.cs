using System.Buffers.Binary;

namespace TeraSharp.Arbiter.Network.Crypto;

/// <summary>
/// The TERA stream cipher. Direct port of the reference implementation
/// (TeraEmulator Crypt/Cryptor.cs), kept algorithmically identical - this is the
/// same cipher the client uses, and any deviation means a silent connect
/// failure. One <see cref="Cryptor"/> is the encryptor and another the
/// decryptor; see <see cref="TeraSession"/> for how the two are seeded from the
/// exchanged keys.
///
/// The cipher is three lagged-Fibonacci blocks (55/57/58) combined by majority
/// vote each step. <see cref="ApplyCryptor"/> XORs a keystream over the buffer
/// in place and carries partial-word state in ChangeData/ChangeLen so that
/// packets whose length isn't a multiple of 4 chain correctly across calls.
/// </summary>
public sealed class Cryptor
{
    private int _changeData;
    private int _changeLen;

    private readonly CryptorKey[] _key =
    {
        new CryptorKey(55, 31),
        new CryptorKey(57, 50),
        new CryptorKey(58, 39),
    };

    private static byte[] FillKey(byte[] src)
    {
        var result = new byte[680];
        for (int i = 0; i < 680; i++)
            result[i] = src[i % 128];
        result[0] = 128;
        return result;
    }

    /// <summary>
    /// Expand a 128-byte key into the three block buffers via the rolling SHA-0
    /// construction, exactly as the reference does. Note: the reference reads the
    /// SHA state back as little-endian uints, and its Sha.Digest returns uint[5]
    /// in host order; our Sha0 returns a big-endian 20-byte digest, so we convert
    /// each 4-byte group from big-endian to a uint and write it little-endian -
    /// producing the identical buffer bytes.
    /// </summary>
    public void GenerateKey(byte[] src)
    {
        byte[] buf = FillKey(src);

        for (int i = 0; i < 680; i += 20)
        {
            var sha = new Sha0();
            sha.Update(buf);
            byte[] hash = sha.Hash(); // 20 bytes, big-endian words

            for (int j = 0; j < 5; j++)
            {
                // Reference: Buffer.BlockCopy(BitConverter.GetBytes(sha[j]), 0, buf, i+j*4, 4)
                // where sha[j] is the j-th 32-bit word of the digest (big-endian
                // in our Hash()), written little-endian into buf.
                uint word = (uint)((hash[j * 4] << 24) | (hash[j * 4 + 1] << 16)
                                 | (hash[j * 4 + 2] << 8) | hash[j * 4 + 3]);
                buf[i + j * 4 + 0] = (byte)word;
                buf[i + j * 4 + 1] = (byte)(word >> 8);
                buf[i + j * 4 + 2] = (byte)(word >> 16);
                buf[i + j * 4 + 3] = (byte)(word >> 24);
            }
        }

        for (int i = 0; i < 220; i += 4)
            _key[0].Buffer[i / 4] = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i));

        for (int i = 0; i < 228; i += 4)
            _key[1].Buffer[i / 4] = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(220 + i));

        for (int i = 0; i < 232; i += 4)
            _key[2].Buffer[i / 4] = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(448 + i));
    }

    /// <summary>XOR the keystream over <paramref name="buf"/> in place.</summary>
    public void ApplyCryptor(byte[] buf, int size)
    {
        int pre = (size < _changeLen) ? size : _changeLen;
        if (pre != 0)
        {
            for (int j = 0; j < pre; j++)
                buf[j] ^= (byte)(_changeData >> (8 * (4 - _changeLen + j)));

            _changeLen -= pre;
            size -= pre;
        }

        for (int i = pre; i < buf.Length - 3; i += 4)
        {
            int result = _key[0].Key & _key[1].Key | _key[2].Key & (_key[0].Key | _key[1].Key);

            for (int j = 0; j < 3; j++)
            {
                CryptorKey k = _key[j];
                if (result == k.Key)
                {
                    uint t1 = k.Buffer[k.Pos1];
                    uint t2 = k.Buffer[k.Pos2];
                    uint t3 = (t1 <= t2) ? t1 : t2;
                    k.Sum = t1 + t2;
                    k.Key = (t3 > k.Sum) ? 1 : 0;
                    k.Pos1 = (k.Pos1 + 1) % k.Size;
                    k.Pos2 = (k.Pos2 + 1) % k.Size;
                }
                buf[i] ^= (byte)k.Sum;
                buf[i + 1] ^= (byte)(k.Sum >> 8);
                buf[i + 2] ^= (byte)(k.Sum >> 16);
                buf[i + 3] ^= (byte)(k.Sum >> 24);
            }
        }

        int remain = size & 3;
        if (remain != 0)
        {
            int result = _key[0].Key & _key[1].Key | _key[2].Key & (_key[0].Key | _key[1].Key);
            _changeData = 0;
            for (int j = 0; j < 3; j++)
            {
                CryptorKey k = _key[j];
                if (result == k.Key)
                {
                    uint t1 = k.Buffer[k.Pos1];
                    uint t2 = k.Buffer[k.Pos2];
                    uint t3 = (t1 <= t2) ? t1 : t2;
                    k.Sum = t1 + t2;
                    k.Key = (t3 > k.Sum) ? 1 : 0;
                    k.Pos1 = (k.Pos1 + 1) % k.Size;
                    k.Pos2 = (k.Pos2 + 1) % k.Size;
                }
                _changeData ^= (int)k.Sum;
            }

            for (int j = 0; j < remain; j++)
                buf[size + pre - remain + j] ^= (byte)(_changeData >> (j * 8));

            _changeLen = 4 - remain;
        }
    }
}
