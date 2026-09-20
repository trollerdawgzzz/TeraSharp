using Microsoft.Extensions.Logging;

namespace TeraSharp.Arbiter.Auth;

// =============================================================================================
// ApiGatewayToken - T124. The two fields that open the In-Game Operation Tool (Alt+A).
//
// T123 found the gate: S_LOGIN_ACCOUNT_INFO.3 (majorPatchVersion >= 100) carries
// apiServerAddress and apiServerAuthToken on top of the .2 def, and TeraSharp was sending
// "127.0.0.1" with no port and an EMPTY token. The tool is an embedded Awesomium web view; with
// no address and no credential it has nothing to open, which is why the panel never appeared
// even with S_LOGIN_ARBITER.status 33 and a byte-identical (and empty, on both sides)
// S_RESPONSE_SERVER_ADMINTOOL_AWESOMIUM_URL.
//
// WHAT THE REAL ARBITER SENDS (cap_final_gm_client2 frame 8, 544 bytes, the session where the
// panel opened):
//
//   dbServerName        "PlanetDB_2800"
//   apiServerAddress    "127.0.0.1:8800"        <- host AND port
//   apiServerAuthToken  a 231-char HS256 JWS:
//       header  {"alg":"HS256","typ":"JWS"}
//       payload {"accountDbId":1,"aud":"api","exp":1789619471,"iat":1789619351,
//                "iss":"arbiter","nbf":1789619351,"planetId":2800}
//
// Claims are in alphabetical key order, nbf == iat, and exp is iat + 120. "JWS" in typ, not
// "JWT" - that is what the capture says and it costs nothing to match it.
//
// NOTHING VERIFIES IT TODAY. tera-api has no jwt.verify anywhere in src, so the gateway accepts
// whatever arrives and the client only needs the fields populated. We still sign properly, with
// a configurable secret, so that turning verification on later is a config change and not a
// rewrite. The secret has NO hard-coded default on purpose: a signing key does not belong in
// source. Unset, this mints with a per-process random key - the token is well-formed and the
// panel opens, and it would fail a real check, which is the honest failure mode.
// =============================================================================================

/// <summary>The per-login credential the client hands to whatever serves apiServerAddress.</summary>
public static class ApiGatewayToken
{
    /// <summary>Host:port the client is told to load the Alt+A panel from. NOT the arbiter API
    /// that TERASHARP_AUTH uses, and (T132) NOT tera-api's gateway API either.</summary>
    public const string AddressVariable = "TERASHARP_API_GATEWAY";

    /// <summary>
    /// What the retail Arbiter sends, measured: <c>Executable\DeploymentConfig.xml</c> carries
    /// <c>&lt;APIServer ip=127.0.0.1 port=8800 protocol=http ttl=120 /&gt;</c> and the untouched
    /// <c>.orig</c> has the same 8800, so it is the vendor default, not this box's localisation.
    /// The <c>ttl=120</c> is the same 120 seconds as this token's <c>exp - iat</c>.
    /// <para>T124 used 8040 because that is tera-api's <c>API_GATEWAY_LISTEN_PORT</c>. T132 found
    /// that port is the wrong SERVICE - tera-api's own .env.example calls it the API "for
    /// receiving connections from the external website (like billing)".</para>
    /// </summary>
    public const string DefaultAddress = "127.0.0.1:8800";

    /// <summary>The name the client shows for the account database.</summary>
    public const string DbServerNameVariable = "TERASHARP_DB_SERVER_NAME";

    /// <summary>What the real Arbiter sends, and what tera-api's own rows call this planet.</summary>
    public const string DefaultDbServerName = "PlanetDB_2800";

    /// <summary>HS256 signing key. Set it to tera-api's <c>API_PORTAL_SECRET</c>.</summary>
    public const string SecretVariable = "TERASHARP_API_JWT_SECRET";

    /// <summary>Seconds the token is valid: exp - iat in the capture, exactly.</summary>
    public const int LifetimeSeconds = 120;

    /// <summary>The planet this Arbiter serves; the <c>planetId</c> claim.</summary>
    public const int PlanetId = 2800;

    /// <summary>Claim values that are literals in the capture.</summary>
    public const string Issuer = "arbiter";
    /// <inheritdoc cref="Issuer"/>
    public const string Audience = "api";

    /// <summary>The JOSE header, verbatim from the capture - note JWS, not JWT.</summary>
    public const string Header = "{\"alg\":\"HS256\",\"typ\":\"JWS\"}";

    private static readonly byte[] SessionSecret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// The configured address, or the box default. A value with no port is repaired rather than
    /// passed on: "127.0.0.1" is exactly what T123 found on the wire and exactly what does not
    /// work, so a config that forgets the port must not reproduce the bug.
    /// </summary>
    public static string Address(string? configured = null)
    {
        string v = configured ?? Environment.GetEnvironmentVariable(AddressVariable) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(v)) return DefaultAddress;
        v = v.Trim();
        // Strip a scheme if somebody pasted a URL; the field is host:port.
        int scheme = v.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) v = v[(scheme + 3)..];
        v = v.TrimEnd('/');
        if (!v.Contains(':')) v = v + DefaultAddress[DefaultAddress.IndexOf(':')..];
        return v;
    }

    /// <summary>The configured db server name, or what the real Arbiter sends.</summary>
    public static string DbServerName(string? configured = null)
    {
        string v = configured ?? Environment.GetEnvironmentVariable(DbServerNameVariable) ?? string.Empty;
        return string.IsNullOrWhiteSpace(v) ? DefaultDbServerName : v.Trim();
    }

    /// <summary>True when a signing key was configured; false means the session key is in use.</summary>
    public static bool HasConfiguredSecret
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SecretVariable));

    /// <summary>The signing key: the configured one, or this process's random one.</summary>
    public static byte[] Secret()
    {
        string? v = Environment.GetEnvironmentVariable(SecretVariable);
        return string.IsNullOrWhiteSpace(v) ? SessionSecret : System.Text.Encoding.UTF8.GetBytes(v);
    }

    /// <summary>
    /// <c>antiCheatChecksumSeed</c>. The capture's 619351 is the low six digits of its own
    /// <c>iat</c> (1789619351), which is one sample and not a proof - but it is stable for a
    /// login, never 0, and costs nothing, so it beats inventing a constant. 0 is what TeraSharp
    /// sent before and 0 is the one value the client could read as "no seed".
    /// </summary>
    public static int ChecksumSeed(long nowUnix)
    {
        int seed = (int)(Math.Abs(nowUnix) % 1_000_000L);
        return seed == 0 ? 1 : seed;
    }

    /// <summary>RFC 7515 base64url: no padding, - and _ for + and /.</summary>
    public static string Base64Url(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The claim set, alphabetically ordered exactly as the capture has it.</summary>
    public static string Payload(long accountDbId, long nowUnix, int planetId = PlanetId)
        => "{\"accountDbId\":" + accountDbId.ToString(System.Globalization.CultureInfo.InvariantCulture)
         + ",\"aud\":\"" + Audience + "\""
         + ",\"exp\":" + (nowUnix + LifetimeSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
         + ",\"iat\":" + nowUnix.ToString(System.Globalization.CultureInfo.InvariantCulture)
         + ",\"iss\":\"" + Issuer + "\""
         + ",\"nbf\":" + nowUnix.ToString(System.Globalization.CultureInfo.InvariantCulture)
         + ",\"planetId\":" + planetId.ToString(System.Globalization.CultureInfo.InvariantCulture)
         + "}";

    /// <summary>Sign <c>header.payload</c> with HS256 and return the whole compact serialization.</summary>
    public static string Mint(long accountDbId, long nowUnix, byte[] secret, int planetId = PlanetId)
    {
        ArgumentNullException.ThrowIfNull(secret);
        string signing = Base64Url(System.Text.Encoding.UTF8.GetBytes(Header))
                       + "." + Base64Url(System.Text.Encoding.UTF8.GetBytes(Payload(accountDbId, nowUnix, planetId)));
        using var mac = new System.Security.Cryptography.HMACSHA256(secret);
        return signing + "." + Base64Url(mac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(signing)));
    }

    /// <summary>As above, against the live environment.</summary>
    public static string Mint(long accountDbId, long nowUnix) => Mint(accountDbId, nowUnix, Secret());

    /// <summary>
    /// Check a token against a secret. Not used in production - nothing on this stack verifies -
    /// but the test needs it, and so will whoever turns verification on.
    /// </summary>
    public static bool Verify(string token, byte[] secret)
    {
        if (string.IsNullOrEmpty(token) || secret == null) return false;
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        using var mac = new System.Security.Cryptography.HMACSHA256(secret);
        string expected = Base64Url(mac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(parts[0] + "." + parts[1])));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(parts[2]));
    }

    /// <summary>Decode one segment of a compact JWS back to its UTF-8 text.</summary>
    public static string Decode(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        string s = segment.Replace('-', '+').Replace('_', '/');
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s.PadRight(s.Length + (3 - (s.Length + 3) % 4) % 4, '=')));
    }

    /// <summary>One line for the startup banner and --check-config.</summary>
    public static string DescribeMode()
        => "admin tool: gateway " + Address() + ", token signed with "
         + (HasConfiguredSecret ? SecretVariable : "a per-process key (" + SecretVariable + " is unset)");

    /// <summary>Warn once if the key is not configured - the panel opens either way.</summary>
    public static void LogMode(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (HasConfiguredSecret) log.LogInformation("{Mode}", DescribeMode());
        else log.LogWarning("{Mode}. Nothing on this stack verifies it, so Alt+A still opens; set it to tera-api's API_PORTAL_SECRET before anyone enables verification.", DescribeMode());
    }
}
