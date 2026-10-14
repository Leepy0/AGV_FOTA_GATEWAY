# ACS Map REST API 스키마 (DevTools 관찰 기준)

> 출처: 2026-10-14 ACS 웹 화면의 Chrome DevTools Network 응답. 정식 OpenAPI 명세가 아닌 실제 호출·응답에서 관찰한 스키마다. 필수성, 인증, 페이지네이션, 필드 enum은 아직 서버 명세로 검증하지 않았다.

ACS 주소 예시: `http://10.233.22.62:2232`

## 관찰된 요청

| 메서드 | 경로 | 용도 |
|---|---|---|
| GET | `/v1/map/subareas` | 맵 하위 구역과 맵 이미지 좌표계 정보 |
| GET | `/v1/map/areas?siteCode=KR03&agvType[]=TAPE` | 사이트·AGV 유형에 따른 상위 구역 목록 |
| GET | `/v1/map/agv-info?subareaCode[]=...` | 지정한 하위 구역들에 소속된 AGV 정보. `subareaCode[]` 를 반복해 전달 |

응답은 모두 DevTools에서 HTTP `200 OK`, `Content-Type: application/json` 으로 확인했다. 브라우저의 응답이 성공했다는 사실만으로 게이트웨이 서비스 계정/네트워크에서도 접근 가능하다고 단정할 수는 없다.

## 1. `GET /v1/map/subareas`

응답: 객체 배열. 관찰된 필드:

| 필드 | 관찰 예 | 의미 |
|---|---|---|
| `code` | `1_FL1+1_WAREHOUSE` | 하위 구역 코드 |
| `name` | `1_WAREHOUSE` | 하위 구역 이름 |
| `areaCode` | `1_FL1` | 상위 구역 코드 |
| `areaName` | `창고_1층` | 상위 구역 표시명으로 보임 |
| `agvType` | `AMR(WEB_ACS)` | AGV 유형 문자열 |
| `uuid` | UUID 문자열 | 식별자 |
| `imageSizeX`, `imageSizeY` | 숫자 | 맵 이미지 크기 |
| `scalingX`, `scalingY` | 숫자 | 이미지 스케일 |
| `originX`, `originY` | 숫자 | 맵 원점 |
| `layoutMapOffsetX`, `layoutMapOffsetY` | 숫자 | 레이아웃 맵 오프셋 |
| `isUsed` | `Y` | 사용 여부로 보임 |

좌표 필드의 단위·좌표계 및 `isUsed` 의 전체 enum은 추가 확인이 필요하다.

## 2. `GET /v1/map/areas?siteCode=KR03&agvType[]=TAPE`

응답: 객체 배열. 관찰된 필드:

```json
[
  {
    "code": "A_FL1",
    "name": "냉장고동 1층",
    "businessUnit": "DA",
    "siteCode": "KR03",
    "siteName": "GWANGJU",
    "isUsed": "Y"
  }
]
```

화면 응답에는 `A_FL1`, `A_FL2`, `A_FL3`, `B_FL1`, `B_FL2`, `C_FL1`, `D_FL1`, `F_FL1`, `G_FL1`, `G_FL2`, `H_FL1` 등의 코드가 보였다. 이는 캡처 시점의 데이터 예시이며 전체 목록이라고 단정하지 않는다.

## 3. `GET /v1/map/agv-info?subareaCode[]=...`

응답: 객체 배열. 캡처에서 확인한 AGV 레코드의 주요 필드:

```json
[
  {
    "agvCode": "DA_GWANGJU_AC_250318_A006",
    "agvName": "1F_EC_Final Material_FMR099",
    "agvOpCtrl": "10.233.177.150",
    "agvSn": "",
    "agvType": "AMR",
    "almEquipId": "1F_EC_Final Material_FMR099",
    "amrType": "FORK-ACKERMANN",
    "autoChargingYn": "Y",
    "autoUpdateYn": "N",
    "businessUnit": "DA",
    "communication": "Wifi",
    "lidar2D": "Y",
    "loaderType": "Tow",
    "mainControl": "Linux",
    "manufacturedDate": "2025-03-18",
    "modelType": "Hik_ForkAMR",
    "payload": "500kgorLess",
    "process": "Main",
    "product": "AC",
    "setupDate": "2025-03-18",
    "siteSetup": "Y",
    "spare": "N",
    "standardLamp": "Y",
    "standardPlatform": "Y",
    "agvAffiliationList": [
      {
        "areaCode": "B_FL1",
        "subareaCode": "B_FL1+SGAC1F_ACS02",
        "aid": 99,
        "isMain": false
      }
    ]
  }
]
```

위 JSON은 캡처에서 읽을 수 있었던 필드만 모은 축약 예시이며, 모든 필드가 모든 레코드에 존재한다는 의미는 아니다. 화면에는 `bcolor`, `callType`, `cycleTime`, `description`, `disuse`, `disusedDate`, `disusedReason`, `fcolor`, `material`, `pro`, `qid`, `rdest`, `rsour`, `transferAgv`, `updateId` 등도 나타났다.

### 게이트웨이 매핑 시 주의점

- `agvCode` 는 `..._A006` 같은 ACS 내부 코드이고, 캡처 예시에서는 소속 정보의 `aid: 99` 및 `almEquipId` / `agvName` 의 `FMR099` 가 서로 대응한다. 게이트웨이의 호기 번호(1~99)와 어떤 필드가 공식적으로 일치하는지는 ACS 담당자 확인이 필요하다.
- 한 AGV에 `agvAffiliationList` 항목이 여러 개일 수 있다. 단순히 첫 항목을 선택하면 안 되며, `isMain` 의 의미와 주 소속 결정 규칙을 확인해야 한다.
- 게이트웨이의 기존 구역 모델은 제품군·층·라인·공정·라우트 5단계다. ACS의 `businessUnit`, `areaCode`, `subareaCode`, `process`, `product` 를 이 단계에 대응시키는 것은 기술적으로 가능하지만, 업무상 의미가 동일하다는 확인은 아직 없다.
- `agv-info` 요청은 `subareaCode[]` 목록을 필요로 한다. 따라서 단일 고정 `AcsInventory.Path` 로 직접 호출하는 것만으로는 전체 동기화가 완성되지 않는다. 실제 연동은 먼저 `subareas` 를 조회해 코드 목록을 만든 다음 `agv-info` 를 호출하는 다단계 클라이언트가 필요하다.
- `areas` 요청은 사이트 코드와 AGV 유형에 따라 필터링된다. `KR03` 및 `TAPE` 는 캡처의 요청 값이지 모든 설치 환경의 상수로 간주하면 안 된다.

## 현재 코드 반영 범위

- `AcsInventoryMapper` 의 `Fields.*` 경로는 객체 속성의 점 표기 외에 `agvAffiliationList[0].areaCode` 같은 배열 인덱스 표기도 지원한다.
- 회귀 테스트는 실제 응답에서 관찰한 `agvName`, `almEquipId`, `businessUnit`, `agvAffiliationList` 구조를 축약해 매핑을 검증한다.
- `AcsInventory.Enabled` 는 기본 `false` 로 유지한다. 다단계 조회, 호기 식별자, 다중 소속 선택 정책이 확인되기 전에는 운영 동기화를 켜지 않는다.

## 다음 확인 사항

1. API 인증 방식 및 서비스 PC에서의 접근 허용 여부
2. `/v1/map/subareas` 및 `/v1/map/areas` 의 필수 쿼리 조건
3. `agv-info` 의 `aid` 와 게이트웨이 호기 번호의 공식 대응 관계
4. 다중 소속에서 `isMain` 의 의미 및 선택 정책
5. 구역 5단계에 대한 업무 매핑과 비사용/폐기 AGV 필터 조건
