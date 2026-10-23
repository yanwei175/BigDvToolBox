<#
.SYNOPSIS
生成自包含绿色版 ZIP、对应项目源码、Ghostscript 源码和 SHA256SUMS；成功后递增版本。
#>
[CmdletBinding()]
param(
    [string]$PrepareVersionFile,
    [string]$PublishedDirectory,
    [string]$PublishedVersion
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
function New-PortableZip([string]$Directory, [string]$ArchivePath) {
    $archive = [IO.Compression.ZipFile]::Open($ArchivePath, 'Create')
    try {
        foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName) {
            $name = $file.FullName.Substring($Directory.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, 'Optimal') | Out-Null
        }
    }
    finally { $archive.Dispose() }
}
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$propsPath = Join-Path $PSScriptRoot 'Release.props'
$artifacts = Join-Path $releaseRoot 'artifacts'
$env:NUGET_SCRATCH = Join-Path $artifacts 'nuget-scratch'
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
$releaseLock = [IO.File]::Open((Join-Path $artifacts 'release.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
Push-Location $releaseRoot
try {
    $props = New-Object Xml.XmlDocument
    $props.PreserveWhitespace = $true
    $props.Load($propsPath)
    $versionNode = $props.SelectSingleNode('/Project/PropertyGroup/Version')
    $current = [version]$versionNode.InnerText
    if ($current.Build -lt 0 -or $current.Revision -ge 0 -or $current.Build -ge 65534) {
        throw '版本须为有效的 major.minor.patch。'
    }
    $nextVersion = '{0}.{1}.{2}' -f $current.Major, $current.Minor, ($current.Build + 1)
    if ($PrepareVersionFile) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($PrepareVersionFile))) | Out-Null
        [IO.File]::WriteAllText($PrepareVersionFile, $nextVersion, [Text.Encoding]::ASCII)
        return
    }
    if ($PublishedDirectory -and $PublishedVersion -ne $nextVersion) {
        throw '本次编译的版本已经过期，请重新发布；版本号未递增。'
    }
    $output = Join-Path $artifacts "releases\$nextVersion"
    if (Test-Path -LiteralPath $output) { throw "版本目录已存在：$output，请先检查上次发布结果。" }
    $staging = Join-Path $artifacts ("portable-staging\" + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($staging) | Out-Null
    $payload = Join-Path $staging 'publish'
    if ($PublishedDirectory) {
        # 复制 VS 的独立发布目录；加入发行说明不会修改 VS 发布输出。
        [IO.Directory]::CreateDirectory($payload) | Out-Null
        Get-ChildItem -LiteralPath ([IO.Path]::GetFullPath($PublishedDirectory)) -Force |
            Copy-Item -Destination $payload -Recurse
    }
    else {
        $buildScript = Join-Path $releaseRoot '.agents\skills\winui3-full-skill\winui-dev-workflow\BuildAndRun.ps1'
        $arguments = @(
            '/t:Publish', '/p:Configuration=Release', '/p:Platform=x64', '/p:RuntimeIdentifier=win-x64',
            '/p:PublishProfile=', '/p:GeneratePortableArchive=false',
            '/p:SelfContained=true', '/p:WindowsAppSDKSelfContained=true', '/p:PublishTrimmed=false',
            '/p:PublishSingleFile=false', '/p:UseSharedCompilation=false', "/p:Version=$nextVersion",
            "/p:PublishDir=$payload\", "/p:PublishUrl=$payload\"
        )
        & $buildScript -Project 'src\BigDvToolBox.App\BigDvToolBox.App.csproj' -SkipRun -UseMSBuild @arguments
        if ($LASTEXITCODE -ne 0) { throw "发布应用失败（$LASTEXITCODE）。" }
    }
    $exe = Join-Path $payload 'BigDvToolBox.exe'
    $author = $props.SelectSingleNode('/Project/PropertyGroup/Authors').InnerText
    $metadata = (Get-Item -LiteralPath $exe).VersionInfo
    if ($metadata.ProductVersion -ne $nextVersion -or $metadata.CompanyName -ne $author) {
        throw '发布文件版本或开发者不一致，版本号未递增。'
    }
    foreach ($required in @('LICENSE','THIRD-PARTY-NOTICES.md','Licenses\ghostscript\COPYING')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $required))) { throw "缺少授权文件：$required" }
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Portable-README.md') -Destination (Join-Path $payload 'README.md')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ReleaseNotes.md') -Destination (Join-Path $payload 'ReleaseNotes.md')

    # 按固定摘要取得与嵌入 DLL 完全相同版本的完整上游源码。
    $gs = Get-Content -LiteralPath (Join-Path $releaseRoot 'third_party\ghostscript\source.json') -Raw | ConvertFrom-Json
    $cache = Join-Path $artifacts 'third-party'
    [IO.Directory]::CreateDirectory($cache) | Out-Null
    $gsArchive = Join-Path $cache $gs.fileName
    if (-not (Test-Path -LiteralPath $gsArchive)) {
        Invoke-WebRequest -UseBasicParsing -Uri $gs.url -OutFile "$gsArchive.download"
        if ((Get-Sha256 "$gsArchive.download") -ne $gs.sha256) {
            throw 'Ghostscript 源码校验失败；未生成发行包。'
        }
        Move-Item -LiteralPath "$gsArchive.download" -Destination $gsArchive
    }
    if ((Get-Sha256 $gsArchive) -ne $gs.sha256) { throw 'Ghostscript 源码摘要不匹配。' }

    # 使用当前工作树（包括未提交的修改）制作匹配的源码包，排除本地配置和构建输出。
    $source = Join-Path $staging 'source'
    [IO.Directory]::CreateDirectory($source) | Out-Null
    $sourcePaths = @(git -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw '无法取得源码文件清单。' }
    foreach ($relative in $sourcePaths) {
        if ($relative -match '(^|/)(\.git|\.vs|\.codex|bin|obj|artifacts|publish)(/|$)' -or
            $relative -match '(^|/)\.env($|\.)|\.(pfx|p12|key|user|suo|binlog|log)$') { continue }
        $original = Join-Path $releaseRoot $relative
        if (-not (Test-Path -LiteralPath $original -PathType Leaf)) { continue }
        # BuildAndRun 的临时分析器导入包含本机绝对路径，不属于项目源码。
        if ($relative -eq 'src/BigDvToolBox.App/Directory.Build.props' -and
            [IO.File]::ReadAllText($original) -match 'Microsoft.WindowsAppSDK.Analyzers.dll') { continue }
        $destination = Join-Path $source $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($original, $destination, $true)
    }
    $versionNode.InnerText = $nextVersion
    $preparedProps = Join-Path $staging 'Release.props'
    $props.Save($preparedProps)
    [IO.File]::Copy($preparedProps, (Join-Path $source 'build\Release.props'), $true)

    $assets = Join-Path $staging 'assets'
    [IO.Directory]::CreateDirectory($assets) | Out-Null
    $archive = Join-Path $assets "BigDvToolBox-$nextVersion-win-x64.zip"
    New-PortableZip $payload $archive
    New-PortableZip $source (Join-Path $assets "BigDvToolBox-$nextVersion-source.zip")
    Copy-Item -LiteralPath $gsArchive -Destination (Join-Path $assets $gs.fileName)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ReleaseNotes.md') -Destination (Join-Path $assets 'ReleaseNotes.md')
    $hashes = @(Get-ChildItem -LiteralPath $assets -File | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-Sha256 $_.FullName), $_.Name
    })
    [IO.File]::WriteAllLines((Join-Path $assets 'SHA256SUMS'), [string[]]$hashes, [Text.Encoding]::ASCII)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
    [IO.Directory]::Move($assets, $output)
    # 所有附件已成功生成，才提交版本号。备份路径兼容 Windows PowerShell 5.1。
    $backup = "$propsPath.$([guid]::NewGuid().ToString('N')).bak"
    [IO.File]::Replace($preparedProps, $propsPath, $backup)
    Remove-Item -LiteralPath $backup
    Write-Host "绿色版：$output\BigDvToolBox-$nextVersion-win-x64.zip"
    Write-Host "版本：$nextVersion；开发者：$author。请将同目录的全部附件上传到 GitHub Releases。"
}
finally {
    Pop-Location
    $releaseLock.Dispose()
}