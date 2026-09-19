# Invoke-VanillaDecompile.ps1
# 一键获取官方 Minecraft 客户端代码并反编译为可读源码(官方命名)。
# 管线:官方版本清单 -> client.jar + client_mappings.txt -> AutoRenamingTool 按
#       官方映射反改名(--reverse!) -> Vineflower 反编译。
# 用法:
#   powershell -ExecutionPolicy Bypass -File Tools\Invoke-VanillaDecompile.ps1              # 默认 1.20.4
#   powershell -ExecutionPolicy Bypass -File Tools\Invoke-VanillaDecompile.ps1 -Version 1.21.1
# 落盘:
#   _vanilla-src\client.jar / client_mappings.txt / client-mapped.jar
#   _vanilla-src\decompiled\net\minecraft\...   (入口: world\entity\animal\Pig.java 等)
# 许可:Mojang 映射"仅用于开发目的,按原样提供";反编译产物受 Minecraft EULA 约束,
#       只作本机参考,不要再分发。
param(
    [string]$Version = "1.20.4",
    [string]$Dest = "D:\zlj world\_vanilla-src",
    [switch]$Force
)
$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force -Path "$Dest\tools", "$Dest\decompiled" | Out-Null

# ---- 0) Java(Temurin 21 JRE,zip 免安装) ----
$jreDir = Get-ChildItem "$Dest\tools" -Directory -Filter "jdk-21*-jre" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $jreDir) {
    curl.exe -sL -o "$Dest\tools\jre.zip" "https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jre/hotspot/normal/eclipse"
    Expand-Archive "$Dest\tools\jre.zip" -DestinationPath "$Dest\tools" -Force
    $jreDir = Get-ChildItem "$Dest\tools" -Directory -Filter "jdk-21*-jre" | Select-Object -First 1
}
$java = "$($jreDir.FullName)\bin\java.exe"
if (-not (Test-Path $java)) { throw "JRE unpack failed" }

# ---- 1) Vineflower 反编译器(Maven Central) ----
$vine = "$Dest\tools\vineflower.jar"
if (-not (Test-Path $vine) -or (Get-Item $vine).Length -lt 1MB) {
    curl.exe -sL -o $vine "https://repo1.maven.org/maven2/org/vineflower/vineflower/1.10.1/vineflower-1.10.1.jar"
}

# ---- 2) AutoRenamingTool(NeoForge maven;必须 -all 胖jar,裸 jar 无主清单) ----
$art = "$Dest\tools\art.jar"
if (-not (Test-Path $art) -or (Get-Item $art).Length -lt 500KB) {
    $meta = curl.exe -sL "https://maven.neoforged.net/releases/net/neoforged/AutoRenamingTool/maven-metadata.xml"
    $ver = ([xml]($meta -join "`n")).metadata.versioning.release
    curl.exe -sL -o $art "https://maven.neoforged.net/releases/net/neoforged/AutoRenamingTool/$ver/AutoRenamingTool-$ver-all.jar"
}

# ---- 3) 官方版本清单 -> client.jar + client_mappings.txt(同为官方渠道) ----
if (-not (Test-Path "$Dest\client.jar") -or -not (Test-Path "$Dest\client_mappings.txt") -or $Force) {
    $manifest = Invoke-RestMethod -Uri "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json" -TimeoutSec 30
    $v = $manifest.versions | Where-Object { $_.id -eq $Version }
    if (-not $v) { throw "version $Version not found in manifest" }
    $vjson = Invoke-RestMethod -Uri $v.url -TimeoutSec 30
    curl.exe -sL -o "$Dest\client.jar" $vjson.downloads.client.url
    curl.exe -sL -o "$Dest\client_mappings.txt" $vjson.downloads.client_mappings.url
}

# ---- 4) 按官方映射反改名 ----
# 映射为 ProGuard 格式,行形如 "net.minecraft...Pig -> aua:"(左=命名,右=混淆)。
# 输入 jar 里的类是右侧(混淆)名,所以必须 --reverse,否则等于没映射。
if (-not (Test-Path "$Dest\client-mapped.jar") -or (Get-Item "$Dest\client-mapped.jar").Length -lt 1MB) {
    & $java -jar $art --input "$Dest\client.jar" --output "$Dest\client-mapped.jar" `
        --map "$Dest\client_mappings.txt" --reverse --ann-fix --ids-fix --src-fix --record-fix 2>&1 | Select-Object -Last 2
    if ($LASTEXITCODE -ne 0) { throw "ART remap failed ($LASTEXITCODE)" }
}

# ---- 5) 全量反编译(约 3-5 分钟;内部类会合并进外部类文件) ----
$marker = "$Dest\decompiled\net\minecraft\world\entity\animal\Pig.java"
if ((-not (Test-Path $marker)) -or $Force) {
    Remove-Item "$Dest\decompiled" -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path "$Dest\decompiled" | Out-Null
    & $java -jar $vine --silent "$Dest\client-mapped.jar" "$Dest\decompiled" 2>&1 | Select-Object -Last 2
    if ($LASTEXITCODE -ne 0) { throw "vineflower failed ($LASTEXITCODE)" }
} else {
    "decompiled output exists, skip rebuild (pass -Force to rebuild)"
}

# ---- 6) 验证:映射应用成功的标志是出现命名包路径 ----
$pig = Get-ChildItem "$Dest\decompiled\net\minecraft\world\entity\animal\Pig.java" -ErrorAction SilentlyContinue
$total = (Get-ChildItem "$Dest\decompiled" -Recurse -Filter "*.java" | Measure-Object).Count
if ($pig) { "OK: $total sources; sample: $($pig.FullName)" }
else { throw "mappings were NOT applied (no net/minecraft/world/entity/animal/Pig.java). Did you pass --reverse?" }
