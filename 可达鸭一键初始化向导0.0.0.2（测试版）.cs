using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using KodakkuAssist.Script;
using KodakkuAssist.Module.GameEvent;

// 卫月底层绘制与核心通信依赖
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace KodakkuInitGuide
{
    [ScriptType(name: "可达鸭一键初始化向导", territorys: [], guid: "50fe62d3-88e1-3190-c4aa-9eddcb78d3e5", version: "0.0.0.2", author: "yuemao3", note: "测试版。实验性脚本，不保证长期可用。首次安装可达鸭的一键初始化配置脚本。可以按需分别一键调整语言、默认危险安全区的颜色、默认小队排序预设，自动添加在线库、配置网络代理，用完即自动卸载并删除。")]
    public class KodakkuInitWizard
    {
        private IDalamudPluginInterface _pi;
        private bool _isDrawing = false;
        private ScriptAccessory _saRef;

        // 状态体检标记
        private bool _isLanguageDone, _isColorDone, _isSortDone, _isCommonRepoDone, _isAllRepoDone, _isProxyDone;

        // 代理配置可视化参数 (默认 socks5://127.0.0.1:7890)
        private int _proxyProtocolIndex = 2;
        private string[] _proxyProtocols = new string[] { "http", "https", "socks5" };
        private string _proxyHost = "127.0.0.1";
        private int _proxyPort = 7890;

        // 推荐的可达鸭预设 23 职业排序 ID 映射
        private readonly List<uint> _cnSortOrder = new List<uint>() { 21, 32, 19, 37, 24, 33, 40, 28, 34, 20, 22, 30, 39, 41, 31, 38, 23, 25, 35, 27, 42, 36, 43 };

        // 预设在线库 (按作者字母自动排序维护)
        private readonly List<string> _commonRepos = new List<string> {
            "https://raw.githubusercontent.com/AdmiralLvtzov/CicerosKodakkuAssist/main/OnlineRepo.json",
            "https://raw.githubusercontent.com/Codaaaaaa/KodakkuScripts/main/OnlineRepo.json",
            "https://raw.githubusercontent.com/Cyf5119/KAScripts/main/OnlineRepo.json",
            "https://raw.githubusercontent.com/DueDine/KDrawScript/main/Repo.json",
            "https://raw.githubusercontent.com/Errerer/KodakkuAssistScript/main/OnlineRepo.json",
            "https://raw.githubusercontent.com/Karlin-Z/KodakkuAssistScript/main/OnlineRepo.json",
            "https://raw.githubusercontent.com/tsingsooAlpha/KodakkuAssistScripts/main/OnlineRepo.json",
            "https://raw.githubusercontent.com/Meedvast/KDA-Script/refs/heads/main/OnlineRepo.json",
            "https://raw.githubusercontent.com/a16239438/RyougiMio_KodakkuScripts/refs/heads/main/OnlineRepo.json",
            "https://raw.githubusercontent.com/Hibiya615/TetoraKAScript/main/OnlineRepo.json",
            "https://raw.githubusercontent.com/Haruna08t9/KodakkuScript/master/OnlineRepo.json",
            "https://raw.githubusercontent.com/lianying1997/UsamisKodakku/main/OnlineRepo.json",
            "https://raw.githubusercontent.com/VeeverSW/Kodakku-Script/main/OnlineRepo.json",
            "https://raw.githubusercontent.com/ShoOtaku/KodakkuAssist/refs/heads/main/OnlineRepo.json",
            "https://raw.githubusercontent.com/Lunar-Nya/KAScript/main/OnlineRepo.json"
        };

        private readonly List<string> _richRepos = new List<string> {
            "https://raw.githubusercontent.com/KurotsukiRuri/Drawing/refs/heads/main/OnlineRepo.json",
            "https://raw.githubusercontent.com/Baelixac/KodakkuFFXIV/refs/heads/master/OnlineRepo.json",
            "https://raw.githubusercontent.com/JiaXX7799/KodakkuAssist/refs/heads/master/OnlineRepo.json",
            "https://raw.githubusercontent.com/MilkVio/FFXIVTrigger/refs/heads/main/KodakkuAssistScript/OnlineRepo.json",
            "https://raw.githubusercontent.com/lr0452/KodakkuAssistScript/refs/heads/master/OnlineRepo.json",
            "https://raw.githubusercontent.com/Sonnet46/KodakkuScript/main/OnlineRepo.json",
            "https://raw.githubusercontent.com/Hibiya615/TetoraKAScript/main/TestScriptRepo.json",
            "https://raw.githubusercontent.com/kanyeishere/kodakku-script/refs/heads/master/ScriptMaster.json",
            "https://raw.githubusercontent.com/keaidell-cyber/MyFF14Scripts/refs/heads/main/OnlineRepo.json"
        };

        public void Init(ScriptAccessory sa)
        {
            _saRef = sa;
            try
            {
                var serviceType = typeof(ScriptAccessory).Assembly.GetType("KodakkuAssist.Data.Service");
                _pi = (IDalamudPluginInterface)serviceType?.GetProperty("PluginInterface", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);

                if (_pi != null && !_isDrawing)
                {
                    RefreshConfigStates(); // 启动时进行全局状态体检与数据同步
                    _pi.UiBuilder.Draw += OnDraw;
                    _isDrawing = true;
                    sa.Method.SendChat("/e [可达鸭初始化向导] 玩家设置检测完毕，UI已成功挂载。");
                }
            }
            catch (Exception ex)
            {
                sa.Method.SendChat($"/e [可达鸭向导报错] 启动失败: {ex.Message}");
            }
        }

        // ================= 全局状态体检机制 =================
        private void RefreshConfigStates()
        {
            Assembly assembly = typeof(ScriptAccessory).Assembly;

            // 1. 语言体检
            try {
                var langMgrType = assembly.GetType("KodakkuAssist.Module.Lang.LanguageManager");
                var langSettingObj = GetMemberValue(langMgrType, null, "LanguageSetting");
                if (langSettingObj != null)
                    _isLanguageDone = (GetMemberValue(langSettingObj.GetType(), langSettingObj, "DisplayLanguage") as string) == "ChineseSimplified";
            } catch { }

            // 2. 颜色体检
            try {
                var drawMgrType = assembly.GetType("KodakkuAssist.Module.Draw.Manager.DrawManager");
                var drawSetting = GetMemberValue(drawMgrType, null, "DrawSetting");
                if (drawSetting != null)
                {
                    var safe = (Vector3)GetMemberValue(drawSetting.GetType(), drawSetting, "DefaultSafeColor");
                    var danger = (Vector3)GetMemberValue(drawSetting.GetType(), drawSetting, "DefaultDangerColor");
                    _isColorDone = (safe == new Vector3(0, 1, 0) && danger == new Vector3(1, 0, 0));
                }
            } catch { }

            // 3. 排序体检
            try {
                var partyListType = assembly.GetType("KodakkuAssist.Data.PartyList.PartyList");
                var partySetting = GetMemberValue(partyListType, null, "PartyListSetting");
                if (partySetting != null)
                {
                    var sortedList = GetMemberValue(partySetting.GetType(), partySetting, "ClassJobSorted") as List<uint>;
                    _isSortDone = sortedList != null && sortedList.SequenceEqual(_cnSortOrder);
                }
            } catch { }

            // 4. 仓库体检
            try {
                var scriptMgrType = assembly.GetType("KodakkuAssist.Module.Script.ScriptManager");
                var scriptSetting = GetMemberValue(scriptMgrType, null, "ScriptSetting");
                if (scriptSetting != null)
                {
                    var repoList = GetMemberValue(scriptSetting.GetType(), scriptSetting, "OnlineRepo") as List<string> ?? new List<string>();
                    _isCommonRepoDone = _commonRepos.All(repoList.Contains);
                    _isAllRepoDone = _isCommonRepoDone && _richRepos.All(repoList.Contains);
                }
            } catch { }

            // 5. 代理体检与同步
            try {
                var userProxyType = assembly.GetType("KodakkuAssist.Module.IPC.UserProxy");
                var proxySetting = GetMemberValue(userProxyType, null, "ProxySetting");
                if (proxySetting != null)
                {
                    bool useProxy = (bool)GetMemberValue(proxySetting.GetType(), proxySetting, "useManualProxy");
                    string host = GetMemberValue(proxySetting.GetType(), proxySetting, "proxyHost") as string;
                    int port = (int)GetMemberValue(proxySetting.GetType(), proxySetting, "proxyPort");
                    string proto = GetMemberValue(proxySetting.GetType(), proxySetting, "proxyProtocol") as string;

                    // 判断依据：只要手动代理开启且主机地址不为空
                    _isProxyDone = useProxy && !string.IsNullOrWhiteSpace(host);

                    // 如果底层已有已保存的有效配置，自动同步至向导输入框
                    if (useProxy && !string.IsNullOrWhiteSpace(host))
                    {
                        _proxyHost = host;
                        _proxyPort = port > 0 ? port : 7890;
                        if (!string.IsNullOrWhiteSpace(proto))
                        {
                            int idx = Array.IndexOf(_proxyProtocols, proto.ToLower().Trim());
                            if (idx >= 0) _proxyProtocolIndex = idx;
                        }
                    }
                }
            } catch { }
        }

        private void OnDraw()
        {
            if (!_isDrawing || _pi == null) return;

            bool open = true;
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);

            // 自适应窗口
            if (ImGui.Begin("可达鸭初始化向导", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextColored(new Vector4(0.2f, 0.8f, 1f, 1f), "状态已校准，可以点击补齐所需设置，关闭本UI后向导脚本自动卸载并删除：");
                ImGui.Separator();
                ImGui.Spacing();

                ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(4f, 6f));

                if (ImGui.BeginTable("##WizardTable", 3, ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("Info", ImGuiTableColumnFlags.WidthFixed);
                    ImGui.TableSetupColumn("Inputs", ImGuiTableColumnFlags.WidthFixed);
                    ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed);

                    // ================= 1. 语言设置 =================
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding(); 
                    RenderCheckItem("1. 语言环境", "将可达鸭语言修改为为简体中文", _isLanguageDone ? 2 : 0);
                    
                    ImGui.TableNextColumn(); 
                    ImGui.TableNextColumn(); 
                    if (!_isLanguageDone)
                    {
                        if (ImGui.Button("应用简体中文##lang")) ExecuteReflectionConfig("Language");
                    }

                    // ================= 2. 颜色设置 =================
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    RenderCheckItem("2. 预警颜色", "将危险区设为正红，安全区设为正绿", _isColorDone ? 2 : 0);
                    
                    ImGui.TableNextColumn(); 
                    ImGui.TableNextColumn();
                    if (!_isColorDone)
                    {
                        if (ImGui.Button("应用红色绿色##color")) ExecuteReflectionConfig("Color");
                    }

                    // ================= 3. 职业排序 =================
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    RenderCheckItem("3. 小队排序", "应用推荐排序预设 (T H1 H2 近战 远敏 法系)", _isSortDone ? 2 : 0);
                    
                    ImGui.TableNextColumn(); 
                    ImGui.TableNextColumn();
                    if (!_isSortDone)
                    {
                        if (ImGui.Button("应用推荐预设##sort")) ExecuteReflectionConfig("Sort");
                    }

                    // ================= 4. 在线库链 =================
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    int repoState = _isAllRepoDone ? 2 : (_isCommonRepoDone ? 1 : 0);
                    RenderCheckItem("4. 在线库链", "自动检测并补充缺失的在线仓库", repoState);
                    
                    ImGui.TableNextColumn(); 
                    if (repoState == 0) 
                    {
                        string btnStr = "仅常用在线库";
                        float btnWidth = ImGui.CalcTextSize(btnStr).X + ImGui.GetStyle().FramePadding.X * 2.0f;
                        float rightAlignX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - btnWidth - ImGui.GetStyle().ItemSpacing.X;
                        ImGui.SetCursorPosX(rightAlignX);
                        
                        if (ImGui.Button($"{btnStr}##commonrepo")) ExecuteReflectionConfig("CommonRepo");
                    }
                    
                    ImGui.TableNextColumn(); 
                    if (repoState < 2)
                    {
                        if (ImGui.Button("丰富的在线库##allrepo")) ExecuteReflectionConfig("AllRepo");
                    }

                    // ================= 5. 网络代理 =================
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    RenderCheckItem("5. 网络代理", "进行网络代理配置", _isProxyDone ? 2 : 0);
                    
                    ImGui.TableNextColumn(); 
                    float frameHeight = ImGui.GetFrameHeight();
                    
                    ImGui.SetNextItemWidth(ImGui.CalcTextSize("socks5").X + frameHeight * 1.5f); 
                    ImGui.Combo("##proto", ref _proxyProtocolIndex, _proxyProtocols, _proxyProtocols.Length);
                    
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(ImGui.CalcTextSize("127.0.0.1222").X + frameHeight);
                    ImGui.InputText("##host", ref _proxyHost, 100);
                    
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(ImGui.CalcTextSize("78900").X + frameHeight);
                    ImGui.InputInt("##port", ref _proxyPort, 0, 0);

                    ImGui.TableNextColumn(); 
                    if (ImGui.Button(_isProxyDone ? "重新配置代理##proxy" : "配置网络代理##proxy")) ExecuteReflectionConfig("Proxy");

                    ImGui.EndTable();
                }

                ImGui.PopStyleVar();

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                if (ImGui.Button("关闭此窗口并自动卸载和删除本脚本", new Vector2(-1, 0)))
                {
                    open = false; 
                }
            }
            ImGui.End();
            ImGui.PopStyleVar();

            if (!open) CloseAndUninstall();
        }

        // 根据 0(未完成)/1(中间态)/2(完成) 渲染图标状态
        private void RenderCheckItem(string title, string desc, int state)
        {
            if (state == 2) ImGui.TextColored(new Vector4(0f, 1f, 0f, 1f), "[✓]");
            else if (state == 1) ImGui.TextColored(new Vector4(1f, 1f, 0f, 1f), "[○]");
            else ImGui.TextColored(new Vector4(1f, 0f, 0f, 1f), "[×]");
            
            ImGui.SameLine();
            ImGui.Text(title);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(desc);
        }

        // ================== 反射读写辅助器 ==================
        private object GetMemberValue(Type type, object instance, string name)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var prop = type.GetProperty(name, flags);
            if (prop != null) return prop.GetValue(instance);
            var field = type.GetField(name, flags);
            if (field != null) return field.GetValue(instance);
            return null;
        }

        private void SetMemberValue(Type type, object instance, string name, object value)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var prop = type.GetProperty(name, flags);
            if (prop != null && prop.CanWrite) { prop.SetValue(instance, value); return; }
            var field = type.GetField(name, flags);
            if (field != null) { field.SetValue(instance, value); return; }
        }
        // ======================================================

        private void ExecuteReflectionConfig(string targetSetting)
        {
            Assembly assembly = typeof(ScriptAccessory).Assembly;

            try
            {
                switch (targetSetting)
                {
                    case "Language":
                        Type langMgrType = assembly.GetType("KodakkuAssist.Module.Lang.LanguageManager");
                        langMgrType?.GetMethod("SwitchLanguage", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                   ?.Invoke(null, new object[] { "ChineseSimplified" });
                        
                        object langSettingObj = GetMemberValue(langMgrType, null, "LanguageSetting");
                        if (langSettingObj != null)
                        {
                            SetMemberValue(langSettingObj.GetType(), langSettingObj, "DisplayLanguage", "ChineseSimplified");
                            langSettingObj.GetType().GetMethod("SaveConfig", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(langSettingObj, null);
                        }
                        _saRef.Method.SendChat("/e [可达鸭初始化向导] 语言已切换为简体中文。");
                        break;

                    case "Color":
                        Type drawMgrType = assembly.GetType("KodakkuAssist.Module.Draw.Manager.DrawManager");
                        object drawSetting = GetMemberValue(drawMgrType, null, "DrawSetting");
                        if (drawSetting != null)
                        {
                            SetMemberValue(drawSetting.GetType(), drawSetting, "DefaultSafeColor", new Vector3(0, 1, 0));
                            SetMemberValue(drawSetting.GetType(), drawSetting, "DefaultDangerColor", new Vector3(1, 0, 0));
                            _saRef.Method.SendChat("/e [可达鸭初始化向导] 安全/危险颜色已校准。");
                        }
                        break;

                    case "Sort":
                        Type partyListType = assembly.GetType("KodakkuAssist.Data.PartyList.PartyList");
                        object partySetting = GetMemberValue(partyListType, null, "PartyListSetting");
                        if (partySetting != null)
                        {
                            SetMemberValue(partySetting.GetType(), partySetting, "ClassJobSorted", _cnSortOrder.ToList());
                            partyListType?.GetMethod("TryRefreshPartyList", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, new object[] { true });
                            _saRef.Method.SendChat("/e [可达鸭初始化向导] 可达鸭小队排序预设已覆写为推荐预设。");
                        }
                        break;

                    case "CommonRepo":
                    case "AllRepo":
                        Type scriptMgrType = assembly.GetType("KodakkuAssist.Module.Script.ScriptManager");
                        object scriptSetting = GetMemberValue(scriptMgrType, null, "ScriptSetting");
                        if (scriptSetting != null)
                        {
                            var repoList = GetMemberValue(scriptSetting.GetType(), scriptSetting, "OnlineRepo") as List<string>;
                            if (repoList != null)
                            {
                                var targets = targetSetting == "AllRepo" ? _commonRepos.Concat(_richRepos) : _commonRepos;
                                foreach (var repo in targets)
                                {
                                    if (!repoList.Contains(repo)) repoList.Add(repo);
                                }
                                _saRef.Method.SendChat($"/e [可达鸭初始化向导] 在线库自动跳过已有项补充添加完毕。");
                                scriptMgrType?.GetMethod("ReloadAll", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, null);
                            }
                        }
                        break;

                    case "Proxy":
                        Type userProxyType = assembly.GetType("KodakkuAssist.Module.IPC.UserProxy");
                        object proxySetting = GetMemberValue(userProxyType, null, "ProxySetting");
                        if (proxySetting != null)
                        {
                            SetMemberValue(proxySetting.GetType(), proxySetting, "useManualProxy", true);
                            SetMemberValue(proxySetting.GetType(), proxySetting, "proxyProtocol", _proxyProtocols[_proxyProtocolIndex]);
                            SetMemberValue(proxySetting.GetType(), proxySetting, "proxyHost", _proxyHost);
                            SetMemberValue(proxySetting.GetType(), proxySetting, "proxyPort", _proxyPort);

                            SetMemberValue(userProxyType, null, "proxyStatus", "待测试");

                            _saRef.Method.SendChat($"/e [可达鸭初始化向导] 网络代理已配置。");
                        }
                        break;
                }

                // 触发底层持久化写入
                Type userSettingBase = assembly.GetType("KodakkuAssist.Interface.UserSettingBase");
                userSettingBase?.GetMethod("RequestSaveAll", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, null);
                // 操作完成后立即重新体检并刷新 UI
                RefreshConfigStates();
            }
            catch (Exception e)
            {
                _saRef.Log.Error($"配置执行异常: {e.Message}");
            }
        }

        private void CloseAndUninstall()
        {
            if (_isDrawing && _pi != null)
            {
                _pi.UiBuilder.Draw -= OnDraw;
                _isDrawing = false;
                Task.Run(() => { SelfDestruct(); });
            }
        }

        private void SelfDestruct()
        {
            try
            {
                Assembly assembly = typeof(ScriptAccessory).Assembly;
                Type scriptMgrType = assembly.GetType("KodakkuAssist.Module.Script.ScriptManager");
                if (scriptMgrType == null) return;

                var scriptList = GetMemberValue(scriptMgrType, null, "ScriptList") as System.Collections.IEnumerable;
                object myInstance = null;
                string filePath = null;

                if (scriptList != null)
                {
                    foreach (var inst in scriptList)
                    {
                        var guid = GetMemberValue(inst.GetType(), inst, "Guid") as string;
                        if (guid == "50fe62d3-88e1-3190-c4aa-9eddcb78d3e5")
                        {
                            myInstance = inst;
                            filePath = GetMemberValue(inst.GetType(), inst, "FilePath") as string;
                            break;
                        }
                    }
                }
                
                if (myInstance != null)
                {
                    // 先发送消息，再剥离实例并删除文件
                    _saRef.Method.SendChat("/e [可达鸭初始化向导] UI已关闭，初始化向导已卸载，向导脚本已删除。");
                    
                    scriptMgrType.GetMethod("Remove", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(null, new object[] { myInstance });
                    
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
            }
            catch (Exception e)
            {
                _saRef.Log.Error($"自毁失败详情: {e.ToString()}");
            }
        }
    }
}