using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;

public class AgentReplay : MonoBehaviour
{
    // 내부 데이터 구조체
    private struct ReplayStep
    {
        public int TimeStep;
        public Vector2 Action;
        public Vector3 GroundTruthPosition;
    }

    [Header("Replay Settings")]
    public GameObject agentPrefab;
    public TextAsset stateCsvFile; // AgentState가 저장한 CSV 파일

    [Tooltip("AgentState 저장 시 사용했던 frameDuration 값입니다.")]
    public float frameDuration = 0.04f; // AgentManager의 기본값

    [Tooltip("재생 속도를 조절합니다. 1은 실제 속도, 2는 2배속입니다.")]
    public float replaySpeedMultiplier = 1.0f;

    private List<ReplayStep> replayData = new List<ReplayStep>();
    private GameObject agentInstance;

    void Start()
    {
        if (stateCsvFile == null)
        {
            Debug.LogError("State CSV File is not assigned.");
            return;
        }

        if (agentPrefab == null)
        {
            Debug.LogError("Agent Prefab is not assigned.");
            return;
        }

        LoadReplayData();
        StartReplay();
    }

    void LoadReplayData()
    {
        string[] lines = stateCsvFile.text.Split('\n');
        // 첫 줄(헤더)은 건너뛰고 시작
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            string[] values = line.Split(',');

            // CSV 포맷: TimeStep,Action_X,Action_Z,Position_X,Position_Y,Position_Z,...
            if (values.Length > 6)
            {
                try
                {
                    ReplayStep step = new ReplayStep
                    {
                        TimeStep = int.Parse(values[0]),
                        Action = new Vector2(float.Parse(values[1]), float.Parse(values[2])),
                        GroundTruthPosition = new Vector3(float.Parse(values[3]), float.Parse(values[4]), float.Parse(values[5]))
                    };
                    replayData.Add(step);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"Could not parse line {i}: {line}. Error: {e.Message}");
                }
            }
        }
        Debug.Log($"{replayData.Count} replay steps loaded.");
    }

    public void StartReplay()
    {
        if (agentInstance != null)
        {
            Destroy(agentInstance);
        }

        if (replayData.Count > 0)
        {
            // 첫 스텝의 위치를 시작점으로 설정
            agentInstance = Instantiate(agentPrefab, replayData[0].GroundTruthPosition, Quaternion.identity);
            StartCoroutine(ReplayMovement());
        }
        else
        {
            Debug.LogError("No replay data loaded to start replay.");
        }
    }

    private IEnumerator ReplayMovement()
    {
        Debug.Log("Starting replay...");

        // 첫 스텝은 이미 위치가 설정되었으므로, 두 번째 스텝부터 루프 시작
        for (int i = 1; i < replayData.Count; i++)
        {
            ReplayStep previousStep = replayData[i - 1];
            ReplayStep currentStep = replayData[i];

            // 이전 스텝의 Action(속도)을 가져옴
            Vector2 action = previousStep.Action;
            Vector3 velocity = new Vector3(action.x, 0, action.y);

            // 두 스텝 사이의 시간 간격 계산
            // AgentState는 TimeStepInterval마다 저장하므로, 실제 시간은 TimeStep 차이에 frameDuration * 10을 곱해야 함
            // 하지만 여기서는 간단히 TimeStep 차이만큼만 frameDuration을 곱하여 시간 흐름을 맞춤
            int timeStepDifference = currentStep.TimeStep - previousStep.TimeStep;
            float deltaTime = timeStepDifference * frameDuration; // AgentState의 TimeStepInterval에 따라 달라짐

            // 변위 = 속도 * 시간
            Vector3 displacement = velocity * deltaTime;
            agentInstance.transform.position += displacement;

            // --- 오차 계산 및 출력 ---
            Vector3 replayedPosition = agentInstance.transform.position;
            Vector3 groundTruthPosition = currentStep.GroundTruthPosition;
            float error = Vector3.Distance(replayedPosition, groundTruthPosition);

            Debug.Log($"TimeStep: {currentStep.TimeStep}, Position Error: <color=yellow>{error:F5}</color>");

            // 몸통 방향 설정
            if (velocity.sqrMagnitude > 0.0001f)
            {
                agentInstance.transform.rotation = Quaternion.LookRotation(velocity.normalized);
            }

            // 다음 스텝까지 대기 (속도 배율 적용)
            if (replaySpeedMultiplier <= 0) replaySpeedMultiplier = 1f; // 0 또는 음수일 경우, 기본 속도로 변경
            yield return new WaitForSeconds(deltaTime / replaySpeedMultiplier);
        }

        Debug.Log("Replay finished.");
        float finalError = Vector3.Distance(agentInstance.transform.position, replayData[replayData.Count - 1].GroundTruthPosition);
        Debug.Log($"<color=green>Final position error: {finalError:F5}</color>");
    }
}