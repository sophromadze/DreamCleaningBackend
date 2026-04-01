using System.Collections.Concurrent;
using DreamCleaningBackend.Models.LiveChat;

namespace DreamCleaningBackend.Services;

public class LiveChatSessionManager
{
    // Whether the chat widget is enabled for visitors (admin-controlled)
    private volatile bool _isChatEnabled = true;
    public bool IsChatEnabled => _isChatEnabled;

    public bool ToggleChatEnabled()
    {
        _isChatEnabled = !_isChatEnabled;
        return _isChatEnabled;
    }

    // SessionId -> ChatSession
    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();
    // SignalR ConnectionId -> SessionId
    private readonly ConcurrentDictionary<string, string> _connectionToSession = new();
    // Telegram Forum TopicThreadId -> SessionId
    private readonly ConcurrentDictionary<int, string> _topicToSession = new();

    public ChatSession CreateSession(string connectionId, string visitorName)
    {
        var session = new ChatSession
        {
            SessionId = Guid.NewGuid().ToString("N")[..12],
            ConnectionId = connectionId,
            VisitorName = string.IsNullOrWhiteSpace(visitorName) ? "Visitor" : visitorName
        };
        _sessions[session.SessionId] = session;
        _connectionToSession[connectionId] = session.SessionId;
        return session;
    }

    public void SetTopicThreadId(string sessionId, int topicThreadId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.TopicThreadId = topicThreadId;
            _topicToSession[topicThreadId] = sessionId;
        }
    }

    public ChatSession? GetSessionByTopicThreadId(int topicThreadId)
    {
        if (_topicToSession.TryGetValue(topicThreadId, out var sessionId))
            return GetSession(sessionId);
        return null;
    }

    public ChatSession? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public ChatSession? GetSessionByConnectionId(string connectionId)
    {
        if (_connectionToSession.TryGetValue(connectionId, out var sessionId))
            return GetSession(sessionId);
        return null;
    }

    public void UpdateConnectionId(string sessionId, string newConnectionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            _connectionToSession.TryRemove(session.ConnectionId, out _);
            session.ConnectionId = newConnectionId;
            _connectionToSession[newConnectionId] = sessionId;
        }
    }

    public void RemoveConnection(string connectionId)
    {
        if (_connectionToSession.TryRemove(connectionId, out var sessionId))
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.IsActive = false;
            }
        }
    }

    public void CleanupInactiveSessions(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var toRemove = _sessions
            .Where(kvp => !kvp.Value.IsActive && kvp.Value.LastActivityAt < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            if (_sessions.TryRemove(key, out var session))
            {
                _topicToSession.TryRemove(session.TopicThreadId, out _);
            }
        }
    }
}
