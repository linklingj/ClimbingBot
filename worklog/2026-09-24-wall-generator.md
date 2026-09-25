# 2026-09-24 — 벽 생성기: 분리 후 spline 방식으로 재작성

## 1. ClimbingWall에서 생성기를 떼어냈다

랜덤 생성기는 결국 버릴 코드다. 최종 시스템의 홀드는 CV 출력에서 온다.
벽 안에 있으면 지울 때 벽 코드를 갈라내야 해서 `01Scripts/Testing/`으로
뺐다. 경계는 "벽이 CV 출력을 받게 되면 무엇이 남는가"로 잡았다 —
`ClimbingWall`은 치수·slab·홀드 보유·좌표 변환, 생성기는 홀드를 어디에
놓을지와 role별 색. 색은 나중에 CV가 실제 벽에서 뽑으므로 벽의 성질이
아니다. `Generate`는 slab을 건드리지 않는다 — 생성기를 지울 때 slab
생성이 딸려가면 안 된다.

## 2. 배치를 spline 방식으로 갈아엎었다

`Generate(seed)`는 이제 knot 2~4개짜리 spline을 만들고, 그 위에
`SplineInstantiate`로 홀드를 뿌린 뒤 위치만 읽어 진짜 홀드로 굽는다.
spline의 위 끝은 항상 top 홀드다.

**start 홀드는 더 이상 최하단이 아니다.** spline이 `splineBottomY`(0.5 m)
까지 내려오므로 start 밑에도 홀드가 생긴다.

start/top은 **높이만 고정**이다. top의 x는 랜덤, start의 x는 spline이
`startHoldY`를 지나는 지점에서 읽는다 — 그래야 start가 루트 옆이 아니라
곡선 위에 놓인다. 곡선을 256등분해 교차 구간을 선형 보간한다. 실측 이탈
1.5 mm면 홀드 반지름 7 cm에 비해 무시할 수준이라 해석적으로 안 풀었다.

### 막힌 곳 두 개

`SplineInstantiate`의 인스턴스는 `HideAndDontSave`이고 컴포넌트가 수명을
관리한다. 씬에 저장되지도 않고 우리가 붙인 id/role/color도 못 지킨다.
그래서 배치 결과를 위치로만 받고 인스턴스는 버린다(bake). 벽은 직렬화되는
진짜 홀드를 갖는다.

그리고 position offset의 축별 randomize 토글이 비공개다. 켜지 않으면
오프셋이 **min 값으로 고정**되고 `Seed`도 무시된다 — 시드를 바꿔도
레이아웃이 안 변해서 한참 헤맸다. 리플렉션으로 `m_PositionOffset.randomX`
를 켰다. 버릴 코드라 감수한다.

### 간격 보장

홀드 간격은 `minHoldSpacing`~`maxHoldSpacing`에서 홀드마다 뽑는다 —
`SplineInstantiate`의 `MinSpacing`/`MaxSpacing`이 숨은 토글 없이 바로
랜덤화해준다(오프셋 쪽과 달랐다). 지터는 넓은 쪽 기준으로 clamp해야
하므로 `maxHoldSpacing + 2 × jitter ≤ maxReach`를 쓴다.

그런데 start/top은 지정된 높이에 놓이므로 대체된 spline 홀드보다 이웃과
멀어질 수 있고, 실제로 150시드 중 1번 새어나갔다. 남은 간격은 중점에
홀드를 끼워 반으로 접는 패스를 붙였다. 이제 구조적으로 `maxReach` 이하다.

## 검증

시드 300개(3345 간격): 홀드 10~16개, 간격 0.176~1.014 m가 고르게 퍼지고
한계 초과 0건, knot 2/3/4 고르게 분포, start가 최하단인 경우 0건, role
오류 0건, start/top 높이 고정 오차 0건, start가 곡선에서 벗어난 거리 최대
1.5 mm. grasp/매달림(손 드리프트 0.035 m)/클리어 판정 모두 그대로.

## 다음

`ClimbingAgent`. 에피소드 시작에서 `Generate(seed)` + `ResetBody()`를
부르고 `ManualControl`은 끈다.
