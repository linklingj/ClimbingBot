# 모듈 1 --- Hold Instance Segmentation

## 목적

클라이밍 벽 이미지에서 개별 홀드를 검출하고 후속 모듈이 사용할 수 있는
구조화된 홀드 데이터를 생성한다.

## 범위

홀드의 종류, grip type, 모양 의미는 분류하지 않는다. 모든 홀드는 하나의
`hold` 클래스로 처리한다.

## 입력

-   iPhone RGB 이미지 또는 영상 프레임

## 출력

각 instance에 대해:

``` json
{
  "id": 1,
  "confidence": 0.94,
  "mask": "...",
  "image_center": [0.42, 0.31],
  "color_feature": [52.1, 68.3, 41.8],
  "color_label": "red"
}
```

## 처리 과정

``` text
RGB Frame
   ↓
Instance Segmentation
   ↓
Mask Filtering
   ↓
Center / Area 계산
   ↓
Mask 내부 색상 샘플링
   ↓
Color Feature / Label
```

색상은 bounding box 전체가 아니라 segmentation mask 내부 픽셀을
사용한다. 조명 변화에 대한 강건성을 위해 RGB만 사용하는 것보다 HSV 또는
Lab 공간의 통계량을 병행한다.

## 데이터 모델

필수: - `hold_id` - `mask` - `confidence` - `image_center` -
`color_feature`

선택: - `color_label` - mask area - bounding box

## 평가

-   Mask mAP / IoU
-   Hold recall
-   작은 홀드 recall
-   Color classification accuracy 또는 intra/inter-route color distance

## 주요 실패 조건

-   홀드가 매우 작음
-   손이나 사람에 의해 가려짐
-   벽과 홀드 색이 유사함
-   그림자/반사
-   볼륨과 홀드 경계 혼동

## 비목표

-   홀드 종류 분류
-   잡는 방향 추정
-   surface normal 추정
-   grip quality 추정
