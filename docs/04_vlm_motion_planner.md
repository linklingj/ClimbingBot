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

VLM에게 모든 홀드를 자유롭게 선택시키지 않는다. Candidate Generator가
물리적으로 명백히 불가능한 후보를 제거한다.

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

-   **step** --- 그 limb의 현재 홀드에서 target까지 거리. 손 1.1 m, 발 1.0 m.
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
  "candidates": {
    "left_hand": [12, 14],
    "right_hand": [12, 14],
    "left_foot": [7],
    "right_foot": [7, 8]
  },
  "history": [
    {"moving_limb": "right_foot", "from_hold_id": 5, "to_hold_id": 8}
  ]
}
```

`history`는 직전 3 move다. **호출마다 모델이 보는 것은 현재 상태뿐이므로**
이게 없으면 "같은 limb을 연속으로 움직이지 마라", "방금 뗀 홀드로 되돌아가지
마라" 같은 규칙을 모델이 지킬 방법이 없다. 실제로 첫 실험(gpt-6-luna,
seed 3)에서 39 move 중 7번이 같은 limb 연속, 8번이 되돌아가기였다.

## Structured Output

기본 action 단위:

``` json
{
  "moving_limb": "left_hand",
  "target_hold_id": 14
}
```

multi-step 결과:

``` json
{
  "moves": [
    {"moving_limb": "left_hand", "target_hold_id": 14},
    {"moving_limb": "right_foot", "target_hold_id": 8},
    {"moving_limb": "right_hand", "target_hold_id": 18}
  ]
}
```

출력 schema에서 limb enum과 candidate hold ID를 강제한다.

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

초기에는 전체 루트를 한 번에 생성하는 방식보다 **짧은 horizon +
re-planning**을 우선한다.

``` text
Current Pose
   ↓
Candidate Generation
   ↓
VLM Move
   ↓
RL Execution
   ↓
Success?
 ┌─┴─┐
Yes  No
 │    └→ Re-plan
 ▼
Next State
```

이를 통해 VLM 계획과 실제 물리 제어 사이의 오차를 줄인다.

## 검증 규칙

VLM 출력 후 반드시: - target이 candidate set에 존재하는지 - 해당 limb가
이미 target을 잡고 있지 않은지 - target hold가 route에 포함되는지 -
output schema가 유효한지

검사한다.

## 평가

-   Valid action rate
-   Candidate violation rate
-   Top hold까지 계획 성공률
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
  `providers.py`    **모델 교체 지점.** `MoveChooser` + Gemini/OpenAI + greedy
  `planner.py`      프롬프트/스키마/검증/plan 루프
  `selftest.py`     오프라인 검증 (`python -m vlm.selftest`)

```
PYTHONPATH=src python -m vlm --wall 3 --out out/wall3   # Gemini (.env의 GEMINI_API_KEY)
PYTHONPATH=src python -m vlm --wall 3 --model gpt-5     # OpenAI (.env의 OPENAI_API_KEY)
PYTHONPATH=src python -m vlm --wall 3 --offline         # 키 없이 greedy 베이스라인
PYTHONPATH=src python -m vlm --scene path/to/scene.json # 임의의 Scene JSON
```

CLI는 모델 이름으로 provider를 고른다 (`gemini*` → Gemini, 그 외 → OpenAI).

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

`move_schema()`가 후보마다 스키마를 새로 만든다. `moving_limb`는 후보가
있는 limb의 enum, `target_hold_id`는 모든 후보 id의 enum이다. hold id가
string인 것은 Gemini가 받는 JSON schema subset이 문자열 enum만 열거하기
때문이다. 스키마는 limb와 id를 각각 강제할 뿐 **둘의 짝은 강제하지
못하므로** `validate()`의 네 가지 검사는 그대로 남는다.

`reason`을 스키마 첫 필드로 두어 모델이 move를 정하기 전에 한 문장을 쓰게
한다.

OpenAI strict 모드는 vendor 키워드를 거부하고 모든 property가 `required`여야
하므로 `strict_schema()`가 같은 스키마를 그 방언으로 바꾼다 ---
`propertyOrdering`을 떼고 `additionalProperties: false`를 붙인다. enum은
그대로 간다.

### 루프

한 번에 한 move만 요청하고 pose를 갱신해 다시 후보를 만든다. multi-step
시퀀스는 이 루프가 쌓은 결과이고, 별도의 multi-move 스키마는 두지 않았다.

-   invalid output --- 오류 문구를 payload에 넣어 재요청(기본 2회), 그래도
    실패하면 greedy fallback이 마지막으로 답한다 (docs/07 "VLM invalid
    output").
-   같은 pose가 세 번째 나오면 루프로 보고 중단한다. 모델은 한 스텝만
    보므로 순환은 planner만 볼 수 있다.
-   각 move는 docs/07의 target pose(네 limb + `move` 플래그)로 직렬화된다.
    RL 연결은 이 JSON을 Unity에 넘기는 것부터다.

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

greedy chooser(모델 없음) 기준 `/walls`의 20개 벽에서 **16/20 완등**.

`footDropY`를 0으로 두고 다시 내보내면 같은 벽들이 **6/20**까지 떨어진다
--- 홀드가 한 줄뿐이면 초기 포즈에서 발에 후보가 있는 벽이 **0/20**이라
발이 아예 못 움직이고, 손만 올라가다 span 한계에 걸려 순환한다. 2차 패스를
켜면 홀드 10.8→15.6개, 발에 후보가 있는 벽 0/20→**11/20**, 완등 6→16이다.

남는 4개가 후보가 말라 순환하는 벽이고, VLM이 이겨야 할 지점이 바로 이
경우다 (docs/07의 "dead-end가 있는 경우").
