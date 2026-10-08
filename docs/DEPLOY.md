# AGV FOTA Gateway — 설치·운영

사내 PC(Windows 10/11, x64) 한 대에 올려 브라우저로 쓰는 서버입니다. .NET 설치가 필요 없습니다(런타임 포함 단일 exe).

## 1. 설치 (원클릭)

1. GitHub **Releases** 에서 `AgvFotaGateway_<버전>_win-x64.zip` 을 받아 원하는 폴더(예: `C:\AgvFotaGateway`)에 풉니다.
2. 둘 중 하나를 실행합니다.

| 방법 | 파일 | 언제 |
|---|---|---|
| 콘솔 실행 | `start.bat` 더블클릭 | 시험, 첫 확인. 창을 닫으면 종료 |
| **서비스 등록** | `install-service.bat` 우클릭 → 관리자 권한으로 실행 | 운영. 부팅 시 자동 시작, 죽으면 자동 재시작, 방화벽(TCP 8080, UDP 50000/50001) 자동 허용 |

3. 브라우저에서 `http://<이 PC IP>:8080` 을 엽니다. 사내 다른 PC·태블릿에서도 같은 주소로 접속합니다.

삭제는 `uninstall-service.bat`(관리자). 업로드한 펌웨어와 작업 기록은 `data\` 폴더에 남습니다.

## 2. 브리지 연결

각 호기의 Wi-Fi 브리지에 이 PC 의 IP 를 서버로 설정합니다 (브리지 콘솔 `set server <IP>` / 설정 AP 웹 페이지).
브리지는 **설정된 서버 IP 에서 온 패킷만** 받으므로 Gateway 는 반드시 그 IP 의 PC 에서 돌아야 하고, 같은 PC 에서 BridgeTest 같은 벤치 도구를 동시에 띄우면 UDP 50000 이 겹칩니다.

호기가 heartbeat 를 보내면 **호기 현황** 화면에 나타납니다.

## 3. 펌웨어 올리기 → 배포

1. **펌웨어** 화면에서 업로드
   - 브리지: Actions/Release 의 `agv_wifi_bridge.bin` 하나
   - DSP: 슬롯 A 용·B 용 이미지 둘 (`.img`, 헤더 포함) 또는 raw `.bin` (버전·프로젝트를 입력하면 헤더를 붙여 저장)
2. **배포 작업** 화면에서 펌웨어 선택 → 대상 호기 체크 → 지금 또는 예약 시각 → 작업 만들기
3. 작업은 호기를 **한 대씩 순차로** 진행하고 결과를 호기별로 기록합니다. 실패하면(기본) 나머지를 중단합니다.

**AGV 정지 상태에서만** 실행하세요. DSP 는 슬롯 지우기 동안 몇 초간 ACS 응답이 끊기고, 브리지는 재부팅으로 1~2초 끊깁니다.

## 4. 설정 (`appsettings.json`)

| 키 | 기본 | 설명 |
|---|---|---|
| `Urls` | `http://0.0.0.0:8080` | 웹 포트 |
| `Gateway.UdpPort` | 50000 | 브리지 server_port |
| `Gateway.RfidPort` | 50001 | RFID 브로드캐스트 수신 (0 = 끔) |
| `Gateway.DataDir` | `data` | 펌웨어·작업 저장 폴더 |
| `Gateway.DspBlockSize` | 64 | DSP 전송 블록 (16~112) |
| `Gateway.PublicBaseUrl` | 빈 값 | 브리지 OTA 파일 URL 의 서버 주소. 비우면 호기 쪽 NIC IP:8080 자동 |
| `Gateway.Acs.*` | 비활성 | ACS TCP 중계. 규격 확인 후 `Enabled: true` |

바꾼 뒤 서비스를 재시작합니다 (`sc stop AgvFotaGateway` / `sc start AgvFotaGateway`).

## 5. 업데이트 (자동)

WPF 프로그램의 자동 업데이트와 같은 방식입니다. 서버가 새 버전을 확인·다운로드해 두었다가, 배포 작업이 없을 때 스스로 파일을 바꾸고 재시작합니다.
`appsettings.json` 과 `data\` 는 유지됩니다.

- 화면 **서버 업데이트**: 지금 확인 → 다운로드 → 적용(서비스 재시작, 약 10초). 메뉴에 ● 가 붙으면 새 버전이 있다는 뜻입니다.
- 자동 확인 주기는 `Update.CheckIntervalMin`(기본 6시간). `Update.AutoApply: true` 면 작업이 없을 때 자동으로 적용까지 합니다.
- 새 버전 출처 (`Update.Source`)

| Source | 설정 | 언제 |
|---|---|---|
| `GitHub` (기본) | `GitHubRepo`, 비공개 저장소면 `GitHubToken`(fine-grained PAT, Contents: Read) | 서버 PC 가 github.com 에 나갈 수 있을 때 |
| `Url` | `FeedUrl`: 사내 HTTP 서버의 `latest.json` 주소 | 사내망이 GitHub 를 막을 때. 릴리스 자산(`latest.json` + zip)을 사내 HTTP 폴더에 복사하고 `latest.json` 의 `url` 을 사내 주소로 고친다 |

토큰은 파일 대신 환경변수 `Update__GitHubToken` 으로도 줄 수 있습니다 (서비스 환경변수는 `sc` 대신 레지스트리 `HKLM\SYSTEM\CurrentControlSet\Services\AgvFotaGateway\Environment`).

적용 기록은 `data\update\apply.log`. 자동 적용이 실패하면 수동으로: 새 zip 을 같은 폴더에 덮어쓰고(`appsettings.json`, `data\` 제외) `install-service.bat` 을 다시 실행합니다.

## 6. 문제 해결

| 증상 | 확인 |
|---|---|
| 호기가 안 보임 | 브리지 `server` IP = 이 PC, 방화벽 UDP 50000, 브리지 콘솔 `stat` 의 `tx_err` |
| 서비스가 바로 멈춤 | 포트 8080/50000 을 다른 프로그램이 사용 중 (`netstat -ano | findstr :50000`), 이벤트 뷰어 → Windows 로그 → 응용 프로그램 |
| 브리지 OTA 가 `fail begin` | ESP 가 이 PC 의 8080 에 접속 못 함 — 방화벽, `PublicBaseUrl` |
| DSP OTA `응답 없음` | 메인보드 펌웨어에 DSP_OTA 코어가 들어 있는지, 호기 번호 일치, 통신 로그 화면에서 `MAIN<` 뒤에 `MAIN>` 응답이 오는지 |
| DSP OTA `trial 상태` | 이전 OTA 가 확정되지 않음 — 통신 로그 화면에서 `<STX>F1107C <ETX>` 류 확정 프레임을 보내거나 180초 롤백 대기 |
