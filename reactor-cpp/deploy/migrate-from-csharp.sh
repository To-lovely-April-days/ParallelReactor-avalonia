#!/usr/bin/env bash
# 把旧 C# 程序的设置、接口配置、数据**复制**到新系统的位置。旧文件一个都不删、不改（回退时旧程序照常能用）。
# 可以重复运行：已经存在的新文件不覆盖（数据目录只补新系统还没有的文件）。
#
# 用法：sudo ./migrate-from-csharp.sh [--user teamhd] [--overwrite] [--no-data]
#   --overwrite  settings.json / api.json 已存在时也用旧的覆盖（覆盖前备份成 .bak-时间）
#   --no-data    只迁设置，不复制数据目录
#
#   旧位置（C#）                                      新位置
#   ~/.config/ParallelReactor/settings.json      →   /etc/reactord/settings.json
#   ~/.config/ParallelReactor/api.json           →   /etc/reactord/api.json（新目录里还是默认文件时才换成旧的）
#   ~/ParallelReactor-data/                      →   /var/lib/reactord/（不含 estop*.latch：新系统没有急停锁存）
#
# 最好在旧程序**停掉之后**运行（switch-to-cpp.sh 就是这么调用的），这样复制到的是最终的数据。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONF_DIR="/etc/reactord"
DATA_DIR="/var/lib/reactord"
RUN_USER="teamhd"
OVERWRITE=0
COPY_DATA=1

die() { echo "错误：$*" >&2; exit 1; }
info() { echo "==> $*"; }
warn() { echo "注意：$*" >&2; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --user)
            [[ $# -ge 2 ]] || die "--user 后面要跟用户名"
            RUN_USER="$2"
            shift 2
            ;;
        --overwrite) OVERWRITE=1; shift ;;
        --no-data) COPY_DATA=0; shift ;;
        -h | --help) sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) die "不认识的参数 $1" ;;
    esac
done

[[ $EUID -eq 0 ]] || die "要用 sudo 运行"
getent passwd "$RUN_USER" >/dev/null || die "用户 $RUN_USER 不存在"
USER_HOME="$(getent passwd "$RUN_USER" | cut -d: -f6)"
RUN_GROUP="$(id -gn "$RUN_USER")"
OLD_CONF="$USER_HOME/.config/ParallelReactor"
OLD_DATA="$USER_HOME/ParallelReactor-data"

[[ -d "$CONF_DIR" && -d "$DATA_DIR" ]] || die "$CONF_DIR 或 $DATA_DIR 不存在：先运行 install.sh"

if pgrep -f "ParallelReactorProject/ParallelReactor" >/dev/null 2>&1; then
    warn "旧程序还在运行：现在复制到的是此刻的快照，之后旧程序再写的数据不会过来。正式切换请用 switch-to-cpp.sh"
fi
if systemctl is-active --quiet reactord.service; then
    warn "reactord 正在运行：settings.json 只在 reactord 停着时迁（它运行中会把内存里的设置写回去，覆盖迁过来的文件）"
fi

stamp="$(date +%Y%m%d-%H%M%S)"

copy_conf() {
    local name="$1" src="$OLD_CONF/$1" dst="$CONF_DIR/$1" replace=0
    if [[ ! -f "$src" ]]; then
        info "旧的 $src 不存在，跳过"
        return 0
    fi
    if [[ ! -e "$dst" ]]; then
        replace=1
    elif cmp -s "$src" "$dst"; then
        info "$dst 和旧文件一样，跳过"
        return 0
    elif [[ "$name" == "api.json" ]] && cmp -s "$dst" "$SCRIPT_DIR/config/api.json"; then
        replace=1   # 新目录里还是装的默认文件 → 换成旧的（PC 端已有的令牌、白名单继续有效）
    elif [[ $OVERWRITE -eq 1 ]]; then
        replace=1
    fi
    if [[ $replace -eq 0 ]]; then
        info "$dst 已存在且和旧的不同，保留不动（要用旧的覆盖：加 --overwrite）"
        return 0
    fi
    if [[ -e "$dst" ]]; then
        cp -p "$dst" "$dst.bak-$stamp"
        info "原来的 $dst 备份为 $dst.bak-$stamp"
    fi
    install -m 0640 -o "$RUN_USER" -g "$RUN_GROUP" "$src" "$dst"
    info "已复制 $src → $dst"
}

if systemctl is-active --quiet reactord.service; then
    warn "跳过 settings.json（reactord 在运行）"
else
    copy_conf settings.json
fi
copy_conf api.json
if [[ -f "$CONF_DIR/api.json" ]]; then
    if python3 -I -c 'import json,sys; c=json.load(open(sys.argv[1],encoding="utf-8-sig")); sys.exit(0 if c.get("enabled") else 1)' "$CONF_DIR/api.json" 2>/dev/null; then
        info "api.json：PC 接口已启用"
    else
        warn "api.json：enabled 不是 true（或文件带注释读不了）→ PC 端连不上，触屏不受影响。需要 PC 时按联调指南 §9 改"
    fi
fi

if [[ $COPY_DATA -eq 1 ]]; then
    if [[ ! -d "$OLD_DATA" ]]; then
        info "旧数据目录 $OLD_DATA 不存在，跳过"
    else
        need_kb="$(du -sk "$OLD_DATA" | cut -f1)"
        free_kb="$(df -Pk "$DATA_DIR" | awk 'NR==2 {print $4}')"
        info "旧数据 $((need_kb / 1024)) MB，$DATA_DIR 所在分区剩 $((free_kb / 1024)) MB"
        if ((need_kb + 512 * 1024 > free_kb)); then
            die "空间不够（要留 512 MB 余量）。可以先 --no-data，旧数据留在原处用 U 盘另存"
        fi
        info "复制 $OLD_DATA/ → $DATA_DIR/（已存在的文件不覆盖）"
        if command -v rsync >/dev/null 2>&1; then
            rsync -a --ignore-existing --exclude 'estop.latch' --exclude 'estop-sim.latch' "$OLD_DATA/" "$DATA_DIR/"
        else
            cp -a -n "$OLD_DATA/." "$DATA_DIR/"
            # cp 没有排除：只删刚复制过来的锁存文件（新系统从来不写它）
            rm -f "$DATA_DIR/estop.latch" "$DATA_DIR/estop-sim.latch"
        fi
        chown -R "$RUN_USER:$RUN_GROUP" "$DATA_DIR"
        info "数据复制完成。旧目录 $OLD_DATA 原样保留"
    fi
fi

info "迁移完成。旧文件都没有动：$OLD_CONF、$OLD_DATA"
