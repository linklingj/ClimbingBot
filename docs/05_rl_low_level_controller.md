# 모듈 5 --- ML-Agents Low-Level Climbing Controller

## 목적

VLM이 지정한 target pose를 Unity 물리 환경에서 실제 ragdoll 관절
움직임으로 수행한다.

``` text
Target Pose → ML-Agents Policy → Joint Motion + Grasp/Release
  → Ragdoll Physics → Target Pose Reached
```

## Agent

Humanoid ragdoll: torso, head, upper/lower arms, upper/lower legs,
hands/feet에 해당하는 limb endpoint.

### 구현

ML-Agents `Walker` 예제의 ragdoll을 클라이밍용으로 전용했다
(`Assets/02Ragdoll/ClimberRagdoll.prefab`).

-   16 body part: hips, spine, chest, head, upper/lower arm L·R, hand
    L·R, thigh/shin/foot L·R. `ConfigurableJoint` +
    `JointDriveController`/`BodyPart`(`Assets/01Scripts/Ragdoll/`,
    ML-Agents 예제에서 복사).
-   손목 관절은 전부 locked이므로 hand는 forearm에 고정된 grasp
    endpoint로 그대로 쓴다.
-   보행 전용 요소(WalkerAgent, ModelOverrider, DecisionRequester,
    FootRays, DirectionIndicator, OrientationCube)는 제거했다.
    DecisionRequester는 `Agent`를 요구하므로 controller 작성 시 다시 붙인다.
-   `GroundContact`가 쓰는 `"ground"` 태그를 프로젝트에 추가했다(추락 판정용).

### 물리 설정 (필수)

이 ragdoll은 Unity 기본 물리 설정으로는 관절이 늘어나 무너진다.
`Physics.defaultSolverIterations`/`defaultSolverVelocityIterations`를
**12/12**(기본 6/1)로 유지한다. 중력은 실제값(-9.81) --- Walker의
1.5배는 보행을 덜 붕 뜨게 하려는 값이라 클라이밍엔 안 맞는다.

컨트롤러가 없는 동안 ragdoll은 바닥에 주저앉는다(정상). slerpDrive가
T자세로 복원하려 밀어 얇은 바닥은 다리가 뚫으므로, 학습 씬 바닥은
두껍게(2 m) 둔다.

### 관절 가동범위

**부호 규칙 주의.** `ConfigurableJoint`의 low/high angular X limit은
`Quaternion.AngleAxis(θ, joint.axis)`와 **부호가 반대**다(실측 확인).
축 방향만 보고 해부학적 의미를 추측하면 굴곡/신전이 뒤집힌다. 또 PhysX
규약상 angularX가 twist, angularY/Z가 swing인데 이 ragdoll은 뼈 방향과
축이 안 맞아서, 고관절의 "twist"는 굴곡/신전이고 대퇴 축회전은 angZ다.

실측한 의미와 현재 값(좌측 기준, +Z가 벽 방향):

| 관절 | angX | angY | angZ |
|---|---|---|---|
| spine / chest | 굴곡·신전 ±20 | 측굴 ±20 / ±24 | **축회전 ±20** |
| 어깨 | 수평내전 +120 / 수평외전 -60 | 거상 ±100 | **상완 축회전 ±60** |
| 팔꿈치 | 굴곡 0~160 | Locked | 전완 회내외 ±60 |
| 고관절 | 굴곡 -125 / **신전 +20** | 외전·내전 ±50 | **대퇴 축회전 ±40** |
| 무릎 | 굴곡 0~120 | Locked | Locked |
| 발목 | ±20 | ±20 | ±20 |

Walker에서 바꾼 것(굵게 표시)과 이유:

-   **고관절 축회전 Locked → ±40.** 백스텝, 드롭니, 프로그 자세는 전부
    이 축을 쓴다. 보행은 시상면 운동이라 Walker는 잠가도 됐다.
-   **고관절 신전 60° → 20°.** 다리를 몸 뒤로 60°는 사람 범위의
    두세 배고 클라이밍에는 안 쓴다(다리는 항상 벽/몸 앞).
-   **몸통 축회전 ±15 → ±20** (spine+chest 합 ±30 → ±40). 어깨-골반을
    비틀어 엉덩이를 벽에 붙이는 동작이 클라이밍 기본이다.
-   **어깨 축회전 Locked → ±60.** 실측상 이 축을 열지 않으면 손이
    어깨보다 아래로만 뻗는다(머리 위 리치가 아예 불가능). 거상/수평외전
    확대는 효과가 없었다 --- 어깨가 2자유도인 것 자체가 원인이었다.
    사람 상완 축회전(내회전 70/외회전 90)보다 보수적인 값이다.

고관절·어깨가 늘어 action space도 x·y·z 3축 전부 필요(다리당 +1, 팔당
+1, 전체 +4). 팔꿈치 angZ ±60과 발목 ±20은 열려 있지만 grasp가 회전을
흡수해 효과가 없어 구동하지 않는다.

## Observation

**고정 크기다.** 홀드 개수는 벽마다 다르지만 관측 차원은 변하지
않는다("주변 홀드를 넣지 않는 이유" 참고). **기준 프레임은 벽이다** ---
클라이머는 항상 고정된 벽 하나를 마주 보므로 벽 자체가 이미 안정화
프레임이라 Walker의 `OrientationCube` 같은 대역 오브젝트가 필요 없다.

구현 크기 **256** (`ClimbingAgent` 순서):

| 블록 | 차원 |
|---|---|
| hips 회전(벽 기준), chest 회전(벽 기준), 평균 속도 | 4 + 4 + 3 |
| limb 4개 `isGrasping` | 4 |
| limb 4개 (목표 상대 위치 3 + 지시 플래그 1) | 16 |
| body part 16개 × (접지 1 + 속도 3 + 각속도 3 + hips 상대 위치 3) | 160 |
| 자세를 보고하는 관절 13개 × (localRotation 4 + 강도 1) | 65 |

16개 중 13개만 자세를 보고한다. hips는 관절이 없고, 손목 둘은 전 축
Locked라 localRotation이 상수(분산 0, `normalize: true`에서 무의미).
어느 부위가 용접인지는 하드코딩 대신 관절 상태로 검사해서 뺀다.

### Body / Contact / Target / Stability

-   **Body**: ML-Agents `Walker`의 body observation 그대로 --- body
    part별 hips 기준 상대 위치/회전, linear/angular velocity, hips
    orientation.
-   **Contact**: limb 4개 각각 `isGrasping`(1). 잡은 홀드 위치는 따로
    안 넣는다 --- grasp가 limb을 그 자리에 고정하므로 body 관측에 이미
    있다.
-   **Target**: limb 4개 각각 목표 지점 상대 위치(3, 해당 limb 기준) +
    이번 pose에서 옮기라고 지시된 limb인지(1) = 16차원. 목표는
    **limb endpoint 4개뿐**이고 16 body part 전체 pose가 아니다 ---
    VLM이 전체 pose를 뱉기는 너무 어렵고, 나머지 자세는 RL이 찾는다.
-   **Stability**: center of mass velocity, chest orientation(벽 기준).
    그립 하중(아래)은 아직 진단용으로만 쓰고 관측에는 없다.

### 주변 홀드를 넣지 않는 이유

이 모듈이 배우는 것은 "끝까지 올라가라"가 아니라 "이 limb을 저
지점으로 옮겨라"다. 어느 홀드로 갈지는 VLM planner가 정한다(`docs/07`의
`PlannerAction / PoseSequence`).

-   목표는 "홀드"가 아니라 **3D 점**이다. 홀드 개수가 벽마다 달라도
    목표 개수는 limb 개수로 고정이라 관측 크기가 안 변한다.
-   절대 wall-local 좌표 대신 limb 기준 **상대 좌표**를 쓴다 ---
    평행이동 불변이라 같은 국소 상황이 같은 관측이 되고, 그게 랜덤 벽
    일반화의 전부다.
-   주변 홀드가 필요한 곳은 grasp action **masking** 하나뿐인데 그건
    `CanGrasp`가 코드에서 계산한다. 정책이 볼 관측이 아니다.

target 슬롯을 4개(단일 limb 이동인데도) 두는 이유는 커리큘럼 때문이다
--- Stage 1은 한 슬롯만 다르고 나머지는 "유지"다. 슬롯을 1개로 줄이면
Stage 3에서 shape이 바뀌어 앞 단계 가중치를 못 이어쓴다. 기각한 대안:
홀드 ID 임베딩(벽마다 ID 의미가 달라 일반화 불가), 2D 그리드(CNN 필요,
Phase 1에는 과함).

## Action Space

### 1. Joint Control

`BodyPart.SetJointTargetRotation`에 축별 목표를 정규화된 [-1, 1]로
넘긴다. Locked 축은 limit이 0이라 값을 줘도 사라지므로 열린 축에만 준다.

구현: 회전 **30** + 관절 강도 **13** = **연속 43개**.

| 관절 | 회전 축 수 |
|---|---|
| spine, chest | 3, 3 |
| head | 2 (angZ Locked) |
| 어깨 L·R | 3씩 --- 축회전 열림 |
| 팔꿈치 L·R | 1씩 |
| 고관절 L·R | 3씩 --- 축회전 열림 |
| 무릎 L·R | 1씩 |
| 발목 L·R | 3씩 |

팔꿈치 angZ는 열려 있지만 목표를 항상 0으로 줘 구동하지 않는다(위
"일부러 두고 온 것"). 손목은 전 축 Locked라 회전/강도 모두 없음 ---
강도 13개는 실제로 구동하는 관절 수와 같다.

### 2. Discrete Grasp Actions

각 limb: `0 = no change`, `1 = grasp`, `2 = release`.

**어느 홀드를 잡을지는 action이 고르지 않는다.** 해당 limb에 지정된
target 홀드를 잡는다 --- 홀드 선택은 planner의 일, 이 모듈은 "지금
잡을지"만 정한다. 언제 놓고 잡을지는 스크립트로 박지 않고 학습 대상으로
둔다("충분히 안정됐을 때 놓는다"가 클라이밍 스킬의 핵심이라서).

구현: **branch 4개 × 크기 3**. `WriteDiscreteActionMask`가 grasp는
`CanGrasp`(지정 홀드까지 `graspRadius` 이내)일 때만, release는 지금
잡고 있을 때만 허용한다.

**Stage 1은 나머지 세 limb의 branch를 통째로 "no change"로 막는다**
(`ClimbingAgent.lockSupportLimbs`). 끄면 Stage 2 동작(전부 자유)이 된다.

## Grasp 모델

실제 손가락/마찰을 simulation하지 않는다. grasp 성공 시 해당 limb
endpoint를 target hold 위치에 물리적으로 고정한다.

구현: `ClimberRagdoll.Grasp/Release` (`Assets/01Scripts/Ragdoll/ClimberRagdoll.cs`)

-   limb endpoint에 `ConfigurableJoint`를 붙이고 `connectedBody = null`로
    월드에 고정(홀드는 static geometry, Rigidbody 없음).
-   **선형 3축만 잠그고 각도는 푼다** --- 손이 홀드 위에서 회전할 수
    있어야 "limb을 위치에 고정"이라는 정의에 맞다(`FixedJoint`로 방향
    까지 용접하면 안 됨). 고정 위치는 홀드 중심이 아니라 **limb의
    현재 위치**다(순간이동 pop 방지, grasp radius가 작아 오차는 범위 안).
-   release는 `DestroyImmediate`로 즉시 제거(다음 physics step까지
    남으면 episode reset과 충돌). `Grasp(limb, hold)`는 어느 홀드를
    잡았는지 기억한다(클리어 판정용).

실측: 한 손 매달리기 손 드리프트 0.046 m --- 선형 잠금만으로 체중을 버틴다.

## 그립 하중 (자세 평가 지표)

`ClimberRagdoll.GripLoad(limb)` --- 그 limb이 버티는 힘을 **체중
배수(BW)**로 준다. `StabilityScore`는 이를 종합한 [0, 1] 값. grasp
조인트는 힘 제한 없는 위치 고정이라 **잡고 있는 한 물리적으로
추락하지 않는다** --- 이 지표가 곧 빠져 있는 손 힘 모델이라, 몸통
자세가 아니라 **그립**에 붙인다.

**측정 위치가 중요하다.** grasp 조인트가 아니라 **limb 자신의
관절**(손목, 발목)의 `currentForce`를 읽는다 --- grasp 조인트는 앵커에서
떨어진 지점에서 고정돼 솔버 보고값이 부풀려진다(실측 손목 1.07 BW ≈
체중, 같은 순간 grasp 조인트 7.8 BW).

`handCapacity`/`footCapacity`(BW)는 캘리브레이션 노브(기본값: 손 1.0 ---
강한 클라이머의 한 팔 매달리기, 발 2.0).

``` text
StabilityScore = 1 - max_over_grasping_limbs(GripLoad / Capacity)   (0~1 clamp)
아무것도 잡고 있지 않으면 0
```

최솟값이 아니라 **최악 그립**이 점수를 정한다(평균은 한 손이 전부 지고
있는 상태를 가린다). `ClimbingAgent.gripLoadPenalty`가 이 값을
읽는다("Reward 설계" 참고) --- `stage1-04`까지는 진단용이었다.

## 수동 조작 도구 (테스트 전용)

`ManualClimberControl` (`Assets/01Scripts/Testing/`) --- Q/W/A/S로 limb
선택, 마우스로 조준·끌기. 좌클릭 grasp, 우클릭 release, R 리셋.

**이건 agent의 action space가 아니고, 그렇게 만들면 안 된다.** limb에
raw force를 걸 뿐이라 `ClimbingAgent`의 joint target과 무관하다.
`Agent.Heuristic()`이 아닌 별도 MonoBehaviour다. Agent가 같은 ragdoll을
몰기 시작하면 이 컴포넌트는 꺼야 한다 --- 둘이 서로 싸운다.

## 에피소드 시작 자세 / 클리어 판정

`ClimberRagdoll.ResetOnHold(hold)` --- 리셋하면 **양손이 start 홀드를
잡은 상태**로 시작한다. 서서 안 닿는 높이면 매달리고, 닿으면 뒤로
물러나 선다(후자가 없으면 낮은 start 홀드에서 발이 바닥에 파묻힌다).
아직 관절을 지시하는 주체가 없어 slerpDrive가 T자세로 당기므로, 시작
후 손이 15 cm 정도 끌려가 평형에 멈춘다(controller가 joint target을
잡으면 줄어드는 값).

`ClimberRagdoll.IsToppedOut` --- **양손이 모두 `Top` 홀드를 잡고 있을
때** 참. 발은 보지 않는다(실제 클라이밍의 탑 판정과 동일).

## Training Strategy --- Curriculum learning

| Stage | 목표 | 벽 |
|---|---|---|
| 1 --- Single Limb Target | 균형 유지·관절 제어 안정화. 다른 limb 고정, 하나만 랜덤 target으로 | `ScatteredWallGenerator`(루트 없음). 종료는 탑아웃이 아니라 목표 홀드 grasp |
| 2 --- Random Wall Climbing | pose-to-pose transition 학습, target pose 연속 제공 | `RandomWallGenerator`(spline 루트) |
| 3 --- Planner Integration | VLM이 생성한 target pose 수행 | 실제 CV 출력 |

## 벽과 홀드

`ClimbingWall` (`Assets/01Scripts/Wall/ClimbingWall.cs`)

-   transform은 벽의 **좌하단**. +X 오른쪽, +Y 위, 등반자는 -Z 쪽
    (`docs/07` 좌표 규칙과 동일).
-   `WallToWorld(Vector2)`가 wall-local 2D(m)를 월드로 변환.
-   크기 고정: 4.0 m × 6.0 m, 두께 0.3 m. `BuildSlab()`이 치수대로
    slab을 만든다(진실의 출처를 하나로 두기 위해 씬에 손으로 안 놓음).
-   `AddHold(id, wallPosition)` / `ClearHolds()`만 제공한다 --- **홀드
    배치는 벽이 정하지 않는다.** Phase 1은 랜덤 생성기, 이후엔 CV 출력.
-   홀드는 `holdPrefab`(`Assets/03Prefabs/Hold_0.prefab`)을 찍어낸다.
-   `AddHold`/`ClearHolds`는 `ObjectPoolManager`(`Util/`)가 있으면 그걸
    쓴다 --- Stage 1은 에피소드마다 홀드 ~70개를 통째로 부수고 다시
    찍어 벽시계의 상당 부분을 여기서 썼다. 매니저가 없는 씬은 그냥
    `Instantiate`/`DestroyImmediate`로 떨어진다. `ClearHolds`는 활성
    오브젝트만 훑는다(비활성까지 훑으면 같은 `PoolObject`가 중복
    반환돼 풀 큐가 오염된다). `Train.unity`의 풀 크기는 96.
-   **풀은 play mode에서만 쓴다.** 매니저는 `Start()`에서 풀을 채우므로
    에디트 모드(생성기의 인스펙터 버튼)에는 꺼낼 게 없다. 물어봐야 홀드마다
    "Pool not found"가 한 줄씩 찍히기만 해서 `AddHold`가 아예 안 묻는다.
    `_poolDictionary`도 `Awake()`가 아니라 선언부에서 만든다 --- 에디트
    모드에서는 `Awake`가 안 도는데 `Singleton.Instance`는 씬의 매니저를
    그대로 찾아주므로, `Get`/`Release`가 null 딕셔너리를 밟고 터졌다.

`Hold` (`Assets/01Scripts/Wall/Hold.cs`)

-   `id` --- scene 안에서 유일(`docs/07`의 ID 규칙)
-   `color` --- `MaterialPropertyBlock`으로 적용(머티리얼 인스턴스 방지)
-   `role` --- `Normal` / `Start` / `Top`
-   `wallPosition` --- wall-local 2D 좌표

## Wall Generator (테스트 전용)

생성기는 **두 개**고 학습 단계에 따라 고른다. 둘 다 `IWallGenerator`
(`Assets/01Scripts/Wall/IWallGenerator.cs`)를 구현하고 벽 GameObject에
같이 붙인다 --- **활성화된 쪽이 선택된 것**이다(mode enum 없음).

| 생성기 | 벽 모양 | 쓰는 곳 |
|---|---|---|
| `ScatteredWallGenerator` | 벽 전체에 홀드가 흩뿌려짐, 루트 없음 | Stage 1 |
| `RandomWallGenerator` | spline 한 줄기를 따라가는 루트 | Stage 2 |

둘 다 **버릴 코드다** --- 최종 시스템의 홀드는 CV 출력에서 온다.
`ClimbingWall`의 공개 API만 쓰는 별도 컴포넌트로 뺐다.

### ScatteredWallGenerator (Stage 1)

정해진 자세에서 시작해 limb 하나와 목표 홀드 하나를 받으므로, 홀드가
climbable한 줄기를 이룰 필요가 없고 오히려 climber 주변 **모든 방향에
후보 타깃이 있어야** 한다(루트가 있으면 방해됨).

dart throwing으로 뿌린다 --- `margin` 안쪽에 점을 찍고 기존 점과
`minSeparation`보다 가까우면 버린다(시도 상한으로 종료 보장). Poisson-disk
정식 알고리즘은 이 개수에 과하다.

`minSeparation`은 limb 리치 안에 홀드가 몇 개 들어오는지를 정한다 ---
"도달 가능한 타깃이 존재하는가"라서 Stage 1의 가장 민감한 노브다.
0.38 m에서 네 limb 모두 후보를 갖는다(실측: holdCount 70개 전부 배치
성공, 벽 위 어디서든 가장 가까운 홀드까지 평균 0.469 m).

`Generate(seed, anchors)`는 주어진 좌표(시작 자세의 손발 위치)에 홀드를
먼저 무조건 놓는다. 앵커끼리는 `minSeparation`을 적용하지 않는다(두
발은 산포 쌍보다 가깝게 붙는다).

**role은 전부 `Normal`**이다. start/top이 없으므로 `ClimbingWall.TopHold`는
null, 탑아웃은 종료 조건이 아니다.

### RandomWallGenerator (Stage 2)

`RandomWallGenerator` (`Assets/01Scripts/Testing/`) --- `Generate(seed)`가
루트를 spline으로 그린다: top 홀드 x는 랜덤(높이 `topHoldY` 고정),
knot 2~4개짜리 spline이 아래 끝 `splineBottomY`(0.5 m)에서 top 홀드까지
이어진다. start 홀드 x는 spline이 `startHoldY`를 지나는 지점에서
읽는다 --- **곡선 위에** 있다(루트 옆이 아님). `SplineInstantiate`가
그 위에 홀드를 배치(간격 `minHoldSpacing`~`maxHoldSpacing` + x 랜덤
오프셋)하면 위치만 읽어 벽의 진짜 홀드로 굽고 아래에서 위로 id를 매긴다.

start/top은 높이만 고정, x는 벽마다 달라진다. **start 홀드는 가장 아래
홀드가 아니다** --- spline이 `startHoldY` 밑까지 내려온다.

`SplineInstantiate`의 인스턴스는 `HideAndDontSave`라 씬에 저장되지도
id/role/color를 유지하지도 못해서 위치만 받아 쓰고 버린다.

**간격 보장.** start/top이 지정 높이라 이웃과 멀어질 수 있는 남은
간격은 중점에 홀드를 끼워 반으로 접는다 --- 이후 모든 연속 간격이
`maxReach`(1.4 m, ragdoll 팔 스팬 기준) 이하다.

**발 홀드 2차 패스.** 홀드가 한 줄뿐이면 손과 발이 같은 홀드를 두고
경쟁하고 대개 발이 진다. 그래서 같은 spline을 `footDropY`(1.0 m)만큼
내려 한 번 더 굽는다. 곡선을 평행이동해도 모양과 호 길이는 그대로라
다시 굽고 y를 빼는 것이 곧 내려간 spline이다. 시드를 바꿔 간격과
jitter를 새로 뽑으므로 손 줄의 사다리를 그대로 복사하지 않는다.

- 이미 놓인 홀드에서 `minHoldSpacing` 안에 들어오면 건너뛴다. 직전
  패스뿐 아니라 이 패스가 놓은 것과도 비교하므로 스스로도 뭉치지 않는다.
- 내려간 곡선의 아래쪽은 바닥을 파고들므로 `holdRadius` 밑은 버린다.
- **`BridgeGaps` 다음에 돈다.** 이 홀드들은 손 줄 옆에 있지 손 줄의
  고리가 아니다. 높이만 사이에 낀 발 홀드를 이웃으로 보고 중점을 끼우면
  두 줄 사이 허공에 홀드가 생긴다.

**Scene JSON 익스포트.** 이 생성기가 프로젝트에 하나뿐인 루트 생성기다.
`exportCount`개(기본 20)를 시드 0부터 만들어 `/walls/wall_NNN.json`으로
내보낸다 --- 인스펙터의 "Export walls". 파이썬 planner(`src/vlm`)는 알고리즘을
복사해 갖는 대신 이 파일들을 읽는다.

-   `Hold.role`을 route의 `start_hold_ids`/`top_hold_id`로 바꿔 쓴다.
    docs/07이 Phase 2부터 JSON을 정본으로 두기 때문이다.
-   `color`는 RGB가 아니라 role에서 나온 이름(`green`/`red`/`white`)이다.
    planner의 렌더러가 그 이름으로 칠한다.
-   좌표는 소수점 3자리 고정, `InvariantCulture`. 커밋되는 파일이라 같은
    시드가 어느 기계에서든 같은 바이트여야 한다(실측: 재익스포트 시 해시 동일).

**벽 모양에 영향을 주는 값을 바꾸면 다시 내보내 커밋한다.** 안 하면 파이썬
쪽은 옛 벽으로 계속 평가한다.

**y축 오프셋의 조건 (실측).** y로 내리는 것은 수직에 가까운 루트에서
곡선을 자기 자신을 따라 미끄러뜨리는 것과 같아서, 새 위치가 중복으로
걸러질 수 있다. 실제로 걸러지느냐는 **드롭과 건너뛰기 반경의 비**에
달렸다 --- `src/vlm`에서 간격 0.35\~0.6 m / 반경 0.45 m로 재보니 발 홀드
수가 루트의 x 이동폭에 그대로 비례하고 수직 루트에서는 0개였지만, 현재
값(간격 0.3\~0.8 m / 반경 0.3 m / 드롭 1.0 m)에서는 20개 시드 전부
2\~7개가 남았다. **이 셋 중 하나를 건드리면 다시 재야 한다.**

곡선의 **법선** 방향 오프셋이면 루트 기울기와 무관하게 평행한 줄이 나온다.
현재 값에서 재보면 법선 0.6\~0.8 m가 발 후보 10\~11/20으로 y축 1.0 m의
9/20보다 약간 낫지만 완등 수는 사실상 같고(17\~18 대 18), 법선을 1.0 m
이상 밀면 줄이 루트에서 너무 떨어져 완등이 7/20으로 무너진다. **지금
수치에서는 바꿀 이유가 없다.**

## Stage 1 환경

`Stage1Environment` (`Assets/01Scripts/Training/Stage1Environment.cs`) ---
에피소드가 어떻게 생겼는지를 정한다(벽, 시작 자세, 어느 limb→어느
홀드). **관측·보상·행동은 여기 없다**(그건 `ClimbingAgent`의 몫).

디버그 뷰: 목표 홀드는 주황(`targetHoldColor`), 성공 시 벽 slab이 잠깐
초록(`successFlashColor`, 0.5초). 색은 관측에 안 들어간다.

`ResetEpisode(seed)` 한 번이 에피소드 하나를 만든다. 목표를 못 찾으면
false를 돌려주고 호출자가 다른 시드로 재시도한다.

### 자세가 먼저, 홀드가 나중

홀드 4개를 먼저 고르고 닿는 자세를 푸는 것보다 **자세를 잡고 각 limb
밑에 홀드를 놓는 쪽이 쉽다**(Phase 1 벽은 합성이라 벽을 몸에 맞춰도
잃을 게 없다). limb 배치는 두 뼈짜리 CCD로 푼다(해석적 2링크 IK보다
쉽고 뼈 방향에 무관). 관절 limit은 안 본다 --- 오프셋이 해부학적으로
평범해서 해가 항상 limit 안에 떨어진다.

기본 시작 자세(힙 기준, 벽면 좌표, m): 손 `(±0.34, +0.42)`, 발
`(±0.26, −0.58)`, 힙은 벽에서 0.26 m.

**손은 어깨 높이 근처까지만 올린다.** 머리 위로 올리면(오프셋 y 1.0)
팔이 리치의 75%까지 뻗어 손이 legal target을 하나도 못 갖는다(이미
도달 구의 위쪽 끝이라 더 위는 리치 밖). 팔을 굽혀 둬야 위로 뻗을 여지가
생긴다.

### 타깃 선택

limb을 무작위 순서로 훑어 후보가 있는 첫 limb을 쓴다. 후보 조건:

-   아무도 안 잡고 있는 홀드
-   `belowTolerance`(0.20 m)보다 더 아래면 제외 --- 위 아니면 좌우
-   limb에서 `minStep`(0.30 m) 이상(안 그러면 움직일 필요가 없다)
-   **체인 뿌리(어깨/고관절)에서 `ChainReach × reachFraction` 이내**
    (`ChainReach`는 두 뼈를 완전히 폈을 때의 거리 --- 지금 굽은 길이로
    재면 심하게 과소평가된다)

실측(시드 150개): 도달 가능한 타깃이 없는 에피소드 0건, 네 limb 전부
grasp 150/150, 타깃 limb 분포가 고르다.

## Reward 설계

`ClimbingAgent`가 **물리 스텝마다** 채점한다(shaping이 동작을 따라가야
하므로, Walker와 동일).

| 항목 | 기본값 | 언제 |
|---|---|---|
| 진행 | `+2.0 × (최근접 거리 − 현재 거리)`, 최근접 갱신 시만 | 매 스텝 |
| 성공 | `+1.0` | 지정 limb이 목표 홀드를 grasp → 종료 |
| 놓기 | `+0.1` | 지정 limb이 처음 손을 뗀 순간, 에피소드당 한 번 |
| 추락 | `−1.0` | hips가 시작 높이에서 `maxDrop`(1 m) 아래 → 종료 |
| 낙차 | `−0.02 × (낙차 − 0.4)`, `dropPenaltyThreshold`(0.4 m) 초과 시만 | 매 스텝 |
| 그립 하중 | `−0.01 × (GripLoad / Capacity − 1)`, 잡고 있는 limb마다 초과분 | 매 스텝 |
| 시간 | `−0.001` | 매 스텝 |
| 지지 상실 | `−0.01` | 매 스텝, 잡고 있지 않은 support limb마다 |
| 에너지 | `0`(꺼 둠) | 매 결정, 연속 action 제곱합 |

**진행 보상은 래칫이다 --- 가까워질 때만 주고, 멀어지는 건 공짜다.**
대칭이던 이전 버전은 벌어진 거리를 매번 청구해 "놓아 봤다가 실패하는
것이 아예 안 놓는 것보다 낮은 점수"가 됐다(`stage1-03`: 손은 놓는 순간
몸이 처져 거리가 먼저 벌어지는 탓에 2.4M 스텝 내내 0%였다). **놓기
보상 `+0.1`**은 한 발 더 간다 --- 래칫은 시도의 벌점만 없앨 뿐이라
"놓았지만 못 가까워진" 시도가 안 놓기와 동점이 되는데, 일회성
보너스가 시도를 확실히 위로 올린다(성공 +1.0보다 작아 그게 목표가 안
됨, 재grasp 불가라 반복 수령도 불가).

**에너지 패널티는 기본 0이다** --- 학습 초기에 넣으면 동작 탐색을
누른다. 지지 상실 패널티는 `lockSupportLimbs`가 켜진 Stage 1에서는
사실상 작동 안 한다(release가 마스크로 막힘) --- Stage 2부터 의미를 갖는다.

**낙차·그립 하중 패널티는 `stage1-04` 20M 스텝 평가 후 추가했다.**
그 전에는 성공률 100%에도 hips가 평균 0.51 m 처진 채 잡고 그립 하중이
capacity를 넘었다 --- grasp 조인트가 힘 제한 없는 위치 고정이라 "놓고
떨어지며 다음 홀드에 걸치는" 동작이 성공과 양립했기 때문이다. 둘 다
**문턱을 넘은 초과분에만** 매긴다(낙차는 `dropPenaltyThreshold` 0.4 m,
그립 하중은 `Capacity` 자체가 "정상 등반 하중" 기준) --- 매 스텝
청구라 초과 상태를 오래 유지할수록 값이 커진다.

## 학습 실행

``` bash
mlagents-learn config/climber.yaml --run-id=stage1-05
# 콘솔에 "Start training by pressing the Play button" 가 뜨면 에디터에서 Play
```

`config/climber.yaml`은 ml-agents의 `ppo/Walker.yaml`에서 출발했다.
`gamma` 0.995 → 0.99가 가장 근본적인 차이 --- 한 번의 limb 이동은 1~2초라
200결정을 거슬러 크레딧할 이유가 없다. 나머지 근거는 인라인 주석과
`worklog/`에 있다(옮기면 다음 튜닝에서 바로 stale해진다).

**보상을 바꾸고 이어서 학습시킬 때는 `--initialize-from`을 쓴다** (`--resume`
아님 --- `--resume`은 같은 run 로그에 이어 붙어 보상 함수가 바뀐 지점
전후가 섞인다):

``` bash
mlagents-learn config/climber.yaml --run-id=stage1-05 --initialize-from=stage1-04
```

**`Run In Background`가 켜져 있어야 한다** --- 꺼져 있으면 에디터가
포커스를 잃는 순간 플레이 루프가 통째로 멈춘다(academy step이 안 늘어남).

### 학습 씬 --- 16각형 링

`Train.unity`에는 `TrainingArea` 프리팹(벽 + 생성기 + `Stage1Environment` +
ragdoll/agent 한 벌)이 **16개** 들어 있다. 벽 폭 4 m 기준 정16각형
apothem `10.0547 m`으로 영역 *i*를 Y축 `i × 22.5°` 돌려 바깥으로 밀면
16개 면이 이어붙는 드럼이 된다(클라이머는 전부 링 바깥에 매달림).
병렬화가 공짜인 이유는 **관측이 벽 프레임 기준**이라 영역을 통째로
돌려도 agent가 보는 값이 안 변하기 때문이다.

-   `episodeSeed`는 영역마다 0~15로 다르게(같으면 샘플이 상관됨), 바닥은
    링 전체를 덮는 28×28 하나(영역별 바닥은 서로 겹침), `ManualControl`은
    꺼 둠(agent와 충돌). 에디터 렌더링 켠 채 40 fps, rigidbody 256개.
    하이퍼파라미터는 Walker 기준값(병렬 10환경 가정) 그대로 ---
    `buffer_size`가 빨리 차면 올린다.

## ClimbingAgent 구성

| | |
|---|---|
| Behavior name | `Climber` |
| Vector observation | 256, stack 1 |
| Continuous actions | 43 |
| Discrete branches | 3, 3, 3, 3 |
| `DecisionRequester` | period 5 (0.1 s마다 결정) |
| `Agent.MaxStep` | 300 (물리 스텝, = 60결정 = 6 s) |

`Heuristic()`은 **무동작**이다(모든 관절 가동범위 중앙, grasp 불변) ---
학습기/모델 없이 씬을 돌릴 때 placeholder action에 안 끌려다니게 하기
위함이지 손으로 쓴 컨트롤러가 아니다. 수동 조작은 `ManualClimberControl`.

## Episode 종료

-   **성공** --- 지정 limb이 목표 홀드를 grasp. `+1.0`, 즉시 종료.
-   **추락** --- hips가 시작 높이에서 `maxDrop`(1 m) 아래. `−1.0`, 즉시
    종료. 태그/접지 판정이 아니라 낙차 하나로 본다. **Stage 1에서는 한
    번도 발화하지 않는다**(지지 limb 셋이 월드에 고정돼 hips가 1 m를
    못 떨어짐 --- 실패는 전부 시간 초과). 이 여유가 낙차 패널티를
    필요하게 만들었다(문턱이 안 걸릴 만큼 넓어 평균 0.51 m 처짐이 공짜였음).
-   **시간 초과** --- `Agent.MaxStep` 300 물리 스텝, 보너스/패널티 없음.
    단위는 결정이 아니라 물리 스텝(`StepCount`가 academy 스텝마다 오름,
    TensorBoard의 `Environment/Episode Length`는 결정 단위라 5를 곱해야 맞음).

두 TensorBoard 지표(`Success/<limb>`, `ClearTime/<limb>`)는
`StatsRecorder`가 `summary_freq` 구간 평균을 내므로 그래프가 곧
limb별 성공률/평균 클리어 시간이다. 시간 초과 에피소드는 다음
`OnEpisodeBegin`까지 기록을 미룬다(`FixedUpdate`를 안 거치므로).

Stage 2 이후로 미룬 것: 일정 시간 pose 유지 요구, 비정상 자세 판정.

## 평가

-   Target pose success rate --- TensorBoard `Success/<limb>`(성공 1,
    추락/시간초과 0)
-   평균 pose completion time --- TensorBoard `ClearTime/<limb>`(성공한
    에피소드만 --- 반드시 `Success/<limb>`와 같이 읽는다)
-   Fall rate / Grasp success rate
-   연속 N-pose 수행 성공률
-   unseen random wall generalization
-   그립 하중 / `StabilityScore` --- 자세가 사람 손 힘 안에 드는지

## 가장 큰 위험

agent가 reward 또는 grasp abstraction을 악용해 사람이 보기에는
부자연스러운 동작을 만들 수 있다. joint limit, 힘 제한, velocity/energy
penalty와 animation 품질 평가가 필요하다.
