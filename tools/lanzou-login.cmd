@echo off
rem 双击运行：打开蓝奏云登录窗口，登录成功后自动保存凭证并关闭窗口。
setlocal
set "DEEPSEEK_LANZOU_SESSION=%~dp0session\lanzou-session.json"
if not exist "%~dp0session" mkdir "%~dp0session"
start "" "%~dp0..\bin\Release\net9.0-windows\DeepSeekUsageTray.exe" lanzou login
endlocal
