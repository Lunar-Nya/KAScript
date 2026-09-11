using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.Numerics;
using Newtonsoft.Json;
using KodakkuAssist.Script;
using KodakkuAssist.Module.GameEvent;

namespace Yuemao3.DSR.PartyAutoSort
{
    [ScriptType(
        guid: "d4e21a88-7f5b-4392-a160-58c9735d4f21",
        name: "绝龙诗P2螺旋刺自动小队排序",
        territorys: [968],
        version: "0.0.0.1",
        author: "yuemao3",
        note: "测试版。通过绝龙诗P2一运螺旋刺击退分散站位自动纠正可达鸭内部小队排序。\n依据标准站位：MT组固定偏南/西半区、ST组固定偏北/东半区；中轴内侧H1/H2、外侧MT/ST、面朝场心左D1/D2、右D3/D4。\n不严格按攻略站位会排列错误。"
    )]
    public class DSRPartyAutoSort
    {
        // ==================== 用户设置项 ====================
        public enum SortTriggerMode
        {
            进本仅校准一次,
            每次战斗重开均重新校准
        }

        [UserSetting("小队校准模式")]
        public SortTriggerMode TriggerMode { get; set; } = SortTriggerMode.进本仅校准一次;
        // ====================================================

        private readonly object _syncLock = new object();
        private HashSet<uint> servicedPartyIds = new HashSet<uint>();

        private readonly bool[] _blockedAxes = new bool[4];
        private int _g1Dir8 = -1;

        public void Init(ScriptAccessory accessory)
        {
            lock (_syncLock)
            {
                ResetThrustRecord();
                if (TriggerMode == SortTriggerMode.每次战斗重开均重新校准)
                {
                    servicedPartyIds.Clear();
                }
            }
        }

        private void ResetThrustRecord()
        {
            for (int i = 0; i < 4; i++) _blockedAxes[i] = false;
            _g1Dir8 = -1;
        }

        [ScriptMethod(name: "P2一运读条重置", eventType: EventTypeEnum.StartCasting, eventCondition: ["ActionId:25555"], userControl: false)]
        public void OnStrengthCast(Event @event, ScriptAccessory accessory)
        {
            lock (_syncLock)
            {
                ResetThrustRecord();
            }
        }

        [ScriptMethod(name: "P2一运冲锋方位记录", eventType: EventTypeEnum.NpcYell, eventCondition: ["Id:regex:^(378[123])$"], userControl: false)]
        public void OnThrustKnightYell(Event @event, ScriptAccessory accessory)
        {
            lock (_syncLock)
            {
                try
                {
                    Vector3 sourcePos = @event.SourcePosition;
                    if (sourcePos == Vector3.Zero && !string.IsNullOrEmpty(@event["SourcePosition"]))
                    {
                        sourcePos = JsonConvert.DeserializeObject<Vector3>(@event["SourcePosition"]);
                    }

                    int dir8 = PositionTo8Dir(sourcePos, new Vector3(100f, 0f, 100f));
                    _blockedAxes[dir8 % 4] = true;

                    int blockedCount = _blockedAxes.Count(b => b);
                    if (blockedCount == 3)
                    {
                        int safeAxis = Array.IndexOf(_blockedAxes, false);
                        // ST组去北/东，MT组去南/西
                        _g1Dir8 = safeAxis + 4;
                    }
                }
                catch
                {
                }
            }
        }

        [ScriptMethod(name: "P2螺旋刺分散_自动排序", eventType: EventTypeEnum.ActionEffect, eventCondition: ["ActionId:25556"], suppress: 5000)]
        public void OnSpiralThrustHit(Event @event, ScriptAccessory accessory)
        {
            List<string> chatQueue = new List<string>();

            lock (_syncLock)
            {
                var allMembers = accessory.Data.PartyList;
                if (allMembers == null || allMembers.Count != 8) return;

                var partyObjects = new List<KodakkuAssist.Data.IGameObject>();
                foreach (var id in allMembers)
                {
                    var obj = accessory.Data.Objects.SearchByEntityId(id);
                    var chara = obj as KodakkuAssist.Data.IBattleChara;
                    if (chara != null && chara.IsValid() && !chara.IsDead)
                    {
                        partyObjects.Add(obj);
                    }
                }

                if (partyObjects.Count != 8) return;

                var currentEntityIds = new HashSet<uint>(partyObjects.Select(p => p.EntityId));
                if (currentEntityIds.SetEquals(servicedPartyIds)) return;

                Vector3 center = new Vector3(100f, 0f, 100f);

                int g1Dir = _g1Dir8;
                if (g1Dir == -1)
                {
                    g1Dir = DeduceG1DirFromPlayers(partyObjects, center);
                    if (g1Dir == -1)
                    {
                        chatQueue.Add("/e [小队检查] 无法判定螺旋刺安全区朝向，自动排序失效。");
                        servicedPartyIds = currentEntityIds;
                        goto EndLock;
                    }
                }

                float radG1 = g1Dir * (MathF.PI / 4f);
                float radG2 = ((g1Dir + 4) % 8) * (MathF.PI / 4f);

                var g1Candidates = new List<PlayerSpreadInfo>();
                var g2Candidates = new List<PlayerSpreadInfo>();

                foreach (var p in partyObjects)
                {
                    float distG1 = CalcRadialDistance(p.Position, center, radG1);
                    float distG2 = CalcRadialDistance(p.Position, center, radG2);

                    if (distG1 > distG2)
                    {
                        float lat = CalcLateralOffset(p.Position, center, radG1);
                        g1Candidates.Add(new PlayerSpreadInfo(p.EntityId, distG1, lat, p.Position));
                    }
                    else
                    {
                        float lat = CalcLateralOffset(p.Position, center, radG2);
                        g2Candidates.Add(new PlayerSpreadInfo(p.EntityId, distG2, lat, p.Position));
                    }
                }

                if (g1Candidates.Count != 4 || g2Candidates.Count != 4)
                {
                    chatQueue.Add("/e [小队检查] 警告：螺旋刺分组人数异常（非4/4分组），自动排序失效。");
                    servicedPartyIds = currentEntityIds;
                    goto EndLock;
                }

                uint[] newOrder = new uint[8];

                if (!ResolveGroupOrder(g1Candidates, out uint mt, out uint h1, out uint d1, out uint d3) ||
                    !ResolveGroupOrder(g2Candidates, out uint st, out uint h2, out uint d2, out uint d4))
                {
                    chatQueue.Add("/e [小队检查] 警告：部分队员站位距离或左右分布不规范，无法准确判定小队顺序。");
                    servicedPartyIds = currentEntityIds;
                    goto EndLock;
                }

                newOrder[0] = mt;
                newOrder[1] = st;
                newOrder[2] = h1;
                newOrder[3] = h2;
                newOrder[4] = d1;
                newOrder[5] = d2;
                newOrder[6] = d3;
                newOrder[7] = d4;

                if (!ValidateJobRoles(accessory, newOrder, out string roleWarn))
                {
                    chatQueue.Add($"/e [小队检查] 警告：{roleWarn}，自动纠偏终止！");
                    servicedPartyIds = currentEntityIds;
                    goto EndLock;
                }

                bool hasChanges = !allMembers.SequenceEqual(newOrder);
                servicedPartyIds = currentEntityIds;

                if (!hasChanges) goto EndLock;

                try
                {
                    Type partyListType = Type.GetType("KodakkuAssist.Data.PartyList.PartyList, KodakkuAssist")
                                      ?? typeof(ScriptAccessory).Assembly.GetType("KodakkuAssist.Data.PartyList.PartyList");

                    if (partyListType != null)
                    {
                        var memberListProp = partyListType.GetProperty("MemberList", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                        if (memberListProp != null)
                        {
                            memberListProp.SetValue(null, newOrder.ToList());
                            chatQueue.Add("/e [自动排序] 绝龙诗小队顺序已根据P2螺旋刺站位校准成功：");

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

                                    bool isHighRisk = (oldRole.StartsWith("H") && newRole.StartsWith("D")) ||
                                                      (oldRole.StartsWith("T") && newRole.StartsWith("D")) ||
                                                      (oldRole.StartsWith("D") && !newRole.StartsWith("D"));

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
                    chatQueue.Add($"/e [自动排序] 报错：反射更新失败 {ex.Message}");
                }

            EndLock:;
            }

            SendChatQueue(accessory, chatQueue);
        }

        private class PlayerSpreadInfo
        {
            public uint EntityId { get; }
            public float RadialDist { get; }
            public float LateralOffset { get; }
            public Vector3 Position { get; }

            public PlayerSpreadInfo(uint id, float dist, float lat, Vector3 pos)
            {
                EntityId = id;
                RadialDist = dist;
                LateralOffset = lat;
                Position = pos;
            }
        }

        private bool ResolveGroupOrder(List<PlayerSpreadInfo> group, out uint tank, out uint healer, out uint dpsLeft, out uint dpsRight)
        {
            tank = healer = dpsLeft = dpsRight = 0;

            var healerCandidate = group.OrderBy(p => p.RadialDist).First();
            if (healerCandidate.RadialDist > 15f) return false;

            uint healerId = healerCandidate.EntityId;
            healer = healerId;

            var outerGroup = group.Where(p => p.EntityId != healerId).ToList();

            var tankCandidate = outerGroup.OrderBy(p => Math.Abs(p.LateralOffset)).First();
            if (Math.Abs(tankCandidate.LateralOffset) > 4.5f || tankCandidate.RadialDist < 16f) return false;

            uint tankId = tankCandidate.EntityId;
            tank = tankId;

            var dpsGroup = outerGroup.Where(p => p.EntityId != tankId).ToList();
            var leftCandidate = dpsGroup.OrderByDescending(p => p.LateralOffset).First();
            var rightCandidate = dpsGroup.OrderBy(p => p.LateralOffset).First();

            if (leftCandidate.LateralOffset < 1.5f || rightCandidate.LateralOffset > -1.5f) return false;

            dpsLeft = leftCandidate.EntityId;
            dpsRight = rightCandidate.EntityId;
            return true;
        }

        private bool ValidateJobRoles(ScriptAccessory accessory, uint[] newOrder, out string warnMessage)
        {
            warnMessage = string.Empty;

            var charaMT = accessory.Data.Objects.SearchByEntityId(newOrder[0]) as KodakkuAssist.Data.ICharacter;
            var charaST = accessory.Data.Objects.SearchByEntityId(newOrder[1]) as KodakkuAssist.Data.ICharacter;
            var charaH1 = accessory.Data.Objects.SearchByEntityId(newOrder[2]) as KodakkuAssist.Data.ICharacter;
            var charaH2 = accessory.Data.Objects.SearchByEntityId(newOrder[3]) as KodakkuAssist.Data.ICharacter;

            if (!IsTank(charaMT?.ClassJob.RowId ?? 0) || !IsTank(charaST?.ClassJob.RowId ?? 0))
            {
                warnMessage = "中轴外侧未正确检测到双T";
                return false;
            }

            if (!IsHealer(charaH1?.ClassJob.RowId ?? 0) || !IsHealer(charaH2?.ClassJob.RowId ?? 0))
            {
                warnMessage = "中轴内侧未正确检测到双奶";
                return false;
            }

            for (int i = 4; i < 8; i++)
            {
                var charaD = accessory.Data.Objects.SearchByEntityId(newOrder[i]) as KodakkuAssist.Data.ICharacter;
                if (IsTank(charaD?.ClassJob.RowId ?? 0) || IsHealer(charaD?.ClassJob.RowId ?? 0))
                {
                    warnMessage = "侧翼分散位置检测到非DPS职业";
                    return false;
                }
            }

            return true;
        }

        private bool IsTank(uint jobId) => jobId is 1 or 3 or 19 or 21 or 32 or 37;
        private bool IsHealer(uint jobId) => jobId is 6 or 24 or 28 or 33 or 40;

        private float CalcRadialDistance(Vector3 pos, Vector3 center, float rad)
        {
            float dx = pos.X - center.X;
            float dz = pos.Z - center.Z;
            return dx * MathF.Sin(rad) - dz * MathF.Cos(rad);
        }

        private float CalcLateralOffset(Vector3 pos, Vector3 center, float rad)
        {
            float dx = pos.X - center.X;
            float dz = pos.Z - center.Z;
            return dx * MathF.Cos(rad) + dz * MathF.Sin(rad);
        }

        private int DeduceG1DirFromPlayers(List<KodakkuAssist.Data.IGameObject> party, Vector3 center)
        {
            int[] dirCounts = new int[8];
            foreach (var p in party)
            {
                int d = PositionTo8Dir(p.Position, center);
                dirCounts[d]++;
            }

            int bestAxis = -1;
            int maxScore = -1;
            for (int axis = 0; axis < 4; axis++)
            {
                int score = dirCounts[axis] + dirCounts[axis + 4];
                if (score > maxScore)
                {
                    maxScore = score;
                    bestAxis = axis;
                }
            }

            if (bestAxis == -1) return -1;
            return bestAxis + 4;
        }

        private int PositionTo8Dir(Vector3 pos, Vector3 center)
        {
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
                        await Task.Delay(150);
                    }
                });
            }
        }
    }
}