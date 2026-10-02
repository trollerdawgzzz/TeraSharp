// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace TeraSharp.Arbiter.Persistence;

/// <summary>
/// T224 - the ONE character-delete path, and the ServerConfig.xml policy that drives it.
///
/// <para>Before this there were two: <c>CharacterHandlers.OnDeleteUser</c> always scheduled a
/// 72 h soft delete from the hardcoded <see cref="CharacterStore.DeleteExpireHours"/>, and
/// <c>AdminApi</c>'s <c>/api/delete-character</c> always hard-deleted. Neither read
/// <c>ServerConfig.xml</c>, so <c>&lt;DeleteUser expireHour2="0"&gt;</c> - "delete immediately" -
/// still parked the row for three days. <see cref="CharacterStore.PurgeExpiredDeletes"/> was
/// never called from anything but a test, and <c>GetCharacters</c> had no
/// <c>delete_at</c> predicate, so the parked row came straight back in the next
/// S_GET_USER_LIST: the reported "delete doesn't stick".</para>
///
/// <para><b>Retail, pinned.</b> cap_final_client2 frames 11 (a delete pending) and 35 (after
/// C_CANCEL_DELETE_USER at frame 32 answered ok): both S_GET_USER_LIST frames are 1183 bytes and
/// differ in six bytes only. So a pending-delete character <b>stays in the list</b> - it is not
/// hidden - and the cancel flips one byte:</para>
/// <list type="bullet">
/// <item>element 0 <c>isDeleting</c> 1 -&gt; 0 (frame offset 107)</item>
/// <item><c>deleteTime</c> 1789879815 in BOTH frames - retail leaves the stamp behind, the
///   client gates on isDeleting</item>
/// <item><c>deleteRemainSec</c> 259182 then 259150, i.e. <c>deleteTime - now</c> recomputed
///   per send (the two frames are 32 s apart, and banRemainSec drops by the same 32)</item>
/// <item>the fixed part reads <c>deletionSectionClassifyLevel 5, deleteCharacterExpireHour1 0,
///   deleteCharacterExpireHour2 72</c> - exactly this deployment's
///   <c>&lt;DeleteUser expireHour1="0" expireHour2="72" deletionSectionClassifyLevel="5" /&gt;</c>,
///   and 259200 s is 72 h to the second</item>
/// </list>
///
/// <para>Decompile, <c>Handler_C_DELETE_USER</c> Arb_part_079.c:9852: guard packet &gt;= 8, read
/// the u32 at frame offset 4, refuse to the account's own character list ("Not Exist User :
/// Account."), then one call whose bool goes straight into S_DELETE_USER (0xB80B) and, when
/// true, a <c>"DELETE_USER"</c> audit row. It carries no hour argument - the window lives in the
/// user manager, which is why ServerConfig.xml is the only place it can come from.</para>
/// </summary>
public static class CharacterDeletion
{
    /// <summary>ServerConfig.xml's <c>&lt;DeleteUser&gt;</c>, i.e. S_GET_USER_LIST's three
    /// delete fields. Hours of 0 mean "no grace window, drop the row now".</summary>
    public readonly record struct Policy(int ExpireHour1, int ExpireHour2, int ClassifyLevel)
    {
        /// <summary>What the shipped ServerConfig.xml and cap_final_client2 frame 11 both say.</summary>
        public static readonly Policy Default = new(0, CharacterStore.DeleteExpireHours, 5);

        /// <summary>
        /// The grace window for one character. <c>OURS:</c> the direction of the split is
        /// inference - the three attributes sit in one element and the capture pairs
        /// classifyLevel 5 with hours (0, 72), so a character below the classify level is the
        /// throwaway that goes at once and one at or above it gets the full window. Nothing in
        /// Arb_part_079 pins which way round it is; set both hours the same to opt out.
        /// </summary>
        public int WindowHoursFor(int level) => level < ClassifyLevel ? ExpireHour1 : ExpireHour2;
    }

    /// <summary>What a delete did. <see cref="Refused"/> is the store saying no.</summary>
    public enum Outcome { Refused = 0, Hard = 1, Scheduled = 2 }

    private static readonly object Gate = new();
    private static Policy? _cached;
    private static Policy? _override;

    /// <summary>Force a policy, for tests and for a deployment that wants to ignore the file.
    /// Pass null to go back to reading ServerConfig.xml.</summary>
    public static void UseForTests(Policy? policy)
    {
        lock (Gate) { _override = policy; _cached = null; }
    }

    /// <summary>The live policy: the override, else ServerConfig.xml read once, else
    /// <see cref="Policy.Default"/>.</summary>
    public static Policy Current
    {
        get
        {
            lock (Gate)
            {
                if (_override.HasValue) return _override.Value;
                _cached ??= Load(World.WorldServerList.DefaultPath);
                return _cached.Value;
            }
        }
    }

    /// <summary>Read the policy out of a ServerConfig.xml path. A missing or broken file is not
    /// an error - it is the default, the same way WorldServerList treats one.</summary>
    public static Policy Load(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (IOException) { return Policy.Default; }
        catch (UnauthorizedAccessException) { return Policy.Default; }
    }

    /// <summary>
    /// Parse <c>&lt;DeleteUser expireHour1="0" expireHour2="72"
    /// deletionSectionClassifyLevel="5" /&gt;</c>. Any attribute that is absent or unparseable
    /// keeps its default, so a half-written element still yields a usable policy.
    /// </summary>
    public static Policy Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return Policy.Default;
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return Policy.Default; }

        var el = doc.Descendants("DeleteUser").FirstOrDefault();
        if (el == null) return Policy.Default;

        var d = Policy.Default;
        return new Policy(
            Attr(el, "expireHour1", d.ExpireHour1),
            Attr(el, "expireHour2", d.ExpireHour2),
            Attr(el, "deletionSectionClassifyLevel", d.ClassifyLevel));
    }

    private static int Attr(XElement el, string name, int fallback)
    {
        var a = el.Attribute(name)?.Value;
        return int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v : fallback;
    }

    /// <summary>
    /// Delete one character the way the policy says: a zero window is a HARD delete (the row and
    /// its dependents go now, nothing to restore), anything else is the soft delete that
    /// C_CANCEL_DELETE_USER and <c>/api/restore-character</c> undo. Every caller - the lobby
    /// handler, the admin API, a GM command - comes through here so the three cannot drift.
    /// </summary>
    /// <param name="level">The character's level, for the classify-level split. Pass the row's
    /// level, not the session's cached one.</param>
    /// <param name="deleteAt">When the row is due to go, 0 for a hard delete.</param>
    public static Outcome Delete(
        CharacterStore? store, int characterId, long accountId, string byWhom,
        int level, DateTimeOffset now, Policy policy, out long deleteAt, ILogger? log = null)
    {
        deleteAt = 0;
        if (store == null) return Outcome.Refused;

        int hours = policy.WindowHoursFor(level);
        long nowUnix = now.ToUnixTimeSeconds();

        if (hours <= 0)
        {
            bool hard = store.DeleteCharacter(characterId, accountId);
            log?.LogInformation(
                "delete {Id} by '{By}': level {Level} window 0 h -> hard delete {Ok}",
                characterId, byWhom, level, hard);
            return hard ? Outcome.Hard : Outcome.Refused;
        }

        deleteAt = nowUnix + (long)hours * 3600L;
        bool soft = store.SoftDeleteCharacter(characterId, accountId, byWhom, deleteAt, nowUnix);
        if (!soft) { deleteAt = 0; return Outcome.Refused; }
        log?.LogInformation(
            "delete {Id} by '{By}': level {Level} window {Hours} h -> due at {At}",
            characterId, byWhom, level, hours, deleteAt);
        return Outcome.Scheduled;
    }

    /// <summary>
    /// The three per-character S_GET_USER_LIST fields, from a <c>delete_at</c> stamp.
    /// <paramref name="deleteAt"/> of 0 is the not-deleting case retail sends as
    /// (false, 0, 0 - which with deleteTime 0 is just 0).
    /// </summary>
    public static (bool IsDeleting, long DeleteTime, int RemainSec) LobbyFields(long deleteAt, long nowUnix)
    {
        if (deleteAt <= 0) return (false, 0L, 0);
        long remain = deleteAt - nowUnix;
        if (remain < 0) remain = 0;
        return (true, deleteAt, remain > int.MaxValue ? int.MaxValue : (int)remain);
    }
}
