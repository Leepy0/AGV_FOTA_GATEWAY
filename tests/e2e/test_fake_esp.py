#!/usr/bin/env python3
"""가짜 브리지(tests/fake_esp.py) 자체 시험 — 서버 없이 UDP·HTTP 로 직접 붙어 브리지 0.5.2 와 같은 보고를 내는지 본다.

  python3 tests/e2e/test_fake_esp.py        (또는 python3 -m unittest discover -s tests/e2e -p 'test_*.py')
"""
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

HB_KEYS = ["fw", "mac", "part", "ota", "up", "rssi", "ch", "bssid", "disc", "reason", "m_rx", "m_tx", "r_rx", "u_err", "n_off",
           "n_err", "heap", "cfg", "setup", "dsp", "dspb", "dsp_build", "dsp_st", "staged"]


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

    def start_json(self, url, img, job, apply=0):
        return json.dumps({"url": url, "ver": "x", "crc32": struct.unpack_from("<I", img, 12)[0], "size": len(img), "job": job, "apply": apply})

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
        self.assertEqual([d["stage"] for d in self.dsp(77)], ["download", "staged", "staged", "staged"])
        self.assertEqual(self.finals(77, "staged")[0], {"job": 77, "stage": "staged", "pct": 100, "ver": "1.1.0"})
        self.fresh_hb(lambda h: h["staged"] == {"job": 77, "ver": "1.1.0"} and h["dsp_st"] == "staged")
        # 같은 job START 재전송 → 다시 받지 않고 staged 재보고
        self.send(fk.T_DSP_START, self.start_json(url, img, 77).encode())
        self.pump(lambda: len(self.finals(77, "staged")) >= 6)
        self.assertEqual(len(self.ev("download")), 1)
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
        self.fresh_hb(lambda h: h["dsp"] == "1.1.0" and h["dsp_st"] == "idle" and h["staged"] is None)
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
        self.send(fk.T_DSP_START, json.dumps({"url": f"http://127.0.0.1:{self.hport}/redir/to/10.0.0.1/x", "job": 9}).encode())
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
        self.assertTrue(res[0][1].startswith("ok hb_ms") and res[1][1].startswith("saved (trial)") and res[2][1] == "rebooting\n>\n")
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
        self.fresh_hb(lambda h: h["dsp"] == "?" and h["dsp_st"] == "staged" and h["staged"] == {"job": 50, "ver": "1.5.0"})


if __name__ == "__main__":
    unittest.main(verbosity=2)
