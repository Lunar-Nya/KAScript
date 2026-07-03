using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using KodakkuAssist.Script;
using KodakkuAssist.Module.GameEvent;

namespace Standalone.Kefka.PartyAutoSort
{
    [ScriptType(
        guid: "7f2bf2ed-6426-2462-8719-93271918838f",
        name: "绝妖星自动小队排序",
        territorys: [1363],
        version: "0.0.0.1",
        author: "yuemao3",
        note: "测试版。通过P1波动炮的站位自动调整可达鸭的小队排序，自适应固定半场和盗火烬攻略。不严格按攻略从左到右站位的情况会排列错误。"
    )]
    public class StatueWaveCannonAutoSort
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

        [ScriptMethod(name: "神像波动炮_自动排序", eventType: EventTypeEnum.ActionEffect, eventCondition: ["ActionId:47784"])]
        public void OnStatueWaveCannon(Event @event, ScriptAccessory accessory)
        {
            // 用来装准备发送的聊天文本的队列
            List<string> chatQueue = new List<string>();

            // 1. 进入锁，瞬间完成计算，防止多个波动炮同时触发
            lock (_syncLock)
            {
                var allMembers = accessory.Data.PartyList;
                if (allMembers == null || allMembers.Count != 8) return;

                var partyObjects = new List<KodakkuAssist.Data.IGameObject>();
                foreach (var id in allMembers)
                {
                    var obj = accessory.Data.Objects.SearchById(id);
                    var chara = obj as KodakkuAssist.Data.IBattleChara;
                    if (chara != null && !chara.IsDead) partyObjects.Add(obj);
                }

                if (partyObjects.Count != 8) return;

                var currentEntityIds = new HashSet<uint>(partyObjects.Select(p => p.EntityId));
                if (currentEntityIds.SetEquals(servicedPartyIds)) return;

                var sortedPlayers = partyObjects.OrderBy(p => p.Position.X).ToList();

                var boss = accessory.Data.Objects.FirstOrDefault(o => o != null && o.DataId == 19504);
                if (boss == null || boss.TargetObjectId == 0 || boss.TargetObjectId == 0xE0000000) return;

                int aggroTargetIndex = sortedPlayers.FindIndex(p => p.EntityId == boss.TargetObjectId);
                if (aggroTargetIndex == -1) return;

                uint[] newOrder = new uint[8];

                if (aggroTargetIndex < 4)
                {
                    newOrder[0] = sortedPlayers[3].EntityId; // MT
                    newOrder[1] = sortedPlayers[2].EntityId; // ST 
                    newOrder[2] = sortedPlayers[1].EntityId; // H1 
                    newOrder[3] = sortedPlayers[0].EntityId; // H2 
                    newOrder[4] = sortedPlayers[4].EntityId; // D1 
                    newOrder[5] = sortedPlayers[5].EntityId; // D2 
                    newOrder[6] = sortedPlayers[6].EntityId; // D3 
                    newOrder[7] = sortedPlayers[7].EntityId; // D4 
                }
                else
                {
                    newOrder[0] = sortedPlayers[4].EntityId; // MT
                    newOrder[1] = sortedPlayers[5].EntityId; // ST 
                    newOrder[2] = sortedPlayers[6].EntityId; // H1 
                    newOrder[3] = sortedPlayers[7].EntityId; // H2 
                    newOrder[4] = sortedPlayers[3].EntityId; // D1 
                    newOrder[5] = sortedPlayers[2].EntityId; // D2 
                    newOrder[6] = sortedPlayers[1].EntityId; // D3 
                    newOrder[7] = sortedPlayers[0].EntityId; // D4 
                }

                bool hasChanges = !allMembers.SequenceEqual(newOrder);

                servicedPartyIds = currentEntityIds;

                if (!hasChanges) return;

                try
                {
                    Type partyListType = Type.GetType("KodakkuAssist.Data.PartyList.PartyList, KodakkuAssist");
                    if (partyListType != null)
                    {
                        var memberListProp = partyListType.GetProperty("MemberList", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                        if (memberListProp != null)
                        {
                            // 执行内存注入
                            memberListProp.SetValue(null, newOrder.ToList());
                            
                            // 将所有变动文本塞进队列，但不立即发送
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
                                    var p = accessory.Data.Objects.SearchById(newPlayerId);
                                    string name = p?.Name?.ToString() ?? "未知";

                                    bool isHighRisk = false;
                                    if (newRole == "MT" && newPlayerId != boss.TargetObjectId) isHighRisk = true;
                                    if ((oldRole == "H1" && newRole == "H2") || (oldRole == "H2" && newRole == "H1")) isHighRisk = true;

                                    if (isHighRisk)
                                    {
                                        chatQueue.Add($"/e [高危] {name}: {oldRole} -> {newRole}");
                                    }
                                    else
                                    {
                                        chatQueue.Add($"/e {name}: {oldRole} -> {newRole}");
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    chatQueue.Add($"/e [自动排序] 报错：反射重写失败 {ex.Message}");
                }
            } // lock 结束，解锁其他线程

            // 2. 离开锁后，如果队列里有消息，启动一个后台小护士按顺序发消息
            if (chatQueue.Count > 0)
            {
                accessory.Method.TextInfo("可达鸭小队顺序有改动，详见聊天框。", 2000);

                _ = Task.Run(async () =>
                {
                    foreach (var msg in chatQueue)
                    {
                        accessory.Method.SendChat(msg);
                        // 150 毫秒的延时，防乱序
                        await Task.Delay(150); 
                    }
                });
            }
        }
    }
}