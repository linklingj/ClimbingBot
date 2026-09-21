# 모듈 3 --- AR Wall Reconstruction

## 목적

2D 이미지에서 검출된 홀드를 실제 수직 벽의 좌표계에 배치하여
Unity/VLM/RL이 사용할 공간 표현을 만든다.

## 환경 가정

-   일반 iPhone
-   LiDAR 없음
-   AR Foundation 사용
-   벽은 하나의 수직 평면으로 근사
-   복잡한 overhang, corner, volume geometry는 초기 범위에서 제외

## 파이프라인

``` text
AR Camera
   │
   ├─ Camera Pose
   └─ RGB Frame
        ↓
Vertical Plane Detection
        ↓
Wall Plane / Anchor
        ↓
Hold Pixel Coordinate
        ↓
Camera Ray
        ↓
Ray-Plane Intersection
        ↓
World Position
```

홀드 중심 픽셀에서 camera ray를 생성하고 검출된 wall plane과 교차시켜 3D
위치를 얻는다.

## 좌표계

RL/VLM에는 AR world coordinate를 직접 사용하기보다 wall-local 좌표를
사용한다.

``` text
Wall Local
Y ↑
  │
  │     H3
  │ H2
  │
  └────────→ X
```

벽이 평면이므로 대부분의 planning에는 `(x, y)`만 필요하다.

## 데이터

``` json
{
  "wall": {
    "origin": [0.0, 1.1, 2.3],
    "normal": [0.02, 0.01, -0.99],
    "width": 4.1,
    "height": 3.8
  },
  "holds": [
    {
      "id": 1,
      "wall_position": [0.54, 1.21],
      "world_position": [0.41, 1.83, 2.28]
    }
  ]
}
```

## AR 정렬 전략

1.  사용자가 벽을 충분히 스캔한다.
2.  vertical plane을 선택/확정한다.
3.  wall anchor를 생성한다.
4.  홀드 위치를 anchor local coordinate로 저장한다.
5.  이후 프레임에서는 anchor를 기준으로 overlay한다.

## 위험 요소

LiDAR가 없으므로 plane estimation과 visual tracking 오차가 누적될 수
있다. 특히 최종 AR 캐릭터의 손/발과 실제 홀드 사이의 작은 오차도 눈에 잘
띈다.

따라서 AR overlay의 핵심 평가지표는 **홀드 중심 기준 화면/실세계 정렬
오차**로 정의한다.

## 비목표

-   복잡한 3D 벽 reconstruction
-   홀드 자체의 depth/normal reconstruction
-   overhang의 정확한 geometry 추정
