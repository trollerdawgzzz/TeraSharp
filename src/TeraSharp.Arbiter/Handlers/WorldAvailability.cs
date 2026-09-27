// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>T185: standalone spawn is an explicit harness option, never a World outage fallback.</summary>
public static class WorldAvailability
{
    public const string StandaloneVariable = "TERASHARP_STANDALONE";
    public static bool StandaloneEnabled => TerasConfig.Get(StandaloneVariable) == "1";

    // WorldEntry currently admits through the default World. A retained READY bit alone
    // is insufficient: that same World must still have a link.
    public static bool Ready([NotNullWhen(true)] WorldBridge? world)
        => world != null && world.IsReady && world.HasLinks(WorldRegistration.DefaultWorldId);

    /// <summary>Arb079:10388-10411: failed AdjustContinentOnConnect sends @769 followed by
    /// accepted=false, adminLevel=0, SelectUserErrorCode=0. cap_social3_client2 33/34 pins both.</summary>
    public static bool RefuseSelection(GameSession session, ILogger log)
    {
        log.LogWarning("C_SELECT_USER refused: no ready World link; character remains in the lobby");
        // Also undo the prepared selection if World disappeared before EnterWorld could use it.
        if (!session.InWorld)
        {
            if (session.PlayerId != 0)
                ((ICollection<KeyValuePair<int, ulong>>)DbProxyHandlers.GameIdByPlayer)
                    .Remove(new((int)session.PlayerId, session.GameId));
            session.SelectedCharacter = null;
            session.PlayerId = 0;
            session.GameId = 0;
        }
        session.SendByDef("S_SYSTEM_MESSAGE", new Dictionary<string, object> { ["message"] = "@769" });
        session.SendByDef("S_SELECT_USER", ArbiterClientHandlers.BuildSelectUserFields(accepted: false));
        return true;
    }
}
