// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Web;

// =============================================================================================
// HubServer - T207. TeraSharp answers the platform hub socket itself.
//
// tera-api's shop, coupons, promo codes and admin panel all deliver through one socket: HUB_HOST
// / HUB_PORT in its .env, which on a retail stack is arb_gw_tw2_log.exe. Pointing that at
// TeraSharp instead means a stock tera-api, no retail gateway binary, and the box rows in our own
// database (Persistence/CharacterStore.Hub.cs). The wire format is HubProtocol.cs; what each call
// means is status/T207-HUB.md.
//
// WHAT IS PINNED AND WHAT IS NOT: every frame, field number and argument name here is read out of
// the client we must satisfy (tera-api's own hubConnection.js / hubFunctions.js and its generated
// protobuf modules), and the box argument names match the ones the retail Arbiter's box code
// builds (Arb_part_077.c). No capture of arb_gw's own traffic exists, so the ANSWER ids
// (BoxNotiUserAns 16, AddBenefitAns 39, ...) are the Req id + 1 by convention - tera-api matches
// an answer by jobId and decodes it with the type it asked for, so the id is never read. Said out
// loud because it is the one guess in this file.
//
//   TERASHARP_HUB_LISTEN      host:port; default 127.0.0.1:11001 (tera-api's default HUB_PORT)
//   TERASHARP_HUB_ENABLED     0/false turns the listener off; anything else, including unset, is on
//   TERASHARP_HUB_SERVER_ID   the server number tera-api addresses; default from planet.dbServerName
//
// The listener stays on loopback unless the operator says otherwise: it is an unauthenticated
// control channel (that is retail's design - the hub is trusted), so exposing it to the network
// hands out free items.
// =============================================================================================
public sealed class HubServer : IDisposable
{
    public const string ListenVariable = "TERASHARP_HUB_LISTEN";
    public const string EnabledVariable = "TERASHARP_HUB_ENABLED";
    public const string ServerIdVariable = "TERASHARP_HUB_SERVER_ID";
    public const string DefaultListen = "127.0.0.1:11001";
    public const int DefaultServerId = 2800;

    /// <summary>
    /// What the hub needs from the running server. Program wires these; a test leaves them null
    /// and every handler still works against the store alone.
    /// </summary>
    public sealed class Hooks
    {
        /// <summary>Show one line to every session of an account (S_SYSTEM_MESSAGE).</summary>
        public Action<long, string>? Notify { get; set; }
        /// <summary>Disconnect an account's sessions; returns how many went.</summary>
        public Func<long, int>? Kick { get; set; }
        /// <summary>True when the account has a session on this server.</summary>
        public Func<long, bool>? IsOnline { get; set; }
        /// <summary>
        /// T207c: every account with a session on this server, for GetServerStat's user count.
        /// Null falls back to the live World, so Program wires nothing and a test injects.
        /// </summary>
        public Func<IReadOnlyList<long>>? OnlineAccounts { get; set; }
        /// <summary>Re-send S_ACCOUNT_BENEFIT_LIST after a benefit changed.</summary>
        public Action<long>? BenefitsChanged { get; set; }
    }

    private readonly CharacterStore _store;
    private readonly Hooks _hooks;
    private readonly ILogger _log;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly uint _ourGusid;
    private readonly int _serverId;
    private bool _statAnswered;
    private Timer? _sweep;

    private HubServer(CharacterStore store, Hooks hooks, ILogger log, IPEndPoint endpoint, int serverId)
    {
        _store = store;
        _hooks = hooks;
        _log = log;
        _listener = new TcpListener(endpoint);
        _serverId = serverId;
        _ourGusid = HubProtocol.Gusid(HubProtocol.CategoryArbiterGw, serverId);
    }

    /// <summary>
    /// Tests: the server without a socket. Handle() and Call() are the whole protocol, so a test
    /// drives the real path - store, delivery and hooks - with no listener and no port.
    /// </summary>
    internal static HubServer ForTests(CharacterStore store, Hooks hooks, ILogger log, int serverId = DefaultServerId)
        => new(store, hooks, log, new IPEndPoint(IPAddress.Loopback, 0), serverId);

    /// <summary>The gusid tera-api addresses this server by: category arbitergw, number server id.</summary>
    public uint Gusid => _ourGusid;

    /// <summary>
    /// The configured server number. tera-api addresses the arbiter by the server id in its own
    /// server list, which on a one-server stack is the PlanetDB number - so the default is read
    /// out of planet.dbServerName (PlanetDB_2800 -> 2800) and can be set outright.
    /// </summary>
    public static int ServerId()
    {
        if (int.TryParse(TerasConfig.Get(ServerIdVariable), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int explicitId) && explicitId > 0) return explicitId;
        string name = TerasConfig.Get("TERASHARP_DB_SERVER_NAME") ?? string.Empty;
        int underscore = name.LastIndexOf('_');
        return underscore >= 0 && int.TryParse(name.AsSpan(underscore + 1), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int fromName) && fromName > 0 ? fromName : DefaultServerId;
    }

    /// <summary>host:port for the listener, from <see cref="ListenVariable"/>.</summary>
    public static IPEndPoint Endpoint(string? configured = null)
    {
        string listen = configured ?? TerasConfig.Get(ListenVariable) ?? DefaultListen;
        int colon = listen.LastIndexOf(':');
        string host = colon > 0 ? listen[..colon] : listen;
        string port = colon >= 0 ? listen[(colon + 1)..] : string.Empty;
        if (!int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            || number is < 1 or > 65535) number = 11001;
        if (host is "" or "0.0.0.0" or "*" or "+") return new IPEndPoint(IPAddress.Any, number);
        return IPAddress.TryParse(host, out var address)
            ? new IPEndPoint(address, number)
            : new IPEndPoint(IPAddress.Loopback, number);
    }

    /// <summary>Starts the hub, or returns null and says why. Never throws.</summary>
    public static HubServer? TryStart(CharacterStore store, Hooks hooks, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(log);

        string enabled = (TerasConfig.Get(EnabledVariable) ?? "true").Trim();
        if (enabled is "0" || enabled.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            log.LogInformation("hub: {Var} is off - tera-api purchases will not reach the game", EnabledVariable);
            return null;
        }

        var endpoint = Endpoint();
        var hub = new HubServer(store, hooks, log, endpoint, ServerId());
        try
        {
            hub._listener.Start();
        }
        catch (SocketException ex)
        {
            log.LogWarning(
                "hub: could not listen on {Endpoint} - {Msg}. Something else (arb_gw?) already has the port; stop it or set {Var}",
                endpoint, ex.Message, ListenVariable);
            hub.Dispose();
            return null;
        }

        store.EnsureHubSchema();
        _ = Task.Run(() => hub.AcceptAsync());
        hub._sweep = new Timer(_ => hub.Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
        log.LogInformation("hub listening on {Endpoint} as server {Server} - point tera-api's HUB_HOST/HUB_PORT here",
            endpoint, hub._ourGusid & 0xFFFFFF);
        return hub;
    }

    /// <summary>
    /// Deliver anything that was waiting for a character to exist. Runs every minute because a
    /// box bought before the buyer's first login has nowhere to go at the time it arrives.
    /// </summary>
    public void Sweep()
    {
        try
        {
            int done = BoxDelivery.DeliverPending(_store, 0, _log);
            if (done > 0) _log.LogInformation("hub: {N} pending box(es) delivered", done);
        }
        catch (Exception ex)
        {
            _log.LogWarning("hub sweep: {Msg}", ex.Message);
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        string peer = client.Client.RemoteEndPoint?.ToString() ?? "?";
        _log.LogInformation("hub: {Peer} connected", peer);
        var buffer = new List<byte>();
        var chunk = new byte[8192];
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                while (!_stop.IsCancellationRequested)
                {
                    int read = await stream.ReadAsync(chunk, _stop.Token).ConfigureAwait(false);
                    if (read <= 0) break;
                    buffer.AddRange(chunk.AsSpan(0, read).ToArray());
                    while (true)
                    {
                        byte[]? frame;
                        int consumed;
                        try { frame = HubProtocol.ReadFrame(buffer.ToArray(), out consumed); }
                        catch (InvalidDataException ex)
                        {
                            _log.LogWarning("hub: {Peer} sent a bad frame - {Msg}; dropping the connection", peer, ex.Message);
                            return;
                        }
                        if (frame is null) break;
                        buffer.RemoveRange(0, consumed);
                        foreach (var reply in Handle(frame))
                            await stream.WriteAsync(reply, _stop.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
            or ObjectDisposedException)
        {
            // A hub client reconnects every ten seconds; a dropped socket is normal.
        }
        catch (Exception ex)
        {
            _log.LogWarning("hub: {Peer} - {Msg}", peer, ex.Message);
        }
        _log.LogInformation("hub: {Peer} disconnected", peer);
    }

    // ---------------------------------------------------------------------------- dispatch

    /// <summary>
    /// One received frame in - size prefix included, exactly as it arrives on the wire and as
    /// <see cref="HubProtocol.Frame"/> writes it - and the frames to write back out. Pure apart
    /// from the store and the hooks, which is what lets the tests drive the whole path with no
    /// socket.
    /// </summary>
    public IReadOnlyList<byte[]> Handle(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ushort msgId = HubProtocol.FrameId(frame);
        byte[] body = HubProtocol.FrameBody(frame);
        switch (msgId)
        {
            case HubProtocol.RegisterReq:
            {
                var fields = HubProtocol.Parse(body);
                uint serverId = (uint)HubProtocol.Scalar(fields, 1);
                _log.LogInformation("hub: register from {Category}:{Number}",
                    serverId >> 24, serverId & 0xFFFFFF);
                var ans = new HubProtocol.Writer().Bool(1, true)
                    .Fixed32(2, _ourGusid)
                    .Fixed32(2, HubProtocol.BoxApiGusid);
                return new[] { HubProtocol.Frame(HubProtocol.RegisterAns, ans.ToArray()) };
            }
            case HubProtocol.SendMessageReq:
            {
                var fields = HubProtocol.Parse(body);
                ulong jobId = HubProtocol.Scalar(fields, 1);
                uint target = (uint)HubProtocol.Scalar(fields, 2);
                byte[] msgBuf = HubProtocol.Bytes(fields, 3);
                var answer = Call(target, HubProtocol.InnerId(msgBuf), HubProtocol.InnerBody(msgBuf));
                var ack = new HubProtocol.Writer().Fixed64(1, jobId).Fixed32(2, target)
                    .Fixed32(3, answer is null ? 0u : 1u).Bool(4, answer is not null);
                var frames = new List<byte[]> { HubProtocol.Frame(HubProtocol.SendMessageAns, ack.ToArray()) };
                if (answer is { } reply)
                {
                    var recv = new HubProtocol.Writer().Fixed32(1, target).Fixed64(2, jobId)
                        .Bytes(3, HubProtocol.Inner(reply.AnsId, reply.Body));
                    frames.Add(HubProtocol.Frame(HubProtocol.RecvMessageReq, recv.ToArray()));
                }
                return frames;
            }
            case HubProtocol.PingAns:
                return Array.Empty<byte[]>();
            default:
                _log.LogWarning("hub: unhandled hub function {Id} ({Len} B)", msgId, body.Length);
                return Array.Empty<byte[]>();
        }
    }

    /// <summary>An answered call: the inner id to send it under, and its protobuf.</summary>
    public readonly record struct Answer(ushort AnsId, byte[] Body);

    /// <summary>
    /// One call on the OpArb channel (anything addressed to this server or to the box API) or on
    /// the OpUent channel (addressed to <see cref="HubProtocol.UserEntityGusid"/>). Null means
    /// refused, which tera-api sees as a failed SendMessageAns.
    /// </summary>
    public Answer? Call(uint target, ushort innerId, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (target == HubProtocol.UserEntityGusid) return UserEntityCall(innerId, body);
        switch (innerId)
        {
            case HubProtocol.OpMsg: return OpMsgCall(body);
            case HubProtocol.KickUserReq:
            {
                var fields = HubProtocol.Parse(body);
                long account = (long)HubProtocol.Scalar(fields, 1);
                int kicked = _hooks.Kick?.Invoke(account) ?? 0;
                _log.LogInformation("hub: kick account {Account} - {N} session(s)", account, kicked);
                return Ok(HubProtocol.KickUserAns);
            }
            case HubProtocol.SendMsgReq:
            {
                var fields = HubProtocol.Parse(body);
                long account = (long)HubProtocol.Scalar(fields, 1);
                string text = Encoding.Unicode.GetString(HubProtocol.Bytes(fields, 2)).TrimEnd('\0');
                _hooks.Notify?.Invoke(account, text);
                _log.LogInformation("hub: message to account {Account}: {Text}", account, text);
                return Ok(HubProtocol.SendMsgAns);
            }
            case HubProtocol.BulkKickReq:
                _log.LogInformation("hub: bulk kick asked for - ignored, TeraSharp has no maintenance kick");
                return Ok(HubProtocol.BulkKickAns);
            case HubProtocol.BoxNotiUserReq:
            {
                var fields = HubProtocol.Parse(body);
                long account = (long)HubProtocol.Scalar(fields, 1);
                int delivered = BoxDelivery.DeliverPending(_store, account, _log);
                if (delivered > 0 && (_hooks.IsOnline?.Invoke(account) ?? false))
                    _hooks.Notify?.Invoke(account, NoticeText);
                _log.LogInformation("hub: box notice for account {Account} - {N} box(es) delivered",
                    account, delivered);
                return Ok(HubProtocol.BoxNotiUserAns);
            }
            case HubProtocol.AddBenefitReq:
            {
                var fields = HubProtocol.Parse(body);
                long account = (long)HubProtocol.Scalar(fields, 1);
                int benefit = (int)HubProtocol.Scalar(fields, 2);
                long remain = (long)HubProtocol.Scalar(fields, 3);
                if (!Grant(account, benefit, remain)) return null;
                return Ok(HubProtocol.AddBenefitAns);
            }
            case HubProtocol.RemoveBenefitReq:
            {
                var fields = HubProtocol.Parse(body);
                long account = (long)HubProtocol.Scalar(fields, 1);
                int benefit = (int)HubProtocol.Scalar(fields, 2);
                bool gone = _store.RevokeAccountBenefit(account, benefit);
                if (gone) _hooks.BenefitsChanged?.Invoke(account);
                _log.LogInformation("hub: benefit {Benefit} removed from account {Account} ({Gone})",
                    benefit, account, gone);
                return Ok(HubProtocol.RemoveBenefitAns);
            }
            default:
                _log.LogWarning("hub: unhandled OpArb call {Id} for {Target:X8} ({Len} B)",
                    innerId, target, body.Length);
                return null;
        }
    }

    /// <summary>The line an online buyer sees. Plain text, the way an admin announce is.</summary>
    public const string NoticeText = "Your shop purchase is in your mailbox.";

    /// <summary>
    /// Elite and any other benefit: the same rows S_ACCOUNT_BENEFIT_LIST is built from. An unknown
    /// account is refused rather than inserted, because account_benefits REFERENCES accounts(id)
    /// and a FOREIGN KEY failure here would take the hub connection down with it.
    /// </summary>
    private bool Grant(long account, int benefit, long remainSeconds)
    {
        if (benefit <= 0 || _store.GetAccountById(account) is null)
        {
            _log.LogWarning("hub: benefit {Benefit} for unknown account {Account} - refused", benefit, account);
            return false;
        }
        long expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(0, remainSeconds);
        _store.GrantAccountBenefit(account, benefit, expires);
        _hooks.BenefitsChanged?.Invoke(account);
        _log.LogInformation("hub: benefit {Benefit} on account {Account} for {Seconds}s",
            benefit, account, remainSeconds);
        return true;
    }

    /// <summary>
    /// T207c. The three OpUent calls tera-api's hubFunctions.js can make, all answered or refused
    /// by name - none of them reaches the unhandled warning any more.
    ///
    /// <list type="table">
    /// <item><term>1 QueryUser</term><description>answered: is this account on this server.</description></item>
    /// <item><term>3 GetServerStat</term><description>answered below. This is the 0-byte call
    /// arriving every 10 s (arbiter-bg3.log 20:47:30 onwards, "unhandled OpUent call 3 (0 B)") -
    /// <c>GetServerStatReq</c> has no fields, so it serialises to nothing.</description></item>
    /// <item><term>5 GetAllServerStat</term><description>refused by name, see below.</description></item>
    /// </list>
    /// </summary>
    private Answer? UserEntityCall(ushort innerId, byte[] body)
    {
        switch (innerId)
        {
            case HubProtocol.QueryUserReq:
            {
                var fields = HubProtocol.Parse(body);
                ulong account = HubProtocol.Scalar(fields, 1);
                bool online = _hooks.IsOnline?.Invoke((long)account) ?? false;
                var ans = new HubProtocol.Writer().Fixed64(1, account).Fixed32(2, online ? _ourGusid : 0u);
                return new(HubProtocol.QueryUserAns, ans.ToArray());
            }
            case HubProtocol.GetServerStatReq:
                return GetServerStat();
            case HubProtocol.GetAllServerStatReq:
                // GetAllServerStatAns.ServerInfo is { serverId, lastMsg, ip, port } for every
                // server in the PLATFORM (opUent.js). TeraSharp is one server and keeps no
                // platform registry, so there is nothing honest to put in it - and tera-api reads
                // the login ip and port out of its own server_info table anyway. Refused, by name,
                // at Information: tera-api catches it as a HubError and carries on.
                _log.LogInformation("hub: GetAllServerStat (OpUent 5) refused - TeraSharp serves one "
                    + "server and has no platform registry to enumerate");
                return null;
            default:
                _log.LogWarning("hub: unhandled OpUent call {Id} ({Len} B)", innerId, body.Length);
                return null;
        }
    }

    /// <summary>
    /// <c>GetServerStatAns</c>: one <c>ServerInfo { fixed32 serverId = 1, fixed32 userCnt = 2 }</c>
    /// per live server, repeated at field 1 (opUent.js GetServerStatAns.ServerInfo.encode).
    ///
    /// <para>What tera-api does with it is the availability poll, not the Online page:
    /// <c>ServerCheckActions.all</c> (src/actions/serverCheck.actions.js) matches
    /// <c>stat.serverList.find(s =&gt; s.serverId == server.get("serverId"))</c> against its own
    /// <c>server_info</c> rows and marks the server available ("method: Hub") when it is there,
    /// falling back to a TCP probe of loginPort when it is not. So <c>serverId</c> here is the
    /// PLAIN server number tera-api stores - <see cref="ServerIdVariable"/>, 2800 by default - not
    /// a gusid. The Online page uses kickUser / bulkKick and its own database, not this call.</para>
    ///
    /// <para><c>userCnt</c> is the number of distinct accounts in world, which is what "users"
    /// means on that panel; the message has no room for account ids.</para>
    /// </summary>
    private Answer GetServerStat()
    {
        var accounts = OnlineAccountIds();
        var info = new HubProtocol.Writer().Fixed32(1, (uint)_serverId).Fixed32(2, (uint)accounts.Count);
        var ans = new HubProtocol.Writer().Bytes(1, info.ToArray());
        if (!_statAnswered)
        {
            _statAnswered = true;
            _log.LogInformation("hub: GetServerStat answered - server {Server} is now reported "
                + "available through the hub instead of a port probe", _serverId);
        }
        _log.LogDebug("hub: GetServerStat -> server {Server}, {Count} user(s)", _serverId, accounts.Count);
        return new(HubProtocol.GetServerStatAns, ans.ToArray());
    }

    /// <summary>
    /// The accounts with a session in world. The hook wins; with none, the live World is read
    /// directly so Program has nothing to wire (Program.cs is not ours to edit).
    /// </summary>
    private static IEnumerable<long> AccountsInWorld()
    {
        foreach (var s in Program.World?.InWorldSessions() ?? new List<Network.GameSession>())
            yield return (long)s.Account.AccountId;
    }

    /// <summary>
    /// Distinct, whichever side supplies them: two characters of one account are one user, and
    /// the de-duplication belongs here so the count means the same thing for the hook and for the
    /// live World.
    /// </summary>
    private IReadOnlyList<long> OnlineAccountIds()
    {
        var ids = new List<long>();
        foreach (long id in _hooks.OnlineAccounts is { } hook ? hook() : AccountsInWorld())
            if (!ids.Contains(id)) ids.Add(id);
        return ids;
    }

    /// <summary>An Ans whose only field is the result, which is 0 for success (FAILED is 1).</summary>
    private static Answer Ok(ushort ansId) => new(ansId, new HubProtocol.Writer().Varint(1, 0).ToArray());

    // ---------------------------------------------------------------------------- box API

    private Answer? OpMsgCall(byte[] body)
    {
        var (gufid, sender, args) = HubProtocol.ParseOpMsg(body);
        int function = HubProtocol.GusidNumber(gufid);
        switch (function)
        {
            case HubProtocol.FnCreateServiceItem:
            {
                int template = (int)HubProtocol.Number(args, "serviceItemMappingItemSN");
                if (template <= 0) return Fail(gufid, "CreateServiceItem without a template id");
                long sn = _store.CreateHubServiceItem(template,
                    HubProtocol.Text(args, "serviceItemName"),
                    HubProtocol.Text(args, "serviceItemDescription"),
                    HubProtocol.Text(args, "serviceItemEnableFlag") != "0",
                    HubProtocol.Number(args, "serviceItemRegisterUserSN"));
                _log.LogInformation("hub: service item {Sn} = template {Template}", sn, template);
                return new(HubProtocol.OpMsg, HubProtocol.BuildOpMsgAns(gufid,
                    sn.ToString(CultureInfo.InvariantCulture)));
            }
            case HubProtocol.FnGetServiceItem:
            {
                long sn = HubProtocol.Number(args, "serviceItemSN");
                var row = sn > 0 ? _store.GetHubServiceItem(sn) : null;
                return new(HubProtocol.OpMsg, HubProtocol.BuildOpMsgAns(gufid, null, ServiceItemColumns,
                    row is null ? Array.Empty<IReadOnlyList<string>>() : new[] { ServiceItemRow(row) }));
            }
            case HubProtocol.FnGetPageServiceItem:
            {
                int template = (int)HubProtocol.Number(args, "serviceItemMappingItemSN");
                var rows = new List<IReadOnlyList<string>>();
                foreach (var row in _store.PageHubServiceItems(template,
                    (int)HubProtocol.Number(args, "offset"), (int)HubProtocol.Number(args, "count", 10)))
                    rows.Add(ServiceItemRow(row));
                return new(HubProtocol.OpMsg, HubProtocol.BuildOpMsgAns(gufid, null, ServiceItemColumns, rows));
            }
            case HubProtocol.FnSetDisableServiceItem:
            {
                long sn = HubProtocol.Number(args, "serviceItemSN");
                bool done = sn > 0 && _store.DisableHubServiceItem(sn);
                return done
                    ? new(HubProtocol.OpMsg, HubProtocol.BuildOpMsgAns(gufid, sn.ToString(CultureInfo.InvariantCulture)))
                    : Fail(gufid, "SetDisableServiceItem for unknown service item " + sn);
            }
            case HubProtocol.FnCreateBox:
                return CreateBox(gufid, args);
            default:
                return Fail(gufid, "box function " + function + " from " + (sender >> 24) + " is not implemented");
        }
    }

    private static readonly string[] ServiceItemColumns =
    {
        "serviceItemSN", "serviceItemMappingItemSN", "serviceItemEnableFlag", "serviceItemName",
    };

    private static IReadOnlyList<string> ServiceItemRow(CharacterStore.HubServiceItemRow row) => new[]
    {
        row.ServiceItemSn.ToString(CultureInfo.InvariantCulture),
        row.TemplateId.ToString(CultureInfo.InvariantCulture),
        row.Enabled ? "1" : "0",
        row.Name,
    };

    /// <summary>
    /// CreateBox: the receiving account, the lines (each naming a service item and a count) and
    /// the three tags tera-api fills - 1 content, 2 title, 3 icon. The box is stored first and
    /// delivered second, so a delivery that cannot happen yet (no character) is not lost.
    /// </summary>
    private Answer? CreateBox(uint gufid, Dictionary<string, string> args)
    {
        long account = HubProtocol.Number(args, "receiverUserSN");
        if (account <= 0) return Fail(gufid, "CreateBox without receiverUserSN");

        var tags = HubProtocol.ParseBoxTags(HubProtocol.Text(args, "boxTagInfo"));
        var lines = HubProtocol.ParseBoxItems(HubProtocol.Text(args, "boxServiceItemInfo"));
        if (lines.Count == 0) return Fail(gufid, "CreateBox for account " + account + " with no items");

        var items = new List<CharacterStore.HubBoxItemRow>();
        int slot = 0, unknown = 0;
        foreach (var line in lines)
        {
            var serviceItem = _store.GetHubServiceItem(line.ServiceItemSn);
            if (serviceItem is null) { unknown++; continue; }
            items.Add(new(0, slot++, line.ServiceItemSn, serviceItem.TemplateId, line.Count));
        }
        if (items.Count == 0) return Fail(gufid, "CreateBox named only unknown service items");
        if (unknown > 0)
            _log.LogWarning("hub: CreateBox for account {Account} named {N} unknown service item(s)",
                account, unknown);

        long boxSn = _store.CreateHubBox(account, (int)HubProtocol.Number(args, "receiverCharacterSN"),
            tags.TryGetValue(2, out string? title) ? title : string.Empty,
            tags.TryGetValue(1, out string? content) ? content : string.Empty,
            tags.TryGetValue(3, out string? icon) ? icon : string.Empty,
            HubProtocol.Timestamp(args, "startActivationDateTime"),
            HubProtocol.Timestamp(args, "endActivationDateTime"),
            HubProtocol.Text(args, "externalTransactionKey"), items);

        var box = _store.GetHubBox(boxSn);
        if (box is not null)
        {
            var result = BoxDelivery.Deliver(_store, box, _log);
            if (result.Delivered && (_hooks.IsOnline?.Invoke(account) ?? false))
                _hooks.Notify?.Invoke(account, NoticeText);
        }
        _log.LogInformation("hub: box {Box} for account {Account}, {N} item(s), title '{Title}'",
            boxSn, account, items.Count, title ?? string.Empty);
        return new(HubProtocol.OpMsg, HubProtocol.BuildOpMsgAns(gufid, boxSn.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// A refused box call: result number 1, which is what tera-api's opMsg() turns into a rejected
    /// promise, so the purchase stays in its queue and is retried instead of vanishing.
    /// </summary>
    private Answer Fail(uint gufid, string why)
    {
        _log.LogWarning("hub: {Why}", why);
        return new(HubProtocol.OpMsg, HubProtocol.BuildOpMsgAns(gufid, resultNumber: 1));
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch (Exception) { }
        try { _sweep?.Dispose(); } catch (Exception) { }
        try { _listener.Stop(); } catch (Exception) { }
        _stop.Dispose();
    }
}
