namespace TeraSharp.Arbiter.Network.Crypto;

/// <summary>
/// SHA-0 (the original, withdrawn 1993 algorithm - NOT SHA-1).
///
/// TERA's key-expansion uses SHA-0, which differs from SHA-1 in exactly one line:
/// the message-schedule extension does NOT left-rotate by 1. .NET's built-in
/// SHA1 therefore produces the wrong result and cannot be used. This is a direct
/// port of the reference used by the TERA proxy/emulators
/// (TeraEmulator .../Crypt/Sha.cs and tera-network-proxy sha0.js), kept
/// byte-exact - the key expansion, and thus the whole cipher, depends on it.
///
/// Operates big-endian internally, matching the reference.
/// </summary>
public sealed class Sha0
{
    private readonly uint[] _digest =
    {
        0x67452301u, 0xEFCDAB89u, 0x98BADCFEu, 0x10325476u, 0xC3D2E1F0u
    };
    private readonly byte[] _block = new byte[64];
    private int _blockIndex;
    private uint _lengthHigh;
    private uint _lengthLow;
    private bool _computed;

    private static uint LeftRotate(uint x, int n) => (x << n) | (x >> (32 - n));

    public void Update(ReadOnlySpan<byte> buffer)
    {
        foreach (byte b in buffer)
        {
            _block[_blockIndex++] = b;
            _lengthLow += 8;
            if (_lengthLow == 0) _lengthHigh++;
            if (_blockIndex == 64) ProcessBlock();
        }
    }

    private void ProcessBlock()
    {
        Span<uint> w = stackalloc uint[80];

        for (int t = 0; t < 16; t++)
            w[t] = (uint)((_block[t * 4] << 24) | (_block[t * 4 + 1] << 16)
                        | (_block[t * 4 + 2] << 8) | _block[t * 4 + 3]);

        // SHA-0: NO left-rotate here (SHA-1 would rotate this left by 1).
        for (int t = 16; t < 80; t++)
            w[t] = w[t - 3] ^ w[t - 8] ^ w[t - 14] ^ w[t - 16];

        uint a = _digest[0], b = _digest[1], c = _digest[2], d = _digest[3], e = _digest[4];

        for (int t = 0; t < 80; t++)
        {
            uint temp = LeftRotate(a, 5) + e + w[t];
            if (t < 20) temp += ((b & c) | (~b & d)) + 0x5A827999u;
            else if (t < 40) temp += (b ^ c ^ d) + 0x6ED9EBA1u;
            else if (t < 60) temp += ((b & c) | (b & d) | (c & d)) + 0x8F1BBCDCu;
            else temp += (b ^ c ^ d) + 0xCA62C1D6u;

            e = d;
            d = c;
            c = LeftRotate(b, 30);
            b = a;
            a = temp;
        }

        _digest[0] += a;
        _digest[1] += b;
        _digest[2] += c;
        _digest[3] += d;
        _digest[4] += e;
        _blockIndex = 0;
    }

    private void PadMessage()
    {
        _block[_blockIndex++] = 0x80;

        if (_blockIndex > 56)
        {
            while (_blockIndex < 64) _block[_blockIndex++] = 0;
            ProcessBlock();
        }

        while (_blockIndex < 56) _block[_blockIndex++] = 0;

        _block[56] = (byte)(_lengthHigh >> 24);
        _block[57] = (byte)(_lengthHigh >> 16);
        _block[58] = (byte)(_lengthHigh >> 8);
        _block[59] = (byte)_lengthHigh;
        _block[60] = (byte)(_lengthLow >> 24);
        _block[61] = (byte)(_lengthLow >> 16);
        _block[62] = (byte)(_lengthLow >> 8);
        _block[63] = (byte)_lengthLow;
        ProcessBlock();
    }

    /// <summary>Finalise and return the 20-byte big-endian digest.</summary>
    public byte[] Hash()
    {
        if (!_computed)
        {
            PadMessage();
            _computed = true;
        }

        var outBytes = new byte[20];
        for (int t = 0; t < 5; t++)
        {
            outBytes[t * 4] = (byte)(_digest[t] >> 24);
            outBytes[t * 4 + 1] = (byte)(_digest[t] >> 16);
            outBytes[t * 4 + 2] = (byte)(_digest[t] >> 8);
            outBytes[t * 4 + 3] = (byte)_digest[t];
        }
        return outBytes;
    }
}
