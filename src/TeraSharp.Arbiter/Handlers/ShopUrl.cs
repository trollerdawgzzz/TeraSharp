// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;

namespace TeraSharp.Arbiter.Handlers;

// =============================================================================================
// ShopUrl - T207. Where the in-game shop button goes.
//
// 100.02 has no item-claim packet; the shop and the claim list are an embedded Awesomium web view.
// The client asks with C_SHOW_AWESOMIUMWEB_SHOP and the server answers S_SHOW_AWESOMIUMWEB_SHOP,
// whose shipped def is a single field - "string link" (tera_v100_MASTER_FINAL). That link is
// tera-api's own shop page, so a purchase is made in the client and delivered by the hub
// (Web/HubServer.cs).
//
// S_RESPONSE_SERVER_ADMINTOOL_AWESOMIUM_URL is deliberately NOT touched here. It is the GM
// tool's (Title, Url) pair, both strings were EMPTY in the capture where the Alt+A panel opened
// (T131/T132), and changing a packet that currently works to carry a shop URL it never carried is
// how a working panel stops working.
//
//   TERASHARP_SHOP_URL   the page; default tera-api's shop on the auth host, port 81
// =============================================================================================
public static class ShopUrl
{
    public const string UrlVariable = "TERASHARP_SHOP_URL";

    /// <summary>tera-api's portal serves the shop from its own root; 81 is its default port.</summary>
    public const string DefaultPath = "/tera/ShopMain";
    public const int DefaultPortalPort = 81;

    /// <summary>
    /// The configured page, or one derived from the auth URL's host - the machine tera-api runs on
    /// is the machine its portal runs on in every deployment this stack has seen, so a turnkey
    /// install needs no shop setting at all.
    /// </summary>
    public static string Address()
    {
        string configured = TerasConfig.Get(UrlVariable) ?? string.Empty;
        if (configured.Trim().Length > 0) return configured.Trim();

        string auth = TerasConfig.Get("TERASHARP_AUTH_URL") ?? string.Empty;
        string host = "127.0.0.1";
        if (Uri.TryCreate(auth, UriKind.Absolute, out var parsed) && parsed.Host.Length > 0) host = parsed.Host;
        return "http://" + host + ":" + DefaultPortalPort + DefaultPath;
    }

    /// <summary>
    /// The body of S_SHOW_AWESOMIUMWEB_SHOP built by hand: one string field, so the header is its
    /// u16 frame-relative offset and the text follows as NUL-terminated UTF-16LE. Built rather
    /// than sent by def so a test can pin the bytes without the def registry.
    /// </summary>
    public static byte[] BuildShowShop(string link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var text = System.Text.Encoding.Unicode.GetBytes(link);
        var body = new byte[2 + text.Length + 2];
        // Offsets in this protocol are frame-relative: 4 bytes of header plus this 2-byte field.
        BitConverter.GetBytes((ushort)(4 + 2)).CopyTo(body, 0);
        text.CopyTo(body, 2);
        return body;
    }

    /// <summary>
    /// C_SHOW_AWESOMIUMWEB_SHOP -&gt; S_SHOW_AWESOMIUMWEB_SHOP. The request carries nothing we need:
    /// which page to open is the server's decision, which is why retail keeps the URL in its own
    /// config and not in the packet.
    /// </summary>
    public static bool OnShowShop(GameSession session, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        string link = Address();
        session.SendRawBody("S_SHOW_AWESOMIUMWEB_SHOP", BuildShowShop(link));
        log?.LogInformation("shop: opened {Link}", link);
        return true;
    }
}
