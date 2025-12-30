using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

public class AgentChunkReplayer : MonoBehaviour
{
    [Header("File Settings")]
    public string jsonlFilePath = "Z_Continuous/agent_1_shifted.jsonl";

    [Header("Playback Settings")]
    [Tooltip("체크하면 0번 청크부터 끝까지 연속으로 재생합니다.")]
    public bool playContinuous = true;

    [Tooltip("단일 청크만 반복해서 보고 싶을 때 사용 (Continuous가 꺼져있을 때 적용)")]
    public int specificChunkIndex = 0;

    [Range(0.1f, 5.0f)]
    public float playSpeed = 1.0f;

    [Header("Debug")]
    public bool showGizmos = true;
    public int currentChunkIndex = 0;

    // 데이터 구조
    [System.Serializable]
    public class ChunkData
    {
        public int chunk_index;
        public float[] startWorldPosition; // [x, y, z] 절대 좌표
        public float[] startForward;       // [x, y, z] 방향 벡터
        public float[][] localCurrent;     // 상대 궤적 (0,0,0 시작)
        public float[] goalLocalPosition;  // 상대 Goal
        public float[] goalWorldPosition;  // 절대 Goal (참고용)
    }

    private List<ChunkData> _allChunks = new List<ChunkData>();
    private Coroutine _playCoroutine;

    void Start()
    {
        LoadData();
        if (_allChunks.Count > 0)
        {
            if (playContinuous)
            {
                StartCoroutine(CoPlaySequence());
            }
            else
            {
                PlaySingleChunk(specificChunkIndex);
            }
        }
    }

    void LoadData()
    {
        string path = jsonlFilePath;
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(Application.dataPath, path);
        }

        if (!File.Exists(path))
        {
            Debug.LogError($"File not found: {path}");
            return;
        }

        string[] lines = File.ReadAllLines(path);
        _allChunks.Clear();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                _allChunks.Add(JsonConvert.DeserializeObject<ChunkData>(line));
            }
            catch { }
        }
        Debug.Log($"Loaded {_allChunks.Count} chunks.");
    }

    // --- 연속 재생 로직 ---
    IEnumerator CoPlaySequence()
    {
        currentChunkIndex = 0;
        while (currentChunkIndex < _allChunks.Count)
        {
            // 현재 청크 재생이 끝날 때까지 대기
            yield return StartCoroutine(CoPlayChunkMovement(_allChunks[currentChunkIndex]));

            // 다음 청크로 인덱스 증가 (끊김 없이)
            currentChunkIndex++;
        }
        Debug.Log("모든 청크 재생 완료");
    }

    public void PlaySingleChunk(int index)
    {
        if (_playCoroutine != null) StopCoroutine(_playCoroutine);
        currentChunkIndex = index;
        _playCoroutine = StartCoroutine(CoLoopSingleChunk(index));
    }

    IEnumerator CoLoopSingleChunk(int index)
    {
        while (true)
        {
            yield return StartCoroutine(CoPlayChunkMovement(_allChunks[index]));
            // 루프 시 잠시 대기하거나 바로 반복
            yield return null;
        }
    }

    // --- 실제 움직임 처리 (핵심) ---
    IEnumerator CoPlayChunkMovement(ChunkData chunk)
    {
        // 1. 기준점 설정 (Global Anchor)
        Vector3 anchorPos = ToVector3(chunk.startWorldPosition);

        // 2. 초기 방향 설정 (해당 청크 시작 시점의 방향)
        Vector3 fwd = ToVector3(chunk.startForward);
        if (fwd != Vector3.zero) transform.rotation = Quaternion.LookRotation(fwd);

        // 3. 상대 궤적을 따라 이동
        // transform.position = Anchor(절대) + Offset(상대)

        float timePerFrame = 0.05f; // 20fps 가정 (데이터 간격에 맞게 수정)

        for (int i = 0; i < chunk.localCurrent.Length; i++)
        {
            Vector3 offset = ToVector3(chunk.localCurrent[i]);

            // 여기가 핵심: 매 프레임 절대 좌표로 변환하여 적용
            Vector3 targetGlobalPos = anchorPos + offset;
            transform.position = targetGlobalPos;

            // 회전 (진행 방향 보기)
            if (i < chunk.localCurrent.Length - 1)
            {
                Vector3 nextOffset = ToVector3(chunk.localCurrent[i + 1]);
                Vector3 dir = (nextOffset - offset).normalized;
                if (dir != Vector3.zero)
                {
                    transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), 15f * Time.deltaTime);
                }
            }

            yield return new WaitForSeconds(timePerFrame / playSpeed);
        }
    }

    Vector3 ToVector3(float[] arr)
    {
        if (arr == null || arr.Length < 3) return Vector3.zero;
        return new Vector3(arr[0], arr[1], arr[2]);
    }

    void OnDrawGizmos()
    {
        if (!showGizmos || _allChunks.Count == 0) return;

        // 현재 재생 중인 청크의 경로 그리기
        if (currentChunkIndex < _allChunks.Count)
        {
            ChunkData c = _allChunks[currentChunkIndex];
            Vector3 anchor = ToVector3(c.startWorldPosition);

            // Path
            Gizmos.color = Color.yellow;
            Vector3 prev = anchor + ToVector3(c.localCurrent[0]);
            foreach (var p in c.localCurrent)
            {
                Vector3 cur = anchor + ToVector3(p);
                Gizmos.DrawLine(prev, cur);
                prev = cur;
            }

            // Goal
            if (c.goalWorldPosition != null)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawWireSphere(ToVector3(c.goalWorldPosition), 0.5f);
                Gizmos.DrawLine(prev, ToVector3(c.goalWorldPosition));
            }
        }
    }
}