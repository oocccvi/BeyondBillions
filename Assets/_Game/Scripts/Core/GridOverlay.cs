using UnityEngine;

public class GridOverlay : MonoBehaviour
{
    public static GridOverlay Instance { get; private set; }

    [Header("Grid Settings")]
    public bool showGrid = false;
    public Color gridColor = new Color(1f, 1f, 1f, 0.2f);

    // [核心修复] 这里的 Y 轴偏移量非常关键
    // 0.01f - 0.05f 通常是最佳范围
    // 太小 = 闪烁 (Z-fighting)
    // 太大 = 浮空感 (Floating)
    [Range(0.001f, 0.5f)]
    public float yOffset = 0.02f;

    public Material gridMaterial; // 确保在 Inspector 中赋值一个支持透明的材质

    private Mesh _gridMesh;
    private GraphicsBuffer _commandBuffer;

    // 用于高亮显示的变量
    private bool _hasHighlight = false;
    private int _highlightX, _highlightZ, _highlightSize;
    private Color _highlightColor;

    void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        Instance = this;
    }

    // 每一帧在 LateUpdate 或 OnPostRender 中绘制，或者使用 DrawMeshNow
    // 这里使用简单的 OnRenderObject (只在有相机渲染时调用)
    void OnRenderObject()
    {
        if (!showGrid && !_hasHighlight) return;
        if (MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized) return;

        CreateGridMaterial();

        gridMaterial.SetPass(0);

        GL.PushMatrix();
        // [核心修复] 应用 Y 轴偏移，确保网格贴合地面但又略高于地面
        GL.MultMatrix(Matrix4x4.TRS(new Vector3(0, yOffset, 0), Quaternion.identity, Vector3.one));

        if (showGrid)
        {
            DrawGridLines();
        }

        if (_hasHighlight)
        {
            DrawHighlight();
        }

        GL.PopMatrix();
    }

    void CreateGridMaterial()
    {
        if (gridMaterial == null)
        {
            // 如果没有指定材质，创建一个简单的 Unlit 透明材质
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            gridMaterial = new Material(shader);
            gridMaterial.hideFlags = HideFlags.HideAndDontSave;
            // 设置混合模式
            gridMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            gridMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            gridMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            gridMaterial.SetInt("_ZWrite", 0); // 关闭 Z 写入，防止遮挡
        }
    }

    void DrawGridLines()
    {
        int w = MapGenerator.Instance.width;
        int h = MapGenerator.Instance.height;

        GL.Begin(GL.LINES);
        GL.Color(gridColor);

        // 绘制竖线
        for (int x = 0; x <= w; x++)
        {
            GL.Vertex3(x, 0, 0);
            GL.Vertex3(x, 0, h);
        }

        // 绘制横线
        for (int z = 0; z <= h; z++)
        {
            GL.Vertex3(0, 0, z);
            GL.Vertex3(w, 0, z);
        }

        GL.End();
    }

    void DrawHighlight()
    {
        GL.Begin(GL.QUADS);
        GL.Color(_highlightColor);

        float xMin = _highlightX - _highlightSize / 2f;
        float xMax = _highlightX + (_highlightSize % 2 == 0 ? _highlightSize / 2f : _highlightSize / 2f + 1f);
        // 对于偶数尺寸（如2x2），中心点在网格线上，范围是 x-1 到 x+1
        // 对于奇数尺寸（如1x1），中心点在格子中心，范围是 x-0.5 到 x+0.5
        // 这里假设输入的是整数中心点，对于偶数尺寸需要特殊处理

        float startX, startZ, endX, endZ;

        if (_highlightSize % 2 != 0)
        {
            // 奇数尺寸 (1x1, 3x3)
            startX = _highlightX;
            startZ = _highlightZ;
            endX = _highlightX + 1; // 假设传入的是格子左下角索引，或者如果是中心点需要 -0.5/+0.5
                                    // 为了简单，我们假设 highlightX/Z 是格子坐标 (0,0) 代表第一个格子的左下角
                                    // 如果 logic 是中心点，请根据 BuildingManager 逻辑调整
                                    // 这里根据 BuildingManager 的 HandleMapClick, 传入的是 Mathf.RoundToInt(hit.point)
                                    // RoundToInt 会把 0.5 变成 0 或 1，通常是对齐到 Grid 交叉点

            // 重新校准：以交叉点为中心
            startX = _highlightX - 0.5f;
            endX = _highlightX + 0.5f;
            startZ = _highlightZ - 0.5f;
            endZ = _highlightZ + 0.5f;
        }
        else
        {
            // 偶数尺寸 (2x2, 4x4) - 以交叉点为中心
            int offset = _highlightSize / 2;
            startX = _highlightX - offset;
            endX = _highlightX + offset;
            startZ = _highlightZ - offset;
            endZ = _highlightZ + offset;
        }

        // 绘制一个填充的 Quad
        GL.Vertex3(startX, 0, startZ);
        GL.Vertex3(startX, 0, endZ);
        GL.Vertex3(endX, 0, endZ);
        GL.Vertex3(endX, 0, startZ);

        GL.End();

        // 可选：再画一圈边框
        GL.Begin(GL.LINES);
        GL.Color(new Color(_highlightColor.r, _highlightColor.g, _highlightColor.b, 1f)); // 不透明边框

        GL.Vertex3(startX, 0, startZ); GL.Vertex3(startX, 0, endZ);
        GL.Vertex3(startX, 0, endZ); GL.Vertex3(endX, 0, endZ);
        GL.Vertex3(endX, 0, endZ); GL.Vertex3(endX, 0, startZ);
        GL.Vertex3(endX, 0, startZ); GL.Vertex3(startX, 0, startZ);

        GL.End();
    }

    public void ShowGrid(bool show)
    {
        showGrid = show;
    }

    public void SetHighlight(int x, int z, int size, Color color)
    {
        _hasHighlight = true;
        _highlightX = x;
        _highlightZ = z;
        _highlightSize = size;
        _highlightColor = color;
    }

    public void ClearHighlight()
    {
        _hasHighlight = false;
    }
}