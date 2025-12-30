using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Assertions.Must;
using System.IO;
using Unity.VisualScripting;

public class LookTarget : MonoBehaviour
{
    public AgentController controller;
    private bool isRecording = false;
    //float recordInterval = 0.1f;
    public Transform target;
    public Transform body;
    // Start is called before the first frame update
    private string csvOutName = "zara01";
    void Start()
    {
        //if (!isRecording)
        //{
        //    isRecording = true;
        //    InvokeRepeating(nameof(RecordRotation), 0f, recordInterval);
        //}
    }

    // Update is called once per frame
    void Update()
    {
        this.transform.LookAt(target.transform.position);

    }

    private List<Quaternion> rotations = new List<Quaternion>();
    private List<Vector2> diffs = new List<Vector2>();
    void RecordRotation()
    {
        if (!isRecording) return;
        // 현재 로컬 회전값 저장
        //rotations.Add(currentHeadRotation.rotation);
        Quaternion headRot = this.transform.rotation;
        Quaternion bodyRot = body.rotation;

        rotations.Add(headRot);

        Vector2 diff = GetSignedDelta(headRot, bodyRot);
        diffs.Add(diff);
    }

    Vector2 GetSignedDelta(Quaternion head, Quaternion body)
    {
        Quaternion rel = Quaternion.Inverse(body) * head;

        Vector3 e = rel.eulerAngles;
        float pitch = e.x > 180f ? e.x - 360f : e.x;
        float yaw = e.y > 180f ? e.y - 360f : e.y;

        return new Vector2(pitch, yaw);
    }

    public void StopRecordingAndSave()
    {
        if (!isRecording) return;
        isRecording = false;
        CancelInvoke(nameof(RecordRotation));
        SaveRotationsToCSV();
    }

    private void SaveRotationsToCSV()
    {
        string path = Application.dataPath + $"/Zara01/{csvOutName}_{controller.agentId}.csv";

        using (var writer = new StreamWriter(path))
        {
            // 헤더 추가
            writer.WriteLine("x,y,z,w,diffPitch,diffYaw");

            for (int i = 0; i < rotations.Count; i++)
            {
                Quaternion q = rotations[i];
                Vector2 d = diffs[i];

                //q.x:F4
                writer.WriteLine($"{q.x:F4},{q.y:F4},{q.z:F4},{q.w:F4},{d.x:F2},{d.y:F2}");
            }
        }

        Debug.Log($"[RotationRecorder] Saved {rotations.Count} rotations + diffs to:\n{path}");
    }
}
