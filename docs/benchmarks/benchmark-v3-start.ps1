param(
    [string]$LoopGolemRoot = "C:\Projetos\Github\loopgolem",
    [string]$Workspace = "$env:USERPROFILE\Desktop\benchmark-loopgolem-v3",
    [string]$Distro = "Ubuntu",
    [switch]$ArmFOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$ExpectedCanonicalGoalHash = "1b1fd2bc6257cecf2a60fda1b579fb9f4f742d16dfdd299fa12070ab0b2e1d79"

function Assert-ExitCode {
    param([string]$Operation)

    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function Get-Sha256Hex {
    param([byte[]]$Bytes)

    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $hasher.ComputeHash($Bytes)
    }
    finally {
        $hasher.Dispose()
    }

    return ([BitConverter]::ToString($hashBytes)).Replace("-", "").ToLowerInvariant()
}

Write-Host ""
Write-Host "============================================================"
Write-Host " LoopGolem benchmark v3 preflight"
Write-Host "============================================================"
Write-Host ""

if (-not (Test-Path -LiteralPath $LoopGolemRoot)) {
    throw "LoopGolem repository not found: $LoopGolemRoot"
}

$LoopGolemRoot = (Resolve-Path -LiteralPath $LoopGolemRoot).Path
Set-Location $LoopGolemRoot

Write-Host "=== Checking LoopGolem repository ===" -ForegroundColor Cyan

$branch = (& git rev-parse --abbrev-ref HEAD).Trim()
Assert-ExitCode "git rev-parse --abbrev-ref HEAD"

if ($branch -ne "main") {
    throw "LoopGolem must be on branch 'main'. Current branch: $branch"
}

$loopDirty = @(& git status --porcelain)
Assert-ExitCode "git status"

if ($loopDirty.Count -ne 0) {
    throw "LoopGolem working tree is not clean:`n$($loopDirty -join "`n")"
}

& git fetch origin
Assert-ExitCode "git fetch origin"

$head = (& git rev-parse HEAD).Trim()
Assert-ExitCode "git rev-parse HEAD"

$originMain = (& git rev-parse origin/main).Trim()
Assert-ExitCode "git rev-parse origin/main"

if ($head -ne $originMain) {
    throw "Local main is not synchronized with origin/main. Local: $head ; origin/main: $originMain. Run 'git pull --ff-only origin main' and rerun this script."
}

Write-Host "LoopGolem HEAD: $head" -ForegroundColor Green
Write-Host "Local main matches origin/main." -ForegroundColor Green

$GoalFile = Join-Path $LoopGolemRoot "docs\benchmarks\taskforge-prompt-v1.txt"
$AcceptanceScript = Join-Path $LoopGolemRoot "docs\benchmarks\taskforge-v3-acceptance.ps1"
$Runner = Join-Path $LoopGolemRoot "docs\benchmarks\benchmark-v3-runner.ps1"

Write-Host ""
Write-Host "=== Checking benchmark files ===" -ForegroundColor Cyan

foreach ($requiredFile in @($GoalFile, $AcceptanceScript, $Runner)) {
    if (-not (Test-Path -LiteralPath $requiredFile)) {
        throw "Required benchmark file not found: $requiredFile"
    }

    Write-Host "Found: $requiredFile"
}

Write-Host ""
Write-Host "=== Checking canonical TaskForge prompt ===" -ForegroundColor Cyan

$rawGoalHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $GoalFile).Hash.ToLowerInvariant()
$goalText = [IO.File]::ReadAllText($GoalFile).Replace("`r`n", "`n").Replace("`r", "`n")

if ([string]::IsNullOrWhiteSpace($goalText)) {
    throw "TaskForge benchmark goal is empty."
}

$canonicalGoalHash = Get-Sha256Hex -Bytes ([Text.Encoding]::UTF8.GetBytes($goalText))

Write-Host "Raw on-disk SHA-256:   $rawGoalHash"
Write-Host "Canonical LF SHA-256:  $canonicalGoalHash"

if ($canonicalGoalHash -ne $ExpectedCanonicalGoalHash) {
    throw "Canonical TaskForge prompt mismatch. Expected: $ExpectedCanonicalGoalHash ; observed: $canonicalGoalHash"
}

Write-Host "Canonical TaskForge prompt: OK" -ForegroundColor Green

Write-Host ""
Write-Host "=== Checking disposable benchmark workspace ===" -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $Workspace)) {
    New-Item -ItemType Directory -Path $Workspace | Out-Null
}

$Workspace = (Resolve-Path -LiteralPath $Workspace).Path
$workspaceGitDirectory = Join-Path $Workspace ".git"

if (-not (Test-Path -LiteralPath $workspaceGitDirectory)) {
    & git -C $Workspace init
    Assert-ExitCode "git init in benchmark workspace"

    & git -C $Workspace config user.name "LoopGolem Benchmark"
    Assert-ExitCode "git config user.name"

    & git -C $Workspace config user.email "benchmark@loopgolem.local"
    Assert-ExitCode "git config user.email"

    & git -C $Workspace commit --allow-empty -m "benchmark base"
    Assert-ExitCode "create benchmark base commit"
}

$insideWorkTree = (& git -C $Workspace rev-parse --is-inside-work-tree).Trim()
Assert-ExitCode "git rev-parse --is-inside-work-tree"

if ($insideWorkTree -ne "true") {
    throw "Benchmark workspace is not a Git working tree: $Workspace"
}

$workspaceDirty = @(& git -C $Workspace status --porcelain)
Assert-ExitCode "benchmark git status"

if ($workspaceDirty.Count -ne 0) {
    throw "Benchmark workspace is not clean:`n$($workspaceDirty -join "`n")"
}

$trackedFiles = @(& git -C $Workspace ls-tree -r --name-only HEAD)
Assert-ExitCode "benchmark git ls-tree"

if ($trackedFiles.Count -ne 0) {
    throw "Benchmark base commit is not empty:`n$($trackedFiles -join "`n")"
}

$targetBase = (& git -C $Workspace rev-parse HEAD).Trim()
Assert-ExitCode "benchmark git rev-parse HEAD"

Write-Host "Workspace:   $Workspace"
Write-Host "Target base: $targetBase"
Write-Host "Workspace baseline: clean and empty" -ForegroundColor Green

Write-Host ""
Write-Host "=== Checking WSL ===" -ForegroundColor Cyan

$availableDistros = @(
    & wsl.exe -l -q |
        ForEach-Object { ($_ -replace "`0", "").Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)

Assert-ExitCode "wsl.exe -l -q"

if ($availableDistros -notcontains $Distro) {
    throw "WSL distro '$Distro' was not found. Available: $($availableDistros -join ', ')"
}

Write-Host "WSL distro '$Distro': OK" -ForegroundColor Green

Write-Host ""
Write-Host "============================================================"
Write-Host " PRE-RUNNER CHECKS PASSED"
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "LoopGolem HEAD:          $head"
Write-Host "Canonical goal SHA-256: $canonicalGoalHash"
Write-Host "Target base:             $targetBase"
Write-Host ""
Write-Host "No Codex/model inference has been requested by this preflight."
Write-Host ""

Set-ExecutionPolicy -Scope Process Bypass -Force

$runnerArguments = @{
    LoopGolemRoot = $LoopGolemRoot
    Workspace = $Workspace
    GoalFile = $GoalFile
    AcceptanceScript = $AcceptanceScript
    Distro = $Distro
}

if ($ArmFOnly) {
    $runnerArguments.ArmFOnly = $true
}

& $Runner @runnerArguments
