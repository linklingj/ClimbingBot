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

## step-by-step 루프를 기본으로 되살리고 one-shot을 `--oneshot`으로 (2026-09-28)

`feature/vlm-oneshot-planner`를 develop에 머지하면서 **두 전략을 다 남겼다.** 기본은
move마다 재계획하는 루프(`plan_steps`)이고, `--oneshot`이 요청 한 번에 전체 시퀀스를
받아 재생하는 쪽(`plan_oneshot`)이다.

**왜.** one-shot으로 갈아탄 근거가 없었다. docs/04에 적힌 유일한 비교가 gpt-6-sol
0/5 대 greedy 20/20인데, greedy는 매 move 후보를 다시 계산하는 쪽이라 애초에 같은
조건이 아니다. 같은 모델로 루프를 돌린 수치는 이 저장소에 없다. 지울 이유가 없으면
남긴다.

무엇을 어떻게 나눴나.

- **planner.py 하나에 둘 다 있다.** `SYSTEM_STEPS` / `SYSTEM_ONESHOT`,
  `move_schema` / `plan_schema`, `step_payload` / `build_payload`, `plan_steps` /
  `plan_oneshot`. 공유하는 것은 `validate()`, `Move`, `Plan`, target pose 직렬화,
  `_topped_out`. 모듈을 쪼개면 `Move`/`Plan`을 두 번 쓰게 돼서 한 파일로 뒀다.
- **`Plan.mode`** 가 `"steps"` / `"oneshot"`을 들고 있고 `plan.json`에도 들어간다.
  `valid_move_rate`의 의미가 모드마다 다르기 때문에 --- one-shot은 재생한 move 중
  실행된 비율, 루프는 요청 중 유효한 답이 온 비율이다. 섞어 비교하면 안 되는 숫자라
  모드를 같이 적게 했다. 카운터는 union으로 뒀다(`requests`/`invalid` 대
  `proposed`/`examined`), 안 쓰는 쪽은 0이다.
- **`GreedyChooser`가 두 스키마를 다 답한다.** `schema`에 `moves` 배열이 있으면
  자기 rollout으로 시퀀스를, 없으면 move 하나를 돌려준다. 점수 계산(`_best`)은 한
  군데다. 루프의 fallback도 이 클래스 그대로다.
- 루프 쪽 규칙은 **머지된 새 규칙을 따른다** --- 매칭 허용, 양손 완등, `rejection()`
  기반 사유. 옛 루프의 "홀드 하나에 limb 하나" / "한 손이 top이면 완등"으로 되돌리지
  않았다. 그 규칙으로는 greedy가 0/20이고(docs/04 표), Unity의 `IsToppedOut`과도
  어긋난다. 루프가 다시 필요한 것은 **요청 방식**이고 규칙이 아니다.
- `--skip-filters`는 one-shot 전용이다. 루프에서는 후보 목록이 곧 모델의 선택지라
  심판을 끄면 고를 것이 없다. CLI가 조합을 거부한다.
- `validate()`에 "후보 목록에 있는지"를 다시 넣지 않았다. 목록은 `max_per_limb`로
  잘려 있어서 그걸로 검사하면 닿는 홀드를 거절한다 --- 루프에서도 심판은 기하학이다.

검증(오프라인, 모델 호출 없음): `selftest`에 `check_steps`(루프를 greedy로 20벽)와
`check_step_prompt`(payload/schema/프롬프트)를 추가했다. **두 모드 모두 20/20 완등**,
루프는 요청 16~18회에 invalid 0, fallback 0회.

다음: 같은 벽 다섯 개를 `--oneshot` 유/무로 한 번씩 돌려 `valid_move_rate`와 완등률을
나란히 적는다. 그게 있어야 기본값을 정한 근거가 생긴다. 호출 수는 CLAUDE.md의 API
호출 주의를 따른다(벽 하나 먼저, 그 다음 승인).

## 자세 규칙 교체, 제한 완화, 프롬프트 축약 (2026-09-28, wall 8 결과 보고)

wall 8 실행 결과에서 나온 셋 --- 발이 손보다 위(step 5), 발 없이 손만 뻗음(step 3),
다시 내려감(step 7) --- 과 "위로 갈 후보가 없는 pose"를 고쳤다. 원인 진단부터.

wall 8은 홀드가 9개뿐인 거의 수직선이고 세로 간격이 0.64\~0.93 m다. 25개 pose 중
**4개에서 위로 가는 후보가 0개**였고 막은 규칙은 이랬다.

```
pose 10  left_hand→7,8   crossed over right_hand (0.83 m 초과, 허용 0.25)
         right_hand→7    out of reach 1.57 m (한계 1.40)
         left_foot→5     above the shoulder line (y 3.06, 어깨선 3.07)   ← 0.01 m
pose 12  right_foot→5    above the shoulder line (y 3.06, 어깨선 3.14)   ← 0.08 m
```

- **hip/shoulder line을 지웠다.** 네 접점의 *중심*에서 torso 절반만큼 위/아래로 그은
  선이라, 손을 기준으로 재지 않는다. 그래서 (a) 손이 3.06에 있는데 발이 3.06으로
  가는 것을 통과시키고(= step 5의 "발이 손 위"), (b) 위 표처럼 1\~8 cm 넘은 발을
  거부해 유일한 상승 경로를 지운다. 같은 버그의 양쪽이다.
- 대신 `rise(scene, pose)` = **가장 낮은 손 − 가장 높은 발**을 재고 `min_rise`(0.25 m)
  아래면 거부한다. 단 **절대 하한이 아니다** --- 초기 pose의 rise가 0.03 m인 벽이 있어서
  (20벽 최소값), rise를 더 나쁘게 만들지 않는 move는 통과시킨다. 안 그러면 시작부터
  갇힌다. `anchors()`와 `ReachModel.torso/shoulder_half/hip_half`는 같이 지웠다.
- **손은 아래로 안 간다**(`hands do not climb down`). 발은 그대로 허용한다 --- pose 10의
  탈출로가 "발을 3으로 내려 발 매칭을 풀고, 손을 6에 매칭, 그 다음 발을 올린다"였다.
  발의 하강을 막으면 그 벽이 막힌다. 그래서 규칙은 손에만 걸었다.
- **직전 move 되돌리기를 후보에서 뺀다**(`plan_steps`). 규칙은 pose 하나만 보므로 방금
  뗀 홀드를 아는 것은 planner뿐이다. 바로 다음 move만 막는다 --- 두 move 뒤의 복귀는
  `Plan.backtracks`가 세고, 그 수가 문제가 될 때 더 긴 history 필터를 본다.
- **제한 완화**: 손 step 1.4→1.6 m, 발 1.0→1.2 m, crossing 0.25→0.5 m. step은 어깨에서
  재는 reach가 아니라 홀드에서 홀드까지의 거리라 1.6 m가 큰 move 하나에 해당한다.
  span(2.4 m)은 **그대로 뒀다** --- 손만 먼저 뻗는 것을 막아 발을 먼저 올리게 하는
  지렛대가 이것이다. 실제로 wall 8 pose 12에서 손이 7로 가려면 span 2.86이 걸리고,
  발을 5로 올린 뒤에야 1.57로 통과한다. 이게 의도한 동작이다.

**프롬프트를 절반 아래로 줄였다** (각 45줄 → 23줄). 심판이 강제하는 규칙을 길게
변호할 필요가 없다 --- 규칙은 한 줄, 분량은 "어떻게 고르는가"에 쓴다. one-shot
프롬프트에는 "손은 아래로 안 간다"와 "가장 낮은 손이 가장 높은 발 위"를 새로 넣었다
(후보 목록이 없으니 모델이 직접 지켜야 한다). 2.4라는 숫자가 `max_span`과 같은지 보는
selftest assert는 그대로 통과한다.

wall 8 재생(greedy, 오프라인): 상승 후보 없는 pose 4개 → 0개(마지막 pose는 top 뿐이라
제외), 발이 손 위인 pose 0개, 24 move → 18 move. 20벽 평균은 21.6 → 15.8 move,
완등은 두 모드 모두 20/20 그대로. **모델로는 아직 안 돌렸다 --- 사용자가 확인한다.**

## crossing 금지 (2026-09-28, wall 8 step 6)

step 6이 네 limb 전부 좌우가 바뀐 자세였다(왼손이 오른손 오른쪽, 왼발이 오른발 오른쪽).
`cross_margin`을 0.5 m로 풀어 둔 것이 직접 원인이다 --- 규칙이 "반대쪽 limb를 0.5 m까지
지나쳐도 된다"였고, 두 limb이 각각 그만큼 지나치면 pose는 완전히 뒤집힌다. 20벽 greedy
재생에서 **pose의 30%(101/335)가 좌우가 바뀐 상태**였다.

`cross_margin`을 **0으로** 조였다. 왼쪽 limb은 오른쪽 limb의 왼쪽(또는 같은 홀드,
매칭이면 x가 같다)에만 있을 수 있다. 필드는 남겼지만 이제 tolerance knob이고 기술
스위치가 아니다 --- cross-through를 허용하려면 꼬인 자세에서 빠져나올 순서를 계획할 수
있는 planner가 필요하고, step-by-step에는 backtracking이 없다.

**대가는 dead end다.** greedy가 20/20 → 14/20으로 떨어졌다. 전부 "후보 0개"이고 사유는
하나다 --- 옆으로 흐르는 홀드 줄을 올라가려면 **뒤에 있는 limb을 먼저** 보내야 하는데,
greedy는 눈앞의 이득만 보므로 그 순서를 계획하지 못한다. 확인한 것들:

- **BFS로 20벽 전부 여전히 풀린다** (10\~15 move, greedy 평균 15.8보다 짧다). 즉 막은
  것은 규칙이 아니라 greedy다. 이 BFS를 `selftest.check_solvable`로 넣었다 --- 규칙을
  조일 때 벽이 실제로 막히는지 보는 유일한 검사다. greedy 완등률은 그 역할을 못 한다.
- **한 move lookahead는 도움이 안 됐다**(14/20 그대로). dead end가 한 move보다 깊다.
  그래서 greedy에는 아무것도 안 넣었다 --- 베이스라인이지 제품이 아니다.
- **발 step 1.2 → 1.3 m.** wall 13의 dead end가 1.25 m짜리 발 move 하나 때문이었다.
  이걸 풀어 16/20이 됐다. span(2.4 m)은 **안 건드렸다** --- 손만 먼저 뻗는 것을 막는
  지렛대라서, greedy 점수 때문에 풀 값이 아니다.

selftest의 greedy 하한을 18 → 15로 내렸다(현재 step-by-step 16/20, one-shot 19/20).
이건 beta 품질 하한이고 규칙 하한은 `check_solvable`이다. greedy plan의 모든 pose에
대해 좌우가 바뀌지 않았는지도 같이 assert한다.

프롬프트 두 곳에 crossing 한 줄씩 넣었다. one-shot 쪽에는 "옆으로 흐르는 줄을 올라가려면
뒤에 있는 limb을 먼저 보내라"까지 --- 후보 목록이 없으니 모델이 그 순서를 직접 만들어야
한다. **모델로는 안 돌렸다, 사용자가 확인한다.**

## beta 품질 세 가지를 프롬프트로 (2026-09-28, wall 8 step 9/12/14)

같은 실행에서 나온 셋. 전부 **가능한 move**라 후보 필터로 내리지 않고 프롬프트로 잡았다
(docs/04 "프롬프트에만 있고 강제하지 않는 것").

- **step 9→10** --- 오른발이 낮은데 왼발을 무리하게 올렸다.
- **step 14** --- 이미 높은 손으로 또 뻗었다. 오른손을 먼저 올려 짝을 맞춰야 할 자리다.
- 위 둘은 같은 규칙 하나다: **뒤처진 limb부터.** 손 둘 중, 발 둘 중 낮은 쪽이 먼저 간다.
  프롬프트 CHOOSE 절 첫 줄로 올렸다(대문자).
- **step 12** --- 10번(가까움)을 두고 11번(멂)으로 갔다. **위로 갈 때 가까운 홀드부터**를
  두 번째 줄로 넣었다. "짧은 move로 균형을 유지하는 게 길게 뻗는 것보다 낫고, 높은 홀드는
  다음 move에도 그 자리에 있다"까지 같이 썼다.

규칙으로 내리지 않은 이유: 큰 move가 실제로 필요한 벽이 있다. 홀드가 드문 벽에서 가까운
것만 잡게 하면 위로 갈 후보가 사라진다 --- wall 8에서 이미 겪은 문제고, step 한계를
1.6/1.3 m로 푼 것이 그 대응이었다.

**코드는 한 군데 건드렸다.** 후보 목록을 프롬프트에 넘기는 순서를 현재 홀드에서 가까운
것부터로 바꿨다. 자르는 순위(top까지의 거리, 쓸 만한 것을 남긴다)는 그대로다. 프롬프트가
"가장 가까운 것을 잡아라"라고 말하는데 목록이 가장 먼 것부터 나열하고 있었다 --- 규칙이
아니라 표현의 문제다.

프롬프트 길이: step 27줄, one-shot 34줄(규칙이 셋 늘어서 각각 3\~4줄 늘었다).
greedy는 후보 순서를 안 보므로 오프라인 수치는 그대로다(step-by-step 16/20, one-shot
19/20, `check_solvable` 10\~15 move). **모델로는 안 돌렸다 --- 사용자가 확인한다.**

## `reason` 필드 제거 (2026-09-28)

사용자 요청으로 move의 `reason`을 없앴다. 스키마(둘 다), `Move`, `plan.json`, CLI 출력,
greedy의 고정 문구, selftest의 canned move까지 전부. 스키마에는 이제 `moving_limb`과
`target_hold_id`뿐이다.

한동안 `reason`을 스키마 **첫 필드**로 두어 모델이 commit 전에 한 줄 쓰게 했었다. 그
자리가 같이 없어졌으니, 계획 품질이 떨어지면 여기를 먼저 의심할 것 --- 되살릴 때는
`propertyOrdering`에서 첫 필드로 돌려놓아야 효과가 있다. docs/04에 적어 뒀다.
