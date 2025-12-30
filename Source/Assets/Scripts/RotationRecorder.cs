using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class RotationRecorder : MonoBehaviour
{
    //public bool isMultiSetting = true;
    //public bool isAblation = true;
    //public Transform currentHeadRotation;
    //public Transform bodyTransform; // bodyTransform 직접 지정 가능

    //[Tooltip("회전값을 저장할 간격(초)")]
    //public float recordInterval = 1f;

    //private List<Quaternion> rotations = new List<Quaternion>();
    //private bool isRecording = false;
    //private List<Vector2> diffs = new List<Vector2>();

    //private string csvOutName = "zara02";

    //void Start()
    //{
    //    if (currentMovement.currentWaypointIndex == 1 && !isRecording)
    //    {
    //        isRecording = true;
    //        InvokeRepeating(nameof(RecordRotation), 0f, recordInterval);
    //    }

    //    if (currentMovement.isReached)
    //    {
    //        StopRecordingAndSave();
    //    }
    //}

    //void RecordRotation()
    //{
    //    if (!isRecording) return;
    //    // 현재 로컬 회전값 저장
    //    //rotations.Add(currentHeadRotation.rotation);
    //    Quaternion headRot = currentHeadRotation.rotation;
    //    Quaternion bodyRot = bodyTransform.rotation;

    //    rotations.Add(headRot);

    //    Vector2 diff = GetSignedDelta(headRot, bodyRot);
    //    diffs.Add(diff);

    //    if (!isMultiSetting)
    //    {
    //        diffXText.text = "diffX: " + diff.x.ToString();
    //        diffYText.text = "diffY: " + diff.y.ToString();
    //    }

    //}

    ///// <summary>
    ///// 호출 시 녹화를 멈추고, 저장까지 처리합니다.
    ///// </summary>
    //public void StopRecordingAndSave()
    //{
    //    if (!isRecording) return;
    //    isRecording = false;
    //    CancelInvoke(nameof(RecordRotation));
    //    SaveRotationsToCSV();
    //}

    //private void SaveRotationsToCSV()
    //{
    //    string path = Application.dataPath + $"/{csvOutName}.csv"; //.Combine(Application.persistentDataPath, saveFileName);

    //    if (isMultiSetting)
    //    {
    //        path = Application.dataPath + $"/Zara02/{csvOutName}_{currentMovement_multi.agentID}.csv";
    //    }
    //    using (var writer = new StreamWriter(path))
    //    {
    //        // 헤더 추가
    //        writer.WriteLine("x,y,z,w,diffPitch,diffYaw");

    //        for (int i = 0; i < rotations.Count; i++)
    //        {
    //            Quaternion q = rotations[i];
    //            Vector2 d = diffs[i];

    //            //q.x:F4
    //            writer.WriteLine($"{q.x:F4},{q.y:F4},{q.z:F4},{q.w:F4},{d.x:F2},{d.y:F2}");
    //        }
    //    }

    //    Debug.Log($"[RotationRecorder] Saved {rotations.Count} rotations + diffs to:\n{path}");
    //}


    //Vector2 GetSignedDelta(Quaternion head, Quaternion body)
    //{
    //    Quaternion rel = Quaternion.Inverse(body) * head;

    //    Vector3 e = rel.eulerAngles;
    //    float pitch = e.x > 180f ? e.x - 360f : e.x;
    //    float yaw = e.y > 180f ? e.y - 360f : e.y;

    //    return new Vector2(pitch, yaw);
    //}
}
