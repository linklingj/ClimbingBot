# 2026-09-23 — 클라이밍 ragdoll 구성

Phase 1의 첫 항목인 "ragdoll humanoid 구성"을 끝냈다.

## 무엇을 했나

ml-agents 저장소의 `Walker` 예제 ragdoll을 ClimbingBot으로 그대로 가져와
클라이밍용으로 전용했다. 처음부터 만들지 않은 이유는 Walker ragdoll이
이미 우리가 필요한 것 — 16-body-part humanoid, `ConfigurableJoint` 기반
관절 구동, ML-Agents가 학습시킬 수 있도록 튜닝된 mass/limit/force — 을
전부 갖고 있기 때문이다.

### 추가된 파일

- `Assets/02Ragdoll/ClimberRagdoll.fbx` / `.prefab` — Walker ragdoll에서
  이름만 바꿔 복사. GUID를 보존해서 prefab 참조가 그대로 산다.
- `Assets/02Ragdoll/Materials/*.mat` — 7개. Built-in Standard 셰이더라
  URP에서 마젠타로 나와서 `Universal Render Pipeline/Lit`으로 바꾸고
  `_Color` → `_BaseColor`를 옮겼다.
- `Assets/01Scripts/Ragdoll/{JointDriveController,GroundContact}.cs` —
  ML-Agents 예제에서 복사. 네임스페이스는 `Unity.MLAgentsExamples`
  그대로 뒀다 (상류에서 복사한 코드라는 표시).
- `Assets/01Scripts/Ragdoll/ClimberRagdoll.cs` — 신규.

### 제거한 것

prefab에서 보행 전용 컴포넌트를 떼어냈다.

- `WalkerAgent` — 걷기 보상/액션. 클라이밍 controller로 대체될 자리.
- `ModelOverrider` — CLI 테스트 유틸. ml-agents 저장소 버전과 레지스트리
  4.1.0 사이 API 차이로 컴파일이 깨질 위험만 있고 쓸 일이 없다.
- `DecisionRequester` — `Agent`를 `RequireComponent`로 요구한다. Agent가
  없는 상태로 임포트하면 Unity가 "Creating missing Agent component"를
  띄우므로 controller 작성 시 다시 붙인다.
- `DirectionIndicator`, `OrientationCube`, `FootRays` 자식 오브젝트 —
  처음엔 "나중에 쓸지도" 하고 남겼다가 지웠다. DirectionIndicator는
  `targetToLookAt`이 비어서 매 프레임
  `UnassignedReferenceException`을 던졌고, 나머지 둘은 비활성 상태로
  놀고 있었다. 필요해지면 다시 붙이는 게 싸다. 대응 스크립트
  (`DirectionIndicator.cs`, `OrientationCubeController.cs`)도 삭제.

`GroundContact`가 `"ground"` 태그를 비교하는데 ClimbingBot에 그 태그가
없어서 `Tag: ground is not defined` 로그가 떴다. 태그를 추가했다.
추락 판정에 그대로 쓸 수 있다.

`JointDriveController.BodyPart`에서 `targetContact` 필드도 지웠다.
`TargetContact.cs`를 끌고 오지 않기 위해서다. 클라이밍에서 contact
모델은 grasp가 대신하므로 이 필드는 쓰이지 않는다. 상류 코드와 달라진
유일한 지점이다.

## 문서 대비 달라진 결정

`docs/05`에 반영했다.

- grasp 구현: 후보 중 **FixedJoint + `connectedBody = null`**을 골랐다.
  홀드는 static geometry라 Rigidbody가 없으므로 월드에 고정하는 편이
  맞다.
- grasp 지점: 문서는 "target hold 위치에 고정"이라고 썼지만 구현은
  **limb의 현재 위치**에 고정한다. Rigidbody를 순간이동시키면 물리 pop이
  생기고, `graspRadius`가 작으니 오차는 그 안에 머문다.
- release에 `DestroyImmediate`를 쓴다. `Destroy`는 프레임 끝까지
  미뤄지는데, 그러면 episode reset이 도는 physics step 동안 grasp가
  살아남아 리셋과 싸운다.

## 검증

Train 씬에 인스턴스를 놓고 play mode에서 `Physics.Simulate`로 확인했다.

- body part 16개 등록
- 범위 밖 grasp 거부, 중복 grasp 거부
- grasp 유지 1.2초: 손 이동 0.0000 m, hips 낙하 2.09 m — 손 하나로 매달림
- `ResetBody()` 후 grasp 해제 + 전 body part 초기 위치 복귀

## 다음

- 랜덤 벽 + 홀드 생성기
- `ClimbingAgent` (observation/action/reward) — 이때 `DecisionRequester`와
  `BehaviorParameters` brain 설정을 붙인다
- 고관절 외전 한계(±40°)가 클라이밍 자세에 좁은지 학습 결과로 판단
