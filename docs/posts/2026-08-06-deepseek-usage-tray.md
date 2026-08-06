# DeepSeek 用量便签：一个藏在系统托盘里的实时用量小工具

用过 DeepSeek 网页版的都知道，想看余额、今日用了多少 token 和花了多少钱，得打开网页慢慢找。这个小工具就是把这堆信息变成一张"便签"，常驻在 Windows 右下角托盘里，双击随时弹出、自动刷新，扫一眼就全知道。

## 它能做什么

- **托盘常驻**：双击托盘图标显示/隐藏，右键菜单支持刷新、设置、退出
- **三个标签页**：
  - **总概**：账户余额、今日使用（token / 请求次数 / 消费金额）、本月使用
  - **实时**：最近 1 分钟 Flash / Pro 两个模型的 token 消耗与费用（总量 / 命中 / 未命中 / 输出）
  - **价格**：DeepSeek 峰谷计价表，实时显示当前是高峰价还是低峰价，以及切换倒计时
- **置顶开关**：默认置顶，一键切换
- **扫码登录**：内嵌网页扫码登录，自动抓取并校验登录凭证，不用手动复制粘贴 API Key

## 使用方式

1. 到 GitHub Releases 页面下载最新版 `DeepSeekUsageTray-win-x64.exe`
2. 双击运行，首次使用会弹出登录窗口，用手机扫码登录 DeepSeek 网页版即可
3. 之后通过右下角托盘图标随时打开/隐藏

## 界面截图

![总概](../screenshots/overview.png)

![实时](../screenshots/live.png)

![价格](../screenshots/price.png)

## 隐私说明

登录凭证只保存在本机 `%APPDATA%\DeepSeekUsageTray\config.json`，不会上传到任何服务器；展示数据来自 DeepSeek 网页版接口。

## 获取方式

项目开源在 GitHub：https://github.com/18922271727/DeepSeekUsageTray

下载地址：https://github.com/18922271727/DeepSeekUsageTray/releases

> 本工具仅供个人学习参考使用，请遵守 DeepSeek 使用条款；网页接口可能随平台改版而失效，需要时请更新工具。
