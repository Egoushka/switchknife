using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Switchknife.Knowledge.Approvals;

/// <summary>
/// The decision signature of the approvals contract: <c>HMAC-SHA256(key, decision|id|action|at|h)</c>, where <c>h</c> is the SHA-256 hex of the
/// edited fields as JSON with sorted keys and no spaces, or empty when nothing was edited. The JSON is written as <c>JSON.stringify</c> writes it,
/// so non-ASCII text hashes the same as on the shell. The host checks freshness of <c>at</c> and decisions seen before; this checks only the bytes.
/// </summary>
public static class ApprovalSignature
{
    /// <summary>The signature hex of <paramref name="decision"/>.</summary>
    /// <param name="key">The shared key, as text.</param>
    /// <param name="decision">The decision; its <see cref="ApprovalDecision.Sig"/> is ignored.</param>
    public static string Sign(string key, ApprovalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(decision);
        return Convert.ToHexStringLower(Mac(key, decision));
    }

    /// <summary>True when <see cref="ApprovalDecision.Sig"/> matches; false when it or any signed field is missing. Compares in constant time.</summary>
    public static bool Verify(string key, ApprovalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Decision is null || decision.Id is null || decision.Action is null || decision.At is null || decision.Sig is not { Length: 64 } sig)
        {
            return false;
        }

        byte[] given;
        try
        {
            given = Convert.FromHexString(sig);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(given, Mac(key, decision));
    }

    /// <summary>The edited fields as the shell hashes them; empty when there are none.</summary>
    public static string CanonicalEdited(IReadOnlyDictionary<string, string>? edited)
    {
        if (edited is null || edited.Count == 0)
        {
            return "";
        }

        var json = new StringBuilder("{");
        foreach (var key in edited.Keys.Order(StringComparer.Ordinal))
        {
            if (json.Length > 1)
            {
                json.Append(',');
            }

            Quote(json, key);
            json.Append(':');
            Quote(json, edited[key]);
        }

        return json.Append('}').ToString();
    }

    private static byte[] Mac(string key, ApprovalDecision d)
    {
        var edited = CanonicalEdited(d.Edited);
        var h = edited.Length == 0 ? "" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(edited)));
        var message = string.Join('|', d.Decision, d.Id, d.Action, d.At, h);
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message));
    }

    // JSON.stringify's string escaping: quote, backslash and control characters only; lone surrogates as \u escapes.
    private static void Quote(StringBuilder json, string text)
    {
        json.Append('"');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\b': json.Append("\\b"); break;
                case '\f': json.Append("\\f"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        json.Append(c).Append(text[++i]);
                    }
                    else if (c < ' ' || char.IsSurrogate(c))
                    {
                        json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        json.Append(c);
                    }

                    break;
            }
        }

        json.Append('"');
    }
}
