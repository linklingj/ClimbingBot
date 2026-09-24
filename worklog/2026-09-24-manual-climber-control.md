# 2026-09-24 — 수동 조작 테스트 도구

`ClimbingAgent`를 쓰기 전에, 벽·홀드·grasp를 사람이 직접 만져볼 수 있게
만들었다.

## 무엇

`Assets/01Scripts/Testing/ManualClimberControl.cs`

- Q/W/A/S — 왼손/오른손/왼발/오른발 선택 (몸 배치 그대로)
- 마우스 — 선택한 limb을 커서 쪽으로 끌어당긴다
- 좌클릭 grasp / 우클릭 release / R 리셋
- 화면 좌상단에 선택 limb, 잡고 있는 홀드, TOPPED OUT 표시

Train 씬에 `ManualControl` 오브젝트로 붙였고 카메라를 벽이 보이는
`(0, 3, -8.5)`로 옮겼다.

## 왜 Agent.Heuristic()이 아닌가

요청대로 학습 행동과 완전히 분리했다. 이 도구는 limb의 Rigidbody에 raw
force를 걸어 끄는 방식이라, `ClimbingAgent`가 내보낼 joint target
rotation과는 아무 관계가 없다.

`Heuristic()`에 넣으면 조작 스키마가 agent의 action space에 묶여서,
나중에 action space를 정할 때 이 테스트 코드가 제약이 된다. 별도
MonoBehaviour면 Agent를 붙일 때 컴포넌트만 끄면 된다. 클래스 주석과
`docs/05`에 적어뒀다.

## 결정

### grasp/release와 R 리셋을 넣었다

요청은 limb 선택과 마우스 조준까지였다. 클라이밍 벽에서 "플레이"하려면
잡아야 하고, 떨어진 뒤 다시 시작할 방법이 없으면 도구가 한 번 쓰고
끝난다. 둘 다 이미 있는 API(`Grasp`/`Release`/`ResetBody`)를 키에
연결한 것뿐이다.

### grasp 중에는 reach force를 걸지 않는다

`FixedJoint`와 힘이 서로 싸우기만 한다. 한 줄로 막았다.

### 조준 평면

커서는 **선택한 limb을 지나는, 벽과 평행한 평면**에 맞춘다. 화면 위치가
limb 자신의 깊이에 있는 목표점으로 매핑돼서, 팔이 벽 쪽으로 빨려들거나
카메라 쪽으로 딸려나오지 않는다.

### `Selected`와 `GraspNearestHold`를 public으로 열었다

스크립트에서 검증하려고 열었다. 테스트 도구라 숨길 이유가 없고, 손으로
시나리오를 재현할 때도 쓸 수 있다.

## 검증

play mode에서 확인했다.

- 조준: 화면 중앙 → 카메라 x/y + limb 깊이 `(0, 3, -0.58)`. 좌/우, 상/하
  매핑 방향 정상.
- grasp: 홀드에서 멀 때 실패, 손을 홀드에 올리면 가장 가까운 홀드(id 2)를
  잡음. `graspRadius`를 그대로 따른다.
- reachForce 보정: 한 손으로 매달린 채 반대 손을 1초간 위로 뻗었을 때
  100 → 0.113 m, **400 → 0.365 m**, 1200 → 0.451 m. 400을 기본값으로 뒀다.
  1200은 이득이 거의 없다 — 관절 한계가 먼저 걸린다.

키 입력 자체는 스크립트로 검증하지 못했다. 에디터가 포커스를 잃으면
Input System이 동기 이벤트를 소비하지 않는다. 손으로 눌러봐야 한다.

주의: 이 프로젝트는 새 Input System 전용(`activeInputHandler: 1`)이라
구 `UnityEngine.Input` API는 예외를 던진다.

## 다음

`ClimbingAgent` — observation / action / reward. 붙일 때 `ManualControl`
오브젝트를 끌 것.
