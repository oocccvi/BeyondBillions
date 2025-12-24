using UnityEngine;

public class ResourceManager : MonoBehaviour
{
    public static ResourceManager Instance { get; private set; }

    [Header("初始资源")]
    public int startingGold = 0; // 既然要自己造HQ，初始也许可以给0，或者给点启动资金
    public int currentGold;
    public int passiveIncome = 0;

    [Header("建造价格")]
    public int hqCost = 0; // [新增] HQ通常是免费的，或者是开局给的
    public int turretCost = 100;
    public int soldierCost = 50;

    private float _timer;

    void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        Instance = this;
        currentGold = startingGold;
    }

    void Update()
    {
        if (passiveIncome > 0)
        {
            _timer += Time.deltaTime;
            if (_timer >= 1.0f)
            {
                AddGold(passiveIncome);
                _timer = 0;
            }
        }

        if (Input.GetKeyDown(KeyCode.M))
        {
            AddGold(1000);
            Debug.Log("作弊成功：金币 +1000");
        }
    }

    public bool TrySpendGold(int amount)
    {
        if (currentGold >= amount)
        {
            currentGold -= amount;
            return true;
        }
        return false;
    }

    public void AddGold(int amount)
    {
        currentGold += amount;
    }

    void OnGUI()
    {
        GUIStyle style = new GUIStyle();
        style.fontSize = 30;
        style.normal.textColor = Color.yellow;
        style.fontStyle = FontStyle.Bold;

        GUI.Label(new Rect(20, 20, 300, 50), $"GOLD: {currentGold}", style);
    }
}