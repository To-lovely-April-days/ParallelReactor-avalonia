#!/usr/bin/env bash
# 从旧 C# 程序切换到新 C++ 程序（reactord + reactord-hmi）。
#
#   1. 确认旧程序**没有实验在跑**：通过旧程序的接口读 /api/v1/snapshot（8 路状态、程序、气路、搅拌、压力）。
#      读不到（接口没开 / 没令牌）就拒绝，除非你在屏幕上亲眼确认过并加 --force。
#   2. 停止并禁用旧服务 reactor-hmi.service（旧程序退出时会停加热、关阀、停搅拌）。
#   3. 迁移旧设置 / 数据（migrate-from-csharp.sh，只复制不删除）。
#   4. 启用并启动 reactord、reactord-hmi。
#
# 用法：sudo ./switch-to-cpp.sh [--token T] [--force] [--yes] [--no-migrate] [--user teamhd]
#   --token T     旧程序接口的令牌（viewer 就够）；也可以 sudo REACTOR_TOKEN=... ./switch-to-cpp.sh（推荐，不进 ps）
#   --force       旧程序接口读不到时，按「我已在屏幕上确认 8 路都停了、没有程序、搅拌停、已放空」继续。
#                 接口**读到了有实验在跑**时 --force 也不放行。
#   --yes         不再问「输入 YES」（只在你已确认后的脚本化场景用）
#   --no-migrate  不迁移旧设置 / 数据
#
# 先运行过 install.sh。可以重复运行：已经切换过就只确认状态。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUN_USER="teamhd"
TOKEN="${REACTOR_TOKEN:-}"
FORCE=0
ASSUME_YES=0
MIGRATE=1
OLD_UNIT="reactor-hmi.service"

die() { echo "错误：$*" >&2; exit 1; }
info() { echo "==> $*"; }
warn() { echo "注意：$*" >&2; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --token)
            [[ $# -ge 2 ]] || die "--token 后面要跟令牌"
            TOKEN="$2"
            shift 2
            ;;
        --force) FORCE=1; shift ;;
        --yes) ASSUME_YES=1; shift ;;
        --no-migrate) MIGRATE=0; shift ;;
        --user)
            [[ $# -ge 2 ]] || die "--user 后面要跟用户名"
            RUN_USER="$2"
            shift 2
            ;;
        -h | --help) sed -n '2,19p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) die "不认识的参数 $1" ;;
    esac
done

[[ $EUID -eq 0 ]] || die "要用 sudo 运行"
[[ -x /opt/reactor/bin/reactord ]] || die "/opt/reactor/bin/reactord 不存在：先运行 install.sh"
[[ -f /etc/systemd/system/reactord.service && -f /etc/systemd/system/reactord-hmi.service ]] ||
    die "服务文件没装：先运行 install.sh"
[[ -f /etc/reactord/reactord.env ]] || die "/etc/reactord/reactord.env 不存在：先运行 install.sh"
getent passwd "$RUN_USER" >/dev/null || die "用户 $RUN_USER 不存在"
USER_HOME="$(getent passwd "$RUN_USER" | cut -d: -f6)"
BUSY_PY="$SCRIPT_DIR/lib/reactor_busy.py"
[[ -f "$BUSY_PY" ]] || die "缺 $BUSY_PY"

old_proc_running() { pgrep -f "ParallelReactorProject/ParallelReactor" >/dev/null 2>&1 || pgrep -x ParallelReactor >/dev/null 2>&1; }

# ---------- 已经切换过？----------
if systemctl is-active --quiet reactord.service; then
    info "reactord 已在运行，看起来已经切换过了"
    if systemctl is-enabled --quiet "$OLD_UNIT" 2>/dev/null; then
        warn "旧服务 $OLD_UNIT 仍是开机自启：sudo systemctl disable $OLD_UNIT"
    fi
    if ! systemctl is-active --quiet reactord-hmi.service; then
        info "启动触屏 reactord-hmi"
        systemctl enable --now reactord-hmi.service
    fi
    systemctl --no-pager --lines=0 status reactord.service reactord-hmi.service || true
    exit 0
fi

# ---------- 1. 旧程序有没有实验在跑 ----------
old_active=0
if systemctl is-active --quiet "$OLD_UNIT" || old_proc_running; then old_active=1; fi

if [[ $old_active -eq 1 ]]; then
    info "旧程序在运行，检查有没有实验在跑"
    OLD_API="$USER_HOME/.config/ParallelReactor/api.json"
    api_url=""
    if [[ -f "$OLD_API" ]]; then
        # 读 enabled / bind / httpPort（旧文件可能带注释：读不了就当没开）
        api_url="$(python3 -I - "$OLD_API" <<'PY' 2>/dev/null || true
import json, re, sys
t = open(sys.argv[1], encoding="utf-8-sig").read()
t = re.sub(r"(?m)^\s*//.*$", "", t)
t = re.sub(r",(\s*[}\]])", r"\1", t)
c = {k.lower(): v for k, v in json.loads(t).items()}
if c.get("enabled"):
    bind = c.get("bind") or "0.0.0.0"
    host = "127.0.0.1" if bind in ("0.0.0.0", "::", "*", "+", "localhost", "") else bind
    print("http://%s:%d" % (host, int(c.get("httpport") or 8080)))
PY
)"
    fi
    verdict=3
    if [[ -n "$api_url" ]]; then
        info "旧程序接口 $api_url"
        set +e
        REACTOR_TOKEN="$TOKEN" python3 -I "$BUSY_PY" --url "$api_url"
        verdict=$?
        set -e
    else
        echo "旧程序的接口没有启用（$OLD_API 里 enabled 不是 true），读不到状态。"
    fi
    case "$verdict" in
        0) info "旧程序空闲" ;;
        1) die "旧程序有实验 / 输出在进行。等实验结束（或在旧界面上按流程停止并放空）后再切换。--force 也不放行。" ;;
        *)
            if [[ $FORCE -eq 1 ]]; then
                warn "无法通过接口确认，按 --force 继续：你已在屏幕上确认 8 路都停了、没有程序、搅拌停、各釜已放空"
            else
                die "无法确认旧程序是否空闲。请到触屏上确认 8 路都没在加热、没有程序、搅拌停、各釜已放空，然后加 --force 重新运行（或给 --token 让脚本自己查）"
            fi
            ;;
    esac
else
    info "旧程序没有在运行"
fi

# ---------- 2. 确认 ----------
cat <<EOF

将要执行：
  - 停止并禁用旧服务 $OLD_UNIT（旧程序退出：停加热、关阀、停搅拌）
  - $( [[ $MIGRATE -eq 1 ]] && echo "复制旧设置 / 数据到 /etc/reactord、/var/lib/reactord（旧文件不动）" || echo "不迁移旧设置 / 数据" )
  - 启用并启动 reactord.service、reactord-hmi.service
  - reactord 启动时会给 8 路温控写「At=4（停输出）」，并读回阀门状态
回退：sudo /opt/reactor/deploy/rollback.sh
EOF
if [[ $ASSUME_YES -ne 1 ]]; then
    read -r -p "确认切换请输入 YES：" ans
    [[ "$ans" == "YES" ]] || die "已取消，什么都没改"
fi

# ---------- 3. 停旧程序 ----------
if systemctl cat "$OLD_UNIT" >/dev/null 2>&1; then
    info "停止并禁用 $OLD_UNIT"
    systemctl stop "$OLD_UNIT" || true
    systemctl disable "$OLD_UNIT" || true
fi
for _ in 1 2 3 4 5 6 7 8 9 10; do
    old_proc_running || break
    sleep 1
done
if old_proc_running; then
    die "旧程序还在运行（可能是在终端里手工 ./run.sh 起的）：请在旧界面上退出它，再重新运行本脚本。新服务没有启动"
fi

# 串口别被别的程序占着（rtu-probe、手工起的 reactord …）
if command -v fuser >/dev/null 2>&1; then
    for p in /dev/ttyS4 /dev/ttyS5 /dev/ttyS6 /dev/ttyS8; do
        if [[ -e "$p" ]] && fuser "$p" >/dev/null 2>&1; then
            die "$p 正被其他进程占用：$(fuser "$p" 2>&1)。关掉它再运行"
        fi
    done
fi

# ---------- 4. 迁移 ----------
if [[ $MIGRATE -eq 1 ]]; then
    "$SCRIPT_DIR/migrate-from-csharp.sh" --user "$RUN_USER"
fi

# ---------- 5. 启动新程序 ----------
info "启用并启动 reactord（Type=notify：等它报 READY）"
systemctl enable reactord.service reactord-hmi.service
if ! systemctl start reactord.service; then
    journalctl -u reactord.service -n 40 --no-pager || true
    die "reactord 没起来（日志见上）。回退：sudo $SCRIPT_DIR/rollback.sh"
fi
info "启动触屏 reactord-hmi"
systemctl start reactord-hmi.service || warn "触屏没起来：journalctl -u reactord-hmi -n 40（控制不受影响）"

sleep 3
echo
systemctl --no-pager --lines=5 status reactord.service reactord-hmi.service || true
echo
info "通过本机入口读一次状态"
runuser -u "$RUN_USER" -- python3 -I "$BUSY_PY" --unix /run/reactord/hmi.sock || true
echo
info "切换完成。按《板子联调指南》继续检查。回退：sudo $SCRIPT_DIR/rollback.sh"
