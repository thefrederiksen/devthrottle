namespace CcDirector.Core.Wingman;

/// <summary>
/// How the sessions a session owns stand at a stop, at every level, counted as the row's own crew line counts
/// them ("5 under it: 3 working, 2 stopped, 0 need you"). Carried on the turn-verdict package so the judge can see
/// an owner that is waiting on its own working sessions (owner ruling, 2026-09-15).
/// </summary>
public sealed record OwnedSessionCounts(int Working, int Stopped, int NeedYou);
