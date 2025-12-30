using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class TrackViewCapture : MonoBehaviour
{
    public Transform body;
    public Transform Trajectory;
    public string imageName;
    public Camera mainCamera;
    public RenderTexture cubeMapRenderTextureLeft;
    public RenderTexture cubeMapRenderTextureRight;
    public RenderTexture equirectRenderTexture;

    int index = 0;

    void Start()
    {
        mainCamera = this.GetComponent<Camera>();
    }
    public void OnCapture()
    {
        body.transform.position = Trajectory.GetChild(index).transform.position;
        print("Capture");
        Capture();
        index += 1;
    }

    public void Capture()
    {
        mainCamera.stereoSeparation = 0.065f;

        // 왼쪽 눈 캡처
        mainCamera.RenderToCubemap(cubeMapRenderTextureLeft, 63, Camera.MonoOrStereoscopicEye.Left);
        cubeMapRenderTextureLeft.ConvertToEquirect(equirectRenderTexture, Camera.MonoOrStereoscopicEye.Left);

        // 텍스처 저장
        Texture2D tex = new Texture2D(equirectRenderTexture.width, equirectRenderTexture.height, TextureFormat.RGB24, false);
        RenderTexture.active = equirectRenderTexture;
        tex.ReadPixels(new Rect(0, 0, equirectRenderTexture.width, equirectRenderTexture.height), 0, 0);
        RenderTexture.active = null;

        // 검정색 영역 제거: 이미지를 자르기 위해 사용
        int cropHeight = equirectRenderTexture.height / 2; // 반으로 자름
        Texture2D croppedTex = new Texture2D(equirectRenderTexture.width, cropHeight, TextureFormat.RGB24, false);
        croppedTex.SetPixels(tex.GetPixels(0, cropHeight, equirectRenderTexture.width, cropHeight));
        croppedTex.Apply();

        // 최종 이미지를 저장
        byte[] bytes = croppedTex.EncodeToJPG();
        string path = Application.dataPath + $"/360Image/{imageName}_{index}.jpg";
        System.IO.File.WriteAllBytes(path, bytes);
    }
}
