// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Protocol;

namespace TeraSharp.Arbiter.Network;

/// <summary>
/// Async TCP accept loop. One <see cref="GameSession"/> per connection, each
/// pumped on its own receive loop. This is the Arbiter's client-facing listener
/// (original binds portForClient, 7701 in DeploymentConfig).
///
/// This port must sit BEHIND the GM proxy in production - only 7801 (the proxy)
/// faces the internet, 7701 stays localhost. Unchanged from the old server.
/// </summary>
public sealed class TcpServer
{
    private readonly IPEndPoint _endpoint;
    private readonly PacketDispatcher _dispatcher;
    private readonly OpcodeTable _opcodes;
    private readonly DefinitionRegistry _definitions;
    private readonly int _protocolVersion;
    private readonly int _majorPatchVersion;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TcpServer> _log;

    private Socket? _listener;

    public TcpServer(IPEndPoint endpoint, PacketDispatcher dispatcher, OpcodeTable opcodes,
        DefinitionRegistry definitions, int protocolVersion, int majorPatchVersion,
        ILoggerFactory loggerFactory)
    {
        _endpoint = endpoint;
        _dispatcher = dispatcher;
        _opcodes = opcodes;
        _definitions = definitions;
        _protocolVersion = protocolVersion;
        _majorPatchVersion = majorPatchVersion;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<TcpServer>();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _listener = new Socket(_endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(_endpoint);
        _listener.Listen(128);
        _log.LogInformation("Arbiter listening on {Endpoint}", _endpoint);

        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Accept failed");
                continue;
            }

            client.NoDelay = true;
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }

        _listener.Close();
    }

    private async Task HandleClientAsync(Socket socket, CancellationToken ct)
    {
        var sessionLog = _loggerFactory.CreateLogger<GameSession>();
        using var session = new GameSession(socket, _dispatcher, _opcodes, _definitions,
            _protocolVersion, _majorPatchVersion, sessionLog);

        var remote = socket.RemoteEndPoint;
        _log.LogInformation("Accepted connection from {Remote}", remote);

        try
        {
            session.Start();

            var buffer = new byte[8192];
            while (!ct.IsCancellationRequested)
            {
                int received = await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
                if (received == 0) break;
                session.OnReceive(buffer.AsSpan(0, received));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException ex)
        {
            _log.LogDebug("Connection from {Remote} dropped: {Msg}", remote, ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unhandled error for connection from {Remote}", remote);
        }
        finally
        {
            session.Close();
        }
    }
}
