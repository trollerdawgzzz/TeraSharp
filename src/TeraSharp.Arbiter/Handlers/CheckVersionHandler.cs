// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>
/// C_CHECK_VERSION - first packet after the crypto handshake. The client sends
/// an array of {index, value} version entries; the server replies
/// S_CHECK_VERSION { bool ok }. Replying ok=1 lets the client proceed to login
/// (universal behaviour across TERA emulators).
///
/// This is also the first handler wired to the definition-driven codec: it reads
/// the incoming packet via the loaded def (proving the reader), and sends the
/// reply via the def (proving the writer).
/// </summary>
public sealed class CheckVersionHandler
{
    // Two arrays' worth of count/offset header minimum; the def's array header is
    // 4 bytes. Keep a small floor.
    public const int MinBodyLength = 4;

    private readonly ILogger _log;

    public CheckVersionHandler(ILogger log) => _log = log;

    public bool Handle(GameSession session, ReadOnlyMemory<byte> body)
    {
        // Definition-driven read (proof of the reader). For C_CHECK_VERSION the
        // contents aren't needed to respond, but reading them exercises the codec
        // and logs the client's reported versions during bring-up.
        var fields = session.ReadByDef("C_CHECK_VERSION", body);
        if (fields != null && fields.TryGetValue("version", out var arrObj)
            && arrObj is System.Collections.IEnumerable arr)
        {
            int n = 0;
            foreach (var _ in arr) n++;
            _log.LogInformation("C_CHECK_VERSION from {Id}: {N} version entr(y/ies) -> replying ok",
                session.Id, n);
        }
        else
        {
            _log.LogInformation("C_CHECK_VERSION from {Id} -> replying ok", session.Id);
        }

        // Definition-driven write (proof of the writer): S_CHECK_VERSION { ok }.
        session.SendByDef("S_CHECK_VERSION", new Dictionary<string, object> { ["ok"] = true });
        return true;
    }
}
