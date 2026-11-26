using System.IO;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Generates a checkered Texture2D and saves it as a PNG in the Assets folder.
/// Tick "Create" in the inspector to generate; the box un-ticks itself automatically.
/// </summary>
[ExecuteAlways]
public class CheckerTextureGenerator : MonoBehaviour
{
    [Header("Texture Settings")]
    [Tooltip("Texture width and height in pixels.")]
    public Vector2Int resolution = new Vector2Int(512, 512);

    [Tooltip("Width/height of each square in pixels.")]
    [Min(1)] public int squareWidth = 32;

    public Color colorA = Color.white;
    public Color colorB = Color.black;

    [Header("Output")]
    [Tooltip("File name without extension. Saved to Assets/<name>.png")]
    public string assetName = "Checker";

    [Header("Actions")]
    [Tooltip("Tick to generate the texture. Resets to false automatically.")]
    public bool create = false;

    private void Update()
    {
        if (!create) return;

        // Flip back first so a failure/exception can't cause repeated generation.
        create = false;

#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
        Generate();
#else
        Debug.LogWarning("CheckerTextureGenerator can only save assets in the Unity Editor.");
#endif
    }

#if UNITY_EDITOR
    private void Generate()
    {
        int width = Mathf.Max(1, resolution.x);
        int height = Mathf.Max(1, resolution.y);
        int size = Mathf.Max(1, squareWidth);

        // Build the pixel data.
        Color32 a = colorA;
        Color32 b = colorB;
        Color32[] pixels = new Color32[width * height];

        for (int y = 0; y < height; y++)
        {
            int rowSquare = y / size;
            for (int x = 0; x < width; x++)
            {
                bool useA = ((x / size) + rowSquare) % 2 == 0;
                pixels[y * width + x] = useA ? a : b;
            }
        }

        Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        tex.SetPixels32(pixels);
        tex.Apply();

        byte[] png = tex.EncodeToPNG();
        DestroyImmediate(tex);

        // Sanitise the file name.
        string safeName = string.IsNullOrWhiteSpace(assetName) ? "Checker" : assetName.Trim();
        foreach (char c in Path.GetInvalidFileNameChars())
            safeName = safeName.Replace(c.ToString(), "_");

        string assetPath = "Assets/" + safeName + ".png";
        string fullPath = Path.Combine(Application.dataPath, safeName + ".png");

        File.WriteAllBytes(fullPath, png);
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

        // Keep the checker crisp: point filtering, no mipmaps.
        TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer != null)
        {
            importer.filterMode = FilterMode.Point;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
        }

        Debug.Log($"Checker texture saved to {assetPath} ({width}x{height}, square size {size}px)");
    }
#endif
}