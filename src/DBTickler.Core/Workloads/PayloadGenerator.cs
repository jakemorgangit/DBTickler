namespace DBTickler.Core.Workloads;

/// <summary>
/// Produces the random text and binary blobs that write operations ship to the server.
///
/// The payload is sliced from a large random buffer built once at construction, so
/// generating a row costs a single memory copy. Building payloads character by character
/// instead — as v1 did, at roughly 200,000 StringBuilder appends per row at large batch
/// sizes — makes the client's CPU the bottleneck, and a load generator that is slower than
/// the server it is testing measures itself rather than the server.
/// </summary>
public sealed class PayloadGenerator
{
    private const int MinimumPoolSize = 256 * 1024;

    private static readonly char[] Alphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,;:!?-_()[]{}".ToCharArray();

    private readonly char[] _charPool;
    private readonly byte[] _bytePool;

    public PayloadGenerator(int maxTextChars, int maxBinaryBytes, int seed)
    {
        MaxTextChars = Math.Max(0, maxTextChars);
        MaxBinaryBytes = Math.Max(0, maxBinaryBytes);

        var random = new Random(seed);

        var charPoolSize = Math.Max(MinimumPoolSize, MaxTextChars * 2);
        _charPool = new char[charPoolSize];
        for (var i = 0; i < charPoolSize; i++)
            _charPool[i] = Alphabet[random.Next(Alphabet.Length)];

        var bytePoolSize = Math.Max(MinimumPoolSize, MaxBinaryBytes * 2);
        _bytePool = new byte[bytePoolSize];
        random.NextBytes(_bytePool);
    }

    public int MaxTextChars { get; }
    public int MaxBinaryBytes { get; }

    /// <summary>Approximate bytes on the wire for one row at the configured sizes.</summary>
    public int ApproximateRowBytes => MaxTextChars * 2 + MaxBinaryBytes;

    public string NextText(Random random, int? chars = null)
    {
        var length = Math.Clamp(chars ?? MaxTextChars, 0, _charPool.Length);
        if (length == 0) return string.Empty;

        var offset = random.Next(_charPool.Length - length + 1);
        return new string(_charPool, offset, length);
    }

    public byte[] NextBytes(Random random, int? count = null)
    {
        var length = Math.Clamp(count ?? MaxBinaryBytes, 0, _bytePool.Length);
        if (length == 0) return [];

        var offset = random.Next(_bytePool.Length - length + 1);
        var result = new byte[length];
        Array.Copy(_bytePool, offset, result, 0, length);
        return result;
    }

    /// <summary>A short label written to the indexed column, so lookups have something selective to find.</summary>
    public static string NextTag(Random random) =>
        string.Create(16, random.NextInt64(), static (span, value) =>
        {
            const string Hex = "0123456789abcdef";
            for (var i = 0; i < span.Length; i++)
                span[i] = Hex[(int)((value >> (i * 4)) & 0xF)];
        });

    /// <summary>
    /// Splits a per-row byte budget between the text and binary columns so the total row size
    /// matches what the operator asked for rather than doubling it.
    /// </summary>
    public static PayloadGenerator ForRowBudget(int payloadBytes, int seed)
    {
        var budget = Math.Max(0, payloadBytes);
        var binaryBytes = budget / 2;
        var textChars = budget / 4; // NVARCHAR: two bytes per character.
        return new PayloadGenerator(textChars, binaryBytes, seed);
    }
}
