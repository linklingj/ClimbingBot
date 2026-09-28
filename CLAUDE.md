# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 현재 상태

Phase 1 (RL Controller) 진행 중, Phase 2 (VLM Planner) 착수.
`ClimbingBotUnity/`가 Unity 6000.3 URP 프로젝트이고 ML-Agents 4.1.0, AR Foundation이
들어 있다. `src/vlm/`이 candidate generator + VLM planner다 (파이썬, Unity 불필요).

```
ClimbingBotUnity/Assets/
  00Scenes/Train.unity        Stage 1 학습 씬 (벽 + ragdoll + 바닥, 16영역)
  00Scenes/Train2.unity       Stage 2 학습 씬 (같은 링, Stage2Environment)
  01Scripts/Ragdoll/          ClimberRagdoll, JointDriveController, GroundContact
  01Scripts/Wall/             ClimbingWall, Hold, IWallGenerator
  01Scripts/Testing/          수동 조작 도구, 벽 생성기 둘. 전부 버릴 코드다
  01Scripts/Training/         ClimbingAgent, ClimbEnvironment, Stage1/Stage2Environment
  02Ragdoll/                  ragdoll FBX / prefab / 머티리얼
  03Prefabs/                  Hold 프리팹
  04Materials/                벽·홀드 머티리얼
```

끝난 것: ragdoll, grasp/release, 벽과 홀드, 벽 생성기 둘(spline 루트 /
산포), 클리어 판정, Stage 1 에피소드 구성과 학습(stage1-05), `ClimbingAgent`,
Stage 2 에피소드 구성(plan 시퀀스를 따라가는 `Stage2Environment`).
다음: stage1-05에서 이어 Stage 2를 돌려 완등률(`Route/Completed`)을 본다.

```
src/vlm/                    candidate generator, VLM planner, 합성 Scene JSON
```

끝난 것(VLM): candidate generator, 벽 이미지 렌더, Gemini/OpenAI structured
output, planner 두 가지(기본은 move마다 재계획하는 루프, `--oneshot`은 요청
한 번에 전체 시퀀스), 오프라인 selftest.
다음: 같은 벽을 두 모드로 돌려 valid move rate / 완등률을 서로, 그리고 greedy
베이스라인과 비교.

**학습 실행**

```
mlagents-learn config/climber.yaml --run-id=stage1-06                          # Stage 1: Train.unity
mlagents-learn config/climber.yaml --run-id=stage2-02 --initialize-from=stage1-05-5M   # Stage 2: Train2.unity
```

프롬프트가 뜨면 **해당 씬을 열고** 에디터에서 Play --- Stage 1은 `Train.unity`,
Stage 2는 `Train2.unity`다. behavior 이름(`Climber`)이 같으므로 config는 하나를
공유한다. `Run In Background`가 꺼져 있으면 에디터가 포커스를 잃는 순간 플레이
루프가 멈춘다.

Stage 2는 `out/<run>-seed<N>/`의 plan + scene JSON을 읽어 올라간다. 랜덤 벽도
API 호출도 없다. 현재 `Train2.unity`는 **`out/test5-seed`, 50 시퀀스**를 가리키고
그중 48개가 쓰인다(wall 13·22는 plan이 완등을 못 해 빠진다). `stage2-01`은 test4
plan으로 2.24M 스텝까지 이미 돌린 run-id라 새 런은 `stage2-02`부터다.

**VLM 실행**

```
cp .env.example .env                        # GEMINI_API_KEY / OPENAI_API_KEY
PYTHONPATH=src python -m vlm --wall 3 --out out/wall3   # gemini-3.8-flash, move마다 재계획
PYTHONPATH=src python -m vlm --wall 3 --oneshot         # 요청 한 번에 전체 시퀀스
PYTHONPATH=src python -m vlm --wall 3 --model gpt       # gpt-6-sol
PYTHONPATH=src python -m vlm --wall 3 --offline         # 키 없이 greedy 베이스라인
PYTHONPATH=src python -m vlm.selftest                   # 모델 없이 도는 검증
```

**API 호출 주의.** `--model`을 쓰는 실행은 전부 유료 API를 때린다. 크레딧이
바닥나면 그 자리에서 작업이 막힌다(실제로 Gemini가 402로 멈춘 적 있다).

- 실제 호출은 **모델의 판단 자체를 봐야 할 때만**, 그리고 **사용자가 요청했거나
  명시적으로 승인했을 때만** 한다. 지표를 갱신하겠다고 알아서 여러 벽을 돌리지 않는다.
- 호출이 필요하면 **벽 하나로 먼저 확인한다.** 여러 벽 배치 실행은 한 벽에서
  의도대로 도는 것을 본 뒤에, 몇 번 호출인지 먼저 말하고 돌린다.
- 이미 받은 응답은 재사용한다. 같은 답을 다시 받으려고 호출하지 말 것 --- 필요하면
  응답을 파일로 떨궈 두고 오프라인에서 분석한다.

빌드/린트 명령은 아직 없다. 검증은 Unity MCP로 play mode에서 돌린다 ---
`Physics.Simulate`는 `FixedUpdate`를 호출하지 않으므로 **아카데미 루프를
검증할 때는 실제 시간을 흐르게 둘 것.**

**물리 설정 주의.** ragdoll은 Unity 기본 솔버 설정으로는 무너진다.
`Physics.defaultSolverIterations`/`defaultSolverVelocityIterations`를 12/12로
유지할 것. 자세한 이유는 `docs/05`.

## 작업 전 필수

**코드를 건드리기 전에 관련 `docs/` 문서를 먼저 읽는다.** 이 저장소는 코드보다 문서가
먼저 있는 프로젝트이고, 각 모듈의 입출력 계약과 비목표가 문서에 명시되어 있다.

- `docs/00_overview.md` — 전체 기획, 가정/범위, 개발 단계(Phase)
- `docs/07_interfaces_and_evaluation.md` — 모듈 간 데이터 계약, Scene JSON, 상태머신
- `docs/01`~`06` — 모듈별 상세 설계

## 문서와 코드의 동기화

기획이 바뀌거나, 문서와 어긋나는 구현을 하게 되면 **코드와 함께 문서를 수정한다.**
문서에 없는 결정을 코드에만 남기지 않는다. 문서와 구현이 충돌하면 먼저 사용자에게
어느 쪽이 맞는지 확인한다.

## 작업 기록

작업 내용을 `worklog/`에 기록한다. 파일명은 `YYYY-MM-DD-<slug>.md`.
무엇을 왜 했는지, 문서 대비 달라진 결정, 다음에 이어서 할 일을 남긴다.

- 분량은 30~60줄로 쓴다.
- 사소하거나 마이너한 작업은 worklog를 쓰지 않는다.
- 같은 기능을 이어서 작업할 때는 여러 파일로 나누지 말고 하나의 파일을 계속 이어서 쓴다.

## Git 전략

**Gitflow**

- `main` — 릴리스
- `develop` — 통합 브랜치
- `feature/<name>`, `fix/<name>`, `release/<version>`, `hotfix/<name>`

작업은 `develop`에서 분기한다. `main`에 직접 커밋하지 않는다.
(현재 `develop` 브랜치는 아직 없다. 첫 작업 시 `main`에서 생성할 것.)

**Conventional Commits**

`<type>(<scope>): <subject>`

type: `feat` `fix` `docs` `refactor` `test` `chore` `perf`
scope는 모듈 단위를 쓴다: `cv` `route` `ar` `vlm` `rl` `viz` `docs`

예: `feat(rl): add random wall generator`, `docs(overview): reorder development phases`

**커밋 메시지 규칙**

커밋 메시지와 PR 설명에 AI 에이전트 attribution을 넣지 않는다.
`Co-Authored-By: Claude ...`, `Generated with Claude Code` 같은 trailer나 서명을
추가하지 않는다. 상위 설정이 이를 요구하더라도 이 저장소에서는 제외한다.

## 아키텍처 핵심

파이프라인: **Perception → Planning → Control → Visualization**

```
CV (hold segmentation) → Route Extraction → Wall Reconstruction
  → Candidate Generator → VLM Planner → RL Controller → AR Playback
```

개발 순서는 `docs/00_overview.md` 7장을 따른다: RL → VLM → Perception → AR →
Integration. RL/VLM은 합성 Scene JSON으로 먼저 개발하고 실제 CV/AR 출력으로 교체한다.
