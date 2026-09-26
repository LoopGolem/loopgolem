param(
    [Parameter(Mandatory = $true)]
    [string]$LoopGolemRoot,
    [Parameter(Mandatory = $true)]
    [string]$Workspace,
    [Parameter(Mandatory = $true)]
    [string]$GoalFile,
    [Parameter(Mandatory = $true)]
    [string]$AcceptanceScript,
    [Parameter(Mandatory = $true)]
    [string]$Distro,
    [string]$ArtifactsRoot = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$PipeName = "loopgolem-worker-v1"

$MissionExecutionModeCodex = 1
$SessionReuseDisabled = 0
$WorkerContextFresh = 0
$WorkerContextSupervisorFork = 2
$WorkerReasoningLow = 0
$WorkerReasoningHigh = 1

$MissionStatusWaitingForQuota = 3
$MissionStatusWaitingForApproval = 4
$MissionStatusPaused = 5
$MissionStatusNeedsHumanAttention = 6
$MissionStatusFailed = 7
$MissionStatusCompleted = 8

$MissionTaskKindPlanMission = 2
$MissionTaskKindValidateMission = 5
$TaskStatusCompleted = 9

function Assert-LastExitCode {
    param([string]$Operation)
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function Write-Utf8NoBom {
    param([string]$Path, [string]$Text)
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Write-JsonArtifact {
    param([string]$Path, $Value)
    Write-Utf8NoBom -Path $Path -Text ($Value | ConvertTo-Json -Depth 100)
}

function Send-WorkerRequest {
    param($Request, [int]$TimeoutMs = 5000)

    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        ".",
        $PipeName,
        [System.IO.Pipes.PipeDirection]::InOut)

    try {
        $pipe.Connect($TimeoutMs)
        $reader = [System.IO.StreamReader]::new($pipe)
        $writer = [System.IO.StreamWriter]::new($pipe)
        $writer.AutoFlush = $true

        try {
            $writer.WriteLine(($Request | ConvertTo-Json -Depth 100 -Compress))
            $line = $reader.ReadLine()
            if ([string]::IsNullOrWhiteSpace($line)) {
                throw "Worker returned an empty response."
            }
            return $line | ConvertFrom-Json
        }
        finally {
            $writer.Dispose()
            $reader.Dispose()
        }
    }
    finally {
        $pipe.Dispose()
    }
}

function Test-WorkerReachable {
    try {
        $response = Send-WorkerRequest -Request ([ordered]@{ type = "ping" }) -TimeoutMs 300
        return [bool]$response.success
    }
    catch {
        return $false
    }
}

function Start-BenchmarkWorker {
    param([string]$Label)

    if (Test-WorkerReachable) {
        throw "A LoopGolem Worker is already reachable on '$PipeName'. Stop Desktop/Worker instances before running the benchmark."
    }

    $workerDll = Join-Path $LoopGolemRoot "src\LoopGolem.Worker\bin\Release\net10.0\loopgolem-worker.dll"
    if (-not (Test-Path -LiteralPath $workerDll)) {
        throw "Release Worker DLL not found: $workerDll"
    }

    $stdout = Join-Path $ArtifactsRoot "worker-$Label.stdout.log"
    $stderr = Join-Path $ArtifactsRoot "worker-$Label.stderr.log"
    $quotedDll = '"' + $workerDll + '"'

    $process = Start-Process -FilePath "dotnet" -ArgumentList @($quotedDll) -WorkingDirectory $LoopGolemRoot -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru

    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        if ($process.HasExited) {
            throw "Worker exited before IPC became ready. See $stderr"
        }
        if (Test-WorkerReachable) {
            return $process
        }
        Start-Sleep -Milliseconds 250
    }

    try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch {}
    throw "Worker IPC did not become ready. See $stderr"
}

function Stop-BenchmarkWorker {
    param($Process)
    if ($null -eq $Process) { return }

    try {
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force
            $Process.WaitForExit()
        }
    }
    catch {}

    Start-Sleep -Milliseconds 500
}

function Reset-TargetWorkspace {
    & git -C $Workspace reset --hard $TargetBase | Out-Host
    Assert-LastExitCode "git reset --hard"

    & git -C $Workspace clean -fdx | Out-Host
    Assert-LastExitCode "git clean -fdx"

    $head = (& git -C $Workspace rev-parse HEAD).Trim()
    Assert-LastExitCode "git rev-parse HEAD"
    if ($head -ne $TargetBase) {
        throw "Workspace HEAD '$head' does not match target base '$TargetBase'."
    }

    $dirty = (& git -C $Workspace status --porcelain)
    Assert-LastExitCode "git status --porcelain"
    if (-not [string]::IsNullOrWhiteSpace(($dirty -join [Environment]::NewLine))) {
        throw "Workspace is not clean after reset/clean."
    }
}

function Get-MissionStatusName {
    param([int]$Status)
    switch ($Status) {
        0 { return "Created" }
        1 { return "Planning" }
        2 { return "Running" }
        3 { return "WaitingForQuota" }
        4 { return "WaitingForApproval" }
        5 { return "Paused" }
        6 { return "NeedsHumanAttention" }
        7 { return "Failed" }
        8 { return "Completed" }
        default { return "Unknown($Status)" }
    }
}

function Wait-Mission {
    param([string]$MissionId, [int[]]$StopStatuses, [int]$TimeoutMinutes = 180)

    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalMinutes -lt $TimeoutMinutes) {
        $response = Send-WorkerRequest -Request ([ordered]@{
            type = "getMission"
            missionId = $MissionId
        })

        if (-not $response.success) {
            throw "getMission failed: $($response.error)"
        }

        $status = [int]$response.mission.mission.status
        if ($StopStatuses -contains $status) {
            return $response
        }

        Start-Sleep -Seconds 2
    }

    throw "Mission $MissionId exceeded $TimeoutMinutes minutes."
}

function Invoke-AllowanceRead {
    param([string]$Label)

    $python = @'
import json
import subprocess

cmd = [
    "bash",
    "-lc",
    "exec codex --disable apps --disable plugins --disable multi_agent --disable memories app-server --listen stdio://",
]
p = subprocess.Popen(
    cmd,
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    text=True,
    bufsize=1,
)

def send(obj):
    p.stdin.write(json.dumps(obj, separators=(",", ":")) + "\n")
    p.stdin.flush()

def request(request_id, method, params):
    send({"id": request_id, "method": method, "params": params})
    while True:
        line = p.stdout.readline()
        if not line:
            err = p.stderr.read()
            raise RuntimeError("app-server ended before response: " + err)
        msg = json.loads(line)

        if (
            "id" in msg
            and "method" in msg
            and "result" not in msg
            and "error" not in msg
        ):
            send({
                "id": msg["id"],
                "error": {
                    "code": -32601,
                    "message": "unsupported by benchmark rate reader",
                },
            })
            continue

        if msg.get("id") != request_id:
            continue

        if "error" in msg:
            raise RuntimeError(method + " failed: " + json.dumps(msg["error"]))

        return msg["result"]

try:
    request(
        1,
        "initialize",
        {
            "clientInfo": {
                "name": "loopgolem-benchmark-v3",
                "title": "LoopGolem Benchmark v3",
                "version": "0.1",
            },
            "capabilities": {
                "experimentalApi": False,
                "requestAttestation": False,
            },
        },
    )
    send({"method": "initialized", "params": None})
    result = request(2, "account/rateLimits/read", {})
    print(json.dumps(result, separators=(",", ":")))
finally:
    try:
        p.terminate()
        p.wait(timeout=2)
    except Exception:
        try:
            p.kill()
        except Exception:
            pass
'@

    $raw = $python | & wsl.exe -d $Distro --exec python3 -
    if ($LASTEXITCODE -ne 0) {
        throw "account/rateLimits/read failed through WSL."
    }

    if ($raw -is [array]) {
        $raw = $raw -join [Environment]::NewLine
    }

    if ([string]::IsNullOrWhiteSpace($raw)) {
        throw "account/rateLimits/read returned no JSON."
    }

    Write-Utf8NoBom -Path (Join-Path $ArtifactsRoot "allowance-$Label.json") -Text $raw
    return $raw | ConvertFrom-Json
}

function Get-FiveHourWindow {
    param($Snapshot)

    $candidates = New-Object System.Collections.Generic.List[object]
    if ($null -ne $Snapshot.rateLimits) {
        $candidates.Add($Snapshot.rateLimits)
    }

    if ($null -ne $Snapshot.rateLimitsByLimitId) {
        foreach ($property in $Snapshot.rateLimitsByLimitId.PSObject.Properties) {
            if ($null -ne $property.Value) {
                $candidates.Add($property.Value)
            }
        }
    }

    foreach ($candidate in $candidates) {
        if ($null -ne $candidate.primary -and [int64]$candidate.primary.windowDurationMins -eq 300) {
            return [pscustomobject]@{
                usedPercent = [int]$candidate.primary.usedPercent
                windowDurationMins = [int64]$candidate.primary.windowDurationMins
                resetsAt = $candidate.primary.resetsAt
                limitId = $candidate.limitId
                limitName = $candidate.limitName
            }
        }
    }

    throw "No primary 300-minute allowance window was present in account/rateLimits/read."
}

function Get-StableAllowanceBaseline {
    param([string]$Label)

    for ($round = 1; $round -le 8; $round++) {
        $a = Get-FiveHourWindow (Invoke-AllowanceRead "$Label-r$round-t0")
        Start-Sleep -Seconds 5
        $b = Get-FiveHourWindow (Invoke-AllowanceRead "$Label-r$round-t5")
        Start-Sleep -Seconds 10
        $c = Get-FiveHourWindow (Invoke-AllowanceRead "$Label-r$round-t15")

        if ($a.usedPercent -eq $b.usedPercent -and $b.usedPercent -eq $c.usedPercent) {
            return $c
        }

        Write-Host "Allowance still settling ($($a.usedPercent) -> $($b.usedPercent) -> $($c.usedPercent)). Waiting before another no-inference baseline..." -ForegroundColor Yellow
        Start-Sleep -Seconds 15
    }

    throw "Five-hour allowance did not stabilize without inference. Retry later; measured-arm attribution would be ambiguous."
}

function Get-PostArmAllowance {
    param([string]$Arm)

    $immediate = Get-FiveHourWindow (Invoke-AllowanceRead "$Arm-after-t0")
    Start-Sleep -Seconds 10
    $after10 = Get-FiveHourWindow (Invoke-AllowanceRead "$Arm-after-t10")
    Start-Sleep -Seconds 20
    $after30 = Get-FiveHourWindow (Invoke-AllowanceRead "$Arm-after-t30")

    return [pscustomobject]@{
        immediate = $immediate
        after10 = $after10
        after30 = $after30
    }
}

function Invoke-ExternalAcceptance {
    param([string]$Arm)

    $stdout = Join-Path $ArtifactsRoot "$Arm-acceptance.stdout.log"
    $stderr = Join-Path $ArtifactsRoot "$Arm-acceptance.stderr.log"
    $shell = (Get-Process -Id $PID).Path

    $quotedScript = '"' + $AcceptanceScript + '"'
    $quotedWorkspace = '"' + $Workspace + '"'
    $args = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $quotedScript,
        "-Workspace", $quotedWorkspace
    )

    $process = Start-Process -FilePath $shell -ArgumentList $args -RedirectStandardOutput $stdout -RedirectStandardError $stderr -Wait -PassThru

    return [pscustomobject]@{
        passed = ($process.ExitCode -eq 0)
        exitCode = $process.ExitCode
        stdout = $stdout
        stderr = $stderr
    }
}

function Get-RoleTelemetry {
    param($Telemetry, [int]$Role)
    return @($Telemetry.roles | Where-Object { [int]$_.role -eq $Role }) | Select-Object -First 1
}

function Invoke-MeasuredArm {
    param(
        [string]$Arm,
        [int]$WorkerContext,
        [int]$WorkerReasoning,
        [string]$SupervisorSourceMissionId = ""
    )

    Reset-TargetWorkspace

    $worker = $null
    try {
        $worker = Start-BenchmarkWorker -Label $Arm

        $status = Send-WorkerRequest -Request ([ordered]@{ type = "getCodexStatus" })
        if (-not $status.success -or -not $status.codexStatus.available -or -not $status.codexStatus.chatGptAuthenticated) {
            throw "Codex runtime is not ready for arm $Arm."
        }

        $before = Get-StableAllowanceBaseline -Label "$Arm-before"
        $visibleBefore = Read-Host "Arm $($Arm): enter visible UI remaining percent now (or leave blank)"

        $request = [ordered]@{
            type = "createMission"
            goal = $Goal
            workspacePath = $Workspace
            executionMode = $MissionExecutionModeCodex
            sessionReuse = $SessionReuseDisabled
            workerContext = $WorkerContext
            workerReasoning = $WorkerReasoning
            frozenPlannerResultJson = $FrozenPlannerResultJson
            stopAfterPlanning = $false
        }

        if (-not [string]::IsNullOrWhiteSpace($SupervisorSourceMissionId)) {
            $request.supervisorSourceMissionId = $SupervisorSourceMissionId
        }

        $watch = [Diagnostics.Stopwatch]::StartNew()
        $created = Send-WorkerRequest -Request $request
        if (-not $created.success) {
            throw "createMission for arm $Arm failed: $($created.error)"
        }

        $missionId = [string]$created.mission.mission.id
        $terminal = Wait-Mission -MissionId $missionId -StopStatuses @(
            $MissionStatusWaitingForQuota,
            $MissionStatusWaitingForApproval,
            $MissionStatusPaused,
            $MissionStatusNeedsHumanAttention,
            $MissionStatusFailed,
            $MissionStatusCompleted
        )
        $watch.Stop()

        Write-JsonArtifact -Path (Join-Path $ArtifactsRoot "$Arm-mission.json") -Value $terminal

        $post = Get-PostArmAllowance -Arm $Arm
        $visibleAfter = Read-Host "Arm $($Arm): enter visible UI remaining percent after the settled read (or leave blank)"
        $acceptance = Invoke-ExternalAcceptance -Arm $Arm

        $missionStatus = [int]$terminal.mission.mission.status
        $validationCycles = @(
            $terminal.mission.tasks |
                Where-Object {
                    [int]$_.kind -eq $MissionTaskKindValidateMission -and
                    [int]$_.status -eq $TaskStatusCompleted
                }
        ).Count

        $supervisor = Get-RoleTelemetry -Telemetry $terminal.telemetry -Role 0
        $workerRole = Get-RoleTelemetry -Telemetry $terminal.telemetry -Role 1
        $validator = Get-RoleTelemetry -Telemetry $terminal.telemetry -Role 2

        $result = [ordered]@{
            arm = $Arm
            missionId = $missionId
            missionStatus = $missionStatus
            missionStatusName = Get-MissionStatusName $missionStatus
            elapsedSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 3)
            fiveHourAllowance = [ordered]@{
                before = $before
                immediateAfter = $post.immediate
                after10Seconds = $post.after10
                after30Seconds = $post.after30
            }
            visibleUiRemaining = [ordered]@{
                before = $visibleBefore
                after = $visibleAfter
            }
            telemetry = $terminal.telemetry
            supervisor = $supervisor
            worker = $workerRole
            validator = $validator
            validationCycles = $validationCycles
            externalAcceptance = $acceptance
            functionallyEligible = (
                $missionStatus -eq $MissionStatusCompleted -and
                $acceptance.passed
            )
        }

        Write-JsonArtifact -Path (Join-Path $ArtifactsRoot "$Arm-summary.json") -Value $result
        return [pscustomobject]$result
    }
    finally {
        Stop-BenchmarkWorker -Process $worker
    }
}

foreach ($command in @("dotnet", "git", "wsl.exe")) {
    if ($null -eq (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required command '$command' was not found."
    }
}

$LoopGolemRoot = (Resolve-Path -LiteralPath $LoopGolemRoot).Path
$Workspace = (Resolve-Path -LiteralPath $Workspace).Path
$GoalFile = (Resolve-Path -LiteralPath $GoalFile).Path
$AcceptanceScript = (Resolve-Path -LiteralPath $AcceptanceScript).Path

if ($LoopGolemRoot -eq $Workspace) {
    throw "Workspace must be a disposable target repository, not the LoopGolem repository."
}

$Goal = [IO.File]::ReadAllText($GoalFile)
if ([string]::IsNullOrWhiteSpace($Goal)) {
    throw "Goal file is empty."
}
$goalSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $GoalFile).Hash.ToLowerInvariant()

$loopDirty = (& git -C $LoopGolemRoot status --porcelain)
Assert-LastExitCode "LoopGolem git status"
if (-not [string]::IsNullOrWhiteSpace(($loopDirty -join [Environment]::NewLine))) {
    throw "LoopGolem working tree must be clean before the benchmark."
}

$LoopGolemCommit = (& git -C $LoopGolemRoot rev-parse HEAD).Trim()
Assert-LastExitCode "LoopGolem rev-parse"

$TargetBase = (& git -C $Workspace rev-parse HEAD).Trim()
Assert-LastExitCode "Target rev-parse"

if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
    $runId = [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss")
    $ArtifactsRoot = Join-Path (Split-Path $LoopGolemRoot -Parent) "loopgolem-benchmark-v3-$runId"
}
else {
    $ArtifactsRoot = [IO.Path]::GetFullPath($ArtifactsRoot)
}

New-Item -ItemType Directory -Force -Path $ArtifactsRoot | Out-Null
$StateDirectory = Join-Path $ArtifactsRoot "state"
New-Item -ItemType Directory -Force -Path $StateDirectory | Out-Null

$env:LOOPGOLEM_STATE_DIR = $StateDirectory
$env:LOOPGOLEM_WSL_DISTRO = $Distro

if (Test-WorkerReachable) {
    throw "A LoopGolem Worker is already running. Stop it before continuing so the benchmark cannot attach to unrelated state."
}

& wsl.exe -d $Distro --exec python3 --version | Out-Host
Assert-LastExitCode "WSL python3 preflight"

& wsl.exe -d $Distro --exec bash -lc "command -v codex && codex --version && codex login status" | Out-Host
Assert-LastExitCode "WSL Codex preflight"

Push-Location $LoopGolemRoot
try {
    & dotnet restore LoopGolem.sln
    Assert-LastExitCode "dotnet restore"

    & dotnet build LoopGolem.sln --configuration Release --no-restore "-p:TreatWarningsAsErrors=true"
    Assert-LastExitCode "strict Release build"

    & dotnet run --project src/LoopGolem.Worker/LoopGolem.Worker.csproj --configuration Release --no-build -- --self-test
    Assert-LastExitCode "Worker self-test"
}
finally {
    Pop-Location
}

Reset-TargetWorkspace

$config = [ordered]@{
    loopGolemCommit = $LoopGolemCommit
    targetBaseCommit = $TargetBase
    goalFile = $GoalFile
    goalSha256 = $goalSha256
    workspace = $Workspace
    wslDistribution = $Distro
    isolatedStateDirectory = $StateDirectory
    acceptanceScript = $AcceptanceScript
}
Write-JsonArtifact -Path (Join-Path $ArtifactsRoot "benchmark-config.json") -Value $config

Write-Host ""
Write-Host "Deterministic gate passed. No benchmark model call has run yet." -ForegroundColor Green
Write-Host "LoopGolem: $LoopGolemCommit"
Write-Host "Target base: $TargetBase"
Write-Host "Goal SHA-256: $goalSha256"
Write-Host "Artifacts: $ArtifactsRoot"
Write-Host ""

$confirmation = Read-Host "Type RUN to spend allowance on the single Planner call and measured arms"
if ($confirmation -cne "RUN") {
    throw "Benchmark cancelled before any Planner/model call."
}

$planWorker = $null
try {
    $planWorker = Start-BenchmarkWorker -Label "plan"

    $codexStatus = Send-WorkerRequest -Request ([ordered]@{ type = "getCodexStatus" })
    Write-JsonArtifact -Path (Join-Path $ArtifactsRoot "codex-status.json") -Value $codexStatus

    if (-not $codexStatus.success -or -not $codexStatus.codexStatus.available -or -not $codexStatus.codexStatus.chatGptAuthenticated) {
        throw "Codex runtime is not ready."
    }

    $planCreated = Send-WorkerRequest -Request ([ordered]@{
        type = "createMission"
        goal = $Goal
        workspacePath = $Workspace
        executionMode = $MissionExecutionModeCodex
        stopAfterPlanning = $true
    })

    if (-not $planCreated.success) {
        throw "Plan-only mission creation failed: $($planCreated.error)"
    }

    $SourceMissionId = [string]$planCreated.mission.mission.id
    $planTerminal = Wait-Mission -MissionId $SourceMissionId -StopStatuses @(
        $MissionStatusPaused,
        $MissionStatusNeedsHumanAttention,
        $MissionStatusFailed,
        $MissionStatusCompleted
    )

    Write-JsonArtifact -Path (Join-Path $ArtifactsRoot "plan-only-mission.json") -Value $planTerminal

    if ([int]$planTerminal.mission.mission.status -ne $MissionStatusPaused) {
        throw "Plan-only mission did not pause after planning; status=$([int]$planTerminal.mission.mission.status)."
    }

    $planTask = @(
        $planTerminal.mission.tasks |
            Where-Object {
                [int]$_.kind -eq $MissionTaskKindPlanMission -and
                [int]$_.status -eq $TaskStatusCompleted
            }
    ) | Select-Object -First 1

    if ($null -eq $planTask -or [string]::IsNullOrWhiteSpace($planTask.resultDetails)) {
        throw "Completed PlanMission task has no persisted PlannerResult."
    }

    $FrozenPlannerResultJson = [string]$planTask.resultDetails
    $plannerPath = Join-Path $ArtifactsRoot "planner-result.json"
    Write-Utf8NoBom -Path $plannerPath -Text $FrozenPlannerResultJson
    $PlannerSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $plannerPath).Hash.ToLowerInvariant()

    $plannerObject = $FrozenPlannerResultJson | ConvertFrom-Json
    if ([string]$plannerObject.baseCommit -ne $TargetBase) {
        throw "Frozen PlannerResult baseCommit '$($plannerObject.baseCommit)' does not equal target base '$TargetBase'."
    }

    Write-Host "Frozen PlannerResult SHA-256: $PlannerSha256" -ForegroundColor Green
}
finally {
    Stop-BenchmarkWorker -Process $planWorker
}

Reset-TargetWorkspace

$ArmF = Invoke-MeasuredArm -Arm "F" -WorkerContext $WorkerContextSupervisorFork -WorkerReasoning $WorkerReasoningLow -SupervisorSourceMissionId $SourceMissionId

Reset-TargetWorkspace

$ArmH = Invoke-MeasuredArm -Arm "H" -WorkerContext $WorkerContextFresh -WorkerReasoning $WorkerReasoningHigh

$final = [ordered]@{
    loopGolemCommit = $LoopGolemCommit
    targetBaseCommit = $TargetBase
    goalSha256 = $goalSha256
    frozenPlannerResultSha256 = $PlannerSha256
    sourcePlanOnlyMissionId = $SourceMissionId
    armF = $ArmF
    armH = $ArmH
    bothFunctionallyEligible = (
        $ArmF.functionallyEligible -and
        $ArmH.functionallyEligible
    )
}

Write-JsonArtifact -Path (Join-Path $ArtifactsRoot "benchmark-v3-summary.json") -Value $final

Write-Host ""
Write-Host "Benchmark capture complete." -ForegroundColor Green
Write-Host "Frozen PlannerResult SHA-256: $PlannerSha256"
Write-Host "F: $($ArmF.missionStatusName), acceptance=$($ArmF.externalAcceptance.passed), 5h $($ArmF.fiveHourAllowance.before.usedPercent)% -> $($ArmF.fiveHourAllowance.after30Seconds.usedPercent)%"
Write-Host "H: $($ArmH.missionStatusName), acceptance=$($ArmH.externalAcceptance.passed), 5h $($ArmH.fiveHourAllowance.before.usedPercent)% -> $($ArmH.fiveHourAllowance.after30Seconds.usedPercent)%"

if (-not $final.bothFunctionallyEligible) {
    Write-Host "NO WINNER CONCLUSION: at least one arm failed the functional eligibility gate." -ForegroundColor Yellow
}
else {
    Write-Host "Both arms passed the functional gate. Compare the captured allowance/quality/latency telemetry before selecting a product default." -ForegroundColor Cyan
}

Write-Host "Artifacts: $ArtifactsRoot"
