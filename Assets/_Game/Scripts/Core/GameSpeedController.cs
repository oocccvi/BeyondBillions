using UnityEngine;

public class GameSpeedController : MonoBehaviour
{
    public static GameSpeedController Instance { get; private set; }

    [Header("倍速设置")]
    public float speed1 = 1.0f; // 正常速度
    public float speed2 = 2.0f; // 2倍速
    public float speed3 = 4.0f; // 4倍速 (极速)

    private float _currentScale = 1.0f;

    void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        Instance = this;
    }

    void Update()
    {
        // 监听数字键 1, 2, 3
        if (Input.GetKeyDown(KeyCode.Alpha1)) SetSpeed(speed1);
        if (Input.GetKeyDown(KeyCode.Alpha2)) SetSpeed(speed2);
        if (Input.GetKeyDown(KeyCode.Alpha3)) SetSpeed(speed3);
    }

    public void SetSpeed(float speed)
    {
        _currentScale = speed;
        Time.timeScale = _currentScale;

        // 可选：如果你的游戏大量依赖物理引擎(Rigidbody)，可能需要调整 fixedDeltaTime
        // Time.fixedDeltaTime = 0.02f * Time.timeScale;

        Debug.Log($"游戏速度已切换为: {_currentScale}x");
    }

    // 在右上角显示当前倍速
    void OnGUI()
    {
        GUIStyle style = new GUIStyle();
        style.fontSize = 25;
        style.normal.textColor = Color.cyan;
        style.fontStyle = FontStyle.Bold;
        style.alignment = TextAnchor.UpperRight;

        string text = $"SPEED: {_currentScale}x  ";
        GUI.Label(new Rect(Screen.width - 250, 20, 230, 50), text, style);
    }
}