#!/usr/bin/env python3
"""가짜 브리지(tests/fake_esp.py) 자체 시험 — 서버 없이 UDP·HTTP 로 직접 붙어 브리지 0.5.2 와 같은 보고를 내는지 본다.
기본은 패키지 보관(브리지 0.9.0)까지 흉내낸다: START 의 pkg_* 필수, 패키지 먼저 받아 크기·sha256 확인, heartbeat 의 pkg_st·dsp_pkg·staged.pkg.

  python3 tests/e2e/test_fake_esp.py        (또는 python3 -m unittest discover -s tests/e2e -p 'test_*.py')
"""
import hashlib
import http.server
import json
import os
import socket
import struct
import sys
import tempfile
import threading
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.dont_write_bytecode = True   # 저장소에 __pycache__ 를 남기지 않음
sys.path.insert(0, os.path.join(os.path.dirname(HERE)))
import fake_esp as fk  # noqa: E402

HB_KEYS_080 = ["fw", "mac", "part", "ota", "up", "rssi", "ch", "bssid", "disc", "reason", "m_rx", "m_tx", "r_rx", "u_err", "n_off",
               "n_err", "heap", "cfg", "setup", "dsp", "dspb", "dsp_build", "dsp_st", "staged"]
HB_KEYS = HB_KEYS_080 + ["pkg_st", "dsp_pkg"]          # 패키지 보관(0.9.0)이 덧붙이는 필드


class Files(http.server.BaseHTTPRequestHandler):
    files = {}

    def do_GET(self):
        if self.path.startswith("/redir/"):
            self.send_response(302)
            self.send_header("Location", self.path[len("/redir"):].replace("/to/", "http://", 1))
            self.end_headers()
            return
        data = self.files.get(self.path)
        if data is None:
            self.send_response(404)
            self.end_headers()
            return
        self.send_response(200)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


class FakeBridgeTest(unittest.TestCase):
    """한 시험마다 가짜 브리지 한 대 + 서버 역할 UDP 소켓 + 파일 HTTP 서버"""
    extra = []

    def setUp(self):
        self.http = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Files)
        threading.Thread(target=self.http.serve_forever, daemon=True).start()
        self.hport = self.http.server_address[1]
        self.srv = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.srv.bind(("127.0.0.1", 0))
        self.srv.settimeout(0.1)
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.bind(("127.0.0.1", 0))
            self.bport = s.getsockname()[1]
        self.events = tempfile.NamedTemporaryFile(prefix="fake-", suffix=".events", delete=False).name
        o = fk.parse_args(["--agv", "7", "--server", f"127.0.0.1:{self.srv.getsockname()[1]}", "--bind", "127.0.0.1",
                           "--hb", "0.2", "--speed", "0.2", "--boot-sec", "0.3", "--events", self.events, "--quiet"] + self.extra)
        self.b = fk.Bridge(7, self.bport, o, "02:fa:ce:00:00:07", "07")
        self.b.start()
        self.got = []
        self.seq = 100

    def tearDown(self):
        self.b.close()
        self.srv.close()
        self.http.shutdown()
        self.http.server_close()
        os.unlink(self.events)

    # ── 도우미 ──
    def send(self, t, pl=b"", agv=7):
        self.seq += 1
        self.srv.sendto(fk.build_pkt(t, agv, self.seq, pl), ("127.0.0.1", self.bport))
        return self.seq

    def pump(self, until, timeout=10.0):
        """조건을 만족할 때까지 받기. 받은 것은 (type, seq, payload, json|None) 로 self.got 에 쌓인다"""
        t0 = time.monotonic()
        while time.monotonic() - t0 < timeout:
            try:
                b, _ = self.srv.recvfrom(2048)
            except socket.timeout:
                b = None
            if b:
                p = fk.parse_pkt(b)
                self.assertIsNotNone(p, "형식 오류 패킷")
                t, agv, _f, seq, pl = p
                self.assertEqual(agv, 7)
                try:
                    js = json.loads(pl)
                except ValueError:
                    js = None
                self.got.append((t, seq, pl, js))
            r = until()
            if r:
                return r
        self.fail("시간 초과: " + json.dumps([(hex(t), j) for t, _, _, j in self.got[-15:]], ensure_ascii=False))

    def dsp(self, job=None):
        return [j for t, _, _, j in self.got if t == fk.T_DSP_RESULT and (job is None or j["job"] == job)]

    def ota(self):
        return [j for t, _, _, j in self.got if t == fk.T_OTA_RESULT]

    def hb(self):
        return [j for t, _, _, j in self.got if t == fk.T_STATUS]

    def fresh_hb(self, cond, timeout=5.0):
        n = len(self.hb())
        return self.pump(lambda: next((h for h in self.hb()[n:] if cond(h)), None), timeout)

    def finals(self, job, stage):
        return [d for d in self.dsp(job) if d["stage"] == stage]

    def ev(self, kind):
        with open(self.events, encoding="utf-8") as f:
            return [e for e in map(json.loads, f) if e["ev"] == kind]

    def serve(self, path, data):
        Files.files[path] = data
        return f"http://127.0.0.1:{self.hport}{path}"

    @property
    def pkg_on(self):
        """이 시험의 가짜 브리지가 패키지 보관(0.9.0)을 아는가"""
        return "--legacy-pkg" not in self.extra and "--legacy" not in self.extra

    def make_pkg(self, ver="1.1.0", n=3000):
        """가짜 패키지: 브리지는 바이트를 풀지 않으므로 아무 내용이나 된다. (바이트, 패키지 ID)"""
        data = (b"DSPPKG-" + ver.encode() + b"-") * (n // 10 + 1)
        data = data[:n]
        return data, hashlib.sha256(data).hexdigest()[:16]

    def start_json(self, url, img, job, apply=0, pkg=True, pkgpath=None, **over):
        """DSP_OTA_START JSON. pkg=True 이고 이 브리지가 패키지 보관을 알면 pkg_* 를 채운다 (over 로 필드를 덮어씀/None 이면 뺌)"""
        d = {"url": url, "ver": "x", "crc32": struct.unpack_from("<I", img, 12)[0], "size": len(img), "job": job, "apply": apply}
        if pkg and self.pkg_on:
            data, pid = self.make_pkg(os.path.basename(url))
            purl = self.serve(pkgpath or url.replace(f"http://127.0.0.1:{self.hport}", "", 1).rsplit(".", 1)[0] + ".dsppkg", data)
            d.update({"pkg_url": purl, "pkg_size": len(data), "pkg_sha256": hashlib.sha256(data).hexdigest(), "pkg_id": pid})
        d.update(over)
        return json.dumps({k: v for k, v in d.items() if v is not None})

    def pkg_id_of(self, ver):
        return self.make_pkg(f"{ver}.dspfw")[1]

    def stage(self, ver="1.1.0", job=77):
        img = fk.build_dspfw(fk.test_code(700, 5), ver)
        url = self.serve(f"/fw/{ver}.dspfw", img)
        self.send(fk.T_DSP_START, self.start_json(url, img, job).encode())
        self.pump(lambda: len(self.finals(job, "staged")) >= 3)
        return img, url


class Basics(FakeBridgeTest):
    def test_heartbeat_fields_and_info(self):
        h = self.fresh_hb(lambda h: h["dsp"] == "1.0.0")
        self.assertEqual(list(h.keys()), HB_KEYS)
        self.assertEqual((h["fw"], h["mac"], h["part"], h["ota"], h["cfg"], h["dsp_st"], h["staged"]),
                         ("0.5.2", "02:fa:ce:00:00:07", "ota_0", "valid", "saved", "idle", None))
        self.assertEqual((h["pkg_st"], h["dsp_pkg"]), ("ok", None))
        seq = self.send(fk.T_PING, b"12345678")
        self.pump(lambda: any(t == fk.T_PONG and s == seq and pl == b"12345678" for t, s, pl, _ in self.got))
        self.send(fk.T_INFO_REQ)
        info = self.pump(lambda: next((j for t, _, _, j in self.got if t == fk.T_INFO), None))
        self.assertEqual((info["agv"], info["mac"]), (7, "02:fa:ce:00:00:07"))
        # 타입별 seq 는 0 부터 1 씩
        seqs = [s for t, s, _, _ in self.got if t == fk.T_STATUS]
        self.assertEqual(seqs, list(range(seqs[0], seqs[0] + len(seqs))))

    def test_image_rules(self):
        code = fk.test_code(700, 9)
        good = fk.build_dspfw(code, "1.2.3")
        self.assertIsNone(fk.stage_check(good)[0])
        self.assertEqual(fk.stage_check(fk.build_dspfw(code, "1.2.3.4.5"))[0], "ver_invalid")
        self.assertEqual(fk.stage_check(fk.build_dspfw(code, "1.2.3", file_ver="1.2.4"))[0], "ver_invalid")
        body = bytearray(good[32:])
        body[-1] ^= 0xFF                                    # 블록 데이터 → 복원 CRC 불일치 (파일 CRC 는 다시 맞춤)
        self.assertEqual(fk.stage_check(fk.wrap_dspfw(bytes(body), "1.2.3"))[0], "image_bad")
        bad = bytearray(good)
        bad[-1] ^= 0xFF                                     # 파일 CRC 불일치
        self.assertEqual(fk.stage_check(bytes(bad))[0], "image_crc")
        self.assertTrue(fk.url_host_is("http://127.0.0.1:8080/fw/a", "127.0.0.1"))
        for u in ("http://localhost/a", "http://127.0.0.01/a", "http://a@127.0.0.1/", "ftp://127.0.0.1/", "http://127.0.0.1:0/"):
            self.assertFalse(fk.url_host_is(u, "127.0.0.1"), u)
        self.assertEqual(fk.json_str('{"url":"' + "a" * 200 + '"}', "url", 200)[0], fk.JSON_TOO_LONG)
        self.assertEqual(fk.json_u32('{"job":4294967295,"x":1}', "job"), 4294967295)

    def test_stage_duplicate_apply_replay(self):
        self.fresh_hb(lambda h: h["dsp"] == "1.0.0")
        img, url = self.stage("1.1.0", 77)
        pid = self.pkg_id_of("1.1.0")
        # 패키지(분모에 포함)를 먼저, 이어서 .dspfw — download 보고는 합계 바이트 기준이라 두 번(66%, 100%)
        self.assertEqual([d["stage"] for d in self.dsp(77)], ["download", "download", "staged", "staged", "staged"])
        pcts = [d["pct"] for d in self.finals(77, "download")]
        self.assertTrue(pcts == sorted(pcts) and pcts[-1] == 100 and 0 < pcts[0] < 100, pcts)
        self.assertEqual(self.finals(77, "staged")[0], {"job": 77, "stage": "staged", "pct": 100, "ver": "1.1.0"})
        self.fresh_hb(lambda h: h["staged"] == {"job": 77, "ver": "1.1.0", "pkg": pid} and h["dsp_st"] == "staged")
        self.assertEqual([e["ev"] for e in map(json.loads, open(self.events, encoding="utf-8")) if e["ev"] in ("pkg_download", "download")],
                         ["pkg_download", "download"])           # 패키지가 먼저
        # 같은 job START 재전송 → 다시 받지 않고 staged 재보고
        self.send(fk.T_DSP_START, self.start_json(url, img, 77).encode())
        self.pump(lambda: len(self.finals(77, "staged")) >= 6)
        self.assertEqual((len(self.ev("download")), len(self.ev("pkg_download"))), (1, 1))
        # 다른 job 의 APPLY → job_mismatch (스테이징 버전 실림)
        self.send(fk.T_DSP_APPLY, b'{"job":78}')
        self.pump(lambda: len(self.finals(78, "fail")) >= 3)
        self.assertEqual(self.finals(78, "fail")[0], {"job": 78, "stage": "fail", "ver": "1.1.0", "err": "job_mismatch"})
        # 적용
        self.send(fk.T_DSP_APPLY, b'{"job":77}')
        self.pump(lambda: len(self.finals(77, "done")) >= 3, 20)
        stages = [d["stage"] for d in self.dsp(77) if d["stage"] not in ("download", "staged")]
        self.assertEqual(stages[:3], ["start", "enter", "erase"])
        self.assertEqual(stages[-4:], ["verify", "done", "done", "done"])
        self.assertTrue(all(s == "progress" for s in stages[3:-4]) and stages.count("progress") >= 1)
        self.assertEqual(self.finals(77, "done")[0], {"job": 77, "stage": "done", "pct": 100, "ver": "1.1.0"})
        h = self.fresh_hb(lambda h: h["dsp"] == "1.1.0" and h["dsp_st"] == "idle" and h["staged"] is None)
        self.assertEqual(h["dsp_pkg"], {"id": pid, "ver": "1.1.0"})      # 적용 뒤 보관 패키지
        self.assertEqual(self.ev("pkg_applied")[0]["id"], pid)
        # 끝난 job 의 APPLY 재전송 → 다시 굽지 않고 done 재보고
        self.send(fk.T_DSP_APPLY, b'{"job":77}')
        self.pump(lambda: len(self.finals(77, "done")) >= 6)
        self.assertEqual(len(self.ev("erase")), 1)

    def test_rejections(self):
        self.send(fk.T_DSP_APPLY, b'{"job":5}')
        self.pump(lambda: len(self.finals(0, "fail")) >= 3)
        self.assertEqual(self.dsp(0)[0], {"job": 0, "stage": "start", "pct": 0, "ver": ""})
        self.assertEqual(self.finals(0, "fail")[0], {"job": 0, "stage": "fail", "ver": "", "err": "not_staged"})
        self.send(fk.T_DSP_START, b'{"url":"http://10.0.0.1/a.dspfw","job":6}')
        self.pump(lambda: len(self.finals(6, "fail")) >= 3)
        self.assertEqual(self.finals(6, "fail")[0]["err"], "url_host")
        self.send(fk.T_DSP_START, json.dumps({"url": "http://127.0.0.1/" + "a" * 190, "job": 8}).encode())
        self.pump(lambda: len(self.finals(8, "fail")) >= 3)
        self.assertEqual(self.finals(8, "fail")[0]["err"], "url_too_long")
        # 리다이렉트로 다른 호스트
        redir_img = fk.build_dspfw(fk.test_code(300, 2), "1.2.3")
        self.send(fk.T_DSP_START, self.start_json(f"http://127.0.0.1:{self.hport}/redir/to/10.0.0.1/x", redir_img, 9, pkgpath="/fw/redir.dsppkg").encode())
        self.pump(lambda: len(self.finals(9, "fail")) >= 3)
        self.assertEqual(self.finals(9, "fail")[0]["err"], "url_host")
        # 9자 버전 이미지 → ver_invalid
        img = fk.build_dspfw(fk.test_code(300, 2), "1.2.3.4.5")
        self.send(fk.T_DSP_START, self.start_json(self.serve("/v9.dspfw", img), img, 10).encode())
        self.pump(lambda: len(self.finals(10, "fail")) >= 3)
        self.assertEqual(self.finals(10, "fail")[0]["err"], "ver_invalid")
        # 다른 호기 번호의 명령은 버림
        self.send(fk.T_DSP_APPLY, b'{"job":11}', agv=8)
        time.sleep(0.5)
        self.pump(lambda: True)
        self.assertEqual(self.dsp(11), [])

    def test_bridge_ota_reboot_confirm(self):
        h0 = self.fresh_hb(lambda h: True)
        url = self.serve("/agv_wifi_bridge.bin", fk.build_esp_app("0.5.3"))
        self.send(fk.T_OTA_START, url.encode())
        self.pump(lambda: sum(1 for o in self.ota() if o["stage"] == "done") >= 3, 10)
        self.assertEqual(self.ota()[0], {"stage": "start", "url": url})
        self.assertIn({"stage": "progress", "pct": 100, "bytes": 512}, self.ota())
        self.assertEqual([o for o in self.ota() if o["stage"] == "done"][0], {"stage": "done", "ver": "0.5.3", "bytes": 512})
        h = self.fresh_hb(lambda h: h["fw"] == "0.5.3", 8)
        self.assertEqual((h["part"], h["ota"]), ("ota_1", "pending"))
        self.assertLess(h["up"], h0["up"])
        self.send(fk.T_PING, b"x")                        # 서버 하향 → 확정
        self.fresh_hb(lambda h: h["ota"] == "valid" and h["fw"] == "0.5.3")
        # 진행 중 두 번째 OTA_START → busy, 다른 호스트 → url_host
        self.send(fk.T_OTA_START, b"http://10.0.0.1/a.bin")
        self.pump(lambda: any(o.get("err") == "url_host" for o in self.ota()))

    def test_cfg_trial_confirm(self):
        self.fresh_hb(lambda h: h["cfg"] == "saved")
        s1 = self.send(fk.T_CFG_CMD, b"set hb_ms 300")
        s2 = self.send(fk.T_CFG_CMD, b"save")
        s3 = self.send(fk.T_CFG_CMD, b"reboot")
        res = self.pump(lambda: (lambda r: r if len(r) >= 3 else None)([(s, pl.decode()) for t, s, pl, _ in self.got if t == fk.T_CFG_RESULT]))
        self.assertEqual([s for s, _ in res], [s1, s2, s3])
        self.assertTrue(res[0][1].startswith("ok hb_ms") and res[1][1].startswith("saved (trial)") and res[2][1] == "rebooting\n")
        self.fresh_hb(lambda h: h["cfg"] == "trial" and h["up"] < 5, 8)
        self.send(fk.T_PING, b"x")
        self.fresh_hb(lambda h: h["cfg"] == "saved")


class HungApp(FakeBridgeTest):
    """앱이 멈춤(버전·U 무응답, UART 프레임 없음) — 복구 적용 대상"""
    extra = ["--nodsp", "mute", "--app-frames", "no", "--power-cycle-sec", "0.5"]

    def test_recover_wait_power(self):
        h = self.fresh_hb(lambda h: True)
        self.assertEqual(h["dsp"], "?")
        self.stage("2.0.0", 90)
        self.send(fk.T_DSP_APPLY, b'{"job":90}')            # recover 없음: 앱 버전 '?' 라 원시 모드 HELLO 3회만 → 실패 (wait_power 없음)
        self.pump(lambda: len(self.finals(90, "fail")) >= 3, 15)
        self.assertEqual(self.finals(90, "fail")[0]["err"], "no_bootloader")
        self.assertFalse(self.finals(90, "wait_power"))
        self.assertEqual(len(self.ev("raw_mode")), 1)
        # 같은 job 을 다시 APPLY 하면 10분 동안은 최근 결과(fail)를 재보고할 뿐 다시 굽지 않는다 (실제 브리지와 같음)
        self.send(fk.T_DSP_APPLY, b'{"job":90,"recover":1}')
        self.pump(lambda: len(self.finals(90, "fail")) >= 6, 10)
        self.assertEqual(len(self.ev("apply")), 1)
        # 새 job 으로 다시 스테이징 → recover:1 적용 → wait_power → 전원 재투입 → done
        self.stage("2.0.0", 91)
        self.send(fk.T_DSP_APPLY, b'{"job":91,"recover":1}')
        self.pump(lambda: len(self.finals(91, "done")) >= 3, 30)
        self.assertTrue(self.finals(91, "wait_power"))
        self.fresh_hb(lambda h: h["dsp"] == "2.0.0")


class OldApp(FakeBridgeTest):
    """살아 있는 구 앱(버전·U 무응답이지만 UART 프레임은 보냄) — recover 여도 원시 모드 없이 app_no_fwup"""
    extra = ["--nodsp", "mute", "--power-cycle-sec", "0.5"]

    def test_app_no_fwup_without_raw_mode(self):
        self.assertEqual(self.fresh_hb(lambda h: True)["dsp"], "?")
        self.stage("2.0.0", 80)
        self.send(fk.T_DSP_APPLY, b'{"job":80}')
        self.pump(lambda: len(self.finals(80, "fail")) >= 3, 15)
        self.assertEqual(self.finals(80, "fail")[0]["err"], "app_no_fwup")
        self.stage("2.0.0", 81)
        self.send(fk.T_DSP_APPLY, b'{"job":81,"recover":1}')
        self.pump(lambda: len(self.finals(81, "fail")) >= 3, 15)
        self.assertEqual(self.finals(81, "fail")[0]["err"], "app_no_fwup")
        self.assertFalse(self.ev("raw_mode"))
        self.assertFalse(self.ev("power_cycle"))
        self.assertFalse(self.finals(81, "wait_power"))
        self.fresh_hb(lambda h: h["dsp_st"] == "staged" and h["staged"]["job"] == 81)


class SilentAppKnownVersion(FakeBridgeTest):
    """버전은 알지만 U 에 침묵하는 앱 — recover 없이 app_no_reply, heartbeat dsp 는 '?' 로 (원시 모드 없음)"""
    extra = ["--app-u", "noreply", "--app-frames", "no"]

    def test_app_no_reply_clears_version(self):
        self.fresh_hb(lambda h: h["dsp"] == "1.0.0")
        self.stage("2.0.0", 70)
        self.send(fk.T_DSP_APPLY, b'{"job":70}')
        self.pump(lambda: len(self.finals(70, "fail")) >= 3, 15)
        self.assertEqual(self.finals(70, "fail")[0]["err"], "app_no_reply")
        self.assertFalse(self.ev("raw_mode"))
        self.fresh_hb(lambda h: h["dsp"] == "?" and h["dsp_build"] == "?")


class EraseFail(FakeBridgeTest):
    extra = ["--fail-at", "erase"]

    def test_retry_then_fail_leaves_unknown_version(self):
        self.fresh_hb(lambda h: h["dsp"] == "1.0.0")
        self.stage("1.5.0", 50)
        self.send(fk.T_DSP_APPLY, b'{"job":50}')
        self.pump(lambda: len(self.finals(50, "fail")) >= 3, 20)
        self.assertEqual([d["pct"] for d in self.finals(50, "retry")], [1, 2])
        self.assertEqual(self.finals(50, "fail")[0], {"job": 50, "stage": "fail", "ver": "1.5.0", "err": "erase"})
        self.fresh_hb(lambda h: h["dsp"] == "?" and h["dsp_st"] == "staged" and h["staged"] == {"job": 50, "ver": "1.5.0", "pkg": self.pkg_id_of("1.5.0")})


class PackageArchive(FakeBridgeTest):
    """브리지 0.9.0 흉내(기본): 패키지 보관 — START 의 pkg_*, 먼저 받아 검증, 적용 전 짝 패키지 확인, 상태 보고"""

    def cfg(self, cmd, timeout=8.0):
        seq = self.send(fk.T_CFG_CMD, cmd.encode())
        return self.pump(lambda: next((pl.decode() for t, s, pl, _ in self.got if t == fk.T_CFG_RESULT and s == seq), None), timeout)

    def fail_of(self, job, err, n=3):
        self.pump(lambda: len(self.finals(job, "fail")) >= n)
        self.assertEqual(self.finals(job, "fail")[0]["err"], err)
        self.assertEqual(self.finals(job, "fail")[0]["stage"], "fail")

    def start(self, job, ver="1.1.0", code_seed=5, **over):
        img = fk.build_dspfw(fk.test_code(700, code_seed), ver)
        url = self.serve(f"/fw/{ver}.dspfw", img)
        self.send(fk.T_DSP_START, self.start_json(url, img, job, **over).encode())
        return img, url

    def test_start_without_package_fields_is_refused_and_touches_nothing(self):
        self.fresh_hb(lambda h: h["dsp"] == "1.0.0")
        self.stage("1.1.0", 10)                                                  # 먼저 정상 스테이징
        for i, drop in enumerate(("pkg_url", "pkg_size", "pkg_sha256", "pkg_id")):
            self.start(20 + i, **{drop: None})
            self.fail_of(20 + i, "no_pkg")
        self.start(30, pkg=False)                                                 # 0.6.x 게이트웨이: pkg_* 를 아예 안 보냄
        self.fail_of(30, "no_pkg")
        # 형식이 틀린 값: 대문자 sha, 짧은 ID, 크기 0
        for i, bad in enumerate(({"pkg_sha256": "A" * 64}, {"pkg_sha256": "a" * 63}, {"pkg_id": "abc"}, {"pkg_id": "G" * 16}, {"pkg_size": 0})):
            self.start(40 + i, **bad)
            self.fail_of(40 + i, "no_pkg")
        # 받지도 않았다: 기존 스테이징·메인보드 그대로, 다운로드는 처음 한 번뿐
        self.assertEqual((len(self.ev("download")), len(self.ev("pkg_download"))), (1, 1))
        h = self.fresh_hb(lambda h: True)
        self.assertEqual((h["dsp"], h["dsp_st"], h["staged"]["job"], h["staged"]["pkg"]), ("1.0.0", "staged", 10, self.pkg_id_of("1.1.0")))

    def test_package_url_rules(self):
        self.start(50, pkg_url="http://10.0.0.1/a.dsppkg")
        self.fail_of(50, "pkg_url_host")
        self.start(51, pkg_url="http://127.0.0.1/" + "a" * 190)
        self.fail_of(51, "pkg_url_too_long")
        self.start(52, pkg_url=f"http://127.0.0.1:{self.hport}/redir/to/10.0.0.1/x")        # 리다이렉트 호스트
        self.pump(lambda: len(self.finals(52, "fail")) >= 3)
        self.assertEqual(self.finals(52, "fail")[0]["err"], "pkg_url_host")
        self.assertEqual(self.ev("download"), [])

    def test_package_download_failures_name_the_package_step(self):
        img, _ = self.start(60, pkg_size=3001)                                    # 길이 불일치
        self.fail_of(60, "pkg_size")
        self.start(61, pkg_sha256="0" * 64)                                        # 해시 불일치 (크기는 맞음)
        self.fail_of(61, "pkg_sha")
        self.start(62, pkg_url=f"http://127.0.0.1:{self.hport}/fw/none.dsppkg")   # 404
        self.fail_of(62, "pkg_http")
        self.start(63, pkg_size=fk.PKG_MAX + 1)                                    # 보관 한도 초과 — 받지 않고 거부
        self.fail_of(63, "pkg_too_big")
        self.assertEqual(self.ev("download"), [])                                  # .dspfw 는 한 번도 받지 않았다
        h = self.fresh_hb(lambda h: True)
        self.assertEqual((h["dsp_st"], h["staged"]), ("idle", None))

    def test_dspfw_failure_after_package_leaves_no_paired_package(self):
        self.fresh_hb(lambda h: h["dsp"] == "1.0.0")
        self.stage("1.1.0", 70)
        self.start(71, ver="1.2.0", size=999)                                      # 패키지는 받지만 .dspfw 길이 불일치(http_length) — 지우기 전에 실패
        self.fail_of(71, "http_length")
        self.assertEqual(len(self.ev("pkg_download")), 2)
        # 이전 스테이징(.dspfw)은 그대로지만 짝 패키지 슬롯은 덮어써져 사라졌다 → 적용은 메인보드를 지우기 전에 no_pkg
        h = self.fresh_hb(lambda h: h["dsp_st"] == "staged" and h["staged"] is not None and h["staged"]["job"] == 70)
        self.assertNotIn("pkg", h["staged"])
        self.send(fk.T_DSP_APPLY, b'{"job":70}')
        self.pump(lambda: len(self.finals(70, "fail")) >= 3)
        self.assertEqual(self.finals(70, "fail")[0]["err"], "no_pkg")
        self.assertEqual(self.ev("erase"), [])
        h = self.fresh_hb(lambda h: h["dsp_st"] == "staged")
        self.assertEqual((h["dsp"], h["dsp_pkg"]), ("1.0.0", None))

    def test_apply_replaces_the_archived_package_and_console_shows_it(self):
        self.stage("1.1.0", 80)
        self.send(fk.T_DSP_APPLY, b'{"job":80}')
        self.pump(lambda: len(self.finals(80, "done")) >= 3, 20)
        a = self.pkg_id_of("1.1.0")
        h = self.fresh_hb(lambda h: h["dsp"] == "1.1.0" and h["dsp_st"] == "idle")
        self.assertEqual((h["dsp_pkg"], h["staged"]), ({"id": a, "ver": "1.1.0"}, None))
        self.assertIn(f"pkg={a}(1.1.0) staged_pkg=-", self.cfg("dsp"))
        # 다음 버전을 스테이징하면 staged.pkg 가 새 ID, 보관 패키지(dsp_pkg)는 아직 이전 것
        self.stage("1.2.0", 81)
        b = self.pkg_id_of("1.2.0")
        h = self.fresh_hb(lambda h: h["staged"] is not None)
        self.assertEqual((h["staged"]["pkg"], h["dsp_pkg"]["id"]), (b, a))
        self.assertIn(f"pkg={a}(1.1.0) staged_pkg={b}", self.cfg("dsp"))
        text = self.cfg("dsp pkg")
        self.assertIn(f"slot0: APPLIED id={a} ver=1.1.0", text)
        self.assertIn(f"slot1: STAGED id={b} ver=1.2.0", text)
        self.send(fk.T_DSP_APPLY, b'{"job":81}')
        self.pump(lambda: len(self.finals(81, "done")) >= 3, 20)
        h = self.fresh_hb(lambda h: h["dsp"] == "1.2.0" and h["dsp_st"] == "idle")
        self.assertEqual(h["dsp_pkg"], {"id": b, "ver": "1.2.0"})
        text = self.cfg("dsp pkg")
        self.assertIn(f"slot1: APPLIED id={b}", text)
        self.assertIn("slot0: empty", text)                                         # 이전 것은 지난 것(OLD) — 표시는 비움
        self.assertIn("dsp [ver|apply|pkg]", self.cfg("help"))
        self.send(fk.T_INFO_REQ)
        info = self.pump(lambda: next((j for t, _, _, j in reversed(self.got) if t == fk.T_INFO), None))
        self.assertEqual((info["pkg_st"], info["dsp_pkg"]), ("ok", {"id": b, "ver": "1.2.0"}))

    def test_reboot_keeps_the_archive_and_staged_pair(self):
        self.stage("1.1.0", 90)
        self.send(fk.T_DSP_APPLY, b'{"job":90}')
        self.pump(lambda: len(self.finals(90, "done")) >= 3, 20)
        self.stage("1.3.0", 91)
        self.cfg("reboot")
        h = self.fresh_hb(lambda h: h["up"] < 5 and h["staged"] is not None, 10)
        self.assertEqual((h["staged"]["pkg"], h["dsp_pkg"]["id"]), (self.pkg_id_of("1.3.0"), self.pkg_id_of("1.1.0")))


class NoPackagePartition(FakeBridgeTest):
    """패키지 파티션이 없는 브리지 (이전 파티션 표인 채 OTA 로만 올라옴) — pkg_st=nopart, 스테이징 거부"""
    extra = ["--nopkgpart"]

    def test_start_is_refused_with_pkg_nopart(self):
        h = self.fresh_hb(lambda h: True)
        self.assertEqual((h["pkg_st"], h["dsp_pkg"], h["dsp_st"]), ("nopart", None, "idle"))      # dspfw 파티션은 있음
        img = fk.build_dspfw(fk.test_code(700, 5), "1.1.0")
        self.send(fk.T_DSP_START, self.start_json(self.serve("/fw/n.dspfw", img), img, 5).encode())
        self.pump(lambda: len(self.finals(5, "fail")) >= 3)
        self.assertEqual(self.finals(5, "fail")[0], {"job": 5, "stage": "fail", "err": "pkg_nopart"})
        self.assertEqual((self.ev("pkg_download"), self.ev("download")), ([], []))
        self.assertIn("no dsppkg partition", self.cfg("dsp pkg"))

    def cfg(self, cmd):
        seq = self.send(fk.T_CFG_CMD, cmd.encode())
        return self.pump(lambda: next((pl.decode() for t, s, pl, _ in self.got if t == fk.T_CFG_RESULT and s == seq), None), 8)


class LegacyPkgBridge(FakeBridgeTest):
    """패키지 보관을 모르는 브리지 0.8.0 (--legacy-pkg) — pkg_* 를 무시하고 예전처럼 동작, 새 필드 없음"""
    extra = ["--legacy-pkg"]

    def test_ignores_package_fields_and_has_no_new_status_fields(self):
        h = self.fresh_hb(lambda h: h["dsp"] == "1.0.0")
        self.assertEqual(list(h.keys()), HB_KEYS_080)
        img = fk.build_dspfw(fk.test_code(700, 5), "1.1.0")
        url = self.serve("/fw/old.dspfw", img)
        self.send(fk.T_DSP_START, self.start_json(url, img, 5, pkg_url="http://10.0.0.1/ignored", pkg_id="zz").encode())   # 엉터리 pkg_* 도 무시
        self.pump(lambda: len(self.finals(5, "staged")) >= 3)
        self.assertEqual([d["stage"] for d in self.dsp(5)], ["download", "staged", "staged", "staged"])
        self.assertEqual(self.ev("pkg_download"), [])
        self.fresh_hb(lambda h: h["staged"] == {"job": 5, "ver": "1.1.0"})
        self.send(fk.T_DSP_APPLY, b'{"job":5}')
        self.pump(lambda: len(self.finals(5, "done")) >= 3, 20)                      # 짝 패키지 없이도 적용
        h = self.fresh_hb(lambda h: h["dsp"] == "1.1.0" and h["dsp_st"] == "idle")
        self.assertNotIn("dsp_pkg", h)
        self.send(fk.T_INFO_REQ)
        info = self.pump(lambda: next((j for t, _, _, j in reversed(self.got) if t == fk.T_INFO), None))
        self.assertNotIn("pkg_st", info)


def now_ms():
    return int(time.time() * 1000)


class ModernBridge(FakeBridgeTest):
    """브리지 0.8.0 흉내: cfg json · stat json · discard · scan · nettest · 시각 동기"""
    extra = ["--fw", "0.8.0", "--clock-first-req", "0.3"]

    def cfg(self, cmd, timeout=8.0):
        """CFG_CMD 를 보내고 같은 seq 의 CFG_RESULT 본문을 돌려준다"""
        seq = self.send(fk.T_CFG_CMD, cmd.encode())
        return self.pump(lambda: next((pl.decode() for t, s, pl, _ in self.got if t == fk.T_CFG_RESULT and s == seq), None), timeout)

    def test_cfg_json_is_one_line_with_end_marker_and_tracks_edits(self):
        text = self.cfg("cfg json")
        self.assertTrue(text.endswith("\n>\n"), text)
        js = json.loads(text[:-3])
        self.assertEqual((js["fw"], js["state"], js["differs"], js["cfg"]["agv"], js["cfg"]["hb_ms"]), ("0.8.0", "saved", False, 7, 200))   # 시험은 --hb 0.2
        self.assertEqual(js["name"], "AGVBR-07-0007")
        self.assertIn("pass", js["keys"])
        self.assertTrue(self.cfg("set hb_ms 300").startswith("ok hb_ms"))
        self.assertTrue(self.cfg("set dns 192.168.10.1").startswith("ok dns"))
        js = json.loads(self.cfg("cfg json")[:-3])
        self.assertEqual((js["differs"], js["cfg"]["hb_ms"], js["cfg"]["dns"]), (True, 300, "192.168.10.1"))
        self.assertEqual(self.cfg("discard"), "discarded - edit copy now equals running config\n>\n")
        self.assertFalse(json.loads(self.cfg("cfg json")[:-3])["differs"])

    def test_password_is_reported_by_length_only(self):
        self.assertTrue(self.cfg("set pass secret-pass").startswith("ok pass"))
        text = self.cfg("cfg json")
        self.assertNotIn("secret-pass", text)
        self.assertEqual(json.loads(text[:-3])["cfg"]["pass_len"], 11)
        self.assertTrue(self.cfg("set pass short").startswith("invalid key or value"))

    def test_stat_json_scan_and_nettest(self):
        st = json.loads(self.cfg("stat json")[:-3])
        self.assertEqual((st["fw"], st["wifi"]["up"], st["setup"]["name"]), ("0.8.0", True, "AGVBR-07-0007"))
        self.assertIn("uart", st)
        self.assertIn("time", st)
        aps = json.loads(self.cfg("scan")[:-3])
        self.assertEqual(aps[0], {"ssid": "AGV_WIFI", "rssi": -48, "ch": 6, "auth": "wpa2"})
        self.assertIn("3/3 ok", self.cfg("nettest"))
        self.assertIn("dns: gateway.local ->", self.cfg("nettest gateway.local"))

    def test_clock_request_reply_sets_time_and_heartbeat_reports_it(self):
        # 시각을 맞추기 전: tsync -1, ts 없음
        h = self.fresh_hb(lambda h: "tsync" in h)
        self.assertEqual(h["tsync"], -1)
        self.assertNotIn("ts", h)
        # 브리지가 먼저 TIME_REQ 를 보낸다 (부팅 0.3초 뒤)
        req = self.pump(lambda: next((js for t, _, _, js in self.got if t == fk.T_TIME_REQ), None), 5)
        self.assertIn("t0", req)
        t1 = now_ms()
        self.send(fk.T_TIME, json.dumps({"t0": req["t0"], "t1": t1, "t2": t1 + 1}).encode())
        h = self.fresh_hb(lambda h: h.get("tsync", -1) >= 0)
        self.assertLess(abs(h["ts"] - now_ms()), 1500)
        ev = [e for e in self.ev("time_sync")]
        self.assertEqual(ev[0]["kind"], "reply")

    def test_unmatched_reply_is_ignored_and_bad_time_is_refused(self):
        req = self.pump(lambda: next((js for t, _, _, js in self.got if t == fk.T_TIME_REQ), None), 5)
        t1 = now_ms()
        self.send(fk.T_TIME, json.dumps({"t0": req["t0"] + 999, "t1": t1, "t2": t1 + 1}).encode())   # 내 요청과 짝이 안 맞음
        self.send(fk.T_TIME, json.dumps({"t2": 1000}).encode())                                        # 1970 근처
        self.send(fk.T_TIME, b"garbage")
        time.sleep(0.6)
        self.assertEqual(self.b.clock_synced, False)
        self.assertEqual(self.ev("time_sync"), [])

    def test_unsolicited_time_from_server_sets_clock_without_a_request(self):
        self.send(fk.T_TIME, json.dumps({"t2": now_ms() + 3000}).encode())
        h = self.fresh_hb(lambda h: h.get("tsync", -1) >= 0)
        self.assertLess(abs(h["ts"] - (now_ms() + 3000)), 1500)
        self.assertEqual(self.ev("time_sync")[0]["kind"], "push")


class WrongClockBridge(FakeBridgeTest):
    """시각이 틀린 채 맞춘 것처럼 시작하고 서버에 묻지 않는 브리지 — 서버가 먼저 보내 주는 경로"""
    extra = ["--fw", "0.8.0", "--clock-wrong-ms", "5000", "--clock-no-req"]

    def test_heartbeat_shows_the_error_until_the_server_pushes(self):
        h = self.fresh_hb(lambda h: "ts" in h)
        self.assertGreater(h["ts"] - now_ms(), 4000)
        self.send(fk.T_TIME, json.dumps({"t2": now_ms()}).encode())
        h = self.fresh_hb(lambda h: abs(h.get("ts", 0) - now_ms()) < 1500)
        self.assertEqual(h["tsync"] >= 0, True)


class OlderBridges(FakeBridgeTest):
    """0.5.5 흉내: 새 기능이 없다 — 인자를 무시하고 텍스트를 내고, 새 명령은 모르는 명령"""
    extra = ["--fw", "0.5.5", "--clock-first-req", "0.2"]

    def cfg(self, cmd):
        seq = self.send(fk.T_CFG_CMD, cmd.encode())
        return self.pump(lambda: next((pl.decode() for t, s, pl, _ in self.got if t == fk.T_CFG_RESULT and s == seq), None), 8)

    def test_no_new_features(self):
        self.assertTrue(self.cfg("cfg json").startswith("agv=7 "))               # 인자 무시 → 텍스트, 끝 표시 줄 없음
        self.assertFalse(self.cfg("cfg json").endswith(">\n"))
        self.assertTrue(self.cfg("scan").startswith("unknown command 'scan'"))
        self.assertTrue(self.cfg("discard").startswith("unknown command 'discard'"))
        h = self.fresh_hb(lambda h: True)
        self.assertNotIn("tsync", h)
        time.sleep(0.6)
        self.assertFalse(any(t == fk.T_TIME_REQ for t, _, _, _ in self.got))      # 시각을 묻지 않는다


if __name__ == "__main__":
    unittest.main(verbosity=2)
