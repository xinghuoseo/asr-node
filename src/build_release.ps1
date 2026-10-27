# ============================================================
# 一键出包: AsrAgent 便携版 + GitHub Release 资源
#
# 产物 (默认 dist\):
#   AsrNode-core.zip          主包(程序 + 便携 Python + vad/diar), 解压双击即用
#   assets\ffmpeg.zip         基础包 (ffmpeg.exe -> tools\ffmpeg.exe)
#   assets\model-qwen3.zip    精准模型 (models\qwen3\*)
#   assets\model-sensevoice.zip  快速模型 (models\sensevoice\*)
#   assets\model-paraformer.zip  经典模型 + 标点 (models\paraformer\* + models\punc\*)
#   AsrNode\                  未压缩主包(可直接本地运行/调试)
#   AsrNode\models.json       下载清单(已写入真实 URL / 大小 / sha256)
#
# 用法:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build_release.ps1
#   powershell ... -File build_release.ps1 -Owner yourname -Repo asr-node -Tag v1.0.0
#
# 上传:
#   gh release create v1.0.0 dist\AsrNode-core.zip dist\assets\*.zip -t "AsrNode v1.0.0"
# ============================================================

param(
    [string]$Owner = "OWNER",
    [string]$Repo  = "REPO",
    [string]$Tag   = "TAG",
    [string]$OutDir = "",
    # 可嵌入版 Python 目录(含 python.exe / python312.zip); 留空则自动查找/下载
    [string]$PyEmbedDir = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$src     = $PSScriptRoot
$root    = Split-Path $PSScriptRoot -Parent
$runtime = Join-Path $root "runtime"
if (-not (Test-Path $runtime)) { throw "找不到运行时目录: $runtime" }

$out = if ($OutDir) { $OutDir } else { Join-Path $root "dist" }
$app = Join-Path $out "AsrNode"
$assets = Join-Path $out "assets"

Write-Output "== AsrNode release build =="
Write-Output "src     : $src"
Write-Output "runtime : $runtime"
Write-Output "out     : $out"

# ---------- [1/8] 清理 ----------
Write-Output "[1/8] clean ..."
if (Test-Path $app)    { Remove-Item $app -Recurse -Force }
if (Test-Path $assets) { Remove-Item $assets -Recurse -Force }
New-Item -ItemType Directory -Force -Path $app | Out-Null
New-Item -ItemType Directory -Force -Path $assets | Out-Null

# ---------- [2/8] 编译 exe ----------
Write-Output "[2/8] compile AsrAgent.exe ..."
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "找不到 csc.exe: $csc" }
$exe = Join-Path $app "AsrAgent.exe"
& $csc /nologo /target:winexe /codepage:65001 /win32icon:"$(Join-Path $src 'app.ico')" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
    /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    /out:"$exe" "$(Join-Path $src 'Agent.cs')"
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) { throw "编译失败" }

# ---------- [3/8] 程序文件 ----------
Write-Output "[3/8] copy app files ..."
foreach ($f in @("asr_server.py", "transcode.py", "requirements.txt")) {
    Copy-Item (Join-Path $src $f) $app -Force
}

# ---------- [4/8] 便携 Python ----------
Write-Output "[4/8] deploy portable python ..."
function Resolve-PyEmbed {
    if ($PyEmbedDir -and (Test-Path (Join-Path $PyEmbedDir "python.exe"))) { return $PyEmbedDir }
    # 本机缓存的便携 Python (runtime\pyembed), 离线可重复构建
    $cached = Join-Path $runtime "pyembed"
    if (Test-Path (Join-Path $cached "python.exe")) { return $cached }
    # 上一次构建产物里已有可嵌入 Python, 直接复用
    $prev = Join-Path $root "release\AsrNode\.venv\Scripts"
    if (Test-Path (Join-Path $prev "python.exe")) { return $prev }
    $zip = Join-Path $env:TEMP "py312embed.zip"
    if (-not (Test-Path $zip)) {
        $url = "https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip"
        Write-Output "  downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    }
    $dir = Join-Path $env:TEMP "py312embed_dir"
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $dir)
    return $dir
}
$pySrc = Resolve-PyEmbed
$pyDst = Join-Path $app ".venv\Scripts"
New-Item -ItemType Directory -Force -Path $pyDst | Out-Null
Get-ChildItem $pySrc -File | Copy-Item -Destination $pyDst -Force
if (-not (Test-Path (Join-Path $pyDst "python.exe"))) { throw "便携 Python 部署失败: $pySrc" }
# _pth: 启用标准库 zip、当前目录、上级 site-packages
$pth = Join-Path $pyDst "python312._pth"
[System.IO.File]::WriteAllLines($pth, @("python312.zip", ".", "..\Lib\site-packages"))
Write-Output "  python from: $pySrc"

# ---------- [5/8] site-packages ----------
Write-Output "[5/8] copy site-packages (exclude pip / imageio ffmpeg binary / __pycache__) ..."
$spSrc = Join-Path $runtime ".venv\Lib\site-packages"
$spDst = Join-Path $app ".venv\Lib\site-packages"
if (-not (Test-Path $spSrc)) { throw "找不到 site-packages: $spSrc" }
New-Item -ItemType Directory -Force -Path $spDst | Out-Null
robocopy $spSrc $spDst /MIR /XD pip pip-* __pycache__ /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy 失败: $LASTEXITCODE" }
Remove-Item (Join-Path $spDst "imageio_ffmpeg\binaries") -Recurse -Force -ErrorAction SilentlyContinue

# ---------- [6/8] 常驻模型 (vad + diar) ----------
Write-Output "[6/8] copy vad + diar models ..."
$modelDst = Join-Path $app "models"
New-Item -ItemType Directory -Force -Path $modelDst | Out-Null
foreach ($m in @("vad", "diar")) {
    $from = Join-Path $runtime "models\$m"
    if (-not (Test-Path $from)) { throw "找不到模型目录: $from" }
    Copy-Item $from (Join-Path $modelDst $m) -Recurse -Force
}

# ---------- [7/8] 生成按需下载资源包 ----------
Write-Output "[7/8] build download packs ..."

function New-ZipPack {
    param([string]$ZipPath, [hashtable]$Entries)
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    $fs = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew)
    try {
        $za = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($name in $Entries.Keys) {
                [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $za, $Entries[$name], $name,
                    [System.IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $za.Dispose() }
    } finally { $fs.Dispose() }
}

# ffmpeg.zip -> tools/ffmpeg.exe
$ffBin = Get-ChildItem (Join-Path $spSrc "imageio_ffmpeg\binaries") -Filter "ffmpeg*.exe" |
    Select-Object -First 1
if (-not $ffBin) { throw "找不到内置 ffmpeg: $spSrc\imageio_ffmpeg\binaries" }
New-ZipPack (Join-Path $assets "ffmpeg.zip") @{ "tools/ffmpeg.exe" = $ffBin.FullName }

# 模型包: 目录内所有文件, 保留相对路径
function Add-ModelPack {
    param([string]$ZipName, [hashtable]$Dirs)
    $entries = @{}
    foreach ($d in $Dirs.Keys) {
        $base = Join-Path $runtime "models\$d"
        if (-not (Test-Path $base)) { throw "找不到模型目录: $base" }
        Get-ChildItem $base -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($base.Length + 1).Replace('\', '/')
            $entries["$($Dirs[$d])$rel"] = $_.FullName
        }
    }
    New-ZipPack (Join-Path $assets $ZipName) $entries
}

Add-ModelPack "model-qwen3.zip" @{ "qwen3" = "models/qwen3/" }
Add-ModelPack "model-sensevoice.zip" @{ "sensevoice" = "models/sensevoice/" }
Add-ModelPack "model-paraformer.zip" @{ "paraformer" = "models/paraformer/"; "punc" = "models/punc/" }

# qwen3 里的测试音频不进包
Write-Output "  packs built"

# ---------- [8/8] 写 models.json + 主包 zip ----------
Write-Output "[8/8] write models.json + core zip ..."
$tpl = Join-Path $src "models.json"
if (-not (Test-Path $tpl)) { throw "找不到清单模板: $tpl" }
$man = Get-Content $tpl -Raw -Encoding UTF8 | ConvertFrom-Json

$srcList = @()
foreach ($s in $man.sources) {
    $srcList += ($s -replace "OWNER", $Owner -replace "REPO", $Repo -replace "TAG", $Tag)
}
$man.sources = $srcList

foreach ($k in @("ffmpeg", "qwen3", "sensevoice", "paraformer")) {
    $pack = $man.packs.$k
    $z = Join-Path $assets $pack.zip
    if (-not (Test-Path $z)) { throw "缺少资源包: $z" }
    $pack.size = [long](Get-Item $z).Length
    $pack.sha256 = (Get-FileHash $z -Algorithm SHA256).Hash.ToLower()
}

$manPath = Join-Path $app "models.json"
$man | ConvertTo-Json -Depth 8 | Set-Content $manPath -Encoding UTF8

# 说明文件
$readme = @"
AsrNode - 本地中文语音识别节点 (便携版)

1. 解压到任意目录(路径不要含特殊字符), 双击 AsrAgent.exe
2. 首次启动: 点左侧"下载基础包"安装 ffmpeg (约 84MB)
3. "识别模型"下拉框选择模型; 本地没有时右侧出现"下载该模型", 点击下载
   下载完成会自动加载, 进度在下方日志里
4. 填服务器端点(如 wss://host/path?token=xxx), 点"连接"
5. 左侧选记录, 右侧查看 分句 / 说话人 / 时间轴

要求: Windows 10/11 64 位
"@
[System.IO.File]::WriteAllText((Join-Path $app "README.txt"), $readme, (New-Object System.Text.UTF8Encoding($false)))

$core = Join-Path $out "AsrNode-core.zip"
if (Test-Path $core) { Remove-Item $core -Force }

# 注意: .NET 4.0 的 ZipFile.CreateFromDirectory 写入的是反斜杠路径,
# 这里手工建包并统一成 '/' 分隔, 保证任何解压工具都能还原目录结构。
function New-CoreZip {
    param([string]$Dir, [string]$ZipPath)
    $base = (Resolve-Path $Dir).Path.TrimEnd('\')
    $fs = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew)
    try {
        $za = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Get-ChildItem $Dir -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring($base.Length + 1).Replace('\', '/')
                [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $za, $_.FullName, $rel,
                    [System.IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $za.Dispose() }
    } finally { $fs.Dispose() }
}
New-CoreZip $app $core

# ---------- 汇总 ----------
function MB([long]$b) { return [math]::Round($b / 1MB, 1) }
Write-Output ""
Write-Output "== done =="
Write-Output ("core   : {0}  ({1} MB)" -f $core, (MB (Get-Item $core).Length))
foreach ($f in Get-ChildItem $assets -Filter *.zip) {
    Write-Output ("asset  : {0}  ({1} MB)  sha256={2}" -f $f.Name, (MB $f.Length),
        (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower())
}
Write-Output ""
Write-Output "upload (需要 gh 已登录):"
Write-Output ("  gh release create {0} `"{1}`" `"{2}\*.zip`" -t `"AsrNode {0}`"" -f $Tag, $core, $assets)
Write-Output "BUILD_RELEASE_OK"
