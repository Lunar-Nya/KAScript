using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Numerics;
using System.Collections.Concurrent; 
using System.Threading;              

using KodakkuAssist.Script;
using KodakkuAssist.Module.GameEvent;
using KodakkuAssist.Data;
using KodakkuAssist.Extensions;
using KodakkuAssist.Module.Draw;
using KodakkuAssist.Module.Draw.Manager;
using KodakkuAssist.Module.GameEvent.Struct;
using KodakkuAssist.Module.GameEvent.Types;
using KodakkuAssist.Module.GameOperate;

namespace KodakkuAssist.Community.DMU_AutoFaceAssist
{
    [ScriptType(name: "绝妖星P134面向P4动静剑辅助", territorys: [1363], guid: "d4e5f6a1-c2b3-49d8-90ef-1a2b3c4d5e6f", version: "0.1.3.1", note: "萌新第一次写脚本。测试版，包含P1P3面向，及P4真伪石化、真伪炸弹自动处理，可能会电。类似功能的脚本尽量只开一个。\n依赖于DailyRoutines插件中的自动面向摄像机方向（在线验证模块）和禁止主动移动与释放技能（自制本地模块）。\n想要稳定使用的话请麻烦去可达鸭dc示例和分享中[月猫三]个人脚本分享里看下使用说明。", author: "yuemao3")]
    public class DMU_AutoFaceCombinedAssist
    {
        [UserSetting("【全局】开启测试日志输出")] public bool EnableDebugLog { get; set; } = false;
        [UserSetting("【全局】面向模块提前加载时间(秒)")] public float PreloadAdvance { get; set; } = 0.5f;
        [UserSetting("【P4 真炸弹】自动开关AE目标选择器（用AE插件要开）")] public bool EnableAeCompatibility { get; set; } = true;
        [UserSetting("【P3】真空波期间自动切换合适的目标")] public bool EnableP3AutoTarget { get; set; } = false;

        [UserSetting("【P1】启用 众神之像3 自动面向")] public bool EnableP1AutoFace { get; set; } = true;
        [UserSetting("【P1】玄乎乎魔法读条(共4.7s)多少秒后开启强制面向")] public float P1LoadDelay { get; set; } = 4.0f;
        [UserSetting("【P1】强制面向状态维持多少秒")] public float P1RestoreDelay { get; set; } = 1.7f;

        [UserSetting("【P3】启用 真空波自动面向")] public bool EnableP3AutoFace { get; set; } = true;
        [UserSetting("【P3】真空波读条(共7.7s)多少秒后开启强制面向")] public float P3StartDelay { get; set; } = 6.7f;
        [UserSetting("【P3】真空波判定后再多少秒后关闭强制面向")] public float P3EndDelay { get; set; } = 2.5f;

        [UserSetting("【P4 石化】启用自动面向")] public bool EnableP4Petrify { get; set; } = true;
        [UserSetting("【P4 石化】介入时机 (Debuff结束前X秒)")] public float P4PetrifyAdvance { get; set; } = 1.2f;
        [UserSetting("【P4 石化】面向维持时间 (秒)")] public float P4PetrifyDuration { get; set; } = 2.5f;

        [UserSetting("【P4 真炸弹】启用自动禁锢与沉默")] public bool EnableP4AccelBombTrue { get; set; } = true;
        [UserSetting("【P4 真炸弹】禁锢介入时机 (Debuff结束前X秒)")] public float P4RootAdvance { get; set; } = 1.2f;
        [UserSetting("【P4 真炸弹】禁锢维持时间 (秒)")] public float P4RootDuration { get; set; } = 2.5f;

        [UserSetting("【P4 伪炸弹】启用自动跳跃")] public bool EnableP4AccelBombFalse { get; set; } = true;
        [UserSetting("【P4 伪炸弹】跳跃介入时机 (Debuff结束前X秒)")] public float P4JumpAdvance { get; set; } = 0.6f;

        private volatile float p1_safeDirection = float.NaN; 
        private int _runId = 0; 
        private volatile bool _isLocking = false;
        
        private volatile bool _p4IsFakeWindow = false; 
        private long _p4WindowExpiresAt = 0; 
        private long _p4PetrifyScheduledTime = 0;
        
        private ConcurrentDictionary<ulong, long> _petrifyExpirations = new ConcurrentDictionary<ulong, long>(); 

        private const int MaxNullRetries = 10;
        
        private const float DEG_30_RAD = 30f * MathF.PI / 180f;
        private const float DEG_90_RAD = 90f * MathF.PI / 180f;
        
        private enum PlayerState { Valid, Retry, Abort }

        #region IPC 核心通信与缓存区
        private object _drSub1, _drSub2, _drSub3, _drSub4, _drSub5;
        private MethodInfo _drInv1, _drInv2, _drInv3, _drInv4, _drInv5;
        private object _drSubIsMod, _drSubLoad, _drSubUnload;
        private MethodInfo _drInvIsMod, _drInvLoad, _drInvUnload;

        private Func<string, bool?> _drFuncIsMod;
        private Func<string, bool, bool> _drFuncLoad;
        private Func<string, bool, bool, bool> _drFuncUnload;
        
        private Action<float> _drActionLockOn;
        private Action _drActionCancelLockOn;
        private Action<bool> _drActionSetRoot;
        private Action _drActionTriggerJump;
        private Action<bool> _drActionSetAutoFace;

        private void ResetDRIpc()
        {
            _drSubIsMod = _drSubLoad = _drSubUnload = null;
            _drSub1 = _drSub2 = _drSub3 = _drSub4 = _drSub5 = null;
            _drInvIsMod = _drInvLoad = _drInvUnload = null;
            _drInv1 = _drInv2 = _drInv3 = _drInv4 = _drInv5 = null;
            
            _drFuncIsMod = null;
            _drFuncLoad = null;
            _drFuncUnload = null;
            _drActionLockOn = null;
            _drActionCancelLockOn = null;
            _drActionSetRoot = null;
            _drActionTriggerJump = null;
            _drActionSetAutoFace = null;
        }

        private void InitDRIpc(ScriptAccessory sa)
        {
            try 
            {
                if (_drSub1 != null && _drSubLoad != null) return;
                var pi = typeof(ScriptAccessory).Assembly.GetType("KodakkuAssist.Data.Service")?.GetProperty("PluginInterface", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                if (pi == null) 
                {
                    sa.Method.SendChat("/e [绝妖星面向辅助 报错] 依赖缺失：请打开DR并进行在线验证，且确保加载了配套本地模块。");
                    return;
                }
                
                var methods = pi.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(x => x.Name == "GetIpcSubscriber").ToList();
                var m1 = methods.FirstOrDefault(x => x.GetGenericArguments().Length == 1);
                var m2 = methods.FirstOrDefault(x => x.GetGenericArguments().Length == 2);
                var m3 = methods.FirstOrDefault(x => x.GetGenericArguments().Length == 3);
                var m4 = methods.FirstOrDefault(x => x.GetGenericArguments().Length == 4);

                if (m1 == null || m2 == null || m3 == null || m4 == null) 
                {
                    sa.Method.SendChat("/e [绝妖星面向辅助 报错] 依赖缺失：请打开DR并进行在线验证，且确保加载了配套本地模块。");
                    return;
                }

                _drSubIsMod = m2.MakeGenericMethod(typeof(string), typeof(bool?)).Invoke(pi, new object[] { "DailyRoutines.IsModuleEnabled" });
                _drInvIsMod = _drSubIsMod?.GetType().GetMethod("InvokeFunc", new[] { typeof(string) });
                if (_drInvIsMod != null) _drFuncIsMod = (Func<string, bool?>)Delegate.CreateDelegate(typeof(Func<string, bool?>), _drSubIsMod, _drInvIsMod);

                _drSubLoad = m3.MakeGenericMethod(typeof(string), typeof(bool), typeof(bool)).Invoke(pi, new object[] { "DailyRoutines.LoadModule" });
                _drInvLoad = _drSubLoad?.GetType().GetMethod("InvokeFunc", new[] { typeof(string), typeof(bool) });
                if (_drInvLoad != null) _drFuncLoad = (Func<string, bool, bool>)Delegate.CreateDelegate(typeof(Func<string, bool, bool>), _drSubLoad, _drInvLoad);

                _drSubUnload = m4.MakeGenericMethod(typeof(string), typeof(bool), typeof(bool), typeof(bool)).Invoke(pi, new object[] { "DailyRoutines.UnloadModule" });
                _drInvUnload = _drSubUnload?.GetType().GetMethod("InvokeFunc", new[] { typeof(string), typeof(bool), typeof(bool) });
                if (_drInvUnload != null) _drFuncUnload = (Func<string, bool, bool, bool>)Delegate.CreateDelegate(typeof(Func<string, bool, bool, bool>), _drSubUnload, _drInvUnload);

                _drSub1 = m2.MakeGenericMethod(typeof(float), typeof(object)).Invoke(pi, new object[] { "DailyRoutines.Modules.AutoFaceCameraDirection.LockOnChara" });
                _drInv1 = _drSub1?.GetType().GetMethod("InvokeAction", new[] { typeof(float) });
                if (_drInv1 != null) _drActionLockOn = (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), _drSub1, _drInv1);

                _drSub2 = m1.MakeGenericMethod(typeof(object)).Invoke(pi, new object[] { "DailyRoutines.Modules.AutoFaceCameraDirection.CancelLockOn" });
                _drInv2 = _drSub2?.GetType().GetMethod("InvokeAction", Type.EmptyTypes);
                if (_drInv2 != null) _drActionCancelLockOn = (Action)Delegate.CreateDelegate(typeof(Action), _drSub2, _drInv2);

                _drSub3 = m2.MakeGenericMethod(typeof(bool), typeof(object)).Invoke(pi, new object[] { "DailyRoutines.Modules.ForceRootAndSilence.SetRootAndSilence" });
                _drInv3 = _drSub3?.GetType().GetMethod("InvokeAction", new[] { typeof(bool) });
                if (_drInv3 != null) _drActionSetRoot = (Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), _drSub3, _drInv3);

                _drSub4 = m1.MakeGenericMethod(typeof(object)).Invoke(pi, new object[] { "DailyRoutines.Modules.ForceRootAndSilence.TriggerJump" });
                _drInv4 = _drSub4?.GetType().GetMethod("InvokeAction", Type.EmptyTypes);
                if (_drInv4 != null) _drActionTriggerJump = (Action)Delegate.CreateDelegate(typeof(Action), _drSub4, _drInv4);

                _drSub5 = m2.MakeGenericMethod(typeof(bool), typeof(object)).Invoke(pi, new object[] { "DailyRoutines.Modules.ForceRootAndSilence.SetAutoFaceTarget" });
                _drInv5 = _drSub5?.GetType().GetMethod("InvokeAction", new[] { typeof(bool) });
                if (_drInv5 != null) _drActionSetAutoFace = (Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), _drSub5, _drInv5);

                if (_drFuncIsMod == null || _drFuncLoad == null || _drFuncUnload == null ||
                    _drActionLockOn == null || _drActionCancelLockOn == null || _drActionSetRoot == null ||
                    _drActionTriggerJump == null || _drActionSetAutoFace == null)
                {
                    sa.Method.SendChat("/e [绝妖星面向辅助 报错] 委托绑定失败！若稳定触发请联系作者适配DR最新版的改动。");
                    return;
                }

                try 
                {
                    _drFuncIsMod.Invoke("PingTest_CheckIfAlive");
                }
                catch 
                {
                    sa.Method.SendChat("/e [绝妖星面向辅助 报错] 未检测到DR运行！请打开DR并完成在线验证，且确保加载了配套本地模块。");
                    ResetDRIpc();
                    return;
                }
            } 
            catch (Exception ex)
            {
                sa.Method.SendChat($"/e [绝妖星面向辅助 报错] 初始化异常：{ex.Message}");
            }
        }

        private bool DR_IsModuleEnabled(ScriptAccessory sa, string moduleName)
        {
            try {
                if (_drFuncIsMod == null) InitDRIpc(sa);
                var res = _drFuncIsMod?.Invoke(moduleName);
                return res == true;
            } catch { 
                LogDebug(sa, () => "[防御性重连] 检测到 DR 接口失效，尝试重新绑定...");
                ResetDRIpc();
                try { InitDRIpc(sa); return _drFuncIsMod?.Invoke(moduleName) == true; } catch { return false; }
            }
        }

        private void DR_LoadModule(ScriptAccessory sa, string moduleName)
        {
            try { if (_drFuncLoad == null) InitDRIpc(sa); _drFuncLoad?.Invoke(moduleName, false); } 
            catch { ResetDRIpc(); try { InitDRIpc(sa); _drFuncLoad?.Invoke(moduleName, false); } catch { } }
        }

        private void DR_UnloadModule(ScriptAccessory sa, string moduleName)
        {
            try { if (_drFuncUnload == null) InitDRIpc(sa); _drFuncUnload?.Invoke(moduleName, false, true); } 
            catch { ResetDRIpc(); try { InitDRIpc(sa); _drFuncUnload?.Invoke(moduleName, false, true); } catch { } }
        }

        private async Task<bool> EnsureModuleLoaded(ScriptAccessory sa, string moduleName, int currentRunId)
        {
            if (DR_IsModuleEnabled(sa, moduleName)) return true;
            DR_LoadModule(sa, moduleName);
            for (int i = 0; i < 20; i++) {
                if (currentRunId != _runId) return false;
                if (DR_IsModuleEnabled(sa, moduleName)) return true;
                await Task.Delay(50);
            }
            sa.Method.SendChat($"/e [绝妖星面向辅助 报错] DR 模块 {moduleName} 加载超时或失败，请检查DR状态。");
            return false;
        }

        private void DR_LockOnChara(ScriptAccessory sa, float r) { 
            try { if (!DR_IsModuleEnabled(sa, "AutoFaceCameraDirection")) return; if (_drActionLockOn == null) InitDRIpc(sa); _drActionLockOn?.Invoke(r); } 
            catch { LogDebug(sa, () => "[防御性重连] DR_LockOnChara 失败，重新绑定..."); ResetDRIpc(); try { InitDRIpc(sa); _drActionLockOn?.Invoke(r); } catch { } } 
        }
        private void DR_CancelLockOn(ScriptAccessory sa) { 
            try { if (!DR_IsModuleEnabled(sa, "AutoFaceCameraDirection")) return; if (_drActionCancelLockOn == null) InitDRIpc(sa); _drActionCancelLockOn?.Invoke(); } 
            catch { LogDebug(sa, () => "[防御性重连] DR_CancelLockOn 失败，重新绑定..."); ResetDRIpc(); try { InitDRIpc(sa); _drActionCancelLockOn?.Invoke(); } catch { } } 
        }
        private void DR_SetRootAndSilence(ScriptAccessory sa, bool s) { 
            try { if (!DR_IsModuleEnabled(sa, "ForceRootAndSilence")) return; if (_drActionSetRoot == null) InitDRIpc(sa); _drActionSetRoot?.Invoke(s); } 
            catch { LogDebug(sa, () => "[防御性重连] DR_SetRootAndSilence 失败，重新绑定..."); ResetDRIpc(); try { InitDRIpc(sa); _drActionSetRoot?.Invoke(s); } catch { } } 
        }
        private void DR_TriggerJump(ScriptAccessory sa) { 
            try { if (!DR_IsModuleEnabled(sa, "ForceRootAndSilence")) return; if (_drActionTriggerJump == null) InitDRIpc(sa); _drActionTriggerJump?.Invoke(); } 
            catch { LogDebug(sa, () => "[防御性重连] DR_TriggerJump 失败，重新绑定..."); ResetDRIpc(); try { InitDRIpc(sa); _drActionTriggerJump?.Invoke(); } catch { } } 
        }
        private void DR_SetAutoFaceTarget(ScriptAccessory sa, bool s) { 
            try { if (!DR_IsModuleEnabled(sa, "ForceRootAndSilence")) return; if (_drActionSetAutoFace == null) InitDRIpc(sa); _drActionSetAutoFace?.Invoke(s); } 
            catch { LogDebug(sa, () => "[防御性重连] DR_SetAutoFaceTarget 失败，重新绑定..."); ResetDRIpc(); try { InitDRIpc(sa); _drActionSetAutoFace?.Invoke(s); } catch { } } 
        }
        #endregion

        private async Task<bool> WaitUntilAsync(ScriptAccessory sa, long targetTimeMs, int runId, Func<bool> extraCondition = null, Action tickAction = null, int delayMs = 50)
        {
            int nullRetryCount = 0;
            while (Environment.TickCount64 < targetTimeMs)
            {
                var pState = CheckPlayerState(sa, ref nullRetryCount, runId);
                if (pState == PlayerState.Abort) return false;
                if (pState == PlayerState.Retry) { await Task.Delay(delayMs); continue; }
                
                if (extraCondition != null && !extraCondition()) return false;
                
                tickAction?.Invoke();
                await Task.Delay(delayMs);
            }
            return true;
        }

        private PlayerState CheckPlayerState(ScriptAccessory sa, ref int retryCount, int expectedRunId)
        {
            if (_runId != expectedRunId) return PlayerState.Abort;

            if (sa.Data.MyObject == null)
            {
                if (++retryCount >= MaxNullRetries)
                {
                    NotifyNullBreak(sa);
                    return PlayerState.Abort;
                }
                return PlayerState.Retry;
            }
            retryCount = 0;
            if (sa.Data.MyObject.IsDead) return PlayerState.Abort;

            return PlayerState.Valid;
        }

        private void LogDebug(ScriptAccessory sa, Func<string> messageFactory) { 
            if (EnableDebugLog) sa.Method.SendChat($"/e [绝妖星面向辅助 测试] {messageFactory()}"); 
        }
        
        private void NotifyNullBreak(ScriptAccessory sa) { sa.Method.SendChat("/e [绝妖星面向辅助 报错] 检测到长时间失去玩家对象内存，触发异常熔断！脚本临时失效，请手动处理机制！"); }

        private async Task SafeRestoreAutoFace(ScriptAccessory sa)
        {
            LogDebug(sa, () => "执行角色发动技能时转身设置的安全还原(多重冗余)");
            for (int i = 0; i < 3; i++) { DR_SetAutoFaceTarget(sa, true); await Task.Delay(50); }
        }

        public void Init(ScriptAccessory accessory)
        {
            p1_safeDirection = float.NaN; 
            Interlocked.Increment(ref _runId); 
            _isLocking = false;
            _p4IsFakeWindow = false; 
            _p4WindowExpiresAt = 0; 
            _p4PetrifyScheduledTime = 0;
            _petrifyExpirations.Clear(); 
            
            ResetDRIpc(); 
            
            InitDRIpc(accessory);
            if (_drSubIsMod == null) return; 

            DR_CancelLockOn(accessory);
            DR_SetRootAndSilence(accessory, false);

            int currentRunId = _runId;
            Task.Run(async () => {
                bool origSwim = DR_IsModuleEnabled(accessory, "ProhibitSwimming");
                bool isVerified = origSwim; 
                bool needRestore = false;

                try {
                    DR_LoadModule(accessory, "ForceRootAndSilence"); 
                    if (!origSwim) {
                        DR_LoadModule(accessory, "ProhibitSwimming"); 
                        needRestore = true;
                    }
                    
                    await Task.Delay(300); 
                    
                    if (needRestore) {
                        isVerified = DR_IsModuleEnabled(accessory, "ProhibitSwimming");
                    }
                } finally {
                    if (needRestore) {
                        DR_UnloadModule(accessory, "ProhibitSwimming");
                    }
                }

                if (currentRunId != _runId) return;

                bool hasRoot = DR_IsModuleEnabled(accessory, "ForceRootAndSilence");

                if (!isVerified || !hasRoot) {
                    string missing = "";
                    if (!isVerified) missing += " [未通过 DR 在线验证] ";
                    if (!hasRoot) missing += " [缺少或关闭了 ForceRootAndSilence 本地模块] ";
                    accessory.Method.SendChat($"/e [绝妖星面向辅助 报错] 自检失败：{missing}\n请确保DR已开启并完成在线验证，且正确导入了配套本地模块。");
                    
                    ResetDRIpc();
                    return;
                }

                await SafeRestoreAutoFace(accessory);
                LogDebug(accessory, () => "开局自检通过！在线验证生效且本地模块一切正常。");
            });
            LogDebug(accessory, () => "脚本已初始化，状态及花名册已重置。");
        }

        private float NormalizeRot(float rot)
        {
            float pi2 = 2 * MathF.PI;
            rot = rot % pi2;
            if (rot > MathF.PI) rot -= pi2;
            if (rot < -MathF.PI) rot += pi2;
            return MathF.Abs(rot) < 0.001f ? 0.001f : rot;
        }

        private float CalcBossTrackingRot(ScriptAccessory sa, ulong bossId, uint fallbackDataId = 18475)
        {
            var myObj = sa.Data.MyObject;
            var boss = (bossId != 0 ? sa.Data.Objects.SearchById(bossId) : null) ?? sa.Data.Objects.FirstOrDefault(o => o.DataId == fallbackDataId);
            if (myObj == null || boss == null) return NormalizeRot(myObj?.Rotation ?? 0.001f);
            return NormalizeRot(MathF.Atan2(boss.Position.X - myObj.Position.X, boss.Position.Z - myObj.Position.Z));
        }

        private float ClampAngle(float currentTarget, float anchorRot, float maxDelta)
        {
            float diff = currentTarget - anchorRot;
            while (diff > MathF.PI) diff -= 2 * MathF.PI;
            while (diff < -MathF.PI) diff += 2 * MathF.PI;
            
            if (diff > maxDelta) diff = maxDelta;
            if (diff < -maxDelta) diff = -maxDelta;
            
            return NormalizeRot(anchorRot + diff);
        }

        private float AngleDiff(float a, float b)
        {
            float d = (a - b) % (2 * MathF.PI);
            if (d > MathF.PI) d -= 2 * MathF.PI;
            if (d < -MathF.PI) d += 2 * MathF.PI;
            return MathF.Abs(d);
        }

        private string FormatAngle(float rad) => $"{rad:F3}弧度 (约 {rad * 180f / MathF.PI:F0}°)";

        [ScriptMethod(name: "P1 面向记录", eventType: EventTypeEnum.ObjectEffect, eventCondition: ["Id1:64"], suppress: 500)]
        public void P1_AutoFace_DirectionRecord(Event @event, ScriptAccessory accessory)
        {
            if (!EnableP1AutoFace || !string.Equals(@event["Id2"], "128")) return;
            try { 
                var pos = @event.SourcePosition; 
                if (Vector3.Distance(pos, new Vector3(105.25f, 13.5f, 34)) < 1) p1_safeDirection = 0.001f;
                else if (Vector3.Distance(pos, new Vector3(95, 12.5f, 25)) < 1) p1_safeDirection = MathF.PI; 
                
                if (!float.IsNaN(p1_safeDirection)) LogDebug(accessory, () => $"P1 记录到安全面朝角度: {FormatAngle(p1_safeDirection)}");
            } catch { }
        }

        [ScriptMethod(name: "P1 面向执行", eventType: EventTypeEnum.StartCasting, eventCondition: ["ActionId:47764"])]
        public async void P1_AutoFace_Execute(Event @event, ScriptAccessory accessory)
        {
            if (!EnableP1AutoFace || float.IsNaN(p1_safeDirection)) return;
            float dir = p1_safeDirection;
            p1_safeDirection = float.NaN; 
            int currentRunId = _runId;
            ulong bossId = @event.SourceId; 
            
            int totalDelay = (int)(P1LoadDelay * 1000);
            int preloadMs = (int)(PreloadAdvance * 1000);
            int firstWait = Math.Max(0, totalDelay - preloadMs);
            int secondWait = totalDelay - firstWait;
            
            if (firstWait > 0) await Task.Delay(firstWait);
            if (currentRunId != _runId) return;

            float playerRotSnapshot = NormalizeRot(accessory.Data.MyObject?.Rotation ?? 0.001f);
            float greedyRotSnapshot = CalcBossTrackingRot(accessory, bossId);

            long loadStart = Environment.TickCount64;
            if (!await EnsureModuleLoaded(accessory, "AutoFaceCameraDirection", currentRunId)) return;
            if (!await EnsureModuleLoaded(accessory, "ForceRootAndSilence", currentRunId)) return;
            DR_SetAutoFaceTarget(accessory, false); 
            
            int elapsed = (int)(Environment.TickCount64 - loadStart);
            int actualSecondWait = Math.Max(0, secondWait - elapsed);
            
            float diffP1 = AngleDiff(playerRotSnapshot, greedyRotSnapshot);
            bool greedyNotInSafeZone = AngleDiff(greedyRotSnapshot, dir) > DEG_90_RAD; 
            bool useSafeLockP1 = (diffP1 > DEG_30_RAD) && greedyNotInSafeZone;

            LogDebug(accessory, () => $"P1 预加载完成。软锁定检测：玩家与贪刀面向相差 {FormatAngle(diffP1)}，贪刀面向不在安全区: {greedyNotInSafeZone} -> 软锁定模式: {(useSafeLockP1 ? "安全版本" : "贪刀版本")}，角度: {FormatAngle(useSafeLockP1 ? dir : greedyRotSnapshot)}");

            int nullRetryCount = 0;
            if (actualSecondWait > 0) {
                int preLoops = actualSecondWait / 100;
                for (int i = 0; i < preLoops; i++) {
                    var pState = CheckPlayerState(accessory, ref nullRetryCount, currentRunId);
                    if (pState == PlayerState.Abort) return;
                    if (pState == PlayerState.Retry) { await Task.Delay(50); continue; }
                    
                    if (useSafeLockP1) {
                        DR_LockOnChara(accessory, dir);
                    } else {
                        float realTimeGreedy = CalcBossTrackingRot(accessory, bossId);
                        float clampedGreedy = ClampAngle(realTimeGreedy, greedyRotSnapshot, DEG_30_RAD);
                        DR_LockOnChara(accessory, clampedGreedy); 
                    }
                    
                    await Task.Delay(100);
                }
                int rem = actualSecondWait % 100;
                if (rem > 0) {
                    var pState = CheckPlayerState(accessory, ref nullRetryCount, currentRunId);
                    if (pState == PlayerState.Valid) { 
                        if (useSafeLockP1) {
                            DR_LockOnChara(accessory, dir);
                        } else {
                            float realTimeGreedy = CalcBossTrackingRot(accessory, bossId);
                            float clampedGreedy = ClampAngle(realTimeGreedy, greedyRotSnapshot, DEG_30_RAD);
                            DR_LockOnChara(accessory, clampedGreedy); 
                        }
                    }
                    await Task.Delay(rem);
                }
            }
            if (currentRunId != _runId) return;
            
            try {
                LogDebug(accessory, () => $"P1 机制判定点到达！瞬间硬切至安全角度: {FormatAngle(dir)}");
                int loops = (int)(P1RestoreDelay * 10);
                nullRetryCount = 0;
                for (int i = 0; i < loops; i++) {
                    var pState = CheckPlayerState(accessory, ref nullRetryCount, currentRunId);
                    if (pState == PlayerState.Abort) break;
                    if (pState == PlayerState.Retry) { await Task.Delay(50); continue; }
                    DR_LockOnChara(accessory, dir); 
                    await Task.Delay(100);
                }
            } finally {
                DR_CancelLockOn(accessory);
                DR_UnloadModule(accessory, "AutoFaceCameraDirection");
                await SafeRestoreAutoFace(accessory);
            }
        }

        [ScriptMethod(name: "P3 辅助启动", eventType: EventTypeEnum.StartCasting, eventCondition: ["ActionId:47891"])]
        public async void VacuumWave_StartAssist(Event ev, ScriptAccessory sa)
        {
            if (!EnableP3AutoFace) return;
            var currentRunId = _runId;
            ulong bossId = ev.SourceId; 
    
            int nullRetryCount = 0;
            while (true) {
                var state = CheckPlayerState(sa, ref nullRetryCount, currentRunId);
                if (state == PlayerState.Abort) return;
                if (state == PlayerState.Valid) break;
                await Task.Delay(50);
            }

            var myObj = sa.Data.MyObject;
            bool wind = myObj.HasStatusAny(new uint[] { 1602 });
            bool revWind = myObj.HasStatusAny(new uint[] { 1603 });
            if (!wind && !revWind) return;

            LogDebug(sa, () => $"P3 真空波启动，检测到玩家Buff: {(wind ? "【混沌之风】(需背对Boss防击退)" : "【混沌之暗】(需面朝Boss防击退)")}");

            float actualStartDelay = Math.Max(P3StartDelay, 4.7f);
            int totalDelay = (int)(actualStartDelay * 1000);
            int preloadMs = (int)(PreloadAdvance * 1000);
            int firstWait = Math.Max(0, totalDelay - preloadMs);
            int secondWait = totalDelay - firstWait;
            
            if (firstWait > 0) await Task.Delay(firstWait);
            if (currentRunId != _runId) return;

            ulong originalTargetId = sa.Data.MyObject?.TargetObjectId ?? 0;
            if (EnableP3AutoTarget)
            {
                if (revWind) {
                    var exdeath = sa.Data.Objects.FirstOrDefault(o => o.DataId == 19509);
                    if (exdeath != null) sa.Method.SelectTarget((uint)exdeath.GameObjectId);
                } else if (wind) {
                    var chaos = sa.Data.Objects.FirstOrDefault(o => o.DataId == 19508);
                    if (chaos != null) sa.Method.SelectTarget((uint)chaos.GameObjectId);
                }
            }

            long loadStart = Environment.TickCount64; 
            if (!await EnsureModuleLoaded(sa, "AutoFaceCameraDirection", currentRunId)) return;
            if (!await EnsureModuleLoaded(sa, "ForceRootAndSilence", currentRunId)) return;
            DR_SetAutoFaceTarget(sa, false); 
            
            int elapsed = (int)(Environment.TickCount64 - loadStart); 
            int actualSecondWait = Math.Max(0, secondWait - elapsed);
            
            float initHardRot = CalcBossTrackingRot(sa, bossId, 19509); 
            if (wind) initHardRot = NormalizeRot(initHardRot + MathF.PI); 

            LogDebug(sa, () => $"P3 判定点到达！执行强制安全面向锁定，目标锁定角度: {FormatAngle(initHardRot)}");
            
            try {
                _isLocking = true;
                nullRetryCount = 0;
                
                int totalDurationMs = actualSecondWait + 9000; 
                long endTime = Environment.TickCount64 + totalDurationMs;

                while (Environment.TickCount64 < endTime && _isLocking && currentRunId == _runId) {
                    var pState = CheckPlayerState(sa, ref nullRetryCount, currentRunId);
                    if (pState == PlayerState.Abort) break;
                    if (pState == PlayerState.Retry) { await Task.Delay(50); continue; }

                    float targetRot = CalcBossTrackingRot(sa, bossId, 19509); 
                    if (wind) targetRot = NormalizeRot(targetRot + MathF.PI); 
                    DR_LockOnChara(sa, targetRot);
                    
                    await Task.Delay(50);
                }
            } finally {
                LogDebug(sa, () => "P3 面向机制执行完毕或被中断，执行安全释放。");
                DR_CancelLockOn(sa);
                DR_UnloadModule(sa, "AutoFaceCameraDirection");

                if (EnableP3AutoTarget && originalTargetId != 0 && originalTargetId != 0xE0000000) {
                    var origTarget = sa.Data.Objects.SearchById(originalTargetId);
                    if (origTarget != null && !origTarget.IsDead) {
                        sa.Method.SelectTarget((uint)originalTargetId);
                    }
                }
                await SafeRestoreAutoFace(sa);
                _isLocking = false;
            }
        }

        [ScriptMethod(name: "P3 辅助结束", eventType: EventTypeEnum.ActionEffect, eventCondition: ["ActionId:47891"])]
        public async void VacuumWave_EndAssist(Event ev, ScriptAccessory sa)
        {
            if (!EnableP3AutoFace) return;
            float actualDelay = Math.Max(P3EndDelay, 2.5f);
            await Task.Delay((int)(actualDelay * 1000));
            _isLocking = false; 
        }

        [ScriptMethod(name: "P4 真伪窗口", eventType: EventTypeEnum.StatusAdd, eventCondition: ["StatusID:2056"])]
        public void P4_TruthWindowCapture(Event @event, ScriptAccessory accessory)
        {
            if (!int.TryParse(@event["Param"], out var param)) return;
            if (param != 1121 && param != 1122) return;
            uint dataId = accessory.Data.Objects.SearchById(@event.TargetId)?.DataId ?? 0;
            if (dataId != 0 && dataId != 19510) return; 

            _p4IsFakeWindow = (param == 1121);
            _p4WindowExpiresAt = Environment.TickCount64 + 12000; 
            
            string windowType = _p4IsFakeWindow ? "【伪 (反转)】-> 需停手/背对" : "【真 (正常)】-> 需动/面朝";
            LogDebug(accessory, () => $"P4 捕获艾克斯迪司大十字，参数: {param}，判定逻辑: {windowType}");
        }

        [ScriptMethod(name: "P4 机制执行", eventType: EventTypeEnum.StatusAdd, eventCondition: ["StatusID:regex:^(5543|5546)$"])]
        public void P4_DebuffExecute(Event @event, ScriptAccessory sa)
        {
            bool isMe = sa.Data.MyObject != null && @event.TargetId == sa.Data.MyObject.GameObjectId;
            if (@event["StatusID"] == "5546" && !isMe) return;
            if (!float.TryParse(@event["DurationMilliseconds"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dur)) return;

            bool isTrue = !(_p4IsFakeWindow && Environment.TickCount64 < _p4WindowExpiresAt); 

            string debuffName = @event["StatusID"] == "5543" ? "【石化眼】" : "【加速度炸弹】";
            string actionDetail = "";
            
            if (@event["StatusID"] == "5543") {
                actionDetail = isTrue ? "(全员需背对带眼者)" : "(全员需直视带眼者)";
                ulong targetId = @event.TargetId; 
                long expTime = Environment.TickCount64 + (long)dur; 
                _petrifyExpirations[targetId] = expTime; 
                LogDebug(sa, () => $"[花名册登记] 记录到实体ID {targetId:X8} 的石化Buff，预期到期时间戳: {expTime} (时长: {(dur/1000f):F1}s)");
            } else {
                actionDetail = isTrue ? "(自己需静止)" : "(自己需移动)";
            }
            LogDebug(sa, () => $"P4结算 {debuffName} -> 当前窗口判定为: {(isTrue ? "真(正常)" : "伪(反转)")} {actionDetail}");

            if (@event["StatusID"] == "5543" && EnableP4Petrify) {
                long exp = Environment.TickCount64 + (long)dur; 
                if (Math.Abs(exp - _p4PetrifyScheduledTime) < 2000) return; 
                _p4PetrifyScheduledTime = exp;
                _ = HandleP4PetrifyAsync(sa, isTrue, (int)dur, _runId, bossId: 0); 
            }
            else if (@event["StatusID"] == "5546" && isMe) {
                if (isTrue && EnableP4AccelBombTrue) _ = HandleP4AccelBombRootAsync(sa, (int)dur, _runId);
                else if (!isTrue && EnableP4AccelBombFalse) _ = HandleP4AccelBombJumpAsync(sa, (int)dur, _runId);
            }
        }

        private float CalcP4PetrifySafeRot(ScriptAccessory sa, bool isTrue, ulong bossId, List<ulong> targetPlayerIds)
        {
            var myObj = sa.Data.MyObject;
            if (myObj == null) return 0.001f;

            float targetRot = CalcBossTrackingRot(sa, bossId);

            if (targetPlayerIds != null && targetPlayerIds.Count > 0)
            {
                var lookAtAngles = new List<float>();
                foreach (var id in targetPlayerIds) {
                    var p = sa.Data.Objects.SearchById(id);
                    if (p == null) continue; 
                    lookAtAngles.Add(MathF.Atan2(p.Position.X - myObj.Position.X, p.Position.Z - myObj.Position.Z)); 
                }

                if (lookAtAngles.Count == 0) return NormalizeRot(targetRot);
                bool isSafe = true;

                if (isTrue) {
                    float safeMarginTrue = 50f * MathF.PI / 180f; 
                    foreach (var lookAt in lookAtAngles) {
                        if (AngleDiff(targetRot, lookAt) < safeMarginTrue) { isSafe = false; break; }
                    }
                    if (!isSafe) {
                        bool found = false;
                        for (int i = 10; i <= 180; i += 10) {
                            float testRotPlus = NormalizeRot(targetRot + (i * MathF.PI / 180f));
                            bool testSafePlus = true;
                            foreach (var lookAt in lookAtAngles) {
                                if (AngleDiff(testRotPlus, lookAt) < safeMarginTrue) { testSafePlus = false; break; }
                            }
                            if (testSafePlus) { targetRot = testRotPlus; found = true; break; }

                            float testRotMinus = NormalizeRot(targetRot - (i * MathF.PI / 180f));
                            bool testSafeMinus = true;
                            foreach (var lookAt in lookAtAngles) {
                                if (AngleDiff(testRotMinus, lookAt) < safeMarginTrue) { testSafeMinus = false; break; }
                            }
                            if (testSafeMinus) { targetRot = testRotMinus; found = true; break; }
                        }
                    }                
                } else {
                    float sumX = 0, sumZ = 0;
                    foreach (var id in targetPlayerIds) {
                        var p = sa.Data.Objects.SearchById(id);
                        if (p != null) { sumX += p.Position.X - myObj.Position.X; sumZ += p.Position.Z - myObj.Position.Z; }
                    }
                    float avgLookAt = MathF.Atan2(sumX, sumZ); 
                    float safeMarginFake = 40f * MathF.PI / 180f; 
                    foreach (var lookAt in lookAtAngles) {
                        if (AngleDiff(targetRot, lookAt) > safeMarginFake) { isSafe = false; break; }
                    }
                    if (!isSafe) targetRot = avgLookAt; 
                }
            }
            return NormalizeRot(targetRot);
        }

        private async Task HandleP4PetrifyAsync(ScriptAccessory sa, bool isTrue, int dur, int runId, ulong bossId)
        {
            long targetTime = Environment.TickCount64 + dur; 
            long preloadTime = targetTime - (int)(P4PetrifyAdvance * 1000) - (int)(PreloadAdvance * 1000);
            long lockTime = targetTime - (int)(P4PetrifyAdvance * 1000);
            long releaseTime = lockTime + (long)(P4PetrifyDuration * 1000);

            if (!await WaitUntilAsync(sa, preloadTime, runId)) return;

            float playerRotSnapshot = NormalizeRot(sa.Data.MyObject?.Rotation ?? 0.001f);
            float greedyRotSnapshot = CalcBossTrackingRot(sa, bossId);

            if (!await EnsureModuleLoaded(sa, "AutoFaceCameraDirection", runId)) return;
            if (!await EnsureModuleLoaded(sa, "ForceRootAndSilence", runId)) return;
            DR_SetAutoFaceTarget(sa, false);
            
            float diffP4 = AngleDiff(playerRotSnapshot, greedyRotSnapshot);
            bool useSafeLockP4 = diffP4 > DEG_30_RAD;

            List<ulong> cachedPetrifyPlayerIds = new List<ulong>(); 
            ulong myEntityId = sa.Data.MyObject?.GameObjectId ?? 0; 
            
            var expectedPlayerIds = _petrifyExpirations
                .Where(kv => Math.Abs(kv.Value - targetTime) < 2000)
                .Select(kv => kv.Key).Where(id => id != myEntityId).ToList();

            var initialActivePlayers = expectedPlayerIds
                .Where(id => {
                    var obj = sa.Data.Objects.SearchById(id) as IBattleChara;
                    return obj != null && !obj.IsDead && obj.HasStatusAny(new uint[] { 5543 });
                }).ToList();

            if (initialActivePlayers.Count > 0) cachedPetrifyPlayerIds = initialActivePlayers;
            
            LogDebug(sa, () => $"P4 石化眼软锁定检测：玩家与贪刀面向相差 {FormatAngle(diffP4)} -> 软锁定模式: {(useSafeLockP4 ? "安全版本" : "贪刀版本")}，角度: {FormatAngle(useSafeLockP4 ? CalcP4PetrifySafeRot(sa, isTrue, bossId, cachedPetrifyPlayerIds) : greedyRotSnapshot)}。已捕获 {cachedPetrifyPlayerIds.Count} 名带Buff队友。");
            if (!await WaitUntilAsync(sa, lockTime, runId, null, () => {
                if (useSafeLockP4) {
                    var currentAlive = cachedPetrifyPlayerIds
                        .Where(id => { var obj = sa.Data.Objects.SearchById(id) as IBattleChara; return obj != null && !obj.IsDead; }).ToList();
                    float preCalcSafeRot = CalcP4PetrifySafeRot(sa, isTrue, bossId, currentAlive);
                    DR_LockOnChara(sa, preCalcSafeRot);
                } else {
                    float realTimeGreedy = CalcBossTrackingRot(sa, bossId);
                    float clampedGreedy = ClampAngle(realTimeGreedy, greedyRotSnapshot, DEG_30_RAD);
                    DR_LockOnChara(sa, clampedGreedy);
                }
            })) return;

            float initialRot = CalcP4PetrifySafeRot(sa, isTrue, bossId, cachedPetrifyPlayerIds);
            LogDebug(sa, () => $"P4 石化眼触发：硬切面向以避让或直视队友！初始计算安全目标角度: {FormatAngle(initialRot)}");
            
            try {
                _isLocking = true;
                await WaitUntilAsync(sa, releaseTime, runId, () => _isLocking, () => {
                    var currentAlivePlayers = cachedPetrifyPlayerIds
                        .Where(id => {
                            var obj = sa.Data.Objects.SearchById(id) as IBattleChara;
                            return obj != null && !obj.IsDead;
                        }).ToList();

                    cachedPetrifyPlayerIds = currentAlivePlayers;
                    DR_LockOnChara(sa, CalcP4PetrifySafeRot(sa, isTrue, bossId, cachedPetrifyPlayerIds)); 
                });
            } finally {
                LogDebug(sa, () => "P4 石化眼结束，恢复自由。");
                DR_CancelLockOn(sa);
                DR_UnloadModule(sa, "AutoFaceCameraDirection");
                await SafeRestoreAutoFace(sa);
                _isLocking = false;
            }
        }

        private async Task HandleP4AccelBombRootAsync(ScriptAccessory sa, int dur, int runId)
        {
            long targetTime = Environment.TickCount64 + dur; 
            long aeOffTime1 = targetTime - 4000; 
            long aeOffTime2 = targetTime - 3000;
            long rootTime = targetTime - (int)(P4RootAdvance * 1000); 

            bool hasDisabledAe = false;

            try
            {
                if (EnableAeCompatibility) {
                    if (!await WaitUntilAsync(sa, aeOffTime1, runId, () => sa.Data.MyObject.HasStatusAny(new uint[] { 5546 }))) return;
                    LogDebug(sa, () => "准备停手：第1次发送 /aeTargetSelector off");
                    sa.Method.SendChat("/aeTargetSelector off");
                    hasDisabledAe = true; 

                    if (!await WaitUntilAsync(sa, aeOffTime2, runId, () => sa.Data.MyObject.HasStatusAny(new uint[] { 5546 }))) return;
                    LogDebug(sa, () => "准备停手：第2次发送 /aeTargetSelector off");
                    sa.Method.SendChat("/aeTargetSelector off");
                }

                if (!await WaitUntilAsync(sa, rootTime, runId, () => sa.Data.MyObject.HasStatusAny(new uint[] { 5546 }))) return;

                if (!await EnsureModuleLoaded(sa, "ForceRootAndSilence", runId)) return;
                LogDebug(sa, () => $"P4 真炸弹：已开启强制禁锢与沉默，持续 {P4RootDuration} 秒！");
                long rootEndTime = Environment.TickCount64 + (int)(P4RootDuration * 1000); 
                
                await WaitUntilAsync(sa, rootEndTime, runId, null, () => DR_SetRootAndSilence(sa, true));
            }
            finally
            {
                LogDebug(sa, () => "P4 真炸弹：禁锢与沉默解除。");
                DR_SetRootAndSilence(sa, false);

                if (hasDisabledAe) {
                    LogDebug(sa, () => "恢复输出：开始异步恢复 AE 目标选择器");
                    _ = Task.Run(async () => {
                        sa.Method.SendChat("/aeTargetSelector on");
                        await Task.Delay(1000);
                        sa.Method.SendChat("/aeTargetSelector on");
                    });
                }
            }
        }

        private async Task HandleP4AccelBombJumpAsync(ScriptAccessory sa, int dur, int runId)
        {
            long targetTime = Environment.TickCount64 + dur; 
            long jumpStartTime = targetTime - (int)(P4JumpAdvance * 1000);
            long jumpEndTime = jumpStartTime + (long)(P4JumpAdvance * 1000) + 600; 

            if (!await WaitUntilAsync(sa, jumpStartTime, runId, () => sa.Data.MyObject.HasStatusAny(new uint[] { 5546 }))) return;

            if (!await EnsureModuleLoaded(sa, "ForceRootAndSilence", runId)) return;
            LogDebug(sa, () => $"P4 伪炸弹：进入跳跃判定窗口。");

            await WaitUntilAsync(sa, jumpEndTime, runId, null, () => {
                if (!sa.Data.MyObject.IsCasting) {
                    DR_TriggerJump(sa);
                }
            }, 100); 
        }
    }
}