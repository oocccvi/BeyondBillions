using UnityEngine;
using Unity.Entities;
using Unity.Collections;

public class FogOfWarDisplay : MonoBehaviour
{
    public static FogOfWarDisplay Instance { get; private set; }

    [Header("设置")]
    public float updateInterval = 0.1f;
    public Color unknownColor = Color.black;
    public Color exploredColor = new Color(0, 0, 0, 0.6f);

    private Texture2D _fogTexture;
    private float _timer;
    private MeshRenderer _renderer;

    private EntityQuery _fogDataQuery;
    private EntityManager _entityManager;

    // [新增] 地图全开状态
    private bool _isMapRevealed = false;

    void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        Instance = this;
    }

    void Start()
    {
        _entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
        _fogDataQuery = _entityManager.CreateEntityQuery(typeof(FogMapData));

        CreateTemporaryCurtain();
    }

    void CreateTemporaryCurtain()
    {
        GameObject curtain = GameObject.CreatePrimitive(PrimitiveType.Quad);
        curtain.name = "Fog_Curtain";
        curtain.transform.SetParent(transform);
        curtain.transform.eulerAngles = new Vector3(90, 0, 0);
        curtain.transform.position = new Vector3(0, 20f, 0);
        curtain.transform.localScale = new Vector3(5000, 5000, 1);

        var rend = curtain.GetComponent<MeshRenderer>();
        rend.material = new Material(Shader.Find("Unlit/Color"));
        rend.material.color = Color.black;

        Destroy(curtain.GetComponent<Collider>());

        _renderer = rend;
    }

    void InitFog()
    {
        if (MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized) return;

        int w = MapGenerator.Instance.width;
        int h = MapGenerator.Instance.height;

        if (_renderer != null) Destroy(_renderer.gameObject);

        GameObject fogObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
        fogObj.name = "FogOfWar_Overlay";
        fogObj.transform.SetParent(transform);

        fogObj.transform.eulerAngles = new Vector3(90, 0, 0);
        fogObj.transform.position = new Vector3(w / 2f, 12f, h / 2f);
        fogObj.transform.localScale = new Vector3(w, h, 1);

        _renderer = fogObj.GetComponent<MeshRenderer>();
        _fogTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);

        _fogTexture.filterMode = FilterMode.Point;
        _fogTexture.wrapMode = TextureWrapMode.Clamp;

        Color[] pixels = new Color[w * h];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = unknownColor;
        _fogTexture.SetPixels(pixels);
        _fogTexture.Apply();

        Material mat = new Material(Shader.Find("Particles/Standard Unlit"));
        mat.SetColor("_Color", Color.white);
        mat.mainTexture = _fogTexture;
        mat.SetFloat("_Mode", 2);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = 3000;

        _renderer.material = mat;

        Destroy(fogObj.GetComponent<Collider>());

        // 初始化时应用当前全开状态
        _renderer.enabled = !_isMapRevealed;
    }

    void Update()
    {
        // [新增] 按 F4 切换地图全开
        if (Input.GetKeyDown(KeyCode.F4))
        {
            ToggleMapReveal();
        }

        if (_fogTexture == null)
        {
            InitFog();
            return;
        }

        // 如果地图全开了，就没必要浪费性能去刷新贴图了
        if (_isMapRevealed) return;

        _timer += Time.deltaTime;
        if (_timer < updateInterval) return;
        _timer = 0;

        UpdateTexture();
    }

    public void ToggleMapReveal()
    {
        _isMapRevealed = !_isMapRevealed;
        if (_renderer != null)
        {
            _renderer.enabled = !_isMapRevealed;
        }
        Debug.Log($"Map Reveal: {_isMapRevealed}");
    }

    void UpdateTexture()
    {
        if (_fogDataQuery.IsEmptyIgnoreFilter) return;

        var data = _fogDataQuery.GetSingleton<FogMapData>();

        if (!data.GridStatus.IsCreated) return;

        NativeArray<Color32> colors = new NativeArray<Color32>(data.GridStatus.Length, Allocator.Temp);
        Color32 cUnknown = unknownColor;
        Color32 cExplored = exploredColor;
        Color32 cVisible = new Color32(0, 0, 0, 0);

        for (int i = 0; i < data.GridStatus.Length; i++)
        {
            byte status = data.GridStatus[i];
            if (status == 0) colors[i] = cUnknown;
            else if (status == 1) colors[i] = cExplored;
            else colors[i] = cVisible;
        }

        _fogTexture.SetPixelData(colors, 0);
        _fogTexture.Apply();

        colors.Dispose();
    }
}