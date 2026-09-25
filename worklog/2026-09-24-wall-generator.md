# 2026-09-24 — 랜덤 생성기를 ClimbingWall에서 분리

랜덤 생성기는 결국 버릴 코드다. 최종 시스템의 홀드는 CV 출력에서 온다.
그런데 `ClimbingWall` 안에 들어 있어서, 지우려면 벽 코드를 갈라내야
했다. 떼어냈다.

## 경계

**`ClimbingWall`** (`01Scripts/Wall/`) — 벽이 무엇인지만 안다.

- 치수(4.0 × 6.0 × 0.3 m), `BuildSlab()`
- 홀드 보유: `Holds`, `TopHold`, `StartHolds`
- `AddHold(id, wallPosition)` / `ClearHolds()`
- `WallToWorld(Vector2)`

**`RandomWallGenerator`** (`01Scripts/Testing/`) — 홀드를 어디에 놓을지
정한다.

- 레이아웃 파라미터(`firstRowHeight`, `rowSpacing`, `maxReach`,
  `sideMargin`), role별 색
- `Generate(seed)`

기준은 "벽이 CV 출력을 받게 되면 무엇이 남는가"다. 남는 것이 벽,
사라지는 것이 생성기.

## 결정

### 색은 생성기로 갔다

홀드 색은 나중에 CV가 실제 벽에서 뽑아낸다. 지금 role별로 칠하는 건
생성기의 선택이지 벽의 성질이 아니다.

### `AddHold`는 role/color를 받지 않는다

`AddHold(id, wallPosition)`이 `Hold`를 돌려주고 호출자가 role과 color를
채운다. 파라미터가 줄고, 이미 `Hold`가 그 필드를 갖고 있다.

### `Generate`는 slab을 건드리지 않는다

전에는 `Generate`가 slab까지 다시 만들었다. slab은 홀드 배치와 무관한
벽 자신의 지오메트리이고, 에피소드마다 바뀌는 건 홀드뿐이다. 생성기를
지웠을 때 slab 생성까지 딸려가면 안 되기도 한다.

### 생성기는 `ClimbingWall`과 같은 GameObject에

`[RequireComponent(typeof(ClimbingWall))]` + `GetComponent`. 참조를 손으로
연결할 것이 없다. 다른 벽을 생성할 일은 없으니 필드로 열지 않았다.

### `ClearHolds`는 리스트가 아니라 자식을 훑는다

`GetComponentsInChildren<Hold>()`로 지운다. 리스트가 어긋나거나 손으로
놓은 홀드가 있어도 남지 않는다.

## 검증

분리가 동작을 바꾸지 않았는지 숫자로 확인했다.

- 시드 12345: 홀드 9개, start (0.70, 0.70), top (1.32, 5.10), 최대 간격
  0.727 m — 분리 전과 동일
- 시드 200개: 최대 간격 0.899 m ≤ 0.9 m, 경계 이탈 0건, role 오류 0건,
  200회 재생성 동안 slab 유지
- grasp/매달림/클리어 판정 그대로

매달림 측정에서 손 드리프트가 0.15 m로 나와서 의심했는데, `ManualControl`이
붙잡기 직전까지 손을 커서 쪽으로 끌고 있었기 때문이었다. 컴포넌트를 끄고
다시 재니 0.041 m. 도구의 정상 동작이지 회귀가 아니다.

## 다음

`ClimbingAgent`. 에피소드 시작에서 `generator.Generate(seed)` +
`ragdoll.ResetBody()`를 부르고, `ManualControl`은 끈다.
