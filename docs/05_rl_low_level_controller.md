# 모듈 5 --- ML-Agents Low-Level Climbing Controller

## 목적

VLM이 지정한 target pose를 Unity 물리 환경에서 실제 ragdoll 관절
움직임으로 수행한다.

## 기본 구조

``` text
Target Pose
    ↓
ML-Agents Policy
    ↓
Joint Motion + Grasp/Release
    ↓
Ragdoll Physics
    ↓
Target Pose Reached
```

## Agent

Humanoid ragdoll: - torso - head - upper/lower arms - upper/lower legs -
hands/feet에 해당하는 limb endpoint

Unity ML-Agents의 articulated body 제어 방식과 관절 관련
observation/action 구조를 최대한 활용한다.

### 구현

ML-Agents `Walker` 예제의 ragdoll을 그대로 가져와 클라이밍용으로
전용한다 (`Assets/02Ragdoll/ClimberRagdoll.prefab`).

-   16 body part: hips, spine, chest, head, upper/lower arm L·R, hand
    L·R, thigh/shin/foot L·R
-   `ConfigurableJoint` + `JointDriveController`/`BodyPart`
    (`Assets/01Scripts/Ragdoll/`, ML-Agents 예제에서 복사)
-   손목 관절은 전부 locked이므로 hand는 forearm에 고정된 grasp
    endpoint로 그대로 쓴다
-   보행 전용 요소(WalkerAgent, ModelOverrider, DecisionRequester,
    FootRays, DirectionIndicator, OrientationCube)는 제거했다.
    DecisionRequester는 `Agent`를 요구하므로 controller 작성 시 다시
    붙인다.
-   `GroundContact`가 쓰는 `"ground"` 태그를 프로젝트에 추가했다. 추락
    판정에 그대로 쓴다.

### 물리 설정 (필수)

이 ragdoll은 Unity 기본 물리 설정으로는 관절이 늘어나 무너진다.
프로젝트 설정을 ml-agents 쪽과 맞췄다.

-   `Physics.defaultSolverIterations` 12 (기본 6)
-   `Physics.defaultSolverVelocityIterations` 12 (기본 1)

중력은 실제값(-9.81)을 쓴다. Walker 씬은 1.5배를 쓰지만 그건 보행을 덜
붕 뜨게 하려는 값이고, 클라이밍은 사람 동작의 타당성을 보는 쪽이 맞다.

컨트롤러가 없는 동안 ragdoll은 바닥에 주저앉는다. 정상이다. 이때
slerpDrive가 T자세로 복원하려 밀기 때문에 얇은 바닥은 다리가 뚫고
내려간다. 학습 씬 바닥은 두껍게(2 m) 둔다.

관절 가동범위는 Walker 값을 그대로 유지한다. 어깨 (-60°\~120° / ±100°),
팔꿈치 (0°\~160°)는 머리 위 리치에 충분하지만, 고관절 외전 (±40°)은
클라이밍 자세에는 좁을 수 있다. 학습 결과를 보고 조정한다.

## Observation

예시:

### Body

-   각 body part position/rotation
-   joint rotation
-   angular velocity
-   linear velocity
-   root orientation

### Contact

-   각 limb가 현재 grasp 중인지
-   grasp 중인 hold ID 또는 target-relative feature

### Target

-   각 limb의 target hold 상대 위치
-   target pose와 현재 pose 차이

### Stability

-   center/root velocity
-   torso orientation
-   필요 시 support/contact 정보

## Action Space

두 종류를 결합한다.

### 1. Joint Control

ML-Agents의 joint control action을 사용하여 목표 관절 회전/힘을
결정한다.

### 2. Discrete Grasp Actions

각 limb:

``` text
0 = no change
1 = grasp
2 = release
```

필요에 따라 MultiDiscrete branch로 구성한다.

## Grasp 모델

실제 손가락/마찰을 simulation하지 않는다.

`grasp` 성공 시:

> 해당 limb endpoint를 target hold 위치에 물리적으로 고정한다.

구현: `ClimberRagdoll.Grasp/Release`
(`Assets/01Scripts/Ragdoll/ClimberRagdoll.cs`)

-   limb endpoint에 `FixedJoint`를 붙이고 `connectedBody = null`로 두어
    월드에 고정한다. 홀드는 static geometry이므로 Rigidbody가 없다.
-   고정 위치는 **홀드 중심이 아니라 limb의 현재 위치**다. 순간이동으로
    인한 물리 pop을 피하기 위한 선택이고, grasp radius가 작으므로 오차는
    그 범위 안에 머문다.
-   release는 `DestroyImmediate`로 즉시 제거한다. 다음 physics step까지
    남으면 episode reset과 충돌한다.
-   `Grasp(limb, hold)`는 어느 홀드를 잡았는지 기억한다. 클리어 판정에
    필요하다.

측정: 중간 홀드를 한 손으로 잡고 2초간 매달렸을 때 손 드리프트
0.026 m, 발은 공중 1.93 m. `FixedJoint`가 체중을 버틴다.

## 수동 조작 도구 (테스트 전용)

`ManualClimberControl` (`Assets/01Scripts/Testing/`)

Q/W/A/S로 limb을 고르고 마우스로 조준해 해당 limb을 끌어당긴다. 좌클릭
grasp, 우클릭 release, R 리셋.

**이건 agent의 action space가 아니고, 그렇게 만들면 안 된다.** limb에
raw force를 걸어 끌 뿐이라 `ClimbingAgent`가 내보낼 joint target과는
아무 관계가 없다. `Agent.Heuristic()`에 넣지 않고 별도 MonoBehaviour로
둔 이유다. Agent가 같은 ragdoll을 몰기 시작하면 이 컴포넌트는 꺼야
한다. 둘이 서로 싸운다.

## 클리어 판정

`ClimberRagdoll.IsToppedOut` --- **양손이 모두 `Top` 홀드를 잡고 있을
때** 참이다. 한 손만 top이거나, 한 손이 다른 홀드에 있으면 거짓이다.

발은 보지 않는다. 실제 클라이밍의 탑 판정(두 손으로 탑 홀드 유지)과
같고, 발까지 요구하면 불필요하게 어려워진다.

## Action Masking

grasp는 다음 조건에서만 허용한다.

``` text
distance(limb, target_hold) < grasp_threshold
```

즉 에이전트가 멀리 있는 홀드를 순간적으로 잡는 것을 막는다.

추가 조건 후보: - 해당 limb가 이미 grasp 중이면 다른 grasp 금지 - target
hold가 현재 target pose에 포함될 때만 허용 - release 가능한 상태 제한

`ClimberRagdoll.CanGrasp`가 거리 조건(`graspRadius`, 기본 0.2 m)과 "이미
grasp 중이면 금지"까지 구현한다. 나머지 조건은 controller 쪽에서
action mask로 건다. `graspRadius`는 홀드 크기에 맞춰 조정하는 값이다.

## Training Strategy - Curriculum learning

### Stage 1 --- Single Limb Target

-   균형 유지 및 관절 제어 안정화
-   다른 limb 고정하고 하나의 limb만 랜덤 target으로 이동

### Stage 2 --- Random Wall Climbing

-   랜덤 hold generator
-   위쪽 target pose 연속 제공
-   pose-to-pose transition 학습

### Stage 3 --- Planner Integration

-   VLM이 생성한 target pose 수행

## 벽과 홀드

`ClimbingWall` (`Assets/01Scripts/Wall/ClimbingWall.cs`)

-   transform은 벽의 **좌하단**에 둔다. +X 오른쪽, +Y 위, 등반자는 -Z
    쪽. `docs/07`의 좌표 규칙과 같다.
-   `WallToWorld(Vector2)`가 wall-local 2D(m)를 월드로 변환한다.
-   크기는 고정: 4.0 m × 6.0 m, 두께 0.3 m. 홀드 배치만 랜덤이다.
-   slab과 홀드는 `Generate`가 전부 만든다. 에디터에서 치수를 바꾼 뒤
    다시 생성하면 되고, 손으로 맞출 것이 없다.

`Hold` (`Assets/01Scripts/Wall/Hold.cs`)

-   `id` --- scene 안에서 유일. `docs/07`의 ID 규칙을 따른다.
-   `color` --- `MaterialPropertyBlock`으로 적용한다. 홀드마다 머티리얼
    인스턴스를 만들지 않기 위해서다.
-   `role` --- `Normal` / `Start` / `Top`.
-   `wallPosition` --- wall-local 2D 좌표.

## Random Wall Generator

`ClimbingWall.Generate(seed)`가 한 줄기 루트를 만든다. 아래에서 위로
`rowSpacing`(0.55 m)마다 홀드를 하나씩 놓고, 가로 위치만 랜덤하게
움직인다.

**클리어 가능성은 rejection sampling이 아니라 구조적으로 보장한다.**
연속한 두 홀드는 항상 세로로 `rowSpacing`만큼 떨어져 있으므로, 가로
이동을 `sqrt(maxReach² - rowSpacing²)`로 제한하면 두 홀드 사이 거리가
`maxReach`(0.9 m)를 넘을 수 없다. 벽 좌우 경계로 clamp하는 것은 가로
이동을 줄이기만 하므로 이 보장을 깨지 않는다.

`maxReach` 0.9 m는 ragdoll 기준값이다 --- 키 1.97 m, 팔 스팬 1.95 m,
hips에서 손까지 1.09 m.

첫 홀드는 `Start`, 마지막 홀드는 `Top`이 된다.

시드 200개로 검증했다: 벽마다 홀드 9개, 최대 연속 간격 0.899 m
(`maxReach` 0.9 m), 경계 이탈 0건, role 오류 0건.

밀도, 한 행에 여러 홀드, 좌우 분포 편향 등은 아직 없다. 학습이 이
난이도를 넘어선 뒤에 붙인다.

## Reward 설계

예시:

\[ R = w_p R\_{pose} +w_g R\_{grasp} +w_s R\_{stability} -w_e
P\_{energy} -w_f P\_{fall} \]

후보: - target limb와 hold 거리 감소 - target grasp 성공 - 전체 target
pose 완성 - torso 안정성 - 추락 penalty - 불필요한 joint velocity/torque
penalty - 시간 penalty

초기에는 dense reward로 학습시키고 점차 최종 pose 성공 비중을 높인다.

## Episode 종료

성공: - 모든 required limb가 target hold를 grasp - 일정 시간 안정적으로
pose 유지

실패: - 추락 - 제한 시간 초과 - 비정상 자세/환경 이탈

## 평가

-   Target pose success rate
-   평균 pose completion time
-   Fall rate
-   Grasp success rate
-   연속 N-pose 수행 성공률
-   unseen random wall generalization

## 가장 큰 위험

agent가 reward 또는 grasp abstraction을 악용해 사람이 보기에는
부자연스러운 동작을 만들 수 있다. joint limit, 힘 제한, velocity/energy
penalty와 animation 품질 평가가 필요하다.
