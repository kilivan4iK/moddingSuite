@echo off
setlocal
echo === WARNO Modding Suite - diagnostic ===
echo This will collect information about your system and try to launch
echo moddingSuite.exe with a 12-second timeout. A diagnostic_dump_*.txt
echo file will be written in this folder regardless of whether the .exe
echo starts.
echo.
echo Please send the resulting .txt file to the developer.
echo.
pause
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0diagnostic.ps1"
echo.
echo Done. Press any key to close.
pause >nul
endlocal