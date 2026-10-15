@echo off
rem Builds ForFlagrum and installs it with desktop and Start menu shortcuts, see tools\Install-ForFlagrum.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Install-ForFlagrum.ps1"
