param(
    [Parameter(Mandatory = $true)]
    [string]$Workspace
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

function Assert-ExitCode {
    param(
        [string]$Operation,
        [int]$ExitCode,
        [bool]$ShouldSucceed = $true
    )

    if ($ShouldSucceed -and $ExitCode -ne 0) {
        throw "$Operation failed with exit code $ExitCode."
    }

    if (-not $ShouldSucceed -and $ExitCode -eq 0) {
        throw "$Operation unexpectedly succeeded."
    }
}

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Needle,
        [string]$Operation
    )

    if ($Text.IndexOf($Needle, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "$Operation did not contain expected text '$Needle'. Output: $Text"
    }
}

function Assert-NotContains {
    param(
        [string]$Text,
        [string]$Needle,
        [string]$Operation
    )

    if ($Text.IndexOf($Needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "$Operation unexpectedly contained '$Needle'. Output: $Text"
    }
}

$Workspace = (Resolve-Path -LiteralPath $Workspace).Path

$solutions = @(
    Get-ChildItem -LiteralPath $Workspace -Recurse -File |
        Where-Object {
            $_.Extension -eq ".sln" -or
            $_.Extension -eq ".slnx"
        } |
        Sort-Object FullName
)

if ($solutions.Count -ne 1) {
    throw "Expected exactly one solution under '$Workspace'; found $($solutions.Count)."
}

$solution = $solutions[0].FullName

& dotnet build $solution --configuration Release
Assert-ExitCode -Operation "TaskForge Release build" -ExitCode $LASTEXITCODE

$runtimeConfigs = @(
    Get-ChildItem -LiteralPath $Workspace -Recurse -File -Filter "*.runtimeconfig.json" |
        Where-Object {
            $_.FullName -match "[\\/]bin[\\/]Release[\\/]net10\.0[\\/]"
        } |
        Sort-Object FullName
)

$candidates = foreach ($runtimeConfig in $runtimeConfigs) {
    $assemblyName = ($runtimeConfig.BaseName -replace "\.runtimeconfig$", "") + ".dll"
    $dll = Join-Path $runtimeConfig.DirectoryName $assemblyName

    if ((Test-Path -LiteralPath $dll) -and $assemblyName -like "TaskForge*.dll") {
        Get-Item -LiteralPath $dll
    }
}

$cliDll = @(
    $candidates |
        Where-Object { $_.Name -eq "TaskForge.Cli.dll" } |
        Select-Object -First 1
)

if ($cliDll.Count -eq 0) {
    $cliDll = @(
        $candidates |
            Where-Object { $_.Name -notmatch "SelfTest|Test" } |
            Sort-Object FullName |
            Select-Object -First 1
    )
}

if ($cliDll.Count -ne 1) {
    throw "Could not identify the runnable TaskForge CLI assembly after the Release build."
}

$script:CliDll = $cliDll[0].FullName

function Invoke-TaskForge {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [bool]$ShouldSucceed = $true,
        [string]$Operation = "TaskForge command"
    )

    $output = (& dotnet $script:CliDll @Arguments 2>&1 | Out-String).Trim()
    $exitCode = $LASTEXITCODE

    Assert-ExitCode -Operation $Operation -ExitCode $exitCode -ShouldSucceed $ShouldSucceed
    return $output
}

$runRoot = Join-Path ([IO.Path]::GetTempPath()) ("taskforge-v3-" + [Guid]::NewGuid().ToString("N"))
$workingDirectory = Join-Path $runRoot "cwd"
$dataDirectory = Join-Path $runRoot "data"
$dataFile = Join-Path $runRoot "tasks.json"

New-Item -ItemType Directory -Force -Path $workingDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null

$env:TASKFORGE_DATA_PATH = $dataFile
$env:TASKFORGE_DATA_FILE = $dataFile
$env:TASKFORGE_DATA_DIR = $dataDirectory

$passed = $false
Push-Location $workingDirectory

try {
    Invoke-TaskForge -Arguments @("add", "Alpha task") -Operation "add task 1" | Out-Null
    Invoke-TaskForge -Arguments @("add", "Beta task") -Operation "add task 2" | Out-Null

    $all = Invoke-TaskForge -Arguments @("list", "--all") -Operation "list --all"
    Assert-Contains -Text $all -Needle "Alpha task" -Operation "list --all"
    Assert-Contains -Text $all -Needle "Beta task" -Operation "list --all"

    $show1 = Invoke-TaskForge -Arguments @("show", "1") -Operation "show 1"
    Assert-Contains -Text $show1 -Needle "Alpha task" -Operation "show 1"

    Invoke-TaskForge -Arguments @("tag", "1", "urgent") -Operation "tag 1 urgent" | Out-Null
    $tagged = Invoke-TaskForge -Arguments @("list", "--tag", "urgent") -Operation "list --tag urgent"
    Assert-Contains -Text $tagged -Needle "Alpha task" -Operation "list --tag urgent"

    Invoke-TaskForge -Arguments @("untag", "1", "urgent") -Operation "untag 1 urgent" | Out-Null
    $untagged = Invoke-TaskForge -Arguments @("list", "--tag", "urgent") -Operation "list --tag urgent after untag"
    Assert-NotContains -Text $untagged -Needle "Alpha task" -Operation "list --tag urgent after untag"

    Invoke-TaskForge -Arguments @("complete", "1") -Operation "complete 1" | Out-Null

    $completed = Invoke-TaskForge -Arguments @("list", "--completed") -Operation "list --completed"
    Assert-Contains -Text $completed -Needle "Alpha task" -Operation "list --completed"

    $openAfterComplete = Invoke-TaskForge -Arguments @("list", "--open") -Operation "list --open after complete"
    Assert-NotContains -Text $openAfterComplete -Needle "Alpha task" -Operation "list --open after complete"
    Assert-Contains -Text $openAfterComplete -Needle "Beta task" -Operation "list --open after complete"

    Invoke-TaskForge -Arguments @("reopen", "1") -Operation "reopen 1" | Out-Null

    $openAfterReopen = Invoke-TaskForge -Arguments @("list", "--open") -Operation "list --open after reopen"
    Assert-Contains -Text $openAfterReopen -Needle "Alpha task" -Operation "list --open after reopen"
    Assert-Contains -Text $openAfterReopen -Needle "Beta task" -Operation "list --open after reopen"

    $search = Invoke-TaskForge -Arguments @("search", "Beta") -Operation "search Beta"
    Assert-Contains -Text $search -Needle "Beta task" -Operation "search Beta"

    Invoke-TaskForge -Arguments @("delete", "2") -Operation "delete 2" | Out-Null
    Invoke-TaskForge -Arguments @("add", "Gamma task") -Operation "add task 3" | Out-Null

    $show3 = Invoke-TaskForge -Arguments @("show", "3") -Operation "show 3"
    Assert-Contains -Text $show3 -Needle "Gamma task" -Operation "show 3"

    $finalList = Invoke-TaskForge -Arguments @("list", "--all") -Operation "final list --all"
    Assert-Contains -Text $finalList -Needle "Alpha task" -Operation "final list --all"
    Assert-Contains -Text $finalList -Needle "Gamma task" -Operation "final list --all"
    Assert-NotContains -Text $finalList -Needle "Beta task" -Operation "final list --all"

    Invoke-TaskForge -Arguments @("show", "999999") -ShouldSucceed $false -Operation "invalid id" | Out-Null
    Invoke-TaskForge -Arguments @("definitely-not-a-command") -ShouldSucceed $false -Operation "invalid command" | Out-Null

    $jsonFiles = @(
        Get-ChildItem -LiteralPath $runRoot -Recurse -File -Filter "*.json"
    )

    if ($jsonFiles.Count -eq 0) {
        throw "No persisted JSON database was found in the isolated acceptance-test root."
    }

    $taskDatabaseFound = $false

    foreach ($jsonFile in $jsonFiles) {
        $raw = [IO.File]::ReadAllText($jsonFile.FullName)

        try {
            $null = $raw | ConvertFrom-Json
        }
        catch {
            throw "Persisted file '$($jsonFile.FullName)' is not valid JSON."
        }

        if ($raw.IndexOf("Alpha task", [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $raw.IndexOf("Gamma task", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $taskDatabaseFound = $true
        }
    }

    if (-not $taskDatabaseFound) {
        throw "No persisted JSON database contained the expected surviving TaskForge tasks."
    }

    $passed = $true
    Write-Host "TaskForge external acceptance: PASS"
}
finally {
    Pop-Location

    if ($passed) {
        Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    else {
        Write-Host "TaskForge acceptance artifacts retained at: $runRoot" -ForegroundColor Yellow
    }
}
