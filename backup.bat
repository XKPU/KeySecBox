@echo off
rem Backup all source, .vscode config, .git folder and root build files
rem directly to the backup\ folder (previous backup is overwritten)
setlocal
set ROOT=%~dp0
set DEST=%ROOT%backup

rem Clear previous backup and recreate
rd /s /q "%DEST%" 2>nul
mkdir "%DEST%" 2>nul
if errorlevel 1 (echo failed to create %DEST% & exit /b 1)

rem Root files
copy /y "%ROOT%build_amd64.bat" "%DEST%\" >nul
copy /y "%ROOT%CMakeLists.txt" "%DEST%\" >nul
copy /y "%ROOT%KeySecBox.DLL.vcxproj" "%DEST%\" >nul
copy /y "%ROOT%KeySecBox.sln" "%DEST%\" >nul
copy /y "%ROOT%README.md" "%DEST%\" >nul
copy /y "%ROOT%version.txt" "%DEST%\" >nul
copy /y "%ROOT%LICENSE" "%DEST%\" >nul
copy /y "%ROOT%.gitignore" "%DEST%\" >nul

rem Source tree
robocopy "%ROOT%src" "%DEST%\src" /E /XD bin obj build >nul
if errorlevel 8 (echo robocopy src failed & exit /b 1)

rem .vscode config
robocopy "%ROOT%.vscode" "%DEST%\.vscode" /E >nul
if errorlevel 8 (echo robocopy .vscode failed & exit /b 1)

rem .git folder
robocopy "%ROOT%.git" "%DEST%\.git" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (echo robocopy .git failed & exit /b 1)

echo Backup done: %DEST%
endlocal
exit /b 0