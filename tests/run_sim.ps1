<#
.SYNOPSIS
  UartHub 시뮬레이션 시나리오 실행(하드웨어 없음, FakeBoard 만).

.DESCRIPTION
  로컬 사본(build.sh 의 %LOCALAPPDATA%\uart_hub_dev\<ver>\)에서 `pwsh -NoProfile -File tests\run_sim.ps1 [-Version <ver>] [-Exe <UartHub.exe>] [-Ids "a,b"]`.
  시험 DLL 은 같은 빌드의 lib\UartHub.Core.dll·lib\UartHub.Tests.dll(계획 H2). 프로세스를 띄우는 시나리오는 -Exe(기본 <사본>\UartHub.exe)의 sup·core·cli 를 띄운다.
  시작할 때 로드한 Core DLL 의 MVID 와 `UartHub.exe version --json` 의 core_mvid 를 비교해 `# mvid=… match` 를 찍는다(다르면 exit 1).
  출력: 시나리오마다 `[PASS] <id> <설명>` 또는 `[FAIL] <id> <이유>` + 세부 줄, 마지막에 합계.
  FAIL 이 하나라도 있으면 종료코드 1. 로그 루트는 %TEMP%\uart_hub_test\<실행시각>\<id>\.
#>
param(
  [string]$Version,
  [string]$Ids = '',
  [string]$Exe,
  # "k/n": 등록 순서의 i 번째(0부터) 시나리오 중 i % n == k-1 인 것만(-Ids 가 비었을 때). ralph 피드백 게이트의 명령당 10분 제한 때문에 나눠 돈다.
  [string]$Part = ''
)
$ErrorActionPreference = 'Stop'
# 시뮬용 가상 제품 프로필(FakeBoard 의 로더 표시·U-Boot 프롬프트·앱 오류 줄과 짝). 이 프로세스·띄우는 UartHub.exe 가 모두 물려받는다.
$env:UART_HUB_PROFILE = Join-Path $PSScriptRoot 'profile.test.json'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$PSStyle.OutputRendering = 'PlainText'

$root = Split-Path -Parent $PSScriptRoot
$lib = Join-Path $root 'lib'
if (-not $Version) { $Version = Split-Path -Leaf $root }
if (-not $Exe) { $Exe = Join-Path $root 'UartHub.exe' }
Add-Type -Path (Join-Path $lib 'UartHub.Core.dll')
Add-Type -Path (Join-Path $lib 'UartHub.Tests.dll')
[UartHub.Testing.TestEnv]::UartHubExe = $Exe

$mutex = [System.Threading.Mutex]::new($false, 'Local\uart_hub_test_runsim')
$own = $false
try { $own = $mutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $own = $true }
if (-not $own) {
  [Console]::Out.WriteLine('[FAIL] harness 다른 run_sim 이 실행 중(mutex Local\uart_hub_test_runsim)')
  exit 1
}
try {
  $testRoot = Join-Path ([IO.Path]::GetTempPath()) 'uart_hub_test'
  $list = @($Ids -split '[,\s]+' | Where-Object { $_ })
  if ($Part -and $list.Count -eq 0) {
    if ($Part -notmatch '^(\d+)/(\d+)$' -or [int]$Matches[1] -lt 1 -or [int]$Matches[1] -gt [int]$Matches[2]) {
      [Console]::Out.WriteLine("[FAIL] harness -Part 형식 오류: '$Part' (k/n, 1 <= k <= n)")
      exit 1
    }
    $pk = [int]$Matches[1]; $pn = [int]$Matches[2]
    $allIds = [UartHub.Testing.ScenarioRunner]::Ids()
    $list = @(for ($i = 0; $i -lt $allIds.Length; $i++) { if ($i % $pn -eq $pk - 1) { $allIds[$i] } })
    [Console]::Out.WriteLine("# part=$Part ids=$($list -join ',')")
  }
  [Console]::Out.WriteLine("# UartHub $Version")
  [Console]::Out.WriteLine("# exe=$Exe")
  [Console]::Out.WriteLine('# 문구의 uart.ps1 = UartHub.exe cli, pwsh(감독·코어) = UartHub.exe sup·core (계획 T1 H2)')
  # in-process 시나리오의 Core DLL 이 exe 에 든 것과 같은 컴파일 결과인지(K10)
  $mvid = [UartHub.Hub].Module.ModuleVersionId.ToString()
  $exeMvid = $null
  try { $exeMvid = (& $Exe version --json | ConvertFrom-Json).core_mvid } catch { }
  if ($exeMvid -ne $mvid) {
    [Console]::Out.WriteLine("# mvid=$mvid != exe core_mvid=$exeMvid")
    [Console]::Out.WriteLine('[FAIL] harness lib\UartHub.Core.dll 과 UartHub.exe 의 Core 가 다른 컴파일 결과')
    exit 1
  }
  [Console]::Out.WriteLine("# mvid=$mvid match")
  $fail = [UartHub.Testing.ScenarioRunner]::Run([string[]]$list, $testRoot, [Console]::Out)
}
finally {
  $mutex.ReleaseMutex()
  $mutex.Dispose()
}
if ($fail -gt 0) { exit 1 }
exit 0
