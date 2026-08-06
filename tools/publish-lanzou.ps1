# 一键发布蓝奏云：
#   1. 打自包含单文件 exe
#   2. 压缩成 zip
#   3. 上传蓝奏云并取分享链接
#   4. 更新 docs/download.md
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'DeepSeekUsageTray.csproj'
$publishDir = Join-Path $root 'publish\win-x64-single'
$tmpDir = Join-Path $root 'publish\zip-tmp'
$python = 'C:\Users\admin\AppData\Local\Programs\Python\Python312\python.exe'
$uploadScript = Join-Path $PSScriptRoot 'lanzou_publish_pw.py'
$sessionFile = Join-Path $PSScriptRoot 'session\lanzou-session.json'

$version = (Select-String -Path $proj -Pattern '<Version>(.*?)</Version>').Matches[0].Groups[1].Value
$zipName = "DeepSeekUsageTray-v$version-win-x64.zip"
$zip = Join-Path $root "publish\$zipName"

Write-Host "==> 1/3 打包自包含单文件 exe (v$version)"
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

Write-Host "==> 2/3 压缩 zip"
if (Test-Path $tmpDir) { Remove-Item -LiteralPath $tmpDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
Copy-Item (Join-Path $publishDir 'DeepSeekUsageTray.exe') -Destination $tmpDir
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $tmpDir '*') -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -LiteralPath $tmpDir -Recurse -Force
$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 2)
Write-Host "    $zipName  ($sizeMb MB)"

Write-Host "==> 3/3 上传蓝奏云"
if (-not (Test-Path $sessionFile)) { throw "未找到蓝奏云登录状态: $sessionFile（先双击 tools\lanzou-login.cmd 登录）" }
$out = & $python $uploadScript $zip --session $sessionFile 2>&1
$out | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw '蓝奏云上传失败' }

$shareUrl = ($out | Select-String '^URL: ').ToString().Substring(5)
$sharePwd = ($out | Select-String '^PWD: ').ToString().Substring(5)

$docPath = Join-Path $root 'docs\download.md'
$doc = @"
# 下载 DeepSeek 用量托盘

## 国内直链（蓝奏云）

- 文件：$zipName（$sizeMb MB，自包含单文件，解压后双击即可运行，无需安装运行库）
- 下载：$shareUrl
$pwdLine

> 蓝奏云链接失效时，请到 GitHub Releases 下载：
> https://github.com/18922271727/DeepSeekUsageTray/releases
"@
if ($sharePwd) {
    $doc = $doc.Replace('$pwdLine', "- 提取码：$sharePwd")
} else {
    $doc = $doc.Replace('$pwdLine', '')
}
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($docPath, $doc, $utf8NoBom)
Write-Host ''
Write-Host '下载文档已更新: docs/download.md'
Write-Host "分享链接: $shareUrl"
