using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Linq;

public class AgentManager : MonoBehaviour
{
    public ParserType parserType = ParserType.Output; // 파서 타입 선택
    public bool isHeadTurn = false;
    public List<GameObject> agentPrefabs; // 에이전트 프리팹
    public TextAsset dataFile; // 인스펙터에서 할당할 데이터 파일
    public TextAsset homographyFile; // 인스펙터에서 할당할 호모그래피 파일
    public TextAsset groupFile; // 인스펙터에서 할당할 그룹 파일

    public Vector3 simulationOffset = Vector3.zero; // 시뮬레이션 전체 오프셋
    public float simulationScale = 1.0f; // 시뮬레이션 XZ 스케일

    public static Dictionary<int, List<int>> groupData;
    public int seed = 42;
    // VLAConnector가 접근할 수 있도록 public으로 변경
    public Dictionary<int, AgentController> agentControllers = new Dictionary<int, AgentController>(); // 에이전트별 컨트롤러 저장
    private Dictionary<int, AgentState> agentStates = new Dictionary<int, AgentState>(); // 에이전트별 상태 저장
    private Dictionary<int, List<AgentData>> agentData = new Dictionary<int, List<AgentData>>(); // 에이전트별 데이터 저장
    public float currentFrame = 0; // 현재 프레임 관리
    public int maxFrame = 10000; // 최대 프레임 설정 (필요시 조정 가능)
    public float frameDuration = 0.04f; // 각 프레임 사이의 간격 (초)

    public bool isMoving = false; // 에이전트가 움직이고 있는지 여부
    private bool isInitialized = false; // 초기화 완료 플래그

    private Matrix4x4 homographyMatrix = Matrix4x4.identity;
    private bool useHomography = false;
    public bool isSavingData = false;
    void Start()
    {
        Random.InitState(seed);  // 42는 예시, 원하는 seed로 바꿔도 됨

        // 그룹 데이터를 먼저 로드
        LoadGroupData();

        if (homographyFile != null)
        {
            LoadHomographyMatrix(homographyFile.text);
            useHomography = true;
        }

        // 파일에서 데이터를 읽어오기
        LoadDataFromFile(dataFile.text);

        foreach (var agent in agentPrefabs)
        {
            agent.GetComponent<AgentState>().isSaveData = isSavingData;
        }

        // 데이터 로드 후, 각 에이전트의 데이터 리스트를 프레임 순으로 정렬
        foreach (var dataList in agentData.Values)
        {
            dataList.Sort((a, b) => a.frame.CompareTo(b.frame));
        }

        // 에이전트들을 초기화 및 생성
        foreach (var agentId in agentData.Keys)
        {
            // 데이터가 2개 미만인 에이전트는 생성하지 않음
            if (agentData[agentId].Count < 2) continue;

            GameObject agentPrefab = agentPrefabs[Random.Range(0, agentPrefabs.Count)];
            GameObject agent = Instantiate(agentPrefab);
            AgentController controller = agent.GetComponent<AgentController>();
            AgentState agentState = agent.GetComponent<AgentState>();

            // ID를 컨트롤러와 상태 스크립트 모두에 명시적으로 할당
            controller.agentId = agentId;
            agentState.AgentId = agentId;

            agentControllers.Add(agentId, controller);
            agentStates.Add(agentId, agentState);
            controller.isHeadTurn = isHeadTurn;

            // 정렬된 리스트의 첫 프레임과 두 번째 프레임 데이터로 초기 위치 설정
            AgentData firstData = agentData[agentId][0];
            AgentData secondData = agentData[agentId][1];
            controller.SetInitialPosition(TransformPosition(firstData.position), TransformPosition(secondData.position));

            List<Vector3> positionsList = new List<Vector3>();
            foreach (AgentData data in agentData[agentId])
            {
                positionsList.Add(TransformPosition(data.position));
            }
            controller.positions = positionsList;
            controller.agentId = agentId;

            agent.SetActive(false);
        }

        isInitialized = true; // 모든 에이전트 생성 후 초기화 완료 플래그 설정
    }

    // VLAConnector가 초기화 완료를 확인할 수 있는 메서드
    public bool IsInitialized()
    {
        return isInitialized;
    }

    // VLAConnector가 제어할 에이전트를 가져가는 메서드
    public GameObject GetAgentGameObject(int agentId)
    {
        if (agentControllers.TryGetValue(agentId, out AgentController controller))
        {
            return controller.gameObject;
        }
        return null;
    }

    // VLAConnector가 제어권을 가져간 에이전트를 Manager의 관리 목록에서 제거하는 메서드
    public void RemoveAgent(int agentId)
    {
        if (agentControllers.ContainsKey(agentId))
        {
            agentControllers.Remove(agentId);
            agentStates.Remove(agentId);
            if (agentData.ContainsKey(agentId))
            {
                agentData.Remove(agentId);
            }
            Debug.Log($"AgentManager: Agent {agentId} has been released for VLA control.");
        }
    }

    void LoadGroupData()
    {
        if (groupFile == null)
        {
            groupData = null;
            return;
        }

        groupData = new Dictionary<int, List<int>>();
        string[] lines = groupFile.text.Split(new[] { '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries);

        foreach (string line in lines)
        {
            try
            {
                int[] ids = line.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                if (ids.Length > 1)
                {
                    List<int> groupMembers = new List<int>(ids);
                    foreach (int id in ids)
                    {
                        if (!groupData.ContainsKey(id))
                        {
                            groupData.Add(id, groupMembers);
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"Could not parse group line: '{line}'. Error: {e.Message}");
            }
        }
    }

    Vector3 TransformPosition(Vector3 position)
    {
        Vector3 transformedPosition = position;
        transformedPosition.x *= simulationScale;
        transformedPosition.z *= simulationScale;
        transformedPosition += simulationOffset;
        return transformedPosition;
    }

    void LoadHomographyMatrix(string fileContent)
    {
        StringReader reader = new StringReader(fileContent);
        float[] h = new float[9];
        int idx = 0;
        for (int i = 0; i < 3; i++)
        {
            string line = reader.ReadLine();
            if (line == null) { Debug.LogError("Not enough lines in H.txt"); return; }
            string[] parts = line.Trim().Split(new char[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) { Debug.LogError($"Invalid line format in H.txt: '{line}'"); return; }
            for (int j = 0; j < 3; j++)
            {
                if (!float.TryParse(parts[j], out h[idx])) { Debug.LogError($"Failed to parse float: '{parts[j]}'"); return; }
                idx++;
            }
        }

        homographyMatrix.SetRow(0, new Vector4(h[0], h[1], h[2], 0));
        homographyMatrix.SetRow(1, new Vector4(h[3], h[4], h[5], 0));
        homographyMatrix.SetRow(2, new Vector4(h[6], h[7], h[8], 0));
        homographyMatrix.SetRow(3, new Vector4(0, 0, 0, 1));
    }

    public void StartMovingAgents()
    {
        if (!isMoving)
        {
            isMoving = true;
            StartCoroutine(UpdateAgents());
        }
    }

    public void Update()
    {
        // 코루틴 한 번만 호출
        if (!isMoving)
        {
            if (Input.GetKeyDown(KeyCode.Space))
            {
                StartMovingAgents();
            }
        }
    }

    void LoadDataFromFile(string fileContent)
    {
        agentData = DatasetParser.Parse(parserType, fileContent, homographyMatrix, useHomography);
    }

    IEnumerator UpdateAgents()
    {
        Dictionary<int, int> agentDataIndices = new Dictionary<int, int>();
        foreach (var agentId in agentControllers.Keys)
        {
            agentDataIndices[agentId] = 0;
        }

        while ((int)currentFrame <= maxFrame)
        {
            // agentControllers.Keys를 복사하여 사용 (VLAConnector가 반복 중 컬렉션을 수정할 수 있으므로)
            List<int> agentIdsToUpdate = new List<int>(agentControllers.Keys);

            foreach (var agentId in agentIdsToUpdate)
            {
                if (!agentControllers.ContainsKey(agentId)) continue; // VLA에 의해 제거되었을 수 있음

                AgentController controller = agentControllers[agentId];
                List<AgentData> dataList = agentData[agentId];

                if (dataList.Count < 2) continue;

                int currentIndex = agentDataIndices[agentId];
                while (currentIndex < dataList.Count - 1 && dataList[currentIndex + 1].frame <= currentFrame)
                {
                    currentIndex++;
                }
                agentDataIndices[agentId] = currentIndex;

                AgentData currentData = dataList[currentIndex];
                AgentData nextData = (currentIndex + 1 < dataList.Count) ? dataList[currentIndex + 1] : null;

                if (nextData != null && currentData.frame <= currentFrame && nextData.frame > currentFrame)
                {
                    if (!controller.gameObject.activeSelf)
                    {
                        controller.gameObject.SetActive(true);
                    }

                    float t = (nextData.frame - currentData.frame) > 0 ? (currentFrame - currentData.frame) / (nextData.frame - currentData.frame) : 0;
                    Vector3 interpolatedPosition = Vector3.Lerp(currentData.position, nextData.position, t);
                    Vector3 direction = (nextData.position - currentData.position).normalized;

                    // 속도 벡터 계산
                    float speed = (nextData.frame - currentData.frame) > 0 ? (nextData.position - currentData.position).magnitude / ((nextData.frame - currentData.frame) * frameDuration) : 0;
                    Vector3 velocity = direction * speed;
                    // if (agentId == 1)
                    // {
                    //     Debug.Log($"Agent {agentId} Positions: {(nextData.position - currentData.position).magnitude} Frame: {nextData.frame - currentData.frame} Speed: {speed} Velocity: {velocity}");
                    // }
                    float interpolatedHeadOrientation = Mathf.LerpAngle(currentData.headOrientation, nextData.headOrientation, t);

                    controller.SetInterpolatedTransform(TransformPosition(interpolatedPosition), velocity, interpolatedHeadOrientation);

                    // AgentState에 상태 기록 요청
                    AgentState agentState = agentStates[agentId];
                    int currentFrameInt = (int)currentFrame;
                    if (currentFrameInt > agentState.lastSavedFrame && currentFrameInt % agentState.TimeStepInterval == 0)
                    {
                        agentState.RecordState(new Vector2(velocity.x, velocity.z), currentFrameInt);
                    }
                }
                else
                {
                    if (controller.gameObject.activeSelf)
                    {
                        controller.gameObject.SetActive(false);
                    }
                }
            }

            currentFrame += 1.0f;
            yield return new WaitForSeconds(frameDuration);
        }
    }
}

[System.Serializable]
public class AgentData
{
    public Vector3 position;
    public int frame;
    public float headOrientation;

    public AgentData(Vector3 position, int frame, float headOrientation)
    {
        this.position = position;
        this.frame = frame;
        this.headOrientation = headOrientation;
    }
}
