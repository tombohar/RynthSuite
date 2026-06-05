@echo off
REM RynthNav bake-ahead watcher: bakes navmesh tiles around/ahead of the player
REM as she roams, reading her position from C:\Games\RynthCore\NavData\_player.txt
REM (written by the RynthNav plugin). Leave this window open while you play.
cd /d "C:\Projects\RynthSuite\Tools\RynthNav.Baker"
echo Starting RynthNav bake-ahead watcher (radius 3.0)...
dotnet run -c Debug -- --watch --radius 3.0
pause
