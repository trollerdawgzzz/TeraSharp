// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace TeraSharp.Arbiter.Web;

// =============================================================================================
// AdminAssets - T206. The admin UI's static files, compiled into the assembly.
//
// WHY EMBEDDED RESOURCES AND NOT A STRING CONSTANT. T106's page was one const string inside
// AdminServer.cs, which forced single-quoted HTML attributes so no double quote would appear in
// a verbatim string (CLAUDE.md's rule) and made a multi-file app impossible: ES modules need
// separate URLs to import each other. Real files under Web/wwwroot are embedded by the csproj
// with LogicalName "admin/<name>", so they are ordinary .html/.js/.css on disk - editable, no
// escaping - and still ship inside TeraSharp.Arbiter.dll with no build step and nothing to
// deploy. PS 5.1 deploy is unchanged because there is no new file to copy.
//
// FLAT ON PURPOSE. Every asset sits directly in Web/wwwroot with no subdirectory, so the
// csproj's LogicalName is %(Filename)%(Extension) and the resource name is identical on Windows
// and Linux. A views\ folder would embed as "admin/views\x.js" on Windows and "admin/views/x.js"
// on Linux, and the same build would serve different URLs on the two machines.
//
// NO TOKEN. Like T106's page, the assets are served before the token check: a browser typing
// http://127.0.0.1:8051/ has nowhere to put a header. They are a static shell that carries no
// data - every byte of anyone's account is behind /api/, which is still gated. The listener is
// bound to 127.0.0.1 and Serve() re-checks the peer, so serving them costs nothing.
// =============================================================================================

/// <summary>One static asset: its bytes as text, its content type and its ETag.</summary>
public sealed record AdminAsset(string Path, string ContentType, string Body, string ETag);

/// <summary>The embedded admin UI. Read once, on first ask, and then served from memory.</summary>
public static class AdminAssets
{
    /// <summary>The prefix every asset's LogicalName carries, set by the csproj.</summary>
    public const string ResourcePrefix = "admin/";

    /// <summary>What <c>GET /</c> serves.</summary>
    public const string IndexPath = "index.html";

    private static readonly object Gate = new();
    private static Dictionary<string, AdminAsset>? _assets;

    /// <summary>Every asset, keyed by its URL path with no leading slash.</summary>
    public static IReadOnlyDictionary<string, AdminAsset> All
    {
        get
        {
            lock (Gate) return _assets ??= Read(typeof(AdminAssets).Assembly);
        }
    }

    /// <summary>True when the UI was embedded. False on a build that stripped the resources,
    /// which is the one case where the tool has to say so rather than serve an empty page.</summary>
    public static bool Available => All.Count > 0;

    /// <summary>
    /// Resolve a request path to an asset, or null. Accepts <c>/</c> and <c>/index.html</c> for
    /// the shell; refuses anything with a path segment in it, so no URL can reach a resource
    /// this file did not choose to expose.
    /// </summary>
    public static AdminAsset? Find(string? path)
    {
        string name = (path ?? string.Empty).TrimStart('/');
        if (name.Length == 0) name = IndexPath;
        // Flat namespace: a slash or a dot-dot means someone is probing, not browsing.
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) return null;
        return All.TryGetValue(name, out var asset) ? asset : null;
    }

    /// <summary>For tests: forget what was read, so a later call reads again.</summary>
    public static void ResetForTests()
    {
        lock (Gate) _assets = null;
    }

    /// <summary>The content type for an extension. Only the four the UI actually uses, plus a
    /// text fallback - an admin tool has no business guessing at arbitrary media types.</summary>
    public static string ContentTypeFor(string name)
    {
        string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            _ => "text/plain; charset=utf-8",
        };
    }

    /// <summary>
    /// What <c>GET /</c> answers on a build whose resources were stripped. It says which files
    /// are missing rather than showing a blank page, because a blank page looks like the server
    /// is broken when in fact the assembly is.
    /// </summary>
    public const string MissingPage =
        "<!doctype html><meta charset='utf-8'><title>TeraSharp admin</title>"
        + "<body style='font:14px system-ui;padding:2rem'>"
        + "<h1>The admin UI is not in this build</h1>"
        + "<p>TeraSharp.Arbiter.dll carries no <code>admin/</code> resources. Rebuild with the"
        + " <code>Web/wwwroot</code> EmbeddedResource item group in TeraSharp.Arbiter.csproj.</p>"
        + "<p>The JSON API under <code>/api/</code> is unaffected and still answers with a token.</p>"
        + "</body>";

    private static Dictionary<string, AdminAsset> Read(Assembly assembly)
    {
        var found = new Dictionary<string, AdminAsset>(StringComparer.Ordinal);
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            string name = resource[ResourcePrefix.Length..];
            if (name.Length == 0) continue;
            try
            {
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream == null) continue;
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string body = reader.ReadToEnd();
                found[name] = new AdminAsset(name, ContentTypeFor(name), body, Tag(body));
            }
            catch
            {
                // A resource that will not read is a broken build, not a runtime error worth
                // taking the server down for: the page it belongs to simply 404s.
            }
        }
        return found;
    }

    /// <summary>A short strong ETag over the content, so a reload is one 304 per file.</summary>
    private static string Tag(string body)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(body));
        var sb = new StringBuilder("\"", 18);
        for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
        return sb.Append('"').ToString();
    }
}
