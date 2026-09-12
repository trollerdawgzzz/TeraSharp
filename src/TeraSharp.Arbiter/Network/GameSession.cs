using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Network.Crypto;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Network;

public sealed class GameSession : IDisposable
{
    private enum HandshakeState { Magic, ClientKey1, ClientKey2, Established, Closed }

    public Guid Id { get; } = Guid.NewGuid();
    public TeraSession Crypto { get; }
    public OpcodeTable Opcodes { get; }
    public DefinitionRegistry Definitions { get; }
    public ILogger Log => _log;

    public FakeAccount Account { get; } = new();
    public FakeCharacter? SelectedCharacter { get; set; }
    public ulong GameId { get; set; }
    public uint PlayerId { get; set; }
    /// <summary>Tunnel routing key assigned by <see cref="WorldBridge.AllocateTunnelKey"/>;
    /// sent to World in AS_ENTER_WORLD [80..83] and echoed in 0x13F7 headers.</summary>
    public uint TunnelKey { get; set; }

    /// <summary>True once the player has been handed to WorldServer; traffic tunnels through it.</summary>
    public bool InWorld { get; private set; }

    /// <summary>Set while a return-to-lobby / exit countdown is running; cancel to abort.</summary>
    public CancellationTokenSource? PendingLobbyReturn { get; set; }

    /// <summary>Which leave the client asked for; decides the S_* sent once World confirms.</summary>
    public LeaveMode PendingLeaveMode { get; private set; }
    private int _leaveFinished;
    private CancellationTokenSource? _leaveFallback;

    public static readonly HashSet<string> DebugDumpPackets = new();

    private readonly Socket _socket;
    private readonly PacketDispatcher _dispatcher;
    private readonly ILogger _log;
    private HandshakeState _state = HandshakeState.Magic;
    private byte[] _buffer = new byte[65536];
    private int _bufferLen;
    private readonly byte[] _keyScratch = new byte[128];
    private int _keyScratchLen;

    public GameSession(Socket socket, PacketDispatcher dispatcher, OpcodeTable opcodes,
        DefinitionRegistry definitions, int protocolVersion, int majorPatchVersion, ILogger log)
    {
        _socket = socket; _dispatcher = dispatcher; Opcodes = opcodes;
        Definitions = definitions; _log = log;
        Crypto = new TeraSession(protocolVersion, majorPatchVersion);
    }

    // ---- World tunnel ----

    public void EnterWorld()
    {
        if (InWorld) return;
        InWorld = true;
        _leaveFinished = 0;
        var w = Program.World;
        if (w != null) w.RegisterPlayer(this);
    }

    /// <summary>
    /// Button press (lobby / exit). Tell World the user requested to leave, exactly as the
    /// real Arbiter does before the countdown. The 0x1460 + 0x1392 pair is sent later by
    /// <see cref="CompleteLeaveToWorld"/> when the countdown finishes.
    /// </summary>
    public void BeginLeaveToWorld(LeaveMode mode)
    {
        PendingLeaveMode = mode;
        var w = Program.World;
        if (w != null && InWorld) w.SendUserRequestExit(PlayerId);
    }

    /// <summary>
    /// Countdown finished. Send AS_CANCEL_SKILL_STRICTLY + AS_LEAVE_WORLD and wait for World's
    /// save sequence + SA_LEAVE_WORLD (0x1393). The tunnel stays subscribed so the client
    /// receives World's cleanup burst; <see cref="OnWorldLeaveConfirmed"/> tears it down. In
    /// standalone mode (no World) the client is released immediately.
    /// </summary>
    public void CompleteLeaveToWorld()
    {
        var w = Program.World;
        if (w == null || !InWorld) { FinishLeaveToClient(); return; }

        w.NotifyPlayerLeave(GameId, PlayerId, PendingLeaveMode);

        // Safety net: if World never sends SA_LEAVE_WORLD, release the client anyway.
        _leaveFallback = new CancellationTokenSource();
        var token = _leaveFallback.Token;
        _ = Task.Delay(TimeSpan.FromSeconds(5), token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                _log.LogWarning("Session {Id}: SA_LEAVE_WORLD not received in 5s - releasing client", Id);
                OnWorldLeaveConfirmed();
            }
        }, TaskScheduler.Default);
    }

    /// <summary>World confirmed the leave (SA_LEAVE_WORLD). Tear down the tunnel and release
    /// the client back to character select / exit. Idempotent.</summary>
    public void OnWorldLeaveConfirmed()
    {
        if (Interlocked.Exchange(ref _leaveFinished, 1) == 1) return;
        _leaveFallback?.Cancel();
        var w = Program.World;
        if (w != null) w.UnregisterPlayer(GameId, TunnelKey);
        InWorld = false;
        FinishLeaveToClient();
    }

    private void FinishLeaveToClient()
    {
        if (_state == HandshakeState.Closed) return;
        switch (PendingLeaveMode)
        {
            case LeaveMode.Exit:
                SendByDef("S_EXIT", new Dictionary<string, object> { ["category"] = 0 });
                _log.LogInformation("Session {Id}: sent S_EXIT", Id);
                break;
            default: // Lobby
                SendByDef("S_RETURN_TO_LOBBY", new Dictionary<string, object>());
                _log.LogInformation("Session {Id}: sent S_RETURN_TO_LOBBY", Id);
                break;
        }
        PendingLobbyReturn = null;
    }

    /// <summary>Client socket dropped. Tell World (disconnect values) and tear down immediately;
    /// there is no client left to send cleanup packets to.</summary>
    public void LeaveWorld()
    {
        if (!InWorld) return;
        InWorld = false;
        _leaveFallback?.Cancel();
        var w = Program.World;
        if (w != null)
        {
            w.UnregisterPlayer(GameId, TunnelKey);
            w.NotifyPlayerLeave(GameId, PlayerId, LeaveMode.Disconnect);
        }
    }

    public void ForwardToWorld(byte[] packet)
    {
        var w = Program.World;
        if (w != null && InWorld) w.TunnelFromClient(GameId, packet);
    }

    // ---- Lifecycle ----

    public void Start() => SendRaw(new byte[] { 0x01, 0x00, 0x00, 0x00 });

    public void OnReceive(ReadOnlySpan<byte> data)
    {
        switch (_state)
        {
            case HandshakeState.Magic: case HandshakeState.ClientKey1: case HandshakeState.ClientKey2:
                HandleHandshake(data); break;
            case HandshakeState.Established: HandleEstablished(data); break;
            case HandshakeState.Closed: break;
        }
    }

    private void HandleHandshake(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        while (offset < data.Length && _state != HandshakeState.Established && _state != HandshakeState.Closed)
        {
            int want = 128 - _keyScratchLen;
            int take = Math.Min(want, data.Length - offset);
            data.Slice(offset, take).CopyTo(_keyScratch.AsSpan(_keyScratchLen));
            _keyScratchLen += take; offset += take;
            if (_keyScratchLen < 128) return;
            _keyScratchLen = 0;

            switch (_state)
            {
                case HandshakeState.Magic:
                    _keyScratch.CopyTo(Crypto.ClientKeys[0], 0);
                    SendServerKey(0); _state = HandshakeState.ClientKey1; break;
                case HandshakeState.ClientKey1:
                    _keyScratch.CopyTo(Crypto.ClientKeys[1], 0);
                    SendServerKey(1); Crypto.Init(); _state = HandshakeState.Established;
                    _log.LogInformation("Session {Id} handshake complete", Id);
                    if (offset < data.Length) HandleEstablished(data.Slice(offset));
                    return;
            }
        }
    }

    private void SendServerKey(int index)
    {
        var key = new byte[128];
        RandomNumberGenerator.Fill(key);
        key.CopyTo(Crypto.ServerKeys[index], 0);
        SendRaw(key);
    }

    private void HandleEstablished(ReadOnlySpan<byte> data)
    {
        var chunk = data.ToArray();
        Crypto.DecryptFromClient(chunk, chunk.Length);
        AppendToBuffer(chunk);
        int pos = 0;
        while (_bufferLen - pos >= 2)
        {
            int size = _buffer[pos] | (_buffer[pos + 1] << 8);
            if (size < 4) { _log.LogWarning("Session {Id}: bad size {Size}", Id, size); Close(); return; }
            if (_bufferLen - pos < size) break;
            var packet = new byte[size];
            Array.Copy(_buffer, pos, packet, 0, size);
            _dispatcher.Dispatch(this, packet);
            pos += size;
        }
        if (pos > 0)
        {
            int remaining = _bufferLen - pos;
            Array.Copy(_buffer, pos, _buffer, 0, remaining);
            _bufferLen = remaining;
        }
    }

    private void AppendToBuffer(ReadOnlySpan<byte> data)
    {
        if (_bufferLen + data.Length > _buffer.Length)
        {
            int newSize = _buffer.Length * 2;
            while (newSize < _bufferLen + data.Length) newSize *= 2;
            Array.Resize(ref _buffer, newSize);
        }
        data.CopyTo(_buffer.AsSpan(_bufferLen));
        _bufferLen += data.Length;
    }

    // ---- Packet I/O ----

    public Dictionary<string, object>? ReadByDef(string packetName, ReadOnlyMemory<byte> body)
    {
        var def = Definitions.Get(packetName);
        if (def == null) { _log.LogWarning("Session {Id}: no def for {Name}", Id, packetName); return null; }
        return new DefinitionReader(body.Span).Read(def);
    }

    public void SendByDef(string packetName, IReadOnlyDictionary<string, object> fields)
    {
        if (!Opcodes.TryGetCode(packetName, out ushort opcode))
        { _log.LogError("Session {Id}: unknown packet {Name} (no opcode)", Id, packetName); return; }
        var def = Definitions.Get(packetName);
        if (def == null) { _log.LogError("Session {Id}: no def for {Name}", Id, packetName); return; }

        var writer = new DefinitionWriter();
        bool dump = DebugDumpPackets.Contains(packetName);
        if (dump) writer.Trace = new List<string>();
        byte[] bodyBytes = writer.Write(def, fields);
        var packet = Frame(opcode, bodyBytes);

        if (dump)
        {
            int max = Math.Min(packet.Length, 120);
            var hex = new System.Text.StringBuilder(max * 3);
            for (int i = 0; i < max; i++) hex.Append(packet[i].ToString("X2")).Append(' ');
            _log.LogInformation("[DUMP] {Name} op={Op} total={Total}: {Hex}", packetName, opcode, packet.Length, hex.ToString().TrimEnd());
            if (writer.Trace != null)
                _log.LogInformation("[TRACE] {Name}: {Trace}", packetName, string.Join("  ", writer.Trace));
        }
        Send(packet);
    }

    public void SendRawBody(string packetName, byte[] body)
    {
        if (!Opcodes.TryGetCode(packetName, out ushort opcode))
        { _log.LogError("Session {Id}: unknown packet {Name} (no opcode)", Id, packetName); return; }
        Send(Frame(opcode, body));
    }

    private static byte[] Frame(ushort opcode, byte[] body)
    {
        int total = body.Length + 4;
        var packet = new byte[total];
        packet[0] = (byte)total; packet[1] = (byte)(total >> 8);
        packet[2] = (byte)opcode; packet[3] = (byte)(opcode >> 8);
        Array.Copy(body, 0, packet, 4, body.Length);
        return packet;
    }

    public void SendPacket(string name, Action<PacketWriter> build)
    {
        if (!Opcodes.TryGetCode(name, out ushort opcode))
        { _log.LogError("Session {Id}: unknown packet {Name}", Id, name); return; }
        var w = new PacketWriter();
        build(w);
        Send(w.ToPacket(opcode));
    }

    private readonly object _sendLock = new();

    public void Send(byte[] packet)
    {
        lock (_sendLock)
        {
            if (_state == HandshakeState.Established)
            {
                var copy = (byte[])packet.Clone();
                Crypto.EncryptToClient(copy, copy.Length);
                SendRaw(copy);
            }
            else SendRaw(packet);
        }
    }

    private void SendRaw(byte[] data)
    {
        try { _socket.Send(data); }
        catch (Exception ex) { _log.LogWarning("Session {Id} send failed: {Msg}", Id, ex.Message); Close(); }
    }

    public void Close()
    {
        if (_state == HandshakeState.Closed) return;
        _state = HandshakeState.Closed;
        LeaveWorld();
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        _socket.Close();
        _log.LogInformation("Session {Id} closed", Id);
    }

    public void Dispose() => Close();
}
