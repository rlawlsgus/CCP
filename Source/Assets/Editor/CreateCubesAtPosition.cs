using UnityEngine;
using UnityEditor;

public class CreateCubesAtPosition : Editor
{
    [MenuItem("Tools/Create Cubes at Selected")]
    public static void CreateCubes()
    {
        // 현재 하이어라키에서 선택한 모든 오브젝트를 가져옴
        GameObject[] selectedObjects = Selection.gameObjects;

        if (selectedObjects.Length == 0)
        {
            Debug.LogWarning("오브젝트를 먼저 선택해주세요!");
            return;
        }

        foreach (GameObject obj in selectedObjects)
        {
            // 큐브 생성
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            // 선택한 오브젝트의 위치와 회전값 적용
            cube.transform.position = obj.transform.position;
            cube.transform.rotation = obj.transform.rotation;
            
            // 실행 취소(Undo)가 가능하도록 등록
            Undo.RegisterCreatedObjectUndo(cube, "Create Cube");
        }
    }
}