// =============================================================================================
// ContinentRouting - FOLDED INTO World/WorldInstances.cs (T138b). This file is a tombstone.
//
// T138 added a parallel routing table here; WorldInstances.cs already owned the same job through
// DungeonChannels / DungeonTransfers / DungeonRouting, and two routing tables that can disagree
// is the failure mode worth avoiding. Everything moved:
//
//   ContinentRouting.ReadServerId          -> WorldRegistration.Parse (already existed; 0x294E)
//   ContinentRouting.ReadRoster / LinkFor  -> WorldContinentList.Parse / .Apply, which feeds the
//                                             SAME DungeonChannels.MapContinent table that
//                                             WorldServerList.SeedDefault seeds from config
//   ContinentRouting.BuildEnterContinent.. -> ContinentHandoff.EnterReply / .ReadyReply
//   ContinentRouting.Read/Add/ReleaseChannel -> DungeonChannels.Add / .Remove (already existed)
//   ContinentRouting.ClientChannel         -> ContinentHandoff.ClientChannel
//
// The one thing the fold RECONCILED: DungeonChannels reads 0x13C5's +4 field as one int32 (from
// the decompile) and T138 read it as two u16s (from the capture). Both are right - the field is
// packed, channel | (planetId << 16), the same packing S_CURRENT_CHANNEL uses. See
// ContinentHandoff.ChannelOf / .PlanetOf.
//
// Safe to delete this file; it is kept only so the fold is discoverable from where the code was.
// =============================================================================================
