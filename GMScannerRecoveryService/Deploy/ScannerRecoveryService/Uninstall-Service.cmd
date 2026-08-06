@echo off
setlocal

net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo 请右键以管理员身份运行此脚本。
    exit /b 1
)

set "SERVICE_NAME=GMScannerRecoveryService"
sc.exe stop "%SERVICE_NAME%" >nul 2>&1
sc.exe delete "%SERVICE_NAME%"
if "%errorlevel%"=="0" echo 扫描枪恢复服务已卸载。
exit /b 0
