# 2026-09-25 — ClimbingAgent, 학습 가능 상태까지

## 무엇을

`ClimbingAgent` + `config/climbing_stage1.yaml` + 프리팹/씬 배선.
관측 256, 연속 action 41, discrete branch 4×3.

하이퍼파라미터는 ml-agents `ppo/Walker.yaml`에서 출발했다. 같은 ragdoll,
같은 `JointDriveController`, 비슷한 action 모양이라 가장 가까운 known-good
출발점이다. 바꾼 건 둘 — `gamma` 0.995→0.99(한 번의 limb 이동은 1~2초라
200결정을 거슬러 크레딧할 이유가 없다), `max_steps` 30M→5M.

## 기준 프레임은 벽

Walker는 ragdoll이 요동쳐 학습이 흔들리는 걸 막으려고 `OrientationCube`라는
안정화 프레임을 따로 둔다. 클라이머는 **언제나 고정된 벽 하나를 마주 본다**.
벽이 이미 그 안정화 프레임이라 대역 오브젝트가 필요 없다. 초기에
`OrientationCube`를 지운 게 결과적으로 맞았다.

## 상수 입력 빼기

body part 16개 중 자세를 보고하는 건 13개다. hips는 관절이 없고, 손목 둘은
전 축 Locked라 localRotation이 상수다. `normalize: true`에서 분산 0인 입력은
쓸모가 없다. Walker는 손을 이름으로 빼는데 여기서는 **관절이 용접인지
검사해서** 뺀다 — 어느 부위가 용접인지 하드코딩하지 않는다.

랜덤 action을 한 번 넣어 확인: 회전 13 + 강도 13이 바뀌고, 손대지 않은
관절이 정확히 `hand_L`, `hand_R`이었다.

팔꿈치 angZ는 열려 있지만 구동하지 않는다. 실측상 효과가 없는데 action
1개를 먹는다. 목표를 0으로 주므로 드라이브가 중립에서 잡아 준다.

## 삽질 둘

**프리팹 인스턴스 오버라이드.** `agent.env = env`를 평범한 필드 대입으로
했더니 play mode에서 null이었다. 프리팹 인스턴스의 필드 대입은 기록하지
않으면 오버라이드가 되지 않아 리로드에 날아간다. `SerializedObject` +
`RecordPrefabInstancePropertyModifications`로 고쳤다. 같은 실수를 다시
디버깅하지 않도록 `Initialize`에 필드명을 말해 주는 에러를 넣었다.

**`Run In Background`.** 8초를 기다렸는데 academy step이 1에서 늘지 않았다.
에디터가 포커스를 잃으면 플레이 루프가 멈춘다. 학습은 원래 포커스 없이
돌아가므로 이건 테스트 편의가 아니라 학습에 필수 설정이다. 켠 뒤
frameCount 1212, academy 334스텝으로 정상 진행했다.

또 하나: **`Physics.Simulate`는 `FixedUpdate`를 호출하지 않는다.** 그래서
지금까지 쓴 검증 방식으로는 아카데미 루프가 한 스텝도 안 돈다. 이것만은
실제 시간을 흐르게 둬야 한다.

## 실측

에러 0. 에피소드 **2회 완주** — MaxStep 종료 → `OnEpisodeBegin` 재진입 →
새 벽(74홀드) + 새 타깃 + 4/4 grasp. 관측 크기 불일치 없음.
마스크는 지정 limb(RightHand)만 열리고 나머지 셋은 grasp/release 둘 다
막혔다. 학습기 없이는 정책이 없어 보상이 시간 패널티만 쌓여 음수다(정상).

## 다음

`mlagents-learn`이 설치되어 있지 않다. torch까지 끌고 오는 설치라 임의로
하지 않았다. 설치 후 첫 런에서 볼 것: 성공률이 오르는지, 안 오르면
`progressReward`(2.0)와 `minStep`(0.30) 중 어느 쪽이 문제인지.

## 추가 (09-26) — GroundContact가 에피소드를 끝내고 있었다

첫 학습 런(`stage1-01`, 941k 스텝) 중 콘솔에 같은 에러가 37번 떴다:
`Destroying GameObjects immediately is not permitted during physics
trigger/contact...`.

원인 체인은 하나다. **`Agent.EndEpisode()`는 동기다** — 그 자리에서
`_AgentReset` → `OnEpisodeBegin`까지 내려간다. 그래서 `OnCollisionEnter`
안에서 부르면 에피소드 리셋 전체가 PhysX 컨택트 콜백 안에서 실행되고,
거기서는 `DestroyImmediate`가 금지다. 우리 리셋은 `ClearHolds`와
`Release`에서 둘 다 그걸 쓴다.

`GroundContact`가 **Walker 설정 그대로**였다. 16개 중 12개가
`agentDoneOnGroundContact`/`penalizeGroundContact` = true. 보행에서는
"넘어졌다 = 끝"이라 맞지만 여기서는 종료가 이미 `ClimbingAgent.FixedUpdate`에
있다(도달 / hips 낙하 / MaxStep). 두 번째 종료 경로였고, 하필 리셋이 불법인
자리에서 불렀다.

더 고약한 건 자기증식이다. 첫 에러가 `ResetBody`의 첫 `Release`에서 터져
`PoseOnWall`이 끝까지 못 갔다 → 벽에 못 올라감 → 계속 바닥에 닿음 → 또
`EndEpisode` → 또 실패. 실측 당시 ragdoll은 hipsY 0.813, 4개 limb 전부
grasp 해제 상태로 **바닥에 서 있었다.**

덤으로 `penalizeGroundContact`은 `SetReward`를 쓴다(`AddReward`가 아니라).
그 스텝에 쌓인 progress shaping을 −1로 **덮어쓴다.** 체크포인트 보상이
−0.78 / −1.66으로 나온 것과 맞는다.

고친 방법: 프리팹의 16개 전부 두 플래그 false. `DestroyImmediate`는 그대로
뒀다 — 주석의 이유(스테일 콜라이더/grasp가 프레임을 넘기면 안 됨)가 여전히
맞고, 불법 **호출 지점**을 없앨 문제였다. `touchingGround`는 관측에 쓰므로
살렸다. `GroundContact.cs`에 왜 꺼져 있는지 주석을 남겼다.

실측(수정 후): 12초 플레이에 에러 0, 에피소드 1회 완주 후 2회차 506스텝
진행, 4/4 grasp, hipsY 1.822. GroundContact done=0 penalize=0 touching=0.

## 다음

`stage1-01`은 신뢰할 수 없다. 보상이 덮어써졌고 리셋이 깨진 채 941k를
돌았다. 다시 돌려야 한다.

## 추가 (09-26) — 학습 환경 16개, 16각형 링

한 씬에 `TrainingArea` 프리팹 16벌. 프리팹 하나에 벽(+ 두 생성기 +
`Stage1Environment`) + ragdoll/agent가 통째로 들어간다. 씬에 흩어져 있던 둘을
빈 부모 밑으로 묶어 저장했으니, 앞으로 환경을 고칠 자리는 프리팹 하나다. 정 16각형 모양으로 배치.
