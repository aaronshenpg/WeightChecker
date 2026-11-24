@echo off
rem WeightCheckerCS 构建脚本：使用 Windows 自带 .NET Framework 4.x 编译器（无需安装 Visual Studio / .NET SDK）。
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo 找不到 %CSC%，请确认已安装 .NET Framework 4.8
  exit /b 1
)
if "%OUT%"=="" set OUT=dist\WeightCheckerCS
if not exist "%OUT%" mkdir "%OUT%"
if not exist "%OUT%\config" mkdir "%OUT%\config"
if not exist "%OUT%\iolib" mkdir "%OUT%\iolib"

set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
set COMMON=/nologo /optimize+ /platform:x64 /langversion:5 /utf8output /codepage:65001 /win32icon:app.ico %REFS%

"%CSC%" %COMMON% /target:winexe /out:"%OUT%\WeightCheckerCS.exe" /recurse:src\*.cs || exit /b 1
"%CSC%" %COMMON% /target:exe /out:"%OUT%\WeightCheckerCS-cli.exe" /recurse:src\*.cs || exit /b 1

copy /y app.ico "%OUT%\" >nul
copy /y iolib\mezio_c.dll "%OUT%\iolib\" >nul
if not exist "%OUT%\config\configs.json" copy /y config\configs.json "%OUT%\config\" >nul
if exist config\settings.json if not exist "%OUT%\config\settings.json" copy /y config\settings.json "%OUT%\config\" >nul
copy /y README.md "%OUT%\" >nul 2>nul
echo 构建完成：%OUT%\WeightCheckerCS.exe
pause
endlocal
