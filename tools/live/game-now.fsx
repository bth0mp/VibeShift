// Exit 1 if VibeShift's own detection sees a game running right now, else 0. Used by live-test.ps1 so test
// windows never pop up over someone's match.
#r "../../src/VibeShift.Core/bin/Release/net10.0-windows/VibeShift.Core.dll"
open System
open VibeShift.Core.Games
open VibeShift.Core.Processes
let self = Diagnostics.Process.GetCurrentProcess()
let w = GameWatcher(GameLibraries.Discover(), [||], Func<int, string>(ProcessQuery.GetImagePath), Func<int, bool>(ProcessQuery.HasVisibleWindow), Nullable self.SessionId)
let games = w.Scan(ProcessSnapshot.Take())
printfn "games running: %d" games.Count
exit (if games.Count > 0 then 1 else 0)
