# 클라이밍 봇 프로젝트 기획서

## 1. 프로젝트 개요

**클라이밍 봇(Climbing Bot)**은 컴퓨터 비전, VLM, 강화학습, Unity 물리
시뮬레이션, AR을 결합하여 실내 클라이밍 루트의 해법(beta)을 생성하고
실제 벽 위에 시각화하는 모바일 애플리케이션이다.

사용자가 일반 iPhone 카메라로 수직 클라이밍 벽을 촬영하면 시스템은
홀드를 instance segmentation으로 검출하고 색상 및 위치 정보를 추출한다.
이후 색상, 공간적 위치, 연결 가능성을 이용하여 홀드들을 루트 단위로
그룹화하고 스타트/탑 홀드를 추정한다.

선택된 루트는 구조화된 데이터로 변환된다. Candidate Generator가 현재
신체 상태를 기준으로 다음에 접근 가능한 홀드를 계산하고, VLM은 벽
이미지와 구조화된 JSON을 함께 입력받아 왼손·오른손·왼발·오른발의 목표
홀드로 구성된 고수준 포즈 시퀀스를 생성한다.

각 포즈는 Unity ML-Agents 기반 저수준 제어기가 수행한다. 에이전트는
ragdoll humanoid이며 관절 제어와 limb별 grasp/release 행동을 통해 목표
포즈에 도달한다. 최종 애니메이션은 AR Foundation을 통해 실제 클라이밍 벽
위에 오버레이된다.

------------------------------------------------------------------------

## 2. 핵심 목표

1.  실제 클라이밍 벽 영상에서 홀드를 안정적으로 분할한다.
2.  홀드 색상, 공간 관계, 연결 가능성을 이용해 실제 루트를 자동
    추출한다.
3.  일반 iPhone에서도 수직 벽의 AR 평면을 인식하고 검출된 홀드를 월드
    좌표에 배치한다.
4.  VLM이 가능한 홀드 후보를 기반으로 현실적인 고수준 등반 계획을
    생성한다.
5.  ML-Agents가 목표 포즈를 물리적으로 안정적인 관절 움직임으로
    변환한다.
6.  생성된 등반 애니메이션을 실제 벽과 정렬하여 AR로 제공한다.

## 3. 핵심 가정 및 범위

-   벽은 하나의 **수직 평면**으로 근사한다.
-   LiDAR가 없는 일반 iPhone을 기준으로 한다.
-   홀드는 형태와 종류를 구분하지 않는 **단일 클래스**이다.
-   홀드별로 위치, segmentation mask, 색상 등의 정보만 유지한다.
-   grasp는 손/발이 홀드의 특정 부분을 잡는 물리적 접촉 모델이 아니라
    **limb을 홀드 위치에 고정하는 추상화**이다.
-   손의 방향, grip type, 홀드 표면 형상 등은 고려하지 않는다.
-   VLM은 고수준의 limb-to-hold 계획을 담당하고 RL은 저수준 물리 제어를
    담당한다.
-   초기 RL 학습은 실제 루트가 아니라 랜덤 벽과 랜덤 목표 포즈를
    사용한다.

------------------------------------------------------------------------

## 4. 전체 시스템 파이프라인

``` text
iPhone Camera / AR Session
        │
        ▼
[1] Hold Instance Segmentation
        │
        ├─ mask
        ├─ center
        └─ color
        ▼
[2] Route Extraction
        │
        ├─ color similarity
        ├─ spatial relation
        ├─ reachability/connectivity
        ├─ start hold
        └─ top hold
        ▼
[3] Wall & Coordinate Reconstruction
        │
        ├─ AR Plane Detection
        ├─ image → plane projection
        └─ hold world coordinates
        ▼
[4] Candidate Generator
        │
        ├─ body state
        ├─ current contacts
        └─ reachable holds per limb
        ▼
[5] VLM High-Level Planner
        │
        ├─ wall image
        ├─ structured JSON
        └─ candidate holds
        ▼
    Pose Sequence
        │
        ▼
[6] ML-Agents Low-Level Controller
        │
        ├─ joint actions
        ├─ grasp/release actions
        └─ physics simulation
        ▼
    Agent Animation
        │
        ▼
[7] AR Visualization
        │
        └─ real wall overlay
```

## 5. 모듈 간 핵심 데이터

### Hold

``` json
{
  "id": 12,
  "color": "red",
  "image_center": [0.43, 0.28],
  "wall_position": [0.72, 1.84],
  "mask": "segmentation-data"
}
```

`wall_position`은 수직 평면의 로컬 2D 좌표 `(x, y)`를 기본 표현으로
사용한다. 필요 시 AR 월드 좌표로 변환한다.

### Route

``` json
{
  "route_id": 3,
  "hold_ids": [1, 4, 7, 9, 12],
  "start_hold_ids": [1, 4],
  "top_hold_id": 12
}
```

### Pose

``` json
{
  "left_hand": 7,
  "right_hand": 9,
  "left_foot": 1,
  "right_foot": 4
}
```

### VLM Action

``` json
{
  "moving_limb": "left_hand",
  "target_hold_id": 12
}
```

------------------------------------------------------------------------

## 6. 아키텍처 원칙

### 계층형 Motion Planning

전체 동작 계획을 한 모델이 직접 해결하지 않는다.

``` text
Candidate Generator
        ↓
VLM High-Level Planner
        ↓
Target Pose
        ↓
RL Low-Level Controller
        ↓
Joint Motion
```

이 구조를 통해 VLM은 의미적·공간적 의사결정에 집중하고 RL은 물리적
안정성과 제어에 집중한다.

### Perception과 Planning 분리

CV의 출력은 모델 내부 표현이 아니라 명시적인 `Hold/Route JSON`으로
변환한다. 이를 통해 CV 모델 변경이 VLM/RL 시스템에 직접 영향을 주지
않도록 한다.

### 실제 환경과 학습 환경 분리

RL은 실제 CV 결과에 의존하지 않고 랜덤 환경에서 먼저 일반적인 climbing
controller를 학습한다. 실제 루트는 추론 단계에서 target pose로 전달한다.

------------------------------------------------------------------------

## 7. 개발 단계

핵심 불확실성이 큰 순서로 진행한다. RL과 VLM은 실제 CV/AR 출력 없이도
**랜덤 벽 생성기와 합성 Scene JSON**만으로 개발할 수 있으므로 먼저
검증하고, Perception과 AR은 이후에 동일한 인터페이스로 교체 투입한다.

### Phase 1 --- RL Controller

-   ragdoll humanoid 구성
-   랜덤 벽 생성기
-   랜덤 target pose 생성
-   grasp/release 구현
-   target pose 도달 학습
-   안정적인 연속 pose transition 학습

### Phase 2 --- VLM Planner

-   합성 Scene JSON(수작업/생성된 hold·route) 기반 개발
-   candidate generator 구현
-   VLM 입력 JSON schema 정의
-   structured output 정의
-   단일 move 생성
-   multi-step pose sequence 생성
-   실패 시 re-planning
-   VLM → RL 연결 및 plan-execute-replan 루프 검증

### Phase 3 --- Perception

-   Hold segmentation 데이터셋 구축
-   홀드 단일 클래스 instance segmentation
-   색상 추출
-   route clustering
-   start/top hold 추론
-   합성 Scene JSON을 실제 CV 출력으로 교체

### Phase 4 --- AR Reconstruction

-   vertical plane detection
-   벽 기준 좌표계 정의
-   segmentation 결과를 AR plane으로 투영
-   실제 홀드와 가상 홀드 정렬

### Phase 5 --- Integration

-   실제 route → candidate generator → VLM → RL 연결
-   animation recording/playback
-   AR overlay
-   end-to-end 평가

------------------------------------------------------------------------

## 8. 주요 위험 요소

  -----------------------------------------------------------------------
  위험                    수준                    대응
  ----------------------- ----------------------- -----------------------
  작은/가려진 홀드        중                      데이터 증강, 고해상도
  segmentation 실패                               입력, confidence
                                                  filtering

  조명에 따른 색상 오분류 중                      HSV/Lab 기반 색상 특징,
                                                  주변 조명 정규화

  같은 색의 복수 루트     높음                    공간 연결 그래프와
  혼합                                            reachability 결합

  start/top 자동 판별     중                      규칙 + confidence +
  오류                                            사용자 수정 UI

  monocular AR 정렬 오차  높음                    plane anchor 고정, 초기
                                                  scan, 지속적 tracking

  VLM의 불가능한 move     높음                    candidate generator로
  생성                                            action space 제한

  VLM 장기 계획 오류      높음                    step-wise planning 및
                                                  re-planning

  RL이 목표 pose에        매우 높음               curriculum,
  도달하지 못함                                   randomization, dense
                                                  reward

  비현실적 ragdoll 동작   높음                    joint limit, torque
                                                  제한, 안정성 reward

  grasp 추상화 악용       높음                    proximity/action mask와
                                                  contact 조건

  sim-to-real motion 차이 높음                    결과를 정확한 생체역학
                                                  해법보다 가이드로 정의

  전체 파이프라인 오류    매우 높음               각 모듈 confidence 및
  누적                                            fallback 설계
  -----------------------------------------------------------------------

------------------------------------------------------------------------

## 9. 성공 기준

프로젝트 성공 여부를 단일 end-to-end 정확도로 평가하지 않고 모듈별로
측정한다.

-   CV: hold mask detection 성능
-   Route: 홀드 그룹 정확도 및 start/top 정확도
-   AR: 실제 홀드와 overlay 사이 위치 오차
-   VLM: valid candidate 선택률, route completion 계획 성공률
-   RL: target pose success rate, fall rate, pose transition 안정성
-   End-to-End: 실제 루트 입력 후 완등 애니메이션 생성 성공률

------------------------------------------------------------------------

## 10. 프로젝트의 핵심 연구 질문

> 실제 클라이밍 벽에서 인식한 제한된 공간 정보를 이용하여, VLM의 고수준
> 추론과 강화학습 기반 물리 제어를 결합해 실행 가능한 등반 동작 시퀀스를
> 생성할 수 있는가?

이 질문을 중심으로 CV는 **Perception**, Candidate Generator + VLM은
**Planning**, ML-Agents는 **Control**, AR은
**Visualization/Interaction** 역할을 담당한다.
