@echo off
chcp 65001 >nul 2>&1
title Durango: Wild Lands — English Launcher
color 0A

echo.
echo  ========================================
echo   Durango: Wild Lands - Private Server
echo   English Launcher
echo  ========================================
echo.

:: Read current gateway from offserver.txt
set "GATEWAY="
if exist "%~dp0game\offserver.txt" (
    for /f "tokens=1,* delims==" %%a in ('findstr /i "gateway" "%~dp0game\offserver.txt"') do (
        set "GATEWAY=%%b"
    )
)
if "%GATEWAY%"=="" set "GATEWAY=http://127.0.0.1:8190"

echo  Current gateway: %GATEWAY%
echo.

:menu
echo  ----------------------------------------
echo   1. Play Game
echo   2. Start Server
echo   3. Start Server + Play
echo   4. Stop Server
echo   5. Check Server Status
echo   6. Change Gateway Address
echo   7. Open Game Folder
echo   8. View Player Log
echo   0. Exit
echo  ----------------------------------------
echo.
set /p choice="  Select option: "

if "%choice%"=="1" goto play
if "%choice%"=="2" goto start_server
if "%choice%"=="3" goto start_and_play
if "%choice%"=="4" goto stop_server
if "%choice%"=="5" goto check_server
if "%choice%"=="6" goto change_gateway
if "%choice%"=="7" goto open_folder
if "%choice%"=="8" goto view_log
if "%choice%"=="0" goto exit

echo  Invalid option. Try again.
echo.
goto menu

:play
echo.
echo  [*] Launching Durango: Wild Lands...
echo  [*] Gateway: %GATEWAY%
start "" "%~dp0game\DurangoV2.exe"
echo  [OK] Game started!
echo.
goto menu

:start_server
echo.
if exist "%~dp0server\DurangoServer.csproj" (
    echo  [*] Starting local server...
    start "Durango Server" cmd /k "cd /d "%~dp0server" && "C:\Program Files\dotnet\dotnet.exe" run -c Release"
    echo  [OK] Server starting in new window.
    echo  [*] Wait for "gateway http://0.0.0.0:8190" then press 1 to Play.
) else (
    echo  [!] Server not found at: %~dp0server\
)
echo.
goto menu

:start_and_play
echo.
if exist "%~dp0server\DurangoServer.csproj" (
    echo  [*] Starting server...
    start "Durango Server" cmd /k "cd /d "%~dp0server" && "C:\Program Files\dotnet\dotnet.exe" run -c Release"
    echo  [*] Waiting 15 seconds for server to start...
    timeout /t 15 /nobreak >nul
    echo  [*] Launching game...
    start "" "%~dp0game\DurangoV2.exe"
    echo  [OK] Server + Game started!
) else (
    echo  [!] Server not found.
)
echo.
goto menu

:stop_server
echo.
echo  [*] Stopping Durango server...
taskkill /IM DurangoServer.exe /F >nul 2>&1
if %errorlevel%==0 (
    echo  [OK] Server stopped.
) else (
    echo  [!] No running server found.
)
echo.
goto menu

:check_server
echo.
echo  [*] Checking server at %GATEWAY% ...
powershell -NoProfile -Command "try { $r = Invoke-WebRequest -Uri '%GATEWAY%/health' -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop; Write-Host '  [OK] Server is ONLINE!' -ForegroundColor Green } catch { Write-Host '  [!!] Server appears OFFLINE' -ForegroundColor Red }"
echo.
goto menu

:change_gateway
echo.
echo  Current: %GATEWAY%
set /p NEWGW="  Enter new gateway (e.g. http://127.0.0.1:8190): "
if "%NEWGW%"=="" (
    echo  Cancelled.
    echo.
    goto menu
)
echo # Durango-OffServer config> "%~dp0game\offserver.txt"
echo gateway=%NEWGW%>> "%~dp0game\offserver.txt"
echo name=Durango OffServer>> "%~dp0game\offserver.txt"
set "GATEWAY=%NEWGW%"
echo  [OK] Gateway updated to: %NEWGW%
echo.
goto menu

:open_folder
explorer.exe "%~dp0game"
goto menu

:view_log
if exist "%~dp0game\player.log" (
    notepad.exe "%~dp0game\player.log"
) else (
    echo  [!] No player.log found. Launch the game first.
    echo.
)
goto menu

:exit
exit
