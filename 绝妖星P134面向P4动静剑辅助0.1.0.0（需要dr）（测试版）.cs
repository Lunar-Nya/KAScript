using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Numerics;
using KodakkuAssist.Script;
using KodakkuAssist.Module.GameEvent;
using KodakkuAssist.Data;
using KodakkuAssist.Extensions;
using KodakkuAssist.Module.Draw;
using KodakkuAssist.Module.Draw.Manager;
using KodakkuAssist.Module.GameEvent.Struct;
using KodakkuAssist.Module.GameEvent.Types;
using KodakkuAssist.Module.GameOperate;
using Newtonsoft.Json;

namespace KodakkuAssist.Community.FRU_AutoFaceAssist
{
    [ScriptType(name: "绝妖星P134面向P4动静剑辅助", territorys: [1363], guid: "d4e5f6a1-c2b3-49d8-90ef-1a2b3c4d5e6f", version: "0.1.0.0", note: null, author: "yuemao3")]
    public class FRU_AutoFaceCombinedAssist
    {
        [UserSetting("【全局】开启测试日志输出")] public bool EnableDebugLog { get; set; } = false;
        [UserSetting("【全局】面向模块提前加载时间(秒)")] public float PreloadAdvance { get; set; } = 0.5f;
        [UserSetting("【P4 真炸弹】自动开关AE目标选择器（用AE插件要开）)")] public bool EnableAeCompatibility { get; set; } = true;

        [UserSetting("【P1】启用 众神之像3 自动面向")] public bool EnableP1AutoFace { get; set; } = true;
        [UserSetting("【P1】玄乎乎魔法读条(共4.7s)多少秒后开启强制面向")] public float P1LoadDelay { get; set; } = 4.0f;
        [UserSetting("【P1】强制面向状态维持多少秒")] public float P1RestoreDelay { get; set; } = 1.7f;

        [UserSetting("【P3】启用 真空波自动面向")] public bool EnableP3AutoFace { get; set; } = true;
        [UserSetting("【P3】真空波读条(共7.7s)多少秒后开启强制面向")] public float P3StartDelay { get; set; } = 6.7f;
        [UserSetting("【P3】真空波判定后再多少秒后关闭强制面向")] public float P3EndDelay { get; set; } = 0.5f;

        [UserSetting("【P4 石化】启用自动面向")] public bool EnableP4Petrify { get; set; } = true;
        [UserSetting("【P4 石化】介入时机 (Debuff结束前X秒)")] public float P4PetrifyAdvance { get; set; } = 1.2f;
        [UserSetting("【P4 石化】面向维持时间 (秒)")] public float P4PetrifyDuration { get; set; } = 1.5f;

        [UserSetting("【P4 真炸弹】启用自动禁锢与沉默")] public bool EnableP4AccelBombTrue { get; set; } = true;
        [UserSetting("【P4 真炸弹】禁锢介入时机 (Debuff结束前X秒)")] public float P4RootAdvance { get; set; } = 1.2f;
        [UserSetting("【P4 真炸弹】禁锢维持时间 (秒)")] public float P4RootDuration { get; set; } = 1.5f;

        [UserSetting("【P4 伪炸弹】启用自动跳跃")] public bool EnableP4AccelBombFalse { get; set; } = true;
        [UserSetting("【P4 伪炸弹】跳跃介入时机 (Debuff结束前X秒)")] public float P4JumpAdvance { get; set; } = 0.6f;

        private volatile float p1_safeDirection = float.NaN; 
        private volatile int _runId = 0;
        private volatile bool _isLocking = false;
        
        private volatile bool _p4IsFakeWindow = false; 
        private long _p4WindowExpiresAt = 0; 
        
        private long _p4PetrifyScheduledTime = 0;

        #region IPC 核心通信与缓存区

        private object _drSub1, _drSub2, _drSub3, _drSub4, _drSub6;
        private MethodInfo _drInv1, _drInv2, _drInv3, _drInv4, _drInv6;
        private object _drSubIsMod, _drSubLoad, _drSubUnload;
        private MethodInfo _drInvIsMod, _drInvLoad, _drInvUnload;

        private void InitDRIpc()
        {
            try 
            {
                if (_drSub1 != null && _drSubLoad != null) return;
                var pi = typeof(ScriptAccessory).Assembly.GetType("KodakkuAssist.Data.Service")?.GetProperty("PluginInterface", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                if (pi == null) return;
                
                var methods = pi.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(x => x.Name == "GetIpcSubscriber").ToList();
                var m1 = methods.FirstOrDefault(x => x.GetGenericArguments().Length == 1);
                var m2 = methods.FirstOrDefault(x => x.GetGenericArguments().Length == 2);
                var m3 = methods.FirstOrDefault(x => x.GetGenericArguments().Length == 3);
                var m4 = methods.FirstOrDefault(x => x.GetGenericArguments().Length == 4);

                if (m1 == null || m2 == null || m3 == null || m4 == null) return;

                _drSubIsMod = m2.MakeGenericMethod(typeof(string), typeof(bool?)).Invoke(pi, new object[] { "DailyRoutines.IsModuleEnabled" });
                _drInvIsMod = _drSubIsMod?.GetType().GetMethod("InvokeFunc", new[] { typeof(string) });
                _drSubLoad = m3.MakeGenericMethod(typeof(string), typeof(bool), typeof(bool)).Invoke(pi, new object[] { "DailyRoutines.LoadModule" });
                _drInvLoad = _drSubLoad?.GetType().GetMethod("InvokeFunc", new[] { typeof(string), typeof(bool) });
                _drSubUnload = m4.MakeGenericMethod(typeof(string), typeof(bool), typeof(bool), typeof(bool)).Invoke(pi, new object[] { "DailyRoutines.UnloadModule" });
                _drInvUnload = _drSubUnload?.GetType().GetMethod("InvokeFunc", new[] { typeof(string), typeof(bool), typeof(bool) });

                _drSub1 = m2.MakeGenericMethod(typeof(float), typeof(object)).Invoke(pi, new object[] { "DailyRoutines.Modules.AutoFaceCameraDirection.LockOnChara" });
                _drInv1 = _drSub1?.GetType().GetMethod("InvokeAction", new[] { typeof(float) });
                _drSub2 = m1.MakeGenericMethod(typeof(object)).Invoke(pi, new object[] { "DailyRoutines.Modules.AutoFaceCameraDirection.CancelLockOn" });
                _drInv2 = _drSub2?.GetType().GetMethod("InvokeAction", Type.EmptyTypes);
                _drSub3 = m2.MakeGenericMethod(typeof(bool), typeof(object)).Invoke(pi, new object[] { "DailyRoutines.Modules.ForceRootAndSilence.SetRootAndSilence" });
                _drInv3 = _drSub3?.GetType().GetMethod("InvokeAction", new[] { typeof(bool) });
                _drSub4 = m1.MakeGenericMethod(typeof(object)).Invoke(pi, new object[] { "DailyRoutines.Modules.ForceRootAndSilence.TriggerJump" });
                _drInv4 = _drSub4?.GetType().GetMethod("InvokeAction", Type.EmptyTypes);
                _drSub6 = m2.MakeGenericMethod(typeof(bool), typeof(object)).Invoke(pi, new object[] { "DailyRoutines.Modules.ForceRootAndSilence.SetAutoFaceTarget" });
                _drInv6 = _drSub6?.GetType().GetMethod("InvokeAction", new[] { typeof(bool) });
            } 
            catch { }
        }

        private bool DR_IsModuleEnabled(string moduleName)
        {
            try {
                if (_drSubIsMod == null) InitDRIpc();
                var res = (bool?)_drInvIsMod?.Invoke(_drSubIsMod, new object[] { moduleName });
                return res == true;
            } catch { return false; }
        }

        private void DR_LoadModule(string moduleName)
        {
            try { if (_drSubLoad == null) InitDRIpc(); _drInvLoad?.Invoke(_drSubLoad, new object[] { moduleName, false }); } catch { }
        }

        private void DR_UnloadModule(string moduleName)
        {
            try { if (_drSubUnload == null) InitDRIpc(); _drInvUnload?.Invoke(_drSubUnload, new object[] { moduleName, false, true }); } catch { }
        }

        private async Task<bool> EnsureModuleLoaded(ScriptAccessory sa, string moduleName, int currentRunId)
        {
            if (DR_IsModuleEnabled(moduleName)) return true;
            DR_LoadModule(moduleName);
            for (int i = 0; i < 20; i++) {
                if (currentRunId != _runId) return false;
                if (DR_IsModuleEnabled(moduleName)) return true;
                await Task.Delay(50);
            }
            LogDebug(sa, $"[测试报错] DR 模块 {moduleName} 加载超时！");
            return false;
        }

        private void DR_LockOnChara(ScriptAccessory sa, float r) { 
            try { if (!DR_IsModuleEnabled("AutoFaceCameraDirection")) return; if (_drSub1 == null) InitDRIpc(); _drInv1?.Invoke(_drSub1, new object[] { r }); } catch { } 
        }
        
        private void DR_CancelLockOn(ScriptAccessory sa) { 
            try { if (!DR_IsModuleEnabled("AutoFaceCameraDirection")) return; if (_drSub2 == null) InitDRIpc(); _drInv2?.Invoke(_drSub2, null); } catch { } 
        }
        
        private void DR_SetRootAndSilence(ScriptAccessory sa, bool s) { 
            try { if (!DR_IsModuleEnabled("ForceRootAndSilence")) return; if (_drSub3 == null) InitDRIpc(); _drInv3?.Invoke(_drSub3, new object[] { s }); } catch { } 
        }
        
        private void DR_TriggerJump(ScriptAccessory sa) { 
            try { if (!DR_IsModuleEnabled("ForceRootAndSilence")) return; if (_drSub4 == null) InitDRIpc(); _drInv4?.Invoke(_drSub4, null); } catch { } 
        }
        
        private void DR_SetAutoFaceTarget(ScriptAccessory sa, bool s) { 
            try { if (!DR_IsModuleEnabled("ForceRootAndSilence")) return; if (_drSub6 == null) InitDRIpc(); _drInv6?.Invoke(_drSub6, new object[] { s }); } catch { } 
        }

        #endregion

        private void LogDebug(ScriptAccessory sa, string message)
        {
            if (EnableDebugLog)
            {
                sa.Method.SendChat($"/e [测试日志] {message}");
            }
        }

        private async Task SafeRestoreAutoFace(ScriptAccessory sa)
        {
            LogDebug(sa, "执行角色发动技能时转身设置的安全还原(多重冗余)");
            for (int i = 0; i < 3; i++)
            {
                DR_SetAutoFaceTarget(sa, true);
                await Task.Delay(50);
            }
        }

        public void Init(ScriptAccessory accessory)
        {
            p1_safeDirection = float.NaN; 
            _runId++;
            _isLocking = false;
            _p4IsFakeWindow = false; 
            _p4WindowExpiresAt = 0; 
            _p4PetrifyScheduledTime = 0;
            
            _drSub1 = _drSub2 = _drSub3 = _drSub4 = _drSub6 = null;
            _drSubIsMod = _drSubLoad = _drSubUnload = null;
            InitDRIpc();
            
            DR_CancelLockOn(accessory);
            DR_SetRootAndSilence(accessory, false);
            DR_UnloadModule("AutoFaceCameraDirection");

            int currentRunId = _runId;
            Task.Run(async () => {
                DR_LoadModule("ForceRootAndSilence");
                await Task.Delay(200);
                if (currentRunId == _runId)
                {
                    await SafeRestoreAutoFace(accessory);
                }
            });
            
            LogDebug(accessory, "脚本已初始化，状态已重置。");
        }

        private float CalcBossTrackingRot(ScriptAccessory sa, uint bossId)
        {
            var myObj = sa.Data.MyObject;
            var boss = sa.Data.Objects.SearchById(bossId) ?? sa.Data.Objects.FirstOrDefault(o => o.DataId == 19510 || o.DataId == 19507);
            if (myObj == null || boss == null) return 0.001f;

            var rot = MathF.Atan2(boss.Position.X - myObj.Position.X, boss.Position.Z - myObj.Position.Z);
            if (rot > MathF.PI) rot -= 2 * MathF.PI;
            if (MathF.Abs(rot) < 0.001f) rot = 0.001f;
            return rot;
        }

        private float NormalizeRot(float rot)
        {
            if (rot > MathF.PI) rot -= 2 * MathF.PI;
            if (MathF.Abs(rot) < 0.001f) rot = 0.001f;
            return rot;
        }
        
        private string FormatAngle(float rad)
        {
            float degree = rad * 180f / MathF.PI;
            return $"{rad:F3}弧度 (约 {degree:F0}°)";
        }

        [ScriptMethod(name: "P1 面向记录", eventType: EventTypeEnum.ObjectEffect, eventCondition: ["Id1:64"])]
        public void P1_AutoFace_DirectionRecord(Event @event, ScriptAccessory accessory)
        {
            if (!EnableP1AutoFace || !string.Equals(@event["Id2"], "128")) return;
            try { 
                var pos = JsonConvert.DeserializeObject<Vector3>(@event["SourcePosition"]); 
                if (Vector3.Distance(pos, new Vector3(105.25f, 13.5f, 34)) < 1) p1_safeDirection = 0.001f;
                else if (Vector3.Distance(pos, new Vector3(95, 12.5f, 25)) < 1) p1_safeDirection = 3.142f;
                
                if (!float.IsNaN(p1_safeDirection))
                {
                    LogDebug(accessory, $"P1 记录到安全面朝角度: {FormatAngle(p1_safeDirection)}");
                }
            } catch { }
        }

        [ScriptMethod(name: "P1 面向执行", eventType: EventTypeEnum.StartCasting, eventCondition: ["ActionId:47764"])]
        public async void P1_AutoFace_Execute(Event @event, ScriptAccessory accessory)
        {
            if (!EnableP1AutoFace || float.IsNaN(p1_safeDirection)) return;
            float dir = p1_safeDirection;
            p1_safeDirection = float.NaN; 

            int currentRunId = _runId;
            uint bossId = (uint)@event.SourceId;
            
            int totalDelay = (int)(P1LoadDelay * 1000);
            int preloadMs = (int)(PreloadAdvance * 1000);
            int firstWait = Math.Max(0, totalDelay - preloadMs);
            int secondWait = totalDelay - firstWait;
            
            if (firstWait > 0) await Task.Delay(firstWait);
            if (currentRunId != _runId) return;

            long loadStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!await EnsureModuleLoaded(accessory, "AutoFaceCameraDirection", currentRunId)) return;
            if (!await EnsureModuleLoaded(accessory, "ForceRootAndSilence", currentRunId)) return;
            DR_SetAutoFaceTarget(accessory, false); 
            
            int elapsed = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - loadStart);
            int actualSecondWait = Math.Max(0, secondWait - elapsed);

            float initSoftRot = CalcBossTrackingRot(accessory, bossId);
            LogDebug(accessory, $"P1 预加载完成。进入 {actualSecondWait}ms 软锁定，快照计算面向Boss角度: {FormatAngle(initSoftRot)}");

            if (actualSecondWait > 0)
            {
                int preLoops = actualSecondWait / 100;
                for (int i = 0; i < preLoops; i++) {
                    if (currentRunId != _runId) return;
                    DR_LockOnChara(accessory, CalcBossTrackingRot(accessory, bossId)); 
                    await Task.Delay(100);
                }
                int rem = actualSecondWait % 100;
                if (rem > 0) {
                    DR_LockOnChara(accessory, CalcBossTrackingRot(accessory, bossId));
                    await Task.Delay(rem);
                }
            }
            if (currentRunId != _runId) return;
            
            try {
                LogDebug(accessory, $"P1 机制判定点到达！瞬间硬切至安全角度: {FormatAngle(dir)}");
                int loops = (int)(P1RestoreDelay * 10);
                for (int i = 0; i < loops; i++) {
                    if (currentRunId != _runId) break;
                    DR_LockOnChara(accessory, dir); 
                    await Task.Delay(100);
                }
            } finally {
                DR_CancelLockOn(accessory);
                DR_UnloadModule("AutoFaceCameraDirection");
                await SafeRestoreAutoFace(accessory);
            }
        }

        [ScriptMethod(name: "P3 辅助启动", eventType: EventTypeEnum.StartCasting, eventCondition: ["ActionId:regex:^(47891)$"])]
        public async void VacuumWave_StartAssist(Event ev, ScriptAccessory sa)
        {
            if (!EnableP3AutoFace) return;
            var currentRunId = _runId;
            uint bossId = (uint)ev.SourceId;
            
            int totalDelay = (int)(P3StartDelay * 1000);
            int preloadMs = (int)(PreloadAdvance * 1000);
            int firstWait = Math.Max(0, totalDelay - preloadMs);
            int secondWait = totalDelay - firstWait;

            var myObj = sa.Data.MyObject;
            if (myObj == null) return;
            bool wind = myObj.HasStatusAny(new uint[] { 1602 });
            bool revWind = myObj.HasStatusAny(new uint[] { 1603 });
            if (!wind && !revWind) return;

            LogDebug(sa, $"P3 真空波启动，检测到玩家Buff: {(wind ? "【混沌之风】(需背对Boss防击退)" : "【混沌之暗】(需面朝Boss防击退)")}");
            
            if (firstWait > 0) await Task.Delay(firstWait);
            if (currentRunId != _runId) return;

            long loadStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!await EnsureModuleLoaded(sa, "AutoFaceCameraDirection", currentRunId)) return;
            if (!await EnsureModuleLoaded(sa, "ForceRootAndSilence", currentRunId)) return;
            
            DR_SetAutoFaceTarget(sa, false); 
            
            int elapsed = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - loadStart);
            int actualSecondWait = Math.Max(0, secondWait - elapsed);

            float initSoftRot = CalcBossTrackingRot(sa, bossId);
            LogDebug(sa, $"P3 进入缓冲软锁定，持续 {actualSecondWait}ms。快照计算面向Boss角度: {FormatAngle(initSoftRot)}");

            if (actualSecondWait > 0)
            {
                int preLoops = actualSecondWait / 100;
                for (int i = 0; i < preLoops; i++) {
                    if (currentRunId != _runId) return;
                    DR_LockOnChara(sa, CalcBossTrackingRot(sa, bossId));
                    await Task.Delay(100);
                }
                int rem = actualSecondWait % 100;
                if (rem > 0) {
                    DR_LockOnChara(sa, CalcBossTrackingRot(sa, bossId));
                    await Task.Delay(rem);
                }
            }
            if (currentRunId != _runId) return;

            float initHardRot = CalcBossTrackingRot(sa, bossId); 
            if (wind) initHardRot = NormalizeRot(initHardRot + MathF.PI); 

            LogDebug(sa, $"P3 判定点到达！执行硬锁定强制切入机制安全区，目标锁定角度: {FormatAngle(initHardRot)}");
            
            try {
                _isLocking = true;
                for (int i = 0; i < 30 && _isLocking && currentRunId == _runId; i++)
                {
                    float targetRot = CalcBossTrackingRot(sa, bossId); 
                    if (wind) targetRot = NormalizeRot(targetRot + MathF.PI); 
                    
                    DR_LockOnChara(sa, targetRot);
                    await Task.Delay(100);
                }
            } finally {
                LogDebug(sa, "P3 面向机制执行完毕或被中断，执行安全释放。");
                DR_CancelLockOn(sa);
                DR_UnloadModule("AutoFaceCameraDirection");
                await SafeRestoreAutoFace(sa);
                _isLocking = false;
            }
        }

        [ScriptMethod(name: "P3 辅助结束", eventType: EventTypeEnum.ActionEffect, eventCondition: ["ActionId:regex:^(47891)$"])]
        public async void VacuumWave_EndAssist(Event ev, ScriptAccessory sa)
        {
            if (!EnableP3AutoFace) return;
            await Task.Delay((int)(P3EndDelay * 1000));
            _isLocking = false; 
        }

        [ScriptMethod(name: "P4 真伪窗口", eventType: EventTypeEnum.StatusAdd, eventCondition: ["StatusID:2056"])]
        public void P4_TruthWindowCapture(Event @event, ScriptAccessory accessory)
        {
            if (!int.TryParse(@event["Param"], out var param)) return;

            if (param != 1121 && param != 1122) return;

            uint dataId = accessory.Data.Objects.SearchById((uint)@event.TargetId)?.DataId ?? 0;
            if (dataId != 0 && dataId != 19510) return; 

            _p4IsFakeWindow = (param == 1121);
            _p4WindowExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 12000;
            
            string windowType = _p4IsFakeWindow ? "【伪 (反转)】-> 需停手/背对" : "【真 (正常)】-> 需动/面朝";
            LogDebug(accessory, $"P4 捕获艾克斯迪司大十字，参数: {param}，判定逻辑: {windowType}");
        }

        [ScriptMethod(name: "P4 机制执行", eventType: EventTypeEnum.StatusAdd, eventCondition: ["StatusID:regex:^(5543|5546)$"])]
        public void P4_DebuffExecute(Event @event, ScriptAccessory sa)
        {
            bool isMe = @event.TargetId == sa.Data.Me;
            if (@event["StatusID"] == "5546" && !isMe) return;
            if (!float.TryParse(@event["DurationMilliseconds"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dur)) return;

            bool isTrue = !(_p4IsFakeWindow && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < _p4WindowExpiresAt);

            string debuffName = @event["StatusID"] == "5543" ? "【石化眼】" : "【加速度炸弹】";
            string actionDetail = "";
            if (@event["StatusID"] == "5543") {
                actionDetail = isTrue ? "(全员需背对带眼者)" : "(全员需直视带眼者)";
            } else {
                actionDetail = isTrue ? "(自己需静止)" : "(自己需移动)";
            }
            LogDebug(sa, $"P4结算 {debuffName} -> 当前窗口判定为: {(isTrue ? "真(正常)" : "伪(反转)")} {actionDetail}");

            if (@event["StatusID"] == "5543" && EnableP4Petrify)
            {
                long exp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)dur;
                if (Math.Abs(exp - _p4PetrifyScheduledTime) < 2000) return; 
                _p4PetrifyScheduledTime = exp;
                _ = HandleP4PetrifyAsync(sa, isTrue, (int)dur, _runId, (uint)@event.SourceId);
            }
            else if (@event["StatusID"] == "5546" && isMe)
            {
                if (isTrue && EnableP4AccelBombTrue) _ = HandleP4AccelBombRootAsync(sa, (int)dur, _runId);
                else if (!isTrue && EnableP4AccelBombFalse) _ = HandleP4AccelBombJumpAsync(sa, (int)dur, _runId);
            }
        }

        private float AngleDiff(float a, float b)
        {
            float d = (a - b) % (2 * MathF.PI);
            if (d > MathF.PI) d -= 2 * MathF.PI;
            if (d < -MathF.PI) d += 2 * MathF.PI;
            return MathF.Abs(d);
        }

        private float CalcP4PetrifySafeRot(ScriptAccessory sa, bool isTrue, uint bossId)
        {
            var myObj = sa.Data.MyObject;
            if (myObj == null) return 0.001f;

            var players = sa.Data.Objects.OfType<IBattleChara>().Where(o => o.HasStatusAny(new uint[] { 5543 })).ToList();
            if (players.Count > 0) players.RemoveAll(o => o.EntityId == myObj.EntityId);

            float targetRot = CalcBossTrackingRot(sa, bossId);

            if (players.Count > 0)
            {
                float sumX = 0, sumZ = 0;
                var lookAtAngles = new List<float>();
                
                foreach (var p in players) {
                    float dx = p.Position.X - myObj.Position.X;
                    float dz = p.Position.Z - myObj.Position.Z;
                    lookAtAngles.Add(MathF.Atan2(dx, dz)); 
                    sumX += dx;
                    sumZ += dz;
                }

                float avgLookAt = MathF.Atan2(sumX, sumZ); 
                float avgLookAway = avgLookAt + MathF.PI;  

                float safeMargin = 50f * MathF.PI / 180f; 
                bool isSafe = true;

                if (isTrue) {
                    foreach (var lookAt in lookAtAngles) {
                        if (AngleDiff(targetRot, lookAt) < safeMargin) { isSafe = false; break; }
                    }
                    if (!isSafe) targetRot = avgLookAway; 
                } else {
                    foreach (var lookAt in lookAtAngles) {
                        if (AngleDiff(targetRot, lookAt) > safeMargin) { isSafe = false; break; }
                    }
                    if (!isSafe) targetRot = avgLookAt; 
                }
            }

            return NormalizeRot(targetRot);
        }

        private async Task HandleP4PetrifyAsync(ScriptAccessory sa, bool isTrue, int dur, int runId, uint bossId)
        {
            long targetTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + dur;
            long preloadTime = targetTime - (int)(P4PetrifyAdvance * 1000) - (int)(PreloadAdvance * 1000);
            long lockTime = targetTime - (int)(P4PetrifyAdvance * 1000);
            long releaseTime = lockTime + (long)(P4PetrifyDuration * 1000);

            while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < preloadTime)
            {
                if (runId != _runId || sa.Data.MyObject == null) return;
                await Task.Delay(50);
            }

            if (!await EnsureModuleLoaded(sa, "AutoFaceCameraDirection", runId)) return;
            if (!await EnsureModuleLoaded(sa, "ForceRootAndSilence", runId)) return;
            DR_SetAutoFaceTarget(sa, false);
            
            float initSoftRot = CalcBossTrackingRot(sa, bossId);
            LogDebug(sa, $"P4 石化眼缓冲期：进入软锁定，快照计算面向Boss角度: {FormatAngle(initSoftRot)}");

            while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < lockTime)
            {
                if (runId != _runId || sa.Data.MyObject == null) return;
                DR_LockOnChara(sa, CalcBossTrackingRot(sa, bossId));
                await Task.Delay(50);
            }

            float initHardRot = CalcP4PetrifySafeRot(sa, isTrue, bossId);
            
            try 
            {
                LogDebug(sa, $"P4 石化眼触发：硬切面向以避让或直视队友！计算安全目标角度: {FormatAngle(initHardRot)}");
                _isLocking = true;
                
                while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < releaseTime && _isLocking) 
                {
                    if (runId != _runId || sa.Data.MyObject == null) break;
                    DR_LockOnChara(sa, CalcP4PetrifySafeRot(sa, isTrue, bossId)); 
                    await Task.Delay(50);
                }
            } 
            finally 
            {
                LogDebug(sa, "P4 石化眼结束，恢复自由。");
                DR_CancelLockOn(sa);
                DR_UnloadModule("AutoFaceCameraDirection");
                await SafeRestoreAutoFace(sa);
                _isLocking = false;
            }
        }

        private async Task HandleP4AccelBombRootAsync(ScriptAccessory sa, int dur, int runId)
        {
            long targetTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + dur;
            long aeOffTime = targetTime - 4000; 
            long rootTime = targetTime - (int)(P4RootAdvance * 1000); 

            bool hasDisabledAe = false;

            try
            {
                if (EnableAeCompatibility)
                {
                    while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < aeOffTime)
                    {
                        if (runId != _runId || sa.Data.MyObject?.HasStatusAny(new uint[] { 5546 }) != true) return;
                        await Task.Delay(50);
                    }

                    LogDebug(sa, "准备停手：触发安全锁，发送 /aeTargetSelector off");
                    sa.Method.SendChat("/aeTargetSelector off");
                    hasDisabledAe = true; 
                }

                while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < rootTime)
                {
                    if (runId != _runId || sa.Data.MyObject?.HasStatusAny(new uint[] { 5546 }) != true) return;
                    await Task.Delay(50);
                }

                if (!await EnsureModuleLoaded(sa, "ForceRootAndSilence", runId)) return;

                LogDebug(sa, $"P4 真炸弹：已开启强制禁锢与沉默，持续 {P4RootDuration} 秒！");
                long rootEndTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (int)(P4RootDuration * 1000);
                
                while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < rootEndTime)
                {
                    if (runId != _runId || sa.Data.MyObject?.HasStatusAny(new uint[] { 5546 }) != true) break;
                    DR_SetRootAndSilence(sa, true);
                    await Task.Delay(50);
                }
            }
            finally
            {
                LogDebug(sa, "P4 真炸弹：禁锢与沉默已解除。");
                DR_SetRootAndSilence(sa, false);

                if (hasDisabledAe)
                {
                    LogDebug(sa, "恢复输出：开始异步恢复 AE 目标选择器");
                    _ = Task.Run(async () => 
                    {
                        sa.Method.SendChat("/aeTargetSelector on");
                        await Task.Delay(1000);
                        sa.Method.SendChat("/aeTargetSelector on");
                    });
                }
            }
        }

        private async Task HandleP4AccelBombJumpAsync(ScriptAccessory sa, int dur, int runId)
        {
            long targetTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + dur;
            long jumpStartTime = targetTime - (int)(P4JumpAdvance * 1000);
            long jumpEndTime = jumpStartTime + (long)(P4JumpAdvance * 1000) + 200; 

            while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < jumpStartTime)
            {
                if (runId != _runId || sa.Data.MyObject?.HasStatusAny(new uint[] { 5546 }) != true) return;
                await Task.Delay(50);
            }

            if (!await EnsureModuleLoaded(sa, "ForceRootAndSilence", runId)) return;

            LogDebug(sa, $"P4 伪炸弹：进入跳跃判定防死窗口。");

            while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < jumpEndTime)
            {
                if (runId != _runId || sa.Data.MyObject?.HasStatusAny(new uint[] { 5546 }) != true) break;
                
                if (!sa.Data.MyObject.IsCasting)
                {
                    DR_TriggerJump(sa);
                }
                await Task.Delay(100); 
            }
        }
    }
}