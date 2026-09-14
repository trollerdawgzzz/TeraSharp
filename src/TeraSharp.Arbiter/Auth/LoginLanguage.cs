namespace TeraSharp.Arbiter.Auth;

/// <summary>
/// The <c>language</c> field of the last <c>C_LOGIN_ARBITER</c> for an account — T45.
///
/// <para><b>Why it lives here.</b> The value arrives in <c>LoginHandlers.OnLoginArbiter</c>, which
/// is human-owned, and it is needed in <c>SocialHandlers</c>, which is not. The one place both
/// sides already meet is the auth gate: <c>LoginHandlers</c> hands every login to
/// <see cref="AuthProviders.Authenticate"/> inside an <see cref="AuthRequest"/> whose
/// <c>Region</c> IS that field. Recording it there needs no change to any human-owned file.</para>
///
/// <para><b>What the field is and is not.</b> The shipped <c>C_LOGIN_ARBITER.2.def</c> documents
/// the values, and they are worth quoting because the obvious reading of them is wrong:
/// <c>0 = INT, 1 = KOR, 2 = USA, 3 = JPN, 4 = GER, 5 = FRA, 6 = EUR, 7 = TW, 8 = RUS</c>.
/// It is the <b>client's</b> language, not the server's. <c>cap_newchar_client.log</c> frame 2 —
/// the login that produced the Chinese strings this table exists to stop shipping — carries
/// <c>language = 6</c>, EUR. The real Arbiter picked those strings out of its own installed
/// StrSheet, not out of this field. We have no StrSheet, so this field is the only signal there
/// is; the table in <c>SocialHandlers</c> keys off it and defaults to English.</para>
/// </summary>
public static class LoginLanguage
{
    public const uint Int = 0, Kor = 1, Usa = 2, Jpn = 3, Ger = 4, Fra = 5, Eur = 6, Twn = 7, Rus = 8;

    /// <summary>What an account gets when we have never seen its login. The client this server
    /// is actually played with sends 6.</summary>
    public const uint Default = Eur;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, uint> _byAccount =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remember the language this account logged in with.</summary>
    public static void Record(string? accountName, uint language)
    {
        if (string.IsNullOrEmpty(accountName)) return;
        _byAccount[accountName] = language;
    }

    /// <summary>The language for an account, or <see cref="Default"/>.</summary>
    public static uint For(string? accountName)
        => !string.IsNullOrEmpty(accountName) && _byAccount.TryGetValue(accountName, out uint l)
            ? l : Default;

    /// <summary>Drop everything. Tests only.</summary>
    public static void Clear() => _byAccount.Clear();
}
