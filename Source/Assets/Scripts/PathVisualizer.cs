using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;

public class PathVisualizer : MonoBehaviour
{
    public GameObject lineRendererPrefab; // LineRenderer를 프리팹으로 만들어서 할당
    public TextAsset dataFile; // 데이터 파일
    private Dictionary<int, List<Vector3>> agentPositions = new Dictionary<int, List<Vector3>>();

    void Start()
    {
        // 파일에서 데이터를 읽어와 에이전트들의 위치 데이터를 파싱
        LoadDataFromFile(dataFile.text);

        // 각 에이전트의 이동 경로를 시각화
        foreach (var agentId in agentPositions.Keys)
        {
            VisualizePath(agentId, agentPositions[agentId]);
        }
    }

    // 파일에서 데이터를 읽어와 에이전트 위치 데이터를 저장
    void LoadDataFromFile(string filePath)
    {
        string[] lines = File.ReadAllLines(filePath);

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // 데이터 파싱
            string[] parts = line.Split(',');

            int agentId = int.Parse(parts[0].Trim());
            float xPos = float.Parse(parts[1].Trim()) / 20.0f;
            float yPos = float.Parse(parts[2].Trim()) / 20.0f;

            Vector3 position = new Vector3(xPos, 0, yPos); // z는 0으로 설정

            // 에이전트별로 위치 리스트 생성 및 추가
            if (!agentPositions.ContainsKey(agentId))
            {
                agentPositions[agentId] = new List<Vector3>();
            }

            agentPositions[agentId].Add(position);
        }
    }

    // 경로를 시각화하는 함수
    void VisualizePath(int agentId, List<Vector3> positions)
    {
        // LineRenderer 프리팹 인스턴스 생성
        GameObject lineRendererObj = Instantiate(lineRendererPrefab);

        // LineRenderer 컴포넌트를 가져와서 설정
        LineRenderer lineRenderer = lineRendererObj.GetComponent<LineRenderer>();

        if (lineRenderer == null)
        {
            Debug.LogError("LineRenderer component not found in prefab.");
            return;
        }

        lineRenderer.positionCount = positions.Count;
        lineRenderer.SetPositions(positions.ToArray());

        // 라인의 색상이나 두께 등 추가 설정이 가능
        lineRenderer.startWidth = 0.1f;
        lineRenderer.endWidth = 0.1f;
        lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
        lineRenderer.startColor = Color.green;
        lineRenderer.endColor = Color.red;
    }
}