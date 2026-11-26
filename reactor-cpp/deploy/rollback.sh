#!/usr/bin/env bash
# 回退到旧 C# 程序：停止并禁用 reactord-hmi、reactord，重新启用并启动旧服务 reactor-hmi.service。
# 新程序的文件（/opt/reactor、/etc/reactord、/var/lib/reactord）都留着，随时可以再 switch-to-cpp.sh。
#
# 用法：sudo ./rollback.sh [--force] [--yes] [--user teamhd]
#   --force  reactord 的状态读不到时也继续（你已在屏幕上确认没有实验在跑）。读到有实验在跑时不放行。
#   --yes    不再问「输入 YES」
#
# 停 reactord = 停全部加热、关阀、停搅拌。实验进行中不要回退。
# 注意：新程序运行期间产生的数据在 /var/lib/reactord，改过的设置在 /etc/reactord/settings.json，旧程序看不到这些。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUN_USER="teamhd"
FORCE=0
ASSUME_YES=0
OLD_UNIT="reactor-hmi.service"

die() { echo "错误：$*" >&2; exit 1; }
info() { echo "==> $*"; }
warn() { echo "注意：$*" >&2; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --force) FORCE=1; shift ;;
        --yes) ASSUME_YES=1; shift ;;
        --user)
            [[ $# -ge 2 ]] || die "--user 后面要跟用户名"
            RUN_USER="$2"
            shift 2
            ;;
        -h | --help) sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) die "不认识的参数 $1" ;;
    esac
done

[[ $EUID -eq 0 ]] || die "要用 sudo 运行"
systemctl cat "$OLD_UNIT" >/dev/null 2>&1 || die "旧服务 $OLD_UNIT 不存在，没法回退（旧程序目录 /home/$RUN_USER/ParallelReactorProject 还在吗？）"
BUSY_PY="$SCRIPT_DIR/lib/reactor_busy.py"

# ---------- 1. 新程序有没有实验在跑 ----------
if systemctl is-active --quiet reactord.service; then
    info "检查 reactord 有没有实验在跑"
    verdict=3
    if [[ -f "$BUSY_PY" ]]; then
        set +e
        # 本机入口只认 $RUN_USER 的 uid，所以以它的身份去问
        runuser -u "$RUN_USER" -- python3 -I "$BUSY_PY" --unix /run/reactord/hmi.sock
        verdict=$?
        set -e
    fi
    case "$verdict" in
        0) info "reactord 空闲" ;;
        1) die "reactord 有实验 / 输出在进行。等实验结束再回退。--force 也不放行。" ;;
        *)
            [[ $FORCE -eq 1 ]] || die "读不到 reactord 的状态。到触屏上确认没有实验在跑后加 --force 重新运行"
            warn "按 --force 继续"
            ;;
    esac
fi

cat <<EOF

将要执行：
  - 停止并禁用 reactord-hmi.service、reactord.service（reactord 退出：停加热、关阀、停搅拌）
  - 启用并启动旧服务 $OLD_UNIT
EOF
if [[ $ASSUME_YES -ne 1 ]]; then
    read -r -p "确认回退请输入 YES：" ans
    [[ "$ans" == "YES" ]] || die "已取消，什么都没改"
fi

# ---------- 2. 停新程序 ----------
for u in reactord-hmi.service reactord.service; do
    if systemctl cat "$u" >/dev/null 2>&1; then
        info "停止并禁用 $u"
        systemctl stop "$u" || true
        systemctl disable "$u" || true
    fi
done
if pgrep -a -x reactord 2>/dev/null | grep -v -- '--sim' | grep -q .; then
    die "还有真机 reactord 进程在运行（手工起的？）：先关掉它，再运行本脚本。旧服务没有启动"
fi

# ---------- 3. 起旧程序 ----------
info "启用并启动 $OLD_UNIT"
systemctl enable "$OLD_UNIT"
systemctl start "$OLD_UNIT"
sleep 3
systemctl --no-pager --lines=10 status "$OLD_UNIT" || true
echo
info "已回退到旧程序。新程序的文件都还在；再切换：sudo /opt/reactor/deploy/switch-to-cpp.sh"
