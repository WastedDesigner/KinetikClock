@echo off
cd /d "%~dp0"
rem Builds KinetikClock.exe with the .NET Framework compiler that ships with Windows. No SDK needed.
rem "build.bat package" also makes KinetikClock.zip (the exe, README and licence) for a release.
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /utf8output /codepage:65001 /platform:x64 /target:winexe /out:KinetikClock.new.exe /win32icon:icon.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ^
  KinetikClock.cs || exit /b 1
rem Built under a new name first, so this works while the app is running. The exe it replaces goes to ..\Old Versions
rem (when that folder is there), stamped with the date and time, to go back to if a build misbehaves.
if exist KinetikClock.exe if exist "..\Old Versions\" (
  for /f %%t in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-dd-HHmmss"') do move /y KinetikClock.exe "..\Old Versions\KinetikClock-%%t.exe" >nul
)
if exist KinetikClock.exe move /y KinetikClock.exe KinetikClock.old.exe >nul
move /y KinetikClock.new.exe KinetikClock.exe >nul || exit /b 1
if exist KinetikClock.old.exe del KinetikClock.old.exe 2>nul
echo Built KinetikClock.exe
if /i not "%~1"=="package" exit /b 0
if exist dist rmdir /s /q dist
mkdir dist\KinetikClock
copy /y KinetikClock.exe dist\KinetikClock\ >nul
copy /y README.md dist\KinetikClock\ >nul
copy /y LICENSE dist\KinetikClock\ >nul
if exist KinetikClock.zip del KinetikClock.zip
powershell -NoProfile -Command "Compress-Archive -Path dist\KinetikClock\* -DestinationPath KinetikClock.zip"
rmdir /s /q dist
echo Made KinetikClock.zip
