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
            // hazards scenario
            Registry.Collect(Kind.Module, sScratch);
            d["stores"] = string.Join(";", sScratch.Values.Cast<Module>().Where(m => m.getPowerStorageIndicator() != null || m.getWaterStorageIndicator() != null)
                .OrderBy(m => m.getId()).Select(m => $"{m.getId()}:{Ratio(m.getPowerStorageIndicator()):F3}:{Ratio(m.getWaterStorageIndicator()):F3}:{Fill(m, "Gauge"):F3}:{Fill(m, "Tank"):F3}"));
            d["colonists"] = Singleton<Colony>.getInstance().getColonistCount().ToString();
            d["meteors"] = string.Join(";", MeteorObjects().Select(g => $"{g.name.Replace(' ', '_')}:{g.transform.position.x:F1}:{g.transform.position.y:F1}:{g.transform.position.z:F1}"));
            d["storms"] = Hazards.StormDigest();
            d["sandfx"] = fSandParticles.GetValue(Singleton<DisasterManager>.getInstance().getSandstorm()) != null ? "1" : "0";
            d["tradewin"] = TradeShare.Digest(TradeShare.OpenWindow);
            d["caught"] = ErrorGuard.Caught.ToString();
            d["fatal"] = (fFatal.GetValue(null) as string) ?? "none";
            d["crawl"] = sCrawlReport;
            d["ghosts"] = Ghosts.Digest();
            return d;
        }

        private static readonly FieldInfo fSandParticles = AccessTools.Field(typeof(Sandstorm), "mParticles");
        private static readonly FieldInfo fFatal = AccessTools.Field(typeof(ErrorManager), "mFatalErrorString");
        private static readonly FieldInfo fMeteorList = AccessTools.Field(typeof(MeteorManager), "mMeteors");
        private static readonly FieldInfo fSandTime = AccessTools.Field(typeof(Sandstorm), "mTime");
        private static readonly FieldInfo fSandDuration = AccessTools.Field(typeof(Sandstorm), "mSandstormTime");

        private static float Ratio(Indicator i) => i == null ? -1f : i.getRatio();

        // what the accumulator's gauge / the tank's level shows on the model (-1: no such part)
        private static float Fill(Module m, string tag)
        {
            var go = m.getSelectionModel()?.findTaggedObject(tag);
            var rend = go?.GetComponent<Renderer>();
            if (rend == null) return -1f;
            var mpb = new MaterialPropertyBlock();
            rend.GetPropertyBlock(mpb);
            return mpb.GetFloat(UnityEngine.ShaderProperty.FillRatio);
        }

        private static List<GameObject> MeteorObjects() =>
            ((List<GameObject>)fMeteorList.GetValue(Singleton<MeteorManager>.getInstance()) ?? new List<GameObject>()).Where(g => g != null).ToList();

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
                        else if (sScenario == "hazards") { sStep = 40; }
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
                        // joiner must have the ship landed too (state record), at the host's place (position records)
                        bool ok = e.TryGetValue(sShip.getId().ToString(), out var t) && int.Parse(t[0]) >= (int)LandingShip.State.Landed &&
                                  Mathf.Abs(float.Parse(t[1]) - mine.Pos.x) < 1.5f &&
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
                // ---------------------------------------------------- hazards scenario (playtest 1 bugs)
                case 40: // accumulator and water tank: stored amount and the gauge/tank on the model
                    if (First())
                    {
                        sAccu = PlaceAndBuild("ModuleTypePowerCollector", 0, sBase, 26f);
                        sTank = PlaceAndBuild("ModuleTypeWaterTank", 0, sAccu.getPosition(), 16f);
                        Singleton<TimeManager>.getInstance().pause();   // keep the stored amounts still while comparing
                        sAccu.getPowerStorageIndicator().setValue(0f);  // the playtest case: empty, shown full
                        sTank.getWaterStorageIndicator().setValue(sTank.getWaterStorageIndicator().getMax() * 0.6f);
                        return;
                    }
                    if (sStepT > 1f && StoresMatch(sStepT >= 15f, "empty accumulator and 60% tank look the same on the joiner")) { sStep = 41; sEntered = false; sStepT = 0; }
                    break;
                case 41:
                    if (First()) { sAccu.getPowerStorageIndicator().setValue(sAccu.getPowerStorageIndicator().getMax() * 0.37f); return; }
                    if (sStepT > 1f && StoresMatch(sStepT >= 15f, "37% accumulator shows 37% on the joiner")) { Singleton<TimeManager>.getInstance().unpause(); Next(); }
                    break;
                case 42: // colonist count on the joiner's top panel
                    {
                        string mine = Singleton<Colony>.getInstance().getColonistCount().ToString(), theirs = Get(sRemote, "colonists");
                        if (theirs == mine && sT - sRemoteAt < 2f) { Check("joiner's colonist counter matches the host", true, mine); Next(); }
                        else if (sStepT > 15f) { Check("joiner's colonist counter matches the host", false, $"host {mine} joiner {theirs}"); Next(); }
                    }
                    break;
                case 43: // a meteor in open ground: same rock, same path on the joiner
                    LogMeteors();
                    if (First())
                    {
                        var mm = Singleton<MeteorManager>.getInstance();
                        sAimSeed = ((List<int>)fMeteorSeedList.GetValue(mm))[0];
                        sAimTarget = FindFreeSpot("ModuleTypeSolarPanel", 0, sBase, 70f);
                        mm.spawnMeteor(sAimTarget);
                        sMeteorName = sAimName = MeteorObjects().Last().name;
                        return;
                    }
                    {
                        var host = MeteorObjects().FirstOrDefault(g => g.name == sMeteorName);
                        var e = ParseEntries(Get(sRemote, "meteors"));
                        string key = sMeteorName.Replace(' ', '_');
                        if (host != null && e.TryGetValue(key, out var t) && sT - sRemoteAt < 1f && sStepT < 3f)
                        {
                            var hp = host.transform.position;
                            float dx = hp.x - float.Parse(t[0]), dy = hp.y - float.Parse(t[1]), dz = hp.z - float.Parse(t[2]);
                            float dist = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                            // the joiner's digest is up to 0.5 s old and the rock falls ~100 m/s
                            Check("meteor seen by the joiner on the host's path", dist < 60f, $"{sMeteorName}, {dist:F1} m apart (digest age {sT - sRemoteAt:F2} s)");
                            Next();
                        }
                        else if (sStepT > 6f) { Check("meteor seen by the joiner", false, $"joiner meteors: {Get(sRemote, "meteors")}"); Next(); }
                    }
                    break;
                case 44: // a meteor shot down (removed) on the host disappears on the joiner
                    if (First())
                    {
                        Singleton<MeteorManager>.getInstance().spawnMeteor(FindFreeSpot("ModuleTypeSolarPanel", 0, sBase, 90f));
                        sMeteorName = MeteorObjects().Last().name;
                        return;
                    }
                    if (sStepT > 0.8f && sStepT < 5f && sEntered && !sRemoved)
                    {
                        string key = sMeteorName.Replace(' ', '_');
                        if (ParseEntries(Get(sRemote, "meteors")).ContainsKey(key) && sT - sRemoteAt < 1f)
                        {
                            var go = MeteorObjects().FirstOrDefault(g => g.name == sMeteorName);
                            if (go != null) { Singleton<MeteorManager>.getInstance().remove(go); sRemoved = true; sStepT = 0f; }
                        }
                    }
                    else if (sRemoved)
                    {
                        if (!ParseEntries(Get(sRemote, "meteors")).ContainsKey(sMeteorName.Replace(' ', '_')) && sT - sRemoteAt < 1f && sStepT > 0.5f)
                        { Check("meteor removed on the host (laser) is gone on the joiner", true); Next(); }
                        else if (sStepT > 6f) { Check("meteor removed on the host is gone on the joiner", false, Get(sRemote, "meteors")); Next(); }
                    }
                    else if (sStepT > 8f) { Check("second meteor reached the joiner", false); Next(); }
                    break;
                case 45: // a meteor on a module: damage comes from the host only (one notification on the joiner)
                    LogMeteors();
                    if (!sAimed)
                    {
                        // gravity bends a meteor's path, so it lands off its target. The same seed flies
                        // the same path shifted by the target: measure the last miss, aim that much off.
                        var last = MeteorObjects().FirstOrDefault(g => g.name == sAimName);
                        if (last != null && last.GetComponent<Rigidbody>() == null)
                        {
                            Vector3 miss = last.transform.position - sAimTarget;
                            miss.y = 0f;
                            var mm = Singleton<MeteorManager>.getInstance();
                            ((List<int>)fMeteorSeedList.GetValue(mm)).Insert(0, sAimSeed);
                            sHitText = StringList.get("message_meteor_hit", sM1.getName());
                            sAimTarget = sM1.getPosition() - miss;
                            mm.spawnMeteor(sAimTarget);
                            sAimName = MeteorObjects().Last().name;
                            sAimed = true;
                            sAimTries++;
                            sStepT = 0f;
                            Log.Info($"bot: meteor try {sAimTries} aimed at {sM1.getName()} {sM1.getPosition()} (miss of the same seed: {miss})");
                        }
                        else Timeout(25, "previous meteor landed");
                        return;
                    }
                    {
                        int hostHits = Count(Get(LocalDigest(), "msgs"), sHitText), joinerHits = Count(Get(sRemote, "msgs"), sHitText);
                        var cur = MeteorObjects().FirstOrDefault(g => g.name == sAimName);
                        bool landed = cur != null && cur.GetComponent<Rigidbody>() == null;
                        if (hostHits > 0 && sStepT > 6f)
                        {
                            Check("meteor hit: the joiner shows the host's notification once (no local damage)", joinerHits == hostHits, $"host {hostHits} joiner {joinerHits}, try {sAimTries}");
                            Next();
                        }
                        else if (hostHits == 0 && landed && sStepT > 4f)
                        {
                            if (sAimTries < 4) sAimed = false;   // landed beside it: correct the aim
                            else { Check("meteor hit the module on the host", false, "missed 4 times"); Next(); }
                        }
                        else if (sStepT > 25f) { Check("meteor hit the module on the host", false, "no landing"); Next(); }
                    }
                    break;
                case 46: // sandstorm starts and ends on the joiner with the host's
                    if (First()) { Singleton<DisasterManager>.getInstance().getSandstorm().trigger(); return; }
                    if (Get(sRemote, "storms").StartsWith("1:") && Get(sRemote, "sandfx") == "1" && sT - sRemoteAt < 1f)
                    {
                        Check("sandstorm shows on the joiner (state and particles)", true, Get(sRemote, "storms"));
                        var ss = Singleton<DisasterManager>.getInstance().getSandstorm();
                        fSandTime.SetValue(ss, (float)fSandDuration.GetValue(ss) + 1f);   // ends on the next host update
                        Next();
                    }
                    else if (sStepT > 8f) { Check("sandstorm shows on the joiner", false, $"storms {Get(sRemote, "storms")} fx {Get(sRemote, "sandfx")}"); Next(); }
                    break;
                case 47:
                    if (Get(sRemote, "storms").StartsWith("0:") && Get(sRemote, "sandfx") == "0" && sT - sRemoteAt < 1f && sStepT > 0.5f)
                    {
                        string txt = StringList.get("message_sandstorm_now");
                        int h = Count(Get(LocalDigest(), "msgs"), txt), j = Count(Get(sRemote, "msgs"), txt);
                        Check("sandstorm ends on the joiner; its notification shown once", h == j && h > 0, $"host {h} joiner {j}");
                        Next();
                    }
                    else if (sStepT > 8f) { Check("sandstorm ends on the joiner", false, Get(sRemote, "storms")); Next(); }
                    break;
                case 48: // shared trade basket
                    if (First())
                    {
                        sPad1 = PlaceAndBuild("ModuleTypeLandingPad", 2, sBase, 40f);
                        sShip = LandingShip.create<MerchantShip>(sPad1, LandingShip.Size.Regular);
                        sMerchantWaiting = true;
                        return;
                    }
                    if (sMerchantWaiting && sShip is MerchantShip ms0 && ms0.canTrade())
                    {
                        sMerchantWaiting = false;
                        GameAccess.Gui().setWindow(new GuiMerchantWindow(ms0));   // host opens the window
                        SendProbe("do", "trade-open " + ms0.getId());              // so does the joiner
                        sStepT = 0f;
                        return;
                    }
                    if (!sMerchantWaiting && sStepT > 1.5f && sTradeStage == 0)
                    {
                        // host clicks a merchant item into the merchant's transaction box
                        var win = TradeShare.OpenWindow;
                        var stock = (ProductAmounts)fWinMStock.GetValue(win);
                        Product first = null;
                        foreach (var pa in stock) if (pa.getAmount() > 0) { first = pa.getProduct(); break; }
                        Event.current = new Event();
                        win.onMerchantStockClicked(first);
                        sTradeStage = 1; sStepT = 0f;
                        return;
                    }
                    if (sTradeStage == 1)
                    {
                        string mine = TradeShare.Digest(TradeShare.OpenWindow);
                        if (Get(sRemote, "tradewin") == mine && sT - sRemoteAt < 1f) { Check("host's click shows in the joiner's trade window", true, mine); SendProbe("do", "trade-fill-base"); sTradeStage = 2; sStepT = 0f; }
                        else if (sStepT > 8f) { Check("host's click shows in the joiner's trade window", false, $"host {mine} joiner {Get(sRemote, "tradewin")}"); sTradeStage = 2; sStepT = 0f; SendProbe("do", "trade-fill-base"); }
                        return;
                    }
                    if (sTradeStage == 2)
                    {
                        string mine = TradeShare.Digest(TradeShare.OpenWindow);
                        int marked = Registry.Resources.Count(x => x != null && (int)fTraderId.GetValue(x) == sShip.getId());
                        bool hasBase = mine.Split('|').Length == 3 && mine.Split('|')[2] != "";
                        if (hasBase && Get(sRemote, "tradewin") == mine && sT - sRemoteAt < 1f)
                        { Check("joiner's clicks show in the host's trade window (host marks the resources)", marked > 0 || mine.Contains("Coins"), $"{mine}, {marked} marked"); SendProbe("do", "trade-commit"); sTradeStage = 3; sStepT = 0f; }
                        else if (sStepT > 8f) { Check("joiner's clicks show in the host's trade window", false, $"host {mine} joiner {Get(sRemote, "tradewin")}"); Next(); }
                        return;
                    }
                    if (sTradeStage == 3)
                    {
                        var m = (MerchantShip)sShip;
                        bool agreed = Convert.ToInt32(fTradeState.GetValue(m)) == 1 && fNeeded.GetValue(m) != null;
                        if (agreed && TradeShare.OpenWindow == null) { Check("joiner's Trade commits the shared basket; the host's window closes", true); Next(); }
                        else if (sStepT > 10f) { Check("joiner's Trade commits the shared basket", false, $"agreed {agreed}, host window {(TradeShare.OpenWindow != null ? "open" : "closed")}"); Next(); }
                        return;
                    }
                    if (sMerchantWaiting) Timeout(150, "merchant ship ready to trade");
                    break;
                case 49: // a game error on the joiner no longer ends its game; the host's log gets it
                    if (First()) { sReceivedBefore = ErrorGuard.Received; SendProbe("do", "log-error"); return; }
                    if (ErrorGuard.Received > sReceivedBefore && Get(sRemote, "fatal") == "none" && sT - sRemoteAt < 1f && sStepT > 1f)
                    { Check("joiner error caught (no fatal screen), reported to the host", true, Get(sRemote, "caught") + " caught"); Next(); }
                    else if (sStepT > 10f) { Check("joiner error caught and reported", false, $"received {ErrorGuard.Received - sReceivedBefore}, fatal '{Get(sRemote, "fatal")}'"); Next(); }
                    break;
                case 50: // the joiner clicks through every object and window while the host plays at speed
                    if (First())
                    {
                        sCaughtBefore = int.TryParse(Get(sRemote, "caught"), out var c0) ? c0 : 0;
                        sReceivedBefore = ErrorGuard.Received;
                        var tm = Singleton<TimeManager>.getInstance();
                        tm.unpause();
                        tm.increaseSpeed(); tm.increaseSpeed();
                        SendProbe("do", "crawl 60");
                        return;
                    }
                    if (Get(sRemote, "crawl").StartsWith("done") && sT - sRemoteAt < 1f)
                    {
                        int caught = (int.TryParse(Get(sRemote, "caught"), out var c1) ? c1 : 0) - sCaughtBefore;
                        Check("joiner UI crawl: no game errors", caught == 0 && Get(sRemote, "fatal") == "none", $"{Get(sRemote, "crawl")}; {caught} game errors (see host log for reports)");
                        Singleton<TimeManager>.getInstance().setNormalSpeed();
                        Next();
                    }
                    else Timeout(120, "joiner UI crawl finished");
                    break;
                case 51: // placement preview ("ghost"): the host's shows on the joiner, follows it, goes away
                    if (First())
                    {
                        sModulesBefore = Module.getCount();
                        sGhostSpot = FindLinkableSpot("ModuleTypeSolarPanel");
                        var panel = TypeList<ModuleType, ModuleTypeList>.find("ModuleTypeSolarPanel");
                        sHostPreview = Module.create(sGhostSpot, panel.getMinSize(), panel);
                        sHostPreview.setValidPosition(true);
                        fActiveModule.SetValue(GameAccess.Game(), sHostPreview);   // what tryPlaceModule leaves while you aim
                        sGhostStage = 0;
                        return;
                    }
                    {
                        var g = ParseEntries(Get(sRemote, "ghosts"));
                        bool fresh = sT - sRemoteAt < 1f;
                        var at = sHostPreview != null ? sHostPreview.getPosition() : Vector3.zero;
                        bool there = g.TryGetValue("0", out var e) && e[0] == "ModuleTypeSolarPanel" && e[4] == "1" &&
                                     Mathf.Abs(float.Parse(e[2]) - at.x) < 0.3f && Mathf.Abs(float.Parse(e[3]) - at.z) < 0.3f;
                        if (sGhostStage == 0 && there && fresh)
                        {
                            Check("host's placement preview shows on the joiner (type, place, green), not as a building",
                                Get(sRemote, "ids.Module") == Get(LocalDigest(), "ids.Module"), Get(sRemote, "ghosts"));
                            sHostPreview.setPosition(at + new Vector3(6f, 0f, 0f));
                            sHostPreview.setValidPosition(false);
                            sGhostStage = 1; sStepT = 0f;
                        }
                        else if (sGhostStage == 1 && fresh && g.TryGetValue("0", out var e1) && e1[4] == "0" && Mathf.Abs(float.Parse(e1[2]) - at.x) < 0.3f)
                        {
                            Check("the joiner's copy follows the host's preview (moved, turned red)", true, Get(sRemote, "ghosts"));
                            sHostPreview.destroy();
                            sHostPreview = null;
                            fActiveModule.SetValue(GameAccess.Game(), null);
                            sGhostStage = 2; sStepT = 0f;
                        }
                        else if (sGhostStage == 2 && fresh && !g.ContainsKey("0") && sStepT > 0.3f)
                        {
                            Check("host's preview gone: gone on the joiner", Module.getCount() == sModulesBefore, Get(sRemote, "ghosts"));
                            Next();
                        }
                        else if (sStepT > 8f) { Check("host's placement preview on the joiner (stage " + sGhostStage + ")", false, "joiner ghosts: " + Get(sRemote, "ghosts")); Next(); }
                    }
                    break;
                case 52: // the joiner's preview shows on the host; the joiner builds right where its ghost is
                    if (First())
                    {
                        sModulesBefore = Module.getCount();
                        sGhostSpot = FindLinkableSpot("ModuleTypeWaterExtractor");
                        SendProbe("do", $"ghost-start ModuleTypeWaterExtractor {sGhostSpot.x:F2} {sGhostSpot.z:F2}");
                        sGhostStage = 0;
                        return;
                    }
                    {
                        string mine = Ghosts.Digest();
                        if (sGhostStage == 0 && mine.Contains(":ModuleTypeWaterExtractor:") && mine.EndsWith(":1"))
                        {
                            var e = mine.Split(':');
                            bool near = Mathf.Abs(float.Parse(e[3]) - sGhostSpot.x) < 0.3f && Mathf.Abs(float.Parse(e[4]) - sGhostSpot.z) < 0.3f;
                            Check("joiner's placement preview shows on the host, not as a building", near && Module.getCount() == sModulesBefore, mine);
                            SendProbe("do", "ghost-commit");
                            sGhostStage = 1; sStepT = 0f;
                        }
                        else if (sGhostStage == 1 && Module.getCount() == sModulesBefore + 1 && Ghosts.Digest() == "")
                        {
                            bool atSpot = Registry.Constructions.OfType<Module>().Any(m => m.getModuleType() is ModuleTypeWaterExtractor && (m.getPosition() - sGhostSpot).magnitude < 1f);
                            Check("joiner builds where its ghost was shown (the ghost blocks nothing); ghost removed", atSpot);
                            Next();
                        }
                        else if (sStepT > 10f) { Check("joiner's placement preview on the host (stage " + sGhostStage + ")", false, $"host ghosts '{mine}', modules {Module.getCount() - sModulesBefore} new, last result {Commands.LastResult}"); sStep = 14; sEntered = false; sStepT = 0; }
                    }
                    break;
                case 53: // component preview inside a module: host's shows on the joiner, then goes away
                    if (First())
                    {
                        sCompsBefore = Registry.Components.Count;
                        sHostComp = ConstructionComponent.create(sM1, TypeList<ComponentType, ComponentTypeList>.find("Bench"));
                        sHostComp.setPosition(sM1.getPosition() + new Vector3(1.5f, 0f, 0.5f));
                        sHostComp.setValidPosition(true);
                        fPlacedComponent.SetValue(GameAccess.Game(), sHostComp);   // what tryPlaceComponent leaves while you aim
                        sGhostStage = 0;
                        return;
                    }
                    {
                        string want = $"0:Bench:{sM1.getId()}:";
                        string theirs = Get(sRemote, "ghosts");
                        bool fresh = sT - sRemoteAt < 1f;
                        if (sGhostStage == 0 && fresh && theirs.Contains(want) && theirs.Contains(want) && theirs.EndsWith(":1"))
                        {
                            var e = theirs.Split(':');
                            var at = sHostComp.getPosition();
                            bool near = Mathf.Abs(float.Parse(e[3]) - at.x) < 0.3f && Mathf.Abs(float.Parse(e[4]) - at.z) < 0.3f;
                            // the host's own preview is in its component list (it's the game's preview); the joiner's copy must not be
                            Check("host's component preview shows on the joiner, in the right module and place, not as a component",
                                near && Get(sRemote, "count.Component") == Registry.Components.Count(c => c != sHostComp && !c.isDestroyed() && Registry.StateOf(c) != BuildableState.Placing).ToString(), theirs);
                            sHostComp.destroy();
                            sHostComp = null;
                            fPlacedComponent.SetValue(GameAccess.Game(), null);
                            sGhostStage = 1; sStepT = 0f;
                        }
                        else if (sGhostStage == 1 && fresh && !theirs.Contains(":Bench:") && sStepT > 0.3f)
                        {
                            Check("host's component preview gone: gone on the joiner", true);
                            Next();
                        }
                        else if (sStepT > 8f) { Check("host's component preview on the joiner (stage " + sGhostStage + ")", false, "joiner ghosts: " + theirs); Next(); }
                    }
                    break;
                case 54: // the joiner's component preview shows on the host
                    if (First()) { SendProbe("do", $"comp-ghost-start {sM1.getId()} Table"); sGhostStage = 0; return; }
                    {
                        string mine = Ghosts.Digest();
                        if (sGhostStage == 0 && mine.Contains($":Table:{sM1.getId()}:"))
                        {
                            Check("joiner's component preview shows on the host, not as a component", Registry.Components.Count == sCompsBefore, mine);
                            SendProbe("do", "comp-ghost-end");
                            sGhostStage = 1; sStepT = 0f;
                        }
                        else if (sGhostStage == 1 && !mine.Contains(":Table:"))
                        {
                            Check("joiner's component preview gone: gone on the host", true);
                            sStep = 14; sEntered = false; sStepT = 0;
                        }
                        else if (sStepT > 8f) { Check("joiner's component preview on the host (stage " + sGhostStage + ")", false, $"host ghosts '{mine}'"); sStep = 14; sEntered = false; sStepT = 0; }
                    }
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
                        // margin: the joiner checks the spot against its own mirrored world a moment later, and the
                        // colony ship's animated door collider can sit a fraction of a metre off there
                        sClientSpot = FindFreeSpot("ModuleTypeWaterExtractor", 0, sM1 != null ? sM1.getPosition() : sBase, 18f, margin: 3f);
                        Log.Info($"bot: joiner spot {sClientSpot}; ships {string.Join(", ", Registry.Ships.Select(s => s.getId() + " state " + ShipRec.From(s).State))}; host ship colliders: {PlacementDiag.Ships(sClientSpot)}");
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
                    if (First())
                    {
                        ForceProgressAll(0.5f);
                        // what the game does when a module's builder walks away (Buildable.tick): back to
                        // Placed, progress kept. The joiner must still show the progress.
                        var idle = Registry.Constructions.OfType<Module>().FirstOrDefault(m => m.getModuleType() is ModuleTypeSolarPanel && !m.isBuilt());
                        if (idle != null) { Registry.SetState(idle, BuildableState.Placed); Log.Info($"bot: module {idle.getId()} left half-built (Placed, 0.5)"); }
                        return;
                    }
                    if (WaitProgressMatch(20f)) Next();
                    break;
                case 11: // T7: colonists move on the joiner like on the host
                    if (First()) { sCharsAtStart = ParseEntries(Get(LocalDigest(), "chars")); return; }
                    if (WaitCharsMatch(60f)) Next();
                    break;
                case 12: // T8: resources (positions and holders) match after the host moves one and stores another
                    // The host is paused for this step: colonists otherwise haul the two resources away
                    // within seconds, and carried resources also reach the joiner through the colonist
                    // records, so a slow check could pass without resource records at all.
                    if (First())
                    {
                        Singleton<TimeManager>.getInstance().pause();
                        Registry.Collect(Kind.Resource, sScratch);
                        var ground = sScratch.Values.Cast<Resource>().Where(x => { ResourcePlaces.Where(x, out var h, out _, out _); return h == Holder.None && x.getState() == Resource.State.Idle; }).ToList();
                        sMoved = ground.FirstOrDefault();
                        sStored = ground.Skip(1).FirstOrDefault();
                        if (sMoved != null) sMoved.setPosition(sMoved.getPosition() + new Vector3(4f, 0f, 3f));
                        var slot = sStorage?.findStorageSlot(sStorage.getPosition());
                        if (sStored != null && slot != null) slot.addResource(sStored);
                        Log.Info($"bot: moved resource {sMoved?.getId()}, stored {sStored?.getId()} (slot {(slot != null)})");
                        if (sMoved == null || sStored == null || slot == null) { Check("resource test setup (2 loose resources, a storage slot)", false); Finish(); return; }
                        sResExpect = ParseEntries(Get(LocalDigest(), "res"));   // what the host just set
                        return;
                    }
                    if (sStepT > 0.5f && WaitResourcesMatch(10f, sMoved.getId(), sStored.getId()))
                    {
                        Singleton<TimeManager>.getInstance().unpause();
                        Next();
                    }
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

        private static Module sAccu, sTank, sHostPreview;
        private static ConstructionComponent sHostComp;
        private static int sCompsBefore;
        private static readonly FieldInfo fPlacedComponent = AccessTools.Field(typeof(GameStateGame), "mPlacedComponent");
        private static Vector3 sGhostSpot;
        private static int sGhostStage;
        private static string sMeteorName, sHitText;
        private static bool sRemoved, sAimed;
        private static int sAimSeed, sAimTries;
        private static Vector3 sAimTarget;
        private static string sAimName;
        private static readonly FieldInfo fMeteorSeedList = AccessTools.Field(typeof(MeteorManager), "mSeeds");
        private static int sTradeStage, sReceivedBefore, sCaughtBefore;
        private static readonly FieldInfo fTraderId = AccessTools.Field(typeof(Resource), "mTraderId");

        private static float sMeteorLogT;
        private static void LogMeteors()
        {
            if (sT - sMeteorLogT < 0.5f) return;
            sMeteorLogT = sT;
            Log.Info($"meteors t={sStepT:F1} timeScale={Time.timeScale} host: " + string.Join(" ", MeteorObjects().Select(g =>
            {
                var rb = g.GetComponent<Rigidbody>();
                return $"{g.name}@{g.transform.position} v={(rb != null ? rb.velocity.ToString() : "none")} grav={(rb != null && rb.useGravity)}";
            })) + " | joiner: " + Get(sRemote, "meteors") + (sM1 != null ? $" | target {sM1.getPosition()} cond {BuildRec.ConditionIndicator(sM1)?.getValue():F2}" : ""));
        }

        private static int Count(string hay, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return 0;
            int n = 0, i = 0;
            while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        // host and joiner stored amounts match (ratio within 0.01) and the joiner's model shows its amount
        private static bool StoresMatch(bool last, string name)
        {
            var mine = ParseEntries(Get(LocalDigest(), "stores"));
            var theirs = ParseEntries(Get(sRemote, "stores"));
            var bad = new List<string>();
            foreach (var id in new[] { sAccu.getId().ToString(), sTank.getId().ToString() })
            {
                if (!mine.TryGetValue(id, out var m) || !theirs.TryGetValue(id, out var t)) { bad.Add(id + " missing"); continue; }
                float hp = float.Parse(m[0]), hw = float.Parse(m[1]), jp = float.Parse(t[0]), jw = float.Parse(t[1]), jg = float.Parse(t[2]), jt = float.Parse(t[3]);
                if (Mathf.Abs(hp - jp) > 0.01f || Mathf.Abs(hw - jw) > 0.01f) bad.Add($"{id} amount host {hp:F3}/{hw:F3} joiner {jp:F3}/{jw:F3}");
                if (jg >= 0f && hp >= 0f && Mathf.Abs(jg - jp) > 0.01f) bad.Add($"{id} gauge {jg:F3} vs {jp:F3}");
                if (jt >= 0f && hw >= 0f && Mathf.Abs(jt - jw) > 0.01f) bad.Add($"{id} tank {jt:F3} vs {jw:F3}");
            }
            if (bad.Count == 0 && sT - sRemoteAt < 1f) { Check(name, true, Get(sRemote, "stores")); return true; }
            if (last) { Check(name, false, string.Join("; ", bad) + " | joiner " + Get(sRemote, "stores")); return true; }
            return false;
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

        private static float sResLogT = -10f;
        private static Dictionary<string, string[]> sResExpect;

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
                // the two resources the host just moved/stored: the joiner must show exactly the state the
                // host set (and the host must still have it)
                if (!theirs.TryGetValue(id.ToString(), out var t) || !mine.TryGetValue(id.ToString(), out var m)) { off++; continue; }
                if (sResExpect != null && sResExpect.TryGetValue(id.ToString(), out var e) && string.Join(":", e) != string.Join(":", m))
                {
                    off++; example = example ?? $"host's {id} changed since the test set it ({string.Join(":", e)} -> {string.Join(":", m)})";
                    continue;
                }
                if (t[0] != m[0] || t[1] != m[1] || t[2] != m[2] || Mathf.Abs(float.Parse(t[3]) - float.Parse(m[3])) > 0.5f || Mathf.Abs(float.Parse(t[4]) - float.Parse(m[4])) > 0.5f) off++;
            }
            bool pass = mine.Count > 0 && off == 0 && sT - sRemoteAt < 1f;
            if (pass || sStepT >= timeout || sStepT - sResLogT >= 2f)
            {
                // the two resources this check is about, as each side sees them
                sResLogT = sStepT;
                Log.Info("res check t=" + sStepT.ToString("F1") + " off=" + off + ": " + string.Join(" | ", mustMatch.Select(id =>
                    $"{id} host {(mine.TryGetValue(id.ToString(), out var m) ? string.Join(":", m) : "-")} joiner {(theirs.TryGetValue(id.ToString(), out var t) ? string.Join(":", t) : "-")}")));
            }
            if (pass) { Check("resources have the same holder and place", true, mine.Count + " resources, incl. one moved and one stored by the host"); return true; }
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

        private static Vector3 FindFreeSpot(string typeName, int size, Vector3 near, float minDist, float margin = 0f)
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
                        if (!probe.canPlaceModule(p, Vector3.up, Module.ValidSizes[size])) continue;
                        float half = Module.ValidSizes[size] * 0.5f;
                        // same checks as canPlaceModule (ships: layer 16, size/2 + 10 m; resources: layer 10, size/2 + 2 m), wider
                        if (margin > 0f && (Physics.CheckSphere(p, half + 10f + margin, 65536) || Physics.CheckSphere(p, half + 2f + margin, 1024))) continue;
                        return p;
                    }
            }
            finally { probe.destroy(); }
            Log.Warn($"bot: no free spot for {typeName} near {near}; using a fallback that may be blocked");
            return near + new Vector3(minDist, 0, 0);
        }

        // A spot a player could really use: new modules must link to a module that already has a
        // link (Connection.canLink), and the bot's extra test buildings have none. So search finely
        // around linked modules, keeping the usual margin from ships and resources.
        private static Vector3 FindLinkableSpot(string typeName)
        {
            var type = TypeList<ModuleType, ModuleTypeList>.find(typeName);
            int size = type.getMinSize();
            float floor = Singleton<TerrainGenerator>.getInstance().getFloorHeight();
            var probe = Module.create(sBase, size, type);
            try
            {
                foreach (var anchor in Registry.Constructions.OfType<Module>().Where(m => m.getLinkCount() > 0).ToList())
                    for (float r = 8f; r < 45f; r += 1f)
                        for (int a = 0; a < 48; a++)
                        {
                            float ang = a * Mathf.PI * 2f / 48f;
                            var p = new Vector3(anchor.getPosition().x + Mathf.Cos(ang) * r, floor, anchor.getPosition().z + Mathf.Sin(ang) * r);
                            probe.setPosition(p);
                            if (!probe.canPlaceModule(p, Vector3.up, Module.ValidSizes[size])) continue;
                            float half = Module.ValidSizes[size] * 0.5f;
                            if (Physics.CheckSphere(p, half + 13f, 65536) || Physics.CheckSphere(p, half + 5f, 1024)) continue;
                            return p;
                        }
            }
            finally { probe.destroy(); }
            Log.Warn("bot: no linkable spot for " + typeName);
            return sBase;
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
        private static readonly FieldInfo fPlacedComponentC = AccessTools.Field(typeof(GameStateGame), "mPlacedComponent");
        private static ConstructionComponent sClientComp;
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
                    if (sCrawlLeft > 0f) CrawlStep(dt);
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
                    Log.Info("bot: preview valid on the joiner: " + preview.isValidPosition() + $"; ships {string.Join(", ", Registry.Ships.Select(s => s.getId() + " state " + ShipRec.From(s).State))}; joiner ship colliders: {PlacementDiag.Ships(pos)}");
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
                case "trade-open":
                {
                    var ship = Registry.Find(Kind.Ship, int.Parse(a[1])) as MerchantShip;
                    if (ship != null) GameAccess.Gui().setWindow(new GuiMerchantWindow(ship));
                    else Log.Warn("bot: no merchant ship " + a[1]);
                    break;
                }
                case "trade-fill-base":
                {
                    // click base items into the base's box (the window's own button handler) until the
                    // basket pays for the merchant side
                    var win = TradeShare.OpenWindow;
                    if (win == null) { Log.Warn("bot: no trade window"); break; }
                    var ship = TradeShare.ShipOf(win);
                    float need = ((ProductAmounts)fWinMTrade.GetValue(win)).getValue() * (1f + (int)fCommission.GetValue(ship) / 100f) + 1f;
                    for (int guard = 0; guard < 200 && ((ProductAmounts)fWinBTrade.GetValue(win)).getValue() < need; guard++)
                    {
                        Product pick = null;
                        foreach (var pa in (ProductAmounts)fWinBStock.GetValue(win)) if (pa.getAmount() > 0) { pick = pa.getProduct(); break; }
                        if (pick == null) break;
                        int before = ((ProductAmounts)fWinBTrade.GetValue(win)).getTotalAmount();
                        Event.current = new Event();
                        win.onBaseStockClicked(pick);
                        if (((ProductAmounts)fWinBTrade.GetValue(win)).getTotalAmount() == before) break;
                    }
                    Log.Info("bot: base basket " + TradeShare.Digest(win));
                    break;
                }
                case "trade-commit":
                {
                    var win = TradeShare.OpenWindow;
                    if (win == null) { Log.Warn("bot: no trade window to commit"); break; }
                    win.onTrade(null);   // Trade button: closes the window through the game's own path
                    if (GameAccess.Gui().getWindow() == win) GameAccess.Gui().setWindow(null);
                    break;
                }
                case "ghost-start":
                {
                    var type = TypeList<ModuleType, ModuleTypeList>.find(a[1]);
                    float floor = Singleton<TerrainGenerator>.getInstance().getFloorHeight();
                    var pos = new Vector3(float.Parse(a[2]), floor, float.Parse(a[3]));
                    var preview = Module.create(pos, type.getMinSize(), type);
                    preview.setValidPosition(preview.canPlaceModule(pos, Vector3.up, Module.ValidSizes[type.getMinSize()]));
                    fActiveModule.SetValue(game, preview);
                    Log.Info("bot: joiner preview at " + pos + " valid " + preview.isValidPosition() + (preview.isValidPosition() ? "" : ": " + PlacementDiag.Why(preview, pos)));
                    break;
                }
                case "comp-ghost-start":
                {
                    var m = Registry.Find(Kind.Module, int.Parse(a[1])) as Module;
                    var cc = ConstructionComponent.create(m, TypeList<ComponentType, ComponentTypeList>.find(a[2]));
                    cc.setPosition(m.getPosition() + new Vector3(-1.5f, 0f, 0.5f));
                    cc.setValidPosition(true);
                    fPlacedComponentC.SetValue(game, cc);
                    sClientComp = cc;
                    break;
                }
                case "comp-ghost-end":   // right click: the game's cancelComponentPlacement
                    sClientComp?.destroy();
                    sClientComp = null;
                    fPlacedComponentC.SetValue(game, null);
                    break;
                case "ghost-commit":
                    mPlaceModule.Invoke(game, null);   // the click: PlaceModulePrefix sends it, drops the preview
                    break;
                case "log-error":
                    Debug.LogError("coop test: deliberate error to check the error guard");
                    break;
                case "crawl":
                    sCrawlLeft = float.Parse(a[1]);
                    sCrawlReport = "running";
                    sCrawlI = 0; sCrawlSel = 0; sCrawlWin = 0; sCrawlWinT = 0f; sCrawlBotErrors = 0; sCrawlFirstError = null;
                    break;
                case "toggle-merchants":
                {
                    var rb = Singleton<LandingShipManager>.getInstance().getLandingPermissions().getMerchantRefBool();
                    rb.set(!rb.get());   // what the window's checkbox does
                    Settings.TestReplayHostResend();   // and the host's 5 s re-send lands before our next check
                    Log.Info("bot: merchants toggled to " + rb.get() + " (after the host's re-send)");
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

        // ------------------------------------------------------------ joiner UI crawl
        // Select every object in turn (info panel, menus) and open every main window, a few frames
        // each, like a player clicking around while the host plays. Game errors land in ErrorGuard.
        private static float sCrawlLeft, sCrawlSelT, sCrawlWinT;
        private static int sCrawlI, sCrawlSel, sCrawlWin, sCrawlBotErrors;
        private static string sCrawlReport = "", sCrawlFirstError;
        private static readonly Type[] CrawlWindows =
        {
            typeof(GuiStatsWindow), typeof(GuiChartsWindow), typeof(GuiGridWindow), typeof(GuiTechsWindow),
            typeof(GuiMilestonesWindow), typeof(GuiSecurityWindow), typeof(GuiManufactureLimitsWindow),
        };

        private static void CrawlStep(float dt)
        {
            sCrawlLeft -= dt;
            sCrawlSelT += dt;
            sCrawlWinT += dt;
            try
            {
                if (sCrawlSelT >= 0.2f)
                {
                    sCrawlSelT = 0f;
                    var all = new List<Selectable>();
                    foreach (var k in Registry.All)
                    {
                        Registry.Collect(k, sScratch);
                        all.AddRange(sScratch.Values.OfType<Selectable>());
                    }
                    if (all.Count > 0)
                    {
                        var s = all[sCrawlI++ % all.Count];
                        Selection.select(s);
                        sCrawlSel++;
                    }
                }
                if (sCrawlWinT >= 2f)
                {
                    sCrawlWinT = 0f;
                    var t = CrawlWindows[sCrawlWin++ % CrawlWindows.Length];
                    GameAccess.Gui().setWindow((GuiWindow)Activator.CreateInstance(t));
                }
            }
            catch (Exception e)
            {
                sCrawlBotErrors++;
                sCrawlFirstError = sCrawlFirstError ?? e.GetType().Name + ": " + e.Message;
                Log.Warn("crawl: " + e);
            }
            if (sCrawlLeft <= 0f)
            {
                sCrawlLeft = 0f;
                try { GameAccess.Gui().setWindow(null); Selection.clear(); } catch { }
                sCrawlReport = $"done: {sCrawlSel} selections, {sCrawlWin} windows, {sCrawlBotErrors} bot-call errors" + (sCrawlFirstError != null ? " (first: " + sCrawlFirstError + ")" : "");
                Log.Info("crawl " + sCrawlReport);
            }
        }

        public static void OnHostDigestRequest()
        {
            if (sMode == "client" && Session.Active) SendProbe("digest", Encode(LocalDigest()));
        }
    }
}
