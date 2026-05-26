@echo off
setlocal

:: -----------------------------------------------------------------------
:: publish.bat — Build self-contained win-x64 single-file exe for testing.
::
::   publish.bat            - Publish to publish\win-x64\ss14-editor.exe
::   publish.bat run        - Publish then launch the exe (serve mode)
:: -----------------------------------------------------------------------

set PROJECT=ss14-editor.csproj
set OUTDIR=publish\win-x64

echo [Publish] Building self-contained win-x64 single-file exe...
dotnet publish %PROJECT% -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=embedded ^
    -o %OUTDIR%
if %ERRORLEVEL% neq 0 ( echo [Publish] FAILED. & exit /b %ERRORLEVEL% )

echo.
echo [Publish] OK: %OUTDIR%\ss14-editor.exe
echo [Publish] Run with: %OUTDIR%\ss14-editor.exe serve

if /i "%1"=="run" (
    echo.
    echo [Publish] Launching...
    "%OUTDIR%\ss14-editor.exe" serve
)

endlocal
