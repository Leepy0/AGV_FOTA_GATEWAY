# ACS Map REST API 스키마 (관찰 기준)

> 이 문서는 테스트 환경의 브라우저 개발자 도구에서 관찰한 요청·응답 구조를 정리한 참고 자료다. 정식 OpenAPI 명세가 아니며, 필수 필드·인증·페이지네이션·enum은 서버 명세로 검증되지 않았다. 공개 저장소 보호를 위해 실제 사이트명, 지역명, 사내 IP, 사업부 코드, AGV 식별자 및 운영 구역 코드는 제거하고 일반화했다.

## 관찰된 요청

| 메서드 | 경로 | 용도 |
|---|---|---|
| GET | `/v1/map/subareas` | 맵 하위 구역과 맵 이미지 좌표계 정보 |
| GET | `/v1/map/areas?siteCode={SITE_CODE}&agvType[]={AGV_TYPE}` | 사이트·AGV 유형에 따른 상위 구역 목록 |
| GET | `/v1/map/agv-info?subareaCode[]={SUBAREA_CODE}` | 지정한 하위 구역에 소속된 AGV 정보. `subareaCode[]` 를 반복해 전달할 수 있음 |

관찰한 응답은 HTTP `200 OK`, `Content-Type: application/json` 형식이었다. 브라우저에서의 성공만으로 게이트웨이 서비스 계정/네트워크에서도 접근 가능하다고 단정할 수 없다.

## 1. `GET /v1/map/subareas`

응답은 객체 배열이다. 관찰된 필드:

| 필드 | 예시 유형 | 의미 |
|---|---|---|
| `code` | `AREA_X+ZONE_Y` | 하위 구역 코드 |
| `name` | 문자열 | 하위 구역 이름 |
| `areaCode` | `AREA_X` | 상위 구역 코드 |
| `areaName` | 문자열 | 상위 구역 표시명으로 추정 |
| `agvType` | 문자열 | AGV 유형 |
| `uuid` | UUID 문자열 | 식별자 |
| `imageSizeX`, `imageSizeY` | 숫자 | 맵 이미지 크기 |
| `scalingX`, `scalingY` | 숫자 | 이미지 스케일 |
| `originX`, `originY` | 숫자 | 맵 원점 |
| `layoutMapOffsetX`, `layoutMapOffsetY` | 숫자 | 레이아웃 맵 오프셋 |
| `isUsed` | 문자열 | 사용 여부로 추정 |

좌표 필드의 단위·좌표계 및 `isUsed` 의 전체 enum은 추가 확인이 필요하다.

## 2. `GET /v1/map/areas`

요청 쿼리에는 사이트 코드와 AGV 유형이 사용된다. 값은 설치 환경별 설정으로 취급하고 코드에 고정하지 않는다.

응답 예시(일반화):

```json
[
  {
    "code": "AREA_X",
    "name": "Area name",
    "businessUnit": "UNIT_X",
    "siteCode": "SITE_X",
    "siteName": "Site name",
    "isUsed": "Y"
  }
]
```

위 값은 설명용 가상 데이터이며 실제 운영 값을 나타내지 않는다.

## 3. `GET /v1/map/agv-info`

응답은 객체 배열이다. 관찰된 주요 필드를 가상 값으로 나타내면 다음과 같다.

```json
[
  {
    "agvCode": "VEHICLE_CODE_X",
    "agvName": "Vehicle name",
    "agvOpCtrl": "CONTROLLER_ADDRESS",
    "agvSn": "",
    "agvType": "AMR",
    "almEquipId": "EQUIPMENT_ID_X",
    "amrType": "MODEL_TYPE_X",
    "autoChargingYn": "Y",
    "autoUpdateYn": "N",
    "businessUnit": "UNIT_X",
    "communication": "Wifi",
    "lidar2D": "Y",
    "loaderType": "Loader type",
    "mainControl": "Linux",
    "manufacturedDate": "YYYY-MM-DD",
    "modelType": "MODEL_X",
    "payload": "CAPACITY_CLASS",
    "process": "PROCESS_X",
    "product": "PRODUCT_X",
    "setupDate": "YYYY-MM-DD",
    "siteSetup": "Y",
    "spare": "N",
    "standardLamp": "Y",
    "standardPlatform": "Y",
    "agvAffiliationList": [
      {
        "areaCode": "AREA_X",
        "subareaCode": "SUBAREA_X",
        "aid": 7,
        "isMain": true
      }
    ]
  }
]
```

이 예시는 필드 구조 설명을 위한 가상 데이터다. 모든 필드가 모든 레코드에 존재한다는 의미는 아니다. 추가로 관찰된 필드에는 `bcolor`, `callType`, `cycleTime`, `description`, `disuse`, `disusedDate`, `disusedReason`, `fcolor`, `material`, `pro`, `qid`, `rdest`, `rsour`, `transferAgv`, `updateId` 등이 있다.

## 게이트웨이 매핑 시 주의점

- `agvCode`, `aid`, `almEquipId`, `agvName` 중 어느 필드가 게이트웨이 호기 번호와 공식적으로 대응하는지 확인이 필요하다. 예시 값만으로 매핑 규칙을 확정하지 않는다.
- 한 AGV에 `agvAffiliationList` 항목이 여러 개일 수 있다. 첫 항목을 무조건 선택하지 말고 `isMain` 의 의미와 주 소속 결정 규칙을 확인해야 한다.
- 게이트웨이의 구역 모델은 제품군·층·라인·공정·라우트 5단계다. ACS의 `businessUnit`, `areaCode`, `subareaCode`, `process`, `product` 를 이 단계에 대응시키려면 업무 의미를 확인해야 한다.
- `agv-info` 는 `subareaCode[]` 목록을 필요로 한다. 전체 동기화에는 먼저 `subareas` 를 조회해 코드 목록을 만든 다음 `agv-info` 를 호출하는 다단계 클라이언트가 필요할 수 있다.
- `areas` 의 사이트 코드와 AGV 유형은 환경별 설정으로 처리하고 운영 값을 상수로 넣지 않는다.

## 현재 코드 반영 범위

- `AcsInventoryMapper` 의 `Fields.*` 경로는 객체 속성의 점 표기 외에 `agvAffiliationList[0].areaCode` 같은 배열 인덱스 표기도 지원한다.
- 회귀 테스트는 민감한 운영 값을 포함하지 않는 가상 데이터를 사용해 배열 경로 매핑을 검증한다.
- `AcsInventory.Enabled` 는 기본 `false` 로 유지한다. 다단계 조회, 호기 식별자, 다중 소속 선택 정책이 확인되기 전에는 운영 동기화를 켜지 않는다.

## 다음 확인 사항

1. API 인증 방식 및 게이트웨이 실행 환경에서의 접근 허용 여부
2. `/v1/map/subareas` 및 `/v1/map/areas` 의 필수 쿼리 조건
3. `agv-info` 의 `aid` 와 게이트웨이 호기 번호의 공식 대응 관계
4. 다중 소속에서 `isMain` 의 의미 및 선택 정책
5. 구역 5단계 매핑과 비사용/폐기 AGV 필터 조건
