// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Net.Sockets;
using System.Security.Cryptography;
using TeraSharp.Arbiter.Network.Crypto;
using TeraSharp.Arbiter.Protocol;

namespace TeraSharp.TestClient;

/// <summary>
/// Standalone login-chain test client. Drives the full sequence a real client
/// would after connecting, using the server's own crypto + codec classes:
///
///   handshake (4-key) -> C_CHECK_VERSION  -> expect S_CHECK_VERSION
///                     -> C_LOGIN_ARBITER   -> expect S_LOGIN_ARBITER (+ S_LOGIN_ACCOUNT_INFO)
///                     -> C_GET_USER_LIST   -> expect S_GET_USER_LIST
///                     -> C_SELECT_USER     -> expect S_LOGIN
///
/// A clean run proves every packet in the login chain encodes and decodes
/// self-consistently (framing, opcodes, codec, the offset/array machinery for
/// the big S_GET_USER_LIST / S_LOGIN packets). It does NOT prove a real client
/// accepts them - only the real-client test does that - but it clears every
/// asymmetry/framing/codec bug in these packets cheaply.
///
/// Crypto direction note (same as before): the client uses the shared
/// TeraSession derived from the same four keys, and because the cipher is
/// symmetric it calls DecryptFromClient() to ENCRYPT outgoing and
/// EncryptToClient() to DECRYPT incoming (the method names are from the server's
/// point of view).
/// </summary>
public static class Program
{
    private const int ProtocolVersion = 376012;
    private const int MajorPatchVersion = 100;
    private const string Host = "127.0.0.1";
    private const int Port = 7701;

    private const string DefinitionsPath = @"D:\v100\TERA_SERVER.100\tera_v100_MASTER_FINAL";
    private const string DataJsonPath = @"D:\v100\TERA_SERVER.100\tera-server-proxy\data\data.json";

    private static OpcodeTable _opcodes = null!;
    private static DefinitionRegistry _defs = null!;

    public static async Task Main()
    {
        _opcodes = OpcodeTable.LoadFromFile(DataJsonPath, "376012");
        _defs = DefinitionRegistry.LoadFromFolder(DefinitionsPath, new NullLogger());

        Console.WriteLine($"[test-client] connecting to {Host}:{Port} ...");
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(Host, Port);
        socket.NoDelay = true;

        var session = new TeraSession(ProtocolVersion, MajorPatchVersion);
        await Handshake(socket, session);
        Console.WriteLine("[test-client] handshake OK\n");

        // 1. C_CHECK_VERSION -> S_CHECK_VERSION
        await Send(socket, session, "C_CHECK_VERSION", new Dictionary<string, object>
        {
            ["version"] = new List<object>
            {
                new Dictionary<string, object> { ["index"] = 0, ["value"] = 0 },
                new Dictionary<string, object> { ["index"] = 1, ["value"] = 0 },
            }
        });
        await ExpectAndPrint(socket, session, "S_CHECK_VERSION");

        // 2. C_LOGIN_ARBITER -> S_LOGIN_ARBITER (+ S_LOGIN_ACCOUNT_INFO)
        await Send(socket, session, "C_LOGIN_ARBITER", new Dictionary<string, object>
        {
            ["unk1"] = 0,
            ["unk2"] = (byte)0,
            ["language"] = 6u,          // EUR
            ["patchVersion"] = 100,
            ["name"] = "testaccount",
            ["ticket"] = Array.Empty<byte>(),
        });
        await ExpectAndPrint(socket, session, "S_LOGIN_ARBITER");
        await ExpectAndPrint(socket, session, "S_LOGIN_ACCOUNT_INFO");

        // 3. C_GET_USER_LIST -> S_GET_USER_LIST
        await Send(socket, session, "C_GET_USER_LIST", new Dictionary<string, object>());
        await ExpectAndPrint(socket, session, "S_GET_USER_LIST");

        // 4. C_SELECT_USER -> S_LOGIN
        await Send(socket, session, "C_SELECT_USER", new Dictionary<string, object>
        {
            ["id"] = 1,
            ["unk"] = (byte)0,
        });
        await ExpectAndPrint(socket, session, "S_LOGIN");

        Console.WriteLine("\n[test-client] LOGIN CHAIN COMPLETE - all packets round-tripped self-consistently.");
    }

    // --- handshake ---
    private static async Task Handshake(Socket socket, TeraSession session)
    {
        var magic = await ReadExact(socket, 4);
        uint m = (uint)(magic[0] | (magic[1] << 8) | (magic[2] << 16) | (magic[3] << 24));
        if (m != 1) throw new Exception($"bad magic 0x{m:X8}");

        var c1 = RandomKey(); c1.CopyTo(session.ClientKeys[0], 0); await SendAll(socket, c1);
        (await ReadExact(socket, 128)).CopyTo(session.ServerKeys[0], 0);
        var c2 = RandomKey(); c2.CopyTo(session.ClientKeys[1], 0); await SendAll(socket, c2);
        (await ReadExact(socket, 128)).CopyTo(session.ServerKeys[1], 0);
        session.Init();
    }

    // --- send a named packet via the codec, framed + encrypted ---
    private static async Task Send(Socket socket, TeraSession session, string name,
        Dictionary<string, object> fields)
    {
        var def = _defs.Get(name) ?? throw new Exception($"no def for {name}");
        ushort opcode = _opcodes[name];
        byte[] body = new DefinitionWriter().Write(def, fields);

        int total = body.Length + 4;
        var packet = new byte[total];
        packet[0] = (byte)total; packet[1] = (byte)(total >> 8);
        packet[2] = (byte)opcode; packet[3] = (byte)(opcode >> 8);
        Array.Copy(body, 0, packet, 4, body.Length);

        var enc = (byte[])packet.Clone();
        session.DecryptFromClient(enc, enc.Length); // encrypt outgoing
        await SendAll(socket, enc);
        Console.WriteLine($"[test-client] -> {name} ({total} bytes, opcode {opcode})");
    }

    // --- read one framed packet, decrypt, verify opcode, decode + print fields ---
    private static async Task ExpectAndPrint(Socket socket, TeraSession session, string expectedName)
    {
        var header = await ReadExact(socket, 4);
        session.EncryptToClient(header, header.Length); // decrypt header
        int total = header[0] | (header[1] << 8);
        ushort opcode = (ushort)(header[2] | (header[3] << 8));

        if (total < 4 || total > 65535)
        {
            Console.WriteLine($"[test-client] <- FAIL implausible size {total} (expected {expectedName})");
            return;
        }

        byte[] body = total > 4 ? await ReadExact(socket, total - 4) : Array.Empty<byte>();
        if (body.Length > 0) session.EncryptToClient(body, body.Length); // decrypt body

        string gotName = _opcodes.NameOf(opcode);
        bool match = gotName == expectedName;
        Console.WriteLine($"[test-client] <- {gotName} ({total} bytes){(match ? "" : $"  [EXPECTED {expectedName}!]")}");

        // Decode with the def and print a short field summary.
        var def = _defs.Get(gotName);
        if (def != null && body.Length > 0)
        {
            try
            {
                var fields = new DefinitionReader(body).Read(def);
                PrintFields(fields, indent: "      ");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      (decode error: {ex.Message})");
            }
        }
    }

    private static void PrintFields(Dictionary<string, object> fields, string indent, int depth = 0)
    {
        if (depth > 2) { Console.WriteLine(indent + "..."); return; }
        foreach (var (k, v) in fields)
        {
            switch (v)
            {
                case System.Collections.IEnumerable e and not string and not byte[]:
                {
                    int n = 0; foreach (var _ in e) n++;
                    Console.WriteLine($"{indent}{k}: [{n} items]");
                    // print first element if it's a record
                    foreach (var item in e)
                    {
                        if (item is Dictionary<string, object> d)
                            PrintFields(d, indent + "  ", depth + 1);
                        break;
                    }
                    break;
                }
                case byte[] b:
                    Console.WriteLine($"{indent}{k}: {b.Length} bytes");
                    break;
                default:
                    Console.WriteLine($"{indent}{k}: {v}");
                    break;
            }
        }
    }

    // --- socket helpers ---
    private static byte[] RandomKey() { var k = new byte[128]; RandomNumberGenerator.Fill(k); return k; }

    private static async Task<byte[]> ReadExact(Socket s, int n)
    {
        var buf = new byte[n]; int got = 0;
        while (got < n)
        {
            int r = await s.ReceiveAsync(buf.AsMemory(got, n - got), SocketFlags.None);
            if (r == 0) throw new IOException($"closed after {got}/{n} bytes");
            got += r;
        }
        return buf;
    }

    private static async Task SendAll(Socket s, byte[] data)
    {
        int sent = 0;
        while (sent < data.Length)
            sent += await s.SendAsync(data.AsMemory(sent), SocketFlags.None);
    }
}

internal sealed class NullLogger : Microsoft.Extensions.Logging.ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) { }
}
