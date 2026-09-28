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

## 매칭과 완등 조건 (같은 날, 이어서)

Unity의 완등 판정 `ClimberRagdoll.IsToppedOut`은 **양손이 top 홀드에** 있는 상태인데,
planner는 한 손만 닿으면 `reached_top`으로 끝내고 있었다. 게다가 validator가 "이미 다른
limb이 잡은 홀드"를 전부 거부해서 두 번째 손이 top에 올라갈 방법 자체가 없었다. 두 모듈의
완등 정의가 어긋나 있었던 것.

- `candidates.blocked_holds(pose, limb)` 하나로 점유 규칙을 모았다. 반대쪽 짝(손-손,
  발-발)의 홀드를 후보로 받고(매칭), 손과 발·자기 홀드는 그대로 막는다. candidate
  generator와 `validate()`가 같은 함수를 쓴다 --- 같은 규칙을 두 군데 적으면 갈라진다.
- **매칭은 한 쌍씩만.** 두 쌍이 동시에 매칭하면 네 limb이 홀드 두 개에 올라간다. 안 막으면
  greedy가 pose의 22%를 그렇게 만들었다. 막아도 완등률은 20/20 그대로고 move만 16.9 →
  21.2로 늘어난다.
- plan 루프의 종료 조건을 `any(hand on top)` → `all(hand on top)`으로 바꿨다.
- 프롬프트: (1) 양손이 top에 올라야 완등이고 마지막 두 move를 같이 계획할 것, (2) 두 손
  또는 두 발은 한 홀드를 공유할 수 있고 두 쌍이 동시에는 안 된다, (3) 위나 옆으로 옮기고
  아래로 내리는 move는 지양할 것.
  (3)은 후보에서 빼지 않았다 --- 발을 내려 딛어 엉덩이를 붙이는 move는 실제로 필요하다.
- `render`가 홀드별로 라벨을 합쳐 그린다(`LH+RH`). 매칭하면 두 라벨이 같은 점에 겹쳐서
  하필 top 홀드에서 글자가 뭉개졌다.

**측정 (greedy, 20벽, 양손 완등 기준).** 손+발 매칭 **20/20**, 손만 13/20, 매칭 없음
0/20(완등 조건을 한 손으로 낮춰도 10/20). 양손 조건 자체는 공짜였다 --- 첫 손이 top에 닿은
벽에서 두 번째 손이 전부 따라붙었다.

**발 매칭이 대부분의 차이다.** 발 step이 1.0 m로 짧아 닿는 홀드가 대개 반대쪽 발이 이미
밟고 있는 것이라, 매칭 전에는 초기 포즈에서 발에 후보가 있는 벽이 6/20뿐이었다(지금
20/20). 발이 못 움직이면 손만 올라가다 span에 걸려 순환한다 --- 발 홀드 2차 패스로
우회하려던 문제의 진짜 원인이 이쪽이었다.

docs/04의 16/20, 발 후보 11/20은 지금 `/walls`보다 앞선 숫자였다. 매칭 이전 상태로 재면
10/20, 6/20이라 `selftest`의 하한(14)도 이미 빨간 상태였다. 측정값으로 문서와 하한(18)을
고쳤다.

## one-shot으로 전면 교체: 요청 한 번, 전체 시퀀스

move 하나씩 재계획하는 루프를 **걷어내고** 요청 한 번으로 전체 move 시퀀스를 받는 방식으로
바꿨다. candidate를 프롬프트에서 빼고(문서상 planner의 핵심이던 단계), route 전체 홀드 +
시작 pose + reach 예산(`limits`)만 준다.

- `plan()`이 이제 one-shot이다. 요청 1회, 그 뒤는 재생. reach filter는 여전히 심판으로
  쓰지만 모델에게 답을 알려 주지 않는다. **첫 불가능 move에서 멈춘다** --- 재요청할 상대가
  없고, 그 뒤 move들은 일어나지 않은 pose를 전제로 쓰여 있다.
- 같이 사라진 것: `SYSTEM`(단일 move 프롬프트), `move_schema`, `history`, 재시도 루프,
  greedy fallback, pose 순환 감지. `Plan.retries`/`fell_back`도.
- `Plan` 지표를 바꿨다. `requests`/`invalid` → `proposed`(모델이 쓴 move 수) /
  `examined`(재생한 수) / `executed`. `valid_move_rate = executed / examined` --- 시퀀스가
  어긋나기까지 몇 move를 갔는지를 재는 값이다.
- `GreedyChooser`가 rollout을 자기 안에서 돌려 전체 시퀀스를 반환한다. 베이스라인이
  같은 인터페이스로 살아남았다. **단 greedy는 rollout 안에서 매 move 후보를 다시
  계산하므로 상한이지 같은 조건의 상대가 아니다** --- 문서에 적었다.
- `strict_schema()`를 재귀로 고쳤다. OpenAI strict는 배열 안 move 객체에도
  `additionalProperties: false`를 요구하고 `maxItems`는 거부한다.
- `chooser_for()` + `ALIASES`: `--model gemini` → `gemini-3.8-flash`, `--model gpt` →
  `gpt-6-sol`. 그 외 이름은 그대로 넘어간다. 기본값은 `gemini`. 기존 기본값
  `gemini-2.5-flash`는 404였다(신규 사용자에게 닫힘).
- `--out`이 move마다 PNG를 쓴다. `step00`이 초기 pose, `stepNN`이 move NN 직후 pose,
  거기에 그 pose의 후보 링이 그려진다 --- 다음 move가 왜 막혔는지가 여기서 보인다.
  `request.png`는 모델에게 실제로 보낸 이미지(후보 링 없음)로 따로 남는다. 이제 매 pose가
  파일로 남으므로 `final.png`는 지웠다.

**측정 (gpt-6-sol, wall 0\~4).** 완등 0/5. 시퀀스는 2\~6 move 버티다 어긋나고 실패는 전부
reach 초과다 --- 모델이 자기가 쓰고 있는 body를 끝까지 추적하지 못한다. greedy는 여전히
20/20(평균 21.6 move).

**주의: 같은 모델로 돌린 re-planning 루프와의 직접 비교 수치는 없다.** 위 0/5는 greedy와의
비교일 뿐이다. 교체는 그 비교 없이 한 결정이고, 문서에도 그렇게 적었다.

## hand-foot match 허용 (규칙 변경)

교체 전 실험에서 실패의 절반이 "발을 손이 잡은 홀드에 올린다"였다. 프롬프트에 금지를
명시해 봤더니 **오히려 나빠졌다**(wall 0,1이 move 1에서 실패). 모델이 계속 요구하는 것에는
이유가 있었다 --- hand-foot match는 실제 등반 기술이다. 프롬프트 추가는 되돌리고 규칙을
바꿨다.

- `blocked_holds()`가 **이동 후 서로 다른 홀드가 셋 이상인지**만 본다. 반대쪽 짝 판정과
  "한 쌍씩만"이 사라지고 세 줄이 두 줄이 됐다. 이전 규칙은 이 조건의 대용이었는데 덤으로
  hand-foot까지 막고 있었다.
- 여전히 금지되는 것은 네 limb-두 홀드 하나다. 한 쌍이 매칭 중이면 **매칭에 안 낀 두
  limb이 묶이고**(어디로 가도 두 홀드가 된다), 매칭 중인 두 limb은 자유롭게 빠져나온다.
- 과격해지지 않게 잡아 주는 것은 이미 있던 hip/shoulder line이다 --- 발은 어깨선 아래
  홀드만 취한다. wall 0에서 `right_foot -> 2`가 지금 거부되는 이유가 점유가 아니라 이것이다.
- 프롬프트가 이제 매칭을 **명시적으로 알려 준다**. "두 limb이 같은 홀드에 올라갈 수 있다"를
  대문자 규칙으로 두고, 점유는 그 자체로 피할 이유가 아니라고 못박았다.
- validator 메시지가 점유 limb 이름을 말한다 --- 예전 "already held"는 프롬프트의 매칭
  규칙과 모순되게 읽혔다.

**측정.** greedy 여전히 20/20, 평균 21.2 → 21.6 move. hand-foot match가 나타나는 pose는
4%(17/432) --- greedy는 이 기술을 거의 안 쓰므로 완등률에 기여하지 않는다. 허용한 이유는
greedy가 아니라 VLM이다.

## 문서

docs/04를 one-shot 기준으로 다시 썼다(역할 분리, VLM 입력, Structured Output, Planning
전략, 검증 규칙, 평가 지표, 구현/CLI/스키마/재생, 베이스라인). docs/07은 매칭 규칙과 "VLM
invalid output" 처리, docs/00은 Phase 2 항목과 위험 표, CLAUDE.md는 VLM 실행 명령을 고쳤다.

## 다음

- **시퀀스가 8 move쯤에서 어긋나는 것이 지금의 병목이다.** 모델이 limb 위치를 놓친다.
  프롬프트로 더 밀어 볼 여지(각 move마다 네 limb 위치를 같이 쓰게 한다)가 남아 있고,
  안 되면 긴 벽에서 구간을 나눠 요청하는 쪽이다.
- greedy가 20/20이라 완등률로는 비교가 안 된다. VLM 비교는 `valid_move_rate`와
  `repeated_limb`/`backtracks`/move 수로 한다.
- greedy는 rollout 안에서 재계획하고 VLM은 못 한다. 공정한 비교를 원하면 greedy도
  시퀀스를 한 번에 쓰고 되돌아보지 않게 만들어야 한다.
- Unity Scene JSON exporter, 그리고 target pose JSON을 ClimbingAgent에 먹이는 연결.

## reach 제한 일시 해제 → 심판 끄기 (2026-09-28)

gpt-6-sol이 0/5인데 실패가 전부 reach 초과라, 2\~6번째 move에서 재생이 끊겨 **그 뒤 시퀀스를
볼 수가 없었다.** 모델이 여덟 move쯤부터 body 추적을 놓친다는 건 알겠는데, 놓친 뒤에 뭘
쓰는지는 안 보이는 상태.

처음에는 배수 knob(`--stretch X`)으로 만들었다. 거리 한계만 늘리는 방식이라 crossing에
걸리면 100배를 줘도 안 뚫렸고(아래), 배수를 몇으로 줄지도 결국 임의였다. **불리언 하나로
바꿨다.**

- `plan(skip_filters=True)` / `--skip-filters`가 재생에서 심판을 끈다 --- route 소속,
  점유(세 홀드 바닥), reach, 자세 전부.
- 안 끄는 것은 둘: `moving_limb`이 실제 limb인지, target이 이 벽의 홀드인지. 등반 제약이
  아니라 그게 아니면 pose를 갱신할 수 없는 값이다.
- 규칙이 거부했을 move는 실행되고 `Move.forced`로 표시된다. `Plan.forced`가 세고, CLI는
  `!`와 `forced=N`, plan.json에도 move마다 들어간다.
- 프롬프트의 `limits`는 어느 쪽이든 진짜 값 그대로다. 모델에게 더 뻗으라고 말하는 옵션이
  아니라 심판만 끄는 옵션이다.
- 곁다리로 고친 것: 심판이 쓰는 후보 목록의 `max_per_limb` 절단을 없앴다. 그 값은 프롬프트
  길이용인데 지금 프롬프트에 후보가 안 들어가고, 절단된 목록으로 검사하면 일곱 번째로 가까운
  홀드가 "out of reach"가 된다. 20벽 재생에서 후보 6개 초과가 stretch 1.0에서 3번,
  1.5배에서 65번 --- stretch를 넣었으면 반드시 터졌을 자리다.

`selftest`의 `check_skip_filters`: 일부러 큰 reach model로 굴린 greedy 시퀀스를 진짜
model로 재생하면 move 1에서 멈추고(0 실행), 심판을 끄면 7 move 전부 실행되며 flag가
붙는다. 그 7개를 다시 심판 켜고 재생하면 도로 0이다 --- flag가 정직한지까지 본다. 네 limb을
한 홀드에 쌓은 pose와 route 밖 홀드도 통과하는지, limb 이름과 없는 홀드 id는 여전히
거절되는지도 같이 본다.


## stretch 100인데 out of reach (2026-09-28)

wall 7을 `--stretch 100`으로 돌렸는데 move 16이 여전히 "out of reach"로 막혔다. 재현해
보니 거리 문제가 아니었다. `left_hand`가 홀드 14(x 1.766)를 잡으려는데 `right_hand`가
홀드 13(x 1.514)에 있어서 **crossing 필터**가 막고 있었다 --- 1.766 > 1.514 + 0.25,
**2 mm 초과**다.

두 가지가 겹쳐 있었다.

- `stretched()`가 `cross_margin`을 안 늘렸다. 골반/어깨선과 같은 "자세 규칙"으로 묶어
  뒀는데, cross_margin은 미터로 된 거리 허용치다. 이제 같이 곱한다. 안 늘리는 건 골반/
  어깨선뿐 --- 그건 거리가 아니라 limb이 몸의 어느 쪽에 있느냐는 규칙이다.
- **거절 사유가 전부 "out of reach"였다.** `validate()`가 후보 목록에 있는지만 보고
  없으면 거리 문제라고 단정했다. 그래서 2 mm crossing 초과가 거리 메시지로 나왔고,
  stretch를 100으로 올려도 안 풀리는 이유를 읽을 수가 없었다.

`candidates.rejection(scene, pose, limb, hold_id, model)` 하나로 기하 필터를 모아
사유 문자열을 돌려준다. 후보 생성기와 `validate()`가 같이 읽으므로 막은 규칙의 이름이
그대로 나온다. `validate()`는 이제 후보 목록 대신 `ReachModel`을 받는다 --- 목록은
프롬프트용이라 `max_per_limb`로 잘려 있어서, 일곱 번째로 가까운 홀드를 reach 밖이라고
하던 버그도 같이 없어졌다(20벽에서 후보 6개 초과가 stretch 1.0에 3번, 1.5배에 65번).

같은 시퀀스 재생 결과:

```
심판 켬  15 move 실행, 16번에서 멈춤: crossed over right_hand: x 1.77 against 1.51, past 0.25 m
심판 끔  16 move 실행 (1개 forced), 시퀀스가 top 전에 끝남
```

거리 한계만 1.05배 늘려 보면 사유가 span으로 바뀌고, 그 다음은 또 다른 규칙이다 --- 한
번에 하나씩만 보인다. 그래서 배수 knob을 버렸다. 보고 싶은 건 "조금 더 뻗으면 되나"가
아니라 "이 시퀀스가 끝까지 뭘 쓰는가"였다.

**측정에는 쓰지 않는다.** `forced > 0`인 plan의 완등률은 아무것도 증명하지 않고,
docs/04 평가 절에도 그렇게 적었다.

## hand-foot match 금지로 되돌리고, span을 프롬프트에 숫자로 (2026-09-28)

프롬프트가 먼저 hand-foot match 금지로 바뀌어 있어서 코드와 문서를 거기 맞췄다.

- `blocked_holds`가 같은 종류끼리만 매칭을 허용한다 --- 손-손, 발-발. 네 limb-두 홀드
  금지는 그대로. 규칙 둘이 한 함수에 있고 candidate generator와 `validate()`가 같이 읽는다.
- greedy 베이스라인은 **20/20 그대로**, 평균 21.6 → 21.2 move. hand-foot이 나타나던
  pose는 4%뿐이라 greedy에는 거의 영향이 없다.
- **이건 뒤집은 결정이다.** 9/27에 이 기술을 금지했을 때 VLM이 두 벽에서 move 1부터
  실패했고, 그래서 규칙을 열었던 기록이 docs/04에 있다. 금지 상태의 VLM 수치는 아직 다시
  재지 않았다 --- 문서에도 그렇게 적어 뒀다.

span을 프롬프트에 숫자로 넣었다.

- RULES에 "매 move 후 각 손과 각 발 사이 거리 중 최대가 `limits.max_span`(2.4 m)를 넘지
  못한다"를 넣고, step과 별개의 한계라는 것(발이 그대로면 손이 1 m만 가도 걸린다)과
  걸리면 발을 먼저 올리라는 것까지 같이 썼다.
- payload의 `limits`에 `max_span`을 추가했다. 프롬프트에 박은 2.4가 `ReachModel.max_span`과
  같은지는 selftest가 본다 --- 숫자를 프롬프트에 적는 값은 이 assert 하나다.
- 재는 방식은 `_span()` 그대로다(손×발 4쌍의 최대). 손-손이나 발-발 거리는 심판이 안 보고,
  프롬프트에도 안 쓴다 --- 심판이 안 보는 규칙을 모델에게 말하면 그게 더 나쁘다.
