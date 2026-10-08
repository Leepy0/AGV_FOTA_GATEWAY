# AGV FOTA Gateway

AGV 의 Wi-Fi 브리지(ESP32-S3)와 메인보드 DSP(TMS320F2808) 펌웨어를 **웹 화면에서 올리고, 대상 호기와 시각을 정해 순차 배포**하는 서버입니다.
ACS(교통제어) TCP 소켓 중계도 같은 프로세스에 들어갑니다 (브리지가 서버 IP 하나로만 통신하기 때문).

```
브라우저 (PC·태블릿) ──HTTP 8080──▶ AgvFotaGateway.exe ──UDP 50000──▶ ESP32 브리지 ──RS232──▶ DSP
                                        │  펌웨어 저장소 · 작업 스케줄러 · 브리지/DSP OTA 클라이언트
                                        └──TCP──▶ ACS (규격 확인 후 활성화)
```

**설치**: Releases 의 zip 을 풀고 `install-service.bat`(관리자) 한 번 → `http://<PC IP>:8080`. 자세한 절차는 [docs/DEPLOY.md](docs/DEPLOY.md).

## 구성

| 경로 | 내용 |
|---|---|
| `src/Gateway.Core` | 브리지 UDP 패킷, AGV 프레임(BCC), DSP 이미지 헤더, DSP OTA·브리지 OTA 클라이언트. 벤치 도구(`DspOtaTest.cs`, `BridgeTest.cs`)에서 옮긴 코드 |
| `src/Gateway.Server` | ASP.NET Core + Blazor Server (.NET 10). 브리지 UDP 서비스, 호기 레지스트리, ACS 중계, 펌웨어 저장소, 작업 실행기, 화면 4개(호기 현황·펌웨어·배포 작업·통신 로그), REST API |
| `tests/Gateway.Core.Tests` | xUnit — 프로토콜 벡터, 패킷/프레임/헤더 왕복, DSP OTA 재전송 |
| `deploy/` | `start.bat`, `install-service.bat`, `uninstall-service.bat` |
| `docs/` | [DEPLOY.md](docs/DEPLOY.md) 설치·운영, [INTERFACES.md](docs/INTERFACES.md) 접점 규격 출처 |

## 개발

```bash
dotnet build && dotnet test
dotnet run --project src/Gateway.Server          # http://localhost:8080
```

하드웨어 없이 확인하려면 DSP_OTA 저장소의 시뮬레이터를 이 서버에 붙입니다 (브리지 + DSP 역할):

```bash
./dsp_ota_sim --agv 7 --server 127.0.0.1:50000 --listen 50010
```

REST API (화면과 같은 기능, 스크립트용): `GET /api/agvs`, `GET|POST|DELETE /api/firmware`, `GET|POST /api/jobs`, `POST /api/jobs/{id}/cancel`,
`POST /api/agvs/{n}/send` `{ "data": "<STX>…<ETX>" }`, `POST /api/agvs/{n}/cfg` `{ "data": "set ip …" }`, `GET /fw/{id}/{file}` (브리지가 받아 가는 파일).

## 릴리스

`v*` 태그를 푸시하면 Actions 가 win-x64 단일 exe(런타임 포함)를 zip 으로 Release 에 첨부합니다. 버전은 태그에서 가져옵니다.

## 아직 정해지지 않은 것

- **ACS 소켓 규격**: 기존 ZigBee 게이트웨이가 ACS 와 주고받는 TCP 규격(접속 방향, 프레임 구분, 호기 매핑). 지금은 프레임이 그대로 흐른다고 가정한 뼈대이며 `Acs.Enabled=false`.
- **로그인·권한**: 사내망 전제로 없음. 필요하면 ASP.NET Core 인증을 붙인다.
- **AGV 정지 확인 자동화**: 지금은 운용자가 정지 상태에서 실행. ACS 중계가 붙으면 마지막 W/E 패킷으로 판단 가능.
