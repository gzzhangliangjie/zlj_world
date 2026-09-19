# Fetch-VanillaResources.ps1
# 一键拉取原版资源:官方镜像贴图 + Mojang 官方模型几何。
# 用法:
#   powershell -ExecutionPolicy Bypass -File Tools\Fetch-VanillaResources.ps1              # 默认集合
#   powershell -ExecutionPolicy Bypass -File Tools\Fetch-VanillaResources.ps1 -More        # 扩展集合
# 落盘:
#   _refs\vanilla\textures\<name>.png   (Java 版官方资产镜像, 64x32 皮肤格式)
#   _refs\vanilla\geo\<name>.geo.json   (Mojang 官方 bedrock-samples, -Z=正面)
param(
    [switch]$More
)
$ErrorActionPreference = "Continue"

$root  = "D:\zlj world\_refs\vanilla"
$txDir = "$root\textures"
$geoDir = "$root\geo"
New-Item -ItemType Directory -Force -Path $txDir, $geoDir | Out-Null

# ---- 1) 贴图:InventivetalentDev/minecraft-assets 是官方资产文件的逐版本镜像 ----
# 分支名 = 游戏版本号(1.20.4 / 1.20.2 ...)。路径 = 资产索引里的 minecraft/textures/...
$txBase = "https://raw.githubusercontent.com/InventivetalentDev/minecraft-assets/1.20.4/assets/minecraft/textures/entity"
$tx = [ordered]@{
    "pig"            = "pig/pig.png"
    "cow"            = "cow/cow.png"
    "sheep"          = "sheep/sheep.png"
    "sheep_fur"      = "sheep/sheep_fur.png"
    "chicken"        = "chicken.png"                     # 1.20.4 起拆分变体,404 时自动回退 1.20.2 分支(见循环内)
    "creeper"        = "creeper/creeper.png"
    "zombie"         = "zombie/zombie.png"
    "skeleton"       = "skeleton/skeleton.png"
    "spider"         = "spider/spider.png"
    "enderman"       = "enderman/enderman.png"
    "villager"       = "villager/villager.png"
    "wolf"           = "wolf/wolf.png"
    "slime"          = "slime/slime.png"
    "iron_golem"     = "iron_golem/iron_golem.png"
    "panda"          = "panda/panda.png"
    "bee"            = "bee/bee.png"
    "ghast"          = "ghast/ghast.png"
    "zombie_villager"= "zombie_villager/zombie_villager.png"
    "phantom"        = "phantom.png"
    "goat"           = "goat/goat.png"
    "ocelot"         = "cat/ocelot.png"
    "squid"          = "squid/squid.png"
    "bat"            = "bat.png"
    "fox"            = "fox/fox.png"
}
if ($More) {
    $tx["mooshroom"]   = "cow/mooshroom.png"
    $tx["cat"]         = "cat/cat.png"
    $tx["rabbit"]      = "rabbit/rabbit.png"
    $tx["horse"]       = "horse/horse.png"
    $tx["witch"]       = "witch.png"
    $tx["blaze"]       = "blaze.png"
    $tx["guardian"]    = "guardian/guardian.png"
    $tx["polar_bear"]  = "polar_bear/polar_bear.png"
    $tx["snow_golem"]  = "snow_golem/snow_golem.png"
    $tx["turtle"]      = "turtle/sea_turtle.png"
    $tx["llama"]       = "llama/llama.png"
    $tx["wolf_fur"]    = "wolf/wolf_fur.png"
}
$ok = 0; $fail = @()
foreach ($k in $tx.Keys) {
    $out = "$txDir\$k.png"
    if ((Test-Path $out) -and (Get-Item $out).Length -gt 200) { $ok++; continue }
    # 个别贴图在 1.20.4 分支路径变动(如 chicken 拆分变体),回退 1.20.2 分支
    $branch = if ($k -eq "chicken") { "1.20.2" } else { "1.20.4" }
    $url = "https://raw.githubusercontent.com/InventivetalentDev/minecraft-assets/$branch/assets/minecraft/textures/entity/$($tx[$k])"
    curl.exe -sL -o $out $url
    if ((Test-Path $out) -and (Get-Item $out).Length -gt 200) { $ok++ }
    else { $fail += $k; Remove-Item $out -ErrorAction SilentlyContinue }
}
"textures ok: $ok / $($tx.Count)" + $(if ($fail) { "  missing: $($fail -join ', ')" })

# ---- 2) 模型几何:Mojang 官方 bedrock-samples(数据驱动,权威于反编译) ----
# 用 GitHub contents API 列目录,按需下载;约定 bedrock -Z=正面,移植到本项目时 z 取反。
$geoBase = "https://raw.githubusercontent.com/Mojang/bedrock-samples/main/resource_pack/models/entity"
$geo = @("pig","cow","sheep","chicken","villager","wolf","creeper","zombie","skeleton","spider",
         "enderman","slime","bat","cat","fox","rabbit","bee","iron_golem","snow_golem","mooshroom",
         "squid","ocelot","panda","polar_bear","zombie_villager","witch","blaze","ghast","guardian",
         "husk","stray","drowned","phantom","turtle","goat","llama","sniffer","allay","armadillo","horse_v3")
if ($More) {
    # 全量列表(190 个):api.github.com/repos/Mojang/bedrock-samples/contents/resource_pack/models/entity
    $listing = Invoke-RestMethod -Uri "https://api.github.com/repos/Mojang/bedrock-samples/contents/resource_pack/models/entity" -TimeoutSec 30
    $geo = $listing | Where-Object { $_.name -match "\.geo\.json$" } | ForEach-Object { $_.name -replace "\.geo\.json$", "" }
}
$ok = 0; $fail = @()
foreach ($g in $geo) {
    $out = "$geoDir\$g.geo.json"
    if (Test-Path $out) { $ok++; continue }
    curl.exe -sL -o $out "$geoBase/$g.geo.json"
    if ((Test-Path $out) -and (Get-Item $out).Length -gt 200) { $ok++ }
    else { $fail += $g; Remove-Item $out -ErrorAction SilentlyContinue }
}
"geo ok: $ok / $($geo.Count)" + $(if ($fail) { "  missing: $($fail -join ', ')" })
"done -> $root"
