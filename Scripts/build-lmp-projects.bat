@echo off
setlocal
REM Builds LMPClient, Server, and MasterServer in Debug then Release (in that order).
cd /d "%~dp0.."

dotnet build LmpClient\LmpClient.csproj -c Debug || exit /b 1
echo.
echo.
echo.

dotnet build LmpClient\LmpClient.csproj -c Release || exit /b 1
echo.
echo.
echo.

dotnet build Server\Server.csproj -c Debug || exit /b 1
echo.
echo.
echo.

dotnet build Server\Server.csproj -c Release || exit /b 1
echo.
echo.
echo.

dotnet build MasterServer\MasterServer.csproj -c Debug || exit /b 1
echo.
echo.
echo.

dotnet build MasterServer\MasterServer.csproj -c Release || exit /b 1

endlocal
