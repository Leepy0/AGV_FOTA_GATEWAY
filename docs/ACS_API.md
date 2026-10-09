# ACS 위치 보고 REST — 연동 준비 (스키마 확정 전)

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
