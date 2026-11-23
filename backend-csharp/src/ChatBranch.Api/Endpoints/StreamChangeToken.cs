using ChatBranch.Data.Stores;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The <c>/api/whatsapp/stream</c> change token, shared by every open SSE
/// connection of this process for up to <see cref="Ttl"/>.
///
/// Each connection re-checks every 2 s, and <see cref="ThreadStore.ChangeToken"/>
/// is <c>MAX(crm_thread.updated_at)</c> plus <c>COUNT(*)</c> over every message —
/// a full count on PostgreSQL. With one stream per open dashboard tab that was
/// two whole-table reads per tab every 2 s; sharing the answer makes it at most
/// one pair per second for the whole process. A write is still seen on the next
/// tick after the window (≤ 1 s later than before, inside the 2 s poll).
/// A singleton per host, so test hosts (one database each) never share a token.
/// </summary>
public sealed class StreamChangeToken
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private string? _token;
    private long _at;

    public string Get(ThreadStore store)
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_token != null && now - _at < (long)Ttl.TotalMilliseconds) return _token;
        }
        var fresh = store.ChangeToken();
        lock (_gate)
        {
            _token = fresh;
            _at = Environment.TickCount64;
        }
        return fresh;
    }
}
