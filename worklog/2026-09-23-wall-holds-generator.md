# 2026-09-23 — 클라이밍 벽, 홀드, 랜덤 생성기

ragdoll 다음으로 Phase 1의 벽/홀드 쪽을 끝냈다. 이제 랜덤 벽을 만들고,
홀드를 잡고 매달리고, 탑을 두 손으로 잡으면 클리어가 뜬다.

## 추가된 것

- `Assets/01Scripts/Wall/Hold.cs` — id, color, role(Normal/Start/Top),
  wall-local 2D 좌표.
- `Assets/01Scripts/Wall/ClimbingWall.cs` — 벽 지오메트리 + 홀드 배치 +
  랜덤 생성기.
- `Assets/03Wall/Materials/{Wall,Hold}.mat` — URP Lit.
- Train 씬에 벽(4.0 × 6.0 m)과 두꺼운 바닥을 놓았다.

`ClimberRagdoll`의 grasp는 이제 `Vector3`가 아니라 `Hold`를 받는다.
어느 홀드를 잡고 있는지 알아야 클리어를 판정할 수 있다.

## 설계 결정

### 클리어 가능성을 rejection sampling으로 풀지 않았다

홀드를 랜덤으로 뿌리고 "너무 멀면 다시 뽑기"를 하면 루프가 언제
끝나는지 보장이 없고, 파라미터를 조이면 조용히 무한루프가 된다.

대신 구조로 막았다. 연속한 두 홀드는 **항상** 세로로 `rowSpacing`만큼
떨어져 있으므로, 가로 이동만 `sqrt(maxReach² - rowSpacing²)`로 제한하면
두 홀드 사이 거리가 `maxReach`를 넘는 일이 산술적으로 불가능하다. 벽
경계 clamp는 가로 이동을 줄이기만 하니 보장을 깨지 않는다.

시드 200개 검증: 최대 연속 간격 0.899 m (한계 0.9 m), 위반 0건.

### 머티리얼 인스턴스 대신 MaterialPropertyBlock

홀드마다 `renderer.material`을 건드리면 홀드 수만큼 머티리얼 인스턴스가
생긴다. 벽을 에피소드마다 다시 만드는 걸 감안하면 그냥 샌다.

### 벽 지오메트리를 코드로 만든다

slab을 씬에 손으로 놓고 `width`/`height` 필드를 따로 두면 둘이 어긋난다.
`Generate`가 slab과 홀드를 전부 만들게 해서 진실의 출처를 하나로 뒀다.

### start/top을 Route가 아니라 Hold에 뒀다

`docs/07`의 Scene JSON은 `start_hold_ids`/`top_hold_id`를 route에 둔다.
Phase 1은 벽 하나에 루트가 하나뿐이라 route 객체가 없어서, 역할을 홀드
자신에게 뒀다. 같은 홀드가 루트마다 다른 역할을 갖는 Phase 2에서 Route로
옮긴다. `docs/07`에 적어뒀다.

## 도중에 잡은 것

### GroundContact NullReferenceException

ragdoll이 바닥에 닿을 때마다 `GroundContact.OnCollisionEnter`가 터졌다.
`agent`를 null 체크 없이 쓰는데 아직 `Agent` 컴포넌트가 없다. 호출부마다
막지 않고 공유 지점에 early-return을 넣었다. ragdoll은 컨트롤러 없이도
동작해야 한다 — 지금은 수동 테스트, 나중엔 AR 재생.

### 물리 솔버 설정 (이게 진짜였다)

ragdoll이 바닥에 서지 못하고 다리가 바닥을 뚫고 1 m 가까이 내려갔다.
관절이 늘어나고 joint `currentForce`가 한계(20000)를 넘고 있었다.

원인은 두 개였다.

1. **솔버 반복 부족.** ClimbingBot은 Unity 기본값 6/1인데 ml-agents
   프로젝트는 12/12다. Walker ragdoll은 12/12를 전제로 튜닝된 값이다.
   프로젝트 설정을 12/12로 맞췄다.
2. **바닥이 얇았다.** 컨트롤러가 없으면 ragdoll은 주저앉는데, 이때
   slerpDrive가 T자세로 복원하려 밀면서 0.4 m 바닥을 관통했다. 2 m로
   두껍게 하니 멈췄다.

중력은 Walker의 1.5배를 따라가지 않고 실제값(-9.81)으로 뒀다. 1.5배는
보행을 덜 붕 뜨게 하려는 값이고, 우리는 사람 동작의 타당성을 본다.

`fixedDeltaTime`도 Walker처럼 0.01333으로 낮출까 했는데, 솔버 설정만으로
안정적이어서 50 Hz 그대로 뒀다. (Unity 6의 TimeManager는
`m_Count / 141120000` 유리수라 float로 쓰면 값이 깨진다. 한 번 깨뜨렸다가
복구했다.)

## 검증

Train 씬 play mode에서 `Physics.Simulate`로 확인했다.

- 생성기: 시드 200개, 홀드 9개/벽, 최대 간격 0.899 m ≤ 0.9 m, 경계 이탈
  0건, role 오류 0건
- 컨트롤러 없는 ragdoll이 바닥에 안착 (최저 지점 y 0.020,
  `touchingGround` true, 예외 없음)
- 중간 홀드를 한 손으로 잡고 2초: 손 드리프트 0.026 m, 발 공중 1.93 m
- 클리어: 왼손만 탑 → false / 왼손 탑 + 오른손 일반 → false / 양손 탑 →
  true

클리어 테스트는 `graspRadius`를 임시로 키워서 했다. 두 손을 한 홀드로
모으는 건 컨트롤러가 할 일인데 아직 없다.

## 다음

- `ClimbingAgent` — observation / action / reward, `DecisionRequester`와
  `BehaviorParameters` brain 설정
- 에피소드 시작 시 `wall.Generate(seed)` + `ragdoll.ResetBody()` 연결
- 한 행에 홀드 여러 개, 밀도/분포 파라미터 — 현재 난이도를 넘어선 뒤에
