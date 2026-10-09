#!/usr/bin/env python3
"""가짜 브리지(ESP 0.5.0, 메인보드 OTA B안) — 하드웨어 없이 서버를 시험한다.

  python3 tests/fake_esp.py --agv 7 --server 127.0.0.1:50000 [--listen 50010] [--dsp 1.0.0] [--fail-at erase]

- heartbeat(0x10) 1초: fw/part/ota + dsp/dspb/dsp_st/staged
- INFO_REQ(0x11) → INFO(0x12)
- DSP_OTA_START(0x32): url 을 HTTP 로 받아 .dspfw 헤더·본문 CRC 검증 → download(pct)/staged 보고. apply=1 이면 이어서 적용
- DSP_OTA_APPLY(0x34): start/enter/erase/progress/verify/done 보고 (job 불일치면 fail job_mismatch), 이후 dsp 버전 갱신
- OTA_START(0x30): ESP 앱 이미지를 받아 OTA_RESULT(0x31) start/progress/done 보고 후 재부팅 흉내 (fw 바뀜, ota=pending → PING 받으면 valid)
- PING → PONG, MAIN_DOWN 은 로그만
"""
import argparse, json, socket, struct, sys, time, urllib.request, zlib

def pkt(t, agv, seq, pl: bytes):
    return b"\xA5\x5A\x01" + struct.pack("<BBBHH", t, agv, 0, seq & 0xFFFF, len(pl)) + pl

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--agv", type=int, default=7); ap.add_argument("--server", default="127.0.0.1:50000")
    ap.add_argument("--listen", type=int, default=50010); ap.add_argument("--dsp", default="1.0.0")
    ap.add_argument("--fail-at", default=""); ap.add_argument("--hb", type=float, default=1.0)
    ap.add_argument("--fw", default="0.5.0", help="브리지 자체 펌웨어 버전 (OTA_START 로 바뀜)")
    a = ap.parse_args()
    host, port = a.server.split(":"); server = (host, int(port))
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.bind(("", a.listen)); s.settimeout(0.2)
    seq = {"hb": 0, "res": 0, "info": 0, "ota": 0}
    st = {"dsp": a.dsp, "dspb": "1.0.0", "state": "idle", "staged": None, "staged_job": 0, "fw": a.fw, "part": "ota_0", "ota": "valid"}
    log = lambda m: print(time.strftime("%H:%M:%S"), "[esp]", m, flush=True)

    def send(t, key, pl):
        seq[key] += 1; s.sendto(pkt(t, a.agv, seq[key], pl), server)
    def report(job, stage, pct=-1, ver=None, err=None):
        d = {"job": job, "stage": stage}
        if pct >= 0: d["pct"] = pct
        if ver is not None: d["ver"] = ver
        if err is not None: d["err"] = err
        js = json.dumps(d, separators=(",", ":")); log("DSP> " + js); send(0x33, "res", js.encode())
    def status_json():
        staged = {"job": st["staged_job"], "ver": st["staged"]} if st["staged"] else None
        return {"fw": st["fw"], "part": st["part"], "ota": st["ota"], "up": int(time.time()) % 100000, "rssi": -55, "ch": 6,
                "cfg": "saved", "ap": 0, "dsp": st["dsp"], "dspb": st["dspb"], "dsp_build": "20261009", "dsp_st": st["state"], "staged": staged}

    def do_start(c):
        job = int(c.get("job", 0)); st["state"] = "downloading"
        try:
            with urllib.request.urlopen(c["url"], timeout=10) as r: data = r.read()
        except Exception as e:
            st["state"] = "staged" if st["staged"] else "idle"; report(job, "fail", err="http_" + type(e).__name__); return
        report(job, "download", 20); report(job, "download", 100)
        if len(data) < 32 or data[:8] != b"AGVDSP1\0" or struct.unpack("<I", data[8:12])[0] != len(data):
            st["state"] = "idle"; report(job, "fail", err="image_crc"); return
        fcrc = zlib.crc32(data[32:]) & 0xFFFFFFFF
        if struct.unpack("<I", data[12:16])[0] != fcrc or (c.get("crc32", 0) and int(c["crc32"]) != fcrc):
            st["state"] = "idle"; report(job, "fail", err="crc_mismatch"); return
        if a.fail_at == "download": st["state"] = "idle"; report(job, "fail", err="flash_write"); return
        ver = data[16:32].rstrip(b"\0").decode()
        st["staged"], st["staged_job"], st["state"] = ver, job, "staged"
        report(job, "staged", 100, ver)
        if int(c.get("apply", 0)): do_apply({"job": job})

    def ota_report(js):
        log("OTA> " + js); send(0x31, "ota", js.encode())
    def do_bridge_ota(url):
        # 실제 브리지 ota.c 와 같은 보고: start → progress(pct,bytes) → done(ver,bytes) / fail(err). 완료 후 "재부팅": fw 바뀌고 ota=pending
        ota_report(json.dumps({"stage": "start", "url": url}, separators=(",", ":")))
        try:
            with urllib.request.urlopen(url, timeout=10) as r: data = r.read()
        except Exception as e:
            ota_report(json.dumps({"stage": "fail", "err": "begin " + type(e).__name__})); return
        if len(data) < 256 or data[0] != 0xE9 or struct.unpack("<I", data[32:36])[0] != 0xABCD5432:
            ota_report(json.dumps({"stage": "fail", "err": "begin ESP_ERR_OTA_VALIDATE_FAILED"})); return
        proj = data[80:112].split(b"\0")[0].decode(); ver = data[48:80].split(b"\0")[0].decode()
        if proj != "agv_wifi_bridge":
            ota_report(json.dumps({"stage": "fail", "err": f"project mismatch ({proj})"})); return
        for p in (25, 50, 75, 100):
            time.sleep(0.2); ota_report(json.dumps({"stage": "progress", "pct": p, "bytes": len(data) * p // 100}, separators=(",", ":")))
        if a.fail_at == "ota": ota_report(json.dumps({"stage": "fail", "err": "finish ESP_ERR_OTA_VALIDATE_FAILED"})); return
        ota_report(json.dumps({"stage": "done", "ver": ver, "bytes": len(data)}, separators=(",", ":")))
        time.sleep(1.5)   # 재부팅
        st["fw"], st["part"], st["ota"] = ver, ("ota_1" if st["part"] == "ota_0" else "ota_0"), "pending"
        log(f"rebooted fw={ver} part={st['part']} ota=pending")

    def do_apply(c):
        job = int(c.get("job", 0))
        if not st["staged"]: report(job, "fail", err="no_image"); return
        if job and job != st["staged_job"]: report(job, "fail", ver=st["staged"], err="job_mismatch"); return
        job = st["staged_job"]; st["state"] = "programming"
        report(job, "start", 0, st["staged"])
        for stage, dt in (("enter", 0.3), ("erase", 1.0)):
            time.sleep(dt)
            if a.fail_at == stage: st["state"] = "staged"; report(job, "fail", ver=st["staged"], err=stage + "_timeout"); return
            report(job, stage)
        for p in (0, 25, 50, 75, 100): time.sleep(0.3); report(job, "progress", p)
        time.sleep(0.3); report(job, "verify")
        time.sleep(0.5); st["dsp"] = st["staged"]; st["state"] = "staged"
        report(job, "done", 100, st["dsp"])

    log(f"agv {a.agv:02d} udp :{a.listen} -> {a.server} dsp={st['dsp']}")
    next_hb = 0
    while True:
        try:
            b, ep = s.recvfrom(2048)
            if len(b) >= 10 and b[:3] == b"\xA5\x5A\x01":
                t, agv, _f, sq, ln = struct.unpack("<BBBHH", b[3:10]); pl = b[10:]
                if agv == a.agv:
                    if t == 0x32: log("DSP< START " + pl.decode(errors="replace")); do_start(json.loads(pl))
                    elif t == 0x34: log("DSP< APPLY " + pl.decode(errors="replace")); do_apply(json.loads(pl))
                    elif t == 0x11: send(0x12, "info", json.dumps({"agv": a.agv, "mac": "aa:bb:cc:dd:ee:ff", "ip": "127.0.0.1", **status_json()}).encode())
                    elif t == 0x20:
                        if st["ota"] == "pending": st["ota"] = "valid"; log("PING -> ota valid")
                        s.sendto(pkt(0x21, a.agv, sq, pl), server)
                    elif t == 0x30: log("OTA< START " + pl.decode(errors="replace")); do_bridge_ota(pl.decode().strip("\0"))
                    elif t == 0x02: log("MAIN< " + pl.decode(errors="replace"))
        except socket.timeout:
            pass
        if time.time() >= next_hb:
            send(0x10, "hb", json.dumps(status_json(), separators=(",", ":")).encode()); next_hb = time.time() + a.hb

if __name__ == "__main__":
    sys.exit(main())
