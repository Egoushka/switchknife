using System.Security.Cryptography;
using System.Text;

namespace Switchknife.Journal.Bench;

/// <summary>
/// The hash chain of ADR 0007 decision 3: <c>body_hash = SHA-256(body)</c>, then <c>hash = SHA-256</c> over a length-prefixed
/// canonical envelope that ends in <c>body_hash</c> and <c>prev_hash</c>, so a purged body leaves the chain verifiable.
/// </summary>
internal static class Chain
{
    public static readonly byte[] Genesis = new byte[32];

    public static byte[] BodyHash(string body) => SHA256.HashData(Encoding.UTF8.GetBytes(body));

    public static byte[] Link(string room, long roomSeq, DateTimeOffset ts, string actor, string kind, string[] refs, string undo, string sourceId, byte[] bodyHash, byte[] prevHash)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, room);
        Append(hash, roomSeq);
        Append(hash, ts.UtcTicks);
        Append(hash, actor);
        Append(hash, kind);
        Append(hash, refs.Length);
        foreach (var r in refs.Order(StringComparer.Ordinal))
        {
            Append(hash, r);
        }

        Append(hash, undo);
        Append(hash, sourceId);
        hash.AppendData(bodyHash);
        hash.AppendData(prevHash);
        return hash.GetHashAndReset();
    }

    /// <summary>The alternative the task text names: one pass over the whole body followed by the previous hash.</summary>
    public static byte[] BodyThenPrev(string body, byte[] prevHash)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(body));
        hash.AppendData(prevHash);
        return hash.GetHashAndReset();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BitConverter.TryWriteBytes(buffer, value);
        hash.AppendData(buffer);
    }
}
