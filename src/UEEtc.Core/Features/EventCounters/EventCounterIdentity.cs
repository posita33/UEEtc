using System.Security.Cryptography;
using System.Text;

namespace UEEtc.Core.Features.EventCounters;

internal static class EventCounterIdentity
{
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
