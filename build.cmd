@echo off
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /win32manifest:"%~dp0DontStop.manifest" /out:"%~dp0DontStop.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0DontStop.cs"
