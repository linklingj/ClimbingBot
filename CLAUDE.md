# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 현재 상태

Phase 1 (RL Controller) 진행 중. `ClimbingBotUnity/`가 Unity 6000.3 URP 프로젝트이고
ML-Agents 4.1.0, AR Foundation이 들어 있다. `src/`는 아직 비어 있다.

```
ClimbingBotUnity/Assets/
  00Scenes/Train.unity        학습 씬 (벽 + ragdoll + 바닥)
  01Scripts/Ragdoll/          ClimberRagdoll, JointDriveController, GroundContact
  01Scripts/Wall/             ClimbingWall, Hold
  02Ragdoll/                  ragdoll FBX / prefab / 머티리얼
  03Wall/Materials/           벽·홀드 머티리얼
```

끝난 것: ragdoll, grasp/release, 벽과 홀드, 랜덤 벽 생성기, 클리어 판정.
다음: `ClimbingAgent` (observation / action / reward).

빌드/테스트/린트 명령은 아직 없다. 검증은 Unity MCP로 play mode에서
`Physics.Simulate`를 돌려서 한다. Python 학습 코드가 추가되면 이 섹션을 실제
명령으로 갱신할 것.

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
