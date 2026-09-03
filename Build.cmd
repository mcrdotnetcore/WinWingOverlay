@echo off
REM Builds a standalone WinWingOverlay.exe into .\dist
setlocal
pushd "%~dp0"

dotnet publish src\WinWingOverlay\WinWingOverlay.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained false ^
  -p:PublishSingleFile=true ^
  -o dist

if errorlevel 1 (
  echo.
  echo Build FAILED.
  popd
  exit /b 1
)

echo.
echo Built: %~dp0dist\WinWingOverlay.exe
popd
endlocal
