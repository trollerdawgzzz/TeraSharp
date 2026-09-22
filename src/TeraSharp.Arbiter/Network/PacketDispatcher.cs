// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;

namespace TeraSharp.Arbiter.Network;

public delegate bool PacketHandler(GameSession session, ReadOnlyMemory<byte> body);

/// <summary>
/// Routes incoming client opcodes to registered handlers. Unknown opcodes are
/// forwarded to WorldServer when the session is in-world, otherwise dropped.
/// A handler that throws never takes down the server.
/// </summary>
public sealed class PacketDispatcher
{
    private readonly record struct Registration(PacketHandler Handler, int MinBodyLength, string Name);

    private readonly Dictionary<ushort, Registration> _handlers = new();
    private readonly ILogger<PacketDispatcher> _log;

    public PacketDispatcher(ILogger<PacketDispatcher> log) => _log = log;

    public void Register(ushort opcode, string name, int minBodyLength, PacketHandler handler)
    {
        if (_handlers.ContainsKey(opcode))
            throw new InvalidOperationException($"opcode {opcode} ({name}) already registered");
        _handlers[opcode] = new Registration(handler, minBodyLength, name);
    }

    public void Dispatch(GameSession session, ReadOnlyMemory<byte> packet)
    {
        var span = packet.Span;
        if (span.Length < 4)
        {
            _log.LogWarning("Dropped runt packet ({Len} bytes) from {Session}", span.Length, session.Id);
            return;
        }

        ushort opcode = (ushort)(span[2] | (span[3] << 8));
        ReadOnlyMemory<byte> body = packet[4..];

        if (!_handlers.TryGetValue(opcode, out var reg))
        {
            // T45: never forward a packet we know the Arbiter owns. World has no handler for any of
            // them and prints "handler has not been implemented yet!!!", which reads as a World bug
            // and hides ours. status/CLIENT-REJECTS.md.
            if (Handlers.ArbiterClientHandlers.ArbiterOwned.Contains(opcode))
            {
                _log.LogError("{Opcode} (0x{Opcode:X4}) is Arbiter-owned and has no handler - dropped, NOT forwarded",
                    opcode, opcode);
                return;
            }
            if (session.InWorld)
            {
                session.ForwardToWorld(packet.ToArray());
                return;
            }
            _log.LogDebug("Unmapped opcode {Opcode} ({Len}-byte body) from {Session}", opcode, body.Length, session.Id);
            return;
        }

        if (body.Length < reg.MinBodyLength)
        {
            _log.LogWarning("{Name}: body {Len} < min {Min} from {Session} - dropped",
                reg.Name, body.Length, reg.MinBodyLength, session.Id);
            return;
        }

        try { reg.Handler(session, body); }
        catch (PacketReadException ex) { _log.LogWarning("{Name}: malformed packet from {Session}: {Msg}", reg.Name, session.Id, ex.Message); }
        catch (Exception ex) { _log.LogError(ex, "{Name}: unhandled error from {Session}", reg.Name, session.Id); }
    }

    public bool IsRegistered(ushort opcode) => _handlers.ContainsKey(opcode);
    public int RegisteredCount => _handlers.Count;
}
