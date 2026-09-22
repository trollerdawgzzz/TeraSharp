// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Network.Crypto;

/// <summary>
/// TERA connection crypto session: derives the encryptor/decryptor from the four
/// 128-byte keys exchanged during the handshake, then transforms traffic.
///
/// Ported from the proxy's encryption Session (tera-network-proxy). Key
/// derivation constants depend on patch version: builds &lt; 45 use the "old"
/// constants (31/17/79), builds &gt;= 45 use 67/29/41. TERA 100.02 is &gt;= 45,
/// so this defaults to the new constants.
///
/// HANDSHAKE SEQUENCE (server side), for reference when wiring the socket layer:
///   1. On accept, the SERVER immediately sends 128 random bytes  -> client key 1.
///   2. Client sends back 128 bytes                               -> client key 1
///      is actually assembled from client-supplied material; follow the exact
///      1725/2117 emulator sequence:
///        - server generates and sends c1 (128 bytes)
///        - client sends c2 (128 bytes)
///        - server generates and sends s1 (128 bytes)
///        - client sends s... etc.
///   The precise ordering is client/version specific; validate against a capture.
///   Once all four keys are set, call Init() and all subsequent traffic is
///   ciphered. Everything BEFORE Init() is plaintext on the wire.
///
/// This class only owns the key math + transform. The socket-level ordering
/// lives in the connection layer (next file to build), where we confirm it
/// against the client with a packet capture before trusting it.
/// </summary>
public sealed class TeraSession
{
    public int ProtocolVersion { get; }
    public int MajorPatchVersion { get; }

    private readonly bool _useOldConstants;
    private Cryptor? _encryptor;
    private Cryptor? _decryptor;

    // Two client keys and two server keys, each 128 bytes. Filled during handshake.
    public byte[][] ClientKeys { get; } = { new byte[128], new byte[128] };
    public byte[][] ServerKeys { get; } = { new byte[128], new byte[128] };

    public bool Ready => _encryptor != null && _decryptor != null;

    public TeraSession(int protocolVersion, int majorPatchVersion)
    {
        ProtocolVersion = protocolVersion;
        MajorPatchVersion = majorPatchVersion;
        _useOldConstants = majorPatchVersion < 45;
    }

    private static void ShiftKey(byte[] tgt, byte[] src, int n)
    {
        int len = src.Length;
        if (n > 0)
        {
            Array.Copy(src, n, tgt, 0, len - n);
            Array.Copy(src, 0, tgt, len - n, n);
        }
        else
        {
            int m = -n;
            Array.Copy(src, len - m, tgt, 0, m);
            Array.Copy(src, 0, tgt, m, len - m);
        }
    }

    private static void XorKey(byte[] tgt, byte[] key1, byte[] key2)
    {
        for (int i = 0; i < 128; i++)
            tgt[i] = (byte)(key1[i] ^ key2[i]);
    }

    /// <summary>
    /// Derive encryptor + decryptor from the four exchanged keys. Call once all
    /// of ClientKeys[0/1] and ServerKeys[0/1] are populated. Mirrors the proxy's
    /// Session.init() exactly.
    /// </summary>
    public void Init()
    {
        byte[] c1 = ClientKeys[0], c2 = ClientKeys[1];
        byte[] s1 = ServerKeys[0], s2 = ServerKeys[1];
        var t1 = new byte[128];
        var t2 = new byte[128];

        ShiftKey(t1, s1, _useOldConstants ? -31 : -67);
        XorKey(t2, t1, c1);
        ShiftKey(t1, c2, _useOldConstants ? 17 : 29);
        XorKey(t2, t1, t2);

        _decryptor = new Cryptor();
        _decryptor.GenerateKey(t2);

        ShiftKey(t1, s2, _useOldConstants ? -79 : -41);
        _decryptor.ApplyCryptor(t1, t1.Length);

        _encryptor = new Cryptor();
        _encryptor.GenerateKey(t1);
    }

    /// <summary>Decrypt bytes received FROM the client, in place.</summary>
    public void DecryptFromClient(byte[] data, int size)
    {
        if (_decryptor == null) throw new InvalidOperationException("session not initialised");
        _decryptor.ApplyCryptor(data, size);
    }

    /// <summary>Encrypt bytes being sent TO the client, in place.</summary>
    public void EncryptToClient(byte[] data, int size)
    {
        if (_encryptor == null) throw new InvalidOperationException("session not initialised");
        _encryptor.ApplyCryptor(data, size);
    }
}
