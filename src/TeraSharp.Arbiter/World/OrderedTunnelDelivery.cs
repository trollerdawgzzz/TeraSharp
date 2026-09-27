// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.World;

/// <summary>
/// A ticket's ready packets keep their order after the sequence lock is released.
/// Enqueue while holding WorldBridge's reorder lock; Drain only after releasing it.
/// Exactly one drainer invokes callbacks, without holding either lock across a callback.
/// cap_instance1:137078/137095/137096 are World seq 0/16/17, but client2 saw 16/17/0.
/// </summary>
public sealed class OrderedTunnelDelivery
{
    private readonly object _gate = new();
    private readonly Queue<(Action<byte[]> Destination, byte[] Packet)> _ready = new();
    private bool _draining;
    private bool _retired;

    public void Enqueue(Action<byte[]> destination, byte[] packet)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(packet);
        lock (_gate)
            if (!_retired) _ready.Enqueue((destination, packet));
    }

    public void Drain()
    {
        lock (_gate)
        {
            if (_retired || _draining) return;
            _draining = true;
        }
        try
        {
            while (true)
            {
                (Action<byte[]> Destination, byte[] Packet) item;
                lock (_gate)
                {
                    if (_retired || _ready.Count == 0)
                    {
                        _draining = false;
                        return;
                    }
                    item = _ready.Dequeue();
                }
                // The owning callback was captured at enqueue. A reissued ticket must never
                // resolve this old packet against its new owner's registration.
                item.Destination(item.Packet);
            }
        }
        catch
        {
            lock (_gate) { _ready.Clear(); _draining = false; }
            throw;
        }
    }

    /// <summary>World reset/re-entry discards old ready packets, keeping the same owner.</summary>
    public void Clear() { lock (_gate) _ready.Clear(); }

    /// <summary>
    /// Unregister/replacement cancels this buffer permanently. An already executing callback
    /// can finish for its original owner; queued packets can never reach a replacement owner.
    /// </summary>
    public void Retire() { lock (_gate) { _retired = true; _ready.Clear(); } }
}
