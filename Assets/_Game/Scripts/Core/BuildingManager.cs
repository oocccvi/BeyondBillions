using UnityEngine;
using Unity.Mathematics;
using Unity.Entities;
using Unity.Collections;
using Unity.Transforms;
using System.Collections.Generic;

public class BuildingManager : MonoBehaviour
{
    // ... (BuildMode 枚举等保持不变)
    public static BuildingManager Instance { get; private set; }

    [Header("设置")]
    public LayerMask terrainLayer;

    public enum BuildMode { None, Turret, Soldier, HQ, Command_AttackMove, Command_Patrol, Command_Scout }
    private BuildMode _currentMode = BuildMode.None;
    private float _bottomBarHeight = 100f;
    public bool hasPlacedHQ = false;
    private bool _isSelecting = false;
    private Vector2 _selectionStartPos;
    private List<Entity> _selectedSoldiers = new List<Entity>();
    private bool _tabGridEnabled = false;
    private Color _validColor = new Color(0f, 1f, 0f, 0.4f);
    private Color _invalidColor = new Color(1f, 0f, 0f, 0.4f);
    private Color _defaultGridColor = new Color(1f, 1f, 1f, 0.2f);
    private float _lastRightClickTime = 0f;
    private float _doubleClickThreshold = 0.3f;

    void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        Instance = this;
    }

    // ... (Update, ToggleBuildMode, ClearBuildMode, UpdateGridState, UpdatePlacementPreview, GetBuildingSize, CheckPlacementValidity, HandleMapClick 均保持不变，可以直接复制以前的) ...
    // 为节省篇幅，这里重点展示修改后的 UpdateSoldierState

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Tab)) { _tabGridEnabled = !_tabGridEnabled; UpdateGridState(); }
        if (Input.GetKeyDown(KeyCode.Escape)) ClearBuildMode();
        if (_currentMode == BuildMode.Turret || _currentMode == BuildMode.HQ || _currentMode == BuildMode.Soldier) UpdatePlacementPreview();
        else if (GridOverlay.Instance != null) GridOverlay.Instance.ClearHighlight();

        if (Input.GetMouseButtonDown(1))
        {
            if (_currentMode != BuildMode.None) ClearBuildMode();
            else if (_selectedSoldiers.Count > 0)
            {
                if (Time.time - _lastRightClickTime < _doubleClickThreshold) CommandSoldiersMove(SoldierCommand.Sprint);
                else CommandSoldiersMove(SoldierCommand.AttackMove);
                _lastRightClickTime = Time.time;
            }
        }

        if (Input.GetMouseButtonDown(0))
        {
            if (Input.mousePosition.y > _bottomBarHeight)
            {
                if (_currentMode != BuildMode.None) HandleMapClick();
                else { _isSelecting = true; _selectionStartPos = Input.mousePosition; }
            }
        }

        if (Input.GetMouseButtonUp(0)) { if (_isSelecting) { FinishSelection(); _isSelecting = false; } }

        if (_selectedSoldiers.Count > 0)
        {
            if (Input.GetKeyDown(KeyCode.S)) IssueStop();
            if (Input.GetKeyDown(KeyCode.H)) IssueHunt();
            if (Input.GetKeyDown(KeyCode.A)) ToggleBuildMode(BuildMode.Command_AttackMove);
            if (Input.GetKeyDown(KeyCode.P)) ToggleBuildMode(BuildMode.Command_Patrol);
            if (Input.GetKeyDown(KeyCode.K)) ToggleBuildMode(BuildMode.Command_Scout);
        }
    }

    // ... 省略中间辅助函数，请直接用上面的完整代码 ...
    // 这里为了演示 UpdateSoldierState 的修改

    public void ToggleBuildMode(BuildMode mode) { if (_currentMode == mode) _currentMode = BuildMode.None; else { _currentMode = mode; if (mode == BuildMode.Turret || mode == BuildMode.HQ || mode == BuildMode.Soldier) _selectedSoldiers.Clear(); } UpdateGridState(); }
    public void ClearBuildMode() { _currentMode = BuildMode.None; UpdateGridState(); if (GridOverlay.Instance != null) GridOverlay.Instance.ClearHighlight(); }
    void UpdateGridState() { if (GridOverlay.Instance != null) { bool shouldShow = _tabGridEnabled || (_currentMode != BuildMode.None); GridOverlay.Instance.ShowGrid(shouldShow); if (_currentMode == BuildMode.None) GridOverlay.Instance.gridColor = _defaultGridColor; } }
    void UpdatePlacementPreview() { if (GridOverlay.Instance == null) return; Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition); RaycastHit hit; if (Physics.Raycast(ray, out hit, 1000f)) { int x = Mathf.RoundToInt(hit.point.x); int z = Mathf.RoundToInt(hit.point.z); int size = GetBuildingSize(_currentMode); bool isValid = CheckPlacementValidity(x, z); Color color = isValid ? _validColor : _invalidColor; GridOverlay.Instance.SetHighlight(x, z, size, color); } else GridOverlay.Instance.ClearHighlight(); }
    int GetBuildingSize(BuildMode mode) { switch (mode) { case BuildMode.Turret: return 2; case BuildMode.HQ: return 4; default: return 1; } }
    bool CheckPlacementValidity(int centerX, int centerZ) { if (MapGenerator.Instance == null) return false; int size = GetBuildingSize(_currentMode); int offset = size / 2; for (int x = centerX - offset; x < centerX - offset + size; x++) { for (int z = centerZ - offset; z < centerZ - offset + size; z++) { if (x < 0 || x >= MapGenerator.Instance.width || z < 0 || z >= MapGenerator.Instance.height) return false; var mapData = MapGenerator.Instance.MapData; int index = z * MapGenerator.Instance.width + x; var cell = mapData[index]; if (cell.TerrainType != 1) return false; if (cell.BuildingType != 0) return false; } } return true; }
    void HandleMapClick() { Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition); RaycastHit hit; if (Physics.Raycast(ray, out hit, 1000f)) { int x = Mathf.RoundToInt(hit.point.x); int z = Mathf.RoundToInt(hit.point.z); if (_currentMode == BuildMode.HQ || _currentMode == BuildMode.Turret || _currentMode == BuildMode.Soldier) { if (CheckPlacementValidity(x, z)) { if (_currentMode == BuildMode.HQ) BuildHQ(x, z); else if (_currentMode == BuildMode.Turret) { if (!hasPlacedHQ) { Debug.LogWarning("必须先放置 HQ！"); return; } BuildTurret(x, z); } else if (_currentMode == BuildMode.Soldier) { if (!hasPlacedHQ) { Debug.LogWarning("必须先放置 HQ！"); return; } BuildSoldier(hit.point); } } } else if (_currentMode == BuildMode.Command_AttackMove) { IssueCommand(SoldierCommand.AttackMove, hit.point); ClearBuildMode(); } else if (_currentMode == BuildMode.Command_Patrol) { IssueCommand(SoldierCommand.Patrol, hit.point); ClearBuildMode(); } else if (_currentMode == BuildMode.Command_Scout) { IssueCommand(SoldierCommand.Scout, hit.point); ClearBuildMode(); } } }

    void IssueStop() { UpdateSoldierState(SoldierCommand.Idle, Vector3.zero); }
    void IssueHunt() { UpdateSoldierState(SoldierCommand.Hunt, Vector3.zero); }
    void IssueCommand(SoldierCommand cmd, Vector3 targetOverride = default) { Vector3 target = targetOverride; if (targetOverride == default) { Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition); if (Physics.Raycast(ray, out RaycastHit hit, 1000f)) target = hit.point; else return; } UpdateSoldierState(cmd, target); }

    // [核心修改]
    void UpdateSoldierState(SoldierCommand cmd, Vector3 targetPos)
    {
        EntityManager em = World.DefaultGameObjectInjectionWorld.EntityManager;
        float3 tPos = new float3(targetPos.x, 1f, targetPos.z);

        // 如果指令需要移动 (Move, AttackMove, Sprint)，则请求生成流场
        Entity flowFieldEntity = Entity.Null;
        if (cmd == SoldierCommand.Move || cmd == SoldierCommand.AttackMove || cmd == SoldierCommand.Sprint)
        {
            if (FlowFieldController.Instance != null && _selectedSoldiers.Count > 0)
            {
                // 为这组士兵请求一个局部流场
                flowFieldEntity = FlowFieldController.Instance.CreateLocalFlowField(_selectedSoldiers, tPos);
            }
        }

        for (int i = _selectedSoldiers.Count - 1; i >= 0; i--)
        {
            Entity e = _selectedSoldiers[i];
            if (em.Exists(e) && em.HasComponent<SoldierState>(e))
            {
                SoldierState state = em.GetComponentData<SoldierState>(e);
                state.Command = cmd;
                state.TargetPosition = tPos;

                // [新增] 分配流场 ID
                state.CurrentFlowFieldEntity = flowFieldEntity;

                if (cmd == SoldierCommand.Idle) state.IsMoving = false;
                else
                {
                    state.IsMoving = true;
                    if (cmd == SoldierCommand.Patrol)
                    {
                        var transform = em.GetComponentData<LocalTransform>(e);
                        state.PatrolStartPosition = transform.Position;
                    }
                }
                state.StopDistance = 0.5f + UnityEngine.Random.Range(0f, 2f);
                em.SetComponentData(e, state);
            }
            else _selectedSoldiers.RemoveAt(i);
        }
    }

    void CommandSoldiersMove(SoldierCommand commandType = SoldierCommand.AttackMove) { Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition); if (Physics.Raycast(ray, out RaycastHit hit, 1000f)) { UpdateSoldierState(commandType, hit.point); } }
    void BuildHQ(int x, int z) { if (hasPlacedHQ) return; if (ResourceManager.Instance == null) return; if (ResourceManager.Instance.TrySpendGold(ResourceManager.Instance.hqCost)) { if (TurretSpawner.Instance != null) { TurretSpawner.Instance.SpawnHQ(new float3(x, 0, z)); hasPlacedHQ = true; ClearBuildMode(); } } }
    void BuildTurret(int x, int z) { if (ResourceManager.Instance == null) return; if (ResourceManager.Instance.TrySpendGold(ResourceManager.Instance.turretCost)) { if (TurretSpawner.Instance != null) TurretSpawner.Instance.SpawnTurret(new float3(x, 0, z)); } }
    void BuildSoldier(Vector3 p) { if (ResourceManager.Instance == null) return; if (ResourceManager.Instance.TrySpendGold(ResourceManager.Instance.soldierCost)) { if (SoldierSpawner.Instance != null) SoldierSpawner.Instance.SpawnSoldier(new float3(p.x, 0, p.z)); } }
    void FinishSelection()
    {
        _selectedSoldiers.Clear(); EntityManager em = World.DefaultGameObjectInjectionWorld.EntityManager; EntityQuery query = em.CreateEntityQuery(typeof(SoldierTag), typeof(LocalTransform)); NativeArray<Entity> ent = query.ToEntityArray(Allocator.Temp); NativeArray<LocalTransform> trs = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        bool isClick = Vector2.Distance(_selectionStartPos, Input.mousePosition) < 10f;
        if (isClick) { float closeD = float.MaxValue; Entity closeE = Entity.Null; Ray r = Camera.main.ScreenPointToRay(Input.mousePosition); for (int i = 0; i < ent.Length; i++) { Vector3 sp = (Vector3)trs[i].Position; float dist = Vector3.Cross(r.direction, sp - r.origin).magnitude; if (dist < 1f) { float dc = Vector3.Distance(r.origin, sp); if (dc < closeD) { closeD = dc; closeE = ent[i]; } } } if (closeE != Entity.Null) _selectedSoldiers.Add(closeE); }
        else { Vector2 min = Vector2.Min(_selectionStartPos, Input.mousePosition); Vector2 max = Vector2.Max(_selectionStartPos, Input.mousePosition); Rect rect = Rect.MinMaxRect(min.x, min.y, max.x, max.y); for (int i = 0; i < ent.Length; i++) { Vector3 sp = Camera.main.WorldToScreenPoint(trs[i].Position); if (sp.z > 0 && rect.Contains(sp)) _selectedSoldiers.Add(ent[i]); } }
        ent.Dispose(); trs.Dispose(); if (_selectedSoldiers.Count > 0) Debug.Log($"Selected {_selectedSoldiers.Count}");
    }
    void OnGUI()
    {
        if (_isSelecting) { Vector2 m = Event.current.mousePosition; Vector2 s = _selectionStartPos; s.y = Screen.height - s.y; Rect r = Rect.MinMaxRect(Mathf.Min(s.x, m.x), Mathf.Min(s.y, m.y), Mathf.Max(s.x, m.x), Mathf.Max(s.y, m.y)); GUI.color = new Color(0, 1, 0, 0.3f); GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = Color.green; GUI.Box(r, ""); GUI.color = Color.white; }
        float sw = Screen.width; float sh = Screen.height; GUI.Box(new Rect(0, sh - _bottomBarHeight, sw, _bottomBarHeight), ""); GUIStyle btn = new GUIStyle(GUI.skin.button); btn.fontSize = 14; btn.fontStyle = FontStyle.Bold; float w = 120; float h = 50; float x = 20; float y = sh - _bottomBarHeight + 25;
        if (hasPlacedHQ) GUI.enabled = false; if (_currentMode == BuildMode.HQ) GUI.backgroundColor = Color.green; if (GUI.Button(new Rect(x, y, w, h), $"HQ\n${ResourceManager.Instance?.hqCost}", btn)) ToggleBuildMode(BuildMode.HQ); GUI.backgroundColor = Color.white; GUI.enabled = true;
        x += w + 10; if (!hasPlacedHQ) GUI.enabled = false; if (_currentMode == BuildMode.Turret) GUI.backgroundColor = Color.green; if (GUI.Button(new Rect(x, y, w, h), $"Turret\n${ResourceManager.Instance?.turretCost}", btn)) ToggleBuildMode(BuildMode.Turret); GUI.backgroundColor = Color.white;
        x += w + 10; if (_currentMode == BuildMode.Soldier) GUI.backgroundColor = Color.green; if (GUI.Button(new Rect(x, y, w, h), $"Soldier\n${ResourceManager.Instance?.soldierCost}", btn)) ToggleBuildMode(BuildMode.Soldier); GUI.backgroundColor = Color.white; GUI.enabled = true;
        if (_selectedSoldiers.Count > 0) { x += w + 50; if (GUI.Button(new Rect(x, y, 60, h), "Stop\n(S)", btn)) IssueStop(); x += 70; if (GUI.Button(new Rect(x, y, 60, h), "歼敌\n(H)", btn)) IssueHunt(); x += 70; if (_currentMode == BuildMode.Command_AttackMove) GUI.backgroundColor = Color.green; if (GUI.Button(new Rect(x, y, 80, h), "Attack\n(A)", btn)) ToggleBuildMode(BuildMode.Command_AttackMove); GUI.backgroundColor = Color.white; x += 90; if (_currentMode == BuildMode.Command_Patrol) GUI.backgroundColor = Color.green; if (GUI.Button(new Rect(x, y, 80, h), "Patrol\n(P)", btn)) ToggleBuildMode(BuildMode.Command_Patrol); GUI.backgroundColor = Color.white; x += 90; if (_currentMode == BuildMode.Command_Scout) GUI.backgroundColor = Color.green; if (GUI.Button(new Rect(x, y, 80, h), "Scout\n(K)", btn)) ToggleBuildMode(BuildMode.Command_Scout); GUI.backgroundColor = Color.white; }
        GUIStyle label = new GUIStyle(); label.fontSize = 20; label.normal.textColor = Color.white; label.alignment = TextAnchor.MiddleCenter; string txt = _currentMode == BuildMode.None ? (_selectedSoldiers.Count > 0 ? $"{_selectedSoldiers.Count} Soldiers Ready" : "Select Units or Build") : $"Command: {_currentMode}"; GUI.Label(new Rect(0, sh - _bottomBarHeight - 30, sw, 30), txt, label);
    }
}