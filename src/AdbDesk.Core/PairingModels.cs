using System.Security.Cryptography;

namespace AdbDesk.Core;

public sealed class QrPairingSession
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    internal QrPairingSession(string serviceName, string pairingSecret, DateTimeOffset expiresAt)
    {
        ServiceName = serviceName;
        PairingSecret = pairingSecret;
        Payload = $"WIFI:T:ADB;S:{serviceName};P:{pairingSecret};;";
        ExpiresAt = expiresAt;
    }

    public string ServiceName { get; }
    public string Payload { get; }
    public DateTimeOffset ExpiresAt { get; }
    internal string PairingSecret { get; }

    internal static string CreateRandomText(int length)
    {
        var result = new char[length];
        for (var index = 0; index < result.Length; index++)
            result[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(result);
    }

    public override string ToString() => $"QR pairing session (expires {ExpiresAt:O})";
}

public record PairingResult(bool Paired, bool Connected, string Endpoint, string Message);
