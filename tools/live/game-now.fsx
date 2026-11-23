// Exit 1 if VibeShift's own detection sees a game running right now, else 0. Used by live-test.ps1 so test
// windows never pop up over someone's match.
#r "../../src/VibeShift.Core/bin/Release/net10.0-windows/VibeShift.Core.dll"
open System
open VibeShift.Core.Games
open VibeShift.Core.Processes
open VibeShift.Core.State
let self = Diagnostics.Process.GetCurrentProcess()
// The user's own My games too (a game outside every store's folder). Read as plain JSON: loading it through the store
// could set a damaged file aside, and this only looks.
let myGames =
    try
        let dir = JsonStore.ResolveDir(Environment.GetEnvironmentVariable JsonStore.DataDirVariable, Environment.GetFolderPath Environment.SpecialFolder.ApplicationData)
        use doc = Text.Json.JsonDocument.Parse(IO.File.ReadAllText(IO.Path.Combine(dir, "settings.json")))
        [| for g in doc.RootElement.GetProperty("MyGames").EnumerateArray() -> g.GetString() |]
    with _ -> [||]
let w = GameWatcher(GameLibraries.Discover(), myGames, Func<int, string>(ProcessQuery.GetImagePath), Func<int, bool>(ProcessQuery.HasVisibleWindow), Nullable self.SessionId)
let games = w.Scan(ProcessSnapshot.Take())
printfn "games running: %d" games.Count
exit (if games.Count > 0 then 1 else 0)
