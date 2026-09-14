using System.Text;

namespace TeraSharp.Arbiter.Auth;

/// <summary>
/// The C_LOGIN_ARBITER ticket. The def calls it <c>bytes ticket</c> and that is exactly right:
/// the Arbiter reads it with the byte-array reader (position AND length fields), not the
/// wide-string one, and hands the raw bytes to AuthManager::ReqAuthentication as
/// <c>const unsigned char* + int</c>. So it is 36 ASCII characters on the wire, NOT UTF-16 -
/// cap_newchar_client.log frame 2 carries
/// <c>34 35 38 32 34 65 34 35 2D ...</c> = "45824e45-4757-4c6d-8285-37ac8ce5eae4".
/// </summary>
public static class AuthTicket
{
    /// <summary>A uuid v4 in canonical form: 8-4-4-4-12 hex digits.</summary>
    public const int CanonicalLength = 36;

    /// <summary>
    /// Decode the packet field. Accepts what the reader can hand us: a byte[] (the normal case),
    /// or a string if a def ever declares it as one. Trailing NULs and whitespace are trimmed.
    /// Returns an empty string when there is nothing usable.
    /// </summary>
    public static string Decode(object? field)
    {
        switch (field)
        {
            case null: return "";
            case byte[] bytes:
            {
                if (bytes.Length == 0) return "";
                // Defensive: a UTF-16 encoding of an ASCII uuid is every other byte zero.
                bool looksUtf16 = bytes.Length >= 4 && bytes.Length % 2 == 0 && bytes[1] == 0 && bytes[3] == 0;
                string s = looksUtf16 ? Encoding.Unicode.GetString(bytes) : Encoding.ASCII.GetString(bytes);
                return s.TrimEnd('\0').Trim();
            }
            case string s2: return s2.TrimEnd('\0').Trim();
            default: return "";
        }
    }

    /// <summary>
    /// True when the ticket looks like the uuid v4 tera-api mints (<c>uuid()</c> in
    /// portalLauncher.controller.js). We only shape-check: the authority is tera-api, which
    /// compares it to <c>account_info.authKey</c>.
    /// </summary>
    public static bool LooksCanonical(string ticket)
    {
        if (ticket is null || ticket.Length != CanonicalLength) return false;
        for (int i = 0; i < ticket.Length; i++)
        {
            char c = ticket[i];
            bool dash = i is 8 or 13 or 18 or 23;
            if (dash) { if (c != '-') return false; continue; }
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }
}
