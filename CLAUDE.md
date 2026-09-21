# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 현재 상태

설계 단계. `docs/`에 기획 문서만 있고 `src/`는 비어 있다. Unity 프로젝트를 전제로 한
`.gitignore`가 있으나 아직 Unity 프로젝트가 생성되지 않았다.

빌드/테스트/린트 명령은 아직 존재하지 않는다. Unity 프로젝트나 Python 학습 코드가
추가되면 그 시점에 이 섹션을 실제 명령으로 갱신할 것.

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

## 아키텍처 핵심

파이프라인: **Perception → Planning → Control → Visualization**

```
CV (hold segmentation) → Route Extraction → Wall Reconstruction
  → Candidate Generator → VLM Planner → RL Controller → AR Playback
```

개발 순서는 `docs/00_overview.md` 7장을 따른다: RL → VLM → Perception → AR →
Integration. RL/VLM은 합성 Scene JSON으로 먼저 개발하고 실제 CV/AR 출력으로 교체한다.
