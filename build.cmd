@echo off
rem Builds with the C# compiler that ships with Windows - no SDK needed.
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /platform:x64 /out:"%~dp0bin\jvm-sandbox.exe" "%~dp0src\JvmSandbox.cs"
