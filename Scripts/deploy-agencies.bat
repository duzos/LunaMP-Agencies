@echo off
setlocal enabledelayedexpansion

REM One-click deploy for the Agencies fork.
REM   1. Builds LmpClient + Server in Release.
REM   2. Copies the client plugin into KSP's GameData.
REM   3. Copies the server binaries into the live LMPServer folder
REM      (preserving Universe, Config, logs — so in-flight state survives).
REM   4. Rebuilds the shareable client zip.
REM   5. Opens the Artifacts folder in Explorer.
REM
REM Edit the three paths below if anything moves.

set REPO=%~dp0..
set KSP_DIR=D:\SteamLibrary\steamapps\common\Kerbal Space Program
set SERVER_DIR=D:\james\downloads\LMPServer
set DOTNET=C:\Users\james\.dotnet\dotnet.exe
set SEVENZIP=C:\Program Files\7-Zip\7z.exe
set ARTIFACTS=%REPO%\Artifacts
set CLIENT_STAGE=%ARTIFACTS%\LMPClient\GameData
set SERVER_STAGE=%ARTIFACTS%\LMPServer
set CLIENT_ZIP=%ARTIFACTS%\LunaMultiplayer-Agencies-Client.zip

echo.
echo === Luna Multiplayer Agencies deploy ===
echo Repo:   %REPO%
echo KSP:    %KSP_DIR%
echo Server: %SERVER_DIR%
echo.

if not exist "%DOTNET%" (
    echo [!] dotnet not found at %DOTNET%. Edit the script.
    exit /b 1
)
if not exist "%SEVENZIP%" (
    echo [!] 7z.exe not found at %SEVENZIP%. Edit the script.
    exit /b 1
)
if not exist "%KSP_DIR%\GameData" (
    echo [!] KSP GameData folder not found at %KSP_DIR%\GameData. Edit the script.
    exit /b 1
)
if not exist "%SERVER_DIR%" (
    echo [!] Server folder not found at %SERVER_DIR%. Edit the script.
    exit /b 1
)

echo.
echo === 1/5 Building Server (Release) ===
"%DOTNET%" build "%REPO%\Server\Server.csproj" -c Release || goto :fail

echo.
echo === 2/5 Building LmpClient (Release) ===
"%DOTNET%" build "%REPO%\LmpClient\LmpClient.csproj" -c Release || goto :fail

echo.
echo === 3/5 Staging client artifacts into %CLIENT_STAGE% ===
if exist "%CLIENT_STAGE%" rmdir /S /Q "%CLIENT_STAGE%"
mkdir "%CLIENT_STAGE%\LunaMultiplayer\Plugins"
mkdir "%CLIENT_STAGE%\LunaMultiplayer\Button"
mkdir "%CLIENT_STAGE%\LunaMultiplayer\Localization"
mkdir "%CLIENT_STAGE%\LunaMultiplayer\PartSync"
mkdir "%CLIENT_STAGE%\LunaMultiplayer\Icons"
mkdir "%CLIENT_STAGE%\LunaMultiplayer\Flags"

xcopy /Y /E "%REPO%\External\Dependencies\Harmony" "%CLIENT_STAGE%\" >nul || goto :fail
xcopy /Y "%REPO%\LmpClient\bin\Release\*" "%CLIENT_STAGE%\LunaMultiplayer\Plugins\" >nul || goto :fail
if exist "%CLIENT_STAGE%\LunaMultiplayer\Plugins\Harmony" rmdir /S /Q "%CLIENT_STAGE%\LunaMultiplayer\Plugins\Harmony"
copy /Y "%REPO%\LunaMultiplayer.version" "%CLIENT_STAGE%\LunaMultiplayer\LunaMultiplayer.version" >nul || goto :fail
if exist "%REPO%\LmpClient\Resources\*.png" xcopy /Y "%REPO%\LmpClient\Resources\*.png" "%CLIENT_STAGE%\LunaMultiplayer\Button\" >nul
if exist "%REPO%\LmpClient\Localization\XML" xcopy /Y /E "%REPO%\LmpClient\Localization\XML" "%CLIENT_STAGE%\LunaMultiplayer\Localization\" >nul
if exist "%REPO%\LmpClient\ModuleStore\XML" xcopy /Y /E "%REPO%\LmpClient\ModuleStore\XML\*.xml" "%CLIENT_STAGE%\LunaMultiplayer\PartSync\" >nul
if exist "%REPO%\LmpClient\Resources\Icons" xcopy /Y "%REPO%\LmpClient\Resources\Icons\*" "%CLIENT_STAGE%\LunaMultiplayer\Icons\" >nul
if exist "%REPO%\LmpClient\Resources\Flags" xcopy /Y "%REPO%\LmpClient\Resources\Flags\*" "%CLIENT_STAGE%\LunaMultiplayer\Flags\" >nul

echo.
echo === 4/5 Deploying to KSP + live server ===

REM KSP: remove prior LunaMultiplayer + 000_Harmony, then copy fresh.
if exist "%KSP_DIR%\GameData\LunaMultiplayer" rmdir /S /Q "%KSP_DIR%\GameData\LunaMultiplayer"
if exist "%KSP_DIR%\GameData\000_Harmony" rmdir /S /Q "%KSP_DIR%\GameData\000_Harmony"
xcopy /Y /E "%CLIENT_STAGE%" "%KSP_DIR%\GameData\" >nul || goto :fail
echo [ok] Client installed at %KSP_DIR%\GameData

REM Server: overwrite binaries only; Universe / Config / logs untouched
REM because they are not present in the release output.
xcopy /Y /E "%REPO%\Server\bin\Release\net10.0\*" "%SERVER_DIR%\" >nul || goto :fail
echo [ok] Server binaries replaced at %SERVER_DIR%
echo     Universe / Config / logs preserved in place.

echo.
echo === 5/5 Packaging shareable client zip ===
if exist "%CLIENT_ZIP%" del /Q "%CLIENT_ZIP%"
pushd "%ARTIFACTS%\LMPClient"
"%SEVENZIP%" a -tzip "%CLIENT_ZIP%" "GameData" >nul || (popd & goto :fail)
popd
echo [ok] %CLIENT_ZIP%

echo.
echo === Done ===
explorer "%ARTIFACTS%"
endlocal
exit /b 0

:fail
echo.
echo [!] Deploy failed — see the output above.
endlocal
exit /b 1
