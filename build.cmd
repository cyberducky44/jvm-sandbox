@echo off
rem Builds with the C# compiler that ships with Windows - no SDK needed.
rem jvm-sandbox.exe  = console version, for terminals
rem jvm-sandboxw.exe = windowless version, for launchers (no console window pops up)
set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%~dp0bin" mkdir "%~dp0bin"
%CSC% /nologo /optimize+ /platform:x64 /out:"%~dp0bin\jvm-sandbox.exe" "%~dp0src\JvmSandbox.cs" || exit /b 1
%CSC% /nologo /optimize+ /platform:x64 /target:winexe /out:"%~dp0bin\jvm-sandboxw.exe" "%~dp0src\JvmSandbox.cs" || exit /b 1
