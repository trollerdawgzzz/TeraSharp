// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TeraSharp.Arbiter.Auth;

/// <summary>
/// Validates the C_LOGIN_ARBITER ticket against tera-api, by calling the same endpoint the hub
/// calls on a real stack:
///
/// <code>
/// POST {baseUrl}/authApi/GameAuthenticationLogin
/// { "authKey": "&lt;uuid from the packet&gt;", "clientIP": "127.0.0.1", "userNo": "1" }
/// -> { "Return": true,  "ReturnCode": 0, "Msg": "success" }
/// -> { "Return": false, "ReturnCode": 50011, "Msg": "authkey mismatch" }
/// </code>
///
/// <para>Source of truth: tera-api <c>src/controllers/arbiterAuth.controller.js</c>
/// (<c>GameAuthenticationLogin</c>) mounted at <c>/authApi</c> by
/// <c>src/routes/arbiter.index.js</c> on the Arbiter API server, whose port is
/// <c>API_ARBITER_LISTEN_PORT</c> (8080 in the human's .env). It looks the account up by
/// <c>userNo</c> = accountDBID, compares <c>authKey</c>, then checks the ban tables.</para>
///
/// <para><b>Fail closed.</b> A timeout, a non-200, an unparseable body or a missing ticket all
/// reject the login. An auth provider that fails open is not an auth provider - and TeraSharp
/// defaults to <see cref="AcceptAllAuthProvider"/> anyway, so anyone who turned this on meant it.</para>
/// </summary>
public sealed class TeraApiAuthProvider : IAuthProvider
{
    /// <summary>The path under the tera-api ARBITER API root.</summary>
    public const string Endpoint = "/authApi/GameAuthenticationLogin";

    private readonly HttpClient _http;
    private readonly ILogger? _log;
    private readonly string _baseUrl;

    public string Name => "tera-api";

    public TeraApiAuthProvider(string baseUrl, ILogger? log = null, HttpClient? http = null)
    {
        _baseUrl = (baseUrl ?? AuthProviders.DefaultUrl).TrimEnd('/');
        _log = log;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(request.Ticket))
            return AuthResult.Reject(AuthResult.CodeNoTicket, "no ticket in C_LOGIN_ARBITER");
        if (request.UserNo <= 0)
            return AuthResult.Reject(AuthResult.CodeNoTicket,
                $"account name '{request.AccountName}' is not a tera-api accountDBID");

        string body = BuildRequestBody(request);
        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(_baseUrl + Endpoint, content, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("auth: tera-api unreachable at {Url}: {Msg}", _baseUrl + Endpoint, ex.Message);
            return AuthResult.Reject(AuthResult.CodeUnreachable, "tera-api unreachable: " + ex.Message);
        }

        using (response)
        {
            string payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log?.LogWarning("auth: tera-api answered {Status} for userNo {No}",
                    (int)response.StatusCode, request.UserNo);
                return AuthResult.Reject(AuthResult.CodeUnreachable,
                    $"tera-api HTTP {(int)response.StatusCode}");
            }
            return ParseResponse(payload);
        }
    }

    /// <summary>
    /// The request body. <c>userNo</c> goes out as a STRING: tera-api validates it with
    /// express-validator's <c>isNumeric()</c>, which accepts a numeric string, and every other
    /// field on this API is a string too.
    /// </summary>
    public static string BuildRequestBody(AuthRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = new Dictionary<string, object>
        {
            ["authKey"] = request.Ticket,
            ["clientIP"] = string.IsNullOrEmpty(request.ClientIp) ? "127.0.0.1" : request.ClientIp,
            ["userNo"] = request.UserNo.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Read <c>{ Return, ReturnCode, Msg }</c>. Anything that is not an explicit
    /// <c>Return: true</c> is a rejection, including a body we cannot parse.
    /// </summary>
    public static AuthResult ParseResponse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return AuthResult.Reject(AuthResult.CodeUnreachable, "empty response");

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            // A JSON array or a bare scalar is still valid JSON, and TryGetProperty throws on
            // one - a reverse proxy that answers [] must reject the login, not crash the call.
            if (root.ValueKind != JsonValueKind.Object)
                return AuthResult.Reject(AuthResult.CodeUnreachable,
                    "tera-api sent a " + root.ValueKind + ", not an object");
            bool ok = root.TryGetProperty("Return", out var ret)
                      && ret.ValueKind == JsonValueKind.True;
            int code = root.TryGetProperty("ReturnCode", out var rc) && rc.TryGetInt32(out int c) ? c : 0;
            string msg = root.TryGetProperty("Msg", out var m) ? m.GetString() ?? "" : "";
            return ok ? AuthResult.Ok : AuthResult.Reject(code, msg.Length > 0 ? msg : Explain(code));
        }
        catch (JsonException)
        {
            return AuthResult.Reject(AuthResult.CodeUnreachable, "tera-api sent a non-JSON body");
        }
    }

    /// <summary>tera-api's ReturnCodes in words, for the log line.</summary>
    public static string Explain(int code) => code switch
    {
        AuthResult.CodeAccountNotExist => "account does not exist",
        AuthResult.CodeAuthKeyMismatch => "authkey mismatch (stale launcher ticket)",
        AuthResult.CodeAccountBanned => "account banned",
        AuthResult.CodeInvalidParameter => "invalid parameter",
        AuthResult.CodeInternalError => "tera-api internal error",
        AuthResult.CodeNoTicket => "no usable ticket",
        AuthResult.CodeUnreachable => "tera-api unreachable",
        _ => "rejected (code " + code + ")",
    };
}
