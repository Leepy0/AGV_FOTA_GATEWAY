# ACS REST 연동 — 위치 보고(보내기)·호기 소속(받기) 준비 (스키마 확정 전)

이 문서는 ACS 연동 세 갈래 중 REST 두 갈래(위치 보고·호기 소속 받기)와, 맨 아래 [TCP 중계의 안전 규칙](#acs-tcp-중계--하향-fr-차단과-허용-ip) 을 다룹니다. 전체 흐름은 [SYSTEM_FLOW.md 4.4절](SYSTEM_FLOW.md#44-acs-연동-세-갈래) 에 있습니다.

서버가 추정한 호기 위치를 **기존 ACS 서버로 REST 로 올립니다.** ACS 쪽 스키마가 정해지면 아래 "반영 지점" 만 고치면 됩니다.
RTLS 추정기는 아직 없으므로 지금은 시험 주입 API 로 위치를 넣어 발행 경로를 확인합니다.

## 구조

```
AgvPositionStore (호기별 최신 위치, 변경 버전)  ──▶  AcsPositionPushService (주기 발행, 변경분만, 백오프)
        ▲                                                   │ AcsPositionMapper (스키마 반영 지점)
   RTLS 추정기 (예정) / POST /api/positions/{agv} (시험)       ▼ AcsRestPublisher (메서드·인증·타임아웃)  ──▶ ACS
```

- 발행은 `AcsApi.IntervalMs`(기본 1초)마다. `OnlyChanged` 면 지난 발행 이후 바뀐 호기만, 아니면 전체.
- `PerAgv` 면 호기마다 한 요청(`Path` 의 `{agv}` 치환), 아니면 한 요청에 묶음.
- 실패하면 큐에 쌓지 않고 다음 주기에 최신값을 다시 보냅니다(위치는 최신값이 전부). 연속 실패는 주기를 2배씩 늘려 `MaxBackoffSec` 까지.
- `LogOnly: true` 면 보내지 않고 **통신 로그** 화면에 JSON 만 남깁니다. 스키마 맞추는 동안 이 모드로 형식을 확인합니다.
- `BaseUrl` 이 `http://` 나 `https://` 로 시작하는 절대 주소가 아니면 요청을 보내지 않고 `잘못된 주소: '…' (BaseUrl 은 http:// 또는 https:// 로 시작)` 실패로 남깁니다.
- 서버를 다시 켜면 DB 의 마지막 위치를 `LOST`·오프라인으로 복원합니다. heartbeat 가 돌아와도 `LOST` 로 남고, 새 추정이나 주입이 와야 `TRACKED` 가 됩니다. 복원한 값은 바뀌기 전까지 발행하지 않습니다.
- ACS 가 **끌어가는** 방식이면 `GET /api/positions` (전체) / `GET /api/positions/{agv}` 를 쓰면 됩니다.

## 임시 본문 (제안) — `GET /api/acs/preview` 로 현재 값을 볼 수 있음

```json
{
  "source": "AgvFotaGateway",
  "sentAt": "2026-10-09T03:00:01+00:00",
  "positions": [
    { "agvId": 7, "map": "1라인", "x": 12.5, "y": 3.0, "heading": 90.0, "speed": 0.8,
      "state": "TRACKED", "rotating": false, "lastTag": "0T21", "lastTagAt": "2026-10-09T02:59:58+00:00",
      "updatedAt": "2026-10-09T03:00:00+00:00", "online": true }
  ]
}
```

| 필드 | 뜻 | 단위·형식 |
|---|---|---|
| x, y | 맵 좌표 | m, 맵 원점 기준 (RTLS.md 3절). 모르면 생략 |
| heading | 진행 방향 | 도, x축 기준 반시계 0~360 |
| state | TRACKED / DEGRADED / LOST / UNKNOWN | RTLS.md 4절 |
| rotating | 제자리 회전 중 | Q 이벤트 시작~끝 |
| lastTag, lastTagAt | 마지막 태그 코드와 시각 | 4자 코드 |
| updatedAt, sentAt | 추정 시각, 전송 시각 | ISO 8601, UTC. 현지 시각이 필요하면 매퍼에서 변환 |
| online | 브리지 heartbeat 기준 | |

## 월요일 스키마 반영 지점

| 바꿀 것 | 파일 |
|---|---|
| 필드 이름·단위·좌표계·시각 형식·묶음/개별 구조 | `src/Gateway.Core/Acs/AcsPositionMapper.cs` (DTO 두 클래스 + `Build`/`Map`, 직렬화 옵션 `Json`) |
| 주소·메서드·인증·주기·묶음 여부 | `appsettings.json` `AcsApi` 섹션 (또는 환경변수 `AcsApi__AuthValue`) |
| 응답 확인 규칙 (ACS 가 2xx 외로 성공을 알리면) | `src/Gateway.Core/Acs/AcsRestPublisher.cs` `PublishAsync` |
| 테스트 | `tests/Gateway.Core.Tests/AcsPositionTests.cs` |

## 스키마 받을 때 확인할 것

1. 엔드포인트 URL, 메서드(POST/PUT), 인증(없음/Bearer/API 키 헤더/Basic), TLS 여부
2. 한 요청에 여러 호기를 묶는지, 호기별인지 (호기별이면 URL 에 호기 번호가 들어가는지)
3. 필드 이름과 표기(camelCase/snake_case), 좌표 단위(m/mm/px)와 원점·축 방향, 방향 단위와 기준축, 속도 단위
4. 시각 형식(ISO 8601/epoch/현지 시각)과 시간대
5. 위치를 모를 때(LOST/UNKNOWN) 보내야 하는 값 — 생략, null, 마지막 위치
6. 호기 식별자 — 호기 번호(01~99)로 충분한지, 라인/맵 구분이 필요한지(PAN ID)
7. 발행 주기와 ACS 가 받아들이는 최대 빈도, 응답 형식(ack 본문·오류 코드), 재전송 규칙
8. ACS 가 끌어가는 방식도 가능한지 (`GET /api/positions`)

## 시험 순서 (스키마 전에도 가능)

```bash
# 1) LogOnly 로 켜고 위치를 주입 → 통신 로그 화면에 ACS< JSON 이 찍히는지
curl -X POST localhost:8080/api/positions/7 -H 'Content-Type: application/json' \
     -d '{"x":12.5,"y":3.0,"heading":90,"state":"TRACKED","speed":0.8,"lastTag":"0T21","map":"1라인"}'
curl localhost:8080/api/acs/preview
# 2) ACS 주소를 넣고 LogOnly=false → GET /api/acs/status 의 lastResult 가 HTTP 2xx 인지
curl -X POST localhost:8080/api/acs/push
```


---

# ACS → 서버: 호기 소속 받기 (제품군 → 층 → 라인 → 공정 → 라우트)

기존 ACS 가 가진 호기 분류를 받아와 **구역** 화면의 트리로 보여 주고, 배포 작업에서 "A제품 / 2F / L1 전체" 처럼 구역 단위로 대상을 고를 수 있게 합니다.
스키마 확정 전에도 구역 화면에서 직접 입력·CSV 붙여넣기로 쓸 수 있습니다.

## 구조

| 위치 | 역할 |
|---|---|
| `Gateway.Core/Groups/AgvGroup.cs` | 5단계 소속 모델(`AgvGroup`), 호기별 소속(`AgvAssignment`), 트리(`GroupTree`), CSV(`GroupCsv`) |
| `Gateway.Core/Acs/AcsInventoryMapper.cs` | **스키마 반영 지점** — ACS 응답 JSON → `AgvAssignment` 목록. 필드 이름은 `AcsInventoryFieldMap` 으로 |
| `Gateway.Server/Options/AcsInventoryOptions.cs` | `appsettings.json` 의 `AcsInventory` 섹션 (주소·인증·주기·필드 매핑) |
| `Gateway.Server/Services/AcsInventoryPullService.cs` | 주기 GET → 해석 → 저장소 반영. 미리보기(저장 없이 해석) |
| `Gateway.Server/Services/AgvGroupStore.cs` | SQLite `data/gateway.db` 의 `agv_groups` 표 (예전 `agv_groups.json` 은 첫 실행 때 자동 이전). 출처 acs / manual / import |
| 화면 **구역** (`/groups`) | 트리, 호기 표, 직접 입력, CSV 가져오기·내려받기, ACS 미리보기·지금 받기, "이 호기들에 배포" |
| 화면 **배포 작업** | 구역 단계별 선택 → 그 아래 호기 전부 대상에 추가 (`/jobs?group=경로` 로도 진입) |
| 화면 **호기 현황** | 구역 열, 구역 필터 (`/?group=경로`) |

## 임시 응답 형식 (기본 필드 매핑)

```json
{ "agvs": [
  { "agvId": 7, "name": "AGV-07", "productFamily": "A제품", "floor": "2F", "line": "L1", "process": "조립", "route": "자재X" }
] }
```

- 호기 목록은 최상위 배열이거나 `agvs`/`items`/`data`/`result`/`list`/`vehicles` 속성 (자동 탐지). 다르면 `Fields.Root` 에 경로(`payload.vehicles`).
- `agvId` 는 숫자(1~99) 또는 `"AGV-07"` 같은 문자열 (`Fields.AgvIdRegex` 의 첫 캡처 그룹으로 번호 추출).
- 중첩 필드는 점으로: `"Floor": "location.floor"`. 없는 필드는 빈 값(미지정).
- 구역 단계는 5개로 고정이며 ACS 에 없는 단계는 비워 둬도 됩니다 (트리에서 `(미지정)`). 다섯 단계가 모두 비면 `미분류` 노드로 모입니다.
- 단계 값 안의 `/` 는 저장할 때 전각 `／`(U+FF0F)로 바뀝니다. 경로 구분자 ` / ` 와 겹치지 않게 하려는 것이며, 화면 입력·CSV·ACS 받기·`PUT /api/agvs/{n}/group` 이 모두 같습니다.

## 반영 규칙과 안전장치

ACS 응답을 받으면 아래 순서로 판단합니다(`AcsInventoryPullService.PullAsync`, 규칙 계산은 `Gateway.Core/Groups/AcsMerge.cs`).

| 경우 | 처리 |
|---|---|
| HTTP 실패, `BaseUrl` 이 http(s) 절대 주소가 아님 | 실패. 반영 안 함 |
| 응답에서 호기 목록 배열을 못 찾음 (`{"result":"error"}`·`{"success":false}` 같은 오류 응답) | `실패 HTTP 200 · 호기 목록을 찾지 못함 — 반영 안 함` |
| 빈 목록 (`{"agvs":[]}`) | `… 호기 목록이 비어 있음 — 반영 안 함` |
| 레코드는 있는데 모두 호기 번호 해석 실패 (필드 이름이 바뀐 경우) | `… N건 모두 호기 번호 해석 실패 (Fields 매핑 확인) — 반영 안 함` |
| 소속 저장소를 기동 때 다 읽지 못함 | `반영 실패: …`. 원인을 고치고 서버를 다시 시작할 때까지 ACS 값을 반영하지 않음 (읽지 못한 수동 값을 덮지 않게) |
| 정상 | 아래 정책대로 반영. 결과 예 `HTTP 200 · 8건 중 8대 해석 · 변경 0 · 삭제 0` |

- 실패하면 실패 횟수를 올리고 1분, 2분, 4분, 8분 간격으로 다시 시도합니다(주기보다 길어지지 않음). 소속은 바꾸지도 지우지도 않습니다.
- "응답 미리보기" 에도 `반영 안 됨: 이유` 가 보입니다.
- **대량 삭제 보류**: `RemoveMissing=true` 여도 ACS 목록에서 빠진 `acs` 출처 호기가 **2대 이상이면서 현재 `acs` 출처의 절반 이상**이면 지우지 않습니다. 결과에 `삭제 보류 N` 이 붙고, 구역 화면 노란 배너·서비스 로그에 `ACS 목록에서 acs 출처 N대가 한 번에 빠져 삭제 보류 (…) — 의도한 것이면 구역 화면에서 직접 삭제` 가 남습니다.
- 반영은 한 DB 트랜잭션입니다. 실패하면 하나도 바뀌지 않습니다.

## 월요일 스키마 반영

1. 대개 `appsettings.json` 의 `AcsInventory` 만 고치면 됩니다: `BaseUrl`, `Path`, `AuthType`/`AuthValue`, `Fields.*`.
2. 구조가 호기별 목록이 아니면(예: 구역별로 호기 배열이 들어 있음) `AcsInventoryMapper.Parse` 를 고칩니다. 테스트는 `AgvGroupTests`.
3. 반영 정책: `OverrideManual=false` 면 화면·CSV 로 넣은 값은 ACS 가 덮지 않음. `RemoveMissing=true` 면 ACS 목록에서 빠진 (acs 출처) 호기는 삭제(한 번에 절반 이상이 빠지면 보류).
4. 먼저 `Enabled=false` 인 채 "응답 미리보기" 로 `반영 안 됨` 이 없는지 봅니다.

## CSV

- 형식: `agv,제품군,층,라인,공정,라우트[,이름]`. 첫 줄 머리글은 건너뛰고, 따옴표 밖에 탭이 있으면 탭 구분으로 읽습니다(엑셀 복사).
- 내보내기(`GET /api/groups/export.csv`, 구역 화면 "CSV 내려받기")는 `=` `+` `-` `@` 탭·CR 로 시작하는 값 앞에 `'` 를 붙입니다. 엑셀에서 열 때 수식으로 실행되지 않게 하려는 것입니다. 쉼표·따옴표·탭·줄바꿈이 든 값은 큰따옴표로 묶습니다(`""` 이스케이프).
- 가져오기(`POST /api/groups/import`, 구역 화면 "CSV 붙여넣기")는 그 `'` 를 떼고(뒤가 수식 첫 글자일 때만), 큰따옴표 안의 줄바꿈을 값으로 읽습니다.
  - 큰따옴표가 닫히지 않은 행은 `N행: 따옴표가 닫히지 않음` 오류로 그 행만 빼고, 다음 줄부터 다시 읽습니다.
  - 오류 행을 뺀 나머지는 한 트랜잭션으로 저장합니다. 저장에 실패하면 하나도 반영되지 않습니다. 응답은 `{ "imported": n, "errors": [...] }`.

## 스키마 받을 때 확인할 것

1. 호기 목록 조회 URL·메서드(GET 가정)·인증·페이지네이션 유무
2. 호기 식별자: 번호인지 이름(`AGV-07`)인지, 우리 호기 번호(01~99)와 1:1 인지
3. 분류 단계가 실제로 5개(제품군·층·라인·공정·라우트)인지, 이름이 뭔지, 빠지거나 더 있는 단계가 있는지
4. 라우트(대상 자재)가 호기 속성인지 공정 속성인지, 한 호기가 여러 라우트를 가질 수 있는지(그러면 첫 번째만 쓸지)
5. 변경 빈도와 적정 조회 주기 (기본 5분), 변경 시각 필드가 있는지
6. ACS 가 거꾸로 우리 쪽을 조회하는 방식(`GET /api/groups`)도 가능한지

## 시험 순서 (스키마 전에도 가능)

```bash
# 1) 구역 화면에서 직접 입력 또는 CSV 붙여넣기 → 트리·배포 대상 선택 확인
curl -X PUT localhost:8080/api/agvs/7/group -H 'Content-Type: application/json' \
     -d '{"productFamily":"A제품","floor":"2F","line":"L1","process":"조립","route":"자재X"}'
curl localhost:8080/api/groups                       # 트리
curl "localhost:8080/api/groups/agvs?path=A제품%20/%202F"   # 경로 아래 호기
# 2) ACS 주소·인증을 넣고 (Enabled 는 아직 false 여도 됨) 구역 화면 "응답 미리보기" 또는
curl -X POST localhost:8080/api/acs/inventory/preview
# 3) 해석 결과가 맞으면 Enabled=true → 자동 반영. 수동: POST /api/acs/inventory/pull
```

---

# ACS TCP 중계 — 하향 F·R 차단과 허용 IP

TCP 소켓 규격은 아직 확인 전입니다(기본 `Gateway.Acs.Enabled=false`). 규격과 상관없이 지금 들어가 있는 안전 규칙입니다. Server 모드로 켤 때의 설정 예:

```json
"Gateway": {
  "Acs": { "Enabled": true, "Mode": "Server", "Host": "127.0.0.1", "Port": 6000, "ReconnectSec": 3,
           "AllowedRemotes": [ "192.168.10.20" ] }
}
```

| 규칙 | 내용 |
|---|---|
| 상향 (AGV → ACS) | STX·ETX·BCC 가 모두 맞는 `E`·`A` 프레임만 넘김. 깨진 조각은 버리고 통신 로그·설정 화면에 수를 남김 |
| 하향 `F`·`R` 차단 | ACS 가 보낸 프레임 중 종류가 `F`(메인보드 업데이트 진입·전송)·`R`(브리지용 응답)이면 메인보드로 넘기지 않고 버림. 서비스 로그에 10초에 한 번 `ACS 하향 F/R 프레임 버림 …`, 설정 화면 "하향 차단" 에 누적 수. Client·Server 모드 모두 |
| 허용 IP (`AllowedRemotes`) | **Server 모드만**. 목록에 없는 IP 의 접속은 받자마자 닫고 기존 연결은 그대로 둠(설정 화면 "거부한 접속"). 비우면 모두 허용. IP 를 그대로 비교(CIDR·호스트명 없음, IPv4-mapped IPv6 는 IPv4 로). 해석되지 않는 항목은 기동 때 경고하고 무시, 항목이 있는데 하나도 해석되지 않으면 모두 거부 |
| 연결 교체 | Server 모드는 리스너를 계속 열어 두고, 허용된 새 접속이 오면 기존 연결을 닫고 교체 (ACS PC 재부팅 뒤 반쯤 끊긴 연결이 남아도 다시 붙음) |
| 끊김 감지 | 두 모드 모두 TCP keepalive(10초 무통신, 3초 간격, 3회) — FIN·RST 없이 사라진 상대를 약 20초 안에 알아챔. 보내기 실패도 연결 종료 |
| 방화벽 | Server 모드로 켜고 `install-service.bat` 을 다시 실행하면 `AGV FOTA Gateway ACS TCP` 규칙(`Port`)을 만듦 |

- Server 모드는 한 번에 한 연결만 씁니다. 다른 PC 가 접속해 ACS 연결을 빼앗지 않도록 **`AllowedRemotes` 에 ACS IP 를 넣습니다.**
- 수동 전송(`POST /api/agvs/{n}/send`, 통신 로그 화면)도 `F`·`R` 프레임을 거부합니다(400).
