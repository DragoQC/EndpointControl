using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MeshAll.Server.Services;

// RFC 6238, interoperable with standard six-digit authenticator applications.
public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret() => Encode(RandomNumberGenerator.GetBytes(20));

    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        StringBuilder result = new StringBuilder();
        int bits = 0;
        int value = 0;
        foreach (byte b in bytes)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                result.Append(Alphabet[(value >> bits) & 31]);
            }
        }
        if (bits > 0)
        {
            result.Append(Alphabet[(value << (5 - bits)) & 31]);
        }

        return result.ToString();
    }
    private static byte[] Decode(string secret)
    {
        List<byte> result = new List<byte>();
        int bits = 0;
        int value = 0;
        foreach (char c in secret)
        {
            int index = Alphabet.IndexOf(c);
            if (index < 0)
            {
                throw new FormatException("Invalid authenticator key.");
            }

            value = (value << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                result.Add((byte)(value >> bits));
            }
        }
        return result.ToArray();
    }
    public static string Code(string secret, long step, int digits = 6)
    {
        if (digits is not (6 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(digits));
        }

        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        byte[] hash = HMACSHA1.HashData(Decode(secret), counter);
        int offset = hash[^1] & 15;
        uint number = BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
        return (number % (digits == 6 ? 1_000_000u : 100_000_000u)).ToString(new string('0', digits), CultureInfo.InvariantCulture);
    }
    public static long? Match(string secret, string input, DateTimeOffset now, long lastStep = -1)
    {
        input = input.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (input.Length != 6 || input.Any(c => c is < '0' or > '9'))
        {
            return null;
        }

        long step = now.ToUnixTimeSeconds() / 30;
        foreach (long candidate in new[] {
            step,
            step - 1,
            step + 1
        })
        {
            if (candidate > lastStep && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(input), Encoding.ASCII.GetBytes(Code(secret, candidate))))
            {
                return candidate;
            }
        }

        return null;
    }
}
