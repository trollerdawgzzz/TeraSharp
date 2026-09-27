// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml.Linq;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// Local replacement for MatchServer's MA_MATCH_POOL_INFO statistics. This is measured queue
/// state, not a fabricated MatchServer reply. Match:177439-178574, 228363-228367, 232022-232023.
/// </summary>
public static class MatchBrowseStatistics
{
    public sealed record PoolRule(DungeonMatchRule Bounds, IReadOnlyList<byte> RoleGroups);
    public sealed record Settings(IReadOnlyDictionary<int, PoolRule> Pools,
        int CheckTime1, int CheckTime2, int CheckNum1Max, int CheckNum2Max, int CheckNum3Max);

    public static readonly SheetValue<Settings> Entry = new(
        "DungeonMatching.xml + MatchingRoleTemplate.xml", "matching browser wait and role statistics",
        new Settings(new Dictionary<int, PoolRule>(), 0, 0, 0, 0, 0), Read, x => x.Pools.Count);

    public static Settings? Read(string directory)
    {
        string roles = Path.Combine(directory, "MatchingRoleTemplate.xml");
        string dungeons = Path.Combine(directory, "DungeonMatching.xml");
        if (!File.Exists(roles) || !File.Exists(dungeons)) return null;
        return Parse(XDocument.Load(roles), XDocument.Load(dungeons));
    }

    public static Settings Parse(XDocument roles, XDocument dungeons)
    {
        static int Required(XElement? row, string key)
            => int.TryParse((string?)row?.Attribute(key), out int n) ? n
                : throw new FormatException($"Missing matching statistics attribute {key}");
        var groups = new Dictionary<int, IReadOnlyList<byte>>();
        foreach (var role in roles.Descendants("Role"))
        {
            var data = role.Element("RoleData");
            if (data == null) continue;
            var masks = new List<byte>();
            byte unconstrained = 0;
            var names = new[] { "tanker", "dealer", "healer" };
            for (int index = 0; index < names.Length; index++)
            {
                byte mask = (byte)(1 << index);
                // Match:81265-81508 groups roles with neither bound into one shared set.
                if (string.IsNullOrEmpty((string?)data.Attribute(names[index] + "Min")) &&
                    string.IsNullOrEmpty((string?)data.Attribute(names[index] + "Max")))
                    unconstrained |= mask;
                else masks.Add(mask);
            }
            if (unconstrained != 0) masks.Add(unconstrained);
            groups[Required(role, "id")] = masks.OrderBy(x => x).ToArray();
        }
        var bounds = DungeonMatchRules.Parse(roles, dungeons);
        var pools = new SortedDictionary<int, PoolRule>();
        foreach (var dungeon in dungeons.Descendants("Dungeon"))
        {
            int id = Required(dungeon, "id");
            int roleId = (int?)dungeon.Attribute("matchingRoleId") ?? 1;
            if (bounds.TryGetValue(id, out var rule) && groups.TryGetValue(roleId, out var masks))
                pools[id] = new PoolRule(rule, masks);
        }
        var time = dungeons.Descendants("MatchingTimeDisplay").FirstOrDefault();
        var state = dungeons.Descendants("MatchingStateDisplay").FirstOrDefault();
        // Match:57953-57974 stores these values directly; checkTime is NOT multiplied by 1000.
        return new Settings(pools, Required(time, "checkTime1"), Required(time, "checkTime2"),
            Required(state, "checkNum1Max"), Required(state, "checkNum2Max"), Required(state, "checkNum3Max"));
    }

    /// <summary>
    /// One native statistics manager. Pending membership is a 600 x 1-second ring; a refresh
    /// samples only its current bucket. Completed counters are retained across rotations by
    /// the native implementation (177762-177798), so they are retained here too.
    /// </summary>
    public sealed class Engine
    {
        private sealed class Pool
        {
            public readonly HashSet<MatchQueueManager.Entry>?[] Pending = new HashSet<MatchQueueManager.Entry>?[600];
            public long CompletedSeconds;
            public long CompletedCount;
            public readonly int[] RoleCounts = new int[3];
        }

        private readonly Settings _settings;
        private readonly Dictionary<int, Pool> _pools;
        private long? _lastRotation;
        private int _index;
        public IReadOnlyList<MatchBrowsePackets.MatchingStatus> Rows { get; private set; }

        public Engine(Settings settings)
        {
            _settings = settings;
            _pools = settings.Pools.Keys.ToDictionary(id => id, _ => new Pool());
            Rows = BuildRows(); // Native no-history defaults are time=3, each role=1.
        }

        /// <summary>Called after formation, once for each whole application, not each member.</summary>
        public void Completed(int instanceId, IReadOnlyList<MatchQueueManager.Entry> applications, DateTimeOffset now)
        {
            if (!_pools.TryGetValue(instanceId, out var pool)) return;
            foreach (var application in applications)
            {
                // Match:235373-235381/235469-235477 use whole UTC seconds and the selected
                // matching pool only. Other destinations retain their sampled pending history.
                pool.CompletedSeconds += now.ToUnixTimeSeconds() - application.QueuedAt.ToUnixTimeSeconds();
                pool.CompletedCount++;
                foreach (var bucket in pool.Pending) bucket?.Remove(application);
            }
        }

        /// <summary>Publish at the native manager's greater-than-10-second matching tick.</summary>
        public void Publish(DateTimeOffset now, IReadOnlyList<MatchQueueManager.Entry> applications)
        {
            long milliseconds = now.ToUnixTimeSeconds() * 1000;
            bool sample = true;
            if (_lastRotation == null)
            {
                _lastRotation = milliseconds;
                _index = (int)((milliseconds % 600000) / 1000);
            }
            else if (milliseconds - _lastRotation.Value <= 1000) sample = false;
            else
            {
                // Do not fill skipped buckets with a made-up historical queue snapshot.
                for (int i = 0; i < 600 && milliseconds - _lastRotation.Value > 1000; i++)
                {
                    _index = (_index + 1) % 600;
                    foreach (var pool in _pools.Values) pool.Pending[_index]?.Clear();
                    _lastRotation += 1000;
                }
            }
            foreach (var (id, pool) in _pools)
            {
                // Formation publishes after removing its applications, even when the local
                // immediate formation pass shares the previous sample's UTC second. Refresh
                // current demand without inventing another pending-history observation.
                Array.Clear(pool.RoleCounts);
                foreach (var application in applications)
                {
                    if (application.State != MatchQueueManager.MatchState.Waiting || !application.Wants(id)) continue;
                    if (sample) (pool.Pending[_index] ??= new()).Add(application);
                    foreach (var member in application.Members) pool.RoleCounts[(int)member.Role]++;
                }
            }
            Rows = BuildRows();
        }

        private IReadOnlyList<MatchBrowsePackets.MatchingStatus> BuildRows()
        {
            var rows = new List<MatchBrowsePackets.MatchingStatus>(_pools.Count);
            foreach (int id in _pools.Keys.OrderBy(id => id))
            {
                var pool = _pools[id];
                var rule = _settings.Pools[id];
                var unique = new HashSet<MatchQueueManager.Entry>();
                long weighted = pool.CompletedSeconds;
                foreach (var bucket in pool.Pending)
                    if (bucket != null) { weighted += bucket.Count * 1000L; unique.UnionWith(bucket); }
                long samples = pool.CompletedCount + unique.Count;
                byte time = 3;
                if (samples > 0)
                {
                    // Native float division, not integer division (Match:178520-178574).
                    float average = (float)weighted / samples;
                    time = (byte)(average <= _settings.CheckTime1 ? 1 : average <= _settings.CheckTime2 ? 2 : 3);
                }
                var roles = new List<MatchBrowsePackets.RoleStatus>();
                int[] minima = { rule.Bounds.TankMin, rule.Bounds.DealerMin, rule.Bounds.HealerMin };
                int[] maxima = { rule.Bounds.TankMax, rule.Bounds.DealerMax, rule.Bounds.HealerMax };
                foreach (byte mask in rule.RoleGroups.OrderBy(mask => mask))
                {
                    float expected = 0;
                    int queued = 0;
                    for (int role = 0; role < 3; role++)
                        if ((mask & (1 << role)) != 0)
                        {
                            expected += (minima[role] + maxima[role]) * 0.5f;
                            queued += pool.RoleCounts[role];
                        }
                    int target = (int)MathF.Floor(expected + 0.5f);
                    byte state = (byte)(queued <= _settings.CheckNum1Max * target ? 1
                        : queued <= _settings.CheckNum2Max * target ? 2
                        : queued <= _settings.CheckNum3Max * target ? 3 : 4);
                    roles.Add(new(mask, state));
                }
                rows.Add(new(id, time, roles));
            }
            return rows;
        }
    }

    private static readonly object Gate = new();
    private static Engine? _engine;
    private static Settings? _settings;
    private static Timer? _timer;
    private static long _lastPublishTick;

    private static Engine Current()
    {
        var settings = Entry.Value;
        if (!ReferenceEquals(_settings, settings))
        {
            _settings = settings;
            _engine = new Engine(settings);
        }
        return _engine!;
    }

    /// <summary>Starts only in the running server; isolated tests drive Engine.Publish directly.</summary>
    public static void Start()
    {
        lock (Gate)
        {
            _ = Current();
            if (Program.World == null || _timer != null) return;
            _lastPublishTick = Environment.TickCount64;
            _timer = new Timer(_ =>
            {
                lock (Gate)
                {
                    if (_timer == null) return;
                    // Native manager uses its monotonic server tick for this cadence;
                    // UTC seconds belong only to history/rotation (Match:228363-228367).
                    long tick = Environment.TickCount64;
                    if (tick - _lastPublishTick <= 10000) return;
                    _lastPublishTick = tick;
                    Current().Publish(MatchWiring.Clock(), MatchQueueManager.All());
                }
            }, null, 1000, 1000);
        }
    }

    public static IReadOnlyList<MatchBrowsePackets.MatchingStatus> Snapshot()
    {
        Start();
        lock (Gate) return Current().Rows;
    }

    public static void Completed(MatchQueueManager.FormedGroup group, DateTimeOffset now)
    {
        Start();
        lock (Gate) Current().Completed(group.InstanceId, group.Entries, now);
    }

    /// <summary>Native matching tick forms first, then publishes (Match:232022-232023).</summary>
    public static void Publish(DateTimeOffset now)
    {
        lock (Gate) Current().Publish(now, MatchQueueManager.All());
    }

    public static void Reset()
    {
        lock (Gate)
        {
            _timer?.Dispose(); _timer = null;
            _settings = null; _engine = null;
        }
    }
}
