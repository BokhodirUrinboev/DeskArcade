@echo off
rem arcade dotnet test: runs the command here while Desk Arcade shows it on the scoreboard, and
rem chimes when it passes or fails. The command's output and exit code come through unchanged.
start "" /b /wait "%~dp0DeskArcade.exe" --while %*
exit /b %ERRORLEVEL%
