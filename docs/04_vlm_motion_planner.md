# 모듈 4 --- Candidate Generator & VLM High-Level Planner

## 목적

현재 신체 상태와 루트 정보를 기반으로 다음에 움직일 limb와 target hold를
결정하고 이를 연속적인 pose sequence로 만든다.

## 역할 분리

``` text
Candidate Generator
    "무엇이 가능한가?"
          ↓
VLM
    "무엇을 선택할 것인가?"
```

Candidate Generator는 물리적으로 명백히 불가능한 move를 제거한다. 후보
목록을 **프롬프트에 넣을지는 planning 전략에 따라 갈린다** --- 기본
(step-by-step)은 그 pose의 후보를 프롬프트와 schema enum에 넣고, one-shot은
route 전체 홀드와 reach 예산만 주고 후보를 심판으로만 쓴다. 어느 쪽이든
출력 **검증**은 같은 후보 규칙으로 한다. 아래 "Planning 전략" 참고.

## Candidate Generator

### 입력

-   현재 body/root position
-   limb 위치
-   현재 각 limb의 grasp hold
-   신체 비율
-   route hold positions

### 출력

``` json
{
  "left_hand": [7, 8, 10],
  "right_hand": [8, 10, 12],
  "left_foot": [2, 4],
  "right_foot": [3, 4, 5]
}
```

### 초기 reachability

초기 버전에서는 limb root 또는 현재 body state로부터 거리 threshold를
계산한다. 구현(`src/vlm/candidates.py`)은 후보 하나당 아래를 본다.

-   **점유** --- **매칭은 같은 종류의 limb 사이에서만 허용한다**
    (`blocked_holds`): 손-손, 발-발. **hand-foot match는 금지한다.**
    자기가 이미 잡은 홀드도 후보가 아니다. 여기에 더해 **네 limb이 홀드 두
    개에 올라가는 자세**를 막는다 --- 매달릴 수 있는 자세가 아니다. 그래서
    `blocked_holds`가 보는 것은 둘이다: 같은 종류인가, 그리고 이동 후 서로
    다른 홀드가 셋 이상인가.

    hand-foot match는 실제 등반 기술이고 한동안 허용했었다. 프롬프트가
    금지로 돌아섰고(2026-09-28) 코드와 이 문서를 거기 맞췄다. 되돌릴 거면
    아래 one-shot 절의 측정을 먼저 볼 것 --- 금지가 더 나빴다는 기록이 있다.
-   **step** --- 그 limb의 현재 홀드에서 target까지 거리. 손 1.6 m, 발 1.3 m.
-   **hands don't climb down** --- 손은 지금 잡은 홀드보다 낮은 홀드로 가지
    않는다. 발은 갈 수 있다 --- 발을 내려 딛어 엉덩이를 벽에 붙이는 move가
    실제로 필요하고, 좁은 벽에서는 발을 내려 매칭을 풀어야 손이 올라간다.
-   **stance (`rise`, `min_rise` 0.25 m)** --- 이동 후 **가장 낮은 손이 가장
    높은 발보다** `min_rise` 이상 위에 있어야 한다. 절대 하한은 아니다 ---
    초기 pose가 이미 그 아래인 벽이 있어서(최소 0.03 m), **rise를 더
    나쁘게 만들지 않는 move는 통과시킨다.** 안 그러면 시작부터 갇힌다.

    이것이 이전의 **hip/shoulder line을 대체했다.** 그 선은 네 접점의
    중심에서 torso의 절반만큼 위/아래였는데, 손을 기준으로 잰 것이 아니라
    중심을 기준으로 잰 것이라 **발이 손과 같은 높이에 있어도 통과했고**
    (wall 8 step 5가 그 경우다) 반대로 2 cm 넘은 발을 거부해 위로 갈
    후보를 지웠다. 같은 버그의 양쪽이다. `anchors()`와 `ReachModel`의
    `torso`/`shoulder_half`/`hip_half`는 같이 지웠다.
-   **crossing** --- **왼쪽 limb은 오른쪽 limb의 왼쪽에 있어야 한다**
    (`cross_margin` 0 m). 같은 홀드를 공유하는 것은 crossing이 아니다(x가
    같다). 2026-09-28에 0.5 m 허용에서 0으로 조였다 --- 0.5 m면 pose의
    30%(101/335)가 좌우가 바뀐 상태였고, wall 8 step 6이 네 limb 모두
    뒤바뀐 자세였다. `cross_margin`은 이제 tolerance knob이고 기술 스위치가
    아니다. cross-through를 허용하려면 **꼬인 자세에서 빠져나올 계획을
    세울 수 있는** planner가 필요한데, step-by-step에는 backtracking이 없다.
-   **span** --- 이동 후 손-발 최대 거리가 `max_span`(2.4 m)을 넘지 않는다.
    step만으로는 발이 그대로인 채 손만 멀어지는 자세를 못 막는다.

`ReachModel`의 이 값들은 1.7 m 인체 기준 추정치이고 ragdoll로 실측한 것이
아니다. **RL이 통과시킨 move를 generator가 거부하거나 그 반대가 반복되면
여기부터 손본다.**

**2026-09-28에 step을 풀었다** (손 1.4→1.6 m, 발 1.0→1.3 m). 홀드가 드문
벽에서 **위로 가는 후보가 아예 없는 pose**가 나왔기 때문이다 --- wall 8의
25개 pose 중 4개가 그랬고, 거기서 계획은 옆으로/아래로 갈 수밖에 없다.
손 1.6 m는 "홀드에서 홀드까지"의 거리이고 어깨에서 재는 reach가 아니라, 큰
move 하나에 해당한다. 발의 1.3 m도 같은 이유다 --- crossing을 조인 뒤
wall 13이 **1.25 m짜리 발 move 하나** 때문에 막혔다.

crossing은 같은 날 반대로 조였다(위). 둘이 부딪히는 자리가 있다 --- 제한을
풀면 위로 갈 후보가 생기고, crossing을 조이면 그 후보가 줄어든다. 그래서
**규칙을 바꿀 때마다 벽이 여전히 풀리는지 BFS로 확인한다**
(`selftest.check_solvable`). 지금은 20벽 전부 10\~15 move로 풀린다.

추후 개선: - limb별 reach ellipse - torso orientation 반영 - joint limit
기반 IK feasibility test - learned reachability model

## VLM 입력

VLM에는 두 종류의 정보를 함께 제공한다.

### 1. 이미지

-   전체 벽 이미지
-   선택된 route 강조 및 홀드 id 오버레이
-   현재 body pose overlay

### 2. Structured JSON

``` json
{
  "goal": {
    "top_hold_id": 21
  },
  "body": {
    "left_hand": 8,
    "right_hand": 10,
    "left_foot": 3,
    "right_foot": 5
  },
  "holds": [
    {"id": 12, "position": [0.4, 1.8]},
    {"id": 14, "position": [0.8, 2.0]}
  ],
  "limits": {
    "hand_step": 1.4,
    "foot_step": 1.0
  }
}
```

위는 one-shot(`--oneshot`)의 payload다. `candidates`도 `history`도 주지
않는다 --- 요청이 한 번뿐이라 직전 move라는 것이 없고(모델이 history를 스스로
쓰는 중이다), 무엇이 닿는지는 `limits`와 홀드 좌표로 모델이 직접 따져야 한다.
**`limits`는 매 move마다 그 limb이 그 시점에 있는 홀드부터 재는 거리다.**
시퀀스 중간에 초기화되지 않는다는 것을 프롬프트가 못박는다.

**step-by-step(기본)의 payload는 다르다**(`planner.step_payload`). 한 move만
고르면 되므로 `limits` 대신 그 pose의 `candidates`를 주고, 직전 세 move를
`history`로 붙인다 --- 요청마다 세계가 새로 시작하므로 이게 없으면 모델은
자기가 발 하나를 왔다 갔다 하고 있다는 것을 알 수가 없다. 거절당한 답을 다시
물을 때는 `previous_answer_rejected`에 사유가 들어간다.

## Structured Output

one-shot의 출력은 move 배열 하나다.

``` json
{
  "moves": [
    {"moving_limb": "left_hand", "target_hold_id": 14},
    {"moving_limb": "right_foot", "target_hold_id": 8},
    {"moving_limb": "right_hand", "target_hold_id": 18}
  ]
}
```

출력 schema에서 limb enum과 **route 전체** hold ID를 강제한다 --- 좁힐
candidate set이 프롬프트에 없으므로 enum은 route의 모든 홀드다.

step-by-step은 move 객체 **하나**를 받고(`planner.move_schema`), enum이 그
pose의 후보다 --- 후보가 있는 limb와 그 limb들의 후보 홀드 id뿐이다. schema가
limb와 id를 각각 강제할 뿐 둘의 짝은 강제하지 못하므로 어느 쪽이든
`validate()`가 그대로 필요하다.

## Pose State

각 move 수행 후 새로운 pose를 만든다.

``` json
{
  "left_hand": 14,
  "right_hand": 10,
  "left_foot": 3,
  "right_foot": 5
}
```

## Planning 전략

**전략이 두 가지 있고 둘 다 유지한다.** 어느 쪽이 나은지 같은 모델로 재 본
기록이 아직 없다(아래 "one-shot 수치와 비교되지 않은 것"). 기본은
step-by-step이고 `--oneshot`이 one-shot이다.

### step-by-step (기본, `plan_steps`)

move 하나씩 요청하고, pose를 갱신해 후보를 다시 만든다.

``` text
Pose + Route + candidates(pose) + history
   ↓
VLM  (move 1개당 요청 1회)
   ↓
검증 --- 실패하면 사유를 붙여 재요청 (최대 2회), 그래도 실패하면 greedy fallback
   ↓
pose 갱신 → 위로 (양손이 top / 후보 없음 / pose 순환 / max_moves 까지)
   ↓
Target Pose Sequence → RL Execution
```

계획이 물리와 부딪혀도 살아남는 쪽이다 --- 매 move가 **그 move를 발행하는
pose**를 보고 선택되고, 후보 목록이 프롬프트에 있으니 닿지 않는 홀드를 쓸 일이
애초에 적다. 대신 요청 수가 move 수만큼이고(재시도 포함), 모델이 한 번에 보는
것은 지금 상태 하나라 계획 전체의 모양을 스스로 볼 수 없다. 그래서 planner가
**pose 순환**을 대신 본다 --- 같은 pose를 세 번째로 지나가면 루프로 보고 끊는다
(두 번은 허용한다, 등반자도 move를 물릴 수 있다).

### one-shot (`--oneshot`, `plan_oneshot`)

초기 pose에서 top까지 move를 순서대로 모두 쓰게 하고, planner는 그것을 재생한다.

``` text
Initial Pose + Route + limits
   ↓
VLM  (요청 1회)
   ↓
Move Sequence
   ↓
재생 --- move마다 그 시점 pose에서 검증
   ↓
첫 불가능 move에서 중단 (재시도·fallback 없음)
   ↓
Target Pose Sequence → RL Execution
```

Candidate Generator는 프롬프트에서 빠지고 **심판으로만** 남는다. 모델에게
답을 알려 주지 않되, 내놓은 move가 그 pose에서 실제로 가능했는지는 똑같이
검사한다.

**첫 불가능 move에서 멈추는 이유**는 거기가 RL 컨트롤러도 멈추는 지점이기
때문이다. 그 뒤 move들은 일어나지 않은 pose를 전제로 쓰여 있어 의미가 없다.
그래서 `Plan.moves`는 **실행 가능한 prefix**이고, `valid_move_rate`는 재생한
move 중 그 prefix의 비율이다. 재시도할 상대가 없으니 재시도도, fallback도,
pose 순환 감지도 없다(루프가 없으므로 순환할 것도 없고, 같은 pose를 다시
지나가는 시퀀스는 `backtracks`가 잡는다).

## 검증 규칙

**두 모드가 같은 `validate()`를 쓴다.** move마다 **그 move가 발행되는 시점의
pose를 기준으로** 검사한다.

-   output schema가 유효한지
-   target hold가 route에 포함되는지
-   해당 limb가 이미 target을 잡고 있지 않은지
-   **점유** --- 같은 종류(손-손, 발-발)의 매칭만 허용하고, 이동 후 서로
    다른 홀드가 셋 이상이어야 한다
-   target이 그 pose의 candidate set에 존재하는지 (= reach)

점유 판정은 `candidates.blocked_holds()` 하나를 candidate generator와
validator가 같이 쓴다 --- 두 군데에 같은 규칙을 적으면 갈라진다.

**`validate()`는 후보 목록이 아니라 `ReachModel`을 받는다.** 후보 목록은
프롬프트용이라 `max_per_limb`로 잘려 있고 top까지의 거리로 정렬돼 있어서,
그걸로 검사하면 일곱 번째로 가까운 홀드가 "reach 밖"이 된다(20벽 재생에서
후보가 6개를 넘는 limb-pose가 3번 나왔다).

거절 사유는 `candidates.rejection()` 하나가 만든다 --- 후보 생성기와
validator가 같은 함수를 읽으므로 **실제로 막은 규칙의 이름이 그대로 나온다**:
`out of reach` / `below the hip line` / `above the shoulder line` /
`crossed over <limb>` / `body too stretched`. 전부 "out of reach"로 찍던
동안 wall 7의 2 mm crossing 초과가 거리 문제로 보였고, reach 한계를 100배로
늘려도 안 풀리는 이유를 알 수 없었다.

### 제약 전부 끄기 (`--oneshot --skip-filters`)

`plan_oneshot(skip_filters=True)` / `--skip-filters`는 **재생에서 심판을 끈다.
one-shot 전용이다** --- step-by-step에서는 후보 목록이 곧 모델이 고를 선택지라,
심판을 끄면 고를 것이 없어진다(CLI가 거부한다).
route 소속, 점유(세 홀드 바닥), reach, 자세 규칙 전부 통과시킨다.

끄지 않는 것은 재생이 돌아가기 위한 둘뿐이다 --- `moving_limb`이 실제 limb인지,
target이 이 벽에 있는 홀드인지. 이건 등반 제약이 아니라 그게 아니면 pose를
갱신할 수 없는 값이다.

**왜 있느냐:** 지금 모델은 2\~6번째 move에서 어긋나는데, 거기서 재생이 끊기면
**그 뒤에 뭘 썼는지 볼 수가 없다.** 시퀀스 전체를 읽고 모델이 어디서부터
body를 놓치는지 보려는 스위치다.

규칙이 거부했을 move는 실행되고 `Move.forced`로 표시된다(`Plan.forced`가
센다, CLI는 `!`). **측정에는 쓰지 않는다** --- 이걸 켠 plan의 완등률은
아무것도 증명하지 않는다.

## 평가

-   `valid_move_rate` --- **모드마다 의미가 다르다.** one-shot은 재생한 move 중
    실행 가능했던 비율(시퀀스가 어긋나기까지 모델이 body를 몇 move나 추적했는지),
    step-by-step은 요청 중 유효한 답이 온 비율이다. `Plan.mode`가 어느 쪽인지
    말해 주고 `plan.json`에도 들어간다. **모드를 섞어 이 숫자를 비교하지 말 것.**
-   `proposed` / `examined` / `executed` (one-shot) --- 모델이 쓴 move 수 /
    재생한 수 / 실행된 수. `proposed > examined`는 완등 후에도 계속 썼다는 뜻이다.
-   `requests` / `invalid` (step-by-step) --- 보낸 요청 수(재시도·fallback 포함) /
    거절된 답 수. `Move.retries`와 `Move.fell_back`이 move별로 남는다.
-   Top hold까지 계획 성공률 (`reached_top`)
-   `forced` --- 규칙을 꺼 준 덕분에 통과한 move 수. 0이 아니면 그 plan의
    완등률은 아무 의미가 없다.
-   평균 move 수
-   RL execution까지 포함한 plan success rate

beta 품질은 위 지표로 안 잡힌다. 전부 valid하면서도 발만 왔다 갔다 하는
계획이 나오므로 `Plan`이 두 가지를 더 센다.

-   `repeated_limb` --- 직전과 같은 limb을 움직인 move 수
-   `backtracks` --- 방금 뗀 홀드로 되돌아간 move 수

둘 다 0에 가까워야 하고, 프롬프트를 고칠 때 이 숫자로 비교한다.

## 핵심 위험

VLM의 시각적 reasoning이 좋아도 실제 물리 feasibility를 완전히
이해한다고 가정하면 안 된다. 따라서 Candidate Generator와 RL execution
결과가 VLM을 둘러싼 **constraint layer** 역할을 한다.

------------------------------------------------------------------------

## 구현 (Phase 2)

`src/vlm/` --- Unity 없이 도는 파이썬 패키지.

  파일              역할
  ----------------- ----------------------------------------------------
  `scene.py`        Scene JSON(docs/07) 자료형 + `/walls` 로더 (`wall(i)`)
  `candidates.py`   Candidate Generator, `ReachModel`, `initial_pose`
  `render.py`       VLM에 주는 벽 이미지(홀드 id, 후보 링, body overlay)
  `providers.py`    **모델 교체 지점.** `MoveChooser` + Gemini/OpenAI + greedy + `chooser_for`
  `planner.py`      프롬프트/스키마/검증 + `plan_steps`(기본)와 `plan_oneshot`
  `selftest.py`     오프라인 검증 (`python -m vlm.selftest`)

```
PYTHONPATH=src python -m vlm --wall 3                    # Gemini (.env의 GEMINI_API_KEY)
PYTHONPATH=src python -m vlm --wall 3 --model gpt        # OpenAI (.env의 OPENAI_API_KEY)
PYTHONPATH=src python -m vlm --wall 3 --model gpt-6-luna # 정확한 모델 이름도 그대로
PYTHONPATH=src python -m vlm --wall 3 --offline          # 키 없이 greedy 베이스라인
PYTHONPATH=src python -m vlm --wall 3 --out out/wall3    # 산출물 저장 (아래)
PYTHONPATH=src python -m vlm --wall 3 --oneshot          # 요청 한 번에 전체 시퀀스
PYTHONPATH=src python -m vlm --wall 3 --oneshot --skip-filters  # 심판 끄고 전체 재생 (측정 아님)
PYTHONPATH=src python -m vlm --scene path/to/scene.json  # 임의의 Scene JSON
```

**기본은 step-by-step이다.** `--oneshot`을 붙이면 요청 한 번에 전체 시퀀스를
받아 재생한다. `--skip-filters`는 `--oneshot`과만 쓸 수 있다.

`--model`은 짧은 별칭 둘을 받는다 --- **`gemini` → `gemini-3.8-flash`,
`gpt` → `gpt-6-sol`** (`providers.ALIASES`). 그 외 이름은 그대로 넘어가고,
provider는 이름으로 고른다 (`gemini*` → Gemini, 그 외 → OpenAI). 기본값은
`gemini`다.

`--out DIR`이 남기는 것:

  파일             내용
  ---------------- --------------------------------------------------------
  `scene.json`     계획에 쓴 Scene JSON
  `plan.json`      `Plan.to_dict()` --- 지표 + move별 docs/07 target pose
  `request.png`    **모델에게 실제로 보낸 이미지** (후보 링 없음). `--oneshot`만
  `stepNN.png`     move마다 한 장. `step00`이 초기 pose, `stepNN`이 move NN
                   직후 pose. 그 pose의 후보 링을 그려 준다 --- 다음 move가 왜
                   가능/불가능했는지가 여기서 보인다. step-by-step에서는 이것이
                   그 move에 실제로 보낸 이미지와 같다.

### 모델 교체

`MoveChooser`는 메서드가 하나다.

``` python
def choose(self, system: str, payload: dict, schema: dict, image_png: bytes | None) -> dict
```

`GeminiChooser`(google-genai)와 `OpenAIChooser`(chat completions, strict
json_schema)가 들어 있다. 다른 모델은 같은 프로토콜을 구현한 클래스를
`providers.py`에 하나 더 두고 `plan_steps(scene, chooser)` 또는
`plan_oneshot(scene, chooser)`에 넘기면 된다. 나머지 코드는 어떤 모델이 돌았는지
모른다. **`schema`를 보고 무엇을 답할지 정하는 것은 chooser의 몫이다** ---
`GreedyChooser`가 그렇게 두 모드를 다 답한다(`moves` 배열이 스키마에 있으면
시퀀스, 없으면 move 하나).

### Structured output

`plan_schema(scene, max_moves)`가 one-shot의 move 배열을 강제한다
(step-by-step은 `move_schema(cands)`, 위 "Structured Output"). `moving_limb`는 네
limb의 enum, `target_hold_id`는 **route 전체** hold id의 enum이다 --- 좁힐
candidate set이 없으니 그렇게 된다. hold id가 string인 것은 Gemini가 받는
JSON schema subset이 문자열 enum만 열거하기 때문이다. 스키마는 limb와 id를
각각 강제할 뿐 **둘의 짝도, 시퀀스의 연속성도 강제하지 못하므로**
`validate()`의 검사는 그대로 남는다. `maxItems`가 시퀀스 길이를 막는다.

`reason`을 move 스키마의 첫 필드로 두어 모델이 각 move를 정하기 전에 한
문장을 쓰게 한다.

OpenAI strict 모드는 vendor 키워드와 배열 길이 키워드를 거부하고 모든
property가 `required`여야 하므로 `strict_schema()`가 같은 스키마를 그
방언으로 바꾼다 --- `propertyOrdering`/`maxItems`를 떼고
`additionalProperties: false`를 붙인다. 배열 안 move 객체까지 내려가야 해서
**재귀**다. enum은 그대로 간다.

### 두 모드가 공유하는 것

-   **종료 조건은 양손이 top 홀드에 있는 것이다**(docs/07). 한 손이
    올라가도 계속 가고, 두 번째 손이 매칭해야 `reached_top`이 된다 ---
    Unity의 `IsToppedOut`이 그 상태이기 때문이다.
-   `validate()`, `Move`, `Plan`, 그리고 각 move의 docs/07 target pose(네 limb +
    `move` 플래그) 직렬화. RL 연결은 이 JSON을 Unity에 넘기는 것부터다.
-   `GreedyChooser`. 오프라인 베이스라인이면서 step-by-step의 fallback이다.

### 재생 (one-shot)

요청은 한 번이고, 그 뒤는 planner가 시퀀스를 재생한다.

-   완등 뒤에 모델이 더 쓴 move는 무시한다.
-   **불가능한 move가 나오면 거기서 끝난다.** 재시도도 greedy fallback도
    없다 --- 재요청할 상대가 없고, 그 뒤 move들은 일어나지 않은 pose를
    전제로 쓰여 있다. `stopped`에 몇 번째 move가 왜 막혔는지 남는다.
    멈춘 시퀀스를 끝까지 보고 싶으면 `--skip-filters`다
    (위 "제약 전부 끄기").
-   pose 순환 감지는 없다. 루프가 없으니 순환할 것도 없고, 같은 pose를
    다시 지나가는 시퀀스는 `backtracks`로 잡힌다.

### 루프 (step-by-step, 기본)

-   요청은 move마다 하나. 거절된 답은 사유(`previous_answer_rejected`)를 붙여
    다시 묻고, `max_retries`(2회) 뒤에는 `GreedyChooser`가 마지막으로 고른다
    (docs/07 "VLM invalid output"). `Move.retries` / `Move.fell_back`에 남는다.
-   프롬프트의 후보 목록과 schema enum이 그 pose의 후보라, 모델이 닿지 않는
    홀드를 고르기 어렵다. 그래도 schema는 limb와 hold를 각각만 강제하므로
    `validate()`는 그대로 돈다.
-   **같은 pose를 세 번째로 지나가면 끊는다.** 모델은 한 번에 상태 하나만 보므로
    자기가 순환하는 것을 볼 수 없고, planner만 볼 수 있다.
-   후보가 하나도 없는 pose에 도달하면 거기서 끝난다(`no reachable candidate`).

### 프롬프트에만 있고 강제하지 않는 것

candidate generator가 거르는 것은 **물리적으로 불가능한** move다. 아래 둘은
가능한 move라 후보에서 빼지 않고 프롬프트로만 억제한다. 프롬프트로 안 잡히면
그때 후보 필터로 내린다.

-   **발이 아래로 가는 move.** 손은 이제 후보 필터가 막지만(위 "hands don't
    climb down") 발은 막지 않는다 --- 필요한 move다. 프롬프트가 "위로 가거나
    다음 move를 만드는 move"를 우선하게만 한다.
-   **같은 limb 연속 이동.**
-   **꼬임을 푸는 순서.** crossing 자체는 후보 필터가 막지만, 옆으로 흐르는
    홀드 줄을 올라가려면 **뒤에 있는 limb을 먼저** 보내야 한다는 것은
    프롬프트만 말한다. 이걸 못 하면 dead end에 들어가고, step-by-step에는
    backtracking이 없다.
-   **직전 move 되돌리기** --- step-by-step에서는 **후보에서 뺀다.** 규칙은
    pose 하나만 보므로 방금 뗀 홀드를 아는 것은 planner뿐이다(`plan_steps`).
    바로 다음 move만 막고, 두 move 뒤에 돌아가는 것은 `Plan.backtracks`가
    센다. one-shot에는 이 필터가 없다 --- 모델이 시퀀스를 다 쓴 뒤라
    "직전"이 재생 시점에만 있다.

매칭은 반대로 **프롬프트가 명시적으로 알려 준다** --- "두 limb이 같은 홀드에
올라갈 수 있다"를 대문자 규칙으로 박아 두었다. 손-손과 발-발만이고
hand-foot은 금지라고 같이 못박는다. 점유가 그 자체로 피할 이유는 아니라는
것, top에서는 매칭이 완등 방법이라는 것도 프롬프트가 말한다.

**span도 프롬프트에 숫자로 들어간다** --- 매 move 후 각 손과 각 발의 거리
중 최대가 `limits.max_span`(2.4 m)을 넘지 못한다. step과 별개의 한계라는
것, 발이 그대로면 손이 1 m만 가도 걸린다는 것, 그러면 발을 먼저 올리라는
것까지 같이 쓴다. payload의 `limits`에도 `max_span`이 들어가고, 프롬프트에
박힌 2.4라는 숫자가 `ReachModel.max_span`과 같은지는 selftest가 본다.

**프롬프트는 짧게 유지한다.** 2026-09-28에 두 프롬프트를 절반 아래로 줄였다
(각각 45줄 → 23줄). 심판이 강제하는 규칙을 프롬프트에서 길게 변호할 필요가
없다 --- 규칙은 한 줄로 말하고, 분량은 "어떻게 고르는가"에 쓴다. 규칙을 지우는
것이 아니라 설명을 지우는 것이다.

### 합성 벽 --- 생성기는 Unity에만 있다

**파이썬에는 벽 생성기가 없다.** 벽은 Unity `RandomWallGenerator`가 만들어
`/walls/wall_NNN.json`(Scene JSON, docs/07)으로 내보낸 것을 커밋해 두고,
`scene.wall(i)`가 그걸 읽는다. 인자는 시드가 아니라 **인덱스**다 --- 시드는
Unity 쪽에만 있다.

한동안 같은 알고리즘이 C#과 파이썬에 각각 있었다. 같은 모양을 두 번
구현하면 반드시 갈라지고, 실제로 한쪽 기본값만 바뀌어 있는 걸 뒤늦게
발견했다. 게다가 Phase 3에서 CV가 넘겨줄 것도 결국 Scene JSON이라, 읽는
쪽을 지금 만들어 두면 그때 그대로 쓴다.

벽 모양에 영향을 주는 값을 건드리면 **에디터에서 "Export walls"를 다시
눌러 커밋한다.** 익스포트는 결정적이다(같은 시드 → 같은 바이트).

`selftest`는 읽어들인 벽이 이쪽에서 필요한 성질을 갖췄는지 검사한다 ---
id 유일성, start/top 존재, 벽 안쪽 좌표, start 아래에 홀드가 있는지, 그리고
start에서 top까지 `MAX_REACH` 걸음으로 **이어지는지**(연결성). 여기에
`check_solvable`이 **현재 규칙으로** 초기 pose에서 양손 top까지 가는 수열이
남아 있는지 BFS로 확인한다 --- 홀드 사이가 이어져 있다는 것과 규칙을 지키며
갈 수 있다는 것은 다른 얘기이고, 규칙을 조일 때 깨지는 쪽은 후자다. 발 홀드가
손 줄 옆에 붙은 뒤로 `hold_ids`가 높이순 한 줄이 아니라서 "연속 간격" 검사는
의미를 잃었다. 잘못된 재익스포트는 여기서 걸린다.

### 현재 베이스라인

greedy chooser(모델 없음) 기준 `/walls`의 20개 벽에서 **step-by-step 16/20,
one-shot 19/20**, 완등한 벽의 평균 15.8 move (2026-09-28, 양손 완등 기준).

**이 숫자는 greedy를 재는 값이고 벽을 재는 값이 아니다.** crossing을 0으로
조인 뒤로 greedy는 몇 벽에서 dead end(후보 0개)에 들어간다 --- 눈앞의 이득이
가장 큰 move만 고르므로 **꼬임을 푸는 순서**를 계획할 수 없기 때문이다. 같은
벽 20개가 BFS로는 10\~15 move에 다 풀리므로(`check_solvable`) 막힌 것은
규칙이 아니라 greedy다. 완등률 비교의 기준선으로 쓸 때 이걸 같이 적을 것.
(허용 0.5 m 시절에는 두 모드 다 20/20, 평균 15.8이었다.)

**greedy는 매 move마다 후보를 다시 계산한다.** one-shot의 VLM은 그걸 못 한다 ---
요청이 한 번이니까. 그래서 이 숫자는 one-shot에 대해서는 reach model이 허용하는
**상한**이지 같은 조건의 상대가 아니고, 비교해야 할 것은 완등률보다
`valid_move_rate`, 즉 시퀀스가 어긋나기까지 몇 move를 갔는지다.
step-by-step의 VLM은 greedy와 같은 정보를 받으므로 그쪽은 완등률을 직접 비교할
수 있다 --- 차이는 후보 중 무엇을 고르는지뿐이다.

매칭이 이 숫자를 만든다. 같은 greedy 실행에서

  매칭 허용        완등 조건 "양손이 top"   완등 조건 "한 손이 top"
  ---------------- ------------------------ -------------------------
  손+발            **20/20**                20/20
  손만             13/20                    13/20
  없음             0/20                     10/20

이 표는 hand-foot match를 막은 지금 규칙에서 잰 것이다. 한동안 허용했을
때도 greedy는 20/20에 평균 21.6 move였고 hand-foot이 나타나는 pose는
4%(17/432)뿐이었다 --- greedy는 이 기술을 거의 안 쓰므로 완등률에 기여하지
않는다. 다시 막은 지금은 20/20, 평균 21.2 move, hand-foot 0%다. **이 규칙의
근거는 greedy가 아니라 VLM 쪽 측정이다**(아래 one-shot 절).

발 매칭이 대부분의 차이다. 발의 step(1.0 m)이 짧아서 닿는 홀드가 대개
**반대쪽 발이 이미 밟고 있는 것**이고, 그래서 매칭 전에는 초기 포즈에서 발에
후보가 있는 벽이 6/20뿐이었다(지금 20/20). 발이 못 움직이면 손만 올라가다
span에 걸려 순환한다.

"양손이 top"이라는 더 엄한 완등 조건은 공짜다 --- 첫 손이 top에 닿은 벽
전부에서 두 번째 손이 따라붙었다.

세 홀드 하한은 완등률을 안 깎는다. 아예 풀어 주면 move가 줄어드는 대신
greedy가 pose의 22%를 네 limb-두 홀드로 만든다. 20/20은 그대로이므로 막아
둔다 --- 막는 것은 그 자세 하나이고, 매칭 자체는 아니다.

이전 판에 적혀 있던 16/20, 발 후보 11/20은 지금 커밋된 `/walls`
익스포트보다 앞선 숫자다. 매칭 전 상태에서 다시 재면 각각 10/20, 6/20이라
**그 두 숫자는 버린다.** 벽을 다시 내보내면 `selftest` 출력으로 이 절을
갱신할 것.

### one-shot 수치와 비교되지 않은 것

one-shot을 한동안 유일한 전략으로 두었다가, step-by-step 루프를 기본으로
되살렸다(2026-09-28). **둘 다 남긴 이유는 어느 쪽이 나은지 모르기 때문이다** ---
아래 0/5는 greedy와의 비교일 뿐이고, 같은 모델로 루프를 돌린 수치가 없다.

**gpt-6-sol, one-shot, wall 0\~4 (2026-09-28).** 완등 0/5. 시퀀스는 2\~6 move 버티다
어긋나고, 실패는 전부 reach 초과였다 --- 모델이 자기가 쓰고 있는 body를
끝까지 추적하지 못한다. `limits`가 "그 시점 그 limb의 홀드에서" 재는
거리라는 것을 프롬프트가 못박지만, 여덟 move쯤 가면 놓친다.

**hand-foot match --- 뒤집힌 결정.** 한때 실패의 절반이 "발을 손이 잡은
홀드에 올린다"였다. 프롬프트로 금지해 봤더니 오히려 나빠져서(두 벽이 move
1에서 실패) 규칙 쪽을 열었고, 점유 실패가 사라졌다. 2026-09-28에 다시
금지로 돌렸다 --- 프롬프트가 먼저 바뀌었고 코드와 문서를 거기 맞췄다.
**금지 상태의 VLM 수치는 아직 다시 재지 않았다.** 위 "두 벽이 move 1에서
실패"가 여전히 유효한지가 다음에 확인할 것이다.

**아직 재지 않은 것:** 같은 모델로 돌린 step-by-step 루프. 위 실패가 전부
"자기 body를 끝까지 추적하지 못한다"는 한 가지 원인이었으므로, 매 move마다 pose와
후보를 다시 주는 쪽이 이 실패 모드를 없애 줄 것으로 예상하지만 **예상일 뿐이다.**
대신 요청 수가 move 수만큼 들고, 모델이 계획 전체를 보지 못한다는 반대쪽 비용이
있다. 다음에 할 일은 같은 벽 다섯 개를 두 모드로 한 번씩 돌려
(`--oneshot` 유/무) `valid_move_rate`와 완등률을 나란히 적는 것이다.
그때까지 **어느 쪽도 지우지 않는다.** 호출 수는 CLAUDE.md의 API 호출 주의를
따른다.
