using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.Numerics;
using KodakkuAssist.Script;
using KodakkuAssist.Module.GameEvent;

namespace Standalone.FRU.PartyAutoSort
{
    [ScriptType(
        guid: "8e55b766-23d4-9e35-04d2-57c701543f30",
        name: "绝伊甸自动小队排序",
        territorys: [1238],
        version: "0.0.0.1",
        author: "yuemao3",
        note: "测试版。通过绝伊甸P1开场八方的站位自动调整可达鸭的小队排序，基于正北逆时针 MT D3 H1 D1 [H2/ST] D2 [ST/H2] D4 站位标准。自适应国服MMW站位和日服Game8站位。不严格按攻略站位的情况会排列错误。"
    )]
    public class FRUPartyAutoSort
    {
        private readonly object _syncLock = new object();
        private HashSet<uint> servicedPartyIds = new HashSet<uint>();

        public void Init(ScriptAccessory accessory)
        {
            lock (_syncLock)
            {
                servicedPartyIds.Clear();
            }
        }

        // 监听P1开场八方的ActionEffect判定。40144，40148
        [ScriptMethod(name: "P1_八方站位_自动排序", eventType: EventTypeEnum.ActionEffect, eventCondition: ["ActionId:regex:^(40144|40148)$"])]
        public void OnOpeningSpread(Event @event, ScriptAccessory accessory)
        {
            List<string> chatQueue = new List<string>();

            lock (_syncLock)
            {
                var allMembers = accessory.Data.PartyList;
                if (allMembers == null || allMembers.Count != 8) return;

                var partyObjects = new List<KodakkuAssist.Data.IGameObject>();
                foreach (var id in allMembers)
                {
                    // 针对 uint 短 ID 使用 SearchByEntityId
                    var obj = accessory.Data.Objects.SearchByEntityId(id);
                    var chara = obj as KodakkuAssist.Data.IBattleChara;
                    
                    // 追加 .IsValid() 以拦截内存野指针
                    if (chara != null && chara.IsValid() && !chara.IsDead) 
                    {
                        partyObjects.Add(obj);
                    }
                }

                if (partyObjects.Count != 8) return;

                var currentEntityIds = new HashSet<uint>(partyObjects.Select(p => p.EntityId));
                // 如果队伍没变且排过了就略过，保护玩家的手动调整
                if (currentEntityIds.SetEquals(servicedPartyIds)) return;

                var dirMap = new Dictionary<int, uint>();
                var center = new Vector3(100f, 0f, 100f);

                // 收集8人的相对方位
                foreach (var p in partyObjects)
                {
                    int dir = PositionTo8Dir(p.Position, center);
                    // 如果有人重叠在同一个方位（站错或者掉线躺地板），直接放弃修正
                    if (dirMap.ContainsKey(dir))
                    {
                        chatQueue.Add("/e [小队检查] 有人八方站位不准确，无法判断小队顺序。自动排序已失效！");
                        servicedPartyIds = currentEntityIds; 
                        goto EndLock;
                    }
                    dirMap[dir] = p.EntityId;
                }

                if (dirMap.Count != 8) goto EndLock;

                uint[] newOrder = new uint[8];
                
                // 标准八方推导：正北逆时针 MT D3 H1 D1 [H2/ST] D2 [ST/H2] D4
                // 方位代号 (顺时针)：0=北, 1=东北, 2=东, 3=东南, 4=南, 5=西南, 6=西, 7=西北
                newOrder[0] = dirMap[0]; // MT - 正北
                newOrder[2] = dirMap[6]; // H1 - 正西
                newOrder[4] = dirMap[5]; // D1 - 西南
                newOrder[5] = dirMap[3]; // D2 - 东南
                newOrder[6] = dirMap[7]; // D3 - 西北
                newOrder[7] = dirMap[1]; // D4 - 东北

                uint pEast = dirMap[2];
                uint pSouth = dirMap[4];

                // 从内存中获取正东和正南两名玩家的强类型实体
                var charaEast = accessory.Data.Objects.SearchByEntityId(pEast) as KodakkuAssist.Data.ICharacter;
                var charaSouth = accessory.Data.Objects.SearchByEntityId(pSouth) as KodakkuAssist.Data.ICharacter;

                // 提取职业 ID (RowId)
                uint jobEast = charaEast?.ClassJob.RowId ?? 0;
                uint jobSouth = charaSouth?.ClassJob.RowId ?? 0;

                // 坦克职业ID：19(骑士), 21(战士), 32(暗骑), 37(绝枪), 1(剑术师), 3(斧术师)
                bool isEastTank = jobEast == 19 || jobEast == 21 || jobEast == 32 || jobEast == 37 || jobEast == 1 || jobEast == 3;
                bool isSouthTank = jobSouth == 19 || jobSouth == 21 || jobSouth == 32 || jobSouth == 37 || jobSouth == 1 || jobSouth == 3;

                if (isEastTank)
                {
                    // 国服 MMW 站位：东边是坦克，说明东边是 ST，南边是 H2
                    newOrder[1] = pEast;  
                    newOrder[3] = pSouth; 
                }
                else if (isSouthTank)
                {
                    // 日服 Game8 站位：南边是坦克，说明南边是 ST，东边是 H2
                    newOrder[1] = pSouth; 
                    newOrder[3] = pEast;  
                }
                else
                {
                    // 东和南都没有坦克，说明小队配置非标，或站位发生严重混乱
                    chatQueue.Add("/e [小队检查] 警告：正东和正南方向均未检测到坦克。");
                    chatQueue.Add("/e [小队检查] 非标准小队配置或站位严重错误，自动排序已失效！");
                    servicedPartyIds = currentEntityIds; 
                    goto EndLock; 
                }

                bool hasChanges = !allMembers.SequenceEqual(newOrder);
                servicedPartyIds = currentEntityIds;

                if (!hasChanges) goto EndLock;

                try
                {
                    // 核心内存注入
                    Type partyListType = Type.GetType("KodakkuAssist.Data.PartyList.PartyList, KodakkuAssist");
                    if (partyListType != null)
                    {
                        var memberListProp = partyListType.GetProperty("MemberList", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                        if (memberListProp != null)
                        {
                            memberListProp.SetValue(null, newOrder.ToList());
                            
                            chatQueue.Add("/e [自动排序] 校准可达鸭小队顺序成功。变动详情如下：");

                            string[] roleNames = { "MT", "ST", "H1", "H2", "D1", "D2", "D3", "D4" };
                            for (int i = 0; i < 8; i++)
                            {
                                uint newPlayerId = newOrder[i];
                                int oldIndex = allMembers.IndexOf(newPlayerId);
                                
                                if (oldIndex != i)
                                {
                                    string oldRole = oldIndex >= 0 ? roleNames[oldIndex] : "未知";
                                    string newRole = roleNames[i];
                                    var p = accessory.Data.Objects.SearchByEntityId(newPlayerId);
                                    string name = p?.Name?.ToString() ?? "未知";

                                    // 如果被修正的是职能跨界的情况，标为高危
                                    bool isHighRisk = false;
                                    if ((oldRole.StartsWith("H") && newRole.StartsWith("D")) || 
                                        (oldRole.StartsWith("T") && newRole.StartsWith("D"))) 
                                        isHighRisk = true;

                                    if (isHighRisk)
                                        chatQueue.Add($"/e [高危变动] {name}: {oldRole} -> {newRole}");
                                    else
                                        chatQueue.Add($"/e {name}: {oldRole} -> {newRole}");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    chatQueue.Add($"/e [自动排序] 报错：反射重写失败 {ex.Message}");
                }

            EndLock:;
            } // lock 结束，解锁其他线程

            SendChatQueue(accessory, chatQueue);
        }

        /// <summary>
        /// 将坐标转换为0-7的八个方位 (0=北, 1=东北, 2=东...)
        /// </summary>
        private int PositionTo8Dir(Vector3 pos, Vector3 center)
        {
            // FFXIV 中，Z 轴向南为正，X 轴向东为正
            // Math.Atan2(y, x) 在此处分别代入 dx 和 -dz 进行翻转
            double angle = Math.Atan2(pos.X - center.X, center.Z - pos.Z);
            if (angle < 0) angle += 2 * Math.PI;
            return (int)Math.Round(angle / (Math.PI / 4)) % 8;
        }

        private void SendChatQueue(ScriptAccessory accessory, List<string> chatQueue)
        {
            if (chatQueue.Count > 0)
            {
                if (chatQueue.Any(msg => msg.Contains("成功") || msg.Contains("失效")))
                    accessory.Method.TextInfo("可达鸭小队排序已有改动，详见聊天框。", 3000);

                _ = Task.Run(async () =>
                {
                    foreach (var msg in chatQueue)
                    {
                        accessory.Method.SendChat(msg);
                        await Task.Delay(150); // 150 毫秒的延时，防乱序
                    }
                });
            }
        }
    }
}
