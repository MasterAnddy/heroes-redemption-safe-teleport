@echo off
cd /d "%~dp0"
HeroesRedemption.SafeTeleportLive.exe --config="%~dp0safe-teleport-config.json"
if errorlevel 1 pause

