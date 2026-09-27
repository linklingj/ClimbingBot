using System.Collections;
using System.Collections.Generic;
using UnityEngine;

//풀링되는 오브젝트가 컴포넌트로 가지고 있어야 하는 클래스
public class PoolObject : MonoBehaviour
{
    private int _prefabID = -1;//풀링하는 프리펩의 정보

    public int PrefabID { get { return _prefabID; } set { _prefabID = value; } }

    //public Transform parentTransform;//풀로 돌아갈 경우 부모가 되는 트렌스폼

    public void Get()
    {
        gameObject.SetActive(true);
    }

    public void Release()
    {
        if (_prefabID == -1 || ObjectPoolManager.Instance == null)
        {
            Destroy(gameObject);
            return;
        }
        
        if (!ObjectPoolManager.Instance.Release(this)) //풀 매니져에 반환 시도, 실패시 삭제
        {
            Destroy(gameObject);
            return;
        }

        transform.position = Vector3.zero;
        gameObject.SetActive(false);
    }

    //파티클용 반환 함수. 오브젝트의 파티클이 종료되면 자동 풀 반환 진행
    private void OnParticleSystemStopped()
    {
        Release();
    }

    //오브젝트가 애니메이션타입인 경우 애니메이션이벤트를 통해 오브젝트의 풀 반환을 진행
    private void OnAnimationStop() { 
        Release();
    }
}