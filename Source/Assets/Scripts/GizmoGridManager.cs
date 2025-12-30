using System.Collections.Generic;
using UnityEngine;

public class GizmoGridManager : MonoBehaviour
{
    // Rect.y 를 "Z 최소값"으로 사용 (xMin, zMin, width(X), depth(Z))
    public Rect worldBounds = new Rect(-5, -5, 10, 10);
    public int n = 32;
    public float thickness = 0.02f;   // Y축 두께
    public float planeY = 0f;         // XZ 평면이 놓일 높이(Y)
    public Color defaultColor = new Color(0, 0, 0, 0);
    private Color[] cellColor;

    void OnValidate()
    {
        n = Mathf.Max(1, n);
        if (cellColor == null || cellColor.Length != n * n) cellColor = new Color[n * n];
    }

    // Vector3 받아서 XZ 기준으로 가장 가까운 셀 칠함
    public void MarkNearestCell(Vector3 worldPos, Color c)
    {
        var (i, j) = WorldToIJ(worldPos);
        int idx = IJToIndex(i, j);
        cellColor[idx] = c; // 필요하면 여기서 블렌드
    }

    // 기존 Vector2 버전도 쓰고 싶다면: new Vector3(p.x, 0, p.y) 로 래핑
    public void MarkNearestCell(Vector2 xz, Color c) => MarkNearestCell(new Vector3(xz.x, 0, xz.y), c);

    (int i, int j) WorldToIJ(Vector3 p)
    {
        float cx = worldBounds.width / n; // 셀 너비 (X)
        float cz = worldBounds.height / n; // 셀 깊이 (Z)  ← Rect.height를 Z로 씀
        int i = Mathf.RoundToInt((p.x - worldBounds.xMin) / cx - 0.5f);
        int j = Mathf.RoundToInt((p.z - worldBounds.yMin) / cz - 0.5f); // yMin == zMin 로 사용
        i = Mathf.Clamp(i, 0, n - 1);
        j = Mathf.Clamp(j, 0, n - 1);
        return (i, j);
    }

    int IJToIndex(int i, int j) => j * n + i;

    Vector3 IJToCenter(int i, int j)
    {
        float cx = worldBounds.width / n;
        float cz = worldBounds.height / n;
        float x = worldBounds.xMin + (i + 0.5f) * cx;
        float z = worldBounds.yMin + (j + 0.5f) * cz; // yMin == zMin
        return new Vector3(x, planeY, z);
    }

    void OnDrawGizmos()
    {
        if (cellColor == null || cellColor.Length != n * n) OnValidate();
        float cx = worldBounds.width / n;
        float cz = worldBounds.height / n;
        Vector3 size = new Vector3(cx, Mathf.Max(0.001f, thickness), cz); // XZ 크기, Y는 두께

        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int idx = IJToIndex(i, j);
                var center = IJToCenter(i, j);

                var col = cellColor[idx];
                if (col.a > 0f) { Gizmos.color = col; Gizmos.DrawCube(center, size); }

                Gizmos.color = new Color(1, 1, 1, 0.15f);
                Gizmos.DrawWireCube(center, size);
            }
        }
    }
}
