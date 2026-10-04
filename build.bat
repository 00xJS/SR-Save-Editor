@echo off
rem Builds MpCharacterEditor.exe with the C# compiler that comes with Windows.
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu ^
  /out:"%~dp0MpCharacterEditor.exe" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll ^
  "%~dp0MpCharacterEditor.cs"
