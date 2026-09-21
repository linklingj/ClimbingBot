# 모듈 2 --- Route Extraction

## 목적

검출된 홀드들을 실제 클라이밍 루트 단위로 그룹화하고 각 루트의 start/top
hold를 결정한다.

## 핵심 아이디어

색상 하나만으로 그룹화하지 않고 다음 세 정보를 함께 사용한다.

1.  **색상 유사도**
2.  **공간적 위치**
3.  **연결 가능성(Reachability / Connectivity)**

## 입력

``` json
{
  "holds": [
    {
      "id": 1,
      "color_feature": [50.2, 61.4, 42.0],
      "wall_position": [0.2, 0.3]
    }
  ]
}
```

## 그래프 표현

각 홀드를 node로 보고 두 홀드 사이에 사람이 이동할 가능성이 있을 때
edge를 생성한다.

``` text
H1 ─ H3 ─ H5
│         │
H2       H7
```

edge score 예시:

\[ S\_{ij}=w_c C\_{ij}+w_s S\_{ij}+w_r R\_{ij} \]

-   `C`: 색상 유사도
-   `S`: 공간 관계 점수
-   `R`: 두 홀드 사이의 연결 가능성

초기 버전의 reachability는 학습 모델보다 거리/방향 기반 heuristic으로
시작한다.

## Clustering

후보: - DBSCAN/HDBSCAN 기반 feature clustering - graph community
detection - constrained clustering - custom graph traversal

단순 색상 clustering 후 spatial graph로 분리하는 2-stage 방식도
baseline으로 유지한다.

## Start Hold 판별

후보 특징: - 루트 하단에 위치 - 다른 루트 홀드와 색상 일치 - 초기
자세에서 접근 가능 - 동일 높이에 두 개의 start hold가 존재할 가능성 고려

출력은 하나가 아니라 `start_hold_ids` 배열을 지원한다.

## Top Hold 판별

기본적으로: - 해당 cluster 상단의 홀드 - 그래프상 terminal 성격 - 이전
홀드에서 접근 가능

confidence가 낮은 경우 사용자에게 후보를 보여주고 수정 가능하게 한다.

## 출력

``` json
{
  "route_id": 2,
  "hold_ids": [3, 6, 8, 10, 14],
  "start_hold_ids": [3, 6],
  "top_hold_id": 14,
  "confidence": 0.91
}
```

## 평가

-   Hold-to-route assignment accuracy
-   Route-level precision/recall
-   Start hold accuracy
-   Top hold accuracy

## 위험 요소

가장 중요한 경우는 **같은 벽에 같은 색의 서로 다른 루트가 존재하는
상황**이다. 따라서 공간적 연결 그래프가 색상 정보보다 route separation에
중요한 역할을 할 수 있다.
