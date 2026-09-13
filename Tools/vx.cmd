@echo off
rem vx - shorthand for Tools\unity-cli.ps1 (VoxelCraft Unity CLI)
rem   vx status | vx compile-fast | vx ci | vx build webgl | vx help
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0unity-cli.ps1" %*
