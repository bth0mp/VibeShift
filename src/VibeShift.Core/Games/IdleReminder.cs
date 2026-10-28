namespace VibeShift.Core.Games;

/// <summary>
/// Gaming mode left on with no game running (the game closed long ago, or VibeShift never recognised it): worth one
/// nudge per session, since the AI apps it closed are still closed.
/// </summary>
public sealed class IdleReminder(TimeSpan after)
{
    private DateTime lastGameUtc = DateTime.MinValue;
    private bool done;

    /// <summary>
    /// True once per session, the first time no game has run for <c>after</c> (counting from the session's start)
    /// and Windows takes notifications, so a full-screen app is never interrupted.
    /// </summary>
    public bool Due(bool gameRunning, bool canNotify, DateTime sessionStartUtc, DateTime nowUtc)
    {
        if (gameRunning) lastGameUtc = nowUtc;
        var quietSince = sessionStartUtc > lastGameUtc ? sessionStartUtc : lastGameUtc;
        if (done || !canNotify || nowUtc - quietSince < after) return false;
        return done = true;
    }

    public void Reset()
    {
        lastGameUtc = DateTime.MinValue;
        done = false;
    }
}
