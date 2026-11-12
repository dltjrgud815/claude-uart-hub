#!/usr/bin/env bash
# src/host/skills_wsl.sh(설치기가 wsl.exe -d <배포판> -e sh -c 로 돌리는 스크립트)를 이 배포판의 dash 로 직접 시험한다.
#   사용: tests/check_skills_wsl.sh            HOME 을 임시 폴더로 바꿔서 돌린다 — 실제 ~/.claude 는 읽지도 쓰지도 않는다.
#   확인: probe(.claude 없음·있음·표시 있음) · write(넣기 755/644·LF 그대로·갱신·표시 없는 기존 폴더 거부·.claude 없음 거부·잘못된 이름 거부)
#         · remove(다른 install_id 거부·같으면 지움·사용자 파일이 있으면 폴더는 남김·표시 없음 → absent)
#         · X5 extras(connect-uart·disconnect-uart: 넣기·갱신·표시 없는 사용자 폴더는 그 폴더만 건너뜀·remove 가 같은 표시만 지움).
# 종료코드: 모두 PASS 면 0.
set -uo pipefail
HUB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
S="$HUB_DIR/src/host/skills_wsl.sh"
SH="$(command -v dash || command -v sh)"
T="$(mktemp -d)"
trap 'rm -rf "$T"' EXIT
fails=0 n=0
ok() { n=$((n + 1)); echo "[PASS] $*"; }
ng() { n=$((n + 1)); fails=$((fails + 1)); echo "[FAIL] $*"; }
run() { local h=$1; shift; HOME="$h" "$SH" -c "$(cat "$S")" uart_hub "$@"; }
b64() { base64 -w0; }
payload() {   # $1=install_id $2=version
  printf 'SKILL.md 644 %s\n' "$(printf -- '---\nname: uart\n---\n본문 "q" \\ $X\n' | b64)"
  printf 'uart 755 %s\n' "$(printf '#!/usr/bin/env bash\nUART_HUB_EXE_DEFAULT='"'"'C:\\x\\UartHub.exe'"'"'\necho hi\n' | b64)"
  printf '.uart_hub 644 %s\n' "$(printf '{"install_id":"%s","version":"%s","exe":"C:\\\\x","env":"wsl"}\n' "$1" "$2" | b64)"
}
echo "# sh=$SH script=$S"

H1="$T/h1"; mkdir -p "$H1"
o="$(run "$H1" probe)"
[[ "$o" == *"home=$H1"* && "$o" == *claude=0* && "$o" == *dir=0* && "$o" != *marker=* ]] && ok "probe: .claude 없음" || ng "probe .claude 없음: $o"
o="$(payload aaaa 1 | run "$H1" write)"; rc=$?
[[ $rc -eq 3 && "$o" == err=no_claude && ! -e "$H1/.claude" ]] && ok "write: .claude 없음 → 3, 아무것도 안 만듦" || ng "write no_claude rc=$rc $o"

H2="$T/h2"; mkdir -p "$H2/.claude"
o="$(payload aaaa 1 | run "$H2" write)"; rc=$?
d="$H2/.claude/skills/uart"
[[ $rc -eq 0 && "$o" == *ok=installed* && "$o" == *"path=$d"* ]] && ok "write: 넣기 ok=installed" || ng "write install rc=$rc $o"
[[ "$(stat -c %a "$d/uart")" == 755 && "$(stat -c %a "$d/SKILL.md")" == 644 && "$(stat -c %a "$d/.uart_hub")" == 644 ]] && ok "write: uart 755 · SKILL.md·.uart_hub 644" || ng "write 모드 $(stat -c '%n %a' "$d"/* "$d/.uart_hub")"
[[ "$(head -n1 "$d/uart")" == '#!/usr/bin/env bash' && $(tr -cd '\r' < "$d/uart" | wc -c) -eq 0 ]] && grep -qx "UART_HUB_EXE_DEFAULT='C:\\\\x\\\\UartHub.exe'" "$d/uart" && ok "write: 내용 그대로(shebang·CR 0·exe 줄)" || ng "write 내용 $(cat -A "$d/uart" | head -3)"
[[ -z "$(ls -A "$d" | grep uart_hub_new_)" ]] && ok "write: 임시 파일 남지 않음" || ng "임시 파일 $(ls -A "$d")"
o="$(run "$H2" probe)"
[[ "$o" == *claude=1* && "$o" == *dir=1* && "$o" == *'marker={"install_id":"aaaa","version":"1"'* ]] && ok "probe: 표시 읽음" || ng "probe marker: $o"
o="$(payload aaaa 2 | run "$H2" write)"; rc=$?
[[ $rc -eq 0 && "$o" == *ok=updated* ]] && grep -q '"version":"2"' "$d/.uart_hub" && ok "write: 갱신 ok=updated, 표시 버전 2" || ng "write update rc=$rc $o"
o="$(printf 'evil 644 eA==\n' | run "$H2" write)"; rc=$?
[[ $rc -eq 6 && "$o" == "err=name evil" && ! -e "$d/evil" && -z "$(ls -A "$d" | grep uart_hub_new_)" ]] && ok "write: 모르는 이름 거부(6), 임시 파일 정리" || ng "write name rc=$rc $o"
o="$(printf 'uart 755 %s\n' "$(echo x | b64)" | run "$H2" write)"; rc=$?
[[ $rc -eq 6 && "$o" == "err=count 1" ]] && grep -q 'echo hi' "$d/uart" && ok "write: 세 줄이 아니면 거부(6), 기존 파일 그대로" || ng "write count rc=$rc $o"

H3="$T/h3"; mkdir -p "$H3/.claude/skills/uart"; echo mine > "$H3/.claude/skills/uart/SKILL.md"
o="$(run "$H3" probe)"
[[ "$o" == *dir=1* && "$o" != *marker=* ]] && ok "probe: 표시 없는 기존 폴더" || ng "probe foreign: $o"
o="$(payload aaaa 1 | run "$H3" write)"; rc=$?
[[ $rc -eq 4 && "$o" == err=foreign && "$(cat "$H3/.claude/skills/uart/SKILL.md")" == mine && ! -e "$H3/.claude/skills/uart/uart" ]] && ok "write: 사용자가 만든 폴더 → 4, 그대로" || ng "write foreign rc=$rc $o"
o="$(run "$H3" remove aaaa)"; rc=$?
[[ $rc -eq 0 && "$o" == ok=absent && -f "$H3/.claude/skills/uart/SKILL.md" ]] && ok "remove: 표시 없음 → absent, 지우지 않음" || ng "remove absent rc=$rc $o"

o="$(run "$H2" remove bbbb)"; rc=$?
[[ $rc -eq 9 && "$o" == err=other_install && -f "$d/uart" ]] && ok "remove: 다른 install_id → 9, 그대로" || ng "remove other rc=$rc $o"
echo user > "$d/note.txt"
o="$(run "$H2" remove aaaa)"; rc=$?
[[ $rc -eq 0 && "$o" == ok=removed_files && ! -e "$d/uart" && ! -e "$d/SKILL.md" && ! -e "$d/.uart_hub" && -f "$d/note.txt" ]] && ok "remove: 같은 install_id → 세 파일 지움, 사용자 파일 있어 폴더는 남김" || ng "remove files rc=$rc $o"
rm -f "$d/note.txt"; rmdir "$d"; payload cccc 3 | run "$H2" write > /dev/null
o="$(run "$H2" remove cccc)"; rc=$?
[[ $rc -eq 0 && "$o" == ok=removed && ! -e "$d" && -d "$H2/.claude/skills" ]] && ok "remove: 빈 폴더까지 지움(.claude/skills 는 남김)" || ng "remove dir rc=$rc $o"
o="$(run "$H2" bogus)"; rc=$?
[[ $rc -eq 2 && "$o" == "err=op bogus" ]] && ok "모르는 op → 2" || ng "op rc=$rc $o"

# ---- X5 extras(connect-uart·disconnect-uart)
xpayload() {   # $1=install_id $2=version
  payload "$1" "$2"
  for e in connect-uart disconnect-uart; do
    printf '%s/SKILL.md 644 %s\n' "$e" "$(printf -- '---\nname: %s\ndisable-model-invocation: true\n---\n본문\n' "$e" | b64)"
    printf '%s/.uart_hub 644 %s\n' "$e" "$(printf '{"install_id":"%s","version":"%s","exe":"C:\\\\x","env":"wsl"}\n' "$1" "$2" | b64)"
  done
}
H4="$T/h4"; mkdir -p "$H4/.claude"
o="$(xpayload xxxx 1 | run "$H4" write)"; rc=$?
k="$H4/.claude/skills"
[[ $rc -eq 0 && "$o" == *ok=installed* && "$o" == *"extras=connect-uart:installed,disconnect-uart:installed"* ]] && ok "write extras: 넣음(extras= 줄)" || ng "write extras rc=$rc $o"
[[ -f "$k/connect-uart/SKILL.md" && -f "$k/connect-uart/.uart_hub" && -f "$k/disconnect-uart/SKILL.md" && "$(stat -c %a "$k/connect-uart/SKILL.md")" == 644 ]] \
  && grep -q 'disable-model-invocation: true' "$k/connect-uart/SKILL.md" && [[ -z "$(ls -A "$k/connect-uart" | grep uart_hub_new_)" ]] && ok "write extras: 두 폴더에 SKILL.md·.uart_hub(644), 임시 파일 없음" || ng "extras 파일 $(ls -la "$k"/*-uart 2>&1 | tr '\n' ' ')"
o="$(xpayload xxxx 2 | run "$H4" write)"; rc=$?
[[ $rc -eq 0 && "$o" == *"extras=connect-uart:updated,disconnect-uart:updated"* ]] && grep -q '"version":"2"' "$k/disconnect-uart/.uart_hub" && ok "write extras: 갱신 updated, 표시 버전 2" || ng "extras update rc=$rc $o"
H5="$T/h5"; mkdir -p "$H5/.claude/skills/connect-uart"; echo mine > "$H5/.claude/skills/connect-uart/SKILL.md"
o="$(xpayload yyyy 1 | run "$H5" write)"; rc=$?
[[ $rc -eq 0 && "$o" == *ok=installed* && "$o" == *"connect-uart:skipped"* && "$o" == *"disconnect-uart:installed"* && "$(cat "$H5/.claude/skills/connect-uart/SKILL.md")" == mine && ! -e "$H5/.claude/skills/connect-uart/.uart_hub" ]] \
  && ok "write extras: 표시 없는 사용자 connect-uart 폴더는 건너뜀(내용 그대로), 나머지는 넣음" || ng "extras foreign rc=$rc $o"
o="$(run "$H4" remove zzzz)"; rc=$?
[[ $rc -eq 9 && "$o" == err=other_install && -f "$k/connect-uart/SKILL.md" ]] && ok "remove extras: 다른 install_id → 9, extras 그대로" || ng "remove extras other rc=$rc $o"
o="$(run "$H4" remove xxxx)"; rc=$?
[[ $rc -eq 0 && "$o" == *ok=removed* && "$o" == *"extras_removed=connect-uart,disconnect-uart"* && ! -e "$k/connect-uart" && ! -e "$k/disconnect-uart" && ! -e "$k/uart" ]] \
  && ok "remove extras: 같은 install_id → uart·connect-uart·disconnect-uart 폴더까지 지움" || ng "remove extras rc=$rc $o"
o="$(run "$H5" remove yyyy)"; rc=$?
[[ $rc -eq 0 && "$o" == *"extras_removed=disconnect-uart"* && "$(cat "$H5/.claude/skills/connect-uart/SKILL.md")" == mine ]] && ok "remove extras: 사용자 connect-uart 폴더는 남김" || ng "remove extras foreign rc=$rc $o"
o="$(printf 'connect-uart/evil 644 eA==\n' | run "$H4" write)"; rc=$?
[[ $rc -eq 6 && "$o" == "err=name connect-uart/evil" && ! -e "$k/connect-uart" ]] && ok "write extras: 모르는 이름 거부(6), 폴더 만들지 않음" || ng "extras name rc=$rc $o"

echo "합계: $((n - fails))/$n PASS"
(( fails == 0 ))
