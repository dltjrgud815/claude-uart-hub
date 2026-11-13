<#
.SYNOPSIS
  WSL 계약 테스트의 Windows 쪽 하네스(계획 §4 S6 "WSL 계약 테스트"). tests/run_sim_wsl.sh 가 백그라운드로 띄운다.

.DESCRIPTION
  로컬 사본(build.sh 의 %LOCALAPPDATA%\uart_hub_dev\<ver>\)에서 `pwsh -NoProfile -File tests\run_sim_wsl.ps1 -Version <ver> -Work <작업 폴더> [-Exe <UartHub.exe>]`.
  1. 시험 허브: <Work>\hub(토큰·config.json), <Work>\logs(로그 루트). 감독 `UartHub.exe sup --no-tray --sim` 을 자식으로 띄우고
     감독이 같은 exe 의 코어(`core --sim --sim-dll`, 코어 안 FakeBoard)를 띄운다 — 실제 COM·FTDI 열거 없음(vids []).
     격리: mutex Local\uart_hub_test_{sup,core,update}_wsl_<난수>, 제어 포트 -CtlPort(17400), 케이블 -BasePort(17450).
  2. 케이블 attached → probe --user-ok → AT_PROMPT 가 되면 <Work>\ready.txt(HUB_DIR= CTL= PORT=)를 쓴다.
  3. run_sim_wsl.sh 가 WSL 래퍼로 케이스를 돌리고 <Work>\wsl_done 을 만들면, <Work>\cases.json(문자열 배열의 배열)의 각 케이스를
     `UartHub.exe cli` 프로세스로 실행해(ProcessStartInfo.ArgumentList — interop 없음) <Work>\win\<n>.{out,err,rc} 에 쓴다. <Work>\abort 면 건너뛴다.
     Windows 기준 호출에는 UART_PATH_STYLE 을 두지 않는다(경로 필드는 Windows 형식).
  4. 끝(정상·실패 모두): 감독을 끝내고, 명령줄에 이 허브 폴더가 든 UartHub.exe(sup·core)를 모두 kill 한 뒤 잔존 수를 <Work>\cleanup.txt 에 쓴다.
  출력: 진행 줄(stdout). 종료코드 0 = 준비·기준 호출·정리 모두 정상, 1 = 그 밖.
#>
param(
  [Parameter(Mandatory)][string]$Version,
  [Parameter(Mandatory)][string]$Work,
  [string]$Exe,
  [int]$CtlPort = 17400,
  [int]$BasePort = 17450,
  [int]$WaitWslS = 900
)
$ErrorActionPreference = 'Stop'
# 시뮬용 가상 제품 프로필(FakeBoard 의 로더 표시·U-Boot 프롬프트·앱 오류 줄과 짝). 이 프로세스·띄우는 UartHub.exe 가 모두 물려받는다.
$env:UART_HUB_PROFILE = Join-Path $PSScriptRoot 'profile.test.json'
$env:UART_HUB_LANG = 'ko'   # 시험 기대값은 한국어 문구 — Windows 언어와 무관하게 한국어로 고정(영어판 시험은 시나리오 안에서 L.Use("en")·환경변수로 따로)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$PSStyle.OutputRendering = 'PlainText'
[Console]::Out.NewLine = "`n"

$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'UartHub.exe' }
$testsDll = Join-Path $root 'lib\UartHub.Tests.dll'
$hubDir = Join-Path $Work 'hub'
$logRoot = Join-Path $Work 'logs'
$utf8 = [Text.UTF8Encoding]::new($false)
$stdout = [Console]::Out
$stderr = [Console]::Error
function Say([string]$s) { $stdout.Write('[' + (Get-Date -Format 'HH:mm:ss.fff') + '] ' + $s + "`n"); $stdout.Flush() }

# UartHub.exe cli 를 프로세스로 실행하고 (rc, stdout, stderr). 인자는 ArgumentList 로(pwsh 의 & 네이티브 인자 전달은 빈 인자·따옴표를 다르게 다룰 수 있다).
function Invoke-Uart([string[]]$argv) {
  $psi = [Diagnostics.ProcessStartInfo]::new($Exe)
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.StandardOutputEncoding = $utf8
  $psi.StandardErrorEncoding = $utf8
  $psi.ArgumentList.Add('cli')
  foreach ($a in $argv) { $psi.ArgumentList.Add([string]$a) }
  $p = [Diagnostics.Process]::Start($psi)
  try {
    $so = $p.StandardOutput.ReadToEndAsync()
    $se = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit(120000)) { try { $p.Kill() } catch { }; throw ('cli 시간 초과: ' + ($argv -join ' ')) }
    $p.WaitForExit()
    [pscustomobject]@{ Rc = $p.ExitCode; Out = $so.Result; Err = $se.Result }
  } finally { $p.Dispose() }
}

function Get-HubProcs([string]$kind) {
  @(Get-CimInstance Win32_Process -Filter "Name = 'UartHub.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($hubDir, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and $_.CommandLine.IndexOf($kind, [StringComparison]::OrdinalIgnoreCase) -ge 0 } |
    ForEach-Object { [int]$_.ProcessId })
}

function Write-Atomic([string]$path, [string]$text) {
  $tmp = "$path.tmp$PID"
  [IO.File]::WriteAllText($tmp, $text, $utf8)
  [IO.File]::Move($tmp, $path, $true)
}

$ok = $true
$sup = $null
try {
  New-Item -ItemType Directory -Force -Path $hubDir, $logRoot, (Join-Path $Work 'win') | Out-Null
  $rnd = [guid]::NewGuid().ToString('N').Substring(0, 6)
  $supMutex = "Local\uart_hub_test_sup_wsl_$rnd"
  $coreMutex = "Local\uart_hub_test_core_wsl_$rnd"
  $updMutex = "Local\uart_hub_test_update_wsl_$rnd"
  $cfg = [ordered]@{
    log_root = $logRoot; hub_dir = $hubDir; base_port = $BasePort; control_port = $CtlPort; vids = @()
    scan_interval_ms = 50; read_timeout_ms = 50; writer_wait_ms = 20; ring_mb = 1; queue_mb = 16
    idle_s = 0.3; quiet_ms = 600; default_timeout_s = 10
  }
  [IO.File]::WriteAllText((Join-Path $hubDir 'config.json'), ($cfg | ConvertTo-Json), $utf8)
  $spec = Join-Path $Work 'sim_spec.json'
  [IO.File]::WriteAllText($spec, '{"boards":[{"cable":"SIMCABWSL","com":"COM400","hostname":"SIMWSL"}]}', $utf8)

  $psi = [Diagnostics.ProcessStartInfo]::new($Exe)
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $psi.WorkingDirectory = $root
  foreach ($a in @('sup', '--no-tray', '--hub-dir', $hubDir, '--sup-mutex', $supMutex, '--core-mutex', $coreMutex, '--update-mutex', $updMutex,
                   '--sim', $spec, '--sim-dll', $testsDll, '--tick-ms', '100')) { $psi.ArgumentList.Add($a) }
  $sup = [Diagnostics.Process]::Start($psi)
  Say "감독 시작 pid=$($sup.Id) hub_dir=$hubDir mutex=$supMutex/$coreMutex ctl=$CtlPort base=$BasePort v=$Version exe=$Exe"

  $env:UART_HUB_DIR = $hubDir
  $env:UART_HUB_CONTROL_PORT = [string]$CtlPort
  Remove-Item Env:UART_PATH_STYLE -ErrorAction SilentlyContinue
  Remove-Item Env:UART_WSL_MOUNT -ErrorAction SilentlyContinue

  # ---- 준비: attached → probe → AT_PROMPT
  $port = $null
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while (-not $port -and $sw.ElapsedMilliseconds -lt 30000) {
    if ($sup.HasExited) { throw "감독이 먼저 끝남(exit=$($sup.ExitCode))" }
    if (Test-Path -LiteralPath (Join-Path $hubDir 'token')) {
      $r = Invoke-Uart @('ls', '--json')
      if ($r.Rc -eq 0) {
        $c = @((ConvertFrom-Json $r.Out).cables) | Where-Object { $_.cable -eq 'SIMCABWSL' -and $_.status -eq 'attached' }
        if ($c) { $port = [string]$c[0].port }
      }
    }
    if (-not $port) { Start-Sleep -Milliseconds 200 }
  }
  if (-not $port) { throw '30초 안에 SIMCABWSL attached 가 안 됨' }
  Say "attached port=$port ($($sw.ElapsedMilliseconds)ms)"
  $state = $null
  $sw.Restart()
  while ($state -ne 'AT_PROMPT' -and $sw.ElapsedMilliseconds -lt 15000) {
    $null = Invoke-Uart @('probe', $port, '--user-ok', '--json')
    Start-Sleep -Milliseconds 400
    $r = Invoke-Uart @('status', $port, '--json')
    if ($r.Rc -eq 0) { $state = (ConvertFrom-Json $r.Out).state }
  }
  if ($state -ne 'AT_PROMPT') { throw "AT_PROMPT 가 안 됨(state=$state)" }
  Say "AT_PROMPT ($($sw.ElapsedMilliseconds)ms)"
  Write-Atomic (Join-Path $Work 'ready.txt') ("HUB_DIR=$hubDir`nCTL=$CtlPort`nPORT=$port`nSUP_PID=$($sup.Id)`n")

  # ---- WSL 쪽이 끝나기를 기다린 뒤 Windows 기준 호출
  $sw.Restart()
  while (-not (Test-Path -LiteralPath (Join-Path $Work 'wsl_done')) -and -not (Test-Path -LiteralPath (Join-Path $Work 'abort'))) {
    if ($sw.Elapsed.TotalSeconds -gt $WaitWslS) { throw "wsl_done 대기 초과($WaitWslS 초)" }
    if ($sup.HasExited) { throw "감독이 먼저 끝남(exit=$($sup.ExitCode))" }
    Start-Sleep -Milliseconds 100
  }
  if (Test-Path -LiteralPath (Join-Path $Work 'abort')) { throw 'abort — WSL 쪽이 중단함' }
  $cases = ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $Work 'cases.json'))) -NoEnumerate
  for ($i = 0; $i -lt $cases.Count; $i++) {
    [string[]]$argv = @($cases[$i] | ForEach-Object { [string]$_ })
    $r = Invoke-Uart $argv
    $n = '{0:d2}' -f ($i + 1)
    [IO.File]::WriteAllText((Join-Path $Work "win\$n.out"), $r.Out, $utf8)
    [IO.File]::WriteAllText((Join-Path $Work "win\$n.err"), $r.Err, $utf8)
    [IO.File]::WriteAllText((Join-Path $Work "win\$n.rc"), [string]$r.Rc, $utf8)
    Say "win $n rc=$($r.Rc)"
  }
  if ($sup.HasExited) { throw "감독이 시험 중 끝남(exit=$($sup.ExitCode))" }
} catch {
  $ok = $false
  Say ('하네스 오류: ' + $_.Exception.Message)
} finally {
  # ---- 정리: 감독 먼저(코어를 다시 띄우지 못하게), 그다음 이 허브 폴더의 UartHub.exe(sup·core) 전부
  $left = 0
  for ($round = 0; $round -lt 3; $round++) {
    if ($sup -and -not $sup.HasExited) { try { $sup.Kill(); $null = $sup.WaitForExit(5000) } catch { } }
    foreach ($id in @(Get-HubProcs ' sup ') + @(Get-HubProcs ' core ')) {
      try { $p = Get-Process -Id $id -ErrorAction Stop; $p.Kill(); $null = $p.WaitForExit(5000) } catch { }
    }
    $ls = @(Get-HubProcs ' sup ').Count
    $lc = @(Get-HubProcs ' core ').Count
    $left = $ls + $lc
    if ($left -eq 0) { break }
    Start-Sleep -Milliseconds 300
  }
  try { Write-Atomic (Join-Path $Work 'cleanup.txt') ("LEFTOVER_SUP=$ls`nLEFTOVER_CORE=$lc`n") } catch { }
  Say "정리: 잔존 감독 $ls · 코어 $lc"
  if ($left -ne 0) { $ok = $false }
}
if ($ok) { exit 0 }
exit 1
