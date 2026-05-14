@echo off
setlocal
set NODE_EXE=%~dp0node\node.exe
if not exist "%NODE_EXE%" (
  echo Merlin SIP legacy Node launcher is not configured.
  echo Use the native MerlinSIP.exe build for CK Media Services releases.
  pause
  exit /b 1
)
title Merlin SIP
start "" http://127.0.0.1:4173
"%NODE_EXE%" "%~dp0server.js"
pause
