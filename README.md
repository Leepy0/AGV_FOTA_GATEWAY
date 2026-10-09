# AGV FOTA Gateway

AGV 의 Wi-Fi 브리지(ESP32-S3)와 메인보드 DSP(TMS320F2808) 펌웨어를 **웹 화면에서 올리고, 대상 호기와 시각을 정해 순차 배포**하는 서버입니다.
메인보드 OTA 는 브리지 0.5.0 의 B안(브리지가 `.dspfw` 를 받아 두었다가 상주 부트로더로 굽는 방식)을 씁니다.
ACS(교통제어) TCP 소켓 중계도 같은 프로세스에 들어갑니다 (브리지가 서버 IP 하나로만 통신하기 때문).

```
브라우저 (PC·태블릿) ──HTTP 8080──▶ AgvFotaGateway.exe ──UDP 50000──▶ ESP32 브리지 ──RS232──▶ DSP (부트로더 + 앱)
                                        │  펌웨어 저장소 · 작업 스케줄러 · 브리지 OTA(0x30) · 메인보드 OTA(0x32/0x34 스테이징→적용)
                                        └──TCP──▶ ACS (규격 확인 후 활성화)
```

**설치**: Releases 의 zip 을 풀고 `install-service.bat`(관리자) 한 번 → `http://<PC IP>:8080`. 이후 버전은 서버가 스스로 받아(SHA-256 검증) 적용합니다(**서버 업데이트** 화면). 사내망에서 api.github.com 이 막혀도 github.com 의 매니페스트로 확인합니다. 자세한 절차는 [docs/DEPLOY.md](docs/DEPLOY.md).

## 구성

| 경로 | 내용 |
|---|---|
| `src/Gateway.Core` | 브리지 UDP 패킷·heartbeat, AGV 프레임(BCC, ACS 화이트리스트), `.dspfw` 파서, 메인보드 OTA(B안)·브리지 OTA 클라이언트, 업데이트 피드 파싱 |
| `src/Gateway.Server` | ASP.NET Core + Blazor Server (.NET 10). 브리지 UDP 서비스, 호기 레지스트리, ACS 중계, 펌웨어 저장소, 작업 실행기, 화면 6개(호기 현황·펌웨어·배포 작업·통신 로그·서버 업데이트·설정), 파일 로그, 서버 자체 업데이트, REST API |
| `tests/Gateway.Core.Tests` | xUnit — 프로토콜 벡터, 패킷/프레임 왕복, `.dspfw`, 가짜 브리지로 메인보드 OTA 단계·실패·job 불일치 |
| `tests/fake_esp.py` | 가짜 브리지(0.5.0 B안) — 하드웨어 없이 서버 종단 시험 (`--fail-at erase` 로 실패 재현) |
| `deploy/` | `start.bat`, `install-service.bat`, `uninstall-service.bat` |
| `docs/` | [DEPLOY.md](docs/DEPLOY.md) 설치·운영, [INTERFACES.md](docs/INTERFACES.md) 접점 규격 출처, [RTLS.md](docs/RTLS.md) 위치 추정 기능 설계 검토(구현 전) |

## 개발

```bash
dotnet build && dotnet test
dotnet run --project src/Gateway.Server          # http://localhost:8080
```

하드웨어 없이 확인하려면 가짜 브리지를 이 서버에 붙입니다:

```bash
python3 tests/fake_esp.py --agv 7 --server 127.0.0.1:50000 --listen 50010 --dsp 1.0.0
```

REST API (화면과 같은 기능, 스크립트용): `GET /api/agvs`, `GET|POST|DELETE /api/firmware`, `GET|POST /api/jobs`, `POST /api/jobs/{id}/cancel`,
`POST /api/agvs/{n}/send` `{ "data": "<STX>…<ETX>" }`, `POST /api/agvs/{n}/cfg` `{ "data": "set ip …" }`, `GET /fw/{id}/{file}` (브리지가 받아 가는 파일),
`GET /api/positions[/{n}]` 호기 위치, `POST /api/positions/{n}` 시험 주입, `GET /api/acs/status` · `POST /api/acs/push` · `GET /api/acs/preview` ACS 위치 보고.

## 릴리스

- main push → `dev` 프리릴리스(테스트 빌드, 덮어쓰기). 자동 업데이트 대상 아님.
- 정식: csproj `<Version>` 을 올려 커밋한 뒤 `vX.Y.Z` 태그 푸시 또는 Actions `build` 를 `release=true` 로 실행 → Latest 릴리스에
  `AgvFotaGateway_win-x64.zip`, `.sha256`, `AgvFotaGateway.json`(매니페스트). 설치된 서버들이 github.com 의 매니페스트 주소로 이 릴리스를 보고 업데이트합니다.
- 절차는 `.claude/skills/fota-gateway-release/SKILL.md` (Claude 가 "릴리스 올려줘" 요청에 따르는 순서).

## 아직 정해지지 않은 것

- **ACS 소켓 규격**: 기존 ZigBee 게이트웨이가 ACS 와 주고받는 TCP 규격(접속 방향, 프레임 구분, 호기 매핑). 지금은 프레임이 그대로 흐른다고 가정한 뼈대이며 `Acs.Enabled=false`. ACS 로는 E·A 프레임만 넘깁니다.
- **메인보드 OTA 실기 검증**: 브리지 0.5.0 과 부트로더·앱은 실기 검증 전. 서버 쪽은 가짜 브리지로만 확인.
- **로그인·권한**: 사내망 전제로 없음. 필요하면 ASP.NET Core 인증을 붙인다.
- **AGV 정지 확인 자동화**: 지금은 운용자가 정지 상태에서 실행. ACS 중계가 붙으면 마지막 W/E 패킷으로 판단 가능.
