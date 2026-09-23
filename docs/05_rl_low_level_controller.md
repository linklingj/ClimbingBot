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

## Random Wall Generator

다양한 위치의 홀드를 생성한다.

파라미터: - wall width/height - hold density - 최소 hold distance - 좌우
분포 - 높이 progression

학습 가능한 범위를 벗어난 불가능한 벽이 과도하게 생성되지 않도록
feasibility constraint를 둔다.

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
