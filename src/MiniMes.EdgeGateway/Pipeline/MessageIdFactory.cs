using System.Security.Cryptography;
using System.Text;

namespace MiniMes.EdgeGateway.Pipeline;

// Spec M4b 2.6, rule G6: the msgId comes from the content, not from Guid.NewGuid(). The same reading (OPC UA republishes
// old notifications after a reconnect; the gateway resends after a crash) gets the same msgId, and the MES (M4) drops
// the copy. "At least once" on the wire + idempotent MES = "exactly once" in the database.
public static class MessageIdFactory
{
    public static Guid Create(string machine, string tag, DateTime sourceTsUtc, string valueText)
    {
        // "O" = round-trip ISO 8601 with 7 decimals: two readings 1 ms apart get two ids.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{machine}|{tag}|{sourceTsUtc.ToUniversalTime():O}|{valueText}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
