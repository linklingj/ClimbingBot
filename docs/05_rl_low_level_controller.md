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

### 관절 가동범위

**부호 규칙 주의.** `ConfigurableJoint`의 low/high angular X limit은
`Quaternion.AngleAxis(θ, joint.axis)`와 **부호가 반대**다. 즉 joint가 말하는
+120°는 축 기준 -120° 회전이다. 실측으로 확인했다(어깨·팔꿈치에 토크를 걸어
어느 쪽 limit에 멈추는지 봤다). 축 방향만 보고 해부학적 의미를 추측하면
굴곡과 신전이 뒤집힌다.

또 PhysX 규약상 angularX가 twist, angularY/Z가 swing(타원뿔)이다. 이 ragdoll은
뼈 방향과 축이 일치하지 않아서, 예컨대 고관절의 "twist"는 굴곡/신전이고
대퇴 축회전은 angZ에 있다.

실측한 의미와 현재 값(좌측 기준, +Z가 벽 방향):

| 관절 | angX | angY | angZ |
|---|---|---|---|
| spine / chest | 굴곡·신전 ±20 | 측굴 ±20 / ±24 | **축회전 ±20** |
| 어깨 | 수평내전 +120 / 수평외전 -60 | 거상 ±100 | 상완 축회전 **Locked** |
| 팔꿈치 | 굴곡 0\~160 | Locked (정상) | 전완 회내외 ±60 |
| 고관절 | 굴곡 -125 / **신전 +20** | 외전·내전 ±50 | **대퇴 축회전 ±40** |
| 무릎 | 굴곡 0\~120 | Locked | Locked |
| 발목 | ±20 | ±20 | ±20 |

Walker에서 바꾼 것은 굵게 표시한 셋이다.

-   **고관절 축회전 Locked → ±40.** 가장 큰 구멍이었다. 백스텝, 드롭니,
    프로그 자세는 전부 무릎을 바깥으로 돌리는 동작이고 그게 이 축이다.
    보행은 시상면 운동이라 Walker는 잠가도 됐다.
-   **고관절 신전 60° → 20°.** 다리를 몸 뒤로 60° 차는 건 사람 범위(20\~30°)의
    두세 배이고 클라이밍에는 쓸 데가 없다. 다리는 벽 쪽, 즉 몸 앞에 있다.
    가동범위를 **줄이는** 변경이라 공짜다.
-   **몸통 축회전 ±15 → ±20** (spine+chest 합쳐 ±30 → ±40). 어깨와 골반을
    비틀어 엉덩이를 벽에 붙이는 동작이 클라이밍의 기본인데 보행은 거의 안
    쓴다. 두 축 모두 이미 열려 있어서 limit 값만 바뀐다.

**action space 영향.** 고관절만 늘었다. `SetJointTargetRotation`은 Locked 축에
0을 곱해 버리므로, 고관절에 지금까지 x·y 2개를 주던 것을 x·y·z 3개로 줘야
한다. 다리당 +1, 전체 +2다. 나머지 두 변경은 limit 값만 바뀌므로 action
개수가 그대로다.

**일부러 두고 온 것.**

-   **어깨 축회전은 계속 Locked.** 팔을 축으로 몸이 도는 문제는 grasp를 볼
    조인트로 바꿔 이미 해결했고(165° 실측), 여기서 또 풀면 같은 자유도를 두 번
    사는 셈이라 action만 늘어난다.
-   **팔꿈치 angZ ±60**(전완 회내외)은 실측상 효과가 없다. grasp가 회전
    자유라 손이 이미 자유롭게 돌고, 한계 관절은 어깨다. action 1개를 쓰는데
    얻는 게 없으니 Locked로 되돌리는 쪽이 맞다고 보지만 판단은 남겨 둔다.
-   **발목 ±20**은 좁지만 grasp가 발 위치를 고정하고 회전은 풀어 두므로
    스미어링/에징 차이가 물리에 나타나지 않는다.

## Observation

**고정 크기다.** 홀드 개수는 벽마다 다르지만 관측 차원은 변하지 않는다.
아래 "주변 홀드를 넣지 않는 이유"를 먼저 읽을 것 --- 이 절의 나머지가
전부 거기서 따라 나온다.

**기준 프레임은 벽이다.** 월드 좌표는 넣지 않는다. Walker는 ragdoll이
요동쳐 학습이 흔들리는 걸 막으려고 `OrientationCube`라는 안정화 프레임을
따로 두는데, 클라이머는 언제나 고정된 벽 하나를 마주 보므로 **벽 자체가
이미 그 안정화 프레임**이다. 대역 오브젝트가 필요 없고, 그래서
`OrientationCube`를 지운 게 결과적으로 맞았다.

구현 크기 **256**. `ClimbingAgent`가 쓰는 순서 그대로:

| 블록 | 차원 |
|---|---|
| hips 회전(벽 기준), chest 회전(벽 기준), 평균 속도 | 4 + 4 + 3 |
| limb 4개 `isGrasping` | 4 |
| limb 4개 (목표 상대 위치 3 + 지시 플래그 1) | 16 |
| body part 16개 × (접지 1 + 속도 3 + 각속도 3 + hips 상대 위치 3) | 160 |
| 자세를 보고하는 관절 13개 × (localRotation 4 + 강도 1) | 65 |

16개 중 13개만 자세를 보고한다. hips는 관절이 없고, 손목 둘은 **전 축
Locked**라 localRotation이 상수다. 상수 입력은 `normalize: true`에서
분산이 0이라 쓸모가 없다. Walker는 손을 이름으로 빼는데, 여기서는 관절이
용접인지 검사해서 뺀다 --- 어느 부위가 용접인지 하드코딩하지 않는다.

### Body (proprioception)

ML-Agents `Walker`의 body observation을 그대로 쓴다.

-   body part별 hips 기준 상대 위치 / 회전
-   linear / angular velocity
-   hips orientation

### Contact

limb 4개(`Limb` enum 순서) 각각:

-   `isGrasping` (1)

잡고 있는 홀드의 위치는 **따로 넣지 않는다.** grasp는 limb을 그 자리에
고정하므로 홀드 위치가 곧 limb 위치이고, 그건 이미 body 관측에 있다.

### Target

limb 4개 각각:

-   목표 지점의 상대 위치 (3) --- 해당 limb 기준
-   이번 pose에서 **옮기라고 지시된 limb인지** (1)

4 × 4 = 16차원. 움직이지 않는 limb의 목표는 현재 위치이므로 상대 위치가
0에 가깝고 지시 플래그가 0이다.

목표는 **limb endpoint 4개뿐**이고 16개 body part의 전체 pose가 아니다.
전체 pose를 주면 reward 설계는 쉬워지지만 VLM이 그걸 뱉기가 너무 어렵다.
사람도 "왼손을 저 크림프로"라고 생각하지 척추 각도로 생각하지 않는다.
나머지 자세는 RL이 찾는다.

### Stability

-   center of mass velocity
-   chest orientation (벽 기준)

limb별 그립 하중(아래 "그립 하중")은 아직 관측에 넣지 않았다. 진단용으로만
쓰는 중이다.

### 주변 홀드를 넣지 않는 이유

이 모듈이 배우는 것은 "끝까지 올라가라"가 아니라 **"이 limb을 저 지점으로
옮겨라"**다. 어느 홀드로 갈지는 VLM planner가 정한다(`docs/07`의
`PlannerAction / PoseSequence`). 따라서:

-   목표는 "홀드"가 아니라 **3D 점**이다. 에이전트는 그게 홀드인지 알
    필요가 없다. 홀드 개수가 벽마다 달라 관측 크기가 가변이 되는 문제가
    **애초에 생기지 않는다** --- 목표 개수는 limb 개수로 고정이다.
-   절대 wall-local 좌표를 넣으면 "높이 3.2 m에서 본 배치"를 통째로
    외운다. limb 기준 상대 좌표는 평행이동 불변이라 같은 국소 상황이 같은
    관측이 되고, 그게 랜덤 벽 일반화의 전부다.
-   주변 홀드가 실제로 필요한 곳은 grasp action **masking** 하나뿐인데
    그건 `CanGrasp`가 코드에서 계산한다. 정책이 볼 관측이 아니다.

단일 limb 이동인데 target 슬롯을 4개 두는 이유는 커리큘럼 때문이다.
Stage 1은 한 슬롯만 현재 위치와 다르고 나머지는 "유지"다. 슬롯을 1개로
줄이면 Stage 3에서 관측 shape이 바뀌어 앞 스테이지 가중치를 못 이어쓴다.

기각한 대안: 홀드 ID 임베딩(벽마다 ID 의미가 달라 일반화가 안 된다),
벽 전체를 2D 그리드로 넣기(CNN이 필요하고 Phase 1에는 과하다. 홀드가
수십 개로 늘고 경로 선택 자체가 어려워지면 그때 다시 본다).

## Action Space

두 종류를 결합한다.

### 1. Joint Control

`BodyPart.SetJointTargetRotation`에 축별 목표를 정규화된 [-1, 1]로 넘긴다.
축 개수는 관절마다 다르다 --- Locked 축은 limit이 0이라 값을 줘도 0이
곱해져 사라지므로, 열린 축에만 값을 준다.

구현: 회전 **28** + 관절 강도 **13** = **연속 41개**.

| 관절 | 회전 축 수 |
|---|---|
| spine, chest | 3, 3 |
| head | 2 (angZ Locked) |
| 어깨 L·R | 2씩 (상완 축회전 Locked) |
| 팔꿈치 L·R | 1씩 |
| 고관절 L·R | 3씩 --- 축회전을 열었다 |
| 무릎 L·R | 1씩 |
| 발목 L·R | 3씩 |

**팔꿈치 angZ(전완 회내외)는 열려 있지만 일부러 구동하지 않는다.** 실측상
효과가 없는데(위 "관절 가동범위") action 1개를 먹는다. 목표를 0으로 주므로
드라이브가 중립에서 잡아 준다 --- 자유롭게 흔들리는 게 아니다.

손목은 전 축 Locked이라 회전도 강도도 주지 않는다. 강도 13개는 실제로
구동하는 관절 수와 같다.

### 2. Discrete Grasp Actions

각 limb:

``` text
0 = no change
1 = grasp
2 = release
```

**어느 홀드를 잡을지는 action이 고르지 않는다.** 해당 limb에 지정된 target
홀드를 잡는다. 홀드 선택은 planner의 일이고, 이 모듈은 "지금 잡을지"만
정한다 --- 그래서 관측에 홀드 목록이 없어도 이 action이 성립한다.

언제 놓고 언제 잡을지는 스크립트로 박지 않고 **학습 대상으로 둔다.**
"충분히 안정됐을 때 놓는다"가 클라이밍 스킬의 핵심이라, 그걸 정책에서
빼면 배우는 게 모터 제어뿐이 된다.

구현: **branch 4개 × 크기 3**.

`WriteDiscreteActionMask`가 두 조건을 건다 --- grasp는 `CanGrasp`(그 limb에
지정된 홀드까지 `graspRadius` 이내)일 때만, release는 지금 잡고 있을 때만.

**Stage 1은 나머지 세 limb의 branch를 통째로 "no change"로 막는다.**
`docs/05`의 Stage 1 정의가 "다른 limb 고정하고 하나만 이동"이기 때문이다.
`ClimbingAgent.lockSupportLimbs`로 끄면 Stage 2 동작이 된다.

## Grasp 모델

실제 손가락/마찰을 simulation하지 않는다.

`grasp` 성공 시:

> 해당 limb endpoint를 target hold 위치에 물리적으로 고정한다.

구현: `ClimberRagdoll.Grasp/Release`
(`Assets/01Scripts/Ragdoll/ClimberRagdoll.cs`)

-   limb endpoint에 `ConfigurableJoint`를 붙이고 `connectedBody = null`로
    두어 월드에 고정한다. 홀드는 static geometry이므로 Rigidbody가 없다.
-   **선형 3축만 잠그고 각도는 푼다.** 손이 홀드 위에서 회전한다는 뜻이고,
    이게 "limb을 홀드 위치에 고정"이라는 정의에 맞다. `FixedJoint`로
    방향까지 용접하면 안 되는 이유는 아래 참고.
-   고정 위치는 **홀드 중심이 아니라 limb의 현재 위치**다. 순간이동으로
    인한 물리 pop을 피하기 위한 선택이고, grasp radius가 작으므로 오차는
    그 범위 안에 머문다.
-   release는 `DestroyImmediate`로 즉시 제거한다. 다음 physics step까지
    남으면 episode reset과 충돌한다.
-   `Grasp(limb, hold)`는 어느 홀드를 잡았는지 기억한다. 클리어 판정에
    필요하다.

측정: 중간 홀드를 한 손으로 잡고 매달렸을 때 손 드리프트 0.046 m, 발은
공중. 선형 잠금만으로 체중을 버틴다.

## 그립 하중 (자세 평가 지표)

`ClimberRagdoll.GripLoad(limb)` --- 그 limb이 지금 버티고 있는 힘을 **체중
배수(BW)**로 준다. `StabilityScore`는 이를 종합한 [0, 1] 값이다.

grasp 조인트는 힘 제한이 없는 위치 고정이라 **잡고 있는 한 물리적으로
추락하지 않는다.** 그래서 이 지표가 곧 빠져 있는 손 힘 모델이고, 몸통
자세가 아니라 **그립**에 붙인다. 자세 실패 양상(몸이 벽에서 뜸, 발 빠짐,
바돌, 한 팔 버티기)이 전부 하중 상승 하나로 나타나므로 기하학적 지표를
따로 두지 않는다.

**측정 위치가 중요하다.** grasp 조인트가 아니라 **limb 자신의 관절**(손목,
발목)의 `currentForce`를 읽는다. 둘은 직렬이라 같은 하중을 전달해야 하지만
실측값이 갈린다 --- 공중에서 한 손으로 매달렸을 때 손목 1.07 BW(체중
1069 N과 일치), 같은 순간 grasp 조인트 7.8 BW. grasp 조인트는 손목 앵커에서
몇 cm 떨어진 지점에서 월드에 고정되는데 거기서 솔버 보고값이 부풀려지는
것으로 보인다. 손목 쪽이 M·g와 맞으므로 그쪽을 쓴다.

`JdController.bodyPartsDict`는 `Awake`에서 채워지므로 거기 담긴 관절은 항상
ragdoll 자신의 것이다. grasp 중인 손에 `GetComponent<ConfigurableJoint>()`를
쓰면 둘 중 무엇이 나올지 보장되지 않는다.

`handCapacity` / `footCapacity`(BW)는 캘리브레이션 노브다. 기본값은 사람
기준 --- 손 1.0(강한 클라이머의 한 팔 매달리기), 발 2.0.

``` text
StabilityScore = 1 - max_over_grasping_limbs(GripLoad / Capacity)   (0~1로 clamp)
아무것도 잡고 있지 않으면 0
```

최솟값이 아니라 **최악 그립**이 점수를 정한다. 평균은 한 손이 전부 지고
있는 상태를 가린다.

**지금은 진단용이다.** 이 값으로 홀드를 놓게 하거나 reward에 넣지 않는다.
스태미너 적분(하중을 시간에 대해 누적해 0이 되면 release)은 뒤로 미뤘다.

기준값(컨트롤러 없이, slerpDrive가 T자세로 당기는 상태):

| 상황 | 하중 |
|---|---|
| 공중에서 한 손 매달리기 | 0.93\~1.1 BW (= 체중) |
| 두 손으로 같은 홀드 | 합계 1.8\~3.9 BW |

두 손 합계가 1을 넘는 것은 정상이다. 초과분은 팔들이 서로 당기는 내부
하중이고, 컨트롤러가 관절 목표를 잡기 시작하면 줄어들 값이다. 그때까지
점수는 0에 붙어 있다.

## 수동 조작 도구 (테스트 전용)

`ManualClimberControl` (`Assets/01Scripts/Testing/`)

Q/W/A/S로 limb을 고르고 마우스로 조준해 해당 limb을 끌어당긴다. 좌클릭
grasp, 우클릭 release, R 리셋.

**이건 agent의 action space가 아니고, 그렇게 만들면 안 된다.** limb에
raw force를 걸어 끌 뿐이라 `ClimbingAgent`가 내보낼 joint target과는
아무 관계가 없다. `Agent.Heuristic()`에 넣지 않고 별도 MonoBehaviour로
둔 이유다. Agent가 같은 ragdoll을 몰기 시작하면 이 컴포넌트는 꺼야
한다. 둘이 서로 싸운다.

## 에피소드 시작 자세

`ClimberRagdoll.ResetOnHold(hold)` --- 리셋하면 **양손이 start 홀드를 잡은
상태**로 시작한다.

authored pose가 T자이므로 양팔을 홀드 쪽으로 겨눠 두 손을 홀드에 모은다.
한 홀드에 두 손이 닿으려면 각 어깨가 홀드에서 팔 길이만큼 떨어져야 하고,
어깨선이 그 오프셋과 수직이므로 어깨 중점은
`sqrt(팔길이² - 어깨너비/2²)`만큼 떨어뜨린다.

홀드 높이에 따라 자세가 갈린다. 서서 닿지 않는 높이면 홀드 **아래로
매달리고**, 닿는 높이면 뒤로 물러나 **선다**. 후자가 없으면 낮은 start
홀드에서 발이 바닥 아래로 파묻힌다.

주의: 아직 관절을 지시하는 주체가 없어서 slerpDrive가 T자세로 되돌리려
당긴다. 그 결과 시작 후 손이 15 cm 정도 끌려가 평형에 멈춘다(계속
미끄러지지는 않는다). controller가 joint target을 잡기 시작하면 줄어들
값이다.

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

여기서 `target_hold`는 그 limb에 지정된 홀드다. 가까이 있는 아무 홀드가
아니다 --- "Discrete Grasp Actions" 참고.

추가 조건 후보: - 해당 limb가 이미 grasp 중이면 다른 grasp 금지 -
release 가능한 상태 제한

`ClimberRagdoll.CanGrasp`가 거리 조건(`graspRadius`, 기본 0.2 m)과 "이미
grasp 중이면 금지"까지 구현한다. 나머지 조건은 controller 쪽에서
action mask로 건다. `graspRadius`는 홀드 크기에 맞춰 조정하는 값이다.

## Training Strategy - Curriculum learning

### Stage 1 --- Single Limb Target

-   균형 유지 및 관절 제어 안정화
-   다른 limb 고정하고 하나의 limb만 랜덤 target으로 이동
-   벽은 `ScatteredWallGenerator`. 정해진 자세에서 시작해 limb 하나와
    목표 홀드 하나를 받는다. 루트가 없으므로 종료 조건은 탑아웃이 아니라
    목표 홀드 grasp다.

### Stage 2 --- Random Wall Climbing

-   벽은 `RandomWallGenerator` (spline 루트)
-   위쪽 target pose 연속 제공
-   pose-to-pose transition 학습

### Stage 3 --- Planner Integration

-   VLM이 생성한 target pose 수행

## 벽과 홀드

`ClimbingWall` (`Assets/01Scripts/Wall/ClimbingWall.cs`)

-   transform은 벽의 **좌하단**에 둔다. +X 오른쪽, +Y 위, 등반자는 -Z
    쪽. `docs/07`의 좌표 규칙과 같다.
-   `WallToWorld(Vector2)`가 wall-local 2D(m)를 월드로 변환한다.
-   크기는 고정: 4.0 m × 6.0 m, 두께 0.3 m.
-   `BuildSlab()`이 치수대로 slab을 만든다. 씬에 손으로 놓고 치수 필드를
    따로 두면 둘이 어긋나므로 진실의 출처를 하나로 뒀다.
-   `AddHold(id, wallPosition)` / `ClearHolds()`만 제공한다. **홀드를
    어디에 놓을지는 벽이 정하지 않는다** --- Phase 1은 랜덤 생성기,
    이후에는 CV 출력이 정한다.
-   홀드는 `holdPrefab`(`Assets/03Prefabs/Hold_0.prefab`)을 찍어낸다.
    `SplineInstantiate`도 같은 프리팹을 쓰므로 홀드의 정의가 하나다.

`Hold` (`Assets/01Scripts/Wall/Hold.cs`)

-   `id` --- scene 안에서 유일. `docs/07`의 ID 규칙을 따른다.
-   `color` --- `MaterialPropertyBlock`으로 적용한다. 홀드마다 머티리얼
    인스턴스를 만들지 않기 위해서다.
-   `role` --- `Normal` / `Start` / `Top`.
-   `wallPosition` --- wall-local 2D 좌표.

## Wall Generator (테스트 전용)

생성기는 **두 개**고 학습 단계에 따라 고른다. 둘 다 `IWallGenerator`
(`Assets/01Scripts/Wall/IWallGenerator.cs`)를 구현하고 벽 GameObject에
같이 붙여 둔다. **활성화된 쪽이 선택된 것**이다 --- mode enum을 따로 두지
않는다.

| 생성기 | 벽 모양 | 쓰는 곳 |
|---|---|---|
| `ScatteredWallGenerator` | 벽 전체에 홀드가 흩뿌려짐. 루트 없음 | Stage 1 |
| `RandomWallGenerator` | spline 한 줄기를 따라가는 루트 | Stage 2 |

둘 다 **버릴 코드다.** 최종 시스템의 홀드는 CV 출력에서 온다. 그래서 벽
안에 두지 않고 `ClimbingWall`의 공개 API만 쓰는 별도 컴포넌트로 뺐다.
지울 때 `ClimbingWall`은 건드리지 않는다.

### ScatteredWallGenerator (Stage 1)

Stage 1은 **정해진 자세에서 시작해 limb 하나와 목표 홀드 하나를 받는다.**
그래서 홀드가 climbable한 줄기를 이룰 필요가 없고, 오히려 climber 주변
**모든 방향에 후보 타깃이 있어야** 한다. 루트를 만들면 그게 방해가 된다.

dart throwing으로 뿌린다 --- `margin` 안쪽에 점을 찍고 이미 놓인 것과
`minSeparation`보다 가까우면 버린다. 시도 횟수에 상한이 있어 반드시
끝난다. Poisson-disk 정식 알고리즘(Bridson)은 이 개수에 과하다.

`minSeparation`은 **limb 리치 안에 홀드가 몇 개 들어오는지**를 정한다.
그게 곧 "도달 가능한 타깃이 존재하는가"라서 Stage 1에서는 이 값이 가장
민감한 노브다. 0.38 m에서 네 limb 모두 후보를 갖는다(아래 Stage 1 환경).

한때 0.45로 두고 "이보다 촘촘하면 리치가 두 홀드 중 아무 쪽에나 붙어
타깃이 모호해진다"고 적었는데, 그건 틀렸다. grasp action은 **지정된 target
홀드를 잡지 가까운 홀드를 찾지 않는다**(Action Space 참고). 탐색이 없으니
모호해질 것도 없다.

`ScatteredWallGenerator.Generate(seed, anchors)`는 주어진 좌표에 홀드를
먼저 무조건 놓는다. Stage 1이 시작 자세 밑에 홀드를 깔 때 쓴다. 앵커끼리는
`minSeparation`을 적용하지 않는다 --- 두 발은 어떤 산포 쌍보다 가깝게
붙는다. 흩뿌리는 홀드는 앵커와의 거리를 지킨다.

**role은 전부 `Normal`이다.** start도 top도 없다. 이 벽에는 완주할 루트가
없으므로 `ClimbingWall.TopHold`는 null이고 탑아웃은 종료 조건이 아니다.
`ResetOnHold(null)`은 `ResetBody()`로 떨어지므로 수동 조작 도구는 벽에
붙지 않고 그냥 선 자세로 시작한다.

실측 (4×6 m 벽, 시드 300개, 기본값 holdCount 70 / minSeparation 0.38 /
margin 0.3):

-   70개 전부 배치 성공, 최소 쌍간 거리 0.380 m, 경계 이탈 0건, role 오류 0건
-   벽 위 임의의 점에서 **가장 가까운 홀드까지 최악 0.640 m**, 평균 0.469 m

### RandomWallGenerator (Stage 2)

`RandomWallGenerator` (`Assets/01Scripts/Testing/`)

`Generate(seed)`는 루트를 spline으로 그린다.

1.  top 홀드의 x를 랜덤으로 뽑는다. 높이만 `topHoldY`로 고정이다.
2.  knot 2\~4개짜리 spline을 만든다. 아래 끝은 `splineBottomY`(0.5 m),
    위 끝은 **항상 top 홀드**다. 나머지 knot의 x는 랜덤이다.
3.  start 홀드의 x는 spline이 `startHoldY`를 지나는 지점에서 읽는다.
    즉 start 홀드는 루트 옆이 아니라 **곡선 위에** 있다.
4.  `SplineInstantiate`가 그 위에 홀드 프리팹을 배치한다. 간격은
    `minHoldSpacing`\~`maxHoldSpacing`에서 홀드마다 뽑고, x 방향 랜덤
    오프셋을 더해 사다리처럼 보이지 않게 한다.
5.  배치 결과를 위치만 읽어 벽의 진짜 홀드로 굽고, start/top을 넣은 뒤
    아래에서 위로 id를 매긴다.

start/top은 **높이만 고정**이다. x는 벽마다 달라진다.

**start 홀드는 가장 아래 홀드가 아니다.** spline이 `startHoldY` 밑까지
내려오므로 그 아래에도 홀드가 생긴다.

`SplineInstantiate`의 인스턴스는 `HideAndDontSave`이고 컴포넌트가
수명을 관리한다. 씬에 저장되지도, 우리가 붙인 id/role/color를 유지하지도
못한다. 그래서 위치만 받아 쓰고 인스턴스는 버린다. 벽은 직렬화되는 진짜
홀드를 갖는다.

또 `SplineInstantiate`는 position offset의 min/max만 공개하고 축별
randomize 토글은 비공개다. 켜지 않으면 오프셋이 min 값으로 **고정**되고
`Seed`도 무시된다. 리플렉션으로 `m_PositionOffset.randomX`를 켠다
(com.unity.splines 2.8.4 기준). 버릴 코드라 감수한다.

**간격 보장.** spline 간격과 지터만으로는 `maxHoldSpacing + 2 × jitter ≤
maxReach`가 성립한다(지터를 그 부등식에 맞춰 clamp한다). 하지만
start/top은 지정된 높이에 놓이므로 대체된 spline 홀드보다 이웃과 멀어질
수 있다. 남은 간격은 중점에 홀드를 끼워 반으로 접는다. 이 패스 뒤에는
모든 연속 간격이 `maxReach` 이하다.

`maxReach` 1.4 m는 ragdoll 기준값이다 --- 키 1.97 m, 팔 스팬 1.95 m,
hips에서 손까지 1.09 m.


## Stage 1 환경

`Stage1Environment` (`Assets/01Scripts/Training/Stage1Environment.cs`)

에피소드가 어떻게 생겼는지를 정한다 --- 벽, 시작 자세, 어느 limb을 어느
홀드로. **관측·보상·행동은 여기 없다.** 그건 `ClimbingAgent`의 몫이고,
이 컴포넌트는 그 위에 얹힌다.

`ResetEpisode(seed)` 한 번이 에피소드 하나를 만든다. 목표를 못 찾으면
false를 돌려주고, 호출자는 다른 시드로 다시 부른다(실측상 아직 일어나지
않았다).

### 자세가 먼저, 홀드가 나중

홀드 4개를 먼저 고르고 거기 닿는 자세를 푸는 것보다, **자세를 잡고 각
limb 밑에 홀드를 놓는 쪽이 훨씬 쉽다.** Phase 1의 벽은 합성이라 벽을
몸에 맞춰도 잃을 게 없다. 덕분에 네 limb grasp가 운이 아니라 **구조적으로**
성공한다 --- 실측 limb-홀드 간격 0.001 m, `graspRadius` 0.2 m.

limb을 자세로 옮기는 것은 두 뼈짜리 CCD다. 해석적 2링크 IK보다 쓰기 쉽고
뼈가 어느 방향을 보든 상관없다(authored pose가 T자라 관절 축이 제각각이다).
관절 limit은 보지 않는다 --- 아래 오프셋이 해부학적으로 평범해서 해가
limit 안에 떨어진다. 자세를 바꾸면 다시 재야 한다.

### 시작 자세

기본값(힙 기준, 벽면 좌표, m): 손 `(±0.34, +0.42)`, 발 `(±0.26, −0.58)`,
힙은 벽에서 0.26 m.

**손은 어깨 높이 근처까지만 올린다.** 처음에 손을 머리 위로 올렸더니
(오프셋 y 1.0) 팔이 리치의 75%까지 뻗은 상태가 되어 **손이 legal target을
하나도 못 가졌다.** 이미 도달 구의 위쪽 끝에 있으니 더 위는 전부 리치 밖,
리치 안은 전부 아래쪽이었다. 뻗은 팔이 더 위를 못 잡는 건 당연한 기하다.
팔을 굽혀 두어야 위로 뻗을 여지가 생긴다.

### 타깃 선택

limb을 무작위 순서로 훑어 후보가 있는 첫 limb을 쓴다. 후보 조건:

-   아무도 안 잡고 있는 홀드
-   `belowTolerance`(0.20 m)보다 더 아래면 제외 --- **위 아니면 좌우**
-   limb에서 `minStep`(0.30 m) 이상 --- 안 그러면 움직일 필요가 없다
-   **체인 뿌리(어깨/고관절)에서 `ChainReach × reachFraction` 이내**

`ChainReach`는 두 뼈 길이의 합, 즉 **완전히 폈을 때** 닿는 거리다. 지금
뻗어 있는 길이로 재면 안 된다 --- 클라이밍 자세는 항상 굽어 있어서 심하게
과소평가된다.

### 실측 (시드 150개)

-   도달 가능한 타깃이 없는 에피소드 **0건**
-   네 limb 전부 grasp **150/150**, 최악 간격 0.001 m
-   타깃 거리 0.383\~0.942 m, limb 아래 타깃 **0건**
-   타깃 limb 분포 41 / 36 / 33 / 40 --- 네 limb이 고르게 나온다

물리로 2초 안정화(컨트롤러 없음): 네 grasp 전부 유지, 힙 y 2.10 → 2.00에서
멈춤. 손이 홀드에서 0.150 m 끌려가는 것은 `에피소드 시작 자세`에 적은
slerpDrive 평형과 같은 값이다.

## Reward 설계

`ClimbingAgent`가 **물리 스텝마다** 채점한다. 결정마다가 아니라 스텝마다인
이유는 shaping이 동작을 따라가야 하기 때문이고, Walker도 같은 자리에서
채점한다.

| 항목 | 기본값 | 언제 |
|---|---|---|
| 진행 | `+2.0 × (이전 거리 − 현재 거리)` | 매 스텝 |
| 성공 | `+1.0` | 지정 limb이 목표 홀드를 grasp → 종료 |
| 추락 | `−1.0` | hips가 시작 높이에서 `maxDrop`(1 m) 아래로 → 종료 |
| 시간 | `−0.0005` | 매 스텝 |
| 지지 상실 | `−0.01` | 매 스텝, 잡고 있지 않은 support limb마다 |
| 에너지 | `0` (꺼 둠) | 매 결정, 연속 action 제곱합 |

**진행 보상은 potential-based다.** 거리의 차분이므로 합이 시작 거리로
묶인다. 앞뒤로 흔들어서 보상을 벌 수 없다.

**에너지 패널티는 기본 0이다.** 학습 초기에 넣으면 동작을 찾는 탐색 자체를
눌러버린다. 배운 동작이 경련하듯 보이면 그때 올린다.

지지 상실 패널티는 `lockSupportLimbs`가 켜진 Stage 1에서는 사실상 작동하지
않는다. support limb의 release가 마스크로 막혀 있기 때문이다. Stage 2에서
마스크를 풀면 그때부터 의미를 갖는다.

`GripLoad` / `StabilityScore`는 **보상에 넣지 않았다.** 아직 진단용이다
(위 "그립 하중").

## 학습 실행

``` bash
pip install mlagents                        # 아직 설치되어 있지 않다
mlagents-learn config/climbing_stage1.yaml --run-id=stage1-01
# 콘솔에 "Start training by pressing the Play button" 가 뜨면 에디터에서 Play
```

`config/climbing_stage1.yaml`은 ml-agents의 `ppo/Walker.yaml`에서 출발했다
--- 같은 ragdoll, 같은 `JointDriveController`, 비슷한 action 모양이라 가장
가까운 known-good 출발점이다. 바꾼 것은 둘뿐이다. `gamma` 0.995 → 0.99(한
번의 limb 이동은 1\~2초라 200결정을 거슬러 크레딧할 이유가 없다),
`max_steps` 30M → 5M(에피소드가 성공으로 끝나고 과제가 한 동작이다).

**`Run In Background`가 켜져 있어야 한다.** 학습은 에디터가 포커스를 잃은
채로 돌아가는데, 꺼져 있으면 플레이 루프가 통째로 멈춘다(academy step이
1에서 늘지 않는 증상).

## ClimbingAgent 구성

| | |
|---|---|
| Behavior name | `ClimbingStage1` |
| Vector observation | 256, stack 1 |
| Continuous actions | 41 |
| Discrete branches | 3, 3, 3, 3 |
| `DecisionRequester` | period 5 (0.1 s마다 결정) |
| `Agent.MaxStep` | 1000 결정 |

`Heuristic()`은 **무동작**이다 --- 모든 관절을 가동범위 중앙에 두고 grasp를
바꾸지 않는다. 학습기도 모델도 없이 씬을 돌릴 때 placeholder action으로
끌려다니지 않게 하기 위한 것이고, 손으로 쓴 컨트롤러가 아니며 그렇게 키워서도
안 된다. 수동 조작은 `ManualClimberControl`에 따로 있다.

## Episode 종료

Stage 1 구현:

-   **성공** --- 지정 limb이 목표 홀드를 grasp. `+1.0`, 즉시 종료.
-   **추락** --- hips가 시작 높이에서 `maxDrop`(1 m) 아래. `−1.0`, 즉시 종료.
    태그나 접지 판정이 아니라 낙차 하나로 본다. 숫자 하나라 튜닝이 쉽고
    "매달린 채 늘어짐"과 "떨어짐"을 가른다.
-   **시간 초과** --- `Agent.MaxStep` 1000 결정(=100초). 보너스도 패널티도
    없다.

Stage 2 이후로 미룬 것: 일정 시간 pose 유지 요구, 비정상 자세 판정.

## 평가

-   Target pose success rate
-   평균 pose completion time
-   Fall rate
-   Grasp success rate
-   연속 N-pose 수행 성공률
-   unseen random wall generalization
-   그립 하중 / `StabilityScore` --- 자세가 사람 손 힘 안에 드는지

## 가장 큰 위험

agent가 reward 또는 grasp abstraction을 악용해 사람이 보기에는
부자연스러운 동작을 만들 수 있다. joint limit, 힘 제한, velocity/energy
penalty와 animation 품질 평가가 필요하다.
