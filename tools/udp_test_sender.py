#!/usr/bin/env python3
"""NlueMix UDP 通道独立验证器

用【已知 PCM 测试数据】验证 Android->Windows 的 UDP 音频链路本身是通的。
注意：这是通道验证，不代表真实手机音频已打通。

用法: python udp_test_sender.py --host 127.0.0.1 --code 5862 --packets 300
"""
import argparse, math, socket, struct, sys, time

MAGIC = 0x4D424C55
CONTROL_PORT = 47820
AUDIO_PORT = 47821
RATE = 48000
CH = 2
FRAME_SAMPLES = 960          # 20 ms @ 48 kHz


def build_hello(device_id, device_name, code, transport):
    return "\t".join(["HELLO", "1", device_id, device_name, code,
                      str(RATE), str(CH), "0", transport]) + "\n"


def make_packet(seq, samples, freq=1000.0):
    # 包头 16 字节，必须与 src/NlueMix/Network/Protocol.cs 的 AudioPacketHeader.Write 逐字段一致：
    #   [0..4) magic | [4] version | [5] codec | [6] flags | [7] reserved
    #   | [8..12) sequence | [12..14) samples | [14..16) reserved
    # ★ 注意是 4 个 B（原写成 3 个，包头少 1 字节，负载整体错位）
    hdr = struct.pack("<IBBBBIHH", MAGIC, 1, 0, 0, 0, seq & 0xFFFFFFFF, samples, 0)
    assert len(hdr) == 16, "包头必须是 16 字节，实际 %d" % len(hdr)
    body = bytearray()
    for i in range(samples):
        v = int(0.5 * 32767 * math.sin(2 * math.pi * freq * i / RATE))
        body += struct.pack("<hh", v, v)
    return hdr + bytes(body)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--code", default="5862")
    ap.add_argument("--packets", type=int, default=300)
    ap.add_argument("--interval-ms", type=float, default=20.0)
    ap.add_argument("--transport", default="udp", choices=["udp", "tcp"])
    ap.add_argument("--diag", action="store_true", help="同时发一条 DIAG 诊断行，验证控制通道诊断上报")
    args = ap.parse_args()

    print("=== NlueMix UDP 通道独立验证 ===")
    print("目标      : %s  控制 %d / 音频 %d" % (args.host, CONTROL_PORT, AUDIO_PORT))
    print("传输方式  : %s" % args.transport)

    # 1) TCP 握手
    tcp = socket.create_connection((args.host, CONTROL_PORT), timeout=8)
    tcp.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    hello = build_hello("udp-test-runner", "UDP 测试器", args.code, args.transport)
    tcp.sendall(hello.encode("utf-8"))
    reply = tcp.makefile("rb").readline().decode("utf-8", "replace").strip()
    print("HELLO 已发 : %r" % hello.strip())
    print("电脑回应   : %r" % reply)
    if not reply.startswith("WELCOME"):
        print("!! 握手失败，终止")
        return 1

    # 1.5) 验证 DIAG 通路：手机侧诊断经控制连接上报
    if args.diag:
        diag = "DIAG\t系统共报告 1 个播放器：\n    usage=1 content=2 flags=0xe00 ★0x400=FLAG_NO_MEDIA_PROJECTION"
        tcp.sendall(diag.encode("utf-8"))
        print("DIAG 已发  : %r" % diag.strip())

    # 2) UDP 发音频
    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 1 << 20)
    target = (args.host, AUDIO_PORT)

    total_bytes = 0
    t0 = time.time()
    for seq in range(args.packets):
        pkt = make_packet(seq, FRAME_SAMPLES)
        udp.sendto(pkt, target)
        total_bytes += len(pkt)
        time.sleep(args.interval_ms / 1000.0)
    dt = time.time() - t0

    print("已发送     : %d 包, %d 字节, 用时 %.2f 秒" % (args.packets, total_bytes, dt))
    print("每包       : %d 字节 (16 头 + %d 采样 x 2ch x 2B)" % (16 + FRAME_SAMPLES * 4, FRAME_SAMPLES))
    print("理论码率   : %.2f Mbps" % (total_bytes * 8 / dt / 1e6))
    print("=== 发送完成（通道验证，非真实手机音频）===")

    # 给接收端一点时间统计
    time.sleep(1.5)
    try: tcp.close()
    except Exception: pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
