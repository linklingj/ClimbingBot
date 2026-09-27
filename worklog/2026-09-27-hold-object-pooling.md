# 2026-09-27 — 홀드에 오브젝트 풀 적용

## 왜

`stage1-04` 분석(09-26 worklog)에서 에피소드당 벽시계의 45%가 홀드
70여 개를 매번 `DestroyImmediate` + `Instantiate`하는 데 나간다고 재 뒀다.
사용자가 `Util/`에 `ObjectPoolManager`/`PoolObject`를 미리 만들어 둬서,
이번에는 그걸 `Hold`에 연결했다.

## 막혀 있던 것

`ObjectPoolManager : Singleton<ObjectPoolManager>`인데 `Singleton<T>`가
프로젝트 어디에도 없어서 **컴파일이 깨져 있었다**(`CS0246`). `Util/Singleton.cs`를
추가했다 --- 첫 접근 시 `FindFirstObjectByType<T>()`로 찾을 뿐 자동 생성은
안 한다. `ObjectPoolManager`의 풀 목록(`poolObjData`)은 인스펙터에서 채우는
값이라, 자동 생성했다간 빈 배열로 뜬다. `PoolObject.Release()`가 이미
`Instance == null`을 정상 케이스로 다루고 있어서(매니저 없으면 그냥
`Destroy`) 그 전제를 그대로 따랐다.

## 무엇을

-   `Hold_0.prefab`에 `PoolObject` 컴포넌트 추가.
-   `ClimbingWall.AddHold`/`ClearHolds`: 매니저가 있으면 `Get`/`Release`,
    없으면 지금까지 하던 대로 `Instantiate`/`DestroyImmediate`. 씬에
    매니저를 안 두면 동작이 안 바뀐다.
-   `Train.unity`에 `ObjectPoolManager` 배치, `Hold_0` 풀 count 96(한 벽
    분량 74개 + 여유).

## 잡은 버그

`ClearHolds()`가 `GetComponentsInChildren<Hold>(true)`로 **비활성 자식까지**
훑고 있었다. 풀에 반환된 홀드는 `SetActive(false)`만 되고 마지막에 있던
벽의 자식으로 그대로 남는다(`ObjectPoolManager.Release`가 부모를 안 옮김).
다음 리셋에서 `true`로 훑으면 그 비활성 홀드를 **다시** `Release()`해서
같은 `PoolObject`가 큐에 두 번 들어간다 --- 이후 두 번의 `Get()`이 같은
인스턴스를 내줘서 서로 다른 두 홀드가 한 Transform을 공유하는 채로 터진다.
Stage 1에서 살아 있는 홀드는 전부 활성이므로(이 코드베이스에서 홀드를
비활성화하는 곳이 여기 말고 없다) `GetComponentsInChildren<Hold>()`(활성만)
로 바꿔도 놓치는 게 없다. 실제로 20회 리셋을 돌려 보고서야 잡았다 ---
정적으로는 "왜 되는지" 설명이 그럴듯해도 물리 확인 없이는 몰랐을 종류의
버그다.

## 실측

1영역, 20회 리셋: 홀드 슬롯 1480개를 인스턴스 **96개**로 돌려 막았다(풀이
안 자람 = 초기 count가 정착값과 맞았다는 뜻). 같은 벽에서 인스턴스 중복
0건, 홀드 위치 오차(로컬 z, `-holdRadius` 대비) 0. 실제 학습된 모델로 30초
자동 재생 --- 10에피소드 완주, 에러 0, 홀드 100% 풀링.

## 안 한 것

16영역 중 15개가 (직접) 비활성화된 상태라 멀티영역 동시 부하는 못 쟀다.
풀 count 96은 한 벽 기준이라 16영역이 전부 켜지면 초기에는 미스가 나고
자체적으로 커지겠지만(Get 실패 시 fallback Instantiate → 다음 Release로
풀에 편입), 16영역 기준 정착값(~1120)까지 미리 늘려 두지는 않았다 ---
탄력적으로 크는 구조라 굳이 선제적으로 채울 필요는 없다고 봤다.
