#!/usr/bin/env python3
"""查一台反应仪（旧 C# 程序或新 reactord，接口格式相同）现在有没有实验在跑。

用法：
  reactor_busy.py --url http://127.0.0.1:8080 [--token T]      # TCP 入口（旧 C# 程序 / reactord 的 PC 入口）
  reactor_busy.py --unix /run/reactord/hmi.sock                  # reactord 的本机入口（要以 teamhd 身份运行）
令牌也可以用环境变量 REACTOR_TOKEN 给（避免出现在 ps / shell 历史里）。

退出码：0 = 空闲（可以切换）；1 = 有东西在动或带压（不要切换）；3 = 连不上 / 读不懂（无法判断）。
只发 GET，不改任何状态。只用 Python 标准库（Ubuntu 20.04 自带 python3）。
"""
import argparse
import http.client
import json
import os
import socket
import sys

ACTIVE_STATES = {"heating", "pressing", "react", "wait", "stir", "cooling"}
PROGRAM_ACTIVE = {"preparing", "running", "waitingGas", "finishing"}
PRESSURE_WARN_PSI = 14.5  # 约 0.1 MPa（表压）：高于此值说明釜里还带压


class UnixHTTPConnection(http.client.HTTPConnection):
    def __init__(self, path, timeout=5):
        super().__init__("localhost", timeout=timeout)
        self._path = path

    def connect(self):
        s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        s.settimeout(self.timeout)
        s.connect(self._path)
        self.sock = s


def fetch(args, path):
    if args.unix:
        conn = UnixHTTPConnection(args.unix)
    else:
        u = args.url.split("://", 1)[-1].rstrip("/")
        host, _, port = u.partition(":")
        conn = http.client.HTTPConnection(host, int(port or 80), timeout=5)
    headers = {"Accept": "application/json"}
    token = args.token or os.environ.get("REACTOR_TOKEN", "")
    if token and not args.unix:
        headers["Authorization"] = "Bearer " + token
    conn.request("GET", path, headers=headers)
    r = conn.getresponse()
    body = r.read()
    conn.close()
    return r.status, body


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--url")
    ap.add_argument("--unix")
    ap.add_argument("--token")
    args = ap.parse_args()
    if not args.url and not args.unix:
        ap.error("要给 --url 或 --unix")

    try:
        status, body = fetch(args, "/api/v1/snapshot")
    except Exception as e:  # noqa: BLE001  连不上就是无法判断
        print("连不上接口：%s" % e)
        return 3
    if status == 401:
        print("接口要求令牌（401）：请用 --token 或环境变量 REACTOR_TOKEN 给一枚 viewer 以上的令牌")
        return 3
    if status != 200:
        print("接口返回 HTTP %d，无法判断" % status)
        return 3
    try:
        snap = json.loads(body.decode("utf-8"))
    except ValueError:
        print("接口返回的不是 JSON，无法判断")
        return 3

    reasons = []
    channels = snap.get("channels") or []
    if not channels:
        print("快照里没有通道数据（程序可能还在启动），无法判断")
        return 3
    for c in channels:
        cid = c.get("id", "?")
        st = c.get("state") or ""
        if st in ACTIVE_STATES:
            reasons.append("%s 号通道状态 %s" % (cid, st))
        prog = c.get("program") or {}
        if prog.get("running") or prog.get("state") in PROGRAM_ACTIVE:
            reasons.append("%s 号通道程序在执行（%s）" % (cid, prog.get("state")))
        if c.get("autotuning"):
            reasons.append("%s 号通道在自整定" % cid)
        out = c.get("heatOutputPct")
        if isinstance(out, (int, float)) and out > 0.5:
            reasons.append("%s 号通道加热输出 %.0f%%" % (cid, out))
        p = c.get("pressurePsi")
        if isinstance(p, (int, float)) and p > PRESSURE_WARN_PSI:
            reasons.append("%s 号釜带压 %.2f MPa（先放空）" % (cid, p * 0.00689476))
    stir = snap.get("stir") or {}
    if stir.get("on"):
        reasons.append("搅拌在转（%s rpm）" % stir.get("rpmSp"))
    gas = snap.get("gas") or {}
    if gas.get("task") or gas.get("owner"):
        reasons.append("气路任务在执行（%s）" % (gas.get("task") or gas.get("owner")))
    for inlet in gas.get("inlets") or []:
        if inlet.get("open"):
            reasons.append("气源阀 %s 开着" % inlet.get("id"))
    if gas.get("ventOpen"):
        reasons.append("排空阀开着")

    if reasons:
        print("有实验 / 输出在进行，不要切换：")
        for r in reasons:
            print("  - " + r)
        return 1
    print("空闲：8 路无加热 / 无程序 / 无气路任务 / 搅拌停 / 不带压")
    return 0


if __name__ == "__main__":
    sys.exit(main())
