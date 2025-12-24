using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Entities;
using Unity.Transforms;
using Unity.Rendering;
using System.Collections.Generic;

public class MapGenerator : MonoBehaviour
{
    public static MapGenerator Instance { get; private set; }

    public enum GenerationMode { Random, FromImage }

    [Header("--- 生成模式 ---")]
    public GenerationMode mode = GenerationMode.Random;
    public Texture2D mapLayoutTexture;

    [Header("--- 核心设置 ---")]
    public int width = 128;
    public int height = 128;
    public int seed = 0;

    [Header("--- 1. 大陆塑形 (破碎大陆核心) ---")]
    [Tooltip("地形扭曲力度 (建议 1.0 - 2.0)")]
    public float warpStrength = 1.5f;
    [Tooltip("噪声缩放 (越大板块越大，建议 80 - 150)")]
    public float noiseScale = 100f;
    [Tooltip("扭曲噪声的缩放")]
    public float warpScale = 45f;
    [Tooltip("细节层级")]
    public int octaves = 6;
    public float persistence = 0.5f;
    public float lacunarity = 2f;

    [Header("--- 2. 海洋与形状 ---")]
    public float seaLevel = 0.35f;
    public int oceanMargin = 10;
    [Tooltip("岛屿大小占比")]
    public float islandSizeFactor = 0.9f;
    public float falloffPower = 2.0f;
    [Tooltip("边缘破碎程度")]
    public float islandDistortion = 45f;
    public float islandFrequency = 80f;

    [Header("--- 3. 生态分布 (Biomes) ---")]
    public float moistureScale = 50f;
    public float temperatureScale = 60f;

    [Tooltip("深水阈值")] public float deepWaterLevel = 0.25f;
    [Tooltip("沙滩阈值")] public float waterLevel = 0.35f;
    [Tooltip("沙滩阈值")] public float sandLevel = 0.38f;
    [Tooltip("森林生成的湿度要求")] public float forestLevel = 0.55f;
    [Tooltip("沼泽生成的湿度要求")] public float swampLevel = 0.75f;
    [Tooltip("山脉起始高度")] public float mountainLevel = 0.75f;
    [Tooltip("雪顶起始高度")] public float snowLevel = 0.92f;

    [Range(0f, 1f)] public float forestDensity = 0.5f;
    [Range(0f, 1f)] public float swampTreeDensity = 0.3f;
    [Range(0f, 1f)] public float swampProbability = 0.4f;

    [Header("--- 4. 水文模拟 (Rivers) ---")]
    public int riverCount = 25;
    public int riverMinLength = 15;
    public int riverCarveRadius = 2;
    public float minRiverHeight = 0.4f;
    public float maxRiverHeight = 0.9f;

    // 兼容旧字段
    public float riverScale = 40f;
    public float riverWidth = 0.04f;
    public float lakeThreshold = 0.15f;

    [Header("--- 5. 城市与资源 ---")]
    public int cityCount = 3;
    public int cityMinSize = 12;
    public int cityMaxSize = 25;
    public float buildingDensity = 0.65f;

    public int oreCount = 15;
    public int oreSize = 3;
    [Range(0f, 1f)] public float oreProbability = 0.4f;

    // 兼容字段
    [HideInInspector] public float heightMultiplier = 5f;
    public float mountainThreshold = 0.6f;
    [HideInInspector] public float safeRadius = 25f;
    [HideInInspector] public float blendRadius = 15f;
    public int swampCount = 5;
    public int swampMinRadius = 8;
    public int swampMaxRadius = 15;

    [Header("--- 6. 平滑处理 ---")]
    public int smoothIterations = 2;

    [Header("--- 环境资产 (必须配置) ---")]
    public Mesh rockMesh;
    public Material rockMaterial;
    public Mesh treeMesh;
    public Material treeMaterial;
    public Mesh ruinsMesh;
    public Material ruinsMaterial;

    [Header("--- 地面颜色 (Alpha=1) ---")]
    public Color colorDeepWater = new Color(0.05f, 0.1f, 0.4f, 1f);
    public Color colorWater = new Color(0.15f, 0.35f, 0.8f, 1f);
    public Color colorSand = new Color(0.88f, 0.82f, 0.55f, 1f);
    public Color colorGrass = new Color(0.3f, 0.55f, 0.2f, 1f);
    public Color colorForest = new Color(0.15f, 0.35f, 0.1f, 1f);
    public Color colorSwamp = new Color(0.2f, 0.25f, 0.3f, 1f);
    public Color colorMountain = new Color(0.4f, 0.4f, 0.4f, 1f);
    public Color colorSnow = new Color(0.95f, 0.95f, 1.0f, 1f);
    public Color colorRoad = new Color(0.45f, 0.45f, 0.45f, 1f);
    public Color colorRuins = new Color(0.5f, 0.4f, 0.3f, 1f);
    public Color colorOre = new Color(0.8f, 0.7f, 0.2f, 1f);

    public Color colorGround => colorGrass;

    private NativeArray<CellData> _mapData;
    private float[] _heightMap;
    private float[] _moistureMap;
    private float[] _tempMap;

    public NativeArray<CellData> MapData => _mapData;
    public bool IsInitialized => _mapData.IsCreated;

    public NativeParallelHashMap<int, Entity> BuildingMap;
    public List<int2> CityCenters = new List<int2>();
    public Vector2Int PlayerSpawnPoint { get; private set; }

    private NativeList<Entity> _environmentEntities;
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private EntityManager _entityManager;

    public struct CellData
    {
        public int2 Position;
        public float Height;
        public byte TerrainType;
        public byte BuildingType;
        public byte SpawnWeight;
        public byte Flags;
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        _entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
        InitializeComponents();
        GenerateWorld();
    }

    void OnDestroy()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world != null && world.IsCreated) world.EntityManager.CompleteAllTrackedJobs();
        if (_mapData.IsCreated) _mapData.Dispose();
        if (BuildingMap.IsCreated) BuildingMap.Dispose();
        if (_environmentEntities.IsCreated) _environmentEntities.Dispose();
    }

    void InitializeComponents()
    {
        _meshFilter = GetComponent<MeshFilter>();
        if (_meshFilter == null) _meshFilter = gameObject.AddComponent<MeshFilter>();
        _meshRenderer = GetComponent<MeshRenderer>();
        if (_meshRenderer == null) _meshRenderer = gameObject.AddComponent<MeshRenderer>();

        SetOpaqueUnlitMaterial();
    }

    void SetOpaqueUnlitMaterial()
    {
        // 强制 Unlit Opaque 解决渲染伪影
        Shader shader = Shader.Find("Particles/Standard Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Sprites/Default");

        if (shader == null) return;

        Material mat = new Material(shader);
        mat.SetFloat("_Mode", 0); // Opaque
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
        mat.SetInt("_ZWrite", 1);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.renderQueue = 2000;

        if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);

        _meshRenderer.sharedMaterial = mat;
    }

    public byte GetMovementCost(byte t)
    {
        // 0-1:Water, 6:Mountain, 7:Snow, 9:Ruins -> 阻挡
        if (t <= 1 || t == 6 || t == 7 || t == 9) return 255;
        // 5:Swamp -> 严重减速
        if (t == 5) return 4;
        // 4:Forest -> 减速
        if (t == 4) return 2;
        // 8:Road -> 加速
        if (t == 8) return 1;
        return 1;
    }

    public bool IsPositionNearCity(float2 pos, float radius)
    {
        foreach (var c in CityCenters) if (math.distance(pos, new float2(c.x, c.y)) < radius) return true;
        return false;
    }

    [ContextMenu("Regenerate World")]
    public void GenerateWorld()
    {
        if (_mapData.IsCreated) _mapData.Dispose();
        if (BuildingMap.IsCreated) BuildingMap.Dispose();
        if (_environmentEntities.IsCreated)
        {
            _entityManager.DestroyEntity(_environmentEntities.AsArray());
            _environmentEntities.Dispose();
        }
        _environmentEntities = new NativeList<Entity>(Allocator.Persistent);
        CityCenters.Clear();

        if (mode == GenerationMode.FromImage && mapLayoutTexture != null)
        {
            width = mapLayoutTexture.width;
            height = mapLayoutTexture.height;
        }

        _mapData = new NativeArray<CellData>(width * height, Allocator.Persistent);
        BuildingMap = new NativeParallelHashMap<int, Entity>(width * height, Allocator.Persistent);

        _heightMap = new float[width * height];
        _moistureMap = new float[width * height];
        _tempMap = new float[width * height];

        if (mode == GenerationMode.Random) GenerateShatteredLand();
        else GenerateFromImage();

        GenerateMesh();
        SpawnEnvironmentEntities();

        Debug.Log($"<color=green>破碎大陆生成完毕: {width}x{height}</color>");
    }

    void GenerateShatteredLand()
    {
        if (seed == 0) seed = UnityEngine.Random.Range(0, 100000);
        System.Random prng = new System.Random(seed);

        float2 center = new float2(width / 2f, height / 2f);
        float maxRadius = Mathf.Min(width, height) / 2f;

        // 1. 生成高度图
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // A. 域扭曲 (Domain Warping)
                float warpX = GetFBM(x, y, seed, warpScale, 2, 0.5f, 2f);
                float warpY = GetFBM(x + 5.2f, y + 1.3f, seed, warpScale, 2, 0.5f, 2f);
                float qx = x + warpX * warpStrength * 20f;
                float qy = y + warpY * warpStrength * 20f;

                // B. FBM 叠加
                float noiseVal = GetFBM(qx, qy, seed + 100, noiseScale, octaves, persistence, lacunarity);

                // C. 不规则岛屿遮罩 (Distorted Radial Mask)
                float dist = math.distance(new float2(x, y), center);
                float normalizedDist = dist / (maxRadius * islandSizeFactor);
                // 边缘扰动
                float maskNoise = Mathf.PerlinNoise(x / islandFrequency, y / islandFrequency) * 0.3f;
                float maskDist = normalizedDist + (maskNoise - 0.15f) * (islandDistortion / 100f);

                float mask = 1f - Mathf.Pow(math.clamp(maskDist, 0, 1), falloffPower);

                float finalHeight = noiseVal * mask;
                finalHeight = Mathf.Clamp01(finalHeight);

                // 强制边缘海洋
                if (x < oceanMargin || x >= width - oceanMargin || y < oceanMargin || y >= height - oceanMargin)
                    finalHeight = 0;

                _heightMap[y * width + x] = finalHeight;

                // D. 湿度与温度
                _moistureMap[y * width + x] = GetNoise(x, y, seed + 500, moistureScale);
                float latitude = 1f - (float)Mathf.Abs(y - height / 2) / (height / 2);
                _tempMap[y * width + x] = latitude * 0.7f + GetNoise(x, y, seed + 600, temperatureScale) * 0.3f;
            }
        }

        // 2. 河流
        GenerateRivers(prng);

        // 3. 确定出生点
        FindSpawnPoint(prng);
        FlattenSpawnArea(PlayerSpawnPoint.x, PlayerSpawnPoint.y, 25);

        // 4. 生态映射
        for (int i = 0; i < width * height; i++)
        {
            int x = i % width;
            int y = i / width;

            CellData cell = new CellData();
            cell.Position = new int2(x, y);
            cell.Height = 0;

            float h = _heightMap[i];
            float m = _moistureMap[i];
            float t = _tempMap[i];

            if (h < deepWaterLevel) cell.TerrainType = 0;
            else if (h < seaLevel) cell.TerrainType = 1;
            else if (h < sandLevel) cell.TerrainType = 2;
            else if (h > snowLevel) cell.TerrainType = 7;
            else if (h > mountainLevel)
            {
                if (t < 0.3f) cell.TerrainType = 7;
                else cell.TerrainType = 6;
            }
            else
            {
                if (m > swampLevel) cell.TerrainType = 5;
                else if (m > forestLevel) cell.TerrainType = 4;
                else cell.TerrainType = 3;
            }
            _mapData[i] = cell;
        }

        // 5. 平滑与结构
        SmoothMap();
        GenerateResources(prng);
        if (cityCount > 0) GenerateCities(prng);
    }

    float GetNoise(float x, float y, int s, float scale)
    {
        return Mathf.PerlinNoise((x + s) / scale, (y + s) / scale);
    }

    float GetFBM(float x, float y, int s, float scale, int oct, float pers, float lac)
    {
        float val = 0;
        float amp = 1;
        float freq = 1;
        float maxVal = 0;

        for (int i = 0; i < oct; i++)
        {
            float nx = (x + s) / scale * freq;
            float ny = (y + s) / scale * freq;
            val += Mathf.PerlinNoise(nx, ny) * amp;
            maxVal += amp;
            amp *= pers;
            freq *= lac;
        }
        return val / maxVal;
    }

    void GenerateRivers(System.Random prng)
    {
        int rivers = 0;
        int maxTry = riverCount * 20;

        for (int i = 0; i < maxTry; i++)
        {
            if (rivers >= riverCount) break;
            int x = prng.Next(0, width);
            int y = prng.Next(0, height);
            int idx = y * width + x;

            if (_heightMap[idx] < minRiverHeight || _heightMap[idx] > maxRiverHeight) continue;

            List<int> path = new List<int>();
            int cx = x, cy = y;
            bool reachedWater = false;

            for (int k = 0; k < 200; k++)
            {
                path.Add(cy * width + cx);
                float minH = _heightMap[cy * width + cx];
                int nx = -1, ny = -1;

                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int tx = cx + dx, ty = cy + dy;
                        if (tx < 0 || tx >= width || ty < 0 || ty >= height) continue;
                        float th = _heightMap[ty * width + tx];
                        if (th < minH) { minH = th; nx = tx; ny = ty; }
                    }

                if (nx == -1) break;
                if (_heightMap[ny * width + nx] < seaLevel) { reachedWater = true; path.Add(ny * width + nx); break; }
                cx = nx; cy = ny;
            }

            if (reachedWater && path.Count > riverMinLength)
            {
                foreach (int pIdx in path)
                {
                    _heightMap[pIdx] = seaLevel - 0.05f;
                    int px = pIdx % width, py = pIdx / width;
                    for (int dy = -riverCarveRadius / 2; dy <= riverCarveRadius / 2; dy++)
                        for (int dx = -riverCarveRadius / 2; dx <= riverCarveRadius / 2; dx++)
                        {
                            int kx = px + dx, ky = py + dy;
                            if (kx >= 0 && kx < width && ky >= 0 && ky < height)
                                _heightMap[ky * width + kx] = seaLevel - 0.05f;
                        }
                }
                rivers++;
            }
        }
    }

    void FindSpawnPoint(System.Random prng)
    {
        for (int i = 0; i < 200; i++)
        {
            int x = prng.Next((int)(width * 0.2f), (int)(width * 0.8f));
            int y = prng.Next((int)(height * 0.2f), (int)(height * 0.8f));
            float h = _heightMap[y * width + x];
            if (h > seaLevel + 0.1f && h < mountainThreshold)
            {
                PlayerSpawnPoint = new Vector2Int(x, y);
                return;
            }
        }
        PlayerSpawnPoint = new Vector2Int(width / 2, height / 2);
    }

    void FlattenSpawnArea(int cx, int cy, int r)
    {
        for (int y = cy - r; y <= cy + r; y++) for (int x = cx - r; x <= cx + r; x++)
            {
                if (x >= 0 && x < width && y >= 0 && y < height && math.distance(new float2(x, y), new float2(cx, cy)) <= r)
                {
                    _heightMap[y * width + x] = seaLevel + 0.2f;
                }
            }
    }

    void SmoothMap()
    {
        for (int iter = 0; iter < smoothIterations; iter++)
        {
            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    int idx = y * width + x;
                    byte myType = _mapData[idx].TerrainType;
                    int waterNeighbors = 0;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            byte t = _mapData[(y + dy) * width + (x + dx)].TerrainType;
                            if (t <= 1) waterNeighbors++;
                        }
                    if (myType > 1 && waterNeighbors > 5) { var c = _mapData[idx]; c.TerrainType = 1; _mapData[idx] = c; }
                    else if (myType <= 1 && waterNeighbors < 3) { var c = _mapData[idx]; c.TerrainType = 3; _mapData[idx] = c; }
                }
            }
        }
    }

    void GenerateResources(System.Random prng)
    {
        for (int i = 0; i < oreCount; i++)
        {
            int x = prng.Next(oceanMargin, width - oceanMargin);
            int y = prng.Next(oceanMargin, height - oceanMargin);
            int idx = y * width + x;
            if (_mapData[idx].TerrainType == 3)
            {
                bool nearMount = false;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        int tx = x + dx, ty = y + dy;
                        if (tx >= 0 && tx < width && ty >= 0 && ty < height && _mapData[ty * width + tx].TerrainType == 6) nearMount = true;
                    }
                if (nearMount && prng.NextDouble() < oreProbability)
                {
                    var c = _mapData[idx]; c.TerrainType = 10; _mapData[idx] = c;
                }
            }
        }
    }

    void GenerateCities(System.Random prng)
    {
        for (int i = 0; i < cityCount; i++)
        {
            int cx = prng.Next(oceanMargin, width - oceanMargin);
            int cy = prng.Next(oceanMargin, height - oceanMargin);
            if (math.distance(new float2(cx, cy), new float2(PlayerSpawnPoint.x, PlayerSpawnPoint.y)) < safeRadius * 1.5f) continue;

            if (_mapData[cy * width + cx].TerrainType == 3)
            {
                CityCenters.Add(new int2(cx, cy));
                int r = prng.Next(cityMinSize, cityMaxSize);
                for (int y = cy - r; y <= cy + r; y++) for (int x = cx - r; x <= cx + r; x++)
                    {
                        if (x < 0 || x >= width || y < 0 || y >= height) continue;
                        if (math.distance(new float2(x, y), new float2(cx, cy)) > r) continue;
                        int idx = y * width + x;
                        if (_mapData[idx].TerrainType == 3)
                        {
                            var c = _mapData[idx];
                            if (prng.NextDouble() < 0.3) c.TerrainType = 8; // Road
                            else if (prng.NextDouble() < buildingDensity) { c.TerrainType = 9; c.BuildingType = 10; } // Ruins
                            _mapData[idx] = c;
                        }
                    }
            }
        }
    }

    void GenerateFromImage()
    {
        PlayerSpawnPoint = new Vector2Int(width / 2, height / 2);
        for (int i = 0; i < _mapData.Length; i++)
        {
            var c = new CellData(); c.Position = new int2(i % width, i / width); c.Height = 0;
            float g = mapLayoutTexture.GetPixel(i % width, i / width).grayscale;
            if (g > 0.8f) { c.TerrainType = 9; c.BuildingType = 10; }
            else if (g > 0.4f) c.TerrainType = 6;
            else c.TerrainType = 3;
            _mapData[i] = c;
        }
        FlattenSpawnArea(PlayerSpawnPoint.x, PlayerSpawnPoint.y, (int)safeRadius);
    }

    void GenerateMesh()
    {
        Mesh mesh = new Mesh(); mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        Vector3[] v = new Vector3[width * height * 4]; int[] t = new int[width * height * 6]; Color[] c = new Color[width * height * 4]; Vector3[] n = new Vector3[width * height * 4]; Vector2[] uv = new Vector2[width * height * 4];
        int vi = 0; int ti = 0; Vector3 up = Vector3.up;

        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int idx = y * width + x; var cell = _mapData[idx]; Color col = Color.black;
                switch (cell.TerrainType)
                {
                    case 0: col = colorDeepWater; break;
                    case 1: col = colorWater; break;
                    case 2: col = colorSand; break;
                    case 3: col = colorGrass; break;
                    case 4: col = colorForest; break;
                    case 5: col = colorSwamp; break;
                    case 6: col = colorMountain; break;
                    case 7: col = colorSnow; break;
                    case 8: col = colorRoad; break;
                    case 9: col = colorRuins; break;
                    case 10: col = colorOre; break;
                }
                col.a = 1f;

                v[vi] = new Vector3(x, 0, y); v[vi + 1] = new Vector3(x, 0, y + 1); v[vi + 2] = new Vector3(x + 1, 0, y + 1); v[vi + 3] = new Vector3(x + 1, 0, y);
                c[vi] = col; c[vi + 1] = col; c[vi + 2] = col; c[vi + 3] = col;
                n[vi] = up; n[vi + 1] = up; n[vi + 2] = up; n[vi + 3] = up;
                uv[vi] = new Vector2(0, 0); uv[vi + 1] = new Vector2(0, 1); uv[vi + 2] = new Vector2(1, 1); uv[vi + 3] = new Vector2(1, 0);
                t[ti] = vi; t[ti + 1] = vi + 1; t[ti + 2] = vi + 2; t[ti + 3] = vi; t[ti + 4] = vi + 2; t[ti + 5] = vi + 3; vi += 4; ti += 6;
            }
        mesh.vertices = v; mesh.triangles = t; mesh.colors = c; mesh.normals = n; mesh.uv = uv;

        _meshFilter.mesh = mesh; MeshCollider mc = GetComponent<MeshCollider>(); if (mc != null) mc.sharedMesh = mesh;
        if (_meshRenderer.sharedMaterial == null) SetOpaqueUnlitMaterial();
        BoxCollider bc = GetComponent<BoxCollider>(); if (bc != null) { bc.center = new Vector3(width / 2f, 0f, height / 2f); bc.size = new Vector3(width, 1f, height); }
    }

    void SpawnEnvironmentEntities()
    {
        if (rockMesh == null || treeMesh == null) return;
        EntityArchetype rockArch = _entityManager.CreateArchetype(typeof(LocalTransform), typeof(LocalToWorld), typeof(RenderMeshArray), typeof(RenderBounds), typeof(BuildingHealth));
        EntityArchetype treeArch = _entityManager.CreateArchetype(typeof(LocalTransform), typeof(LocalToWorld), typeof(RenderMeshArray), typeof(RenderBounds), typeof(BuildingHealth));
        EntityArchetype ruinsArch = _entityManager.CreateArchetype(typeof(LocalTransform), typeof(LocalToWorld), typeof(RenderMeshArray), typeof(RenderBounds), typeof(BuildingHealth));

        RenderMeshDescription rockDesc = new RenderMeshDescription(UnityEngine.Rendering.ShadowCastingMode.On, true);
        RenderMeshArray rockRMA = new RenderMeshArray(new Material[] { rockMaterial }, new Mesh[] { rockMesh });
        RenderMeshDescription treeDesc = new RenderMeshDescription(UnityEngine.Rendering.ShadowCastingMode.On, true);
        RenderMeshArray treeRMA = new RenderMeshArray(new Material[] { treeMaterial }, new Mesh[] { treeMesh });
        RenderMeshDescription ruinsDesc = new RenderMeshDescription(UnityEngine.Rendering.ShadowCastingMode.On, true);
        RenderMeshArray ruinsRMA = new RenderMeshArray(new Material[] { ruinsMaterial }, new Mesh[] { ruinsMesh });

        for (int i = 0; i < _mapData.Length; i++)
        {
            var c = _mapData[i]; int x = c.Position.x; int y = c.Position.y; float3 pos = new float3(x, 0, y);

            if (c.TerrainType == 6 || c.TerrainType == 7)
            {
                Entity e = _entityManager.CreateEntity(rockArch); RenderMeshUtility.AddComponents(e, _entityManager, rockDesc, rockRMA, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
                _entityManager.SetComponentData(e, LocalTransform.FromPositionRotationScale(pos, quaternion.RotateY(math.radians(UnityEngine.Random.Range(0, 360))), UnityEngine.Random.Range(0.8f, 1.5f)));
                _entityManager.SetComponentData(e, new BuildingHealth { Value = 2000, Max = 2000 }); _environmentEntities.Add(e);
            }
            else if (c.TerrainType == 4 || c.TerrainType == 5)
            {
                Entity e = _entityManager.CreateEntity(treeArch); RenderMeshUtility.AddComponents(e, _entityManager, treeDesc, treeRMA, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
                _entityManager.SetComponentData(e, LocalTransform.FromPositionRotationScale(new float3(x + UnityEngine.Random.Range(-0.3f, 0.3f), 0, y + UnityEngine.Random.Range(-0.3f, 0.3f)), quaternion.RotateY(math.radians(UnityEngine.Random.Range(0, 360))), UnityEngine.Random.Range(0.7f, 1.3f)));
                _entityManager.SetComponentData(e, new BuildingHealth { Value = 100, Max = 100 }); _environmentEntities.Add(e);
            }
            else if (c.TerrainType == 9)
            {
                Entity e = _entityManager.CreateEntity(ruinsArch); RenderMeshUtility.AddComponents(e, _entityManager, ruinsDesc, ruinsRMA, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
                _entityManager.SetComponentData(e, LocalTransform.FromPosition(pos)); _entityManager.SetComponentData(e, new BuildingHealth { Value = 500, Max = 500 });
                BuildingMap.TryAdd(i, e); _environmentEntities.Add(e);
            }
        }
    }

    public void PlaceStructure(int x, int y, byte buildingType, bool blocksMovement, Entity buildingEntity = default)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return;
        int i = y * width + x; var c = _mapData[i]; c.BuildingType = buildingType; if (blocksMovement) c.TerrainType = 6;
        _mapData[i] = c; if (buildingEntity != Entity.Null) BuildingMap.TryAdd(i, buildingEntity);
    }
    public void RemoveStructure(int x, int y)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return;
        int i = y * width + x; var c = _mapData[i]; c.BuildingType = 0; c.TerrainType = 3;
        _mapData[i] = c; if (BuildingMap.ContainsKey(i)) BuildingMap.Remove(i);
        if (FlowFieldController.Instance != null) FlowFieldController.Instance.CalculateFlowField();
    }
}