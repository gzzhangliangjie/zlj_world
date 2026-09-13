# One-command WebGL publish: build -> commit output -> push main -> Actions auto-deploys.
# Usage:  powershell -File Tools\publish-webgl.ps1 [-Message "note"]
param(
    [string]$UnityExe = "D:\Unity\2022.3.57f1c2\Editor\Unity.exe",
    [string]$Message = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "VoxelCraft"
$log = Join-Path (Join-Path $root "_logs") "publish_webgl.log"

Write-Host "== [1/3] Unity WebGL build ==" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path (Split-Path $log -Parent) | Out-Null
Remove-Item $log -Force -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $UnityExe -ArgumentList @"
-batchmode -quit -projectPath "$project" -executeMethod VoxelCraft.Editor.WebGLBuild.Build -logFile "$log"
"@ -PassThru
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt 900) { Start-Sleep -Milliseconds 2000 }
if (-not $p.HasExited) { throw "Unity build timed out" }
$okLine = Select-String -Path $log -Pattern "WEBGL BUILD OK" -ErrorAction SilentlyContinue
if (-not $okLine) {
    Select-String -Path $log -Pattern "error|WEBGL BUILD" | Select-Object -First 12 | ForEach-Object { Write-Host $_.Line -ForegroundColor Red }
    throw "WebGL build failed - see $log"
}
Write-Host "build OK: $((Get-Item (Join-Path $project 'Builds\WebGL\index.html')).Length)B index" -ForegroundColor Green

Write-Host "== [2/3] Commit build output ==" -ForegroundColor Cyan
Set-Location $root
# git writes line-ending/progress chatter to stderr; PS 5.1 + EAP=Stop turns
# that into a terminating NativeCommandError (especially when the caller
# redirects output), so keep EAP relaxed for the whole git section.
$prevEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
git add "VoxelCraft/Builds/WebGL"
if (-not $Message) { $Message = "webgl build $(Get-Date -Format 'yyyy-MM-dd HH:mm')" }
git commit -m $Message | Out-Null
$ErrorActionPreference = $prevEap
Write-Host "committed: $Message"

Write-Host "== [3/3] Push (retries included) ==" -ForegroundColor Cyan
$token = $env:GITHUB_TOKEN
$pushed = $false
foreach ($i in 1..8) {
    if ($token) {
        git push "https://gzzhangliangjie:$token@github.com/gzzhangliangjie/zlj_world.git" main 2>&1 | Out-Null
    } else {
        git push origin main 2>&1 | Out-Null
    }
    if ($LASTEXITCODE -eq 0) { $pushed = $true; break }
    Write-Host "push retry $i..."
    Start-Sleep -Seconds 12
}
$ErrorActionPreference = $prevEap
if (-not $pushed) { throw "push failed after retries" }
Write-Host "pushed. GitHub Actions is deploying (~1-2 min): https://github.com/gzzhangliangjie/zlj_world/actions" -ForegroundColor Green
Write-Host "play at: https://gzzhangliangjie.github.io/zlj_world/" -ForegroundColor Green
