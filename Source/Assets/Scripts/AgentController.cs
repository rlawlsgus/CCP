using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Animator))]
public class AgentController : MonoBehaviour
{
    public GizmoGridManager grid;
    public Color agentColor = Color.red;
    public float minDistanceToMark = 0.05f; // 너무 자주 칠하지 않게


    public LayerMask floorLayer;


    public TrackViewCapture camCapture;
    public LookTarget recorder;
    public bool isStart = false;
    public bool isHeadTurn = false;
    public float rotationSpeed = 5f; // 몸통 회전 속도

    private Transform targetTransform; // target 오브젝트의 Transform 저장
    public Transform headAimTransform; // HeadAim 오브젝트의 Transform 저장
    private Animator animator; // 에이전트의 애니메이터
    public float distanceFromHead = 1.0f; // HeadAim으로부터 target까지의 거리
    public List<Vector3> positions = new List<Vector3>();
    public int agentId = -1;
    public Vector2 currentAction = Vector2.zero; // 현재 Action(속도 벡터) (AgentState가 사용)
    void Start()
    {
        // "Rig1/HeadAim/target" 경로로 target 오브젝트 찾기
        Transform rig1 = transform.Find("Rig 1");
        if (rig1 != null)
        {
            headAimTransform = rig1.Find("HeadAim");
            if (headAimTransform != null)
            {
                targetTransform = headAimTransform.Find("Target");
            }
        }

        // grid = FindObjectOfType<GizmoGridManager>();

        // target 오브젝트가 제대로 찾지 못한 경우 경고 출력
        if (targetTransform == null)
        {
            Debug.LogError("target transform not found in Rig1/HeadAim.");
        }

        // Animator 컴포넌트 가져오기
        animator = GetComponent<Animator>();
        if (animator == null)
        {
            Debug.LogError("Animator component not found in AgentPrefab.");
        }
    }

    // Vector2 _lastMarkedXZ;
    // void Update()
    // {

    //     if (agentId == 0 || agentId == 7)
    //     {
    //         // XZ 평면 좌표
    //         Vector3 pos = transform.position;
    //         Vector2 xz = new Vector2(pos.x, pos.z);

    //         if (Vector2.Distance(xz, _lastMarkedXZ) >= minDistanceToMark)
    //         {
    //             grid.MarkNearestCell(pos, agentColor); // ★ Vector3 버전 호출 (XZ 기준 내부 처리됨)
    //             _lastMarkedXZ = xz;
    //         }
    //     }

    // }

    // 첫 위치를 설정하고, 두 번째 위치를 향하도록 회전 설정
    public void SetInitialPosition(Vector3 currentPosition, Vector3 nextPosition)
    {
        // 첫 위치로 이동
        transform.position = currentPosition;

        // 두 번째 위치를 바라보도록 회전 설정
        Vector3 direction = nextPosition - currentPosition;
        if (direction != Vector3.zero)
        {
            Quaternion lookRotation = Quaternion.LookRotation(direction);
            transform.rotation = lookRotation; // 다음 위치를 향하도록 회전 설정
        }
    }

    // AgentManager로부터 보간된 위치/회전 값을 받아 직접 설정하는 메서드
    public void SetInterpolatedTransform(Vector3 position, Vector3 velocity, float headOrientation)
    {
        // 위치를 직접 설정
        transform.position = position;

        RaycastHit hit;
        if (Physics.Raycast(position + Vector3.up * 2f, Vector3.down, out hit, 50f, floorLayer))
        {
            Vector3 correctedPos = position;
            correctedPos.y = hit.point.y;
            transform.position = correctedPos;
        }

        // 속도 벡터로부터 방향과 크기(속력)를 계산
        Vector3 direction = velocity.normalized;
        float speed = velocity.magnitude;

        // 이동 방향에 따라 몸통을 부드럽게 회전 (Slerp 사용)
        if (direction != Vector3.zero)
        {
            Quaternion lookRotation = Quaternion.LookRotation(direction);
            transform.rotation = Quaternion.Slerp(transform.rotation, lookRotation, Time.deltaTime * rotationSpeed);
        }

        // 애니메이터의 Blend 값을 속도에 비례하여 설정
        if (animator != null)
        {
            // speed가 10보다 크면 1, 아니면 0으로 설정 
            float blendValue = speed;
            animator.SetFloat("Blend", blendValue);
        }

        // 현재 Action(속도 벡터) 저장
        currentAction = new Vector2(velocity.x, velocity.z);

        // 머리 방향 설정
        if (isHeadTurn && targetTransform != null && headAimTransform != null)
        {
            float rad = Mathf.Deg2Rad * headOrientation;
            Vector3 newTargetPosition = headAimTransform.position + new Vector3(
                distanceFromHead * Mathf.Cos(rad),
                1.61f,
                distanceFromHead * Mathf.Sin(rad)
            );
            targetTransform.position = newTargetPosition;
        }
    }

}
