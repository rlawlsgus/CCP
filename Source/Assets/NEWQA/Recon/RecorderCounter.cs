using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Text;

public class RecorderCounter : MonoBehaviour
{
    [Header("Settings")]
    public float rayDistance = 2.0f;
    public float collisionRadius = 0.5f;
    public float groupDistanceThreshold = 5.0f;

    [Header("Image Capture")]
    public bool saveImage = true;
    public int imageWidth = 512;
    public int imageHeight = 512;

    [Header("Status")]
    public bool isRecordingFinished = false;

    // ★ [추가] 현재 그룹 멤버들과의 평균 거리 (GUI 확인용)
    public float currentGroupAverageDist = -1.0f;

    // 내부 변수
    private GlobalCrowdReplayer _replayer;
    private int _myId;
    private StreamWriter _writer;
    private bool _isWriterOpen = false;

    private Camera _agentCamera;
    private string _imagesDir;

    // ★ 초기화
    public void Initialize(GlobalCrowdReplayer replayer, int myId, string runRootDir)
    {
        _replayer = replayer;
        _myId = myId;
        isRecordingFinished = false;

        // 1. 카메라 찾기
        Transform camTrans = transform.Find("Rig 1/HeadAim/Camera");
        if (camTrans != null) _agentCamera = camTrans.GetComponent<Camera>();
        else Debug.LogWarning($"[Recorder] Agent {myId}: Camera not found at 'Rig 1/HeadAim/Camera'");

        // 2. 폴더 생성
        string agentsDir = Path.Combine(runRootDir, "agents");
        if (!Directory.Exists(agentsDir)) Directory.CreateDirectory(agentsDir);

        _imagesDir = Path.Combine(runRootDir, "images", $"agent_{myId}");
        if (saveImage && !Directory.Exists(_imagesDir)) Directory.CreateDirectory(_imagesDir);

        // 3. CSV 파일 열기
        string csvPath = Path.Combine(agentsDir, $"agent_{myId}.csv");
        _writer = new StreamWriter(csvPath, false, Encoding.UTF8);

        // ★ [수정] 헤더에 GroupDist 추가
        // Frame, Ground, ObstacleCol, AgentCol, GroupStatus, GroupDist, LeftTime, ImagePath
        _writer.WriteLine("Frame,Ground,ObsCol,AgentCol,GroupStat,GroupDist,LeftTime,ImagePath");
        _isWriterOpen = true;
    }

    public void FinishRecording()
    {
        if (isRecordingFinished) return;

        if (_isWriterOpen && _writer != null)
        {
            _writer.Flush();
            _writer.Close();
            _isWriterOpen = false;
        }
        isRecordingFinished = true;
    }

    public void AnalyzeFrame(List<int> groupMemberIds, float remainingSeconds)
    {
        if (isRecordingFinished) return;

        // 1. 분석 (여기서 currentGroupAverageDist가 계산됨)
        CheckGround();
        CheckCollisions();
        CheckGroupDistance(groupMemberIds);

        // 2. 이미지 캡처
        string savedImagePath = "None";
        if (saveImage && _agentCamera != null)
        {
            savedImagePath = CaptureCamera(_replayer.currentGlobalFrame);
        }

        // 3. CSV 기록
        if (_isWriterOpen)
        {
            // ★ [수정] currentGroupAverageDist 추가
            string line = string.Format("{0},{1},{2},{3},{4},{5:F2},{6:F2},{7}",
                _replayer.currentGlobalFrame,   // 0
                currentGroundTag,               // 1
                isCollidingObstacle,            // 2
                isCollidingAgent,               // 3
                groupStatus,                    // 4
                currentGroupAverageDist,        // 5 (거리)
                remainingSeconds,               // 6
                savedImagePath                  // 7
            );
            _writer.WriteLine(line);
        }
    }

    // ★ 카메라 캡처 로직
    private string CaptureCamera(int frameIndex)
    {
        RenderTexture rt = new RenderTexture(imageWidth, imageHeight, 24);
        RenderTexture prevActive = RenderTexture.active;
        RenderTexture prevTarget = _agentCamera.targetTexture;

        try
        {
            _agentCamera.targetTexture = rt;
            _agentCamera.Render();

            RenderTexture.active = rt;
            Texture2D screenShot = new Texture2D(imageWidth, imageHeight, TextureFormat.RGB24, false);
            screenShot.ReadPixels(new Rect(0, 0, imageWidth, imageHeight), 0, 0);
            screenShot.Apply();

            byte[] bytes = screenShot.EncodeToPNG();
            Destroy(screenShot);

            string fileName = $"frame_{frameIndex}.png";
            string fullPath = Path.Combine(_imagesDir, fileName);
            File.WriteAllBytes(fullPath, bytes);

            return fullPath;
        }
        finally
        {
            _agentCamera.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            rt.Release();
            Destroy(rt);
        }
    }

    void OnDestroy()
    {
        FinishRecording();
    }

    // --- 분석 함수들 ---
    public string currentGroundTag = "None";
    public bool isCollidingObstacle = false;
    public bool isCollidingAgent = false;
    public string groupStatus = "No Group";

    void CheckGround()
    {
        RaycastHit hit;
        if (Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.down, out hit, rayDistance))
            currentGroundTag = hit.collider.tag;
        else currentGroundTag = "None";
    }

    void CheckCollisions()
    {
        isCollidingObstacle = false;
        isCollidingAgent = false;
        Collider[] hits = Physics.OverlapSphere(transform.position, collisionRadius);
        foreach (var col in hits)
        {
            if (col.gameObject == this.gameObject) continue;
            if (col.CompareTag("Obstacle")) isCollidingObstacle = true;
            else if (col.CompareTag("Agent")) isCollidingAgent = true;
        }
    }

    void CheckGroupDistance(List<int> memberIds)
    {
        // 초기화
        currentGroupAverageDist = -1.0f;

        if (memberIds == null || memberIds.Count == 0)
        {
            groupStatus = "No Group";
            return;
        }

        bool anyFar = false, anyClose = false;
        float totalDistance = 0f; // ★ 거리 합계
        int validMemberCount = 0; // ★ 실제 위치 확인된 멤버 수

        foreach (int mId in memberIds)
        {
            if (mId == _myId) continue;

            Vector3? pos = _replayer.GetAgentPosition(mId);
            if (pos.HasValue)
            {
                float dist = Vector3.Distance(transform.position, pos.Value);

                // 거리 누적
                totalDistance += dist;
                validMemberCount++;

                // 상태 체크
                if (dist <= groupDistanceThreshold) anyClose = true;
                else anyFar = true;
            }
        }

        // ★ 평균 거리 계산
        if (validMemberCount > 0)
        {
            currentGroupAverageDist = totalDistance / validMemberCount;
        }
        else
        {
            currentGroupAverageDist = -1.0f; // 멤버는 있는데 로드가 안 된 경우 등
        }

        // 상태 문자열 결정
        if (anyFar && !anyClose) groupStatus = "All Far";
        else if (!anyFar && anyClose) groupStatus = "All Close";
        else if (anyFar && anyClose) groupStatus = "Mixed";
        else groupStatus = "Alone";
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, collisionRadius);
    }
}