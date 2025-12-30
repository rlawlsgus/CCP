using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Globalization;

public enum ParserType
{
    Output,
    Annotation,
    Obsmat
}

public static class DatasetParser
{
    public static Dictionary<int, List<AgentData>> Parse(ParserType type, string fileContent, Matrix4x4 homographyMatrix, bool useHomography)
    {
        return type switch
        {
            ParserType.Output => LoadOutputData(fileContent, homographyMatrix, useHomography),
            ParserType.Annotation => LoadAnnotationData(fileContent, homographyMatrix, useHomography),
            ParserType.Obsmat => LoadObsmatData(fileContent, homographyMatrix, useHomography),
            _ => new Dictionary<int, List<AgentData>>(),
        };

    }

    private static Dictionary<int, List<AgentData>> LoadOutputData(string fileContent, Matrix4x4 homographyMatrix, bool useHomography)
    {
        var agentData = new Dictionary<int, List<AgentData>>();
        StringReader reader = new StringReader(fileContent);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                string[] parts = line.Split(',');
                if (parts.Length < 5) continue;

                if (!int.TryParse(parts[0].Trim(), out int agentId) ||
                    !float.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out float xPos) ||
                    !float.TryParse(parts[2].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out float yPos) ||
                    !int.TryParse(parts[3].Trim(), out int frame) ||
                    !float.TryParse(parts[4].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out float headOrientation))
                {
                    Debug.LogWarning($"Could not parse line: '{line}'. Skipping.");
                    continue;
                }

                headOrientation += 90.0f;

                Vector3 position;
                if (useHomography)
                {
                    Vector3 pixelHomogeneous = new Vector3(-xPos, yPos, 1f);
                    float w = homographyMatrix.m20 * pixelHomogeneous.x + homographyMatrix.m21 * pixelHomogeneous.y + homographyMatrix.m22 * pixelHomogeneous.z;
                    if (Mathf.Abs(w) < 1e-6f)
                    {
                        position = Vector3.zero;
                    }
                    else
                    {
                        float world_x = (homographyMatrix.m00 * pixelHomogeneous.x + homographyMatrix.m01 * pixelHomogeneous.y + homographyMatrix.m02 * pixelHomogeneous.z) / w;
                        float world_z = (homographyMatrix.m10 * pixelHomogeneous.x + homographyMatrix.m11 * pixelHomogeneous.y + homographyMatrix.m12 * pixelHomogeneous.z) / w;
                        position = new Vector3(world_x, 0, world_z);
                    }
                }
                else
                {
                    position = new Vector3(-xPos, 0, yPos);
                }

                AgentData data = new AgentData(position, frame, headOrientation);
                if (!agentData.ContainsKey(agentId))
                {
                    agentData[agentId] = new List<AgentData>();
                }
                agentData[agentId].Add(data);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"Could not parse line: '{line}'. Error: {e.Message}");
            }
        }
        return agentData;
    }

    private static Dictionary<int, List<AgentData>> LoadAnnotationData(string fileContent, Matrix4x4 homographyMatrix, bool useHomography)
    {
        var agentData = new Dictionary<int, List<AgentData>>();
        StringReader reader = new StringReader(fileContent);
        string firstLine = GetCleanLine(reader);
        if (firstLine == null || !int.TryParse(firstLine, out int numberOfSplines))
        {
            Debug.LogError($"Failed to parse the number of splines from the first line: '{firstLine}'");
            return agentData;
        }

        for (int i = 0; i < numberOfSplines; i++)
        {
            int agentId = i + 1; // 에이전트 ID를 1부터 시작하도록 설정
            agentData[agentId] = new List<AgentData>();

            string numControlPointsLine = GetCleanLine(reader);
            if (numControlPointsLine == null || !int.TryParse(numControlPointsLine, out int numControlPoints))
            {
                Debug.LogError($"Failed to parse the number of control points for spline {i} from line: '{numControlPointsLine}'");
                return agentData;
            }

            for (int j = 0; j < numControlPoints; j++)
            {
                string dataLine = GetCleanLine(reader);
                if (dataLine == null) { continue; }
                try
                {
                    string[] parts = dataLine.Split(new char[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 4) { continue; }

                    if (!float.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out float xPos) ||
                        !float.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out float yPos) ||
                        !int.TryParse(parts[2], out int frame) ||
                        !float.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out float headOrientation))
                    {
                        Debug.LogWarning($"Could not parse line: '{dataLine}'. Skipping.");
                        continue;
                    }

                    Vector3 position;
                    if (useHomography)
                    {
                        Vector3 pixelHomogeneous = new Vector3(xPos, yPos, 1f);
                        float w = homographyMatrix.m20 * pixelHomogeneous.x + homographyMatrix.m21 * pixelHomogeneous.y + homographyMatrix.m22 * pixelHomogeneous.z;
                        if (Mathf.Abs(w) < 1e-6f)
                        {
                            position = Vector3.zero;
                        }
                        else
                        {
                            float world_x = (homographyMatrix.m00 * pixelHomogeneous.x + homographyMatrix.m01 * pixelHomogeneous.y + homographyMatrix.m02 * pixelHomogeneous.z) / w;
                            float world_z = (homographyMatrix.m10 * pixelHomogeneous.x + homographyMatrix.m11 * pixelHomogeneous.y + homographyMatrix.m12 * pixelHomogeneous.z) / w;
                            position = new Vector3(world_x, 0, world_z);
                        }
                    }
                    else
                    {
                        position = new Vector3(xPos, 0, yPos);
                    }

                    AgentData data = new AgentData(position, frame, headOrientation);
                    agentData[agentId].Add(data);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"Could not parse line: '{dataLine}'. Error: {e.Message}");
                }
            }
        }
        return agentData;
    }

    public static Dictionary<int, List<AgentData>> LoadObsmatData(string fileContent, Matrix4x4 homographyMatrix, bool useHomography)
    {
        var agentData = new Dictionary<int, List<AgentData>>();
        StringReader reader = new StringReader(fileContent);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                string[] parts = line.Split(new char[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 8) continue;

                if (!float.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out float frameFloat) ||
                    !float.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out float agentIdFloat) ||
                    !float.TryParse(parts[2].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out float xPos) ||
                    !float.TryParse(parts[4].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out float yPos))
                {
                    Debug.LogWarning($"Could not parse line: '{line}'. Skipping.");
                    continue;
                }

                int frame = (int)frameFloat;
                int agentId = (int)agentIdFloat;

                Vector3 position;
                if (useHomography)
                {
                    Vector3 pixelHomogeneous = new Vector3(xPos, yPos, 1f);
                    float w = homographyMatrix.m20 * pixelHomogeneous.x + homographyMatrix.m21 * pixelHomogeneous.y + homographyMatrix.m22 * pixelHomogeneous.z;
                    if (Mathf.Abs(w) < 1e-6f)
                    {
                        position = Vector3.zero;
                    }
                    else
                    {
                        float world_x = (homographyMatrix.m00 * pixelHomogeneous.x + homographyMatrix.m01 * pixelHomogeneous.y + homographyMatrix.m02 * pixelHomogeneous.z) / w;
                        float world_z = (homographyMatrix.m10 * pixelHomogeneous.x + homographyMatrix.m11 * pixelHomogeneous.y + homographyMatrix.m12 * pixelHomogeneous.z) / w;
                        position = new Vector3(world_x, 0, world_z);
                    }
                }
                else
                {
                    position = new Vector3(xPos, 0, yPos);
                }

                // head orientation이 obsmat 파일에 주어지지 않았음 -> 지금은 0으로 설정
                AgentData data = new AgentData(position, frame, 0);
                if (!agentData.ContainsKey(agentId))
                {
                    agentData[agentId] = new List<AgentData>();
                }
                agentData[agentId].Add(data);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"Could not parse line: '{line}'. Error: {e.Message}");
            }
        }
        return agentData;
    }


    private static string GetCleanLine(StringReader reader)
    {
        string line = reader.ReadLine();
        if (line == null) return null;
        int commentIndex = line.IndexOf(" - ");
        if (commentIndex != -1)
        {
            return line[..commentIndex].Trim();
        }
        return line.Trim();
    }
}
