using UnityEngine;
using System.Collections.Generic;

public class LawManager : MonoBehaviour
{
    public static LawManager Instance { get; private set; }

    [System.Serializable]
    public class Law
    {
        public string title;       // 法律名称
        public string description; // 描述
        public bool isSigned;      // 是否已签署
        // 这里只是数据，逻辑我们在代码里通过 switch case 简单处理
        // 如果想要更高级的，可以使用 C# 委托 (Action)
    }

    public List<Law> laws = new List<Law>();

    void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        Instance = this;
    }

    void Start()
    {
        // 初始化一些法律
        // 注意：这里的效果描述只是文本，实际逻辑在 SignLaw 里写
        laws.Add(new Law { title = "战时配给 (Rationing)", description = "造塔便宜 20%，但每秒自动收入归零。", isSigned = false });
        laws.Add(new Law { title = "过度充能 (Overcharge)", description = "新塔射速 +100%，但射程减半。", isSigned = false });
        laws.Add(new Law { title = "强征税收 (Heavy Tax)", description = "立即获得 500 金币，但所有塔造价永久 +50。", isSigned = false });
        laws.Add(new Law { title = "噪音引诱 (Noise Lure)", description = "僵尸移速 +50% (更危险)，但击杀赏金翻倍。", isSigned = false });
    }

    // 签署法律的核心逻辑
    public void SignLaw(int index)
    {
        if (index < 0 || index >= laws.Count) return;
        Law law = laws[index];

        if (law.isSigned)
        {
            Debug.Log($"法律 [{law.title}] 已经签署过了！");
            return;
        }

        Debug.Log($"签署法案: <color=yellow>{law.title}</color>");
        law.isSigned = true;

        // --- 执行法律效果 ---
        switch (index)
        {
            case 0: // 战时配给
                if (ResourceManager.Instance != null)
                {
                    ResourceManager.Instance.turretCost = (int)(ResourceManager.Instance.turretCost * 0.8f);
                    ResourceManager.Instance.passiveIncome = 0; // 没收低保
                }
                break;

            case 1: // 过度充能
                if (TurretSpawner.Instance != null)
                {
                    TurretSpawner.Instance.fireRate *= 2.0f; // 射速翻倍
                    TurretSpawner.Instance.range *= 0.5f;    // 射程减半
                }
                break;

            case 2: // 强征税收
                if (ResourceManager.Instance != null)
                {
                    ResourceManager.Instance.AddGold(500); // 抢钱
                    ResourceManager.Instance.turretCost += 50; // 通货膨胀
                }
                break;

            case 3: // 噪音引诱
                if (WaveManager.Instance != null)
                {
                    // 这里的修改会影响之后生成的所有僵尸
                    WaveManager.Instance.zombieSpeed *= 1.5f;
                }
                // 我们还没有在 ResourceManager 里做赏金倍率变量，
                // 如果你想实现赏金翻倍，需要在 CombatSystems 的 ProjectileSystem 里把 Enqueue(5) 改成读取一个倍率变量。
                // 暂时我们在 Console 模拟一下效果：
                Debug.Log("警告：僵尸变快了！(赏金翻倍逻辑需去 CombatSystems 修改)");
                break;
        }
    }

    // UI 显示
    void OnGUI()
    {
        // 放在左侧，金币 UI 下方
        float startY = 150;
        float width = 300;
        float height = 50;
        float spacing = 10;

        GUIStyle styleBtn = new GUIStyle(GUI.skin.button);
        styleBtn.fontSize = 12;
        styleBtn.wordWrap = true;
        styleBtn.alignment = TextAnchor.MiddleLeft;

        GUIStyle styleLabel = new GUIStyle();
        styleLabel.fontSize = 20;
        styleLabel.normal.textColor = Color.white;
        styleLabel.fontStyle = FontStyle.Bold;

        GUI.Label(new Rect(20, startY - 30, 200, 30), "=== LAWS (法典) ===", styleLabel);

        for (int i = 0; i < laws.Count; i++)
        {
            Law law = laws[i];
            string displayText = law.isSigned ? $"[已签署] {law.title}" : $"{law.title}\n<color=yellow>{law.description}</color>";

            // 如果已签署，按钮变灰或不可点
            if (law.isSigned)
            {
                GUI.enabled = false;
            }

            if (GUI.Button(new Rect(20, startY + (i * (height + spacing)), width, height), displayText, styleBtn))
            {
                SignLaw(i);
            }

            GUI.enabled = true; // 恢复
        }
    }
}