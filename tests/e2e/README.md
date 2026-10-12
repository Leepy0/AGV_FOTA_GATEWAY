# 종단(E2E) 시험

빌드한 게이트웨이 서버를 임시 데이터 폴더·임의 포트로 띄우고, 가짜 브리지(`tests/fake_esp.py`, 브리지 0.5.2 흉내)를
여러 대 붙여 REST API 로 배포 흐름을 끝까지 돌립니다. 파이썬 3.10 이상, 표준 라이브러리만 씁니다.

```bash
dotnet build -c Release
python3 tests/e2e/run_e2e.py              # 전체 12개, 약 3분
python3 tests/e2e/run_e2e.py --only 1,3   # 골라서
python3 tests/e2e/run_e2e.py --list       # 시나리오 목록
python3 tests/e2e/run_e2e.py --dll <다른 빌드의 AgvFotaGateway.dll> --keep   # 임시 폴더(서버·가짜 브리지 로그) 남기기
python3 tests/e2e/test_fake_esp.py        # 가짜 브리지 자체 시험 (서버 없이, 약 40초)
```

시나리오마다 `PASS`/`FAIL` 과 요약을 출력하고, 실패하면 작업 기록·서버 로그·가짜 브리지 로그 끝부분을 붙입니다.
종료 코드는 실패한 시나리오 수입니다. 서버는 `Gateway:OfflineAfterSec`(기본 5초)×2 동안 작업을 시작하지 않으므로
첫 시나리오는 10초쯤 기다렸다가 시작합니다.

| 번호 | 시나리오 |
|---|---|
| 1 | 메인보드 Approve: 3대(한 프로세스) 스테이징 → 적용 대기 → 승인 → 적용 → Done, job 번호가 스테이징·적용에서 같음, heartbeat 버전 |
| 2 | 메인보드 Immediate |
| 3 | staged 보고 3회 유실 / done 보고 3회 유실 → heartbeat 로 판정해 성공 |
| 4 | 상향 6.5초 단절로 서버 재전송 유도 → 같은 job 중복 START·APPLY → 다운로드·굽기 각 1회 |
| 5 | 브리지 자체 OTA → 재부팅 → 서버 자동 PING 으로 확정(valid), done 3회 유실 호기도 성공 |
| 6 | 같은 호기 번호 장치 둘(`--dup-id`) → 수동 전송 409, 작업 거부, endpoint 유지 |
| 7 | 마감 시각 지난 대상 건너뜀, 지난 예약 시각·잘못된 입력 400 |
| 8 | 버전 9자·블록 깨짐·헤더 버전 불일치 `.dspfw`, 다른 프로젝트 ESP 이미지 업로드 거부, 다른 출처 업로드 403 |
| 9 | 멈춘 앱(`--nodsp mute --app-frames no`, dsp=`?`) → '복구 적용' 작업(`recover:true`) → APPLY 에 `recover:1`, 원시 모드·wait_power 뒤 전원 재투입으로 성공 |
| 10 | 수동 전송 F/R 프레임 400, 다른 출처 Origin·cross-site POST 403 |
| 11 | 원격 설정 `save`·`reboot` → cfg=trial → 서버 자동 PING 으로 saved |
| 12 | 살아 있는 구 앱(`--nodsp mute`, dsp=`?`) 일반 적용 → 원시 모드 없이 `app_no_fwup` / 버전 아는 앱이 U 에 침묵(`--app-u noreply --app-frames no`) → `app_no_reply` 뒤 heartbeat dsp `?` — 둘 다 recover 없음, 실패 안내에 '복구 적용으로 다시 배포' |

가짜 브리지 단독 실행과 장애 주입 옵션(`--drop`, `--drop-rx`, `--mute-after-cmd`, `--fail-at`, `--app-u`, `--app-frames`, `--nodsp`,
`--nopart`, `--legacy`, `--dup-id`, `--events` …)은 `python3 tests/fake_esp.py --help` 와 파일 머리 주석에 있습니다.
실제 브리지와 다른 점도 그 주석의 '남은 차이'에 적어 두었습니다.
