using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Module = Planetbase.Module;
using System.Text;
using HarmonyLib;
using Planetbase;
using PlanetbaseCoop.Net;
using PlanetbaseCoop.Sync;
using UnityEngine;

namespace PlanetbaseCoop
{
    // In-game test bots (COOP_TEST=host|client). The host bot runs the scenario: it sets up a
    // colony, hosts, tells the joiner bot what to do (through the joiner's real hook paths) and
    // compares both worlds. Inert unless COOP_TEST is set.
    public static class TestBot
    {
        // ------------------------------------------------------------ shared
        private static string sMode;
        private static float sT;
        private static bool sDone;
        private static int sPass, sFail;
        private static readonly List<string> sFailures = new List<string>();

        // last digest received from the other side
        private static Dictionary<string, string> sRemote = new Dictionary<string, string>();
        private static float sRemoteAt = -1;
        private static string sRefusal;

        public static void Init()
        {
            sMode = TestEnv.Mode;
            if (sMode != "host" && sMode != "client") sMode = null;
            if (sMode != null) Log.Info("Test bot: " + sMode + ", scenario " + (TestEnv.Get("COOP_SCENARIO") ?? "join"));
        }

        public static void Update(float dt)
        {
            if (sMode == null || sDone) return;
            sT += dt;
            try
            {
                if (sMode == "host") HostStep(dt); else ClientStep(dt);
            }
            catch (Exception e)
            {
                Check("bot exception: " + e.Message, false);
                Log.Error(e.ToString());
                Finish();
            }
        }

        private static void Check(string name, bool ok, string detail = null)
        {
            if (ok) sPass++; else { sFail++; sFailures.Add(name); }
            TestEnv.WriteStatus((ok ? "PASS " : "FAIL ") + name + (detail != null ? " (" + detail + ")" : ""));
        }

        private static void Finish()
        {
            if (sDone) return;
            sDone = true;
            TestEnv.WriteStatus(sFail == 0 && sPass > 0 ? $"RESULT: PASS {sPass} checks" : $"RESULT: FAIL {sFail} of {sPass + sFail}: {string.Join("; ", sFailures)}");
            Session.Stop("test finished");
            TestEnv.Quit();
        }

        private static void SendProbe(string kind, string payload)
        {
            var msg = Wire.Build(Msg.TestProbe, w => { w.Write(kind); w.Write(payload); });
            if (Session.IsHost) Session.Transport?.Broadcast(msg); else Session.Transport?.Send(0, msg);
        }

        public static void OnProbe(int peer, BinaryReader r)
        {
            if (sMode == null) return;
            string kind = r.ReadString(), payload = r.ReadString();
            if (kind == "digest") { sRemote = ParseDigest(payload); sRemoteAt = sT; }
            else if (kind == "do" && sMode == "client") sClientQueue.Enqueue(payload);   // run as input, next frame
            else if (kind == "end" && sMode == "client") { sClientEnd = true; }
        }

        public static void OnRefused(string reason) { sRefusal = reason; }
        public static void OnHostCommand(Cmd cmd, bool ok) { if (sMode != null) Log.Info($"bot: host executed {cmd} ok={ok}"); }
        public static void OnCommandResult(Cmd cmd, bool ok, string msg) { if (sMode != null) Log.Info($"bot: result {cmd} ok={ok} {msg}"); }

        // ------------------------------------------------------------ digests (what both sides compare)
        private static readonly Dictionary<int, object> sScratch = new Dictionary<int, object>();
        private static readonly FieldInfo fMessages = AccessTools.Field(typeof(MessageLog), "mMessages");
        private static readonly FieldInfo fTradeState = AccessTools.Field(typeof(MerchantShip), "mTradeState");
        private static readonly FieldInfo fNeeded = AccessTools.Field(typeof(MerchantShip), "mNeededProducts");
        private static readonly FieldInfo fCommission = AccessTools.Field(typeof(MerchantShip), "mCommission");
        private static readonly FieldInfo fWinResult = AccessTools.Field(typeof(GuiMerchantWindow), "mResult");
        private static readonly FieldInfo fWinMStock = AccessTools.Field(typeof(GuiMerchantWindow), "mMerchantStockAmounts");
        private static readonly FieldInfo fWinMTrade = AccessTools.Field(typeof(GuiMerchantWindow), "mMerchantTradeAmounts");
        private static readonly FieldInfo fWinBStock = AccessTools.Field(typeof(GuiMerchantWindow), "mBaseStockAmounts");
        private static readonly FieldInfo fWinBTrade = AccessTools.Field(typeof(GuiMerchantWindow), "mBaseTradeAmounts");
        private static Module sPad1, sPad2, sStorage;
        private static Resource sMoved, sStored;
        private static LandingShip sShip;
        private static int sCharsBefore;
        private static bool sMerchantWaiting;

        public static Dictionary<string, string> LocalDigest()
        {
            var d = new Dictionary<string, string>();
            foreach (var k in Registry.All)
            {
                Registry.Collect(k, sScratch);
                var ids = sScratch.Keys.OrderBy(x => x).ToList();
                d["count." + k] = ids.Count.ToString();
                d["ids." + k] = string.Join(",", ids);
            }
            Registry.Collect(Kind.Module, sScratch);
            d["modules"] = string.Join(";", sScratch.Values.Cast<Module>().OrderBy(m => m.getId()).Select(m =>
                $"{m.getId()}:{m.getModuleType().GetType().Name}:{(int)Registry.StateOf(m)}:{m.getPosition().x:F1}:{m.getPosition().z:F1}:{BuildRec.ProgressOf(m):F2}"));
            Registry.Collect(Kind.Character, sScratch);
            d["chars"] = string.Join(";", sScratch.Values.Cast<Character>().OrderBy(c => c.getId()).Select(c =>
                $"{c.getId()}:{c.getPosition().x:F1}:{c.getPosition().z:F1}:{(int)c.getLocation()}"));
            Registry.Collect(Kind.Resource, sScratch);
            d["res"] = string.Join(";", sScratch.Values.Cast<Resource>().OrderBy(x => x.getId()).Select(x =>
            {
                ResourcePlaces.Where(x, out var h, out int hid, out int slot);
                var p = x.getGameObject()?.transform.position ?? Vector3.zero;
                return $"{x.getId()}:{(int)h}:{hid}:{slot}:{p.x:F1}:{p.z:F1}";
            }));
            Registry.Collect(Kind.Ship, sScratch);
            d["ships"] = string.Join(";", sScratch.Values.Cast<Ship>().OrderBy(x => x.getId()).Select(x =>
                $"{x.getId()}:{ShipRec.From(x).State}:{x.getTransform().position.x:F1}:{x.getTransform().position.y:F1}:{x.getTransform().position.z:F1}"));
            d["msgs"] = string.Join("|", ((List<Message>)fMessages.GetValue(Singleton<MessageLog>.getInstance())).Select(m => m.getDescription()));
            foreach (var kv in Settings.Read()) if (kv.Key.StartsWith("land.")) d[kv.Key] = kv.Value.ToString();
            var tm = Singleton<TimeManager>.getInstance();
            d["speed"] = tm.getTimeScale().ToString("F0");
            d["dropped"] = sDropped;
            d["alert"] = ((int)Singleton<SecurityManager>.getInstance().getAlertState()).ToString();
            d["tod"] = Singleton<EnvironmentManager>.getInstance().getTimeIndicator().getValue().ToString("F3");
            return d;
        }

        private static string Encode(Dictionary<string, string> d) => string.Join("\n", d.Select(kv => kv.Key + "=" + kv.Value));

        private static Dictionary<string, string> ParseDigest(string s)
        {
            var d = new Dictionary<string, string>();
            foreach (var line in s.Split('\n'))
            {
                int i = line.IndexOf('=');
                if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
            }
            return d;
        }

        private static string Get(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : "";

        // ------------------------------------------------------------ host scenario
        private static int sStep;
        private static float sStepT;
        private static Vector3 sBase;
        private static Module sM1, sM2;
        private static int sModulesBefore, sConnectionsBefore, sComponentsBefore;
        private static Vector3 sClientSpot;
        private static string sScenario;
        private static int sLastDigestRequest = -1;

        private static bool sEntered;
        private static Dictionary<string, string[]> sCharsAtStart;

        private static void Next(string why = null)
        {
            if (why != null) Log.Info("bot step " + sStep + " done: " + why);
            sStep++;
            sStepT = 0f;
            sEntered = false;
        }

        // true on the first frame of a step only
        private static bool First()
        {
            if (sEntered) return false;
            sEntered = true;
            return true;
        }

        private static bool Timeout(float seconds, string what)
        {
            if (sStepT < seconds) return false;
            Check(what, false, "timeout after " + seconds + " s");
            Finish();
            return true;
        }

        private static void HostStep(float dt)
        {
            sStepT += dt;
            sScenario = sScenario ?? (TestEnv.Get("COOP_SCENARIO") ?? "join");
            var state = GameAccess.CurrentState();
            switch (sStep)
            {
                case 0: // title -> new game
                    if (state != null && state.isTitleState() && sStepT > 2f)
                    {
                        GameAccess.Manager.setGameStateGameNew(424242, 0, false, null);
                        Next("new game requested");
                    }
                    else Timeout(120, "host reached the title screen");
                    break;
                case 1: // wait loaded
                    if (GameAccess.Game() != null && (Mirror.SkipLoadWait() || Mirror.IsUpdating()))
                    {
                        Check("host started a new colony", true);
                        Next();
                    }
                    else Timeout(240, "host colony loaded");
                    break;
                case 2: // let the colony ship land and colonists come out, then build a small base
                    if (sStepT > 3f && Character.getHumanCount() > 0 || sStepT > 60f)
                    {
                        sBase = FindColonyShipPosition();
                        sM1 = PlaceAndBuild("ModuleTypeOxygenGenerator", 1, sBase, 20f);
                        sM2 = PlaceAndBuild("ModuleTypeAirlock", 0, sM1 != null ? sM1.getPosition() : sBase, 16f);
                        if (sM1 != null && sM2 != null && Connection.canLink(sM1, sM2)) Module.linkModules(sM1, sM2, true);
                        sStorage = PlaceAndBuild("ModuleTypeStorage", 0, sM2 != null ? sM2.getPosition() : sBase, 16f);
                        ForceBuildAll();
                        // some loose resources to carry around
                        for (int i = 0; i < 4; i++)
                            Resource.create(TypeList<ResourceType, ResourceTypeList>.find("Metal"), sBase + new Vector3(6 + i, 0, -6), Location.Exterior);
                        Check("host base built", sM1 != null && sM2 != null, $"modules {Module.getCount()}, humans {Character.getHumanCount()}");
                        Next();
                    }
                    break;
                case 3: // host
                    {
                        int port = int.Parse(TestEnv.Get("COOP_PORT") ?? "47610");
                        string err = Session.StartHost(TcpTransport.Host(port));
                        Check("host started hosting", err == null, err);
                        TestEnv.WriteStatus("READY host listening on " + port);
                        if (err != null) { Finish(); return; }
                        Next();
                    }
                    break;
                case 4: // wait for the joiner
                    if (sScenario == "refuse")
                    {
                        // the joiner runs a different mod version; it must be refused and never become ready
                        if (sStepT > 40f)
                        {
                            Check("mismatched joiner never joined", !Session.Peers.Values.Any(p => p.Ready));
                            Finish();
                        }
                        return;
                    }
                    if (Session.Peers.Values.Any(p => p.Ready)) { Check("joiner connected and loaded the colony", true); Next(); }
                    else Timeout(240, "joiner ready");
                    break;
                case 5: // T1: worlds match after join
                    if (WaitMatch(new[] { Kind.Module, Kind.Connection, Kind.Component, Kind.Character, Kind.Ship }, 30f, "join: same objects on both sides"))
                    {
                        Next();
                        if (sScenario == "ships") { sStep = 20; }
                    }
                    break;
                // ---------------------------------------------------- ships scenario
                case 20: // landing pads, then a colonist ship
                    if (First())
                    {
                        sPad1 = PlaceAndBuild("ModuleTypeLandingPad", 2, sBase, 30f);
                        sPad2 = PlaceAndBuild("ModuleTypeLandingPad", 2, sPad1.getPosition(), 30f);
                        sCharsBefore = Registry.Characters.Count;
                        sShip = LandingShip.create<ColonistShip>(sPad1, LandingShip.Size.Regular);
                        Log.Info("bot: colonist ship " + sShip.getId() + " coming");
                        return;
                    }
                    if (sShip != null && !sShip.isDestroyed() && ShipRec.From(sShip).State >= (int)LandingShip.State.Landed)
                    {
                        var e = ParseEntries(Get(sRemote, "ships"));
                        var mine = ShipRec.From(sShip);
                        bool ok = e.TryGetValue(sShip.getId().ToString(), out var t) && Mathf.Abs(float.Parse(t[1]) - mine.Pos.x) < 1.5f &&
                                  Mathf.Abs(float.Parse(t[3]) - mine.Pos.z) < 1.5f && Mathf.Abs(float.Parse(t[2]) - mine.Pos.y) < 1.5f;
                        if (ok && sT - sRemoteAt < 1f) { Check("landing ship flew in and landed in the same place on the joiner", true, $"state host {mine.State} joiner {t[0]}"); Next(); }
                        else Timeout(60, "landing ship position on the joiner");
                    }
                    else Timeout(150, "colonist ship landed on the host");
                    break;
                case 21: // its colonists exist once on each side
                    if (Registry.Characters.Count > sCharsBefore && sStepT > 5f)
                    {
                        if (WaitMatch(new[] { Kind.Character }, 30f, "ship's new colonists exist once on each side (no duplicates)")) Next();
                    }
                    else Timeout(90, "ship's colonists spawned on the host");
                    break;
                case 22: // merchant ship on the second pad, the joiner trades with it
                    if (First())
                    {
                        sShip = LandingShip.create<MerchantShip>(sPad2, LandingShip.Size.Regular);
                        sMerchantWaiting = true;
                        return;
                    }
                    if (sMerchantWaiting && sShip is MerchantShip ms && ms.canTrade())
                    {
                        sMerchantWaiting = false;
                        sStepT = 0f;
                        SendProbe("do", "trade " + ms.getId());
                        return;
                    }
                    if (!sMerchantWaiting)
                    {
                        var m = (MerchantShip)sShip;
                        bool agreed = Convert.ToInt32(fTradeState.GetValue(m)) == 1 && fNeeded.GetValue(m) != null;
                        if (agreed) { Check("joiner's trade is agreed on the host (colonists start loading)", true, ((ProductAmounts)fNeeded.GetValue(m)).getTotalAmount() + " items to deliver"); Next(); }
                        else Timeout(20, "joiner's trade agreed on the host");
                    }
                    else Timeout(150, "merchant ship ready to trade");
                    break;
                case 23: // notifications reach the joiner
                    if (First()) { Singleton<MessageLog>.getInstance().addMessage(new Message("coop test notice 42", ResourceList.StaticIcons.MessageAlert, 0)); return; }
                    if (Get(sRemote, "msgs").Contains("coop test notice 42")) { Check("host notification shown on the joiner", true); Next(); }
                    else Timeout(10, "host notification shown on the joiner");
                    break;
                case 24: // landing permissions: joiner edits, host follows; host edits, joiner follows
                    if (First()) { SendProbe("do", "toggle-merchants"); return; }
                    if (!Singleton<LandingShipManager>.getInstance().getLandingPermissions().getMerchantRefBool().get())
                    {
                        Singleton<LandingShipManager>.getInstance().getLandingPermissions().getVisitorRefBool().set(false);
                        Check("joiner's landing permission change reaches the host", true);
                        Next();
                    }
                    else Timeout(10, "joiner's landing permission change reaches the host");
                    break;
                case 25:
                    if (Get(sRemote, "land.visitors") == "0") { Check("host's landing permission change reaches the joiner", true); sStep = 14; sEntered = false; sStepT = 0; }
                    else Timeout(10, "host's landing permission change reaches the joiner");
                    break;
                case 6: // T2: host builds a module, joiner sees it
                    if (First())
                    {
                        sModulesBefore = Module.getCount();
                        PlaceAndBuild("ModuleTypeSolarPanel", 0, sBase, 26f, build: false);
                        return;
                    }
                    if (WaitMatch(new[] { Kind.Module }, 20f, "host-built module appears on the joiner", () => Module.getCount() == sModulesBefore + 1)) Next();
                    break;
                case 7: // T3: joiner places a module through its own placement code
                    if (First())
                    {
                        sModulesBefore = Module.getCount();
                        sClientSpot = FindFreeSpot("ModuleTypeWaterExtractor", 0, sM1 != null ? sM1.getPosition() : sBase, 18f);
                        SendProbe("do", $"place-module ModuleTypeWaterExtractor 0 {sClientSpot.x:F2} {sClientSpot.z:F2}");
                        return;
                    }
                    if (WaitMatch(new[] { Kind.Module }, 25f, "joiner-placed module built by the host and mirrored back",
                        () => Module.getCount() == sModulesBefore + 1 && Construction.getCount() > 0 &&
                              Registry.Constructions.OfType<Module>().Any(m => m.getModuleType().GetType().Name == "ModuleTypeWaterExtractor" && (m.getPosition() - sClientSpot).magnitude < 1f)))
                        Next();
                    break;
                case 8: // T4: joiner links two modules
                    if (First())
                    {
                        sConnectionsBefore = Registry.Constructions.Count(c => c is Connection);
                        var water = Registry.Constructions.OfType<Module>().FirstOrDefault(m => m.getModuleType().GetType().Name == "ModuleTypeWaterExtractor");
                        if (water != null && sM1 != null) SendProbe("do", $"link {water.getId()} {sM1.getId()}");
                        else { Check("link: modules exist", false); Next(); }
                        return;
                    }
                    if (WaitMatch(new[] { Kind.Connection }, 20f, "joiner-made connection exists on both sides",
                        () => Registry.Constructions.Count(c => c is Connection) == sConnectionsBefore + 1)) Next();
                    break;
                case 9: // T5: joiner changes game speed
                    if (First()) { SendProbe("do", "faster"); return; }
                    if (Singleton<TimeManager>.getInstance().getTimeScale() == 2f)
                    {
                        if (WaitKey("speed", "2", 10f, "joiner's speed change reaches host and comes back")) Next();
                    }
                    else Timeout(10, "joiner's speed change reaches the host");
                    break;
                case 10: // T6: construction progress mirrors (colonists build the new modules for real)
                    if (First()) { ForceProgressAll(0.5f); return; }
                    if (WaitProgressMatch(20f)) Next();
                    break;
                case 11: // T7: colonists move on the joiner like on the host
                    if (First()) { sCharsAtStart = ParseEntries(Get(LocalDigest(), "chars")); return; }
                    if (WaitCharsMatch(60f)) Next();
                    break;
                case 12: // T8: resources (positions and holders) match after the host moves one and stores another
                    if (First())
                    {
                        Registry.Collect(Kind.Resource, sScratch);
                        var ground = sScratch.Values.Cast<Resource>().Where(x => { ResourcePlaces.Where(x, out var h, out _, out _); return h == Holder.None && x.getState() == Resource.State.Idle; }).ToList();
                        sMoved = ground.FirstOrDefault();
                        sStored = ground.Skip(1).FirstOrDefault();
                        if (sMoved != null) sMoved.setPosition(sMoved.getPosition() + new Vector3(4f, 0f, 3f));
                        var slot = sStorage?.findStorageSlot(sStorage.getPosition());
                        if (sStored != null && slot != null) slot.addResource(sStored);
                        Log.Info($"bot: moved resource {sMoved?.getId()}, stored {sStored?.getId()} (slot {(slot != null)})");
                        if (sMoved == null || sStored == null || slot == null) { Check("resource test setup (2 loose resources, a storage slot)", false); Finish(); }
                        return;
                    }
                    if (sStepT > 1f && WaitResourcesMatch(30f, sMoved.getId(), sStored.getId())) Next();
                    break;
                case 13: // T9: census repairs a missing object (drop one on the joiner on purpose)
                    if (First()) { SendProbe("do", "drop-a-resource"); return; }
                    {
                        string dropped = Get(sRemote, "dropped");
                        if (dropped == "") { Timeout(10, "joiner dropped a resource for the census test"); break; }
                        if (dropped == "none") { Check("joiner dropped a resource for the census test", false); Finish(); break; }
                        bool back = ("," + Get(sRemote, "ids.Resource") + ",").Contains("," + dropped + ",");
                        if (back && WaitMatch(new[] { Kind.Resource }, 20f, "census restores the resource the joiner lost (id " + dropped + ")")) Next();
                        else Timeout(20, "census restores the resource the joiner lost (id " + dropped + ")");
                    }
                    break;
                case 14: // T10: joiner leaves; host keeps running
                    if (First()) { SendProbe("end", ""); return; }
                    if (!Session.Peers.Any()) { Check("joiner left cleanly, host still in its colony", GameAccess.Game() != null); Finish(); }
                    else Timeout(20, "joiner left");
                    break;
            }
        }

        private static bool WaitMatch(Kind[] kinds, float timeout, string name, Func<bool> hostCond = null)
        {
            if (hostCond != null && !hostCond()) return Timeout(timeout, name + " (host side)") && false;
            var local = LocalDigest();
            var diffs = new List<string>();
            foreach (var k in kinds)
                if (Get(local, "ids." + k) != Get(sRemote, "ids." + k)) diffs.Add($"{k}: host {Get(local, "count." + k)} joiner {Get(sRemote, "count." + k)}");
            if (diffs.Count == 0 && sRemoteAt > 0 && sT - sRemoteAt < 2f)
            {
                Check(name, true, string.Join(", ", kinds.Select(k => k + " " + Get(local, "count." + k))));
                return true;
            }
            if (sStepT >= timeout)
            {
                Check(name, false, string.Join("; ", diffs));
                return true;
            }
            return false;
        }

        private static bool WaitKey(string key, string want, float timeout, string name)
        {
            if (Get(sRemote, key) == want) { Check(name, true); return true; }
            if (sStepT >= timeout) { Check(name, false, $"joiner {key}={Get(sRemote, key)}"); return true; }
            return false;
        }

        private static bool WaitProgressMatch(float timeout)
        {
            // entries: id:type:state:x:z:progress. Same type/state/place, progress within 0.1
            // (the joiner's digest is up to ~0.5 s old while colonists keep building).
            var mine = ParseEntries(Get(LocalDigest(), "modules"));
            var theirs = ParseEntries(Get(sRemote, "modules"));
            int off = 0, building = 0;
            string example = null;
            foreach (var kv in mine)
            {
                if (kv.Value[1] != "3") building++;
                if (!theirs.TryGetValue(kv.Key, out var t)) { off++; example = example ?? kv.Key + " missing"; continue; }
                bool same = kv.Value[0] == t[0] && kv.Value[1] == t[1] &&
                            Mathf.Abs(float.Parse(kv.Value[2]) - float.Parse(t[2])) < 0.5f && Mathf.Abs(float.Parse(kv.Value[3]) - float.Parse(t[3])) < 0.5f &&
                            Mathf.Abs(float.Parse(kv.Value[4]) - float.Parse(t[4])) <= 0.1f;
                if (!same) { off++; example = example ?? $"{kv.Key} host {string.Join(":", kv.Value)} joiner {string.Join(":", t)}"; }
            }
            string name = "construction state and progress match";
            if (mine.Count > 0 && off == 0 && theirs.Count == mine.Count && sT - sRemoteAt < 1f) { Check(name, true, $"{mine.Count} modules, {building} not finished"); return true; }
            if (sStepT >= timeout) { Check(name, false, $"{off} of {mine.Count} off, e.g. {example}"); return true; }
            return false;
        }

        private static bool WaitCharsMatch(float timeout)
        {
            var mine = ParseEntries(Get(LocalDigest(), "chars"));
            var theirs = ParseEntries(Get(sRemote, "chars"));
            // the joiner's digest is up to ~0.5 s old and walkers move ~3 m/s: allow 5 m
            int far = 0, moved = 0;
            float worst = 0f;
            foreach (var kv in mine)
            {
                if (sCharsAtStart != null && sCharsAtStart.TryGetValue(kv.Key, out var s0))
                {
                    float mx = float.Parse(kv.Value[0]) - float.Parse(s0[0]), mz = float.Parse(kv.Value[1]) - float.Parse(s0[1]);
                    if (mx * mx + mz * mz > 64f) moved++;   // walked 8 m or more during the step
                }
                if (!theirs.TryGetValue(kv.Key, out var t)) { far++; continue; }
                float dx = float.Parse(kv.Value[0]) - float.Parse(t[0]), dz = float.Parse(kv.Value[1]) - float.Parse(t[1]);
                worst = Mathf.Max(worst, Mathf.Sqrt(dx * dx + dz * dz));
                if (dx * dx + dz * dz > 25f || kv.Value[2] != t[2]) far++;
            }
            string name = "colonists are where the host has them (within 5 m, one walked 8 m+)";
            if (mine.Count > 0 && far == 0 && moved > 0 && sT - sRemoteAt < 1f) { Check(name, true, $"{mine.Count} characters, {moved} moved, worst {worst:F1} m"); return true; }
            if (sStepT >= timeout) { Check(name, false, $"{far} of {mine.Count} off, {moved} moved, worst {worst:F1} m"); return true; }
            return false;
        }

        private static bool WaitResourcesMatch(float timeout, params int[] mustMatch)
        {
            var mine = ParseEntries(Get(LocalDigest(), "res"));
            var theirs = ParseEntries(Get(sRemote, "res"));
            int off = 0;
            string example = null;
            foreach (var kv in mine)
            {
                if (!theirs.TryGetValue(kv.Key, out var t)) { off++; example = example ?? kv.Key + " missing"; continue; }
                bool sameHolder = kv.Value[0] == t[0] && kv.Value[1] == t[1] && kv.Value[2] == t[2];
                bool onGround = kv.Value[0] == "0";
                float dx = float.Parse(kv.Value[3]) - float.Parse(t[3]), dz = float.Parse(kv.Value[4]) - float.Parse(t[4]);
                if (!sameHolder || (onGround && dx * dx + dz * dz > 1f)) { off++; example = example ?? $"{kv.Key} host {string.Join(":", kv.Value)} joiner {string.Join(":", t)}"; }
            }
            foreach (var id in mustMatch)
            {
                // the two resources the host just moved/stored: the joiner must show the new place
                if (!theirs.TryGetValue(id.ToString(), out var t) || !mine.TryGetValue(id.ToString(), out var m)) { off++; continue; }
                if (t[0] != m[0] || t[1] != m[1] || t[2] != m[2] || Mathf.Abs(float.Parse(t[3]) - float.Parse(m[3])) > 0.5f || Mathf.Abs(float.Parse(t[4]) - float.Parse(m[4])) > 0.5f) off++;
            }
            if (mine.Count > 0 && off == 0 && sT - sRemoteAt < 1f) { Check("resources have the same holder and place", true, mine.Count + " resources, incl. one moved and one stored by the host"); return true; }
            if (sStepT >= timeout) { Check("resources have the same holder and place", false, $"{off} of {mine.Count} off, e.g. {example}"); return true; }
            return false;
        }

        private static Dictionary<string, string[]> ParseEntries(string s)
        {
            var d = new Dictionary<string, string[]>();
            if (string.IsNullOrEmpty(s)) return d;
            foreach (var e in s.Split(';'))
            {
                var parts = e.Split(':');
                if (parts.Length > 1) d[parts[0]] = parts.Skip(1).ToArray();
            }
            return d;
        }

        // ------------------------------------------------------------ host helpers (test setup only)
        private static Vector3 FindColonyShipPosition()
        {
            var ship = Registry.Ships.FirstOrDefault();
            var p = ship != null ? ship.getPosition() : Singleton<TerrainGenerator>.getInstance().getCenter();
            p.y = Singleton<TerrainGenerator>.getInstance().getFloorHeight();
            return p;
        }

        private static Vector3 FindFreeSpot(string typeName, int size, Vector3 near, float minDist)
        {
            var type = TypeList<ModuleType, ModuleTypeList>.find(typeName);
            size = Mathf.Clamp(size, type.getMinSize(), type.getMaxSize());
            float floor = Singleton<TerrainGenerator>.getInstance().getFloorHeight();
            var probe = Module.create(near, size, type);
            try
            {
                for (float r = minDist; r < 120f; r += 3f)
                    for (int a = 0; a < 24; a++)
                    {
                        float ang = a * Mathf.PI * 2f / 24f;
                        var p = new Vector3(near.x + Mathf.Cos(ang) * r, floor, near.z + Mathf.Sin(ang) * r);
                        probe.setPosition(p);
                        if (probe.canPlaceModule(p, Vector3.up, Module.ValidSizes[size])) return p;
                    }
            }
            finally { probe.destroy(); }
            return near + new Vector3(minDist, 0, 0);
        }

        private static Module PlaceAndBuild(string typeName, int size, Vector3 near, float minDist, bool build = true)
        {
            var type = TypeList<ModuleType, ModuleTypeList>.find(typeName);
            size = Mathf.Clamp(size, type.getMinSize(), type.getMaxSize());
            var spot = FindFreeSpot(typeName, size, near, minDist);
            var m = Module.create(spot, size, type);
            m.setValidPosition(true);
            m.setPositionY(Singleton<TerrainGenerator>.getInstance().getFloorHeight());
            m.onUserPlaced();
            if (build) { m.startBuilding(); m.updateBuild(1e6f); }
            Log.Info($"bot placed {typeName} at {spot}");
            return m;
        }

        private static void ForceBuildAll()
        {
            foreach (var c in Registry.Constructions.ToList())
                if (!c.isBuilt() && Registry.StateOf(c) != BuildableState.Placing) { c.startBuilding(); c.updateBuild(1e6f); }
        }

        private static void ForceProgressAll(float to)
        {
            foreach (var c in Registry.Constructions.ToList())
            {
                var st = Registry.StateOf(c);
                if (st == BuildableState.Placing || st == BuildableState.Built) continue;
                if (st == BuildableState.Placed) c.startBuilding();
                float cur = BuildRec.ProgressOf(c);
                if (to > cur) BuildRec.ProgressIndicator(c).setValue(to);
            }
        }

        // ------------------------------------------------------------ client bot
        private static int sCStep;
        private static float sCStepT, sDigestTimer;
        private static bool sClientEnd;
        private static readonly Queue<string> sClientQueue = new Queue<string>();
        private static string sDropped = "";
        private static readonly FieldInfo fActiveModule = AccessTools.Field(typeof(GameStateGame), "mActiveModule");
        private static readonly MethodInfo mPlaceModule = AccessTools.Method(typeof(GameStateGame), "placeModule");
        private static readonly MethodInfo mIncreaseSpeed = AccessTools.Method(typeof(GameStateGame), "increaseSpeed");

        private static void ClientStep(float dt)
        {
            sCStepT += dt;
            var state = GameAccess.CurrentState();
            switch (sCStep)
            {
                case 0:
                    if (state != null && state.isTitleState() && sCStepT > 2f)
                    {
                        if (TestEnv.Get("COOP_FAKE_MOD") != null)
                        {
                            // mutate our own version info so the host must refuse us
                            VersionInfo.Local.Mod = TestEnv.Get("COOP_FAKE_MOD");
                        }
                        int port = int.Parse(TestEnv.Get("COOP_PORT") ?? "47610");
                        string err = Session.StartClient(TcpTransport.Connect("127.0.0.1", port));
                        Check("joiner started connecting", err == null, err);
                        sCStep++; sCStepT = 0;
                    }
                    else if (sCStepT > 120f) { Check("joiner reached the title screen", false); Finish(); }
                    break;
                case 1:
                    if (TestEnv.Get("COOP_FAKE_MOD") != null)
                    {
                        if (sRefusal != null)
                        {
                            Check("joiner with a different mod version was refused with a readable reason", sRefusal.Contains("Planetbase Co-op version"), sRefusal);
                            Check("refused joiner stayed out of the colony", GameAccess.Game() == null);
                            Finish();
                        }
                        else if (sCStepT > 60f) { Check("joiner refused", false, "no refusal"); Finish(); }
                        return;
                    }
                    if (Mirror.Active)
                    {
                        Check("joiner loaded the host's colony and froze its simulation", true);
                        sCStep++; sCStepT = 0;
                    }
                    else if (sCStepT > 240f) { Check("joiner loaded the colony", false, Session.Status); Finish(); }
                    break;
                case 2:
                    if (!Session.Active)
                    {
                        bool ended = Session.Status.Contains("test finished");
                        if (!ended) Check("joiner session still up", false, Session.Status);
                        else Check("joiner saw no failed creates", WorldApply.CreateFailures == 0, "failures " + WorldApply.CreateFailures);
                        Finish();
                        return;
                    }
                    while (sClientQueue.Count > 0) ClientDo(sClientQueue.Dequeue());
                    sDigestTimer += dt;
                    if (sDigestTimer >= 0.5f) { sDigestTimer = 0; SendProbe("digest", Encode(LocalDigest())); }
                    if (sClientEnd)
                    {
                        Check("joiner saw no failed creates", WorldApply.CreateFailures == 0, "failures " + WorldApply.CreateFailures);
                        Finish();
                    }
                    break;
            }
        }

        // the host asks the joiner bot to act like a player
        private static void ClientDo(string payload)
        {
            Log.Info("bot do: " + payload);
            var a = payload.Split(' ');
            var game = GameAccess.Game();
            switch (a[0])
            {
                case "place-module":
                {
                    var type = TypeList<ModuleType, ModuleTypeList>.find(a[1]);
                    int size = int.Parse(a[2]);
                    float floor = Singleton<TerrainGenerator>.getInstance().getFloorHeight();
                    var pos = new Vector3(float.Parse(a[3]), floor, float.Parse(a[4]));
                    // what tryPlaceModule leaves behind: a preview module, valid, then the click commits it
                    var preview = Module.create(pos, size, type);
                    preview.setValidPosition(preview.canPlaceModule(pos, Vector3.up, Module.ValidSizes[size]));
                    fActiveModule.SetValue(game, preview);
                    mPlaceModule.Invoke(game, null);
                    break;
                }
                case "link":
                {
                    var m1 = Registry.Find(Kind.Module, int.Parse(a[1])) as Module;
                    var m2 = Registry.Find(Kind.Module, int.Parse(a[2])) as Module;
                    if (m1 != null && m2 != null) Module.linkModules(m1, m2, true);
                    break;
                }
                case "faster":
                    mIncreaseSpeed.Invoke(game, null);
                    break;
                case "trade":
                {
                    var ship = Registry.Find(Kind.Ship, int.Parse(a[1])) as MerchantShip;
                    if (ship == null) { Log.Warn("bot: no merchant ship " + a[1]); break; }
                    var win = new GuiMerchantWindow(ship);
                    var mStock = (ProductAmounts)fWinMStock.GetValue(win);
                    var mTrade = (ProductAmounts)fWinMTrade.GetValue(win);
                    var bStock = (ProductAmounts)fWinBStock.GetValue(win);
                    var bTrade = (ProductAmounts)fWinBTrade.GetValue(win);
                    ProductAmount buy = null;
                    foreach (var p in mStock.clone()) if (p.getAmount() > 0) { buy = p; break; }
                    if (buy != null) { mStock.remove(buy.getProduct(), 1); mTrade.add(buy.getProduct(), 1); }
                    float need = mTrade.getValue() * (1f + (int)fCommission.GetValue(ship) / 100f) + 1f;
                    foreach (var pa in bStock.clone())
                        for (int i = 0; i < pa.getAmount() && bTrade.getValue() < need; i++)
                            if (pa.getProduct().markForTrading(ship)) { bStock.remove(pa.getProduct(), 1); bTrade.add(pa.getProduct(), 1); }
                    Log.Info($"bot: trading {mTrade.getTotalAmount()} for {bTrade.getTotalAmount()} (value {bTrade.getValue()} vs {need:F0})");
                    fWinResult.SetValue(win, Enum.ToObject(fWinResult.FieldType, 0));   // Trade
                    win.destroy();
                    break;
                }
                case "toggle-merchants":
                {
                    var rb = Singleton<LandingShipManager>.getInstance().getLandingPermissions().getMerchantRefBool();
                    rb.set(!rb.get());   // what the window's checkbox does
                    break;
                }
                case "drop-a-resource":
                {
                    Registry.Collect(Kind.Resource, sScratch);
                    var r = sScratch.Values.Cast<Resource>().FirstOrDefault(x => { ResourcePlaces.Where(x, out var h, out _, out _); return h == Holder.None; })
                            ?? sScratch.Values.Cast<Resource>().FirstOrDefault();
                    if (r != null) { Log.Info("bot: destroying resource " + r.getId() + " locally"); sDropped = r.getId().ToString(); ResourcePlaces.Release(r); r.destroy(); }
                    else sDropped = "none";
                    break;
                }
            }
        }

        public static void OnHostDigestRequest()
        {
            if (sMode == "client" && Session.Active) SendProbe("digest", Encode(LocalDigest()));
        }
    }
}
