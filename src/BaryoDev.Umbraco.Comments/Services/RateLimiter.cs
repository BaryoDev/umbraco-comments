using System.Security.Cryptography;
using System.Text;

namespace BaryoDev.Umbraco.Comments.Services;

/// <summary>In-memory sliding window per client, at most 50,000 clients at a time. When it is full the least recently seen clients are dropped, so a new client is never refused for that reason. The address is hashed with a per-process key and never kept.</summary>
public sealed class RateLimiter(TimeProvider time, int maxTrackedClients = RateLimiter.DefaultMaxTrackedClients)
{
    public const int DefaultMaxTrackedClients = 50_000;

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _hits = new();
    private readonly Lock _gate = new();
    private DateTimeOffset _lastSweep;

    public bool TryAcquire(string client, int max, TimeSpan window)
    {
        string id = Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(client)));
        DateTimeOffset now = time.GetUtcNow();
        lock (_gate)
        {
            if (now - _lastSweep > window)
            {
                Sweep(now, window);
                _lastSweep = now;
            }
            if (!_hits.TryGetValue(id, out var queue))
            {
                if (_hits.Count >= maxTrackedClients)
                {
                    Sweep(now, window);
                    if (_hits.Count >= maxTrackedClients)
                    {
                        Evict(maxTrackedClients / 10 + 1);
                    }
                }
                queue = new Queue<DateTimeOffset>();
                _hits[id] = queue;
            }
            while (queue.Count > 0 && now - queue.Peek() > window)
            {
                queue.Dequeue();
            }
            if (queue.Count >= max)
            {
                return false;
            }
            queue.Enqueue(now);
            return true;
        }
    }

    internal int TrackedClients
    {
        get { lock (_gate) { return _hits.Count; } }
    }

    // A full table never turns a client away. The least recently seen tenth goes, so the cost is paid once per many new clients.
    private void Evict(int count)
    {
        foreach (var key in _hits.OrderBy(p => p.Value.Count == 0 ? DateTimeOffset.MinValue : p.Value.Last()).Take(count).Select(p => p.Key).ToList())
        {
            _hits.Remove(key);
        }
    }

    private void Sweep(DateTimeOffset now, TimeSpan window)
    {
        foreach (var key in _hits.Where(p => p.Value.Count == 0 || now - p.Value.Peek() > window && now - p.Value.Last() > window)
                     .Select(p => p.Key).ToList())
        {
            _hits.Remove(key);
        }
    }
}
