using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using Unity.VisualScripting;

public class JsonTest : MonoBehaviour
{
    private void Awake()
    {
        print("Start");
    }
    void Start()
    {
        // 사용자님이 주신 데이터 중 일부를 하드코딩했습니다.
        string json = "{\"chunk_index\": 12, \"globalFrames\": [241, 242], \"groups\": [{\"index\": 0, \"members\": [{\"id\": 1, \"abs\": [-15.4618, 0, 1.911904], \"rel\": [0, 0, 0]}, {\"id\": 2, \"abs\": [-15.54805, 0, 2.648875], \"rel\": [-0.086253, 0, 0.736971]}]}]}";

        Debug.Log("=== JSON 파싱 테스트 시작 ===");

        try
        {
            var data = JsonConvert.DeserializeObject<ChunkData>(json);

            if (data == null) Debug.LogError("데이터가 NULL입니다!");
            else if (data.groups == null) Debug.LogError("Groups 리스트가 NULL입니다! (여기가 문제)");
            else
            {
                Debug.Log($"성공! Groups 개수: {data.groups.Count}");
                Debug.Log($"첫번째 그룹 멤버 수: {data.groups[0].members.Count}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"파싱 에러: {e.Message}");
        }
    }

    // 테스트용 클래스 정의 (기존과 동일)
    public class GroupMember { public int id; public float[] rel; }
    public class GroupInfo { public int index; public List<GroupMember> members; }
    public class ChunkData
    {
        public List<int> globalFrames;
        public List<GroupInfo> groups; // 여기 이름이 JSON과 똑같아야 함
    }
}