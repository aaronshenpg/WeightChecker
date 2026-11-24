"""称重机模拟器：在本机 50001 端口扮演称重机，读取 test_data 下的 CSV，每0.2秒发送一条称重数据。

WeightCheckerCS 作为 TCP 客户端连接称重机，所以本程序是 TCP 服务端。
报文格式与程序默认字段序号一致（settings.json：sequence=2, timestamp=3, qualification=5, weight=6）：
    WC,01,<序号>,<时间>,0,<原始判定 1/2/E>,<重量>\r\n
例：WC,01,1,2026-09-24T17:55:25.427+08:00,0,E,212.8

用法:
  python tools/weigh_sim.py                                   # 默认 tools/test_data/36-sku.csv，127.0.0.1:50001，1 秒一条
  python tools/weigh_sim.py tools/test_data/36-sku.csv --port 50001 --interval 1 --start 100 --loop
程序断开后会等待重新连接，并从断开处继续发送。Ctrl+C 退出。
"""
import argparse
import csv
import socket
import sys
import time
from pathlib import Path

DEFAULT_CSV = Path(__file__).resolve().parent / "test_data" / "36-sku.csv"


def load_rows(path):
    with open(path, encoding="utf-8-sig", newline="") as f:
        rows = list(csv.DictReader(f))
    if rows and not {"received_at", "weight"} <= rows[0].keys():
        raise SystemExit(f"{path}: 需要列 received_at, weight[, qualification_field_value]")
    return rows


def to_line(seq, row):
    code = row.get("qualification_field_value", "") or ""
    return f"WC,01,{seq},{row['received_at']},0,{code},{row['weight']}\r\n"


def main():
    ap = argparse.ArgumentParser(description="称重机模拟器（TCP 服务端）")
    ap.add_argument("csv", nargs="?", default=str(DEFAULT_CSV), help="称重数据 CSV，默认 tools/test_data/36-sku.csv")
    ap.add_argument("--host", default="127.0.0.1", help="监听地址，默认 127.0.0.1（局域网可用 0.0.0.0）")
    ap.add_argument("--port", type=int, default=50001, help="监听端口，默认 50001")
    ap.add_argument("--interval", type=float, default=0.2, help="发送间隔（秒），默认 0.2")
    ap.add_argument("--start", type=int, default=1, help="从第几条开始（1 起），默认 1")
    ap.add_argument("--count", type=int, default=0, help="最多发送条数，0=全部")
    ap.add_argument("--loop", action="store_true", help="发完后从头循环")
    a = ap.parse_args()

    rows = load_rows(a.csv)
    if not rows:
        raise SystemExit("CSV 为空")
    idx, sent = max(a.start, 1) - 1, 0
    srv = socket.socket()
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((a.host, a.port))
    srv.listen(1)
    print(f"称重机模拟器：{a.host}:{a.port}，{Path(a.csv).name} 共 {len(rows)} 条，间隔 {a.interval:g} 秒。等待程序连接…", flush=True)
    try:
        while True:
            conn, addr = srv.accept()
            print(f"已连接：{addr[0]}:{addr[1]}", flush=True)
            try:
                with conn:
                    next_t = time.monotonic()
                    while True:
                        if idx >= len(rows):
                            if not a.loop:
                                print("全部发送完毕", flush=True)
                                return
                            idx = 0
                        if a.count and sent >= a.count:
                            print(f"已发送 {sent} 条，结束", flush=True)
                            return
                        line = to_line(idx + 1, rows[idx])
                        conn.sendall(line.encode("ascii"))
                        idx += 1
                        sent += 1
                        print(f"[{sent}] {line.strip()}", flush=True)
                        next_t += a.interval
                        time.sleep(max(0.0, next_t - time.monotonic()))
            except OSError as e:
                print(f"连接断开（{e}），等待重新连接，下一条为第 {idx + 1} 条", flush=True)
    except KeyboardInterrupt:
        print(f"\n已停止，共发送 {sent} 条", flush=True)
    finally:
        srv.close()


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    main()
