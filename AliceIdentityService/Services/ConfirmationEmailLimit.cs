using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace AliceIdentityService.Services;

// Bounded, process-local recipient throttling; complements the HTTP IP limit.
// Entries survive failures so a broken transport cannot be hammered by resend.
public sealed class ConfirmationEmailLimit(TimeProvider clock) : IDisposable
{
    private sealed record Window(DateTimeOffset Started, DateTimeOffset Last, int Count);
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 10000 });
    private readonly object gate = new();

    public bool TryAcquire(string normalizedEmail)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail)));
        var now = clock.GetUtcNow();
        lock (gate)
        {
            cache.TryGetValue<Window>(key, out var window);
            if (window is not null && now - window.Started < TimeSpan.FromHours(1))
            {
                if (now - window.Last < TimeSpan.FromMinutes(1) || window.Count >= 5) return false;
            }
            else window = new(now, now, 0);
            cache.Set(key, window with { Last = now, Count = window.Count + 1 },
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1), Size = 1 });
            return true;
        }
    }

    public void Dispose() => cache.Dispose();
}
