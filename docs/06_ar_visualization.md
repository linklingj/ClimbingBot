# 모듈 6 --- AR Animation Visualization

## 목적

VLM + RL이 생성한 등반 동작을 실제 클라이밍 벽과 정렬된 AR 캐릭터
애니메이션으로 사용자에게 보여준다.

## 입력

-   wall anchor
-   hold world positions
-   humanoid animation/trajectory
-   route metadata

## 출력

실제 카메라 화면 위에: - 선택된 route - 홀드 강조 - climbing humanoid -
다음 limb/hold 안내

를 표시한다.

## 좌표 변환

RL 학습 환경과 실제 AR 환경이 동일한 절대 좌표계를 사용할 필요는 없다.

``` text
RL Wall Local Coordinate
        ↓
Scale / Transform
        ↓
AR Wall Local Coordinate
        ↓
World Coordinate
```

wall width/height와 anchor transform을 기준으로 mapping한다.

## 애니메이션 처리

RL 실행 결과는 실시간 policy inference 자체를 보여주는 것보다
trajectory/animation으로 저장한 후 재생하는 방식을 우선 고려한다.

저장 후보: - body part transform sequence - humanoid joint rotations -
keyframe-reduced animation

## 사용자 흐름

``` text
1. 벽 스캔
2. 루트 검출
3. 사용자가 루트 선택
4. 계획 생성
5. 시뮬레이션 수행
6. 결과 애니메이션 준비
7. AR로 재생
```

## UI 후보

-   route 색상/홀드 강조
-   Play / Pause
-   이전/다음 move
-   속도 조절
-   현재 이동 limb 표시
-   target hold 강조
-   루트/스타트/탑 수정

## 정확도 문제

실제 홀드와 캐릭터의 손/발이 어긋나면 시스템 품질이 크게 낮아 보인다.
따라서 캐릭터 자체의 렌더링 품질보다 **registration accuracy**를
우선한다.

## 평가

-   실제 hold center ↔ virtual contact point 오차
-   tracking 중 drift
-   재배치 후 anchor stability
-   animation playback consistency
-   사용자에게 move sequence가 명확하게 전달되는지

## 비목표

초기 버전에서는 실제 사용자의 체형과 정확히 동일한 avatar fitting이나
실시간 사용자 pose tracking을 필수 기능으로 두지 않는다.
