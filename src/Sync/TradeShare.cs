using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Planetbase;
using PlanetbaseCoop.Net;

namespace PlanetbaseCoop.Sync
{
    // Shared trade basket. While players have the same merchant's window open, what one moves into
    // the two transaction boxes shows up in the others' windows at once. The host keeps the basket
    // of each ship and relays changes; whoever presses Trade commits it (a joiner through the
    // Trade command), and every other window on that ship closes.
    //
    // On the host the base's side of the basket is marked for trading (as the game does while you
    // browse), so colonists don't use those resources meanwhile. A joiner's marks are local only.
    public static class TradeShare
    {
        private static readonly FieldInfo fShip = AccessTools.Field(typeof(GuiMerchantWindow), "mMerchantShip");
        private static readonly FieldInfo fMStock = AccessTools.Field(typeof(GuiMerchantWindow), "mMerchantStockAmounts");
        private static readonly FieldInfo fMTrade = AccessTools.Field(typeof(GuiMerchantWindow), "mMerchantTradeAmounts");
        private static readonly FieldInfo fBStock = AccessTools.Field(typeof(GuiMerchantWindow), "mBaseStockAmounts");
        private static readonly FieldInfo fBTrade = AccessTools.Field(typeof(GuiMerchantWindow), "mBaseTradeAmounts");
        private static readonly FieldInfo fResult = AccessTools.Field(typeof(GuiMerchantWindow), "mResult");
        private static readonly MethodInfo mUpdateUi = AccessTools.Method(typeof(GuiMerchantWindow), "updateUi");

        public class Basket { public string Base = "", Merchant = ""; public string Key => Base + "\n" + Merchant; }

        private static readonly Dictionary<int, Basket> sBaskets = new Dictionary<int, Basket>();
        private static readonly Dictionary<int, HashSet<int>> sViewers = new Dictionary<int, HashSet<int>>();   // host: -1 = host itself
        private const int HostViewer = -1;
        private static GuiMerchantWindow sOpen;     // the merchant window open on this machine (the game shows one at a time)
        private static bool sApplying, sForcing;
        public static int Sent, Applied, Relayed, ForcedCloses;

        public static void Reset()
        {
            sBaskets.Clear();
            sViewers.Clear();
            sOpen = null;
        }

        public static void Patch(Harmony h)
        {
            h.Patch(AccessTools.Constructor(typeof(GuiMerchantWindow), new[] { typeof(MerchantShip) }),
                postfix: new HarmonyMethod(typeof(TradeShare), nameof(OpenedPostfix)));
            h.Patch(mUpdateUi, postfix: new HarmonyMethod(typeof(TradeShare), nameof(ChangedPostfix)));
            h.Patch(AccessTools.Method(typeof(GuiMerchantWindow), nameof(GuiMerchantWindow.destroy)),
                postfix: new HarmonyMethod(typeof(TradeShare), nameof(ClosedPostfix)));
        }

        public static GuiMerchantWindow OpenWindow => sOpen;
        public static MerchantShip ShipOf(GuiMerchantWindow w) => (MerchantShip)fShip.GetValue(w);
        public static int ResultOf(GuiMerchantWindow w) => Convert.ToInt32(fResult.GetValue(w));   // 0 Trade, 1 Dismiss, 2 Cancel

        public static Basket Read(GuiMerchantWindow w) => new Basket
        {
            Base = Registry.XmlOut(root => ((ProductAmounts)fBTrade.GetValue(w)).serialize(root, "base")),
            Merchant = Registry.XmlOut(root => ((ProductAmounts)fMTrade.GetValue(w)).serialize(root, "merchant")),
        };

        private static ProductAmounts Parse(string xml)
        {
            var pa = new ProductAmounts();
            if (!string.IsNullOrEmpty(xml)) Registry.XmlIn(xml, root => pa.deserialize(root.FirstChild));
            return pa;
        }

        // ---------------------------------------------------------------- window hooks (both sides)
        private static void OpenedPostfix(GuiMerchantWindow __instance)
        {
            if (!Session.Active) return;
            var ship = ShipOf(__instance);
            if (ship == null) return;
            sOpen = __instance;
            int id = ship.getId();
            if (Session.IsHost)
            {
                Viewers(id).Add(HostViewer);
                if (sBaskets.TryGetValue(id, out var b)) Apply(__instance, b);
            }
            else if (!Commands.ApplyingRemote)
            {
                Session.SendCommand(Wire.Build(Msg.TradeView, w => { w.Write(id); w.Write(true); }));
                if (sBaskets.TryGetValue(id, out var b)) Apply(__instance, b);
            }
        }

        private static void ChangedPostfix(GuiMerchantWindow __instance)
        {
            if (!Session.Active || sApplying || sOpen != __instance) return;
            var ship = ShipOf(__instance);
            if (ship == null) return;
            int id = ship.getId();
            var now = Read(__instance);
            if (sBaskets.TryGetValue(id, out var last) && last.Key == now.Key) return;
            sBaskets[id] = now;
            var msg = BasketMsg(id, now);
            if (Session.IsHost) { foreach (var p in Session.ReadyPeers) Session.Transport.Send(p, msg); }
            else Session.SendCommand(msg);
            Sent++;
        }

        private static void ClosedPostfix(GuiMerchantWindow __instance)
        {
            if (sOpen == __instance) sOpen = null;
            if (!Session.IsHost || sForcing) return;
            var ship = ShipOf(__instance);
            if (ship == null) return;
            int id = ship.getId();
            int result = ResultOf(__instance);
            if (result == 0 || result == 1) { End(id, closeWindows: true); return; }   // traded or dismissed
            var v = Viewers(id);
            v.Remove(HostViewer);
            if (v.Count == 0) End(id, closeWindows: false);
        }

        // joiner: its window closed (Commands.TradeWindowClosePrefix)
        public static void OnJoinerClosed(GuiMerchantWindow w)
        {
            if (sOpen == w) sOpen = null;
            var ship = ShipOf(w);
            if (ship == null || sForcing) return;
            int id = ship.getId();
            if (ResultOf(w) == 2) Session.SendCommand(Wire.Build(Msg.TradeView, m => { m.Write(id); m.Write(false); }));
        }

        private static byte[] BasketMsg(int id, Basket b) => Wire.Build(Msg.TradeBasket, w => { w.Write(id); w.Write(b.Base); w.Write(b.Merchant); });

        private static HashSet<int> Viewers(int ship)
        {
            if (!sViewers.TryGetValue(ship, out var v)) sViewers[ship] = v = new HashSet<int>();
            return v;
        }

        // ---------------------------------------------------------------- host
        public static void OnHostBasket(int peer, BinaryReader r)
        {
            int id = r.ReadInt32();
            var b = new Basket { Base = r.ReadString(), Merchant = r.ReadString() };
            Viewers(id).Add(peer);
            sBaskets[id] = b;
            if (sOpen != null && ShipOf(sOpen)?.getId() == id)
            {
                Apply(sOpen, b);
                // the host's stock decides: if not everything could be marked, everyone gets what was
                var actual = Read(sOpen);
                if (actual.Key != b.Key) { sBaskets[id] = b = actual; peer = -2; }   // -2: send to the sender too
            }
            var msg = BasketMsg(id, b);
            foreach (var p in Session.ReadyPeers) if (p != peer) { Session.Transport.Send(p, msg); Relayed++; }
        }

        public static void OnHostView(int peer, BinaryReader r)
        {
            int id = r.ReadInt32();
            bool open = r.ReadBoolean();
            var v = Viewers(id);
            if (open)
            {
                v.Add(peer);
                if (sBaskets.TryGetValue(id, out var b)) Session.Transport.Send(peer, BasketMsg(id, b));
            }
            else
            {
                v.Remove(peer);
                if (v.Count == 0) End(id, closeWindows: false);
            }
        }

        public static void OnPeerLeft(int peer)
        {
            foreach (var kv in sViewers.ToList())
            {
                if (kv.Value.Remove(peer) && kv.Value.Count == 0) End(kv.Key, closeWindows: false);
            }
        }

        // A joiner commits a trade (Cmd.Trade): the host's own window on that ship must let go of
        // the ship first (it holds its trading state and marks).
        public static void BeforeJoinerTrade(int shipId)
        {
            if (sOpen != null && ShipOf(sOpen)?.getId() == shipId) ForceClose(sOpen);
        }

        public static void AfterJoinerTrade(int shipId) => End(shipId, closeWindows: true);

        private static void End(int id, bool closeWindows)
        {
            sBaskets.Remove(id);
            sViewers.Remove(id);
            if (closeWindows && sOpen != null && ShipOf(sOpen)?.getId() == id) ForceClose(sOpen);
            if (Session.Transport == null) return;
            var msg = Wire.Build(Msg.TradeEnd, w => { w.Write(id); w.Write(closeWindows); });
            foreach (var p in Session.ReadyPeers) Session.Transport.Send(p, msg);
        }

        // ---------------------------------------------------------------- joiner
        public static void OnClientBasket(BinaryReader r)
        {
            int id = r.ReadInt32();
            var b = new Basket { Base = r.ReadString(), Merchant = r.ReadString() };
            sBaskets[id] = b;
            if (sOpen != null && ShipOf(sOpen)?.getId() == id) Apply(sOpen, b);
        }

        public static void OnClientEnd(BinaryReader r)
        {
            int id = r.ReadInt32();
            bool close = r.ReadBoolean();
            sBaskets.Remove(id);
            if (close && sOpen != null && ShipOf(sOpen)?.getId() == id)
            {
                ForceClose(sOpen);
                Ui.Toast("The trade was concluded by another player.");
            }
        }

        // ---------------------------------------------------------------- both
        // Make an open window show the basket: move products between stock and transaction boxes
        // the way the window's own buttons do (marking base resources for trading).
        private static void Apply(GuiMerchantWindow win, Basket b)
        {
            var ship = ShipOf(win);
            if (ship == null) return;
            sApplying = true;
            try
            {
                var wantB = Parse(b.Base);
                var wantM = Parse(b.Merchant);
                var mStock = (ProductAmounts)fMStock.GetValue(win);
                var mTrade = (ProductAmounts)fMTrade.GetValue(win);
                var bStock = (ProductAmounts)fBStock.GetValue(win);
                var bTrade = (ProductAmounts)fBTrade.GetValue(win);

                foreach (var p in Products(mTrade, wantM))
                {
                    int diff = wantM.getProductCount(p) - mTrade.getProductCount(p);
                    if (diff > 0)
                    {
                        int n = Math.Min(diff, mStock.getProductCount(p));
                        if (n > 0) { mStock.remove(p, n); mTrade.add(p, n); }
                    }
                    else if (diff < 0) { mTrade.remove(p, -diff); mStock.add(p, -diff); }
                }
                foreach (var p in Products(bTrade, wantB))
                {
                    int diff = wantB.getProductCount(p) - bTrade.getProductCount(p);
                    for (int i = 0; i < diff; i++)
                    {
                        if (bStock.getProductCount(p) <= 0 || !p.markForTrading(ship)) break;
                        bStock.remove(p, 1);
                        bTrade.add(p, 1);
                    }
                    for (int i = 0; i < -diff; i++)
                    {
                        bTrade.remove(p, 1);
                        bStock.add(p, 1);
                        p.unmarkForTrading(ship.getId());
                    }
                }
                mUpdateUi.Invoke(win, null);
                Applied++;
            }
            catch (Exception e)
            {
                Log.Warn("trade basket apply: " + e.Message);
            }
            finally { sApplying = false; }
        }

        private static List<Product> Products(ProductAmounts a, ProductAmounts b)
        {
            var d = new Dictionary<string, Product>();
            foreach (var pa in a) d[pa.getProduct().getId()] = pa.getProduct();
            foreach (var pa in b) if (!d.ContainsKey(pa.getProduct().getId())) d[pa.getProduct().getId()] = pa.getProduct();
            return d.Values.ToList();
        }

        // Close a merchant window without committing it (Cancel: marks undone, ship free again).
        private static void ForceClose(GuiMerchantWindow win)
        {
            sForcing = true;
            try
            {
                fResult.SetValue(win, Enum.ToObject(fResult.FieldType, 2));
                var gui = GameAccess.Gui();
                if (gui != null && gui.getWindow() == win) gui.setWindow(null);   // destroys it
                else win.destroy();
                ForcedCloses++;
            }
            finally { sForcing = false; }
            if (sOpen == win) sOpen = null;
        }

        public static string Digest(GuiMerchantWindow w)
        {
            if (w == null) return "";
            string Fmt(ProductAmounts a)
            {
                var items = new List<string>();
                foreach (var x in a) items.Add(x.getProduct().getId() + "x" + x.getAmount());
                items.Sort(StringComparer.Ordinal);
                return string.Join(",", items);
            }
            return ShipOf(w)?.getId() + "|" + Fmt((ProductAmounts)fMTrade.GetValue(w)) + "|" + Fmt((ProductAmounts)fBTrade.GetValue(w));
        }
    }
}
