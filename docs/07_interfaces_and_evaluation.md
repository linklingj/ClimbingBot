# 모듈 7 --- 인터페이스, 통합 및 평가 명세

## 목적

각 모듈을 독립적으로 개발하고 교체할 수 있도록 공통 데이터 계약과
end-to-end 평가 방식을 정의한다.

## 핵심 데이터 흐름

``` text
Frame
 → HoldDetection[]
 → Route[]
 → WallScene
 → BodyState + CandidateSet
 → PlannerAction / PoseSequence
 → RLTrajectory
 → ARPlayback
```

## 권장 공통 Scene JSON

``` json
{
  "wall": {
    "coordinate_system": "wall_local_2d",
    "width": 4.0,
    "height": 3.5
  },
  "holds": [
    {
      "id": 1,
      "position": [0.5, 0.4],
      "color": "red"
    }
  ],
  "routes": [
    {
      "id": 0,
      "hold_ids": [1, 3, 7, 9],
      "start_hold_ids": [1],
      "top_hold_id": 9
    }
  ]
}
```

## ID 규칙

홀드 ID는 한 scene 안에서 유일해야 한다. VLM, RL, AR 모두 동일 ID를
사용한다.

## Phase 1 구현과의 차이

Unity 쪽 `Hold`는 start/top을 **홀드 자신의 `role`**
(`Normal`/`Start`/`Top`)로 들고 있다. 위 Scene JSON처럼 route가
`start_hold_ids`/`top_hold_id`로 갖고 있지 않다.

Phase 1은 벽 하나에 루트가 하나뿐이라 route 객체가 없기 때문이다. 같은
홀드가 루트마다 다른 역할을 갖는 상황이 생기는 Phase 2에서 역할을
`Route`로 옮긴다. 그때 JSON 계약이 정본이다.

`ClimbingWall`은 위 좌표 규칙을 그대로 따른다 --- 원점은 벽 좌하단, +X
오른쪽, +Y 위, 단위 meter. `Hold.wallPosition`이 그 좌표다.

## 좌표 규칙

모듈 간 전달에는 가능한 한 `wall-local normalized/metric coordinate`를
사용한다.

권장: - 원점: 벽 좌하단 또는 명시적 wall origin - +X: 벽의 오른쪽 - +Y:
위 - 단위: meter

이미지 normalized coordinate와 AR world coordinate는 별도 필드로
유지한다.

## 실패 처리

### CV confidence 부족

사용자에게 hold 추가/삭제/수정 UI 제공.

### Route confidence 부족

사용자가 route 또는 start/top을 수정.

### VLM invalid output

schema validator → 재요청 또는 fallback.

### RL pose 실패

동일 pose 재시도 → VLM re-plan → 실패 표시 순서로 처리.

### AR tracking loss

animation을 일시 정지하고 wall anchor 재인식.

## End-to-End 상태 머신

``` text
SCAN_WALL
   ↓
DETECT_HOLDS
   ↓
EXTRACT_ROUTES
   ↓
SELECT_ROUTE
   ↓
BUILD_SCENE
   ↓
PLAN_MOVE
   ↓
SIMULATE_MOVE
   ├─ failure → REPLAN
   ↓
COMPLETE_SEQUENCE
   ↓
AR_PLAYBACK
```

## 실험 설계

### CV

다양한 거리, 조명, 홀드 크기에서 평가한다.

### Route Extraction

동일 색 복수 루트가 있는 벽을 반드시 포함한다.

### VLM

-   candidate set이 충분한 경우
-   dead-end가 있는 경우
-   손/발 교체가 필요한 경우
-   한 limb만 이동해서는 진행 불가능한 경우

### RL

-   seen/unseen hold layout
-   target distance별 성공률
-   1 pose와 연속 pose 수행 비교

### AR

실제 홀드 위치에 기준점을 두고 화면 또는 실제 거리 오차를 측정한다.

## 핵심 지표 Dashboard

  모듈                  대표 지표
  --------------------- -----------------------------------
  Segmentation          mask mAP / hold recall
  Route Extraction      route assignment accuracy
  Start/Top             start/top accuracy
  Candidate Generator   feasible candidate recall
  VLM                   valid move rate / plan success
  RL                    target pose success / fall rate
  AR                    overlay position error
  End-to-End            route solution generation success

## 개발 우선순위

가장 먼저 검증해야 할 기술적 불확실성은 다음 세 가지다.

1.  **RL ragdoll이 추상화된 grasp 조건에서 랜덤 target pose를 안정적으로
    수행할 수 있는가**
2.  **일반 iPhone의 plane tracking으로 홀드와 avatar를 충분히 정확하게
    정렬할 수 있는가**
3.  **Candidate Generator + VLM 구조가 유효한 pose sequence를 안정적으로
    생성하는가**

CV와 route extraction도 중요하지만, 프로젝트 전체의 실현 가능성을
좌우하는 부분은 위 세 영역이다.

## MVP 완료 조건

MVP는 다음 시나리오가 하나의 앱 흐름으로 동작하면 완료된 것으로
정의한다.

> 수직 벽을 촬영한다 → 홀드가 검출된다 → 하나의 루트가 추출된다 →
> 사용자가 루트를 선택한다 → VLM이 candidate 기반 pose sequence를
> 생성한다 → ML-Agents가 해당 sequence를 수행한다 → 생성된 캐릭터 동작이
> 실제 벽 위에 AR로 재생된다.

완벽한 biomechanical beta 생성보다 **전체 계층형 시스템이 실제 입력에서
end-to-end로 동작하는 것**을 MVP의 우선 목표로 둔다.
