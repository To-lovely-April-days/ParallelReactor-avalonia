#!/usr/bin/env bash
# 生成一枚 PC 端用的接口令牌：明文只显示这一次（抄到 PC 端），api.json 里只存它的 SHA-256。
#
# 用法：
#   ./gen-token.sh NAME [ROLE]                       # 只打印，不改文件
#   sudo ./gen-token.sh NAME [ROLE] --apply [FILE]   # 同时把 {name, sha256, role} 加进 api.json（默认 /etc/reactord/api.json，先备份）
# ROLE：viewer（只看）/ operator（可操作，默认）/ admin。
# 改完 api.json 要重启 reactord 才生效 —— 只能在没有实验运行时重启（重启 = 停全部加热、关阀、停搅拌）。
#
# 不要把明文令牌写进任何文件、聊天记录或缺陷报告。丢了就重新生成一枚、删掉旧条目。
set -euo pipefail

usage() {
    sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'
    exit 2
}

[[ $# -ge 1 ]] || usage
name="$1"
shift
role="operator"
if [[ $# -ge 1 && "$1" != --* ]]; then
    role="$1"
    shift
fi
apply=0
file="/etc/reactord/api.json"
if [[ $# -ge 1 ]]; then
    [[ "$1" == "--apply" ]] || usage
    apply=1
    shift
    if [[ $# -ge 1 ]]; then
        file="$1"
        shift
    fi
fi
[[ $# -eq 0 ]] || usage

case "$role" in
    viewer | operator | admin) ;;
    *)
        echo "ROLE 只能是 viewer / operator / admin" >&2
        exit 2
        ;;
esac
if [[ ! "$name" =~ ^[A-Za-z0-9._-]{1,32}$ ]]; then
    echo "NAME 只用字母、数字、点、横线、下划线，最长 32（例：PC-1）" >&2
    exit 2
fi

# 32 字节随机数 → 64 位十六进制
token="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
[[ ${#token} -eq 64 ]] || { echo "生成随机数失败" >&2; exit 1; }
hash="$(printf '%s' "$token" | sha256sum | cut -d' ' -f1)"

entry="{ \"name\": \"${name}\", \"sha256\": \"${hash}\", \"role\": \"${role}\" }"

echo "================ 令牌（只显示这一次，抄到 PC 端「连接设置」里）================"
echo "$token"
echo "==============================================================================="
echo
echo "api.json 的 tokens 数组里加这一项："
echo "  ${entry}"

if [[ $apply -eq 1 ]]; then
    if [[ $EUID -ne 0 ]]; then
        echo "--apply 要用 sudo 运行" >&2
        exit 1
    fi
    [[ -f "$file" ]] || { echo "找不到 $file" >&2; exit 1; }
    backup="${file}.bak-$(date +%Y%m%d-%H%M%S)"
    cp -p -- "$file" "$backup"
    # api.json 可能带注释 / 尾逗号（reactord 和 C# 都允许）；Python 读不了就不改，让人手工加
    if ! python3 -I - "$file" "$name" "$hash" "$role" <<'PY'
import json, os, sys
path, name, digest, role = sys.argv[1:5]
with open(path, encoding="utf-8-sig") as f:
    cfg = json.load(f)
toks = cfg.get("tokens") or []
toks = [t for t in toks if (t.get("name") or "") != name]
toks.append({"name": name, "sha256": digest, "role": role})
cfg["tokens"] = toks
tmp = path + ".tmp"
with open(tmp, "w", encoding="utf-8") as f:
    json.dump(cfg, f, ensure_ascii=False, indent=2)
    f.write("\n")
os.replace(tmp, path)
PY
    then
        echo "自动修改失败（文件可能带注释）：请手工把上面那一项加进 $file 的 tokens 数组" >&2
        exit 1
    fi
    # 保持原来的属主 / 权限
    chown --reference="$backup" -- "$file"
    chmod --reference="$backup" -- "$file"
    echo
    echo "已写入 $file（同名条目已替换；备份 $backup）。"
    echo "重启 reactord 后生效 —— 先确认没有实验在跑：sudo systemctl restart reactord"
fi
