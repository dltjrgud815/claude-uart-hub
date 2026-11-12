# skills_wsl.sh — UART 허브 설치기(SkillsDeployer)가 WSL 배포판마다
#   wsl.exe -d <배포판> -e sh -c '<이 파일 내용>' uart_hub <op> [인자]
# 로 돌린다(계획 §2.3 g, T4). 그 배포판의 기본 사용자로 돌므로 소유자·권한이 맞다. POSIX sh(dash) — bash 전용 문법 없음.
#   probe        읽기만: home= · claude=0|1 · dir=0|1 · marker=<.uart_hub 한 줄>(있을 때만)
#   write        표준 입력의 "<이름> <모드> <base64>" 세 줄(SKILL.md · uart · .uart_hub)을 임시 파일로 풀고 chmod 한 뒤 이름 바꾸기
#                (.uart_hub 먼저). ~/.claude 가 없으면(3) · skills/uart 가 .uart_hub 없이 이미 있으면(4, 사용자가 만든 것) 아무것도 쓰지 않는다.
#                X5: 함께 오는 "connect-uart/SKILL.md"·"connect-uart/.uart_hub"·"disconnect-uart/…" 줄은 ~/.claude/skills/<그 이름>/ 에 같은 방식으로
#                넣는다(그 폴더가 .uart_hub 없이 이미 있으면 그 폴더만 건너뜀). 결과 줄 extras=<이름>:installed|updated|skipped|failed,…
#   remove <id>  .uart_hub 의 install_id 가 <id> 일 때만 세 파일을 지우고, 폴더가 비었으면 폴더도 지운다. 같은 표시의 connect-uart·disconnect-uart 도
#                지운다(결과 줄 extras_removed=…).
# 결과는 ok=… 또는 err=… 한 줄(+ write 는 path=, 있으면 extras=).
set -u
d="$HOME/.claude/skills/uart"
m="$d/.uart_hub"
x="$HOME/.claude/skills"
extras='connect-uart disconnect-uart'
made=''
cleanup() {
  rm -f "$d"/.uart_hub_new_*
  for e in $extras; do
    rm -f "$x/$e"/.uart_hub_new_* 2>/dev/null
    case " $made " in *" $e "*) rmdir "$x/$e" 2>/dev/null ;; esac
  done
}
case "${1:-}" in
probe)
  printf 'home=%s\n' "$HOME"
  if [ -d "$HOME/.claude" ]; then echo claude=1; else echo claude=0; fi
  if [ -e "$d" ]; then echo dir=1; else echo dir=0; fi
  if [ -f "$m" ]; then printf 'marker=%s\n' "$(tr -d '\r\n' < "$m")"; fi
  ;;
write)
  [ -d "$HOME/.claude" ] || { echo err=no_claude; exit 3; }
  if [ -e "$d" ] && [ ! -f "$m" ]; then echo err=foreign; exit 4; fi
  had=0
  [ -f "$m" ] && had=1
  mkdir -p "$d" || { echo err=mkdir; exit 5; }
  skipx=''
  for e in $extras; do
    if [ -e "$x/$e" ] && [ ! -f "$x/$e/.uart_hub" ]; then skipx="$skipx $e"; fi    # 사용자가 만든 폴더 — 건드리지 않음
  done
  n=0
  while read -r name mode b64; do
    case "$name" in
      SKILL.md|uart|.uart_hub) dst="$d/.uart_hub_new_$name"; n=$((n + 1)) ;;
      connect-uart/SKILL.md|connect-uart/.uart_hub|disconnect-uart/SKILL.md|disconnect-uart/.uart_hub)
        e=${name%%/*}; f=${name#*/}
        case " $skipx " in *" $e "*) continue ;; esac
        if [ ! -d "$x/$e" ]; then mkdir -p "$x/$e" || { echo err=mkdir; cleanup; exit 5; }; made="$made $e"; fi
        dst="$x/$e/.uart_hub_new_$f" ;;
      *) echo "err=name $name"; cleanup; exit 6 ;;
    esac
    case "$mode" in 644|755) ;; *) echo "err=mode $mode"; cleanup; exit 6 ;; esac
    printf '%s' "$b64" | base64 -d > "$dst" 2>/dev/null || { echo err=base64; cleanup; exit 7; }
    chmod "$mode" "$dst" || { echo err=chmod; cleanup; exit 7; }
  done
  [ "$n" -eq 3 ] || { echo "err=count $n"; cleanup; exit 6; }
  for f in .uart_hub SKILL.md uart; do
    mv -f "$d/.uart_hub_new_$f" "$d/$f" || { echo "err=mv $f"; exit 8; }
  done
  ex=''
  for e in $extras; do
    case " $skipx " in *" $e "*) ex="$ex,$e:skipped"; continue ;; esac
    if [ ! -f "$x/$e/.uart_hub_new_SKILL.md" ] || [ ! -f "$x/$e/.uart_hub_new_.uart_hub" ]; then
      rm -f "$x/$e"/.uart_hub_new_* 2>/dev/null
      case " $made " in *" $e "*) rmdir "$x/$e" 2>/dev/null ;; esac
      continue
    fi
    hx=0
    [ -f "$x/$e/.uart_hub" ] && hx=1
    if mv -f "$x/$e/.uart_hub_new_.uart_hub" "$x/$e/.uart_hub" && mv -f "$x/$e/.uart_hub_new_SKILL.md" "$x/$e/SKILL.md"; then
      if [ "$hx" = 1 ]; then ex="$ex,$e:updated"; else ex="$ex,$e:installed"; fi
    else ex="$ex,$e:failed"; fi
  done
  if [ "$had" = 1 ]; then echo ok=updated; else echo ok=installed; fi
  printf 'path=%s\n' "$d"
  if [ -n "$ex" ]; then printf 'extras=%s\n' "${ex#,}"; fi
  ;;
remove)
  id=${2:-}
  [ -n "$id" ] || { echo err=no_id; exit 2; }
  ex=''
  rmx() {   # 같은 install_id 표시의 connect-uart·disconnect-uart
    for e in $extras; do
      mx="$x/$e/.uart_hub"
      [ -f "$mx" ] || continue
      case "$(tr -d '\r\n' < "$mx")" in *"\"install_id\":\"$id\""*) ;; *) continue ;; esac
      rm -f "$x/$e/SKILL.md" "$mx" "$x/$e"/.uart_hub_new_*
      rmdir "$x/$e" 2>/dev/null
      ex="$ex,$e"
    done
    if [ -n "$ex" ]; then printf 'extras_removed=%s\n' "${ex#,}"; fi
    return 0
  }
  [ -f "$m" ] || { echo ok=absent; rmx; exit 0; }
  case "$(tr -d '\r\n' < "$m")" in *"\"install_id\":\"$id\""*) ;; *) echo err=other_install; exit 9 ;; esac
  rm -f "$d/SKILL.md" "$d/uart" "$m" "$d"/.uart_hub_new_*
  if rmdir "$d" 2>/dev/null; then echo ok=removed; else echo ok=removed_files; fi
  rmx
  ;;
*)
  echo "err=op ${1:-}"
  exit 2
  ;;
esac
