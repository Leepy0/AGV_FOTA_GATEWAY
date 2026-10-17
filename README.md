# AGV FOTA Gateway

AGV 의 Wi-Fi 브리지(ESP32-S3)와 메인보드 DSP(TMS320F2808) 펌웨어를 **웹 화면에서 올리고, 대상 호기와 시각을 정해 순차 배포**하는 서버입니다.
메인보드 OTA 는 브리지의 B안(브리지가 `.dspfw` 를 받아 두었다가 상주 부트로더로 굽는 방식)을 씁니다. 브리지 0.5.0 이상에서 되고, 이번 릴리스(게이트웨이 v0.3.0)의 기준은 브리지 **v0.5.2** 입니다.
메인보드 작업은 기본으로 **전 호기 스테이징 → 적용 대기 → AGV 정지 확인 후 '적용 시작' → 한 대씩 적용** 순서로 흐릅니다.
ACS(교통제어) TCP 소켓 중계도 같은 프로세스에 들어갑니다 (브리지가 서버 IP 하나로만 통신하기 때문).

![전체 구성](docs/images/diagrams/architecture.png)

*브라우저는 HTTP 8080, 브리지는 UDP 50000 으로 서버와 통신하고, 브리지가 펌웨어 파일을 서버에서 HTTP 로 받아 갑니다.*

![호기 현황 화면](docs/images/screens/01-home.png)

| 문서 | 내용 |
|---|---|
| [docs/SYSTEM_FLOW.md](docs/SYSTEM_FLOW.md) | **전체 흐름** — 구성 요소, 통신, 번호 충돌, 브리지·메인보드 OTA 순서, 배포 작업(승인 흐름), 구역, 서버 업데이트, 데이터, 화면 안내, 0.3.0 에서 달라진 점 |
| [docs/DEPLOY.md](docs/DEPLOY.md) | **설치·운영 가이드** — 도입 순서, 서버 설치, 브리지 연결, 펌웨어·구역·배포, 설정, 백업, 업데이트, 문제 해결 |
| [docs/INTERFACES.md](docs/INTERFACES.md) | 접점 규격 출처, 브리지 UDP·JSON 필드와 오류 코드, **REST API** |
| [docs/ACS_API.md](docs/ACS_API.md) · [docs/ACS_MAP_API_SCHEMA.md](docs/ACS_MAP_API_SCHEMA.md) · [docs/RTLS.md](docs/RTLS.md) | ACS 연동 준비(위치 보고·소속 받기·TCP 중계 안전 규칙) · DevTools에서 확인한 ACS Map API 스키마 · 위치 추정 설계 검토(구현 전) |
| [docs/REVIEW_2026-10-10.md](docs/REVIEW_2026-10-10.md) | 전체 점검 결과(91건)와 결정 사항. 모두 v0.3.0 · 브리지 v0.5.2 에 반영 |
| [tests/e2e/README.md](tests/e2e/README.md) | 종단 시험 — 빌드한 서버 + 가짜 브리지로 배포 흐름 12개 시나리오 |
| 브리지 저장소 [FLASH_GUIDE.md](https://github.com/Leepy0/AGV_Wifi_Bridge/blob/main/docs/FLASH_GUIDE.md) · [dsp/README.md](https://github.com/Leepy0/AGV_Wifi_Bridge/blob/main/dsp/README.md) | 브리지 USB 굽기·호기 설정 · 메인보드 부트로더 첫 설치 |

**설치**: Releases 의 zip 을 풀고 `install-service.bat`(관리자) 한 번 → `http://<PC IP>:8080`. 방화벽 규칙과 설치 폴더 권한도 이 배치가 맞춥니다.
이후 버전은 서버가 스스로 받아(SHA-256 검증, GitHub https 출처만) 적용하고, 60초 안에 새 버전이 응답하지 않으면 이전 버전으로 자동 되돌립니다(**서버 업데이트** 화면).
사내망에서 api.github.com 이 막혀도 github.com 의 매니페스트로 확인합니다. v0.2.x 에서 v0.3.0 으로 처음 올릴 때의 주의는 [SYSTEM_FLOW.md 9절](docs/SYSTEM_FLOW.md#9-서버-자체-업데이트), 자세한 절차는 [docs/DEPLOY.md](docs/DEPLOY.md).

## 구성

| 경로 | 내용 |
|---|---|
| `src/Gateway.Core` | 브리지 UDP 패킷·heartbeat·번호 충돌 판정·seq 손실, AGV 프레임(BCC, ACS 화이트리스트, 하향 F·R 차단), `.dspfw` 파서(블록·`app_crc` 검사)·버전 규칙, 메인보드 OTA(B안)·브리지 OTA 클라이언트(재전송·heartbeat 판정), 업데이트 피드·교체 배치 |
| `src/Gateway.Server` | ASP.NET Core + Blazor Server (.NET 10). 브리지 UDP 서비스, 호기 레지스트리, ACS 중계, 펌웨어 저장소, 작업 실행기(스테이징·적용 대기·승인), 화면 9개(호기 현황·구역·펌웨어·배포 작업·호기 설정·Wi-Fi 분석·통신 로그·서버 업데이트·설정), 파일 로그, SQLite 데이터베이스(`data/gateway.db`, 스키마 v3 — 펌웨어 목록·작업·소속·최근 위치·마지막 접속), 서버 자체 업데이트, REST API |
| `tests/Gateway.Core.Tests` · `tests/Gateway.Server.Tests` | xUnit — 프로토콜 벡터, 패킷/프레임 왕복, `.dspfw`(브리지 기준 이미지 대조), 메인보드·브리지 OTA 단계·재전송·실패, 작업 실행기·DB 마이그레이션, API 검사 |
| `tests/fake_esp.py` | 가짜 브리지 — 브리지 0.5.2 흉내, 장애 주입 옵션 (`python3 tests/fake_esp.py --help`) |
| `tests/e2e/` | 종단 시험 `run_e2e.py`, 가짜 브리지 자체 시험 `test_fake_esp.py` ([README](tests/e2e/README.md)) |
| `deploy/` | `start.bat`, `install-service.bat`, `uninstall-service.bat` (CP949 저장) |
| `docs/` | 위 문서들, `diagrams/`(도식 원본 Mermaid + 렌더 도구), `images/`(도식 PNG·화면 캡처) |

## 개발

```bash
dotnet build && dotnet test
dotnet run --project src/Gateway.Server          # http://localhost:8080
```

하드웨어 없이 확인하려면 가짜 브리지를 이 서버에 붙입니다. 여러 호기를 한 프로세스로 띄울 수 있습니다(포트는 연속):

```bash
python3 tests/fake_esp.py --agv 7,12,23 --server 127.0.0.1:50000 --listen 50010 --dsp 1.0.0
```

`--dspb` 는 메인보드 부트로더 버전입니다(기본 `1.0.0` = 옛 부트로더, 0.5.2 부트로더처럼 보이려면 `--dspb 1.1.0`).

장애 주입: `--drop staged,done,ota_done[:N]`(보고 유실), `--drop-rx START:1`(명령 유실), `--mute-after-cmd 6.5`(서버 재전송 유도), `--fail-at erase[:N]`, `--app-u ng:03`, `--nodsp [mute|boot]`(앱 버전 `?`), `--app-frames yes|no`(앱이 살아 있음·침묵), `--nopart`, `--legacy`(0.4.x), `--dup-id`(번호 충돌), `--events FILE`.
실제 브리지와 다른 점은 파일 머리 주석의 '남은 차이' 에 있습니다.

종단 시험(빌드한 서버를 임시 폴더·임의 포트로 띄워 12개 시나리오, 약 3분, Python 3.10+ 표준 라이브러리만):

```bash
dotnet build -c Release
python3 tests/e2e/run_e2e.py              # --only 1,3 · --list · --keep
python3 tests/e2e/test_fake_esp.py        # 가짜 브리지 자체 시험 (서버 없이)
```

REST API (화면과 같은 기능, 스크립트용 — 자세한 본문·응답 코드는 [docs/INTERFACES.md](docs/INTERFACES.md#rest-api)):
`GET /api/agvs`, `GET|POST|DELETE /api/firmware`, `GET|POST /api/jobs` (`applyMode`·`notAfter`·`recover`), `POST /api/jobs/{id}/approve`, `POST /api/jobs/{id}/cancel[?queued=true]`, `DELETE /api/jobs/{id}` (끝난 작업만, 없는 작업 404·끝나지 않은 작업 409),
`POST /api/agvs/{n}/send` `{ "data": "<STX>…<ETX>" }`, `POST /api/agvs/{n}/cfg` `{ "data": "set ip …" }`, `GET /fw/{id}/{file}` (브리지가 받아 가는 파일),
`GET /api/positions[/{n}]` 호기 위치, `POST /api/positions/{n}` 시험 주입, `GET /api/acs/status` · `POST /api/acs/push` · `GET /api/acs/preview` ACS 위치 보고,
`POST /api/agvs/{n}/cfg/wait` · `POST /api/agvs/{n}/time` 원격 진단·시각 동기, `GET|POST /api/cfg…` 호기 설정·기준값·일괄 변경, `GET /api/wifi/{aps,spots,events}` Wi-Fi 분석, `GET|POST /api/bridge/release…` 브리지 새 버전,
`GET /api/groups` 구역 트리 · `GET /api/groups/agvs?path=` · `PUT|DELETE /api/agvs/{n}/group` · `POST /api/groups/import` (CSV) · `GET /api/groups/export.csv` · `POST /api/acs/inventory/preview|pull` ACS 소속 받기, `GET /api/health`.
다른 웹페이지에서 브라우저로 보낸 변경 요청은 403 이고, 오류는 상태 코드와 짧은 사유(JSON 문자열)로 돌려줍니다.
접속 주소(Host)가 IP·`localhost`·이 PC 이름이 아니면 화면·API 모두 403 입니다(DNS 리바인딩 방어). 사내 DNS 별칭으로 접속하면 `Gateway.AllowedHostNames` 에 그 이름을 넣습니다.

## 릴리스

- main push → `dev` 프리릴리스(테스트 빌드, 덮어쓰기). 자동 업데이트 대상 아님.
- 정식: csproj `<Version>` 을 올려 커밋한 뒤 `vX.Y.Z` 태그 푸시 또는 Actions `build` 를 `release=true` 로 실행 → Latest 릴리스에
  `AgvFotaGateway_win-x64.zip`, `.sha256`, `AgvFotaGateway.json`(매니페스트). 설치된 서버들이 github.com 의 매니페스트 주소로 이 릴리스를 보고 업데이트합니다.
- CI 는 빌드·단위 시험 뒤 종단 시험(`tests/e2e`)을 돌립니다. 정식 배포 실행은 뒤이은 main push 에 취소되지 않습니다.
- 절차는 `.claude/skills/fota-gateway-release/SKILL.md` (Claude 가 "릴리스 올려줘" 요청에 따르는 순서).

## 아직 정해지지 않은 것

- **ACS 소켓 규격**: 기존 ZigBee 게이트웨이가 ACS 와 주고받는 TCP 규격(접속 방향, 프레임 구분, 호기 매핑). 지금은 프레임이 그대로 흐른다고 가정한 뼈대이며 `Acs.Enabled=false`. ACS 로는 온전한 E·A 프레임만 넘기고, 하향 F·R 은 버립니다.
- **메인보드 OTA 실기 검증**: 브리지 0.5.2 와 부트로더(1.1.0)·앱은 실기 검증 전. 서버 쪽은 가짜 브리지 종단 시험, 브리지·부트로더는 호스트 시험으로만 확인. 부트로더 1.1.0 은 현장 설치 전에 JTAG 로 굽는다.
- **서버 자체 업데이트 실기 확인**: 교체 배치(적용·되돌리기·60초 자동 되돌림)는 Windows 실기에서 아직 돌려 보지 않음. 릴리스 전에 1회 확인.
- **로그인·권한**: 사내망 전제로 없음. 필요하면 ASP.NET Core 인증을 붙인다.
- **AGV 정지 확인 자동화**: 지금은 운용자가 적용 대기에서 정지를 확인하고 승인(메인보드 앱도 주행 중이면 거부). ACS 중계가 붙으면 마지막 W/E 패킷으로 판단 가능.
- **ACS REST 스키마 2건**: 위치 보고(보내기)와 호기 소속(받기). 임시 형식으로 틀만 있음 (`docs/ACS_API.md`).
- **펌웨어 서명**: 없음. 출처만 제한(서버 업데이트는 GitHub https, 브리지 OTA·스테이징 URL 은 브리지 server IP).
- **DB**: SQLite 로 결정(2026-10-09). 위치 이력·이벤트 이력 표는 RTLS 구현 때 추가.
