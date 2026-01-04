using UnityEngine;
using Unity.Entities;
using Unity.Collections;

public class FogOfWarDisplay : MonoBehaviour
{
    public static FogOfWarDisplay Instance { get; private set; }

    [Header("外观设置")]
    public Material fogMaterial;
    public Texture2D noiseTexture;

    [Header("性能设置")]
    public float updateInterval = 0.1f;
    // 未探索区域：白色 (Alpha 1) -> Shader 显示厚云
    public Color unknownColor = Color.white;
    // [修改] 已探索区域：透明 (Alpha 0) -> Shader 不显示云
    // 不需要 ExploredColor 了，因为没有中间状态
    public Color visibleColor = new Color(0, 0, 0, 0);

    private Texture2D _fogTexture;
    private float _timer;
    private MeshRenderer _meshRenderer;

    private EntityQuery _fogDataQuery;
    private EntityManager _entityManager;

    void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        Instance = this;
    }

    void Start()
    {
        _entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
        _fogDataQuery = _entityManager.CreateEntityQuery(typeof(FogMapData));
    }

    void InitFog()
    {
        if (MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized) return;

        int w = MapGenerator.Instance.width;
        int h = MapGenerator.Instance.height;

        if (_meshRenderer != null) Destroy(_meshRenderer.gameObject);

        GameObject fogObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
        fogObj.name = "FogOfWar_Overlay";
        fogObj.transform.SetParent(transform);
        fogObj.transform.eulerAngles = new Vector3(90, 0, 0);

        // 贴地
        fogObj.transform.position = new Vector3(w / 2f, 0.1f, h / 2f);
        fogObj.transform.localScale = new Vector3(w, h, 1);

        _meshRenderer = fogObj.GetComponent<MeshRenderer>();
        Destroy(fogObj.GetComponent<Collider>());

        _fogTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);
        _fogTexture.filterMode = FilterMode.Bilinear;
        _fogTexture.wrapMode = TextureWrapMode.Clamp;

        Color[] pixels = new Color[w * h];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = unknownColor;
        _fogTexture.SetPixels(pixels);
        _fogTexture.Apply();

        if (fogMaterial != null)
        {
            _meshRenderer.material = new Material(fogMaterial);
            _meshRenderer.material.mainTexture = _fogTexture;

            // [关键新增] 将地图尺寸传给 Shader
            // 这样 Shader 就能自动计算出正确的偏移量
            _meshRenderer.material.SetFloat("_MapSize", (float)w);

            if (noiseTexture != null)
            {
                _meshRenderer.material.SetTexture("_NoiseTex", noiseTexture);
            }
        }
    }

    void Update()
    {
        if (_fogTexture == null)
        {
            InitFog();
            return;
        }

        _timer += Time.deltaTime;
        if (_timer < updateInterval) return;
        _timer = 0;

        UpdateTexture();
    }

    void UpdateTexture()
    {
        if (_fogDataQuery.IsEmptyIgnoreFilter) return;

        var data = _fogDataQuery.GetSingleton<FogMapData>();
        if (!data.GridStatus.IsCreated) return;

        NativeArray<Color32> colors = new NativeArray<Color32>(data.GridStatus.Length, Allocator.Temp);

        Color32 cUnknown = unknownColor; // 白 (有云)
        Color32 cVisible = visibleColor; // 透明 (无云)

        for (int i = 0; i < data.GridStatus.Length; i++)
        {
            byte status = data.GridStatus[i];

            // [逻辑简化]
            // 0 = 未探索 -> 显示云
            // 任何非 0 值 (1或2) -> 都是已探索 -> 显示透明
            if (status == 0)
            {
                colors[i] = cUnknown;
            }
            else
            {
                colors[i] = cVisible;
            }
        }

        _fogTexture.SetPixelData(colors, 0);
        _fogTexture.Apply();

        colors.Dispose();
    }
}