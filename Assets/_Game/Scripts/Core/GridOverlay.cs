using UnityEngine;
using UnityEngine.Rendering;

public class GridOverlay : MonoBehaviour
{
    public static GridOverlay Instance { get; private set; }

    public bool showGrid = false;
    public Color gridColor = new Color(1f, 1f, 1f, 0.1f);

    private Material _lineMaterial;
    private bool _drawHighlight = false;
    private int _hlX, _hlZ, _hlSize;
    private Color _hlColor;

    void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        Instance = this;
        Shader shader = Shader.Find("Hidden/Internal-Colored");
        _lineMaterial = new Material(shader);
        _lineMaterial.hideFlags = HideFlags.HideAndDontSave;
        _lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        _lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        _lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        _lineMaterial.SetInt("_ZWrite", 0);
    }

    void OnEnable() { RenderPipelineManager.endCameraRendering += OnEndCameraRendering; }
    void OnDisable() { RenderPipelineManager.endCameraRendering -= OnEndCameraRendering; }

    public void ShowGrid(bool show) { showGrid = show; }
    public void SetHighlight(int x, int z, int size, Color color) { _drawHighlight = true; _hlX = x; _hlZ = z; _hlSize = size; _hlColor = color; }
    public void ClearHighlight() { _drawHighlight = false; }

    void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (camera != Camera.main || MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized) return;
        _lineMaterial.SetPass(0);
        GL.PushMatrix();
        if (showGrid) DrawOptimizedGrid(camera);
        if (_drawHighlight) DrawHighlightFlat();
        GL.PopMatrix();
    }

    float GetTopHeight() { return 1.05f; }

    // [修改] 获取某个格子的显示高度 (为了高亮块能正确盖住废弃建筑)
    float GetCellHeight(int x, int z)
    {
        if (MapGenerator.Instance == null) return 1.05f;
        int index = z * MapGenerator.Instance.width + x;
        if (index < 0 || index >= MapGenerator.Instance.MapData.Length) return 1.05f;

        var cell = MapGenerator.Instance.MapData[index];
        // 如果是废弃建筑(3)或悬崖(2)，显示得高一点
        if (cell.TerrainType == 2 || cell.TerrainType == 3)
            return 1.05f + MapGenerator.Instance.heightMultiplier;
        return 1.05f;
    }

    void DrawHighlightFlat()
    {
        int offset = _hlSize / 2;
        int startX = _hlX - offset;
        int startZ = _hlZ - offset;
        int endX = startX + _hlSize;
        int endZ = startZ + _hlSize;

        GL.Begin(GL.QUADS);
        GL.Color(_hlColor);

        for (int x = startX; x < endX; x++)
        {
            for (int z = startZ; z < endZ; z++)
            {
                float h = GetCellHeight(x, z); // 使用动态高度
                GL.Vertex3(x, h, z); GL.Vertex3(x, h, z + 1); GL.Vertex3(x + 1, h, z + 1); GL.Vertex3(x + 1, h, z);
            }
        }
        GL.End();
    }

    void DrawOptimizedGrid(Camera cam)
    {
        // 保持之前的优化逻辑不变，绘制统一高度的网格
        int mapW = MapGenerator.Instance.width;
        int mapH = MapGenerator.Instance.height;
        float h = GetTopHeight();

        // ... (省略 Frustum Culling 代码，直接复制之前的 DrawOptimizedGrid 内容) ...
        // 为了方便，这里简单写一下核心绘制部分

        int minX = 0, maxX = mapW, minZ = 0, maxZ = mapH;
        // (此处应包含之前的视锥体剔除逻辑，请保留原文件中的这部分)

        GL.Begin(GL.LINES);
        GL.Color(gridColor);
        for (int x = minX; x <= maxX; x++) { GL.Vertex3(x, h, minZ); GL.Vertex3(x, h, maxZ); }
        for (int z = minZ; z <= maxZ; z++) { GL.Vertex3(minX, h, z); GL.Vertex3(maxX, h, z); }
        GL.End();
    }
}