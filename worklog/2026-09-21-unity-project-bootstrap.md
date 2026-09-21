# Unity 프로젝트 생성 및 학습 환경 구성

## 한 일

### Unity 프로젝트

- `ClimbingBotUnity/` 에 Unity **6000.3.12f1** URP 3D 프로젝트 생성.
- 저장소 루트가 아닌 하위 폴더에 둔다. `docs/01`(CV), `docs/04`(VLM)가 Python이라
  이 저장소는 polyglot이고, Unity 산출물이 `docs/`, `worklog/`와 한 층에 섞이는 걸 피한다.
- AR Mobile 템플릿 대신 URP 3D 템플릿을 썼다. 개발 순서상 AR은 마지막 단계(`docs/00` 7장)라
  학습 단계 내내 XR 설정과 모바일 빌드 타겟이 방해만 된다. AR 패키지는 필요할 때 추가한다.

### .gitignore 분리

- `ClimbingBotUnity/.gitignore` — Unity 표준 규칙. 루트 앵커(`/[Ll]ibrary/` 등)가
  프로젝트 폴더 기준으로 해석되도록 파일 위치만 옮겼다. 내용은 그대로다.
- `.gitignore` (루트) — Python, 데이터셋, 시크릿, ML-Agents 산출물.
  `results/`와 `[Tt]raining[Rr]esults/`는 앵커를 풀었다. `mlagents-learn`을
  어느 디렉터리에서 돌려도 잡히도록.

### 패키지

- `com.unity.ml-agents` **4.1.0**
- `com.unity.xr.arfoundation` **6.5.1**

ML-Agents 4.0.0부터 추론 백엔드가 Sentis → **Inference Engine**(`com.unity.ai.inference`)으로
바뀌었고 최소 Unity가 6000.0이 됐다. 4.1.0이 요구하는 `com.unity.ai.inference 2.6.1`은
URP 템플릿에 이미 포함된 버전이라 충돌이 없다.

4.0.x가 아닌 4.1.0을 고른 이유는 모바일 때문이다. 4.1.0 체인지로그에
`Google.Protobuf_Packed.dll`의 IL2CPP 호환 수정이 있다. iOS/Android 빌드는 IL2CPP 강제다.

### Python 학습 환경

- conda `ml-agents-4` (기존 `ml-agents` 1.1.0 환경은 보존, `--clone`으로 복제)
- `mlagents` / `mlagents-envs` **1.2.0.dev0** — GitHub `develop` 브랜치에서 editable 설치.
  소스 클론은 `../ml-agents` (저장소 밖, shallow).
- `gym 0.26.2` 제거 → `gymnasium 1.3.0` + `pettingzoo 1.27.0` (4.1.0에서 마이그레이션됨)

**PyPI의 `mlagents`는 1.1.0에서 멈춰 있다.** C# 4.1.0의 짝은 1.2.0.dev0이고 PyPI에 없다.
`pip install mlagents`로는 안 되고 소스 설치가 필요하다.
`API_VERSION`은 양쪽 다 `1.5.0`이라 프로토콜 자체는 호환되지만, 4.1.0의 Python 쪽 수정
(LSTM+SAC 버퍼 버그, 트레이너의 CPU/GPU 텐서 디바이스 불일치)이 1.1.0에는 없다.

Python 버전이 `>=3.10.1,<=3.10.12`로 하드핀이고 기존 환경이 정확히 3.10.12였다.
`numpy`는 `<1.24.0`이 더 좁은 상한이라 1.23.5를 유지해야 한다. **올리면 깨진다.**

### 도구

- Unity CLI 1.0.0-beta.8 스킬을 `~/.claude/skills/unity-cli`에 설치.
- Unity MCP 서버를 `.mcp.json`에 project 스코프로 등록.
  CLI 기본값은 user 스코프인데, 특정 프로젝트 경로에 고정되는 서버를 전역에 두는 건 맞지 않는다.

## 문서 대비 달라진 결정

없음. `docs/00` 개발 순서(RL → VLM → Perception → AR → Integration)를 그대로 따른다.

## 알려진 문제

- `.mcp.json`에 절대경로가 박혀 있다. 다른 머신에서는 안 맞는다.
- conda 환경에 `onnxscript 0.7.1` / `onnx-ir 0.2.1`이 남아 있고 `onnx==1.15.0` 핀과
  충돌 경고를 낸다. torch 2.2.2는 레거시 익스포터가 기본이라 실제 영향은 없다.
  ONNX export 스모크 테스트로 확인했다.
- `CLAUDE.md`는 `worklog/`라고 쓰는데 저장소에 빈 `docs/worklogs/`가 있다.
  CLAUDE.md를 따라 루트 `worklog/`를 썼다.

## 다음

- AR provider 패키지 결정: `com.unity.xr.arcore`(Android), `com.unity.xr.arkit`(iOS).
  타겟 플랫폼 확정 후 설치. XR Interaction Toolkit과 OpenXR Plugin은 불필요
  (탭 인터랙션은 `ARRaycastManager`로 충분, OpenXR은 헤드셋용).
- `docs/05` Stage 1 — ragdoll prefab, single limb target 학습 씬.
- `docs/07` Scene JSON 스키마를 C#/Python 양쪽에 정의.
