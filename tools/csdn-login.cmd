@echo off
rem 双击运行：打开 CSDN 登录窗口，登录成功后自动保存凭证并关闭窗口。
start "" "%~dp0..\publish\csdn-dev\DeepSeekUsageTray.exe" csdn login
