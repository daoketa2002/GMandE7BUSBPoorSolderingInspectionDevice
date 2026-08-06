@echo off
setlocal

net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo 请右键以管理员身份运行此脚本。
    exit /b 1
)

set "SERVICE_NAME=GMScannerRecoveryService"
set "SERVICE_EXE=%~dp0GMScannerRecoveryService.exe"

if not exist "%SERVICE_EXE%" (
    echo 未找到服务程序：%SERVICE_EXE%
    echo 请先执行 dotnet publish，并将发布输出复制到本脚本目录。
    exit /b 1
)

sc.exe query "%SERVICE_NAME%" >nul 2>&1
if "%errorlevel%"=="0" (
    sc.exe stop "%SERVICE_NAME%" >nul 2>&1
    sc.exe delete "%SERVICE_NAME%" >nul 2>&1
    timeout /t 1 /nobreak >nul
)

sc.exe create "%SERVICE_NAME%" binPath= "\"%SERVICE_EXE%\"" start= auto DisplayName= "GM Scanner Recovery Service"
if not "%errorlevel%"=="0" exit /b 1
sc.exe failure "%SERVICE_NAME%" reset= 86400 actions= restart/5000/restart/10000/restart/30000
sc.exe start "%SERVICE_NAME%"
if not "%errorlevel%"=="0" exit /b 1

echo 扫描枪恢复服务已安装并启动。
exit /b 0
