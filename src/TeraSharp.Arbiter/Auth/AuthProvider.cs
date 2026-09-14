using Microsoft.Extensions.Logging;

namespace TeraSharp.Arbiter.Auth;

/// <summary>
/// One login attempt, as it arrives in C_LOGIN_ARBITER.
///
/// <para><paramref name="AccountName"/> is the packet's <c>name</c> string, which on this stack
/// is the tera-api <c>accountDBID</c> in decimal ("1", "2800", ...) - the launcher puts the
/// account id there, not a display name. <paramref name="Ticket"/> is the packet's <c>ticket</c>
/// byte array decoded as ASCII: a uuid v4 minted by tera-api's launcher endpoint
/// (<c>GetAuthKeyAction</c>) and stored on <c>account_info.authKey</c>.</para>
/// </summary>
public sealed record AuthRequest(
    string AccountName,
    long AccountId,
    string Ticket,
    string ClientIp,
    uint Region,
    int BuildVersion)
{
    /// <summary>
    /// The <c>userNo</c> tera-api wants: the numeric account id. The packet carries it twice -
    /// as the <c>name</c> string (what the launcher fills in) and as the AccountId field (0 in
    /// every capture we have) - so prefer the name when it parses as a number.
    /// </summary>
    public long UserNo =>
        long.TryParse(AccountName, out long fromName) && fromName > 0 ? fromName : AccountId;
}

/// <summary>The verdict. <paramref name="Code"/> is tera-api's ReturnCode where there was one.</summary>
public sealed record AuthResult(bool Accepted, int Code, string Message)
{
    public static AuthResult Ok { get; } = new(true, 0, "success");
    public static AuthResult Reject(int code, string message) => new(false, code, message);

    /// <summary>tera-api ReturnCodes, from src/controllers/arbiterAuth.controller.js.</summary>
    public const int CodeInternalError = 1;
    public const int CodeInvalidParameter = 2;
    public const int CodeAccountNotExist = 50000;
    public const int CodeAuthKeyMismatch = 50011;
    public const int CodeAccountBanned = 50012;
    /// <summary>Not a tera-api code: we could not reach or understand the API at all.</summary>
    public const int CodeUnreachable = -1;
    /// <summary>Not a tera-api code: the ticket was missing or malformed before any call.</summary>
    public const int CodeNoTicket = -2;
}

/// <summary>
/// Decides whether a C_LOGIN_ARBITER may proceed. The real Arbiter does not decide this itself
/// either: it hands the ticket to the hub (GwArb <c>UserLoginReq</c>) and waits for
/// <c>UserLoginAns</c> before it answers S_LOGIN_ARBITER - status/AUTH-DESIGN.md.
/// </summary>
public interface IAuthProvider
{
    /// <summary>A short name for logs.</summary>
    string Name { get; }

    Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct = default);
}

/// <summary>Today's behaviour: every account is accepted, no ticket needed. The default.</summary>
public sealed class AcceptAllAuthProvider : IAuthProvider
{
    public string Name => "accept-all";

    public Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct = default)
        => Task.FromResult(AuthResult.Ok);
}

/// <summary>Picks the provider from the environment and offers the blocking call the
/// packet handler needs (the dispatcher is synchronous).</summary>
public static class AuthProviders
{
    /// <summary>TERASHARP_AUTH=true|1 turns real validation on. Anything else is accept-all.</summary>
    public const string EnableVariable = "TERASHARP_AUTH";
    /// <summary>Base URL of tera-api's ARBITER API (its .env: API_ARBITER_LISTEN_PORT=8080).</summary>
    public const string UrlVariable = "TERASHARP_AUTH_URL";
    public const string DefaultUrl = "http://127.0.0.1:8080";

    /// <summary>True when the environment asks for real validation.</summary>
    public static bool EnabledFromEnvironment(string? value)
        => value is "true" or "True" or "TRUE" or "1";

    /// <summary>
    /// The provider this process should use: <see cref="TeraApiAuthProvider"/> when
    /// TERASHARP_AUTH is on, otherwise <see cref="AcceptAllAuthProvider"/>.
    /// </summary>
    public static IAuthProvider FromEnvironment(ILogger log)
    {
        bool enabled = EnabledFromEnvironment(Environment.GetEnvironmentVariable(EnableVariable));
        if (!enabled) return new AcceptAllAuthProvider();
        string url = Environment.GetEnvironmentVariable(UrlVariable) ?? DefaultUrl;
        return new TeraApiAuthProvider(url, log);
    }

    /// <summary>
    /// Blocking wrapper for the packet dispatcher. Never throws: an unexpected exception is a
    /// rejection, because an auth provider that fails open is not an auth provider.
    /// </summary>
    public static AuthResult Authenticate(IAuthProvider provider, AuthRequest request)
    {
        ArgumentNullException.ThrowIfNull(provider);
        // T45: the only place the client's language crosses from the (human-owned) login handler
        // into code Cowork owns. SocialHandlers reads it back to pick the seeded friend-group
        // name instead of shipping the TW capture's Chinese strings to every character.
        LoginLanguage.Record(request?.AccountName, request?.Region ?? LoginLanguage.Default);
        try
        {
            return provider.AuthenticateAsync(request).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return AuthResult.Reject(AuthResult.CodeUnreachable, ex.GetType().Name + ": " + ex.Message);
        }
    }
}
