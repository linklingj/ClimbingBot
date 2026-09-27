# VLM planner (Phase 2 착수)

`src/vlm/` 신규. docs/04 + docs/07을 코드로 옮겼다. Unity 없이 도는 파이썬 패키지이고,
모델은 Gemini structured output이지만 갈아끼울 수 있게 한 군데로 모았다.

## 무엇을

- `scene.py` --- docs/07 Scene JSON 자료형과 `random_wall(seed)`.
- `candidates.py` --- Candidate Generator, `ReachModel`, `initial_pose`.
- `render.py` --- VLM 이미지 입력(홀드 id, 후보 노란 링, body overlay). PIL.
- `providers.py` --- `MoveChooser` 프로토콜 + `GeminiChooser` / `OpenAIChooser` + `GreedyChooser`.
- `planner.py` --- 시스템 프롬프트, 동적 스키마, `validate()`, plan 루프.
- `__main__.py` / `selftest.py` --- CLI와 오프라인 검증.

## 왜 그렇게

**모델 교체점을 메서드 하나로.** `choose(system, payload, schema, image_png) -> dict`.
프롬프트도 스키마도 planner가 만들고 chooser는 호출만 한다. Gemini와 OpenAI 둘 다 들어
있고, 다른 모델은 `providers.py`에 클래스 하나 추가하고 `plan()`에 넘기는 것이 전부다.
스키마는 한 벌만 쓰고, OpenAI strict 방언으로의 변환(`strict_schema()`)만 provider 쪽에
둔다 --- planner가 provider별 스키마를 들고 있기 시작하면 교체점이 두 군데가 된다.

**합성 벽을 파이썬으로 다시 썼다.** Unity generator를 쓰면 루프를 돌릴 때마다 에디터가
필요하다. docs/00 7장이 Phase 2를 합성 Scene JSON으로 개발하라고 하고, 둘 다 Phase 3에서
CV 출력으로 교체될 코드다. 실제 배치 평가는 Unity에서 Scene JSON을 내보내 `--scene`으로.

**단일 move + 재계획만.** multi-move 스키마는 두지 않았다. 짧은 horizon이 문서의 우선
전략이고, 시퀀스는 루프가 쌓는다. 스키마가 하나면 검증도 하나다.

**스키마로 limb/hold를 enum 강제.** hold id는 string이다 --- Gemini가 받는 JSON schema
subset이 문자열 enum만 열거한다. 스키마는 둘의 *짝*은 강제하지 못하므로 docs/04의 검증
네 가지는 그대로 남겼다.

## 문서 대비 달라진 결정

- Candidate Generator의 reachability를 "거리 threshold" 한 줄 대신 네 조건으로 구현했다
  (step / hip·shoulder line / crossing / max span). span 조건은 문서에 없던 것인데,
  step만으로는 발이 그대로인 채 손만 위로 멀어지는 자세를 못 막는다. docs/04에 추가.
- `ReachModel` 값(손 1.1 m, 발 1.0 m, span 2.4 m)은 1.7 m 인체 기준 추정치다. ragdoll로
  실측하지 않았고, RL과 어긋나면 여기부터 조정한다.
- planner에 순환 감지(같은 pose 3회)와 greedy fallback을 넣었다. 문서의 "실패 표시"를
  planner가 실제로 판정할 수 있는 형태로 옮긴 것.

## 검증

`PYTHONPATH=src python -m vlm.selftest` --- 생성(결정성·연결성·JSON 왕복), 후보(reach·중복·
루트 소속), validator 7케이스, 루프(greedy로 12벽), 렌더까지 통과.

## 프롬프트 2차 (첫 실제 호출 이후)

gpt-6-luna로 seed 3을 돌린 결과(`out/test1-seed3`)는 완등했지만 beta가 나빴다. 39 move 중
같은 limb 연속 7회, 방금 뗀 홀드로 되돌아가기 8회, 그리고 네 limb이 한 뼘 안에 뭉치는
자세(step 14). valid move rate는 1.00이었다 --- **기존 지표로는 전부 정상이었다.**

- `Plan.repeated_limb` / `Plan.backtracks` 추가. 프롬프트 비교는 이 두 숫자로 한다.
- payload에 `history`(직전 3 move) 추가. 모델은 호출마다 현재 pose만 보므로 "같은 limb을
  연속으로 움직이지 마라"는 프롬프트만으로는 **지킬 방법이 없었다.** 규칙을 쓰기 전에
  정보를 먼저 줘야 했다.
- 시스템 프롬프트를 클라이머의 판단 순서로 다시 썼다: 삼각형 지지 → 뭉치지 않기 →
  발 먼저/번갈아 → 선 자세에서 뻗기 → 한 수 앞.
- `random_wall`에 발 홀드 2차 패스(Unity `AddFootHolds` 포팅). hand line 하나면 발 후보가
  거의 없어서(12벽 중 1) 모델이 발을 왔다 갔다 시킬 수밖에 없었다. 지금은 5.
- 생성기 기본값을 Unity 프리팹 값에 맞췄다 (spacing 0.3\~0.8, maxReach 1.4, sideMargin 1,
  footDrop 1.0). `MAX_REACH`는 `scene.py` 상수 하나로 두고 selftest가 그걸 쓴다.

강제하지 않고 프롬프트에 맡긴 부분: 같은 limb 연속 이동은 물리적으로 가능한 move라
candidate generator에서 빼지 않았다. 프롬프트로 안 잡히면 그때 후보에서 제외한다.

## 다음

- 새 프롬프트로 seed 3을 다시 돌려 `repeated_limb 7 / backtracks 8`과 비교한다.
- 발 후보가 5/12에 그친다. 프롬프트가 "발 먼저"를 지키려 해도 후보가 없으면 못 지킨다 ---
  다음 후보는 generator 쪽(발 라인을 더 촘촘히)이나 `foot_step`이다.
- greedy가 막히는 5개 벽(후보 고갈)이 VLM이 이겨야 할 지점이다.
- Unity Scene JSON exporter, 그리고 target pose JSON을 ClimbingAgent에 먹이는 연결.
