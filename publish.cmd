@echo off
chcp 65001 >nul
title Car Freedom - PDX Mods Publisher

rem ============================================================
rem  Car Freedom  ->  Paradox Mods
rem  ModId 163257  (already published)
rem
rem  usage:
rem    publish.cmd            update metadata (thumbnail / description / changelog)
rem    publish.cmd new        publish a NEW VERSION (after you changed the code)
rem ============================================================

set "MODPUB=D:\SteamLibrary\steamapps\common\Cities Skylines II\Cities2_Data\Content\Game\.ModdingToolchain\ModPublisher\ModPublisher.exe"
rem Staging folder INSIDE the project, not the game's Mods folder.
rem Reason: a dll sitting in <UserData>\Mods\HouseholdCars while the same mod is
rem also subscribed from PDX makes the assembly appear twice, which triggers
rem IsNotUniqueWarning and gets the whole mod rejected by the game.
set "CONTENT=%~dp0dist"

set "ACTION=Update"
if /i "%~1"=="new"     set "ACTION=NewVersion"
if /i "%~1"=="version" set "ACTION=NewVersion"

if not exist "%MODPUB%" (
  echo [ERROR] ModPublisher.exe not found:
  echo         %MODPUB%
  echo         Please check your game install path.
  pause
  exit /b 1
)

if not exist "%CONTENT%\HouseholdCars.dll" (
  echo [ERROR] Mod dll not found in:
  echo         %CONTENT%
  echo         Run:  copy /y bin\Release\HouseholdCars.dll bin\Release\HouseholdCars.pdb dist\
  pause
  exit /b 1
)

rem the thumbnail path inside the config is resolved relative to the
rem working directory, so always run from the project folder
cd /d "%~dp0"

echo ================================================
if /i "%ACTION%"=="Update" (
  echo  UPDATE METADATA  ^(thumbnail / description^)
  echo  Use this after editing the cover or the description.
  echo  Subscribers are NOT affected - no new version.
) else (
  echo  PUBLISH NEW VERSION  ^(ships the dll^)
  echo  Use this after you rebuilt HouseholdCars.dll.
  echo  Remember to bump ^<ModVersion^> in Properties\PublishConfiguration.xml
)
echo ------------------------------------------------
echo  Config : %~dp0Properties\PublishConfiguration.xml
echo  Content: %CONTENT%
echo ================================================
echo.

rem A version number lives in three unrelated files and none of them is read
rem from the others, so print them side by side. A forgotten bump is exactly
rem the kind of mistake that ships a v1.3 dll as "1.2" and nobody notices.
echo  Version check:
powershell -NoProfile -ExecutionPolicy Bypass -Command "$c=''; $t=Get-Content 'HouseholdCars.csproj' -Raw; if($t -match '<Version>(.*?)</Version>'){$c=$Matches[1]}; $j=(Get-Content 'dist\mod.json' -Raw | ConvertFrom-Json).version; $x=New-Object System.Xml.XmlDocument; $x.Load((Resolve-Path 'Properties\PublishConfiguration.xml').Path); $p=$x.SelectSingleNode('//ModVersion').Value; Write-Host ('   csproj   : ' + $c); Write-Host ('   mod.json : ' + $j); Write-Host ('   publish  : ' + $p); if($c -and ($c -ne $j -or $c -notlike ($p + '*'))){ Write-Host '   !! versions do not line up - check before uploading' }"
echo ------------------------------------------------
echo.

"%MODPUB%" %ACTION% "Properties\PublishConfiguration.xml" -c "%CONTENT%" -v

echo.
echo ================================================
echo  Finished.  Check it at:
echo  https://mods.paradoxplaza.com/mods/163257/Windows
echo  (the page may take a few minutes to refresh)
echo ================================================
pause
