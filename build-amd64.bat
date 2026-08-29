@echo off
setlocal

rem KeySecBox x64 build script.  Usage: build-amd64.bat [Debug|Release]  (default Debug)
rem   Debug   -> bin\Debug\x64\KeySecBox.exe
rem   Release -> bin\Release\x64\framework  (framework-dependent)
rem              bin\Release\x64\selfcontained  (self-contained, staged via TMP)

set CFG=%1
if "%CFG%"=="" set CFG=Debug
set ROOT=%~dp0
set PROJ=%ROOT%KeySecBox.csproj
set OUT=%ROOT%bin\Release\x64

echo === KeySecBox build: config=%CFG% platform=x64 ===

rem Optional: only needed for makepri.exe (Release self-contained). Keep going without VS.
set VSROOT=
if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" (
    for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath`) do set "VSROOT=%%i"
)
if defined VSROOT if exist "%VSROOT%\Common7\Tools\VsDevCmd.bat" call "%VSROOT%\Common7\Tools\VsDevCmd.bat" -arch=amd64 -no_logo

rem arm64 makepri.exe fails with exit 216, so pick the x64 one
set MAKEPRI=
if defined WindowsSdkBinPath (
    for /f "delims=" %%m in ('dir /b /s "%WindowsSdkBinPath%makepri.exe" 2^>nul ^| findstr /i "\\x64\\"') do if not defined MAKEPRI set MAKEPRI=%%m
)

rd /s /q "%ROOT%bin\%CFG%" 2>nul
rd /s /q "%ROOT%obj\x64\%CFG%" 2>nul
rd /s /q "%ROOT%obj\%CFG%" 2>nul

if /i not "%CFG%"=="Release" goto :build

rem --- self-contained: publish to TMP, merge PRI, trim, then copy to selfcontained ---
rem Single-file is disabled: PublishSingleFile extracts to %TEMP%\.net (slow, error 0x8013134b).
rem OutputPath keeps the intermediate build output (every satellite assembly) inside TMP too.
rd /s /q "%OUT%\TMP" "%OUT%\selfcontained" "%OUT%\framework" 2>nul

dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -p:OutputPath="%OUT%\TMP\build" -o "%OUT%\TMP"
if errorlevel 1 (echo dotnet publish selfcontained failed & goto :fail)

if not defined MAKEPRI (echo makepri.exe not found & goto :fail)
call :MergePri "%OUT%\TMP" "%MAKEPRI%"
if errorlevel 1 (echo merge selfcontained PRI failed & goto :fail)
call :Trim "%OUT%\TMP"
if errorlevel 1 (echo trim TMP failed & goto :fail)
call :Copy "%OUT%\TMP" "%OUT%\selfcontained"
if errorlevel 1 (echo copy selfcontained failed & goto :fail)

rem --- framework-dependent ---
dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained false -p:Platform=x64 -p:WindowsAppSDKSelfContained=false -p:OutputPath="%OUT%\TMP\build" -o "%OUT%\framework"
if errorlevel 1 (echo dotnet publish framework failed & goto :fail)

rd /s /q "%OUT%\TMP" 2>nul
echo Publish selfcontained done: bin\Release\x64\selfcontained\KeySecBox.exe
echo Publish framework done    : bin\Release\x64\framework\KeySecBox.exe
goto :done

:build
dotnet build "%PROJ%" -c %CFG% -p:Platform=x64
if errorlevel 1 (echo dotnet build failed & goto :fail)
echo Build done: bin\%CFG%\x64\KeySecBox.exe

:done
echo BUILD_DONE
exit /b 0

:fail
echo BUILD_FAILED
exit /b 1

rem Copy the trimmed staging dir to the final output dir (robocopy <8 == success)
:Copy
if not exist "%~2" md "%~2"
robocopy "%~1" "%~2" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (echo robocopy failed: %~1 -^> %~2 & exit /b 1)
exit /b 0

rem Keep only en-us, zh-CN and Microsoft.UI.Xaml (needed by the merged PRI); drop the rest
:Trim
for /d %%d in ("%~1\*") do (
    if /i not "%%~nxd"=="en-us" if /i not "%%~nxd"=="Microsoft.UI.Xaml" if /i not "%%~nxd"=="zh-CN" (
        rd /s /q "%%d" || exit /b 1
    )
)
exit /b 0

rem A self-contained deployment has no registered framework package, so MRT Core fails with
rem "Cannot locate resource ... themeresources.xaml". Merging the WinAppSDK framework .pri
rem files into KeySecBox.pri fixes it.
:MergePri
set MAKEPRI=%~2
if not exist "%MAKEPRI%" (
    for /f "delims=" %%m in ('dir /b /s "%ProgramFiles(x86)%\Windows Kits\10\bin\makepri.exe" 2^>nul ^| findstr /i "\\x64\\"') do set MAKEPRI=%%m
)
if not exist "%MAKEPRI%" (echo makepri.exe not found & exit /b 1)

dir /b "%~1\*.pri" > "%~1\pri.resfiles"
> "%~1\priconfig.xml" (
    echo ^<?xml version="1.0" encoding="utf-8"?^>
    echo ^<resources targetOsVersion="10.0.0" majorVersion="1"^>
    echo   ^<index root="\" startIndexAt="pri.resfiles"^>
    echo     ^<default^>
    echo       ^<qualifier name="Language" value="en-US" /^>
    echo       ^<qualifier name="Contrast" value="standard" /^>
    echo       ^<qualifier name="Scale" value="200" /^>
    echo     ^</default^>
    echo     ^<indexer-config type="PRI" /^>
    echo     ^<indexer-config type="RESFILES" qualifierDelimiter="." /^>
    echo   ^</index^>
    echo ^</resources^>
)

"%MAKEPRI%" new /pr "%~1" /cf "%~1\priconfig.xml" /of "%~1\merged.pri" /in KeySecBox /o >nul 2>&1
if errorlevel 1 (echo makepri merge failed & exit /b 1)
copy /y "%~1\merged.pri" "%~1\KeySecBox.pri" >nul || exit /b 1
del /q "%~1\merged.pri" "%~1\pri.resfiles" "%~1\priconfig.xml" 2>nul
echo Merged framework PRI into KeySecBox.pri
exit /b 0
