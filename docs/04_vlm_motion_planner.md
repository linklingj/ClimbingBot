# 모듈 4 --- Candidate Generator & VLM High-Level Planner

## 목적

현재 신체 상태와 루트 정보를 기반으로 다음에 움직일 limb와 target hold를
결정하고 이를 연속적인 pose sequence로 만든다.

## 역할 분리

``` text
Candidate Generator
    "무엇이 가능한가?"
          ↓
VLM
    "무엇을 선택할 것인가?"
```

VLM에게 모든 홀드를 자유롭게 선택시키지 않는다. Candidate Generator가
물리적으로 명백히 불가능한 후보를 제거한다.

## Candidate Generator

### 입력

-   현재 body/root position
-   limb 위치
-   현재 각 limb의 grasp hold
-   신체 비율
-   route hold positions

### 출력

``` json
{
  "left_hand": [7, 8, 10],
  "right_hand": [8, 10, 12],
  "left_foot": [2, 4],
  "right_foot": [3, 4, 5]
}
```

### 초기 reachability

초기 버전에서는 limb root 또는 현재 body state로부터 거리 threshold를
계산한다.

추후 개선: - limb별 reach ellipse - torso orientation 반영 - joint limit
기반 IK feasibility test - learned reachability model

## VLM 입력

VLM에는 두 종류의 정보를 함께 제공한다.

### 1. 이미지

-   전체 벽 이미지
-   선택된 route 강조 및 홀드 id 오버레이
-   현재 body pose overlay

### 2. Structured JSON

``` json
{
  "goal": {
    "top_hold_id": 21
  },
  "body": {
    "left_hand": 8,
    "right_hand": 10,
    "left_foot": 3,
    "right_foot": 5
  },
  "holds": [
    {"id": 12, "position": [0.4, 1.8]},
    {"id": 14, "position": [0.8, 2.0]}
  ],
  "candidates": {
    "left_hand": [12, 14],
    "right_hand": [12, 14],
    "left_foot": [7],
    "right_foot": [7, 8]
  }
}
```

## Structured Output

기본 action 단위:

``` json
{
  "moving_limb": "left_hand",
  "target_hold_id": 14
}
```

multi-step 결과:

``` json
{
  "moves": [
    {"moving_limb": "left_hand", "target_hold_id": 14},
    {"moving_limb": "right_foot", "target_hold_id": 8},
    {"moving_limb": "right_hand", "target_hold_id": 18}
  ]
}
```

출력 schema에서 limb enum과 candidate hold ID를 강제한다.

## Pose State

각 move 수행 후 새로운 pose를 만든다.

``` json
{
  "left_hand": 14,
  "right_hand": 10,
  "left_foot": 3,
  "right_foot": 5
}
```

## Planning 전략

초기에는 전체 루트를 한 번에 생성하는 방식보다 **짧은 horizon +
re-planning**을 우선한다.

``` text
Current Pose
   ↓
Candidate Generation
   ↓
VLM Move
   ↓
RL Execution
   ↓
Success?
 ┌─┴─┐
Yes  No
 │    └→ Re-plan
 ▼
Next State
```

이를 통해 VLM 계획과 실제 물리 제어 사이의 오차를 줄인다.

## 검증 규칙

VLM 출력 후 반드시: - target이 candidate set에 존재하는지 - 해당 limb가
이미 target을 잡고 있지 않은지 - target hold가 route에 포함되는지 -
output schema가 유효한지

검사한다.

## 평가

-   Valid action rate
-   Candidate violation rate
-   Top hold까지 계획 성공률
-   평균 move 수
-   RL execution까지 포함한 plan success rate

## 핵심 위험

VLM의 시각적 reasoning이 좋아도 실제 물리 feasibility를 완전히
이해한다고 가정하면 안 된다. 따라서 Candidate Generator와 RL execution
결과가 VLM을 둘러싼 **constraint layer** 역할을 한다.
