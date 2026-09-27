// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace TeraSharp.Arbiter.World;

/// <summary>One <c>&lt;WorldServer&gt;</c> row of ServerConfig.xml's WorldServerList.</summary>
/// <param name="Type">
/// <c>normal</c> / <c>battlefield</c> / <c>partyMatching</c> / <c>dungeon</c>, empty when the
/// attribute is absent. World-side only: WorldServer.exe parses it into a bitmask
/// (<c>normal=1, battlefield=2, partyMatching=4, dungeon=8</c>) and it never reaches the wire,
/// so nothing here routes on it - it is kept for the log line and the status tab.
/// </param>
public readonly record struct WorldServerEntry(
    int WorldId, string Type, bool LoadAllContinents, IReadOnlyList<int> Continents);

/// <summary>
/// T111. ServerConfig.xml's <c>&lt;WorldServerList&gt;</c>, read once at startup into
/// <see cref="DungeonChannels.MapContinent"/>.
///
/// <para>This is the whole allocator. MULTIWORLD-DESIGN.md section 7.1: the Arbiter does not
/// balance anything - <c>WorldSessionManager::GetDataSession(continentId)</c> reads the
/// continent's <c>worldServerInfo</c> list out of PlanetInfo and asserts at
/// <c>WorldSessionManager.cpp(356)</c> if the count is not exactly 1. One continent, one World,
/// from this file. Seeding it is what makes the FIRST cross-World dungeon entry work, with no
/// announced channel and nothing to capture.</para>
///
/// <para>The deployment's own list, for reference:</para>
/// <code>
///   id 0   loadAllContinents="true"                      - the catch-all
///   id 10  battlefield    102 103 110 112 113 115 116 117 118 1200
///   id 11  partyMatching  (no continents)
///   id 12  dungeon        9920 3023 3027 3126 3026
///   id 13  dungeon        (no continents)
///   id 31  battlefield    156
/// </code>
/// <para>A continent no row names belongs to the catch-all World - which is why a
/// single-World server, where only id 0 is running, is unaffected by seeding this: every
/// continent it is ever asked for either resolves to 0 or resolves to a World with no links,
/// and <see cref="WorldRouting.IsLive"/> sends both to the catch-all.</para>
/// </summary>
public static class WorldServerList
{
    public const string FileName = "ServerConfig.xml";

    /// <summary>
    /// Where the live server keeps it. <c>TERASHARP_SERVERCONFIG</c> overrides the whole path;
    /// otherwise it is <c>&lt;TERASHARP_DATA&gt;\Executable\ServerConfig.xml</c>, mirroring
    /// Program.DataRoot - which is human-owned, so the default is spelled out again here rather
    /// than threaded through a constructor.
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            var explicitPath = TerasConfig.Get("TERASHARP_SERVERCONFIG");
            if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
            var root = TerasConfig.Get("TERASHARP_DATA");
            if (string.IsNullOrWhiteSpace(root)) root = @"D:\v100\TERA_SERVER.100";
            return Path.Combine(root, "Executable", FileName);
        }
    }

    /// <summary>
    /// Parse the <c>&lt;WorldServerList&gt;</c> out of ServerConfig.xml's text. Returns an empty
    /// list for text that is not XML or has no such element - a missing config is not an error,
    /// it is a single-World server.
    /// </summary>
    public static IReadOnlyList<WorldServerEntry> Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return Array.Empty<WorldServerEntry>();
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return Array.Empty<WorldServerEntry>(); }

        var list = doc.Descendants("WorldServerList").FirstOrDefault();
        if (list == null) return Array.Empty<WorldServerEntry>();

        var rows = new List<WorldServerEntry>();
        foreach (var el in list.Elements("WorldServer"))
        {
            if (!int.TryParse((string?)el.Attribute("id"), out int worldId)) continue;
            var continents = new List<int>();
            foreach (var c in el.Elements("Continent"))
                if (int.TryParse((string?)c.Attribute("id"), out int continentId))
                    continents.Add(continentId);
            rows.Add(new WorldServerEntry(
                worldId,
                (string?)el.Attribute("type") ?? string.Empty,
                string.Equals((string?)el.Attribute("loadAllContinents"), "true",
                    StringComparison.OrdinalIgnoreCase),
                continents));
        }
        return rows;
    }

    /// <summary>
    /// Apply parsed rows to the registry. Returns the number of continents mapped.
    ///
    /// <para>A continent claimed by two rows keeps the FIRST and logs. That is the closest safe
    /// reading of the binary, which treats a count other than 1 as an assertion failure and
    /// refuses to route the continent at all - refusing outright would strand players on a
    /// config typo, so the first owner wins and the log names the clash.</para>
    /// </summary>
    public static int Seed(DungeonChannels channels, IReadOnlyList<WorldServerEntry> entries,
                           ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(entries);

        var owner = new Dictionary<int, int>();
        int mapped = 0;
        foreach (var e in entries)
        {
            if ((uint)e.WorldId >= WorldRegistration.MaxWorldId)
            {
                log?.LogWarning("WorldServerList: id {W} is past the {Max} ceiling - ignored",
                    e.WorldId, WorldRegistration.MaxWorldId);
                continue;
            }
            if (e.LoadAllContinents)
            {
                channels.CatchAllWorldId = e.WorldId;
                log?.LogInformation("WorldServerList: world {W} loads all continents", e.WorldId);
            }
            foreach (int continentId in e.Continents)
            {
                if (owner.TryGetValue(continentId, out int first))
                {
                    // CA2017: one placeholder per argument - {A} may not appear twice.
                    log?.LogWarning("WorldServerList: continent {C} is claimed by world {A} and "
                        + "world {B} - the real Arbiter asserts on this; the first one keeps it",
                        continentId, first, e.WorldId);
                    continue;
                }
                owner[continentId] = e.WorldId;
                channels.MapContinent(continentId, e.WorldId);
                mapped++;
            }
        }
        return mapped;
    }

    /// <summary>Read one file and seed from it. A missing or unreadable file seeds nothing.</summary>
    public static int SeedFromFile(DungeonChannels channels, string? path, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            log?.LogDebug("WorldServerList: no {File} at {Path} - single-World defaults", FileName, path);
            return 0;
        }
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException ex)
        {
            log?.LogWarning("WorldServerList: cannot read {Path}: {Msg}", path, ex.Message);
            return 0;
        }
        var entries = Parse(text);
        int mapped = Seed(channels, entries, log);
        log?.LogInformation("WorldServerList: {N} World(s), {C} continent(s) mapped, catch-all is world {W}",
            entries.Count, mapped, channels.CatchAllWorldId);
        return mapped;
    }

    /// <summary>Seed from <see cref="DefaultPath"/>. What WorldBridge calls at startup.</summary>
    public static int SeedDefault(DungeonChannels channels, ILogger? log = null)
        => SeedFromFile(channels, DefaultPath, log);
}
