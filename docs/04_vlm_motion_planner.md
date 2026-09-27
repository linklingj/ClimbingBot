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

Candidate Generator는 물리적으로 명백히 불가능한 move를 제거한다. **단
프롬프트에는 후보 목록을 넣지 않는다** --- VLM은 route 전체 홀드와 reach
예산만 받고, candidate set은 출력을 **검증**하는 쪽에서만 쓴다. 아래
"Planning 전략" 참고.

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
계산한다. 구현(`src/vlm/candidates.py`)은 후보 하나당 네 가지를 본다.

-   **점유** --- **매칭은 어느 두 limb 사이에서나 허용한다**(`blocked_holds`):
    손-손, 발-발, 그리고 손이 잡은 홀드에 발을 올리는 **hand-foot match**.
    실제로 쓰는 기술이고, one-shot 실험에서 VLM이 반복해서 요구한 것도
    이것이다(프롬프트로 금지해 봤지만 오히려 악화됐다). 자기가 이미 잡은
    홀드는 후보가 아니다. 유일한 금지는 **네 limb이 홀드 두 개에 올라가는
    자세** --- 매달릴 수 있는 자세가 아니다. 그래서 `blocked_holds`가 보는
    것은 하나다: 이동 후 서로 다른 홀드가 **셋 이상**이어야 한다. 이전의
    "한 쌍씩만"은 이 조건의 대용이었는데, 덤으로 hand-foot까지 막고 있었다.
    hand-foot match가 과격해지지 않게 잡아 주는 것은 아래 hip/shoulder
    line이다 --- 발은 어깨선 아래 홀드만 취할 수 있다.
-   **step** --- 그 limb의 현재 홀드에서 target까지 거리. 손 1.4 m, 발 1.0 m.
-   **hip/shoulder line** --- 네 접점의 중심에서 torso(0.55 m)의 절반만큼
    위가 어깨선, 아래가 골반선. 손은 골반선 위, 발은 어깨선 아래로만 간다.
-   **crossing** --- 반대쪽 같은 종류의 limb를 `cross_margin`(0.25 m) 넘게
    지나치지 않는다.
-   **span** --- 이동 후 손-발 최대 거리가 `max_span`(2.4 m)을 넘지 않는다.
    step만으로는 발이 그대로인 채 손만 멀어지는 자세를 못 막는다.

`ReachModel`의 이 값들은 1.7 m 인체 기준 추정치이고 ragdoll로 실측한 것이
아니다. **RL이 통과시킨 move를 generator가 거부하거나 그 반대가 반복되면
여기부터 손본다.**

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

`candidates`도 `history`도 주지 않는다. 요청이 한 번뿐이라 직전 move라는
것이 없고 --- 모델이 history를 스스로 쓰는 중이다 --- 무엇이 닿는지는
`limits`와 홀드 좌표로 모델이 직접 따져야 한다. **`limits`는 매 move마다
그 limb이 그 시점에 있는 홀드부터 재는 거리다.** 시퀀스 중간에 초기화되지
않는다는 것을 프롬프트가 못박는다.

## Structured Output

출력은 move 배열 하나다.

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

**요청 한 번에 전체 시퀀스를 받는다.** 초기 pose에서 top까지 move를 순서대로
모두 쓰게 하고, planner는 그것을 재생한다.

``` text
Initial Pose + Route + limits
   ↓
VLM  (요청 1회)
   ↓
Move Sequence
   ↓
재생 --- move마다 그 시점 pose에서 Candidate Generation → 검증
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
move 중 그 prefix의 비율이다.

이전 판은 move 하나씩 요청하고 pose를 갱신해 후보를 다시 만드는
re-planning 루프였다. 재시도·greedy fallback·pose 순환 감지가 거기 붙어
있었고, 전부 같이 사라졌다.

## 검증 규칙

시퀀스의 move마다, **그 move가 발행되는 시점의 pose를 기준으로** 검사한다.

-   output schema가 유효한지
-   target hold가 route에 포함되는지
-   해당 limb가 이미 target을 잡고 있지 않은지
-   **이동 후 서로 다른 홀드가 셋 이상인지** (매칭은 어느 두 limb 사이에서나
    허용, 네 limb-두 홀드만 금지)
-   target이 그 pose의 candidate set에 존재하는지 (= reach)

점유 판정은 `candidates.blocked_holds()` 하나를 candidate generator와
validator가 같이 쓴다 --- 두 군데에 같은 규칙을 적으면 갈라진다.

## 평가

-   `valid_move_rate` --- 재생한 move 중 실행 가능했던 비율. 시퀀스가
    어긋나기까지 모델이 body를 몇 move나 추적했는지를 재는 값이다.
-   `proposed` / `examined` / `executed` --- 모델이 쓴 move 수 / 재생한 수 /
    실행된 수. `proposed > examined`는 완등 후에도 계속 썼다는 뜻이다.
-   Top hold까지 계획 성공률 (`reached_top`)
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
  `planner.py`      프롬프트/스키마/검증/시퀀스 재생
  `selftest.py`     오프라인 검증 (`python -m vlm.selftest`)

```
PYTHONPATH=src python -m vlm --wall 3                    # Gemini (.env의 GEMINI_API_KEY)
PYTHONPATH=src python -m vlm --wall 3 --model gpt        # OpenAI (.env의 OPENAI_API_KEY)
PYTHONPATH=src python -m vlm --wall 3 --model gpt-6-luna # 정확한 모델 이름도 그대로
PYTHONPATH=src python -m vlm --wall 3 --offline          # 키 없이 greedy 베이스라인
PYTHONPATH=src python -m vlm --wall 3 --out out/wall3    # 산출물 저장 (아래)
PYTHONPATH=src python -m vlm --scene path/to/scene.json  # 임의의 Scene JSON
```

`--model`은 짧은 별칭 둘을 받는다 --- **`gemini` → `gemini-3.8-flash`,
`gpt` → `gpt-6-sol`** (`providers.ALIASES`). 그 외 이름은 그대로 넘어가고,
provider는 이름으로 고른다 (`gemini*` → Gemini, 그 외 → OpenAI). 기본값은
`gemini`다.

`--out DIR`이 남기는 것:

  파일             내용
  ---------------- --------------------------------------------------------
  `scene.json`     계획에 쓴 Scene JSON
  `plan.json`      `Plan.to_dict()` --- 지표 + move별 docs/07 target pose
  `request.png`    **모델에게 실제로 보낸 이미지** (후보 링 없음)
  `stepNN.png`     move마다 한 장. `step00`이 초기 pose, `stepNN`이 move NN
                   직후 pose. 이쪽은 그 pose의 후보 링을 그려 준다 ---
                   다음 move가 왜 가능/불가능했는지가 여기서 보인다.

### 모델 교체

`MoveChooser`는 메서드가 하나다.

``` python
def choose(self, system: str, payload: dict, schema: dict, image_png: bytes | None) -> dict
```

`GeminiChooser`(google-genai)와 `OpenAIChooser`(chat completions, strict
json_schema)가 들어 있다. 다른 모델은 같은 프로토콜을 구현한 클래스를
`providers.py`에 하나 더 두고 `plan(scene, chooser)`에 넘기면 된다. 나머지
코드는 어떤 모델이 돌았는지 모른다.

### Structured output

`plan_schema(scene, max_moves)`가 move 배열을 강제한다. `moving_limb`는 네
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

### 재생

요청은 한 번이고, 그 뒤는 planner가 시퀀스를 재생한다.

-   **종료 조건은 양손이 top 홀드에 있는 것이다**(docs/07). 한 손이
    올라가도 계속 가고, 두 번째 손이 매칭해야 `reached_top`이 된다 ---
    Unity의 `IsToppedOut`이 그 상태이기 때문이다. 그 뒤에 모델이 더 쓴
    move는 무시한다.
-   **불가능한 move가 나오면 거기서 끝난다.** 재시도도 greedy fallback도
    없다 --- 재요청할 상대가 없고, 그 뒤 move들은 일어나지 않은 pose를
    전제로 쓰여 있다. `stopped`에 몇 번째 move가 왜 막혔는지 남는다.
-   pose 순환 감지는 없앴다. 루프가 없으니 순환할 것도 없고, 같은 pose를
    다시 지나가는 시퀀스는 `backtracks`로 잡힌다.
-   각 move는 docs/07의 target pose(네 limb + `move` 플래그)로 직렬화된다.
    RL 연결은 이 JSON을 Unity에 넘기는 것부터다.

### 프롬프트에만 있고 강제하지 않는 것

candidate generator가 거르는 것은 **물리적으로 불가능한** move다. 아래 둘은
가능한 move라 후보에서 빼지 않고 프롬프트로만 억제한다. 프롬프트로 안 잡히면
그때 후보 필터로 내린다.

-   **아래로 가는 move.** 위나 옆으로 옮기는 것을 우선하고 limb을 지금보다
    낮은 홀드로 내리는 것은 지양하게 한다. 후보에서 빼지 않는 이유는 발을
    내려 딛어 엉덩이를 벽에 붙이는 move가 실제로 필요하기 때문이다.
    generator는 손이 골반선 위, 발이 어깨선 아래인지만 본다.
-   **같은 limb 연속 이동.**

매칭은 반대로 **프롬프트가 명시적으로 알려 준다** --- "두 limb이 같은 홀드에
올라갈 수 있다"를 대문자 규칙으로 박아 두었다. 손-손, 발-발, 그리고 손이
잡은 홀드에 발을 올리는 hand-foot match까지. 점유는 그 자체로 피할 이유가
아니라는 것, 그리고 top에서는 그것이 완등 방법이라는 것을 같이 말한다.
금지하는 쪽으로 써 봤더니 오히려 나빠졌다(아래 one-shot 절).

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
start에서 top까지 `MAX_REACH` 걸음으로 **이어지는지**(연결성). 발 홀드가
손 줄 옆에 붙은 뒤로 `hold_ids`가 높이순 한 줄이 아니라서 "연속 간격" 검사는
의미를 잃었다. 잘못된 재익스포트는 여기서 걸린다.

### 현재 베이스라인

greedy chooser(모델 없음) 기준 `/walls`의 20개 벽에서 **20/20 완등**
(2026-09-28 측정, 양손 완등 기준, 평균 21.6 move).

**greedy는 자기 rollout 안에서 매 move마다 후보를 다시 계산한다.** VLM은 그걸
못 한다 --- 요청이 한 번이니까. 그래서 이 숫자는 reach model이 허용하는
**상한**이지 같은 조건의 상대가 아니다. 비교해야 할 것은 완등률보다
`valid_move_rate`, 즉 시퀀스가 어긋나기까지 몇 move를 갔는지다.

매칭이 이 숫자를 만든다. 같은 greedy 실행에서

  매칭 허용        완등 조건 "양손이 top"   완등 조건 "한 손이 top"
  ---------------- ------------------------ -------------------------
  손+발            **20/20**                20/20
  손만             13/20                    13/20
  없음             0/20                     10/20

이 표는 hand-foot match를 막아 둔 상태에서 측정한 것이다. hand-foot까지
허용한 현재 규칙에서 greedy는 여전히 **20/20**, 평균 21.6 move이고
hand-foot match가 나타나는 pose는 4%(17/432)다 --- greedy는 이 기술을 거의
쓰지 않으므로 완등률에 기여하지 않는다. 허용한 이유는 greedy가 아니라 VLM이
계속 요구했기 때문이고, 손만/없음 행은 다시 재지 않았다.

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

### one-shot으로 바꾼 근거와 현재 수치

move 하나씩 재계획하던 루프를 걷어내고 요청 한 번으로 바꿨다. 없어진 것:
후보 목록(프롬프트), `history`, 재시도, greedy fallback, pose 순환 감지.

**gpt-6-sol, wall 0\~4 (2026-09-28).** 완등 0/5. 시퀀스는 2\~6 move 버티다
어긋나고, 실패는 전부 reach 초과였다 --- 모델이 자기가 쓰고 있는 body를
끝까지 추적하지 못한다. `limits`가 "그 시점 그 limb의 홀드에서" 재는
거리라는 것을 프롬프트가 못박지만, 여덟 move쯤 가면 놓친다.

점유 실패는 hand-foot match를 허용하면서 사라졌다. 그전에는 실패의 절반이
"발을 손이 잡은 홀드에 올린다"였고, 프롬프트로 금지해 봤더니 **오히려
나빠졌다**(두 벽이 move 1에서 실패). 모델이 계속 요구하는 데는 이유가 있었고
--- 실제 등반 기술이다 --- 규칙을 바꾸는 쪽이 맞았다.

**아직 재지 않은 것:** 같은 모델로 돌린 re-planning 루프와의 직접 비교.
위 0/5는 greedy(20/20)와의 비교일 뿐이고, 루프 쪽을 모델로 돌린 수치는
없다. 완등률만 보면 one-shot이 유리하다고 말할 근거는 이 저장소에 없다.
