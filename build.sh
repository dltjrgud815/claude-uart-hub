#!/usr/bin/env bash
# UartHub 빌드(WSL, .NET SDK 10 은 ~/.dotnet). 세 프로젝트를 한 번만 컴파일하고 그 결과를 publish·lib 에 같이 쓴다.
#   사용: build.sh [<17자리 버전>]   새로 빌드(기본 버전 = 지금 시각)
#         build.sh --pin <ver>        배포 후보 고정(빌드 안 함): out/<ver>/UartHub.exe → out/release/UartHub-<ver>.exe + .sha256,
#                                     같은 바이트의 Windows 사본 %LOCALAPPDATA%\uart_hub_dev\release-<ver>\(정리 대상 아님)
#         build.sh --use <ver>        무빌드 시험용(빌드 안 함): 지정 빌드의 Windows 사본을 찾아 out/<ver> 와 같은 바이트인지 확인하고 아래 마지막 줄만 낸다
#                                     (release-<ver> → <ver> 순, 둘 다 없으면 out/<ver> 에서 <ver> 사본을 다시 만든다)
#         build.sh --package <ver>    배포 폴더(계획 부록 D.2, X7): 시험을 마친 고정판 out/release/UartHub-<ver>.exe(.sha256 과 대조) →
#                                     out/package/UartHub_<ver>/{INSTALL_UART_HUB.md, UartHub.exe(이름만 바꿈), USAGE_ONE_PAGE.md, GUIDE.md, [UartHub.profile.json], SHA256SUMS} + out/package/UartHub_<ver>.zip.
#                                     제품 프로필: UART_HUB_PACKAGE_PROFILE=<경로>(빈 값 = 넣지 않음), 없으면 비공개 internal/profile.json 이 있을 때만 넣는다.
#                                     <ver> 는 꼭 적고, --pin 으로 고정돼 있어야 한다(빌드·고정 안 함 — 고정 → 5종 시험 → 묶음 순서, §5).
#                                     마지막 줄 PACKAGE= / ZIP= / EXE_SHA256= / ZIP_SHA256= (전달 메시지에 두 해시를 적는다 — SHA256SUMS 는 깨짐만, 바꿔치기는 이 값으로 확인)
# 산출: out/<ver>/{UartHub.exe, lib/UartHub.Core.dll, lib/UartHub.Tests.dll, vB/UartHub.exe(버전만 <ver>+1 인 시험용 두 번째 빌드)},
#       Windows 사본 %LOCALAPPDATA%\uart_hub_dev\<ver>\ (최근 3개, + tests\*.ps1·golden).
#       정리: 17자리 이름 폴더만, 최근 3개 밖이고 out/release/UartHub-<ver>.exe 로 고정되지 않은 것만 지운다(release-* 사본은 대상 아님).
# 마지막 줄: [BUILD=reuse] / VERSION= / EXE_WIN= / EXE_SHA256=(EXE_WIN 의 해시)
set -euo pipefail
HUB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET="$HOME/.dotnet/dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
KEEP=3
REL="$HUB_DIR/out/release"

# Windows 로컬 사본 위치(UART_HUB_DEV_WIN 이 있으면 그것)
resolve_dev() {
  if [[ -z "${UART_HUB_DEV_WIN:-}" ]]; then
    local lad
    lad="$(cd /mnt/c && cmd.exe /c 'echo %LOCALAPPDATA%' 2>/dev/null | tr -d '\r')"
    [[ -n "$lad" && "$lad" != *%* ]] || { echo "build: LOCALAPPDATA 를 얻지 못함" >&2; exit 2; }
    UART_HUB_DEV_WIN="${lad}\\uart_hub_dev"
  fi
  DEV="$(wslpath -u "$UART_HUB_DEV_WIN")"
  mkdir -p "$DEV"
}
# out/<ver> → Windows 사본 $1. 개발 사본에만 시험 하네스를 함께 둔다(pwsh 는 \\wsl.localhost 의 .ps1 을 실행 정책 때문에 못 돌린다, S0-1).
# golden 은 lib\..\tests\golden(Golden.Dir) — 비공개 internal/golden 이 있을 때만(공개 저장소에는 없다).
# profile.test.json = 시뮬용 가상 제품 프로필(run_sim*.ps1 이 UART_HUB_PROFILE 로 준다).
stage_copy() {
  rm -rf "$1"; cp -r "$OUT" "$1"
  mkdir -p "$1/tests"
  cp "$HUB_DIR"/tests/*.ps1 "$HUB_DIR/tests/profile.test.json" "$1/tests/"
  if [[ -d "$HUB_DIR/internal/golden" ]]; then cp -r "$HUB_DIR/internal/golden" "$1/tests/"; fi
}
pinned() { [[ -f "$REL/UartHub-$1.exe" ]]; }
sha() { sha256sum "$1" | cut -d' ' -f1; }

# ---- --package [<ver>]: 배포 폴더 + zip(부록 D.2). exe 는 고정판 바이트 그대로(시험한 바이트 = 배포한 바이트, §7.3)
if [[ "${1:-}" == "--package" ]]; then
  VER="${2:-}"
  [[ "$VER" =~ ^[0-9]{17}$ ]] || { echo "build: --package <시험한 17자리 버전>: '$VER' — 버전 없이 묶지 않는다(고정 → 5종 시험 → 묶음)" >&2; exit 2; }
  pinned "$VER" || { echo "build: $VER 가 고정되지 않음 — 먼저 build.sh --pin $VER 뒤 5종 시험(--exe-version $VER)" >&2; exit 2; }
  EXE="$REL/UartHub-$VER.exe"
  SHA="$(sha "$EXE")"
  [[ "$(cut -d' ' -f1 "$REL/UartHub-$VER.sha256")" == "$SHA" ]] || { echo "build: 고정판 해시가 .sha256 과 다름: $EXE" >&2; exit 2; }
  PK="$HUB_DIR/out/package"; D="$PK/UartHub_$VER"
  rm -rf "$D" "$D.zip"; mkdir -p "$D"
  cp "$EXE" "$D/UartHub.exe"
  cp "$HUB_DIR/INSTALL_UART_HUB.md" "$HUB_DIR/USAGE_ONE_PAGE.md" "$HUB_DIR/GUIDE.md" "$D/"
  # 제품 프로필: UART_HUB_PACKAGE_PROFILE(경로, 빈 값 = 넣지 않음) → 없으면 비공개 internal/profile.json(있을 때만). exe 옆 UartHub.profile.json 으로.
  PROF="${UART_HUB_PACKAGE_PROFILE-$HUB_DIR/internal/profile.json}"
  sums=(UartHub.exe INSTALL_UART_HUB.md USAGE_ONE_PAGE.md GUIDE.md)
  if [[ -n "$PROF" && -f "$PROF" ]]; then cp "$PROF" "$D/UartHub.profile.json"; sums+=(UartHub.profile.json); fi
  (cd "$D" && sha256sum "${sums[@]}" > SHA256SUMS && sha256sum -c --quiet SHA256SUMS) || { echo "build: SHA256SUMS 확인 실패" >&2; exit 2; }
  [[ "$(sha "$D/UartHub.exe")" == "$SHA" ]] || { echo "build: 배포 폴더 exe 가 고정판과 다름" >&2; exit 2; }
  (cd "$PK" && zip -q -r -X "UartHub_$VER.zip" "UartHub_$VER")
  echo "PACKAGE=$D ($(ls "$D" | tr '\n' ' '))"
  echo "ZIP=$D.zip ($(stat -c %s "$D.zip") bytes)"
  echo "EXE_SHA256=$SHA"
  echo "ZIP_SHA256=$(sha "$D.zip")"
  exit 0
fi

# ---- --pin / --use: 빌드하지 않는다
if [[ "${1:-}" == "--pin" || "${1:-}" == "--use" ]]; then
  mode="${1#--}"; VER="${2:-}"
  [[ "$VER" =~ ^[0-9]{17}$ ]] || { echo "build: --$mode <17자리 버전>: '$VER'" >&2; exit 2; }
  OUT="$HUB_DIR/out/$VER"
  for f in UartHub.exe lib/UartHub.Core.dll lib/UartHub.Tests.dll vB/UartHub.exe; do
    [[ -f "$OUT/$f" ]] || { echo "build: 지정 빌드에 파일 없음: $OUT/$f" >&2; exit 2; }
  done
  resolve_dev
  if [[ $mode == pin ]]; then
    mkdir -p "$REL"
    cp "$OUT/UartHub.exe" "$REL/UartHub-$VER.exe"
    (cd "$REL" && sha256sum "UartHub-$VER.exe" > "UartHub-$VER.sha256")
    stage_copy "$DEV/release-$VER"
    echo "RELEASE=$REL/UartHub-$VER.exe ($(stat -c %s "$REL/UartHub-$VER.exe") bytes)"
  fi
  if [[ -d "$DEV/release-$VER" ]]; then
    D="$DEV/release-$VER"; DW="${UART_HUB_DEV_WIN}\\release-$VER"
  else
    D="$DEV/$VER"; DW="${UART_HUB_DEV_WIN}\\$VER"
    [[ -f "$D/UartHub.exe" ]] || stage_copy "$D"
  fi
  # 시험할 사본 = 지정 빌드(같은 바이트). 고정판이 있으면 그 .sha256 과도 같아야 한다.
  for f in UartHub.exe lib/UartHub.Core.dll lib/UartHub.Tests.dll vB/UartHub.exe; do
    cmp -s "$OUT/$f" "$D/$f" || { echo "build: Windows 사본이 out/$VER 와 다름: $D/$f" >&2; exit 2; }
  done
  SHA="$(sha "$D/UartHub.exe")"
  if pinned "$VER"; then
    [[ "$(cut -d' ' -f1 "$REL/UartHub-$VER.sha256")" == "$SHA" && "$(sha "$REL/UartHub-$VER.exe")" == "$SHA" ]] \
      || { echo "build: 고정판 해시가 시험 사본과 다름: $REL/UartHub-$VER.{exe,sha256}" >&2; exit 2; }
  fi
  echo "BUILD=reuse"
  echo "VERSION=$VER"
  echo "EXE_WIN=$DW\\UartHub.exe"
  echo "EXE_SHA256=$SHA"
  exit 0
fi

VER="${1:-$(date +%Y%m%d%H%M%S%3N)}"
[[ "$VER" =~ ^[0-9]{17}$ ]] || { echo "build: 버전은 17자리 숫자여야 함: $VER" >&2; exit 2; }

GEN="$HUB_DIR/artifacts/gen"
mkdir -p "$GEN"
printf 'namespace UartHub { public static class BuildInfo { public const string Version = "%s"; } }\n' "$VER" > "$GEN/BuildInfo.g.cs"

cd "$HUB_DIR"
"$DOTNET" build app/UartHub.csproj -c Release -r win-x64 -p:Version=0.0.0 -nologo -v:m
"$DOTNET" build testing/UartHub.Tests.csproj -c Release -r win-x64 -nologo -v:m
"$DOTNET" publish app/UartHub.csproj -c Release -r win-x64 --no-build -nologo -v:m

PUB="$HUB_DIR/artifacts/publish/UartHub/release_win-x64"
BIN_CORE="$HUB_DIR/artifacts/bin/UartHub.Core/release_win-x64"
BIN_TESTS="$HUB_DIR/artifacts/bin/UartHub.Tests/release_win-x64"

OUT="$HUB_DIR/out/$VER"
rm -rf "$OUT"; mkdir -p "$OUT/lib"
cp "$PUB/UartHub.exe" "$OUT/UartHub.exe"
cp "$BIN_CORE/UartHub.Core.dll" "$BIN_TESTS/UartHub.Tests.dll" "$OUT/lib/"
echo "publish files: $(ls "$PUB" | tr '\n' ' ')"

# 업데이트 시험용 두 번째 빌드(계획 T2 install-update: "버전 문자열만 다른 두 번째 빌드"): 같은 소스, BuildInfo.Version 만 VER+1.
# out/<ver>/vB/UartHub.exe. 시험 DLL(lib)은 첫 빌드 것만 쓴다(vB 코어는 sim_dll 로 같은 Tests.dll 을 로드 — Core 어셈블리 이름·버전 0.0.0.0 동일).
VER_B=$((10#$VER + 1))
printf 'namespace UartHub { public static class BuildInfo { public const string Version = "%s"; } }\n' "$VER_B" > "$GEN/BuildInfo.g.cs"
"$DOTNET" build app/UartHub.csproj -c Release -r win-x64 -p:Version=0.0.0 -nologo -v:m
"$DOTNET" publish app/UartHub.csproj -c Release -r win-x64 --no-build -nologo -v:m
mkdir -p "$OUT/vB"
cp "$PUB/UartHub.exe" "$OUT/vB/UartHub.exe"
printf 'namespace UartHub { public static class BuildInfo { public const string Version = "%s"; } }\n' "$VER" > "$GEN/BuildInfo.g.cs"

# 오래된 out/ 정리(고정된 배포 후보는 남김)
ls -1 "$HUB_DIR/out" | grep -E '^[0-9]{17}$' | sort -r | tail -n +$((KEEP+1)) | while read -r o; do pinned "$o" || rm -rf "$HUB_DIR/out/$o"; done

# Windows 로컬 사본
resolve_dev
stage_copy "$DEV/$VER"
ls -1 "$DEV" | grep -E '^[0-9]{17}$' | sort -r | tail -n +$((KEEP+1)) | while read -r o; do pinned "$o" || rm -rf "$DEV/$o" 2>/dev/null || true; done

SHA="$(sha "$OUT/UartHub.exe")"
echo "VERSION=$VER"
echo "EXE_WIN=${UART_HUB_DEV_WIN}\\${VER}\\UartHub.exe"
echo "EXE_SHA256=$SHA"
