# ACS REST 연동 — 위치 보고(보내기)·호기 소속(받기) 준비 (스키마 확정 전)

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
| `Gateway.Server/Services/AgvGroupStore.cs` | `data/agv_groups.json`. 출처 acs / manual / import |
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
- 구역 단계는 5개로 고정이며 ACS 에 없는 단계는 비워 둬도 됩니다 (트리에서 `(미지정)`).

## 월요일 스키마 반영

1. 대개 `appsettings.json` 의 `AcsInventory` 만 고치면 됩니다: `BaseUrl`, `Path`, `AuthType`/`AuthValue`, `Fields.*`.
2. 구조가 호기별 목록이 아니면(예: 구역별로 호기 배열이 들어 있음) `AcsInventoryMapper.Parse` 를 고칩니다. 테스트는 `AgvGroupTests`.
3. 반영 정책: `OverrideManual=false` 면 화면·CSV 로 넣은 값은 ACS 가 덮지 않음. `RemoveMissing=true` 면 ACS 목록에서 빠진 (acs 출처) 호기는 삭제.

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
