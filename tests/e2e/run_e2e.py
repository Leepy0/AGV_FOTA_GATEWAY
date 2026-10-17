#!/usr/bin/env python3
"""게이트웨이 종단(E2E) 시험 — 빌드된 서버를 임시 데이터 폴더·임의 포트로 띄우고, 가짜 브리지(tests/fake_esp.py)
여러 대를 붙여 REST API 로 주요 흐름을 끝까지 돌린다. 표준 라이브러리만 쓴다.

  dotnet build -c Release                       # 먼저 빌드 (또는 --build)
  python3 tests/e2e/run_e2e.py                  # 전체
  python3 tests/e2e/run_e2e.py --only 1,3       # 골라서
  python3 tests/e2e/run_e2e.py --list           # 시나리오 목록
  python3 tests/e2e/run_e2e.py --dll path/AgvFotaGateway.dll --keep

시나리오마다 PASS/FAIL 과 요약을 출력하고, 실패하면 작업 기록·서버 로그·가짜 브리지 로그 끝부분을 덧붙인다.
끝나면 서버와 가짜 브리지를 모두 정리한다 (--keep 이면 임시 폴더를 남김). 종료 코드 = 실패한 시나리오 수(최대 100).
"""
import argparse
import datetime as dt
import json
import os
import random
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import traceback
import urllib.error
import urllib.request
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
FAKE = os.path.join(ROOT, "tests", "fake_esp.py")
sys.dont_write_bytecode = True   # 저장소에 __pycache__ 를 남기지 않음
sys.path.insert(0, os.path.join(ROOT, "tests"))
import fake_esp as fk  # noqa: E402  (이미지 생성 도우미)

DEFAULT_DLL = os.path.join(ROOT, "src", "Gateway.Server", "bin", "Release", "net10.0", "AgvFotaGateway.dll")
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))   # 사내 프록시 환경변수 무시 (127.0.0.1)


ALL_FAKES = []     # 중단(Ctrl+C·오류) 때도 모두 정리


class Fail(Exception):
    pass


def check(cond, msg):
    if not cond:
        raise Fail(msg)


def now_utc():
    return dt.datetime.now(dt.timezone.utc)


def iso(t):
    return t.strftime("%Y-%m-%dT%H:%M:%SZ")


def free_tcp_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def free_udp_block(n, extra=()):
    """연속 n 개(+ extra 오프셋) UDP 포트가 비어 있는 시작 번호"""
    for _ in range(200):
        base = random.randrange(20000, 60000)
        socks = []
        try:
            for p in [base + i for i in range(n)] + [base + e + i for e in extra for i in range(n)]:
                s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                socks.append(s)
                s.bind(("", p))
            return base
        except OSError:
            continue
        finally:
            for s in socks:
                s.close()
    raise RuntimeError("빈 UDP 포트 없음")


def tail(path, n):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return "".join(f.readlines()[-n:])
    except OSError:
        return "(없음)\n"


# ───────── 서버·가짜 브리지 프로세스 ─────────

class Server:
    def __init__(self, dll, work, offline_sec):
        self.dll, self.work, self.offline_sec = dll, work, offline_sec
        self.http = free_tcp_port()
        self.udp = free_udp_block(1)
        self.data = os.path.join(work, "data")
        self.log = os.path.join(work, "server.log")
        self.proc = None
        self.base = f"http://127.0.0.1:{self.http}"

    def start(self, dotnet):
        args = [dotnet, self.dll, "--urls", self.base, f"--Gateway:DataDir={self.data}", f"--Gateway:UdpPort={self.udp}",
                "--Gateway:RfidPort=0", f"--Gateway:OfflineAfterSec={self.offline_sec}", "--Gateway:WifiFlushSec=2", "--Update:Enabled=false",
                "--Logging:LogLevel:Default=Information", "--Logging:LogLevel:Microsoft.AspNetCore=Warning"]
        env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", ASPNETCORE_ENVIRONMENT="Production")
        self.out = open(self.log, "w", encoding="utf-8")
        self.proc = subprocess.Popen(args, cwd=self.work, stdout=self.out, stderr=subprocess.STDOUT, env=env)
        self.started = time.monotonic()
        t0 = time.monotonic()
        while time.monotonic() - t0 < 60:
            if self.proc.poll() is not None:
                raise RuntimeError("서버가 바로 종료됨:\n" + tail(self.log, 40))
            try:
                with OPENER.open(self.base + "/api/health", timeout=2) as r:
                    if r.status == 200:
                        return
            except Exception:
                time.sleep(0.3)
        raise RuntimeError("서버가 60초 안에 뜨지 않음:\n" + tail(self.log, 40))

    def stop(self):
        if self.proc and self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(10)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(5)
        if getattr(self, "out", None):
            self.out.close()


class Fake:
    """가짜 브리지 프로세스 하나 (여러 호기 가능)"""

    def __init__(self, ctx, tag, agvs, extra=(), dup=False):
        self.ctx, self.tag, self.agvs = ctx, tag, list(agvs)
        self.base = free_udp_block(len(agvs), (100,) if dup else ())
        self.events = os.path.join(ctx.work, f"fake-{tag}.events")
        self.log = os.path.join(ctx.work, f"fake-{tag}.log")
        args = [sys.executable, "-u", FAKE, "--agv", ",".join(map(str, agvs)), "--server", f"127.0.0.1:{ctx.server.udp}",
                "--listen", str(self.base), "--bind", "127.0.0.1", "--events", self.events, "--seed", str(agvs[0])] + list(extra)
        if dup:
            args.append("--dup-id")
        self.out = open(self.log, "w", encoding="utf-8")
        self.proc = subprocess.Popen(args, stdout=self.out, stderr=subprocess.STDOUT, cwd=ctx.work)
        ctx.fakes.append(self)
        ALL_FAKES.append(self)

    def port(self, agv):
        return self.base + self.agvs.index(agv)

    def ev(self, kind=None, agv=None, dev=None):
        out = []
        try:
            with open(self.events, encoding="utf-8") as f:
                for line in f:
                    e = json.loads(line)
                    if (kind is None or e["ev"] == kind) and (agv is None or e["agv"] == agv) and (dev is None or e["dev"] == dev):
                        out.append(e)
        except OSError:
            pass
        return out

    def rx(self, typ, agv):
        """받은 명령 (JSON payload 를 풀어서)"""
        res = []
        for e in self.ev("rx", agv):
            if e["type"] == typ and "#2" not in e["dev"]:
                try:
                    e["json"] = json.loads(e["payload"])
                except ValueError:
                    e["json"] = None
                res.append(e)
        return res

    def stop(self):
        if self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(5)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(5)
        if not self.out.closed:
            self.out.close()


# ───────── REST ─────────

class Api:
    def __init__(self, base):
        self.base = base

    def call(self, method, path, body=None, headers=None, raw=None, ctype="application/json", timeout=15):
        data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
        req = urllib.request.Request(self.base + path, data=data, method=method)
        if data is not None:
            req.add_header("Content-Type", ctype)
        for k, v in (headers or {}).items():
            req.add_header(k, v)
        try:
            with OPENER.open(req, timeout=timeout) as r:
                return r.status, self._decode(r.read())
        except urllib.error.HTTPError as e:
            return e.code, self._decode(e.read())

    @staticmethod
    def _decode(b):
        try:
            return json.loads(b) if b else None
        except ValueError:
            return b.decode(errors="replace")

    def get(self, path):
        st, body = self.call("GET", path)
        check(st == 200, f"GET {path} → {st} {body}")
        return body

    def post(self, path, body=None, **kw):
        return self.call("POST", path, body, **kw)

    def upload(self, kind, filename, content, version="", headers=None):
        b = "----e2e" + uuid.uuid4().hex
        parts = []
        for k, v in (("kind", kind), ("version", version), ("notes", "e2e")):
            parts.append(f"--{b}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n{v}\r\n".encode())
        parts.append(f"--{b}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\n"
                     f"Content-Type: application/octet-stream\r\n\r\n".encode() + content + b"\r\n")
        parts.append(f"--{b}--\r\n".encode())
        return self.call("POST", "/api/firmware", raw=b"".join(parts), ctype=f"multipart/form-data; boundary={b}", headers=headers)


# ───────── 시나리오 공통 ─────────

class Ctx:
    def __init__(self, server, work, verbose):
        self.server, self.work, self.verbose = server, work, verbose
        self.api = Api(server.base)
        self.fakes = []
        self.jobs = []
        self.notes = []
        self.warnings = []

    def fake(self, tag, agvs, *extra, dup=False):
        return Fake(self, tag, agvs, extra, dup)

    def stop_fakes(self):
        for f in self.fakes:
            f.stop()

    def agvs(self):
        return {a["id"]: a for a in self.api.get("/api/agvs")}

    def agv(self, n):
        return self.agvs().get(n)

    def wait(self, fn, timeout, what, interval=0.3):
        t0 = time.monotonic()
        last = None
        while time.monotonic() - t0 < timeout:
            last = fn()
            if last:
                return last
            time.sleep(interval)
        raise Fail(f"{what} — {timeout:.0f}초 안에 안 됨")

    def wait_online(self, ids, need_dsp=True, timeout=20):
        def ok():
            a = self.agvs()
            return all(i in a and a[i]["online"] and (not need_dsp or (a[i].get("dsp") or "?") not in ("", "?")) for i in ids)
        self.wait(ok, timeout, f"호기 {ids} 온라인{'·메인보드 버전 확인' if need_dsp else ''}")

    def upload_dsp(self, ver, seed=1, words=700):
        st, body = self.api.upload("dsp", f"agv_{ver}.dspfw", fk.build_dspfw(fk.test_code(words, seed), ver))
        check(st == 200, f"메인보드 펌웨어 {ver} 업로드 실패: {st} {body}")
        return body

    def upload_esp(self, ver):
        st, body = self.api.upload("esp", "agv_wifi_bridge.bin", fk.build_esp_app(ver), ver)
        check(st == 200, f"브리지 펌웨어 {ver} 업로드 실패: {st} {body}")
        return body

    def create_job(self, fw, targets, mode="Approve", **kw):
        body = {"name": kw.pop("name", f"e2e {fw['version']}"), "firmwareId": fw["id"], "targets": targets, "applyMode": mode}
        body.update(kw)
        st, j = self.api.post("/api/jobs", body)
        check(st == 200, f"작업 생성 실패: {st} {j}")
        self.jobs.append(j["id"])
        return j

    def job(self, jid):
        return self.api.get(f"/api/jobs/{jid}")

    def wait_job(self, jid, states, timeout, seen=None):
        end = {"Done", "Failed", "Cancelled"}
        t0 = time.monotonic()
        while time.monotonic() - t0 < timeout:
            j = self.job(jid)
            if seen is not None:
                seen.add(j["state"])
            if j["state"] in states:
                return j
            if j["state"] in end:
                raise Fail(f"작업이 {j['state']} 로 끝남 (기대 {sorted(states)})\n{job_text(j)}")
            time.sleep(0.4)
        raise Fail(f"작업 상태 {sorted(states)} 대기 {timeout}초 초과 (현재 {self.job(jid)['state']})\n{job_text(self.job(jid))}")

    def cancel_active(self):
        for jid in self.jobs:
            try:
                j = self.job(jid)
                if j["state"] in ("Scheduled", "Running", "AwaitingApproval"):
                    self.api.post(f"/api/jobs/{jid}/cancel")
            except Exception:
                pass


def job_text(j, log_lines=12):
    out = [f"작업 {j['id']} '{j['name']}' {j['kind']} {j['firmwareVersion']} 상태 {j['state']} 방식 {j.get('applyMode')}"]
    for r in j["results"]:
        out.append(f"  [{r['agv']:02d}] {r['state']} {r['percent']}% stageJob={r.get('stageJob')} before={r.get('versionBefore')} "
                   f"after={r.get('versionAfter')} — {r['message']}")
        for line in (r.get("log") or [])[-log_lines:]:
            out.append("       " + line)
    return "\n".join(out)


def result(j, agv):
    return next(r for r in j["results"] if r["agv"] == agv)


# ───────── 시나리오 ─────────

def s1_approve(ctx):
    """메인보드 Approve: 3대 스테이징 → AwaitingApproval → 승인 → 3대 적용 → Done, job 번호 일치, heartbeat 버전"""
    ids = [11, 12, 13]
    f = ctx.fake("s1", ids)
    ctx.wait_online(ids)
    fw = ctx.upload_dsp("1.1.0", seed=11)
    j = ctx.create_job(fw, ids, "Approve")
    j = ctx.wait_job(j["id"], {"AwaitingApproval"}, 120)
    # 마지막 호기는 staged 보고로 끝나므로 heartbeat 반영은 1초 안쪽 뒤
    ctx.wait(lambda: all(ctx.agv(i)["stagedJob"] for i in ids), 5, "heartbeat 에 staged 반영")
    a = ctx.agvs()
    for i in ids:
        r = result(j, i)
        check(r["state"] == "Staged" and r["stageJob"] > 0, f"[{i}] 스테이징 안 됨: {r['state']} {r['message']}")
        check(a[i]["stagedJob"] == r["stageJob"] and a[i]["stagedVer"] == "1.1.0",
              f"[{i}] heartbeat staged={a[i]['stagedJob']}/{a[i]['stagedVer']} ≠ 작업 {r['stageJob']}")
        check(a[i]["dsp"] == "1.0.0", f"[{i}] 승인 전에 메인보드가 바뀜 ({a[i]['dsp']})")
        check(not f.rx("APPLY", i), f"[{i}] 승인 전에 APPLY 를 보냄")
    st, body = ctx.api.post(f"/api/jobs/{j['id']}/approve")
    check(st == 200, f"승인 실패 {st} {body}")
    j = ctx.wait_job(j["id"], {"Done"}, 150)
    a = ctx.agvs()
    for i in ids:
        r = result(j, i)
        check(r["state"] == "Done" and r["versionAfter"] == "1.1.0", f"[{i}] {r['state']} after={r['versionAfter']}")
        check(a[i]["dsp"] == "1.1.0" and a[i]["dspState"] == "idle" and a[i]["stagedJob"] is None,
              f"[{i}] heartbeat dsp={a[i]['dsp']} st={a[i]['dspState']} staged={a[i]['stagedJob']}")
        starts = {e["json"]["job"] for e in f.rx("START", i)}
        applies = {e["json"]["job"] for e in f.rx("APPLY", i)}
        check(starts == {r["stageJob"]} and applies == {r["stageJob"]}, f"[{i}] job 번호 불일치 START {starts} APPLY {applies} 작업 {r['stageJob']}")
        check(len(f.ev("erase", i)) == 1, f"[{i}] 굽기 {len(f.ev('erase', i))}회")
    return f"3대 스테이징→승인→적용 Done, job 번호 {[result(j, i)['stageJob'] for i in ids]} 스테이징·적용 동일, heartbeat dsp 1.1.0"


def s2_immediate(ctx):
    """메인보드 Immediate: 승인 없이 스테이징 직후 적용"""
    f = ctx.fake("s2", [21])
    ctx.wait_online([21])
    fw = ctx.upload_dsp("1.2.0", seed=21)
    j = ctx.create_job(fw, [21], "Immediate")
    seen = set()
    j = ctx.wait_job(j["id"], {"Done"}, 90, seen)
    check("AwaitingApproval" not in seen, "Immediate 인데 적용 대기를 거침")
    r = result(j, 21)
    a = ctx.agv(21)
    check(a["dsp"] == "1.2.0" and r["versionAfter"] == "1.2.0", f"버전 {a['dsp']} / {r['versionAfter']}")
    check(len(f.ev("erase", 21)) == 1, "굽기 횟수")
    start = f.rx("START", 21)[0]["json"]
    check(start.get("apply") == 0, f"START apply={start.get('apply')} (서버는 스테이징·적용을 나눠 보냄)")
    return f"상태 {sorted(seen)} → Done, dsp 1.2.0"


def s3_lost_final(ctx):
    """staged 보고 유실(--drop staged) / done 보고 유실(--drop done) → heartbeat 로 판정해 성공 (C-01)"""
    f1 = ctx.fake("s3a", [31], "--drop", "staged")
    f2 = ctx.fake("s3b", [32], "--drop", "done")
    ctx.wait_online([31, 32])
    fw = ctx.upload_dsp("1.3.0", seed=31)
    j = ctx.create_job(fw, [31, 32], "Immediate")
    j = ctx.wait_job(j["id"], {"Done"}, 120)
    # 서버는 heartbeat 로 먼저 끝낼 수 있다 — 브리지의 남은 반복 보고(300ms 간격)가 다 나간 뒤 센다
    drops = lambda: ([e for e in f1.ev("tx_drop", 31) if e["kind"] == "staged"], [e for e in f2.ev("tx_drop", 32) if e["kind"] == "done"])
    try:
        ctx.wait(lambda: all(len(d) >= 3 for d in drops()), 5, "반복 보고")
    except Fail:
        pass
    d1, d2 = drops()
    check(len(d1) >= 3 and len(d2) >= 3, f"보고 유실이 일어나지 않음 (staged {len(d1)}, done {len(d2)})")
    sent = [e for e in f1.ev("dsp_report", 31) + f2.ev("dsp_report", 32) if e["stage"] in ("staged", "done") and e["sent"]]
    check(not any((e["agv"] == 31 and e["stage"] == "staged") or (e["agv"] == 32 and e["stage"] == "done") for e in sent),
          "유실시킨 보고가 서버에 감")
    a = ctx.agvs()
    check(a[31]["dsp"] == "1.3.0" and a[32]["dsp"] == "1.3.0", f"dsp {a[31]['dsp']} {a[32]['dsp']}")
    return f"staged 3회 유실·done 3회 유실에도 Done (heartbeat 판정)"


def s4_duplicate(ctx):
    """같은 job 중복(상향 일시 단절로 서버 재전송 유도) → 다시 받거나 두 번 굽지 않음"""
    f = ctx.fake("s4", [41], "--mute-after-cmd", "6.5", "--speed", "2")
    ctx.wait_online([41])
    fw = ctx.upload_dsp("1.4.0", seed=41)
    j = ctx.create_job(fw, [41], "Immediate")
    j = ctx.wait_job(j["id"], {"Done"}, 120)
    job = result(j, 41)["stageJob"]
    starts = [e for e in f.rx("START", 41) if e["json"]["job"] == job]
    applies = [e for e in f.rx("APPLY", 41) if e["json"]["job"] == job]
    check(len(starts) >= 2 and len(applies) >= 2, f"서버 재전송이 일어나지 않음 (START {len(starts)}회, APPLY {len(applies)}회) — 시나리오 조건 미충족")
    downloads, erases = f.ev("download", 41), f.ev("erase", 41)
    check(len(downloads) == 1, f"다운로드 {len(downloads)}회 (중복 START 로 다시 받음)")
    check(len(erases) == 1, f"굽기 {len(erases)}회 (이중 굽기)")
    dups = [e["ev"] for e in f.ev(agv=41) if e["ev"] in ("dup_ignored", "dup_staged", "replay")]
    check(dups, "중복 처리 흔적 없음")
    return f"START {len(starts)}회·APPLY {len(applies)}회 수신 → 다운로드 1회·굽기 1회 ({', '.join(dups)})"


def s5_bridge_ota(ctx):
    """브리지 자체 OTA → 재부팅 → 서버 자동 확정(PING) → valid. done 3회 유실(--drop ota_done)에도 성공"""
    f1 = ctx.fake("s5a", [51])
    f2 = ctx.fake("s5b", [52], "--drop", "ota_done")
    ctx.wait_online([51, 52])
    before = ctx.agvs()
    fw = ctx.upload_esp("0.5.3")
    j = ctx.create_job(fw, [51, 52])
    j = ctx.wait_job(j["id"], {"Done"}, 120)
    a = ctx.agvs()
    for i, f in ((51, f1), (52, f2)):
        check(a[i]["fw"] == "0.5.3" and a[i]["ota"] == "valid", f"[{i}] fw={a[i]['fw']} ota={a[i]['ota']}")
        check(a[i]["part"] != before[i]["part"], f"[{i}] 슬롯 그대로 ({a[i]['part']})")
        conf = f.ev("ota_confirmed", i)
        check(conf, f"[{i}] 확정 흔적 없음")
        check(not f.ev("rollback", i), f"[{i}] 롤백됨")
    d = [e for e in f2.ev("tx_drop", 52) if e["kind"] == "ota_done"]
    check(len(d) >= 3, f"ota done 유실이 일어나지 않음 ({len(d)})")
    return (f"51·52 fw 0.5.3 part {a[51]['part']} valid (확정 패킷 {f1.ev('ota_confirmed', 51)[0]['by']}, "
            f"{f2.ev('ota_confirmed', 52)[0]['by']}), done 3회 유실 호기도 heartbeat 로 진행")


def s6_dup_id(ctx):
    """같은 호기 번호 두 장치(--dup-id) → 서버가 충돌 표시·그 호기 작업·수동 전송 거부"""
    f = ctx.fake("s6", [61], "--dup-delay", "3", dup=True)
    ctx.wait_online([61])
    first = f.port(61)

    sent_ok = []

    def conflict():
        st, body = ctx.api.post("/api/agvs/61/cfg", {"data": "stat"})
        if st == 200:
            sent_ok.append(1)               # 두 번째 장치가 뜨기 전에는 정상 전달
        return st == 409 and body
    msg = ctx.wait(conflict, 15, "수동 설정 전송이 충돌(409)로 거부")
    a = ctx.agv(61)
    check(a["endPoint"].endswith(f":{first}"), f"endpoint 가 두 번째 장치로 바뀜 ({a['endPoint']})")
    keys = [k for k in a if "onflict" in k]
    if not keys:
        ctx.warnings.append("/api/agvs 에 충돌 필드가 없음 — 화면 밖(REST·스크립트)에서는 409 응답으로만 알 수 있음")
    fw = ctx.upload_dsp("1.6.0", seed=61)
    j = ctx.create_job(fw, [61], "Immediate")
    j = ctx.wait_job(j["id"], {"Failed"}, 60)
    r = result(j, 61)
    check("충돌" in r["message"], f"실패 사유에 충돌이 없음: {r['message']}")
    check(not f.ev("rx", 61, "61#2"), "두 번째 장치가 명령을 받음")
    # 충돌 전에 200 으로 받아 준 'stat' 만 도착해야 한다 — 409 뒤로는 배포·설정 명령이 나가면 안 됨
    leaked = [e for e in f.ev("rx", 61) if e["type"] in ("START", "APPLY")]
    cfgs = [e for e in f.ev("rx", 61) if e["type"] == "CFG"]
    check(not leaked and len(cfgs) == len(sent_ok),
          f"충돌 중에 배포·설정 명령이 나감: START/APPLY {len(leaked)}건, CFG 도착 {len(cfgs)}건 (200 응답 {len(sent_ok)}건)")
    return f"409 '{str(msg)[:40]}…', 작업 Failed '{r['message'][:30]}…', endpoint 유지{', /api/agvs 충돌 필드 ' + str(keys) if keys else ' (충돌 필드 없음 — 경고)'}"


def s7_deadline_input(ctx):
    """마감 시각(notAfter) 지난 대상 건너뜀, 지난 예약 시각 거부(400), POST /api/jobs 잘못된 입력 400"""
    ctx.fake("s7a", [71], "--speed", "4")
    ctx.fake("s7b", [72])
    ctx.wait_online([71, 72])
    fw = ctx.upload_dsp("1.7.0", seed=71)
    j = ctx.create_job(fw, [71, 72], "Immediate", notAfter=iso(now_utc() + dt.timedelta(seconds=10)))
    t0 = time.monotonic()
    while ctx.job(j["id"])["state"] not in ("Done", "Failed", "Cancelled"):
        check(time.monotonic() - t0 < 120, "작업이 끝나지 않음")
        time.sleep(0.5)
    j = ctx.job(j["id"])
    r1, r2 = result(j, 71), result(j, 72)
    check(r1["state"] == "Done", f"[71] 마감 전에 시작한 호기는 끝까지 ({r1['state']} {r1['message']})")
    check(r2["state"] == "Skipped" and "마감" in r2["message"], f"[72] 마감 지난 호기 건너뜀이 아님 ({r2['state']} {r2['message']})")
    check(j["state"] != "Done", f"작업 상태 {j['state']}")
    later = now_utc() + dt.timedelta(hours=1)
    bad = [
        ("지난 예약 시각", {"firmwareId": fw["id"], "targets": [72], "scheduledAt": iso(now_utc() - dt.timedelta(minutes=10))}),
        ("대상 없음", {"firmwareId": fw["id"], "targets": []}),
        ("대상 0", {"firmwareId": fw["id"], "targets": [0]}),
        ("대상 100", {"firmwareId": fw["id"], "targets": [100]}),
        ("applyMode 오류", {"firmwareId": fw["id"], "targets": [72], "applyMode": "Bogus"}),
        ("applyMode 숫자", {"firmwareId": fw["id"], "targets": [72], "applyMode": "1"}),
        ("없는 펌웨어", {"firmwareId": "nope", "targets": [72]}),
        ("firmwareId 없음", {"targets": [72]}),
        ("지난 마감 시각", {"firmwareId": fw["id"], "targets": [72], "notAfter": iso(now_utc() - dt.timedelta(minutes=1))}),
        ("마감 ≤ 예약", {"firmwareId": fw["id"], "targets": [72], "scheduledAt": iso(later), "notAfter": iso(later - dt.timedelta(minutes=30))}),
        ("targets 형식", {"firmwareId": fw["id"], "targets": "abc"}),
    ]
    wrong = []
    for name, body in bad:
        st, resp = ctx.api.post("/api/jobs", body)
        if st != 400:
            wrong.append(f"{name} → {st} {str(resp)[:80]}")
            if st == 200 and isinstance(resp, dict):
                ctx.jobs.append(resp["id"])
                ctx.api.post(f"/api/jobs/{resp['id']}/cancel")
    for name, raw in (("JSON 깨짐", b"{"), ("본문 없음", b"")):
        st, resp = ctx.api.call("POST", "/api/jobs", raw=raw)
        if st != 400:
            wrong.append(f"{name} → {st} {str(resp)[:80]}")
    check(not wrong, "400 이 아님: " + "; ".join(wrong))
    st, resp = ctx.api.call("DELETE", "/api/jobs/no-such-job")
    check(st == 404, f"없는 작업 삭제 → {st} (기대 404) {resp}")
    return f"[71] Done·[72] Skipped(마감), 작업 {j['state']}, 잘못된 입력 {len(bad) + 2}종 모두 400, 없는 작업 삭제 404"


def s8_upload_reject(ctx):
    """.dspfw 업로드 검사: 버전 9자·블록 깨짐·헤더 버전 불일치 거부, 잘못된 프로젝트 ESP 이미지 거부"""
    code = fk.test_code(700, 8)
    good = fk.build_dspfw(code, "1.8.0")
    nine = fk.build_dspfw(code, "1.8.0.001")
    body = bytearray(good[fk.FILE_HDR_LEN:])
    import struct
    struct.pack_into("<I", body, fk.HDR_WORDS * 2, fk.APP_HDR_ADDR)   # 첫 블록 주소를 앱 영역 밖으로 (파일 CRC 는 다시 맞춤)
    broken = fk.wrap_dspfw(bytes(body), "1.8.0")
    check(fk.stage_check(broken)[0] == "image_bad" and fk.stage_check(nine)[0] == "ver_invalid", "시험 이미지 준비 오류")
    mism = fk.build_dspfw(code, "1.8.0", file_ver="1.8.1")
    cases = [("버전 9자", "dsp", "a9.dspfw", nine), ("블록 깨짐", "dsp", "bad.dspfw", broken), ("헤더 버전 불일치", "dsp", "m.dspfw", mism),
             ("다른 프로젝트", "esp", "x.bin", fk.build_esp_app("0.9.0", "other_proj"))]
    wrong = []
    for name, kind, fn, data in cases:
        st, resp = ctx.api.upload(kind, fn, data)
        if st != 400:
            wrong.append(f"{name} → {st} {str(resp)[:80]}")
    st, resp = ctx.api.upload("dsp", "ok.dspfw", good)
    check(st == 200, f"정상 이미지 업로드 실패 {st} {resp}")
    check(not wrong, "거부되지 않음: " + "; ".join(wrong))
    st, resp = ctx.api.upload("dsp", "ok2.dspfw", good, headers={"Origin": "http://evil.example"})
    check(st == 403, f"다른 출처 업로드 {st}")
    return "버전 9자·블록 깨짐·헤더 버전 불일치·다른 프로젝트 400, 정상 200, 다른 출처 403"


def s9_recover(ctx):
    """멈춘 앱(--nodsp mute --app-frames no, dsp='?') 호기 → '복구 적용' 작업(recover:true)이 APPLY 에 recover:1, wait_power 뒤 전원 재투입으로 성공"""
    f = ctx.fake("s9", [91], "--nodsp", "mute", "--app-frames", "no", "--power-cycle-sec", "3")
    ctx.wait_online([91], need_dsp=False)
    check(ctx.agv(91)["dsp"] == "?", f"dsp={ctx.agv(91)['dsp']}")
    fw = ctx.upload_dsp("1.9.0", seed=91)
    j = ctx.create_job(fw, [91], "Immediate", recover=True)
    check(j.get("recover") is True, f"작업에 recover 가 없음: {j.get('recover')}")
    j = ctx.wait_job(j["id"], {"Done"}, 150)
    ap = f.rx("APPLY", 91)
    check(ap and all(e["json"].get("recover") == 1 for e in ap), f"APPLY 에 recover:1 없음: {[e['payload'] for e in ap]}")
    check(f.ev("raw_mode", 91), "원시 모드(부트로더 직행) 흔적 없음")
    wp = [e for e in f.ev("dsp_report", 91) if e["stage"] == "wait_power"]
    check(wp, "wait_power 보고 없음")
    check(ctx.agv(91)["dsp"] == "1.9.0", f"dsp={ctx.agv(91)['dsp']}")
    check(any("복구 적용" in line for line in result(j, 91)["log"]), "작업 기록에 복구 적용 안내 없음")
    return f"recover 작업 → APPLY {ap[0]['payload']} → wait_power {len(wp)}회 → 전원 재투입 → Done dsp 1.9.0"


def s10_manual_guard(ctx):
    """수동 전송 F/R 프레임 거부, 다른 출처(Origin)·cross-site POST 403"""
    f = ctx.fake("s10", [1])
    ctx.wait_online([1])
    base = ctx.server.base
    wrong = []
    for name, data in (("F 프레임", "<STX>F1101U000 X<ETX>"), ("R 프레임", "<STX>R1101UOK00 X<ETX>"), ("STX 없는 F", "F1101V000 X")):
        st, resp = ctx.api.post("/api/agvs/1/send", {"data": data})
        if st != 400:
            wrong.append(f"{name} → {st} {str(resp)[:60]}")
    st, resp = ctx.api.post("/api/agvs/1/send", {"data": "<STX>C1101S00 X<ETX>"})
    if st != 200:
        wrong.append(f"정상 프레임 → {st} {resp}")
    for name, hdr, want in (("다른 출처 Origin", {"Origin": "http://evil.example"}, 403), ("다른 포트 Origin", {"Origin": "http://127.0.0.1:1"}, 403),
                            ("cross-site", {"Sec-Fetch-Site": "cross-site"}, 403), ("같은 출처 Origin", {"Origin": base}, 200)):
        st, resp = ctx.api.post("/api/agvs/1/info", headers=hdr)
        if st != want:
            wrong.append(f"{name} → {st} (기대 {want})")
    st, _ = ctx.api.call("POST", "/api/jobs/x/cancel", headers={"Origin": "http://evil.example"})
    if st != 403:
        wrong.append(f"작업 취소 다른 출처 → {st}")
    st, _ = ctx.api.call("GET", "/api/agvs", headers={"Origin": "http://evil.example"})
    if st != 200:
        wrong.append(f"GET 다른 출처 → {st} (읽기는 허용)")
    time.sleep(0.5)
    mains = [e["payload"] for e in f.rx("MAIN", 1)]
    if any(("F1" in m or "R1" in m) for m in mains):
        wrong.append(f"F/R 프레임이 브리지에 도착: {mains}")
    check(not wrong, "; ".join(wrong))
    return f"F/R 400, 정상 프레임 전달({len(mains)}건), 다른 출처·cross-site 403, 같은 출처 200"


def s12_old_app(ctx):
    """살아 있는 구 앱(dsp='?') 일반 적용 → 원시 모드 없이 app_no_fwup(JTAG 안내) / 버전 아는 앱이 U 에 침묵 → app_no_reply 뒤 dsp '?'(복구 적용 안내) — 둘 다 recover 없음"""
    f1 = ctx.fake("s12a", [85], "--nodsp", "mute")
    f2 = ctx.fake("s12b", [86], "--app-u", "noreply", "--app-frames", "no")
    ctx.wait_online([86])
    ctx.wait_online([85], need_dsp=False)
    check(ctx.agv(85)["dsp"] == "?", f"[85] dsp={ctx.agv(85)['dsp']}")
    fw = ctx.upload_dsp("1.12.0", seed=85)
    j = ctx.create_job(fw, [85, 86], "Immediate", stopOnFailure=False)
    check(j.get("recover") is False, f"recover 를 고르지 않았는데 {j.get('recover')}")
    j = ctx.wait_job(j["id"], {"Failed"}, 90)
    r1, r2 = result(j, 85), result(j, 86)
    check(r1["state"] == "Failed" and "app_no_fwup" in r1["message"], f"[85] {r1['state']} {r1['message']}")
    check(r2["state"] == "Failed" and "app_no_reply" in r2["message"], f"[86] {r2['state']} {r2['message']}")
    check("JTAG" in r1["message"] and "복구 적용으로" not in r1["message"], f"[85] 구 앱은 JTAG 안내만 (복구 적용으로도 안 됨): {r1['message']}")
    check(r2["message"].count("복구 적용으로 다시 배포") == 1, f"[86] 복구 적용 안내가 한 번 있어야 함: {r2['message']}")
    ap = f1.rx("APPLY", 85) + f2.rx("APPLY", 86)
    check(ap and all("recover" not in e["json"] for e in ap), f"recover 가 붙음: {[e['payload'] for e in ap]}")
    check(not f1.ev("raw_mode") and not f2.ev("raw_mode"), "원시 모드에 들어감 (RFID·ACS 중계 끊김)")
    check(not [e for e in f1.ev("dsp_report") + f2.ev("dsp_report") if e["stage"] == "wait_power"], "wait_power 보고가 나감")
    ctx.wait(lambda: ctx.agv(86)["dsp"] == "?", 5, "[86] app_no_reply 뒤 heartbeat dsp '?'")
    return "[85] app_no_fwup(JTAG 안내)·[86] app_no_reply(복구 적용 안내) — 원시 모드·wait_power 없음, recover 없음, 86 dsp → '?'"


def s11_cfg_trial(ctx):
    """원격 설정 save·reboot → cfg=trial → 서버 자동 PING 으로 saved (결정 3)"""
    f = ctx.fake("s11", [2], "--confirm-sec", "60")
    ctx.wait_online([2])
    wrong = []
    for cmd in ("set hb_ms 500", "save", "reboot"):
        st, resp = ctx.api.post("/api/agvs/2/cfg", {"data": cmd})
        if st != 200:
            wrong.append(f"{cmd} → {st} {resp}")
    check(not wrong, "; ".join(wrong))
    ctx.wait(lambda: f.ev("booted", 2), 15, "재부팅")
    boot = f.ev("booted", 2)[0]
    check(boot["cfg"] == "trial", f"재부팅 뒤 cfg={boot['cfg']}")
    conf = ctx.wait(lambda: f.ev("cfg_confirmed", 2), 20, "원격 설정 확정 (cfg=saved)")
    ctx.wait(lambda: ctx.agv(2)["cfg"] == "saved", 10, "서버가 cfg=saved 를 봄")
    check(not f.ev("cfg_reverted", 2), "이전 설정으로 되돌아감")
    t = conf[0]["t"] - boot["t"]
    results = [e["payload"] for e in f.rx("CFG", 2)]
    return f"save→reboot→trial→{conf[0]['by']} 로 {t:.1f}초 만에 saved (명령 {results})"


def s13_clock(ctx):
    """시각 동기: 브리지가 서버에 묻고(reply), 틀린 시계는 서버가 편차를 보고 먼저 보낸다(push). 시각 동기를 모르는 구 펌웨어는 제외"""
    fa = ctx.fake("s13a", [31], "--fw", "0.8.0", "--clock-first-req", "0.5", "--up-start", "5")
    fb = ctx.fake("s13b", [32], "--fw", "0.8.0", "--clock-wrong-ms", "5000", "--clock-no-req", "--up-start", "5")
    ctx.fake("s13c", [33], "--fw", "0.7.0")
    ctx.wait_online([31, 32, 33])
    ev = ctx.wait(lambda: fa.ev("time_sync", 31), 20, "31 호기가 서버에 시각을 물어 맞춤")
    check(ev[0]["kind"] == "reply", f"처음 동기가 reply 가 아님: {ev[0]}")
    a = ctx.wait(lambda: (lambda x: x if x and x.get("clockSynced") and x.get("clockSkewMs") is not None else None)(ctx.agv(31)), 15, "서버가 31 호기 편차를 봄")
    check(a["timeReplies"] >= 1, f"서버가 시각 요청에 답한 횟수 {a['timeReplies']}")
    check(abs(a["clockSkewMs"]) < 1000, f"31 호기 편차 {a['clockSkewMs']} ms")

    # 32 호기: +5초 틀린 채 맞춘 줄 알고 서버에 묻지 않음 → 서버가 heartbeat 의 ts 로 편차를 보고 시각을 먼저 보낸다
    pushed = ctx.wait(lambda: fb.ev("time_sync", 32), 20, "서버가 32 호기에 시각을 먼저 보냄")
    check(pushed[0]["kind"] == "push", f"32 호기 동기 방식 {pushed[0]['kind']}")
    fixed = ctx.wait(lambda: (lambda x: x if x and x.get("clockSkewMs") is not None and abs(x["clockSkewMs"]) < 1000 else None)(ctx.agv(32)), 15, "32 호기 편차가 1초 안으로 줄어듦")

    # 화면의 "시각 동기" = POST /api/agvs/{n}/time
    n = len(fa.ev("time_sync", 31))
    st, body = ctx.api.post("/api/agvs/31/time")
    check(st == 200, f"수동 시각 동기 {st} {body}")
    ctx.wait(lambda: len(fa.ev("time_sync", 31)) > n, 10, "수동으로 보낸 시각이 도착")

    # 0.7.0 은 시각 동기를 모른다: 거부, 편차 없음
    st, body = ctx.api.post("/api/agvs/33/time")
    check(st == 400 and "0.8.0" in str(body), f"구 펌웨어 시각 동기 거부: {st} {body}")
    old = ctx.agv(33)
    check(old["clockSkewMs"] is None and not old["clockSynced"], f"구 펌웨어에 편차가 보임: {old}")
    return f"31 reply(편차 {a['clockSkewMs']} ms) · 32 push(+5초 → {fixed['clockSkewMs']} ms) · 수동 동기 도착 · 0.7.0 거부"


def s14_diag(ctx):
    """원격 진단: cfg/wait 로 설정·상태·AP 스캔·ping 을 읽는다. 0.5.5 는 텍스트·모르는 명령, 끝 표시 줄은 서버가 뗀다"""
    ctx.fake("s14a", [34], "--fw", "0.8.0")
    ctx.fake("s14b", [35], "--fw", "0.5.5")
    ctx.wait_online([34, 35])

    def ask(agv, cmd):
        return ctx.api.post(f"/api/agvs/{agv}/cfg/wait", {"data": cmd})

    st, r = ask(34, "cfg json")
    check(st == 200 and r["ok"], f"cfg json {st} {r}")
    check(not r["text"].rstrip().endswith("\n>"), "응답 끝 표시 줄('>')이 남아 있음")
    js = json.loads(r["text"])
    check(js["fw"] == "0.8.0" and js["cfg"]["agv"] == 34 and js["state"] == "saved", f"cfg json 내용: {js}")
    st, r = ask(34, "stat json")
    stj = json.loads(r["text"])
    check(stj["wifi"]["up"] is True and stj["setup"]["name"].startswith("AGVBR-34"), f"stat json 내용: {stj}")
    st, r = ask(34, "scan")
    aps = json.loads(r["text"])
    check(aps[0]["ssid"] == "AGV_WIFI" and aps[0]["auth"] == "wpa2", f"scan: {aps}")
    st, r = ask(34, "nettest")
    check("3/3 ok" in r["text"], f"nettest: {r}")

    st, r = ask(35, "cfg json")                                 # 0.5.5: 인자를 무시하고 텍스트
    check(st == 200 and r["text"].startswith("agv=35 "), f"0.5.5 cfg json: {r}")
    st, r = ask(35, "scan")
    check(r["text"].startswith("unknown command 'scan'"), f"0.5.5 scan: {r}")

    st, r = ask(98, "stat")                                     # 모르는 호기
    check(st == 504 and r["ok"] is False, f"모르는 호기: {st} {r}")
    st, r = ctx.api.post("/api/agvs/34/cfg/wait", {"data": "x" * 1100})
    check(st == 400, f"1024바이트 초과 명령 거부: {st}")
    return "cfg json·stat json·scan·nettest 응답 확인, 0.5.5 는 텍스트·unknown command, 모르는 호기 504"


def s15_cfg_fleet(ctx):
    """호기 설정 자동 수집 → 기준값(다수결)·드리프트 → 일괄 변경(2대씩, 재부팅·확정·확인)"""
    ctx.fake("s15a", [41, 42], "--fw", "0.8.0", "--confirm-sec", "60")
    ctx.fake("s15b", [43], "--fw", "0.8.0", "--hb", "0.5", "--confirm-sec", "60")        # 43: hb_ms 500 (다른 호기는 1000)
    ctx.wait_online([41, 42, 43])
    ctx.wait(lambda: {a["agv"] for a in ctx.api.get("/api/cfg")["agvs"]} >= {41, 42, 43}, 40, "설정이 자동으로 수집됨 (처음 보인 호기)")

    st, r = ctx.api.post("/api/cfg/baseline/from-fleet")
    check(st == 200 and r["baseline"]["hb_ms"] == "1000", f"다수결 기준값: {st} {r}")
    drift = {a["agv"]: [d["key"] for d in a["drift"]] for a in ctx.api.get("/api/cfg")["agvs"]}
    check(drift[41] == [] and drift[42] == [] and drift[43] == ["hb_ms"], f"드리프트: {drift}")

    # 입력 검사
    for body, why in (({"agvs": [41], "changes": [{"key": "ip", "value": "1.2.3.4"}]}, "ip 는 호기마다 달라 일괄 불가"),
                      ({"agvs": [41], "changes": []}, "바꿀 항목 없음"),
                      ({"agvs": [], "changes": [{"key": "hb_ms", "value": "500"}]}, "대상 없음"),
                      ({"agvs": [41], "changes": [{"key": "hb_ms", "value": "500"}], "batchSize": 99}, "묶음 크기 범위")):
        st, resp = ctx.api.post("/api/cfg/bulk", body)
        check(st == 400, f"{why}: {st} {resp}")

    # 기준값(hb_ms 1000)으로 맞추면서 dns 도 바꿈 — 2대씩 두 묶음
    st, run = ctx.api.post("/api/cfg/bulk", {"agvs": [41, 42, 43], "changes": [{"key": "hb_ms", "value": "1000"}, {"key": "dns", "value": "192.168.10.1"}], "batchSize": 2})
    check(st == 200, f"일괄 변경 시작: {st} {run}")
    st, again = ctx.api.post("/api/cfg/bulk", {"agvs": [41], "changes": [{"key": "hb_ms", "value": "900"}]})
    check(st == 400, f"실행 중에 또 시작: {st} {again}")
    final = ctx.wait(lambda: (lambda r: r if r["state"] != "Running" else None)(ctx.api.get("/api/cfg/bulk")), 150, "일괄 변경 끝", interval=1)
    detail = [(t["agv"], t["state"], t["message"]) for t in final["targets"]]
    check(final["state"] == "Done" and all(t["state"] == "Done" for t in final["targets"]), f"일괄 변경 결과 {final['state']} {final['message']}: {detail}")
    check(final["batchCount"] == 2, f"묶음 수 {final['batchCount']}")

    st, res = ctx.api.post("/api/cfg/collect", {"agvs": [41, 42, 43]})
    check(st == 200 and res["ok"] == 3, f"다시 읽기: {st} {res}")
    cfg = {a["agv"]: a for a in ctx.api.get("/api/cfg")["agvs"]}
    shown = {k: (v["values"]["dns"], v["values"]["hb_ms"]) for k, v in cfg.items()}
    check(all(c["values"]["dns"] == "192.168.10.1" and c["values"]["hb_ms"] == "1000" for c in cfg.values()), f"반영 안 됨: {shown}")
    check(not [d for d in cfg[43]["drift"] if d["key"] == "hb_ms"], "43 호기 hb_ms 드리프트가 남음")
    return f"자동 수집 3대 · 다수결 기준값 · 43 hb_ms 드리프트 → 일괄 변경 {final['batchCount']}묶음 완료, 값 반영 확인"


def s16_wifi(ctx):
    """Wi-Fi 분석: heartbeat 의 BSSID 변경 → 로밍, disc 증가 → 끊김. AP 별 집계"""
    ctx.fake("s16", [51], "--fw", "0.8.0", "--roam-at", "4", "--disc-at", "9")
    ctx.wait_online([51])
    ev = ctx.wait(lambda: (lambda e: e if {x["kind"] for x in e} >= {"Roam", "Disconnect"} else None)(ctx.api.get("/api/wifi/events?hours=1&agv=51")), 60, "로밍·끊김 사건이 기록됨", interval=1)
    roam = next(x for x in ev if x["kind"] == "Roam")
    check(roam["fromBssid"] == "02:00:00:00:00:01" and roam["toBssid"] == "02:00:00:00:00:02" and roam["channel"] == 11, f"로밍 내용: {roam}")
    aps = {a["bssid"]: a for a in ctx.api.get("/api/wifi/aps?hours=1")}
    check({"02:00:00:00:00:01", "02:00:00:00:00:02"} <= set(aps), f"AP 집계: {list(aps)}")
    check(aps["02:00:00:00:00:02"]["roams"] >= 1 and aps["02:00:00:00:00:02"]["samples"] > 0, f"새 AP 집계: {aps['02:00:00:00:00:02']}")
    spots = ctx.api.get("/api/wifi/spots?hours=1")
    check(spots and spots[0]["tag"] == "", f"태그를 읽은 적이 없으면 위치 키가 비어 있음: {spots}")
    other = ctx.api.get("/api/wifi/events?hours=1&agv=52")
    check(other == [], f"다른 호기 필터: {other}")
    return f"로밍 {roam['fromBssid'][-2:]}→{roam['toBssid'][-2:]} (ch {roam['channel']}) · 끊김 기록 · AP {len(aps)}곳 집계"


SCENARIOS = [
    (1, "메인보드 Approve 3대", s1_approve),
    (2, "메인보드 Immediate", s2_immediate),
    (3, "staged·done 보고 유실 (C-01)", s3_lost_final),
    (4, "같은 job 중복 → 이중 굽기 없음", s4_duplicate),
    (5, "브리지 OTA 자동 확정·done 유실", s5_bridge_ota),
    (6, "호기 번호 충돌", s6_dup_id),
    (7, "마감 시각·잘못된 입력 400", s7_deadline_input),
    (8, ".dspfw 업로드 거부", s8_upload_reject),
    (9, "멈춘 앱 복구 적용 (recover:true)", s9_recover),
    (10, "수동 전송 F/R·다른 출처 403", s10_manual_guard),
    (11, "원격 설정 trial → saved", s11_cfg_trial),
    (12, "구 앱·침묵 앱 일반 적용 → app_no_fwup·app_no_reply", s12_old_app),
    (13, "시각 동기 (요청 응답·서버가 먼저 보정·구 펌웨어 제외)", s13_clock),
    (14, "원격 진단 cfg/wait (cfg json·stat json·scan·nettest)", s14_diag),
    (15, "호기 설정 수집·기준값·드리프트·일괄 변경", s15_cfg_fleet),
    (16, "Wi-Fi 분석 (로밍·끊김·AP 집계)", s16_wifi),
]


def find_dotnet():
    for c in (os.environ.get("DOTNET"), shutil.which("dotnet"), os.path.expanduser("~/.dotnet/dotnet"), "/home/user/.dotnet/dotnet"):
        if c and os.path.exists(c):
            return c
    raise SystemExit("dotnet 을 찾을 수 없음 (DOTNET 환경변수로 지정)")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--only", default="", help="시나리오 번호 (쉼표)")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--dll", default=DEFAULT_DLL)
    ap.add_argument("--build", action="store_true", help="먼저 dotnet build -c Release")
    ap.add_argument("--keep", action="store_true", help="임시 폴더 남김")
    ap.add_argument("--offline-sec", type=int, default=5, help="서버 Gateway:OfflineAfterSec")
    ap.add_argument("--verbose", action="store_true", help="성공한 시나리오도 작업 기록을 출력")
    a = ap.parse_args()
    if a.list:
        for n, name, fn in SCENARIOS:
            print(f"{n:2d}. {name} — {fn.__doc__}")
        return 0
    only = {int(x) for x in a.only.split(",") if x.strip()}
    todo = [s for s in SCENARIOS if not only or s[0] in only]
    dotnet = find_dotnet()
    if a.build:
        subprocess.run([dotnet, "build", "-c", "Release"], cwd=ROOT, check=True)
    if not os.path.exists(a.dll):
        raise SystemExit(f"서버 dll 없음: {a.dll} (dotnet build -c Release 또는 --build)")

    work = tempfile.mkdtemp(prefix="gw-e2e-")
    server = Server(a.dll, work, a.offline_sec)
    print(f"서버 {a.dll}\n  http {server.base}  udp {server.udp}  임시 폴더 {work}", flush=True)
    t_all = time.monotonic()
    results = []
    try:
        server.start(dotnet)
        for n, name, fn in todo:
            ctx = Ctx(server, work, a.verbose)
            t0 = time.monotonic()
            print(f"\n── {n}. {name}", flush=True)
            try:
                summary = fn(ctx)
                ok = True
            except Fail as e:
                ok, summary = False, str(e)
            except Exception as e:
                ok, summary = False, f"시험 스크립트 오류: {e!r}\n{traceback.format_exc()}"
            dur = time.monotonic() - t0
            print(f"{'PASS' if ok else 'FAIL'} {n}. {name} ({dur:.1f}s) — {summary}", flush=True)
            for w in ctx.warnings:
                print(f"  경고: {w}", flush=True)
            if ok and a.verbose:
                for jid in ctx.jobs:
                    try:
                        print(job_text(ctx.job(jid), 40))
                    except Exception:
                        pass
            if not ok:
                for jid in ctx.jobs:
                    try:
                        print(job_text(ctx.job(jid), 30))
                    except Exception:
                        pass
                print("  ── 서버 로그 (끝 40줄)\n" + tail(server.log, 40))
                for f in ctx.fakes:
                    print(f"  ── 가짜 브리지 {f.tag} 로그 (끝 30줄)\n" + tail(f.log, 30))
            ctx.cancel_active()
            ctx.stop_fakes()
            results.append((n, name, ok, dur, ctx.warnings))
    finally:
        for f in ALL_FAKES:
            f.stop()
        server.stop()
        if not a.keep:
            shutil.rmtree(work, ignore_errors=True)
    total = time.monotonic() - t_all
    fails = [r for r in results if not r[2]]
    print(f"\n결과: {len(results) - len(fails)}/{len(results)} PASS ({total:.0f}s)")
    for n, name, ok, dur, warns in results:
        print(f"  {'PASS' if ok else 'FAIL'} {n:2d}. {name} ({dur:.0f}s){'  [경고 ' + str(len(warns)) + ']' if warns else ''}")
    if a.keep:
        print(f"임시 폴더: {work}")
    return min(len(fails), 100)


if __name__ == "__main__":
    sys.exit(main())
