namespace TeraSharp.Arbiter.Network.Crypto;

/// <summary>
/// One of the three lagged-Fibonacci generator blocks that make up the TERA
/// stream cipher. Ported verbatim from the reference (TeraEmulator Crypt) - the
/// field layout and update rule must match exactly or the keystream diverges and
/// the client silently fails to connect.
///
/// The three blocks in a <see cref="Cryptor"/> use sizes 55/57/58 and lag
/// positions 31/50/39. Buffer is Size*4 uints (it holds the expanded key words;
/// only the first ~Size are stepped, matching the reference's allocation).
/// </summary>
public sealed class CryptorKey
{
    public int Size;
    public int Pos1;
    public int Pos2;
    public int MaxPos;
    public int Key;
    public uint[] Buffer;
    public uint Sum;

    public CryptorKey(int size, int maxPos)
    {
        Size = size;
        Pos2 = MaxPos = maxPos;
        Buffer = new uint[Size * 4];
    }
}
