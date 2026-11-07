using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;

namespace DiscordChatExporter.Web.Services;

// Single-user session: holds the one Discord token in memory (never persisted, never sent to the
// browser) along with the resolved client, token kind and current user.
public sealed class DiscordSession
{
    private readonly object _lock = new();
    private SessionState? _state;

    public bool IsAuthenticated
    {
        get
        {
            lock (_lock)
                return _state is not null;
        }
    }

    public SessionState? Current
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    public async Task<SessionState> SignInAsync(string token, CancellationToken cancellationToken)
    {
        var client = new DiscordClient(token);

        // Validates the token and tells us whether it's a user or bot token
        var tokenKind = await client.GetTokenKindAsync(cancellationToken);
        var user = await client.GetCurrentUserAsync(cancellationToken);

        var state = new SessionState(client, tokenKind, user);
        lock (_lock)
            _state = state;

        return state;
    }

    public void SignOut()
    {
        lock (_lock)
            _state = null;
    }
}

public sealed record SessionState(DiscordClient Client, TokenKind TokenKind, User User);
