using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System;

namespace GifExportAll;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class ExportBatchTools : BaseUnityPlugin
{
    // 缓存当前的列表控制器实例
    public static AnimationListController _listInstance;
    
    // UI 文本配置
    public const string ListLabel = "选择此项可导出全部"; 
    private const string BtnBatch = "批量导出全部";
    private const string BtnSingle = "导出当前动画";

    // 状态控制变量
    private bool _isBatching = false;
    private Button _exportBtn;
    private Coroutine _worker;

    // 反射字段缓存：提高 Update 效率
    private static FieldInfo _handlerInfo = typeof(AnimationListController).GetField("_spineModelHandler", BindingFlags.NonPublic | BindingFlags.Instance);
    private static FieldInfo _itemsInfo = typeof(AnimationListController).GetField("_allAnimationItems", BindingFlags.NonPublic | BindingFlags.Instance);
    private static FieldInfo _selectInfo = typeof(AnimationListController).GetField("_selectedAnimationName", BindingFlags.NonPublic | BindingFlags.Instance);
    private static FieldInfo _uiListField = typeof(AnimationListController).GetField("_animationList", BindingFlags.NonPublic | BindingFlags.Instance);
    private FieldInfo _skelInfo; 

    void Awake()
    {
        
        // 补丁初始化
        new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll();
        Logger.LogInfo($"Sinsa: {MyPluginInfo.PLUGIN_GUID} 已成功载入");
    }

    void Update()
    {
        FindExportButton();
        UpdateInterface();
    }

    // 实时锁定导出按钮，处理面板切换后的引用失效问题
    private void FindExportButton()
    {
        var docs = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
        foreach (var doc in docs)
        {
            var btn = doc.rootVisualElement.Q<Button>("export-button");
            if (btn != null && _exportBtn != btn)
            {
                _exportBtn = btn;
                _exportBtn.clicked += OnBtnClicked;
            }
        }
    }

    // 更新按钮状态及视觉效果
    private void UpdateInterface()
    {
        if (_exportBtn == null || _listInstance == null || _isBatching) return;

        // 获取底层选中的 Key
        string currentKey = _selectInfo?.GetValue(_listInstance) as string;
        
        // 检查骨骼是否处于播放状态（双重判定模式切换）
        bool active = IsSpineActive();

        if (currentKey != "-" && !string.IsNullOrEmpty(currentKey) || active)
        {
            // 单次导出模式
            _exportBtn.text = BtnSingle;
            _exportBtn.style.color = Color.white;
            _exportBtn.style.backgroundColor = new StyleColor(StyleKeyword.Null); 
        }
        else
        {
            // 批量导出模式
            _exportBtn.text = BtnBatch;
            _exportBtn.style.color = Color.cyan;
            _exportBtn.style.backgroundColor = new StyleColor(new Color(0.1f, 0.25f, 0.25f, 1f));
        }
    }

    // 检测 Spine 骨骼当前的播放状态
    private bool IsSpineActive()
    {
        try {
            var handler = _handlerInfo?.GetValue(_listInstance);
            var controller = handler?.GetType().GetProperty("AnimationController")?.GetValue(handler);
            if (controller == null) return false;

            if (_skelInfo == null) 
                _skelInfo = controller.GetType().GetField("_skeletonAnimation", BindingFlags.NonPublic | BindingFlags.Instance);
            
            var skel = _skelInfo?.GetValue(controller);
            var name = skel?.GetType().GetProperty("AnimationName")?.GetValue(skel) as string;
            
            return !string.IsNullOrEmpty(name);
        } catch { return false; }
    }

    // 按钮点击响应处理
    private void OnBtnClicked()
    {
        if (_isBatching) { Abort(); return; }

        string key = _selectInfo?.GetValue(_listInstance) as string;
        
        // 当选中项为空位且模型静止时，判定为进入批量逻辑
        if ((key == "-" || string.IsNullOrEmpty(key)) && !IsSpineActive())
        {
            _worker = StartCoroutine(BatchProcess());
        }
    }

    // 停止当前的导出协程
    private void Abort()
    {
        if (_worker != null) StopCoroutine(_worker);
        SetListLock(false); // 强制解锁列表
        _isBatching = false;
        Logger.LogWarning("批量任务已由用户手动中止。");
        UpdateInterface();
    }

    // --- 核心新增：控制列表锁定状态 ---
    private void SetListLock(bool isLocked)
    {
        try
        {
            var listView = _uiListField?.GetValue(_listInstance) as ListView;
            if (listView != null)
            {
                // 锁定选择功能：设置为 None 则无法点击选择，恢复为 Single
                listView.selectionType = isLocked ? SelectionType.None : SelectionType.Single;
                
                // 视觉反馈：锁定后降低亮度，提示不可操作
                listView.style.opacity = isLocked ? 0.5f : 1.0f;
                // 禁用拾取，彻底防止悬停效果
                listView.pickingMode = isLocked ? PickingMode.Ignore : PickingMode.Position;
            }
        }
        catch (Exception e)
        {
            Logger.LogError($"设置列表锁定失败: {e.Message}");
        }
    }

    IEnumerator BatchProcess()
    {
        _isBatching = true;
        SetListLock(true); // 锁定列表
        
        _exportBtn.text = "中止录制";
        _exportBtn.style.color = Color.red;

        var exporter = UnityEngine.Object.FindFirstObjectByType<AnimationExporter>();
        var handler = _handlerInfo?.GetValue(_listInstance);
        var items = _itemsInfo?.GetValue(_listInstance) as Dictionary<string, string>;

        if (exporter == null || handler == null || items == null) 
        { 
            Logger.LogError("找不到组件，任务取消。");
            SetListLock(false);
            _isBatching = false; 
            yield break; 
        }
        
        var controller = handler.GetType().GetProperty("AnimationController")?.GetValue(handler);
        var play = controller.GetType().GetMethod("PlayAnimation");

        // 过滤占位项，保留真实动作
        var tasks = items.Where(x => x.Key != "-" && !string.IsNullOrEmpty(x.Value)).ToArray();

        Logger.LogInfo($"开始批量任务，共计 {tasks.Length} 个动画。");

        for (int i = 0; i < tasks.Length; i++)
        {
            Logger.LogInfo($"[进度 {i + 1}/{tasks.Length}] 正在导出动画: {tasks[i].Key}");
            
            // 触发播放
            play?.Invoke(controller, new object[] { tasks[i].Value, true, 0 });
            // 等待模型加载及录制准备 (1.5s 为经验安全值)
            yield return new WaitForSeconds(1.5f);
            yield return new WaitForEndOfFrame();
            
            // 跳过第一项逻辑
            if(i == 0) continue;

            bool done = false;
            Action callback = () => done = true;
            exporter.OnExportComplete += callback;

            // 执行单次导出动作
            exporter.ExportAnimation();
            
            // 阻塞直至单次导出回调完成
            while (!done) yield return null;

            exporter.OnExportComplete -= callback;
            yield return new WaitForSeconds(0.3f); 
        }

        SetListLock(false); // 解锁列表
        _isBatching = false;
        _worker = null;
        Logger.LogInfo("批量导出完成。");
    }
}

// 劫持渲染层，替换列表展示文案
[HarmonyPatch(typeof(AnimationListController), "InitializeAniamtionList")]
class ListPatch
{
    static void Postfix(AnimationListController __instance)
    {
        // 捕获当前的单例引用
        ExportBatchTools._listInstance = __instance;
        
        var listField = typeof(AnimationListController).GetField("_animationList", BindingFlags.NonPublic | BindingFlags.Instance);
        var listView = listField?.GetValue(__instance) as ListView;

        if (listView != null)
        {
            // 劫持原有的 bindItem 委托
            var originBind = listView.bindItem;
            listView.bindItem = (element, idx) => {
                originBind?.Invoke(element, idx); 

                var ctrl = element.userData as AnimationListEntryController;
                var filter = typeof(AnimationListController).GetField("_filteredAnimationItems", BindingFlags.NonPublic | BindingFlags.Instance);
                var data = filter?.GetValue(__instance) as List<string>;
                
                // 仅修改第一项占位符的 UI 显示
                if (ctrl != null && data != null && idx < data.Count && data[idx] == "-") {
                    ctrl.BindLabel(ExportBatchTools.ListLabel);
                }
            };
            listView.RefreshItems();
        }
    }
}