@echo off
setlocal

:: -----------------------------------------------------------------------
:: Build ss14-redactor
::   build.bat            - Debug build, runs in-place (dotnet run style)
::   build.bat release    - Release build output to bin\
::   build.bat publish    - Self-contained win-x64 single-file to publish\
:: -----------------------------------------------------------------------

set PROJECT=ss14-redactor.csproj

if /i "%1"=="publish" goto publish
if /i "%1"=="release" goto release

:: ---- Default: Debug build ---------------------------------------------
:debug
echo [Build] Debug...
dotnet build %PROJECT% -c Debug -v q
if %ERRORLEVEL% neq 0 ( echo Build FAILED. & exit /b %ERRORLEVEL% )
echo [Build] OK: bin\ss14-redactor.exe
goto end

:: ---- Release build ----------------------------------------------------
:release
echo [Build] Release...
dotnet build %PROJECT% -c Release -v q
if %ERRORLEVEL% neq 0 ( echo Build FAILED. & exit /b %ERRORLEVEL% )
echo [Build] OK: bin\ss14-redactor.exe
goto end

:: ---- Self-contained single-file publish for Windows ------------------
:publish
echo [Build] Publishing self-contained win-x64...
dotnet publish %PROJECT% -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=embedded ^
    -o publish\win-x64
if %ERRORLEVEL% neq 0 ( echo Publish FAILED. & exit /b %ERRORLEVEL% )
echo [Build] Published: publish\win-x64\ss14-redactor.exe
goto end

:end
endlocal
