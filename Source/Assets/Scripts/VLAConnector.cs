using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

public class VLAConnector : MonoBehaviour
{
    [Header("Required Components")]
    public AgentManager agentManager;
    public GameObject vlaAgentPrefab;

    [Header("VLA Control Settings")]
    [Tooltip("VLA 모델로 제어할 에이전트의 ID 목록")]
    public List<int> vlaAgentIds;
    [Tooltip("VLA 에이전트의 AgentState 데이터를 저장할지 여부")]
    public bool saveVLAData = false;

    [Header("Capture Settings")]
    public int width = 256;
    public int height = 256;

    [Header("Simulation Settings")]
    [Tooltip("한 스텝에 소요되는 시간 (초). Action의 변위 계산에 사용됩니다.")]
    public float frameDuration = 0.04f;

    [Header("Goal Information")]
    public Vector3 goalPosition = new Vector3(-1.14f, 0, 1.07f);

    // VLA가 제어하는 에이전트 목록
    private Dictionary<int, GameObject> vlaAgents = new Dictionary<int, GameObject>();
    private const string FoVCameraPath = "Root/Hips/Spine_01/Spine_02/Spine_03/Neck/Head/FoVCamera";
    // private const string ServerUrl = "http://163.152.162.171:8000/infer_raw"; // "http://127.0.0.1:8000/infer_raw"; //"http://163.152.162.171:8000/infer_raw";
    private const string ServerUrl = "http://127.0.0.1:8000/infer_raw";

    [System.Serializable]
    private class ActionResponse
    {
        public float action_x;
        public float action_z;
    }

    IEnumerator Start()
    {
        if (agentManager == null || vlaAgentPrefab == null)
        {
            Debug.LogError("필수 컴포넌트(AgentManager, VLA Agent Prefab)가 설정되지 않았습니다.");
            yield break; // 코루틴 종료
        }

        // AgentManager가 모든 에이전트를 생성하고 초기화할 때까지 대기
        yield return new WaitUntil(() => agentManager.IsInitialized());
        Debug.Log("AgentManager 초기화 완료. VLA 에이전트 제어권을 가져옵니다.");

        // 지정된 ID의 에이전트 제어권 가져오기
        foreach (int id in vlaAgentIds)
        {
            GameObject originalAgent = agentManager.GetAgentGameObject(id);
            if (originalAgent != null)
            {
                Vector3 startPosition = originalAgent.transform.position;
                Quaternion startRotation = originalAgent.transform.rotation;

                agentManager.RemoveAgent(id);
                Destroy(originalAgent);

                GameObject newAgent = Instantiate(vlaAgentPrefab, startPosition, startRotation);
                newAgent.name = $"VLA_Agent_{id}";

                // AgentState의 IsSaveData 값 설정
                if (newAgent.TryGetComponent<AgentState>(out var agentState))
                {
                    agentState.isSaveData = saveVLAData;
                }
                else
                {
                    Debug.LogWarning($"VLA Agent Prefab에 AgentState 컴포넌트가 없습니다. 데이터 저장을 제어할 수 없습니다.");
                }

                vlaAgents.Add(id, newAgent);
                Debug.Log($"에이전트 {id}의 제어권을 가져왔습니다. 데이터 저장: {saveVLAData}");
            }
            else
            {
                Debug.LogWarning($"AgentManager에 에이전트 ID {id}가 존재하지 않습니다.");
            }
        }

        yield return new WaitUntil(() => agentManager.isMoving);
        StartCoroutine(ActionLoop());
    }

    private IEnumerator ActionLoop()
    {
        while (true)
        {
            Time.timeScale = 0f;
            Debug.Log("<color=orange>시뮬레이션 정지. VLA 에이전트들의 행동을 결정합니다.</color>");

            foreach (var agentEntry in vlaAgents)
            {
                yield return StartCoroutine(ProcessSingleAgent(agentEntry.Key, agentEntry.Value));
            }

            Time.timeScale = 1f;
            Debug.Log("<color=green>시뮬레이션 재개.</color>");

            yield return new WaitForSecondsRealtime(frameDuration);
        }
    }

    private IEnumerator ProcessSingleAgent(int agentId, GameObject agent)
    {
        Transform agentTransform = agent.transform;
        Debug.Log($"VLA 에이전트 {agentId} 처리 중...");

        // 에이전트의 FoVCamera 찾기
        Transform cameraTransform = agentTransform.Find(FoVCameraPath);
        if (cameraTransform == null)
        {
            Debug.LogError($"에이전트 {agentId}에서 카메라를 찾지 못했습니다. 경로를 확인하세요: {FoVCameraPath}");
            yield break;
        }
        if (!cameraTransform.TryGetComponent<Camera>(out var agentCamera))
        {
            Debug.LogError($"에이전트 {agentId}의 FoVCamera 오브젝트에 Camera 컴포넌트가 없습니다.");
            yield break;
        }

        Vector3 currentPosition = agentTransform.position;
        float goalDistance = Vector3.Distance(currentPosition, goalPosition); // ToDo: 여러 목표 지점 지원

        // PNG 바이트로 카메라 프레임 캡처
        byte[] pngBytes = CaptureCameraToPNG(agentCamera, width, height);

        // multipart/form-data 구성
        var form = new List<IMultipartFormSection>
        {
            new MultipartFormDataSection("Position_X",     currentPosition.x.ToString("F4")),
            new MultipartFormDataSection("Position_Z",     currentPosition.z.ToString("F4")),
            new MultipartFormDataSection("GoalPosition_X", goalPosition.x.ToString("F2")),
            new MultipartFormDataSection("GoalPosition_Z", goalPosition.z.ToString("F2")),
            new MultipartFormDataSection("GoalDistance",   goalDistance.ToString("F2")),
            // 파일 필드 (name: "FoVImage", 파일명과 MIME 타입 지정)
            new MultipartFormFileSection("FoVImage", pngBytes, "fov.png", "image/png")
        };

        using UnityWebRequest req = UnityWebRequest.Post(ServerUrl, form);
        // 필요시 타임아웃/압축 설정
        req.timeout = 60;
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            Debug.Log($"에이전트 {agentId} 응답: {req.downloadHandler.text}");
            try
            {
                ActionResponse response = JsonUtility.FromJson<ActionResponse>(req.downloadHandler.text);
                ApplyAction(new Vector2(response.action_x, response.action_z), agentTransform);
            }
            catch (System.Exception e) { Debug.LogError($"응답 파싱 실패: {e.Message}"); }
        }
        else
        {
            Debug.LogError($"에이전트 {agentId} 요청 실패: {req.error}");

        }
    }

    private void ApplyAction(Vector2 action, Transform agentTransform)
    {
        Vector3 velocity = new Vector3(action.x, 0, action.y);
        agentTransform.position += velocity * frameDuration;

        if (velocity.sqrMagnitude > 0.0001f)
        {
            agentTransform.rotation = Quaternion.LookRotation(velocity.normalized);
        }
    }

    // 카메라를 PNG 바이트로 캡처
    private byte[] CaptureCameraToPNG(Camera cam, int w, int h)
    {
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        var prevRT = cam.targetTexture;

        cam.targetTexture = rt;
        cam.Render();

        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();

        byte[] png = tex.EncodeToPNG();

        // 정리
        cam.targetTexture = prevRT;
        RenderTexture.active = null;
        rt.Release();
        Destroy(rt);
        Destroy(tex);

        return png;
    }
}
