using System.Collections.Concurrent;
using System.Net.Sockets;

namespace NativeSmtpReceiver;

public sealed class SmtpConnectionGuard
{
    private readonly SmtpServerOptions _options;
    private readonly SemaphoreSlim _globalSlots;
    private readonly ConcurrentDictionary<string, int> _activeConnectionsByIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> _connectionAttemptsByIp = new(StringComparer.OrdinalIgnoreCase);

    public SmtpConnectionGuard(SmtpServerOptions options)
    {
        _options = options;
        _globalSlots = new SemaphoreSlim(options.MaxConcurrentConnections, options.MaxConcurrentConnections);
    }

    public bool TryAcquire(TcpClient client, out SmtpConnectionLease? lease, out string rejectionResponse)
    {
        var remoteIp = (client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "unknown";
        lease = null;

        if (!_globalSlots.Wait(0))
        {
            rejectionResponse = "421 4.3.2 Too many concurrent connections";
            return false;
        }

        var activeForIp = _activeConnectionsByIp.AddOrUpdate(remoteIp, 1, static (_, current) => current + 1);
        if (activeForIp > _options.MaxConcurrentConnectionsPerIp)
        {
            Release(remoteIp);
            rejectionResponse = "421 4.7.0 Too many concurrent connections from this IP";
            return false;
        }

        var attempts = _connectionAttemptsByIp.GetOrAdd(remoteIp, static _ => new ConcurrentQueue<DateTimeOffset>());
        var now = DateTimeOffset.UtcNow;
        attempts.Enqueue(now);
        while (attempts.TryPeek(out var timestamp) && now - timestamp > TimeSpan.FromMinutes(1))
        {
            attempts.TryDequeue(out _);
        }

        if (attempts.Count > _options.MaxConnectionsPerIpPerMinute)
        {
            Release(remoteIp);
            rejectionResponse = "421 4.7.0 Connection rate limit exceeded";
            return false;
        }

        lease = new SmtpConnectionLease(this, remoteIp);
        rejectionResponse = string.Empty;
        return true;
    }

    private void Release(string remoteIp)
    {
        _activeConnectionsByIp.AddOrUpdate(remoteIp, 0, static (_, current) => Math.Max(0, current - 1));
        _globalSlots.Release();
    }

    public sealed class SmtpConnectionLease : IDisposable
    {
        private readonly SmtpConnectionGuard _guard;
        private readonly string _remoteIp;
        private int _disposed;

        internal SmtpConnectionLease(SmtpConnectionGuard guard, string remoteIp)
        {
            _guard = guard;
            _remoteIp = remoteIp;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _guard.Release(_remoteIp);
        }
    }
}
